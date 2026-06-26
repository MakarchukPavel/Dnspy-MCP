using System;
using System.Threading;
using dndbg.Engine;
using DnSpyMcp.Agent.Services;

namespace DnSpyMcp.Agent.Handlers;

public static class StepHandlers
{
    public static void Register(Dispatcher d)
    {
        d.Register("step.in",
            "[DEBUG] Step into the next IL instruction on the current thread. Waits until stepping completes or timeoutMs (default 5000).",
            p => DoStep(p, "in"));

        d.Register("step.over",
            "[DEBUG] Step over the next IL instruction on the current thread.",
            p => DoStep(p, "over"));

        d.Register("step.out",
            "[DEBUG] Step out of the current function.",
            p => DoStep(p, "out"));

        // step.go / step.pause: were session.go / session.pause. Moved here
        // because they are execution-flow primitives (continue / break) that
        // belong with the rest of stepping, not with session lifecycle.
        d.Register("step.go",
            "[DEBUG] Continue a paused target (like WinDbg `g`).",
            _ =>
            {
                Program.Session.OnDbg(() =>
                {
                    if (Program.Session.DnDebugger.ProcessState == DebuggerProcessState.Paused)
                        Program.Session.DnDebugger.Continue();
                });
                return new { state = Program.Session.OnDbg(() => Program.Session.DnDebugger.ProcessState.ToString()) };
            });

        d.Register("step.pause",
            "[DEBUG] Break (pause) the target.",
            _ =>
            {
                Program.Session.OnDbg(() => Program.Session.DnDebugger.TryBreakProcesses());
                return new { state = Program.Session.OnDbg(() => Program.Session.DnDebugger.ProcessState.ToString()) };
            });

        d.Register("debug.wait_paused",
            "[DEBUG] Block until the target is Paused (breakpoint, step complete, exception, or pause). Returns {state, bpHit?, exceptionHit?, timedOut?}. bpHit is populated for a registered breakpoint; exceptionHit is populated when an armed exception filter (exception.break_set) stopped at a throw and carries {type, message, hResult, unhandled, thread, address}. Params: {timeoutMs?:int=5000}.",
            p =>
            {
                var timeout = Dispatcher.Opt<int>(p, "timeoutMs", 5000);
                var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
                while (DateTime.UtcNow < deadline)
                {
                    var state = Program.Session.OnDbg(() => Program.Session.DnDebugger.ProcessState);
                    if (state == DebuggerProcessState.Paused || state == DebuggerProcessState.Terminated)
                    {
                        object? bpHit = null;
                        try { bpHit = BreakpointHandlers.DescribeCurrentBpHit(); } catch { }
                        object? exceptionHit = null;
                        try { exceptionHit = ExceptionHandlers.DescribeCurrentExceptionHit(); } catch { }
                        return new { state = state.ToString(), bpHit, exceptionHit };
                    }
                    Thread.Sleep(50);
                }
                return new { state = Program.Session.OnDbg(() => Program.Session.DnDebugger.ProcessState.ToString()), timedOut = true };
            });
    }

    private static object DoStep(Newtonsoft.Json.Linq.JObject? p, string kind)
    {
        var timeout = Dispatcher.Opt<int>(p, "timeoutMs", 5000);
        var done = new ManualResetEventSlim(false);

        Program.Session.OnDbg(() =>
        {
            var dbg = Program.Session.DnDebugger;
            if (dbg.ProcessState != DebuggerProcessState.Paused)
                throw new InvalidOperationException($"cannot step: state={dbg.ProcessState}");

            var frame = dbg.Current.ILFrame;
            Action<DnDebugger, StepCompleteDebugCallbackEventArgs?, bool> onDone =
                (_, _, _) => done.Set();

            CorStepper? stepper = kind switch
            {
                "in"   => dbg.StepInto(frame, onDone),
                "over" => dbg.StepOver(frame, onDone),
                "out"  => dbg.StepOut(frame, onDone),
                _      => throw new InvalidOperationException("bad step kind"),
            };
            if (stepper == null) throw new InvalidOperationException("failed to create stepper");

            if (dbg.ProcessState == DebuggerProcessState.Paused)
                dbg.Continue();
        });

        bool ok = done.Wait(timeout);
        var state = Program.Session.OnDbg(() => Program.Session.DnDebugger.ProcessState.ToString());
        return new { ok, timedOut = !ok, state };
    }
}
