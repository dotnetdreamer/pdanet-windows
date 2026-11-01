using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PdaNetPortable.Core;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>
/// The Launchpad: apps that run right inside PdaNet. For now the PdaNet browser, with a box to
/// search or open a web address straight away, the web apps pinned for one click, and the
/// websites opened lately.
/// </summary>
public partial class LaunchpadView : UserControl, IPage
{
    private readonly BrowserStore _store = BrowserStore.Instance;
    private readonly ConnectionManager _cm = ConnectionManager.Instance;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _confirmTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private MainWindow? _shell;
    private bool _stale, _confirming, _orbiting;
    private bool? _breathing;
    private string? _runtime;

    public LaunchpadView()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => UpdateStatus();
        _confirmTimer.Tick += (_, _) => ResetClear();
    }

    public void Attach(MainWindow shell)
    {
        _shell = shell;
        _store.Changed += () => Changed();
        SiteIcons.Updated += _ => Changed();
        _cm.StateChanged += _ => Dispatcher.BeginInvoke(UpdateStatus);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                if (_stale) Load();
                CheckRuntime();
                UpdateStatus();
                StartOrbit();
                _tick.Start();
            }
            else
            {
                StopOrbit();
                _tick.Stop();
                _breathing = null;
                OrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                OrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                OrbRing.BeginAnimation(OpacityProperty, null);
            }
        };
        Load();
        CheckRuntime();
        UpdateStatus();
    }

    public void OnShown()
    {
        Load();
        UpdateStatus();
    }

    public void Refresh()
    {
        Load();
        CheckRuntime();
        UpdateStatus();
        _shell?.Toast("Launchpad refreshed", ToastKind.Info);
    }

    // ============================ Lists ============================
    private void Changed()
    {
        if (IsVisible) Load();
        else _stale = true;
    }

    private void Load()
    {
        _stale = false;
        var apps = _store.State.WebApps.Select(WebRow.Of).ToList();
        AppTiles.ItemsSource = apps;
        AppTiles.Visibility = apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppsEmpty.Visibility = apps.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        AppCount.Text = apps.Count > 0 ? apps.Count.ToString() : "";

        var recent = _store.RecentWebsites(7).Select(WebRow.Of).ToList();
        RecentList.ItemsSource = recent;
        RecentEmpty.Visibility = recent.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        ClearRecentBtn.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EngineText.Text = $"Searching with {_store.Engine.Name}";
    }

    // ============================ Status ============================
    private void CheckRuntime()
    {
        _runtime = WebRuntime.Version();
        bool ready = _runtime != null;
        OpenBtn.Content = ready ? "Open browser" : "Get WebView2";
        OpenBtn.Tag = ready ? "" : "";
        OpenBtn.ToolTip = ready ? "Open the PdaNet browser" : "Download WebView2 from Microsoft";
        HeroBody.Text = ready
            ? "Browse the web in a separate window with tabs, your web apps one click away, and sign ins kept apart from Edge and Chrome"
            : "The browser needs Microsoft Edge WebView2, a free part of Windows from Microsoft for showing websites inside apps, install WebView2, then come back here";
        GoBox.IsEnabled = ready;
        GoBox.ToolTip = ready ? null : "Install WebView2 first";
    }

    /// <summary>Brings the pill, the badge and the line under the search box up to date.</summary>
    public void UpdateStatus()
    {
        var browser = _shell?.Browser;
        var (text, key) = browser != null ? browser.RouteNow
            : _cm.State switch
            {
                ConnectionState.Connected => ($"PdaNet online, {Friendly.ModeName(_cm.CurrentMode)}", "B.Cyan"),
                ConnectionState.Connecting => ("PdaNet connecting", "B.Amber"),
                ConnectionState.Disconnecting => ("PdaNet disconnecting", "B.Amber"),
                ConnectionState.Error => ("PdaNet needs attention", "B.Red"),
                _ => ("PdaNet offline", "B.Text3"),
            };
        var brush = Ui.Brush(key);
        var color = ((SolidColorBrush)brush).Color;
        RouteText.Text = text;
        RouteDot.Fill = brush;
        RoutePill.Background = new SolidColorBrush(Ui.WithAlpha(color, 0x18));
        RoutePill.BorderBrush = new SolidColorBrush(Ui.WithAlpha(color, 0x55));
        RoutePill.ToolTip = browser != null ? browser.RouteTip : "Open the browser, the bar at the bottom of the browser shows how the browser goes online";

        bool open = browser != null;
        int tabs = browser?.TabCount ?? 0;
        if (_runtime == null)
        {
            SetBadge("NEEDS WEBVIEW2", "B.Amber");
            LiveText.Text = "WebView2 is not on this PC yet";
            LiveDot.Fill = Ui.Brush("B.Amber");
        }
        else if (open)
        {
            SetBadge(tabs == 1 ? "OPEN · 1 TAB" : $"OPEN · {tabs} TABS", "B.Accent");
            LiveText.Text = tabs == 1 ? "Open with 1 tab, press Open browser to bring the window forward" : $"Open with {tabs} tabs, press Open browser to bring the window forward";
            LiveDot.Fill = Ui.Brush("B.Accent");
        }
        else
        {
            SetBadge("READY", "B.Accent");
            LiveText.Text = "Ready, type below and press Enter to go straight to a website";
            LiveDot.Fill = Ui.Brush("B.Text3");
        }
        Breathe(open);
    }

    private void SetBadge(string text, string key)
    {
        var color = Ui.Color(key == "B.Amber" ? "C.Amber" : "C.Accent");
        StateText.Text = text;
        StateText.Foreground = Ui.Brush(key);
        StateBadge.Background = new SolidColorBrush(Ui.WithAlpha(color, 0x1A));
        StateBadge.BorderBrush = new SolidColorBrush(Ui.WithAlpha(color, 0x66));
    }

    // ============================ Motion ============================
    private void StartOrbit()
    {
        if (_orbiting) return;
        _orbiting = true;
        SatelliteTurn.BeginAnimation(RotateTransform.AngleProperty, Ui.Loop(-38, 322, 26, 24));
    }

    private void StopOrbit()
    {
        _orbiting = false;
        SatelliteTurn.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    // A slow ring around the globe while the browser is open, like the radar on the Apps page.
    private void Breathe(bool on)
    {
        if (_breathing == on || !IsVisible) return;
        _breathing = on;
        if (!on)
        {
            OrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            OrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            OrbRing.BeginAnimation(OpacityProperty, null);
            OrbRing.Opacity = 0;
            return;
        }
        var grow = Ui.Loop(0.9, 1.5, 2.4, 30, Ui.EaseOut);
        OrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        OrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        OrbRing.BeginAnimation(OpacityProperty, Ui.Loop(0.8, 0, 2.4, 30, Ui.EaseOut));
    }

    // ============================ Actions ============================
    private void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        if (_runtime == null)
        {
            CheckRuntime();
            if (_runtime == null)
            {
                OpenLink(WebRuntime.Download);
                _shell?.Toast("Install WebView2, then press Open browser again", ToastKind.Info);
                return;
            }
            UpdateStatus();
        }
        _shell?.OpenBrowser();
    }

    private void OnGoKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var text = GoBox.Text.Trim();
        if (text.Length == 0)
        {
            _shell?.OpenBrowser();
            return;
        }
        var url = _store.Resolve(text, out var plain);
        GoBox.Clear();
        _shell?.OpenBrowser(url, plain: plain);
    }

    private void OnOpenWebApp(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WebRow { App: { } app })
            _shell?.OpenBrowser(app.Url, reuse: true);
    }

    private void OnOpenRecent(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WebRow row) _shell?.OpenBrowser(row.Url);
    }

    private void OnAddWebApp(object sender, RoutedEventArgs e) => _shell?.OpenWebAppSheet(null);

    private void OnWebAppMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { DataContext: WebRow { App: { } app }, ContextMenu: { } menu })
        {
            e.Handled = true;
            return;
        }
        menu.Items.Clear();
        menu.Items.Add(MenuEntry("Open", () => _shell?.OpenBrowser(app.Url, reuse: true)));
        menu.Items.Add(MenuEntry("Open in a new tab", () => _shell?.OpenBrowser(app.Url)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
        menu.Items.Add(MenuEntry("Edit", () => _shell?.OpenWebAppSheet(app)));
        menu.Items.Add(MenuEntry("Copy web address", () => CopyText(app.Url)));
        menu.Items.Add(MenuEntry("Remove", () =>
        {
            _store.RemoveWebApp(app);
            _shell?.Toast($"{app.Name} removed from your web apps", ToastKind.Info);
        }, danger: true));
    }

    private void OnRecentMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { DataContext: WebRow { Site: { } site } row, ContextMenu: { } menu })
        {
            e.Handled = true;
            return;
        }
        menu.Items.Clear();
        menu.Items.Add(MenuEntry("Open", () => _shell?.OpenBrowser(site.Url)));
        if (_store.WebAppFor(site.Url) == null)
            menu.Items.Add(MenuEntry("Pin as a web app", () =>
                _shell?.OpenWebAppSheet(null, site.Url, BrowserStore.NameFor(site.Url, site.Title))));
        menu.Items.Add(MenuEntry("Copy web address", () => CopyText(site.Url)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
        menu.Items.Add(MenuEntry($"Remove {row.Detail} from recent", () => _store.Forget(row.Detail), danger: true));
    }

    // Clearing the list takes a second click, like unblocking everything.
    private void OnClearRecent(object sender, RoutedEventArgs e)
    {
        if (!_confirming)
        {
            _confirming = true;
            ClearRecentBtn.Content = "Click again to clear";
            _confirmTimer.Start();
            return;
        }
        ResetClear();
        _store.ClearRecent();
        _shell?.Toast("Recent websites cleared", ToastKind.Info);
    }

    private void ResetClear()
    {
        _confirmTimer.Stop();
        _confirming = false;
        ClearRecentBtn.Content = "Clear";
    }

    private void CopyText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            _shell?.Toast("Web address copied");
        }
        catch
        {
            _shell?.Toast("Could not reach the clipboard, try again", ToastKind.Error);
        }
    }

    private void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { _shell?.Toast("Could not open the web page, visit microsoft.com and search for WebView2", ToastKind.Error); }
    }

    private static MenuItem MenuEntry(string text, Action act, bool danger = false)
    {
        // A TextBlock header keeps an underscore in a name from turning into an access key.
        var item = new MenuItem { Header = new TextBlock { Text = text } };
        if (danger) item.Foreground = Ui.Brush("B.Red");
        item.Click += (_, _) => act();
        return item;
    }
}
