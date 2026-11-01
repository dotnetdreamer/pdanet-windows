using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdaNetPortable.Mcp;

/// <summary>
/// The MCP stdio transport: newline-delimited JSON-RPC 2.0 over this process's stdin and
/// stdout, UTF-8, one JSON object per line. Nothing but protocol messages may go to stdout,
/// so every log line is sent to stderr (and the file logger) instead — a stray Console.Write
/// to stdout would corrupt the stream and break the client.
/// </summary>
public sealed class StdioTransport
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Compact single-line output; System.Text.Json never emits raw newlines in a value.
        WriteIndented = false,
    };

    private readonly TextReader _in;
    private readonly TextWriter _out;
    private readonly object _writeLock = new();

    public StdioTransport()
    {
        _in = new StreamReader(Console.OpenStandardInput(), Utf8, detectEncodingFromByteOrderMarks: false);
        _out = new StreamWriter(Console.OpenStandardOutput(), Utf8) { NewLine = "\n", AutoFlush = false };
    }

    /// <summary>Reads the next message, or null at end of input (the client closed the pipe).</summary>
    public async Task<JsonObject?> ReadAsync()
    {
        while (true)
        {
            var line = await _in.ReadLineAsync().ConfigureAwait(false);
            if (line == null) return null;            // EOF — time to shut down
            if (line.Length == 0) continue;           // tolerate blank keep-alive lines
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException) { return ParseErrorMarker; }
            if (node is JsonObject obj) return obj;
            return ParseErrorMarker;
        }
    }

    /// <summary>A unique sentinel meaning "the line was not valid JSON" (so the caller can reply -32700).</summary>
    public static JsonObject ParseErrorMarker { get; } = new() { ["__pda_parse_error"] = true };

    public void Write(JsonNode message)
    {
        var text = message.ToJsonString(Json);
        lock (_writeLock)
        {
            _out.Write(text);
            _out.Write('\n');
            _out.Flush();
        }
    }
}
