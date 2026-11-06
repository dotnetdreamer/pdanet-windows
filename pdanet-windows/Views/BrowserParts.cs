using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PdaNetPortable.Services;

namespace PdaNetPortable.Views;

/// <summary>The Microsoft Edge WebView2 Runtime the PdaNet browser runs on.</summary>
internal static class WebRuntime
{
    /// <summary>Microsoft's small WebView2 Runtime installer.</summary>
    public const string Download = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>The installed runtime version, or null when WebView2 is missing.</summary>
    public static string? Version()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { return null; }
    }
}

/// <summary>Website icons for tabs, web apps and recent websites, kept per website in data\browser\icons.</summary>
internal static class SiteIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised with the website name when a new icon arrives.</summary>
    public static event Action<string>? Updated;

    public static ImageSource? For(string? url)
    {
        var host = BrowserStore.HostOf(url);
        if (host.Length == 0) return null;
        if (Cache.TryGetValue(host, out var hit)) return hit;
        ImageSource? image = null;
        try
        {
            var path = BrowserStore.IconPath(host);
            if (File.Exists(path)) image = Decode(File.ReadAllBytes(path));
        }
        catch { /* no icon, the first letter shows instead */ }
        Cache[host] = image;
        return image;
    }

    /// <summary>Keeps a new icon for the website of <paramref name="url"/> and returns the icon.</summary>
    public static ImageSource? Store(string url, byte[] png)
    {
        var host = BrowserStore.HostOf(url);
        var image = Decode(png);
        if (host.Length == 0 || image == null) return image;
        Cache[host] = image;
        try
        {
            var path = BrowserStore.IconPath(host);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, png);
        }
        catch { /* the icon still shows for this run */ }
        Updated?.Invoke(host);
        return image;
    }

    /// <summary>Drops the icons of every website except the ones given, when the browsing data is cleared.</summary>
    public static void Forget(IEnumerable<string> keep)
    {
        var hosts = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        foreach (var host in Cache.Keys.Where(h => !hosts.Contains(h)).ToList()) Cache.Remove(host);
        try
        {
            var dir = Path.GetDirectoryName(BrowserStore.IconPath("x"))!;
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.png"))
                if (!hosts.Contains(Path.GetFileNameWithoutExtension(file))) File.Delete(file);
        }
        catch { /* an icon left behind does no harm */ }
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}

/// <summary>
/// One tab of the PdaNet browser. A new tab shows the start page and has no web view, the web
/// view is made for the first website, so a new tab costs nothing. A tab brought back from last
/// time keeps the address in PendingUrl until someone opens the tab.
/// </summary>
public sealed class BrowserTab : INotifyPropertyChanged
{
    private static int _ids;
    private string _title = "New tab", _url = "", _status = "";
    private ImageSource? _icon;
    private bool _loading, _audio, _muted;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stays the same while the tab is open, wherever the tab moves.</summary>
    public int Id { get; } = Interlocked.Increment(ref _ids);

    public WebView2? View { get; set; }
    public Task? Ready { get; set; }
    public BrowserTab? Opener { get; set; }
    public string? PendingUrl { get; set; }
    /// <summary>The http address to try when the https address someone typed does not answer.</summary>
    public string? HttpFallback { get; set; }
    public bool Crashed { get; set; }
    /// <summary>When the page last finished loading, titles set right after loading still go to the recent list.</summary>
    public DateTime LoadedAt { get; set; }

    /// <summary>True for a tab that shows the start page: nothing opened yet.</summary>
    public bool IsStart => View == null && PendingUrl == null && string.IsNullOrEmpty(Url) && !Loading;

    public string Title { get => _title; set { if (Set(ref _title, value)) Raise(nameof(Tip)); } }
    public string Url { get => _url; set { if (Set(ref _url, value)) Raise(nameof(Tip)); } }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public bool Loading { get => _loading; set => Set(ref _loading, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    public bool Audio
    {
        get => _audio;
        set { if (Set(ref _audio, value)) RaiseAudio(); }
    }

    public bool Muted
    {
        get => _muted;
        set { if (Set(ref _muted, value)) RaiseAudio(); }
    }

    public Visibility AudioVisibility => Audio || Muted ? Visibility.Visible : Visibility.Collapsed;
    public string AudioGlyph => Muted ? "" : "";
    public string AudioTip => Muted ? "Turn the sound of this tab back on" : "Mute this tab";

    public string Tip
    {
        get
        {
            var host = BrowserStore.HostOf(Url);
            return host.Length == 0 || Title == host ? Title : $"{Title}{Environment.NewLine}{host}";
        }
    }

    /// <summary>What a screen reader says for the tab.</summary>
    public override string ToString() => Title;

    private void RaiseAudio()
    {
        Raise(nameof(AudioVisibility));
        Raise(nameof(AudioGlyph));
        Raise(nameof(AudioTip));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A web app tile or a recent website row, on the Launchpad and on the start page.</summary>
public sealed class WebRow
{
    private WebRow(string name, string url, string detail, string tip)
    {
        Name = name;
        Url = url;
        Detail = detail;
        Tip = tip;
        Icon = SiteIcons.For(url);
        Monogram = AppsView.MonogramOf(name);
    }

    public static WebRow Of(WebApp app) =>
        new(app.Name, app.Url, BrowserStore.HostOf(app.Url), $"{app.Name}{Environment.NewLine}{BrowserStore.Display(app.Url)}") { App = app };

    public static WebRow Of(RecentSite site)
    {
        var host = BrowserStore.HostOf(site.Url);
        var title = string.IsNullOrWhiteSpace(site.Title) ? host : site.Title;
        return new(title, site.Url, host, $"{title}{Environment.NewLine}{BrowserStore.Display(site.Url)}")
        {
            Site = site,
            When = AppsView.Ago(site.Last),
        };
    }

    /// <summary>The last tile on the start page, for pinning a website by its address.</summary>
    public static WebRow AddTile() => new("Pin a website", "", "opens in one click", "Pin a website to your web apps") { IsAdd = true };

    public WebApp? App { get; private init; }
    public RecentSite? Site { get; private init; }
    public bool IsAdd { get; private init; }
    public string Name { get; }
    public string Url { get; }
    public string Detail { get; }
    public string Tip { get; }
    public string When { get; private init; } = "";
    public ImageSource? Icon { get; }
    public string Monogram { get; }
    public Visibility IconVisibility => Icon != null && !IsAdd ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MonogramVisibility => Icon == null && !IsAdd ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AddVisibility => IsAdd ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What a screen reader says for the tile.</summary>
    public override string ToString() => IsAdd ? Name : $"{Name}, {Detail}";
}

/// <summary>One line under the address bar.</summary>
public sealed class SuggestRow
{
    public SuggestRow(Suggestion s)
    {
        Suggestion = s;
        Icon = s.Kind == SuggestionKind.Search ? null : SiteIcons.For(s.Url);
        Glyph = s.Kind switch
        {
            SuggestionKind.Search => "",
            SuggestionKind.WebApp => "",
            SuggestionKind.Recent => "",
            _ => "",
        };
        Badge = s.Kind == SuggestionKind.WebApp ? "WEB APP" : "";
    }

    public Suggestion Suggestion { get; }
    public string Title => Suggestion.Title;
    public string Detail => Suggestion.Detail;
    public string Url => Suggestion.Url;
    public ImageSource? Icon { get; }
    public string Glyph { get; }
    public string Badge { get; }
    public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public override string ToString() => $"{Title}, {Detail}";
}
