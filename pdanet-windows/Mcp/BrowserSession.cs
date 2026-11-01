using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PdaNetPortable.Services;
using PdaNetPortable.Views;

namespace PdaNetPortable.Mcp;

/// <summary>
/// The automation engine behind the browser tools. It owns a real <see cref="BrowserWindow"/>
/// and exposes async primitives — navigate, snapshot, click, type, read, screenshot — that the
/// MCP tool handlers call. WebView2 may only be touched on the UI thread, so every method hops
/// onto the window's dispatcher first; the long waits (page loads, script evaluation) happen
/// there while the dispatcher keeps pumping, so the UI stays live and watchable.
///
/// Perception is an injected script that returns an accessibility tree with [ref=eN] handles.
/// Interaction uses the Chrome DevTools Protocol (trusted mouse and key events), so clicks and
/// typing work on demanding single-page apps the same way a person's input would.
/// </summary>
public sealed class BrowserSession
{
    private static readonly string PageJs = LoadPageJs();

    private readonly Dispatcher _dispatcher;
    private readonly bool _visible;
    private BrowserWindow? _win;

    // Per-page instrumentation: console lines, recent responses, the last JS dialog.
    private readonly HashSet<CoreWebView2> _wired = new();
    private readonly List<string> _console = new();
    private readonly List<string> _network = new();
    private (string Kind, string Message)? _lastDialog;
    private bool _dialogAccept = true;
    private string? _dialogPromptText;

    private const int BufferCap = 120;

    public BrowserSession(Dispatcher dispatcher, bool visible)
    {
        _dispatcher = dispatcher;
        _visible = visible;
    }

    /// <summary>Creates and (optionally) shows the browser window. Call once, on the UI thread.</summary>
    public void Start()
    {
        _win = new BrowserWindow(null);
        if (_visible)
        {
            _win.Show();
            _win.Activate();
        }
        else
        {
            // Off-screen but realized, so WebView2 still renders and screenshots work.
            _win.WindowStartupLocation = WindowStartupLocation.Manual;
            _win.Left = -32000;
            _win.Top = -32000;
            _win.ShowInTaskbar = false;
            _win.Show();
        }
        _win.Closed += (_, _) => Closed?.Invoke();
    }

    /// <summary>Raised when the browser window closes, so the host can shut the server down.</summary>
    public event Action? Closed;

    public void Stop() => _dispatcher.Invoke(() => { try { _win?.Close(); } catch { } });

    // ============================ Navigation & tabs ============================
    public Task<string> NavigateAsync(string text, bool newTab, int timeoutMs) => OnUi(async () =>
    {
        var (url, plain) = Resolve(text);
        if (newTab) Win.AutoNewTab();
        var core = await Win.AutoEnsureCoreAsync();   // create the view first…
        if (core != null) Instrument(core);           // …so console/network capture the first load
        return await AfterNavAsync(await Win.AutoNavigateAsync(url, plain, timeoutMs));
    });

    public Task<string> BackAsync(int timeoutMs) => NavResultAsync(() => Win.AutoBackAsync(timeoutMs));
    public Task<string> ForwardAsync(int timeoutMs) => NavResultAsync(() => Win.AutoForwardAsync(timeoutMs));
    public Task<string> ReloadAsync(bool hard, int timeoutMs) => NavResultAsync(() => Win.AutoReloadAsync(hard, timeoutMs));

    private Task<string> NavResultAsync(Func<Task<BrowserWindow.AutoPage>> act) =>
        OnUi(async () => await AfterNavAsync(await act()));

    // The snapshot already carries the page title/URL, so a navigation reply is just the fresh
    // snapshot, with any load note (timeout, DNS failure, start page) on top.
    private async Task<string> AfterNavAsync(BrowserWindow.AutoPage page)
    {
        var core = Win.AutoActiveCore;
        if (core == null) return PageHeader(page);
        var snap = await SnapshotTextAsync(core);
        return string.IsNullOrEmpty(page.Note) ? snap : $"Note: {page.Note}\n\n{snap}";
    }

