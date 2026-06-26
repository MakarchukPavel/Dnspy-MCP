using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dndbg.Engine;
using DnSpyMcp.Agent.Services;
using Newtonsoft.Json.Linq;

namespace DnSpyMcp.Agent.Handlers;

public static class BreakpointHandlers
{
    public static void Register(Dispatcher d)
    {
        d.Register("bp.set_il",
            "[DEBUG] Set an IL-offset breakpoint. Params: {modulePath:string, token:uint, offset?:uint=0, condition?:string, logExpressions?:string[], logOnly?:bool=true, maxHits?:int=200}. modulePath matches DnModule.Name suffix (case-insensitive). condition (op: ==/!=/>=/<=/>/<) is one of: `count <op> N` — pause on the Nth hit onward (e.g. \"count >= 5\"); or a value gate `arg<i>[.field...] <op> lit` / `local<i>[.field...] <op> lit` evaluated against the frame at the hit. Literals: integer (dec or 0x-hex), true/false, null, or a quoted string. Guid/DateTime fields compare by text and enums by member name, so e.g. \"arg0.UId == '0c81...'\", \"local1 > 100\", \"arg1.Name != null\". TRACEPOINT/LOGPOINT: pass logExpressions (e.g. [\"arg0.Value\",\"local1\"]) to snapshot those passive values at each hit into a buffer fetched via bp.log; logOnly=true (default) auto-continues WITHOUT pausing (combine with condition to capture only matching hits); maxHits caps the buffer (default 200) then capture stops. NOTE: every hit still physically stops the whole process briefly (ICorDebug) — safe on cold paths only; no func-eval, so only primitives/strings/Guid/DateTime/enum/field-paths.",
            p => Program.Session.OnDbg(() =>
            {
                var modulePath = Dispatcher.Req<string>(p, "modulePath");
                var token = Dispatcher.Req<uint>(p, "token");
                var offset = Dispatcher.Opt<uint>(p, "offset", 0);
                var condition = Dispatcher.Opt<string?>(p, "condition", null);
                var (exprs, continueAfter, max) = ReadTraceParams(p);

                var mod = FindModule(modulePath)
                    ?? throw new ArgumentException($"module not found: {modulePath}");

                var entryRef = new BreakpointEntryRef();
                var cond = BuildCallback(condition, exprs, continueAfter, max, entryRef);
                var bp = Program.Session.DnDebugger.CreateBreakpoint(mod.DnModuleId, token, offset, cond);
                var entry = Program.Session.Breakpoints.Add(
                    exprs != null ? "tracepoint" : "il",
                    $"IL bp {Path.GetFileName(mod.Name)}!0x{token:X8}+{offset}",
                    bp);
                entry.Condition = condition;
                PrimeTrace(entry, exprs, continueAfter, max);
                entryRef.Entry = entry;
                return Describe(entry);
            }));

        d.Register("bp.set_by_name",
            "[DEBUG] Set a breakpoint at IL=0 of a method identified by type and method name. Params: {modulePath:string, typeFullName:string, methodName:string, overloadIndex?:int=0, condition?:string, logExpressions?:string[], logOnly?:bool=true, maxHits?:int=200}. condition (op: ==/!=/>=/<=/>/<) is one of: `count <op> N` — pause on the Nth hit onward (e.g. \"count >= 5\"); or a value gate `arg<i>[.field...] <op> lit` / `local<i>[.field...] <op> lit` evaluated against the frame (arg0 is the receiver 'this' on instance methods). Literals: integer (dec or 0x-hex), true/false, null, or quoted string. Guid/DateTime fields compare by text, enums by member name — e.g. \"arg0.Id == '0c81...'\", \"arg1 >= 10\", \"arg2.Name == 'Contact'\". TRACEPOINT/LOGPOINT: pass logExpressions (e.g. [\"arg0.Name\",\"arg1\"]) to snapshot those passive values at each hit into a buffer fetched via bp.log; logOnly=true (default) auto-continues WITHOUT pausing (combine with condition to capture only matching hits); maxHits caps the buffer (default 200) then capture stops. NOTE: every hit still physically stops the whole process briefly (ICorDebug) — safe on cold paths only; no func-eval, so only primitives/strings/Guid/DateTime/enum/field-paths.",
            p => Program.Session.OnDbg(() =>
            {
                var modulePath = Dispatcher.Req<string>(p, "modulePath");
                var typeFullName = Dispatcher.Req<string>(p, "typeFullName");
                var methodName = Dispatcher.Req<string>(p, "methodName");
                var overloadIndex = Dispatcher.Opt<int>(p, "overloadIndex", 0);
                var condition = Dispatcher.Opt<string?>(p, "condition", null);
                var (exprs, continueAfter, max) = ReadTraceParams(p);

                var mod = FindModule(modulePath)
                    ?? throw new ArgumentException($"module not found: {modulePath}");
                var mdi = mod.CorModule.GetMetaDataInterface<dndbg.COM.MetaData.IMetaDataImport>()
                    ?? throw new InvalidOperationException("failed to get IMetaDataImport");

                var typeToken = MetaDataUtils.FindTypeDefByName(mdi, typeFullName);
                if (typeToken == 0) throw new ArgumentException($"type not found: {typeFullName}");
                var methodToken = MetaDataUtils.FindMethodByName(mdi, typeToken, methodName, overloadIndex);
                if (methodToken == 0) throw new ArgumentException($"method not found: {typeFullName}::{methodName} (overload {overloadIndex})");

                var entryRef = new BreakpointEntryRef();
                var cond = BuildCallback(condition, exprs, continueAfter, max, entryRef);
                var bp = Program.Session.DnDebugger.CreateBreakpoint(mod.DnModuleId, methodToken, 0, cond);
                var entry = Program.Session.Breakpoints.Add(
                    exprs != null ? "tracepoint" : "by_name",
                    $"{typeFullName}::{methodName} [token=0x{methodToken:X8}]",
                    bp);
                entry.Condition = condition;
                PrimeTrace(entry, exprs, continueAfter, max);
                entryRef.Entry = entry;
                return Describe(entry);
            }));

        d.Register("bp.log",
            "[DEBUG] Fetch the values captured by a tracepoint/logpoint (a bp.set_* set with logExpressions). Params: {id:int, clear?:bool=false, offset?:int=0, max?:int=200}. Returns {id, description, hitCount, captured, capped, maxHits, logExpressions, samples:[{seq, hit, values:{expr->{kind,value,...}}}]}. capped=true means maxHits was reached and capture stopped. clear=true empties the buffer after reading (the breakpoint stays armed). Use bp.delete to remove the tracepoint entirely (stops the per-hit process stop).",
            p =>
            {
                var id = Dispatcher.Req<int>(p, "id");
                var clear = Dispatcher.Opt<bool>(p, "clear", false);
                var offset = Dispatcher.Opt<int>(p, "offset", 0);
                var max = Dispatcher.Opt<int>(p, "max", 200);
                if (!Program.Session.Breakpoints.TryGet(id, out var entry))
                    throw new ArgumentException($"bp id {id} not found");
                if (entry.Captures == null)
                    throw new ArgumentException($"bp id {id} is not a tracepoint (set logExpressions on bp.set_* to capture values)");

                List<object> snapshot;
                bool capped;
                lock (entry.Captures)
                {
                    snapshot = entry.Captures.Skip(Math.Max(0, offset)).Take(Math.Max(0, max)).ToList();
                    capped = entry.Capped;
                    if (clear) { entry.Captures.Clear(); entry.Capped = false; }
                }
                return new
                {
                    id = entry.Id,
                    description = entry.Description,
                    hitCount = entry.HitCount,
                    captured = entry.CaptureSeq,
                    capped,
                    maxHits = entry.MaxCaptures,
                    logExpressions = entry.LogExpressions,
                    returned = snapshot.Count,
                    samples = snapshot,
                };
            });

        d.Register("bp.set_native",
            "[DEBUG] Set a native-code breakpoint by absolute address. Params: {address:ulong, modulePath?:string, token?:uint}. If (modulePath,token) present, bp is scoped to that jitted function; else breakpoints the raw address via native code handle.",
            p => Program.Session.OnDbg(() =>
            {
                var address = Dispatcher.Req<ulong>(p, "address");
                var modulePath = Dispatcher.Opt<string?>(p, "modulePath", null);
                var token = Dispatcher.Opt<uint>(p, "token", 0);

                if (modulePath != null && token != 0)
                {
                    var mod = FindModule(modulePath) ?? throw new ArgumentException($"module not found: {modulePath}");
                    var moduleId = mod.DnModuleId;
                    var bp = Program.Session.DnDebugger.CreateNativeBreakpoint(moduleId, token, (uint)address, null);
                    var entry = Program.Session.Breakpoints.Add("native",
                        $"native {Path.GetFileName(mod.Name)}!0x{token:X8}+0x{address:X}", bp);
                    return Describe(entry);
                }
                throw new ArgumentException("bp.set_native currently requires (modulePath, token, offset)");
            }));

        d.Register("bp.list",
            "[DEBUG] List all breakpoints registered on this agent. Rows: {id, kind, description, enabled, condition, hitCount}.",
            _ =>
            {
                var rows = new List<object>();
                foreach (var e in Program.Session.Breakpoints.All) rows.Add(Describe(e));
                return rows;
            });

        d.Register("bp.delete",
            "[DEBUG] Remove a breakpoint by id. Params: {id:int}.",
            p => Program.Session.OnDbg(() =>
            {
                var id = Dispatcher.Req<int>(p, "id");
                if (!Program.Session.Breakpoints.TryGet(id, out var entry))
                    throw new ArgumentException($"bp id {id} not found");
                Program.Session.DnDebugger.RemoveBreakpoint((DnBreakpoint)entry.DnBreakpoint);
                Program.Session.Breakpoints.Remove(id);
                return new { deleted = id };
            }));

        d.Register("bp.enable",
            "[DEBUG] Enable a breakpoint by id. Params: {id:int}.",
            p => Program.Session.OnDbg(() => SetEnabled(p, true)));

        d.Register("bp.disable",
            "[DEBUG] Disable a breakpoint by id. Params: {id:int}.",
            p => Program.Session.OnDbg(() => SetEnabled(p, false)));
    }

