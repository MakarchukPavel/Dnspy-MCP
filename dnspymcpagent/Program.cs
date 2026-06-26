using System;
using System.Threading;
using DnSpyMcp.Agent.Handlers;
using DnSpyMcp.Agent.Services;

namespace DnSpyMcp.Agent;

internal static class Program
{
    public static DebuggerSession Session { get; private set; } = default!;
    public static Dispatcher Dispatcher { get; private set; } = default!;

    private static int Main(string[] args)
    {
        var cli = CliOptions.Parse(args);
        if (cli is null) return 1;

        Console.WriteLine($"dnspymcpagent v0.1.0 (net48, ICorDebug, tcp+ndjson)");
        Console.WriteLine($"  bind : {cli.Host}:{cli.Port}");
        Console.WriteLine($"  CLR  : {Environment.Version}");
        Console.WriteLine($"  pid  : {System.Diagnostics.Process.GetCurrentProcess().Id}");

        Session = new DebuggerSession();
        Dispatcher = new Dispatcher();

        SessionHandlers.Register(Dispatcher);
        ThreadStackHandlers.Register(Dispatcher);
        BreakpointHandlers.Register(Dispatcher);
        StepHandlers.Register(Dispatcher);
        ModuleHandlers.Register(Dispatcher);
        HeapHandlers.Register(Dispatcher);
        MemoryHandlers.Register(Dispatcher);
        FrameHandlers.Register(Dispatcher);
        ExceptionHandlers.Register(Dispatcher);
        EvalHandlers.Register(Dispatcher);

        // The agent boots in "no target" mode and waits for an MCP-driven
        // debug_pid_attach (session.attach RPC) to bind to a PID.

        var server = new TcpJsonServer(Dispatcher, cli.Host, cli.Port, cli.Token);
        server.Start();
        Console.WriteLine($"Listening on tcp://{cli.Host}:{cli.Port}  (ndjson). Ctrl+C to quit.");
        if (cli.Token != null) Console.WriteLine("  token required (send first: {\"method\":\"auth\",\"params\":{\"token\":\"...\"}})");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); server.Stop(); };

        var acceptThread = new Thread(() => server.RunAcceptLoop(cts.Token)) { IsBackground = false, Name = "dnspymcp-accept" };
        acceptThread.Start();
        acceptThread.Join();

        Console.WriteLine("shutting down...");
        try { Session.Dispose(); } catch { /* ignore */ }
        return 0;
    }
}

internal sealed class CliOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5555;
    public string? Token { get; set; }

    public static CliOptions? Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "-h":
                case "--host":
                    o.Host = Next() ?? throw new ArgumentException("missing value for --host");
                    break;
                case "-p":
                case "--port":
                    o.Port = int.Parse(Next() ?? throw new ArgumentException("missing value for --port"));
                    break;
                case "--token":
                    o.Token = Next();
                    break;
                case "--help":
                case "-?":
                    PrintHelp();
                    return null;
                default:
                    Console.Error.WriteLine($"unknown arg: {a}");
                    PrintHelp();
                    return null;
            }
        }
        return o;
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine("""
            dnspymcpagent [options]
              -h, --host HOST      bind host (default 127.0.0.1; use 0.0.0.0 for any)
              -p, --port PORT      bind tcp port (default 5555)
                  --token TOKEN    require an `auth` frame with this token as first message
                  --help           this help

            Live process attach is driven from the MCP tool `debug_pid_attach`
            (or RPC method `session.attach` over TCP) after the agent is up —
            the agent boots in 'no target' mode by default. Dump-file analysis
            is not supported; use IDA / WinDbg MCPs for offline dumps.

            Protocol: persistent TCP + newline-delimited JSON.
              Client sends:   {"id":N,"method":"...","params":{...}}
              Agent replies:  {"id":N,"ok":true,"result":...}
                              {"id":N,"ok":false,"error":"...","errorType":"..."}
              One client at a time. Agent sends a banner line on connect.
              Special method "__list__" returns the full method+description catalog.
            """);
    }
}
