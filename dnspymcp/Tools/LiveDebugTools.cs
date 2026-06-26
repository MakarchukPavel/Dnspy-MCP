using System.ComponentModel;
using DnSpyMcp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DnSpyMcp.Tools;

/// <summary>
/// Thin proxies over the dnspymcpagent TCP+NDJSON backend. Every tool here
/// is [DEBUG] — it talks to a running / dumped .NET process through an
/// <see cref="AgentClient"/>. Multiple agents can be connected at once (one
/// per named slot in <see cref="AgentRegistry"/>); tools default to the
/// active slot, or you can pass <c>agent</c> to target a specific one.
/// </summary>
[McpServerToolType]
public static class LiveDebugTools
{
    // ---- agent session management (idalib-style) ----------------------
    // Each agent endpoint (host:port) is a named session. Open once, then
    // list / switch between them for free — the TCP connection is kept warm
    // and auto-reconnects if the underlying agent restarts. You never need
    // to disconnect+reconnect between tool calls; just `switch`.

    [McpServerTool(Name = "debug_session_connect")]
    [Description("[DEBUG] Connect (or re-connect) a named TCP session to a dnspymcpagent at host:port and make it active. Idempotent — calling with an existing name reconfigures and reconnects that slot. Does NOT attach the debugger to any target; call `debug_pid_attach` for that. The MCP server enforces per-tenant exclusivity on (host, port): a different MCP tenant (different Authorization Bearer) cannot hold the same dnspymcpagent at the same time. Params: host (required), port (required), token=null (agent-side auth), name='default'.")]
    public static object AgentConnect(AgentRegistry reg, string host, int port, string? token = null, string name = "default")
    {
        reg.OpenSlot(name, host, port, token);
        return new { connected = true, name, host, port, active = name };
    }

    [McpServerTool(Name = "debug_session_disconnect")]
    [Description("[DEBUG] Disconnect a session: closes TCP link and unregisters the slot. Does NOT detach the remote agent from its target (use `debug_pid_detach` first if needed). Params: name (required).")]
    public static object AgentDisconnect(AgentRegistry reg, string name)
        => new { disconnected = reg.Remove(name), current = reg.ActiveName };

    [McpServerTool(Name = "debug_session_list")]
    [Description("[DEBUG] List every open session (name, host:port, TCP connected?, active?).")]
    public static object AgentList(AgentRegistry reg)
    {
        var active = reg.ActiveName;
        return reg.All.Select(kv => new {
            name = kv.Key,
            host = kv.Value.Host,
            port = kv.Value.Port,
            connected = kv.Value.IsConnected,
            active = string.Equals(kv.Key, active, StringComparison.OrdinalIgnoreCase),
        }).ToArray();
    }

    [McpServerTool(Name = "debug_session_info")]
    [Description("[DEBUG] Describe the currently-active session: which named slot is active, TCP host/port, and the full debug state of its agent (attached pid / dump path / last-exit pid+reason+UTC retained across detach). One-stop status — no separate session_info needed.")]
    public static object AgentCurrent(AgentRegistry reg)
    {
        var name = reg.ActiveName;
        if (string.IsNullOrEmpty(name))
            return new { current = (string?)null, connected = false, debugState = (object?)null };

        var agent = reg.Get(null);
        // Fetch the agent's debug state; if the agent TCP session is down we
        // still want to surface SOMETHING useful rather than throwing.
        object? debugState = null;
        string? error = null;
        try { debugState = agent.Result("session.info"); }
        catch (Exception ex) { error = ex.Message; }

        return new
        {
            current = name,
            host = agent.Host,
            port = agent.Port,
            connected = agent.IsConnected,
            debugState,
            debugStateError = error,
        };
    }

    [McpServerTool(Name = "debug_session_switch")]
    [Description("[DEBUG] Switch the active session. Subsequent LIVE tools target this one when 'agent' is omitted — no reconnect needed.")]
    public static object AgentSwitch(AgentRegistry reg, string name)
    {
        reg.Switch(name);
        return new { current = name };
    }

    [McpServerTool(Name = "debug_list_methods")]
    [Description("[DEBUG] Ask an agent to list every registered debug method (paginated). Params: offset=0, max=200, agent (optional — uses active). Response: {total, offset, returned, truncated, items}.")]
    public static object AgentListMethods(AgentRegistry reg, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("__list__"), offset, max);

    // ---- session ---------------------------------------------------------

    [McpServerTool(Name = "debug_list_dotnet_processes")]
    [Description("[DEBUG] List running .NET processes on the agent's host (paginated). Params: offset=0, max=200.")]
    public static object ListProcesses(AgentRegistry reg, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("session.dotnet_processes"), offset, max);

    // Agent lifecycle is runtime-controllable: one agent process can be
    // attached to any local PID over its lifetime, and auto-detaches when
    // the target dies. It can also be repointed at a dump file (debug_load_dump).

