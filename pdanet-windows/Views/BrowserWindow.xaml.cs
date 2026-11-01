using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PdaNetPortable.Core;
using PdaNetPortable.Interop;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace PdaNetPortable.Views;

/// <summary>How the browser goes online right now, in the words of each place that shows it.</summary>
public sealed record BrowserRoute(string Strip, string Pill, string Title, string Body, string Key, string Glyph);

/// <summary>
/// The PdaNet browser: a separate window with tabs, an address bar that searches or opens a web
/// address, web apps one click away, find on page, zoom, downloads and full screen, on the
/// Microsoft Edge WebView2 Runtime. Sign ins and website data stay in data\browser, apart from
/// Edge and Chrome. The bar at the bottom says how the browser goes online: "Through PdaNet"
/// only once PdaNet's own records hold connections from the browser's processes.
/// </summary>
public partial class BrowserWindow : Window
{
    public static readonly DependencyProperty TabWidthProperty =
        DependencyProperty.Register(nameof(TabWidth), typeof(double), typeof(BrowserWindow), new PropertyMetadata(MaxTab));

    public static readonly DependencyProperty TabsCompactProperty =
        DependencyProperty.Register(nameof(TabsCompact), typeof(bool), typeof(BrowserWindow), new PropertyMetadata(false));

    private const double MaxTab = 236, MinTab = 44, CompactBelow = 92, FindWidth = 300;
    private const StringComparison Ignore = StringComparison.OrdinalIgnoreCase;
    private static readonly double[] ZoomSteps = { 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5 };

    private readonly MainWindow _shell;
    private readonly BrowserStore _store = BrowserStore.Instance;
    private readonly ConnectionManager _cm = ConnectionManager.Instance;
    private readonly ObservableCollection<BrowserTab> _tabs = new();
    private readonly List<(string Url, int Index)> _closed = new();
    private readonly DispatcherTimer _routeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _findTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private Task<CoreWebView2Environment>? _engine;
    private CoreWebView2Environment? _env;
    private Exception? _engineError;
    private BrowserTab? _active;
    private TextBox? _suggestFor;
    private WebApp? _pinApp;
    private string? _notice;
    private bool _pinAdding, _pinRemoved, _pinRight, _settingText, _freezeWidths, _restoreOffered, _sessionUsed;
    private bool _clearArmed, _noticeError, _fullScreen, _pageFullScreen, _engineLost, _startStale, _browsed;
    private WindowState _stateBeforeFull = WindowState.Normal;

    // Dragging a tab to a new place
    private BrowserTab? _dragTab;
    private Point _dragStart;
    private bool _dragging;

    // How the browser goes online: PdaNet's records of connections from the browser's own processes
    private readonly HashSet<int> _pids = new();
    private readonly List<TrafficFlow> _flows = new();
    private long _flowId, _bytesGone;
    private DateTime _since = DateTime.MaxValue, _firstLoad = DateTime.MaxValue;
    private bool _seen;
    private int _loads;

    // Downloads of this window
    private int _downloads, _downloading;

    public BrowserWindow(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        TabStrip.ItemsSource = _tabs;
        Place();

        SourceInitialized += (_, _) => NativeMethods.StyleWindow(new WindowInteropHelper(this).Handle);
        PreviewKeyDown += OnKey;
        PreviewMouseDown += OnMouseButtons;
        Deactivated += (_, _) => CloseSuggest();
        LocationChanged += (_, _) => CloseFlyouts();
        SizeChanged += (_, _) => CloseSuggest();
        Closing += OnClosingWindow;
        Closed += OnClosedWindow;

        _store.Changed += OnStoreChanged;
        SiteIcons.Updated += OnIconUpdated;
        _cm.StateChanged += OnConnectionState;
        _routeTimer.Tick += (_, _) => CheckRoute();
        _findTimer.Tick += (_, _) => { _findTimer.Stop(); StartFind(); };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notice = null; UpdateStrip(); };
        _tabs.CollectionChanged += (_, _) => { UpdateTabWidths(); UpdateStrip(); };

        _flowId = LatestFlowId();
        if (_cm.IsConnected) _since = DateTime.Now;
        _restoreOffered = _store.State.LastTabs.Count > 0;

        Loaded += (_, _) =>
        {
            _routeTimer.Start();
            UpdateTabWidths();
            // Start the engine right away, so the first website does not wait for the engine.
            _ = WarmUp();
        };

