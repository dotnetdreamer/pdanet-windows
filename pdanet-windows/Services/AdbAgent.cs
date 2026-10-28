using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using PdaNetPortable.Core;

namespace PdaNetPortable.Services;

/// <summary>
/// Drives the phone-side agent over ADB (the robust "option 2" path).
///
/// Flow: push the embedded dex (ProxyMain) to the device, launch it with `app_process`
/// (runs as the shell user, so the phone's VPN captures it), `adb forward` a PC port to the
/// agent, and point Windows at that port. No native tethering, no mobile-data toggle, no PC
/// admin. The agent re-originates connections on the phone, so tethered PC traffic inherits
/// whatever the phone uses — including an active VPN.
/// </summary>
public static class AdbAgent
{
    private const string DexRemote = "/data/local/tmp/pdanet-agent.dex";
    private const string AgentClass = "ProxyMain";

    public static string? AdbPath { get; private set; }
    private static Process? _agentProc;
    private static readonly object _extractLock = new();

    public enum PhoneState { NoAdb, NotFound, Unauthorized, Offline, Ready }

    /// <summary>
    /// Read only check used by the Connect page to show whether the phone is ready.
    /// Lists devices and returns the best state seen plus the phone model when known.
    /// </summary>
    public static (PhoneState State, string? Model) Probe(string? configured)
    {
        var adb = FindAdb(configured);
        if (adb == null) return (PhoneState.NoAdb, null);

        var r = ProcessRunner.Run(adb, "devices -l", 10000);
        var best = PhoneState.NotFound;
        foreach (var line in r.StdOut.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length == 0 || l.StartsWith("List of devices") || l.StartsWith("*")) continue;
            var tok = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 2) continue;
            switch (tok[1])
            {
                case "device":
                    var model = tok.FirstOrDefault(t => t.StartsWith("model:"))?.Substring(6).Replace('_', ' ');
                    return (PhoneState.Ready, model);
                case "unauthorized":
                    best = PhoneState.Unauthorized;
                    break;
                case "offline":
                    if (best == PhoneState.NotFound) best = PhoneState.Offline;
                    break;
            }
        }
        return (best, null);
    }

    public static string? FindAdb(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        // Prefer the bundled adb so the app is fully portable (no separate install).
        var bundled = ExtractBundledAdb();
        if (bundled != null) return bundled;

        var onPath = ProcessRunner.Run("where", "adb");
        if (onPath.Ok)
        {
            var hit = onPath.StdOut.Split('\n').Select(s => s.Trim())
                .FirstOrDefault(s => s.EndsWith("adb.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(s));
            if (hit != null) return hit;
        }
        foreach (var p in new[]
                 {
                     @"C:\platform-tools\adb.exe",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Android", "Sdk", "platform-tools", "adb.exe"),
                 })
            if (File.Exists(p)) return p;
        return null;
    }

    private static ProcessRunner.Result Adb(string args, int timeout = 30000)
        => ProcessRunner.Run(AdbPath ?? "adb", args, timeout);

    /// <summary>Extract the embedded adb.exe + its DLLs to the app data dir and return its path.</summary>
    private static string? ExtractBundledAdb()
    {
        lock (_extractLock) return ExtractBundledAdbLocked();
    }

    private static string? ExtractBundledAdbLocked()
    {
        try
        {
            var dir = Path.Combine(AppPaths.DataDir, "adb");
            Directory.CreateDirectory(dir);
            var asm = Assembly.GetExecutingAssembly();
            var files = new[]
            {
                ("PdaNetPortable.adb.adb.exe", "adb.exe"),
                ("PdaNetPortable.adb.AdbWinApi.dll", "AdbWinApi.dll"),
                ("PdaNetPortable.adb.AdbWinUsbApi.dll", "AdbWinUsbApi.dll"),
            };
            foreach (var (res, name) in files)
            {
                var dst = Path.Combine(dir, name);
                if (File.Exists(dst) && new FileInfo(dst).Length > 0) continue; // keep (may be in use)
                using var rs = asm.GetManifestResourceStream(res);
                if (rs == null) return null;
                using var fs = File.Create(dst);
                rs.CopyTo(fs);
            }
            var adb = Path.Combine(dir, "adb.exe");
            return File.Exists(adb) ? adb : null;
        }
        catch { return null; }
    }

    public static bool Init(string? configured, out string error)
    {
        error = "";
        AdbPath = FindAdb(configured);
        if (AdbPath == null)
        {
            error = "The adb helper is missing, set a custom adb program in Settings, Advanced";
            return false;
        }

        var dev = Adb("devices");
        var lines = dev.StdOut.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("List of devices")).ToList();
        if (lines.Any(l => l.EndsWith("\tdevice") || l.EndsWith(" device"))) return true;

        if (lines.Any(l => l.Contains("unauthorized")))
            error = "Your phone has not allowed this PC yet, unlock it and tap Allow on the USB debugging prompt";
        else
            error = "No phone found, plug it in with a USB cable and turn on USB debugging";
        return false;
    }

    public static bool Deploy(out string error)
    {
        error = "";
        var tmp = Path.Combine(Path.GetTempPath(), "pdanet-agent.dex");
        try
        {
            using var rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("PdaNetPortable.proxy.dex");
            if (rs == null) { error = "The phone helper is missing from this app build"; return false; }
            using var fs = File.Create(tmp);
            rs.CopyTo(fs);
        }
        catch (Exception e) { error = "Could not unpack the phone helper: " + e.Message; return false; }

        KillRemote();
        var push = Adb($"push \"{tmp}\" {DexRemote}");
        if (!push.Ok) { error = "Could not copy the helper to your phone: " + push.All; return false; }
        return true;
    }

    public static bool StartAgent(int agentPort, out string error)
    {
        error = "";
        try
        {
            // The agent lives for as long as this adb-shell process does; we kill it on disconnect.
            var psi = new ProcessStartInfo
            {
                FileName = AdbPath ?? "adb",
                Arguments = $"shell CLASSPATH={DexRemote} app_process /system/bin {AgentClass} {agentPort}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            _agentProc = Process.Start(psi);
            if (_agentProc == null) { error = "Could not start adb"; return false; }

            bool listening = false;
            _agentProc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Logger.Instance.Debug("[agent] " + e.Data);
                if (e.Data.Contains("listening")) listening = true;
            };
            _agentProc.ErrorDataReceived += (_, e) => { if (e.Data != null) Logger.Instance.Debug("[agent] " + e.Data); };
            _agentProc.BeginOutputReadLine();
            _agentProc.BeginErrorReadLine();

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 7000 && !listening && !_agentProc.HasExited)
                Thread.Sleep(200);

            if (_agentProc.HasExited) { error = "The phone helper stopped right away, check that USB debugging is still on"; return false; }
            if (!listening) Logger.Instance.Warning("The phone helper did not confirm it is ready, continuing anyway");
            return true;
        }
        catch (Exception e) { error = "Could not start the phone helper: " + e.Message; return false; }
    }

    public static bool Forward(int localPort, int agentPort, out string error)
    {
        error = "";
        var r = Adb($"forward tcp:{localPort} tcp:{agentPort}");
        if (!r.Ok) { error = "Could not link this PC to the phone: " + r.All; return false; }
        return true;
    }

    public static void Stop(int localPort)
    {
        try { if (_agentProc is { HasExited: false }) _agentProc.Kill(true); } catch { }
        _agentProc = null;
        RemoveForward(localPort);
        KillRemote();
    }

    public static void RemoveForward(int localPort)
    {
        try { Adb($"forward --remove tcp:{localPort}"); } catch { }
    }

    /// <summary>Kill any lingering agent on the device (parse ps, avoid pkill/awk which vary).</summary>
    private static void KillRemote()
    {
        try
        {
            var ps = Adb("shell ps -A -o PID,ARGS");
            if (!ps.Ok) ps = Adb("shell ps -A");
            foreach (var line in ps.StdOut.Split('\n'))
            {
                if (!line.Contains(AgentClass) || line.Contains("grep")) continue;
                var tok = line.TrimStart().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length > 0 && int.TryParse(tok[0], out var pid))
                    Adb($"shell kill {pid}");
            }
        }
        catch { }
    }
}
