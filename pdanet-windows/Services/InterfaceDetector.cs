using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using PdaNetPortable.Core;

namespace PdaNetPortable.Services;

public record TetherInterface(string Alias, string Description, string? Gateway, NetworkInterface? Nic);

/// <summary>
/// Finds the active tether network interface and probes the PdaNet proxy.
/// Windows analogue of the interface-detection + curl proxy checks in the shell scripts.
/// </summary>
public static class InterfaceDetector
{
    private static readonly string[] UsbHints =
        { "remote ndis", "rndis", "pdanet", "usb", "android", "ncm" };

    public static TetherInterface? DetectUsbInterface()
    {
        foreach (var nic in ActiveNics())
        {
            var desc = nic.Description.ToLowerInvariant();
            var gw = GetGateway(nic);
            var isPdaGw = gw != null && gw.StartsWith("192.168.49.");
            var looksUsb = UsbHints.Any(h => desc.Contains(h));
            if (isPdaGw || (looksUsb && nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211))
                return new TetherInterface(nic.Name, nic.Description, gw, nic);
        }
        return null;
    }

    public static TetherInterface? DetectWifiInterface()
    {
        foreach (var nic in ActiveNics())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                return new TetherInterface(nic.Name, nic.Description, GetGateway(nic), nic);
        }
        return null;
    }

    private static IEnumerable<NetworkInterface> ActiveNics()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel);
    }

    public static string? GetGateway(NetworkInterface nic)
    {
        try
        {
            foreach (var gw in nic.GetIPProperties().GatewayAddresses)
            {
                if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                    return gw.Address.ToString();
            }
        }
        catch { }
        return null;
    }

    public static NetworkInterface? FindByAlias(string alias) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.Name.Equals(alias, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Verify the PdaNet HTTP proxy is reachable by issuing an HTTP CONNECT, mirroring the
    /// `curl -x host:port` check in pdanet-connect.
    /// </summary>
    public static bool ProxyReachable(string ip, int port, int timeoutMs = 5000)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(ip, port);
            if (!connect.Wait(timeoutMs)) return false;
            using var stream = client.GetStream();
            stream.ReadTimeout = timeoutMs;
            stream.WriteTimeout = timeoutMs;

            var req = "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(req);
            stream.Write(bytes, 0, bytes.Length);

            var buf = new byte[256];
            int read = stream.Read(buf, 0, buf.Length);
            if (read <= 0) return false;
            var resp = Encoding.ASCII.GetString(buf, 0, read);
            return resp.Contains(" 200") || resp.Contains("Connection established");
        }
        catch { return false; }
    }

    /// <summary>
    /// General internet reachability check. Tries ICMP first, then falls back to TCP probes —
    /// many phones/tethers block ICMP while normal TCP traffic works, so ping alone is unreliable.
    /// </summary>
    public static bool InternetReachable(string host = "8.8.8.8", int timeoutMs = 3000)
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send(host, timeoutMs);
            if (reply is { Status: IPStatus.Success }) return true;
        }
        catch { }

        // ICMP failed/blocked — confirm via real TCP connectivity to well-known endpoints.
        return TcpProbe("1.1.1.1", 443, timeoutMs)
               || TcpProbe("8.8.8.8", 53, timeoutMs)
               || TcpProbe("www.msftconnecttest.com", 80, timeoutMs);
    }

    private static bool TcpProbe(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            return connect.Wait(timeoutMs) && client.Connected;
        }
        catch { return false; }
    }
}
