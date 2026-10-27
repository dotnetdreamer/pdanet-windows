using Microsoft.Win32;

namespace PdaNetPortable.Services;

/// <summary>
/// Launch-on-login via the per-user Run key (Windows analogue of the upstream
/// ~/.config/autostart/pdanet-linux.desktop entry).
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PdaNetWindows";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    public static void Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(RunKey);
            var exe = Environment.ProcessPath;
            if (key != null && exe != null)
                key.SetValue(ValueName, $"\"{exe}\" --start-minimized", RegistryValueKind.String);
        }
        catch (Exception e) { Logger.Instance.Warning($"Could not turn on start with Windows: {e.Message}"); }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
        }
        catch (Exception e) { Logger.Instance.Warning($"Could not turn off start with Windows: {e.Message}"); }
    }
}
