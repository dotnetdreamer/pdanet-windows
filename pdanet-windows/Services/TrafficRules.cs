using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using PdaNetPortable.Core;

namespace PdaNetPortable.Services;

public enum BlockKind { App, Site }

/// <summary>
/// One thing you blocked: a whole app, or a site for every app or for one app. Apps are matched
/// by their program file name, so a rule keeps working after the app updates itself.
/// </summary>
public sealed class BlockRule
{
    [JsonPropertyName("kind"), JsonConverter(typeof(JsonStringEnumConverter))]
    public BlockKind Kind { get; set; }

    /// <summary>App rules: the program file. Site rules: only this program, or null for every app.</summary>
    [JsonPropertyName("exe")] public string? Exe { get; set; }
    [JsonPropertyName("app_name")] public string? AppName { get; set; }
    [JsonPropertyName("app_path")] public string? AppPath { get; set; }

    /// <summary>Site rules: the host name, with <see cref="Subdomains"/> also every name under it.</summary>
    [JsonPropertyName("site")] public string? Site { get; set; }
    [JsonPropertyName("subdomains")] public bool Subdomains { get; set; }
    [JsonPropertyName("created")] public DateTime Created { get; set; }

    /// <summary>Connections refused by this rule since the app started (not saved).</summary>
    [JsonIgnore] public int Hits => Volatile.Read(ref _hits);
    internal int _hits;

