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
            "[DEBUG] Set an IL-offset breakpoint. Params: {modulePath:string, token:uint, offset?:uint=0, condition?:string}. modulePath matches DnModule.Name suffix (case-insensitive). condition (op: ==/!=/>=/<=/>/<) is one of: `count <op> N` — pause on the Nth hit onward (e.g. \"count >= 5\"); or a value gate `arg<i>[.field...] <op> lit` / `local<i>[.field...] <op> lit` evaluated against the frame at the hit. Literals: integer (dec or 0x-hex), true/false, null, or a quoted string. Guid/DateTime fields compare by text and enums by member name, so e.g. \"arg0.UId == '0c81...'\", \"local1 > 100\", \"arg1.Name != null\".",
            p => Program.Session.OnDbg(() =>
            {
                var modulePath = Dispatcher.Req<string>(p, "modulePath");
                var token = Dispatcher.Req<uint>(p, "token");
                var offset = Dispatcher.Opt<uint>(p, "offset", 0);
                var condition = Dispatcher.Opt<string?>(p, "condition", null);

                var mod = FindModule(modulePath)
                    ?? throw new ArgumentException($"module not found: {modulePath}");

                var entryRef = new BreakpointEntryRef();
                var cond = BuildCondition(condition, entryRef);
                var bp = Program.Session.DnDebugger.CreateBreakpoint(mod.DnModuleId, token, offset, cond);
                var entry = Program.Session.Breakpoints.Add(
                    "il",
                    $"IL bp {Path.GetFileName(mod.Name)}!0x{token:X8}+{offset}",
                    bp);
                entry.Condition = condition;
                entryRef.Entry = entry;
                return Describe(entry);
            }));

        d.Register("bp.set_by_name",
            "[DEBUG] Set a breakpoint at IL=0 of a method identified by type and method name. Params: {modulePath:string, typeFullName:string, methodName:string, overloadIndex?:int=0, condition?:string}. condition (op: ==/!=/>=/<=/>/<) is one of: `count <op> N` — pause on the Nth hit onward (e.g. \"count >= 5\"); or a value gate `arg<i>[.field...] <op> lit` / `local<i>[.field...] <op> lit` evaluated against the frame (arg0 is the receiver 'this' on instance methods). Literals: integer (dec or 0x-hex), true/false, null, or quoted string. Guid/DateTime fields compare by text, enums by member name — e.g. \"arg0.Id == '0c81...'\", \"arg1 >= 10\", \"arg2.Name == 'Contact'\".",
            p => Program.Session.OnDbg(() =>
            {
                var modulePath = Dispatcher.Req<string>(p, "modulePath");
                var typeFullName = Dispatcher.Req<string>(p, "typeFullName");
                var methodName = Dispatcher.Req<string>(p, "methodName");
                var overloadIndex = Dispatcher.Opt<int>(p, "overloadIndex", 0);
                var condition = Dispatcher.Opt<string?>(p, "condition", null);

                var mod = FindModule(modulePath)
                    ?? throw new ArgumentException($"module not found: {modulePath}");
                var mdi = mod.CorModule.GetMetaDataInterface<dndbg.COM.MetaData.IMetaDataImport>()
                    ?? throw new InvalidOperationException("failed to get IMetaDataImport");

                var typeToken = MetaDataUtils.FindTypeDefByName(mdi, typeFullName);
                if (typeToken == 0) throw new ArgumentException($"type not found: {typeFullName}");
                var methodToken = MetaDataUtils.FindMethodByName(mdi, typeToken, methodName, overloadIndex);
                if (methodToken == 0) throw new ArgumentException($"method not found: {typeFullName}::{methodName} (overload {overloadIndex})");

                var entryRef = new BreakpointEntryRef();
                var cond = BuildCondition(condition, entryRef);
                var bp = Program.Session.DnDebugger.CreateBreakpoint(mod.DnModuleId, methodToken, 0, cond);
                var entry = Program.Session.Breakpoints.Add(
                    "by_name",
                    $"{typeFullName}::{methodName} [token=0x{methodToken:X8}]",
                    bp);
                entry.Condition = condition;
                entryRef.Entry = entry;
                return Describe(entry);
            }));

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
    {
        if (string.IsNullOrWhiteSpace(condition)) return null;
        var predicate = ConditionEvaluator.Compile(condition!);
        return ctx =>
        {
            var entry = entryRef.Entry;
            int n = entry == null ? 1 : System.Threading.Interlocked.Increment(ref entry.HitCount);
            return predicate(n, ctx);
        };
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
