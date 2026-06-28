# Request playbooks

This MCP is driven by **natural-language requests** to an AI agent: you describe
the goal, the agent picks the tools. The agent will usually infer the right
approach, but a clear request makes its behavior **reliable and reproducible** —
which matters most when several people share the same server. Below are the
recommended phrasings for the common scenarios. Copy a phrasing, fill in the
`<placeholders>`, and you'll get predictable results.

## Safety model — read this first

- **Attaching is passive.** `debug_pid_attach` never touches config and never
  restarts anything; the target's live state (statics, caches, in-flight
  requests) is preserved.
- **Only three actions are state-destroying**, and each runs **only when you
  explicitly ask**: `debug_touch_config` (bumps `web.config` → ASP.NET
  app-domain reload), recycling the app pool yourself, and `debug_launch`
  (starts a fresh process).
- **Only func-eval needs a reload.** Calling methods/getters on already-loaded
  Release code (`debug_eval_call`) requires debuggable JIT, which is what
  `debug_touch_config` enables. *Everything else* — reading variables, walking
  the heap, breaking on exceptions, conditional breakpoints, tracepoints, leak
  analysis — works on optimized/Release code with **zero** restart.
- **Rule of thumb:** to understand *what broke* from the current live state, ask
  for a **passive attach** and don't authorize a reload until you've captured
  what you need. Restarting first can erase the evidence.

> Tip: if you want it to be *impossible* to touch config on a production agent,
> ask the maintainer about a read-only mode (a server flag that makes
> `debug_touch_config` / `debug_launch` refuse).

---

## Catch an UNKNOWN exception on a live IIS app

**When:** an error surfaces as an exception somewhere, type/location unknown, on
a busy worker that throws many background (first-chance) exceptions internally.

**Ask like:**

> "Attach passively to w3wp PID `<N>`. Arm interception of **all** first-chance
> exceptions and run a **noise-filtering pass** — add each background exception
> type to the ignore-list until it goes quiet. **Then tell me when to
> reproduce.** After I reproduce, catch the real exception and show its
> type / message / HResult / stack plus the variable values at the throw site."

**What the agent does:** passive attach → `debug_exception_break_set(mode=all,
firstChance=true)` → loop: `debug_wait_paused` → `debug_exception_ignore_add(<noisy type>)`
→ `debug_go`, until it stays quiet → tells you to reproduce → on the real hit,
reads frame locals/arguments and evaluates paths at the throw → clears
interception when done.

**Notes:** the warm-up needs the app's normal background traffic so the noise
actually fires and can be filtered. Each first-chance hit briefly stops the
process; the ignore-list filters server-side to keep that minimal.

## Catch a KNOWN exception type

**When:** you already know the exception type (or roughly the method).

**Ask like:**

> "Attach passively to w3wp PID `<N>` and break when a `<Namespace.ExceptionType>`
> is thrown — I'll reproduce. Show type / message / stack and the locals at the
> throw."

**What the agent does:** `debug_exception_break_set(mode=by_type,
typeName=<...>)` — no noise pass needed.

## Inspect live state WITHOUT restarting the pool

**When:** you need to see why something is in a bad state, and a restart would
erase the evidence.

**Ask like:**

> "Attach passively to w3wp PID `<N>` — **do not** touch web.config or recycle
> the pool. Read `<these statics / this object graph / instances of Type X>` and
> tell me what you find."

**Works on Release/optimized code:** `debug_frame_locals` / `debug_frame_arguments`
/ `debug_eval`, and the heap suite (`find_instances` / `read_object` /
`read_array` / `read_collection` / `static_field` / `references` / `referencing`
/ `roots` / `retention_path` / `leak_report` / `snapshot_diff`).

## Run code (func-eval) on a Release build

**When:** you need a value that only exists by *running* code — a computed
property, `ToString()`, a helper method — on an already-running optimized worker.

**Heads-up:** this **requires a debuggable-JIT reload**, which resets the
app-domain state (statics, caches, in-flight requests). Don't do it if you still
need the current state.

**Ask like:**

> "I need func-eval on this Release worker (PID `<N>`) — **you may** touch
> web.config to reload the app domain. Once it's debuggable, call `<expr>`."

**What the agent does:** passive attach → `debug_touch_config(<...>\web.config)`
(or you recycle the pool) → `debug_jit_status` to confirm
`loadedUnderDebugger: true` → `debug_eval_call(<expr>)`.

## Find a memory leak

**When:** memory grows over time.

**Ask like:**

> "Attach passively to w3wp PID `<N>`. Take a heap snapshot; I'll run the suspect
> operation a few times; take another snapshot and diff it — show what grew with
> a retention path back to the GC root."

**Tools:** `debug_heap_snapshot` / `debug_heap_snapshot_diff` /
`debug_heap_leak_report` / `debug_heap_retention_path` / `debug_heap_roots`.

## Set a breakpoint by source line / map a paused frame to source

**Ask like:**

> "Open `<assembly>.dll`, decompile `<Type.Method>`, and set a breakpoint at the
> line that does `<…>`." — or — "I'm paused; show me the C# line for the current
> frame."

**Tools:** `reverse_method_line_map` / `reverse_il_at_source_line` (→ feeds
`debug_bp_set_il`) / `reverse_source_at_il`.

## Capture values on a hot path without pausing (tracepoint)

**When:** you want values at a point that's hit often and can't afford a pause.

**Ask like:**

> "Set a **tracepoint** at `<Type.Method>` capturing `<arg0.Id, local2>`,
> log-only, maxHits 100 — I'll fetch the log after a while."

**Notes:** cold paths only — every hit still briefly stops the whole process
(ICorDebug has no zero-stop breakpoint). For high-frequency runtime telemetry,
prefer ETW / EventPipe instead of a debugger.

---

## Cheat-sheet

| Goal | One-line ask | Restart? |
|------|--------------|----------|
| Unknown exception, busy host | "passive attach, break all first-chance, filter noise to quiet, then I reproduce" | no |
| Known exception type | "passive attach, break on type `X`, I'll reproduce" | no |
| Read live state | "passive attach, do NOT touch config, read `<…>`" | no |
| Run a method/getter on Release | "you may touch web.config; then call `<expr>`" | **yes** (app-domain reload) |
| Memory leak | "snapshot, I repeat the op, snapshot, diff with retention path" | no |
| Break at a source line | "decompile `<Type.Method>`, break at the line doing `<…>`" | no |
| Values on a hot path | "tracepoint at `<Type.Method>` capturing `<…>`, log-only" | no (brief per-hit stop) |