    private static object SetEnabled(JObject? p, bool enabled)
    {
        var id = Dispatcher.Req<int>(p, "id");
        if (!Program.Session.Breakpoints.TryGet(id, out var entry))
            throw new ArgumentException($"bp id {id} not found");
        ((DnBreakpoint)entry.DnBreakpoint).IsEnabled = enabled;
        entry.Enabled = enabled;
        return Describe(entry);
    }

    private static object Describe(Services.BreakpointEntry e) => new
    {
        id = e.Id,
        kind = e.Kind,
        description = e.Description,
        enabled = e.Enabled,
        condition = e.Condition,
        hitCount = e.HitCount,
        // Tracepoint fields are null/0 for ordinary breakpoints.
        logExpressions = e.LogExpressions,
        logOnly = e.LogExpressions == null ? (bool?)null : e.ContinueAfter,
        maxHits = e.LogExpressions == null ? (int?)null : e.MaxCaptures,
        captured = e.LogExpressions == null ? (int?)null : e.CaptureSeq,
        capped = e.LogExpressions == null ? (bool?)null : e.Capped,
    };

    /// <summary>
    /// Forward-reference holder so the condition closure can find its
    /// BreakpointEntry after the entry is created. Avoids a chicken-and-egg
    /// problem (CreateBreakpoint needs the callback; the callback needs the
    /// entry; the entry is built from the bp returned by CreateBreakpoint).
    /// </summary>
    private sealed class BreakpointEntryRef { public Services.BreakpointEntry? Entry; }