    [McpServerTool(Name = "debug_load_dump")]
    [Description("[DEBUG] Load a .NET crash/process dump (.dmp) for passive postmortem analysis via ClrMD. Heap tools (debug_heap_find_instances / read_object / read_array / read_string / stats) and struct decoding (Guid/DateTime/enum/...) work on the dump. NO live debugging — breakpoints, stepping, frames, exceptions, and func-eval need a live process and are unavailable on a dump. Detaches any current target first. Params: path = absolute path to the .dmp ON THE AGENT'S MACHINE; agent (optional). Switch back with debug_pid_attach.")]
    public static object LoadDump(AgentRegistry reg, string path, string? agent = null)
        => reg.Get(agent).Result("session.load_dump", new { path })!;

    [McpServerTool(Name = "debug_launch")]
    [Description("[DEBUG] Launch a .NET Framework executable UNDER the debugger and break at its managed entry point. Unlike debug_pid_attach, the debugger is present before any module loads, so JIT optimization is disabled for ALL modules — func-eval (debug_eval_call) then works even on Release/optimized assemblies, which a late attach cannot achieve (it hits BAD_START_POINT). Detaches any current target. On return the process is PAUSED at the entry point — set breakpoints, then debug_go. Params: exePath (absolute path on the agent's machine), args (optional), workingDir (optional), agent (optional). NOTE: this launches a process YOU control — for IIS-hosted apps you cannot launch w3wp yourself (deploy Debug, or attach early to a freshly-recycled worker).")]
    public static object Launch(AgentRegistry reg, string exePath, string? args = null, string? workingDir = null, string? agent = null)
        => reg.Get(agent).Result("session.launch", new { exePath, args, workingDir })!;

    [McpServerTool(Name = "debug_jit_status")]
    [Description("[DEBUG] Report which loaded modules are func-eval-ready. func-eval needs debuggable (un-optimized) JIT, which a module gets only if it loaded UNDER the debugger; a module already JITted before attach stays optimized (func-eval there fails with BAD_START_POINT). Workflow for func-eval on Release Creatio: debug_pid_attach to w3wp, then debug_touch_config (or recycle the app pool) to force an app-domain reload of app-private assemblies (e.g. Terrasoft.Core) WHILE attached, then call this to confirm 'loadedUnderDebugger':true before func-eval. Params: pattern (optional substring, e.g. 'Terrasoft.Core'); agent (optional).")]
    public static object JitStatus(AgentRegistry reg, string? pattern = null, string? agent = null)
        => reg.Get(agent).Result("session.jit_status", new { pattern })!;

    [McpServerTool(Name = "debug_touch_config")]
    [Description("[DEBUG] Touch a file (bump its last-write time, no content change) on the agent's machine — typically a site's web.config — to trigger an ASP.NET app-domain reload. While attached, the reloaded app-private assemblies JIT debuggable (verify with debug_jit_status), enabling func-eval on them without recycling the whole worker process. Params: path (absolute path to the file); agent (optional).")]
    public static object TouchConfig(AgentRegistry reg, string path, string? agent = null)
        => reg.Get(agent).Result("session.touch_file", new { path })!;

    [McpServerTool(Name = "debug_pid_attach")]
    [Description("[DEBUG] Ask the agent to attach its debugger to a local PID. If already attached, detaches first. Idempotent on the same pid. Optionally registers breakpoints atomically with the attach via initialBreakpointsJson — a JSON array of `{kind:\"by_name\"|\"il\"|\"native\", ...}` specs (matching debug_bp_set_by_name / debug_bp_set_il / debug_bp_set_native params). Closes the attach<->first-RPC race so very-early code paths get caught. Response carries `initialBreakpoints[]` per-spec results. Params: pid:int, initialBreakpointsJson (optional), agent (optional).")]
    public static object Attach(AgentRegistry reg, int pid, string? initialBreakpointsJson = null, string? agent = null)
    {
        if (!string.IsNullOrWhiteSpace(initialBreakpointsJson))
        {
            // Validate parse client-side so the caller gets a clear MCP-level
            // error rather than a generic agent failure message.
            try { _ = System.Text.Json.JsonDocument.Parse(initialBreakpointsJson); }
            catch (System.Text.Json.JsonException ex)
            { throw new McpException($"initialBreakpointsJson is not valid JSON: {ex.Message}"); }
            return reg.Get(agent).Result("session.attach", new { pid, initialBreakpointsJson })!;
        }
        return reg.Get(agent).Result("session.attach", new { pid })!;
    }

    [McpServerTool(Name = "debug_pid_detach")]
    [Description("[DEBUG] Ask the agent to detach from its current target. Agent keeps listening. No-op if not attached. Params: agent (optional).")]
    public static object Detach(AgentRegistry reg, string? agent = null)
        => reg.Get(agent).Result("session.detach")!;

    // session state is surfaced through debug_session_info — no separate
    // tool. To kill the target process use the OS (taskkill / kill -9);
    // that's not a debugger responsibility so no terminate tool is exposed.

