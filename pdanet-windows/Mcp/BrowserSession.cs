using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PdaNetPortable.Services;
using PdaNetPortable.Views;

namespace PdaNetPortable.Mcp;

/// <summary>The browser window an agent drives: the PdaNet app's own browser, or a window of its own.</summary>
public interface IBrowserHost
{
    /// <summary>The window when it is open, else null.</summary>
    BrowserWindow? Window { get; }

    /// <summary>Opens the window without taking the focus from what the person does, or returns the open one.</summary>
    BrowserWindow Open();

    /// <summary>True when nobody can see the window (started with --hidden).</summary>
    bool Hidden { get; }
}

/// <summary>
/// The automation engine behind the browser tools. It drives a real <see cref="BrowserWindow"/>
/// through its automation facade and the Chrome DevTools Protocol of each tab.
///
/// Perception runs <c>snapshot.js</c> in an isolated JavaScript world, so pages can neither see nor
/// spoof it. Input goes through CDP (trusted mouse and key events). Every CDP call races against
/// the page opening a JavaScript dialog, because a dialog blocks the page and anything waiting on
/// it: the call returns at once and the agent is told to answer the dialog. Results describe what
/// an action changed (navigation, new tabs, dialogs, downloads, console errors) followed by the
/// page outline, or only the lines that changed since the agent's last look.
///
/// WebView2 may only be touched on the UI thread, so every tool call hops onto the dispatcher, one
/// call at a time; the long waits happen there while the dispatcher keeps pumping.
/// </summary>
public sealed partial class BrowserSession
{
    private static readonly string PageJs = LoadPageJs();
    private const string World = "pdanet-agent";

    private readonly Dispatcher _ui;
    private readonly IBrowserHost _host;
    private readonly bool _standalone;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<CoreWebView2, Page> _pages = new();
    private readonly List<DownloadItem> _downloads = new();
    private readonly DispatcherTimer _release;
    private BrowserWindow? _bound;
    private BrowserTab? _agentTab;          // the tab the agents work in
    private readonly HashSet<int> _mine = new(); // tabs the agents opened
    private readonly HashSet<int> _lent = new(); // tabs of the person's they let the agents act in
    private readonly AsyncLocal<ToolCall?> _call = new(); // the call the code at hand runs for
    private int _running;                   // tool calls running on the UI thread right now
    private bool _acting => _running > 0;
    private long _nextRef = 1;
    private int _downloadSeq;

