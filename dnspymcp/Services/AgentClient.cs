using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace DnSpyMcp.Services;

/// <summary>
/// Persistent TCP + newline-delimited-JSON client for dnspymcpagent.
/// One connection is kept alive across many tool invocations. Requests/
/// responses use System.Text.Json so the objects returned by <see cref="Result"/>
/// serialize cleanly back to MCP clients.
/// </summary>
public sealed class AgentClient : IDisposable
{
    private readonly object _connectLock = new();
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;
    private string _host = "127.0.0.1";
    private int _port = 5555;
    private string? _token;

    public bool IsConnected => _tcp?.Connected ?? false;
    public string? Host => _tcp != null ? _host : null;
    public int? Port => _tcp != null ? _port : null;

    public void Configure(string host, int port, string? token)
    {
        _host = host;
        _port = port;
        _token = token;
    }

    public void Connect()
    {
        lock (_connectLock)
        {
            CloseLocked();
            try
            {
                _tcp = new TcpClient();
                _tcp.Connect(_host, _port);
                _tcp.NoDelay = true;
                var stream = _tcp.GetStream();
                // Banner must arrive promptly — if the port is open but the listener
                // isn't dnspymcpagent (e.g. typo'd port hits a different service),
                // ReadLine would otherwise block forever. Cap at 5s for the banner read.
                stream.ReadTimeout = 5000;
                _reader = new StreamReader(stream, new UTF8Encoding(false));
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                string? banner;
                try
                {
                    banner = _reader.ReadLine();
                }
                catch (IOException ex) when (ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.TimedOut)
                {
                    CloseLocked();
                    throw new McpException($"agent at {_host}:{_port} did not send a banner within 5s — port likely belongs to a different service. Start dnspymcpagent.exe on the right port and retry.");
                }
                if (banner == null)
                    throw new McpException($"agent at {_host}:{_port} closed the connection without sending a banner (is dnspymcpagent actually running there?)");
                // Restore infinite read timeout for ongoing RPC traffic — long-running
                // ops (debug_step_over, debug_go) legitimately stall waiting for events.
                stream.ReadTimeout = Timeout.Infinite;

                if (_token != null)
                {
                    var resp = CallLocked("auth", new { token = _token });
                    if (resp["ok"]?.GetValue<bool>() != true)
                        throw new McpException($"agent at {_host}:{_port} rejected auth: {resp["error"]}");
                }
            }
            catch (SocketException ex)
            {
                CloseLocked();
                throw new McpException($"could not reach dnspymcpagent at {_host}:{_port} — {ex.SocketErrorCode} ({ex.Message}). Start the agent with `dnspymcpagent.exe --host {_host} --port {_port} --attach <pid>` and retry.");
            }
            catch (IOException ex)
            {
                CloseLocked();
                throw new McpException($"IO failure talking to agent at {_host}:{_port}: {ex.Message}");
            }
        }
    }

    public void Close()
    {
        lock (_connectLock) CloseLocked();
    }

    private void CloseLocked()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _tcp?.Close(); } catch { }
        _writer = null; _reader = null; _tcp = null;
    }

    public JsonObject Call(string method, object? @params = null)
    {
        lock (_connectLock)
        {
            if (_tcp == null || !_tcp.Connected)
                Connect();
            try
            {
                return CallLocked(method, @params);
            }
            catch (IOException ex)
            {
                CloseLocked();
                throw new McpException($"IO failure talking to agent at {_host}:{_port} during '{method}': {ex.Message}");
            }
        }
    }

    private JsonObject CallLocked(string method, object? @params)
    {
        if (_writer == null || _reader == null) throw new McpException("agent client is not connected — call debug_session_connect first");
        int id = ++_nextId;
        var frame = JsonSerializer.Serialize(new { id, method, @params });
        _writer.WriteLine(frame);
        var line = _reader.ReadLine() ?? throw new IOException($"agent closed the connection while waiting for a response to '{method}'");
        return JsonNode.Parse(line)?.AsObject()
               ?? throw new IOException($"agent returned a non-object response to '{method}': {line}");
    }

    /// <summary>
    /// Issue an RPC and return the `result` payload as a JsonNode (serializes
    /// cleanly through System.Text.Json). Throws if the agent reported an error.
    /// </summary>
    public JsonNode? Result(string method, object? @params = null)
    {
        var resp = Call(method, @params);
        if (resp["ok"]?.GetValue<bool>() != true)
        {
            var err = resp["error"]?.ToString() ?? "unknown";
            var errType = resp["errorType"]?.ToString();
            throw new McpException($"agent error ({method}): {err}{(errType != null ? $" [{errType}]" : "")}");
        }
        var result = resp["result"];
        // detach from parent so the caller can freely move it into a new tree
        return result?.DeepClone();
    }

    public void Dispose() => Close();
}
