using System;
using System.Linq;
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
            "[DEBUG] Arm managed-exception interception: pause the target when an exception is thrown. Params: {mode:\"all\"|\"unhandled\"|\"by_type\", typeName?:string (by_type; substring/FQN, case-insensitive), firstChance?:bool=true, excludeTypes?:string (comma/semicolon-separated type-name substrings to ignore)}. mode=all pauses on every first-chance throw; mode=unhandled only on unhandled; mode=by_type only when the thrown type matches. WORKFLOW for an unknown bug amid noise: arm mode=all, then exception.ignore_add each noisy type as it appears until debug_wait_paused goes quiet, then reproduce. excludeTypes seeds the ignore list up front. Catch the pause with debug_wait_paused (returns exceptionHit). Idempotent.",
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
                var excludeTypes = ParseList(Dispatcher.Opt<string?>(p, "excludeTypes", null));

                Program.Session.ExceptionInterception = new DebuggerSession.ExceptionFilter
                {
                    Mode = mode,
                    TypeName = string.IsNullOrWhiteSpace(typeName) ? null : typeName,
                    FirstChance = firstChance,
                    ExcludeTypes = excludeTypes,
                };
                return new { armed = true, mode, typeName, firstChance, excludeTypes };
            });

        d.Register("exception.break_clear",
            "[DEBUG] Disarm exception interception (throws no longer pause the target). Returns {armed:false}.",
            _ =>
            {
                Program.Session.ExceptionInterception = null;
                return new { armed = false };
            });

        d.Register("exception.ignore_add",
            "[DEBUG] Add a thrown-exception type to the active filter's ignore list — it stops pausing the target (filtered server-side, no round-trip per throw). Use under mode=all to drop noisy types one at a time until the background goes quiet, then reproduce the real issue. Substring match, case-insensitive. Params: {typeName:string}. Returns the updated ignore list.",
            p =>
            {
                var typeName = (Dispatcher.Req<string>(p, "typeName") ?? string.Empty).Trim();
                if (typeName.Length == 0) throw new ArgumentException("typeName is required");
                var f = Program.Session.ExceptionInterception
                    ?? throw new InvalidOperationException("no exception filter armed — call exception.break_set (debug_exception_break_set) first");
                var cur = f.ExcludeTypes;
                if (!cur.Any(x => string.Equals(x, typeName, StringComparison.OrdinalIgnoreCase)))
                    f.ExcludeTypes = cur.Concat(new[] { typeName }).ToArray();
                return new { ignored = f.ExcludeTypes };
            });

        d.Register("exception.ignore_clear",
            "[DEBUG] Clear the active exception filter's ignore list (everything in scope pauses again). Returns {ignored:[]}.",
            _ =>
            {
                var f = Program.Session.ExceptionInterception;
                if (f != null) f.ExcludeTypes = Array.Empty<string>();
                return new { ignored = Array.Empty<string>() };
            });
    }

    private static string[] ParseList(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
        return csv.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim())
                  .Where(s => s.Length > 0)
                  .ToArray();
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
