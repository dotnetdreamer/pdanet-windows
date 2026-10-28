using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PdaNetPortable.Core;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>Raises change notices so rows can update in place while they are on screen.</summary>
public abstract class LiveRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>One app in the list on the left, or the All apps row at the top.</summary>
public sealed class AppRow : LiveRow
{
    private string _sub = "", _total = "", _rate = "", _tip = "";
    private Brush _rateBrush = Ui.Brush("B.Text3"), _tileBrush = Ui.Brush("B.Line2");
    private double _share;
    private Visibility _live = Visibility.Collapsed;

    public AppRow(AppTraffic? app)
    {
        App = app;
        Name = app?.Name ?? "All apps";
        Icon = app != null ? AppIcons.For(app.Path) : null;
        Monogram = AppsView.MonogramOf(Name);
    }

    public AppTraffic? App { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
    public string Monogram { get; }
    public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MonogramVisibility => Icon == null && App != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AllVisibility => App == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShareVisibility => App == null ? Visibility.Collapsed : Visibility.Visible;

    public string Sub { get => _sub; private set => Set(ref _sub, value); }
    public string Total { get => _total; private set => Set(ref _total, value); }
    public string Rate { get => _rate; private set => Set(ref _rate, value); }
    public Brush RateBrush { get => _rateBrush; private set => Set(ref _rateBrush, value); }
    public double Share { get => _share; private set => Set(ref _share, value); }
    public Visibility LiveVisibility { get => _live; private set => Set(ref _live, value); }
    public string Tip { get => _tip; private set => Set(ref _tip, value); }

    /// <summary>Outline of the icon tile, red while the app is blocked.</summary>
    public Brush TileBrush { get => _tileBrush; private set => Set(ref _tileBrush, value); }

    /// <summary>Data used, for keeping the list in order.</summary>
    public long Bytes => App?.BytesTotal ?? long.MaxValue;

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => $"{Name}, {Total}, {Rate}";

    public void Update(TrafficSnapshot snap)
    {
        long all = snap.BytesUp + snap.BytesDown;
        if (App == null)
        {
            Sub = $"{AppsView.Count(snap.Apps.Count, "app")}  ·  {AppsView.Count(snap.Sites, "website")}";
            Total = Fmt.Bytes(all);
            SetRate(snap.RateDown, snap.RateUp, snap.Apps.Any(a => a.OpenConnections > 0), null);
            Tip = "All the data every app sent and received through PdaNet";
            return;
        }

        var a = App;
        Sub = AppsView.Count(a.Sites, "website");
        Total = Fmt.Bytes(a.BytesTotal);
        Share = all > 0 ? Math.Max(2, a.BytesTotal * 100.0 / all) : 0;
        bool blocked = TrafficRules.Instance.AppRule(a.Exe) != null;
        TileBrush = Ui.Brush(blocked ? "B.Red" : "B.Line2");
        if (blocked)
        {
            LiveVisibility = Visibility.Collapsed;
            Rate = "blocked";
            RateBrush = Ui.Brush("B.Red");
            Tip = $"{a.Name} is blocked, PdaNet stops every connection from {a.Name}";
            return;
        }
        SetRate(a.RateDown, a.RateUp, a.OpenConnections > 0, a.LastSeen);
        Tip = $"{a.Name} used {Share:0}% of the data, {Fmt.Bytes(a.BytesDown)} received and {Fmt.Bytes(a.BytesUp)} sent";
    }

    private void SetRate(double down, double up, bool open, DateTime? lastSeen)
    {
        bool busy = down + up >= 256;
        LiveVisibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            bool sending = up > down;
            Rate = sending ? $"↑ {Fmt.Bandwidth(up)}" : $"↓ {Fmt.Bandwidth(down)}";
            RateBrush = Ui.Brush(sending ? "B.Cyan" : "B.Accent");
            return;
        }
        Rate = open ? "connected" : lastSeen.HasValue ? AppsView.Ago(lastSeen.Value) : "quiet";
        RateBrush = Ui.Brush("B.Text3");
    }
}

/// <summary>One request or connection in the list on the right.</summary>
public sealed class FlowRow : LiveRow
{
    private string _result = "", _up = "", _down = "", _took = "";
    private Brush _resultBrush = Ui.Brush("B.Text2");
    private string? _resultTip;

