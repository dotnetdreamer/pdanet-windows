namespace PdaNetPortable.Core;

/// <summary>Formatting helpers, ported from theme.Format (bandwidth / bytes / uptime).</summary>
public static class Fmt
{
    public static string Bandwidth(double bytesPerSec)
    {
        var (value, unit) = BandwidthParts(bytesPerSec);
        return $"{value} {unit}";
    }

    public static string Bytes(long v)
    {
        var (value, unit) = BytesParts(v);
        return $"{value} {unit}";
    }

    /// <summary>Bandwidth split into number and unit so the UI can style them separately.</summary>
    public static (string Value, string Unit) BandwidthParts(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return ($"{bytesPerSec:0}", "B/s");
        if (bytesPerSec < 1024 * 1024) return ($"{bytesPerSec / 1024:0.0}", "KB/s");
        return ($"{bytesPerSec / (1024 * 1024):0.00}", "MB/s");
    }

    /// <summary>Byte count split into number and unit so the UI can style them separately.</summary>
    public static (string Value, string Unit) BytesParts(long v)
    {
        if (v < 1024) return ($"{v}", "B");
        if (v < 1024L * 1024) return ($"{v / 1024.0:0.0}", "KB");
        if (v < 1024L * 1024 * 1024) return ($"{v / (1024.0 * 1024):0.0}", "MB");
        return ($"{v / (1024.0 * 1024 * 1024):0.00}", "GB");
    }

    /// <summary>A size given in megabytes, shown in GB once it is large enough.</summary>
    public static string Megabytes(long mb) => mb >= 1024 ? $"{mb / 1024.0:0.#} GB" : $"{mb} MB";

    public static string Uptime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }
}
