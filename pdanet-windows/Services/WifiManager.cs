using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PdaNetPortable.Core;

namespace PdaNetPortable.Services;

/// <summary>
/// WiFi control via netsh wlan. Windows analogue of the NetworkManager/nmcli usage in
/// pdanet-wifi-connect (scan, connect with a generated profile, disconnect, current SSID).
/// </summary>
public static class WifiManager
{
    public static string? GetWifiInterfaceAlias()
    {
        var r = ProcessRunner.Netsh("wlan show interfaces");
        if (!r.Ok) return null;
        foreach (var line in r.StdOut.Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*Name\s*:\s*(.+?)\s*$");
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    public static string? GetCurrentSsid()
    {
        var r = ProcessRunner.Netsh("wlan show interfaces");
        if (!r.Ok) return null;
        string? state = null, ssid = null;
        foreach (var line in r.StdOut.Split('\n'))
        {
            var ms = Regex.Match(line, @"^\s*State\s*:\s*(.+?)\s*$");
            if (ms.Success) state = ms.Groups[1].Value.Trim();
            // Match "SSID" but not "BSSID"
            var m = Regex.Match(line, @"^\s*SSID\s*:\s*(.+?)\s*$");
            if (m.Success) ssid = m.Groups[1].Value.Trim();
        }
        if (!string.IsNullOrEmpty(state) && state.Equals("connected", StringComparison.OrdinalIgnoreCase))
            return ssid;
        return null;
    }

    public static List<string> ScanNetworks()
    {
        var result = new List<string>();
        var r = ProcessRunner.Netsh("wlan show networks mode=ssid");
        if (!r.Ok) return result;
        foreach (var line in r.StdOut.Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*SSID\s+\d+\s*:\s*(.+?)\s*$");
            if (m.Success)
            {
                var ssid = m.Groups[1].Value.Trim();
                if (ssid.Length > 0 && !result.Contains(ssid)) result.Add(ssid);
            }
        }
        return result;
    }

    public static bool Disconnect()
    {
        var r = ProcessRunner.Netsh("wlan disconnect");
        return r.Ok;
    }

    /// <summary>
    /// Connect to an SSID. If a password is supplied, a WPA2-PSK profile is generated and
    /// added before connecting; otherwise an existing saved profile is used.
    /// </summary>
    public static bool Connect(string ssid, string? password)
    {
        if (!string.IsNullOrEmpty(password))
        {
            var xml = BuildProfileXml(ssid, password!);
            var tmp = Path.Combine(Path.GetTempPath(), $"pdanetwin_{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(tmp, xml, new UTF8Encoding(false));
                ProcessRunner.Netsh($"wlan add profile filename=\"{tmp}\" user=current");
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        var r = ProcessRunner.Netsh($"wlan connect name=\"{ssid}\" ssid=\"{ssid}\"");
        if (!r.Ok) return false;

        // Give it a few seconds to associate, then confirm.
        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(1000);
            if (string.Equals(GetCurrentSsid(), ssid, StringComparison.Ordinal))
                return true;
        }
        return string.Equals(GetCurrentSsid(), ssid, StringComparison.Ordinal);
    }

    private static string BuildProfileXml(string ssid, string password)
    {
        string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;
        var ssidEsc = Esc(ssid);
        var pwdEsc = Esc(password);
        return $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
  <name>{ssidEsc}</name>
  <SSIDConfig>
    <SSID>
      <name>{ssidEsc}</name>
    </SSID>
  </SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>auto</connectionMode>
  <MSM>
    <security>
      <authEncryption>
        <authentication>WPA2PSK</authentication>
        <encryption>AES</encryption>
        <useOneX>false</useOneX>
      </authEncryption>
      <sharedKey>
        <keyType>passPhrase</keyType>
        <protected>false</protected>
        <keyMaterial>{pwdEsc}</keyMaterial>
      </sharedKey>
    </security>
  </MSM>
</WLANProfile>";
    }
}
