using System;
using System.Linq;
using System.Text.RegularExpressions;
using dndbg.COM.CorDebug;
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
            "[DEBUG] Read a value expression against the currently-paused frame WITHOUT running any code (passive object-graph read — no func-eval / method calls). expr starts with a root — arg<i>, local<i>, or 'this' (= arg0 on instance methods) — then a dotted field/auto-property path. Examples: \"arg0\", \"this.Entity\", \"arg1.Owner.Name\", \"local0.UId\". Property names resolve their backing field; Guid/DateTime/enum/struct leaves are decoded; an object leaf returns {kind:object,type,address} to drill into with debug_heap_read_object. Params: {expr:string, frameIndex?:int=0}. Method/property invocation (.ToString(), GetX()) is unsupported by design — ICorDebug func-eval can deadlock a live worker.",
            p => Program.Session.OnDbg<object>(() => Evaluate(p)));
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