    public FlowRow(TrafficFlow flow)
    {
        Flow = flow;
        Time = flow.Started.ToString("HH:mm:ss");
        AppName = flow.App.Name;
        AppIcon = AppIcons.For(flow.App.Path);
        AppMonogram = AppsView.MonogramOf(flow.App.Name);
        Badge = flow.Secure ? "Private" : "Open";
        BadgeTip = flow.Secure
            ? "Private, only the app and the website can read the content"
            : "Not private, people on the same network could read this data";
        (BadgeBrush, BadgeBack) = flow.Secure
            ? (Ui.Brush("B.Cyan"), Ui.Brush("B.CyanSoft"))
            : (Ui.Brush("B.Amber"), Ui.Brush("B.AmberSoft"));
        bool defaultPort = flow.Secure ? flow.Port == 443 : flow.Port == 80;
        Host = defaultPort ? flow.Host : $"{flow.Host}:{flow.Port}";
        Path = flow.Secure ? "" : flow.Path;
        Refresh();
    }

    public TrafficFlow Flow { get; }
    public string Time { get; }
    public string AppName { get; }
    public ImageSource? AppIcon { get; }
    public string AppMonogram { get; }
    public Visibility AppMonogramVisibility => AppIcon == null ? Visibility.Visible : Visibility.Collapsed;
    public string Badge { get; }
    public Brush BadgeBrush { get; }
    public Brush BadgeBack { get; }
    public string BadgeTip { get; }
    public string Host { get; }
    public string Path { get; }
    public Visibility PlainVisibility => Flow.Secure ? Visibility.Collapsed : Visibility.Visible;

    public string Result { get => _result; private set => Set(ref _result, value); }
    public Brush ResultBrush { get => _resultBrush; private set => Set(ref _resultBrush, value); }
    public string? ResultTip { get => _resultTip; private set => Set(ref _resultTip, value); }
    public string Up { get => _up; private set => Set(ref _up, value); }
    public string Down { get => _down; private set => Set(ref _down, value); }
    public string Took { get => _took; private set => Set(ref _took, value); }

    /// <summary>True once the connection has closed and this row was brought up to date.</summary>
    public bool IsFinal { get; private set; }

    /// <summary>Brings the live numbers up to date. Returns false once the row will not change again.</summary>
    public bool Refresh()
    {
        if (IsFinal) return false;
        bool open = Flow.IsOpen; // read first, so a row is never marked final with numbers from before the close
        Up = Fmt.Bytes(Flow.BytesUp);
        Down = Fmt.Bytes(Flow.BytesDown);
        Took = AppsView.Span(Flow.Duration);
        (Result, ResultBrush, ResultTip) = Describe(Flow);
        // One more look after the close, for the last bytes the other direction was still passing on.
        if (!open && ++_closedSeen >= 2) IsFinal = true;
        return true;
    }

    private int _closedSeen;

    public bool Matches(string q) =>
        AppName.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Flow.App.Exe.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Host.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Path.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Badge.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Result.Contains(q, StringComparison.OrdinalIgnoreCase);

    /// <summary>What a screen reader says for the line.</summary>
    public override string ToString() => $"{Time} {AppName} {Badge} {Host}{Path} {Result}";

    /// <summary>Plain text form used for copy.</summary>
    public string Export() =>
        $"{Flow.Started:dd MMM yyyy HH:mm:ss}  {AppName}  {Badge,-6} {Flow.Url}  {Result}  sent {Up}  received {Down}  took {Took}";

    internal static (string Text, Brush Brush, string? Tip) Describe(TrafficFlow f)
    {
        if (f.Outcome == FlowOutcome.Blocked) return ("Blocked", Ui.Brush("B.Red"), f.BlockedBy);
        if (f.Outcome == FlowOutcome.Failed) return ("Failed", Ui.Brush("B.Red"), f.Error);
        if (f.Secure)
        {
            if (f.Status >= 400) return ("Refused", Ui.Brush("B.Red"), "The phone did not allow this connection");
            return f.IsOpen
                ? ("Live", Ui.Brush("B.Accent"), "Still connected")
                : ("Done", Ui.Brush("B.Text2"), "Finished");
        }
        if (f.Status > 0)
        {
            var key = f.Status >= 500 ? "B.Red" : f.Status >= 400 ? "B.Amber" : f.Status >= 300 ? "B.Cyan" : "B.Accent";
            return (PlainStatus(f.Status), Ui.Brush(key), $"Website answer: {f.Status} {f.Reason}".Trim());
        }
        return f.IsOpen
            ? ("Waiting", Ui.Brush("B.Amber"), "Waiting for the website to answer")
            : ("Done", Ui.Brush("B.Text2"), "Finished");
    }

