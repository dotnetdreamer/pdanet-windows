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
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string BypassList = "<local>;192.168.49.1;127.*;10.*;172.16.*;172.17.*;192.168.*";

    // ---- WinINET (per-user, no elevation needed) ----
    public static void SetWinInet(string hostPort)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RegPath);
        if (key == null) return;
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", hostPort, RegistryValueKind.String);
        key.SetValue("ProxyOverride", BypassList, RegistryValueKind.String);
        NativeMethods.NotifyProxyChanged();
        Logger.Instance.Ok($"System proxy set to {hostPort}");
    }

    public static void ClearWinInet()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
        if (key == null) return;
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        try { key.DeleteValue("ProxyServer", false); } catch { }
        NativeMethods.NotifyProxyChanged();
        Logger.Instance.Info("System proxy cleared");
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
