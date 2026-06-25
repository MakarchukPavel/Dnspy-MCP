using System;
using System.Collections.Generic;
using dndbg.COM.CorDebug;
using dndbg.Engine;
using DnSpyMcp.Agent.Services;

namespace DnSpyMcp.Agent.Handlers;

/// <summary>
/// Handlers for inspecting the currently-paused frame (D3): read locals,
/// read arguments. Built on dndbg's per-index <c>CorFrame.GetILLocal</c> /
/// <c>GetILArgument</c> APIs (no enumeration is exposed by ICorDebug, so
/// we probe sequentially until a slot returns null/HR error and stop).
/// </summary>
public static class FrameHandlers
{
    private const int MaxProbeSlots = 64;

    public static void Register(Dispatcher d)
    {
        d.Register("frame.locals",
            "[DEBUG] Read managed local variables in the currently-paused frame. Frame defaults to the deepest IL frame on the current pause-thread; pass {frameIndex:int} to pick another. Primitives are decoded; references return type+address. Params: {frameIndex?:int=0}.",
            p => Program.Session.OnDbg<object>(() => ReadFrameSlots(p, locals: true)));

        d.Register("frame.arguments",
            "[DEBUG] Read managed arguments (this + parameters) of the currently-paused frame. Same shape as frame.locals. Params: {frameIndex?:int=0}.",
            p => Program.Session.OnDbg<object>(() => ReadFrameSlots(p, locals: false)));
    }

    private static object ReadFrameSlots(Newtonsoft.Json.Linq.JObject? p, bool locals)
    {
        var frameIndex = Dispatcher.Opt<int>(p, "frameIndex", 0);
        var dbg = Program.Session.DnDebugger;
        if (dbg.ProcessState != DebuggerProcessState.Paused)
            throw new InvalidOperationException($"cannot read frame: state={dbg.ProcessState} (must be Paused)");

        var thread = dbg.Current?.Thread
            ?? throw new InvalidOperationException("no current thread on the active pause");
        var frame = WalkToFrame(thread, frameIndex);
        if (frame == null)
            throw new ArgumentException($"frameIndex {frameIndex} not found on the paused thread");

        var rows = new List<object>();
        int lastReadable = -1;
        for (uint i = 0; i < MaxProbeSlots; i++)
        {
            CorValue? v = locals
                ? frame.GetILLocal(i, out _)
                : frame.GetILArgument(i, out _);
            if (v is null)
            {
                // A null slot is EITHER optimized away (the JIT dropped the value
                // on an optimized/release build) OR past the last real slot —
                // ICorDebug can't tell us which. Record a placeholder and KEEP
                // probing instead of breaking, so an optimized-away slot 0 (e.g.
                // 'this' on an instance method, or an inlined extension-method
                // receiver) no longer truncates every argument/local after it.
                // Trailing placeholders are trimmed below.
                rows.Add(new
                {
                    index = (int)i,
                    kind = locals ? "local" : "arg",
                    elementType = (string?)null,
                    value = (object)new { kind = "unavailable", reason = "optimized away or not in scope" },
                });
                continue;
            }
            rows.Add(new
            {
                index = (int)i,
                kind = locals ? "local" : "arg",
                elementType = v.ElementType.ToString(),
                value = ReadValue(v),
            });
            lastReadable = rows.Count - 1;
        }
        // Drop trailing placeholders: they are past the last readable slot (end of
        // list). A gap BEFORE the last readable slot is kept and labeled
        // 'unavailable'. NOTE: if the very last real slot was optimized away it
        // gets trimmed too — unavoidable without method metadata.
        if (lastReadable + 1 < rows.Count)
            rows.RemoveRange(lastReadable + 1, rows.Count - (lastReadable + 1));
        return new
        {
            frameIndex,
            kind = locals ? "locals" : "arguments",
            count = rows.Count,
            readableCount = lastReadable + 1,
            items = rows,
        };
    }

    private static CorFrame? WalkToFrame(DnThread thread, int targetIndex)
    {
        int idx = 0;
        foreach (var chain in thread.CorThread.Chains)
        {
            foreach (var f in chain.Frames)
            {
                if (idx == targetIndex) return f;
                idx++;
            }
        }
        return null;
    }

    /// <summary>
    /// Render a CorValue into a JSON-friendly shape. Primitives decoded by
    /// reading the CorGenericValue bytes and reinterpreting via BitConverter;
    /// strings dereferenced; everything else surfaced as {address, typeName}
    /// so the caller has enough context to drill down with debug_heap_*
    /// without crashing on unsupported shapes.
    /// </summary>
    private static object? ReadValue(CorValue v)
    {
        try
        {
            // Reference types: dereference, then re-render.
            if (v.IsReference)
            {
                var deref = v.DereferencedValue;
                if (deref is null) return new { kind = "null" };
                if (deref.IsString) return new { kind = "string", value = deref.String };
                return new { kind = "object", typeElement = deref.ElementType.ToString(), address = (long)deref.Address };
            }

            switch (v.ElementType)
            {
                case CorElementType.Boolean:
                case CorElementType.I1:
                case CorElementType.U1:
                case CorElementType.I2:
                case CorElementType.U2:
                case CorElementType.Char:
                case CorElementType.I4:
                case CorElementType.U4:
                case CorElementType.I8:
                case CorElementType.U8:
                case CorElementType.R4:
                case CorElementType.R8:
                case CorElementType.I:
                case CorElementType.U:
                {
                    var bytes = v.ReadGenericValue();
                    if (bytes is null) return new { kind = "primitive", error = "ReadGenericValue failed" };
                    return new { kind = "primitive", elementType = v.ElementType.ToString(), value = DecodePrimitive(v.ElementType, bytes) };
                }
                case CorElementType.String:
                    return new { kind = "string", value = v.String };
                default:
                    return new { kind = "raw", elementType = v.ElementType.ToString(), address = (long)v.Address };
            }
        }
        catch (Exception ex)
        {
            return new { kind = "error", error = ex.Message };
        }
    }

    private static object? DecodePrimitive(CorElementType et, byte[] b) => et switch
    {
        CorElementType.Boolean => b.Length >= 1 && b[0] != 0,
        CorElementType.I1     => (sbyte)b[0],
        CorElementType.U1     => b[0],
        CorElementType.I2     => BitConverter.ToInt16(b, 0),
        CorElementType.U2     => BitConverter.ToUInt16(b, 0),
        CorElementType.Char   => (char)BitConverter.ToUInt16(b, 0),
        CorElementType.I4     => BitConverter.ToInt32(b, 0),
        CorElementType.U4     => BitConverter.ToUInt32(b, 0),
        CorElementType.I8     => BitConverter.ToInt64(b, 0),
        CorElementType.U8     => BitConverter.ToUInt64(b, 0),
        CorElementType.R4     => BitConverter.ToSingle(b, 0),
        CorElementType.R8     => BitConverter.ToDouble(b, 0),
        CorElementType.I      => b.Length == 8 ? (object)BitConverter.ToInt64(b, 0) : BitConverter.ToInt32(b, 0),
        CorElementType.U      => b.Length == 8 ? (object)BitConverter.ToUInt64(b, 0) : BitConverter.ToUInt32(b, 0),
        _ => $"0x{BitConverter.ToString(b).Replace("-", "")}",
    };
}
