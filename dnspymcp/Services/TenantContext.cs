namespace DnSpyMcp.Services;

/// <summary>
/// Bundles the per-tenant state owned by one Authorization Bearer token: the
/// Workspace (opened asm_files + cross-DLL index) and the AgentRegistry
/// (named live-debug TCP sessions). Two tenants get two TenantContexts —
/// they cannot see each other's opened binaries or attached debug agents,
/// and the analyzer/xref tools naturally scope to one tenant's Workspace.
///
/// Lifetime is owned by <see cref="TenantStore"/> (singleton); we dispose
/// on tenant eviction or host shutdown so file handles and TCP links don't
/// leak.
/// </summary>
public sealed class TenantContext : IDisposable
{
    public string Token { get; }
    public Workspace Workspace { get; }
    public AgentRegistry Agents { get; }

    internal TenantContext(string token, DebugTargetRegistry debugTargets)
    {
        Token = token;
        Workspace = new Workspace();
        Agents = new AgentRegistry(token, debugTargets);
    }

    public void Dispose()
    {
        try { Agents.CloseAll(); } catch { }
        try { Workspace.CloseAll(); } catch { }
    }
}
