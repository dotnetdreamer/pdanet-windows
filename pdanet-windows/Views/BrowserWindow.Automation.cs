using System.Windows;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;
using PdaNetPortable.Core;
using PdaNetPortable.Services;

namespace PdaNetPortable.Views;

/// <summary>
/// The automation facade the MCP server drives (see <c>Mcp\BrowserSession</c>). It lives in a
/// partial of <see cref="BrowserWindow"/> so it reuses the window's own tabs, engine, find box,
/// zoom, menu settings and route indicator: an agent drives the exact browser a person uses, and
/// every change it makes shows in the window the way a person's change would. Every member runs
/// on the UI thread; the MCP layer marshals to the dispatcher before calling in.
/// </summary>
public partial class BrowserWindow
{
    /// <summary>Test pages for the built in MCP self check: a folder served at https://{Host}/.</summary>
    internal static (string Host, string Folder)? AutoTestHost { get; set; }

    /// <summary>A tab got its web view, before its first page loads, so an agent can watch it from the start.</summary>
    internal event Action<BrowserTab, CoreWebView2>? AutoViewReady;

    /// <summary>A download starts in one of the tabs.</summary>
    internal event Action<BrowserTab, CoreWebView2DownloadStartingEventArgs>? AutoDownloadStarting;

    // ============================ Tabs ============================
    internal IReadOnlyList<BrowserTab> AutoTabs => _tabs;
    internal BrowserTab? AutoActive => _active;
    internal int AutoClosedCount => _closed.Count;
    internal int AutoLastSessionCount => _store.State.LastTabs.Count(BrowserStore.IsWeb);

    /// <summary>Why no page can open right now, or null when the engine is fine.</summary>
    internal string? AutoEngineProblem =>
        _engineError != null
            ? (_engineError is WebView2RuntimeNotFoundException || WebRuntime.Version() == null
                ? "Microsoft Edge WebView2 is not installed on this PC"
                : "the browser engine could not start: " + Friendly.Clean(_engineError.Message))
            : _engineLost ? "the browser engine stopped, restart the browser" : null;

