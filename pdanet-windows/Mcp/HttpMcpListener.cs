using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdaNetPortable.Services;
using Timer = System.Threading.Timer;

namespace PdaNetPortable.Mcp;

/// <summary>
/// The MCP Streamable HTTP transport, for agent frameworks and apps that connect over HTTP rather
/// than starting PdaNet themselves. A small HTTP/1.1 server on the loopback address only, so it
/// needs no admin rights and nothing else on the network can reach it.
///
/// Safety: every request needs the key (Authorization: Bearer …). The Host and Origin headers must
/// name this PC's loopback address, which stops a website from reaching the server through DNS
/// rebinding. Bodies are capped.
///
/// It speaks both eras of the transport: 2025-03-26 to 2025-11-25 clients start a session with
/// initialize (Mcp-Session-Id, GET for a stream of notifications, DELETE to end it), 2026-07-28
/// clients send stateless requests with mirrored MCP-Protocol-Version and Mcp-Method headers.
/// A tool call answers as a stream of server-sent events when the client accepts them, so progress
/// arrives as it happens and long waits are kept alive.
/// </summary>
public sealed class HttpMcpListener
{
    private const int MaxHeader = 32 * 1024;
    private const int MaxBody = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly McpServer _server;
    private readonly Func<string> _key;
    private readonly ConcurrentDictionary<string, McpConnection> _sessions = new();
    private readonly List<TcpListener> _listeners = new();
    private CancellationTokenSource? _cts;
    private Timer? _sweep;

    public HttpMcpListener(McpServer server, int port, Func<string> key)
    {
        _server = server;
        Port = port;
        _key = key;
    }

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/mcp";

