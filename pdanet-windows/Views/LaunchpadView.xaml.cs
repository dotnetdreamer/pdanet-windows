using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using PdaNetPortable.Core;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
using PdaNetPortable.Share;
using Button = System.Windows.Controls.Button;
using DataObject = System.Windows.DataObject;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>
/// The Launchpad: apps that run right inside PdaNet. The PdaNet browser, with a box to search or
/// open a web address straight away, the web apps pinned for one click and the websites opened
/// lately, and screen share, to see another PC or show this one with a code.
/// </summary>
public partial class LaunchpadView : UserControl, IPage
{
    private readonly BrowserStore _store = BrowserStore.Instance;
    private readonly ConnectionManager _cm = ConnectionManager.Instance;
    private readonly AppConfig _cfg = ConfigManager.Instance.Config;
    private readonly ShareSession _share = ShareSession.Instance;
    private bool _formatting, _shareBreathing;
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
        _cm.StateChanged += _ => Dispatcher.BeginInvoke(() =>
        {
            UpdateStatus();
            UpdateShare();
        });
        _share.Changed += UpdateShare;
        _share.Ended += OnShareEnded;
        AllowControl.IsChecked = _cfg.ShareAllowControl;
        RoutePhone.IsChecked = _cfg.ShareViaPhone;
        RouteDirect.IsChecked = !_cfg.ShareViaPhone;
        UpdateShare();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                if (_stale) Load();
                CheckRuntime();
                UpdateStatus();
                UpdateShare();
                StartOrbit();
                _tick.Start();
            }
            else
            {
                StopOrbit();
                _tick.Stop();
                _breathing = null;
                _shareBreathing = false;
                WaitSpin.BeginAnimation(RotateTransform.AngleProperty, null);
                ShareRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ShareRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ShareRing.BeginAnimation(OpacityProperty, null);
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

    private void CopyText(string text, string done = "Web address copied")
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            _shell?.Toast(done);
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

    // ============================ Screen share ============================
    private bool ViaPhone => RoutePhone.IsChecked == true;

    private void OnShare(object sender, RoutedEventArgs e)
    {
        var problem = _share.Share(ViaPhone);
        if (problem != null) _shell?.Toast(problem, ToastKind.Warning);
    }

    private void OnJoin(object sender, RoutedEventArgs e)
    {
        var problem = _share.Join(CodeBox.Text, ViaPhone);
        if (problem != null)
        {
            ShowJoinHint(problem, "B.Red");
            Ui.Shake(CodeBox);
            return;
        }
        _formatting = true;
        CodeBox.Clear();
        _formatting = false;
        ShowJoinHint("Ten digits, the spaces come by themselves", "B.Text3");
    }

    private void OnStop(object sender, RoutedEventArgs e) => _share.Stop();

    private void OnShowRemote(object sender, RoutedEventArgs e) => _shell?.ShowRemote();

    private void OnAllowControl(object sender, RoutedEventArgs e)
    {
        bool allow = ((ToggleButton)sender).IsChecked == true;
        if (_share.Active) _share.SetAllowControl(allow);
        else
        {
            _cfg.ShareAllowControl = allow;
            ConfigManager.Instance.SaveConfig();
        }
        AllowControl.IsChecked = ActiveControl.IsChecked = allow;
    }

    private void OnRoute(object sender, RoutedEventArgs e)
    {
        _cfg.ShareViaPhone = ViaPhone;
        ConfigManager.Instance.SaveConfig();
        UpdateShare();
    }

    private void OnCopyCode(object sender, RoutedEventArgs e)
    {
        if (_share.Code is { Length: 10 } code) CopyText(FormatCode(code), "Code copied, send it to the other person");
    }

    private void OnShareEnded(string text, bool error)
    {
        if (!error) return;
        // A code that did not work stays in view, so it is easy to fix.
        if (_share.Role == ShareRole.None && text.Contains("code", StringComparison.OrdinalIgnoreCase)) ShowJoinHint(text, "B.Red");
    }

    // The code box keeps digits only and adds the spaces itself: "482 619 3075".
    private void OnCodeInput(object sender, TextCompositionEventArgs e)
    {
        if (!e.Text.All(char.IsAsciiDigit)) e.Handled = true;
    }

    private void OnCodePaste(object sender, DataObjectPastingEventArgs e)
    {
        var text = e.SourceDataObject.GetData(System.Windows.DataFormats.UnicodeText) as string ?? "";
        e.CancelCommand();
        var digits = new string(text.Where(char.IsAsciiDigit).Take(10).ToArray());
        if (digits.Length > 0) CodeBox.Text = digits;
    }

    private void OnCodeChanged(object sender, TextChangedEventArgs e)
    {
        if (_formatting) return;
        var digits = new string(CodeBox.Text.Where(char.IsAsciiDigit).Take(10).ToArray());
        var formatted = FormatCode(digits);
        if (CodeBox.Text != formatted)
        {
            _formatting = true;
            CodeBox.Text = formatted;
            CodeBox.CaretIndex = formatted.Length;
            _formatting = false;
        }
        JoinBtn.Style = (Style)FindResource(digits.Length == 10 ? "Btn.Primary" : "Btn.Secondary");
        ShowJoinHint(digits.Length == 10 ? "Press Enter or Connect" : "Ten digits, the spaces come by themselves", "B.Text3");
    }

    private void OnCodeKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnJoin(sender, e);
    }

    private void ShowJoinHint(string text, string brush)
    {
        JoinHint.Text = text;
        JoinHint.Foreground = Ui.Brush(brush);
    }

    /// <summary>Ten digits as three groups, the way a phone number is read out.</summary>
    internal static string FormatCode(string digits) => digits.Length switch
    {
        <= 3 => digits,
        <= 6 => $"{digits[..3]} {digits[3..]}",
        _ => $"{digits[..3]} {digits[3..6]} {digits[6..]}",
    };

    /// <summary>Shows where the session is: share or connect, the code, or the live session.</summary>
    private void UpdateShare()
    {
        var s = _share;
        bool host = s.Role == ShareRole.Host;
        bool waiting = host && s.Phase is SharePhase.Starting or SharePhase.Waiting;
        ShareIdle.Visibility = s.Active ? Visibility.Collapsed : Visibility.Visible;
        ShareWaiting.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        ShareActive.Visibility = s.Active && !waiting ? Visibility.Visible : Visibility.Collapsed;

        var (badge, badgeKey) = s.Phase switch
        {
            SharePhase.Idle => ("READY", "B.Accent"),
            SharePhase.Starting or SharePhase.Waiting when host => ("WAITING", "B.Cyan"),
            SharePhase.Live => (host ? "SHARING" : "VIEWING", "B.Accent"),
            SharePhase.Reconnecting => ("RECONNECTING", "B.Amber"),
            _ => ("CONNECTING", "B.Amber"),
        };
        var badgeColor = ((SolidColorBrush)Ui.Brush(badgeKey)).Color;
        ShareBadgeText.Text = badge;
        ShareBadgeText.Foreground = Ui.Brush(badgeKey);
        ShareBadge.Background = new SolidColorBrush(Ui.WithAlpha(badgeColor, 0x1A));
        ShareBadge.BorderBrush = new SolidColorBrush(Ui.WithAlpha(badgeColor, 0x66));

        if (waiting)
        {
            CodeText.Text = s.Code is { Length: 10 } code ? FormatCode(code) : "··· ··· ····";
            CopyCodeBtn.IsEnabled = s.Code != null;
            WaitText.Text = s.Phase == SharePhase.Starting ? "Getting a code"
                : s.RelaysTotal > 0 ? $"Waiting for the other PC, reachable {s.RelaysUp} of {s.RelaysTotal} ways"
                : "Waiting for the other PC";
            WaitNotice.Text = s.Notice ?? "";
            WaitNotice.Visibility = string.IsNullOrEmpty(s.Notice) ? Visibility.Collapsed : Visibility.Visible;
        }
        WaitSpin.BeginAnimation(RotateTransform.AngleProperty, waiting && IsVisible ? Ui.Loop(0, 360, 0.9, 60) : null);

        if (s.Active && !waiting)
        {
            string peer = s.Peer ?? "the other PC";
            ActiveTitle.Text = (host, s.Phase) switch
            {
                (true, SharePhase.Live) => $"Sharing your screen with {peer}",
                (true, SharePhase.Reconnecting) => $"Reconnecting with {peer}",
                (true, _) => $"Connecting with {peer}",
                (false, SharePhase.Starting) => $"Looking for {FormatCode(s.Code ?? "")}",
                (false, SharePhase.Pairing) => "Found the other PC, checking the code",
                (false, SharePhase.Connecting) => $"Connecting to {peer}",
                (false, SharePhase.Live) => $"Viewing {peer}",
                (false, _) => $"Reconnecting to {peer}",
            };
            if (s.Phase == SharePhase.Live && s.LiveSince is { } since)
            {
                var detail = $"{char.ToUpper(ShareSession.PathWords(s.Path)[0])}{ShareSession.PathWords(s.Path)[1..]}, for {Fmt.Uptime((DateTime.Now - since).TotalSeconds)}";
                if (!host && !s.RemoteFlags.HasFlag(HostFlags.ControlAllowed)) detail += ", you can watch only";
                if (host && !s.AllowControl) detail += ", they can watch only";
                ActiveDetail.Text = detail;
                var st = s.Stats;
                int kbps = host ? st.SendKbps : st.RecvKbps;
                ActiveStats.Text = $"{st.Rtt:0} ms · {(kbps >= 1000 ? $"{kbps / 1000.0:0.0} Mbit/s" : $"{kbps} kbit/s")} · {st.Fps} fps" +
                                   (string.IsNullOrEmpty(st.Detail) ? "" : $" · {st.Detail}");
            }
            else
            {
                ActiveDetail.Text = s.Phase == SharePhase.Reconnecting ? "The connection dropped, PdaNet is finding a new way"
                    : "Finding the shortest way between the two PCs";
                ActiveStats.Text = "";
            }
            HostControls.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
            ActiveControl.IsChecked = s.AllowControl;
            ShowWindowBtn.Visibility = host ? Visibility.Collapsed : Visibility.Visible;
            EndBtn.Content = host ? "Stop sharing" : "Disconnect";
            ShareLiveDot.Fill = Ui.Brush(s.Phase == SharePhase.Live ? "B.Accent" : "B.Amber");
        }

        // The route: the phone only works while PdaNet is connected to it.
        bool phoneReady = _cm.IsConnected;
        RouteDirect.IsEnabled = RoutePhone.IsEnabled = !s.Active;
        if (!phoneReady && !s.Active) RoutePhone.IsEnabled = ViaPhone; // keep it pickable to switch back
        ShareRouteText.Text = s.Active
            ? s.ViaPhone ? "This session goes through your phone" : "This session goes straight between the PCs"
            : ViaPhone
                ? phoneReady ? $"Through your phone, with {Friendly.ModeName(_cm.CurrentMode)}" : "Connect PdaNet to your phone first"
                : "Straight between the PCs, the shortest way";
        ShareRouteText.Foreground = Ui.Brush(!s.Active && ViaPhone && !phoneReady ? "B.Amber" : "B.Text3");
        ShareBtn.IsEnabled = !ViaPhone || phoneReady;

        BreatheShare(waiting || s.Phase == SharePhase.Live);
    }

    // A slow ring around the screen icon while a code waits or a session runs.
    private void BreatheShare(bool on)
    {
        if (_shareBreathing == on || !IsVisible) return;
        _shareBreathing = on;
        if (!on)
        {
            ShareRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ShareRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ShareRing.BeginAnimation(OpacityProperty, null);
            ShareRing.Opacity = 0;
            return;
        }
        var grow = Ui.Loop(0.9, 1.45, 2.4, 30, Ui.EaseOut);
        ShareRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ShareRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ShareRing.BeginAnimation(OpacityProperty, Ui.Loop(0.8, 0, 2.4, 30, Ui.EaseOut));
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
