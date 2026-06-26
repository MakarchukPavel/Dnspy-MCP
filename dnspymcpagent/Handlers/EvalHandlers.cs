using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using dndbg.COM.CorDebug;
using dndbg.COM.MetaData;
using dndbg.Engine;
using DnSpyMcp.Agent.Services;
using Microsoft.Diagnostics.Runtime;
using Newtonsoft.Json.Linq;

namespace DnSpyMcp.Agent.Handlers;

/// <summary>
/// Passive expression evaluation against the currently-paused frame. Reads an
/// object-graph path (<c>arg0.Entity.Name</c>, <c>this.Configuration</c>,
/// <c>local1.UId</c>) WITHOUT executing any target code — the root is read off
/// the frame via ICorDebug, then field/auto-property hops are walked with ClrMD
/// off the dereferenced object address (a plain memory read).
///
/// Method / property invocation (real ICorDebug func-eval) is intentionally
/// NOT implemented: driving <c>ICorDebugEval</c> on a live IIS/w3wp worker can
/// deadlock or corrupt the process if another thread holds a lock at the
/// eval point. Reading fields and auto-property backing fields covers the
/// common "what's in this object graph" need without that risk. Guid/DateTime/
/// enum/struct leaves are decoded via <see cref="ClrStructDecoder"/>; object
/// leaves return {kind:object,type,address} to drill into with heap tools.
/// </summary>
public static class EvalHandlers
{
    private static readonly Regex RootRe =
        new(@"^(arg|local)(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static void Register(Dispatcher d)
    {
        d.Register("eval.expression",
            "[DEBUG] Read a value expression against the currently-paused frame WITHOUT running any code (passive object-graph read — no func-eval / method calls). expr starts with a root — arg<i>, local<i>, or 'this' (= arg0 on instance methods) — then a dotted field/auto-property path. Examples: \"arg0\", \"this.Entity\", \"arg1.Owner.Name\", \"local0.UId\". Property names resolve their backing field; Guid/DateTime/enum/struct leaves are decoded; an object leaf returns {kind:object,type,address} to drill into with debug_heap_read_object. Params: {expr:string, frameIndex?:int=0}. For computed properties / methods that must RUN code, use eval.call.",
            p => Program.Session.OnDbg<object>(() => Evaluate(p)));

        d.Register("eval.call",
            "[DEBUG] Invoke an instance method or property getter on the paused frame and return the result — real func-eval via ICorDebug (RUNS target code). expr = \"<receiver>.<member>[<TypeArgs>]([args])\". Receiver is a root (arg<i>/local<i>/'this') with an OPTIONAL field/auto-property path, e.g. \"this.Entity.GetTypedColumnValue<System.Guid>('Id')\". A bare <member> (no parens) is a property getter (get_<member>) then a 0-arg method; <member>(...) calls a method; generic methods take explicit type args. Arguments are literals (integer dec/0x-hex, true/false, null, quoted string) OR value expressions arg/local/this[.field] resolved to live objects, e.g. \"arg0.Equals(arg1)\", \"this.Save(arg0.Context)\". Overloads selected by (arg count, type-arg count, AND argument type — a string literal picks (string) over (SomeClass)). Resolves across the type hierarchy. Runs on the paused thread with other threads SUSPENDED, timeout (default 2000ms) + abort. ⚠ Runs target code: if it blocks on a lock another thread holds it stalls until the timeout. Result decoded like eval.expression; a thrown exception returns {kind:'exception',type,message}; timeout returns {kind:'timeout'}. Params: {expr:string, frameIndex?:int=0, timeoutMs?:int=2000}. Not supported: nested-generic type args (List<int>), value-type (struct) receivers.",
            p => Program.Session.OnDbg<object>(() => CallExpr(p)));
    }

    private static object Evaluate(JObject? p)
    {
        var expr = (Dispatcher.Req<string>(p, "expr") ?? string.Empty).Trim();
        var frameIndex = Dispatcher.Opt<int>(p, "frameIndex", 0);
        if (expr.Length == 0) throw new ArgumentException("expr is required");
        if (expr.IndexOf('(') >= 0 || expr.IndexOf(')') >= 0)
            throw new ArgumentException("method/property invocation is not supported (passive reads only) — use a field/auto-property path like arg0.Name");
        if (expr.IndexOf('[') >= 0)
            throw new ArgumentException("indexing is not supported yet — read the collection object then use debug_heap_read_array");

        var parts = expr.Split('.');
        var root = parts[0].Trim();
        var path = parts.Skip(1).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

        var dbg = Program.Session.DnDebugger;
        if (dbg.ProcessState != DebuggerProcessState.Paused)
            throw new InvalidOperationException($"cannot evaluate: state={dbg.ProcessState} (must be Paused)");
        var thread = dbg.Current?.Thread
            ?? throw new InvalidOperationException("no current thread on the active pause");
        var frame = FrameHandlers.WalkToFrame(thread, frameIndex)
            ?? throw new ArgumentException($"frameIndex {frameIndex} not found on the paused thread");

        var rootVal = ResolveRoot(frame, root);
        if (rootVal == null)
            return new { expr, frameIndex, value = new { kind = "unavailable", reason = "root slot optimized away or not in scope" } };

        object? value = path.Length == 0 ? FrameHandlers.ReadValue(rootVal) : WalkPath(rootVal, path);
        return new { expr, frameIndex, value };
    }

    // ---- eval.call (func-eval) ----------------------------------------------

    private static object CallExpr(JObject? p)
    {
        var expr = (Dispatcher.Req<string>(p, "expr") ?? string.Empty).Trim();
        var frameIndex = Dispatcher.Opt<int>(p, "frameIndex", 0);
        var timeoutMs = Dispatcher.Opt<int>(p, "timeoutMs", 2000);
        if (expr.Length == 0) throw new ArgumentException("expr is required");

        // Split into top-level dot segments (ignoring dots inside <...>, (...),
        // or quotes). The last segment is the invocation; segment 0 is the root;
        // anything between is a field path walked to reach the receiver.
        var segments = SplitTopLevelDots(expr);
        if (segments.Count < 2)
            throw new ArgumentException("eval.call expects '<receiver>.<member>[<TypeArgs>]([args])' — e.g. \"arg0.ToString()\", \"this.Entity.GetTypedColumnValue<System.Guid>('Id')\", \"arg0.Equals(arg1)\"");
        var root = segments[0].Trim();
        var receiverPath = segments.Skip(1).Take(segments.Count - 2).Select(s => s.Trim()).ToArray();
        var (member, typeArgNames, argLits, isCall) = ParseInvocation(segments[segments.Count - 1].Trim());

        var dbg = Program.Session.DnDebugger;
        if (dbg.ProcessState != DebuggerProcessState.Paused)
            throw new InvalidOperationException($"cannot eval.call: state={dbg.ProcessState} (must be Paused)");
        var thread = dbg.Current?.Thread
            ?? throw new InvalidOperationException("no current thread on the active pause");
        var frame = FrameHandlers.WalkToFrame(thread, frameIndex)
            ?? throw new ArgumentException($"frameIndex {frameIndex} not found on the paused thread");

        var rootVal = ResolveValueExpr(frame, root, receiverPath)
            ?? throw new ArgumentException($"receiver '{string.Join(".", new[] { root }.Concat(receiverPath))}' is unavailable (optimized away, null, or a field not found)");
        if (!rootVal.IsReference)
            throw new ArgumentException("eval.call requires a reference-type receiver (value-type/struct receivers not supported)");
        var deref = rootVal.DereferencedValue;
        if (deref == null || deref.IsNull) return new { expr, value = new { kind = "null" } };
        var exactType = deref.ExactType
            ?? throw new InvalidOperationException("could not resolve the receiver's runtime type");
        var typeName = DebuggerSession.TryGetCorValueTypeName(deref) ?? "<receiver>";

        using var dnEval = dbg.CreateEval(System.Threading.CancellationToken.None, suspendOtherThreads: true);
        dnEval.SetThread(thread);
        dnEval.SetTimeout(TimeSpan.FromMilliseconds(Math.Max(200, timeoutMs)));

        // Resolve method type args (generics) to CorTypes.
        var methodTypeArgs = typeArgNames
            .Select(n => ResolveCorType(n) ?? throw new ArgumentException($"generic type argument '{n}' could not be resolved to a loaded type"))
            .ToArray();

        // Resolve the overload + build the argument list (this + literals).
        CorFunction func; CorType declType; CorValue[] args;
        if (!isCall)
        {
            var r0 = ResolveMethodForCall(exactType, new[] { "get_" + member, member }, System.Array.Empty<ArgLit>(), 0)
                ?? throw new ArgumentException($"member '{member}' not found as a 0-arg property getter or method on {typeName} or its base types");
            (func, declType) = r0;
            args = new[] { rootVal };
        }
        else
        {
            var r0 = ResolveMethodForCall(exactType, new[] { member }, argLits, typeArgNames.Count)
                ?? throw new ArgumentException($"no overload of '{member}' taking {argLits.Count} argument(s) and {typeArgNames.Count} type argument(s) found on {typeName} or its base types");
            (func, declType) = r0;
            var proc = thread.CorThread?.Process;
            var list = new List<CorValue> { rootVal };
            foreach (var lit in argLits) list.Add(MakeArgValue(dnEval, proc, frame, lit));
            args = list.ToArray();
        }

        // CallParameterizedFunction wants the declaring type's generic args first,
        // then the method's own type args.
        var typeArgs = declType.TypeParameters.Concat(methodTypeArgs).ToArray();

        EvalResult? res;
        int hr;
        try
        {
            res = dnEval.Call(func, typeArgs, args, out hr);
        }
        catch (TimeoutException)
        {
            return new { expr, frameIndex, value = new { kind = "timeout", reason = $"func-eval exceeded {timeoutMs}ms and was aborted" } };
        }
        if (res == null)
            throw new InvalidOperationException($"func-eval setup failed (hr=0x{hr:X8})");

        var r = res.Value;
        if (r.WasCancelled)
            return new { expr, frameIndex, value = new { kind = "timeout", reason = "func-eval cancelled/timed out" } };
        if (r.WasException)
            return new { expr, frameIndex, value = new { kind = "exception", type = DebuggerSession.TryGetCorValueTypeName(r.ResultOrException), message = TryExceptionMessage(r.ResultOrException) } };

        return new { expr, frameIndex, value = r.ResultOrException == null ? (object)new { kind = "void" } : (FrameHandlers.ReadValue(r.ResultOrException) ?? new { kind = "void" }) };
    }

    // ---- parsing: <member>[<T,...>]([arg,...]) -------------------------------

    private enum ArgKind { Int, Bool, Str, Null, Expr }

    // An argument that is itself a value expression: arg/local/this[.field]*.
    private static readonly Regex ExprRe =
        new(@"^(arg\d+|local\d+|this)(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly struct ArgLit
    {
        public readonly ArgKind Kind; public readonly long Int; public readonly bool Bool; public readonly string? Str;
        public ArgLit(ArgKind k, long i = 0, bool b = false, string? s = null) { Kind = k; Int = i; Bool = b; Str = s; }
    }

    private static (string member, List<string> typeArgs, List<ArgLit> args, bool isCall) ParseInvocation(string s)
    {
        int i = 0;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        var member = s.Substring(0, i).Trim();
        if (member.Length == 0) throw new ArgumentException("missing member name after '.'");
        var rest = s.Substring(i).Trim();

        var typeArgs = new List<string>();
        var args = new List<ArgLit>();
        bool isCall = false;

        if (rest.StartsWith("<", StringComparison.Ordinal))
        {
            int gt = rest.IndexOf('>');
            if (gt < 0) throw new ArgumentException("unterminated generic type-argument list (missing '>')");
            foreach (var t in SplitTopLevel(rest.Substring(1, gt - 1)))
                if (t.Trim().Length > 0) typeArgs.Add(t.Trim());
            rest = rest.Substring(gt + 1).Trim();
            isCall = true;
        }

        if (rest.StartsWith("(", StringComparison.Ordinal))
        {
            int close = rest.LastIndexOf(')');
            if (close < 0) throw new ArgumentException("unterminated argument list (missing ')')");
            foreach (var a in SplitTopLevel(rest.Substring(1, close - 1)))
                if (a.Trim().Length > 0) args.Add(ParseArgLiteral(a.Trim()));
            isCall = true;
        }
        else if (rest.Length > 0 && !isCall)
            throw new ArgumentException($"unexpected trailing text after member: '{rest}'");

        return (member, typeArgs, args, isCall);
    }

    // Split a comma-separated list, respecting single/double quotes (no nesting).
    private static IEnumerable<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        int start = 0; char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c == '"' || c == '\'') quote = c;
            else if (c == ',') { parts.Add(s.Substring(start, i - start)); start = i + 1; }
        }
        if (start <= s.Length) parts.Add(s.Substring(start));
        return parts;
    }

    // Split on top-level dots, ignoring dots inside <...>, (...), or quotes
    // (so "this.Entity.GetTypedColumnValue<System.Guid>('a.b')" splits into
    // [this, Entity, GetTypedColumnValue<System.Guid>('a.b')]).
    private static List<string> SplitTopLevelDots(string s)
    {
        var parts = new List<string>();
        int start = 0, angle = 0, paren = 0; char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            switch (c)
            {
                case '"': case '\'': quote = c; break;
                case '<': angle++; break;
                case '>': if (angle > 0) angle--; break;
                case '(': paren++; break;
                case ')': if (paren > 0) paren--; break;
                case '.':
                    if (angle == 0 && paren == 0) { parts.Add(s.Substring(start, i - start)); start = i + 1; }
                    break;
            }
        }
        parts.Add(s.Substring(start));
        return parts;
    }

    /// <summary>
    /// Resolve a value expression — a root slot (arg/local/this) followed by a
    /// field/auto-property path — to a CorValue, reading intermediate fields via
    /// ICorDebug (<see cref="CorValue.GetFieldValue"/>) so the result stays a
    /// live CorValue usable as a receiver or argument. Returns null if the root
    /// is unavailable, a hop is null, or a field name doesn't resolve.
    /// </summary>
    private static CorValue? ResolveValueExpr(CorFrame frame, string root, string[] fieldPath)
    {
        var v = ResolveRoot(frame, root);
        if (v == null) return null;
        foreach (var seg in fieldPath)
        {
            var d = v.IsReference ? v.DereferencedValue : v;
            if (d == null || d.IsNull) return null;
            var t = d.ExactType;
            if (t == null) return null;

            CorClass? declClass = null; uint ftok = 0;
            for (var ct = t; ct != null; ct = ct.Base)
            {
                if (!ct.HasClass) continue;
                var mdi = ct.GetMetaDataImport(out uint typeToken);
                if (mdi == null || typeToken == 0) continue;
                uint f = MetaDataUtils.FindFieldByName(mdi, typeToken, seg);
                if (f != 0) { declClass = ct.Class; ftok = f; break; }
            }
            if (declClass == null || ftok == 0) return null;
            v = d.GetFieldValue(declClass, ftok);
            if (v == null) return null;
        }
        return v;
    }

    private static ArgLit ParseArgLiteral(string s)
    {
        if (s.Equals("null", StringComparison.OrdinalIgnoreCase)) return new ArgLit(ArgKind.Null);
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return new ArgLit(ArgKind.Bool, b: true);
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return new ArgLit(ArgKind.Bool, b: false);
        if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
            return new ArgLit(ArgKind.Str, s: s.Substring(1, s.Length - 2));
        bool neg = s.StartsWith("-", StringComparison.Ordinal);
        var body = neg ? s.Substring(1) : s;
        long val;
        if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(body.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out val))
                throw new ArgumentException($"bad hex argument '{s}'");
        }
        else if (!long.TryParse(body, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out val))
        {
            // Not a literal — accept a value expression (arg/local/this[.field]*),
            // resolved to a CorValue at call time.
            if (ExprRe.IsMatch(s)) return new ArgLit(ArgKind.Expr, s: s);
            throw new ArgumentException($"unsupported argument '{s}' (expected integer, true/false, null, a quoted string, or an arg/local/this[.field] expression)");
        }
        return new ArgLit(ArgKind.Int, i: neg ? -val : val);
    }

    // ---- argument marshalling -----------------------------------------------

    private static CorValue MakeArgValue(DnEval dnEval, dndbg.Engine.CorProcess? proc, CorFrame frame, ArgLit lit)
    {
        if (lit.Kind == ArgKind.Expr)
        {
            var segs = SplitTopLevelDots(lit.Str ?? string.Empty);
            return ResolveValueExpr(frame, segs[0].Trim(), segs.Skip(1).Select(x => x.Trim()).ToArray())
                ?? throw new InvalidOperationException($"argument expression '{lit.Str}' could not be resolved (optimized away, null, or field not found)");
        }
        switch (lit.Kind)
        {
            case ArgKind.Null:
                return dnEval.CreateNull();
            case ArgKind.Bool:
            {
                var v = dnEval.eval.CreateValue(CorElementType.Boolean) ?? throw new InvalidOperationException("could not create a Boolean value");
                v.WriteGenericValue(new byte[] { (byte)(lit.Bool ? 1 : 0) }, proc);
                return v;
            }
            case ArgKind.Str:
            {
                var rs = dnEval.CreateString(lit.Str ?? string.Empty, out int hr);
                if (rs == null || !rs.Value.NormalResult || rs.Value.ResultOrException == null)
                    throw new InvalidOperationException($"could not create a string value (hr=0x{hr:X8})");
                return rs.Value.ResultOrException;
            }
            default: // Int — narrow to I4 when it fits, else I8
            {
                bool fitsI4 = lit.Int >= int.MinValue && lit.Int <= int.MaxValue;
                var et = fitsI4 ? CorElementType.I4 : CorElementType.I8;
                var v = dnEval.eval.CreateValue(et) ?? throw new InvalidOperationException("could not create an integer value");
                v.WriteGenericValue(fitsI4 ? BitConverter.GetBytes((int)lit.Int) : BitConverter.GetBytes(lit.Int), proc);
                return v;
            }
        }
    }

    // ---- method + type resolution -------------------------------------------

    /// <summary>
    /// Walk the type hierarchy from <paramref name="startType"/> up its base
    /// chain; return the (CorFunction, declaring CorType) for the best-matching
    /// overload of any of <paramref name="names"/> with the given generic arity
    /// and argument count. Among same-arity overloads the one whose parameter
    /// types best fit the supplied argument literals wins (so a string literal
    /// picks `(string)` over `(EntitySchemaColumn)`). Base CorTypes carry their
    /// own metadata, so inherited / cross-module members resolve too.
    /// </summary>
    private static (CorFunction func, CorType declType)? ResolveMethodForCall(CorType startType, string[] names, IReadOnlyList<ArgLit> args, int genArity)
    {
        int argCount = args.Count;
        for (var t = startType; t != null; t = t.Base)
        {
            if (!t.HasClass) continue;
            var mdi = t.GetMetaDataImport(out uint typeToken);
            if (mdi == null || typeToken == 0) continue;

            CorFunction? bestFunc = null;
            int bestScore = int.MinValue;
            foreach (var name in names)
            {
                foreach (var mt in dndbg.Engine.MDAPI.GetMethodTokens(mdi, typeToken))
                {
                    if (!string.Equals(dndbg.Engine.MDAPI.GetMethodName(mdi, mt), name, StringComparison.Ordinal)) continue;

                    int ga, pc, score;
                    if (SignatureUtils.TryGetMethodSig(mdi, mt, out ga, out var ptypes))
                    {
                        pc = ptypes.Length;
                        score = ScoreArgs(args, ptypes);
                    }
                    else
                    {
                        // Couldn't parse the full signature — fall back to arity only.
                        if (!SignatureUtils.TryGetMethodArity(mdi, mt, out ga, out pc)) continue;
                        score = int.MinValue + 1; // accept, but lose to any type-scored match
                    }
                    if (pc != argCount || ga != genArity) continue;

                    if (score > bestScore)
                    {
                        var func = t.Class?.Module?.GetFunctionFromToken(mt);
                        if (func != null) { bestFunc = func; bestScore = score; }
                    }
                }
            }
            if (bestFunc != null) return (bestFunc, t);
        }
        return null;
    }

    // Score how well the argument literals fit a candidate's parameter types;
    // higher = better. Lets overload resolution prefer an exact-ish parameter
    // type (string->String) over a merely-reference-compatible one.
    private static int ScoreArgs(IReadOnlyList<ArgLit> args, CorElementType[] ptypes)
    {
        int s = 0;
        for (int i = 0; i < args.Count && i < ptypes.Length; i++) s += MatchScore(args[i].Kind, ptypes[i]);
        return s;
    }

    private static bool IsNumericElement(CorElementType et) => et switch
    {
        CorElementType.Char or CorElementType.I1 or CorElementType.U1
        or CorElementType.I2 or CorElementType.U2 or CorElementType.I4 or CorElementType.U4
        or CorElementType.I8 or CorElementType.U8 or CorElementType.R4 or CorElementType.R8
        or CorElementType.I or CorElementType.U => true,
        _ => false,
    };

    private static int MatchScore(ArgKind kind, CorElementType pet)
    {
        bool isGenericVar = pet == CorElementType.Var || pet == CorElementType.MVar;
        switch (kind)
        {
            case ArgKind.Str:
                if (pet == CorElementType.String) return 3;
                if (pet == CorElementType.Object) return 2;
                if (isGenericVar) return 2;
                if (pet == CorElementType.Class || pet == CorElementType.GenericInst) return 1;
                return -3;
            case ArgKind.Int:
                if (IsNumericElement(pet)) return 3;
                if (pet == CorElementType.Object || isGenericVar) return 1;
                if (pet == CorElementType.Boolean) return 0;
                return -3;
            case ArgKind.Bool:
                if (pet == CorElementType.Boolean) return 3;
                if (pet == CorElementType.Object || isGenericVar) return 1;
                if (IsNumericElement(pet)) return 0;
                return -3;
            case ArgKind.Expr: // an object expression — prefer a class param over string/value types
                if (pet == CorElementType.Class) return 3;
                if (pet == CorElementType.Object || isGenericVar || pet == CorElementType.GenericInst || pet == CorElementType.SZArray) return 2;
                if (pet == CorElementType.String) return 1;
                return -3;
            default: // Null — fits any reference type, not a value type
                if (pet == CorElementType.Class || pet == CorElementType.Object || pet == CorElementType.String
                    || pet == CorElementType.SZArray || pet == CorElementType.GenericInst || isGenericVar) return 2;
                return -3;
        }
    }

    private static readonly HashSet<string> KnownValueTypes = new(StringComparer.Ordinal)
    {
        "System.Boolean","System.Byte","System.SByte","System.Char","System.Int16","System.UInt16",
        "System.Int32","System.UInt32","System.Int64","System.UInt64","System.Single","System.Double",
        "System.IntPtr","System.UIntPtr","System.Decimal","System.Guid","System.DateTime","System.TimeSpan",
        "System.DateTimeOffset",
    };

    /// <summary>Resolve a (non-generic) type name to a CorType by scanning loaded modules.</summary>
    private static CorType? ResolveCorType(string typeName)
    {
        typeName = typeName.Trim();
        foreach (var (mod, mdi) in EnumModulesWithMeta())
        {
            uint tok = MetaDataUtils.FindTypeDefByName(mdi, typeName);
            if (tok == 0) continue;
            var cls = mod.GetClassFromToken(tok);
            if (cls == null) continue;
            var et = IsValueTypeToken(mdi, tok, typeName) ? CorElementType.ValueType : CorElementType.Class;
            return cls.GetParameterizedType(et, System.Array.Empty<CorType>());
        }
        return null;
    }

    private static bool IsValueTypeToken(IMetaDataImport mdi, uint typeToken, string typeName)
    {
        if (KnownValueTypes.Contains(typeName)) return true;
        uint ext = dndbg.Engine.MDAPI.GetTypeDefExtends(mdi, typeToken);
        if (ext == 0) return false;
        string? baseName = (ext & 0xFF000000) == 0x01000000
            ? dndbg.Engine.MDAPI.GetTypeRefName(mdi, ext)
            : MetaDataUtils.FullTypeName(mdi, ext);
        return baseName == "System.ValueType" || baseName == "System.Enum";
    }

    private static IEnumerable<(CorModule mod, IMetaDataImport mdi)> EnumModulesWithMeta()
    {
        var dbg = Program.Session.DnDebugger;
        foreach (var proc in dbg.Processes)
            foreach (var ad in proc.AppDomains)
                foreach (var asm in ad.Assemblies)
                    foreach (var mod in asm.Modules)
                    {
                        var cm = mod.CorModule;
                        var mdi = cm?.GetMetaDataInterface<IMetaDataImport>();
                        if (cm != null && mdi != null) yield return (cm, mdi);
                    }
    }

    private static string? TryExceptionMessage(CorValue? exVal)
    {
        try
        {
            var d = exVal?.IsReference == true ? exVal.DereferencedValue : exVal;
            ulong addr = d == null ? 0 : d.Address;
            if (addr == 0) return null;
            var o = Program.Session.ClrRuntime.Heap.GetObject(addr);
            return o.Type == null ? null : o.ReadStringField("_message");
        }
        catch { return null; }
    }

    private static CorValue? ResolveRoot(CorFrame frame, string root)
    {
        if (root.Equals("this", StringComparison.OrdinalIgnoreCase))
            return frame.GetILArgument(0, out _);
        var m = RootRe.Match(root);
        if (!m.Success)
            throw new ArgumentException($"expression root must be arg<n>, local<n>, or 'this' (got '{root}')");
        uint idx = uint.Parse(m.Groups[2].Value);
        return m.Groups[1].Value.Equals("arg", StringComparison.OrdinalIgnoreCase)
            ? frame.GetILArgument(idx, out _)
            : frame.GetILLocal(idx, out _);
    }

    private static object? WalkPath(CorValue rootVal, string[] path)
    {
        var d = rootVal.IsReference ? rootVal.DereferencedValue : rootVal;
        if (d == null || d.IsNull) return new { kind = "null" };
        ulong addr = d.Address;
        if (addr == 0) return new { kind = "null" };

        var heap = Program.Session.ClrRuntime.Heap;
        var cur = heap.GetObject(addr);
        if (cur.Type == null) return new { kind = "unknown", reason = "no managed type at root address" };

        // Traverse intermediate reference fields.
        for (int i = 0; i < path.Length - 1; i++)
        {
            var f = cur.Type == null ? null : ClrStructDecoder.FindField(cur.Type, path[i]);
            if (f == null) return new { kind = "error", error = $"field '{path[i]}' not found on {cur.Type?.Name}" };
            if (!f.IsObjectReference) return new { kind = "error", error = $"cannot traverse into non-reference field '{path[i]}' ({f.Type?.Name})" };
            cur = cur.ReadObjectField(f.Name);
            if (cur.IsNull) return new { kind = "null", at = path[i] };
        }

        var leaf = cur.Type == null ? null : ClrStructDecoder.FindField(cur.Type, path[path.Length - 1]);
        if (leaf == null) return new { kind = "error", error = $"field '{path[path.Length - 1]}' not found on {cur.Type?.Name}" };
        return RenderClrField(cur, leaf);
    }

    /// <summary>Render a ClrMD field leaf into the same shape the heap reader uses.</summary>
    private static object? RenderClrField(ClrObject obj, ClrInstanceField f)
    {
        try
        {
            if (f.IsObjectReference)
            {
                var o = obj.ReadObjectField(f.Name);
                if (o.IsNull) return new { kind = "null" };
                if (o.Type?.IsString == true) return new { kind = "string", value = o.AsString(int.MaxValue) };
                return new { kind = "object", type = o.Type?.Name, address = $"0x{o.Address:X}" };
            }
            if (f.ElementType == ClrElementType.String)
                return new { kind = "string", value = obj.ReadStringField(f.Name) };
            if (ClrStructDecoder.IsPrimitive(f.ElementType))
            {
                object? val = ReadPrimitive(obj, f);
                return new { kind = "primitive", elementType = f.ElementType.ToString(), value = ClrStructDecoder.DecorateEnum(f.Type, val) };
            }
            if (f.IsValueType)
                return ClrStructDecoder.DecodeValueType(obj.ReadValueTypeField(f.Name));
            return new { kind = "raw", elementType = f.ElementType.ToString() };
        }
        catch (Exception ex) { return new { kind = "error", error = ex.Message }; }
    }

    private static object? ReadPrimitive(ClrObject obj, ClrInstanceField f) => f.ElementType switch
    {
        ClrElementType.Boolean   => obj.ReadField<bool>(f.Name),
        ClrElementType.Char      => (int)obj.ReadField<char>(f.Name),
        ClrElementType.Int8      => obj.ReadField<sbyte>(f.Name),
        ClrElementType.UInt8     => obj.ReadField<byte>(f.Name),
        ClrElementType.Int16     => obj.ReadField<short>(f.Name),
        ClrElementType.UInt16    => obj.ReadField<ushort>(f.Name),
        ClrElementType.Int32     => obj.ReadField<int>(f.Name),
        ClrElementType.UInt32    => obj.ReadField<uint>(f.Name),
        ClrElementType.Int64     => obj.ReadField<long>(f.Name),
        ClrElementType.UInt64    => obj.ReadField<ulong>(f.Name),
        ClrElementType.Float     => obj.ReadField<float>(f.Name),
        ClrElementType.Double    => obj.ReadField<double>(f.Name),
        ClrElementType.NativeInt => obj.ReadField<long>(f.Name),
        ClrElementType.Pointer   => obj.ReadField<long>(f.Name),
        _ => $"<{f.ElementType}>",
    };
}