    public BrowserSession(Dispatcher ui, IBrowserHost host, AgentPresence presence, bool standalone)
    {
        _ui = ui;
        _host = host;
        _standalone = standalone;
        Presence = presence;
        // In the app the person uses the same browser: once every agent is gone, the pages go back to normal.
        _release = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(20) };
        _release.Tick += (_, _) =>
        {
            if (_standalone || Presence.AnyActive || _pages.Count == 0) return;
            ReleaseAll();
        };
        _release.Start();
    }

    public AgentPresence Presence { get; }

    /// <summary>Raised when something an agent may watch changes: "tabs", "downloads", "web-apps", "history".</summary>
    public event Action<string>? ResourceChanged;

    /// <summary>Something worth telling connected agents as a log message: (level, text).</summary>
    public event Action<string, string>? Notice;

    /// <summary>Whether a person can watch the browser, so highlights and waits for answers make sense.</summary>
    private bool Watched => !_host.Hidden && (_bound?.AutoSeen ?? false);

    // ============================ Running a call ============================
    /// <summary>
    /// Runs a tool call on the UI thread, one at a time, once the person lets the agent act.
    /// The caption is what the agent bar says the agent is doing.
    /// </summary>
    private async Task<ToolResult> RunAsync(ToolCall call, string caption, Func<Task<ToolResult>> work)
    {
        await _gate.WaitAsync(call.Ct);
        try
        {
            await Presence.EnterAsync(call, caption);
            try
            {
                // Work still waiting for the UI thread when its call is given up on never starts, and
                // work that does start first checks again that the call still stands.
                var task = _ui.InvokeAsync(async () =>
                {
                    call.Ct.ThrowIfCancellationRequested();
                    Presence.Check(call);
                    await Presence.HoldAsync(call);
                    _call.Value = call;
                    _running++;
                    try { return await work(); }
                    finally { _running--; }
                }, DispatcherPriority.Normal, call.Ct).Task.Unwrap();
                // A page that never answers must not hold the browser for every later call: when the call
                // is cancelled or times out, the lock opens and the work stops at its next step.
                try { return await task.WaitAsync(call.Ct); }
                catch (OperationCanceledException) { Observe(task); throw; }
            }
            finally { Presence.Leave(call.Agent); }
        }
        catch (CdpException e)
        {
            throw new McpToolException(e.Friendly);
        }
        catch (DialogOpenException)
        {
            throw new McpToolException("The page opened a dialog and waits for an answer. Call browser_handle_dialog (accept or dismiss) first.");
        }
        finally { _gate.Release(); }
    }

    private Task<ToolResult> RunTextAsync(ToolCall call, string caption, Func<Task<string>> work) =>
        RunAsync(call, caption, async () => ToolResult.Ok(await work()));

    // ============================ The window ============================
    private BrowserWindow Win()
    {
        var w = _host.Open();
        if (!ReferenceEquals(w, _bound)) Bind(w);
        return w;
    }

    private void Bind(BrowserWindow w)
    {
        _bound = w;
        w.AutoViewReady += (tab, core) =>
        {
            // A popup of an agent's page is the agent's, and is watched from its first request. A tab
            // the person opens or wakes up meanwhile stays theirs, even while an agent acts.
            if (OpenedByAgents(tab)) _mine.Add(tab.Id);
            if (_standalone || _acting || Mine(tab)) Instrument(core, tab);
            ResourceChanged?.Invoke("tabs");
        };
        w.AutoDownloadStarting += OnDownload;
        w.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_bound, w)) return;
            _bound = null;
            _pages.Clear();
            ResourceChanged?.Invoke("tabs");
        };
    }

    /// <summary>
    /// The page the agents work on: the tab they last used, else the active tab. When the person moved
    /// to another tab meanwhile, an action brings the agents' tab back to the front first, so it never
    /// lands in the person's tab; a read just reads the agents' tab where it is.
    /// </summary>
    private async Task<Page> ActivePageAsync(ToolCall call, bool create = false, bool allowDialog = false, bool front = true)
    {
        var w = Win();
        var tab = AgentTab(w);
        string? moved = null;
        if (front && !ReferenceEquals(tab, w.AutoActive))
        {
            int was = IndexOf(w.AutoActive);
            w.AutoPick(tab);
            moved = $"Brought the agent's tab [{IndexOf(tab)}] back to the front (the person had switched to tab [{was}]).";
        }
        _agentTab = tab;
        var p = await PageOfAsync(w, tab, create, call.Ct);
        if (p != null && moved != null) p.Notes.Add(moved);
        if (p == null)
        {
            if (w.AutoEngineProblem is { } why) throw new McpToolException($"The browser cannot open websites: {why}.");
            throw new McpToolException("The active tab shows the start page, no website is open in it. Call browser_navigate to open one.");
        }
        if (tab.Crashed) throw new McpToolException("The page in the active tab stopped working. Call browser_reload to load it again.");
        if (!allowDialog && p.Dialog is { } d)
            throw new McpToolException($"{DialogLine(d)} The page waits for that answer, so answer it with browser_handle_dialog first.");
        return p;
    }

    /// <summary>The tab the agents work in: the one they last used while it is open, else the active tab.</summary>
    private BrowserTab AgentTab(BrowserWindow w)
    {
        if (_agentTab != null && w.AutoTabs.Contains(_agentTab)) return _agentTab;
        _agentTab = w.AutoActive ?? w.AutoNewTab();
        return _agentTab;
    }

    /// <summary>A tab an agent opened (or, in a browser of its own, any tab), as against a page the person opened.</summary>
    private bool Mine(BrowserTab tab) => _standalone || _mine.Contains(tab.Id) || tab.IsStart;

    /// <summary>
    /// A popup of a page the agents own, or of a page of the person's they lent the agents while
    /// an agent acts there (a sign in window its click opened).
    /// </summary>
    private bool OpenedByAgents(BrowserTab tab) =>
        _standalone || tab.Opener is { } o && (_mine.Contains(o.Id) || (_acting && _lent.Contains(o.Id)));

    /// <summary>
    /// Agents act in tabs of their own. Before one clicks, types, goes back or runs a script in a
    /// page the person opened, the person is asked, once per tab. Reading needs no question.
    /// </summary>
    private async Task OwnAsync(ToolCall call, Page p)
    {
        var tab = p.Tab;
        if (Mine(tab) || _lent.Contains(tab.Id)) return;
        string title = TabTitle(tab);
        bool ok = await Presence.ApproveAsync(call, $"{call.Agent.Name} wants to act in your tab \"{title}\"",
            "It would click, type and run scripts in a page you opened. Deny keeps it to tabs of its own");
        if (!ok)
            throw new McpToolException($"Tab [{IndexOf(tab)}] \"{title}\" holds a page the person opened, and they did not let the agent act in it. " +
                                       "Reading it works. To act, open the page in a tab of your own (browser_navigate, or browser_tabs with action new).");
        _lent.Add(tab.Id);
        Logger.Instance.Info($"The person let agent {call.Agent.Name} act in their tab {title}");
    }

    /// <summary>
    /// Asks the person before an agent closes tabs of theirs. Tabs the agents opened, and empty
    /// start tabs, close without a question.
    /// </summary>
    private async Task AllowCloseAsync(ToolCall call, IReadOnlyCollection<BrowserTab> closing)
    {
        var theirs = closing.Where(t => !Mine(t)).ToList();
        if (theirs.Count == 0) return;
        string what = theirs.Count == 1 ? $"your tab \"{TabTitle(theirs[0])}\"" : $"{theirs.Count} of your tabs";
        string list = string.Join(", ", theirs.Take(4).Select(t => $"\"{TabTitle(t)}\"")) + (theirs.Count > 4 ? " and more" : "");
        if (!await Presence.ApproveAsync(call, $"{call.Agent.Name} wants to close {what}", $"{list}. You can bring closed tabs back with Ctrl+Shift+T"))
            throw new McpToolException($"The person did not let the agent close {(theirs.Count == 1 ? "their tab" : "their tabs")}. Nothing was closed.");
    }

    private static string TabTitle(BrowserTab tab) =>
        Short(string.IsNullOrWhiteSpace(tab.Title) ? BrowserStore.HostOf(tab.PendingUrl ?? tab.Url) : tab.Title, 60);

    /// <summary>
    /// A point between two steps of input where the running call waits out the person's pause.
    /// True when it waited: the person may have clicked elsewhere meanwhile.
    /// </summary>
    private Task<bool> HoldAsync() => _call.Value is { } c ? Presence.HoldAsync(c) : Task.FromResult(false);

    /// <summary>The automation state of a tab, made when the tab has a web view (or when asked to make one).</summary>
    private async Task<Page?> PageOfAsync(BrowserWindow w, BrowserTab tab, bool create, CancellationToken ct)
    {
        var core = tab.View?.CoreWebView2;
        if (core == null && (create || tab.View != null)) core = await w.AutoEnsureCoreAsync(tab).WaitAsync(ct);
        if (core == null) return null;
        var p = Instrument(core, tab);
        await p.Ready.WaitAsync(ct); // the hooks are on before anything loads, so the first requests are seen too
        return p;
    }

    private Page? PageOf(BrowserTab? tab)
    {
        var core = tab?.View?.CoreWebView2;
        return core == null ? null : Instrument(core, tab!);
    }

    private int IndexOf(BrowserTab? tab) => tab == null || _bound == null ? -1 : _bound.AutoTabs.ToList().IndexOf(tab);

    // ============================ Instrumentation ============================
    private sealed class Page
    {
        public required CoreWebView2 Core { get; init; }
        public required BrowserTab Tab { get; init; }
        public Task Ready { get; set; } = Task.CompletedTask;
        public int? Iso;
        public long Epoch;
        public long RefLow = long.MaxValue, RefHigh;
        public readonly List<(long Low, long High, string Url)> OldRefs = new();
        public readonly Dictionary<string, (List<string> Lines, string Url, long Epoch)> Seen = new();
        public readonly List<string> Console = new();
        public readonly List<NetEntry> Net = new();
        public readonly Dictionary<string, NetEntry> InFlight = new();
        public DateTime LastNet = DateTime.MinValue;
        public int Errors;
        public DialogInfo? Dialog;
        public TaskCompletionSource DialogSignal = Signal();
        public ChooserInfo? Chooser;
        public bool Intercepting;
        public bool Overlay;
        public ulong NavId, DomId, DoneId;
        public bool NavOk = true;
        public int NavStatus;
        public CoreWebView2WebErrorStatus NavError;
        public readonly List<string> Notes = new();
        public string? DragData;
        public readonly List<Action> Unhook = new();
    }

    private sealed record DialogInfo(string Type, string Message, string? DefaultPrompt);

    private sealed record ChooserInfo(int BackendNodeId, bool Multiple, DateTime At);

    private sealed class NetEntry
    {
        public required string Id { get; init; }
        public required string Method { get; init; }
        public required string Url { get; set; }
        public string Type { get; set; } = "";
        public int Status { get; set; }
        public string Mime { get; set; } = "";
        public long Size { get; set; }
        public string? Failed { get; set; }
        public bool Done { get; set; }
        public DateTime Start { get; } = DateTime.Now;
        public bool LongLived => Type is "EventSource" or "WebSocket" || Url.Contains("long-poll", StringComparison.OrdinalIgnoreCase);
    }

    private Page Instrument(CoreWebView2 core, BrowserTab tab)
    {
        if (_pages.TryGetValue(core, out var known)) return known;
        var p = new Page { Core = core, Tab = tab };
        _pages[core] = p;

        On(p, "Runtime.executionContextDestroyed", n =>
        {
            if (n?["executionContextId"]?.GetValue<int>() == p.Iso) p.Iso = null;
        });
        On(p, "Runtime.executionContextsCleared", _ => p.Iso = null);
        On(p, "Page.frameNavigated", n =>
        {
            var frame = n?["frame"];
            if (frame == null || frame["parentId"] != null) return;
            NewDocument(p, frame["url"]?.GetValue<string>() ?? "");
        });
        On(p, "Runtime.consoleAPICalled", n => OnConsole(p, n));
        On(p, "Runtime.exceptionThrown", n => OnException(p, n));
        On(p, "Log.entryAdded", n => OnLog(p, n));
        On(p, "Network.requestWillBeSent", n => OnRequest(p, n));
        On(p, "Network.responseReceived", n => OnResponse(p, n));
        On(p, "Network.loadingFinished", n => OnFinished(p, n, null));
        On(p, "Network.loadingFailed", n => OnFinished(p, n, n?["errorText"]?.GetValue<string>() ?? "failed"));
        On(p, "Page.javascriptDialogOpening", n => OnDialogOpening(p, n));
        On(p, "Page.javascriptDialogClosed", _ => OnDialogClosed(p));
        On(p, "Page.fileChooserOpened", n =>
        {
            var id = n?["backendNodeId"]?.GetValue<int>() ?? 0;
            p.Chooser = new ChooserInfo(id, n?["mode"]?.GetValue<string>() == "selectMultiple", DateTime.Now);
            p.Notes.Add(id > 0
                ? $"A file chooser opened ({(p.Chooser.Multiple ? "several files" : "one file")}). Call browser_file_upload with the file paths to pick the files."
                : "A file chooser opened, but PdaNet cannot reach its field. Use browser_file_upload with the ref of the file field.");
        });
        On(p, "Input.dragIntercepted", n => p.DragData = n?["data"]?.ToJsonString());

        EventHandler<CoreWebView2NavigationStartingEventArgs> starting = (_, e) =>
        {
            p.NavId = e.NavigationId;
            p.NavOk = true;
        };
        EventHandler<CoreWebView2DOMContentLoadedEventArgs> dom = (_, e) => p.DomId = e.NavigationId;
        EventHandler<CoreWebView2NavigationCompletedEventArgs> completed = (_, e) =>
        {
            p.DoneId = e.NavigationId;
            if (e.NavigationId != p.NavId) return;
            p.NavOk = e.IsSuccess;
            p.NavError = e.WebErrorStatus;
            try { p.NavStatus = e.HttpStatusCode; } catch { p.NavStatus = 0; }
        };
        core.NavigationStarting += starting;
        core.DOMContentLoaded += dom;
        core.NavigationCompleted += completed;
        p.Unhook.Add(() =>
        {
            core.NavigationStarting -= starting;
            core.DOMContentLoaded -= dom;
            core.NavigationCompleted -= completed;
        });

        p.Ready = EnableAsync(p);
        return p;
    }

    private async Task EnableAsync(Page p)
    {
        foreach (var (m, args) in new (string, string)[]
                 {
                     ("Runtime.enable", "{}"), ("Log.enable", "{}"), ("Page.enable", "{}"),
                     ("Network.enable", "{\"maxPostDataSize\":0}"),
                 })
        {
            try { await p.Core.CallDevToolsProtocolMethodAsync(m, args); }
            catch (Exception e) { Logger.Instance.Debug($"Agent hooks, {m}: {e.Message}"); }
        }
        // In the agents' own tabs a file chooser never shows: it waits for browser_file_upload. The
        // person's tabs keep theirs, they are caught only around an agent's action there.
        if (Mine(p.Tab)) await InterceptChooserAsync(p, true);
    }

    /// <summary>Gives a page back to the person: no hooks, no intercepted file choosers.</summary>
    private void ReleaseAll()
    {
        foreach (var p in _pages.Values)
        {
            foreach (var u in p.Unhook) { try { u(); } catch { } }
            foreach (var m in new[] { "Page.setInterceptFileChooserDialog", "Overlay.disable", "DOM.disable", "Network.disable", "Log.disable", "Page.disable" })
            {
                var args = m == "Page.setInterceptFileChooserDialog" ? "{\"enabled\":false}" : "{}";
                try { _ = p.Core.CallDevToolsProtocolMethodAsync(m, args).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
                catch { /* the tab closed */ }
            }
        }
        _pages.Clear();
        _lent.Clear(); // the person lent a tab to the agents of that time, not to the next ones
        Logger.Instance.Debug("Agent hooks removed from the browser tabs");
    }

    private void On(Page p, string evt, Action<JsonNode?> handler)
    {
        try
        {
            var rcv = p.Core.GetDevToolsProtocolEventReceiver(evt);
            EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> h = (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.SessionId)) return; // only the top page, not iframes or workers
                JsonNode? n;
                try { n = JsonNode.Parse(e.ParameterObjectAsJson); }
                catch { return; }
                try { handler(n); }
                catch (Exception ex) { Logger.Instance.Debug($"Agent hook {evt}: {ex.Message}"); }
            };
            rcv.DevToolsProtocolEventReceived += h;
            p.Unhook.Add(() => rcv.DevToolsProtocolEventReceived -= h);
        }
        catch (Exception e) { Logger.Instance.Debug($"Agent hook {evt} not available: {e.Message}"); }
    }

    private void NewDocument(Page p, string url)
    {
        if (p.RefHigh > 0) p.OldRefs.Add((p.RefLow, p.RefHigh, url));
        if (p.OldRefs.Count > 30) p.OldRefs.RemoveAt(0);
        p.RefLow = long.MaxValue;
        p.RefHigh = 0;
        p.Epoch++;
        p.Iso = null;
        p.Chooser = null;
        p.Seen.Clear();
    }

    // ============================ Console and network ============================
    private void OnConsole(Page p, JsonNode? n)
    {
        var type = n?["type"]?.GetValue<string>() ?? "log";
        var parts = (n?["args"] as JsonArray)?.Select(ArgText) ?? Enumerable.Empty<string>();
        var where = Where(n?["stackTrace"]?["callFrames"]?[0]);
        if (type is "error" or "assert") p.Errors++;
        Push(p.Console, $"[{type}] {Short(string.Join(" ", parts), 600)}{where}");
    }

    private static string ArgText(JsonNode? a)
    {
        if (a == null) return "";
        if (a["value"] is JsonNode v) return v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToJsonString();
        if (a["unserializableValue"]?.GetValue<string>() is { } u) return u;
        if (a["description"]?.GetValue<string>() is { } d) return d;
        return a["type"]?.GetValue<string>() ?? "";
    }

    private void OnException(Page p, JsonNode? n)
    {
        var d = n?["exceptionDetails"];
        var text = d?["exception"]?["description"]?.GetValue<string>() ?? d?["text"]?.GetValue<string>() ?? "Uncaught error";
        p.Errors++;
        Push(p.Console, $"[uncaught] {Short(text.Split('\n')[0], 600)}{Where(d)}");
    }

    private void OnLog(Page p, JsonNode? n)
    {
        var e = n?["entry"];
        var level = e?["level"]?.GetValue<string>() ?? "info";
        if (level is not ("error" or "warning")) return;
        if (level == "error") p.Errors++;
        var url = e?["url"]?.GetValue<string>();
        Push(p.Console, $"[{level}] {Short(e?["text"]?.GetValue<string>() ?? "", 600)}{(string.IsNullOrEmpty(url) ? "" : "  (" + Short(url, 90) + ")")}");
    }

    private static string Where(JsonNode? frame)
    {
        var url = frame?["url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(url)) return "";
        var line = frame?["lineNumber"]?.GetValue<int>() ?? 0;
        return $"  ({Short(url, 90)}:{line + 1})";
    }

    private static void Push(List<string> buf, string line)
    {
        buf.Add(line);
        if (buf.Count > 200) buf.RemoveRange(0, buf.Count - 200);
    }

    private void OnRequest(Page p, JsonNode? n)
    {
        var id = n?["requestId"]?.GetValue<string>();
        if (id == null) return;
        var url = n?["request"]?["url"]?.GetValue<string>() ?? "";
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
        p.LastNet = DateTime.Now;
        if (p.InFlight.TryGetValue(id, out var redirected))
        {
            redirected.Url = url; // a redirect keeps the request id
            return;
        }
        var e = new NetEntry
        {
            Id = id,
            Method = n?["request"]?["method"]?.GetValue<string>() ?? "GET",
            Url = url,
            Type = n?["type"]?.GetValue<string>() ?? "",
        };
        p.InFlight[id] = e;
        p.Net.Add(e);
        if (p.Net.Count > 300) p.Net.RemoveRange(0, p.Net.Count - 300);
    }

    private void OnResponse(Page p, JsonNode? n)
    {
        var id = n?["requestId"]?.GetValue<string>();
        if (id == null || !p.InFlight.TryGetValue(id, out var e)) return;
        e.Status = n?["response"]?["status"]?.GetValue<int>() ?? 0;
        e.Mime = n?["response"]?["mimeType"]?.GetValue<string>() ?? "";
        if (n?["type"]?.GetValue<string>() is { Length: > 0 } t) e.Type = t;
        p.LastNet = DateTime.Now;
    }

    private void OnFinished(Page p, JsonNode? n, string? failed)
    {
        var id = n?["requestId"]?.GetValue<string>();
        if (id == null || !p.InFlight.Remove(id, out var e)) return;
        e.Done = true;
        e.Failed = failed;
        try { e.Size = (long)(n?["encodedDataLength"]?.GetValue<double>() ?? 0); } catch { }
        p.LastNet = DateTime.Now;
    }

    // ============================ Dialogs ============================
    private void OnDialogOpening(Page p, JsonNode? n)
    {
        var type = n?["type"]?.GetValue<string>() ?? "alert";
        var message = n?["message"]?.GetValue<string>() ?? "";
        if (type == "alert" && (Mine(p.Tab) || ReferenceEquals(p.Tab, _agentTab)))
        {
            // An alert only tells something: take note, close it, and let the page go on.
            p.Notes.Add($"The page showed an alert: \"{Short(message, 200)}\" (closed with OK).");
            _ = p.Core.CallDevToolsProtocolMethodAsync("Page.handleJavaScriptDialog", "{\"accept\":true}")
                .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return;
        }
        p.Dialog = new DialogInfo(type, message, n?["defaultPrompt"]?.GetValue<string>());
        p.DialogSignal.TrySetResult();
        Notice?.Invoke("warning", $"Tab [{IndexOf(p.Tab)}]: {DialogLine(p.Dialog)} It waits for browser_handle_dialog.");
        Logger.Instance.Debug($"Agent: a {type} dialog opened on {BrowserStore.HostOf(p.Core.Source)}");
    }

    private void OnDialogClosed(Page p)
    {
        p.Dialog = null;
        p.DialogSignal = Signal();
    }

    private static string DialogLine(DialogInfo d) => d.Type switch
    {
        "beforeunload" => "The page asks whether to leave it (it may have unsaved changes).",
        "prompt" => $"The page asks for text: \"{Short(d.Message, 200)}\"{(string.IsNullOrEmpty(d.DefaultPrompt) ? "" : $" (suggested: \"{Short(d.DefaultPrompt!, 60)}\")")}.",
        _ => $"The page asks: \"{Short(d.Message, 200)}\" (OK or Cancel).",
    };

    // ============================ Downloads ============================
    private sealed class DownloadItem
    {
        public required int Id { get; init; }
        public required CoreWebView2DownloadOperation Op { get; init; }
        public required string Url { get; init; }
        public string Path { get; set; } = "";
        public DateTime Started { get; } = DateTime.Now;
        public string State { get; set; } = "downloading";
        public long Bytes { get; set; }
        public long Total { get; set; }
        public string? Reason { get; set; }
        public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Update()
        {
            try
            {
                Path = Op.ResultFilePath ?? Path;
                Bytes = Op.BytesReceived;
                Total = Op.TotalBytesToReceive is { } t ? (long)t : 0;
                State = Op.State switch
                {
                    CoreWebView2DownloadState.Completed => "complete",
                    CoreWebView2DownloadState.Interrupted => Op.CanResume && Op.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled ? "paused" : "interrupted",
                    _ => Op.InterruptReason == CoreWebView2DownloadInterruptReason.None ? "downloading" : "paused",
                };
                Reason = Op.InterruptReason == CoreWebView2DownloadInterruptReason.None ? null : Op.InterruptReason.ToString();
                if (Op.State != CoreWebView2DownloadState.InProgress) Finished.TrySetResult();
            }
            catch { /* the operation is gone */ }
        }
    }

    private void OnDownload(BrowserTab tab, CoreWebView2DownloadStartingEventArgs e)
    {
        if (!_standalone && !Presence.AnyActive) return;
        var op = e.DownloadOperation;
        var d = new DownloadItem { Id = ++_downloadSeq, Op = op, Url = op.Uri, Path = e.ResultFilePath };
        _downloads.Add(d);
        if (_downloads.Count > 60) _downloads.RemoveAt(0);
        op.StateChanged += (_, _) =>
        {
            d.Update();
            if (d.State != "downloading")
            {
                Logger.Instance.Info($"Agent download {d.State}: {Path.GetFileName(d.Path)}");
                Notice?.Invoke(d.State == "complete" ? "info" : "warning", $"Download {d.Id} {d.State}: {d.Path}");
            }
            ResourceChanged?.Invoke("downloads");
        };
        op.BytesReceivedChanged += (_, _) => d.Update();
        d.Update();
        var page = PageOf(tab);
        page?.Notes.Add($"A download started: {Path.GetFileName(d.Path)} (download {d.Id}). Use browser_downloads to follow it.");
        ResourceChanged?.Invoke("downloads");
    }

    // ============================ CDP and the page helper ============================
    /// <summary>
    /// Calls a DevTools Protocol method. Unless the page has a dialog open already, the call races
    /// against one opening: a dialog blocks the page and anything waiting on it, so the call gives
    /// up at once with <see cref="DialogOpenException"/> and the caller reports the dialog.
    /// </summary>
    private async Task<JsonNode?> Cdp(Page p, string method, JsonObject? args, CancellationToken ct, bool race = true)
    {
        // A call given up on (cancelled or timed out) stops here rather than sending more input.
        ct.ThrowIfCancellationRequested();
        await p.Ready.WaitAsync(ct);
        Task<string> call;
        try { call = p.Core.CallDevToolsProtocolMethodAsync(method, args?.ToJsonString() ?? "{}"); }
        catch (Exception e) { throw new CdpException(method, e.Message); }
        if (race)
        {
            if (p.Dialog != null) { Observe(call); throw new DialogOpenException(); }
            // A dialog answered without its "closed" event leaves the signal set: no dialog means no signal.
            if (p.DialogSignal.Task.IsCompleted) p.DialogSignal = Signal();
            Task done;
            try { done = await Task.WhenAny(call, p.DialogSignal.Task).WaitAsync(ct); }
            catch (OperationCanceledException) { Observe(call); throw; }
            if (done != call)
            {
                Observe(call);
                throw new DialogOpenException();
            }
        }
        string json;
        try { json = await call.WaitAsync(ct); }
        catch (OperationCanceledException) { Observe(call); throw; }
        catch (Exception e) { throw new CdpException(method, e.Message); }
        return string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json);
    }

    /// <summary>Runs one operation of the page helper in the agent's isolated world and returns its result.</summary>
    private async Task<JsonNode?> Js(Page p, string op, JsonObject? arg, CancellationToken ct)
    {
        var expr = "globalThis.__pda?globalThis.__pda.call(" + JsonSerializer.Serialize(op) + "," +
                   (arg?.ToJsonString() ?? "{}") + "):{__pdaMissing:true}";
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int ctx = await WorldAsync(p, ct);
            JsonNode? r;
            try
            {
                r = await Cdp(p, "Runtime.evaluate", new JsonObject
                {
                    ["expression"] = expr,
                    ["contextId"] = ctx,
                    ["returnByValue"] = true,
                    ["silent"] = true,
                }, ct);
            }
            catch (CdpException e) when (e.ContextGone)
            {
                p.Iso = null;
                await Task.Delay(60 * (attempt + 1), ct);
                continue;
            }
            if (r?["exceptionDetails"] is JsonNode ex)
                throw new McpToolException("The page could not be read: " + (ex["exception"]?["description"]?.GetValue<string>() ?? ex["text"]?.GetValue<string>() ?? "script error"));
            var value = r?["result"]?["value"];
            if (value is JsonObject o && o.ContainsKey("__pdaMissing"))
            {
                await InjectAsync(p, ctx, ct);
                continue;
            }
            return value;
        }
        throw new McpToolException("The page could not be read right now, it may still be loading. Try again in a moment.");
    }

    /// <summary>The agent's isolated world in the page's main frame, made (and the helper put in) when needed.</summary>
    private async Task<int> WorldAsync(Page p, CancellationToken ct)
    {
        if (p.Iso is int id) return id;
        var tree = await Cdp(p, "Page.getFrameTree", null, ct);
        var frameId = tree?["frameTree"]?["frame"]?["id"]?.GetValue<string>()
                      ?? throw new McpToolException("The page has no frame to read yet. Try again in a moment.");
        var world = await Cdp(p, "Page.createIsolatedWorld", new JsonObject { ["frameId"] = frameId, ["worldName"] = World }, ct);
        var ctx = world?["executionContextId"]?.GetValue<int>()
                  ?? throw new McpToolException("The page could not be prepared for reading. Try again in a moment.");
        await InjectAsync(p, ctx, ct);
        p.Iso = ctx;
        return ctx;
    }

    private async Task InjectAsync(Page p, int ctx, CancellationToken ct)
    {
        var r = await Cdp(p, "Runtime.evaluate", new JsonObject { ["expression"] = PageJs, ["contextId"] = ctx, ["silent"] = true }, ct);
        if (r?["exceptionDetails"] is JsonNode ex)
            throw new McpToolException("The page helper could not start: " + (ex["exception"]?["description"]?.GetValue<string>() ?? ex["text"]?.GetValue<string>() ?? "script error"));
    }

    /// <summary>A remote object id for the element behind a handle, in the agent's world.</summary>
    private async Task<string> ObjectIdAsync(Page p, string @ref, CancellationToken ct)
    {
        await RequireAsync(p, @ref, ct);
        int ctx = await WorldAsync(p, ct);
        var r = await Cdp(p, "Runtime.evaluate", new JsonObject
        {
            ["expression"] = "globalThis.__pda.element(" + JsonSerializer.Serialize(@ref) + ")",
            ["contextId"] = ctx,
            ["silent"] = true,
        }, ct);
        return r?["result"]?["objectId"]?.GetValue<string>() ?? throw Missing(p, @ref);
    }

    private async Task<int> BackendNodeAsync(Page p, string @ref, CancellationToken ct)
    {
        var obj = await ObjectIdAsync(p, @ref, ct);
        var d = await Cdp(p, "DOM.describeNode", new JsonObject { ["objectId"] = obj }, ct);
        return d?["node"]?["backendNodeId"]?.GetValue<int>() ?? throw Missing(p, @ref);
    }

    // ============================ Handles ============================
    /// <summary>Turns what an agent passes as a handle ("e12", "[ref=e12]", "ref=e12", "12") into "e12".</summary>
    private static string Ref(string? raw)
    {
        var s = (raw ?? "").Trim().Trim('[', ']').Trim();
        if (s.StartsWith("ref=", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        s = s.Trim().TrimStart('e', 'E');
        if (s.Length == 0 || !s.All(char.IsDigit)) throw new McpToolException($"'{raw}' is not a handle. Handles look like e12 and come from the [ref=…] marks in the latest snapshot.");
        return "e" + s;
    }

    private async Task RequireAsync(Page p, string @ref, CancellationToken ct)
    {
        var r = await Js(p, "describe", new JsonObject { ["ref"] = @ref }, ct);
        Check(p, r, @ref);
    }

    private void Check(Page p, JsonNode? r, string @ref)
    {
        if (r?["ok"]?.GetValue<bool>() == true) return;
        if (r?["gone"]?.GetValue<bool>() == true)
            throw new McpToolException($"The element {@ref} is no longer on the page (the page changed). Call browser_snapshot to see the page as it is now.");
        if (r?["missing"]?.GetValue<bool>() == true) throw Missing(p, @ref);
        throw new McpToolException(r?["error"]?.GetValue<string>() is { } err ? $"Could not use {@ref}: {err}." : $"Could not use {@ref}.");
    }

    /// <summary>Explains where a handle that is not on this page came from.</summary>
    private McpToolException Missing(Page p, string @ref)
    {
        long n = long.TryParse(@ref.TrimStart('e'), out var v) ? v : -1;
        foreach (var other in _pages.Values)
        {
            if (ReferenceEquals(other, p) || n < other.RefLow || n > other.RefHigh) continue;
            int i = IndexOf(other.Tab);
            return new McpToolException($"{@ref} belongs to another tab, [{i}] \"{Short(other.Tab.Title, 60)}\", not the active tab. Switch with browser_tabs (select) or use handles from the active tab.");
        }
        foreach (var (low, high, url) in p.OldRefs)
            if (n >= low && n <= high)
                return new McpToolException($"{@ref} is from an earlier page in this tab ({Short(url, 80)}), before it navigated. Use handles from the latest snapshot.");
        return new McpToolException($"There is no element {@ref} on this page. Use a [ref=…] handle from the latest snapshot (browser_snapshot).");
    }

    // ============================ Snapshots ============================
    private sealed record Snap(string Url, string Title, List<string> Lines, int Count, bool Truncated, int ScrollY, int ScrollHeight, int ViewportHeight);

    private async Task<Snap> SnapAsync(Page p, CancellationToken ct, string? scope = null, int max = 1200)
    {
        var arg = new JsonObject { ["next"] = _nextRef, ["max"] = max };
        if (scope != null) arg["scope"] = scope;
        var r = await Js(p, "snapshot", arg, ct);
        if (scope != null) Check(p, r, scope);
        if (r?["ok"]?.GetValue<bool>() != true)
            throw new McpToolException("The page could not be read: " + (r?["error"]?.GetValue<string>() ?? "unknown problem") + ".");
        long next = (long)(r["next"]?.GetValue<double>() ?? _nextRef);
        if (next > _nextRef)
        {
            p.RefLow = Math.Min(p.RefLow, _nextRef);
            p.RefHigh = Math.Max(p.RefHigh, next - 1);
            _nextRef = next;
        }
        var lines = (r["lines"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? new();
        return new Snap(Str(r, "url"), Str(r, "title"), lines, (int)Num(r, "count"), r["truncated"]?.GetValue<bool>() == true,
            (int)Num(r, "scrollY"), (int)Num(r, "scrollHeight"), (int)Num(r, "viewportHeight"));
    }

    private enum SnapMode { Full, Auto, None }

    /// <summary>The page outline for an agent: in full, or as the lines that changed since that agent's last look.</summary>
    private async Task<string> OutlineAsync(ToolCall call, Page p, SnapMode mode, string? scope = null, int max = 1200)
    {
        var s = await SnapAsync(p, call.Ct, scope, max);
        var sb = new StringBuilder(Header(p, s));
        var key = call.Agent.Key + (scope ?? "");
        string body = string.Join("\n", s.Lines);
        if (mode == SnapMode.Auto && p.Seen.TryGetValue(key, out var seen) && seen.Url == s.Url && seen.Epoch == p.Epoch)
        {
            var diff = Diff(seen.Lines, s.Lines, out int plus, out int minus);
            if (plus == 0 && minus == 0) body = "(nothing on the page changed)";
            else if (diff != null && plus + minus <= Math.Max(14, s.Lines.Count / 2))
                body = $"Changes since your last look at this page (+{plus} −{minus}; call browser_snapshot for the whole page):\n{diff}";
        }
        if (s.Lines.Count == 0 && body.Length == 0) body = "(nothing to act on is visible; try browser_read or browser_take_screenshot)";
        p.Seen[key] = (s.Lines, s.Url, p.Epoch);
        sb.Append('\n').Append(body);
        if (s.Truncated) sb.Append("\n… (the outline stops here: call browser_snapshot with a ref to see one region, or browser_read for the text)");
        return sb.ToString();
    }

    private string Header(Page p, Snap s)
    {
        var w = _bound;
        int count = w?.AutoTabs.Count ?? 1;
        var sb = new StringBuilder();
        sb.Append("Page: ").Append(string.IsNullOrWhiteSpace(s.Title) ? "(untitled)" : s.Title).Append('\n');
        sb.Append("URL: ").Append(s.Url).Append('\n');
        sb.Append($"Tab [{IndexOf(p.Tab)}] of {count}");
        if (p.Tab.Loading) sb.Append(" (still loading)");
        sb.Append($" · {s.Count} element{(s.Count == 1 ? "" : "s")} to act on");
        if (s.ScrollHeight > s.ViewportHeight * 1.15 && s.ViewportHeight > 0)
            sb.Append($" · showing {s.ScrollY:N0}–{s.ScrollY + s.ViewportHeight:N0} of {s.ScrollHeight:N0} px");
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// The lines that changed between two outlines, each hunk under its parent lines for context.
    /// Null when the outlines differ too much for a diff to help.
    /// </summary>
    private static string? Diff(List<string> a, List<string> b, out int plus, out int minus)
    {
        plus = minus = 0;
        int pre = 0;
        while (pre < a.Count && pre < b.Count && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Count - pre && suf < b.Count - pre && a[a.Count - 1 - suf] == b[b.Count - 1 - suf]) suf++;
        int n = a.Count - pre - suf, m = b.Count - pre - suf;
        if (n == 0 && m == 0) return "";
        if ((long)n * m > 4_000_000) { plus = m; minus = n; return null; }

        // Longest common subsequence of the middle part.
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[pre + i] == b[pre + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var ops = new List<(char Op, int Index)>();
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[pre + x] == b[pre + y]) { ops.Add(('=', pre + y)); x++; y++; }
            else if (x < n && (y >= m || lcs[x + 1, y] >= lcs[x, y + 1])) { ops.Add(('-', pre + x)); x++; minus++; }
            else { ops.Add(('+', pre + y)); y++; plus++; }
        }

        var sb = new StringBuilder();
        var context = new HashSet<string>(); // parent lines already printed
        bool gap = false;
        foreach (var (op, idx) in ops)
        {
            if (op == '=') { gap = true; continue; }
            var list = op == '-' ? a : b;
            if (gap && sb.Length > 0) sb.Append("  ⋯\n");
            gap = false;
            // The parents of a changed line (less indented lines above it) say where on the page it is.
            var parents = new List<string>();
            int depth = Indent(list[idx]);
            for (int k = idx - 1; k >= 0 && depth > 0; k--)
            {
                int d = Indent(list[k]);
                if (d < depth) { parents.Add(list[k]); depth = d; }
            }
            for (int k = parents.Count - 1; k >= 0; k--)
                if (context.Add(parents[k])) sb.Append("  ").Append(parents[k]).Append('\n');
            sb.Append(op == '+' ? "+ " : "- ").Append(list[idx]).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static int Indent(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        return i;
    }

    // ============================ Waiting for the page ============================
    /// <summary>
    /// Waits until the page is quiet: no navigation under way and no requests in flight for a short
    /// while, bounded so a page that never stops talking does not hold the agent.
    /// </summary>
    private async Task SettleAsync(Page p, CancellationToken ct, int minMs = 120, int quietMs = 350, int maxMs = 4000)
    {
        var start = DateTime.Now;
        await Task.Delay(minMs, ct);
        while ((DateTime.Now - start).TotalMilliseconds < maxMs)
        {
            if (p.Dialog != null) return;
            bool navigating = p.NavId != 0 && p.DoneId != p.NavId && p.DomId != p.NavId;
            int busy = p.InFlight.Values.Count(e => !e.LongLived && DateTime.Now - e.Start < TimeSpan.FromSeconds(6));
            if (!navigating && busy == 0 && (DateTime.Now - p.LastNet).TotalMilliseconds >= quietMs) return;
            await Task.Delay(50, ct);
        }
    }

    /// <summary>
    /// Waits for a navigation that started after <paramref name="since"/> to be usable: the document
    /// is ready (DOMContentLoaded) and the network settles, or it fails, or the time is up.
    /// </summary>
    private async Task<string?> WaitLoadAsync(Page p, ulong since, ToolCall call, int timeoutMs)
    {
        var start = DateTime.Now;
        DateTime? ready = null;
        while (true)
        {
            var spent = (DateTime.Now - start).TotalMilliseconds;
            if (p.Dialog != null) return null;
            bool started = p.NavId != since;
            if (started && p.DoneId == p.NavId)
            {
                // A failed https try of a typed name gets one more try on http, by the window itself.
                if (!p.NavOk && p.Tab.Loading && spent < timeoutMs) { await Task.Delay(80, call.Ct); continue; }
                if (!p.Tab.Loading || spent >= timeoutMs) break;
            }
            if (started && p.DomId == p.NavId)
            {
                ready ??= DateTime.Now;
                if ((DateTime.Now - ready.Value).TotalMilliseconds > 1500) break; // usable, heavy pages keep loading
            }
            if (!started && spent > 1200 && !p.Tab.Loading) break; // nothing navigated (a single-page app moved in place)
            if (spent >= timeoutMs) return "The page took too long to load, it may still be loading.";
            if ((int)spent % 3000 < 60) call.Progress($"Loading {BrowserStore.HostOf(p.Core.Source)}");
            await Task.Delay(60, call.Ct);
        }
        if (!p.NavOk && p.DoneId == p.NavId) return NavNote(p.NavError);
        await SettleAsync(p, call.Ct, minMs: 50, maxMs: 2500);
        return p.NavStatus >= 400 ? $"The website answered with error {p.NavStatus}." : null;
    }

    private static string NavNote(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.Timeout => "The page took too long to load.",
        CoreWebView2WebErrorStatus.HostNameNotResolved => "The website address could not be found (DNS). Check the address or the connection.",
        CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ServerUnreachable =>
            "Could not reach the website. If PdaNet goes online through the phone, the phone's internet may be down.",
        CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset => "The connection to the website dropped.",
        CoreWebView2WebErrorStatus.OperationCanceled => "The navigation was cancelled (a download, or the page went elsewhere).",
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired
            or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or CoreWebView2WebErrorStatus.CertificateRevoked
            or CoreWebView2WebErrorStatus.CertificateIsInvalid => "The website's certificate did not check out.",
        _ => $"The page did not load ({status}).",
    };

    // ============================ What an action changed ============================
    private sealed record Before(int Tabs, BrowserTab? Active, long Epoch, ulong NavId, int Downloads, int Errors, HashSet<int> TabIds);

    private Before Capture(Page p)
    {
        var w = _bound;
        var ids = w == null ? new HashSet<int>() : w.AutoTabs.Select(t => t.Id).ToHashSet();
        return new Before(ids.Count, w?.AutoActive, p.Epoch, p.NavId, _downloads.Count, p.Errors, ids);
    }

    /// <summary>
    /// Runs an action on the active page and reports what it changed, then the page outline (or
    /// what changed in it). An action interrupted by a dialog reports the dialog instead.
    /// </summary>
    private Task<ToolResult> ActAsync(ToolCall call, string caption, Func<Page, Act, Task> action,
        bool chooser = false, SnapMode mode = SnapMode.Auto, int settleMs = 4000) =>
        RunAsync(call, caption, async () =>
        {
            var p = await ActivePageAsync(call);
            await OwnAsync(call, p);
            await HoldAsync();
            var before = Capture(p);
            var act = new Act();
            // A file chooser the action opens is caught rather than shown. It can open a moment after the
            // click, so the catching lasts until the page settles. Tabs the agents opened catch all along.
            bool intercept = chooser && !p.Intercepting;
            if (intercept) await InterceptChooserAsync(p, true);
            try
            {
                try { await action(p, act); }
                catch (DialogOpenException) { /* reported below */ }
                return ToolResult.Ok(await ReportAsync(call, p, before, act, mode, settleMs));
            }
            finally
            {
                if (intercept) await InterceptChooserAsync(p, false);
            }
        });

    private sealed class Act
    {
        public string Line = "Done.";
        public List<string> Notes { get; } = new();
    }

    private async Task<string> ReportAsync(ToolCall call, Page p, Before before, Act act, SnapMode mode, int settleMs)
    {
        var sb = new StringBuilder(act.Line);
        foreach (var n in act.Notes) sb.Append("\n→ ").Append(n);
        if (p.Dialog == null)
        {
            if (p.NavId != before.NavId) { if (await WaitLoadAsync(p, before.NavId, call, 15000) is { } note) p.Notes.Add(note); }
            else await SettleAsync(p, call.Ct, maxMs: settleMs);
        }

        var w = Win();
        BrowserTab? follow = null;
        foreach (var t in w.AutoTabs.Where(t => !before.TabIds.Contains(t.Id)))
        {
            // A tab the action opened is the agent's. One the person opened meanwhile stays theirs.
            if (t.IsStart || !(_mine.Contains(t.Id) || OpenedByAgents(t))) continue;
            _mine.Add(t.Id);
            bool front = ReferenceEquals(t, w.AutoActive);
            if (front) follow = t;
            sb.Append($"\n→ A new tab opened: [{IndexOf(t)}] {Short(t.Title, 60)}{(BrowserStore.IsWeb(t.Url) ? " — " + Short(t.Url, 80) : "")}{(front ? " (now the active tab)" : "")}");
        }
        // The agent follows a tab its action opened. Otherwise it stays in the tab it acted in, also
        // when the person moved to another tab meanwhile: that tab is theirs, not the agent's.
        var active = w.AutoActive;
        var tab = follow ?? (w.AutoTabs.Contains(p.Tab) ? p.Tab : null);
        _agentTab = tab;
        if (tab == null)
            sb.Append("\n→ The page closed its tab.");
        else if (active != null && !ReferenceEquals(active, tab) && !ReferenceEquals(active, before.Active) && before.TabIds.Contains(active.Id))
            sb.Append($"\n→ The person is on tab [{IndexOf(active)}] {Short(active.Title, 60)} now. Your tab is still [{IndexOf(tab)}]; your next action brings it back to the front.");
        if (p.Epoch != before.Epoch && ReferenceEquals(p.Tab, tab))
            sb.Append($"\n→ Navigated to {Short(p.Core.Source, 120)}");

        // The outline is of the tab the next call works in, so its handles fit that call.
        var shown = tab ?? active;
        var page = shown == null || ReferenceEquals(shown, p.Tab) ? p : PageOf(shown) ?? p;
        if (!ReferenceEquals(page, p) && page.Dialog == null) await SettleAsync(page, call.Ct, minMs: 0, maxMs: 2500);
        foreach (var src in ReferenceEquals(page, p) ? new[] { p } : new[] { p, page })
        {
            foreach (var n in src.Notes) sb.Append("\n→ ").Append(n);
            src.Notes.Clear();
        }
        int errors = p.Errors - before.Errors;
        if (errors > 0) sb.Append($"\n→ {errors} new console error{(errors == 1 ? "" : "s")} (see browser_console_messages).");

        if (page.Dialog is { } d)
        {
            sb.Append("\n→ ").Append(DialogLine(d)).Append(" The page waits for an answer: call browser_handle_dialog (accept or dismiss).");
            return sb.ToString();
        }
        if (mode == SnapMode.None) return sb.ToString();
        try { sb.Append("\n\n").Append(await OutlineAsync(call, page, mode)); }
        catch (DialogOpenException)
        {
            if (page.Dialog is { } d2) sb.Append("\n→ ").Append(DialogLine(d2)).Append(" Answer it with browser_handle_dialog.");
        }
        catch (McpToolException e) { sb.Append("\n\n(").Append(e.Message).Append(')'); }
        return sb.ToString();
    }

    private async Task InterceptChooserAsync(Page p, bool on)
    {
        if (p.Intercepting == on) return;
        if (await QuickAsync(p, "Page.setInterceptFileChooserDialog", on ? "{\"enabled\":true}" : "{\"enabled\":false}")) p.Intercepting = on;
    }

    /// <summary>A small CDP call for upkeep (hooks, highlights): best effort, and never longer than a few seconds.</summary>
    private static async Task<bool> QuickAsync(Page p, string method, string args)
    {
        try
        {
            await p.Core.CallDevToolsProtocolMethodAsync(method, args).WaitAsync(TimeSpan.FromSeconds(3));
            return true;
        }
        catch (Exception e)
        {
            Logger.Instance.Debug($"Agent upkeep {method}: {e.Message}");
            return false;
        }
    }

    /// <summary>Draws the agent's mark around what it is about to touch, so the person sees it, without changing the page.</summary>
    private async Task HighlightAsync(Page p, double x, double y, double w, double h, CancellationToken ct)
    {
        if (!Watched || w <= 0 || h <= 0) return;
        try
        {
            if (!p.Overlay)
            {
                await p.Core.CallDevToolsProtocolMethodAsync("DOM.enable", "{}").WaitAsync(TimeSpan.FromSeconds(3), ct);
                await p.Core.CallDevToolsProtocolMethodAsync("Overlay.enable", "{}").WaitAsync(TimeSpan.FromSeconds(3), ct);
                p.Overlay = true;
            }
            await p.Core.CallDevToolsProtocolMethodAsync("Overlay.highlightRect", new JsonObject
            {
                ["x"] = (int)Math.Round(x) - 2,
                ["y"] = (int)Math.Round(y) - 2,
                ["width"] = (int)Math.Round(w) + 4,
                ["height"] = (int)Math.Round(h) + 4,
                ["color"] = new JsonObject { ["r"] = 43, ["g"] = 245, ["b"] = 168, ["a"] = 0.16 },
                ["outlineColor"] = new JsonObject { ["r"] = 43, ["g"] = 245, ["b"] = 168, ["a"] = 0.95 },
            }.ToJsonString()).WaitAsync(TimeSpan.FromSeconds(3), ct);
            await Task.Delay(140, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Logger.Instance.Debug($"Highlight: {e.Message}"); }
        finally
        {
            try { _ = p.Core.CallDevToolsProtocolMethodAsync("Overlay.hideHighlight", "{}").ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
            catch { }
        }
    }

    // ============================ Helpers ============================
    private sealed class DialogOpenException : Exception { }

    private sealed class CdpException : Exception
    {
        public CdpException(string method, string message) : base($"{method}: {message}") => Method = method;

        public string Method { get; }

        public bool ContextGone =>
            Message.Contains("context", StringComparison.OrdinalIgnoreCase) &&
            (Message.Contains("not found", StringComparison.OrdinalIgnoreCase) || Message.Contains("Cannot find", StringComparison.OrdinalIgnoreCase) || Message.Contains("destroyed", StringComparison.OrdinalIgnoreCase));

        public string Friendly =>
            Message.Contains("No node", StringComparison.OrdinalIgnoreCase) || Message.Contains("Could not find node", StringComparison.OrdinalIgnoreCase)
                ? "The element is no longer on the page. Call browser_snapshot to see the page as it is now."
                : ContextGone ? "The page changed while it was being read. Try again."
                : "The browser could not do that: " + Message;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Observe(Task t) => t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);

    private static double Num(JsonNode? n, string key)
    {
        try { return n?[key]?.GetValue<double>() ?? 0; }
        catch { return 0; }
    }

    private static string Str(JsonNode? n, string key)
    {
        try { return n?[key]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }

    private static string Short(string? s, int n)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
        return s.Length > n ? s[..(n - 1)] + "…" : s;
    }

    private static string LoadPageJs()
    {
        var asm = typeof(BrowserSession).Assembly;
        using var s = asm.GetManifestResourceStream("PdaNetPortable.snapshot.js")
            ?? throw new InvalidOperationException("Embedded snapshot.js not found.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