    /// <summary>
    /// Compile a condition string into an ICorDebug breakpoint-condition
    /// callback. Returns null when condition is null/whitespace (unconditional
    /// BP). Delegates parsing to <see cref="ConditionEvaluator"/>, which
    /// supports both `count &lt;op&gt; N` hit-count gates and
    /// `arg&lt;i&gt;[.field…]/local&lt;i&gt;[.field…] &lt;op&gt; literal` value
    /// comparisons. A malformed condition throws here (at set time) so the
    /// error surfaces in the bp.set_* response.
    ///
    /// The callback Interlocked.Increments the entry's HitCount on every firing
    /// — so HitCount is the total number of times the instruction was hit,
    /// regardless of how many of those triggered an actual pause.
    /// </summary>
    private static Func<dndbg.Engine.ILCodeBreakpointConditionContext, bool>? BuildCondition(string? condition, BreakpointEntryRef entryRef)
        => BuildCallback(condition, null, false, 0, entryRef);

    /// <summary>
    /// Build the breakpoint-hit callback, fusing an optional condition gate with
    /// optional tracepoint capture. Returns null only when there is neither a
    /// condition nor any capture (a plain unconditional BP needs no callback).
    ///
    /// Semantics per hit (callback runs on the debugger STA thread, process
    /// physically stopped):
    ///   1. HitCount++ (true physical-hit count).
    ///   2. gate = condition predicate, or true if none.
    ///   3. if gate AND capturing AND under the cap: snapshot the passive values
    ///      into the entry's ring buffer. Once the cap is reached, capturing
    ///      stops — post-cap hits skip all ClrMD work.
    ///   4. Return value decides pause-vs-continue: a log-only tracepoint
    ///      (ContinueAfter) always returns false so dndbg auto-continues without
    ///      a client-visible pause; otherwise return the gate (pause iff true).
    ///
    /// The callback NEVER calls Continue() itself — it signals "continue" by
    /// returning false; dndbg's dispatch loop performs the actual resume. Driving
    /// Continue() (or func-eval) from inside this callback is unsafe.
    /// </summary>
    private static Func<dndbg.Engine.ILCodeBreakpointConditionContext, bool>? BuildCallback(
        string? condition, string[]? logExpressions, bool continueAfter, int maxCaptures, BreakpointEntryRef entryRef)
    {
        var predicate = string.IsNullOrWhiteSpace(condition) ? null : ConditionEvaluator.Compile(condition!);
        var readers = (logExpressions == null || logExpressions.Length == 0)
            ? null
            : logExpressions.Select(e => (expr: e, read: ConditionEvaluator.CompileCapture(e))).ToArray();

        if (predicate == null && readers == null) return null;

        return ctx =>
        {
            var entry = entryRef.Entry;
            int n = entry == null ? 1 : System.Threading.Interlocked.Increment(ref entry.HitCount);

            bool gate;
            try { gate = predicate == null || predicate(n, ctx); }
            catch { gate = true; } // fail-open: a broken gate pauses/captures rather than silently dropping

            if (gate && readers != null && entry != null)
                CaptureSample(entry, n, readers, ctx);

            // Log-only tracepoint never pauses; a plain/conditional BP pauses on gate.
            return continueAfter ? false : gate;
        };
    }