    [McpServerTool(Name = "debug_go")]
    [Description("[DEBUG] Continue the target (like WinDbg `g`).")]
    public static object Go(AgentRegistry reg, string? agent = null)
        => reg.Get(agent).Result("step.go")!;

    [McpServerTool(Name = "debug_pause")]
    [Description("[DEBUG] Break (pause) the target.")]
    public static object Pause(AgentRegistry reg, string? agent = null)
        => reg.Get(agent).Result("step.pause")!;

    [McpServerTool(Name = "debug_wait_paused")]
    [Description("[DEBUG] Wait until the target enters Paused (breakpoint / step). Params: timeoutMs=5000.")]
    public static object WaitPaused(AgentRegistry reg, int timeoutMs = 5000, string? agent = null)
        => reg.Get(agent).Result("debug.wait_paused", new { timeoutMs })!;

    // ---- threads / stack ------------------------------------------------

    [McpServerTool(Name = "debug_thread_list")]
    [Description("[DEBUG] List managed threads in the target process (paginated). Params: offset=0, max=200.")]
    public static object ThreadList(AgentRegistry reg, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("thread.list"), offset, max);

    [McpServerTool(Name = "debug_thread_stack")]
    [Description("[DEBUG] Walk a thread's managed call stack (paginated). Identify the thread with EITHER uniqueId (debugger-assigned, from debug_thread_list) OR osThreadId (OS thread id). Legacy alias: threadId == uniqueId. Params: uniqueId?:int, osThreadId?:int, threadId?:int, offset=0, max=200.")]
    public static object ThreadStack(AgentRegistry reg, int? uniqueId = null, int? osThreadId = null, int? threadId = null, int offset = 0, int max = 200, string? agent = null)
    {
        // threadId is a legacy alias for uniqueId. If both given, they must agree.
        if (threadId is int leg)
        {
            if (uniqueId is int u && u != leg)
                throw new McpException("threadId and uniqueId disagree; pass only one.");
            uniqueId ??= leg;
        }
        int provided = (uniqueId.HasValue ? 1 : 0) + (osThreadId.HasValue ? 1 : 0);
        if (provided == 0) throw new McpException("supply uniqueId or osThreadId.");
        if (provided == 2) throw new McpException("pass exactly one of uniqueId / osThreadId.");

        var fetch = System.Math.Max(1, offset + System.Math.Min(max, Paging.HardMaxRows));
        var payload = uniqueId.HasValue
            ? (object)new { uniqueId = uniqueId.Value, max = fetch }
            : new { osThreadId = osThreadId!.Value, max = fetch };
        return Paging.PageJsonArray(reg.Get(agent).Result("thread.stack", payload), offset, max);
    }

    [McpServerTool(Name = "debug_frame_locals")]
    [Description("[DEBUG] Read managed local variables of the currently-paused frame. Primitives are decoded; references return type+address. Pass frameIndex to pick a deeper frame on the pause-thread (default 0 = top). Params: frameIndex=0.")]
    public static object FrameLocals(AgentRegistry reg, int frameIndex = 0, string? agent = null)
        => reg.Get(agent).Result("frame.locals", new { frameIndex })!;

    [McpServerTool(Name = "debug_frame_arguments")]
    [Description("[DEBUG] Read managed arguments (this + parameters) of the currently-paused frame. Same shape as debug_frame_locals. Params: frameIndex=0.")]
    public static object FrameArguments(AgentRegistry reg, int frameIndex = 0, string? agent = null)
        => reg.Get(agent).Result("frame.arguments", new { frameIndex })!;

    [McpServerTool(Name = "debug_thread_current")]
    [Description("[DEBUG] Return which thread triggered the last pause.")]
    public static object CurrentThread(AgentRegistry reg, string? agent = null)
        => reg.Get(agent).Result("thread.current")!;

    [McpServerTool(Name = "debug_eval")]
    [Description("[DEBUG] Read a value expression against the currently-paused frame WITHOUT running any target code (passive object-graph read — NOT func-eval). expr starts with a root — arg<i>, local<i>, or 'this' (= arg0 on instance methods) — then an optional dotted field/auto-property path. Examples: \"arg0\", \"this.Entity\", \"arg1.Owner.Name\", \"local0.UId\". Property names resolve their backing field; Guid/DateTime/enum/struct leaves are decoded; an object leaf returns {kind:object,type,address} to drill into with debug_heap_read_object. Method/property invocation (.ToString(), GetX()) is unsupported by design — ICorDebug func-eval can deadlock a live worker. Params: expr (required), frameIndex=0.")]
    public static object Eval(AgentRegistry reg, string expr, int frameIndex = 0, string? agent = null)
        => reg.Get(agent).Result("eval.expression", new { expr, frameIndex })!;

