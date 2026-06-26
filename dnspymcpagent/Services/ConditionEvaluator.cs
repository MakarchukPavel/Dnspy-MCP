using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using dndbg.COM.CorDebug;
using dndbg.Engine;
using Microsoft.Diagnostics.Runtime;

namespace DnSpyMcp.Agent.Services;

/// <summary>
/// Compiles a breakpoint-condition string into a predicate evaluated inside
/// dndbg's IL breakpoint condition callback. Two families:
///
///   count &lt;op&gt; N                 hit-count gate (op ==/!=/&gt;=/&lt;=/&gt;/&lt;)
///   arg&lt;i&gt;[.field…]   &lt;op&gt; lit   compare an argument (optional field path)
///   local&lt;i&gt;[.field…] &lt;op&gt; lit   compare a local
///
/// Literals: integer (decimal or 0x-hex), <c>true</c>/<c>false</c>, <c>null</c>,
/// or a single/double-quoted string. Guid/DateTime fields compare against their
/// text form, so <c>arg0.UId == "0c81…"</c> works. Enum fields compare against
/// either the member name (string literal) or the numeric value.
///
/// Argument/local primitives are read straight off the frame via
/// <c>GetILArgument</c>/<c>GetILLocal</c>; field paths are walked with ClrMD off
/// the dereferenced object address — both passive reads, safe inside the
/// callback (no func-eval, which ICorDebug forbids there).
/// </summary>
public static class ConditionEvaluator
{
    private static readonly string[] Ops = { ">=", "<=", "==", "!=", ">", "<" };
    private static readonly Regex SlotRe =
        new(@"^(arg|local)(\d+)((?:\.[A-Za-z_][A-Za-z0-9_]*)*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse <paramref name="raw"/> into a predicate. The int argument is the
    /// post-increment hit count (used by `count`); the context gives the
    /// paused thread/frame (used by arg/local). Throws on a malformed
    /// condition — the error text always contains the word "condition".
    /// </summary>
    public static Func<int, ILCodeBreakpointConditionContext, bool> Compile(string raw)
    {
        var (lhs, op, rhsRaw) = Split(raw);
        var lit = ParseLiteral(rhsRaw, raw);

        if (lhs.Equals("count", StringComparison.OrdinalIgnoreCase))
        {
            if (lit.Kind != LitKind.Int)
                throw new ArgumentException($"unsupported condition '{raw}': 'count' must compare against an integer");
            long n = lit.Int;
            return (hit, _) => CompareNum(hit, op, n, integral: true, lInt: hit);
        }

        var m = SlotRe.Match(lhs);
        if (!m.Success)
            throw new ArgumentException(
                $"unsupported condition '{raw}': left side must be 'count', 'arg<n>' or 'local<n>' (optionally with a .field path)");

        bool isArg = m.Groups[1].Value.Equals("arg", StringComparison.OrdinalIgnoreCase);
        uint idx = uint.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        string[] path = m.Groups[3].Value.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);

        return (_, ctx) =>
        {
            try { return EvalSlot(ctx, isArg, idx, path, op, lit); }
            catch { return true; } // fail-open: if we can't evaluate, pause rather than silently miss
        };
    }

    /// <summary>
    /// Parse a capture expression (<c>arg&lt;n&gt;[.field…]</c> or
    /// <c>local&lt;n&gt;[.field…]</c>) into a reader that, given the paused-frame
    /// context, returns a JSON-friendly decoded value. Used by tracepoints to
    /// snapshot a value at each hit. Same passive read path as conditions (no
    /// func-eval): primitives, strings, bool, enum (with member name),
    /// Guid/DateTime/etc. by text, null, and an opaque marker for object refs.
    /// An unreadable slot (optimized away / out of scope) yields
    /// <c>{kind:"unavailable"}</c> rather than throwing, so one bad expression
    /// never aborts the whole capture.
    /// </summary>
    public static Func<ILCodeBreakpointConditionContext, object> CompileCapture(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            throw new ArgumentException("capture expression must be non-empty");
        var m = SlotRe.Match(expr.Trim());
        if (!m.Success)
            throw new ArgumentException(
                $"unsupported capture expression '{expr}': must be 'arg<n>' or 'local<n>' (optionally with a .field path)");

        bool isArg = m.Groups[1].Value.Equals("arg", StringComparison.OrdinalIgnoreCase);
        uint idx = uint.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        string[] path = m.Groups[3].Value.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);

