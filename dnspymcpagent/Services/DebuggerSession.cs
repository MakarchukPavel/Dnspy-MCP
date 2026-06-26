using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using dndbg.Engine;
using dndbg.COM.CorDebug;
using dnSpy.Debugger.DotNet.CorDebug.Impl;
using Microsoft.Diagnostics.Runtime;

namespace DnSpyMcp.Agent.Services;

/// <summary>
/// Holds the single active debugger target — either a LIVE process or a loaded
/// crash/process dump (mutually exclusive).
/// For a live target, uses dnSpy's <see cref="DebuggerThread"/> to serialize
/// ICorDebug calls onto an STA thread, with ClrMD attached in parallel for
/// passive heap inspection. For a dump (<see cref="LoadDump"/>), only ClrMD is
/// used — the heap readers and struct decoding work, but live operations
/// (breakpoints / stepping / frames / func-eval) are unavailable.
/// </summary>
public sealed class DebuggerSession : IDisposable
{
    private readonly object _lock = new();
    private DebuggerThread? _dbgThread;
    private DnDebugger? _dnDebugger;
    private DataTarget? _clrMdTarget;
    private ClrRuntime? _clrRuntime;
    private System.Threading.Thread? _deathWatcher;
    private System.Threading.CancellationTokenSource? _deathWatcherCts;
    private Process? _watchedProcess;

    public int? Pid { get; private set; }
    public bool IsAttached => _dnDebugger != null;

    // Set when a crash/process dump is loaded (passive ClrMD analysis, no live
    // ICorDebug). Mutually exclusive with a live attach. Cleared on Detach.
    public string? DumpPath { get; private set; }

    // Func-eval needs the JIT to emit debuggable code, which the options
    // provider requests at LoadModule — but only effective for modules that load
    // AFTER the debugger is present. We track each module's name -> "loaded under
    // the debugger?" so callers can tell whether func-eval will work there.
    // _jitBaselineDone flips true once the pre-existing-module replay is over
    // (attach: at OnAttachComplete; launch: before the process runs), so every
    // load recorded after it is a genuine under-debugger load.
    private volatile bool _jitBaselineDone;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _moduleLoads =
        new(StringComparer.OrdinalIgnoreCase);

    // Last exit info retained across detach so callers can see WHY the session
    // ended (user-initiated detach, target process exit, etc.).
    public int? LastExitedPid { get; private set; }
    public string? LastExitReason { get; private set; }
    public DateTime? LastExitUtc { get; private set; }

    public readonly BreakpointRegistry Breakpoints = new();

    /// <summary>
    /// Active managed-exception interception filter (null = disabled). Set by the
    /// exception.break_set tool; consulted by <see cref="OnDebugCallback"/> on every
    /// Exception2 callback. Cleared on Detach.
    /// </summary>
    public volatile ExceptionFilter? ExceptionInterception;

    public sealed class ExceptionFilter
    {
        public string Mode = "by_type";   // all | unhandled | by_type
        public string? TypeName;          // by_type: substring / FQN, case-insensitive
        public bool FirstChance = true;   // all/by_type: stop at first-chance (true) else unhandled
        // Type-name substrings to NEVER pause on (the "ignore the noise" list). Grown live by
        // exception.ignore_add; replaced atomically (volatile) so the STA callback always reads
        // a consistent immutable snapshot without locking.
        public volatile string[] ExcludeTypes = System.Array.Empty<string>();
    }

    public DnDebugger DnDebugger =>
        _dnDebugger ?? throw new InvalidOperationException("Not attached. Call /tool/session/attach first.");

    public DebuggerThread DbgThread =>
        _dbgThread ?? throw new InvalidOperationException("Debugger thread not running.");

    public ClrRuntime ClrRuntime =>
        _clrRuntime ?? throw new InvalidOperationException("No ClrMD runtime (not attached).");

    public DataTarget ClrMdTarget =>
        _clrMdTarget ?? throw new InvalidOperationException("No ClrMD target (not attached).");