    [McpServerTool(Name = "debug_eval_call")]
    [Description("[DEBUG] Invoke an instance method or property getter on the paused frame and return the result — real func-eval via ICorDebug (RUNS target code, unlike debug_eval). expr = \"<receiver>.<member>[<TypeArgs>]([args])\". Receiver is a root (arg<i>/local<i>/'this') with an OPTIONAL field/auto-property path — e.g. \"this.Entity.GetTypedColumnValue<System.Guid>('Id')\". A bare <member> (no parens) is a property getter (get_<member>) then a 0-arg method; <member>(...) calls a method; generic methods take explicit type args. Arguments are literals (integer dec/0x-hex, true/false, null, quoted string) OR value expressions arg/local/this[.field] resolved to live objects — e.g. \"arg0.Equals(arg1)\", \"this.Save(arg0.Context)\". Overloads selected by (arg count, type-arg count, AND arg type — a string literal picks (string) over (SomeClass)); resolves across base types. Runs on the paused thread with other threads SUSPENDED, timeout (default 2000ms) + abort. WARNING: runs target code — if it blocks on a lock another thread holds it can stall the target until the timeout; prefer debug_eval (passive) when a field/auto-property answers the question. Result decoded like debug_eval; a thrown exception returns {kind:'exception',type,message}; timeout returns {kind:'timeout'}. Not supported: nested-generic type args (List<int>), value-type receivers. Params: expr (required), frameIndex=0, timeoutMs=2000.")]
    public static object EvalCall(AgentRegistry reg, string expr, int frameIndex = 0, int timeoutMs = 2000, string? agent = null)
        => reg.Get(agent).Result("eval.call", new { expr, frameIndex, timeoutMs })!;

    // ---- modules --------------------------------------------------------

    [McpServerTool(Name = "debug_list_modules")]
    [Description("[DEBUG] List managed modules currently loaded in the attached process (paginated). Default rows: {shortName, name, address}. Pass verbose=true to also get {appDomain, assembly, size, isDynamic, isInMemory}. Params: offset=0, max=200, verbose=false.")]
    public static object ListModules(AgentRegistry reg, int offset = 0, int max = 200, bool verbose = false, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("module.list_live", new { verbose }), offset, max);

    [McpServerTool(Name = "debug_find_type")]
    [Description("[DEBUG] Find a type by full name across all loaded modules (paginated). Returns module path + typeDef token. Params: typeFullName, offset=0, max=200.")]
    public static object FindType(AgentRegistry reg, string typeFullName, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("module.find_type_live", new { typeFullName }), offset, max);

    [McpServerTool(Name = "debug_list_type_methods")]
    [Description("[DEBUG] Enumerate methods of a type inside a loaded module (paginated). Params: modulePath (path suffix ok), typeFullName, offset=0, max=200.")]
    public static object ListTypeMethods(AgentRegistry reg, string modulePath, string typeFullName, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("module.list_type_methods", new { modulePath, typeFullName }), offset, max);

    // ---- breakpoints ----------------------------------------------------

    [McpServerTool(Name = "debug_bp_set_il")]
    [Description("[DEBUG] Set an IL-offset breakpoint. Params: modulePath (suffix ok), token (uint as string — decimal '123' or hex '0x06000123'; also 0o / 0b accepted), offset (uint, default '0'; same forms), condition (optional; op ==/!=/>=/<=/>/<). condition is either `count <op> N` (e.g. 'count >= 5' skips the first 4 hits) or a value gate `arg<i>[.field...] <op> lit` / `local<i>[.field...] <op> lit` evaluated against the frame. Literals: integer (dec or 0x-hex), true/false, null, or a quoted string. Guid/DateTime fields compare by text, enums by member name; property names resolve their backing field — e.g. \"arg0.UId == '0c81...'\", \"local1 > 100\", \"arg1.Name != null\". TRACEPOINT/LOGPOINT: pass logExpressions (e.g. [\"arg0.Value\",\"local1\"]) to snapshot those passive values at each hit into a buffer fetched via debug_bp_log; logOnly=true (default) auto-continues WITHOUT pausing; combine with condition to capture only matching hits; maxHits caps the buffer (default 200), then capture stops. Every hit still briefly stops the whole process (ICorDebug) — cold paths only; no func-eval (primitives/strings/Guid/DateTime/enum/field-paths only). Get IL offsets for a source line via reverse_il_at_source_line.")]
    public static object BpSetIl(AgentRegistry reg, string modulePath, string token, string offset = "0", string? condition = null, string[]? logExpressions = null, bool logOnly = true, int maxHits = 200, string? agent = null)
    {
        var tok = Numbers.ParseUInt32(token, "token");
        var off = Numbers.ParseUInt32(offset, "offset");
        return reg.Get(agent).Result("bp.set_il", new { modulePath, token = tok, offset = off, condition, logExpressions, logOnly, maxHits })!;
    }

