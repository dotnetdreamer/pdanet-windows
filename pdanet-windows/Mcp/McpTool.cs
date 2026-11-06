using System.Text.Json.Nodes;

namespace PdaNetPortable.Mcp;

/// <summary>A piece of a tool result: a line of text, or an image for screenshots.</summary>
public sealed class ToolContent
{
    private readonly JsonObject _node;
    private ToolContent(JsonObject node) => _node = node;

    public static ToolContent Text(string text) =>
        new(new JsonObject { ["type"] = "text", ["text"] = text });

    public static ToolContent Image(byte[] data, string mime = "image/png") =>
        new(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(data), ["mimeType"] = mime });

    public JsonObject ToJson() => (JsonObject)_node.DeepClone();
}

/// <summary>
/// What a tool handler returns: content blocks, optional structured content that matches the
/// tool's output schema, and whether the call failed.
/// </summary>
public sealed class ToolResult
{
    public List<ToolContent> Content { get; } = new();
    public JsonObject? Structured { get; set; }
    public bool IsError { get; init; }

    public static ToolResult Ok(string text) => new ToolResult().With(ToolContent.Text(text));
    public static ToolResult Fail(string text) => new ToolResult { IsError = true }.With(ToolContent.Text(text));

    public static ToolResult Ok(string text, JsonObject structured) =>
        new ToolResult { Structured = structured }.With(ToolContent.Text(text));

    public ToolResult With(ToolContent c) { Content.Add(c); return this; }

    public JsonObject ToJson(bool modern)
    {
        var arr = new JsonArray();
        foreach (var c in Content) arr.Add(c.ToJson());
        var obj = new JsonObject { ["content"] = arr };
        if (Structured != null && !IsError) obj["structuredContent"] = Structured.DeepClone();
        if (IsError) obj["isError"] = true;
        if (modern) obj["resultType"] = "complete";
        return obj;
    }
}

/// <summary>
/// Thrown by a tool handler for an expected, explainable failure (no page open yet, a handle that
/// is gone, a bad argument). The server turns it into a normal tool result with isError=true and
/// this message, so the model reads it and adjusts rather than the whole call erroring out.
/// </summary>
public sealed class McpToolException : Exception
{
    public McpToolException(string message) : base(message) { }
}

/// <summary>
/// Everything a tool handler gets for one call: the arguments, the cancellation of the request,
/// a way to report progress, and the agent that called.
/// </summary>
public sealed class ToolCall
{
    public required string Tool { get; init; }
    public required JsonObject Args { get; init; }
    public required CancellationToken Ct { get; init; }
    public required AgentClient Agent { get; init; }
    /// <summary>Sends progress for the request; it keeps the numbers rising, as the protocol asks.</summary>
    public Action<string, double?, double?>? ProgressSink { get; init; }

    /// <summary>The same call with another cancellation, such as the tool's time limit.</summary>
    public ToolCall With(CancellationToken ct) => new() { Tool = Tool, Args = Args, Ct = ct, Agent = Agent, ProgressSink = ProgressSink };

    /// <summary>Tells the client the call is still working, which also keeps its timeouts from firing.</summary>
    public void Progress(string message, double? progress = null, double? total = null)
    {
        try { ProgressSink?.Invoke(message, progress, total); } catch { /* the client went away */ }
    }

    // ============================ Arguments ============================
    public string Str(string key) =>
        OptStr(key) ?? throw new McpToolException($"Missing required argument '{key}'.");

    public string? OptStr(string key)
    {
        var n = Args[key];
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s.Length == 0 ? null : s;
            if (v.TryGetValue<double>(out var d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        }
        return null;
    }

    public bool Bool(string key, bool fallback = false)
    {
        if (Args[key] is not JsonValue v) return fallback;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p)) return p;
        return fallback;
    }

    public bool? OptBool(string key) => Args[key] is JsonValue ? Bool(key) : null;

    public int Int(string key, int fallback) => (int)Math.Round(Num(key) ?? fallback);

    public double? Num(string key)
    {
        if (Args[key] is not JsonValue v) return null;
        // Parsed JSON converts to any number type; a value built in code converts only to its own type.
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public List<string> StrList(string key, bool required = true)
    {
        var list = new List<string>();
        if (Args[key] is JsonArray arr)
            foreach (var n in arr)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) list.Add(s);
        if (Args[key] is JsonValue one && one.TryGetValue<string>(out var single) && single.Length > 0) list.Add(single);
        if (required && list.Count == 0) throw new McpToolException($"'{key}' must be a non-empty list of strings.");
        return list;
    }

    public JsonArray Array(string key) =>
        Args[key] as JsonArray ?? throw new McpToolException($"'{key}' must be a list.");
}

/// <summary>
/// One MCP tool: its name, the hints a client shows, its JSON Schema for arguments (and for
/// structured results), the behaviour annotations from the spec, and the handler that runs it.
/// </summary>
public sealed class McpTool
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required JsonObject InputSchema { get; init; }
    public JsonObject? OutputSchema { get; init; }
    public required Func<ToolCall, Task<ToolResult>> Handler { get; init; }

    /// <summary>How long a call may run before the server gives up on it.</summary>
    public int TimeoutMs { get; init; } = 120_000;

    // ToolAnnotations (hints, per the MCP schema). Defaults match the spec defaults.
    public bool ReadOnly { get; init; }
    public bool Destructive { get; init; }
    public bool Idempotent { get; init; }
    public bool OpenWorld { get; init; } = true;

    public JsonObject Describe()
    {
        var o = new JsonObject
        {
            ["name"] = Name,
            ["title"] = Title,
            ["description"] = Description,
            ["inputSchema"] = InputSchema.DeepClone(),
        };
        if (OutputSchema != null) o["outputSchema"] = OutputSchema.DeepClone();
        o["annotations"] = new JsonObject
        {
            ["title"] = Title,
            ["readOnlyHint"] = ReadOnly,
            ["destructiveHint"] = Destructive,
            ["idempotentHint"] = Idempotent,
            ["openWorldHint"] = OpenWorld,
        };
        return o;
    }
}

/// <summary>Small builders for the JSON Schema of each tool's arguments and results.</summary>
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

    /// <summary>An object schema for results: extra keys allowed, so the schema can grow without breaking clients.</summary>
    public static JsonObject Result(params (string Name, JsonObject Spec, bool Required)[] props)
    {
        var o = Object(props);
        o.Remove("additionalProperties");
        return o;
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

    public static JsonObject Num(string desc) => new() { ["type"] = "number", ["description"] = desc };

    public static JsonObject StrArray(string desc) =>
        new() { ["type"] = "array", ["description"] = desc, ["items"] = new JsonObject { ["type"] = "string" } };

    public static JsonObject ArrayOf(string desc, JsonObject item) =>
        new() { ["type"] = "array", ["description"] = desc, ["items"] = item };

    /// <summary>A value that may be a number or a word, such as a zoom of 1.5 or "in".</summary>
    public static JsonObject NumOrStr(string desc) =>
        new() { ["type"] = new JsonArray("number", "string"), ["description"] = desc };
}
