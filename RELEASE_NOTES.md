# Dnspy-MCP v1.0.0

This release turns the upstream project (a static MCP for decompiling/analysing
.NET assemblies) into a **full LIVE-debugging and memory-leak-analysis toolkit**,
aimed at long-running server apps (.NET Framework 4.8 on IIS / w3wp) but equally
usable on .NET Core / .NET 9 and on crash dumps.

## Platform & compatibility
- **Attach to .NET Core / .NET 9** (CoreCLR 2.1–9+), not just .NET Framework —
  the runtime is detected via `coreclr.dll` and the native `dbgshim.dll` ships
  in the package.
- **x86 agent alongside x64** — debug 32-bit processes (debugger bitness must
  match the target).
- **Dump (`.dmp`) loading** — passive post-mortem heap analysis with the same
  tools as a live process.
- Portable helper launch scripts (relative paths) + an updated README.

## Exception interception
- **Break on a thrown managed exception** — modes `all | unhandled | by_type`,
  first-chance, surfacing type / message / HResult / thread (read via ClrMD, so
  no func-eval inside the ICorDebug callback).
- **Server-side ignore-list** — "break on everything except the noise": filter
  out the framework's chatty internal exceptions to catch the real one.

## Value inspection (no code execution)
- **Struct decoding** — `Guid` / `DateTime` / `enum` / `decimal` / arbitrary
  structs — both on the heap and in stack frames.
- **Value-based conditional breakpoints** — `arg/local[.field] <op> literal` and
  hit-count gates (`count >= N`).
- **`debug_eval`** — passive object-graph path reads (`arg0.Owner.Name`) without
  running any target code.
- **Robust frame slot reads** — an optimized-away `this` / receiver no longer
  hides the arguments and locals after it (slots become `unavailable`
  placeholders instead of truncating the list).

## Func-eval (running code to obtain a value)
- **`debug_eval_call` v1→v5**: invoke 0-arg getters/methods → arguments and
  generic methods → overload selection by argument type → object/reference
  arguments and multi-hop receivers → **actionable HRESULT hints** explaining
  exactly what blocked a call.
- **Func-eval on optimized/Release builds**: `debug_jit_status` (debuggable-JIT
  diagnosis) + `debug_touch_config` (app-domain reload via web.config to enable
  the debuggable JIT) + `debug_launch` (start an EXE under the debugger from the
  entry point).

## Heap & object-graph reads
- **`debug_heap_static_field`** — static fields (the entry point into
  singletons / caches / feature flags).
- **`debug_heap_read_collection`** — `List<T>` → elements, `Dictionary<K,V>` →
  `{key, value}`.
- **`debug_heap_read_array`** — element-by-element (paged) walk of `T[]` /
  a `List<T>` backing array.
- **`debug_heap_references` / `referencing`** — outbound references / who points
  at an object (one hop).

## Source-aware debugging (source ↔ IL bridge)
- **`reverse_method_line_map`** + **`reverse_source_at_il`** +
  **`reverse_il_at_source_line`** — translate a paused frame's IL offset to a C#
  source line and back, via the decompiler's sequence points; lambdas / async
  state machines are attributed correctly through their `functionToken`. This
  connects the live debugger to the decompiled source.

## Non-invasive instrumentation
- **Tracepoints / logpoints** — a breakpoint that snapshots passive frame values
  and **auto-continues** (no human-visible pause); combinable with a condition,
  bounded by `maxHits`, with the buffer fetched via `debug_bp_log`. (Documented
  honestly: every hit still briefly stops the process — safe on cold paths.)

## Memory-leak analysis (full toolkit)
- **`debug_heap_roots`** — enumerate GC roots (handle / stack / finalizer) with a
  per-kind summary (rising `StrongHandle` = handle leak; many `PinnedHandle` =
  fragmentation).
- **`debug_heap_retention_path`** — the full chain "GC root → … → object" (the
  managed `!gcroot`): why an object is still alive.
- **`debug_heap_leak_report`** — top-N types + auto-retention for the suspicious
  ones, in a single call.
- **`debug_heap_snapshot` / `snapshot_diff` / `snapshot_list`** — growth over
  time: snapshot → exercise the operation → snapshot → diff, with auto-retention
  on the growing types. (Includes a ClrMD cache-flush fix so each walk reflects
  the current heap.)

---

*Inherited from the upstream base (Jimmy01240397/Dnspy-MCP), also shipped in this
release:* static reverse engineering — decompilation (types / methods / IL),
cross-DLL xref analysis, IL/byte patching, annotations; the live-debug skeleton
(attach/detach, breakpoints, call stack, `heap_stats` / `find_instances` /
`read_object` / `read_string`); per-Bearer multi-tenant isolation; flexible
hex/oct/bin numeric parameter parsing.
