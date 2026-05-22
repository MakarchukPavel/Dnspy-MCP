using DnSpyMcp.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DnSpyMcp;

internal static class Program
{
    private static int Main(string[] args)
    {
        var cli = Cli.Parse(args);
        if (cli is null) return 1;

        return cli.Transport switch
        {
            "stdio" => RunStdio(cli),
            "http"  => RunHttp(cli),
            "sse"   => RunSse(cli),
            _       => Fail($"unknown transport: {cli.Transport}"),
        };
    }

    private static int Fail(string msg) { Console.Error.WriteLine(msg); return 1; }

    // ---------- stdio (default) ----------
    // Stdio is a 1:1 pipe — one process pair shares one synthetic tenant.
    // No Bearer extraction is required; every call resolves to the same
    // TenantContext via StdioTenantResolver.
    private static int RunStdio(Cli cli)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        RegisterShared(builder.Services, httpScoped: false);
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithRequestFilters(f => f.AddCallToolFilter(ToolErrorFilter.Wrap));
        builder.Build().Run();
        return 0;
    }

    // ---------- http (Streamable HTTP) ----------
    private static int RunHttp(Cli cli) => RunHttpLike(cli, "http");

    // ---------- sse (legacy /sse transport; same pipeline as http) ----------
    private static int RunSse(Cli cli) => RunHttpLike(cli, "sse");

    private static int RunHttpLike(Cli cli, string label)
    {
        var builder = WebApplication.CreateBuilder();
        RegisterShared(builder.Services, httpScoped: true);
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly()
            .WithRequestFilters(f => f.AddCallToolFilter(ToolErrorFilter.Wrap));
        var app = builder.Build();
        app.MapMcp(cli.McpPath);
        app.Urls.Add($"http://{cli.BindHost}:{cli.BindPort}");
        Console.Error.WriteLine($"dnspymcp ({label}) listening on http://{cli.BindHost}:{cli.BindPort}{cli.McpPath} (Authorization: Bearer optional; no header => shared 'anonymous' tenant)");
        app.Run();
        return 0;
    }

    private static void RegisterShared(IServiceCollection s, bool httpScoped)
    {
        // TenantStore is the DI container's only durable handle on per-tenant
        // state. Disposed at host shutdown — which in turn disposes every
        // TenantContext (Workspace + AgentRegistry), so on-disk asms unlock
        // and TCP links to dnspymcpagent close.
        s.AddSingleton<TenantStore>();

        if (httpScoped)
        {
            s.AddHttpContextAccessor();
            s.AddScoped<ITenantResolver, HttpTenantResolver>();
        }
        else
        {
            s.AddSingleton<ITenantResolver, StdioTenantResolver>();
        }

        // Scoped factory: each MCP request scope gets the *tenant's* Workspace /
        // AgentRegistry — the actual objects live inside TenantStore and are
        // shared across every call that carries the same Bearer.
        s.AddScoped<Workspace>(sp =>
            sp.GetRequiredService<TenantStore>()
              .GetOrCreate(sp.GetRequiredService<ITenantResolver>().ResolveToken())
              .Workspace);
        s.AddScoped<AgentRegistry>(sp =>
            sp.GetRequiredService<TenantStore>()
              .GetOrCreate(sp.GetRequiredService<ITenantResolver>().ResolveToken())
              .Agents);
    }
}

internal sealed class Cli
{
    public string Transport { get; set; } = "stdio";
    public string BindHost { get; set; } = "127.0.0.1";
    public int BindPort { get; set; } = 5556;
    public string McpPath { get; set; } = "/mcp";

    public static Cli? Parse(string[] args)
    {
        var o = new Cli();
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--transport": o.Transport = Next() ?? throw new ArgumentException("--transport needs value"); break;
                case "--bind-host": o.BindHost = Next() ?? throw new ArgumentException("--bind-host needs value"); break;
                case "--bind-port": o.BindPort = int.Parse(Next()!); break;
                case "--mcp-path":  o.McpPath = Next() ?? "/mcp"; break;
                case "--help":
                case "-?":
                case "-h":
                    PrintHelp(); return null;
                default:
                    Console.Error.WriteLine($"unknown arg: {args[i]}");
                    PrintHelp(); return null;
            }
        }
        return o;
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine("""
            dnspymcp — MCP server exposing dnSpy static & ICorDebug live tools.

            Transports (pick one via --transport):
              stdio        Default. One MCP session over stdin/stdout (single-tenant).
              http         Streamable-HTTP transport (modern MCP). Per-request
                           `Authorization: Bearer <token>` identifies the tenant
                           (workspace + debug sessions are per-tenant; two tenants
                           cannot share or hijack each other's opened binaries or
                           attached debug agents). Requests with no Authorization
                           header fall into a single shared 'anonymous' tenant.
              sse          Legacy SSE transport (same auth contract as http).

            Usage:
              dnspymcp [--transport stdio|http|sse]
                       [--bind-host 127.0.0.1] [--bind-port 5556] [--mcp-path /mcp]

            The agent target is picked via the debug_session_connect tool at
            runtime — host and port are required parameters there.
            """);
    }
}
