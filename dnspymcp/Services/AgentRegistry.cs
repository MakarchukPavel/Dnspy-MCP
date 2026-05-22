using System.Collections.Concurrent;
using ModelContextProtocol;

namespace DnSpyMcp.Services;

/// <summary>
/// Holds N named <see cref="AgentClient"/> instances so the MCP server can talk
/// to several dnspymcpagent processes at once (e.g. one per target VM). Each
/// tool call operates on the <em>active</em> agent unless a <c>name</c> is passed
/// explicitly.
///
/// One AgentRegistry instance belongs to one tenant (one Bearer token). It
/// coordinates with the shared <see cref="DebugTargetRegistry"/> so a tenant
/// cannot open a TCP session to a host:port that's already held by another
/// tenant — the second tenant gets an immediate, clear error rather than two
/// MCPs racing for one ICorDebug attach.
///
/// Thread safety: the agent map is a ConcurrentDictionary; the active-name slot
/// and the per-slot reservation table are guarded by a small lock. AgentClient
/// itself serialises its own IO.
///
/// Deliberately NOT IDisposable: a scoped DI factory hands the tenant's
/// AgentRegistry to every tool call, and MS DI would Dispose-track each
/// resolution and close every TCP link at scope end. Cleanup is explicit via
/// <see cref="CloseAll"/> from <see cref="TenantContext"/>.
/// </summary>
public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, AgentClient> _agents = new(StringComparer.OrdinalIgnoreCase);
    // Tracks which (host, port) each named slot currently has reserved with the
    // process-wide DebugTargetRegistry, so we can release on disconnect /
    // replace / dispose without re-derive-from-AgentClient gymnastics.
    private readonly Dictionary<string, (string host, int port)> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private string? _active;

    private readonly string _ownerToken;
    private readonly DebugTargetRegistry _debugTargets;

    internal AgentRegistry(string ownerToken, DebugTargetRegistry debugTargets)
    {
        _ownerToken = ownerToken;
        _debugTargets = debugTargets;
    }

    public string? ActiveName
    {
        get { lock (_stateLock) return _active; }
    }

    /// <summary>Resolve the agent to use. If <paramref name="name"/> is null/empty, use the active one.</summary>
    public AgentClient Get(string? name = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            string? n;
            lock (_stateLock) n = _active;
            if (n == null) throw new McpException("no active debug session in this tenant. Call debug_session_connect first; use debug_session_list to see what's open.");
            if (!_agents.TryGetValue(n, out var a))
                throw new McpException($"active debug session '{n}' was removed concurrently. Re-run debug_session_connect, or pick an existing one from debug_session_list and call debug_session_switch.");
            return a;
        }
        if (!_agents.TryGetValue(name, out var agent))
            throw new McpException($"debug session '{name}' is not registered in this tenant. Call debug_session_connect with this name first, or use debug_session_list to see registered session names under this Bearer.");
        return agent;
    }

    /// <summary>
    /// Open (or re-open) the named slot against <paramref name="host"/>:<paramref name="port"/>
    /// using <paramref name="token"/> for the agent-side auth handshake. Reserves the
    /// debug target in the shared <see cref="DebugTargetRegistry"/> first so a
    /// rival tenant cannot grab the same host:port mid-flight, releases on
    /// failure, and replaces any previous reservation if the slot was already
    /// pointing at a different host:port.
    /// </summary>
    public AgentClient OpenSlot(string name, string host, int port, string? token)
    {
        AgentClient? agent;
        (string host, int port)? oldReservation = null;
        lock (_stateLock)
        {
            // If this slot already had a reservation (re-connect or move),
            // release it under the same lock that protects the table so we
            // can't lose track of it on a concurrent Dispose.
            if (_reserved.TryGetValue(name, out var prev))
            {
                oldReservation = prev;
                _reserved.Remove(name);
            }
        }
        if (oldReservation is { } prevRes)
            _debugTargets.Release(prevRes.host, prevRes.port, _ownerToken);

        // Reserve BEFORE creating the TCP connection, so a same-host:port
        // concurrent OpenSlot from another tenant loses the race here rather
        // than later (and we don't half-open a TCP we'll have to tear down).
        _debugTargets.Reserve(host, port, _ownerToken);
        try
        {
            agent = _agents.GetOrAdd(name, _ => new AgentClient());
            agent.Configure(host, port, token);
            agent.Connect();
            lock (_stateLock)
            {
                _reserved[name] = (host, port);
                _active = name;
            }
            return agent;
        }
        catch
        {
            _debugTargets.Release(host, port, _ownerToken);
            throw;
        }
    }

    public bool Remove(string name)
    {
        if (_agents.TryRemove(name, out var a))
        {
            (string host, int port)? releaseMe = null;
            lock (_stateLock)
            {
                if (_reserved.TryGetValue(name, out var r))
                {
                    releaseMe = r;
                    _reserved.Remove(name);
                }
                if (string.Equals(_active, name, StringComparison.OrdinalIgnoreCase))
                    _active = _agents.Keys.FirstOrDefault();
            }
            try { a.Dispose(); } catch { /* ignore */ }
            if (releaseMe is { } r2)
                _debugTargets.Release(r2.host, r2.port, _ownerToken);
            return true;
        }
        return false;
    }

    public AgentClient Switch(string name)
    {
        if (!_agents.TryGetValue(name, out var a))
            throw new McpException($"debug session '{name}' is not registered in this tenant. Call debug_session_connect with this name first, or use debug_session_list to pick an existing slot.");
        lock (_stateLock) _active = name;
        return a;
    }

    public IEnumerable<KeyValuePair<string, AgentClient>> All => _agents;

    /// <summary>
    /// Disconnect every registered slot — invoked by <see cref="TenantContext.Dispose"/>
    /// (and ultimately the host shutdown that disposes <see cref="TenantStore"/>)
    /// so we never leak TCP connections back to the agents or hold global
    /// reservations past tenant lifetime.
    /// </summary>
    public void CloseAll()
    {
        KeyValuePair<string, AgentClient>[] snapshot;
        List<(string host, int port)> releases;
        lock (_stateLock)
        {
            snapshot = _agents.ToArray();
            releases = _reserved.Values.ToList();
            _reserved.Clear();
            _active = null;
        }
        foreach (var kv in snapshot)
        {
            try { kv.Value.Dispose(); } catch { /* ignore */ }
        }
        _agents.Clear();
        foreach (var r in releases)
            _debugTargets.Release(r.host, r.port, _ownerToken);
    }
}