    [McpServerTool(Name = "debug_bp_set_by_name")]
    [Description("[DEBUG] Set a breakpoint at IL=0 of a named method. Params: modulePath, typeFullName, methodName, overloadIndex=0, condition (optional; op ==/!=/>=/<=/>/<). condition is either `count <op> N` (pause on the Nth hit onward, e.g. 'count >= 5') or a value gate `arg<i>[.field...] <op> lit` / `local<i>[.field...] <op> lit` evaluated against the frame (arg0 = 'this' on instance methods). Literals: integer (dec or 0x-hex), true/false, null, or quoted string. Guid/DateTime fields compare by text, enums by member name; property names resolve their backing field — e.g. \"arg0.Id == '0c81...'\", \"arg1 >= 10\", \"arg2.Name == 'Contact'\". TRACEPOINT/LOGPOINT: pass logExpressions (e.g. [\"arg0.Name\",\"arg1\"]) to snapshot those passive values at each hit into a buffer fetched via debug_bp_log; logOnly=true (default) auto-continues WITHOUT pausing; combine with condition to capture only matching hits; maxHits caps the buffer (default 200), then capture stops. Every hit still briefly stops the whole process (ICorDebug) — cold paths only; no func-eval (primitives/strings/Guid/DateTime/enum/field-paths only).")]
    public static object BpSetByName(AgentRegistry reg, string modulePath, string typeFullName, string methodName, int overloadIndex = 0, string? condition = null, string[]? logExpressions = null, bool logOnly = true, int maxHits = 200, string? agent = null)
        => reg.Get(agent).Result("bp.set_by_name", new { modulePath, typeFullName, methodName, overloadIndex, condition, logExpressions, logOnly, maxHits })!;

    [McpServerTool(Name = "debug_bp_log")]
    [Description("[DEBUG] Fetch the values captured by a tracepoint/logpoint (a breakpoint set with logExpressions). Params: id (bp id), clear=false (empty the buffer after reading; the bp stays armed), offset=0, max=200. Returns {id, description, hitCount, captured, capped, maxHits, logExpressions, samples:[{seq, hit, values:{expr->{kind,value,...}}}]}. capped=true means maxHits was reached and capture stopped. Use debug_bp_delete to remove the tracepoint entirely (stops the per-hit process stop).")]
    public static object BpLog(AgentRegistry reg, int id, bool clear = false, int offset = 0, int max = 200, string? agent = null)
        => reg.Get(agent).Result("bp.log", new { id, clear, offset, max })!;

    [McpServerTool(Name = "debug_bp_list")]
    [Description("[DEBUG] List all breakpoints currently registered on the agent (paginated). Params: offset=0, max=200.")]
    public static object BpList(AgentRegistry reg, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("bp.list"), offset, max);

    [McpServerTool(Name = "debug_bp_delete")]
    [Description("[DEBUG] Delete a breakpoint by id.")]
    public static object BpDelete(AgentRegistry reg, int id, string? agent = null)
        => reg.Get(agent).Result("bp.delete", new { id })!;

    [McpServerTool(Name = "debug_bp_enable")]
    [Description("[DEBUG] Enable a breakpoint by id.")]
    public static object BpEnable(AgentRegistry reg, int id, string? agent = null)
        => reg.Get(agent).Result("bp.enable", new { id })!;

    [McpServerTool(Name = "debug_bp_disable")]
    [Description("[DEBUG] Disable a breakpoint by id (kept registered, just not active).")]
    public static object BpDisable(AgentRegistry reg, int id, string? agent = null)
        => reg.Get(agent).Result("bp.disable", new { id })!;

    // ---- exception interception -----------------------------------------

    [McpServerTool(Name = "debug_exception_break_set")]
    [Description("[DEBUG] Arm managed-exception interception: pause the target when an exception is thrown. mode: all | unhandled | by_type. typeName (for by_type; substring/full name, case-insensitive). firstChance=true stops at the throw; false waits for unhandled. excludeTypes: comma/semicolon-separated type-name substrings to IGNORE. WORKFLOW for an unknown bug amid noise (IIS/Creatio throw many first-chance internally): arm mode=all, then debug_exception_ignore_add each noisy type as it appears until debug_wait_paused goes quiet, then reproduce — the real exception (not ignored) pauses. Catch with debug_wait_paused (exceptionHit: type/message/hResult/unhandled/thread). Params: mode='by_type', typeName?, firstChance=true, excludeTypes?.")]
    public static object ExceptionBreakSet(AgentRegistry reg, string mode = "by_type", string? typeName = null, bool firstChance = true, string? excludeTypes = null, string? agent = null)
        => reg.Get(agent).Result("exception.break_set", new { mode, typeName, firstChance, excludeTypes })!;

    [McpServerTool(Name = "debug_exception_break_clear")]
    [Description("[DEBUG] Disarm exception interception (thrown exceptions no longer pause the target).")]
    public static object ExceptionBreakClear(AgentRegistry reg, string? agent = null)
        => reg.Get(agent).Result("exception.break_clear")!;

