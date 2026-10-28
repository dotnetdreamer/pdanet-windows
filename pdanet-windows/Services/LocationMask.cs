using System.Globalization;
using PdaNetPortable.Core;

namespace PdaNetPortable.Services;

/// <summary>
/// A single place the app can pretend to be. The Windows time zone change needs no admin
/// (standard users hold SeTimeZonePrivilege), and region plus format culture are per user,
/// so the whole feature runs without elevation.
/// </summary>
public record LocationPreset(string City, string Country, string Iso, string TzId, string Culture)
{
    public string Display => $"{City}, {Country}";

    public TimeZoneInfo? Zone
    {
        get { try { return TimeZoneInfo.FindSystemTimeZoneById(TzId); } catch { return null; } }
    }

    public string OffsetLabel
    {
        get
        {
            var z = Zone;
            if (z == null) return "UTC";
            var off = z.GetUtcOffset(DateTime.UtcNow);
            var sign = off < TimeSpan.Zero ? "-" : "+";
            return $"UTC{sign}{Math.Abs(off.Hours):00}:{Math.Abs(off.Minutes):00}";
        }
    }

    public int GeoId
    {
        get { try { return new RegionInfo(Iso).GeoId; } catch { return 0; } }
    }
}

/// <summary>
/// Applies and reverts a <see cref="LocationPreset"/>, remembering the user's real settings
/// so they can be put back exactly. State is persisted so a restart still knows what to undo.
/// </summary>
public static class LocationMask
{
    public static readonly LocationPreset[] Presets =
    {
        new("Eindhoven", "Netherlands", "NL", "W. Europe Standard Time", "nl-NL"),
        new("Amsterdam", "Netherlands", "NL", "W. Europe Standard Time", "nl-NL"),
        new("London", "United Kingdom", "GB", "GMT Standard Time", "en-GB"),
        new("Dublin", "Ireland", "IE", "GMT Standard Time", "en-IE"),
        new("Berlin", "Germany", "DE", "W. Europe Standard Time", "de-DE"),
        new("Paris", "France", "FR", "Romance Standard Time", "fr-FR"),
        new("Madrid", "Spain", "ES", "Romance Standard Time", "es-ES"),
        new("Zurich", "Switzerland", "CH", "W. Europe Standard Time", "de-CH"),
        new("Stockholm", "Sweden", "SE", "W. Europe Standard Time", "sv-SE"),
        new("Lisbon", "Portugal", "PT", "GMT Standard Time", "pt-PT"),
        new("New York", "United States", "US", "Eastern Standard Time", "en-US"),
        new("Chicago", "United States", "US", "Central Standard Time", "en-US"),
        new("Denver", "United States", "US", "Mountain Standard Time", "en-US"),
        new("Los Angeles", "United States", "US", "Pacific Standard Time", "en-US"),
        new("Toronto", "Canada", "CA", "Eastern Standard Time", "en-CA"),
        new("Sao Paulo", "Brazil", "BR", "E. South America Standard Time", "pt-BR"),
        new("Dubai", "United Arab Emirates", "AE", "Arabian Standard Time", "ar-AE"),
        new("Istanbul", "Turkey", "TR", "Turkey Standard Time", "tr-TR"),
        new("Karachi", "Pakistan", "PK", "Pakistan Standard Time", "en-US"),
        new("Mumbai", "India", "IN", "India Standard Time", "en-IN"),
        new("Singapore", "Singapore", "SG", "Singapore Standard Time", "en-SG"),
        new("Hong Kong", "Hong Kong", "HK", "China Standard Time", "zh-HK"),
        new("Tokyo", "Japan", "JP", "Tokyo Standard Time", "ja-JP"),
        new("Sydney", "Australia", "AU", "AUS Eastern Standard Time", "en-AU"),
    };

    private static ConfigManager Cfg => ConfigManager.Instance;

    public static bool Active => Cfg.GetState<bool>("loc_active");
    public static string? ActiveCity => Cfg.GetState<string?>("loc_city");

    public static LocationPreset? Find(string? city) =>
        Presets.FirstOrDefault(p => string.Equals(p.City, city, StringComparison.OrdinalIgnoreCase));

    public static LocationPreset Default => Find(Cfg.Config.LocationCity) ?? Presets[0];

    /// <summary>The time zone id the PC would return to if the mask were turned off.</summary>
    public static string RealZoneId =>
        Active ? (Cfg.GetState<string?>("loc_orig_tz") ?? CurrentTzId()) : CurrentTzId();

    public static bool Apply(LocationPreset p, bool matchRegion, out string error)
    {
        error = "";
        try
        {
            if (!Active)
            {
                Cfg.SetState("loc_orig_tz", CurrentTzId());
                Cfg.SetState("loc_orig_geo", CurrentGeoId());
                Cfg.SetState("loc_orig_culture", CultureInfo.CurrentCulture.Name);
            }

            var r = ProcessRunner.Run("tzutil", $"/s \"{p.TzId}\"");
            if (!r.Ok) { error = "Could not set the time zone. " + r.All.Trim(); return false; }

            if (matchRegion)
            {
                ProcessRunner.PowerShell($"Set-WinHomeLocation -GeoId {p.GeoId}");
                ProcessRunner.PowerShell($"Set-Culture {p.Culture}");
            }

            Cfg.SetState("loc_active", true);
            Cfg.SetState("loc_city", p.City);
            Cfg.SetState("loc_region", matchRegion);
            Logger.Instance.Ok($"Location mask on: {p.Display} ({p.OffsetLabel})");
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    public static void Restore()
    {
        if (!Active) return;
        var tz = Cfg.GetState<string?>("loc_orig_tz");
        if (!string.IsNullOrEmpty(tz)) ProcessRunner.Run("tzutil", $"/s \"{tz}\"");

        if (Cfg.GetState<bool>("loc_region"))
        {
            var geo = Cfg.GetState<int>("loc_orig_geo");
            var cul = Cfg.GetState<string?>("loc_orig_culture");
            if (geo > 0) ProcessRunner.PowerShell($"Set-WinHomeLocation -GeoId {geo}");
            if (!string.IsNullOrEmpty(cul)) ProcessRunner.PowerShell($"Set-Culture {cul}");
        }

        Cfg.SetState("loc_active", false);
        Logger.Instance.Info("Location mask off, original settings restored");
    }

    public static string CurrentTzId()
    {
        var r = ProcessRunner.Run("tzutil", "/g");
        if (r.Ok && !string.IsNullOrWhiteSpace(r.StdOut)) return r.StdOut.Trim();
        try { return TimeZoneInfo.Local.Id; } catch { return "UTC"; }
    }

    private static int CurrentGeoId()
    {
        var r = ProcessRunner.PowerShell("(Get-WinHomeLocation).GeoId");
        return int.TryParse(r.StdOut.Trim(), out var g) ? g : 0;
    }

    /// <summary>Live local time for a zone id, computed from UTC so it ignores per-process caching.</summary>
    public static DateTime NowIn(string tzId)
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tzId)); }
        catch { return DateTime.UtcNow; }
    }

    public static string OffsetOf(string tzId)
    {
        try
        {
            var off = TimeZoneInfo.FindSystemTimeZoneById(tzId).GetUtcOffset(DateTime.UtcNow);
            var sign = off < TimeSpan.Zero ? "-" : "+";
            return $"UTC{sign}{Math.Abs(off.Hours):00}:{Math.Abs(off.Minutes):00}";
        }
        catch { return "UTC"; }
    }
}
