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
///   * chain              - we relay to an upstream HTTP proxy (the PdaNet USB proxy, or the
///                          phone helper's adb link in Phone VPN mode) instead.
///
/// In both modes every connection is handed to <see cref="TrafficMonitor"/>, which records the
/// app that opened it, where it goes and how much it moves, for the Apps page.
/// </summary>
public sealed class LocalProxy
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    public int LocalPort { get; }
    private readonly short _ttl;
    private readonly string? _upstreamProxyHost;
    private readonly int _upstreamProxyPort;

    /// <summary>Name used in the activity log.</summary>
    public string Label { get; init; } = "Stealth proxy";

    /// <summary>False when the upstream is on this PC (the phone link), where a TTL means nothing.</summary>
    public bool ApplyTtl { get; init; } = true;

    /// <summary>Whether this relay reports its connections to the Apps page.</summary>
    public bool Watch { get; init; } = true;

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
        Logger.Instance.Ok($"{Label} listening on 127.0.0.1:{LocalPort}, {mode}{(ApplyTtl ? $", TTL {_ttl}" : "")}");
        _ = AcceptLoop(_cts.Token);
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        Logger.Instance.Info($"{Label} stopped");
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
        // A plain relay (no TTL) reaches sites the way a direct connection would, IPv6 included.
        if (!ApplyTtl) return new TcpClient { NoDelay = true };

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
            TrafficFlow? flow = null;
            // Linked so one connection can be closed on its own when you block its app or site.
            using var cut = CancellationTokenSource.CreateLinkedTokenSource(token);
            var ct = cut.Token;
            try
            {
                client.NoDelay = true;
                var cs = client.GetStream();

                // Both modes read the request head first, so the Apps page can see where it goes.
                var (head, leftover) = await ReadHead(cs, ct);
                if (head == null) return;

                var firstLineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
                var firstLine = firstLineEnd >= 0 ? head.Substring(0, firstLineEnd) : head;
                var parts = firstLine.Split(' ');
                if (parts.Length < 2) return;
                var method = parts[0];
                var target = parts[1];
                var version = parts.Length > 2 ? parts[2] : "HTTP/1.1";
                bool tunnel = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);

                string host;
                int port;
                string pathAndQuery = "";
                bool parsed;
                if (tunnel)
                {
                    (host, port) = SplitHostPort(target, 443);
                    parsed = true;
                }
                else parsed = TryParseAbsoluteUri(target, out host, out port, out pathAndQuery);

                // Find the app once, for the Apps page and for your block list.
                var monitor = TrafficMonitor.Instance;
                var rules = TrafficRules.Instance;
                bool record = Watch && parsed && monitor.Enabled;
                bool enforce = Watch && rules.Any;
                if (record || enforce)
                {
                    var (pid, who) = monitor.Identify(client.Client.RemoteEndPoint, LocalPort);
                    if (record) flow = monitor.Begin(pid, who, tunnel, method, host, port, pathAndQuery, head);
                    // Before the rule check, so a rule you make from this moment on can still close it.
                    if (flow != null)
                    {
                        flow.Abort = () =>
                        {
                            try { cut.Cancel(); } catch { }
                            try { client.Client.Close(); } catch { }
                            try { upstream?.Client?.Close(); } catch { }
                        };
                    }
                    var rule = enforce ? rules.Match(who.Exe, parsed ? host : null) : null;
                    if (rule != null)
                    {
                        rules.Hit(rule);
                        flow?.Block(rule.Reason);
                        await WriteBlocked(cs, tunnel, parsed ? host : null, ct);
                        return;
                    }
                }

                // Chain mode: the client already speaks proxy protocol, pass it on to the upstream proxy.
                if (_upstreamProxyHost != null)
                {
                    upstream = NewUpstream();
                    try { await upstream.ConnectAsync(_upstreamProxyHost, _upstreamProxyPort, ct); }
                    catch (Exception e)
                    {
                        flow?.Fail(HttpHead.Reason(e));
                        await WriteAscii(cs, "HTTP/1.1 502 Bad Gateway\r\n\r\n", ct);
                        return;
                    }

                    var us = upstream.GetStream();
                    var outHead = Encoding.Latin1.GetBytes(tunnel || !parsed ? head : HttpHead.CloseAfterResponse(head));
                    await us.WriteAsync(outHead, ct);
                    if (leftover.Length > 0) await us.WriteAsync(leftover, ct);
                    if (!tunnel) flow?.AddUp(outHead.Length + leftover.Length);
                    else if (leftover.Length > 0) flow?.AddUp(leftover.Length);
                    await Relay(cs, us, flow, sniffResponse: true, ct);
                    return;
                }

                // Forward mode: dial the origin ourselves.
                if (!parsed) return;

                if (tunnel)
                {
                    upstream = NewUpstream();
                    try { await upstream.ConnectAsync(host, port, ct); }
                    catch (Exception e)
                    {
                        flow?.Fail(HttpHead.Reason(e));
                        await WriteAscii(cs, "HTTP/1.1 502 Bad Gateway\r\n\r\n", ct);
                        return;
                    }

                    await WriteAscii(cs, "HTTP/1.1 200 Connection established\r\n\r\n", ct);
                    flow?.Respond("HTTP/1.1 200 Connection established");
                    var us = upstream.GetStream();
                    if (leftover.Length > 0)
                    {
                        await us.WriteAsync(leftover, ct);
                        flow?.AddUp(leftover.Length);
                    }
                    await Relay(cs, us, flow, sniffResponse: false, ct);
                }
                else
                {
                    // Absolute-URI HTTP request (e.g. "GET http://host/path HTTP/1.1").
                    upstream = NewUpstream();
                    try { await upstream.ConnectAsync(host, port, ct); }
                    catch (Exception e)
                    {
                        flow?.Fail(HttpHead.Reason(e));
                        await WriteAscii(cs, "HTTP/1.1 502 Bad Gateway\r\n\r\n", ct);
                        return;
                    }

                    var us = upstream.GetStream();
                    var restHead = firstLineEnd >= 0 ? head.Substring(firstLineEnd + 2) : "\r\n";
                    var rewritten = HttpHead.CloseAfterResponse($"{method} {pathAndQuery} {version}\r\n{restHead}");
                    var bytes = Encoding.Latin1.GetBytes(rewritten);
                    await us.WriteAsync(bytes, ct);
                    if (leftover.Length > 0) await us.WriteAsync(leftover, ct);
                    flow?.AddUp(bytes.Length + leftover.Length);
                    await Relay(cs, us, flow, sniffResponse: true, ct);
                }
            }
            catch (Exception e) when (flow != null && e is OperationCanceledException)
            {
                flow.Fail(HttpHead.Reason(e));
            }
            catch { /* connection reset/closed */ }
            finally
            {
                upstream?.Dispose();
                TrafficMonitor.Instance.End(flow);
            }
        }
    }

    // What an app gets back for a blocked connection: a plain refusal for secure sites, and a small
    // page for plain web addresses so a browser can say why.
    private static async Task WriteBlocked(NetworkStream s, bool tunnel, string? host, CancellationToken token)
    {
        if (tunnel)
        {
            await WriteAscii(s, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token);
            return;
        }
        var site = WebUtility.HtmlEncode(host ?? "This website");
        var body = Encoding.UTF8.GetBytes(
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Blocked by PdaNet</title><style>" +
            "body{margin:0;height:100vh;display:grid;place-items:center;background:#05070a;color:#e7edf4;font-family:'Segoe UI',sans-serif}" +
            "main{max-width:460px;text-align:center;padding:24px}h1{font-size:22px;font-weight:600;color:#ff5e7b;margin:0 0 12px}" +
            "p{color:#a4b1c1;line-height:1.55;margin:6px 0}</style></head><body><main><h1>Blocked by PdaNet</h1>" +
            $"<p>{site} is blocked on this PC</p>" +
            $"<p>To allow {site} again, open PdaNet, then Apps, then Blocked</p></main></body></html>");
        await WriteAscii(s, "HTTP/1.1 403 Forbidden\r\nContent-Type: text/html; charset=utf-8\r\n" +
                            $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n", token);
        await s.WriteAsync(body, token);
    }

    private async Task Relay(NetworkStream a, NetworkStream b, TrafficFlow? flow, bool sniffResponse, CancellationToken token)
    {
        var t1 = Pump(a, b, up: true, flow, null, token);
        var t2 = Pump(b, a, up: false, flow, sniffResponse && flow != null ? new HeadSniffer() : null, token);
        await Task.WhenAny(t1, t2);
    }

    private async Task Pump(NetworkStream from, NetworkStream to, bool up, TrafficFlow? flow, HeadSniffer? sniffer,
        CancellationToken token)
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
                if (flow == null) continue;

                if (up) flow.AddUp(n);
                else flow.AddDown(n);
                if (sniffer != null && sniffer.Feed(buffer.AsSpan(0, n), out var responseHead))
                {
                    if (responseHead != null) flow.Respond(responseHead);
                    sniffer = null;
                }
            }
        }
        catch { }
    }

    private static async Task WriteAscii(NetworkStream s, string text, CancellationToken token)
    {
        var b = Encoding.ASCII.GetBytes(text);
        await s.WriteAsync(b, token);
    }

    // Latin1 maps every byte to one char and back, so a head passed on unchanged stays byte exact.
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
                    var head = Encoding.Latin1.GetString(acc.GetRange(0, i + 1).ToArray());
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
