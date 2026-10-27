using System.Reflection;

namespace PdaNetPortable.Core;

/// <summary>
/// Application constants, ported from the upstream src/constants.py.
/// Keeps parity with pdanet-linux defaults where they are meaningful on Windows.
/// </summary>
public static class Constants
{
    // App identity
    public const string AppName = "PdaNet Windows";
    public static readonly string AppVersion = ResolveVersion();
    public const string AppDescription = "Network tethering with carrier detection bypass";

    // Version comes from the build (set by CI via -p:Version), falling back to 1.0.0 locally.
    private static string ResolveVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return "1.0.0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info.Substring(0, plus) : info;
    }

    // Connection
    public const int ConnectionTimeoutSeconds = 30;
    public const int ProxyCheckTimeoutSeconds = 5;
    public const int ReconnectDelaySeconds = 5;
    public const int RetryMaxAttempts = 3;

    // Network
    public const int TtlPhoneTraffic = 65;   // matches phone traffic (hide tethering)
    public const int TtlDesktop = 64;
    public const string ProxyDefaultIp = "192.168.49.1";
    public const int ProxyDefaultPort = 8000;
    public const int LocalProxyDefaultPort = 12345; // mirrors redsocks local_port

    // GUI update intervals (ms)
    public const int GuiUpdateIntervalMs = 1000;

    // Window
    public const int MainWindowWidth = 1120;
    public const int MainWindowHeight = 760;

    // Stealth levels
    public const int StealthBasic = 1;      // TTL only
    public const int StealthModerate = 2;   // TTL + IPv6 + DNS
    public const int StealthAggressive = 3; // all layers
    public const int DefaultStealthLevel = StealthAggressive;

    // Data usage thresholds (bytes)
    public const long DataWarningThresholdBytes = 10L * 1024 * 1024 * 1024; // 10 GB
    public const long DataCriticalThresholdBytes = 20L * 1024 * 1024 * 1024; // 20 GB

    // Quality thresholds (0-100)
    public const int QualityExcellent = 90;
    public const int QualityGood = 70;
    public const int QualityFair = 50;
    public const int QualityPoor = 30;

    // Latency thresholds (ms)
    public const int LatencyExcellentMs = 50;
    public const int LatencyGoodMs = 100;
    public const int LatencyFairMs = 200;

    // Logging
    public const long LogMaxBytes = 5 * 1024 * 1024; // 5 MB
    public const int LogBackupCount = 5;
    public const int LogBufferMaxSize = 1000;

    // Modes
    public const string ModeUsb = "usb";
    public const string ModeWifi = "wifi";
    public const string ModeIphone = "iphone";

    // OS-update / carrier-detection domains blocked by the bypass (Layer 4).
    public static readonly string[] BlockedUpdateDomains =
    {
        "windowsupdate.com",
        "update.microsoft.com",
        "dl.delivery.mp.microsoft.com",
        "sls.update.microsoft.com",
        "fe2.update.microsoft.com",
        "swcdn.apple.com",
        "mesu.apple.com",
        "archive.ubuntu.com",
        "security.ubuntu.com",
    };
}
