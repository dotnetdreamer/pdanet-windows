using System.IO;
using System.Text;
using System.Text.Json;
using PdaNetPortable.Core;

namespace PdaNetPortable.Services;

/// <summary>
/// Windows implementation of the 6-layer carrier-detection bypass from
/// scripts/wifi-stealth.sh. These methods assume the process is already elevated
/// (they are invoked from PrivilegedHelper). Each layer maps a Linux
/// iptables/sysctl action onto its Windows equivalent:
///
///   L1 TTL=65       -> netsh int ipv4/ipv6 set global defaultcurhoplimit (+ DefaultTTL registry)
///   L2 IPv6 block   -> disable ms_tcpip6 binding on the tether adapter
///   L3 DNS leak      -> pin adapter DNS to the phone gateway + block public resolvers (8.8.8.8/8.8.4.4)
///   L4 OS updates    -> hosts-file sinkhole for update/telemetry domains
///   L5 MSS/MTU clamp -> lower the tether subinterface MTU
///   L6 shaping       -> reserved (no-op, mirrors upstream)
/// </summary>
public static class CarrierBypass
{
    private const string FwPrefix = "PdaNetWin_";
    private const string HostsBegin = "# >>> PdaNetWin begin >>>";
    private const string HostsEnd = "# <<< PdaNetWin end <<<";
    private static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public record BypassState(bool Active, int Level, int Ttl, string? Interface, string[] Layers, string Timestamp);

    public static void Enable(int level, int ttl, string? ifaceAlias, string? gateway)
    {
        var layers = new List<string>();
        var log = Logger.Instance;
        log.Info($"[STEALTH] Enabling carrier bypass (level {level}, ttl {ttl}, iface '{ifaceAlias}')");

        // Layer 1 — TTL normalization (always, even at level 1)
        if (SetHopLimit(ttl)) { layers.Add("TTL=" + ttl); log.Ok($"Layer 1: TTL set to {ttl}"); }

        // Layer 2 — IPv6 block
        if (level >= 2 && !string.IsNullOrEmpty(ifaceAlias))
        {
            if (SetIpv6Binding(ifaceAlias!, enabled: false)) { layers.Add("IPv6-OFF"); log.Ok("Layer 2: IPv6 disabled on adapter"); }
        }

        // Layer 3 — DNS leak prevention
        if (level >= 2 && !string.IsNullOrEmpty(ifaceAlias))
        {
            if (!string.IsNullOrEmpty(gateway))
                PinDns(ifaceAlias!, gateway!);
            BlockPublicDns(true);
            layers.Add("DNS-PINNED");
            log.Ok($"Layer 3: DNS pinned to gateway {gateway}, public resolvers blocked");
        }

        // Layer 4 — OS update / telemetry blocking
        if (level >= 3)
        {
            if (SinkholeDomains(true)) { layers.Add("UPDATES-BLOCKED"); log.Ok("Layer 4: OS update/telemetry domains sinkholed"); }
        }

        // Layer 5 — MSS/MTU clamp
        if (level >= 3 && !string.IsNullOrEmpty(ifaceAlias))
        {
            if (SetMtu(ifaceAlias!, 1420)) { layers.Add("MTU=1420"); log.Ok("Layer 5: MTU clamped to 1420"); }
        }

        // Layer 6 — traffic shaping (reserved)
        if (level >= 3) layers.Add("SHAPING-READY");

        WriteState(new BypassState(true, level, ttl, ifaceAlias, layers.ToArray(),
            DateTime.Now.ToString("o")));
        log.Ok($"[STEALTH] Carrier bypass ACTIVE ({layers.Count} layers)");
    }

    public static void Disable(string? ifaceAlias)
    {
        var log = Logger.Instance;
        log.Info("[STEALTH] Disabling carrier bypass...");

        // Restore default hop limit (Windows default is 128)
        SetHopLimit(128, restore: true);

        if (!string.IsNullOrEmpty(ifaceAlias))
        {
            SetIpv6Binding(ifaceAlias!, enabled: true);
            ResetDns(ifaceAlias!);
            SetMtu(ifaceAlias!, 1500);
        }

        BlockPublicDns(false);
        SinkholeDomains(false);
        RemoveAllFirewallRules();

        WriteState(new BypassState(false, 0, 128, ifaceAlias, Array.Empty<string>(),
            DateTime.Now.ToString("o")));
        log.Ok("[STEALTH] Carrier bypass disabled");
    }

