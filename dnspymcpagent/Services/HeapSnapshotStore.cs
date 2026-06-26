using System;
using System.Collections.Generic;
using System.Linq;

namespace DnSpyMcp.Agent.Services;

/// <summary>
/// A captured heap type-histogram (per-type object count + total size) at a
/// point in time. Holds only strings/numbers — no ClrMD object references — so
/// it stays valid after the heap moves and can be diffed against a later
/// snapshot to find what grew (the leak-detection workflow).
/// </summary>
public sealed class HeapSnapshot
{
    public int Id;
    public string? Label;
    public DateTime TakenUtc;
    public long TotalObjects;
    public long TotalSize;
    public Dictionary<string, (long count, long size)> Histogram = new();
}

/// <summary>
/// Agent-side store of <see cref="HeapSnapshot"/>s. Keeps the most recent
/// <see cref="MaxKept"/> (FIFO eviction) and is wiped on detach so a new attach
/// starts clean (snapshots from a different process must not be diffed).
/// </summary>
public static class HeapSnapshotStore
{
    private const int MaxKept = 20;
    private static readonly object _lock = new();
    private static readonly Dictionary<int, HeapSnapshot> _map = new();
    private static int _next = 1;

    public static HeapSnapshot Add(HeapSnapshot s)
    {
        lock (_lock)
        {
            s.Id = _next++;
            _map[s.Id] = s;
            while (_map.Count > MaxKept)
                _map.Remove(_map.Keys.Min()); // evict oldest id
            return s;
        }
    }

    public static bool TryGet(int id, out HeapSnapshot snapshot)
    {
        lock (_lock) { return _map.TryGetValue(id, out snapshot!); }
    }

    public static List<HeapSnapshot> All
    {
        get { lock (_lock) { return _map.Values.OrderBy(v => v.Id).ToList(); } }
    }

    public static int? NewestId
    {
        get { lock (_lock) { return _map.Count == 0 ? (int?)null : _map.Keys.Max(); } }
    }

    public static void Clear()
    {
        lock (_lock) { _map.Clear(); _next = 1; }
    }
}