    [McpServerTool(Name = "debug_exception_ignore_add")]
    [Description("[DEBUG] Add a thrown-exception type to the active filter's ignore list so it no longer pauses the target (filtered server-side in the agent — no round-trip per throw). Under mode=all, call this for each noisy type as debug_wait_paused surfaces it, until the background goes quiet; then reproduce the real issue. Substring match, case-insensitive. Returns the updated ignore list. Params: typeName.")]
    public static object ExceptionIgnoreAdd(AgentRegistry reg, string typeName, string? agent = null)
        => reg.Get(agent).Result("exception.ignore_add", new { typeName })!;

    [McpServerTool(Name = "debug_exception_ignore_clear")]
    [Description("[DEBUG] Clear the active exception filter's ignore list (everything in scope pauses again).")]
    public static object ExceptionIgnoreClear(AgentRegistry reg, string? agent = null)
        => reg.Get(agent).Result("exception.ignore_clear")!;

    // ---- stepping -------------------------------------------------------

    [McpServerTool(Name = "debug_step_in")]
    [Description("[DEBUG] Step into the next IL instruction on the current thread. Blocks until step completes or timeoutMs (default 5000).")]
    public static object StepIn(AgentRegistry reg, int timeoutMs = 5000, string? agent = null)
        => reg.Get(agent).Result("step.in", new { timeoutMs })!;

    [McpServerTool(Name = "debug_step_over")]
    [Description("[DEBUG] Step over the next IL instruction on the current thread.")]
    public static object StepOver(AgentRegistry reg, int timeoutMs = 5000, string? agent = null)
        => reg.Get(agent).Result("step.over", new { timeoutMs })!;

    [McpServerTool(Name = "debug_step_out")]
    [Description("[DEBUG] Step out of the current function.")]
    public static object StepOut(AgentRegistry reg, int timeoutMs = 5000, string? agent = null)
        => reg.Get(agent).Result("step.out", new { timeoutMs })!;

    // ---- heap / memory --------------------------------------------------

    [McpServerTool(Name = "debug_heap_find_instances")]
    [Description("[DEBUG] Find managed object addresses by type name (substring ok, paginated). Params: typeName, offset=0, max=200. Agent walks heap up to (offset+max); MCP slices to envelope. Bump offset to continue past truncation.")]
    public static object HeapFind(AgentRegistry reg, string typeName, int offset = 0, int max = 200, string? agent = null)
    {
        var fetch = System.Math.Max(1, offset + System.Math.Min(max, Paging.HardMaxRows));
        return Paging.PageJsonArray(reg.Get(agent).Result("heap.find_instances", new { typeName, max = fetch }), offset, max);
    }

    [McpServerTool(Name = "debug_heap_read_object")]
    [Description("[DEBUG] Dump fields of a managed object. Params: address (ulong as string — decimal '12345' or hex '0x7ff800001234'; 0o/0b also accepted), maxFields=64.")]
    public static object HeapReadObject(AgentRegistry reg, string address, int maxFields = 64, string? agent = null)
        => reg.Get(agent).Result("heap.read_object", new { address = Numbers.ParseUInt64(address, "address"), maxFields })!;

    [McpServerTool(Name = "debug_heap_read_string")]
    [Description("[DEBUG] Read a System.String at a managed address. Params: address (ulong as string — decimal or hex e.g. '0x7ff800001234').")]
    public static object HeapReadString(AgentRegistry reg, string address, string? agent = null)
        => reg.Get(agent).Result("heap.read_string", new { address = Numbers.ParseUInt64(address, "address") })!;

    [McpServerTool(Name = "debug_heap_read_array")]
    [Description("[DEBUG] Read elements of a managed single-dimension array (e.g. a List<T> backing _items, or a T[] field). Primitives decoded; reference elements as {address,type}; string elements as text; null elements as null. Paged. Params: address (ulong as string — decimal or hex), offset=0, count=128.")]
    public static object HeapReadArray(AgentRegistry reg, string address, int offset = 0, int count = 128, string? agent = null)
        => reg.Get(agent).Result("heap.read_array", new { address = Numbers.ParseUInt64(address, "address"), offset, count })!;

    [McpServerTool(Name = "debug_heap_read_collection")]
    [Description("[DEBUG] Read a generic List<T> or Dictionary<K,V> at a managed address as decoded elements / {key,value} pairs — instead of manually walking _items / entries / buckets. Primitives/enums/strings/Guid/DateTime/structs decoded inline; references as {kind:object,type,address}; Dictionary skips removed slots. Paged. Params: address (ulong as string — decimal or hex), offset=0, count=128. Other collection types: use debug_heap_read_object + debug_heap_read_array.")]
    public static object HeapReadCollection(AgentRegistry reg, string address, int offset = 0, int count = 128, string? agent = null)
        => reg.Get(agent).Result("heap.read_collection", new { address = Numbers.ParseUInt64(address, "address"), offset, count })!;

