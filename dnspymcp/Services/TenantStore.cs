using System.Collections.Concurrent;

namespace DnSpyMcp.Services;

/// <summary>
/// Singleton owner of every <see cref="TenantContext"/>. One tenant per
/// distinct Bearer token — created lazily on first access, disposed at
/// host shutdown.
///
/// Holds the shared <see cref="DebugTargetRegistry"/> so any tenant's
/// AgentRegistry can refuse to connect to a debug agent already held
/// elsewhere.
/// </summary>
public sealed class TenantStore : IDisposable
{
    private readonly ConcurrentDictionary<string, TenantContext> _tenants = new(StringComparer.Ordinal);
    private readonly DebugTargetRegistry _debugTargets = new();

    public TenantContext GetOrCreate(string token)
    {
        if (string.IsNullOrEmpty(token))
            throw new ArgumentException("token must be non-empty", nameof(token));
        return _tenants.GetOrAdd(token, t => new TenantContext(t, _debugTargets));
    }

    /// <summary>Snapshot of every active tenant token (for diagnostics only).</summary>
    public IEnumerable<string> Tokens => _tenants.Keys;

    public void Dispose()
    {
        foreach (var kv in _tenants.ToArray())
        {
            try { kv.Value.Dispose(); } catch { /* ignore */ }
        }
        _tenants.Clear();
    }
}
