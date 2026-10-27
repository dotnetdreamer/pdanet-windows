namespace PdaNetPortable.Core;

/// <summary>Formatting helpers, ported from theme.Format (bandwidth / bytes / uptime).</summary>
public static class Fmt
{
    public static string Bandwidth(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:0.0} KB/s";
        return $"{bytesPerSec / (1024 * 1024):0.00} MB/s";
    }

    public static string Bytes(long v)
    {
        if (v < 1024) return $"{v} B";
        if (v < 1024L * 1024) return $"{v / 1024.0:0.0} KB";
        if (v < 1024L * 1024 * 1024) return $"{v / (1024.0 * 1024):0.0} MB";
        return $"{v / (1024.0 * 1024 * 1024):0.00} GB";
    }

    public static string Uptime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }
}
