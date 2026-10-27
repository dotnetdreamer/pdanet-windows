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
            error = "adb not found. Install Android platform-tools (or set the adb path in Settings).";
            return false;
        }

        var dev = Adb("devices");
        var lines = dev.StdOut.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("List of devices")).ToList();
        if (lines.Any(l => l.EndsWith("\tdevice") || l.EndsWith(" device"))) return true;

        if (lines.Any(l => l.Contains("unauthorized")))
            error = "Device is UNAUTHORIZED — accept the 'Allow USB debugging' prompt on the phone.";
        else
            error = "No ADB device found. Enable Developer options > USB debugging and connect via USB.";
        return false;
    }

    public static bool Deploy(out string error)
    {
        error = "";
        var tmp = Path.Combine(Path.GetTempPath(), "pdanet-agent.dex");
        try
        {
            using var rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("PdaNetPortable.proxy.dex");
            if (rs == null) { error = "embedded agent dex not found in assembly"; return false; }
            using var fs = File.Create(tmp);
            rs.CopyTo(fs);
        }
        catch (Exception e) { error = "failed to extract agent dex: " + e.Message; return false; }

        KillRemote();
        var push = Adb($"push \"{tmp}\" {DexRemote}");
        if (!push.Ok) { error = "adb push failed: " + push.All; return false; }
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
            if (_agentProc == null) { error = "failed to start adb shell"; return false; }

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

            if (_agentProc.HasExited) { error = "agent process exited immediately (check USB debugging)"; return false; }
            if (!listening) Logger.Instance.Warning("Agent did not confirm 'listening'; continuing anyway");
            return true;
        }
        catch (Exception e) { error = "start agent failed: " + e.Message; return false; }
    }

    public static bool Forward(int localPort, int agentPort, out string error)
    {
        error = "";
        var r = Adb($"forward tcp:{localPort} tcp:{agentPort}");
        if (!r.Ok) { error = "adb forward failed: " + r.All; return false; }
        return true;
    }

    public static void Stop(int localPort)
    {
        try { if (_agentProc is { HasExited: false }) _agentProc.Kill(true); } catch { }
        _agentProc = null;
        try { Adb($"forward --remove tcp:{localPort}"); } catch { }
        KillRemote();
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
