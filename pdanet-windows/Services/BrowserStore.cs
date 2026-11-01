using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdaNetPortable.Core;

namespace PdaNetPortable.Services;

/// <summary>A website pinned as a web app, one click away on the Launchpad and on every new tab.</summary>
public sealed class WebApp
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("added")] public DateTime Added { get; set; } = DateTime.Now;
}

/// <summary>A page the browser opened, for the recent list and the address bar suggestions.</summary>
public sealed class RecentSite
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("visits")] public int Visits { get; set; }
    [JsonPropertyName("last")] public DateTime Last { get; set; }
}

/// <summary>Everything the PdaNet browser keeps between runs, saved to data\browser.json.</summary>
public sealed class BrowserState
{
    [JsonPropertyName("web_apps")] public List<WebApp> WebApps { get; set; } = new();
    [JsonPropertyName("recent")] public List<RecentSite> Recent { get; set; } = new();
    [JsonPropertyName("search")] public string Search { get; set; } = "google";
    // How websites pick light or dark colors: "auto" follows Windows, else "dark" or "light".
    [JsonPropertyName("colors")] public string Colors { get; set; } = "auto";
    // Load a website even when the safety of the connection cannot be checked: a certificate that
    // does not match, an old way of locking the connection, or a website a safety check blocks. On
    // by default, so a company website that a normal browser refuses still opens here.
    [JsonPropertyName("open_unsafe_sites")] public bool OpenUnsafeSites { get; set; } = true;
    [JsonPropertyName("last_tabs")] public List<string> LastTabs { get; set; } = new();
    // Left, top, width and height of the browser window, in device independent pixels.
    [JsonPropertyName("window")] public double[]? Window { get; set; }
    [JsonPropertyName("maximized")] public bool Maximized { get; set; }
}

public enum SuggestionKind { Address, Search, WebApp, Recent }

/// <summary>One line under the address bar.</summary>
public sealed record Suggestion(SuggestionKind Kind, string Title, string Url, string Detail);

/// <summary>A search engine the address bar can use.</summary>
public sealed record SearchEngine(string Key, string Name, string Prefix);

/// <summary>
/// Web apps, recent websites and the settings of the PdaNet browser, plus the rules that turn
/// typed text into a web address. Website data itself (sign ins, cookies and cache) lives in
/// data\browser, which WebView2 owns. Used from the UI thread only.
/// </summary>
public sealed class BrowserStore
{
    private static readonly Lazy<BrowserStore> _instance = new(() => new BrowserStore());
    public static BrowserStore Instance => _instance.Value;

    public const int MaxRecent = 300;

    public static readonly SearchEngine[] Engines =
    {
        new("google", "Google", "https://www.google.com/search?q="),
        new("bing", "Bing", "https://www.bing.com/search?q="),
        new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q="),
    };

