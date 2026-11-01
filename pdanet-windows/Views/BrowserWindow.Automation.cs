using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PdaNetPortable.Core;
using PdaNetPortable.Services;

namespace PdaNetPortable.Views;

/// <summary>
/// The automation facade the MCP server drives (see <c>Mcp\BrowserSession</c>). It lives in a
/// partial of <see cref="BrowserWindow"/> so it can reuse the window's own tab, engine and
/// navigation machinery — the agent drives the exact same browser a person would, tabs, web
/// apps, the "through PdaNet" indicator and all. Every method here runs on the UI thread; the
/// MCP layer marshals to the dispatcher before calling in.
/// </summary>
public partial class BrowserWindow
{
    /// <summary>A snapshot of the active tab for a tool reply.</summary>
    public sealed record AutoPage(bool Ok, string Url, string Title, bool Loading, string? Note);

    /// <summary>One open tab, as the agent sees it.</summary>
    public sealed record AutoTab(int Index, string Title, string Url, bool Active, bool Loading);

    /// <summary>The CoreWebView2 of the active tab, or null when the active tab still shows the start page.</summary>
    internal CoreWebView2? AutoActiveCore => _active?.View?.CoreWebView2;

    /// <summary>The WPF view of the active tab, for screenshots.</summary>
    internal WebView2? AutoActiveView => _active?.View;

    internal AutoPage AutoDescribe()
    {
        var t = _active;
        if (t == null) return new AutoPage(false, "", "", false, "No tab is open.");
        var core = t.View?.CoreWebView2;
        var url = core != null ? core.Source : t.PendingUrl ?? t.Url;
        var title = core != null ? core.DocumentTitle : t.Title;
        if (string.IsNullOrWhiteSpace(title)) title = BrowserStore.HostOf(url);
        return new AutoPage(true, url ?? "", title ?? "", t.Loading, core == null ? "The tab is on the start page — navigate to open a website." : null);
    }

    internal List<AutoTab> AutoTabs()
    {
        var list = new List<AutoTab>(_tabs.Count);
        for (int i = 0; i < _tabs.Count; i++)
        {
            var t = _tabs[i];
            var core = t.View?.CoreWebView2;
            var url = core != null ? core.Source : t.PendingUrl ?? t.Url;
            var title = core != null ? core.DocumentTitle : t.Title;
            if (string.IsNullOrWhiteSpace(title)) title = BrowserStore.HostOf(url);
            if (string.IsNullOrWhiteSpace(title)) title = t.IsStart ? "Start page" : "New tab";
            list.Add(new AutoTab(i, title!, url ?? "", ReferenceEquals(t, _active), t.Loading));
        }
        return list;
    }

    /// <summary>
    /// Makes sure the active tab (creating one if needed) has a ready CoreWebView2 and returns it,
    /// without navigating. The MCP layer calls this first so it can instrument the page for console
    /// and network capture before the first navigation runs.
    /// </summary>
    internal async Task<CoreWebView2?> AutoEnsureCoreAsync()
    {
        var tab = _active ?? AddTab(atEnd: true);
        Pick(tab);
        return await EnsureView(tab);
    }

    /// <summary>Opens a web address in the active tab (its view is created if needed). Waits until the page settles.</summary>
    internal async Task<AutoPage> AutoNavigateAsync(string url, string? plain, int timeoutMs)
    {
        var tab = _active;
        if (tab == null) return new AutoPage(false, "", "", false, "No tab is open.");
        var core = tab.View?.CoreWebView2 ?? await EnsureView(tab);
        if (core == null)
            return new AutoPage(false, "", "", false, _engineError?.Message is { Length: > 0 } m
                ? "The browser engine could not start: " + Friendly.Clean(m)
                : "The browser engine (WebView2) is not available.");

        var (ok, status) = await NavOnceAsync(core, tab, url, timeoutMs);
        if (!ok && plain != null && NoAnswer(status))
            (ok, status) = await NavOnceAsync(core, tab, plain, timeoutMs);

        var page = AutoDescribe();
        if (!ok) page = page with { Note = NavNote(status) };
        return page;
    }

