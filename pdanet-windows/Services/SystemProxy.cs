using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Win32;
using PdaNetPortable.Core;
using PdaNetPortable.Interop;

namespace PdaNetPortable.Services;

/// <summary>
/// Configures the Windows system HTTP proxy. This is the Windows analogue of the
/// iptables REDSOCKS redirect: instead of transparently rerouting packets, we point
/// WinINET (per-user, no admin) and optionally WinHTTP (machine-wide, admin) at our
/// local relay so applications send traffic through the PdaNet proxy.
/// </summary>
public static class SystemProxy
{
    internal const string BypassList = "<local>;192.168.49.1;127.*;10.*;172.16.*;172.17.*;192.168.*";

    // The per-user WinINET key, a scratch key during the proxy self test.
    internal static string RegPath { get; set; } = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    // The user's own WinINET values, saved before PdaNet first points Windows at itself and put back
    // on disconnect. Kept on disk, so a crash or a killed app is repaired at the next start.
    internal static string SavedFile { get; set; } = Path.Combine(AppPaths.DataDir, "proxy_saved.json");

    private sealed record Saved(int? Enable, string? Server, string? Override);

    // ---- WinINET (per-user, no elevation needed) ----
    /// <summary>
    /// Points Windows at PdaNet. The user's own values are saved first, and when they cannot be
    /// saved nothing changes and the answer is false, so the values can always be put back.
    /// </summary>
    public static bool SetWinInet(string hostPort)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RegPath);
        if (key == null) return false;
        if (!File.Exists(SavedFile) && !Save(key)) return false;
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", hostPort, RegistryValueKind.String);
        key.SetValue("ProxyOverride", BypassList, RegistryValueKind.String);
        NativeMethods.NotifyProxyChanged();
        Logger.Instance.Ok($"Windows proxy set to {hostPort}");
        return true;
    }

    /// <summary>
    /// Puts back the values saved before PdaNet pointed Windows at itself. With nothing saved,
    /// PdaNet did not change the proxy, so nothing is touched.
    /// </summary>
    public static void ClearWinInet()
    {
        var saved = Load();
        if (saved == null) return;
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
        if (key == null) return;
        Put(key, "ProxyEnable", saved.Enable, RegistryValueKind.DWord);
        Put(key, "ProxyServer", saved.Server, RegistryValueKind.String);
        Put(key, "ProxyOverride", saved.Override, RegistryValueKind.String);
        try { File.Delete(SavedFile); } catch { }
        NativeMethods.NotifyProxyChanged();
        Logger.Instance.Info("Windows proxy put back as before");
    }

    /// <summary>
    /// At start, puts back the user's values if PdaNet stopped without disconnecting last time.
    /// Older builds saved nothing, so their leftover is turned off when PdaNet's own address and
    /// PdaNet's own bypass list are both still in place and nothing listens on that port.
    /// </summary>
    public static void RepairLeftover()
    {
        // A second PdaNet window on the same data folder may still be online, its relay then still listens.
        int port = ConfigManager.Instance.Config.LocalProxyPort;
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port)) return;
        if (File.Exists(SavedFile))
        {
            ClearWinInet();
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
        if (key == null || !IsOurLeftover(key)) return;
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        try { key.DeleteValue("ProxyServer", false); } catch { }
        try { key.DeleteValue("ProxyOverride", false); } catch { }
        NativeMethods.NotifyProxyChanged();
        Logger.Instance.Info("Turned off a Windows proxy that an older PdaNet left behind");
    }

    private static bool Save(RegistryKey key)
    {
        // PdaNet's own leftover from an older build is not the user's setting, keep the proxy off afterwards.
        var saved = IsOurLeftover(key)
            ? new Saved(0, null, null)
            : new Saved(key.GetValue("ProxyEnable") as int?, key.GetValue("ProxyServer") as string,
                        key.GetValue("ProxyOverride") as string);
        try
        {
            File.WriteAllText(SavedFile, JsonSerializer.Serialize(saved));
            return true;
        }
        catch (Exception e)
        {
            Logger.Instance.Warning($"Could not write {SavedFile}: {e.Message}");
            return false;
        }
    }

    private static Saved? Load()
    {
        try { return File.Exists(SavedFile) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavedFile)) : null; }
        catch
        {
            // An unreadable file cannot be put back, drop the file so the next connect saves afresh.
            try { File.Delete(SavedFile); } catch { }
            return null;
        }
    }

    private static void Put(RegistryKey key, string name, object? value, RegistryValueKind kind)
    {
        if (value == null) { try { key.DeleteValue(name, false); } catch { } }
        else key.SetValue(name, value, kind);
    }

    // PdaNet always writes its address and its bypass list together, a proxy the user set at the
    // same address would not carry PdaNet's exact bypass list as well.
    private static bool IsOurLeftover(RegistryKey key) =>
        key.GetValue("ProxyServer") as string == $"127.0.0.1:{ConfigManager.Instance.Config.LocalProxyPort}"
        && key.GetValue("ProxyOverride") as string == BypassList;

    /// <summary>The three WinINET values PdaNet changes, for the self tests to compare before and after.</summary>
    public static (int? Enable, string? Server, string? Bypass) Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: false);
        return (key?.GetValue("ProxyEnable") as int?, key?.GetValue("ProxyServer") as string,
                key?.GetValue("ProxyOverride") as string);
    }

    public static (bool enabled, string? server) GetWinInet()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: false);
            if (key == null) return (false, null);
            var enabled = (key.GetValue("ProxyEnable") as int?) == 1;
            var server = key.GetValue("ProxyServer") as string;
            return (enabled, server);
        }
        catch { return (false, null); }
    }

    // ---- WinHTTP (machine-wide, requires elevation; called from PrivilegedHelper) ----
    public static bool SetWinHttp(string hostPort)
    {
        var r = ProcessRunner.Netsh($"winhttp set proxy proxy-server=\"{hostPort}\" bypass-list=\"{BypassList}\"");
        return r.Ok;
    }

    public static bool ResetWinHttp()
    {
        var r = ProcessRunner.Netsh("winhttp reset proxy");
        return r.Ok;
    }
}
