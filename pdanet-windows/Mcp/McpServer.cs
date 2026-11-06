using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PdaNetPortable.Core;
using PdaNetPortable.Services;

namespace PdaNetPortable.Mcp;

/// <summary>
/// One logical client connection: a stdio or pipe stream, or a session of the HTTP transport. It
/// holds what the 2025 protocol revisions negotiate once (client, version, log level, resource
/// subscriptions) and the requests in flight, so a cancellation finds its request.
/// </summary>
public sealed class McpConnection
{
    private readonly CancellationTokenSource _life = new();

    public McpConnection(string transport, Action<JsonObject>? send)
    {
        Transport = transport;
        Send = send;
    }

    public string Transport { get; }
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Sends a message to the client outside a response (notifications). Null when there is no way to.</summary>
    public Action<JsonObject>? Send { get; set; }

    public JsonObject? ClientInfo { get; set; }
    public string? ProtocolVersion { get; set; }
    public string LogLevel { get; set; } = "warning";
    public AgentClient? Agent { get; set; }
    public ConcurrentDictionary<string, byte> Subscriptions { get; } = new();
    public ConcurrentDictionary<string, CancellationTokenSource> InFlight { get; } = new();
    public CancellationToken Lifetime => _life.Token;
    public bool Closed => _life.IsCancellationRequested;
    public DateTime LastUsed { get; set; } = DateTime.Now;

    public void Close()
    {
        if (_life.IsCancellationRequested) return;
        try { _life.Cancel(); } catch { }
    }
}

/// <summary>A resource the server lists, or a template of resources.</summary>
public sealed record McpResource(string Uri, string Name, string Description, string Mime, bool Template = false);

/// <summary>A prompt the server offers, with the text it expands to.</summary>
public sealed record McpPrompt(string Name, string Title, string Description, (string Name, string Description, bool Required)[] Arguments,
    Func<IReadOnlyDictionary<string, string>, string> Render);

/// <summary>
/// A hand-rolled MCP server, independent of the transport. It speaks both protocol eras:
/// <list type="bullet">
/// <item>2024-11-05 to 2025-11-25: the <c>initialize</c> handshake, then requests on that connection.</item>
/// <item>2026-07-28: stateless requests that carry the protocol version, client and capabilities in
/// <c>_meta</c>, with <c>server/discover</c> to probe the server first.</item>
/// </list>
/// Requests run concurrently: a ping or a cancellation is answered while a long tool call waits.
/// Tool handlers do the real work of driving the PdaNet browser; this class is the protocol.
/// </summary>
public sealed class McpServer
{
    public static readonly string[] ModernVersions = { "2026-07-28" };
    public static readonly string[] LegacyVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
    public const string LatestLegacy = "2025-11-25";

    private const string MetaVersion = "io.modelcontextprotocol/protocolVersion";
    private const string MetaClient = "io.modelcontextprotocol/clientInfo";
    private const string MetaCapabilities = "io.modelcontextprotocol/clientCapabilities";
    private const string MetaLogLevel = "io.modelcontextprotocol/logLevel";

    private static readonly string[] Levels = { "debug", "info", "notice", "warning", "error", "critical", "alert", "emergency" };

    private readonly IReadOnlyDictionary<string, McpTool> _tools;
    private readonly JsonArray _toolList;
    private readonly IReadOnlyList<McpResource> _resources;
    private readonly IReadOnlyDictionary<string, McpPrompt> _prompts;
    private readonly ConcurrentDictionary<string, McpConnection> _connections = new();

    public McpServer(IReadOnlyList<McpTool> tools, IReadOnlyList<McpResource> resources, IReadOnlyList<McpPrompt> prompts, AgentPresence presence)
    {
        _tools = tools.ToDictionary(t => t.Name);
        _toolList = new JsonArray();
        foreach (var t in tools) _toolList.Add(t.Describe());
        _resources = resources;
        _prompts = prompts.ToDictionary(p => p.Name);
        Presence = presence;
    }

    public AgentPresence Presence { get; }

    /// <summary>Reads a resource for an agent: (mime type, text).</summary>
    public Func<ToolCall, string, Task<(string Mime, string Text)>>? ReadResource { get; init; }

    public string Instructions { get; init; } = "";

    /// <summary>Old tool names that still work without being listed.</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new Dictionary<string, string>();

    public static IEnumerable<string> AllVersions => ModernVersions.Concat(LegacyVersions);

    private static bool IsModern(string? v) => v != null && ModernVersions.Contains(v);

