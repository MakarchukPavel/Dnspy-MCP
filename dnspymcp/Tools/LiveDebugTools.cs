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
    // the target dies. Load-dump stays startup-only (dumps are immutable).

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
    [Description("[DEBUG] Set an IL-offset breakpoint. Params: modulePath (suffix ok), token (uint as string — decimal '123' or hex '0x06000123'; also 0o / 0b accepted), offset (uint, default '0'; same forms), condition (optional; op ==/!=/>=/<=/>/<). condition is either `count <op> N` (e.g. 'count >= 5' skips the first 4 hits) or a value gate `arg<i>[.field...] <op> lit` / `local<i>[.field...] <op> lit` evaluated against the frame. Literals: integer (dec or 0x-hex), true/false, null, or a quoted string. Guid/DateTime fields compare by text, enums by member name; property names resolve their backing field — e.g. \"arg0.UId == '0c81...'\", \"local1 > 100\", \"arg1.Name != null\".")]
    public static object BpSetIl(AgentRegistry reg, string modulePath, string token, string offset = "0", string? condition = null, string? agent = null)
    {
        var tok = Numbers.ParseUInt32(token, "token");
        var off = Numbers.ParseUInt32(offset, "offset");
        return reg.Get(agent).Result("bp.set_il", new { modulePath, token = tok, offset = off, condition })!;
    }

    [McpServerTool(Name = "debug_bp_set_by_name")]
    [Description("[DEBUG] Set a breakpoint at IL=0 of a named method. Params: modulePath, typeFullName, methodName, overloadIndex=0, condition (optional; op ==/!=/>=/<=/>/<). condition is either `count <op> N` (pause on the Nth hit onward, e.g. 'count >= 5') or a value gate `arg<i>[.field...] <op> lit` / `local<i>[.field...] <op> lit` evaluated against the frame (arg0 = 'this' on instance methods). Literals: integer (dec or 0x-hex), true/false, null, or quoted string. Guid/DateTime fields compare by text, enums by member name; property names resolve their backing field — e.g. \"arg0.Id == '0c81...'\", \"arg1 >= 10\", \"arg2.Name == 'Contact'\".")]
    public static object BpSetByName(AgentRegistry reg, string modulePath, string typeFullName, string methodName, int overloadIndex = 0, string? condition = null, string? agent = null)
        => reg.Get(agent).Result("bp.set_by_name", new { modulePath, typeFullName, methodName, overloadIndex, condition })!;

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

    [McpServerTool(Name = "debug_heap_stats")]
    [Description("[DEBUG] Top-N types on the managed heap by total size (paginated). Params: top=25 (agent-side), offset=0, max=200 (MCP envelope cap).")]
    public static object HeapStats(AgentRegistry reg, int top = 25, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("heap.stats", new { top }), offset, max);

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
