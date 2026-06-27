Turns the upstream static-analysis MCP into a full **live-debugging + memory-leak-analysis** toolkit for .NET (.NET Framework 4.8 on IIS, also .NET Core/9 and dumps).

**Highlights**
- 🔌 Attach to .NET Core/9 (not just Framework) · x86 + x64 agents · load `.dmp` dumps
- 🧱 Struct decoding (Guid/DateTime/enum/decimal) in heap & frames
- ⛔ Exception interception (break on throw, by type, + ignore-list)
- 🔎 `debug_eval` passive object-graph reads · value-based conditional breakpoints
- ▶️ Func-eval (`debug_eval_call`) v1–v5: args, generics, overloads, multi-hop receivers, HRESULT hints — incl. enabling it on Release/optimized builds (`debug_jit_status` / `debug_touch_config` / `debug_launch`)
- 🧩 Heap reads: static fields · `List`/`Dictionary` · arrays · references/referencing
- 🗺️ Source↔IL mapping — show the C# line at the paused IP, set a breakpoint by source line
- 📝 Tracepoints/logpoints — capture frame values and auto-continue
- 🧠 Leak analysis — GC roots · retention path (`!gcroot`) · one-call leak report · snapshot diff over time

See `RELEASE_NOTES.md` for the full breakdown.