    /// <summary>Snapshot the configured passive expressions into the entry's capped ring buffer.</summary>
    private static void CaptureSample(Services.BreakpointEntry entry, int hit,
        (string expr, Func<dndbg.Engine.ILCodeBreakpointConditionContext, object> read)[] readers,
        dndbg.Engine.ILCodeBreakpointConditionContext ctx)
    {
        var buf = entry.Captures;
        if (buf == null) return;
        // Cheap cap check first so post-cap hits skip all ClrMD reads.
        lock (buf)
        {
            if (buf.Count >= entry.MaxCaptures) { entry.Capped = true; return; }
        }
        var values = new Dictionary<string, object>();
        foreach (var r in readers)
        {
            try { values[r.expr] = r.read(ctx); }
            catch (Exception ex) { values[r.expr] = new { kind = "error", error = ex.Message }; }
        }
        var sample = new { seq = ++entry.CaptureSeq, hit, values };
        lock (buf)
        {
            if (buf.Count < entry.MaxCaptures) buf.Add(sample);
            else entry.Capped = true;
        }
    }

    private const int DefaultMaxCaptures = 200;

    /// <summary>
    /// Read tracepoint-mode params from a request and, when logExpressions are
    /// present, prime the entry's capture buffer. Returns the (logExpressions,
    /// continueAfter, maxCaptures) triple to feed <see cref="BuildCallback"/>.
    /// </summary>
    private static (string[]? exprs, bool continueAfter, int max) ReadTraceParams(JObject? p)
    {
        var arr = p? ["logExpressions"] as JArray;
        var exprs = arr?.Select(t => t.Value<string>() ?? "").Where(s => s.Length > 0).ToArray();
        if (exprs != null && exprs.Length == 0) exprs = null;
        // log-only (auto-continue) defaults to true when capturing — that's the
        // tracepoint use case; pass logOnly:false to ALSO pause on each capture.
        var continueAfter = exprs == null ? false : Dispatcher.Opt<bool>(p, "logOnly", true);
        var max = Dispatcher.Opt<int>(p, "maxHits", DefaultMaxCaptures);
        if (max <= 0) max = DefaultMaxCaptures;
        return (exprs, continueAfter, max);
    }