    [McpServerTool(Name = "debug_heap_stats")]
    [Description("[DEBUG] Top-N types on the managed heap by total size (paginated). Params: top=25 (agent-side), offset=0, max=200 (MCP envelope cap).")]
    public static object HeapStats(AgentRegistry reg, int top = 25, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("heap.stats", new { top }), offset, max);

    [McpServerTool(Name = "debug_heap_references")]
    [Description("[DEBUG] List the objects an object points TO (outbound references) with the field/array-element each came from — walk an object graph forward. Params: address (ulong as string — decimal or hex), max=128, agent (optional). Returns references:[{field, isArrayElement, offset, target:{type,address}}].")]
    public static object HeapReferences(AgentRegistry reg, string address, int max = 128, string? agent = null)
        => reg.Get(agent).Result("heap.references", new { address = Numbers.ParseUInt64(address, "address"), max })!;

    [McpServerTool(Name = "debug_heap_referencing")]
    [Description("[DEBUG] Find which objects REFERENCE a given object (inbound references) — the 'who is keeping this alive / why isn't it collected' tool, with the field/array-element that points at it. Walks the WHOLE managed heap (can be slow on a large process like w3wp; capped by max). Params: address (ulong as string — decimal or hex), max=50, agent (optional). Returns {target, scanned, returned, truncated, referrers:[{address,type,field,isArrayElement}]}. Pair with debug_heap_references (outbound) to trace retention toward a GC root.")]
    public static object HeapReferencing(AgentRegistry reg, string address, int max = 50, string? agent = null)
        => reg.Get(agent).Result("heap.referencing", new { address = Numbers.ParseUInt64(address, "address"), max })!;

    [McpServerTool(Name = "debug_heap_roots")]
    [Description("[DEBUG] Enumerate GC roots — the anchors keeping objects alive: GC handles (Strong/Pinned/Weak/Dependent/AsyncPinned/RefCounted/SizedRef), stack locals of live threads, and the finalizer queue. Leak-analysis entry point: rising StrongHandle count = handle leak; many PinnedHandle = heap fragmentation. Params: kind (optional substring on root kind, e.g. 'handle','pinned','stack','strong','finalizer'), typeFilter (optional substring on the rooted object's type), max=200, agent (optional). Returns {summary:{kind->count} over ALL roots, scanned, returned, truncated, roots:[{kind,isPinned,isInterior,rootAddress,object:{type,address}}]}. The summary is always complete; roots[] is the filtered+capped sample. Use debug_heap_retention_path for the full chain from a root to one object.")]
    public static object HeapRoots(AgentRegistry reg, string? kind = null, string? typeFilter = null, int max = 200, string? agent = null)
        => reg.Get(agent).Result("heap.roots", new { kind, typeFilter, max })!;

    [McpServerTool(Name = "debug_heap_retention_path")]
    [Description("[DEBUG] Answer 'why is this object still alive?' — find a reference chain from a GC root down to the target object (managed equivalent of SOS !gcroot / a dotMemory retention path). Heavyweight: builds a reverse-reachability index over the whole heap, so it can be slow on a large process like w3wp. Params: address (ulong as string — decimal or hex), agent (optional). Returns {target, rooted, rootKind, depth, path:[{address,type,field}]} ordered ROOT -> ... -> target (each hop's `field` is the member on the previous object pointing here). rooted=false => no root path (object is collectible). Typical use: debug_heap_find_instances/stats spots a type whose count keeps growing, then this shows what holds an instance (e.g. a static cache or un-removed event handler).")]
    public static object HeapRetentionPath(AgentRegistry reg, string address, string? agent = null)
        => reg.Get(agent).Result("heap.retention_path", new { address = Numbers.ParseUInt64(address, "address") })!;

    [McpServerTool(Name = "debug_heap_leak_report")]
    [Description("[DEBUG] One-call leak triage: a top-N type histogram (count + total size) over the whole heap PLUS an automatic retention path for the top suspicious types — what's biggest AND why it's alive, in one shot. Params: top=20, bySize=false (default orders by instance COUNT), typeFilter (optional substring to focus), retentionFor=3 (auto-retention for this many top non-noise types; 0 disables), agent (optional). Returns {typeCount, totalObjects, totalSize, top:[{type,count,totalSize,sampleAddress,retention?}]}. Auto-retention skips ubiquitous noise (System.String, System.*[]). For growth over TIME use debug_heap_snapshot + debug_heap_snapshot_diff.")]
    public static object HeapLeakReport(AgentRegistry reg, int top = 20, bool bySize = false, string? typeFilter = null, int retentionFor = 3, string? agent = null)
        => reg.Get(agent).Result("heap.leak_report", new { top, bySize, typeFilter, retentionFor })!;

