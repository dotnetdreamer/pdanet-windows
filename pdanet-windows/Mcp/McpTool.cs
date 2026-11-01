using System.Text.Json.Nodes;

namespace PdaNetPortable.Mcp;

/// <summary>A piece of a tool result: a line of text, or a PNG image for screenshots.</summary>
public sealed class ToolContent
{
    private readonly JsonObject _node;
    private ToolContent(JsonObject node) => _node = node;

    public static ToolContent Text(string text) =>
        new(new JsonObject { ["type"] = "text", ["text"] = text });

    public static ToolContent Image(byte[] png, string mime = "image/png") =>
        new(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = mime });

    public JsonObject ToJson() => (JsonObject)_node.DeepClone();
}

/// <summary>What a tool handler returns: content blocks plus whether the call failed.</summary>
public sealed class ToolResult
{
    public List<ToolContent> Content { get; } = new();
    public bool IsError { get; init; }

    public static ToolResult Ok(string text) => new ToolResult { }.With(ToolContent.Text(text));
    public static ToolResult Fail(string text) => new ToolResult { IsError = true }.With(ToolContent.Text(text));

    public ToolResult With(ToolContent c) { Content.Add(c); return this; }

    public JsonObject ToJson()
    {
        var arr = new JsonArray();
        foreach (var c in Content) arr.Add(c.ToJson());
        var obj = new JsonObject { ["content"] = arr };
        if (IsError) obj["isError"] = true;
        return obj;
    }
}

/// <summary>
/// Thrown by a tool handler for an expected, explainable failure (no page open yet, a stale
/// element handle, a bad argument). The server turns it into a normal tool result with
/// isError=true and this message — the model reads it and adjusts, rather than the whole
/// JSON-RPC call erroring out.
/// </summary>
public sealed class McpToolException : Exception
{
    public McpToolException(string message) : base(message) { }
}

/// <summary>
/// One MCP tool: its name, the hints a client shows, its JSON Schema for arguments, the
/// behaviour annotations from the spec, and the handler that runs it.
/// </summary>
public sealed class McpTool
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required JsonObject InputSchema { get; init; }
    public required Func<JsonObject, CancellationToken, Task<ToolResult>> Handler { get; init; }

    // ToolAnnotations (hints, per the MCP schema). Defaults match the spec defaults.
    public bool ReadOnly { get; init; }
    public bool Destructive { get; init; }
    public bool Idempotent { get; init; }
    public bool OpenWorld { get; init; } = true;

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["name"] = Name,
            ["title"] = Title,
            ["description"] = Description,
            ["inputSchema"] = (JsonObject)InputSchema.DeepClone(),
            ["annotations"] = new JsonObject
            {
                ["title"] = Title,
                ["readOnlyHint"] = ReadOnly,
                ["destructiveHint"] = Destructive,
                ["idempotentHint"] = Idempotent,
                ["openWorldHint"] = OpenWorld,
            },
        };
    }
}

/// <summary>Small builders for the JSON Schema of each tool's arguments.</summary>
public static class Schema
{
    public static JsonObject Object(params (string Name, JsonObject Spec, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, spec, req) in props)
        {
            properties[name] = spec;
            if (req) required.Add(name);
        }
        var obj = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Count > 0) obj["required"] = required;
        return obj;
    }

    public static JsonObject Str(string desc) => new() { ["type"] = "string", ["description"] = desc };

    public static JsonObject Str(string desc, params string[] options)
    {
        var o = Str(desc);
        var en = new JsonArray();
        foreach (var v in options) en.Add(v);
        o["enum"] = en;
        return o;
    }

    public static JsonObject Bool(string desc) => new() { ["type"] = "boolean", ["description"] = desc };

    public static JsonObject Int(string desc) => new() { ["type"] = "integer", ["description"] = desc };

    public static JsonObject StrArray(string desc) =>
        new() { ["type"] = "array", ["description"] = desc, ["items"] = new JsonObject { ["type"] = "string" } };
}
