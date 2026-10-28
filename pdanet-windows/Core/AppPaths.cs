using System.IO;

namespace PdaNetPortable.Core;

/// <summary>
/// Resolves where the app stores its data. Portable-first: a "data" folder next to
/// the executable (so settings travel with the .exe). Falls back to %APPDATA% when the
/// exe directory is not writable (e.g. run from a read-only location).
/// Mirrors the upstream ~/.config/pdanet-linux/ directory of files.
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; }
    public static bool IsPortable { get; }

    static AppPaths()
    {
        // For a single-file publish, AppContext.BaseDirectory points at a temp extraction
        // folder, so use the real executable path instead.
        string exeDir;
        try
        {
            exeDir = Path.GetDirectoryName(Environment.ProcessPath!) ?? AppContext.BaseDirectory;
        }
        catch
        {
            exeDir = AppContext.BaseDirectory;
        }

        var portable = Path.Combine(exeDir, "data");
        try
        {
            Directory.CreateDirectory(portable);
            var probe = Path.Combine(portable, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            DataDir = portable;
            IsPortable = true;
        }
        catch
        {
            DataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "pdanet-windows");
            Directory.CreateDirectory(DataDir);
            IsPortable = false;
        }
    }

    public static string Config => Path.Combine(DataDir, "config.json");
    public static string Profiles => Path.Combine(DataDir, "profiles.json");
    public static string State => Path.Combine(DataDir, "state.json");
    public static string WifiNetworks => Path.Combine(DataDir, "wifi_networks.json");
    public static string ConnectionHistory => Path.Combine(DataDir, "connection_history.json");
    public static string BypassState => Path.Combine(DataDir, "bypass_state.json");
    public static string LogFile => Path.Combine(DataDir, "pdanet.log");
    public static string AppRules => Path.Combine(DataDir, "app_rules.json");
}
