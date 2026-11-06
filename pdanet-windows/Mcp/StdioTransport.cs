using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace PdaNetPortable.Mcp;

/// <summary>
/// The MCP stream transport: newline-delimited JSON-RPC over a pair of streams, UTF-8, one JSON
/// message per line. It carries stdio (the transport every MCP client launches) and the named pipe
/// a running PdaNet listens on. On stdio nothing but protocol messages may go to stdout, so every
/// log line goes to stderr and the file log instead: a stray Console.Write would break the client.
/// </summary>
public sealed class LineChannel
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly StreamReader _in;
    private readonly Stream _out;
    private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private volatile bool _broken;

    public LineChannel(Stream input, Stream output)
    {
        _in = new StreamReader(input, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
        _out = output;
        _writer = Task.Run(WriteLoopAsync);
    }

    public static LineChannel Stdio() => new(Console.OpenStandardInput(), Console.OpenStandardOutput());

    /// <summary>Raised once when writing fails, which means the other side went away.</summary>
    public event Action? Broken;

    /// <summary>The next message, null at the end of the input, or <see cref="McpServer.ParseErrorMarker"/> for a line that is not JSON.</summary>
    public async Task<JsonNode?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await _in.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) return null;
            if (string.IsNullOrWhiteSpace(line)) continue; // tolerate blank keep-alive lines
            try
            {
                var node = JsonNode.Parse(line);
                if (node is JsonObject or JsonArray) return node;
            }
            catch (JsonException) { }
            return JsonValue.Create(McpServer.ParseErrorMarker);
        }
    }

    /// <summary>
    /// Queues a message. One writer sends the queue in order, so a caller (the UI thread with a progress
    /// note, say) never waits on a client that reads slowly or not at all.
    /// </summary>
    public void Write(JsonNode message)
    {
        if (_broken) return;
        _queue.Writer.TryWrite(Utf8.GetBytes(message.ToJsonString(Json) + "\n"));
    }

    /// <summary>Sends what is queued, then stops writing. Waits a little at most.</summary>
    public async Task CloseAsync()
    {
        _queue.Writer.TryComplete();
        try { await _writer.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var bytes in _queue.Reader.ReadAllAsync())
            {
                await _out.WriteAsync(bytes);
                await _out.FlushAsync();
            }
        }
        catch (Exception)
        {
            _broken = true;
            _queue.Writer.TryComplete();
            Broken?.Invoke();
        }
    }
}
