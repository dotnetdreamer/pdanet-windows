using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using PdaNetPortable.Services;

namespace PdaNetPortable.Share;

/// <summary>A screen sharing session that went live, as Recent sessions on the Launchpad lists it.</summary>
internal sealed class PastShare
{
    [JsonPropertyName("peer")] public string Peer { get; set; } = "";
    // This PC viewed the other one, else the other one saw this screen.
    [JsonPropertyName("viewed")] public bool Viewed { get; set; }
    // The other PC's easy access code, kept encrypted with Windows DPAPI for this Windows user, so
    // one click connects again. Only for a PC that shares with easy access, other codes work once.
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("at")] public DateTime At { get; set; } // when it went live
    [JsonPropertyName("seconds")] public int Seconds { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }

    public DateTime Ended => At.AddSeconds(Seconds);
}

/// <summary>
/// The last screen sharing sessions, newest first, at most <see cref="Limit"/>: one for each other
/// PC and way round, so a PC used every day does not push the others out. Kept in state.json.
/// Lives on the UI thread.
/// </summary>
internal static class ShareHistory
{
    public const int Limit = 10;
    private const string Key = "share_recent";
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("pdanet recent share code");
    private static List<PastShare>? _all;

    /// <summary>The list changed.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<PastShare> All => List;

    private static List<PastShare> List =>
        _all ??= (ConfigManager.Instance.GetState<List<PastShare>>(Key) ?? new()).Take(Limit).ToList();

    /// <summary>
    /// Puts a session that ended first, in place of the last one with the same PC the same way
    /// round. <paramref name="code"/> is the other PC's easy access code, or null.
    /// </summary>
    public static void Add(string peer, bool viewed, string? code, DateTime at, TimeSpan length, string? path)
    {
        var list = List;
        // A line with a kept code is the PC with that code, whatever it is called: only the same
        // code takes its place, so two PCs with one name keep a line each and no working code is
        // lost. A line without a code has only the name to go by.
        list.RemoveAll(p => p.Viewed == viewed &&
                            (CodeOf(p) is { } kept ? kept == code : p.Peer.Equals(peer, StringComparison.OrdinalIgnoreCase)));
        list.Insert(0, new PastShare
        {
            Peer = peer,
            Viewed = viewed,
            Code = code == null ? null : Convert.ToBase64String(ProtectedData.Protect(Encoding.ASCII.GetBytes(code), Entropy, DataProtectionScope.CurrentUser)),
            At = at,
            Seconds = (int)Math.Max(0, length.TotalSeconds),
            Path = path,
        });
        if (list.Count > Limit) list.RemoveRange(Limit, list.Count - Limit);
        Save();
    }

    public static void Remove(PastShare past)
    {
        if (List.Remove(past)) Save();
    }

    /// <summary>Forgets every session, and the codes kept with them.</summary>
    public static void Clear()
    {
        if (List.Count == 0) return;
        List.Clear();
        Save();
    }

    /// <summary>The other PC's easy access code, or null when none was kept or it cannot be read.</summary>
    public static string? CodeOf(PastShare past)
    {
        if (string.IsNullOrEmpty(past.Code)) return null;
        try
        {
            var code = Encoding.ASCII.GetString(ProtectedData.Unprotect(Convert.FromBase64String(past.Code), Entropy, DataProtectionScope.CurrentUser));
            return code.Length == 10 && code.All(char.IsAsciiDigit) ? code : null;
        }
        catch { return null; } // another Windows user's, or damaged
    }

    private static void Save()
    {
        ConfigManager.Instance.SetState(Key, List);
        Changed?.Invoke();
    }
}