    /// <summary>The website's answer code in everyday words, the code itself stays in the tooltip.</summary>
    private static string PlainStatus(int status) => status switch
    {
        >= 200 and < 300 => "OK",
        >= 300 and < 400 => "Moved",
        401 or 403 => "Not allowed",
        404 or 410 => "Not found",
        429 => "Too many tries",
        >= 400 and < 500 => "Refused",
        _ => "Website error",
    };
}

/// <summary>One site in the top sites list.</summary>
public sealed record SiteRow(string Host, string Size, double Share, string Tip);

/// <summary>
/// Block and unblock actions shared by the Apps page, the record sheet and the blocked list. A new
/// rule also closes the connections it covers that are open right now.
/// </summary>
internal static class Blocking
{
    /// <summary>PdaNet itself and apps it could not name cannot be blocked as a whole.</summary>
    public static bool CanBlock(AppTraffic app) => !app.IsSelf && !string.IsNullOrEmpty(app.Exe);

    public static BlockRule App(MainWindow? shell, AppTraffic app)
    {
        var rule = TrafficRules.Instance.BlockApp(app.Exe, app.Name, app.Path);
        int cut = TrafficMonitor.Instance.Cut(rule);
        Done(shell, cut > 0 ? $"{app.Name} is blocked, {AppsView.Count(cut, "open connection")} closed" : $"{app.Name} is blocked");
        return rule;
    }

    public static BlockRule Site(MainWindow? shell, string site, bool subdomains, AppTraffic? only)
    {
        var rule = TrafficRules.Instance.BlockSite(site, subdomains, only?.Exe, only?.Name, only?.Path);
        int cut = TrafficMonitor.Instance.Cut(rule);
        var what = (subdomains ? $"all of {site}" : site) + (only == null ? "" : $" in {only.Name}");
        Done(shell, $"Blocked {what}" + (cut > 0 ? $", {AppsView.Count(cut, "open connection")} closed" : ""));
        return rule;
    }

    public static void Undo(MainWindow? shell, BlockRule rule)
    {
        TrafficRules.Instance.Remove(rule);
        shell?.Toast($"Unblocked {rule.Label}", ToastKind.Info);
    }

    public static string Short(string host) => host.Length <= 40 ? host : host[..37] + "…";

    private static void Done(MainWindow? shell, string message)
    {
        shell?.Toast(message);
        // A connection made with app watch off in Phone VPN mode has no relay, so rules wait for the next one.
        var cm = ConnectionManager.Instance;
        if (cm.IsConnected && !cm.RelayActive)
            shell?.Toast("Reconnect so PdaNet can apply your block list", ToastKind.Warning);
    }
}

/// <summary>
/// The Apps page: which apps on this PC go online through PdaNet, every site they talk to, how
/// much they send and receive, and each plain HTTP request with its answer. A list of apps on the
/// left, details and a live log for the picked app on the right, and a sheet with the full record
/// of a line on double click.
/// </summary>
public partial class AppsView : UserControl, IPage
{
    private readonly TrafficMonitor _monitor = TrafficMonitor.Instance;
    private readonly ConnectionManager _cm = ConnectionManager.Instance;
    private readonly AppConfig _cfg = ConfigManager.Instance.Config;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ObservableCollection<AppRow> _apps = new();
    private readonly Dictionary<string, AppRow> _appByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppRow _allRow = new(null);
    private readonly List<FlowRow> _flows = new(); // every row we know about, oldest first
    private readonly List<FlowRow> _live = new();  // rows whose connection is still open
    private ObservableCollection<FlowRow> _shown = new();
    private TrafficSnapshot? _snap;
    private MainWindow? _shell;
    private long _lastId;
    private string? _emptyMode;
    private bool? _watchingShown;

    public AppsView()
    {
        InitializeComponent();
        _apps.Add(_allRow);
        AppList.ItemsSource = _apps;
        FlowList.ItemsSource = _shown;
        _tick.Tick += (_, _) => Pull();
    }