    internal Task<AutoPage> AutoBackAsync(int timeoutMs) => AutoHistoryAsync(back: true, timeoutMs);

    internal Task<AutoPage> AutoForwardAsync(int timeoutMs) => AutoHistoryAsync(back: false, timeoutMs);

    private async Task<AutoPage> AutoHistoryAsync(bool back, int timeoutMs)
    {
        var tab = _active;
        var core = tab?.View?.CoreWebView2;
        if (core == null) return AutoDescribe() with { Note = "There is no page to go back or forward from." };
        bool can = back ? core.CanGoBack : core.CanGoForward;
        if (!can) return AutoDescribe() with { Note = back ? "Cannot go back — no earlier page." : "Cannot go forward — no later page." };
        await NavTriggerAsync(core, tab!, () => { if (back) core.GoBack(); else core.GoForward(); }, timeoutMs);
        return AutoDescribe();
    }

    internal async Task<AutoPage> AutoReloadAsync(bool hard, int timeoutMs)
    {
        var tab = _active;
        var core = tab?.View?.CoreWebView2;
        if (core == null)
        {
            if (tab is { IsStart: false }) return await AutoNavigateAsync(tab.PendingUrl ?? tab.Url, null, timeoutMs);
            return AutoDescribe() with { Note = "Nothing to reload — the tab is on the start page." };
        }
        await NavTriggerAsync(core, tab!, () =>
        {
            if (hard)
                core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}")
                    .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            else core.Reload();
        }, timeoutMs);
        return AutoDescribe();
    }

    internal AutoPage AutoNewTab()
    {
        var t = AddTab(atEnd: true);
        Pick(t);
        return AutoDescribe();
    }

    internal AutoPage AutoSelectTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) throw new ArgumentOutOfRangeException(nameof(index));
        Pick(_tabs[index]);
        return AutoDescribe();
    }

    internal AutoPage AutoCloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var target = _tabs[index];
        // Never let the agent close the last tab out from under itself: that would close the
        // whole browser and end the MCP session. Keep one fresh tab alive instead.
        if (_tabs.Count == 1)
        {
            var keep = AddTab(atEnd: true);
            Pick(keep);
        }
        CloseTab(target);
        return AutoDescribe();
    }

    // Navigate once and wait for the page to finish (or time out). Returns success + why not.
    private async Task<(bool Ok, CoreWebView2WebErrorStatus Status)> NavOnceAsync(
        CoreWebView2 core, BrowserTab tab, string target, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<(bool, CoreWebView2WebErrorStatus)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(object? _, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= Done;
            tcs.TrySetResult((e.IsSuccess, e.WebErrorStatus));
        }
        core.NavigationCompleted += Done;
        tab.Loading = true;
        try { core.Navigate(target); }
        catch
        {
            core.NavigationCompleted -= Done;
            try { core.Navigate(_store.SearchUrl(target)); core.NavigationCompleted += Done; }
            catch { return (false, CoreWebView2WebErrorStatus.Unknown); }
        }
        if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) != tcs.Task)
        {
            core.NavigationCompleted -= Done;
            core.Stop();
            return (false, CoreWebView2WebErrorStatus.Timeout);
        }
        return tcs.Task.Result;
    }

    // Trigger a navigation (back/forward/reload) and wait for it to finish or time out.
    private async Task NavTriggerAsync(CoreWebView2 core, BrowserTab tab, Action trigger, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(object? _, CoreWebView2NavigationCompletedEventArgs e) { core.NavigationCompleted -= Done; tcs.TrySetResult(true); }
        core.NavigationCompleted += Done;
        tab.Loading = true;
        try { trigger(); } catch { core.NavigationCompleted -= Done; return; }
        if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) != tcs.Task)
            core.NavigationCompleted -= Done;
    }

    private static string NavNote(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.Timeout => "The page took too long to load — it may still be loading.",
        CoreWebView2WebErrorStatus.HostNameNotResolved => "The website address could not be found (DNS). Check the URL or the connection.",
        CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ServerUnreachable =>
            "Could not reach the website. If PdaNet is not connected, the phone's internet may be unavailable.",
        CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset =>
            "The connection to the website dropped.",
        _ => $"The page did not load ({status}).",
    };
}