        return ctx =>
        {
            try
            {
                var frame = ActiveIlFrame(ctx.E?.CorThread);
                if (frame == null) return new { kind = "unavailable", reason = "no managed frame" };
                var v = isArg ? frame.GetILArgument(idx, out _) : frame.GetILLocal(idx, out _);
                if (v == null) return new { kind = "unavailable", reason = "optimized away / out of scope" };
                var leaf = path.Length == 0 ? ReadSlotLeaf(v) : ReadPathLeaf(v, path);
                return CmpToJson(leaf);
            }
            catch (Exception ex) { return new { kind = "error", error = ex.Message }; }
        };
    }

    /// <summary>Project a comparison leaf into a JSON-friendly capture value.</summary>
    private static object CmpToJson(Cmp c) => c.Kind switch
    {
        CmpKind.Null => new { kind = "null" },
        CmpKind.Bool => new { kind = "bool", value = (object)c.Bool },
        CmpKind.Num => c.Integral ? new { kind = "int", value = (object)c.Long }
                                   : new { kind = "float", value = (object)c.Num },
        CmpKind.Str => new { kind = "string", value = (object?)c.Str },
        CmpKind.Enum => new { kind = "enum", value = (object)c.Long, name = (object?)c.Str },
        CmpKind.Obj => new { kind = "object" },
        _ => new { kind = "unknown" },
    };

    // ---- evaluation ---------------------------------------------------------

    private static bool EvalSlot(ILCodeBreakpointConditionContext ctx, bool isArg, uint idx,
                                 string[] path, string op, Literal lit)
    {
        var frame = ActiveIlFrame(ctx.E?.CorThread);
        if (frame == null) return true; // no managed frame -> can't evaluate, fail-open
        var v = isArg ? frame.GetILArgument(idx, out _) : frame.GetILLocal(idx, out _);
        if (v == null) return true;     // optimized away / out of scope -> fail-open

        var left = path.Length == 0 ? ReadSlotLeaf(v) : ReadPathLeaf(v, path);
        return Apply(left, op, lit);
    }

    private static CorFrame? ActiveIlFrame(CorThread? ct)
    {
        if (ct == null) return null;
        var af = ct.ActiveFrame;
        if (af != null && af.IsILFrame) return af;
        foreach (var f in ct.AllFrames)
            if (f.IsILFrame) return f;
        return null;
    }

    /// <summary>Leaf value of a bare arg/local slot (no field path), via CorValue.</summary>
    private static Cmp ReadSlotLeaf(CorValue v)
    {
        if (v.IsReference)
        {
            var d = v.DereferencedValue;
            if (d == null || d.IsNull) return Cmp.MkNull();
            if (d.IsString) return Cmp.MkStr(d.String);
            return Cmp.MkObj();
        }
        if (v.IsString) return Cmp.MkStr(v.String);

        switch (v.ElementType)
        {
            case CorElementType.Boolean:
            case CorElementType.I1: case CorElementType.U1:
            case CorElementType.I2: case CorElementType.U2:
            case CorElementType.Char:
            case CorElementType.I4: case CorElementType.U4:
            case CorElementType.I8: case CorElementType.U8:
            case CorElementType.R4: case CorElementType.R8:
            case CorElementType.I:  case CorElementType.U:
            {
                var bytes = v.ReadGenericValue();
                if (bytes == null) return Cmp.MkUnknown();
                return DecodePrimitive(v.ElementType, bytes);
            }
            case CorElementType.ValueType:
            {
                // Guid/DateTime/etc. slot — compare by text if well-known.
                var typeName = DebuggerSession.TryGetCorValueTypeName(v);
                if (ClrStructDecoder.IsWellKnown(typeName) && v.Address != 0)
                {
                    var txt = ClrStructDecoder.TryWellKnownText(typeName, v.Address);
                    if (txt != null) return Cmp.MkStr(txt);
                }
                return Cmp.MkUnknown();
            }
            default:
                return Cmp.MkUnknown();
        }
    }

    /// <summary>Leaf value of a field path off an object slot, walked via ClrMD.</summary>
    private static Cmp ReadPathLeaf(CorValue v, string[] path)
    {
        var d = v.IsReference ? v.DereferencedValue : v;
        if (d == null || d.IsNull) return Cmp.MkNull();
        ulong addr = d.Address;
        if (addr == 0) return Cmp.MkNull();

        var heap = Program.Session.ClrRuntime.Heap;
        var cur = heap.GetObject(addr);
        if (cur.Type == null) return Cmp.MkUnknown();

        // Traverse intermediate reference fields.
        for (int i = 0; i < path.Length - 1; i++)
        {
            var f = ClrStructDecoder.FindField(cur.Type, path[i]);
            if (f == null || !f.IsObjectReference) return Cmp.MkUnknown();
            cur = cur.ReadObjectField(f.Name);
            if (cur.IsNull) return Cmp.MkNull();
        }

        var leaf = cur.Type == null ? null : ClrStructDecoder.FindField(cur.Type, path[path.Length - 1]);
        if (leaf == null) return Cmp.MkUnknown();
        return ReadClrFieldLeaf(cur, leaf);
    }

    private static Cmp ReadClrFieldLeaf(ClrObject obj, ClrInstanceField f)
    {
        if (f.IsObjectReference)
        {
            var o = obj.ReadObjectField(f.Name);
            if (o.IsNull) return Cmp.MkNull();
            if (o.Type?.IsString == true) return Cmp.MkStr(o.AsString(int.MaxValue));
            return Cmp.MkObj();
        }
        if (f.ElementType == ClrElementType.String) return Cmp.MkStr(obj.ReadStringField(f.Name));

        if (ClrStructDecoder.IsPrimitive(f.ElementType))
        {
            object num = ReadClrPrimitive(obj, f);
            if (f.Type != null && SafeIsEnum(f.Type))
                return Cmp.MkEnum(Convert.ToInt64(num), ClrStructDecoder.EnumName(f.Type, num));
            if (num is bool b) return Cmp.MkBool(b);
            return Cmp.MkNum(Convert.ToDouble(num), IsIntegral(f.ElementType), Convert.ToInt64(num));
        }

        if (f.IsValueType)
        {
            var vt = obj.ReadValueTypeField(f.Name);
            var txt = ClrStructDecoder.TryWellKnownText(f.Type?.Name, vt.Address);
            return txt != null ? Cmp.MkStr(txt) : Cmp.MkUnknown();
        }
        return Cmp.MkUnknown();
    }

    // ---- comparison ---------------------------------------------------------

    private static bool Apply(Cmp left, string op, Literal lit)
    {
        bool eq = op == "==";
        bool ne = op == "!=";

        // null comparisons
        if (lit.Kind == LitKind.Null)
        {
            bool isNull = left.Kind == CmpKind.Null;
            if (eq) return isNull;
            if (ne) return !isNull;
            return false; // ordering against null is undefined
        }
        if (left.Kind == CmpKind.Null)
            return ne; // non-null literal vs null value: equal? no. not-equal? yes.

        switch (lit.Kind)
        {
            case LitKind.Int:
                if (left.Kind == CmpKind.Num)
                    return CompareNum(left.Num, op, lit.Int, left.Integral, left.Long);
                if (left.Kind == CmpKind.Enum)
                    return CompareNum(left.Long, op, lit.Int, integral: true, lInt: left.Long);
                return false;

            case LitKind.Bool:
                if (left.Kind == CmpKind.Num) // tolerate bool stored as 0/1
                    return (eq || ne) && (eq == ((left.Long != 0) == lit.Bool));
                if (left.Kind == CmpKind.Bool)
                    return (eq || ne) && (eq == (left.Bool == lit.Bool));
                return false;

            case LitKind.Str:
                string? s = left.Kind == CmpKind.Str ? left.Str
                          : left.Kind == CmpKind.Enum ? left.Str
                          : null;
                if (s == null) return false;
                if (eq) return string.Equals(s, lit.Str, StringComparison.Ordinal);
                if (ne) return !string.Equals(s, lit.Str, StringComparison.Ordinal);
                return false; // ordering on strings unsupported

            default:
                return false;
        }
    }

    // Compare two numbers. When both sides are integral we compare as long for
    // exact equality on large ids; otherwise as double.
    private static bool CompareNum(double l, string op, long rInt, bool integral, long lInt = 0)
    {
        if (integral)
        {
            long a = lInt, b = rInt;
            return op switch
            {
                "==" => a == b, "!=" => a != b,
                ">=" => a >= b, "<=" => a <= b,
                ">"  => a > b,  "<"  => a < b,
                _ => false,
            };
        }
        double da = l, db = rInt;
        return op switch
        {
            "==" => da == db, "!=" => da != db,
            ">=" => da >= db, "<=" => da <= db,
            ">"  => da > db,  "<"  => da < db,
            _ => false,
        };
    }

    // ---- parsing ------------------------------------------------------------

    private static (string lhs, string op, string rhs) Split(string raw)
    {
        var s = raw.Trim();
        for (int i = 0; i < s.Length; i++)
        {
            foreach (var op in Ops)
            {
                if (i + op.Length <= s.Length && s.Substring(i, op.Length) == op)
                    return (s.Substring(0, i).Trim(), op, s.Substring(i + op.Length).Trim());
            }
        }
        throw new ArgumentException(
            $"unsupported condition '{raw}': missing comparison operator (==, !=, >=, <=, >, <)");
    }

    private enum LitKind { Int, Bool, Str, Null }
    private readonly struct Literal
    {
        public readonly LitKind Kind;
        public readonly long Int;
        public readonly bool Bool;
        public readonly string? Str;
        public Literal(LitKind kind, long i = 0, bool b = false, string? s = null)
        { Kind = kind; Int = i; Bool = b; Str = s; }
    }

    private static Literal ParseLiteral(string raw, string full)
    {
        var s = raw.Trim();
        if (s.Length == 0) throw new ArgumentException($"unsupported condition '{full}': empty right-hand side");

        if (s.Equals("null", StringComparison.OrdinalIgnoreCase)) return new Literal(LitKind.Null);
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return new Literal(LitKind.Bool, b: true);
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return new Literal(LitKind.Bool, b: false);

        if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
            return new Literal(LitKind.Str, s: s.Substring(1, s.Length - 2));

        // integer: decimal or 0x-hex (optionally signed)
        bool neg = s.StartsWith("-");
        var body = neg ? s.Substring(1) : s;
        long val;
        if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(body.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out val))
                throw new ArgumentException($"unsupported condition '{full}': bad hex literal '{raw}'");
        }
        else if (!long.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out val))
        {
            throw new ArgumentException($"unsupported condition '{full}': right-hand side must be an integer, bool, null, or quoted string");
        }
        return new Literal(LitKind.Int, i: neg ? -val : val);
    }

    // ---- low-level value reads ----------------------------------------------

    private static Cmp DecodePrimitive(CorElementType et, byte[] b) => et switch
    {
        CorElementType.Boolean => Cmp.MkBool(b.Length >= 1 && b[0] != 0),
        CorElementType.I1 => Cmp.MkInt((sbyte)b[0]),
        CorElementType.U1 => Cmp.MkInt(b[0]),
        CorElementType.I2 => Cmp.MkInt(BitConverter.ToInt16(b, 0)),
        CorElementType.U2 => Cmp.MkInt(BitConverter.ToUInt16(b, 0)),
        CorElementType.Char => Cmp.MkInt(BitConverter.ToUInt16(b, 0)),
        CorElementType.I4 => Cmp.MkInt(BitConverter.ToInt32(b, 0)),
        CorElementType.U4 => Cmp.MkInt(BitConverter.ToUInt32(b, 0)),
        CorElementType.I8 => Cmp.MkInt(BitConverter.ToInt64(b, 0)),
        CorElementType.U8 => Cmp.MkInt(unchecked((long)BitConverter.ToUInt64(b, 0))),
        CorElementType.R4 => Cmp.MkNum(BitConverter.ToSingle(b, 0), false, 0),
        CorElementType.R8 => Cmp.MkNum(BitConverter.ToDouble(b, 0), false, 0),
        CorElementType.I => Cmp.MkInt(b.Length == 8 ? BitConverter.ToInt64(b, 0) : BitConverter.ToInt32(b, 0)),
        CorElementType.U => Cmp.MkInt(b.Length == 8 ? unchecked((long)BitConverter.ToUInt64(b, 0)) : BitConverter.ToUInt32(b, 0)),
        _ => Cmp.MkUnknown(),
    };

    private static object ReadClrPrimitive(ClrObject obj, ClrInstanceField f) => f.ElementType switch
    {
        ClrElementType.Boolean => obj.ReadField<bool>(f.Name),
        ClrElementType.Char    => (int)obj.ReadField<char>(f.Name),
        ClrElementType.Int8    => obj.ReadField<sbyte>(f.Name),
        ClrElementType.UInt8   => obj.ReadField<byte>(f.Name),
        ClrElementType.Int16   => obj.ReadField<short>(f.Name),
        ClrElementType.UInt16  => obj.ReadField<ushort>(f.Name),
        ClrElementType.Int32   => obj.ReadField<int>(f.Name),
        ClrElementType.UInt32  => obj.ReadField<uint>(f.Name),
        ClrElementType.Int64   => obj.ReadField<long>(f.Name),
        ClrElementType.UInt64  => unchecked((long)obj.ReadField<ulong>(f.Name)),
        ClrElementType.Float   => (double)obj.ReadField<float>(f.Name),
        ClrElementType.Double  => obj.ReadField<double>(f.Name),
        ClrElementType.NativeInt => obj.ReadField<long>(f.Name),
        ClrElementType.Pointer => obj.ReadField<long>(f.Name),
        _ => 0,
    };

    private static bool IsIntegral(ClrElementType et) => et != ClrElementType.Float && et != ClrElementType.Double;

    private static bool SafeIsEnum(ClrType t) { try { return t.IsEnum; } catch { return false; } }

    // ---- comparable leaf ----------------------------------------------------

    private enum CmpKind { Null, Num, Bool, Str, Enum, Obj, Unknown }
    private readonly struct Cmp
    {
        public readonly CmpKind Kind;
        public readonly double Num;
        public readonly long Long;
        public readonly bool Integral;
        public readonly bool Bool;
        public readonly string? Str;

        private Cmp(CmpKind kind, double num = 0, long l = 0, bool integral = false, bool b = false, string? s = null)
        { Kind = kind; Num = num; Long = l; Integral = integral; Bool = b; Str = s; }

        public static Cmp MkNull() => new(CmpKind.Null);
        public static Cmp MkObj() => new(CmpKind.Obj);
        public static Cmp MkUnknown() => new(CmpKind.Unknown);
        public static Cmp MkStr(string? s) => new(CmpKind.Str, s: s);
        public static Cmp MkBool(bool b) => new(CmpKind.Bool, b: b);
        public static Cmp MkInt(long v) => new(CmpKind.Num, num: v, l: v, integral: true);
        public static Cmp MkNum(double d, bool integral, long l) => new(CmpKind.Num, num: d, l: l, integral: integral);
        public static Cmp MkEnum(long v, string? name) => new(CmpKind.Enum, l: v, s: name);
    }
}