    /// <summary>The web view of a tab, made when needed, without opening anything.</summary>
    internal async Task<CoreWebView2?> AutoEnsureCoreAsync(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)) return null;
        return await EnsureView(tab);
    }

    internal BrowserTab AutoNewTab(bool pick = true)
    {
        var t = AddTab(atEnd: true);
        if (pick) Pick(t);
        return t;
    }

    internal void AutoPick(BrowserTab tab) => Pick(tab);

    /// <summary>Opens an address in a tab the way the address bar does, the https to http retry included.</summary>
    internal void AutoGo(BrowserTab tab, string url, string? plain) => Go(tab, url, plain, focus: false);

    internal bool AutoBack()
    {
        var core = _active?.View?.CoreWebView2;
        if (core?.CanGoBack != true) return false;
        core.GoBack();
        return true;
    }

    internal bool AutoForward()
    {
        var core = _active?.View?.CoreWebView2;
        if (core?.CanGoForward != true) return false;
        core.GoForward();
        return true;
    }

    internal void AutoReload(bool hard) => Reload(hard: hard);

    internal void AutoStop() => StopLoading();

    /// <summary>Closes a tab. The last tab is swapped for a fresh one, so the browser stays open.</summary>
    internal void AutoClose(BrowserTab tab)
    {
        if (_tabs.Count == 1) Pick(AddTab(atEnd: true));
        CloseTab(tab);
    }

    internal BrowserTab? AutoReopen()
    {
        if (_closed.Count == 0) return null;
        ReopenClosed();
        return _active;
    }

    internal BrowserTab? AutoDuplicate(BrowserTab tab)
    {
        if (tab.IsStart) return null;
        Duplicate(tab);
        return _active;
    }

    internal void AutoCloseOthers(BrowserTab keep) => CloseOthers(keep);

    internal void AutoCloseRight(BrowserTab tab) => CloseRight(tab);

    internal void AutoMove(BrowserTab tab, int to)
    {
        int from = _tabs.IndexOf(tab);
        to = Math.Clamp(to, 0, _tabs.Count - 1);
        if (from >= 0 && from != to) _tabs.Move(from, to);
        TabStrip.ScrollIntoView(tab);
    }

    internal bool AutoMute(BrowserTab tab, bool muted)
    {
        var core = tab.View?.CoreWebView2;
        if (core == null) return false;
        core.IsMuted = muted;
        return true;
    }

    /// <summary>Brings back the tabs from last time, as the chip on the start page does.</summary>
    internal int AutoRestore()
    {
        int n = AutoLastSessionCount;
        if (n > 0) OnRestore(this, new RoutedEventArgs());
        return n;
    }

    // ============================ Find on page ============================
    /// <summary>Finds text with the find box of the window, so the person sees the matches too.</summary>
    internal async Task<(int Matches, int Active)> AutoFindAsync(string text, bool matchCase, bool wholeWord)
    {
        var core = _active?.View?.CoreWebView2;
        if (core == null || _env == null) return (0, 0);
        if (FindBox.Visibility != Visibility.Visible)
        {
            FindBox.Visibility = Visibility.Visible;
            FindBox.BeginAnimation(WidthProperty, new DoubleAnimation(0, FindWidth, TimeSpan.FromMilliseconds(200)) { EasingFunction = Ui.EaseOut });
        }
        FindText.Text = text;
        _findTimer.Stop(); // the box would start its own search a moment later, with default options
        var options = _env.CreateFindOptions();
        options.FindTerm = text;
        options.SuppressDefaultFindDialog = true;
        options.ShouldHighlightAllMatches = true;
        options.IsCaseSensitive = matchCase;
        options.ShouldMatchWord = wholeWord;
        await core.Find.StartAsync(options);
        // The count arrives a moment after the search starts, wait until it holds still.
        int last = -2;
        for (int i = 0; i < 24; i++)
        {
            await Task.Delay(50);
            int n = core.Find.MatchCount;
            if (n == last) break;
            last = n;
        }
        UpdateFindCount();
        return (Math.Max(0, core.Find.MatchCount), core.Find.ActiveMatchIndex);
    }

    internal async Task<(int Matches, int Active)> AutoFindStepAsync(bool back)
    {
        var find = _active?.View?.CoreWebView2?.Find;
        if (find == null) return (0, 0);
        int before = find.ActiveMatchIndex;
        FindStep(back);
        for (int i = 0; i < 10 && find.ActiveMatchIndex == before; i++) await Task.Delay(30);
        UpdateFindCount();
        return (Math.Max(0, find.MatchCount), find.ActiveMatchIndex);
    }

    internal void AutoFindClose() => CloseFind();

    // ============================ View ============================
    internal double AutoZoom
    {
        get => _active?.View?.ZoomFactor ?? 1;
        set
        {
            var view = _active?.View;
            if (view?.CoreWebView2 != null) view.ZoomFactor = Math.Clamp(value, ZoomSteps[0], ZoomSteps[^1]);
        }
    }

    internal void AutoZoomStep(int step) => Zoom(step);

    internal bool AutoFullScreen
    {
        get => _fullScreen;
        set => SetFullScreen(value);
    }

    internal void AutoPrintDialog() => Print();

    internal void AutoDevTools() => OpenDevTools();

    internal bool AutoDownloadsPanel(bool open)
    {
        var core = _active?.View?.CoreWebView2;
        if (core == null) return false;
        if (open && !core.IsDefaultDownloadDialogOpen) core.OpenDefaultDownloadDialog();
        else if (!open && core.IsDefaultDownloadDialogOpen) core.CloseDefaultDownloadDialog();
        return true;
    }

    // ============================ Window ============================
    /// <summary>Set for "--mcp --hidden": the window works off screen, for an agent alone.</summary>
    internal bool AutoHidden { get; set; }

    /// <summary>Whether a person can see the window: shown, on a screen, not minimized.</summary>
    internal bool AutoSeen => IsVisible && WindowState != WindowState.Minimized && !AutoHidden;

    internal void AutoWindow(string action)
    {
        switch (action)
        {
            case "focus":
                Reveal();
                break;
            case "minimize":
                WindowState = WindowState.Minimized;
                break;
            case "maximize":
                if (_fullScreen) SetFullScreen(false);
                WindowState = WindowState.Maximized;
                break;
            case "restore":
                if (_fullScreen) SetFullScreen(false);
                if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
                break;
        }
    }

    /// <summary>The size of the page area, in device independent pixels.</summary>
    internal (double Width, double Height) AutoViewport => (Pages.ActualWidth, Pages.ActualHeight);

    /// <summary>Sizes the window so the page area is the given size in CSS pixels at the current zoom.</summary>
    internal void AutoResizeViewport(double cssWidth, double cssHeight)
    {
        if (_fullScreen) SetFullScreen(false);
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        double zoom = _active?.View?.ZoomFactor ?? 1;
        double width = Width + cssWidth * zoom - Pages.ActualWidth;
        double height = Height + cssHeight * zoom - Pages.ActualHeight;
        // A phone sized viewport is narrower than a person would make the window.
        MinWidth = Math.Min(MinWidth, Math.Max(240, width));
        MinHeight = Math.Min(MinHeight, Math.Max(200, height));
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
        UpdateLayout();
    }

    // ============================ Settings and data ============================
    internal void AutoSearchEngine(string key) => _store.SetSearch(key);

    internal void AutoColors(string key) => SetColors(key);

    internal void AutoOpenUnsafe(bool on) => SetOpenUnsafe(on);

    internal Task<bool> AutoClearDataAsync() => ClearData();

    internal void AutoNotice(string text) => Notice(text);

    // ============================ How the browser goes online ============================
    internal BrowserRoute AutoRoute => Route();

    /// <summary>Data the browser sent and received through PdaNet since the window opened.</summary>
    internal long AutoBytes => _bytesGone + _flows.Sum(f => f.BytesUp + f.BytesDown);

    internal bool AutoInApp => _shell != null;

    // ============================ Hooks for the window ============================
    private void AutomationViewReady(BrowserTab tab, CoreWebView2 core)
    {
        if (AutoTestHost is { } host)
        {
            try { core.SetVirtualHostNameToFolderMapping(host.Host, host.Folder, CoreWebView2HostResourceAccessKind.Allow); }
            catch (Exception e) { Logger.Instance.Debug($"Test pages: {e.Message}"); }
        }
        try { AutoViewReady?.Invoke(tab, core); }
        catch (Exception e) { Logger.Instance.Debug($"Agent hook: {e.Message}"); }
    }

    private void AutomationDownload(BrowserTab tab, CoreWebView2DownloadStartingEventArgs e)
    {
        try { AutoDownloadStarting?.Invoke(tab, e); }
        catch (Exception ex) { Logger.Instance.Debug($"Agent download hook: {ex.Message}"); }
    }
}