    [McpServerTool(Name = "debug_heap_snapshot")]
    [Description("[DEBUG] Capture a heap type-histogram snapshot (per-type count + total size) and store it on the agent for later diffing. Params: label (optional), top=25, agent (optional). Returns {id, label, takenUtc, totalObjects, totalSize, typeCount, top:[...]}. WORKFLOW: snapshot -> exercise the app (repeat the suspect operation N times) -> snapshot -> debug_heap_snapshot_diff the two ids; types whose count grew are leak suspects. Snapshots are lightweight; the agent keeps the last 20 and clears them on detach.")]
    public static object HeapSnapshot(AgentRegistry reg, string? label = null, int top = 25, string? agent = null)
        => reg.Get(agent).Result("heap.snapshot", new { label, top })!;

    [McpServerTool(Name = "debug_heap_snapshot_list")]
    [Description("[DEBUG] List heap snapshots stored on the agent: [{id, label, takenUtc, totalObjects, totalSize, typeCount}] oldest-first.")]
    public static object HeapSnapshotList(AgentRegistry reg, string? agent = null)
        => reg.Get(agent).Result("heap.snapshot_list")!;

    [McpServerTool(Name = "debug_heap_snapshot_diff")]
    [Description("[DEBUG] Diff two heap snapshots to find what GREW between them — the core managed-leak workflow. Params: before (snapshot id), after (optional; default newest snapshot), top=25, onlyGrowth=true, retentionFor=3, agent (optional). Returns {before, after, types:[{type, countBefore, countAfter, deltaCount, deltaSize, retention?}]} sorted by deltaCount desc. retentionFor>0 auto-runs a retention path on a CURRENT live instance of the top-K grown non-noise types (one batched whole-heap index). Take snapshots around a repeated operation; types with positive deltaCount that keep climbing are the leak.")]
    public static object HeapSnapshotDiff(AgentRegistry reg, int before, int? after = null, int top = 25, bool onlyGrowth = true, int retentionFor = 3, string? agent = null)
    {
        // Omit 'after' when unset so the agent defaults it to the newest snapshot.
        object payload = after.HasValue
            ? new { before, after = after.Value, top, onlyGrowth, retentionFor }
            : new { before, top, onlyGrowth, retentionFor };
        return reg.Get(agent).Result("heap.snapshot_diff", payload)!;
    }

    [McpServerTool(Name = "debug_heap_static_field")]
    [Description("[DEBUG] Read a STATIC field of a type — the entry point into singletons / caches / feature toggles (e.g. read AppManager's instance static, then drill into the live object graph with debug_heap_read_object). Statics are per-AppDomain; by default reads the first AppDomain where the field is initialized. Value is decoded like debug_heap_read_object (primitive/enum/string/Guid/DateTime/struct inline; a reference returns {kind:object,type,address}). Params: typeName (FULL type name, e.g. 'Terrasoft.Core.AppConnection'), fieldName, appDomainIndex=-1 (or a specific index), agent (optional).")]
    public static object HeapStaticField(AgentRegistry reg, string typeName, string fieldName, int appDomainIndex = -1, string? agent = null)
        => reg.Get(agent).Result("heap.static_field", new { typeName, fieldName, appDomainIndex })!;

    [McpServerTool(Name = "debug_memory_read")]
    [Description("[DEBUG] Read raw bytes (returned as hex) at a virtual address. Params: address (ulong as string — decimal or hex e.g. '0x7ff800001234'), size:int (1..1MB).")]
    public static object MemoryRead(AgentRegistry reg, string address, int size, string? agent = null)
        => reg.Get(agent).Result("memory.read", new { address = Numbers.ParseUInt64(address, "address"), size })!;

    [McpServerTool(Name = "debug_memory_write")]
    [Description("[DEBUG] Write raw bytes (hex string) at a virtual address — live edit against the running process, not a file patch. Use reverse_patch_bytes for on-disk edits. Params: address (ulong as string — decimal or hex), hex (bytes payload as hex).")]
    public static object MemoryWrite(AgentRegistry reg, string address, string hex, string? agent = null)
        => reg.Get(agent).Result("memory.write", new { address = Numbers.ParseUInt64(address, "address"), hex })!;

    [McpServerTool(Name = "debug_memory_read_int")]
    [Description("[DEBUG] Read a typed integer from memory. kind in {i8,u8,i16,u16,i32,u32,i64,u64}, default 'i32'. Params: address (ulong as string — decimal or hex), kind='i32'.")]
    public static object MemoryReadInt(AgentRegistry reg, string address, string kind = "i32", string? agent = null)
        => reg.Get(agent).Result("memory.read_int", new { address = Numbers.ParseUInt64(address, "address"), kind })!;

    [McpServerTool(Name = "debug_disasm")]
    [Description("[DEBUG] Disassemble x64 bytes at a virtual address via Iced. Params: address (ulong as string — decimal or hex), size=128.")]
    public static object Disasm(AgentRegistry reg, string address, int size = 128, string? agent = null)
        => reg.Get(agent).Result("memory.disasm", new { address = Numbers.ParseUInt64(address, "address"), size })!;
}
