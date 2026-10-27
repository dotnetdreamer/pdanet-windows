using System.Diagnostics;
using System.Text;

namespace PdaNetPortable.Core;

/// <summary>Runs console tools (netsh, powershell, ping...) capturing output.</summary>
public static class ProcessRunner
{
    public record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
        public string All => (StdOut + "\n" + StdErr).Trim();
    }

    public static Result Run(string file, string arguments, int timeoutMs = 20000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var p = Process.Start(psi);
            if (p == null) return new Result(-1, "", "failed to start process");

            var outSb = new StringBuilder();
            var errSb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return new Result(-2, outSb.ToString(), "timeout");
            }
            p.WaitForExit(); // flush async buffers
            return new Result(p.ExitCode, outSb.ToString(), errSb.ToString());
        }
        catch (Exception e)
        {
            return new Result(-3, "", e.Message);
        }
    }

    /// <summary>Run a netsh command.</summary>
    public static Result Netsh(string args, int timeoutMs = 20000) => Run("netsh.exe", args, timeoutMs);

    /// <summary>Run a PowerShell command (used for adapter binding toggles).</summary>
    public static Result PowerShell(string command, int timeoutMs = 30000) =>
        Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"", timeoutMs);
}