        Pick(AddTab());
        LoadStart();
        UpdateRoute();
        Logger.Instance.Info("Browser opened");
    }

    public double TabWidth
    {
        get => (double)GetValue(TabWidthProperty);
        set => SetValue(TabWidthProperty, value);
    }

    public bool TabsCompact
    {
        get => (bool)GetValue(TabsCompactProperty);
        set => SetValue(TabsCompactProperty, value);
    }

    /// <summary>How many tabs are open, for the Launchpad.</summary>
    public int TabCount => _tabs.Count;

    /// <summary>How the browser goes online, for the pill on the Launchpad.</summary>
    public (string Text, string Key) RouteNow
    {
        get
        {
            var r = Route();
            return (r.Pill, r.Key);
        }
    }

    public string RouteTip => Route().Body;

    // ============================ From PdaNet ============================
    /// <summary>
    /// Opens an address: in the start page tab when the picked tab shows the start page, else in
    /// a new tab. With <paramref name="reuse"/> a web app that is open already gets picked instead.
    /// </summary>
    public void Open(string url, bool reuse = false, string? plain = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (reuse && _store.WebAppFor(url) is { } app)
        {
            var hit = _tabs.FirstOrDefault(t => !t.IsStart && ReferenceEquals(_store.WebAppFor(t.PendingUrl ?? t.Url), app));
            if (hit != null)
            {
                Pick(hit);
                return;
            }
        }
        var tab = _active is { IsStart: true } ? _active : AddTab(atEnd: true);
        Pick(tab);
        Go(tab, url, plain, focus: true);
    }

    /// <summary>Brings the window forward, out of the taskbar when minimized.</summary>
    public void Reveal()
    {
        if (WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(this);
        Show();
        Activate();
    }

    // ============================ Engine ============================
    private Task<CoreWebView2Environment> Engine() => _engine ??= StartEngine();

    private async Task<CoreWebView2Environment> StartEngine()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.BrowserDir);
            var options = new CoreWebView2EnvironmentOptions
            {
                // Work accounts on this PC sign in to Microsoft websites by themselves, as in Edge.
                AllowSingleSignOnUsingOSPrimaryAccount = true,
                ScrollBarStyle = CoreWebView2ScrollbarStyle.FluentOverlay,
            };
            if (_store.State.OpenUnsafeSites)
                // Turn off the newest connection locking step that some company networks break,
                // the cause of "this connection is not secure" with no certificate to allow. The
                // name of the step changes between versions, so a few names are turned off at once,
                // an unknown name is ignored.
                options.AdditionalBrowserArguments =
                    "--disable-features=PostQuantumKyber,X25519Kyber768Draft00,X25519MLKEM768";
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.BrowserDir, options);
            _env = env;
            _engineError = null;
            Logger.Instance.Info($"Browser engine ready, WebView2 {env.BrowserVersionString}");
            return env;
        }
        catch (Exception e)
        {
            _engine = null; // the next try starts afresh
            _env = null;
            _engineError = e;
            Logger.Instance.Warning($"The browser could not start: {e.Message}");
            ShowActive();
            throw;
        }
    }

    private async Task WarmUp()
    {
        try { await Engine(); }
        catch { /* the problem panel explains, Try again starts over */ }
    }

    private void RetryEngine()
    {
        _engineError = null;
        _engine = null;
        ShowActive();
        var tab = _active;
        if (tab != null && !tab.IsStart && tab.View == null) Go(tab, tab.PendingUrl ?? tab.Url, focus: true);
        else _ = WarmUp();
    }

    /// <summary>After the engine stopped every tab went with the engine, start a new engine for the picked tab.</summary>
    private void RestartEngine()
    {
        _engineLost = false;
        _engine = null;
        _env = null;
        var tab = _active;
        if (tab != null && tab.View == null && tab.PendingUrl is { } url)
        {
            tab.PendingUrl = null;
            Go(tab, url, focus: true);
        }
        ShowActive();
        UpdateAll();
    }

    /// <summary>Makes the web view of a tab, once. Null when the engine or the tab could not start.</summary>
    private async Task<CoreWebView2?> EnsureView(BrowserTab tab)
    {
        if (tab.View != null)
        {
            try { if (tab.Ready != null) await tab.Ready; }
            catch { return null; }
            return tab.View?.CoreWebView2;
        }

        CoreWebView2Environment env;
        try { env = await Engine(); }
        catch { return null; }
        if (!_tabs.Contains(tab)) return null;
        if (tab.View != null) return await EnsureView(tab); // made while this call waited for the engine

        var view = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.White,
            Visibility = ReferenceEquals(tab, _active) ? Visibility.Visible : Visibility.Collapsed,
        };
        tab.View = view;
        Pages.Children.Insert(0, view);
        if (ReferenceEquals(tab, _active)) ShowActive();
        var ready = view.EnsureCoreWebView2Async(env);
        tab.Ready = ready;
        try { await ready; }
        catch (Exception e)
        {
            Logger.Instance.Warning($"A browser tab could not start: {e.Message}");
            DropView(tab);
            _engineError ??= e;
            ShowActive();
            return null;
        }
        if (!_tabs.Contains(tab) || !ReferenceEquals(tab.View, view)) return null;
        Wire(tab, view);
        return view.CoreWebView2;
    }

    private void Wire(BrowserTab tab, WebView2 view)
    {
        var core = view.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false; // the link under the mouse shows in the bar at the bottom instead
        if (_store.State.OpenUnsafeSites)
        {
            // Skip the website safety check that shows "blocked by your organization", and open a
            // website whose certificate does not check out instead of the red warning page. Both
            // need a recent WebView2 Runtime, an older one keeps the warnings.
            try { core.Settings.IsReputationCheckingRequired = false; } catch { }
            try { core.ServerCertificateErrorDetected += OnCertificateError; } catch { }
        }
        core.Profile.PreferredColorScheme = Scheme(_store.State.Colors);
        core.DefaultDownloadDialogCornerAlignment = CoreWebView2DefaultDownloadDialogCornerAlignment.TopRight;
        core.DefaultDownloadDialogMargin = new System.Drawing.Point(10, 6);

        core.NavigationStarting += (_, e) => OnNavigationStarting(tab, e);
        core.ContentLoading += (_, _) => { if (ReferenceEquals(tab, _active)) ProgressTo(0.62, 300, 0.92, 6000); };
        core.SourceChanged += (_, _) => OnSourceChanged(tab);
        core.HistoryChanged += (_, _) => { if (ReferenceEquals(tab, _active)) UpdateNav(); };
        core.NavigationCompleted += (_, e) => OnNavigationCompleted(tab, e);
        core.DocumentTitleChanged += (_, _) => OnTitleChanged(tab);
        core.FaviconChanged += (_, _) => _ = LoadIcon(tab);
        core.StatusBarTextChanged += (_, _) =>
        {
            tab.Status = core.StatusBarText ?? "";
            if (ReferenceEquals(tab, _active)) UpdateStrip();
        };
        core.NewWindowRequested += (_, e) => OnNewWindow(tab, e);
        core.WindowCloseRequested += (_, _) => CloseTab(tab, toOpener: true);
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (ReferenceEquals(tab, _active)) SetPageFullScreen(core.ContainsFullScreenElement);
        };
        core.ProcessFailed += (_, e) => OnProcessFailed(tab, e);
        core.IsDocumentPlayingAudioChanged += (_, _) => tab.Audio = core.IsDocumentPlayingAudio;
        core.IsMutedChanged += (_, _) => tab.Muted = core.IsMuted;
        core.DownloadStarting += (_, e) => OnDownloadStarting(e);
        core.ContextMenuRequested += (_, e) => OnPageMenu(tab, core, e);
        try
        {
            core.Find.MatchCountChanged += (_, _) => { if (ReferenceEquals(tab, _active)) UpdateFindCount(); };
            core.Find.ActiveMatchIndexChanged += (_, _) => { if (ReferenceEquals(tab, _active)) UpdateFindCount(); };
        }
        catch { /* an older WebView2 Runtime without find, the find box then stays quiet */ }
        view.ZoomFactorChanged += (_, _) => { if (ReferenceEquals(tab, _active)) UpdateZoom(); };
        // A click into the page closes the flyouts, the page is a window of its own that PdaNet cannot see clicks in.
        view.GotFocus += (_, _) => CloseFlyouts();
    }

    // Open a website even when its certificate does not check out (expired, wrong name, self signed).
    // WebView2 remembers the allow for the website for the rest of the run, so this runs once per website.
    private void OnCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        if (_store.State.OpenUnsafeSites) e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
    }

    private void DropView(BrowserTab tab)
    {
        var view = tab.View;
        if (view == null) return;
        tab.View = null;
        tab.Ready = null;
        Pages.Children.Remove(view);
        try { view.Dispose(); } catch { /* already gone */ }
    }

    // ============================ Tabs ============================
    private BrowserTab AddTab(BrowserTab? opener = null, bool atEnd = false)
    {
        var tab = new BrowserTab { Opener = opener };
        int at = _tabs.Count;
        if (opener != null && !atEnd && _tabs.Contains(opener))
        {
            // Tabs opened from a page line up right after that page, in the order they opened.
            at = _tabs.IndexOf(opener) + 1;
            while (at < _tabs.Count && ReferenceEquals(_tabs[at].Opener, opener)) at++;
        }
        _tabs.Insert(at, tab);
        return tab;
    }

    private void Pick(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        bool changed = !ReferenceEquals(_active, tab);
        if (changed)
        {
            CloseFind();
            CloseFlyouts();
            if (_pageFullScreen) SetFullScreen(false);
        }
        _active = tab;
        if (!ReferenceEquals(TabStrip.SelectedItem, tab)) TabStrip.SelectedItem = tab;
        TabStrip.ScrollIntoView(tab);
        if (tab.View == null && tab.PendingUrl is { } pending)
        {
            tab.PendingUrl = null;
            Go(tab, pending);
        }
        ShowActive();
        UpdateAll();
        if (!changed) return;
        UpdateProgressForTab();
        FocusContent();
    }

    private void FocusContent()
    {
        var tab = _active;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!ReferenceEquals(tab, _active) || tab == null) return;
            if (tab.View != null && !tab.Crashed) tab.View.Focus();
            else if (tab.IsStart) StartSearch.Focus();
        });
    }

    private void NewTab() => Pick(AddTab(atEnd: true));

    private void CloseTab(BrowserTab tab, bool byMouse = false, bool toOpener = false)
    {
        int i = _tabs.IndexOf(tab);
        if (i < 0) return;
        var url = tab.PendingUrl ?? tab.Url;
        if (BrowserStore.IsWeb(url))
        {
            _closed.Add((url, i));
            if (_closed.Count > 25) _closed.RemoveAt(0);
        }
        bool wasActive = ReferenceEquals(tab, _active);
        if (wasActive)
        {
            CloseFind();
            CloseFlyouts();
            if (_pageFullScreen) SetFullScreen(false);
        }
        foreach (var t in _tabs)
            if (ReferenceEquals(t.Opener, tab)) t.Opener = null;
        if (byMouse) _freezeWidths = true;
        _tabs.RemoveAt(i);
        DropView(tab);
        if (_tabs.Count == 0)
        {
            Close();
            return;
        }
        if (wasActive)
        {
            var next = toOpener && tab.Opener != null && _tabs.Contains(tab.Opener) ? tab.Opener : _tabs[Math.Min(i, _tabs.Count - 1)];
            _active = null;
            Pick(next);
        }
    }

    private void ReopenClosed()
    {
        if (_closed.Count == 0) return;
        var (url, index) = _closed[^1];
        _closed.RemoveAt(_closed.Count - 1);
        var tab = new BrowserTab();
        _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), tab);
        Pick(tab);
        Go(tab, url, focus: true);
    }

    private void Duplicate(BrowserTab tab)
    {
        var copy = AddTab(tab);
        Pick(copy);
        Go(copy, tab.PendingUrl ?? tab.Url, focus: true);
    }

    private void CloseOthers(BrowserTab keep)
    {
        Pick(keep);
        foreach (var t in _tabs.Where(t => !ReferenceEquals(t, keep)).ToList()) CloseTab(t);
    }

    private void CloseRight(BrowserTab tab)
    {
        foreach (var t in _tabs.Skip(_tabs.IndexOf(tab) + 1).ToList()) CloseTab(t);
    }

    private void Cycle(int step)
    {
        if (_active == null || _tabs.Count < 2) return;
        int i = (_tabs.IndexOf(_active) + step + _tabs.Count) % _tabs.Count;
        Pick(_tabs[i]);
    }

    private void ToggleMute(BrowserTab tab)
    {
        var core = tab.View?.CoreWebView2;
        if (core != null) core.IsMuted = !core.IsMuted;
    }

    private void OnTabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TabStrip.SelectedItem is BrowserTab tab)
        {
            if (!ReferenceEquals(tab, _active)) Pick(tab);
        }
        else if (_active != null && _tabs.Contains(_active))
        {
            TabStrip.SelectedItem = _active;
        }
    }

    private void OnNewTab(object sender, RoutedEventArgs e) => NewTab();

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab) CloseTab(tab, byMouse: true);
        e.Handled = true;
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab) ToggleMute(tab);
        e.Handled = true;
    }

    private BrowserTab? TabAt(object source) =>
        source is DependencyObject d && ItemsControl.ContainerFromElement(TabStrip, d) is ListBoxItem { Content: BrowserTab tab } ? tab : null;

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        var tab = TabAt(e.OriginalSource);
        if (tab == null) return;
        if (e.ChangedButton == MouseButton.Middle)
        {
            CloseTab(tab, byMouse: true);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && Ancestor<Button>(e.OriginalSource as DependencyObject) == null)
        {
            _dragTab = tab;
            _dragStart = e.GetPosition(TabStrip);
            _dragging = false;
        }
    }

    // Drag a tab sideways to move the tab.
    private void OnTabDrag(object sender, MouseEventArgs e)
    {
        if (_dragTab == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        var p = e.GetPosition(TabStrip);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragStart.X) < 10) return;
            _dragging = true;
            Mouse.Capture(TabStrip, CaptureMode.SubTree);
        }
        var scroller = Find<ScrollViewer>(TabStrip);
        int to = (int)Math.Clamp((p.X + (scroller?.HorizontalOffset ?? 0)) / TabWidth, 0, _tabs.Count - 1);
        int from = _tabs.IndexOf(_dragTab);
        if (from >= 0 && to != from) _tabs.Move(from, to);
    }

    private void OnTabMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        bool was = _dragging;
        _dragTab = null;
        _dragging = false;
        if (was) Mouse.Capture(null);
    }

    private void OnTabStripLeave(object sender, MouseEventArgs e)
    {
        if (!_freezeWidths) return;
        _freezeWidths = false;
        UpdateTabWidths();
    }

    private void OnTabWheel(object sender, MouseWheelEventArgs e)
    {
        var scroller = Find<ScrollViewer>(TabStrip);
        if (scroller == null || scroller.ScrollableWidth <= 0) return;
        scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void OnTabMenu(object sender, ContextMenuEventArgs e)
    {
        var tab = TabAt(e.OriginalSource);
        if (tab == null || TabStrip.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }
        menu.Items.Clear();
        bool page = tab.View?.CoreWebView2 != null;
        menu.Items.Add(Entry("Reload", "", () => Reload(tab), page || tab.PendingUrl != null));
        menu.Items.Add(Entry("Duplicate", "", () => Duplicate(tab), !tab.IsStart));
        if (page) menu.Items.Add(Entry(tab.Muted ? "Turn the sound back on" : "Mute tab", "", () => ToggleMute(tab)));
        if (page && BrowserStore.IsWeb(tab.Url) && _store.WebAppFor(tab.Url) == null)
            menu.Items.Add(Entry("Pin website", "", () =>
            {
                Pick(tab);
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Star));
            }));
        menu.Items.Add(Line());
        menu.Items.Add(Entry("Close tab", "Ctrl+W", () => CloseTab(tab)));
        menu.Items.Add(Entry("Close other tabs", "", () => CloseOthers(tab), _tabs.Count > 1));
        menu.Items.Add(Entry("Close tabs to the right", "", () => CloseRight(tab), _tabs.IndexOf(tab) < _tabs.Count - 1));
    }

    private void OnTabAreaSize(object sender, SizeChangedEventArgs e)
    {
        _freezeWidths = false;
        UpdateTabWidths();
    }

    /// <summary>Tabs share the room evenly, like other browsers, and turn into icons when there are many.</summary>
    private void UpdateTabWidths()
    {
        if (_freezeWidths || TabArea.ActualWidth <= 0) return;
        double room = TabArea.ActualWidth - 40 - 72; // the new tab button, and room to drag the window
        int n = Math.Max(1, _tabs.Count);
        double w = Math.Clamp(Math.Floor(room / n), MinTab, MaxTab);
        TabWidth = w;
        TabsCompact = w < CompactBelow;
        TabStrip.MaxWidth = Math.Max(MinTab, room);
    }

    // ============================ Pages ============================
    private async void Go(BrowserTab tab, string url, string? plain = null, bool focus = false)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        tab.Url = url;
        tab.PendingUrl = null;
        tab.HttpFallback = plain;
        tab.Crashed = false;
        if (tab.View == null) tab.Title = TitleFor(url);
        tab.Icon = SiteIcons.For(url);
        tab.Loading = true;
        if (ReferenceEquals(tab, _active))
        {
            ProgressStart();
            UpdateAll();
        }

        var core = await EnsureView(tab);
        if (core == null)
        {
            tab.Loading = false;
            if (ReferenceEquals(tab, _active))
            {
                ProgressHide();
                ShowActive();
                UpdateAll();
            }
            return;
        }
        try { core.Navigate(url); }
        catch (ArgumentException) { core.Navigate(_store.SearchUrl(url)); }
        if (focus && ReferenceEquals(tab, _active) && !Address.IsKeyboardFocused && !StartSearch.IsKeyboardFocused)
            tab.View?.Focus();
    }

    private void GoBack()
    {
        var core = _active?.View?.CoreWebView2;
        if (core?.CanGoBack == true) core.GoBack();
    }

    private void GoForward()
    {
        var core = _active?.View?.CoreWebView2;
        if (core?.CanGoForward == true) core.GoForward();
    }

    private void Reload(BrowserTab? tab = null, bool hard = false)
    {
        tab ??= _active;
        if (tab == null) return;
        if (_engineLost)
        {
            RestartEngine();
            return;
        }
        var core = tab.View?.CoreWebView2;
        if (core == null)
        {
            if (!tab.IsStart) Go(tab, tab.PendingUrl ?? tab.Url);
            return;
        }
        tab.Crashed = false;
        if (ReferenceEquals(tab, _active)) ShowActive();
        if (hard)
            core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}")
                .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        else core.Reload();
    }

    private void StopLoading() => _active?.View?.CoreWebView2?.Stop();

    private void OnNavigationStarting(BrowserTab tab, CoreWebView2NavigationStartingEventArgs e)
    {
        tab.Loading = true;
        tab.Crashed = false;
        if (!BrowserStore.HostOf(e.Uri).Equals(BrowserStore.HostOf(tab.Url), Ignore)) tab.Icon = SiteIcons.For(e.Uri);
        if (!ReferenceEquals(tab, _active)) return;
        ProgressStart();
        UpdateNav();
        UpdateStrip();
    }

    private void OnSourceChanged(BrowserTab tab)
    {
        var source = tab.View?.CoreWebView2?.Source;
        if (string.IsNullOrEmpty(source) || (source == "about:blank" && tab.Loading)) return;
        tab.Url = source;
        if (!ReferenceEquals(tab, _active)) return;
        UpdateAddress();
        UpdateStar();
        UpdateStrip();
        UpdateTitle();
    }

    private void OnNavigationCompleted(BrowserTab tab, CoreWebView2NavigationCompletedEventArgs e)
    {
        var core = tab.View?.CoreWebView2;
        if (!e.IsSuccess && tab.HttpFallback is { } plain && NoAnswer(e.WebErrorStatus) && core != null)
        {
            // The website did not answer on https, open the plain http address someone typed instead.
            tab.HttpFallback = null;
            Logger.Instance.Info($"{BrowserStore.HostOf(plain)} did not answer on https, trying http");
            core.Navigate(plain);
            return;
        }
        tab.HttpFallback = null;
        tab.Loading = false;
        tab.LoadedAt = DateTime.Now;
        if (e.IsSuccess && core != null && BrowserStore.IsWeb(core.Source))
        {
            _store.Visit(core.Source, core.DocumentTitle);
            _restoreOffered = false;
            _browsed = true;
            if (_cm.IsConnected)
            {
                _loads++;
                if (_firstLoad == DateTime.MaxValue) _firstLoad = DateTime.Now;
            }
        }
        if (!ReferenceEquals(tab, _active)) return;
        ProgressDone();
        UpdateAll();
    }

    private static bool NoAnswer(CoreWebView2WebErrorStatus status) => status is
        CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ConnectionAborted
        or CoreWebView2WebErrorStatus.ConnectionReset or CoreWebView2WebErrorStatus.ServerUnreachable
        or CoreWebView2WebErrorStatus.Timeout or CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse;

    private void OnTitleChanged(BrowserTab tab)
    {
        var core = tab.View?.CoreWebView2;
        if (core == null) return;
        var title = core.DocumentTitle;
        tab.Title = string.IsNullOrWhiteSpace(title) ? TitleFor(core.Source) : title.Trim();
        if (ReferenceEquals(tab, _active)) UpdateTitle();
        // Pages often set the title right after loading. Later changes, such as a count of new mail, stay off the disk.
        if (!tab.Loading && DateTime.Now - tab.LoadedAt < TimeSpan.FromSeconds(10)) _store.Retitle(core.Source, title);
    }

    private async Task LoadIcon(BrowserTab tab)
    {
        var core = tab.View?.CoreWebView2;
        if (core == null || string.IsNullOrEmpty(core.FaviconUri)) return;
        var source = core.Source;
        try
        {
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream == null) return;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var image = SiteIcons.Store(source, ms.ToArray());
            if (image != null && ReferenceEquals(tab.View?.CoreWebView2, core)
                && BrowserStore.HostOf(core.Source).Equals(BrowserStore.HostOf(source), Ignore))
                tab.Icon = image;
        }
        catch { /* no icon, the globe stays */ }
    }

    /// <summary>A link that opens a new window opens a new tab. Ctrl and click keeps the new tab behind.</summary>
    private async void OnNewWindow(BrowserTab from, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            bool behind = NativeMethods.CtrlDown();
            var tab = AddTab(from);
            tab.Url = e.Uri ?? "";
            tab.Title = TitleFor(tab.Url);
            tab.Icon = SiteIcons.For(tab.Url);
            tab.Loading = true;
            if (!behind) Pick(tab);
            var core = await EnsureView(tab);
            if (core != null) e.NewWindow = core;
            else tab.Loading = false;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning($"Could not open a new tab: {ex.Message}");
            e.Handled = true;
        }
        finally { deferral.Complete(); }
    }

    // The page menu says "new tab" where WebView2 says "new window".
    private void OnPageMenu(BrowserTab tab, CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var target = e.ContextMenuTarget;
        if (!target.HasLinkUri || !BrowserStore.IsWeb(target.LinkUri)) return;
        var link = target.LinkUri;
        var items = e.MenuItems;
        int at = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Name != "openLinkInNewWindow") continue;
            at = i;
            items.RemoveAt(i);
            break;
        }
        try
        {
            var item = core.Environment.CreateContextMenuItem("Open link in new tab", null, CoreWebView2ContextMenuItemKind.Command);
            item.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                var t = AddTab(tab);
                t.Title = TitleFor(link);
                Go(t, link);
            });
            items.Insert(at, item);
        }
        catch { /* the WebView2 menu stays as it is */ }
    }

    private void OnProcessFailed(BrowserTab tab, CoreWebView2ProcessFailedEventArgs e)
    {
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                if (_engineLost) return;
                Logger.Instance.Warning($"The browser engine stopped ({e.Reason}, exit code {e.ExitCode})");
                _engineLost = true;
                _engine = null;
                _env = null;
                foreach (var t in _tabs)
                {
                    if (t.View == null) continue;
                    if (BrowserStore.IsWeb(t.Url)) t.PendingUrl = t.Url;
                    t.Loading = false;
                    DropView(t);
                }
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                Logger.Instance.Warning($"A browser tab stopped ({e.ProcessFailedKind}, {e.Reason})");
                tab.Crashed = true;
                tab.Loading = false;
                break;
            default:
                return; // other helper processes of the engine start again by themselves
        }
        if (ReferenceEquals(tab, _active) || _engineLost)
        {
            ShowActive();
            UpdateAll();
            ProgressHide();
        }
    }

    // ============================ What shows ============================
    private void ShowActive()
    {
        var tab = _active;
        bool problem = _engineError != null || _engineLost || tab?.Crashed == true;
        foreach (var t in _tabs)
            if (t.View != null)
                t.View.Visibility = ReferenceEquals(t, tab) && !problem ? Visibility.Visible : Visibility.Collapsed;
        Problem.Visibility = problem ? Visibility.Visible : Visibility.Collapsed;
        bool start = !problem && tab?.View == null;
        StartPage.Visibility = start ? Visibility.Visible : Visibility.Collapsed;
        if (start && _startStale) LoadStart();
        if (problem) FillProblem();
    }

    private void FillProblem()
    {
        if (_engineError != null)
        {
            bool missing = _engineError is WebView2RuntimeNotFoundException || WebRuntime.Version() == null;
            if (missing)
                SetProblem("", "The browser needs WebView2",
                    "WebView2 is a free part of Windows from Microsoft for showing websites inside apps, install WebView2, then press Try again",
                    "Get WebView2", "Try again", "");
            else
                SetProblem("", "The browser could not start", "Close other copies of PdaNet, then press Try again",
                    "Try again", "Open PdaNet", Friendly.Clean(_engineError.Message));
            return;
        }
        if (_engineLost)
            SetProblem("", "The browser stopped", "The tabs closed together with the browser, restart the browser to open the tabs again",
                "Restart browser", "Close tab", "");
        else
            SetProblem("", "The page in this tab stopped", "Reload the page to try again", "Reload", "Close tab", "");
    }

    private void SetProblem(string glyph, string title, string body, string action, string second, string detail)
    {
        ProblemIcon.Text = glyph;
        ProblemTitle.Text = title;
        ProblemBody.Text = body;
        ProblemAction.Content = action;
        ProblemSecond.Content = second;
        ProblemDetail.Text = detail;
        ProblemDetail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnProblemAction(object sender, RoutedEventArgs e)
    {
        if (_engineError != null)
        {
            if (_engineError is WebView2RuntimeNotFoundException || WebRuntime.Version() == null) OpenExternal(WebRuntime.Download);
            else RetryEngine();
            return;
        }
        if (_engineLost) RestartEngine();
        else Reload();
    }

    private void OnProblemSecond(object sender, RoutedEventArgs e)
    {
        if (_engineError != null)
        {
            if (_engineError is WebView2RuntimeNotFoundException || WebRuntime.Version() == null) RetryEngine();
            else OpenPdaNet();
            return;
        }
        if (_active != null) CloseTab(_active);
    }

    private void UpdateAll()
    {
        UpdateAddress();
        UpdateNav();
        UpdateStar();
        UpdateZoom();
        UpdateTitle();
        UpdateDownloads();
        UpdateStrip();
    }

    private void UpdateTitle() =>
        Title = _active is { IsStart: false } tab ? $"{tab.Title} · PdaNet Browser" : "PdaNet Browser";

    private void UpdateNav()
    {
        var core = _active?.View?.CoreWebView2;
        BackBtn.IsEnabled = core?.CanGoBack == true;
        ForwardBtn.IsEnabled = core?.CanGoForward == true;
        bool loading = _active?.Loading == true;
        ReloadBtn.Content = loading ? "" : "";
        ReloadBtn.ToolTip = loading ? "Stop  (Esc)" : "Reload  (F5)";
        AutomationProperties.SetName(ReloadBtn, loading ? "Stop" : "Reload");
        ReloadBtn.IsEnabled = _active is { IsStart: false };
    }

    private void UpdateAddress()
    {
        var tab = _active;
        var url = tab == null || tab.IsStart ? "" : tab.PendingUrl ?? tab.Url;
        if (!Address.IsKeyboardFocused) SetText(Address, url);

        if (BrowserStore.IsWeb(url))
        {
            var shown = BrowserStore.Display(url);
            int cut = shown.IndexOfAny(new[] { '/', '?', '#' });
            ShownHost.Text = cut < 0 ? shown : shown[..cut];
            ShownRest.Text = cut < 0 ? "" : shown[cut..];
        }
        else
        {
            ShownHost.Text = url;
            ShownRest.Text = "";
        }
        bool pretty = !Address.IsKeyboardFocused && url.Length > 0;
        AddressShown.Visibility = pretty ? Visibility.Visible : Visibility.Collapsed;
        Address.Foreground = pretty ? System.Windows.Media.Brushes.Transparent : Ui.Brush("B.Text");

        SearchGlyph.Visibility = url.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Badge.Visibility = url.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (url.Length == 0) return;
        var (glyph, text, key, tip) =
            url.StartsWith("https://", Ignore) ? ("", "Private", "B.Cyan", "Private, only this browser and the website can read what goes between the two")
            : url.StartsWith("http://", Ignore) ? ("", "Open", "B.Amber", "Not private, people on the same network could read this data")
            : url.StartsWith("file:", Ignore) ? ("", "File", "B.Text2", "A file on this PC")
            : ("", "Page", "B.Text2", "A page of the browser");
        var brush = Ui.Brush(key);
        BadgeIcon.Text = glyph;
        BadgeText.Text = text;
        BadgeIcon.Foreground = brush;
        BadgeText.Foreground = brush;
        Badge.Background = new SolidColorBrush(Ui.WithAlpha(((SolidColorBrush)brush).Color, 0x1C));
        Badge.ToolTip = tip;
    }

    private void UpdateStar()
    {
        var tab = _active;
        bool web = tab?.View != null && BrowserStore.IsWeb(tab.Url);
        StarBtn.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
        bool pinned = web && _store.WebAppFor(tab!.Url) != null;
        StarBtn.Content = pinned ? "" : "";
        if (pinned) StarBtn.Foreground = Ui.Brush("B.Accent");
        else StarBtn.ClearValue(ForegroundProperty);
        StarBtn.ToolTip = pinned ? "Edit or remove this web app  (Ctrl+D)" : "Pin this website to your web apps  (Ctrl+D)";
        AutomationProperties.SetName(StarBtn, pinned ? "Edit this web app" : "Pin this website");
    }

    private void UpdateZoom()
    {
        double z = _active?.View?.ZoomFactor ?? 1;
        bool zoomed = _active?.View != null && Math.Abs(z - 1) > 0.001;
        ZoomPill.Visibility = zoomed ? Visibility.Visible : Visibility.Collapsed;
        ZoomPill.Content = $"{z * 100:0}%";
        UpdateStrip();
    }

    private void UpdateDownloads()
    {
        DownloadsBtn.Visibility = _downloads > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool busy = _downloading > 0;
        if ((DownloadDot.Visibility == Visibility.Visible) != busy)
        {
            DownloadDot.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            DownloadDot.BeginAnimation(OpacityProperty, busy ? Ui.Loop(1, 0.25, 0.8, 20) : null);
        }
        DownloadsBtn.ToolTip = !busy ? "Downloads  (Ctrl+J)"
            : _downloading == 1 ? "1 download running  (Ctrl+J)" : $"{_downloading} downloads running  (Ctrl+J)";
    }

    /// <summary>The bar at the bottom: how the browser goes online, privacy, the link under the mouse, data, zoom and tabs.</summary>
    private void UpdateStrip()
    {
        var tab = _active;
        var url = tab == null || tab.IsStart ? "" : tab.PendingUrl ?? tab.Url;
        bool secure = url.StartsWith("https://", Ignore);
        PrivacyPart.Visibility = BrowserStore.IsWeb(url) ? Visibility.Visible : Visibility.Collapsed;
        PrivacyText.Text = secure ? "PRIVATE" : "OPEN";
        PrivacyText.Foreground = Ui.Brush(secure ? "B.Cyan" : "B.Amber");

        string hover = _notice
            ?? (!string.IsNullOrEmpty(tab?.Status) ? tab!.Status
            : tab?.Loading == true && url.Length > 0 ? $"Opening {BrowserStore.HostOf(url)}" : "");
        HoverText.Text = hover;
        HoverText.Foreground = Ui.Brush(_notice == null ? "B.Text2" : _noticeError ? "B.Red" : "B.Accent");
        HoverSep.Visibility = hover.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        long bytes = Demo.Value("route") == "seen" ? 19_293_798 : _bytesGone + _flows.Sum(f => f.BytesUp + f.BytesDown);
        DataPart.Visibility = bytes > 0 ? Visibility.Visible : Visibility.Collapsed;
        DataText.Text = $"DATA {Fmt.Bytes(bytes)}";

        double z = tab?.View?.ZoomFactor ?? 1;
        ZoomPart.Visibility = tab?.View != null && Math.Abs(z - 1) > 0.001 ? Visibility.Visible : Visibility.Collapsed;
        ZoomText.Text = $"ZOOM {z * 100:0}%";

        TabsText.Text = _tabs.Count == 1 ? "1 TAB" : $"{_tabs.Count} TABS";
    }

    private void Notice(string text, bool error = false)
    {
        _notice = text;
        _noticeError = error;
        _noticeTimer.Stop();
        _noticeTimer.Start();
        UpdateStrip();
    }

    // ============================ Loading line ============================
    private void ProgressStart()
    {
        Progress.BeginAnimation(OpacityProperty, null);
        Progress.Opacity = 1;
        if (ProgressScale.ScaleX >= 0.99)
        {
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressScale.ScaleX = 0;
        }
        ProgressTo(0.3, 260, 0.85, 9000);
    }

    private void ProgressTo(double to, double ms, double then = 0, double thenMs = 0)
    {
        if (_active?.Loading != true || ProgressScale.ScaleX >= to) return;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), Ui.EaseOut));
        if (then > to)
            a.KeyFrames.Add(new EasingDoubleKeyFrame(then, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms + thenMs)),
                new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
    }

    private void ProgressDone()
    {
        var finish = new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = Ui.EaseOut };
        finish.Completed += (_, _) =>
        {
            if (_active?.Loading == true) return;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(340));
            fade.Completed += (_, _) =>
            {
                if (_active?.Loading == true) return;
                ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ProgressScale.ScaleX = 0;
            };
            Progress.BeginAnimation(OpacityProperty, fade);
        };
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, finish);
    }

    private void ProgressHide()
    {
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Progress.BeginAnimation(OpacityProperty, null);
        ProgressScale.ScaleX = 0;
        Progress.Opacity = 0;
    }

    private void UpdateProgressForTab()
    {
        ProgressHide();
        if (_active?.Loading != true) return;
        Progress.Opacity = 1;
        ProgressScale.ScaleX = 0.5;
        ProgressTo(0.6, 200, 0.9, 5000);
    }

    // ============================ Address bar and suggestions ============================
    private void OnAddressFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressShown.Visibility = Visibility.Collapsed;
        Address.Foreground = Ui.Brush("B.Text");
        AddressFrame.BorderBrush = Ui.Brush("B.Accent");
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Address.SelectAll());
    }

    private void OnAddressBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressFrame.BorderBrush = Ui.Brush("B.Line2");
        if (ReferenceEquals(_suggestFor, Address)) CloseSuggest();
        UpdateAddress();
    }

    // The first click selects the whole address, like other browsers.
    private void OnAddressMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Address.IsKeyboardFocusWithin) return;
        e.Handled = true;
        Address.Focus();
    }

    private void OnStartFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        StartSearchFrame.BorderBrush = Ui.Brush("B.AccentLine");
        StartSearchGlow.Opacity = 0.12;
    }

    private void OnStartBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        StartSearchFrame.BorderBrush = Ui.Brush("B.Line2");
        StartSearchGlow.Opacity = 0;
        if (ReferenceEquals(_suggestFor, StartSearch)) CloseSuggest();
    }

    private void FocusAddress()
    {
        if (_fullScreen) return;
        Address.Focus();
        Address.SelectAll();
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingText || sender is not TextBox box || !box.IsKeyboardFocused) return;
        var rows = _store.Suggest(box.Text).Select(s => new SuggestRow(s)).ToList();
        if (rows.Count == 0)
        {
            CloseSuggest();
            return;
        }
        _suggestFor = box;
        FrameworkElement anchor = ReferenceEquals(box, Address) ? AddressArea : StartSearchArea;
        SuggestList.ItemsSource = rows;
        SuggestList.SelectedIndex = 0;
        SuggestCard.Width = anchor.ActualWidth;
        SuggestPopup.PlacementTarget = anchor;
        SuggestPopup.HorizontalOffset = -16;
        SuggestPopup.IsOpen = true;
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool open = SuggestPopup.IsOpen && ReferenceEquals(_suggestFor, box);
        switch (key)
        {
            case Key.Enter:
                Submit(box, newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                e.Handled = true;
                break;
            case Key.Down when open:
                SuggestList.SelectedIndex = Math.Min(SuggestList.Items.Count - 1, SuggestList.SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Up when open:
                SuggestList.SelectedIndex = Math.Max(0, SuggestList.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Escape:
                if (open) CloseSuggest();
                else if (ReferenceEquals(box, Address))
                {
                    var tab = _active;
                    SetText(Address, tab == null || tab.IsStart ? "" : tab.PendingUrl ?? tab.Url);
                    Address.SelectAll();
                }
                else SetText(box, "");
                e.Handled = true;
                break;
        }
    }

    /// <summary>Opens what was typed or the picked suggestion. Alt and Enter opens a new tab.</summary>
    private void Submit(TextBox box, bool newTab)
    {
        var picked = SuggestPopup.IsOpen && ReferenceEquals(_suggestFor, box) && SuggestList.SelectedIndex > 0
            ? SuggestList.SelectedItem as SuggestRow : null;
        var text = box.Text.Trim();
        CloseSuggest();
        string? plain = null;
        var url = picked?.Url ?? _store.Resolve(text, out plain);
        if (url.Length == 0) return;
        var tab = newTab || _active == null ? AddTab(atEnd: true) : _active;
        if (ReferenceEquals(box, StartSearch)) SetText(StartSearch, "");
        Pick(tab);
        Pages.Focus(); // leave the box, the page takes the keyboard once the page is ready
        Go(tab, url, plain, focus: true);
    }

    private void OnSuggestClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(SuggestList, source) is not ListBoxItem item || _suggestFor is not { } box) return;
        SuggestList.SelectedItem = item.Content;
        Submit(box, newTab: false);
        e.Handled = true;
    }

    private void CloseSuggest()
    {
        SuggestPopup.IsOpen = false;
        _suggestFor = null;
    }

    private void CloseFlyouts()
    {
        CloseSuggest();
        PinPopup.IsOpen = false;
        Menu.IsOpen = false;
    }

    private void SetText(TextBox box, string text)
    {
        if (box.Text == text) return;
        _settingText = true;
        box.Text = text;
        _settingText = false;
    }

    // ============================ Web apps ============================
    private void OnStar(object sender, RoutedEventArgs e) => Star();

    /// <summary>Pins the website of the picked tab, or shows the pin to rename or remove the web app.</summary>
    private void Star()
    {
        var core = _active?.View?.CoreWebView2;
        if (core == null || !BrowserStore.IsWeb(core.Source) || _fullScreen) return;
        var app = _store.WebAppFor(core.Source);
        bool added = false;
        if (app == null)
        {
            app = _store.AddWebApp(BrowserStore.NameFor(core.Source, core.DocumentTitle), core.Source);
            added = true;
        }
        ShowPin(app, StarBtn, added, right: true);
    }

    private void ShowPin(WebApp? app, FrameworkElement anchor, bool added = false, bool right = false)
    {
        CloseSuggest();
        _pinApp = app;
        _pinAdding = app == null;
        _pinRemoved = false;
        _pinRight = right;
        PinTitle.Text = app == null ? "Pin a website" : added ? "Pinned to your web apps" : "Pinned web app";
        PinStar.Text = app == null ? "" : "";
        PinAddressPart.Visibility = app == null ? Visibility.Visible : Visibility.Collapsed;
        SetText(PinAddress, "");
        SetText(PinName, app?.Name ?? "");
        PinError.Visibility = Visibility.Collapsed;
        PinRemove.Visibility = app == null ? Visibility.Collapsed : Visibility.Visible;
        PinDone.Content = app == null ? "Pin" : "Done";
        PinPopup.PlacementTarget = anchor;
        PinPopup.CustomPopupPlacementCallback = PinPlace;
        PinPopup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var box = app == null ? PinAddress : PinName;
            box.Focus();
            box.SelectAll();
        });
        UpdateStar();
    }

    // The card has 16 pixels of shadow room on each side. The sizes come in whatever unit Windows
    // places popups in, so the room scales with the popup itself.
    private CustomPopupPlacement[] PinPlace(Size popup, Size target, Point offset)
    {
        double k = popup.Width / 348.0;
        double x = _pinRight ? target.Width - popup.Width + 16 * k : -16 * k;
        return new[] { new CustomPopupPlacement(new Point(x, target.Height + 2 * k), PopupPrimaryAxis.Horizontal) };
    }

    private void OnPinDone(object sender, RoutedEventArgs e)
    {
        if (_pinAdding)
        {
            var address = BrowserStore.AddressOf(PinAddress.Text);
            if (address == null || !BrowserStore.IsWeb(address))
            {
                PinError.Text = "Type a web address, for example outlook.office.com";
                PinError.Visibility = Visibility.Visible;
                Ui.Shake(PinAddress);
                PinAddress.Focus();
                return;
            }
            var name = PinName.Text.Trim();
            if (name.Length == 0) name = BrowserStore.NameFor(address);
            _pinAdding = false;
            _store.AddWebApp(name, address);
            Notice($"{name} is on your web apps");
        }
        else SavePinName();
        PinPopup.IsOpen = false;
    }

    private void SavePinName()
    {
        if (_pinApp == null || _pinRemoved) return;
        var name = PinName.Text.Trim();
        if (name.Length > 0 && name != _pinApp.Name) _store.UpdateWebApp(_pinApp, name, _pinApp.Url);
    }

    // Clicking somewhere else keeps the name typed so far, like a bookmark bubble.
    private void OnPinClosed(object? sender, EventArgs e)
    {
        SavePinName();
        _pinApp = null;
        _pinAdding = false;
        UpdateStar();
    }

    private void OnPinRemove(object sender, RoutedEventArgs e)
    {
        if (_pinApp != null)
        {
            _store.RemoveWebApp(_pinApp);
            Notice($"{_pinApp.Name} removed from your web apps");
        }
        _pinRemoved = true;
        PinPopup.IsOpen = false;
    }

    private void OnPinKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnPinDone(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_pinApp != null) SetText(PinName, _pinApp.Name);
            PinPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnPinAddressChanged(object sender, TextChangedEventArgs e) => PinError.Visibility = Visibility.Collapsed;

    // ============================ Start page ============================
    private void LoadStart()
    {
        _startStale = false;
        int hour = DateTime.Now.Hour;
        Greeting.Text = hour is >= 5 and < 12 ? "Good morning" : hour is >= 12 and < 18 ? "Good afternoon" : "Good evening";
        var apps = _store.State.WebApps.Select(WebRow.Of).ToList();
        apps.Add(WebRow.AddTile());
        StartApps.ItemsSource = apps;
        var recent = _store.RecentWebsites(6).Select(WebRow.Of).ToList();
        StartRecent.ItemsSource = recent;
        StartRecentHead.Visibility = StartRecentCard.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EngineLine.Text = $"Searching with {_store.Engine.Name}";
        int n = _store.State.LastTabs.Count;
        RestoreChip.Visibility = _restoreOffered && n > 0 ? Visibility.Visible : Visibility.Collapsed;
        RestoreChip.Content = n == 1 ? "Open the tab from last time" : $"Open the {n} tabs from last time";
    }

    private void OnStoreChanged()
    {
        if (StartPage.Visibility == Visibility.Visible) LoadStart();
        else _startStale = true;
        UpdateStar();
    }

    private void OnIconUpdated(string host)
    {
        foreach (var t in _tabs)
            if (t.Icon == null && BrowserStore.HostOf(t.Url).Equals(host, Ignore)) t.Icon = SiteIcons.For(t.Url);
        _startStale = true;
        if (StartPage.Visibility == Visibility.Visible) LoadStart();
    }

    private void OnStartTile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WebRow row) return;
        if (row.IsAdd)
        {
            ShowPin(null, (FrameworkElement)sender);
            return;
        }
        if (_active != null) Go(_active, row.Url, focus: true);
    }

    private void OnStartTileMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { DataContext: WebRow { App: { } app }, ContextMenu: { } menu })
        {
            e.Handled = true;
            return;
        }
        menu.Items.Clear();
        menu.Items.Add(Entry("Open", "", () => { if (_active != null) Go(_active, app.Url, focus: true); }));
        menu.Items.Add(Entry("Open in a new tab", "", () => Open(app.Url)));
        menu.Items.Add(Line());
        var remove = Entry($"Remove {app.Name} from your web apps", "", () =>
        {
            _store.RemoveWebApp(app);
            Notice($"{app.Name} removed from your web apps");
        });
        remove.Foreground = Ui.Brush("B.Red");
        menu.Items.Add(remove);
    }

    private void OnStartRecent(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WebRow row && _active != null) Go(_active, row.Url, focus: true);
    }

    /// <summary>Brings back the tabs open when the browser closed last time. Only the first loads now, the rest when opened.</summary>
    private void OnRestore(object sender, RoutedEventArgs e)
    {
        var urls = _store.State.LastTabs.Where(BrowserStore.IsWeb).ToList();
        _restoreOffered = false;
        _sessionUsed = true;
        RestoreChip.Visibility = Visibility.Collapsed;
        if (urls.Count == 0) return;
        var first = _active is { IsStart: true } ? _active : AddTab(atEnd: true);
        int at = _tabs.IndexOf(first);
        foreach (var url in urls.Skip(1))
            _tabs.Insert(++at, new BrowserTab { Url = url, PendingUrl = url, Title = TitleFor(url), Icon = SiteIcons.For(url) });
        Pick(first);
        Go(first, urls[0], focus: true);
    }

    private void OnEngineMenu(object sender, RoutedEventArgs e)
    {
        if (EngineBtn.ContextMenu is not { } menu) return;
        menu.Items.Clear();
        foreach (var engine in BrowserStore.Engines)
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = engine.Name },
                InputGestureText = engine.Key == _store.State.Search ? "✓" : "",
            };
            item.Click += (_, _) => _store.SetSearch(engine.Key);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = EngineBtn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private string TitleFor(string url)
    {
        var known = _store.State.Recent.FirstOrDefault(r => r.Url == url)?.Title;
        if (!string.IsNullOrWhiteSpace(known)) return known;
        var host = BrowserStore.HostOf(url);
        return host.Length > 0 ? host : string.IsNullOrEmpty(url) ? "New tab" : url;
    }

    // ============================ Find on page ============================
    private void OpenFind()
    {
        if (_active?.View?.CoreWebView2 == null || _fullScreen) return;
        if (FindBox.Visibility != Visibility.Visible)
        {
            FindBox.Visibility = Visibility.Visible;
            FindBox.BeginAnimation(WidthProperty, new DoubleAnimation(0, FindWidth, TimeSpan.FromMilliseconds(200)) { EasingFunction = Ui.EaseOut });
        }
        FindText.Focus();
        FindText.SelectAll();
        if (FindText.Text.Length > 0) StartFind();
    }

    private void CloseFind()
    {
        if (FindBox.Visibility != Visibility.Visible) return;
        _findTimer.Stop();
        try { _active?.View?.CoreWebView2?.Find.Stop(); } catch { /* no find session */ }
        FindCount.Text = "";
        var shrink = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        shrink.Completed += (_, _) => { if (FindBox.Width < 1) FindBox.Visibility = Visibility.Collapsed; };
        FindBox.BeginAnimation(WidthProperty, shrink);
    }

    private async void StartFind()
    {
        var core = _active?.View?.CoreWebView2;
        if (core == null || _env == null) return;
        var term = FindText.Text;
        try
        {
            if (term.Length == 0)
            {
                core.Find.Stop();
                FindCount.Text = "";
                return;
            }
            var options = _env.CreateFindOptions();
            options.FindTerm = term;
            options.SuppressDefaultFindDialog = true;
            options.ShouldHighlightAllMatches = true;
            await core.Find.StartAsync(options);
        }
        catch (Exception e) { Logger.Instance.Debug($"Find on page: {e.Message}"); }
        UpdateFindCount();
    }

    private void UpdateFindCount()
    {
        if (FindBox.Visibility != Visibility.Visible) return;
        CoreWebView2Find? find = null;
        try { find = _active?.View?.CoreWebView2?.Find; } catch { /* older runtime */ }
        if (find == null || FindText.Text.Length == 0)
        {
            FindCount.Text = "";
            return;
        }
        int n = find.MatchCount, i = find.ActiveMatchIndex;
        FindCount.Text = n <= 0 ? "No match" : $"{Math.Max(1, i)} of {n}";
        FindCount.Foreground = Ui.Brush(n <= 0 ? "B.Red" : "B.Text3");
    }

    private void FindStep(bool back)
    {
        try
        {
            var find = _active?.View?.CoreWebView2?.Find;
            if (find == null) return;
            if (back) find.FindPrevious();
            else find.FindNext();
        }
        catch { /* no find session yet */ }
    }

    private void OnFindChanged(object sender, TextChangedEventArgs e)
    {
        _findTimer.Stop();
        _findTimer.Start();
    }

    private void OnFindKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_findTimer.IsEnabled)
            {
                _findTimer.Stop();
                StartFind();
            }
            else FindStep(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFind();
            FocusContent();
            e.Handled = true;
        }
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => FindStep(false);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => FindStep(true);

    private void OnFindClose(object sender, RoutedEventArgs e)
    {
        CloseFind();
        FocusContent();
    }

    // ============================ Zoom, print, downloads ============================
    private void Zoom(int step)
    {
        var view = _active?.View;
        if (view?.CoreWebView2 == null) return;
        double z = view.ZoomFactor;
        view.ZoomFactor = step == 0 ? 1
            : step > 0 ? ZoomSteps.FirstOrDefault(s => s > z + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(s => s < z - 0.001, ZoomSteps[0]);
    }

    private void OnZoomReset(object sender, RoutedEventArgs e) => Zoom(0);

    private void Print()
    {
        try { _active?.View?.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); }
        catch (Exception e) { Logger.Instance.Debug($"Print: {e.Message}"); }
    }

    private void OpenDevTools()
    {
        try { _active?.View?.CoreWebView2?.OpenDevToolsWindow(); }
        catch (Exception e) { Logger.Instance.Debug($"Developer tools: {e.Message}"); }
    }

    private void ToggleDownloads()
    {
        var core = _active?.View?.CoreWebView2;
        if (core == null) return;
        try
        {
            if (core.IsDefaultDownloadDialogOpen) core.CloseDefaultDownloadDialog();
            else core.OpenDefaultDownloadDialog();
        }
        catch (Exception e) { Logger.Instance.Debug($"Downloads: {e.Message}"); }
    }

    private void OnDownloads(object sender, RoutedEventArgs e) => ToggleDownloads();

    private void OnDownloadStarting(CoreWebView2DownloadStartingEventArgs e)
    {
        _downloads++;
        _downloading++;
        var op = e.DownloadOperation;
        op.StateChanged += (_, _) =>
        {
            if (op.State == CoreWebView2DownloadState.InProgress) return;
            _downloading = Math.Max(0, _downloading - 1);
            UpdateDownloads();
        };
        UpdateDownloads();
    }

    private static CoreWebView2PreferredColorScheme Scheme(string colors) => colors switch
    {
        "dark" => CoreWebView2PreferredColorScheme.Dark,
        "light" => CoreWebView2PreferredColorScheme.Light,
        _ => CoreWebView2PreferredColorScheme.Auto,
    };

    /// <summary>
    /// Turns on or off opening a website that gives a safety warning. The safety list and certificate
    /// parts take effect on the open tabs right away, the connection locking part needs the browser to
    /// open again, so turning this on for an old cipher website is sure only after the next open.
    /// </summary>
    private void SetOpenUnsafe(bool on)
    {
        if (_store.State.OpenUnsafeSites == on) return;
        _store.SetOpenUnsafeSites(on);
        foreach (var t in _tabs)
        {
            var core = t.View?.CoreWebView2;
            if (core == null) continue;
            try
            {
                core.Settings.IsReputationCheckingRequired = !on;
                core.ServerCertificateErrorDetected -= OnCertificateError;
                if (on) core.ServerCertificateErrorDetected += OnCertificateError;
                else _ = core.ClearServerCertificateErrorActionsAsync();
            }
            catch (Exception e) { Logger.Instance.Debug($"Open unsafe sites: {e.Message}"); }
        }
        Logger.Instance.Info($"Browser: open sites with warnings turned {(on ? "on" : "off")}");
        Notice(on ? "Websites with a safety warning will open" : "Websites with a safety warning stay blocked");
        if (_active?.View?.CoreWebView2 != null) Reload();
    }

    private void SetColors(string key)
    {
        _store.SetColors(key);
        var core = _tabs.Select(t => t.View?.CoreWebView2).FirstOrDefault(c => c != null);
        try { if (core != null) core.Profile.PreferredColorScheme = Scheme(_store.State.Colors); }
        catch { /* applies to new tabs */ }
    }

    /// <summary>Clears history, sign ins, cookies and the cache of the browser, plus the recent list.</summary>
    private async Task ClearData()
    {
        var profile = _tabs.Select(t => t.View?.CoreWebView2).FirstOrDefault(c => c != null)?.Profile;
        WebView2? temp = null;
        try
        {
            if (profile == null)
            {
                // Every tab shows the start page, a hidden web view reaches the saved website data.
                var env = await Engine();
                temp = new WebView2 { Visibility = Visibility.Collapsed };
                Pages.Children.Insert(0, temp);
                await temp.EnsureCoreWebView2Async(env);
                profile = temp.CoreWebView2.Profile;
            }
            await profile.ClearBrowsingDataAsync();
            _store.ClearRecent();
            _closed.Clear();
            SiteIcons.Forget(_store.State.WebApps.Select(a => BrowserStore.HostOf(a.Url)));
            Logger.Instance.Ok("Browser history, sign ins and website data cleared");
            Notice("History, sign ins and website data cleared");
        }
        catch (Exception e)
        {
            Logger.Instance.Warning($"Could not clear the browser data: {e.Message}");
            Notice("Could not clear the browsing data, try again", error: true);
        }
        finally
        {
            if (temp != null)
            {
                Pages.Children.Remove(temp);
                try { temp.Dispose(); } catch { }
            }
        }
    }

    // ============================ Menu ============================
    private void OnMenu(object sender, RoutedEventArgs e)
    {
        var menu = Menu;
        menu.Items.Clear();
        _clearArmed = false;
        bool page = _active is { Crashed: false, View.CoreWebView2: not null };
        menu.Items.Add(Entry("New tab", "Ctrl+T", NewTab));
        menu.Items.Add(Entry("Reopen closed tab", "Ctrl+Shift+T", ReopenClosed, _closed.Count > 0));
        menu.Items.Add(Line());
        menu.Items.Add(ZoomRow(page));
        menu.Items.Add(Entry("Find on page", "Ctrl+F", OpenFind, page));
        menu.Items.Add(Entry("Print", "Ctrl+P", Print, page));
        menu.Items.Add(Entry("Downloads", "Ctrl+J", ToggleDownloads, page));
        menu.Items.Add(Entry(_fullScreen ? "Leave full screen" : "Full screen", "F11", ToggleFullScreen));
        menu.Items.Add(Line());
        menu.Items.Add(ChoiceRow("Search with", BrowserStore.Engines.Select(x => (x.Key, x.Name)).ToArray(), _store.State.Search,
            key => _store.SetSearch(key)));
        menu.Items.Add(ChoiceRow("Website colors", new[] { ("auto", "Windows"), ("dark", "Dark"), ("light", "Light") },
            _store.State.Colors, SetColors));
        menu.Items.Add(ChoiceRow("Open sites with warnings", new[] { ("on", "On"), ("off", "Off") },
            _store.State.OpenUnsafeSites ? "on" : "off", v => SetOpenUnsafe(v == "on")));
        menu.Items.Add(Line());
        menu.Items.Add(ClearEntry());
        menu.Items.Add(Entry("Developer tools", "F12", OpenDevTools, page));
        menu.Items.Add(Line());
        menu.Items.Add(Entry("Open PdaNet", "", OpenPdaNet));
        menu.Items.Add(Entry("Close browser", "Ctrl+Shift+W", Close));
        menu.PlacementTarget = MenuBtn;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = RightBelow;
        menu.IsOpen = true;
    }

    private static CustomPopupPlacement[] RightBelow(Size popup, Size target, Point offset) =>
        new[] { new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 4), PopupPrimaryAxis.Horizontal) };

    private void OnMenuClosed(object sender, RoutedEventArgs e) => _clearArmed = false;

    private static MenuItem Entry(string text, string keys, Action act, bool enabled = true)
    {
        // A TextBlock header keeps an underscore in a title from turning into an access key.
        var item = new MenuItem { Header = new TextBlock { Text = text }, InputGestureText = keys, IsEnabled = enabled };
        item.Click += (_, _) => act();
        return item;
    }

    private Separator Line() => new() { Style = (Style)FindResource("MenuLine") };

    private MenuItem ZoomRow(bool page)
    {
        var value = new TextBlock
        {
            FontFamily = (System.Windows.Media.FontFamily)FindResource("F.Mono"),
            FontSize = 11.5,
            MinWidth = 46,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void Show() => value.Text = $"{(_active?.View?.ZoomFactor ?? 1) * 100:0}%";
        Show();

        Button Small(string glyph, string name, Action act)
        {
            var b = new Button
            {
                Style = (Style)FindResource("ToolBtn"),
                Width = 28,
                Height = 26,
                FontSize = 12,
                Content = glyph,
                IsEnabled = page,
                Focusable = false,
            };
            AutomationProperties.SetName(b, name);
            b.Click += (_, e) =>
            {
                act();
                Show();
                e.Handled = true;
            };
            return b;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, -4, -6, -4) };
        buttons.Children.Add(Small("", "Zoom out", () => Zoom(-1)));
        buttons.Children.Add(value);
        buttons.Children.Add(Small("", "Zoom in", () => Zoom(1)));
        buttons.Children.Add(Small("", "Full screen", () =>
        {
            Menu.IsOpen = false;
            ToggleFullScreen();
        }));
        var row = new DockPanel { MinWidth = 280 };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(new TextBlock { Text = "Zoom", VerticalAlignment = VerticalAlignment.Center });
        return new MenuItem { Header = row, StaysOpenOnClick = true };
    }

    private MenuItem ChoiceRow(string text, (string Key, string Name)[] choices, string current, Action<string> pick)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, name) in choices)
        {
            var choice = new RadioButton
            {
                Style = (Style)FindResource("Seg"),
                Content = name,
                GroupName = "menu " + text,
                IsChecked = key == current,
                Padding = new Thickness(10, 3, 10, 4),
                FontSize = 12,
            };
            choice.Checked += (_, _) => pick(key);
            panel.Children.Add(choice);
        }
        var host = new Border { Style = (Style)FindResource("Seg.Host"), Child = panel, Margin = new Thickness(24, -3, -6, -3) };
        var row = new DockPanel { MinWidth = 280 };
        DockPanel.SetDock(host, Dock.Right);
        row.Children.Add(host);
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return new MenuItem { Header = row, StaysOpenOnClick = true };
    }

    // Clearing takes a second click, like unblocking everything on the Apps page.
    private MenuItem ClearEntry()
    {
        var label = new TextBlock { Text = "Clear browsing data" };
        var item = new MenuItem { Header = label, StaysOpenOnClick = true };
        item.Click += async (_, _) =>
        {
            if (!_clearArmed)
            {
                _clearArmed = true;
                label.Text = "Click again to clear history, sign ins and website data";
                item.Foreground = Ui.Brush("B.Red");
                return;
            }
            _clearArmed = false;
            Menu.IsOpen = false;
            await ClearData();
        };
        return item;
    }

    // ============================ Full screen ============================
    private void ToggleFullScreen() => SetFullScreen(!_fullScreen);

    private void SetFullScreen(bool on)
    {
        if (_fullScreen == on) return;
        CloseFlyouts();
        if (on)
        {
            _stateBeforeFull = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            _fullScreen = true;
            TabBar.Visibility = Visibility.Collapsed;
            TabRow.Height = new GridLength(0);
            Bar.Visibility = Visibility.Collapsed;
            Strip.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            // F11 or a tab change during a full screen video: the page leaves full screen too.
            if (_pageFullScreen)
                _active?.View?.CoreWebView2?.ExecuteScriptAsync("if (document.fullscreenElement) document.exitFullscreen()")
                    .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _fullScreen = false;
            _pageFullScreen = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFull == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            TabBar.Visibility = Visibility.Visible;
            TabRow.Height = new GridLength(42);
            Bar.Visibility = Visibility.Visible;
            Strip.Visibility = Visibility.Visible;
        }
        ApplyFrame();
    }

    // A video or a game in the page asks for full screen.
    private void SetPageFullScreen(bool on)
    {
        if (on && !_fullScreen)
        {
            SetFullScreen(true);
            _pageFullScreen = true;
        }
        else if (!on && _pageFullScreen) SetFullScreen(false);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        ApplyFrame();
        CloseFlyouts();
    }

    private void ApplyFrame()
    {
        bool max = WindowState == WindowState.Maximized;
        // A maximized custom chrome window hangs over the screen edge by its resize frame.
        var t = SystemParameters.WindowResizeBorderThickness;
        Frame.Margin = max && !_fullScreen ? new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4) : new Thickness(0);
        Frame.BorderThickness = new Thickness(max || _fullScreen ? 0 : 1);
        MaxBtn.Content = max ? "" : "";
        MaxBtn.ToolTip = max ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaxBtn, max ? "Restore" : "Maximize");
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ============================ Keyboard and mouse ============================
    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        bool typing = e.OriginalSource is TextBox;

        if (ctrl && !shift && key == Key.T) NewTab();
        else if (ctrl && shift && key == Key.T) ReopenClosed();
        else if (ctrl && shift && key == Key.W) Close();
        else if (ctrl && (key == Key.W || key == Key.F4)) { if (_active != null) CloseTab(_active); }
        else if (ctrl && key == Key.Tab) Cycle(shift ? -1 : 1);
        else if (ctrl && key == Key.PageDown) Cycle(1);
        else if (ctrl && key == Key.PageUp) Cycle(-1);
        else if (ctrl && key >= Key.D1 && key <= Key.D8) { int i = key - Key.D1; if (i < _tabs.Count) Pick(_tabs[i]); }
        else if (ctrl && key == Key.D9) Pick(_tabs[^1]);
        else if ((ctrl && key == Key.L) || (alt && key == Key.D) || key == Key.F6) FocusAddress();
        else if ((ctrl && shift && key == Key.R) || ((ctrl || shift) && key == Key.F5)) Reload(hard: true);
        else if ((ctrl && key == Key.R) || key == Key.F5) Reload();
        else if (alt && key == Key.Left) GoBack();
        else if (alt && key == Key.Right) GoForward();
        else if (ctrl && key is Key.OemPlus or Key.Add) Zoom(1);
        else if (ctrl && key is Key.OemMinus or Key.Subtract) Zoom(-1);
        else if (ctrl && key is Key.D0 or Key.NumPad0) Zoom(0);
        else if (ctrl && key == Key.F) OpenFind();
        else if (ctrl && key == Key.P) Print();
        else if (ctrl && key == Key.J) ToggleDownloads();
        else if (ctrl && key == Key.D) Star();
        else if (key == Key.F11) ToggleFullScreen();
        else if (key == Key.F12 || (ctrl && shift && key == Key.I)) OpenDevTools();
        else if (key == Key.Escape && !typing && _fullScreen && !_pageFullScreen) SetFullScreen(false);
        else if (key == Key.Escape && !typing && _active?.Loading == true) StopLoading();
        else return;
        e.Handled = true;
    }

    // The back and forward buttons on the side of a mouse.
    private void OnMouseButtons(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) GoBack();
        else if (e.ChangedButton == MouseButton.XButton2) GoForward();
        else return;
        e.Handled = true;
    }

    private void OnBack(object sender, RoutedEventArgs e) => GoBack();

    private void OnForward(object sender, RoutedEventArgs e) => GoForward();

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (_active?.Loading == true) StopLoading();
        else Reload();
    }

    // ============================ How the browser goes online ============================
    private static long LatestFlowId()
    {
        var flows = TrafficMonitor.Instance.Snapshot(0).NewFlows;
        return flows.Count > 0 ? flows[^1].Id : 0;
    }

    /// <summary>
    /// Reads PdaNet's records of new connections and keeps the ones from the browser's own
    /// processes. One such connection since PdaNet connected means the browser goes online
    /// through PdaNet.
    /// </summary>
    private void CheckRoute()
    {
        if (_env != null)
        {
            try
            {
                _pids.Clear();
                foreach (var p in _env.GetProcessInfos()) _pids.Add(p.ProcessId);
            }
            catch { /* the engine is starting again */ }
        }
        foreach (var f in TrafficMonitor.Instance.Snapshot(_flowId).NewFlows)
        {
            _flowId = f.Id;
            if (!_pids.Contains(f.Pid)) continue;
            _flows.Add(f);
            if (f.Started >= _since) _seen = true;
        }
        // Finished connections beyond the newest few hundred keep only their total.
        if (_flows.Count > 400)
        {
            int extra = _flows.Count - 400;
            _flows.RemoveAll(f =>
            {
                if (extra <= 0 || f.IsOpen) return false;
                extra--;
                _bytesGone += f.BytesUp + f.BytesDown;
                return true;
            });
        }
        UpdateRoute();
        UpdateStrip();
    }

    private void OnConnectionState(ConnectionState state) => Dispatcher.BeginInvoke(() =>
    {
        // Every new connection checks the way afresh.
        _since = state == ConnectionState.Connected ? DateTime.Now : DateTime.MaxValue;
        _seen = false;
        _loads = 0;
        _firstLoad = DateTime.MaxValue;
        UpdateRoute();
    });

    private BrowserRoute Route()
    {
        var demo = Demo.Value("route");
        var state = demo.Length == 0 ? _cm.State : demo == "offline" ? ConnectionState.Disconnected : ConnectionState.Connected;
        var mode = Friendly.ModeName(demo.Length == 0 ? _cm.CurrentMode : ConnectionMode.AdbAgent);
        bool seen = demo.Length == 0 ? _seen : demo == "seen";
        bool watching = demo.Length > 0 || _cm.WatchingApps;
        bool unseen = demo.Length == 0
            ? _loads >= 3 && DateTime.Now - _firstLoad > TimeSpan.FromSeconds(10)
            : demo == "unseen";
        var upper = mode.ToUpperInvariant();
        return state switch
        {
            ConnectionState.Connected when seen => new($"THROUGH PDANET · {upper}", "Browser through PdaNet", "Through PdaNet",
                $"PdaNet sees the browser go online with {mode}", "B.Accent", ""),
            ConnectionState.Connected when !watching => new($"PDANET ONLINE · {upper}", $"PdaNet online, {mode}", "PdaNet is online",
                "Turn on app watch in PdaNet Settings, then PdaNet can check how the browser goes online", "B.Cyan", ""),
            ConnectionState.Connected when unseen => new("NOT SEEN BY PDANET", "Browser not seen by PdaNet", "Not seen by PdaNet",
                "The browser opened websites, but PdaNet has not seen the browser go online, on some company PCs network settings of the company win over PdaNet",
                "B.Amber", ""),
            ConnectionState.Connected => new($"PDANET ONLINE · {upper}", $"PdaNet online, {mode}", "PdaNet is online",
                "The browser goes online the same way as other apps on this PC, PdaNet checks the way as soon as a website opens", "B.Cyan", ""),
            ConnectionState.Connecting => new("PDANET CONNECTING", "PdaNet connecting", "PdaNet is connecting",
                "PdaNet is connecting to your phone", "B.Amber", ""),
            ConnectionState.Disconnecting => new("PDANET DISCONNECTING", "PdaNet disconnecting", "PdaNet is disconnecting",
                "PdaNet is disconnecting from your phone", "B.Amber", ""),
            ConnectionState.Error => new("PDANET NEEDS ATTENTION", "PdaNet needs attention", "PdaNet needs attention",
                "Open PdaNet to see what went wrong", "B.Red", ""),
            _ => new("PDANET OFFLINE", "PdaNet offline", "PdaNet is offline",
                "Websites open through the network of this PC, connect in PdaNet to go online through your phone", "B.Text3", ""),
        };
    }

    private void UpdateRoute()
    {
        var r = Route();
        var brush = Ui.Brush(r.Key);
        var color = ((SolidColorBrush)brush).Color;
        RouteDot.Fill = brush;
        RouteText.Text = r.Strip;
        RouteText.Foreground = brush;
        RouteArea.ToolTip = r.Body;

        RouteCard.Background = new SolidColorBrush(Ui.WithAlpha(color, 0x10));
        RouteCard.BorderBrush = new SolidColorBrush(Ui.WithAlpha(color, 0x40));
        RouteTile.Background = new SolidColorBrush(Ui.WithAlpha(color, 0x1A));
        RouteTile.BorderBrush = new SolidColorBrush(Ui.WithAlpha(color, 0x66));
        RouteIcon.Text = r.Glyph;
        RouteIcon.Foreground = brush;
        RouteTitle.Text = r.Title;
        RouteBody.Text = r.Body;
    }

    private void OnRouteClick(object sender, MouseButtonEventArgs e) => OpenPdaNet();

    private void OnOpenPdaNet(object sender, RoutedEventArgs e) => OpenPdaNet();

    /// <summary>Brings PdaNet forward: the Apps page while online, where the browser shows up, else the Connect page.</summary>
    private void OpenPdaNet()
    {
        _shell.ShowFromTray();
        _shell.Navigate(_cm.IsConnected ? AppPage.Apps : AppPage.Connect);
    }

    // ============================ Window place and closing ============================
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        var saved = _store.State.Window;
        if (saved is { Length: 4 } && saved[2] >= MinWidth && saved[3] >= MinHeight && OnScreen(new Rect(saved[0], saved[1], saved[2], saved[3])))
        {
            Left = saved[0];
            Top = saved[1];
            Width = saved[2];
            Height = saved[3];
        }
        else
        {
            Width = Math.Max(MinWidth, Math.Min(1380, area.Width * 0.86));
            Height = Math.Max(MinHeight, Math.Min(900, area.Height * 0.88));
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }
        if (_store.State.Maximized) WindowState = WindowState.Maximized;
    }

    private static bool OnScreen(Rect r)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var seen = Rect.Intersect(screen, r);
        return !seen.IsEmpty && seen.Width >= 200 && seen.Height >= 120;
    }

    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        var tabs = _tabs.Select(t => t.PendingUrl ?? t.Url).Where(BrowserStore.IsWeb).ToList();
        // A window closed on the start page right away keeps the tabs of last time for the next start.
        bool keepOld = tabs.Count == 0 && !_sessionUsed && !_browsed;
        bool maximized = _fullScreen ? _stateBeforeFull == WindowState.Maximized : WindowState == WindowState.Maximized;
        var b = WindowState == WindowState.Normal && !_fullScreen ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var rect = b.IsEmpty || b.Width < MinWidth ? _store.State.Window : new[] { b.Left, b.Top, b.Width, b.Height };
        _store.SaveSession(keepOld ? null : tabs, rect, maximized);
    }

    private void OnClosedWindow(object? sender, EventArgs e)
    {
        _routeTimer.Stop();
        _findTimer.Stop();
        _noticeTimer.Stop();
        _store.Changed -= OnStoreChanged;
        SiteIcons.Updated -= OnIconUpdated;
        _cm.StateChanged -= OnConnectionState;
        foreach (var t in _tabs.ToList()) DropView(t);
        _tabs.Clear();
        Logger.Instance.Info("Browser closed");
    }

    // ============================ Helpers ============================
    private void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { Notice("Could not open the web page, visit microsoft.com and search for WebView2", error: true); }
    }

    private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = Find<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }
}
