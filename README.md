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
# debug_pid_attach also accepts initialBreakpointsJson to register BPs
# atomically inside the attach handshake (closes the attach<->first-RPC race).

# control flow
debug_go / debug_pause / debug_wait_paused
debug_step_in / debug_step_over / debug_step_out

# threads / frames / modules
debug_thread_list / debug_thread_stack / debug_thread_current
debug_frame_locals / debug_frame_arguments
debug_list_modules / debug_find_type / debug_list_type_methods

# breakpoints (with optional `count <op> N` conditions)
debug_bp_set_il / debug_bp_set_by_name / debug_bp_set_native
debug_bp_list / debug_bp_delete / debug_bp_enable / debug_bp_disable

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
debug_heap_read_string / debug_heap_stats

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
`condition` string of the form `count <op> N`:

```
condition: "count >= 5"   # pause on the 5th hit and every hit after
condition: "count == 1"   # one-shot (only the first hit fires)
condition: "count != 0"   # always fires (count is post-increment)
condition: "count > 100"  # busy loop survey
```

The agent records every callback invocation (`hitCount` in `bp.list`)
regardless of whether the predicate let the pause through, so the
counter always reflects the true number of physical hits.

---

## Frame inspection (D3)

While paused, `debug_frame_locals` and `debug_frame_arguments` decode
the current frame's locals and arguments. Primitives are read via
`ReadGenericValue` + `BitConverter`; strings via `ICorDebugStringValue`;
references surface as `{address, typeName}` so the caller can drill
down with `debug_heap_*` from there. Pass `frameIndex` to inspect a
deeper frame on the pause-thread.

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
