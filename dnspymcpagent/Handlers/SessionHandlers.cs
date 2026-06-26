using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DnSpyMcp.Agent.Services;
using Newtonsoft.Json.Linq;

namespace DnSpyMcp.Agent.Handlers;

public static class SessionHandlers
{
    public static void Register(Dispatcher d)
    {
        // Attach / detach / load_dump are runtime-controllable — one agent
        // process can be repointed at any local PID or dump file across its
        // lifetime, no restart required. Target process death auto-detaches;
        // the agent itself keeps listening.

        d.Register("session.attach",
            "[DEBUG] Attach the debugger to a local .NET process by PID. If already attached elsewhere, detaches first. Optionally registers a list of breakpoints atomically with the attach (eliminates the attach<->first-RPC race). Params: {pid:int, initialBreakpointsJson?:string=JSON-encoded array of breakpoint specs; supported kinds: \"by_name\" / \"il\" / \"native\"}. Returns {attached, pid, description, initialBreakpoints?:[{ok, bp?, error?, spec?}]}.",
            args =>
            {
                if (args is not JObject obj || obj["pid"] == null)
                    throw new ArgumentException("pid (int) is required");
                int pid = obj["pid"]!.Value<int>();

                Program.Session.Attach(pid);

                List<object>? bpResults = null;
                var rawJson = obj["initialBreakpointsJson"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(rawJson))
                {
                    JArray arr;
                    try { arr = JArray.Parse(rawJson!); }
                    catch (Newtonsoft.Json.JsonException ex)
                    { throw new ArgumentException($"initialBreakpointsJson is not a valid JSON array: {ex.Message}"); }

                    bpResults = new List<object>();
                    Program.Session.OnDbg(() =>
                    {
                        foreach (var token in arr)
                        {
                            if (token is not JObject spec)
                            {
                                bpResults.Add(new { ok = false, error = "spec must be a JSON object" });
                                continue;
                            }
                            try
                            {
                                var bp = BreakpointHandlers.SetBreakpointFromSpec(spec);
                                bpResults.Add(new { ok = true, bp });
                            }
                            catch (Exception ex)
                            {
                                bpResults.Add(new { ok = false, spec, error = ex.Message });
                            }
                        }
                    });
                }

                return new
                {
                    attached = Program.Session.IsAttached,
                    pid = Program.Session.Pid,
                    description = Program.Session.Describe(),
                    initialBreakpoints = bpResults,
                };
            });

        d.Register("session.load_dump",
            "[DEBUG] Load a .NET crash/process dump (.dmp) for passive postmortem analysis via ClrMD — heap walk, read object/array/string, struct decoding all work on the dump. NO live debugging (breakpoints / stepping / frames / func-eval need a live process). Detaches any current target first. Params: {path:string = absolute path to the .dmp on the agent's machine}. Returns {dumpLoaded, dumpPath, description}.",
            args =>
            {
                var path = Dispatcher.Req<string>(args, "path");
                Program.Session.LoadDump(path);
                return new
                {
                    dumpLoaded = true,
                    dumpPath = Program.Session.DumpPath,
                    description = Program.Session.Describe(),
                };
            });

        d.Register("session.launch",
            "[DEBUG] Launch a .NET Framework EXE UNDER the debugger and break at its managed entry point. The debugger is present before any module loads, so JIT optimization is disabled for every module — func-eval (eval.call) works even on Release/optimized assemblies (a late attach cannot achieve this). Detaches any current target first. Params: {exePath:string (absolute), args?:string, workingDir?:string}. Returns {launched, pid, description}. On return the process is PAUSED at the entry point — set breakpoints, then step.go.",
            args =>
            {
                var exePath = Dispatcher.Req<string>(args, "exePath");
                var a = Dispatcher.Opt<string?>(args, "args", null);
                var wd = Dispatcher.Opt<string?>(args, "workingDir", null);
                Program.Session.LaunchProcess(exePath, a, wd);
                return new
                {
                    launched = true,
                    pid = Program.Session.Pid,
                    description = Program.Session.Describe(),
                };
            });

        d.Register("session.detach",
            "[DEBUG] Detach from the current target. Agent keeps listening. Idempotent — detach without attach is a no-op.",
            _ =>
            {
                bool wasAttached = Program.Session.IsAttached;
                Program.Session.Detach();
                return new
                {
                    detached = wasAttached,
                    lastExitedPid = Program.Session.LastExitedPid,
                    lastExitReason = Program.Session.LastExitReason,
                    lastExitUtc = Program.Session.LastExitUtc?.ToString("o"),
                };
            });

        d.Register("session.info",
            "[DEBUG] Describe the current debug session (attached pid + last exit info if any). When paused on a breakpoint, `bpHit` carries the matching registry id, kind, description, token, ilOffset and the thread that hit it.",
            _ =>
            {
                object? bpHit = null;
                try { bpHit = BreakpointHandlers.DescribeCurrentBpHit(); } catch { /* not attached / state race */ }
                return new
                {
                    isAttached = Program.Session.IsAttached,
                    pid = Program.Session.Pid,
                    dumpPath = Program.Session.DumpPath,
                    description = Program.Session.Describe(),
                    lastExitedPid = Program.Session.LastExitedPid,
                    lastExitReason = Program.Session.LastExitReason,
                    lastExitUtc = Program.Session.LastExitUtc?.ToString("o"),
                    bpHit,
                };
            });

        // session.dotnet_processes: stays here because it's part of the
        // attach-side workflow (the agent helps the caller pick a PID before
        // calling session.attach). Keeping it next to attach/detach makes
        // the related operations group naturally on the agent surface.
        d.Register("session.dotnet_processes",
            "[DEBUG] List .NET processes on this machine (has CLR loaded). Use to pick a PID for debug_pid_attach.",
            _ =>
            {
                var rows = new List<object>();
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        bool hasClr = proc.Modules.Cast<ProcessModule>().Any(m =>
                            (m.ModuleName?.StartsWith("coreclr", StringComparison.OrdinalIgnoreCase) ?? false) ||
                            (m.ModuleName?.StartsWith("clr.dll", StringComparison.OrdinalIgnoreCase) ?? false) ||
                            (m.ModuleName?.Equals("mscorwks.dll", StringComparison.OrdinalIgnoreCase) ?? false));
                        if (hasClr) rows.Add(new { pid = proc.Id, name = proc.ProcessName });
                    }
                    catch { /* access denied: skip */ }
                }
                return rows;
            });

        // Process-control operations (go / pause) used to live here under
        // session.* but they are stepping/execution-flow primitives, not
        // session-lifecycle. Moved to StepHandlers as step.go / step.pause.
        // session.terminate (destructive) was removed entirely — no MCP tool
        // ever exposed it, the death-watcher already handles target exits,
        // and "kill the debuggee" is an OS responsibility, not a debugger
        // RPC we want to invite from a remote caller.
    }
}
