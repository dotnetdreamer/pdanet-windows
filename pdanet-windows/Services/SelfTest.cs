using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using PdaNetPortable.Core;
using PdaNetPortable.Models;

namespace PdaNetPortable.Services;

/// <summary>
/// Headless end-to-end verification of the Windows pipeline (run via "--selftest").
/// Exercises the real ConnectionManager USB path and proves traffic flows through the
/// no-admin local proxy, then confirms teardown. No GUI, no elevation.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        Logger.Instance.EntryAdded += e => Console.WriteLine($"  [{e.Level}] {e.Message}");

        var cfg = ConfigManager.Instance.Config;
        cfg.StealthMode = true;        // force the TTL-proxy path
        cfg.UseSystemBypass = false;   // no admin / no UAC

        Console.WriteLine("==== PdaNet Windows self-test ====");
        Console.WriteLine($"Data dir     : {AppPaths.DataDir}");
        Console.WriteLine($"Local port   : {cfg.LocalProxyPort}");
        Console.WriteLine($"Stealth/sys  : {cfg.StealthMode}/{cfg.UseSystemBypass}");
        Console.WriteLine("----------------------------------");

        var cm = ConnectionManager.Instance;
        bool pass = true;

        Console.WriteLine(">> Connecting (USB)...");
        cm.Connect(ConnectionMode.Usb);
        if (!WaitFor(() => cm.State is ConnectionState.Connected or ConnectionState.Error, 45000))
        {
            Console.WriteLine("FAIL: connect timed out");
            return 1;
        }
        if (cm.State != ConnectionState.Connected)
        {
            Console.WriteLine($"FAIL: state={cm.State}  lastError={cm.LastError}");
            return 1;
        }
        Console.WriteLine($"OK  : connected  iface={cm.CurrentInterface?.Alias}  stealth={cm.StealthStatusString()}");

        // 1) proxy listening
        bool listening = TcpOpen("127.0.0.1", cfg.LocalProxyPort);
        Report(ref pass, listening, $"local proxy listening on 127.0.0.1:{cfg.LocalProxyPort}");

        // 2) WinINET proxy set (per-user, no admin)
        var (en, server) = SystemProxy.GetWinInet();
        bool winOk = en && server != null && server.Contains(cfg.LocalProxyPort.ToString());
        Report(ref pass, winOk, $"WinINET proxy enabled={en} server={server}");

        // 3) real traffic through OUR proxy to the internet (proves the TTL path carries data)
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{cfg.LocalProxyPort}"),
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var ip = http.GetStringAsync("https://api.ipify.org").GetAwaiter().GetResult().Trim();
            Report(ref pass, !string.IsNullOrWhiteSpace(ip), $"HTTPS via proxy OK, public IP = {ip}");

            var http2body = http.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt")
                                 .GetAwaiter().GetResult().Trim();
            Report(ref pass, http2body.Contains("Microsoft"), $"HTTP via proxy OK ('{http2body}')");
        }
        catch (Exception e)
        {
            Report(ref pass, false, $"request through proxy failed: {e.Message}");
        }

        // 4) stats moved
        Thread.Sleep(1500);
        Console.WriteLine($"INFO: stats down={Fmt.Bytes(StatsCollector.Instance.TotalDownloaded)} " +
                          $"up={Fmt.Bytes(StatsCollector.Instance.TotalUploaded)} uptime={Fmt.Uptime(StatsCollector.Instance.UptimeSeconds)}");

        // Disconnect + teardown checks
        Console.WriteLine(">> Disconnecting...");
        cm.Disconnect();
        WaitFor(() => cm.State == ConnectionState.Disconnected, 15000);
        Report(ref pass, cm.State == ConnectionState.Disconnected, $"disconnected (state={cm.State})");

        var (en2, _) = SystemProxy.GetWinInet();
        Report(ref pass, !en2, $"WinINET proxy cleared (enabled={en2})");
        bool still = TcpOpen("127.0.0.1", cfg.LocalProxyPort);
        Report(ref pass, !still, $"local proxy stopped (listening={still})");

        Console.WriteLine("----------------------------------");
        Console.WriteLine(pass ? "==== SELFTEST PASS ====" : "==== SELFTEST FAIL ====");
        return pass ? 0 : 2;
    }

    public static int RunAgent()
    {
        Logger.Instance.EntryAdded += e => Console.WriteLine($"  [{e.Level}] {e.Message}");
        var cfg = ConfigManager.Instance.Config;
        Console.WriteLine("==== PdaNet Windows ADB-agent self-test ====");
        Console.WriteLine($"Forward port : {cfg.LocalProxyPort}  Agent port: {cfg.AgentPort}");
        Console.WriteLine("--------------------------------------------");

        var cm = ConnectionManager.Instance;
        bool pass = true;

        Console.WriteLine(">> Connecting (ADB agent)...");
        cm.Connect(ConnectionMode.AdbAgent);
        if (!WaitFor(() => cm.State is ConnectionState.Connected or ConnectionState.Error, 45000))
        { Console.WriteLine("FAIL: connect timed out"); return 1; }
        if (cm.State != ConnectionState.Connected)
        { Console.WriteLine($"FAIL: state={cm.State}  lastError={cm.LastError}"); return 1; }
        Console.WriteLine($"OK  : connected  iface={cm.CurrentInterface?.Alias}  stealth={cm.StealthStatusString()}");

        bool listening = TcpOpen("127.0.0.1", cfg.LocalProxyPort);
        Report(ref pass, listening, $"forwarded port open on 127.0.0.1:{cfg.LocalProxyPort}");

        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{cfg.LocalProxyPort}"),
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var ip = http.GetStringAsync("https://api.ipify.org").GetAwaiter().GetResult().Trim();
            var country = http.GetStringAsync("https://ipinfo.io/country").GetAwaiter().GetResult().Trim();
            Report(ref pass, !string.IsNullOrWhiteSpace(ip), $"exit IP via phone agent = {ip} ({country})");
        }
        catch (Exception e)
        {
            Report(ref pass, false, $"request via agent failed: {e.Message}");
        }

        Console.WriteLine(">> Disconnecting...");
        cm.Disconnect();
        WaitFor(() => cm.State == ConnectionState.Disconnected, 15000);
        Report(ref pass, cm.State == ConnectionState.Disconnected, $"disconnected (state={cm.State})");
        var (en, _) = SystemProxy.GetWinInet();
        Report(ref pass, !en, $"WinINET proxy cleared (enabled={en})");

        Console.WriteLine("--------------------------------------------");
        Console.WriteLine(pass ? "==== AGENT SELFTEST PASS ====" : "==== AGENT SELFTEST FAIL ====");
        return pass ? 0 : 2;
    }

    /// <summary>
    /// Checks the app watch path without touching any Windows setting: runs a relay on a spare
    /// port, sends a few requests through it from this process, and confirms each one is recorded
    /// against this app with the right site, result and size, and that cookies stay hidden.
    /// </summary>
    public static int RunMonitor()
    {
        Logger.Instance.EntryAdded += e => Console.WriteLine($"  [{e.Level}] {e.Message}");
        var cfg = ConfigManager.Instance.Config;
        cfg.WatchApps = true; // in memory only, never saved

        int port = FreePort();

        Console.WriteLine("==== PdaNet Windows app watch self-test ====");
        Console.WriteLine($"Relay port   : {port} (the Windows proxy is not touched)");
        Console.WriteLine("--------------------------------------------");

        bool pass = true;

        // Head handling and site names, no network needed.
        var plainHead = "GET / HTTP/1.1\r\nHost: a\r\nProxy-Connection: keep-alive\r\nUpgrade-Insecure-Requests: 1\r\n\r\n";
        var closed = HttpHead.CloseAfterResponse(plainHead);
        Report(ref pass, closed.EndsWith("Connection: close\r\n\r\n") && !closed.Contains("Proxy-Connection"),
            "plain HTTP asks for one request per connection");
        var signIn = "GET / HTTP/1.1\r\nHost: intranet\r\nAuthorization: NTLM TlRMTVNTUAABAAAA\r\n\r\n";
        Report(ref pass, HttpHead.CloseAfterResponse(signIn) == signIn, "Windows sign in (NTLM) keeps its connection");
        Report(ref pass, TrafficRules.SiteOf("rr4---sn-x.googlevideo.com") == "googlevideo.com"
                         && TrafficRules.SiteOf("www.bbc.co.uk") == "bbc.co.uk" && TrafficRules.SiteOf("10.0.0.1") == "10.0.0.1",
            "site names worked out from host names");

        var monitor = TrafficMonitor.Instance;
        monitor.Clear();
        var relay = new LocalProxy(port, Constants.TtlPhoneTraffic) { Label = "Self test relay", ApplyTtl = false };
        relay.Start();

        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true,
                UseCookies = false, // send the test cookie exactly as written
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

            var req = new HttpRequestMessage(HttpMethod.Get, "http://example.com/");
            req.Headers.Add("Cookie", "selftest=secret-value");
            using (var resp = http.Send(req))
                Report(ref pass, resp.IsSuccessStatusCode, $"HTTP through the relay answered {(int)resp.StatusCode}");

            var body = http.GetStringAsync("https://example.com/").GetAwaiter().GetResult();
            Report(ref pass, body.Length > 0, $"HTTPS through the relay returned {body.Length} characters");

            try { http.GetStringAsync("http://does-not-exist.invalid/").GetAwaiter().GetResult(); }
            catch { /* expected, the relay answers 502 */ }

            // Another program on this PC must be told apart from us.
            var curl = System.IO.Path.Combine(Environment.SystemDirectory, "curl.exe");
            if (System.IO.File.Exists(curl))
            {
                var r = ProcessRunner.Run(curl, $"-s -o NUL -x http://127.0.0.1:{port} https://example.com/", 20000);
                Report(ref pass, r.Ok, "curl.exe went online through the relay");
            }
        }
        catch (Exception e)
        {
            Report(ref pass, false, $"request through the relay failed: {e.Message}");
        }

        // Disposing the client closes its pooled connections, give the relay a moment to see that.
        WaitFor(() => monitor.Snapshot(0).NewFlows.All(f => !f.IsOpen), 5000);
        var snap = monitor.Snapshot(0);
        foreach (var f in snap.NewFlows)
            Console.WriteLine($"INFO: {f.App.Name} ({f.App.Exe}, pid {f.Pid})  {f.Method} {f.Url}  " +
                              $"{f.Error ?? f.Status.ToString()}  sent {Fmt.Bytes(f.BytesUp)}  received {Fmt.Bytes(f.BytesDown)}");

        var plain = snap.NewFlows.FirstOrDefault(f => !f.Secure && f.Host == "example.com");
        Report(ref pass, plain != null && plain.App.IsSelf && plain.Pid == Environment.ProcessId,
            $"HTTP request credited to this app ({plain?.App.Name}, pid {plain?.Pid})");
        Report(ref pass, plain is { Status: 200 } && plain.BytesDown > 0 && plain.ContentType != null,
            $"HTTP response recorded (status {plain?.Status}, {plain?.ContentType}, {Fmt.Bytes(plain?.BytesDown ?? 0)})");
        Report(ref pass, plain != null && plain.RequestHead.Contains("Cookie: [hidden") && !plain.RequestHead.Contains("secret-value"),
            "cookie hidden in the recorded request");

        var tls = snap.NewFlows.FirstOrDefault(f => f.Secure && f.Host == "example.com");
        Report(ref pass, tls is { Port: 443, Status: 200 } && tls.App.IsSelf && tls.BytesDown > 0,
            $"HTTPS connection recorded by site and size only ({Fmt.Bytes(tls?.BytesDown ?? 0)} received)");

        var failed = snap.NewFlows.FirstOrDefault(f => f.Host == "does-not-exist.invalid");
        Report(ref pass, failed is { Outcome: FlowOutcome.Failed }, $"unknown site recorded as failed ({failed?.Error})");

        if (System.IO.File.Exists(System.IO.Path.Combine(Environment.SystemDirectory, "curl.exe")))
        {
            var other = snap.NewFlows.FirstOrDefault(f => f.App.Exe.Equals("curl.exe", StringComparison.OrdinalIgnoreCase));
            Report(ref pass, other != null && other.Pid != Environment.ProcessId && other.Secure,
                $"another app told apart ({other?.App.Name}, {other?.App.Exe}, pid {other?.Pid})");
        }

        var app = snap.Apps.FirstOrDefault(a => a.IsSelf);
        Report(ref pass, app is { Connections: >= 3, Sites: >= 2 },
            $"app totals: {app?.Connections} connections, {app?.Sites} sites, {Fmt.Bytes(app?.BytesTotal ?? 0)}");
        relay.Stop();

        // Phone VPN shape: the watch relay passes everything on to an upstream proxy (the phone helper
        // over adb in real use, a stand in relay here) and still records every line exactly once.
        Console.WriteLine(">> Chained like Phone VPN mode");
        monitor.Clear();
        int helperPort = FreePort(), watchPort = FreePort();
        var helper = new LocalProxy(helperPort, Constants.TtlPhoneTraffic)
            { Label = "Stand in phone helper", ApplyTtl = false, Watch = false };
        var watch = new LocalProxy(watchPort, Constants.TtlPhoneTraffic, "127.0.0.1", helperPort)
            { Label = "App watch relay", ApplyTtl = false };
        helper.Start();
        watch.Start();
        try
        {
            var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{watchPort}"), UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using (var resp = http.Send(new HttpRequestMessage(HttpMethod.Get, "http://example.com/")))
                Report(ref pass, resp.IsSuccessStatusCode, $"chained HTTP answered {(int)resp.StatusCode}");
            var body = http.GetStringAsync("https://example.com/").GetAwaiter().GetResult();
            Report(ref pass, body.Length > 0, $"chained HTTPS returned {body.Length} characters");
        }
        catch (Exception e)
        {
            Report(ref pass, false, $"request through the chained relays failed: {e.Message}");
        }

        WaitFor(() => monitor.Snapshot(0).NewFlows.All(f => !f.IsOpen), 5000);
        var chain = monitor.Snapshot(0);
        foreach (var f in chain.NewFlows)
            Console.WriteLine($"INFO: {f.App.Name}  {f.Method} {f.Url}  {f.Error ?? f.Status.ToString()}  " +
                              $"sent {Fmt.Bytes(f.BytesUp)}  received {Fmt.Bytes(f.BytesDown)}");
        Report(ref pass, chain.NewFlows.Count == 2, $"each chained request recorded once ({chain.NewFlows.Count} lines)");
        var chainedHttp = chain.NewFlows.FirstOrDefault(f => !f.Secure);
        Report(ref pass, chainedHttp is { Status: 200 } && chainedHttp.App.IsSelf && chainedHttp.BytesDown > 0,
            $"chained HTTP answer read from the site (status {chainedHttp?.Status}, {chainedHttp?.ContentType})");
        var chainedTls = chain.NewFlows.FirstOrDefault(f => f.Secure);
        Report(ref pass, chainedTls is { Status: 200 } && chainedTls.BytesDown > 0,
            $"chained HTTPS confirmed by the upstream proxy (status {chainedTls?.Status}, {Fmt.Bytes(chainedTls?.BytesDown ?? 0)} received)");
        watch.Stop();
        helper.Stop();

        // Blocking, no admin: refuse a site, refuse an app, and cut a download that is already running.
        Console.WriteLine(">> Blocking");
        var rules = TrafficRules.Instance;
        rules.UseInMemory(); // never reads or writes the saved block list
        monitor.Clear();
        int guardPort = FreePort();
        var guard = new LocalProxy(guardPort, Constants.TtlPhoneTraffic) { Label = "Self test relay", ApplyTtl = false };
        guard.Start();
        long got = 0;
        try
        {
            var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{guardPort}"), UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var siteRule = rules.BlockSite("example.com", subdomains: true);
            using (var resp = http.Send(new HttpRequestMessage(HttpMethod.Get, "http://www.example.com/")))
            {
                var page = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Report(ref pass, (int)resp.StatusCode == 403 && page.Contains("Blocked by PdaNet"),
                    $"blocked site answered {(int)resp.StatusCode} with the PdaNet page");
            }
            bool refused = false;
            try { http.GetStringAsync("https://example.com/").GetAwaiter().GetResult(); }
            catch { refused = true; }
            Report(ref pass, refused, "blocked secure site refused");
            rules.Remove(siteRule);

            var curl = System.IO.Path.Combine(Environment.SystemDirectory, "curl.exe");
            if (System.IO.File.Exists(curl))
            {
                var appRule = rules.BlockApp("curl.exe", "curl", curl);
                var r = ProcessRunner.Run(curl, $"-s -o NUL -w %{{http_code}} -x http://127.0.0.1:{guardPort} http://example.com/", 20000);
                Report(ref pass, r.StdOut.Trim() == "403", $"blocked app refused (curl got {r.StdOut.Trim()})");
                using (var resp = http.Send(new HttpRequestMessage(HttpMethod.Get, "http://example.com/")))
                    Report(ref pass, resp.IsSuccessStatusCode, $"other apps still go online ({(int)resp.StatusCode})");
                rules.Remove(appRule);
            }

            // 20 MB at most if the cut ever failed, it normally stops after half a megabyte.
            var reader = Task.Run(async () =>
            {
                try
                {
                    using var resp = await http.GetAsync("https://speed.cloudflare.com/__down?bytes=20000000",
                        HttpCompletionOption.ResponseHeadersRead);
                    await using var s = await resp.Content.ReadAsStreamAsync();
                    var buf = new byte[65536];
                    int n;
                    while ((n = await s.ReadAsync(buf)) > 0) Interlocked.Add(ref got, n);
                    return "finished";
                }
                catch (Exception e) { return e.GetType().Name; }
            });
            WaitFor(() => Interlocked.Read(ref got) > 512 * 1024 || reader.IsCompleted, 15000);
            var speedRule = rules.BlockSite("speed.cloudflare.com", subdomains: false);
            int cut = monitor.Cut(speedRule);
            bool ended = reader.Wait(5000);
            Report(ref pass, cut >= 1 && ended && reader.Result != "finished",
                $"running download cut when its site was blocked ({cut} connection, stopped after {Fmt.Bytes(Interlocked.Read(ref got))}, " +
                $"{(ended ? reader.Result : "still running")})");
            rules.Remove(speedRule);
        }
        catch (Exception e)
        {
            Report(ref pass, false, $"blocking check failed: {e.Message}");
        }

        WaitFor(() => monitor.Snapshot(0).NewFlows.All(f => !f.IsOpen), 5000);
        var blockSnap = monitor.Snapshot(0);
        foreach (var f in blockSnap.NewFlows)
            Console.WriteLine($"INFO: {f.App.Exe}  {f.Method} {f.Url}  {f.BlockedBy ?? f.Error ?? f.Status.ToString()}  " +
                              $"received {Fmt.Bytes(f.BytesDown)}");
        int blockedLines = blockSnap.NewFlows.Count(f => f.Outcome == FlowOutcome.Blocked);
        Report(ref pass, blockedLines >= 3, $"blocked and cut connections shown as blocked lines ({blockedLines})");
        guard.Stop();

        Console.WriteLine("--------------------------------------------");
        Console.WriteLine(pass ? "==== APP WATCH SELFTEST PASS ====" : "==== APP WATCH SELFTEST FAIL ====");
        return pass ? 0 : 2;
    }

    public static int RunLocation(string city)
    {
        Console.WriteLine("==== PdaNet Windows location-mask self-test ====");
        var preset = LocationMask.Find(city) ?? LocationMask.Presets[0];
        bool pass = true;

        var before = LocationMask.CurrentTzId();
        Console.WriteLine($"Before      : {before}");
        Console.WriteLine($">> Applying : {preset.Display} ({preset.TzId})");

        if (!LocationMask.Apply(preset, matchRegion: false, out var err))
        {
            Console.WriteLine($"FAIL: apply failed: {err}");
            return 1;
        }

        var masked = LocationMask.CurrentTzId();
        Report(ref pass, masked == preset.TzId, $"system zone is now '{masked}'");
        Console.WriteLine($"Masked time : {LocationMask.NowIn(preset.TzId):yyyy-MM-dd HH:mm:ss} ({preset.OffsetLabel})");

        Console.WriteLine(">> Restoring");
        LocationMask.Restore();
        var after = LocationMask.CurrentTzId();
        Report(ref pass, after == before, $"restored to '{after}'");

        Console.WriteLine(pass ? "==== LOCATION SELFTEST PASS ====" : "==== LOCATION SELFTEST FAIL ====");
        return pass ? 0 : 2;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static void Report(ref bool pass, bool ok, string msg)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")}: {msg}");
        pass &= ok;
    }

    private static bool WaitFor(Func<bool> cond, int ms)
    {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < ms)
        {
            if (cond()) return true;
            Thread.Sleep(200);
        }
        return cond();
    }

    private static bool TcpOpen(string host, int port)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync(host, port);
            return t.Wait(1500) && c.Connected;
        }
        catch { return false; }
    }
}
