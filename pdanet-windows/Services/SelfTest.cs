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
