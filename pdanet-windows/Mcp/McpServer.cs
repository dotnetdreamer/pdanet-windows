using System.Text.Json;
using System.Text.Json.Nodes;
using PdaNetPortable.Core;
using PdaNetPortable.Services;

namespace PdaNetPortable.Mcp;

/// <summary>
/// A hand-rolled MCP server over stdio. It implements the JSON-RPC surface that today's
/// clients use — the <c>initialize</c> handshake (2024-11-05 … 2025-11-25), <c>tools/list</c>
/// and <c>tools/call</c>, plus <c>ping</c> — and, as a forward-compatible bonus, the stateless
/// <c>server/discover</c> RPC from 2026-07-28. Tool handlers do the real work of driving the
/// PdaNet browser; this class is just the protocol.
/// </summary>
public sealed class McpServer
{
    // Protocol revisions this server understands, newest first. We echo back the client's
    // requested version when we know it, otherwise our preferred one (the list head).
    private static readonly string[] Supported = { "2025-06-18", "2025-11-25", "2025-03-26", "2024-11-05" };
    private const string Preferred = "2025-06-18";

    private const string Instructions =
        "Drive the PdaNet browser — a real, visible Chromium window — to accomplish web tasks. " +
        "Start with browser_navigate, then call browser_snapshot to see the page as an accessibility " +
        "tree where every actionable element has a [ref=eN] handle. Pass those refs to browser_click, " +
        "browser_type, browser_hover and browser_select_option. Snapshots go stale after the page " +
        "changes, so re-snapshot (most actions already return a fresh one) before using a ref again. " +
        "Use browser_read for article text, browser_screenshot to see pixels, and browser_network to " +
        "check whether traffic is flowing through the tethered phone.";

    private readonly StdioTransport _transport;
    private readonly IReadOnlyDictionary<string, McpTool> _tools;
    private readonly JsonArray _toolList;
    private readonly Action _shutdown;

    public McpServer(StdioTransport transport, IReadOnlyList<McpTool> tools, Action shutdown)
    {
        _transport = transport;
        _tools = tools.ToDictionary(t => t.Name);
        _toolList = new JsonArray();
        foreach (var t in tools) _toolList.Add(t.Describe());
        _shutdown = shutdown;
    }

    /// <summary>Reads and answers messages until the client closes stdin.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Logger.Instance.Info($"MCP server ready over stdio, {_tools.Count} browser tools");
        while (!ct.IsCancellationRequested)
        {
            JsonObject? msg;
            try { msg = await _transport.ReadAsync(); }
            catch (Exception e) { Logger.Instance.Error($"MCP read failed: {e.Message}"); break; }
            if (msg == null) break; // EOF

            if (msg.ContainsKey("__pda_parse_error"))
            {
                _transport.Write(Error(null, -32700, "Parse error"));
                continue;
            }

            try { await HandleAsync(msg, ct); }
            catch (Exception e)
            {
                Logger.Instance.Error($"MCP handler crashed: {e.Message}");
                _transport.Write(Error(msg["id"], -32603, "Internal error: " + e.Message));
            }
        }
        Logger.Instance.Info("MCP client disconnected");
    }

    private async Task HandleAsync(JsonObject msg, CancellationToken ct)
    {
        var id = msg["id"];                      // absent/null => notification
        var method = msg["method"]?.GetValue<string>();
        var @params = msg["params"] as JsonObject;

        if (method == null)
        {
            if (id != null) _transport.Write(Error(id, -32600, "Invalid request: no method"));
            return;
        }

        // Notifications carry no id and get no reply.
        if (id == null)
        {
            if (method == "notifications/cancelled" || method.StartsWith("notifications/"))
                return;
            return;
        }

        switch (method)
        {
            case "initialize":
                _transport.Write(Result(id, Initialize(@params)));
                break;

            case "server/discover":
                _transport.Write(Result(id, Discover()));
                break;

            case "ping":
                _transport.Write(Result(id, new JsonObject()));
                break;

            case "tools/list":
                _transport.Write(Result(id, new JsonObject { ["tools"] = (JsonArray)_toolList.DeepClone() }));
                break;

            case "tools/call":
                _transport.Write(Result(id, await CallToolAsync(@params, ct)));
                break;

            case "resources/list":
                _transport.Write(Result(id, new JsonObject { ["resources"] = new JsonArray() }));
                break;

            case "prompts/list":
                _transport.Write(Result(id, new JsonObject { ["prompts"] = new JsonArray() }));
                break;

            default:
                _transport.Write(Error(id, -32601, $"Method not found: {method}"));
                break;
        }
    }

    private JsonObject Initialize(JsonObject? @params)
    {
        var requested = @params?["protocolVersion"]?.GetValue<string>();
        var version = requested != null && Supported.Contains(requested) ? requested : Preferred;
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "pdanet-browser",
                ["title"] = "PdaNet Browser",
                ["version"] = Constants.AppVersion,
            },
            ["instructions"] = Instructions,
        };
    }

    private JsonObject Discover()
    {
        var versions = new JsonArray();
        foreach (var v in Supported) versions.Add(v);
        return new JsonObject
        {
            ["resultType"] = "complete",
            ["supportedVersions"] = versions,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "pdanet-browser", ["version"] = Constants.AppVersion },
            ["instructions"] = Instructions,
            ["ttlMs"] = 0,
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject? @params, CancellationToken ct)
    {
        var name = @params?["name"]?.GetValue<string>();
        if (name == null || !_tools.TryGetValue(name, out var tool))
            return ToolResult.Fail($"Unknown tool: {name}").ToJson();

        var args = @params?["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            return (await tool.Handler(args, ct)).ToJson();
        }
        catch (McpToolException e)
        {
            return ToolResult.Fail(e.Message).ToJson();
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("The action timed out.").ToJson();
        }
        catch (Exception e)
        {
            Logger.Instance.Warning($"MCP tool {name} failed: {e.Message}");
            return ToolResult.Fail($"{name} failed: {e.Message}").ToJson();
        }
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