    /// <summary>Starts listening; false (with the reason) when the port is taken.</summary>
    public bool Start(out string? error)
    {
        error = null;
        _cts = new CancellationTokenSource();
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            try
            {
                var l = new TcpListener(address, Port);
                l.Server.ExclusiveAddressUse = true;
                l.Start(64);
                _listeners.Add(l);
                _ = AcceptAsync(l, _cts.Token);
            }
            catch (SocketException e) when (address.Equals(IPAddress.IPv6Loopback))
            {
                Logger.Instance.Debug($"Agent HTTP: no IPv6 loopback ({e.SocketErrorCode})");
            }
            catch (SocketException e)
            {
                error = e.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"Port {Port} is in use by another program, pick another port"
                    : $"Port {Port} could not be opened ({e.SocketErrorCode})";
                Stop();
                return false;
            }
        }
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        Logger.Instance.Info($"AI agents can connect over HTTP at {Url}");
        return true;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        foreach (var l in _listeners) { try { l.Stop(); } catch { } }
        _listeners.Clear();
        foreach (var s in _sessions.Values) _server.Close(s);
        _sessions.Clear();
        _sweep?.Dispose();
        _sweep = null;
    }

    /// <summary>Ends every session, for Stop in the browser.</summary>
    public void DropSessions()
    {
        foreach (var s in _sessions.Values) _server.Close(s);
        _sessions.Clear();
    }

    private void Sweep()
    {
        foreach (var (id, s) in _sessions)
        {
            if (DateTime.Now - s.LastUsed < TimeSpan.FromMinutes(60) || s.InFlight.Count > 0) continue;
            if (_sessions.TryRemove(id, out _)) _server.Close(s);
        }
    }

    /// <summary>A new random key: 32 bytes, URL-safe base64.</summary>
    public static string NewKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ============================ Connections ============================
    private async Task AcceptAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => ServeClientAsync(client, ct));
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        var reader = new RequestReader(stream);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromMinutes(2));
                var req = await reader.ReadAsync(idle.Token);
                if (req == null) break;
                bool keep = await RespondAsync(req, stream, ct);
                if (!keep || !req.KeepAlive) break;
            }
        }
        catch (HttpProblem p)
        {
            try { await WriteAsync(stream, p.Status, "text/plain", Utf8.GetBytes(p.Message), close: true); } catch { }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception e) { Logger.Instance.Debug($"Agent HTTP: {e.Message}"); }
    }

    private async Task<bool> RespondAsync(Request req, Stream s, CancellationToken ct)
    {
        var path = req.Path.Split('?')[0];
        if (path != "/mcp" && path != "/mcp/")
            return await WriteAsync(s, 404, "text/plain", Utf8.GetBytes("Not found. The MCP endpoint is /mcp."));

        // DNS rebinding: a website that points its own name at 127.0.0.1 sends its name as Host and its origin as Origin.
        if (!LoopbackHost(req.Header("Host")))
            return await RpcErrorAsync(s, 403, null, -32600, "Forbidden: the Host header must be this PC's loopback address.");
        var origin = req.Header("Origin");
        if (origin != null && origin != "null" && !LoopbackOrigin(origin))
            return await RpcErrorAsync(s, 403, null, -32600, "Forbidden: requests from websites are not allowed.");
        if (!Authorized(req.Header("Authorization")))
        {
            var body = Utf8.GetBytes(MsgJson(McpServer.Error(null, -32001, "Unauthorized: send the key from PdaNet Settings, AI agents, as Authorization: Bearer <key>.")));
            return await WriteAsync(s, 401, "application/json", body, extra: new() { ["WWW-Authenticate"] = "Bearer realm=\"PdaNet browser\"" });
        }

        switch (req.Method)
        {
            case "OPTIONS":
                return await WriteAsync(s, 204, null, null, extra: new() { ["Allow"] = "POST, GET, DELETE, OPTIONS" });
            case "GET":
                return await StreamSessionAsync(req, s, ct);
            case "DELETE":
                var gone = req.Header("Mcp-Session-Id");
                if (gone == null) return await WriteAsync(s, 405, null, null, extra: new() { ["Allow"] = "POST" });
                if (!_sessions.TryRemove(gone, out var ended)) return await RpcErrorAsync(s, 404, null, -32001, "Session not found");
                _server.Close(ended);
                return await WriteAsync(s, 204, null, null);
            case "POST":
                return await PostAsync(req, s, ct);
            default:
                return await WriteAsync(s, 405, null, null, extra: new() { ["Allow"] = "POST, GET, DELETE, OPTIONS" });
        }
    }

    private async Task<bool> PostAsync(Request req, Stream s, CancellationToken ct)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(req.Body); }
        catch { return await RpcErrorAsync(s, 400, null, -32700, "Parse error"); }
        if (msg is not (JsonObject or JsonArray)) return await RpcErrorAsync(s, 400, null, -32600, "Invalid Request");

        var first = msg as JsonObject ?? (msg as JsonArray)?.FirstOrDefault() as JsonObject;
        var id = first?["id"];
        string? method = null, metaVersion = null;
        try { method = first?["method"]?.GetValue<string>(); } catch { }
        try { metaVersion = first?["params"]?["_meta"]?["io.modelcontextprotocol/protocolVersion"]?.GetValue<string>(); } catch { }

        // 2026-07-28: the headers mirror the body. A request of that version must carry them, and they
        // must agree with the body; older clients may send some, and what they send must agree too.
        var headerVersion = req.Header("MCP-Protocol-Version");
        var headerMethod = req.Header("Mcp-Method");
        var headerName = req.Header("Mcp-Name");
        bool modern = McpServer.ModernVersions.Contains(metaVersion ?? headerVersion ?? "");
        string? bodyName = null;
        if (first?["params"] is JsonObject prm && method is "tools/call" or "prompts/get" or "resources/read")
        {
            try { bodyName = (method == "resources/read" ? prm["uri"] : prm["name"])?.GetValue<string>(); } catch { }
        }
        if (modern)
        {
            if (msg is JsonArray)
                return await RpcErrorAsync(s, 400, null, -32600, "Invalid Request: this protocol version takes one message per request, not a batch");
            if (headerVersion == null)
                return await RpcErrorAsync(s, 400, id, -32020, "Header mismatch: the MCP-Protocol-Version header is missing");
            if (headerMethod == null && method != null)
                return await RpcErrorAsync(s, 400, id, -32020, "Header mismatch: the Mcp-Method header is missing");
            if (headerName == null && bodyName != null)
                return await RpcErrorAsync(s, 400, id, -32020, $"Header mismatch: the Mcp-Name header is missing (it carries the {(method == "resources/read" ? "uri" : "name")} of {method})");
        }
        if (headerVersion != null && metaVersion != null && headerVersion != metaVersion)
            return await RpcErrorAsync(s, 400, id, -32020, $"Header mismatch: MCP-Protocol-Version '{headerVersion}' does not match the body's '{metaVersion}'");
        if (headerMethod != null && method != null && headerMethod != method)
            return await RpcErrorAsync(s, 400, id, -32020, $"Header mismatch: Mcp-Method '{headerMethod}' does not match the body's '{method}'");
        if (headerName != null && bodyName != null && Decode(headerName) != bodyName)
            return await RpcErrorAsync(s, 400, id, -32020, "Header mismatch: Mcp-Name does not match the body");

        McpConnection conn;
        bool transient = false;
        var extra = new Dictionary<string, string>();
        var sessionId = req.Header("Mcp-Session-Id");
        if (method == "initialize")
        {
            conn = new McpConnection("http", send: null);
            _sessions[conn.Id] = conn;
            _server.Open(conn);
            extra["Mcp-Session-Id"] = conn.Id;
        }
        else if (modern || method == "server/discover")
        {
            // Stateless: everything the request needs is in it, and session ids are ignored. Known to the
            // server while it runs, so Stop in the browser can cancel it.
            conn = new McpConnection("http", send: null);
            transient = true;
            _server.Open(conn);
        }
        else if (sessionId == null)
            return await RpcErrorAsync(s, 400, id, -32600, "Bad Request: no Mcp-Session-Id. Send initialize first.");
        else if (!_sessions.TryGetValue(sessionId, out conn!))
            return await RpcErrorAsync(s, 404, id, -32001, "Session not found. Send initialize again.");

        try
        {
            return await AnswerAsync(req, s, msg, method, modern, conn, extra, ct);
        }
        finally
        {
            if (transient) _server.Close(conn);
        }
    }

    private async Task<bool> AnswerAsync(Request req, Stream s, JsonNode msg, string? method, bool modern, McpConnection conn,
        Dictionary<string, string> extra, CancellationToken ct)
    {
        // Notifications and responses only: accepted, no body.
        bool request = msg is JsonObject o ? o["id"] != null && o["method"] != null
            : ((JsonArray)msg).Any(m => m is JsonObject mo && mo["id"] != null && mo["method"] != null);
        if (!request)
        {
            await _server.HandleAsync(msg, conn, null, ct);
            return await WriteAsync(s, 202, null, null, extra: extra);
        }

        bool sse = (req.Header("Accept") ?? "").Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
                   && method is "tools/call" or "resources/read";
        if (!sse)
        {
            var reply = await _server.HandleAsync(msg, conn, null, ct);
            if (reply == null) return await WriteAsync(s, 202, null, null, extra: extra);
            return await WriteAsync(s, StatusFor(reply, modern), "application/json", Utf8.GetBytes(MsgJson(reply)), extra: extra);
        }

        // A stream of events: progress as it comes, comments to keep it alive, then the answer.
        extra["Cache-Control"] = "no-cache, no-transform";
        await WriteHeadAsync(s, 200, "text/event-stream", chunked: true, extra);
        var gate = new SemaphoreSlim(1, 1);
        bool ended = false;
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Event(string text)
        {
            await gate.WaitAsync();
            try
            {
                // Nothing may follow the last chunk: the connection carries the next response.
                if (!ended) await ChunkAsync(s, Utf8.GetBytes(text));
            }
            catch { gone.Cancel(); }
            finally { gate.Release(); }
        }
        var alive = new Timer(_ => _ = Event(": keep-alive\n\n"), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        JsonNode? answer;
        try { answer = await _server.HandleAsync(msg, conn, m => _ = Event("event: message\ndata: " + MsgJson(m) + "\n\n"), gone.Token); }
        finally { await alive.DisposeAsync(); }
        if (answer != null) await Event("event: message\ndata: " + MsgJson(answer) + "\n\n");
        await gate.WaitAsync();
        try
        {
            ended = true;
            await ChunkAsync(s, Array.Empty<byte>());
        }
        finally { gate.Release(); }
        return !gone.IsCancellationRequested;
    }

    /// <summary>The 2025 revisions' GET: a stream of notifications for a session (resource updates, logs).</summary>
    private async Task<bool> StreamSessionAsync(Request req, Stream s, CancellationToken ct)
    {
        var id = req.Header("Mcp-Session-Id");
        if (id == null || !(req.Header("Accept") ?? "").Contains("text/event-stream"))
            return await WriteAsync(s, 405, null, null, extra: new() { ["Allow"] = "POST" });
        if (!_sessions.TryGetValue(id, out var conn)) return await RpcErrorAsync(s, 404, null, -32001, "Session not found");
        await WriteHeadAsync(s, 200, "text/event-stream", chunked: true, new() { ["Cache-Control"] = "no-cache, no-transform" });
        var gate = new SemaphoreSlim(1, 1);
        using var done = CancellationTokenSource.CreateLinkedTokenSource(ct, conn.Lifetime);
        void Send(JsonObject m) => _ = Write("event: message\ndata: " + MsgJson(m) + "\n\n");
        async Task Write(string text)
        {
            await gate.WaitAsync();
            try { await ChunkAsync(s, Utf8.GetBytes(text)); }
            catch { done.Cancel(); }
            finally { gate.Release(); }
        }
        conn.Send = Send;
        try
        {
            while (!done.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), done.Token).ContinueWith(_ => { });
                if (!done.IsCancellationRequested) await Write(": keep-alive\n\n");
            }
        }
        finally
        {
            if (conn.Send == Send) conn.Send = null;
        }
        return false;
    }

    // ============================ Checks ============================
    private bool LoopbackHost(string? host)
    {
        if (host == null) return false;
        var h = host.Trim().ToLowerInvariant();
        return h == $"127.0.0.1:{Port}" || h == $"localhost:{Port}" || h == $"[::1]:{Port}";
    }

    private bool LoopbackOrigin(string origin)
    {
        var o = origin.Trim().TrimEnd('/').ToLowerInvariant();
        return o == $"http://127.0.0.1:{Port}" || o == $"http://localhost:{Port}" || o == $"http://[::1]:{Port}";
    }

    private bool Authorized(string? header)
    {
        var key = _key();
        if (string.IsNullOrEmpty(key) || header == null) return false;
        var h = header.Trim();
        if (!h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var given = Utf8.GetBytes(h[7..].Trim());
        var want = Utf8.GetBytes(key);
        return given.Length == want.Length && CryptographicOperations.FixedTimeEquals(given, want);
    }

    /// <summary>Header values that are not plain ASCII come base64 encoded as =?base64?…?=.</summary>
    private static string Decode(string v)
    {
        if (v.StartsWith("=?base64?") && v.EndsWith("?="))
        {
            try { return Utf8.GetString(Convert.FromBase64String(v[9..^2])); } catch { }
        }
        return v;
    }

    /// <summary>2026-07-28 maps some JSON-RPC errors to HTTP statuses; earlier revisions answer 200.</summary>
    private static int StatusFor(JsonNode reply, bool modern)
    {
        if (!modern || reply is not JsonObject o || o["error"]?["code"] is not JsonNode c) return 200;
        int code = c.GetValue<int>();
        return code switch { -32601 => 404, -32602 or -32600 or -32020 or -32021 or -32022 => 400, _ => 200 };
    }

    // ============================ HTTP ============================
    private static string MsgJson(JsonNode m) => m.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    private static Task<bool> RpcErrorAsync(Stream s, int status, JsonNode? id, int code, string message) =>
        WriteAsync(s, status, "application/json", Utf8.GetBytes(MsgJson(McpServer.Error(id, code, message))));

    private static async Task<bool> WriteAsync(Stream s, int status, string? type, byte[]? body, bool close = false,
        Dictionary<string, string>? extra = null)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        if (type != null) sb.Append("Content-Type: ").Append(type).Append(type.StartsWith("application/json") ? "; charset=utf-8" : "").Append("\r\n");
        sb.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        if (extra != null) foreach (var (k, v) in extra) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append(close ? "Connection: close\r\n" : "Connection: keep-alive\r\n");
        sb.Append("\r\n");
        var head = Utf8.GetBytes(sb.ToString());
        await s.WriteAsync(head);
        if (body is { Length: > 0 }) await s.WriteAsync(body);
        await s.FlushAsync();
        return !close;
    }

    private static async Task WriteHeadAsync(Stream s, int status, string type, bool chunked, Dictionary<string, string> extra)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        sb.Append("Content-Type: ").Append(type).Append("\r\n");
        if (chunked) sb.Append("Transfer-Encoding: chunked\r\n");
        foreach (var (k, v) in extra) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("Connection: keep-alive\r\n\r\n");
        await s.WriteAsync(Utf8.GetBytes(sb.ToString()));
        await s.FlushAsync();
    }

    /// <summary>Writes one chunk of a chunked body; an empty chunk ends the body.</summary>
    private static async Task ChunkAsync(Stream s, byte[] data)
    {
        await s.WriteAsync(Utf8.GetBytes(data.Length.ToString("x") + "\r\n"));
        if (data.Length > 0) await s.WriteAsync(data);
        await s.WriteAsync("\r\n"u8.ToArray());
        await s.FlushAsync();
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 202 => "Accepted", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
        404 => "Not Found", 405 => "Method Not Allowed", 413 => "Payload Too Large", 431 => "Request Header Fields Too Large",
        _ => "Error",
    };

    private sealed class HttpProblem : Exception
    {
        public HttpProblem(int status, string message) : base(message) => Status = status;
        public int Status { get; }
    }

    private sealed class Request
    {
        public required string Method { get; init; }
        public required string Path { get; init; }
        public required Dictionary<string, string> Headers { get; init; }
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public bool KeepAlive { get; init; }
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>Reads HTTP/1.1 requests off a connection, one after the other.</summary>
    private sealed class RequestReader
    {
        private readonly Stream _s;
        private byte[] _buf = new byte[8192];
        private int _start, _end;

        public RequestReader(Stream s) => _s = s;

        public async Task<Request?> ReadAsync(CancellationToken ct)
        {
            // Head: up to the blank line.
            int headEnd;
            while ((headEnd = IndexOf("\r\n\r\n"u8)) < 0)
            {
                if (_end - _start > MaxHeader) throw new HttpProblem(431, "Request headers too large");
                if (!await FillAsync(ct)) return null;
            }
            var head = Encoding.ASCII.GetString(_buf, _start, headEnd - _start);
            _start = headEnd + 4;
            var lines = head.Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 3) throw new HttpProblem(400, "Bad request line");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var k = line[..colon].Trim();
                var v = line[(colon + 1)..].Trim();
                headers[k] = headers.TryGetValue(k, out var had) ? had + ", " + v : v;
            }
            bool http10 = first[2].EndsWith("1.0");
            var connection = headers.TryGetValue("Connection", out var c) ? c : "";
            var req = new Request
            {
                Method = first[0].ToUpperInvariant(),
                Path = first[1],
                Headers = headers,
                KeepAlive = http10 ? connection.Equals("keep-alive", StringComparison.OrdinalIgnoreCase)
                                   : !connection.Equals("close", StringComparison.OrdinalIgnoreCase),
            };
            if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                req.Body = await ChunkedAsync(ct);
            else if (headers.TryGetValue("Content-Length", out var cl))
            {
                if (!int.TryParse(cl, out var len) || len < 0) throw new HttpProblem(400, "Bad Content-Length");
                if (len > MaxBody) throw new HttpProblem(413, "Body too large");
                req.Body = await TakeAsync(len, ct);
            }
            return req;
        }

        private async Task<byte[]> ChunkedAsync(CancellationToken ct)
        {
            using var body = new MemoryStream();
            while (true)
            {
                var line = await LineAsync(ct);
                var size = Convert.ToInt32(line.Split(';')[0].Trim(), 16);
                if (size == 0)
                {
                    while ((await LineAsync(ct)).Length > 0) { } // trailers
                    return body.ToArray();
                }
                if (body.Length + size > MaxBody) throw new HttpProblem(413, "Body too large");
                body.Write(await TakeAsync(size, ct));
                await LineAsync(ct);
            }
        }

        private async Task<string> LineAsync(CancellationToken ct)
        {
            int at;
            while ((at = IndexOf("\r\n"u8)) < 0)
            {
                if (_end - _start > MaxHeader) throw new HttpProblem(400, "Line too long");
                if (!await FillAsync(ct)) throw new IOException("closed");
            }
            var s = Encoding.ASCII.GetString(_buf, _start, at - _start);
            _start = at + 2;
            return s;
        }

        private async Task<byte[]> TakeAsync(int n, CancellationToken ct)
        {
            var result = new byte[n];
            int got = 0;
            while (got < n)
            {
                if (_end == _start && !await FillAsync(ct)) throw new IOException("closed");
                int take = Math.Min(n - got, _end - _start);
                Buffer.BlockCopy(_buf, _start, result, got, take);
                _start += take;
                got += take;
            }
            return result;
        }

        private int IndexOf(ReadOnlySpan<byte> needle)
        {
            int i = _buf.AsSpan(_start, _end - _start).IndexOf(needle);
            return i < 0 ? -1 : _start + i;
        }

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            if (_end == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
            int n = await _s.ReadAsync(_buf.AsMemory(_end), ct);
            if (n <= 0) return false;
            _end += n;
            return true;
        }
    }
}