    // ============================ Streams (stdio, pipe) ============================
    /// <summary>Serves one stream connection until the client closes it.</summary>
    public async Task ServeAsync(Func<CancellationToken, Task<JsonNode?>> read, Action<JsonNode> write, string transport, CancellationToken ct)
    {
        var conn = new McpConnection(transport, m => write(m));
        Open(conn);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(ct, conn.Lifetime);
        try
        {
            while (!ct.IsCancellationRequested && !conn.Closed)
            {
                JsonNode? msg;
                try { msg = await read(reading.Token); }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { Logger.Instance.Debug($"MCP read on {transport} ended: {e.Message}"); break; }
                if (msg == null) break; // the client closed the stream
                if (msg is JsonValue v && v.TryGetValue<string>(out var bad) && bad == ParseErrorMarker)
                {
                    write(Error(null, -32700, "Parse error"));
                    continue;
                }
                // Each message runs on its own, so a long tool call never holds up a ping or a cancellation.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var reply = await HandleAsync(msg, conn, conn.Send, ct);
                        if (reply != null) write(reply);
                    }
                    catch (Exception e) { Logger.Instance.Warning($"MCP message failed: {e.Message}"); }
                });
            }
        }
        finally { Close(conn); }
    }

    /// <summary>The value a reader returns for a line that is not JSON, so the server can answer -32700.</summary>
    public const string ParseErrorMarker = "\u0000pda-parse-error";

    public void Open(McpConnection conn) => _connections[conn.Id] = conn;

    public void Close(McpConnection conn)
    {
        AgentClient? agent;
        lock (conn)
        {
            conn.Close();
            agent = conn.Agent;
            conn.Agent = null;
        }
        _connections.TryRemove(conn.Id, out _);
        foreach (var cts in conn.InFlight.Values) { try { cts.Cancel(); } catch { } }
        if (agent != null) Presence.Detach(agent);
    }

    /// <summary>Drops every connection, for Stop or when access is turned off.</summary>
    public void CloseAll()
    {
        foreach (var c in _connections.Values.ToList()) Close(c);
    }

    public int ConnectionCount => _connections.Count;

    /// <summary>Tells subscribed clients that a resource changed.</summary>
    public void NotifyResource(string uri)
    {
        foreach (var c in _connections.Values)
        {
            if (c.Send == null || !c.Subscriptions.ContainsKey(uri)) continue;
            try { c.Send(Notification("notifications/resources/updated", new JsonObject { ["uri"] = uri })); }
            catch { /* the client went away */ }
        }
    }

    /// <summary>Sends a log message to every connected client that asked for messages of this level.</summary>
    public void Broadcast(string level, string text)
    {
        int at = Array.IndexOf(Levels, level);
        foreach (var c in _connections.Values)
        {
            if (c.Send == null || at < Array.IndexOf(Levels, c.LogLevel)) continue;
            try { c.Send(Notification("notifications/message", new JsonObject { ["level"] = level, ["logger"] = "pdanet-browser", ["data"] = text })); }
            catch { /* the client went away */ }
        }
    }

    // ============================ Messages ============================
    /// <summary>
    /// Handles one message (or a batch). Returns the response, or null for notifications and for
    /// requests the client cancelled. <paramref name="notify"/> carries progress and log messages
    /// for this request: the connection's stream, or an HTTP response stream.
    /// </summary>
    public async Task<JsonNode?> HandleAsync(JsonNode msg, McpConnection conn, Action<JsonObject>? notify, CancellationToken ct)
    {
        if (msg is JsonArray batch)
        {
            var replies = new JsonArray();
            foreach (var item in batch)
            {
                if (item == null) continue;
                var r = await HandleAsync(item, conn, notify, ct);
                if (r != null) replies.Add(r);
            }
            return replies.Count == 0 ? null : replies;
        }
        if (msg is not JsonObject o) return Error(null, -32600, "Invalid Request: a message must be a JSON object");
        conn.LastUsed = DateTime.Now;
        var id = o["id"];
        if (!(o["jsonrpc"] is JsonValue jv && jv.TryGetValue<string>(out var rpc) && rpc == "2.0"))
            return id == null ? null : Error(id, -32600, "Invalid Request: \"jsonrpc\" must be \"2.0\"");
        string? method = null;
        try { method = o["method"]?.GetValue<string>(); } catch { }
        if (method == null)
        {
            // A response to a request of ours (we send none), or junk.
            if (id != null && (o.ContainsKey("result") || o.ContainsKey("error"))) return null;
            return id == null ? null : Error(id, -32600, "Invalid Request: no method");
        }
        var p = o["params"] as JsonObject;
        if (id == null)
        {
            OnNotification(method, p, conn);
            return null;
        }

        var meta = p?["_meta"] as JsonObject;
        string? version = null;
        try { version = meta?[MetaVersion]?.GetValue<string>(); } catch { }
        if (version != null)
        {
            if (!AllVersions.Contains(version))
            {
                var supported = new JsonArray();
                foreach (var v in AllVersions) supported.Add(v);
                return Error(id, -32022, "Unsupported protocol version", new JsonObject { ["supported"] = supported, ["requested"] = version });
            }
            if (IsModern(version) && method != "server/discover" && meta?[MetaCapabilities] is not JsonObject)
                return Error(id, -32602, $"Invalid params: _meta is missing the required {MetaCapabilities}");
        }
        bool modern = IsModern(version ?? conn.ProtocolVersion);
        var key = id.ToJsonString();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, conn.Lifetime);
        conn.InFlight[key] = cts;
        try
        {
            var result = await DispatchAsync(method, p, meta, conn, notify, modern, cts.Token);
            if (result is JsonObject r && modern)
            {
                if (!r.ContainsKey("resultType")) r["resultType"] = "complete";
                // 2026-07-28: lists and reads say how long a client may keep them.
                if (CacheHint(method) is var (ttl, scope))
                {
                    if (!r.ContainsKey("ttlMs")) r["ttlMs"] = ttl;
                    if (!r.ContainsKey("cacheScope")) r["cacheScope"] = scope;
                }
            }
            return Result(id, result);
        }
        catch (McpProtocolException e)
        {
            return Error(id, e.Code, e.Message, e.Payload);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return null; // cancelled requests get no response
        }
        catch (Exception e)
        {
            Logger.Instance.Warning($"MCP {method} failed: {e.Message}");
            return Error(id, -32603, "Internal error: " + e.Message);
        }
        finally { conn.InFlight.TryRemove(key, out _); }
    }

    /// <summary>
    /// The caching hint a 2026-07-28 result must carry: the lists are the same for everyone for this
    /// run of PdaNet; what a resource reads is the person's browser right now, so it is never kept.
    /// </summary>
    private static (int TtlMs, string Scope)? CacheHint(string method) => method switch
    {
        "tools/list" or "prompts/list" or "resources/list" or "resources/templates/list" => (300_000, "public"),
        "resources/read" => (0, "private"),
        _ => null,
    };

    private void OnNotification(string method, JsonObject? p, McpConnection conn)
    {
        switch (method)
        {
            case "notifications/cancelled":
                var rid = p?["requestId"];
                if (rid != null && conn.InFlight.TryGetValue(rid.ToJsonString(), out var cts))
                {
                    try { cts.Cancel(); } catch { }
                }
                break;
            // notifications/initialized, roots/list_changed and others need nothing from us.
        }
    }

    private async Task<JsonNode> DispatchAsync(string method, JsonObject? p, JsonObject? meta, McpConnection conn,
        Action<JsonObject>? notify, bool modern, CancellationToken ct)
    {
        switch (method)
        {
            case "initialize":
                return Initialize(p, conn);
            case "server/discover":
                return Discover();
            case "ping":
                return new JsonObject();
            case "tools/list":
                return new JsonObject { ["tools"] = _toolList.DeepClone() };
            case "tools/call":
                return await CallToolAsync(p, meta, conn, notify, modern, ct);
            case "resources/list":
                return new JsonObject { ["resources"] = ResourceList(template: false) };
            case "resources/templates/list":
                return new JsonObject { ["resourceTemplates"] = ResourceList(template: true) };
            case "resources/read":
                return await ReadAsync(p, meta, conn, notify, modern, ct);
            case "resources/subscribe":
            case "resources/unsubscribe":
                var uri = p?["uri"]?.GetValue<string>() ?? throw new McpProtocolException(-32602, "Invalid params: uri is required");
                if (method == "resources/subscribe") conn.Subscriptions[uri] = 1;
                else conn.Subscriptions.TryRemove(uri, out _);
                return new JsonObject();
            case "prompts/list":
                return new JsonObject { ["prompts"] = PromptList() };
            case "prompts/get":
                return GetPrompt(p);
            case "logging/setLevel":
                var level = p?["level"]?.GetValue<string>();
                if (level == null || !Levels.Contains(level)) throw new McpProtocolException(-32602, "Invalid params: level must be one of " + string.Join(", ", Levels));
                conn.LogLevel = level;
                return new JsonObject();
            default:
                throw new McpProtocolException(-32601, $"Method not found: {method}");
        }
    }

    private JsonObject Initialize(JsonObject? p, McpConnection conn)
    {
        string? requested = null;
        try { requested = p?["protocolVersion"]?.GetValue<string>(); } catch { }
        var version = requested != null && LegacyVersions.Contains(requested) ? requested : LatestLegacy;
        conn.ProtocolVersion = version;
        conn.ClientInfo = p?["clientInfo"] as JsonObject;
        Logger.Instance.Info($"MCP client {Name(conn.ClientInfo) ?? "(unnamed)"} connected over {conn.Transport} (protocol {version})");
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = Capabilities(subscribe: true),
            ["serverInfo"] = ServerInfo(),
            ["instructions"] = Instructions,
        };
    }

    private JsonObject Discover()
    {
        var versions = new JsonArray();
        foreach (var v in AllVersions) versions.Add(v);
        return new JsonObject
        {
            ["resultType"] = "complete",
            ["supportedVersions"] = versions,
            // Stateless requests have no session to push resource updates on.
            ["capabilities"] = Capabilities(subscribe: false),
            ["_meta"] = new JsonObject { ["io.modelcontextprotocol/serverInfo"] = ServerInfo() },
            ["instructions"] = Instructions,
            ["ttlMs"] = 60_000,
            ["cacheScope"] = "public",
        };
    }

    private static JsonObject Capabilities(bool subscribe) => new()
    {
        ["tools"] = new JsonObject { ["listChanged"] = false },
        ["resources"] = new JsonObject { ["subscribe"] = subscribe, ["listChanged"] = false },
        ["prompts"] = new JsonObject { ["listChanged"] = false },
        ["logging"] = new JsonObject(),
    };

    private static JsonObject ServerInfo() => new()
    {
        ["name"] = "pdanet-browser",
        ["title"] = "PdaNet Browser",
        ["version"] = Constants.AppVersion,
    };

    // ============================ Tools ============================
    private async Task<JsonObject> CallToolAsync(JsonObject? p, JsonObject? meta, McpConnection conn, Action<JsonObject>? notify, bool modern, CancellationToken ct)
    {
        string? name = null;
        try { name = p?["name"]?.GetValue<string>(); } catch { }
        if (name != null && !_tools.ContainsKey(name) && Aliases.TryGetValue(name, out var current)) name = current;
        if (name == null || !_tools.TryGetValue(name, out var tool))
            throw new McpProtocolException(-32602, $"Unknown tool: {name ?? "(none)"}");
        var args = p?["arguments"] as JsonObject ?? new JsonObject();
        var auth = NewCall(name, args, meta, conn, notify, ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<ToolResult>? work = null;
        try
        {
            // Waiting for the person to allow the agent does not count against the tool's time.
            await Presence.AuthorizeAsync(auth);
            limit.CancelAfter(tool.TimeoutMs);
            work = tool.Handler(auth.With(limit.Token));
            // The limit ends the wait even for work that does not listen; that work then stops at its next step.
            return (await work.WaitAsync(limit.Token)).ToJson(modern);
        }
        catch (McpToolException e)
        {
            return ToolResult.Fail(e.Message).ToJson(modern);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (work != null) Observe(work);
            throw;
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            if (work != null) Observe(work);
            return ToolResult.Fail(Presence.Paused
                ? $"The person has paused the agents in the PdaNet browser, so {name} did not run. Try again later, or ask them to resume."
                : $"{name} did not finish within {tool.TimeoutMs / 1000} s and was stopped. The page may be stuck: try again, or open another page.").ToJson(modern);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("The action was cancelled.").ToJson(modern);
        }
        catch (Exception e)
        {
            Logger.Instance.Warning($"MCP tool {name} failed: {e}");
            return ToolResult.Fail($"{name} failed: {e.Message}").ToJson(modern);
        }
    }

    private ToolCall NewCall(string tool, JsonObject args, JsonObject? meta, McpConnection conn, Action<JsonObject>? notify, CancellationToken ct)
    {
        var client = meta?[MetaClient] as JsonObject ?? conn.ClientInfo;
        var agent = Presence.Client(Name(client), Title(client), conn.Transport);
        if (conn.Send != null)
        {
            lock (conn)
            {
                if (conn.Agent == null && !conn.Closed)
                {
                    conn.Agent = agent;
                    Presence.Attach(agent);
                }
            }
        }
        var token = meta?["progressToken"];
        Action<string, double?, double?>? progress = null;
        if (token != null && notify != null)
        {
            var tokenCopy = token.DeepClone();
            double last = 0;
            var gate = new object();
            progress = (message, value, total) =>
            {
                double v;
                lock (gate)
                {
                    v = value is double d && d > last ? d : last + 1; // progress only ever rises
                    last = v;
                }
                var pp = new JsonObject { ["progressToken"] = tokenCopy.DeepClone(), ["progress"] = v, ["message"] = message };
                if (total is double t) pp["total"] = Math.Max(t, v);
                notify(Notification("notifications/progress", pp));
            };
        }
        return new ToolCall { Tool = tool, Args = args, Ct = ct, Agent = agent, ProgressSink = progress };
    }

    // ============================ Resources and prompts ============================
    private JsonArray ResourceList(bool template)
    {
        var arr = new JsonArray();
        foreach (var r in _resources.Where(r => r.Template == template))
        {
            var o = new JsonObject
            {
                [template ? "uriTemplate" : "uri"] = r.Uri,
                ["name"] = r.Name,
                ["description"] = r.Description,
                ["mimeType"] = r.Mime,
            };
            arr.Add(o);
        }
        return arr;
    }

    private async Task<JsonObject> ReadAsync(JsonObject? p, JsonObject? meta, McpConnection conn, Action<JsonObject>? notify, bool modern, CancellationToken ct)
    {
        var uri = p?["uri"]?.GetValue<string>() ?? throw new McpProtocolException(-32602, "Invalid params: uri is required");
        // 2026-07-28 says "invalid params" for a resource that does not exist; earlier versions had their own code.
        int notFound = modern ? -32602 : -32002;
        if (ReadResource == null || !uri.StartsWith("browser://"))
            throw new McpProtocolException(notFound, "Resource not found", new JsonObject { ["uri"] = uri });
        var call = NewCall("resources/read", new JsonObject(), meta, conn, notify, ct);
        try
        {
            await Presence.AuthorizeAsync(call);
            var (mime, text) = await ReadResource(call, uri);
            return new JsonObject
            {
                ["contents"] = new JsonArray(new JsonObject { ["uri"] = uri, ["mimeType"] = mime, ["text"] = text }),
            };
        }
        catch (McpToolException e) when (e.Message.StartsWith("Unknown resource"))
        {
            throw new McpProtocolException(notFound, "Resource not found", new JsonObject { ["uri"] = uri });
        }
        catch (McpToolException e)
        {
            throw new McpProtocolException(-32603, e.Message);
        }
    }

    private JsonArray PromptList()
    {
        var arr = new JsonArray();
        foreach (var p in _prompts.Values)
        {
            var args = new JsonArray();
            foreach (var (n, d, r) in p.Arguments) args.Add(new JsonObject { ["name"] = n, ["description"] = d, ["required"] = r });
            arr.Add(new JsonObject { ["name"] = p.Name, ["title"] = p.Title, ["description"] = p.Description, ["arguments"] = args });
        }
        return arr;
    }

    private JsonObject GetPrompt(JsonObject? p)
    {
        var name = p?["name"]?.GetValue<string>() ?? throw new McpProtocolException(-32602, "Invalid params: name is required");
        if (!_prompts.TryGetValue(name, out var prompt)) throw new McpProtocolException(-32602, $"Unknown prompt: {name}");
        var args = new Dictionary<string, string>();
        if (p?["arguments"] is JsonObject a)
            foreach (var (k, v) in a) args[k] = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v?.ToJsonString() ?? "";
        foreach (var (n, _, required) in prompt.Arguments)
            if (required && (!args.TryGetValue(n, out var val) || string.IsNullOrWhiteSpace(val)))
                throw new McpProtocolException(-32602, $"Missing required argument: {n}");
        return new JsonObject
        {
            ["description"] = prompt.Description,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = prompt.Render(args) },
            }),
        };
    }

    private static void Observe(Task t) => t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);

    // ============================ JSON-RPC ============================
    private static string? Name(JsonObject? client)
    {
        try { return client?["name"]?.GetValue<string>(); } catch { return null; }
    }

    private static string? Title(JsonObject? client)
    {
        try { return client?["title"]?.GetValue<string>(); } catch { return null; }
    }

    public static JsonObject Notification(string method, JsonObject p) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method,
        ["params"] = p,
    };

    private static JsonObject Result(JsonNode id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result,
    };

    public static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var e = new JsonObject { ["code"] = code, ["message"] = message };
        if (data != null) e["data"] = data.DeepClone();
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = e };
    }
}

/// <summary>A JSON-RPC error the server answers with (unknown method, bad params, unknown resource).</summary>
public sealed class McpProtocolException : Exception
{
    public McpProtocolException(int code, string message, JsonNode? payload = null) : base(message)
    {
        Code = code;
        Payload = payload;
    }

    public int Code { get; }
    public JsonNode? Payload { get; }
}
