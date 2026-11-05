using System.Text.Json.Serialization;

namespace PdaNetPortable.Models;

/// <summary>
/// Strongly-typed settings, mirroring the upstream config.json schema
/// (config_manager.py defaults) plus a few Windows-specific keys.
/// </summary>
public class AppConfig
{
    [JsonPropertyName("auto_start")] public bool AutoStart { get; set; } = false;
    [JsonPropertyName("start_minimized")] public bool StartMinimized { get; set; } = false;
    [JsonPropertyName("auto_reconnect")] public bool AutoReconnect { get; set; } = true;
    [JsonPropertyName("reconnect_attempts")] public int ReconnectAttempts { get; set; } = 3;
    [JsonPropertyName("reconnect_delay")] public int ReconnectDelay { get; set; } = 5;
    [JsonPropertyName("stealth_mode")] public bool StealthMode { get; set; } = true;
    [JsonPropertyName("stealth_level")] public int StealthLevel { get; set; } = 3;
    [JsonPropertyName("proxy_ip")] public string ProxyIp { get; set; } = "192.168.49.1";
    [JsonPropertyName("proxy_port")] public int ProxyPort { get; set; } = 8000;
    [JsonPropertyName("connection_timeout")] public int ConnectionTimeout { get; set; } = 30;
    [JsonPropertyName("status_update_interval")] public int StatusUpdateInterval { get; set; } = 1000;
    [JsonPropertyName("enable_notifications")] public bool EnableNotifications { get; set; } = true;
    [JsonPropertyName("enable_logging")] public bool EnableLogging { get; set; } = true;
    [JsonPropertyName("log_level")] public string LogLevel { get; set; } = "INFO";
    [JsonPropertyName("theme")] public string Theme { get; set; } = "dark";
    [JsonPropertyName("window_width")] public int WindowWidth { get; set; } = 1120;
    [JsonPropertyName("window_height")] public int WindowHeight { get; set; } = 760;
    [JsonPropertyName("single_instance")] public bool SingleInstance { get; set; } = true;
    // New installs start on the recommended Phone VPN mode.
    [JsonPropertyName("connection_mode")] public string ConnectionMode { get; set; } = "adb";

    // Windows-specific
    [JsonPropertyName("use_local_proxy")] public bool UseLocalProxy { get; set; } = true;
    [JsonPropertyName("local_proxy_port")] public int LocalProxyPort { get; set; } = 12345;
    [JsonPropertyName("set_winhttp_proxy")] public bool SetWinHttpProxy { get; set; } = true;
    // When false (default), connecting still succeeds without the carrier bypass if the user
    // cannot/declines to elevate — so non-admin users can still tether (without stealth).
    [JsonPropertyName("require_bypass")] public bool RequireBypass { get; set; } = false;
    // Bypass strategy. false (default) = NO-ADMIN per-socket TTL via the local proxy (no UAC).
    // true = system-wide bypass (global TTL / IPv6 / firewall / hosts) which needs admin/UAC.
    [JsonPropertyName("use_system_bypass")] public bool UseSystemBypass { get; set; } = false;
    [JsonPropertyName("location_city")] public string LocationCity { get; set; } = "Eindhoven";
    [JsonPropertyName("location_match_region")] public bool LocationMatchRegion { get; set; } = false;
    [JsonPropertyName("location_auto_on_connect")] public bool LocationAutoOnConnect { get; set; } = false;
    [JsonPropertyName("agent_port")] public int AgentPort { get; set; } = 8899;   // phone-side agent listen port
    [JsonPropertyName("adb_path")] public string AdbPath { get; set; } = "";       // optional explicit adb.exe path
    // Phone VPN: how to reach the phone, "usb" (default), "wifi", or "other" for the phone in phone_serial.
    [JsonPropertyName("phone_link")] public string PhoneLink { get; set; } = "usb";
    [JsonPropertyName("phone_serial")] public string PhoneSerial { get; set; } = "";
    [JsonPropertyName("data_warning_mb")] public int DataWarningMb { get; set; } = 10240;   // 10 GB
    [JsonPropertyName("data_critical_mb")] public int DataCriticalMb { get; set; } = 20480; // 20 GB
    // Record which apps go online through the local relay, for the Apps page. In Phone VPN mode
    // this puts the relay in front of the adb link, so it takes effect on the next connect.
    [JsonPropertyName("watch_apps")] public bool WatchApps { get; set; } = true;

    // Screen sharing on the Launchpad. Whether the other PC may use this PC's mouse and
    // keyboard, the person can change it during a session.
    [JsonPropertyName("share_allow_control")] public bool ShareAllowControl { get; set; } = true;
    // Easy access: this PC shares its screen whenever PdaNet runs, with a code that stays the
    // same, so a PC that has the code connects without anyone here. The code is kept encrypted
    // with Windows DPAPI, for this Windows user only, and forgotten when easy access goes off.
    [JsonPropertyName("share_easy_access")] public bool ShareEasyAccess { get; set; } = false;
    [JsonPropertyName("share_easy_code")] public string? ShareEasyCode { get; set; }
    // How the easy access code stands against wrong tries, so a lockout holds when PdaNet starts again.
    [JsonPropertyName("share_easy_tries")] public ShareTries? ShareEasyTries { get; set; }
    [JsonPropertyName("share_clipboard")] public bool ShareClipboard { get; set; } = true;
    // Relays (TURN) for screen sharing, for a PC whose network blocks direct links: a Cloudflare
    // Realtime TURN key, and servers of the person's own. The token and the password are kept
    // encrypted with Windows DPAPI, for this Windows user only, see ShareRelays.
    [JsonPropertyName("share_relay_key_id")] public string ShareRelayKeyId { get; set; } = "";
    [JsonPropertyName("share_relay_key_token")] public string ShareRelayKeyToken { get; set; } = "";
    [JsonPropertyName("share_relay_urls")] public List<string> ShareRelayUrls { get; set; } = new();
    [JsonPropertyName("share_relay_username")] public string ShareRelayUsername { get; set; } = "";
    [JsonPropertyName("share_relay_password")] public string ShareRelayPassword { get; set; } = "";

    public AppConfig Clone() => (AppConfig)MemberwiseClone();
}

/// <summary>
/// How an easy access code stands against wrong tries, as the screen sharing helper reports it:
/// wrong tries this round, rounds that ended in a lockout, and when the current lockout ends.
/// </summary>
public class ShareTries
{
    [JsonPropertyName("wrong")] public int Wrong { get; set; }
    [JsonPropertyName("lockouts")] public int Lockouts { get; set; }
    [JsonPropertyName("until")] public long Until { get; set; } // Unix seconds, 0 for none
}

/// <summary>A saved connection profile (mirrors README profiles schema).</summary>
public class ConnectionProfile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "wifi";
    [JsonPropertyName("ssid")] public string? Ssid { get; set; }
    [JsonPropertyName("stealth_level")] public int StealthLevel { get; set; } = 3;
    [JsonPropertyName("created")] public string Created { get; set; } = "";

    public override string ToString() => Name;
}