    public void Attach(MainWindow shell)
    {
        _shell = shell;
        AppList.SelectedItem = _allRow;
        FlowList.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelected()));
        IsVisibleChanged += (_, _) =>
        {
            PromptCaret.BeginAnimation(Brush.OpacityProperty, IsVisible ? Ui.Blink() : null);
            if (IsVisible)
            {
                Pull();
                _tick.Start();
            }
            else
            {
                _tick.Stop();
                _emptyMode = null; // restart the radar animation next time
            }
        };
        ShowHeader(_allRow);
    }

    public void OnShown()
    {
        Pull();
        ScrollToEnd();
    }

    public void Refresh()
    {
        Pull();
        _shell?.Toast("Apps refreshed", ToastKind.Info);
    }

    /// <summary>Screenshot only: picks the first app whose name or program contains the text.</summary>
    public void DemoSelect(string text)
    {
        Pull();
        var row = _apps.FirstOrDefault(r => r.App != null &&
            (r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || r.App.Exe.Contains(text, StringComparison.OrdinalIgnoreCase)));
        if (row != null) AppList.SelectedItem = row;
    }

    /// <summary>Screenshot only: opens the full record of the newest line whose site contains the text.</summary>
    public void DemoRecord(string host)
    {
        Pull();
        var row = _flows.LastOrDefault(r => r.Flow.Host.Contains(host, StringComparison.OrdinalIgnoreCase));
        if (row == null) return;
        FlowList.SelectedItem = row;
        OpenRecord(row);
    }

    // ============================ Live data ============================
    private void Pull()
    {
        var snap = _snap = _monitor.Snapshot(_lastId);

        foreach (var app in snap.Apps)
        {
            if (!_appByKey.TryGetValue(app.Key, out var row) || !ReferenceEquals(row.App, app))
            {
                if (row != null) _apps.Remove(row);
                row = new AppRow(app);
                _appByKey[app.Key] = row;
                _apps.Add(row);
            }
            row.Update(snap);
        }
        _allRow.Update(snap);
        SortApps();
        AppCount.Text = snap.Apps.Count > 0 ? snap.Apps.Count.ToString() : "";
        int rules = TrafficRules.Instance.All.Count;
        BlockedChip.Visibility = rules > 0 ? Visibility.Visible : Visibility.Collapsed;
        BlockedChip.Content = $"{rules} blocked";

        bool rebuild = false;
        foreach (var flow in snap.NewFlows)
        {
            var row = new FlowRow(flow);
            _flows.Add(row);
            if (!row.IsFinal) _live.Add(row);
            if (Matches(row)) _shown.Add(row);
            _lastId = flow.Id;
        }
        Trim(snap.OldestId);

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var row = _live[i];
            row.Refresh();
            if (!row.IsFinal) continue;
            _live.RemoveAt(i);
            // A connection that just failed belongs in the problems view.
            if (FilterProblems.IsChecked == true && row.Flow.IsProblem) rebuild = true;
        }
        if (rebuild) Rebuild();

        UpdateDetail();
        UpdateStatus();
        if (snap.NewFlows.Count > 0 && Follow.IsChecked == true && IsVisible) ScrollToEnd();
    }

    private void SortApps()
    {
        var order = _apps.OrderByDescending(r => r.Bytes).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < order.Count; i++)
        {
            int at = _apps.IndexOf(order[i]);
            if (at != i) _apps.Move(at, i);
        }
    }

    private void Trim(long oldestId)
    {
        int drop = 0;
        while (drop < _flows.Count && _flows[drop].Flow.Id < oldestId) drop++;
        if (drop == 0) return;
        _flows.RemoveRange(0, drop);
        _live.RemoveAll(r => r.Flow.Id < oldestId);
        while (_shown.Count > 0 && _shown[0].Flow.Id < oldestId) _shown.RemoveAt(0);
    }

    private AppTraffic? SelectedApp => (AppList.SelectedItem as AppRow)?.App;

    private bool Matches(FlowRow row)
    {
        var app = SelectedApp;
        if (app != null && !ReferenceEquals(row.Flow.App, app)) return false;
        if (FilterProblems.IsChecked == true && !row.Flow.IsProblem) return false;
        var q = Search.Text?.Trim();
        return string.IsNullOrEmpty(q) || row.Matches(q);
    }

    private void Rebuild()
    {
        _shown = new ObservableCollection<FlowRow>(_flows.Where(Matches));
        FlowList.ItemsSource = _shown;
        ScrollToEnd();
    }

    // ============================ Details ============================
    private void ShowHeader(AppRow row)
    {
        var app = row.App;
        DetName.Text = row.Name;
        DetIcon.Source = row.Icon;
        DetGlyph.Visibility = app == null ? Visibility.Visible : Visibility.Collapsed;
        DetMono.Text = app != null && row.Icon == null ? row.Monogram : "";
        FolderBtn.Visibility = app?.Path != null ? Visibility.Visible : Visibility.Collapsed;
        DetPath.Visibility = app == null ? Visibility.Collapsed : Visibility.Visible;
        DetPath.Text = app?.Path ?? app?.Exe ?? "";
        DetPath.ToolTip = app?.Path;
        FlowList.Tag = app == null ? Visibility.Visible : Visibility.Collapsed;
        HeadAppCol.Width = new GridLength(app == null ? 24 : 0);
        LegendDown.Text = "Received";
        LegendUp.Text = "Sent";
    }

    private void UpdateDetail()
    {
        var snap = _snap;
        if (snap == null) return;
        var app = SelectedApp;
        if (app == null)
        {
            DetDown.Text = Fmt.Bytes(snap.BytesDown);
            DetUp.Text = Fmt.Bytes(snap.BytesUp);
            DetConn.Text = snap.Apps.Sum(a => a.Connections).ToString("N0", CultureInfo.CurrentCulture);
            DetSites.Text = snap.Sites.ToString("N0", CultureInfo.CurrentCulture);
            DetMeta.Text = snap.Apps.Count == 0
                ? "An app shows up here as soon as the app goes online"
                : $"{Count(snap.Apps.Sum(a => a.OpenConnections), "connection")} open right now";
        }
        else
        {
            DetDown.Text = Fmt.Bytes(app.BytesDown);
            DetUp.Text = Fmt.Bytes(app.BytesUp);
            DetConn.Text = app.Connections.ToString("N0", CultureInfo.CurrentCulture);
            DetSites.Text = app.Sites.ToString("N0", CultureInfo.CurrentCulture);
            var meta = new List<string>();
            if (!string.IsNullOrEmpty(app.Publisher)) meta.Add(app.Publisher!);
            meta.Add($"first seen {app.FirstSeen:HH:mm}");
            if (app.OpenConnections > 0) meta.Add($"{Count(app.OpenConnections, "connection")} open now");
            DetMeta.Text = string.Join("  ·  ", meta);
        }

        var appRule = app != null ? TrafficRules.Instance.AppRule(app.Exe) : null;
        BlockedBanner.Visibility = appRule != null ? Visibility.Visible : Visibility.Collapsed;
        BlockAppBtn.Visibility = app != null && appRule == null && Blocking.CanBlock(app) ? Visibility.Visible : Visibility.Collapsed;
        if (app != null && appRule != null)
        {
            BannerTitle.Text = $"{app.Name} is blocked";
            BannerBody.Text = app.BlockedCount > 0
                ? $"PdaNet stopped {Count(app.BlockedCount, "connection")} so far, blocked since {appRule.Created:d MMM, HH:mm}"
                : $"PdaNet stops every connection from {app.Name}, blocked since {appRule.Created:d MMM, HH:mm}";
        }

        DrawChart();

        long top = 0;
        var sites = _monitor.TopSites(app, 3);
        if (sites.Count > 0) top = Math.Max(1, sites[0].Bytes);
        var siteRows = sites.Select(s => new SiteRow(s.Host, Fmt.Bytes(s.Bytes), s.Bytes * 100.0 / top,
            $"{s.Host}, {Fmt.Bytes(s.Bytes)} over {Count(s.Connections, "connection")}")).ToList();
        // Records compare by value, so an unchanged list keeps its rows (and any open tooltip).
        if (TopSites.ItemsSource is not List<SiteRow> shownSites || !shownSites.SequenceEqual(siteRows))
            TopSites.ItemsSource = siteRows;
        TopSitesEmpty.Visibility = sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int shown = _shown.Count;
        int total = app == null ? _flows.Count : _flows.Count(r => ReferenceEquals(r.Flow.App, app));
        CountText.Text = shown == total ? Count(total, "line") : $"{shown} of {Count(total, "line")}";
        NoMatch.Visibility = shown == 0 && total > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatchText.Text = FilterProblems.IsChecked == true && string.IsNullOrWhiteSpace(Search.Text)
            ? "No problems, every line went through fine"
            : "No lines match your search";
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void DrawChart()
    {
        double w = Chart.ActualWidth, h = Chart.ActualHeight;
        if (w < 10 || h < 10) return;

        var app = SelectedApp;
        var down = _monitor.History(app, down: true);
        var up = _monitor.History(app, down: false);
        double peak = Math.Max(down.DefaultIfEmpty(0).Max(), up.DefaultIfEmpty(0).Max());
        double max = Math.Max(16 * 1024, peak * 1.2);
        DownLine.Data = Ui.Smooth(down, TrafficMonitor.HistoryPoints, w, h, max, area: false);
        DownArea.Data = Ui.Smooth(down, TrafficMonitor.HistoryPoints, w, h, max, area: true);
        UpLine.Data = Ui.Smooth(up, TrafficMonitor.HistoryPoints, w, h, max, area: false);
        ChartMax.Text = peak > 0 ? Fmt.Bandwidth(max) : "";
        ChartEmpty.Visibility = peak > 0 ? Visibility.Collapsed : Visibility.Visible;

        double nowDown = down.Length > 0 ? down[^1] : 0, nowUp = up.Length > 0 ? up[^1] : 0;
        LegendDown.Text = nowDown > 0 ? $"Received {Fmt.Bandwidth(nowDown)}" : "Received";
        LegendUp.Text = nowUp > 0 ? $"Sent {Fmt.Bandwidth(nowUp)}" : "Sent";
    }

    // Narrow windows: keep the two numbers people look at most, and give the chart the room.
    private void OnDetailSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        StatConn.Visibility = w < 640 ? Visibility.Collapsed : Visibility.Visible;
        StatSites.Visibility = w < 560 ? Visibility.Collapsed : Visibility.Visible;
        SitesCol.Width = new GridLength(w < 600 ? 170 : 230);
    }

    // ============================ Status ============================
    private void UpdateStatus()
    {
        var snap = _snap;
        if (snap == null) return;
        bool connected = _cm.IsConnected;
        bool watching = _cm.WatchingApps || Demo.Active;

        var (text, key) = !_cfg.WatchApps ? ("App watch is off", "B.Text3")
            : watching ? (snap.Apps.Count > 0 ? $"Watching {Count(snap.Apps.Count, "app")}" : "Watching", "B.Accent")
            : connected ? ("Reconnect to watch", "B.Amber")
            : ("Not connected", "B.Text3");
        var brush = Ui.Brush(key);
        var color = ((SolidColorBrush)brush).Color;
        WatchText.Text = text;
        WatchDot.Fill = brush;
        WatchPill.Background = new SolidColorBrush(Ui.WithAlpha(color, 0x18));
        WatchPill.BorderBrush = new SolidColorBrush(Ui.WithAlpha(color, 0x55));
        if (_watchingShown != watching)
        {
            _watchingShown = watching;
            WatchDot.BeginAnimation(OpacityProperty, watching
                ? new DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
                : null);
        }

        var app = SelectedApp;
        PromptText.Text = !watching ? "not watching right now"
            : app == null ? "watching all apps, double click a line for details"
            : $"watching {app.Name}, double click a line for details";

        // Nothing recorded yet: explain what to do next instead of showing empty boxes.
        bool any = snap.Apps.Count > 0;
        string mode = any ? "" : !_cfg.WatchApps ? "off" : watching ? "waiting" : connected ? "reconnect" : "offline";
        if (mode == _emptyMode) return;
        _emptyMode = mode;
        Board.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (any)
        {
            StopRadar();
            return;
        }

        (EmptyTitle.Text, EmptyBody.Text, EmptyAction.Content) = mode switch
        {
            "off" => ("App watch is off",
                "Turn on app watch to see which apps use the internet and how much data each app uses",
                (object)"Turn on app watch"),
            "waiting" => ("Waiting for apps to go online",
                "Open a browser or any other app, the app shows up here right away",
                (object)""),
            "reconnect" => ("Reconnect to start watching",
                "App watch starts the next time you connect, disconnect and connect again on the Connect page",
                (object)"Go to Connect"),
            _ => ("Nothing to show yet",
                "Connect first, then open any app, the app shows up here right away",
                (object)"Go to Connect"),
        };
        EmptyAction.Visibility = mode == "waiting" ? Visibility.Collapsed : Visibility.Visible;
        EmptyIcon.Foreground = Ui.Brush(mode == "waiting" ? "B.Accent" : "B.Text3");
        if (mode == "waiting") StartRadar();
        else StopRadar();
    }

    private void StartRadar()
    {
        var grow = Ui.Loop(0.62, 1.45, 2.2, 30, Ui.EaseOut);
        RadarScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        RadarScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        RadarRing.BeginAnimation(OpacityProperty, Ui.Loop(0.85, 0, 2.2, 30, Ui.EaseOut));
    }

    private void StopRadar()
    {
        RadarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RadarScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RadarRing.BeginAnimation(OpacityProperty, null);
        RadarRing.Opacity = 0;
    }

    private void OnEmptyAction(object sender, RoutedEventArgs e)
    {
        if (_emptyMode != "off")
        {
            _shell?.Navigate(AppPage.Connect);
            return;
        }
        _cfg.WatchApps = true;
        ConfigManager.Instance.SaveConfig();
        Logger.Instance.Ok("App watch turned on");
        _shell?.Toast(_cm.WatchingApps ? "App watch is on"
            : _cm.IsConnected ? "App watch is on, reconnect to start watching"
            : "App watch is on, watching starts when you connect");
        Pull();
    }

    // ============================ Actions ============================
    private void OnAppSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AppList.SelectedItem is not AppRow row)
        {
            // Keep one row picked at all times, All apps when the picked app goes away.
            if (_apps.Count > 0) AppList.SelectedItem = _allRow;
            return;
        }
        ShowHeader(row);
        Rebuild();
        UpdateDetail();
        UpdateStatus();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_shell == null) return; // still loading
        Rebuild();
        UpdateDetail();
    }

    private void OnFlowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FlowList, (DependencyObject)e.OriginalSource) is ListBoxItem { Content: FlowRow row })
            OpenRecord(row);
    }

    private void OnFlowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || FlowList.SelectedItem is not FlowRow row) return;
        OpenRecord(row);
        e.Handled = true;
    }

    private void OpenRecord(FlowRow row)
    {
        if (_shell == null || _shell.SheetOpen) return;
        var sheet = new RequestSheet(row.Flow);
        sheet.Closed += _shell.CloseSheet;
        _shell.ShowSheet(sheet);
    }

    // ============================ Blocking ============================
    private void OnOpenBlocked(object sender, RoutedEventArgs e) => _shell?.OpenBlocked();

    private void OnBlockApp(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is { } app && Blocking.CanBlock(app)) Blocking.App(_shell, app);
        Pull();
    }

    private void OnUnblockApp(object sender, RoutedEventArgs e)
    {
        var rule = SelectedApp is { } app ? TrafficRules.Instance.AppRule(app.Exe) : null;
        if (rule != null) Blocking.Undo(_shell, rule);
        Pull();
    }

    // Right click a line: block its app, or its site for this app or for every app.
    private void OnFlowMenu(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(FlowList, source) is not ListBoxItem { Content: FlowRow row })
        {
            e.Handled = true;
            return;
        }
        var menu = FlowList.ContextMenu!;
        menu.Items.Clear();
        var f = row.Flow;
        AddBlockItems(menu, f.App, f.Host);
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
        menu.Items.Add(MenuEntry("See details", () => OpenRecord(row)));
        menu.Items.Add(MenuEntry("Copy web address", () => CopyText(f.Url, "Web address copied")));
    }

    // Right click a top site: block it for the picked app or for every app.
    private void OnSiteMenu(object sender, ContextMenuEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SiteRow site)
        {
            e.Handled = true;
            return;
        }
        var menu = TopSites.ContextMenu!;
        menu.Items.Clear();
        AddBlockItems(menu, SelectedApp, site.Host);
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
        menu.Items.Add(MenuEntry("Copy website name", () => CopyText(site.Host, "Website name copied")));
    }

    private void AddBlockItems(ContextMenu menu, AppTraffic? app, string host)
    {
        var rules = TrafficRules.Instance;
        if (app != null && Blocking.CanBlock(app))
        {
            var appRule = rules.AppRule(app.Exe);
            menu.Items.Add(appRule == null
                ? MenuEntry($"Block {app.Name}", () => { Blocking.App(_shell, app); Pull(); }, danger: true)
                : MenuEntry($"Unblock {app.Name}", () => { Blocking.Undo(_shell, appRule); Pull(); }));
        }

        // Already covered by a site rule: offer the way back instead of a second rule.
        var siteRules = rules.SiteRules(app?.Exe ?? "", host);
        foreach (var r in siteRules)
            menu.Items.Add(MenuEntry($"Unblock {r.Label}", () => { Blocking.Undo(_shell, r); Pull(); }));
        if (siteRules.Count > 0) return;

        var name = Blocking.Short(host);
        if (app != null && Blocking.CanBlock(app))
            menu.Items.Add(MenuEntry($"Block {name} in {app.Name} only",
                () => { Blocking.Site(_shell, host, false, app); Pull(); }, danger: true));
        menu.Items.Add(MenuEntry($"Block {name} for every app", () => { Blocking.Site(_shell, host, false, null); Pull(); }, danger: true));
        var site = TrafficRules.SiteOf(host);
        if (!site.Equals(host, StringComparison.OrdinalIgnoreCase))
            menu.Items.Add(MenuEntry($"Block all of {site} for every app",
                () => { Blocking.Site(_shell, site, true, null); Pull(); }, danger: true));
    }

    private static MenuItem MenuEntry(string text, Action act, bool danger = false)
    {
        // A TextBlock header keeps an underscore in a name from turning into an access key.
        var item = new MenuItem { Header = new TextBlock { Text = text } };
        if (danger) item.Foreground = Ui.Brush("B.Red");
        item.Click += (_, _) => act();
        return item;
    }

    private void CopyText(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            _shell?.Toast(done);
        }
        catch
        {
            _shell?.Toast("Could not reach the clipboard, try again", ToastKind.Error);
        }
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        var path = SelectedApp?.Path;
        if (string.IsNullOrEmpty(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { _shell?.Toast("Could not open the folder", ToastKind.Error); }
    }

    private void ScrollToEnd()
    {
        if (_shown.Count > 0) FlowList.ScrollIntoView(_shown[^1]);
    }

    private void OnCopy(object sender, RoutedEventArgs e) => CopyRows(_shown.ToList(), "Lines copied");

    private void CopySelected()
    {
        var selected = FlowList.SelectedItems.Cast<FlowRow>().OrderBy(r => r.Flow.Id).ToList();
        CopyRows(selected.Count > 0 ? selected : _shown.ToList(), "Lines copied");
    }

    private void CopyRows(List<FlowRow> rows, string done)
    {
        if (rows.Count == 0)
        {
            _shell?.Toast("There is nothing to copy yet", ToastKind.Info);
            return;
        }
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(r => r.Export())));
            _shell?.Toast(done);
        }
        catch
        {
            _shell?.Toast("Could not reach the clipboard, try again", ToastKind.Error);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var rows = _shown.ToList();
        if (rows.Count == 0)
        {
            _shell?.Toast("There is nothing to save yet", ToastKind.Info);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"PdaNet apps {DateTime.Now:yyyyMMdd HHmm}.csv",
            Filter = "Spreadsheet (*.csv)|*.csv|Text files (*.txt)|*.txt",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            bool text = dlg.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(dlg.FileName, text ? string.Join(Environment.NewLine, rows.Select(r => r.Export())) : Csv(rows),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _shell?.Toast(text ? "Saved to a text file" : "Saved, open the file in Excel");
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Could not save the apps file: {ex.Message}");
            _shell?.Toast("Could not save the file, try another folder", ToastKind.Error);
        }
    }

    private static string Csv(List<FlowRow> rows)
    {
        static string Q(string? s) =>
            string.IsNullOrEmpty(s) ? "" : s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

        var sb = new StringBuilder();
        sb.AppendLine("Started,App,Program file,Process number,Privacy,Method,Web address,Website,Port,Result,Answer code,Kind of data,Sent bytes,Received bytes,Time taken ms,Problem");
        foreach (var r in rows)
        {
            var f = r.Flow;
            sb.AppendJoin(',', new[]
            {
                f.Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Q(f.App.Name), Q(f.App.Exe),
                f.Pid.ToString(CultureInfo.InvariantCulture), f.Secure ? "Private" : "Open", f.Method, Q(f.Url), Q(f.Host),
                f.Port.ToString(CultureInfo.InvariantCulture), Q(r.Result), f.Status > 0 ? f.Status.ToString(CultureInfo.InvariantCulture) : "",
                Q(f.ContentType), f.BytesUp.ToString(CultureInfo.InvariantCulture), f.BytesDown.ToString(CultureInfo.InvariantCulture),
                ((long)f.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture), Q(f.BlockedBy ?? f.Error),
            });
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _monitor.Clear();
        _flows.Clear();
        _live.Clear();
        _appByKey.Clear();
        _apps.Clear();
        _apps.Add(_allRow);
        AppList.SelectedItem = _allRow;
        Rebuild();
        _emptyMode = null;
        Pull();
        _shell?.Toast("Apps list cleared", ToastKind.Info);
    }

    // ============================ Formatting ============================
    internal static string Count(int n, string word) => n == 1 ? $"1 {word}" : $"{n:N0} {word}s";

    internal static string MonogramOf(string name)
    {
        var c = name.FirstOrDefault(char.IsLetterOrDigit);
        return c == default ? "?" : char.ToUpperInvariant(c).ToString();
    }

    internal static string Ago(DateTime t)
    {
        var s = (DateTime.Now - t).TotalSeconds;
        if (s < 45) return "just now";
        if (s < 3600) return $"{Math.Max(1, (int)(s / 60))} min ago";
        if (s < 86400) return $"{(int)(s / 3600)} h ago";
        return t.ToString("d MMM", CultureInfo.CurrentCulture);
    }

    internal static string Span(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return $"{Math.Max(1, t.TotalMilliseconds):0} ms";
        if (t.TotalSeconds < 60) return $"{t.TotalSeconds:0.0} s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }
}
