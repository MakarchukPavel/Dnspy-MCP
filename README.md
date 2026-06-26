# dnspymcp

A Model-Context-Protocol server that exposes **dnSpy**'s static analysis and
**ICorDebug** live-debug capabilities to LLM clients. Designed to make
reverse-engineering a .NET binary or attached process feel as productive
from a chat client as it does in the dnSpy GUI.

Two binaries:

| Binary              | TFM      | Role                                                                  |
|---------------------|----------|-----------------------------------------------------------------------|
| **dnspymcp**        | net9.0   | MCP server. Stdio / Streamable-HTTP / SSE transports.                 |
| **dnspymcpagent**   | net4.8   | Lightweight debug backend that runs on the target host and speaks ICorDebug via dnSpy's `dndbg` engine. Persistent TCP + newline-delimited JSON. |

`dnspymcp` talks to `dnspymcpagent` over **one** persistent TCP connection
(no HTTP, no per-call headers, no reconnect storms). The debug session is
opened once and stays warm until the MCP server exits.

---

## Why two binaries?

ICorDebug lives under .NET Framework. For cross-version debugging the only
safe host is an **out-of-process net48** debugger that loads the right
`mscordbi.dll` through `ICLRMetaHost`. That's the agent.

The MCP-facing half lives on net9 so it can use the official
`ModelContextProtocol` + AspNetCore packages and run side-by-side with other
modern-.NET tooling.

---

## No-handroll philosophy

dnspymcp **does not duplicate dnSpy's source** — when dnSpy already does
the heavy work, we drive its types directly. The agent uses `dndbg`'s
`DnDebugger` for ICorDebug; the static surface uses
`dnSpy.Analyzer.x.dll`'s `ScopedWhereUsedAnalyzer<T>` and every concrete
analyzer node (`MethodUsedByNode`, `TypeUsedByNode`, `FieldAccessNode`,
`SubtypesNode`, `MethodOverridesNode`, ...) end-to-end. `Krafs.Publicizer`
opens the `internal` types at build time so we can `new` them up from C#
without forking dnSpy or copying any `.cs` file.

The thin layer in this repo is glue:

* `Services/AnalyzerDriver.cs` — adapter that satisfies the WPF-coupled
  bits of `IAnalyzerTreeNodeDataContext` (Decompiler via `DispatchProxy`,
  the rest null-stubbed) so we can pull `FetchChildrenInternal` off any
  `SearchNode` subclass.
* `Services/WorkspaceDocumentService.cs` — minimal `IDsDocumentService`
  that wraps the workspace's opened-DLL list.
* `Services/CrossDllIndex.cs` — the only legitimately hand-rolled engine,
  because dnSpy ships no library-accessible string-literal index
  (`FilterSearcher` lives in the WPF main project).

The result: every cross-DLL `reverse_xref_*` query reuses the production
analyzer with full TypeRef pre-filtering, accessibility scoping, friend-
assembly handling, type-equivalence and virtual-dispatch awareness.

---

## Tool naming convention

Every MCP tool is tagged either `[REVERSE]` or `[DEBUG]` and prefixed
accordingly:

* **`[REVERSE]`** — operates on a `.dll` / `.exe` on disk via dnlib +
  ICSharpCode.Decompiler + dnSpy.Analyzer. Doesn't need the agent.
  Prefixed `reverse_*`.
* **`[DEBUG]`** — operates on a live .NET process through the agent
  (ICorDebug + ClrMD). Prefixed `debug_*`.

The first line of every tool description states the target context so the
LLM never confuses "I'm inspecting a file on disk" with "I'm poking at a
running process".

### `reverse_*` (static, file-on-disk)

