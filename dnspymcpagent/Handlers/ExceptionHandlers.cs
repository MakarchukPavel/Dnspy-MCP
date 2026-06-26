using System;
using dndbg.Engine;
using DnSpyMcp.Agent.Services;

namespace DnSpyMcp.Agent.Handlers;

/// <summary>
/// Managed-exception interception. Arms a filter on <see cref="DebuggerSession"/>;
/// the actual "stay paused at the throw" happens in DebuggerSession.OnDebugCallback
/// (subscribed to dndbg's Exception2 callback). This class also describes the
/// current exception-induced pause for debug.wait_paused, mirroring
/// BreakpointHandlers.DescribeCurrentBpHit.
/// </summary>
public static class ExceptionHandlers
{
    public static void Register(Dispatcher d)
    {
        d.Register("exception.break_set",
            "[DEBUG] Arm managed-exception interception: pause the target when an exception is thrown. Params: {mode:\"all\"|\"unhandled\"|\"by_type\", typeName?:string (by_type; substring/FQN, case-insensitive), firstChance?:bool=true}. mode=all pauses on EVERY first-chance throw (NOISY on busy apps like IIS/Creatio); mode=unhandled only on unhandled; mode=by_type only when the thrown type name matches. Catch the pause with debug_wait_paused (returns exceptionHit). Idempotent.",
            p =>
            {
                if (!Program.Session.IsAttached) throw new InvalidOperationException("not attached");
                var mode = (Dispatcher.Opt<string?>(p, "mode", "by_type") ?? "by_type").Trim().ToLowerInvariant();
                if (mode != "all" && mode != "unhandled" && mode != "by_type")
                    throw new ArgumentException("mode must be one of: all | unhandled | by_type");
                var typeName = Dispatcher.Opt<string?>(p, "typeName", null);
                var firstChance = Dispatcher.Opt<bool>(p, "firstChance", true);
                if (mode == "by_type" && string.IsNullOrWhiteSpace(typeName))
                    throw new ArgumentException("mode=by_type requires a typeName");

                Program.Session.ExceptionInterception = new DebuggerSession.ExceptionFilter
                {
                    Mode = mode,
                    TypeName = string.IsNullOrWhiteSpace(typeName) ? null : typeName,
                    FirstChance = firstChance,
                };
                return new { armed = true, mode, typeName, firstChance };
            });

        d.Register("exception.break_clear",
            "[DEBUG] Disarm exception interception (throws no longer pause the target). Returns {armed:false}.",
            _ =>
            {
                Program.Session.ExceptionInterception = null;
                return new { armed = false };
            });
    }

    /// <summary>
    /// Describe the current exception-induced pause, mirroring DescribeCurrentBpHit.
    /// Returns null unless the target is Paused with an Exception/UnhandledException
    /// pause reason. Reads the throwing thread's CurrentException for {type,address};
    /// message/HResult are read via ClrMD on the exception object so we never
    /// func-eval inside the ICorDebug callback path.
    /// </summary>
    public static object? DescribeCurrentExceptionHit()
    {
        if (!Program.Session.IsAttached) return null;
        return Program.Session.OnDbg<object?>(() =>
        {
            var dbg = Program.Session.DnDebugger;
            if (dbg.ProcessState != DebuggerProcessState.Paused) return null;
            var current = dbg.Current;
            if (current?.PauseStates == null || current.PauseStates.Length == 0) return null;

            bool isExc = false, unhandled = false;
            foreach (var ps in current.PauseStates)
            {
                if (ps.Reason == DebuggerPauseReason.UnhandledException) { isExc = true; unhandled = true; }
                else if (ps.Reason == DebuggerPauseReason.Exception) { isExc = true; }
            }
            if (!isExc) return null;

            var exObj = current.Thread?.CorThread?.CurrentException;
            string? type = DebuggerSession.TryGetExceptionTypeName(exObj);
            long? address = AddressOf(exObj);

            // message + HResult via ClrMD on the exception object (no func-eval).
            string? message = null; int? hResult = null;
            try
            {
                if (address is long a && a != 0)
                {
                    var o = Program.Session.ClrRuntime.Heap.GetObject((ulong)a);
                    if (o.Type != null)
                    {
                        try { message = o.ReadStringField("_message"); } catch { }
                        try { hResult = o.ReadField<int>("_HResult"); } catch { }
                    }
                }
            }
            catch { /* ClrMD best-effort */ }

            return new
            {
                type,
                message,
                hResult,
                unhandled,
                threadUniqueId = current.Thread?.UniqueId,
                osThreadId = current.Thread?.ThreadId,
                address,
            };
        });
    }

    private static long? AddressOf(CorValue? v)
    {
        try
        {
            if (v is null) return null;
            if (v.IsReference)
            {
                var deref = v.DereferencedValue;
                return deref is null ? (long?)null : (long)deref.Address;
            }
            return (long)v.Address;
        }
        catch { return null; }
    }
}