    public void Attach(int pid)
    {
        lock (_lock)
        {
            Detach();
            // Clear stale exit info — a fresh attach starts a clean history.
            LastExitedPid = null;
            LastExitReason = null;
            LastExitUtc = null;

            _dbgThread = new DebuggerThread("dnspymcp-dbg");
            _dbgThread.CallDispatcherRun();

            // OnAttachComplete fires on the STA after the CLR's attach-time callback burst
            // (CreateProcess / CreateAppDomain / LoadAssembly / LoadModule / CreateThread) has
            // finished draining. Subscribe inside the same STA Invoke that calls Attach, before
            // yielding back to the dispatcher — otherwise the burst could race ahead of us.
            var attachComplete = new ManualResetEventSlim(false);
            Exception? attachError = null;

            // Wrap the STA Invoke body in try/catch: any unhandled exception
            // from DnDebugger.Attach (e.g. bad PID, access denied) would
            // otherwise kill the STA thread and take the whole agent down —
            // precisely what A5 (auto-detach on target death, agent survives)
            // was designed to prevent, applied here symmetrically for the
            // attach failure path. Capture and rethrow on the caller thread.
            _dnDebugger = _dbgThread.Invoke(() =>
            {
                try
                {
                    var attachInfo = BuildAttachInfo(pid);
                    // DebugOptions.DebugOptionsProvider is non-nullable — if left null, the
                    // CreateProcess callback handler inside dndbg dereferences it and throws
                    // NRE. The exception is swallowed by OnManagedCallbackFromAnyThread2 and
                    // Continue() is never called, which stalls the entire callback burst.
                    var options = new AttachProcessOptions(attachInfo)
                    {
                        ProcessId = pid,
                        DebugMessageDispatcher = _dbgThread.GetDebugMessageDispatcher(),
                        DebugOptions = new DebugOptions { DebugOptionsProvider = new DefaultDebugOptionsProvider() },
                    };
                    var dbg = DnDebugger.Attach(options);

                    // If DebugActiveProcess HRESULT'd we'd have no processes — fail loud, don't
                    // return a half-dead DnDebugger to the caller.
                    if (dbg.Processes.Length == 0)
                    {
                        attachError = new InvalidOperationException(
                            $"ICorDebug attach returned no processes for pid={pid} (DebugActiveProcess failed — process gone, wrong CLR version, already being debugged, or access denied)");
                        return dbg;
                    }

                    // Krafs.Publicizer republishes the internal backing field alongside the public
                    // event, so direct `dbg.OnAttachComplete += ...` fails to compile with CS0229.
                    // Subscribe via reflection — reflection sees the event, not the field.
                    var evt = typeof(DnDebugger).GetEvent("OnAttachComplete")
                        ?? throw new InvalidOperationException("DnDebugger.OnAttachComplete event missing");
                    // Mark the JIT baseline: every LoadModule from here on is a
                    // genuine under-debugger load (the pre-existing modules were
                    // replayed during the bootstrap burst before this fires).
                    EventHandler handler = (_, _) => { _jitBaselineDone = true; attachComplete.Set(); };
                    evt.AddEventHandler(dbg, handler);

                    // Subscribe to the raw callback stream so we can implement
                    // exception interception (break on a thrown managed exception).
                    // Same Krafs.Publicizer/reflection route as OnAttachComplete
                    // (direct += would hit CS0229 against the republished backing field).
                    var cbEvt = typeof(DnDebugger).GetEvent("DebugCallbackEvent");
                    var cbMethod = typeof(DebuggerSession).GetMethod(nameof(OnDebugCallback),
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (cbEvt?.EventHandlerType != null && cbMethod != null)
                    {
                        var cb = Delegate.CreateDelegate(cbEvt.EventHandlerType, this, cbMethod);
                        cbEvt.AddEventHandler(dbg, cb);
                    }
                    return dbg;
                }
                catch (Exception ex)
                {
                    attachError = ex;
                    return null!;
                }
            });

            if (attachError != null)
            {
                // Tear down the half-initialized STA thread before throwing
                // so the next attach starts from a clean state.
                if (_dbgThread != null)
                {
                    try { _dbgThread.Terminate(); } catch { /* ignore */ }
                    _dbgThread = null;
                }
                _dnDebugger = null;
                throw attachError;
            }

            Pid = pid;

            // Pump the attach-time callback burst and block until the CLR signals it's done.
            // The burst runs on the STA dispatcher, so we must NOT hold an Invoke here —
            // wait on the MRE instead. 15s is generous; real-world attach completes in <500ms.
            if (!attachComplete.Wait(TimeSpan.FromSeconds(15)))
            {
                var diag = DescribeBootstrapState();
                Detach();
                throw new TimeoutException(
                    $"ICorDebug attach-bootstrap timed out after 15s. Diagnostic: {diag}");
            }

            // Sanity check — Processes was non-empty pre-wait, but modules might still be
            // empty if the process has no managed code loaded (pathological, but explicit
            // error beats a silent half-state).
            var (procCount, adCount, modCount, thrCount) = ReadBootstrapState();
            if (procCount == 0 || adCount == 0 || modCount == 0)
            {
                Detach();
                throw new InvalidOperationException(
                    $"attach completed but state is empty (procs={procCount}, appdomains={adCount}, modules={modCount}, threads={thrCount})");
            }

            // Also attach ClrMD for passive read (heap walk etc.). Passive = non-invasive.
            // Done AFTER ICorDebug bootstrap so the process is in a stable state.
            _clrMdTarget = DataTarget.AttachToProcess(pid, false);
            _clrRuntime = _clrMdTarget.ClrVersions.FirstOrDefault()?.CreateRuntime();

            // Start a watchdog that polls the target PID. If the target goes
            // away (process crash, user kills it, IIS recycles the app pool),
            // we auto-detach so the agent itself survives and a new `attach`
            // is possible without bouncing the whole agent.
            StartDeathWatcher(pid);
        }
    }

    /// <summary>
    /// Launch a .NET Framework executable UNDER the debugger and break at its
    /// managed entry point. Because the debugger is present before any module
    /// loads, the JIT-disable options (see <see cref="DefaultDebugOptionsProvider"/>)
    /// apply to every module — so func-eval works even on Release/optimized
    /// assemblies (the thing a late attach can't achieve). Detaches any current
    /// target first. Returns once the process is paused at the entry point.
    /// </summary>
    public void LaunchProcess(string exePath, string? args, string? workingDir)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("exePath is required");
            if (!System.IO.File.Exists(exePath)) throw new System.IO.FileNotFoundException($"executable not found: {exePath}");

            Detach();
            LastExitedPid = null; LastExitReason = null; LastExitUtc = null;

            _dbgThread = new DebuggerThread("dnspymcp-dbg");
            _dbgThread.CallDispatcherRun();

            Exception? launchError = null;
            _dnDebugger = _dbgThread.Invoke(() =>
            {
                try
                {
                    var options = new DebugProcessOptions(new DesktopCLRTypeDebugInfo())
                    {
                        Filename = exePath,
                        CommandLine = "\"" + exePath + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args),
                        CurrentDirectory = string.IsNullOrWhiteSpace(workingDir) ? System.IO.Path.GetDirectoryName(exePath) : workingDir,
                        DebugMessageDispatcher = _dbgThread.GetDebugMessageDispatcher(),
                        DebugOptions = new DebugOptions { DebugOptionsProvider = new DefaultDebugOptionsProvider() },
                        BreakProcessKind = BreakProcessKind.EntryPoint, // stop at managed entry, modules loaded + debuggable
                        // Inherit the agent's environment — dndbg's CreateProcess NREs on a null
                        // Environment, and an empty block would strip the child's env entirely.
                        Environment = System.Environment.GetEnvironmentVariables()
                            .Cast<System.Collections.DictionaryEntry>()
                            .Select(de => new System.Collections.Generic.KeyValuePair<string, string>((string)de.Key, (string)(de.Value ?? "")))
                            .ToArray(),
                    };
                    var dbg = DnDebugger.DebugProcess(options);

                    // Same exception-interception callback wiring as Attach.
                    var cbEvt = typeof(DnDebugger).GetEvent("DebugCallbackEvent");
                    var cbMethod = typeof(DebuggerSession).GetMethod(nameof(OnDebugCallback),
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (cbEvt?.EventHandlerType != null && cbMethod != null)
                        cbEvt.AddEventHandler(dbg, Delegate.CreateDelegate(cbEvt.EventHandlerType, this, cbMethod));
                    return dbg;
                }
                catch (Exception ex) { launchError = ex; return null!; }
            });

            if (launchError != null)
            {
                if (_dbgThread != null) { try { _dbgThread.Terminate(); } catch { } _dbgThread = null; }
                _dnDebugger = null;
                throw launchError;
            }

            // Launched under the debugger from creation, so every module load is
            // a genuine under-debugger (debuggable) load — set the baseline before
            // the dispatcher starts delivering LoadModule callbacks.
            _jitBaselineDone = true;

            // The dispatcher drives the process to its entry point and pauses there.
            // Poll (don't hold an STA Invoke, so the dispatcher can run).
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var st = OnDbg(() => _dnDebugger!.ProcessState);
                if (st == DebuggerProcessState.Paused) break;
                if (st == DebuggerProcessState.Terminated)
                { Detach(); throw new InvalidOperationException("launched process terminated before reaching the entry point"); }
                System.Threading.Thread.Sleep(50);
            }
            if (OnDbg(() => _dnDebugger!.ProcessState) != DebuggerProcessState.Paused)
            { Detach(); throw new TimeoutException("launched process did not reach its entry point within 30s"); }