    // ---- Layer 1 ----
    private static bool SetHopLimit(int ttl, bool restore = false)
    {
        var r1 = ProcessRunner.Netsh($"int ipv4 set global defaultcurhoplimit={ttl}");
        var r2 = ProcessRunner.Netsh($"int ipv6 set global defaultcurhoplimit={ttl}");
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters");
            if (restore) key?.DeleteValue("DefaultTTL", false);
            else key?.SetValue("DefaultTTL", ttl, Microsoft.Win32.RegistryValueKind.DWord);
            using var key6 = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters");
            if (restore) key6?.DeleteValue("DefaultTTL", false);
            else key6?.SetValue("DefaultTTL", ttl, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception e) { Logger.Instance.Warning($"TTL registry write failed: {e.Message}"); }
        return r1.Ok || r2.Ok;
    }

    // ---- Layer 2 ----
    private static bool SetIpv6Binding(string ifaceAlias, bool enabled)
    {
        var verb = enabled ? "Enable-NetAdapterBinding" : "Disable-NetAdapterBinding";
        var r = ProcessRunner.PowerShell($"{verb} -Name '{ifaceAlias.Replace("'", "''")}' -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue");
        return r.Ok;
    }

    // ---- Layer 3 ----
    private static void PinDns(string ifaceAlias, string gateway)
    {
        ProcessRunner.Netsh($"interface ipv4 set dnsservers name=\"{ifaceAlias}\" static {gateway} primary");
    }

    private static void ResetDns(string ifaceAlias)
    {
        ProcessRunner.Netsh($"interface ipv4 set dnsservers name=\"{ifaceAlias}\" source=dhcp");
    }

    private static void BlockPublicDns(bool block)
    {
        string[] dns = { "8.8.8.8", "8.8.4.4", "1.1.1.1" };
        foreach (var ip in dns)
        {
            var name = $"{FwPrefix}BlockDNS_{ip.Replace('.', '_')}";
            if (block)
                ProcessRunner.Netsh($"advfirewall firewall add rule name=\"{name}\" dir=out action=block remoteip={ip} protocol=any");
            else
                ProcessRunner.Netsh($"advfirewall firewall delete rule name=\"{name}\"");
        }
    }

    // ---- Layer 4 ----
    private static bool SinkholeDomains(bool enable)
    {
        try
        {
            var path = HostsPath;
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            // Strip any previous block first.
            text = StripHostsBlock(text);

            if (enable)
            {
                var sb = new StringBuilder();
                sb.Append(text.TrimEnd('\r', '\n'));
                sb.AppendLine();
                sb.AppendLine(HostsBegin);
                foreach (var d in Constants.BlockedUpdateDomains)
                {
                    sb.AppendLine($"0.0.0.0 {d}");
                    sb.AppendLine($"0.0.0.0 www.{d}");
                }
                sb.AppendLine(HostsEnd);
                File.WriteAllText(path, sb.ToString());
            }
            else
            {
                File.WriteAllText(path, text);
            }
            return true;
        }
        catch (Exception e)
        {
            Logger.Instance.Warning($"hosts sinkhole update failed: {e.Message}");
            return false;
        }
    }

    private static string StripHostsBlock(string text)
    {
        int b = text.IndexOf(HostsBegin, StringComparison.Ordinal);
        int e = text.IndexOf(HostsEnd, StringComparison.Ordinal);
        if (b >= 0 && e > b)
        {
            e += HostsEnd.Length;
            var before = text.Substring(0, b).TrimEnd('\r', '\n');
            var after = (e < text.Length) ? text.Substring(e) : "";
            return (before + after).TrimEnd('\r', '\n') + Environment.NewLine;
        }
        return text;
    }

    // ---- Layer 5 ----
    private static bool SetMtu(string ifaceAlias, int mtu)
    {
        var r = ProcessRunner.Netsh($"interface ipv4 set subinterface \"{ifaceAlias}\" mtu={mtu} store=persistent");
        return r.Ok;
    }

    private static void RemoveAllFirewallRules()
    {
        // Best-effort cleanup of any lingering rules created by this app.
        foreach (var ip in new[] { "8.8.8.8", "8.8.4.4", "1.1.1.1" })
            ProcessRunner.Netsh($"advfirewall firewall delete rule name=\"{FwPrefix}BlockDNS_{ip.Replace('.', '_')}\"");
    }

    // ---- State ----
    public static void WriteState(BypassState state)
    {
        try
        {
            File.WriteAllText(AppPaths.BypassState,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static BypassState? ReadState()
    {
        try
        {
            if (!File.Exists(AppPaths.BypassState)) return null;
            return JsonSerializer.Deserialize<BypassState>(File.ReadAllText(AppPaths.BypassState));
        }
        catch { return null; }
    }

    /// <summary>Read the live default hop limit (unprivileged) for status display.</summary>
    public static int? QueryCurrentTtl()
    {
        var r = ProcessRunner.Netsh("int ipv4 show global");
        if (!r.Ok) return null;
        foreach (var line in r.StdOut.Split('\n'))
        {
            if (line.Contains("Default Hop Limit", StringComparison.OrdinalIgnoreCase))
            {
                var digits = new string(line.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var v)) return v;
            }
        }
        return null;
    }
}
