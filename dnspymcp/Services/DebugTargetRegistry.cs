using System.Collections.Concurrent;
using ModelContextProtocol;

namespace DnSpyMcp.Services;

/// <summary>
/// Global, process-wide reservation table that prevents two tenants from
/// simultaneously TCP-connecting to the same dnspymcpagent (host:port).
///
/// One dnspymcpagent process owns one ICorDebug attach at a time — letting
/// two MCP tenants both drive it would corrupt step/pause state, so we
/// refuse the second connect with a clear error rather than racing.
///
/// Reservation is keyed by "host:port" (normalised, case-insensitive). The
/// owner token is stored so disconnect by the wrong tenant is a no-op and
/// the listing tools can answer "is this address in use, and by me?" without
/// leaking the other tenant's identity.
/// </summary>
internal sealed class DebugTargetRegistry
{
    private readonly ConcurrentDictionary<string, string> _heldBy = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string host, int port) => $"{host}:{port}";

    /// <summary>
    /// Attempt to reserve (host, port) for <paramref name="ownerToken"/>. If the
    /// slot is already held by the same token (re-connect), the reservation is
    /// kept. If held by a different token, throws.
    /// </summary>
    public void Reserve(string host, int port, string ownerToken)
    {
        var key = Key(host, port);
        var existing = _heldBy.GetOrAdd(key, ownerToken);
        if (!string.Equals(existing, ownerToken, StringComparison.Ordinal))
            throw new McpException(
                $"debug agent {key} is currently held by another MCP tenant — only one tenant may attach a given dnspymcpagent at a time. " +
                "Options: (1) target a different agent (start another dnspymcpagent on a different port and pass that host/port), " +
                "(2) wait and retry once the other tenant disconnects, " +
                "or (3) confirm with debug_session_list that you don't already hold this address under a different slot name.");
    }

    /// <summary>
    /// Release the reservation if it is held by <paramref name="ownerToken"/>.
    /// No-op if the slot is free or held by someone else (defensive — we never
    /// want one tenant's disconnect path to free another tenant's lock).
    /// </summary>
    public void Release(string host, int port, string ownerToken)
    {
        var key = Key(host, port);
        if (_heldBy.TryGetValue(key, out var current) &&
            string.Equals(current, ownerToken, StringComparison.Ordinal))
        {
            _heldBy.TryRemove(new KeyValuePair<string, string>(key, current));
        }
    }

    /// <summary>True if (host, port) is currently held by <paramref name="ownerToken"/>.</summary>
    public bool IsHeldBy(string host, int port, string ownerToken)
        => _heldBy.TryGetValue(Key(host, port), out var t) &&
           string.Equals(t, ownerToken, StringComparison.Ordinal);
}