            int pid = OnDbg(() => _dnDebugger!.Processes.FirstOrDefault()?.ProcessId ?? 0);
            if (pid == 0) { Detach(); throw new InvalidOperationException("could not determine launched process id"); }
            Pid = pid;

            _clrMdTarget = DataTarget.AttachToProcess(pid, false);
            _clrRuntime = _clrMdTarget.ClrVersions.FirstOrDefault()?.CreateRuntime();
            StartDeathWatcher(pid);
        }
    }

    /// <summary>
    /// Load a crash/process dump (.dmp) for passive postmortem analysis via
    /// ClrMD. No ICorDebug — the heap readers and struct decoding work, but
    /// live operations (breakpoints / stepping / frames / func-eval) do not.
    /// Detaches any current live target or previously-loaded dump first.
    /// </summary>
    public void LoadDump(string path)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("dump path is required");
            if (!System.IO.File.Exists(path))
                throw new System.IO.FileNotFoundException($"dump file not found: {path}");

            Detach(); // drop any live attach or prior dump
            // A fresh dump load starts a clean history.
            LastExitedPid = null;
            LastExitReason = null;
            LastExitUtc = null;

            var target = DataTarget.LoadDump(path);
            var runtime = target.ClrVersions.FirstOrDefault()?.CreateRuntime();
            if (runtime == null)
            {
                target.Dispose();
                throw new InvalidOperationException($"no CLR runtime found in dump: {path}");
            }
            _clrMdTarget = target;
            _clrRuntime = runtime;
            DumpPath = path;
        }
    }

    // ---- target process death watcher -----------------------------------
    // ICorDebug's managed callback model is STA-bound and fragile in remote-
    // attach scenarios; rather than rely on ExitProcess callbacks we poll the
    // OS Process object. Cheap, reliable, and works for both local and
    // remote targets (ICorDebug can only attach to local processes anyway).
    private void StartDeathWatcher(int pid)
    {
        StopDeathWatcher();
        try { _watchedProcess = Process.GetProcessById(pid); }
        catch (ArgumentException) { /* already gone; Attach() will throw separately */ return; }

        _deathWatcherCts = new CancellationTokenSource();
        var cts = _deathWatcherCts;
        var watched = _watchedProcess;
        _deathWatcher = new Thread(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    // WaitForExit with a short poll window so we cooperate
                    // with cancellation on user-initiated detach / Dispose.
                    if (watched.WaitForExit(500))
                    {
                        OnTargetProcessDied(pid, $"target process {pid} exited (code {TryGetExitCode(watched)})");
                        return;
                    }
                }
            }
            catch (Exception ex) { OnTargetProcessDied(pid, $"death watcher error: {ex.Message}"); }
        }) { IsBackground = true, Name = "dnspymcp-death-watcher" };
        _deathWatcher.Start();
    }

    private static string TryGetExitCode(Process p)
    {
        try { return p.ExitCode.ToString(); } catch { return "?"; }
    }

    private void StopDeathWatcher()
    {
        try { _deathWatcherCts?.Cancel(); } catch { }
        _deathWatcherCts = null;
        _watchedProcess = null;
        // Don't Join the thread — it owns an STA-bound callback into the
        // debug session and we're already inside `_lock`. It exits on its
        // next poll tick (<500ms).
        _deathWatcher = null;
    }

    private void OnTargetProcessDied(int pid, string reason)
    {
        // Record exit info BEFORE detach clears Pid.
        LastExitedPid = pid;
        LastExitReason = reason;
        LastExitUtc = DateTime.UtcNow;
        try { Detach(); } catch { /* best-effort — agent must keep listening */ }
    }

    private (int processes, int appDomains, int modules, int threads) ReadBootstrapState()
    {
        int p = 0, a = 0, m = 0, t = 0;
        try
        {
            _dbgThread!.Invoke(() =>
            {
                foreach (var proc in _dnDebugger!.Processes)
                {
                    p++;
                    foreach (var th in proc.Threads) t++;
                    foreach (var ad in proc.AppDomains)
                    {
                        a++;
                        foreach (var asm in ad.Assemblies)
                            foreach (var _ in asm.Modules) m++;
                    }
                }
            });
        }
        catch { /* STA gone */ }
        return (p, a, m, t);
    }

    private string DescribeBootstrapState()
    {
        var (p, a, m, t) = ReadBootstrapState();
        return $"processes={p}, appDomains={a}, modules={m}, threads={t}";
    }

    public void Detach()
    {
        lock (_lock)
        {
            // If the caller invoked Detach directly (not via death watcher),
            // attribute the exit reason. Death-watcher path sets its own
            // LastExitReason before calling Detach, so we don't clobber it.
            if (Pid != null && LastExitReason == null)
            {
                LastExitedPid = Pid;
                LastExitReason = "user detach";
                LastExitUtc = DateTime.UtcNow;
            }

            StopDeathWatcher();

            if (_dnDebugger != null)
            {
                try
                {
                    _dbgThread?.Invoke(() =>
                    {
                        try
                        {
                            if (_dnDebugger.ProcessState != DebuggerProcessState.Terminated)
                            {
                                // TryDetach returns an HRESULT. Only fall back to terminate
                                // when the CLR refuses to release the debuggee cleanly
                                // (CORDBG_E_UNRECOVERABLE_ERROR / CORDBG_E_PROCESS_NOT_SYNCHRONIZED).
                                int hr = _dnDebugger.TryDetach();
                                if (hr < 0)
                                    _dnDebugger.TerminateProcesses();
                            }
                        }
                        catch { /* ignore */ }
                    });
                }
                catch { /* ignore */ }
                _dnDebugger = null;
            }

            if (_dbgThread != null)
            {
                try { _dbgThread.Terminate(); } catch { /* ignore */ }
                _dbgThread = null;
            }

            _clrRuntime = null;
            _clrMdTarget?.Dispose();
            _clrMdTarget = null;

            Breakpoints.Clear();
            HeapSnapshotStore.Clear();
            ExceptionInterception = null;
            Pid = null;
            DumpPath = null;
            _jitBaselineDone = false;
            _moduleLoads.Clear();
        }
    }

    /// <summary>
    /// Report, per loaded module matching <paramref name="pattern"/> (substring,
    /// case-insensitive; null = all), whether it loaded UNDER the debugger. A
    /// module loaded under the debugger got the debuggable JIT flags, so
    /// func-eval works on it; a pre-existing module (already JITted optimized
    /// when we attached) does not. This is how a caller checks "did the
    /// DISABLE_OPTIMIZATION flag actually take" for e.g. Terrasoft.Core.
    /// </summary>
    public object ModuleLoadStatus(string? pattern)
    {
        var rows = _moduleLoads
            .Where(kv => string.IsNullOrEmpty(pattern) || kv.Key.IndexOf(pattern!, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new { module = kv.Key, loadedUnderDebugger = kv.Value, funcEvalReady = kv.Value })
            .ToList();
        return new
        {
            pattern,
            mode = DumpPath != null ? "dump" : (IsAttached ? (_jitBaselineDone ? "live" : "attaching") : "none"),
            count = rows.Count,
            modules = rows,
        };
    }

    // ---- exception interception ------------------------------------------
    // Receives EVERY dndbg callback on the STA debugger thread. Acts only on
    // Exception2 when a filter is armed: on a match it calls AddPauseReason,
    // which makes dndbg's ShouldStopQueued keep the target paused at the throw
    // — the exact mechanism dnSpy's DbgEngineImpl uses. ICorDebug calls here
    // are safe because this fires on the STA thread.
    private void OnDebugCallback(object sender, DebugCallbackEventArgs e)
    {
        // Record every module load with whether it happened under the debugger
        // (after the pre-existing-module baseline). dndbg has already applied the
        // options-provider's debuggable JIT flags to this module by now, so a
        // post-baseline load == func-eval-ready.
        if (e.Kind == DebugCallbackKind.LoadModule)
        {
            try
            {
                var nm = ((LoadModuleDebugCallbackEventArgs)e).CorModule?.Name;
                if (!string.IsNullOrEmpty(nm))
                    _moduleLoads[System.IO.Path.GetFileName(nm)] = _jitBaselineDone;
            }
            catch { /* never throw on the STA callback */ }
        }

        var filter = ExceptionInterception;
        if (filter is null) return;
        if (e.Kind != DebugCallbackKind.Exception2) return;
        try
        {
            var e2 = (Exception2DebugCallbackEventArgs)e;
            bool isFirst = e2.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE;
            bool isUnhandled = e2.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED;
            if (!isFirst && !isUnhandled) return; // ignore USER_FIRST_CHANCE / CATCH_HANDLER_FOUND

            // Don't break on first-chance noise while a func-eval is running; always honor unhandled.
            if (sender is DnDebugger d && d.IsEvaluating && !isUnhandled) return;

            bool wantUnhandled = filter.Mode == "unhandled" || !filter.FirstChance;
            if (wantUnhandled ? !isUnhandled : !isFirst) return;

            // Resolve the thrown type name once, only if we actually need it
            // (by_type matching, or a non-empty ignore list).
            var excludes = filter.ExcludeTypes;
            string? exName = (filter.Mode == "by_type" || excludes.Length > 0)
                ? TryGetExceptionTypeName(e2.CorThread?.CurrentException)
                : null;

            if (filter.Mode == "by_type")
            {
                if (exName is null) return; // can't confirm the type -> don't pause
                if (!string.IsNullOrEmpty(filter.TypeName) &&
                    exName.IndexOf(filter.TypeName, StringComparison.OrdinalIgnoreCase) < 0)
                    return;
            }

            // Ignore list (applies to every mode). This is what lets mode=all run at full
            // speed once noisy types are ignored: the skip happens here on the STA thread,
            // with NO MCP round-trip per noise throw.
            if (exName != null && excludes.Length > 0)
            {
                foreach (var ex in excludes)
                    if (!string.IsNullOrEmpty(ex) && exName.IndexOf(ex, StringComparison.OrdinalIgnoreCase) >= 0)
                        return;
            }

            e.AddPauseReason(isUnhandled ? DebuggerPauseReason.UnhandledException : DebuggerPauseReason.Exception);
        }
        catch { /* a throwing callback would kill the STA thread — swallow */ }
    }

    /// <summary>Full type name ("Ns.Type" / "Ns.Outer+Inner") of a thrown exception CorValue, or null.</summary>
    internal static string? TryGetExceptionTypeName(CorValue? exObj) => TryGetCorValueTypeName(exObj);

    /// <summary>
    /// Full type name ("Ns.Type" / "Ns.Outer+Inner") of any CorValue via its
    /// ExactType metadata, or null. Used to resolve value-type locals/arguments
    /// so they can be decoded by name (Guid/DateTime/enum/struct).
    /// </summary>
    internal static string? TryGetCorValueTypeName(CorValue? v)
    {
        var exactType = v?.ExactType;
        if (exactType is null) return null;
        var mdi = exactType.GetMetaDataImport(out uint token);
        if (mdi is null) return null;
        return MetaDataUtils.FullTypeName(mdi, token);
    }

    // ---- runtime detection (desktop CLR vs CoreCLR) ----------------------
    // Decide which attach-info to hand DnDebugger.Attach for a target pid.
    // CoreCLR is detected by the loaded coreclr.dll module; the .NET Framework
    // path is unchanged (DesktopCLRTypeAttachInfo). For CoreCLR we pass the path
    // to a bundled dbgshim.dll — dndbg's CoreCLRHelper then resolves the exact
    // runtime version and the matching mscordbi/mscordaccore next to the
    // debuggee's coreclr.dll. Works for .NET Core 2.1 .. .NET 9+ (x64).
    private static CLRTypeAttachInfo BuildAttachInfo(int pid)
    {
        string? coreclrPath = null;
        try
        {
            using (var proc = Process.GetProcessById(pid))
            {
                foreach (ProcessModule m in proc.Modules)
                {
                    if (string.Equals(m.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        coreclrPath = m.FileName;
                        break;
                    }
                }
            }
        }
        catch { /* cross-bitness / access denied — assume desktop; a wrong guess surfaces a clear attach error */ }

        if (coreclrPath != null)
        {
            var dbgShim = FindDbgShim()
                ?? throw new InvalidOperationException(
                    $"pid={pid} is .NET Core (coreclr.dll loaded) but dbgshim.dll was not found next to the agent. " +
                    "Rebuild the agent with the Microsoft.Diagnostics.DbgShim.win-x64 NuGet package.");
            // version=null => dndbg/dbgshim auto-resolves the exact CoreCLR version for this pid.
            return new CoreCLRTypeAttachInfo(null, dbgShim, coreclrPath);
        }
        return new DesktopCLRTypeAttachInfo(string.Empty);
    }

    private static string? FindDbgShim()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (var rel in new[] { "dbgshim.dll", @"runtimes\win-x64\native\dbgshim.dll" })
        {
            var p = System.IO.Path.Combine(baseDir, rel);
            if (System.IO.File.Exists(p)) return p;
        }
        return null;
    }

    public string Describe()
    {
        if (_dnDebugger != null)
        {
            var clr = _clrRuntime?.ClrInfo.Version.ToString() ?? "?";
            return $"attached pid={Pid} CLR={clr}";
        }
        if (DumpPath != null)
        {
            var clr = _clrRuntime?.ClrInfo.Version.ToString() ?? "?";
            return $"dump loaded: {DumpPath} CLR={clr}";
        }
        return "no target";
    }

    /// <summary>
    /// Run a callback on the debugger STA thread (required for all ICorDebug calls).
    /// The wrapper catches any exception thrown by <paramref name="fn"/> so it never
    /// escapes to dnSpy's dispatcher — an unhandled exception there kills the STA
    /// thread and ultimately the whole process.
    /// </summary>
    public T OnDbg<T>(Func<T> fn)
    {
        if (_dbgThread == null) throw new InvalidOperationException("no debugger thread (not attached)");
        T value = default!;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? caught = null;
        _dbgThread.Invoke(() =>
        {
            try { value = fn(); }
            catch (Exception ex) { caught = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
        });
        caught?.Throw();
        return value;
    }

    public void OnDbg(Action fn) => OnDbg<object?>(() => { fn(); return null; });

    public void Dispose() => Detach();
}
