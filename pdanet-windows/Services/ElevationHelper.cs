using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace PdaNetPortable.Services;

/// <summary>
/// On-demand privilege elevation — the Windows equivalent of the project's pkexec/sudo
/// model. The GUI runs unelevated; privileged network changes are performed by re-launching
/// this same executable with "--priv ..." through a UAC prompt (ShellExecute "runas").
/// </summary>
public static class ElevationHelper
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public enum ElevationResult { Success, Failed, Cancelled }

    /// <summary>
    /// Runs the given privileged operation, elevating if necessary. Returns the outcome.
    /// When already elevated, executes in-process; otherwise spawns an elevated copy.
    /// </summary>
    public static ElevationResult RunPrivileged(string argLine)
    {
        if (IsElevated())
        {
            // Should not usually happen (GUI is unelevated) but handle gracefully.
            var code = PrivilegedHelper.Run(SplitArgs(argLine));
            return code == 0 ? ElevationResult.Success : ElevationResult.Failed;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                Arguments = argLine,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p == null) return ElevationResult.Failed;
            p.WaitForExit();
            return p.ExitCode == 0 ? ElevationResult.Success : ElevationResult.Failed;
        }
        catch (Win32Exception w) when (w.NativeErrorCode == 1223)
        {
            // User declined the UAC prompt.
            Logger.Instance.Warning("Elevation cancelled by user");
            return ElevationResult.Cancelled;
        }
        catch (Exception e)
        {
            Logger.Instance.Error($"Elevation failed: {e.Message}");
            return ElevationResult.Failed;
        }
    }

    private static string[] SplitArgs(string line)
    {
        // Minimal splitter honoring double quotes.
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (ch == ' ' && !inQuotes) { if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); } }
            else cur.Append(ch);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result.ToArray();
    }
}