    /// <summary>Attach tracepoint capture state to a freshly-created entry (no-op when not a tracepoint).</summary>
    private static void PrimeTrace(Services.BreakpointEntry entry, string[]? exprs, bool continueAfter, int max)
    {
        if (exprs == null) return;
        entry.LogExpressions = exprs;
        entry.ContinueAfter = continueAfter;
        entry.MaxCaptures = max;
        entry.Captures = new List<object>();
    }

    /// <summary>
    /// Describe which breakpoint(s) the target is currently paused on, if any.
    /// Returns null when the target is running, paused for a non-BP reason
    /// (step complete, user pause, exception), or when no opened DnDebugger.
    ///
    /// Walks dnSpy's <c>DnDebugger.Current.PauseStates</c> and matches each
    /// <c>ILCodeBreakpointPauseState</c> / <c>NativeCodeBreakpointPauseState</c>
    /// against the agent's <see cref="BreakpointRegistry"/> by reference equality
    /// on the underlying <c>DnBreakpoint</c>. Surfaces a list because a single
    /// pause can be triggered by multiple BPs at the same address (rare, but
    /// dnSpy iterates all matching BPs).
    /// </summary>
    public static object? DescribeCurrentBpHit()
    {
        if (!Program.Session.IsAttached) return null;
        return Program.Session.OnDbg<object?>(() =>
        {
            var dbg = Program.Session.DnDebugger;
            if (dbg.ProcessState != DebuggerProcessState.Paused) return null;
            var current = dbg.Current;
            if (current?.PauseStates == null || current.PauseStates.Length == 0) return null;

            var hits = new List<object>();
            foreach (var ps in current.PauseStates)
            {
                object? dnBp = ps switch
                {
                    ILCodeBreakpointPauseState il => il.Breakpoint,
                    NativeCodeBreakpointPauseState nb => nb.Breakpoint,
                    _ => null,
                };
                if (dnBp == null) continue;

                var entry = Program.Session.Breakpoints.All.FirstOrDefault(e => ReferenceEquals(e.DnBreakpoint, dnBp));
                uint? token = null, offset = null;
                if (dnBp is DnCodeBreakpoint code) { token = code.Token; offset = code.Offset; }
                hits.Add(new
                {
                    id = entry?.Id,
                    kind = entry?.Kind ?? (dnBp is DnILCodeBreakpoint ? "il" : dnBp is DnNativeCodeBreakpoint ? "native" : "unknown"),
                    description = entry?.Description,
                    methodToken = token,
                    ilOffset = offset,
                    threadUniqueId = current.Thread?.UniqueId,
                    osThreadId = current.Thread?.ThreadId,
                });
            }
            return hits.Count == 0 ? null : new { count = hits.Count, hits };
        });
    }

    /// <summary>
    /// Set a breakpoint from a JSON spec produced by a session.attach
    /// initialBreakpoints entry. Returns the public {id, kind, description,
    /// enabled} envelope so the attach response can list everything that was
    /// registered (and what failed). Caller MUST be on the debugger STA
    /// thread (this is not wrapped in OnDbg internally; the attach handler
    /// already owns that scope).
    ///
    /// Supported kinds (the same handlers exposed via bp.set_*):
    ///   {kind:"by_name", modulePath, typeFullName, methodName, overloadIndex?, condition?}
    ///   {kind:"il",      modulePath, token, offset?, condition?}
    ///   {kind:"native",  address, modulePath?, token? (no condition support — different cb sig)}
    /// </summary>
    public static object SetBreakpointFromSpec(JObject spec)
    {
        var kind = spec["kind"]?.Value<string>()?.ToLowerInvariant()
            ?? throw new ArgumentException("breakpoint spec missing 'kind'");

        return kind switch
        {
            "by_name" => SetByName(spec),
            "il" => SetIl(spec),
            "native" => SetNative(spec),
            _ => throw new ArgumentException($"unsupported breakpoint kind: {kind}"),
        };
    }

