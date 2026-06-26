using System.Collections.Generic;
using dndbg.Engine;

namespace DnSpyMcp.Agent.Services;

public sealed class BreakpointEntry
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";  // il, native, by_name
    public string Description { get; set; } = "";
    public object DnBreakpoint { get; set; } = default!;
    public bool Enabled { get; set; } = true;

    // Conditional-BP state (D2). Condition is the user-supplied source
    // expression, kept verbatim for `bp.list` display. HitCount increments
    // on every callback firing — both when the condition holds and when
    // it doesn't, so the count is always the true number of times the
    // instruction was hit.
    public string? Condition { get; set; }
    public int HitCount;  // public field so the condition callback can Interlocked.Increment it

    // Tracepoint / logpoint state (D5). When LogExpressions is set, the hit
    // callback snapshots those passive values into Captures and, when
    // ContinueAfter is true, never pauses (log-only). MaxCaptures caps the
    // buffer: once reached, capturing stops (post-cap hits skip all ClrMD work)
    // so the per-hit cost drops to the bare stop+continue. Captures is guarded
    // by locking on the list itself (callback runs on the debugger STA thread;
    // bp.log reads from the dispatcher thread).
    public string[]? LogExpressions;
    public bool ContinueAfter;          // log-only: capture then auto-continue (no pause)
    public int MaxCaptures;             // hard cap on stored samples
    public List<object>? Captures;      // ring of {seq, hit, values:{expr->value}}
    public int CaptureSeq;              // monotonic sample counter (total attempts, incl. dropped after cap)
    public bool Capped;                 // set once MaxCaptures was reached
}

public sealed class BreakpointRegistry
{
    private int _nextId = 1;
    private readonly Dictionary<int, BreakpointEntry> _map = new();

    public BreakpointEntry Add(string kind, string description, object dnBreakpoint)
    {
        var entry = new BreakpointEntry
        {
            Id = _nextId++,
            Kind = kind,
            Description = description,
            DnBreakpoint = dnBreakpoint,
            Enabled = true,
        };
        _map[entry.Id] = entry;
        return entry;
    }

    public bool TryGet(int id, out BreakpointEntry entry) => _map.TryGetValue(id, out entry!);
    public bool Remove(int id) => _map.Remove(id);
    public IEnumerable<BreakpointEntry> All => _map.Values;

    /// <summary>
    /// Wipe every registered breakpoint AND reset the id counter back to 1.
    /// Called from <see cref="DebuggerSession.Detach"/> so a subsequent attach
    /// (to the same or a different PID) starts from a clean state — no
    /// stale entries, no surprising "next BP got id=4 instead of id=1"
    /// counter leak. <see cref="Remove(int)"/> intentionally does NOT
    /// touch the counter so within-session ids stay monotonic.
    /// </summary>
    public void Clear()
    {
        _map.Clear();
        _nextId = 1;
    }
}
