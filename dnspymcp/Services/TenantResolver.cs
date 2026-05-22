using Microsoft.AspNetCore.Http;
using ModelContextProtocol;

namespace DnSpyMcp.Services;

/// <summary>
/// Resolves the per-request Bearer token used to look up the active
/// <see cref="TenantContext"/>. One implementation per transport:
///
///  * <see cref="HttpTenantResolver"/> — reads <c>Authorization: Bearer ...</c>
///    off the current HTTP request, throws if absent.
///  * <see cref="StdioTenantResolver"/> — single-tenant: stdio is a 1:1
///    pipe, so all calls share one synthetic token.
/// </summary>
public interface ITenantResolver
{
    string ResolveToken();
}

/// <summary>
/// Extracts the Bearer token from <c>Authorization</c>. Token contents are
/// opaque to us — we only use them as an identity key. Requests that arrive
/// without a Bearer header (or with a malformed Authorization) collapse to a
/// single shared <see cref="AnonymousToken"/> tenant: their state is isolated
/// from every named tenant, but anonymous callers all share each other's
/// view (Workspace + debug sessions). That makes the server usable from a
/// quick `curl` without auth setup while still preventing accidental
/// cross-pollination with named tenants.
/// </summary>
public sealed class HttpTenantResolver : ITenantResolver
{
    /// <summary>Synthetic token used when no Bearer is presented.</summary>
    public const string AnonymousToken = "anonymous";

    private readonly IHttpContextAccessor _accessor;
    public HttpTenantResolver(IHttpContextAccessor accessor) => _accessor = accessor;

    public string ResolveToken()
    {
        var ctx = _accessor.HttpContext
            ?? throw new McpException("no HTTP context — cannot resolve tenant (is the call coming through the HTTP transport?)");
        return ExtractBearerOrAnonymous(ctx.Request.Headers.Authorization.ToString());
    }

    /// <summary>
    /// Parse a raw Authorization header value and return the token. Missing
    /// or malformed Authorization headers collapse to <see cref="AnonymousToken"/>
    /// rather than erroring — the shared anonymous context is a first-class
    /// tenant, not a privileged escape hatch.
    /// </summary>
    public static string ExtractBearerOrAnonymous(string? authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader)) return AnonymousToken;
        const string prefix = "Bearer ";
        if (!authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return AnonymousToken;
        var token = authHeader.Substring(prefix.Length).Trim();
        return token.Length == 0 ? AnonymousToken : token;
    }
}

/// <summary>Single-tenant resolver used by the stdio transport.</summary>
public sealed class StdioTenantResolver : ITenantResolver
{
    public const string FixedToken = "stdio";
    public string ResolveToken() => FixedToken;
}