    private static object SetByName(JObject p)
    {
        var modulePath = p["modulePath"]?.Value<string>() ?? throw new ArgumentException("by_name missing modulePath");
        var typeFullName = p["typeFullName"]?.Value<string>() ?? throw new ArgumentException("by_name missing typeFullName");
        var methodName = p["methodName"]?.Value<string>() ?? throw new ArgumentException("by_name missing methodName");
        var overloadIndex = p["overloadIndex"]?.Value<int>() ?? 0;
        var condition = p["condition"]?.Value<string>();

        var mod = FindModule(modulePath) ?? throw new ArgumentException($"module not found: {modulePath}");
        var mdi = mod.CorModule.GetMetaDataInterface<dndbg.COM.MetaData.IMetaDataImport>()
            ?? throw new InvalidOperationException("failed to get IMetaDataImport");
        var typeToken = MetaDataUtils.FindTypeDefByName(mdi, typeFullName);
        if (typeToken == 0) throw new ArgumentException($"type not found: {typeFullName}");
        var methodToken = MetaDataUtils.FindMethodByName(mdi, typeToken, methodName, overloadIndex);
        if (methodToken == 0) throw new ArgumentException($"method not found: {typeFullName}::{methodName} (overload {overloadIndex})");

        var entryRef = new BreakpointEntryRef();
        var cond = BuildCondition(condition, entryRef);
        var bp = Program.Session.DnDebugger.CreateBreakpoint(mod.DnModuleId, methodToken, 0, cond);
        var entry = Program.Session.Breakpoints.Add("by_name",
            $"{typeFullName}::{methodName} [token=0x{methodToken:X8}]", bp);
        entry.Condition = condition;
        entryRef.Entry = entry;
        return Describe(entry);
    }

    private static object SetIl(JObject p)
    {
        var modulePath = p["modulePath"]?.Value<string>() ?? throw new ArgumentException("il missing modulePath");
        var token = p["token"]?.Value<uint>() ?? throw new ArgumentException("il missing token");
        var offset = p["offset"]?.Value<uint>() ?? 0;
        var condition = p["condition"]?.Value<string>();

        var mod = FindModule(modulePath) ?? throw new ArgumentException($"module not found: {modulePath}");
        var entryRef = new BreakpointEntryRef();
        var cond = BuildCondition(condition, entryRef);
        var bp = Program.Session.DnDebugger.CreateBreakpoint(mod.DnModuleId, token, offset, cond);
        var entry = Program.Session.Breakpoints.Add("il",
            $"IL bp {Path.GetFileName(mod.Name)}!0x{token:X8}+{offset}", bp);
        entry.Condition = condition;
        entryRef.Entry = entry;
        return Describe(entry);
    }

    private static object SetNative(JObject p)
    {
        var address = p["address"]?.Value<ulong>() ?? throw new ArgumentException("native missing address");
        var modulePath = p["modulePath"]?.Value<string>();
        var token = p["token"]?.Value<uint>() ?? 0;
        if (modulePath == null || token == 0)
            throw new ArgumentException("native bp currently requires (modulePath, token, address)");
        // Native bp condition callback uses NativeCodeBreakpointConditionContext,
        // not ILCodeBreakpointConditionContext — different signature, so we skip
        // condition support for native bps until a generic dispatcher exists.
        // (Conditions are rarely useful for native code anyway.)
        var mod = FindModule(modulePath) ?? throw new ArgumentException($"module not found: {modulePath}");
        var bp = Program.Session.DnDebugger.CreateNativeBreakpoint(mod.DnModuleId, token, (uint)address, null);
        var entry = Program.Session.Breakpoints.Add("native",
            $"native {Path.GetFileName(mod.Name)}!0x{token:X8}+0x{address:X}", bp);
        return Describe(entry);
    }

    private static DnModule? FindModule(string modulePathSuffix)
    {
        var dbg = Program.Session.DnDebugger;
        DnModule? best = null;
        foreach (var proc in dbg.Processes)
            foreach (var ad in proc.AppDomains)
                foreach (var asm in ad.Assemblies)
                    foreach (var mod in asm.Modules)
                    {
                        if (mod.Name.EndsWith(modulePathSuffix, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetFileName(mod.Name), modulePathSuffix, StringComparison.OrdinalIgnoreCase))
                            return mod;
                        if (best == null && mod.Name.IndexOf(modulePathSuffix, StringComparison.OrdinalIgnoreCase) >= 0)
                            best = mod;
                    }
        return best;
    }
}
