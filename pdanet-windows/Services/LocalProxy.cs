using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PdaNetPortable.Services;

/// <summary>
/// Local HTTP/HTTPS forward proxy used to implement the carrier-detection bypass
/// WITHOUT administrator rights.
///
/// The crucial trick: every connection we open to the real destination has its
/// per-socket IP TTL set to 65 (SocketOptionName.IpTimeToLive — a normal-user option).
/// Those packets leave the PC with TTL 65, the phone forwards them (TTL -> 64), and the
/// carrier sees a value that matches normal phone traffic instead of the desktop default
/// of 128. Pointing Windows at this proxy (WinINET) is also a per-user action, so the whole
/// path needs no UAC/elevation.
///
/// Two modes:
///   * forward (default)  - we parse CONNECT / absolute-URI HTTP and dial the origin directly.
///   * chain              - we relay to an upstream HTTP proxy (the PdaNet USB proxy) instead,
///                          still applying TTL on the socket to the phone.
/// </summary>
public sealed class LocalProxy
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    public int LocalPort { get; }
    private readonly short _ttl;
    private readonly string? _upstreamProxyHost;
    private readonly int _upstreamProxyPort;

    public long BytesUp;   // client -> internet
    public long BytesDown; // internet -> client
    public bool Running { get; private set; }

    public LocalProxy(int localPort, int ttl, string? upstreamProxyHost = null, int upstreamProxyPort = 0)
    {
        LocalPort = localPort;
        _ttl = (short)Math.Clamp(ttl, 1, 255);
        _upstreamProxyHost = upstreamProxyHost;
        _upstreamProxyPort = upstreamProxyPort;
    }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, LocalPort);
        _listener.Start();
        Running = true;
        var mode = _upstreamProxyHost != null ? $"through {_upstreamProxyHost}:{_upstreamProxyPort}" : "direct";
        Logger.Instance.Ok($"Stealth proxy listening on 127.0.0.1:{LocalPort}, {mode}, TTL {_ttl}");
        _ = AcceptLoop(_cts.Token);
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        Logger.Instance.Info("Stealth proxy stopped");
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token); }
            catch { break; }
            _ = HandleClient(client, token);
        }
    }

    private TcpClient NewUpstream()
    {
        // IPv4-only upstream: applies TTL and avoids IPv6 leaks through the proxied path.
        var c = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try { c.Client.Ttl = _ttl; } catch { }
        return c;
    }

    private async Task HandleClient(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            TcpClient? upstream = null;
            try
            {
                client.NoDelay = true;
                var cs = client.GetStream();

                // Chain mode: client already speaks proxy protocol; just relay to the upstream proxy.
                if (_upstreamProxyHost != null)
                {
                    upstream = NewUpstream();
                    await upstream.ConnectAsync(_upstreamProxyHost, _upstreamProxyPort, token);
                    await Relay(cs, upstream.GetStream(), token);
                    return;
                }

                // Forward mode: read the request head and dial the origin ourselves.
                var (head, leftover) = await ReadHead(cs, token);
                if (head == null) return;

                var firstLineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
                var firstLine = firstLineEnd >= 0 ? head.Substring(0, firstLineEnd) : head;
                var parts = firstLine.Split(' ');
                if (parts.Length < 2) return;
                var method = parts[0];
                var target = parts[1];
                var version = parts.Length > 2 ? parts[2] : "HTTP/1.1";

                if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    var (host, port) = SplitHostPort(target, 443);
                    upstream = NewUpstream();
                    try { await upstream.ConnectAsync(host, port, token); }
                    catch { await WriteAscii(cs, "HTTP/1.1 502 Bad Gateway\r\n\r\n", token); return; }

                    await WriteAscii(cs, "HTTP/1.1 200 Connection established\r\n\r\n", token);
                    var us = upstream.GetStream();
                    if (leftover.Length > 0) await us.WriteAsync(leftover, token);
                    await Relay(cs, us, token);
                }
                else
                {
                    // Absolute-URI HTTP request (e.g. "GET http://host/path HTTP/1.1").
                    if (!TryParseAbsoluteUri(target, out var host, out var port, out var pathAndQuery))
                        return;
                    upstream = NewUpstream();
                    try { await upstream.ConnectAsync(host, port, token); }
                    catch { await WriteAscii(cs, "HTTP/1.1 502 Bad Gateway\r\n\r\n", token); return; }

                    var us = upstream.GetStream();
                    var restHead = firstLineEnd >= 0 ? head.Substring(firstLineEnd + 2) : "\r\n";
                    var rewritten = $"{method} {pathAndQuery} {version}\r\n{restHead}";
                    await us.WriteAsync(Encoding.ASCII.GetBytes(rewritten), token);
                    if (leftover.Length > 0) await us.WriteAsync(leftover, token);
                    await Relay(cs, us, token);
                }
            }
            catch { /* connection reset/closed */ }
            finally { upstream?.Dispose(); }
        }
    }

    private async Task Relay(NetworkStream a, NetworkStream b, CancellationToken token)
    {
        var t1 = Pump(a, b, up: true, token);
        var t2 = Pump(b, a, up: false, token);
        await Task.WhenAny(t1, t2);
    }

    private async Task Pump(NetworkStream from, NetworkStream to, bool up, CancellationToken token)
    {
        var buffer = new byte[65536];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer, token)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), token);
                if (up) Interlocked.Add(ref BytesUp, n);
                else Interlocked.Add(ref BytesDown, n);
            }
        }
        catch { }
    }

    private static async Task WriteAscii(NetworkStream s, string text, CancellationToken token)
    {
        var b = Encoding.ASCII.GetBytes(text);
        await s.WriteAsync(b, token);
    }

    private static async Task<(string? head, byte[] leftover)> ReadHead(NetworkStream s, CancellationToken token)
    {
        var acc = new List<byte>(8192);
        var buf = new byte[8192];
        while (acc.Count < 65536)
        {
            int n;
            try { n = await s.ReadAsync(buf, token); }
            catch { break; }
            if (n <= 0) break;
            for (int i = 0; i < n; i++) acc.Add(buf[i]);

            // Look for the end of headers: CR LF CR LF
            for (int i = 3; i < acc.Count; i++)
            {
                if (acc[i - 3] == 13 && acc[i - 2] == 10 && acc[i - 1] == 13 && acc[i] == 10)
                {
                    var head = Encoding.ASCII.GetString(acc.GetRange(0, i + 1).ToArray());
                    var leftover = acc.GetRange(i + 1, acc.Count - (i + 1)).ToArray();
                    return (head, leftover);
                }
            }
        }
        return (null, Array.Empty<byte>());
    }

    private static (string host, int port) SplitHostPort(string target, int defaultPort)
    {
        var idx = target.LastIndexOf(':');
        if (idx > 0 && int.TryParse(target.Substring(idx + 1), out var p))
            return (target.Substring(0, idx), p);
        return (target, defaultPort);
    }

    private static bool TryParseAbsoluteUri(string target, out string host, out int port, out string pathAndQuery)
    {
        host = ""; port = 80; pathAndQuery = "/";
        try
        {
            if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
            var uri = new Uri(target);
            host = uri.Host;
            port = uri.IsDefaultPort ? 80 : uri.Port;
            pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            return true;
        }
        catch { return false; }
    }
}