    // Typed addresses with these schemes open as typed. javascript and data stay out on purpose.
    private static readonly HashSet<string> Schemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "file", "about", "edge", "view-source", "mailto", "tel",
    };

    private const StringComparison Ignore = StringComparison.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private bool _readOnly;

    private BrowserStore() => State = Load();

    public BrowserState State { get; private set; }

    /// <summary>Raised when web apps or recent websites change.</summary>
    public event Action? Changed;

    public SearchEngine Engine => Engines.FirstOrDefault(e => e.Key == State.Search) ?? Engines[0];

    // ============================ Web apps ============================
    /// <summary>The web app a page belongs to: the page address starts with the web app address.</summary>
    public WebApp? WebAppFor(string? url)
    {
        if (!IsWeb(url)) return null;
        return State.WebApps.Where(a => Covers(a.Url, url!)).OrderByDescending(a => Bare(a.Url).Length).FirstOrDefault();
    }

    public WebApp AddWebApp(string name, string url)
    {
        var app = new WebApp { Name = name.Trim(), Url = url.Trim(), Added = DateTime.Now };
        State.WebApps.Add(app);
        Commit();
        return app;
    }

    public void UpdateWebApp(WebApp app, string name, string url)
    {
        app.Name = name.Trim();
        app.Url = url.Trim();
        Commit();
    }

    public void RemoveWebApp(WebApp app)
    {
        if (State.WebApps.Remove(app)) Commit();
    }

    // ============================ Recent ============================
    /// <summary>Remembers a page the browser opened. Only web pages count.</summary>
    public void Visit(string url, string? title)
    {
        if (!IsWeb(url)) return;
        var key = Bare(url);
        var hit = State.Recent.FirstOrDefault(r => Bare(r.Url).Equals(key, Ignore));
        if (hit != null) State.Recent.Remove(hit);
        hit ??= new RecentSite();
        hit.Url = url;
        hit.Visits++;
        hit.Last = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(title)) hit.Title = title.Trim();
        State.Recent.Insert(0, hit);
        if (State.Recent.Count > MaxRecent) State.Recent.RemoveRange(MaxRecent, State.Recent.Count - MaxRecent);
        Commit();
    }

    /// <summary>Keeps the title of a recent page up to date, many pages set the title after loading.</summary>
    public void Retitle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || !IsWeb(url)) return;
        var key = Bare(url);
        var hit = State.Recent.FirstOrDefault(r => Bare(r.Url).Equals(key, Ignore));
        if (hit == null || hit.Title == title.Trim()) return;
        hit.Title = title.Trim();
        Commit();
    }

    /// <summary>The newest page of each website, newest first.</summary>
    public List<RecentSite> RecentWebsites(int count) =>
        State.Recent.GroupBy(r => HostOf(r.Url), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).Take(count).ToList();

    /// <summary>Drops every recent page of one website.</summary>
    public void Forget(string host)
    {
        if (State.Recent.RemoveAll(r => HostOf(r.Url).Equals(host, Ignore)) > 0) Commit();
    }

    public void ClearRecent()
    {
        if (State.Recent.Count == 0) return;
        State.Recent.Clear();
        Commit();
    }

    // ============================ Settings and session ============================
    public void SetSearch(string key)
    {
        if (Engines.All(e => e.Key != key)) return;
        State.Search = key;
        Commit();
    }

    public void SetColors(string colors)
    {
        State.Colors = colors is "dark" or "light" ? colors : "auto";
        Commit(notify: false);
    }

    public void SetOpenUnsafeSites(bool on)
    {
        State.OpenUnsafeSites = on;
        Commit(notify: false);
    }

    /// <summary>Saves the open tabs and the place of the window when the browser closes. Null tabs keeps the old list.</summary>
    public void SaveSession(List<string>? tabs, double[]? window, bool maximized)
    {
        if (tabs != null) State.LastTabs = tabs;
        State.Window = window;
        State.Maximized = maximized;
        Commit(notify: false);
    }

    // ============================ Typed text ============================
    public string SearchUrl(string text) => Engine.Prefix + Uri.EscapeDataString(text.Trim());

    /// <summary>
    /// Turns what someone typed into a web address. A full address stays as typed, a website name
    /// like example.com gets https, the name of a web app opens that web app, and anything else
    /// becomes a search. <paramref name="plain"/> is the http address to try when https fails,
    /// for a website name typed without http or https.
    /// </summary>
    public string Resolve(string text, out string? plain)
    {
        plain = null;
        var t = text.Trim();
        if (t.Length == 0) return "";
        var app = State.WebApps.FirstOrDefault(a => a.Name.Equals(t, Ignore));
        if (app != null) return app.Url;
        var address = AddressOf(t);
        if (address == null) return SearchUrl(t);
        if (address.StartsWith("https://", Ignore) && !t.StartsWith("https://", Ignore))
            plain = "http://" + address["https://".Length..];
        return address;
    }

    /// <summary>The web address typed text stands for, or null when the text reads as a search.</summary>
    public static string? AddressOf(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;

        // A file or folder on this PC, such as C:\Reports\May.pdf
        if (t.Length > 2 && char.IsLetter(t[0]) && t[1] == ':' && (t[2] == '\\' || t[2] == '/'))
            return File.Exists(t) || Directory.Exists(t) ? new Uri(t).AbsoluteUri : null;
        if (t.Any(char.IsWhiteSpace)) return null;

        int colon = t.IndexOf(':');
        if (colon > 0 && Schemes.Contains(t[..colon]) && (t.Contains("://") || t[..colon] is "about" or "mailto" or "tel" or "view-source"))
            return Uri.TryCreate(t, UriKind.Absolute, out var abs) && Schemes.Contains(abs.Scheme) ? abs.AbsoluteUri : null;
        if (t.Contains('@')) return null; // reads like an email address

        if (!Uri.TryCreate("http://" + t, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return null;
        var host = u.Host;
        bool ip = u.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6;
        int dot = host.LastIndexOf('.');
        bool local = ip || host == "localhost" || host.EndsWith(".localhost", Ignore) || host.EndsWith(".local", Ignore);
        // A single word like wiki is a website only with a slash or a port after the word, else a search.
        bool intranet = !ip && dot < 0 && (t.Contains('/') || !u.IsDefaultPort);
        if (local || intranet || !u.IsDefaultPort) return u.AbsoluteUri;
        bool named = !ip && dot > 0 && host.Length - dot - 1 >= 2 && host[(dot + 1)..].All(char.IsLetter);
        if (!named) return null;
        return new UriBuilder(u) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri;
    }

    /// <summary>Lines for the address bar: the typed text first, then web apps and recent pages that match.</summary>
    public List<Suggestion> Suggest(string text, int max = 7)
    {
        var t = text.Trim();
        var list = new List<Suggestion>();
        if (t.Length == 0) return list;

        var app = State.WebApps.FirstOrDefault(a => a.Name.Equals(t, Ignore));
        var address = app?.Url ?? AddressOf(t);
        list.Add(app != null ? new Suggestion(SuggestionKind.WebApp, app.Name, app.Url, Display(app.Url))
            : address != null ? new Suggestion(SuggestionKind.Address, Display(address), address, "Open website")
            : new Suggestion(SuggestionKind.Search, t, SearchUrl(t), $"Search {Engine.Name}"));

        foreach (var a in State.WebApps)
        {
            if (list.Count >= max) break;
            if (ReferenceEquals(a, app) || !(a.Name.Contains(t, Ignore) || Bare(a.Url).Contains(t, Ignore))) continue;
            list.Add(new Suggestion(SuggestionKind.WebApp, a.Name, a.Url, Display(a.Url)));
        }

        var recent = State.Recent
            .Where(r => r.Title.Contains(t, Ignore) || Bare(r.Url).Contains(t, Ignore))
            .OrderByDescending(r => Bare(r.Url).StartsWith(t, Ignore))
            .ThenByDescending(r => r.Visits)
            .ThenByDescending(r => r.Last);
        foreach (var r in recent)
        {
            if (list.Count >= max) break;
            if (list.Any(s => Bare(s.Url).Equals(Bare(r.Url), Ignore))) continue;
            var title = string.IsNullOrWhiteSpace(r.Title) ? HostOf(r.Url) : r.Title;
            list.Add(new Suggestion(SuggestionKind.Recent, title, r.Url, Display(r.Url)));
        }
        return list;
    }

    // ============================ Address helpers ============================
    public static bool IsWeb(string? url) =>
        url != null && (url.StartsWith("https://", Ignore) || url.StartsWith("http://", Ignore));

    /// <summary>A web address for people: no http or https in front and no slash at the end.</summary>
    public static string Display(string url)
    {
        if (!IsWeb(url)) return url;
        var u = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
        return u.TrimEnd('/');
    }

    /// <summary>The website name in an address, without www in front, empty when there is none.</summary>
    public static string HostOf(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return "";
        var h = u.Host;
        return h.StartsWith("www.", Ignore) ? h[4..] : h;
    }

    /// <summary>
    /// A short name for a new web app. A part of the page title that matches the website wins,
    /// such as Outlook in "Inbox, Outlook", else a word from the website name.
    /// </summary>
    public static string NameFor(string url, string? title = null)
    {
        var host = HostOf(url);
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (!string.IsNullOrWhiteSpace(title))
        {
            var t = title.Trim();
            var parts = t.Split(new[] { " - ", " | ", " · ", " — ", " – ", ": ", ", " },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var match = parts
                .Where(p => p.Length >= 3 && p.Length <= 32 && labels.Any(l => l.Length > 2 &&
                    (p.Replace(" ", "").Contains(l, Ignore) || l.Contains(p.Replace(" ", ""), Ignore))))
                .OrderBy(p => p.Length)
                .FirstOrDefault();
            if (match != null) return match;
            if (parts.Length == 1 && t.Length <= 24) return t;
        }
        if (labels.Length == 0) return host;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "www", "app", "apps", "web", "m", "my", "portal", "login", "home" };
        var pick = labels.Length == 1 ? labels[0]
            : labels.Take(labels.Length - 1).FirstOrDefault(l => !skip.Contains(l) && !(l.Length == 2 && labels.Length > 2)) ?? labels[^2];
        return char.ToUpperInvariant(pick[0]) + pick[1..];
    }

    /// <summary>Where the icon of a website is kept between runs.</summary>
    public static string IconPath(string host)
    {
        var safe = new string(host.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
        return Path.Combine(AppPaths.BrowserDir, "icons", safe + ".png");
    }

    /// <summary>True when the page sits under the web app: the same address, or a deeper page of the address.</summary>
    private static bool Covers(string appUrl, string pageUrl)
    {
        var app = Bare(appUrl);
        var page = Bare(pageUrl);
        if (app.Length == 0 || !page.StartsWith(app, Ignore)) return false;
        return page.Length == app.Length || page[app.Length] is '/' or '?';
    }

    /// <summary>An address without http or https, www, the part after # and a slash at the end, for comparing.</summary>
    private static string Bare(string url)
    {
        var u = url.Trim();
        int hash = u.IndexOf('#');
        if (hash >= 0) u = u[..hash];
        int scheme = u.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) u = u[(scheme + 3)..];
        if (u.StartsWith("www.", Ignore)) u = u[4..];
        return u.TrimEnd('/');
    }

    // ============================ Disk ============================
    private static BrowserState Load()
    {
        BrowserState? state = null;
        try
        {
            if (File.Exists(AppPaths.Browser))
                state = JsonSerializer.Deserialize<BrowserState>(File.ReadAllText(AppPaths.Browser));
        }
        catch (Exception e)
        {
            Logger.Instance.Warning($"Could not read {AppPaths.Browser}, the browser starts with no web apps: {e.Message}");
        }
        state ??= new BrowserState();
        state.WebApps ??= new();
        state.Recent ??= new();
        state.LastTabs ??= new();
        state.Search ??= "google";
        state.Colors ??= "auto";
        state.WebApps.RemoveAll(a => a == null || !IsWeb(a.Url));
        state.Recent.RemoveAll(r => r == null || !IsWeb(r.Url));
        foreach (var r in state.Recent) r.Title ??= "";
        foreach (var a in state.WebApps) a.Name ??= "";
        return state;
    }

    private void Commit(bool notify = true)
    {
        if (!_readOnly)
        {
            try { File.WriteAllText(AppPaths.Browser, JsonSerializer.Serialize(State, JsonOptions)); }
            catch (Exception e) { Logger.Instance.Warning($"Could not save {AppPaths.Browser}: {e.Message}"); }
        }
        if (notify) Changed?.Invoke();
    }

    // ======================= Screenshot only demo =======================
    /// <summary>Sample web apps and recent websites for screenshots. Nothing is written to disk afterwards.</summary>
    public void SeedDemo()
    {
        _readOnly = true;
        var now = DateTime.Now;
        State = new BrowserState
        {
            Search = State.Search,
            Colors = State.Colors,
            Window = State.Window,
            Maximized = State.Maximized,
            WebApps =
            {
                new() { Name = "Outlook", Url = "https://outlook.office.com/mail/" },
                new() { Name = "Teams", Url = "https://teams.microsoft.com/" },
                new() { Name = "Wikipedia", Url = "https://en.wikipedia.org/" },
                new() { Name = "GitHub", Url = "https://github.com/" },
                new() { Name = "BBC News", Url = "https://www.bbc.com/news" },
                new() { Name = "Speed test", Url = "https://fast.com/" },
            },
            Recent =
            {
                new() { Url = "https://en.wikipedia.org/wiki/Tethering", Title = "Tethering, Wikipedia", Visits = 3, Last = now.AddMinutes(-4) },
                new() { Url = "https://github.com/MicrosoftEdge/WebView2Feedback", Title = "MicrosoftEdge/WebView2Feedback", Visits = 2, Last = now.AddMinutes(-26) },
                new() { Url = "https://www.bbc.com/news", Title = "BBC News, Home", Visits = 5, Last = now.AddHours(-2) },
                new() { Url = "https://fast.com/", Title = "Internet Speed Test", Visits = 1, Last = now.AddHours(-5) },
                new() { Url = "https://www.openstreetmap.org/", Title = "OpenStreetMap", Visits = 1, Last = now.AddDays(-1) },
            },
            LastTabs = { "https://en.wikipedia.org/wiki/Tethering", "https://github.com/" },
        };
        Changed?.Invoke();
    }
}