```
# session
reverse_open / reverse_close / reverse_list / reverse_current / reverse_switch
reverse_list_references                  # opened-vs-missing AssemblyRef map

# member enumeration
reverse_list_types / reverse_list_methods / reverse_list_overloads
reverse_list_fields / reverse_list_properties / reverse_list_events
reverse_list_nested_types / reverse_type_info

# decompile / IL
reverse_decompile_type   / reverse_decompile_method
reverse_decompile_property / reverse_decompile_event / reverse_decompile_field
reverse_il_method        / reverse_il_method_by_token

# source <-> IL mapping (sequence points — makes the live debugger source-aware)
reverse_method_line_map        # IL offsets <-> decompiled C# lines (by type+method OR raw token)
reverse_source_at_il           # paused frame {token, ilOffset} -> the C# statement (where am I?)
reverse_il_at_source_line      # decompiled C# line -> IL offset to feed debug_bp_set_il

# search
reverse_find_string                      # cross-DLL ldstr index, regex optional

# cross-DLL xref (dnSpy.Analyzer-driven)
reverse_xref_to_method
reverse_xref_to_type / reverse_xref_type_instantiations
reverse_xref_to_field
reverse_xref_to_property / reverse_xref_to_event / reverse_event_fired_by
reverse_method_calls                     # outgoing references
reverse_find_attribute_usage

# inheritance / overrides (dnSpy.Analyzer-driven)
reverse_subtypes
reverse_method_overrides    / reverse_method_overridden_by_base
reverse_property_overrides  / reverse_property_overridden_by_base
reverse_event_overrides     / reverse_event_overridden_by_base
reverse_interface_method_implemented_by
reverse_interface_property_implemented_by
reverse_interface_event_implemented_by
reverse_type_exposed_by / reverse_type_extension_methods

# patching
reverse_patch_il_nop / reverse_patch_bytes / reverse_save_assembly

# annotations (sidecar JSON next to the assembly)
reverse_rename_member / reverse_set_comment
reverse_list_annotations / reverse_clear_annotation
```

### `debug_*` (live process via agent)

```
# agent session
debug_session_connect / debug_session_disconnect / debug_session_list
debug_session_info / debug_session_switch / debug_list_methods
debug_list_dotnet_processes

# attach / detach (runtime — no agent restart needed)
debug_pid_attach / debug_pid_detach
debug_launch                                     # launch an EXE under the debugger (func-eval on Release)
debug_load_dump                                  # passive postmortem analysis of a .dmp
# debug_pid_attach also accepts initialBreakpointsJson to register BPs
# atomically inside the attach handshake (closes the attach<->first-RPC race).

# control flow
debug_go / debug_pause / debug_wait_paused
debug_step_in / debug_step_over / debug_step_out

# threads / frames / modules
debug_thread_list / debug_thread_stack / debug_thread_current
debug_frame_locals / debug_frame_arguments
debug_eval                                       # passive object-graph path read (no func-eval)
debug_eval_call                                  # func-eval: invoke a 0-arg getter/method (runs code)
debug_list_modules / debug_find_type / debug_list_type_methods

# breakpoints (optional conditions: `count <op> N` or value gate `arg/local[.field] <op> literal`)
debug_bp_set_il / debug_bp_set_by_name / debug_bp_set_native
debug_bp_list / debug_bp_delete / debug_bp_enable / debug_bp_disable
debug_bp_log                                     # fetch values captured by a tracepoint/logpoint

# exception interception (break on a thrown managed exception)
debug_exception_break_set    # mode: all | unhandled | by_type  (+ typeName, firstChance, excludeTypes)
debug_exception_break_clear
debug_exception_ignore_add   # add a noisy type to the ignore list (filtered server-side)
debug_exception_ignore_clear
# debug_wait_paused returns an exceptionHit block {type,message,hResult,unhandled,thread}.
# Unknown bug amid noise: arm mode=all, ignore_add each noisy type until wait_paused goes
# quiet, then reproduce -> the real exception (not ignored) is the one that pauses.

# heap (ClrMD)
debug_heap_find_instances / debug_heap_read_object
debug_heap_read_array                            # elements of a List<T> backing / T[]
debug_heap_read_collection                       # List<T> -> elements, Dictionary<K,V> -> {key,value}
debug_heap_read_string / debug_heap_stats
debug_heap_static_field                          # read a type's static field (entry into singletons)
debug_heap_references / debug_heap_referencing   # outbound refs / who-references-this (1 hop)
debug_heap_roots                                 # GC roots: handles / stack / finalizer (the anchors)
debug_heap_retention_path                        # full chain GC root -> ... -> object ("why is this alive")

# memory
debug_memory_read / debug_memory_write / debug_memory_read_int / debug_disasm
```

Every list-returning tool emits a uniform pagination envelope —
`{ total, offset, returned, truncated, nextOffset, items }` — so a chat
client never blows context on a `__list__` of every method in mscorlib.

---

## Running

### Target side (the host whose .NET process you want to debug)

```
dnspymcpagent.exe --host 0.0.0.0 --port 5555
# optional: --token SECRET   (client must `auth` first)
```