    public Task<string> TabsAsync(string action, int index, string? url, int timeoutMs) => OnUi(async () =>
    {
        switch (action)
        {
            case "new":
                Win.AutoNewTab();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var (u, p) = Resolve(url!);
                    var core = await Win.AutoEnsureCoreAsync();
                    if (core != null) Instrument(core);
                    await Win.AutoNavigateAsync(u, p, timeoutMs);
                }
                break;
            case "select": Win.AutoSelectTab(index); break;
            case "close": Win.AutoCloseTab(index); break;
            case "list": break;
            default: throw new McpToolException($"Unknown tabs action '{action}'. Use list, new, select or close.");
        }
        return TabListText();
    });

    private string TabListText()
    {
        var tabs = Win.AutoTabs();
        var sb = new StringBuilder();
        sb.Append(tabs.Count == 1 ? "1 open tab:" : $"{tabs.Count} open tabs:").Append('\n');
        foreach (var t in tabs)
        {
            sb.Append(t.Active ? "  * " : "    ").Append('[').Append(t.Index).Append("] ");
            sb.Append(t.Title);
            if (!string.IsNullOrEmpty(t.Url)) sb.Append("  —  ").Append(Shorten(t.Url, 80));
            if (t.Loading) sb.Append("  (loading)");
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    // ============================ Perception ============================
    public Task<string> SnapshotAsync() => OnUi(async () =>
    {
        var core = ReqCore();
        return await SnapshotTextAsync(core);
    });

    public Task<string> ReadAsync(string format) => OnUi(async () =>
    {
        var core = ReqCore();
        var r = await PageAsync(core, "read", JsonValue.Create(format == "markdown" ? "markdown" : "text"));
        var title = Str(r, "title");
        var text = Str(r, "text");
        return (string.IsNullOrEmpty(title) ? "" : $"# {title}\n\n") + text;
    });

    public Task<(byte[] Png, string Note)> ScreenshotAsync(bool fullPage) => OnUi(async () =>
    {
        var core = ReqCore();
        byte[] bytes;
        if (fullPage)
        {
            var raw = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot",
                "{\"format\":\"png\",\"captureBeyondViewport\":true}");
            var data = JsonNode.Parse(raw)?["data"]?.GetValue<string>();
            if (string.IsNullOrEmpty(data)) throw new McpToolException("The screenshot could not be captured.");
            bytes = Convert.FromBase64String(data);
        }
        else
        {
            using var ms = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            bytes = ms.ToArray();
        }
        return (bytes, $"Screenshot of {core.Source} ({(fullPage ? "full page" : "viewport")}, {bytes.Length / 1024} KB).");
    });

    // ============================ Interaction ============================
    public Task<string> ClickAsync(string @ref, string button, bool doubleClick) => OnUi(async () =>
    {
        var core = ReqCore();
        var r = await Resolve(core, @ref);
        double x = Num(r, "cx"), y = Num(r, "cy");
        string btn = button is "right" or "middle" ? button : "left";
        await Cdp(core, "Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseMoved", ["x"] = x, ["y"] = y });
        int clicks = doubleClick ? 2 : 1;
        await Cdp(core, "Input.dispatchMouseEvent", new JsonObject
        { ["type"] = "mousePressed", ["x"] = x, ["y"] = y, ["button"] = btn, ["buttons"] = Buttons(btn), ["clickCount"] = clicks });
        await Cdp(core, "Input.dispatchMouseEvent", new JsonObject
        { ["type"] = "mouseReleased", ["x"] = x, ["y"] = y, ["button"] = btn, ["buttons"] = 0, ["clickCount"] = clicks });
        await SettleAsync(core);
        var what = doubleClick ? "Double-clicked" : btn == "right" ? "Right-clicked" : "Clicked";
        return await ActionResultAsync(core, $"{what} {Describe(r)}.");
    });

    public Task<string> TypeAsync(string @ref, string text, bool submit, bool clear) => OnUi(async () =>
    {
        var core = ReqCore();
        var f = await PageAsync(core, "focus", RefArg(@ref));
        CheckResolved(f, @ref);
        if (clear) await PageAsync(core, "clear", RefArg(@ref));
        if (text.Length > 0) await Cdp(core, "Input.insertText", new JsonObject { ["text"] = text });
        if (submit) await PressRawAsync(core, "Enter");
        await SettleAsync(core);
        var line = $"Typed \"{Shorten(text, 60)}\" into {Describe(f)}." + (submit ? " Submitted with Enter." : "");
        return await ActionResultAsync(core, line);
    });

    public Task<string> PressKeyAsync(string key) => OnUi(async () =>
    {
        var core = ReqCore();
        await PressRawAsync(core, key);
        await SettleAsync(core);
        return await ActionResultAsync(core, $"Pressed {key}.");
    });

    public Task<string> HoverAsync(string @ref) => OnUi(async () =>
    {
        var core = ReqCore();
        var r = await Resolve(core, @ref);
        await Cdp(core, "Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseMoved", ["x"] = Num(r, "cx"), ["y"] = Num(r, "cy") });
        await SettleAsync(core, 250);
        return await ActionResultAsync(core, $"Hovered {Describe(r)}.");
    });

    public Task<string> SelectAsync(string @ref, IReadOnlyList<string> values) => OnUi(async () =>
    {
        var core = ReqCore();
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        var r = await PageAsync(core, "select", new JsonObject { ["ref"] = @ref, ["values"] = arr });
        CheckResolved(r, @ref);
        if (r?["ok"]?.GetValue<bool>() != true)
            throw new McpToolException("None of those options matched. Pass option labels or values exactly as they appear.");
        var chosen = (r["selected"] as JsonArray)?.Select(n => n!.GetValue<string>()) ?? Enumerable.Empty<string>();
        await SettleAsync(core, 250);
        return await ActionResultAsync(core, $"Selected {string.Join(", ", chosen)}.");
    });

    public Task<string> ScrollAsync(string? direction, string? @ref, int amount) => OnUi(async () =>
    {
        var core = ReqCore();
        var arg = new JsonObject();
        if (!string.IsNullOrEmpty(@ref)) arg["ref"] = @ref;
        if (!string.IsNullOrEmpty(direction)) arg["dir"] = direction;
        if (amount > 0) arg["amount"] = amount;
        await PageAsync(core, "scroll", arg);
        await SettleAsync(core, 200);
        return await ActionResultAsync(core, @ref != null ? "Scrolled element into view." : $"Scrolled {direction ?? "down"}.");
    });

    public Task<string> DragAsync(string fromRef, string toRef) => OnUi(async () =>
    {
        var core = ReqCore();
        var a = await Resolve(core, fromRef);
        var b = await Resolve(core, toRef);
        await Cdp(core, "Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseMoved", ["x"] = Num(a, "cx"), ["y"] = Num(a, "cy") });
        await Cdp(core, "Input.dispatchMouseEvent", new JsonObject { ["type"] = "mousePressed", ["x"] = Num(a, "cx"), ["y"] = Num(a, "cy"), ["button"] = "left", ["buttons"] = 1, ["clickCount"] = 1 });
        await Cdp(core, "Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseMoved", ["x"] = Num(b, "cx"), ["y"] = Num(b, "cy"), ["buttons"] = 1 });
        await Cdp(core, "Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseReleased", ["x"] = Num(b, "cx"), ["y"] = Num(b, "cy"), ["button"] = "left", ["buttons"] = 0, ["clickCount"] = 1 });
        await SettleAsync(core);
        return await ActionResultAsync(core, $"Dragged {Describe(a)} onto {Describe(b)}.");
    });

    // ============================ Control & inspection ============================
    public Task<string> WaitForAsync(string? text, string? textGone, int timeMs, int timeoutMs) => OnUi(async () =>
    {
        var core = ReqCore();
        if (timeMs > 0)
        {
            await Task.Delay(Math.Min(timeMs, timeoutMs));
            return await ActionResultAsync(core, $"Waited {Math.Min(timeMs, timeoutMs)} ms.");
        }
        var needle = text ?? textGone;
        bool want = text != null;
        if (needle == null) throw new McpToolException("Pass text, textGone or time.");
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs)
        {
            var r = await PageAsync(core, "hasText", JsonValue.Create(needle));
            if ((r?["found"]?.GetValue<bool>() ?? false) == want)
                return await ActionResultAsync(core, want ? $"Text \"{Shorten(needle, 60)}\" appeared." : $"Text \"{Shorten(needle, 60)}\" is gone.");
            await Task.Delay(200);
        }
        throw new McpToolException(want
            ? $"Timed out after {timeoutMs} ms waiting for text \"{Shorten(needle, 60)}\"."
            : $"Timed out after {timeoutMs} ms waiting for text \"{Shorten(needle, 60)}\" to disappear.");
    });

    public Task<string> EvaluateAsync(string script, string? @ref) => OnUi(async () =>
    {
        var core = ReqCore();
        if (!string.IsNullOrEmpty(@ref))
        {
            var bind = await PageAsync(core, "focus", RefArg(@ref)); // also resolves & validates the ref
            CheckResolved(bind, @ref);
        }
        // The element (when a ref was given) is available to the script as window.__pdaEl.
        var bindEl = string.IsNullOrEmpty(@ref) ? "" :
            $"window.__pdaEl=(window.__pdaEls||[])[{RefIndex(@ref)}];";
        var wrapped = "(function(){try{" + bindEl + "var __r=(function(){" + script + "\n})();" +
                      "return JSON.stringify(__r===undefined?null:__r);}catch(e){return JSON.stringify({__pdaError:String(e&&e.message||e)});}})()";
        var raw = await core.ExecuteScriptAsync(wrapped);
        var inner = raw == null ? "null" : JsonNode.Parse(raw)?.GetValue<string>() ?? "null";
        var node = JsonNode.Parse(inner);
        if (node is JsonObject o && o.ContainsKey("__pdaError"))
            throw new McpToolException("Script error: " + o["__pdaError"]!.GetValue<string>());
        return Shorten(inner, 8000);
    });

    public Task<string> ConsoleAsync(int limit) => OnUi(() =>
    {
        _ = ReqCore(); // ensures a page is open and instrumented
        var items = _console.TakeLast(Math.Clamp(limit, 1, BufferCap)).ToList();
        return Task.FromResult(items.Count == 0 ? "No console messages captured since this page loaded." : string.Join("\n", items));
    });

    public Task<string> NetworkAsync(int limit) => OnUi(() =>
    {
        var core = ReqCore();
        var (title, body, _) = Win.AutoRoute();
        var sb = new StringBuilder();
        sb.Append("Route: ").Append(title).Append(" — ").Append(body).Append("\n\n");
        var items = _network.TakeLast(Math.Clamp(limit, 1, BufferCap)).ToList();
        sb.Append(items.Count == 0
            ? "No network responses captured yet. Reload the page (browser_reload) to capture its requests, or interact to trigger more."
            : string.Join("\n", items));
        return Task.FromResult(sb.ToString());
    });

    public Task<string> HandleDialogAsync(bool accept, string? promptText) => OnUi(() =>
    {
        _dialogAccept = accept;
        _dialogPromptText = promptText;
        var last = _lastDialog;
        var note = last == null
            ? "No dialog has appeared yet."
            : $"Last dialog — {last.Value.Kind}: \"{last.Value.Message}\".";
        return Task.FromResult($"{note} Future dialogs will be {(accept ? "accepted" : "dismissed")}" +
            (accept && promptText != null ? $" with text \"{promptText}\"" : "") + ".");
    });

    // ============================ In-page script & CDP ============================
    private async Task<JsonNode?> PageAsync(CoreWebView2 core, string op, JsonNode? arg)
    {
        var call = "(function(){" + PageJs + "\ntry{return JSON.stringify(window.__pdaDispatch(" +
                   JsonStr(op) + "," + (arg?.ToJsonString() ?? "null") + "));}catch(e){return JSON.stringify({ok:false,error:String(e)});}})()";
        var raw = await core.ExecuteScriptAsync(call);
        if (string.IsNullOrEmpty(raw) || raw == "null")
            throw new McpToolException("The page could not be inspected (it may be a browser or PDF page with no DOM).");
        var inner = JsonNode.Parse(raw)?.GetValue<string>();
        return inner == null ? null : JsonNode.Parse(inner);
    }

    private Task<string> Cdp(CoreWebView2 core, string method, JsonObject @params) =>
        core.CallDevToolsProtocolMethodAsync(method, @params.ToJsonString());

    private async Task<JsonNode> Resolve(CoreWebView2 core, string @ref)
    {
        var r = await PageAsync(core, "resolve", RefArg(@ref));
        CheckResolved(r, @ref);
        return r!;
    }

    private async Task<string> SnapshotTextAsync(CoreWebView2 core)
    {
        Instrument(core);
        var r = await PageAsync(core, "snapshot", null);
        var url = Str(r, "url");
        var title = Str(r, "title");
        var text = Str(r, "text");
        var count = (int)Num(r, "count");
        var head = $"Page: {title}\nURL: {url}\n{count} actionable element(s)"
            + (r?["truncated"]?.GetValue<bool>() == true ? " (snapshot truncated — scroll or read for more)" : "") + "\n";
        var dialog = DrainDialogNote();
        if (dialog != null) head = dialog + "\n" + head;
        return head + "\n" + (string.IsNullOrWhiteSpace(text) ? "(no actionable elements found)" : text);
    }

    private async Task<string> ActionResultAsync(CoreWebView2 core, string line)
    {
        var dialog = DrainDialogNote();
        var snap = await SnapshotTextAsync(core);
        var sb = new StringBuilder(line);
        if (dialog != null) sb.Append('\n').Append(dialog);
        sb.Append("\n\n").Append(snap);
        return sb.ToString();
    }

    // Wait briefly, then until the document stops loading, so the returned snapshot reflects the result.
    private async Task SettleAsync(CoreWebView2 core, int quietMs = 400, int maxMs = 9000)
    {
        await Task.Delay(quietMs);
        var start = Environment.TickCount;
        while (Environment.TickCount - start < maxMs)
        {
            string rs;
            try { rs = await core.ExecuteScriptAsync("document.readyState"); }
            catch { break; }
            if (rs is "\"complete\"" or "\"interactive\"") break;
            await Task.Delay(150);
        }
    }

    private async Task PressRawAsync(CoreWebView2 core, string key)
    {
        if (NamedKeys.TryGetValue(key, out var k))
        {
            string text = key.Equals("Enter", StringComparison.OrdinalIgnoreCase) ? "\r"
                : key.Equals("Space", StringComparison.OrdinalIgnoreCase) ? " " : "";
            string keyName = key.Equals("Space", StringComparison.OrdinalIgnoreCase) ? " "
                : key.Equals("Esc", StringComparison.OrdinalIgnoreCase) ? "Escape" : key;
            var down = new JsonObject
            {
                ["type"] = text.Length > 0 ? "keyDown" : "rawKeyDown",
                ["windowsVirtualKeyCode"] = k.Vk,
                ["key"] = keyName,
                ["code"] = k.Code,
            };
            if (text.Length > 0) down["text"] = text;
            await Cdp(core, "Input.dispatchKeyEvent", down);
            await Cdp(core, "Input.dispatchKeyEvent", new JsonObject
            { ["type"] = "keyUp", ["windowsVirtualKeyCode"] = k.Vk, ["key"] = keyName, ["code"] = k.Code });
        }
        else if (key.Length == 1)
        {
            await Cdp(core, "Input.insertText", new JsonObject { ["text"] = key });
        }
        else throw new McpToolException($"Unknown key '{key}'. Try a single character or Enter, Tab, Escape, Backspace, Delete, ArrowUp/Down/Left/Right, Home, End, PageUp, PageDown.");
    }

    // ============================ Instrumentation ============================
    private void Instrument(CoreWebView2 core)
    {
        if (!_wired.Add(core)) return;
        try
        {
            core.ScriptDialogOpening += OnDialog;
            core.PermissionRequested += OnPermission;
            _ = core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            _ = core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
            _ = core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += OnConsole;
            core.GetDevToolsProtocolEventReceiver("Log.entryAdded").DevToolsProtocolEventReceived += OnLogEntry;
            core.GetDevToolsProtocolEventReceiver("Network.responseReceived").DevToolsProtocolEventReceived += OnResponse;
        }
        catch { /* best effort — console/network just stay empty */ }
    }

    private void OnDialog(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        _lastDialog = (e.Kind.ToString(), e.Message ?? "");
        var deferral = e.GetDeferral();
        try
        {
            if (_dialogAccept)
            {
                if (e.Kind == CoreWebView2ScriptDialogKind.Prompt && _dialogPromptText != null)
                    e.ResultText = _dialogPromptText;
                e.Accept();
            }
            // When not accepting, completing the deferral without Accept() dismisses the dialog.
        }
        finally { deferral.Complete(); }
    }

    private void OnPermission(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        // Deny silently so an automated run never blocks on a permission prompt.
        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;
    }

    private void OnConsole(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            var n = JsonNode.Parse(e.ParameterObjectAsJson);
            var type = n?["type"]?.GetValue<string>() ?? "log";
            var args = n?["args"] as JsonArray;
            var parts = args?.Select(a => a?["value"]?.ToJsonString() ?? a?["description"]?.GetValue<string>() ?? a?["type"]?.GetValue<string>() ?? "")
                ?? Enumerable.Empty<string>();
            Push(_console, $"[{type}] {string.Join(" ", parts)}".Trim());
        }
        catch { }
    }

    private void OnLogEntry(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            var entry = JsonNode.Parse(e.ParameterObjectAsJson)?["entry"];
            var level = entry?["level"]?.GetValue<string>() ?? "info";
            var text = entry?["text"]?.GetValue<string>() ?? "";
            if (level is "error" or "warning") Push(_console, $"[{level}] {text}");
        }
        catch { }
    }

    private void OnResponse(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            var resp = JsonNode.Parse(e.ParameterObjectAsJson)?["response"];
            var status = resp?["status"]?.GetValue<int>() ?? 0;
            var url = resp?["url"]?.GetValue<string>() ?? "";
            var type = JsonNode.Parse(e.ParameterObjectAsJson)?["type"]?.GetValue<string>() ?? "";
            Push(_network, $"{status}  {type,-10}  {Shorten(url, 90)}");
        }
        catch { }
    }

    private static void Push(List<string> buf, string line)
    {
        buf.Add(line);
        if (buf.Count > BufferCap) buf.RemoveRange(0, buf.Count - BufferCap);
    }

    private string? DrainDialogNote()
    {
        if (_lastDialog == null) return null;
        var d = _lastDialog.Value;
        _lastDialog = null;
        return $"A JavaScript {d.Kind} dialog appeared: \"{Shorten(d.Message, 160)}\" — {(_dialogAccept ? "accepted" : "dismissed")} automatically.";
    }

    // ============================ Helpers ============================
    private BrowserWindow Win => _win ?? throw new McpToolException("The browser is not running.");

    private CoreWebView2 ReqCore()
    {
        var core = Win.AutoActiveCore ?? throw new McpToolException("No web page is open yet. Call browser_navigate first.");
        Instrument(core);
        return core;
    }

    private Task<T> OnUi<T>(Func<Task<T>> f) => _dispatcher.InvokeAsync(f).Task.Unwrap();

    private static (string Url, string? Plain) Resolve(string text)
    {
        var url = BrowserStore.Instance.Resolve(text, out var plain);
        if (string.IsNullOrEmpty(url)) throw new McpToolException("Nothing to open — pass a URL or search text.");
        return (url, plain);
    }

    private static string PageHeader(BrowserWindow.AutoPage p)
    {
        var head = $"Page: {(string.IsNullOrEmpty(p.Title) ? "(untitled)" : p.Title)}\nURL: {p.Url}";
        if (!string.IsNullOrEmpty(p.Note)) head += $"\nNote: {p.Note}";
        return head;
    }

    private static void CheckResolved(JsonNode? r, string @ref)
    {
        if (r == null) throw new McpToolException("The page could not be inspected. Call browser_snapshot and try again.");
        if (r["ok"]?.GetValue<bool>() == true) return;
        if (r["stale"]?.GetValue<bool>() == true)
            throw new McpToolException($"Handle '{@ref}' is stale — the page changed since the last snapshot. Call browser_snapshot to get fresh handles.");
        if (r["missing"]?.GetValue<bool>() == true)
            throw new McpToolException($"No element has handle '{@ref}'. Call browser_snapshot to see the current handles.");
        var err = r["error"]?.GetValue<string>();
        throw new McpToolException(err ?? $"Could not use handle '{@ref}'.");
    }

    private static string Describe(JsonNode? r)
    {
        var tag = r?["tag"]?.GetValue<string>() ?? "element";
        var name = r?["name"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(name) ? $"the {tag}" : $"the {tag} \"{Shorten(name!, 50)}\"";
    }

    // resolve / focus / clear take the ref as a bare JSON string argument.
    private static JsonNode RefArg(string @ref) => JsonValue.Create(@ref)!;

    private static int RefIndex(string @ref) => int.TryParse(@ref.TrimStart('e'), out var i) ? i - 1 : -1;

    private static double Num(JsonNode? n, string key) => n?[key]?.GetValue<double>() ?? 0;
    private static string Str(JsonNode? n, string key) => n?[key]?.GetValue<string>() ?? "";
    private static string JsonStr(string s) => JsonSerializer.Serialize(s);
    private static int Buttons(string btn) => btn switch { "right" => 2, "middle" => 4, _ => 1 };

    private static string Shorten(string s, int n)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
        return s.Length > n ? s[..(n - 1)] + "…" : s;
    }

    private static readonly Dictionary<string, (int Vk, string Code)> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = (13, "Enter"), ["Tab"] = (9, "Tab"), ["Escape"] = (27, "Escape"), ["Esc"] = (27, "Escape"),
        ["Backspace"] = (8, "Backspace"), ["Delete"] = (46, "Delete"), ["Space"] = (32, "Space"),
        ["ArrowUp"] = (38, "ArrowUp"), ["ArrowDown"] = (40, "ArrowDown"),
        ["ArrowLeft"] = (37, "ArrowLeft"), ["ArrowRight"] = (39, "ArrowRight"),
        ["Home"] = (36, "Home"), ["End"] = (35, "End"), ["PageUp"] = (33, "PageUp"), ["PageDown"] = (34, "PageDown"),
    };

    private static string LoadPageJs()
    {
        var asm = typeof(BrowserSession).Assembly;
        using var s = asm.GetManifestResourceStream("PdaNetPortable.snapshot.js")
            ?? throw new InvalidOperationException("Embedded snapshot.js not found.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
