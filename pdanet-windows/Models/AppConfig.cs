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
    [JsonPropertyName("window_width")] public int WindowWidth { get; set; } = 900;
    [JsonPropertyName("window_height")] public int WindowHeight { get; set; } = 620;
    [JsonPropertyName("single_instance")] public bool SingleInstance { get; set; } = true;
    [JsonPropertyName("connection_mode")] public string ConnectionMode { get; set; } = "wifi";

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
    [JsonPropertyName("agent_port")] public int AgentPort { get; set; } = 8899;   // phone-side agent listen port
    [JsonPropertyName("adb_path")] public string AdbPath { get; set; } = "";       // optional explicit adb.exe path
    [JsonPropertyName("data_warning_mb")] public int DataWarningMb { get; set; } = 10240;   // 10 GB
    [JsonPropertyName("data_critical_mb")] public int DataCriticalMb { get; set; } = 20480; // 20 GB

    public AppConfig Clone() => (AppConfig)MemberwiseClone();
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