The agent boots in 'no target' mode and binds to a PID via the MCP tool
`debug_pid_attach` (RPC `session.attach`). Live attach is fully runtime-
controllable: one agent process can be repointed at any local PID across
its lifetime, no restart required. Target-process death auto-detaches
and the agent itself keeps listening, ready for the next attach. For
offline crash-dump analysis use IDA / WinDbg MCPs — dnspymcp is
live-attach only.

**Runtimes & bitness.** The agent attaches to both classic **.NET Framework**
(4.x desktop CLR) and **.NET Core / .NET 5–9+** (CoreCLR) targets — it detects
the runtime from the target's loaded `coreclr.dll` and bundles the matching
`dbgshim`. ICorDebug requires the debugger bitness to match the debuggee's, so
run the **x64** agent for 64-bit targets and the **x86** agent for 32-bit ones.
`builder.ps1` produces both: `dist/dnspymcpagent` (x64) and
`dist/dnspymcpagent-x86` (x86) — see [`scripts/`](#helper-scripts-scripts) for
ready launchers (x64 on :5555, x86 on :5556).

Protocol: one NDJSON request per line, one NDJSON response per line.

```
>>> {"id":1,"method":"session.info"}
<<< {"id":1,"ok":true,"result":{...}}
```

Special method `__list__` enumerates every registered debug command.
Exactly one client at a time — a second connection is rejected.

### MCP-server side (the host running your LLM client)

```
dnspymcp.exe                                   # stdio (default — for Claude Desktop etc.)
dnspymcp.exe --transport http --bind-port 5556 # Streamable HTTP
dnspymcp.exe --transport sse  --bind-port 5556 # legacy SSE
```

The agent target is not a CLI concern — host and port are **required**
parameters of the `debug_session_connect` tool, so the LLM must declare
where it's connecting every time. You can call it multiple times with
different `name`s to register several target agents.

---

## Helper scripts (`scripts/`)

Convenience launchers for this fork. They are **portable** — each resolves
paths relative to its own location (`%~dp0`), so they work wherever the repo
is cloned, no editing required.

| Script | What it does |
|--------|--------------|
| `build-full.bat` | `builder.ps1` — full build (dnSpy subset + both agents + host). |
| `build-fast.bat` | `builder.ps1 -SkipDnSpy` — fast rebuild, reuses `lib/`. |
| `register-claude-code.bat` | Idempotently registers the built host with Claude Code (`claude mcp add`). Safe to re-run. |
| `start-agent.bat` | Starts the **x64** agent on `127.0.0.1:5555`. |
| `start-agent-x86.bat` | Starts the **x86** agent on `127.0.0.1:5556` (for 32-bit targets). |

Typical flow: `build-fast.bat` → `register-claude-code.bat` (once) → restart
Claude Code → `start-agent.bat` (or `-x86` for a 32-bit target) → connect with
`debug_session_connect(port=5555|5556)`. The debugger bitness must match the
target's. Stop the agent and close Claude Code before rebuilding — the running
`dist\*.exe` are otherwise locked.

---

## Conditional breakpoints (D2)

`debug_bp_set_by_name` and `debug_bp_set_il` accept an optional
`condition` string. Operators are `==`, `!=`, `>=`, `<=`, `>`, `<`.

**Hit-count gate** — `count <op> N`:

```
condition: "count >= 5"   # pause on the 5th hit and every hit after
condition: "count == 1"   # one-shot (only the first hit fires)
condition: "count != 0"   # always fires (count is post-increment)
condition: "count > 100"  # busy loop survey
```

**Value gate** — compare an argument or local (optionally a field path)
against a literal, evaluated against the frame at each hit:

```
condition: "arg0 == 12"              # primitive arg (arg0 is `this` on instance methods)
condition: "local2 >= 100"           # primitive local
condition: "arg0.Value == 14"        # field path (property names work — backing fields resolved)
condition: "arg1.Name != null"       # null / non-null reference check
condition: "arg0.Name == 'Contact'"  # string field equality
condition: "arg2.Kind == 'Gadget'"   # enum compared by member name (or by number)
condition: "arg0.UId == '0c81...'"   # Guid/DateTime fields compare by text form
```

Literals are an integer (decimal or `0x`-hex), `true`/`false`, `null`, or a
single/double-quoted string. Field paths may be multi-level (`arg0.Owner.Name`)
and are walked with ClrMD off the dereferenced object — a passive read, safe
inside the condition callback (ICorDebug forbids func-eval there, so calling
properties/methods is *not* supported; only field/auto-property reads). If a
value condition can't be evaluated (slot optimized away, field missing) the
breakpoint **fails open** and pauses, so a mistyped path surfaces rather than
silently swallowing every hit.

The agent records every callback invocation (`hitCount` in `bp.list`)
regardless of whether the predicate let the pause through, so the
counter always reflects the true number of physical hits.

---

## Tracepoints / logpoints (D5)

A *tracepoint* is a breakpoint that **snapshots values and resumes** instead of
pausing — non-interactive instrumentation. Pass `logExpressions` to
`debug_bp_set_by_name` / `debug_bp_set_il`:

```
debug_bp_set_by_name(
    modulePath="Terrasoft.Core", typeFullName="...", methodName="...",
    logExpressions=["arg0.Id", "arg1.Name", "local2"],   # passive reads, same language as conditions
    logOnly=true,        # capture then auto-continue (no pause). default true when logExpressions set
    maxHits=200)         # hard cap: once reached, capturing stops
# ... let it run ...
debug_bp_log(id=<bp id>)         # -> {hitCount, captured, capped, samples:[{seq, hit, values:{expr->{kind,value}}}]}
debug_bp_log(id=<bp id>, clear=true)   # drain the buffer (bp stays armed)
debug_bp_delete(id=<bp id>)            # stop entirely
```

A `condition` combines with logging — capture only matching hits, e.g.
`condition="arg0.Kind == 'Gadget'"` + `logExpressions=[...]`. Values are decoded
the same way conditions read them: `{kind:int|float|string|bool|enum|null|object}`
(enums carry their member `name`; Guid/DateTime/decimal as text). `kind:unavailable`
means the slot was optimized away / out of scope.

**How it works, and the cost — read before using on production.** This is built
on ICorDebug, which has **no zero-stop breakpoint** for managed code. Every hit
**physically stops the whole process** (all managed threads), the agent reads the
values out-of-process, then the engine auto-continues. `logOnly` means *no
human-visible pause* — **not** *no stop*. So:

- **Cold paths only.** On a method hit a few times the per-hit stop is
  imperceptible. On a **hot path** (thousands of hits/sec) the repeated
  whole-process stop + cross-process round-trip seriously degrades or
  near-freezes the target. For hot-path telemetry use ETW / EventPipe /
  `dotnet-trace`, not a debugger.
- A `condition` reduces *pauses/captures*, **not** the per-hit stop cost — the
  breakpoint still fires and stops the world on every hit (a value gate makes
  each hit *more* expensive). To bound cost, keep `maxHits` low (capture then
  capture stops) and `debug_bp_delete` the tracepoint when done.
- **No func-eval.** Only passive reads are possible inside the hit callback
  (args/locals/fields/struct decode) — calling a property getter or method there
  can deadlock/corrupt a live w3wp, so it is not supported. Use a paused
  `debug_eval_call` for that.

Pair with `reverse_il_at_source_line` to place an IL-offset tracepoint at a
specific decompiled source line.

---

## Frame inspection (D3)

While paused, `debug_frame_locals` and `debug_frame_arguments` decode
the current frame's locals and arguments. Primitives are read via
`ReadGenericValue` + `BitConverter`; strings via `ICorDebugStringValue`;
references surface as `{address, typeName}` so the caller can drill
down with `debug_heap_*` from there. Value-type (struct) slots are
decoded in place (see **Struct decoding** below) instead of surfacing as
a raw address. Pass `frameIndex` to inspect a deeper frame on the
pause-thread.

Slot probing no longer stops at the first empty slot: a value the JIT
optimized away (or not yet in scope) is reported as `{kind:"unavailable"}`
instead of truncating the list, and trailing unavailable slots are trimmed
(`readableCount` says how many were actually read). So an optimized-away
`this`, or an inlined extension-method receiver, no longer hides the
arguments and locals that come after it.

`debug_heap_read_array` walks a `List<T>` backing array or a `T[]` field
element-by-element (paged via `offset` / `count`): primitives decoded,
reference elements as `{address, type}`, strings as text, nulls as null —
the missing piece for stepping through a collection's contents.

### Leak analysis (GC roots & retention)

Three tools answer "what's keeping memory alive", in increasing depth:

- `debug_heap_referencing` — one hop up: who points at this object.
- `debug_heap_roots` — the **anchors** themselves: GC handles
  (Strong / Pinned / Weak / Dependent / AsyncPinned / RefCounted / SizedRef),
  stack locals of live threads, and the finalizer queue. The result carries a
  complete `{kind → count}` summary, so a rising `StrongHandle` count (handle
  leak) or a pile of `PinnedHandle` (heap fragmentation) jumps out.
- `debug_heap_retention_path` — the full chain **GC root → … → object** (the
  managed `!gcroot`), each hop labelled with the field that points to the next.
  This is the "why is this still alive?" answer.

Typical leak hunt: `debug_heap_stats` / `debug_heap_find_instances` across a few
snapshots → spot a type whose instance count keeps climbing →
`debug_heap_retention_path` on one instance → see exactly what holds it (a
static cache, an un-removed event handler, a captured closure in a long-lived
timer). A static-held object shows up rooted via the runtime's `PinnedHandle` on
the per-domain static-storage `object[]`, with the chain running through it.
All three are read-only ClrMD walks (no func-eval, no code execution) and work
against a dump too. `debug_heap_retention_path` is heavyweight — it builds a
reverse-reachability index over the whole heap, so it can be slow on a large
process.

### Source-aware debugging (source ↔ IL)

ICorDebug works in *IL offsets*; humans work in *source lines*. The
`reverse_method_line_map` family bridges the two using the decompiler's
sequence points, so a paused IL offset can be shown as a C# statement and a
breakpoint can be placed by source line. These are `reverse_*` (on-disk) tools
— they need an opened assembly (`reverse_open`) but **not** a live agent, so
the mapping works against a dump, a Release build, or before attaching.

The lines are 1-based into the **decompiled** text (the same text
`reverse_decompile_method` / `reverse_method_line_map` render), not the
original `.cs`. A method with lambdas / local functions / an async state
machine decompiles into several IL functions; every statement carries its
`functionToken`, and a frame paused inside a lambda or `MoveNext` reports that
nested token — so resolution by `token` lands on the right body.

**Where am I paused?** — frame → source statement:

```
top = debug_thread_stack(...).frames[0]          # -> {token, ilOffset, ...}
reverse_source_at_il(token=top.token, ilOffset=top.ilOffset)
   -> match:{line, text, ilEnd, exact}, window:[±2 neighbouring statements]
```

`exact=false` means the IP fell between sequence points and was mapped to the
nearest preceding one.

**Break at line N** — source line → IL offset → breakpoint:

```
hit = reverse_il_at_source_line(typeFullName=..., methodName=..., line=37)
debug_bp_set_il(... ilOffset=hit.match.ilOffset)   # exact IL the JIT will stop on
```

`reverse_method_line_map` returns the full table when you want to see every
statement at once; both directional tools accept either `token` (what a frame
reports) or `typeFullName`+`methodName` (with the usual `signature` /
`overloadIndex` overload selector).

### Expression evaluation (`debug_eval`)

`debug_eval` reads a value expression against the paused frame **without
running any target code**:

```
debug_eval(expr="arg0")              # the receiver/first arg as {kind:object,...}
debug_eval(expr="this.Entity")       # 'this' == arg0 on instance methods
debug_eval(expr="arg1.Owner.Name")   # multi-level field / auto-property path
debug_eval(expr="local0.UId")        # Guid leaf -> decoded text
```

The root is `arg<i>`, `local<i>`, or `this`; the rest is a dotted path. Each
hop is a field or auto-property (property names resolve their
`<Name>k__BackingField`), read from the dereferenced object via ClrMD — a
passive memory read. Leaves are decoded the same way the heap reader decodes
fields (primitives, strings, Guid/DateTime/enum/struct); an object leaf comes
back as `{kind:object,type,address}` so you can drill in with
`debug_heap_read_object`.

`debug_eval` is **passive** — it never runs target code. For computed
properties and methods that must execute, use `debug_eval_call` (below).
Missing fields and null hops come back as structured `{kind:"error"|"null"}`
rather than throwing.

### Func-eval (`debug_eval_call`)

When a value only exists by *running* code — a computed property, `ToString()`,
a helper method — `debug_eval_call` invokes it via `ICorDebugEval`:

```
debug_eval_call(expr="arg0.ToString()")                       # method (parens = call)
debug_eval_call(expr="this.DisplayName")                      # bare member: get_DisplayName, then DisplayName()
debug_eval_call(expr="local0.Plus(10)")                       # literal int argument
debug_eval_call(expr="arg0.Tag('hot')")                       # literal string argument
debug_eval_call(expr="this.GetTypedColumnValue<System.Guid>('UId')")  # generic method + arg
debug_eval_call(expr="arg0.Entity.GetColumnValue('Name')")    # multi-hop receiver (arg0.Entity, then call)
debug_eval_call(expr="arg0.Compare(arg1.Owner)")              # object argument (resolved from the frame)
```

The receiver is a root slot (`arg<i>` / `local<i>` / `this`) with an **optional
field/auto-property path** (`arg0.Entity.Foo`), walked via ICorDebug to reach
the object the call is made on. The member resolves across the type hierarchy
(including inherited / cross-module members like `Object.ToString`). Arguments
are either **literals** (integer dec / `0x`-hex, `true`/`false`, `null`, quoted
string) or **value expressions** `arg/local/this[.field]` resolved from the
frame to live objects — so you can pass one frame value into a method on
another. Overloads are selected by **argument count, type-arg count, and
argument type** — a string literal picks `(string)` over `(SomeClass)`, and an
object expression picks the class overload, so
`GetTypedColumnValue<Guid>("Id")` binds to `(string)` not
`(EntitySchemaColumn)`. Generic methods take explicit type arguments in `<...>`.
The eval runs on the paused thread with **all other threads suspended** and a
timeout (default 2000 ms) + abort. The result is decoded like `debug_eval`; a
thrown exception returns `{kind:"exception", type, message}`; a timeout returns
`{kind:"timeout"}`.

⚠ This **runs code in the target**. If the called method blocks on a lock
another (suspended) thread holds, it stalls until the timeout fires, then
aborts. Prefer passive `debug_eval` whenever a field or auto-property answers
the question. Not supported: type arguments that are themselves generic
(`List<int>`) and value-type (struct) receivers.

When the runtime refuses a call, the error is mapped to an actionable hint
instead of a raw HRESULT — e.g. an optimized module
(`CORDBG_E_ILLEGAL_IN_OPTIMIZED_CODE`) points you at the reload-debuggable
workflow above, and a bad start point (`CORDBG_E_FUNC_EVAL_BAD_START_POINT`)
suggests a non-generic equivalent / different site / passive read. Note: the
runtime occasionally refuses a *specific* method (often a particular generic
instantiation, e.g. `GetTypedColumnValue<Guid>`) with a bad start point even
when other func-evals at the same stop succeed — use the non-generic
equivalent (`GetColumnValue("Id")`) or passive `debug_eval` there.

### Func-eval needs *debuggable* code (Release vs Debug, and `debug_launch`)

Func-eval only works where the JIT produced **debuggable** (un-optimized,
tracking-info) code. The agent's options provider already requests that for
every module — but ICorDebug applies it **at module load**, so it only takes
effect for modules that load *after* the debugger is present:

- **Attaching to an already-running process** (`debug_pid_attach`): modules
  already loaded (e.g. a warmed-up `w3wp`'s `Terrasoft.Core`) stay optimized →
  func-eval there fails with `CORDBG_E_FUNC_EVAL_BAD_START_POINT`. Passive
  tools still work fully; only func-eval is affected.
- **Launching under the debugger** (`debug_launch`): the debugger is present
  before any module loads, so **all** modules are debuggable → func-eval works
  even on Release/optimized assemblies. Use this for any target you can start
  yourself (a console app, a service).
- **IIS / Creatio**: you can't launch `w3wp` yourself. Best option: **deploy
  the build in Debug** (a normal `debug_pid_attach` then gives working
  func-eval — verified). Otherwise, force the target assemblies to **reload
  while you're attached** so they re-JIT debuggable:

  ```
  debug_pid_attach(pid=<w3wp>)
  debug_jit_status(pattern="Terrasoft.Core")     # loadedUnderDebugger:false (pre-existing, optimized)
  debug_touch_config(path="…\\bin\\..\\web.config")  # bump web.config -> ASP.NET app-domain reload
  # (or recycle the app pool)
  debug_jit_status(pattern="Terrasoft.Core")     # loadedUnderDebugger:true  -> func-eval ready
  ```

  `debug_touch_config` bumps a file's timestamp to trigger an app-domain reload;
  the reloaded **app-private** assemblies fire fresh `LoadModule` callbacks and
  JIT debuggable. `debug_jit_status` tells you, per module, whether it loaded
  under the debugger (so you know before trying func-eval). Caveats: assemblies
  that are **domain-neutral** (GAC / framework) are shared across app domains
  and won't re-JIT, so func-eval into those still won't work; and setting
  `COMPlus_JITMinOpts=1` does **not** help — it disables optimization but not
  the debuggability tracking func-eval needs.

---

## Struct decoding

Value types no longer surface as an opaque `<Struct>` placeholder. Across
`debug_heap_read_object` (fields), `debug_heap_read_array` (elements) and the
frame readers (struct locals/arguments), a shared decoder resolves:

```
System.Guid            -> {kind:"Guid",     value:"0c81...-..."}
System.DateTime        -> {kind:"DateTime", value:"2026-06-26T07:27:56.95Z"}  (ISO-8601)
System.TimeSpan        -> {kind:"TimeSpan", value:"..."}
System.DateTimeOffset  -> {kind:"DateTimeOffset", value:"...±..."}
System.Decimal         -> {kind:"decimal",  value:"..."}
<any enum>             -> {kind:"enum", type:"...", value:N, name:"Member"}
<other struct>         -> {kind:"struct", type:"...", fields:[...]}   (one level of fields)
```

Well-known BCL structs are read straight off target memory as the real .NET
type (their layout is a fixed unmanaged blob). Enums map the underlying number
to the member name. Any other struct is expanded one level into its fields, so
e.g. an entity's `UId`/`_modifiedOnUtc` come back as real values rather than
`<Struct>`.

---

## Dump (postmortem) analysis

`debug_load_dump(path=...)` points the agent at a crash/process dump (`.dmp`)
instead of a live process — for postmortem analysis when there's nothing left
to attach to:

```
debug_load_dump(path="C:\\dumps\\w3wp.dmp")
debug_heap_stats() / debug_heap_find_instances(...) / debug_heap_read_object(...)
```

ClrMD reads the dump, so the whole **passive** surface works — heap walk, read
object/array/string, and full struct decoding (Guid/DateTime/enum/...). Because
there's no live process, the **ICorDebug** features do not: breakpoints,
stepping, frames, exception interception, and `debug_eval*` all require a live
target. Loading a dump detaches any current target; `debug_pid_attach` switches
back to live. The path is resolved on the **agent's** machine. Optimization
doesn't matter here — postmortem reads are passive, so dumps of Release
assemblies analyze fine (unlike live func-eval).

Produce a full dump however you like, e.g. the built-in
`rundll32 C:\Windows\System32\comsvcs.dll,MiniDump <pid> <path> full`,
`procdump -ma`, or Task Manager → *Create dump file*.

---

## Exception interception

Pause the target when a managed exception is **thrown** (not only at an
unhandled crash). `debug_exception_break_set` takes a `mode`:

```
mode: "by_type"     # pause only when the thrown type name matches `typeName`
mode: "unhandled"   # pause only on exceptions that escape unhandled
mode: "all"         # pause on every first-chance throw
```

`firstChance` (default true) selects the throw site vs the unhandled point.
`debug_wait_paused` then carries an `exceptionHit` block
(`type / message / hResult / unhandled / thread`); `debug_exception_break_clear`
disarms. This replaces the old trick of breakpointing an exception's `..ctor`.

### Finding an unknown exception amid noise

A busy app (IIS / Creatio) throws **many** first-chance exceptions internally,
so `mode=all` alone is a firehose. The fix is a server-side **ignore list**:
`debug_exception_ignore_add` drops a type so the agent skips it *without an
MCP round-trip per throw* — the process keeps running at full speed.

The real-debugger workflow, when you don't know the bug's exception type:

1. `debug_exception_break_set(mode="all")`.
2. `debug_wait_paused` → read the thrown type. If it's noise,
   `debug_exception_ignore_add("<type>")` then `debug_go`. Check the stack
   (`debug_thread_stack`) when unsure whether a type is noise or your bug.
3. Repeat until `debug_wait_paused` goes quiet — the background is drained.
4. Reproduce the issue → the first **un-ignored** exception is the one that
   pauses → inspect its stack and frames.

`excludeTypes` on `debug_exception_break_set` seeds the ignore list up front;
`debug_exception_ignore_clear` resets it.

---

## Annotation store (Phase 7)

`reverse_rename_member` / `reverse_set_comment` persist user notes to a
sidecar JSON next to the assembly:

```
<asm_path>.dnspymcp.json
  ├─ renames:  { "100663304": "AddTwoInts", ... }
  └─ comments: { "100663304": "trivial — used by Compute", ... }
```

Annotations are MCP-visible only — they don't rewrite decompiler output
or modify the on-disk PE/MD. Treat them as a curator's notebook bound
to a particular DLL: useful for "remember what I learned about token
0x06000123" workflows during long reverse-engineering sessions.
Re-opening the same DLL reloads them. Use `reverse_list_annotations`
to dump the current notebook and `reverse_clear_annotation` to remove
single entries.

---

## Build from source

Requirements: Windows, .NET SDK 9.0, .NET Framework 4.8 reference assemblies
(installed by VS / Build Tools or `dotnet workload install desktop`).

```
git clone --recurse-submodules https://github.com/Jimmy01240397/Dnspy-MCP.git
cd Dnspy-MCP
pwsh -File builder.ps1                 # full build: dnspy subset -> lib/ -> dist/
pwsh -File builder.ps1 -Zip            # also produce a release zip
pwsh -File builder.ps1 -SkipDnSpy      # reuse an already-populated lib/
pwsh -File builder.ps1 -Clean          # wipe lib/ and dist/
```

`builder.ps1` is the single entry point — the GitHub Actions release job
calls the exact same script, so local and CI builds are byte-identical.
It builds two dnSpy subprojects out of the submodule (`dnSpy.Analyzer`
for the where-used engine; `dnSpy.Debugger.DotNet.CorDebug` for `dndbg`),
copies the referenced DLLs into `lib/`, builds dnspymcp + dnspymcpagent
+ dnspymcptest, and stages a distributable layout under `dist/`.
`Krafs.Publicizer` exposes the internal surface without patching dnSpy
source. The dnSpy source is a git submodule under `dnspy/`; `lib/` is a
build artifact, regenerated by `builder.ps1`.

---

## Agent session registry

`dnspymcp` keeps a named registry of agent sessions so one MCP server can
drive several target hosts at once. An "agent session" is a persistent
TCP link to one `dnspymcpagent` process. The registry is idalib-style:
open once, switch for free — TCP auto-reconnects if the agent restarts,
so you never need to disconnect between tool calls.

```
debug_session_connect(host, port, token?, name?)    # open/re-open a session, becomes active
debug_session_disconnect(name)                       # disconnect TCP and drop the slot
debug_session_list()                                 # list sessions, mark the active one
debug_session_info()                                 # active slot + its full debug state
debug_session_switch(name)                           # route DEBUG calls to another slot
```

Every `debug_*` call targets the active slot unless you pass `agent=<name>`.
To debug a different PID on the same agent, just call `debug_pid_attach`
with the new PID — the agent detaches the current target and re-binds.

---

## Testing

The repo ships a pytest suite that covers both the REVERSE and DEBUG
surfaces end-to-end. The DEBUG fixtures spawn three cooperating processes:

1. **`dnspymcptest.exe`** — a tiny idle .NET Framework 4.8 target with
   a few managed types on the heap (Animal/Cat/IPet hierarchy plus a
   ticking Compute() loop). Safe to attach to.
2. **`dnspymcpagent.exe`** — listens on `127.0.0.1:5555`. The fixture
   uses `debug_pid_attach` to bind it to the test target after launch.
3. **`dnspymcp.exe`** — launched with `--transport http --bind-port 5556`,
   talks to the agent over NDJSON.

```
pwsh -File builder.ps1                                 # build dist/ + dnspymcptest
pip install -r tests/requirements.txt
pytest tests -v --tb=short
```

The DEBUG agent must **only** attach to the bundled `dnspymcptest.exe` —
never point the test fixtures at an unrelated process. The CI workflow
in `.github/workflows/ci.yml` runs the same commands on `windows-latest`.

---

## License

This project is licensed under the MIT License — see [LICENSE](LICENSE) for
the full text. `dnspymcp` vendors the `dnSpy` repository as a git submodule
under `dnspy/`; those sources remain under their original **GPLv3** license
(`dnspy/LICENSE.txt`). Only the compiled `dndbg` / `dnSpy.Analyzer` glue
DLLs are linked at runtime; no GPL source is redistributed as part of
`dnspymcp`.

---

## Safety note

The agent can attach to any .NET process it has permission to open, set
breakpoints, read / write arbitrary memory in the target. Only run it
against processes you own or have authorization to debug.