    public bool Matches(string exe, string? host)
    {
        if (Kind == BlockKind.App) return !string.IsNullOrEmpty(Exe) && exe.Equals(Exe, StringComparison.OrdinalIgnoreCase);
        if (host == null || string.IsNullOrEmpty(Site)) return false;
        if (Exe != null && !exe.Equals(Exe, StringComparison.OrdinalIgnoreCase)) return false;
        return host.Equals(Site, StringComparison.OrdinalIgnoreCase)
               || (Subdomains && host.EndsWith("." + Site, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Short name for messages, like "Spotify" or "googlevideo.com in Microsoft Edge".</summary>
    [JsonIgnore]
    public string Label => Kind == BlockKind.App ? AppName ?? Exe ?? "an app"
        : Exe == null ? Site ?? "a website" : $"{Site} in {AppName ?? Exe}";

    /// <summary>Why a connection was stopped, shown on the line.</summary>
    [JsonIgnore]
    public string Reason => Kind == BlockKind.App ? $"You blocked {Label}"
        : $"You blocked {(Subdomains ? "all of " : "")}{Site}{(Exe == null ? "" : $" in {AppName ?? Exe}")}";

    /// <summary>What the block covers, in plain words.</summary>
    [JsonIgnore]
    public string Scope => Kind == BlockKind.App ? "Whole app"
        : (Subdomains ? "Whole website" : "Only this address")
          + (Exe == null ? ", every app" : $", in {AppName ?? Exe} only");
}

/// <summary>
/// Your block list. PdaNet's relay checks it before it opens any connection and refuses the
/// ones that match, so blocking needs no admin rights. It only covers apps whose traffic goes
/// through PdaNet: in Phone VPN mode that is everything that uses the phone's data. Saved next
/// to the settings and applied again on every connect.
/// </summary>
public sealed class TrafficRules
{
    private static readonly Lazy<TrafficRules> _instance = new(() => new TrafficRules());
    public static TrafficRules Instance => _instance.Value;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private List<BlockRule> _rules;
    private volatile bool _any;
    private bool _persist = true;

    private TrafficRules()
    {
        _rules = Load();
        _any = _rules.Count > 0;
    }

    public bool Any => _any;

    public IReadOnlyList<BlockRule> All
    {
        get { lock (_lock) return _rules.ToList(); }
    }

    /// <summary>The first rule that blocks this app talking to this host, or null.</summary>
    public BlockRule? Match(string exe, string? host)
    {
        // An app PdaNet could not name has an empty program name: only site rules for every app reach it.
        if (!_any) return null;
        lock (_lock)
        {
            // An app rule wins, so the line says the whole app is blocked.
            return _rules.FirstOrDefault(r => r.Kind == BlockKind.App && r.Matches(exe, host))
                   ?? _rules.FirstOrDefault(r => r.Matches(exe, host));
        }
    }

    public BlockRule? AppRule(string exe)
    {
        if (!_any || string.IsNullOrEmpty(exe)) return null;
        lock (_lock) return _rules.FirstOrDefault(r => r.Kind == BlockKind.App && r.Matches(exe, null));
    }

    /// <summary>Site rules that cover this host for this app (or for every app).</summary>
    public List<BlockRule> SiteRules(string exe, string host)
    {
        if (!_any) return new();
        lock (_lock) return _rules.Where(r => r.Kind == BlockKind.Site && r.Matches(exe, host)).ToList();
    }

    public BlockRule BlockApp(string exe, string name, string? path) => Add(new BlockRule
    {
        Kind = BlockKind.App, Exe = exe, AppName = name, AppPath = path, Created = DateTime.Now,
    });

    public BlockRule BlockSite(string site, bool subdomains, string? exe = null, string? appName = null, string? appPath = null) =>
        Add(new BlockRule
        {
            Kind = BlockKind.Site, Site = site.Trim().TrimEnd('.').ToLowerInvariant(), Subdomains = subdomains,
            Exe = exe, AppName = exe == null ? null : appName, AppPath = exe == null ? null : appPath, Created = DateTime.Now,
        });

    public void Remove(BlockRule rule)
    {
        lock (_lock)
        {
            if (!_rules.Remove(rule)) return;
            _any = _rules.Count > 0;
            Save();
        }
        Logger.Instance.Info($"Unblocked {rule.Label}");
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (_rules.Count == 0) return;
            _rules = new();
            _any = false;
            Save();
        }
        Logger.Instance.Info("Removed every block");
    }

    internal void Hit(BlockRule rule) => Interlocked.Increment(ref rule._hits);

    /// <summary>Self test and screenshots: start from the given rules and never touch the saved file.</summary>
    internal void UseInMemory(IEnumerable<BlockRule>? rules = null)
    {
        lock (_lock)
        {
            _persist = false;
            _rules = rules?.ToList() ?? new();
            _any = _rules.Count > 0;
        }
    }

    /// <summary>The site a host name belongs to, like googlevideo.com for rr4---sn-x.googlevideo.com.</summary>
    public static string SiteOf(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        // Country codes with a second level, like co.uk or com.au, keep three labels.
        bool secondLevel = labels[^1].Length == 2 && SecondLevel.Contains(labels[^2]);
        return string.Join('.', labels[^(secondLevel ? 3 : 2)..]);
    }

    private static readonly HashSet<string> SecondLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        "co", "com", "net", "org", "gov", "edu", "ac", "or", "ne", "go", "gob", "nic", "mil",
    };

    private BlockRule Add(BlockRule rule)
    {
        lock (_lock)
        {
            // Blocking the same thing twice keeps the first rule.
            var same = _rules.FirstOrDefault(r => r.Kind == rule.Kind && r.Subdomains == rule.Subdomains
                && string.Equals(r.Exe, rule.Exe, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Site, rule.Site, StringComparison.OrdinalIgnoreCase));
            if (same != null) return same;
            _rules.Add(rule);
            _any = true;
            Save();
        }
        Logger.Instance.Info(rule.Kind == BlockKind.App
            ? $"Blocked {rule.Label}"
            : $"Blocked {(rule.Subdomains ? "all of " : "")}{rule.Site}" +
              (rule.Exe == null ? " for every app" : $" in {rule.AppName ?? rule.Exe}"));
        return rule;
    }

    private List<BlockRule> Load()
    {
        try
        {
            if (File.Exists(AppPaths.AppRules))
                return JsonSerializer.Deserialize<List<BlockRule>>(File.ReadAllText(AppPaths.AppRules), JsonOpts) ?? new();
        }
        catch (Exception e)
        {
            Logger.Instance.Error($"Could not read the block list, starting with nothing blocked: {e.Message}");
        }
        return new();
    }

    private void Save()
    {
        if (!_persist) return;
        try
        {
            var tmp = AppPaths.AppRules + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_rules, JsonOpts));
            File.Move(tmp, AppPaths.AppRules, true);
        }
        catch (Exception e)
        {
            Logger.Instance.Error($"Could not save the block list: {e.Message}");
        }
    }
}
