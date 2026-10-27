namespace PdaNetPortable.Services;

/// <summary>
/// Executes privileged operations in an elevated process. Invoked via
/// "PdaNetPortable.exe --priv &lt;op&gt; [--key value ...]". Returns process exit code
/// (0 = success) so the unelevated GUI can learn the outcome.
/// </summary>
public static class PrivilegedHelper
{
    public static int Run(string[] args)
    {
        try
        {
            var map = Parse(args);
            var op = map.GetValueOrDefault("_op", "");
            switch (op)
            {
                case "apply-bypass":
                    {
                        int level = ParseInt(map, "level", 3);
                        int ttl = ParseInt(map, "ttl", 65);
                        string? iface = map.GetValueOrDefault("iface");
                        string? gateway = map.GetValueOrDefault("gateway");
                        CarrierBypass.Enable(level, ttl, iface, gateway);
                        if (map.TryGetValue("winhttp", out var hp) && !string.IsNullOrEmpty(hp))
                            SystemProxy.SetWinHttp(hp);
                        return 0;
                    }
                case "clear-bypass":
                    {
                        string? iface = map.GetValueOrDefault("iface");
                        CarrierBypass.Disable(iface);
                        if (map.ContainsKey("winhttp"))
                            SystemProxy.ResetWinHttp();
                        return 0;
                    }
                default:
                    Console.Error.WriteLine($"Unknown privileged op: {op}");
                    return 2;
            }
        }
        catch (Exception e)
        {
            try { Logger.Instance.Error($"Privileged op failed: {e.Message}"); } catch { }
            return 1;
        }
    }

    private static int ParseInt(Dictionary<string, string> map, string key, int fallback) =>
        map.TryGetValue(key, out var s) && int.TryParse(s, out var v) ? v : fallback;

    // args = ["--priv", "apply-bypass", "--level", "3", "--iface", "Wi-Fi", ...]
    private static Dictionary<string, string> Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        // Skip leading "--priv"
        if (i < args.Length && args[i] == "--priv") i++;
        // First bare token is the op name
        if (i < args.Length && !args[i].StartsWith("--")) { map["_op"] = args[i]; i++; }

        for (; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i].TrimStart('-');
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                map[key] = args[i + 1];
                i++;
            }
            else
            {
                map[key] = "true"; // flag with no value (e.g. --winhttp)
            }
        }
        return map;
    }
}
