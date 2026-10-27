namespace PdaNetPortable.Models;

/// <summary>Connection transport, mirrors MODE_USB / MODE_WIFI / MODE_IPHONE.</summary>
public enum ConnectionMode
{
    Usb,
    Wifi,
    Iphone,
    AdbAgent
}

/// <summary>
/// Connection state machine, ported from connection_manager.ConnectionState.
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Error
}

public static class ConnectionModeExtensions
{
    public static string ToKey(this ConnectionMode mode) => mode switch
    {
        ConnectionMode.Usb => "usb",
        ConnectionMode.Wifi => "wifi",
        ConnectionMode.Iphone => "iphone",
        ConnectionMode.AdbAgent => "adb",
        _ => "wifi"
    };

    public static ConnectionMode FromKey(string? key) => key?.ToLowerInvariant() switch
    {
        "usb" => ConnectionMode.Usb,
        "iphone" => ConnectionMode.Iphone,
        "adb" => ConnectionMode.AdbAgent,
        _ => ConnectionMode.Wifi
    };

    public static string Display(this ConnectionMode mode) => mode switch
    {
        ConnectionMode.Usb => "USB cable",
        ConnectionMode.Wifi => "WiFi hotspot",
        ConnectionMode.Iphone => "iPhone hotspot",
        ConnectionMode.AdbAgent => "Phone VPN",
        _ => "WiFi hotspot"
    };
}

/// <summary>
/// Valid state transitions, ported from VALID_TRANSITIONS. Transition to Error is
/// always permitted (forced) for safety.
/// </summary>
public static class StateMachine
{
    private static readonly Dictionary<ConnectionState, HashSet<ConnectionState>> Valid = new()
    {
        [ConnectionState.Disconnected] = new() { ConnectionState.Connecting, ConnectionState.Error },
        [ConnectionState.Connecting] = new() { ConnectionState.Connected, ConnectionState.Error, ConnectionState.Disconnecting },
        [ConnectionState.Connected] = new() { ConnectionState.Disconnecting, ConnectionState.Error, ConnectionState.Disconnected },
        [ConnectionState.Disconnecting] = new() { ConnectionState.Disconnected, ConnectionState.Error },
        [ConnectionState.Error] = new() { ConnectionState.Connecting, ConnectionState.Disconnected, ConnectionState.Disconnecting },
    };

    public static bool CanTransition(ConnectionState from, ConnectionState to)
    {
        if (to == ConnectionState.Error) return true;
        return Valid.TryGetValue(from, out var set) && set.Contains(to);
    }
}
