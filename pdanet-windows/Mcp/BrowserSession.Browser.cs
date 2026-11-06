using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using PdaNetPortable.Services;
using PdaNetPortable.Views;

namespace PdaNetPortable.Mcp;

// The tools for everything else the browser offers: tabs, find, view and window, print, downloads,
// web apps, history, settings, browsing data, the network route, and asking the person for help.
public sealed partial class BrowserSession
{
    // ============================ Tabs ============================
    public Task<ToolResult> TabsAsync(ToolCall call, string action, int? index, string? url, int? to) =>
        RunAsync(call, action == "list" ? "Looking at the tabs" : $"Tabs: {action.Replace('_', ' ')}", async () =>
        {
            var w = Win();
            BrowserTab At(int? i)
            {
                var tabs = w.AutoTabs;
                if (i is int n)
                {
                    if (n < 0 || n >= tabs.Count) throw new McpToolException($"There is no tab [{n}]; the tabs are [0] to [{tabs.Count - 1}].");
                    return tabs[n];
                }
                return AgentTab(w);
            }
            string line;
            bool outline = false;
            switch (action)
            {
                case "list":
                    line = "";
                    break;
                case "new":
                    var fresh = w.AutoNewTab();
                    _mine.Add(fresh.Id);
                    _agentTab = fresh;
                    line = "Opened a new tab.";
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        var target = BrowserStore.Instance.Resolve(url!, out var plain);
                        var p = await PageOfAsync(w, fresh, create: true, call.Ct) ?? throw new McpToolException("The browser cannot open websites right now.");
                        ulong since = p.NavId;
                        w.AutoGo(fresh, target, plain);
                        var note = await WaitLoadAsync(p, since, call, 30000);
                        line = $"Opened {Short(p.Core.Source, 120)} in a new tab." + (note != null ? " " + note : "");
                        outline = true;
                    }
                    break;
                case "select":
                    var pick = At(index ?? throw new McpToolException("select needs the index of the tab."));
                    w.AutoPick(pick);
                    _agentTab = pick;
                    line = $"Switched to tab [{IndexOf(pick)}].";
                    outline = true;
                    break;
                case "close":
                    var closing = At(index);
                    await AllowCloseAsync(call, new[] { closing });
                    line = $"Closed tab [{IndexOf(closing)}] {Short(closing.Title, 60)}.";
                    w.AutoClose(closing);
                    if (ReferenceEquals(closing, _agentTab)) _agentTab = null;
                    break;
                case "duplicate":
                    var copy = w.AutoDuplicate(At(index)) ?? throw new McpToolException("That tab has no website to copy.");
                    _mine.Add(copy.Id);
                    _agentTab = copy;
                    line = $"Duplicated the tab, the copy is [{IndexOf(copy)}].";
                    break;
                case "reopen":
                    var back = w.AutoReopen() ?? throw new McpToolException("No tab was closed in this browser window.");
                    _mine.Add(back.Id);
                    _agentTab = back;
                    line = $"Reopened {Short(back.Url, 100)} in tab [{IndexOf(back)}].";
                    outline = true;
                    break;
                case "close_others":
                    var keep = At(index);
                    await AllowCloseAsync(call, w.AutoTabs.Where(t => !ReferenceEquals(t, keep)).ToList());
                    w.AutoCloseOthers(keep);
                    line = "Closed every other tab.";
                    break;
                case "close_right":
                    var from = At(index);
                    int count = w.AutoTabs.Count - IndexOf(from) - 1;
                    await AllowCloseAsync(call, w.AutoTabs.Skip(IndexOf(from) + 1).ToList());
                    w.AutoCloseRight(from);
                    line = count == 1 ? "Closed the tab to the right." : $"Closed the {count} tabs to the right.";
                    break;
                case "move":
                    var moving = At(index);
                    w.AutoMove(moving, to ?? throw new McpToolException("move needs to, the new position."));
                    line = $"Moved the tab to position [{IndexOf(moving)}].";
                    break;
                case "mute":
                case "unmute":
                    var sound = At(index);
                    if (!w.AutoMute(sound, action == "mute")) throw new McpToolException("That tab has no page to mute.");
                    line = action == "mute" ? $"Muted tab [{IndexOf(sound)}]." : $"Turned the sound of tab [{IndexOf(sound)}] back on.";
                    break;
                case "restore":
                    int n2 = w.AutoRestore();
                    if (n2 == 0) throw new McpToolException("There are no tabs from last time to bring back.");
                    _agentTab = w.AutoActive;
                    line = n2 == 1 ? "Brought back the tab from last time." : $"Brought back the {n2} tabs from last time (each loads when you switch to it).";
                    outline = true;
                    break;
                default:
                    throw new McpToolException("action must be list, new, select, close, duplicate, reopen, close_others, close_right, move, mute, unmute or restore.");
            }
            if (action != "list") Logger.Instance.Info($"Agent {call.Agent.Name}: {line}");
            ResourceChanged?.Invoke("tabs");
            var (text, data) = TabList(w);
            var sb = new StringBuilder();
            if (line.Length > 0) sb.Append(line).Append("\n\n");
            sb.Append(text);
            if (outline && PageOf(w.AutoActive) is { } ap && ap.Dialog == null)
            {
                try { sb.Append("\n\n").Append(await OutlineAsync(call, ap, SnapMode.Auto)); }
                catch (McpToolException) { /* the tab may not have loaded yet */ }
            }
            return ToolResult.Ok(sb.ToString(), data);
        });

    private (string Text, JsonObject Data) TabList(BrowserWindow w)
    {
        var tabs = w.AutoTabs;
        var arr = new JsonArray();
        var sb = new StringBuilder(tabs.Count == 1 ? "1 tab:\n" : $"{tabs.Count} tabs:\n");
        for (int i = 0; i < tabs.Count; i++)
        {
            var t = tabs[i];
            bool active = ReferenceEquals(t, w.AutoActive);
            var url = t.IsStart ? "" : t.PendingUrl ?? t.Url;
            var title = t.IsStart ? "Start page" : string.IsNullOrWhiteSpace(t.Title) ? BrowserStore.HostOf(url) : t.Title;
            var app = BrowserStore.Instance.WebAppFor(url);
            sb.Append(active ? "* " : "  ").Append('[').Append(i).Append("] ").Append(Short(title, 80));
            if (url.Length > 0) sb.Append("  —  ").Append(Short(url, 100));
            var flags = new List<string>();
            if (t.Loading) flags.Add("loading");
            if (t.PendingUrl != null && t.View == null) flags.Add("not loaded yet");
            if (t.Audio && !t.Muted) flags.Add("playing sound");
            if (t.Muted) flags.Add("muted");
            if (app != null) flags.Add($"web app {app.Name}");
            if (t.Crashed) flags.Add("stopped working");
            if (PageOf(t)?.Dialog != null) flags.Add("dialog open");
            if (flags.Count > 0) sb.Append("  (").Append(string.Join(", ", flags)).Append(')');
            sb.Append('\n');
            arr.Add(new JsonObject
            {
                ["index"] = i, ["title"] = title, ["url"] = url, ["active"] = active, ["loading"] = t.Loading,
                ["audible"] = t.Audio && !t.Muted, ["muted"] = t.Muted, ["webApp"] = app?.Name,
            });
        }
        return (sb.ToString().TrimEnd(), new JsonObject { ["tabs"] = arr, ["activeIndex"] = IndexOf(w.AutoActive) });
    }

    // ============================ Find on page ============================
    public Task<ToolResult> FindAsync(ToolCall call, string? text, string action, bool matchCase, bool wholeWord) =>
        RunAsync(call, action == "close" ? "Closing find" : $"Finding \"{Short(text ?? "", 40)}\"", async () =>
        {
            var w = Win();
            await ActivePageAsync(call);
            (int Matches, int Active) r;
            switch (action)
            {
                case "find":
                    if (string.IsNullOrEmpty(text)) throw new McpToolException("Pass the text to find.");
                    r = await w.AutoFindAsync(text!, matchCase, wholeWord).WaitAsync(call.Ct);
                    break;
                case "next":
                case "previous":
                    r = await w.AutoFindStepAsync(action == "previous").WaitAsync(call.Ct);
                    break;
                case "close":
                    w.AutoFindClose();
                    return ToolResult.Ok("Closed find on page.", new JsonObject { ["matches"] = 0, ["active"] = 0 });
                default:
                    throw new McpToolException("action must be find, next, previous or close.");
            }
            var line = r.Matches == 0 ? "No match on the page."
                : $"{r.Matches} match{(r.Matches == 1 ? "" : "es")}, showing match {Math.Max(1, r.Active)} (highlighted and scrolled into view). Use action next or previous to move between them.";
            return ToolResult.Ok(line, new JsonObject { ["matches"] = r.Matches, ["active"] = Math.Max(0, r.Active) });
        });

    // ============================ View and window ============================
    public Task<ToolResult> WindowAsync(ToolCall call, string? action, bool? fullScreen, JsonNode? zoom, double? width, double? height, bool devTools) =>
        RunAsync(call, "Changing the view", async () =>
        {
            var w = Win();
            var done = new List<string>();
            if (action != null)
            {
                if (action is not ("focus" or "minimize" or "maximize" or "restore")) throw new McpToolException("action must be focus, minimize, maximize or restore.");
                w.AutoWindow(action);
                done.Add(action switch { "focus" => "Brought the browser to the front", "minimize" => "Minimized the browser", "maximize" => "Maximized the browser", _ => "Restored the browser window" });
            }
            if (fullScreen is bool fs)
            {
                w.AutoFullScreen = fs;
                done.Add(fs ? "Full screen is on (the person leaves it with F11 or Esc)" : "Left full screen");
            }
            if (zoom != null)
            {
                await ActivePageAsync(call);
                if (zoom is JsonValue zv && zv.TryGetValue<string>(out var word) && !double.TryParse(word, out _))
                {
                    switch (word.Trim().ToLowerInvariant())
                    {
                        case "in": w.AutoZoomStep(1); break;
                        case "out": w.AutoZoomStep(-1); break;
                        case "reset": w.AutoZoomStep(0); break;
                        default: throw new McpToolException("zoom is a factor such as 1.5, a percentage such as 150, or in, out or reset.");
                    }
                }
                else
                {
                    double z = zoom is JsonValue nv && nv.TryGetValue<double>(out var d) ? d
                        : double.Parse(zoom.ToString(), System.Globalization.CultureInfo.InvariantCulture);
                    if (z > 5) z /= 100; // 150 means 150%
                    w.AutoZoom = z;
                }
                done.Add($"Zoom is {w.AutoZoom * 100:0}%");
            }
            if (width is double vw && height is double vh)
            {
                w.AutoResizeViewport(Math.Clamp(vw, 240, 3840), Math.Clamp(vh, 200, 2400));
                await Task.Delay(150);
                done.Add("Resized the window");
            }
            else if (width != null || height != null) throw new McpToolException("Pass both width and height to resize the page area.");
            if (devTools)
            {
                await ActivePageAsync(call);
                w.AutoDevTools();
                done.Add("Opened the developer tools in a window of their own");
            }
            var (pw, ph) = w.AutoViewport;
            double zf = w.AutoZoom;
            var state = w.AutoFullScreen ? "full screen" : w.WindowState.ToString().ToLowerInvariant();
            var summary = $"Window: {state} · page area {pw / zf:0}×{ph / zf:0} CSS px · zoom {zf * 100:0}%";
            var text = (done.Count > 0 ? string.Join(". ", done) + ".\n" : "") + summary;
            return ToolResult.Ok(text, new JsonObject
            {
                ["state"] = state, ["fullScreen"] = w.AutoFullScreen, ["zoom"] = Math.Round(zf, 3),
                ["viewport"] = new JsonObject { ["width"] = Math.Round(pw / zf), ["height"] = Math.Round(ph / zf) },
            });
        });

    // ============================ Print ============================
    public Task<ToolResult> PrintAsync(ToolCall call, string? path, bool landscape, bool background, bool dialog, double scale) =>
        RunAsync(call, dialog ? "Opening the print dialog" : "Saving the page as a PDF", async () =>
        {
            var p = await ActivePageAsync(call);
            var w = Win();
            if (dialog)
            {
                w.AutoPrintDialog();
                return ToolResult.Ok("Opened the print dialog in the browser for the person to finish.", new JsonObject { ["dialog"] = true });
            }
            var target = PdfPath(p, path);
            var settings = p.Core.Environment.CreatePrintSettings();
            settings.Orientation = landscape ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
            settings.ShouldPrintBackgrounds = background;
            if (scale > 0) settings.ScaleFactor = Math.Clamp(scale, 0.1, 2);
            call.Progress("Writing the PDF");
            var ok = await p.Core.PrintToPdfAsync(target, settings).WaitAsync(call.Ct);
            if (!ok || !File.Exists(target)) throw new McpToolException("The PDF could not be written (another print may still be running).");
            var size = new FileInfo(target).Length;
            Logger.Instance.Info($"Agent {call.Agent.Name} saved {BrowserStore.HostOf(p.Core.Source)} as a PDF");
            return ToolResult.Ok($"Saved the page as a PDF: {target} ({size / 1024:N0} KB).",
                new JsonObject { ["path"] = target, ["bytes"] = size, ["dialog"] = false });
        });

    private static string PdfPath(Page p, string? wanted)
    {
        string folder;
        try { folder = p.Core.Profile.DefaultDownloadFolderPath; }
        catch { folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); }
        var title = string.IsNullOrWhiteSpace(p.Core.DocumentTitle) ? BrowserStore.HostOf(p.Core.Source) : p.Core.DocumentTitle;
        var name = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c).ToArray()).Trim();
        if (name.Length == 0) name = "page";
        if (name.Length > 80) name = name[..80].Trim();
        string path;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var w = Environment.ExpandEnvironmentVariables(wanted.Trim().Trim('"'));
            path = Path.IsPathFullyQualified(w) ? w : Path.Combine(folder, w);
            if (Directory.Exists(path)) path = Path.Combine(path, name + ".pdf");
            if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) path += ".pdf";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return Unused(path);
        }
        Directory.CreateDirectory(folder);
        return Unused(Path.Combine(folder, name + ".pdf"));
    }

    /// <summary>The path, or "name (2).pdf" and so on when a file is there already: a PDF never replaces a file.</summary>
    private static string Unused(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stem} ({i}){ext}");
        return path;
    }

    // ============================ Downloads ============================
    public Task<ToolResult> DownloadsAsync(ToolCall call, string action, int? id, double timeoutSeconds) =>
        RunAsync(call, action == "wait" ? "Waiting for a download" : "Looking at the downloads", async () =>
        {
            var w = Win();
            DownloadItem Pick()
            {
                var d = id is int n ? _downloads.FirstOrDefault(x => x.Id == n) : _downloads.LastOrDefault();
                return d ?? throw new McpToolException(id is int m ? $"There is no download {m}." : "No download started while an agent was connected.");
            }
            string line = "";
            switch (action)
            {
                case "list":
                    break;
                case "wait":
                    var d = Pick();
                    var until = DateTime.Now.AddSeconds(Math.Clamp(timeoutSeconds <= 0 ? 120 : timeoutSeconds, 1, 1800));
                    while (!d.Finished.Task.IsCompleted && DateTime.Now < until)
                    {
                        d.Update();
                        call.Progress($"{Path.GetFileName(d.Path)}: {Size(d.Bytes)}{(d.Total > 0 ? " of " + Size(d.Total) : "")}", d.Bytes, d.Total > 0 ? d.Total : null);
                        await Task.WhenAny(d.Finished.Task, Task.Delay(1000, call.Ct));
                        call.Ct.ThrowIfCancellationRequested();
                    }
                    d.Update();
                    line = d.State switch
                    {
                        "complete" => $"Download {d.Id} finished: {d.Path} ({Size(d.Bytes)}).",
                        "downloading" => $"Download {d.Id} is still running ({Size(d.Bytes)} so far).",
                        _ => $"Download {d.Id} stopped: {d.State}{(d.Reason != null ? " (" + d.Reason + ")" : "")}.",
                    };
                    break;
                case "cancel":
                    var c = Pick();
                    c.Op.Cancel();
                    line = $"Cancelled download {c.Id}.";
                    break;
                case "pause":
                    var pa = Pick();
                    pa.Op.Pause();
                    line = $"Paused download {pa.Id}.";
                    break;
                case "resume":
                    var re = Pick();
                    if (!re.Op.CanResume) throw new McpToolException($"Download {re.Id} cannot be resumed.");
                    re.Op.Resume();
                    line = $"Resumed download {re.Id}.";
                    break;
                case "show":
                case "hide":
                    if (!w.AutoDownloadsPanel(action == "show")) throw new McpToolException("Open a website first, the downloads panel belongs to a page.");
                    line = action == "show" ? "Opened the downloads panel." : "Closed the downloads panel.";
                    break;
                default:
                    throw new McpToolException("action must be list, wait, cancel, pause, resume, show or hide.");
            }
            var (text, data) = DownloadList();
            return ToolResult.Ok((line.Length > 0 ? line + "\n\n" : "") + text, data);
        });

    private (string Text, JsonObject Data) DownloadList()
    {
        var arr = new JsonArray();
        var sb = new StringBuilder();
        foreach (var d in _downloads.AsEnumerable().Reverse())
        {
            d.Update();
            sb.Append($"[{d.Id}] {d.State,-11} {Path.GetFileName(d.Path)}  {Size(d.Bytes)}{(d.Total > 0 ? " of " + Size(d.Total) : "")}  —  {d.Path}\n");
            arr.Add(new JsonObject
            {
                ["id"] = d.Id, ["state"] = d.State, ["file"] = Path.GetFileName(d.Path), ["path"] = d.Path, ["url"] = d.Url,
                ["bytes"] = d.Bytes, ["totalBytes"] = d.Total, ["reason"] = d.Reason,
            });
        }
        var text = arr.Count == 0 ? "No downloads yet (downloads count from when an agent connected)." : "Downloads, newest first:\n" + sb.ToString().TrimEnd();
        return (text, new JsonObject { ["downloads"] = arr });
    }

    private static string Size(long b) => b <= 0 ? "0 B" : PdaNetPortable.Core.Fmt.Bytes(b);

    // ============================ Web apps ============================
    public Task<ToolResult> WebAppsAsync(ToolCall call, string action, string? name, string? url, string? newName) =>
        RunAsync(call, action == "list" ? "Looking at the web apps" : $"Web apps: {action}", async () =>
        {
            var store = BrowserStore.Instance;
            var w = Win();
            WebApp Find()
            {
                var key = name ?? url ?? throw new McpToolException("Pass the name (or url) of the web app.");
                return store.State.WebApps.FirstOrDefault(a => a.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                       ?? store.WebAppFor(BrowserStore.AddressOf(key) ?? key)
                       ?? throw new McpToolException($"There is no web app called \"{key}\". The web apps are: {string.Join(", ", store.State.WebApps.Select(a => a.Name))}.");
            }
            string line = "";
            bool outline = false;
            switch (action)
            {
                case "list":
                    break;
                case "add":
                    var address = url != null ? BrowserStore.AddressOf(url) : w.AutoActive?.View?.CoreWebView2?.Source;
                    if (address == null || !BrowserStore.IsWeb(address))
                        throw new McpToolException(url != null ? $"\"{url}\" is not a web address." : "Open the website first, or pass its url.");
                    if (store.WebAppFor(address) is { } existing)
                    {
                        line = $"{address} is already the web app \"{existing.Name}\".";
                        break;
                    }
                    var title = w.AutoActive?.View?.CoreWebView2?.Source == address ? w.AutoActive?.View?.CoreWebView2?.DocumentTitle : null;
                    var app = store.AddWebApp(string.IsNullOrWhiteSpace(name) ? BrowserStore.NameFor(address, title) : name!, address);
                    w.AutoNotice($"{app.Name} is on your web apps");
                    line = $"Pinned \"{app.Name}\" ({BrowserStore.Display(app.Url)}) to the web apps. It shows on every new tab and on the Launchpad.";
                    break;
                case "rename":
                    var r = Find();
                    if (string.IsNullOrWhiteSpace(newName)) throw new McpToolException("Pass newName.");
                    var old = r.Name;
                    store.UpdateWebApp(r, newName!, r.Url);
                    line = $"Renamed the web app \"{old}\" to \"{r.Name}\".";
                    break;
                case "remove":
                    var gone = Find();
                    store.RemoveWebApp(gone);
                    w.AutoNotice($"{gone.Name} removed from your web apps");
                    line = $"Removed the web app \"{gone.Name}\".";
                    break;
                case "open":
                    var open = Find();
                    w.Open(open.Url, reuse: true);
                    _agentTab = w.AutoActive;
                    if (_agentTab != null && _agentTab.View == null) _mine.Add(_agentTab.Id);
                    var p = await PageOfAsync(w, w.AutoActive!, create: true, call.Ct);
                    if (p != null && p.Tab.Loading) await WaitLoadAsync(p, 0, call, 30000);
                    line = $"Opened the web app \"{open.Name}\" in tab [{IndexOf(w.AutoActive)}].";
                    outline = true;
                    break;
                default:
                    throw new McpToolException("action must be list, add, rename, remove or open.");
            }
            if (action != "list") Logger.Instance.Info($"Agent {call.Agent.Name}: {line}");
            if (action is "add" or "rename" or "remove") ResourceChanged?.Invoke("web-apps");
            var openUrls = w.AutoTabs.Select(t => t.PendingUrl ?? t.Url).ToList();
            var arr = new JsonArray();
            var sb = new StringBuilder(store.State.WebApps.Count == 0 ? "No web apps are pinned yet." : "Web apps:\n");
            foreach (var a in store.State.WebApps)
            {
                bool isOpen = openUrls.Any(u => ReferenceEquals(store.WebAppFor(u), a));
                sb.Append($"- {a.Name}  —  {BrowserStore.Display(a.Url)}{(isOpen ? "  (open)" : "")}\n");
                arr.Add(new JsonObject { ["name"] = a.Name, ["url"] = a.Url, ["open"] = isOpen });
            }
            var text = (line.Length > 0 ? line + "\n\n" : "") + sb.ToString().TrimEnd();
            if (outline && PageOf(w.AutoActive) is { Dialog: null } ap)
            {
                try { text += "\n\n" + await OutlineAsync(call, ap, SnapMode.Full); } catch (McpToolException) { }
            }
            return ToolResult.Ok(text, new JsonObject { ["webApps"] = arr });
        });

    // ============================ History ============================
    public Task<ToolResult> HistoryAsync(ToolCall call, string action, string? query, string? host, int limit) =>
        RunAsync(call, action is "list" or "search" ? "Looking at the history" : $"History: {action}", async () =>
        {
            var store = BrowserStore.Instance;
            string line = "";
            switch (action)
            {
                case "list":
                case "search":
                    break;
                case "forget":
                    var h = BrowserStore.HostOf(BrowserStore.AddressOf(host ?? query ?? "") ?? host ?? "");
                    if (h.Length == 0) h = (host ?? query ?? "").Trim();
                    if (h.Length == 0) throw new McpToolException("Pass the website (host) to forget, such as example.com.");
                    // Deleting history cannot be undone, so the person says yes first, as for clearing data.
                    if (!await Presence.ApproveAsync(call, $"{call.Agent.Name} wants to remove {h} from the history", "The pages of that website go from the list of recent websites"))
                        return ToolResult.Fail("The person did not approve removing that website from the history. Nothing was removed.");
                    int before = store.State.Recent.Count;
                    store.Forget(h);
                    line = $"Forgot {before - store.State.Recent.Count} page(s) of {h}.";
                    break;
                case "clear":
                    if (!await Presence.ApproveAsync(call, $"{call.Agent.Name} wants to clear the history", "The whole list of recent websites goes. Sign ins and website data stay"))
                        return ToolResult.Fail("The person did not approve clearing the history. Nothing was cleared.");
                    int all = store.State.Recent.Count;
                    store.ClearRecent();
                    line = $"Cleared the list of recent websites ({all} pages). Sign ins and website data stay; use browser_clear_data for those.";
                    break;
                default:
                    throw new McpToolException("action must be list, search, forget or clear.");
            }
            if (action is "forget" or "clear") { Logger.Instance.Info($"Agent {call.Agent.Name}: {line}"); ResourceChanged?.Invoke("history"); }
            IEnumerable<RecentSite> pages = store.State.Recent;
            if (!string.IsNullOrWhiteSpace(query) && action != "forget")
                pages = pages.Where(r => r.Title.Contains(query!, StringComparison.OrdinalIgnoreCase) || r.Url.Contains(query!, StringComparison.OrdinalIgnoreCase));
            var list = pages.Take(Math.Clamp(limit <= 0 ? 25 : limit, 1, 300)).ToList();
            var arr = new JsonArray();
            var sb = new StringBuilder(list.Count == 0 ? "No pages in the history" + (query != null ? $" match \"{query}\"." : ".") : "Recent pages, newest first:\n");
            foreach (var r in list)
            {
                sb.Append($"- {(string.IsNullOrWhiteSpace(r.Title) ? BrowserStore.HostOf(r.Url) : Short(r.Title, 80))}  —  {Short(r.Url, 110)}  ({r.Visits} visit{(r.Visits == 1 ? "" : "s")}, {r.Last:yyyy-MM-dd HH:mm})\n");
                arr.Add(new JsonObject { ["title"] = r.Title, ["url"] = r.Url, ["visits"] = r.Visits, ["lastVisit"] = r.Last.ToString("o") });
            }
            var text = (line.Length > 0 ? line + "\n\n" : "") + sb.ToString().TrimEnd();
            return ToolResult.Ok(text, new JsonObject { ["pages"] = arr });
        });

    // ============================ Settings ============================
    public Task<ToolResult> SettingsAsync(ToolCall call, string? searchEngine, string? colors, bool? openSitesWithWarnings) =>
        RunAsync(call, "Browser settings", async () =>
        {
            var store = BrowserStore.Instance;
            var w = Win();
            var done = new List<string>();
            // Turning a safety check off is the person's call.
            if (openSitesWithWarnings == true && !store.State.OpenUnsafeSites &&
                !await Presence.ApproveAsync(call, $"{call.Agent.Name} wants to open websites that have a safety warning",
                    "PdaNet then opens pages that Microsoft Defender SmartScreen warns about, instead of blocking them"))
                return ToolResult.Fail("The person did not approve opening websites that have a safety warning. No setting changed.");
            if (searchEngine != null)
            {
                var e = BrowserStore.Engines.FirstOrDefault(x => x.Key.Equals(searchEngine, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(searchEngine, StringComparison.OrdinalIgnoreCase))
                        ?? throw new McpToolException($"The search engines are {string.Join(", ", BrowserStore.Engines.Select(x => x.Key))}.");
                w.AutoSearchEngine(e.Key);
                done.Add($"Searches now use {e.Name}");
            }
            if (colors != null)
            {
                var c = colors.ToLowerInvariant() switch { "auto" or "windows" or "system" => "auto", "dark" => "dark", "light" => "light", _ => throw new McpToolException("websiteColors is auto, dark or light.") };
                w.AutoColors(c);
                done.Add(c == "auto" ? "Websites follow the Windows colors" : $"Websites show their {c} colors");
            }
            if (openSitesWithWarnings is bool on)
            {
                w.AutoOpenUnsafe(on);
                done.Add(on ? "Websites with a safety warning open anyway (the page reloads)" : "Websites with a safety warning stay blocked (the page reloads)");
            }
            if (done.Count > 0) Logger.Instance.Info($"Agent {call.Agent.Name} changed browser settings: {string.Join(", ", done)}");
            var s = store.State;
            var text = (done.Count > 0 ? string.Join(". ", done) + ".\n\n" : "") +
                       $"Search engine: {store.Engine.Name} ({s.Search}) · Website colors: {s.Colors} · Open sites with warnings: {(s.OpenUnsafeSites ? "on" : "off")}\n" +
                       $"Search engines: {string.Join(", ", BrowserStore.Engines.Select(e => e.Key))}";
            var engines = new JsonArray();
            foreach (var e in BrowserStore.Engines) engines.Add(e.Key);
            return ToolResult.Ok(text, new JsonObject
            {
                ["searchEngine"] = s.Search, ["websiteColors"] = s.Colors, ["openSitesWithWarnings"] = s.OpenUnsafeSites, ["searchEngines"] = engines,
            });
        });

    // ============================ Browsing data ============================
    public Task<ToolResult> ClearDataAsync(ToolCall call) =>
        RunAsync(call, "Asking to clear the browsing data", async () =>
        {
            var w = Win();
            if (!Presence.CanAsk())
                throw new McpToolException("Clearing the browsing data needs the person's approval, and nobody can see this browser (it runs hidden).");
            w.AutoWindow("focus");
            bool ok = await Presence.ApproveAsync(call, $"{call.Agent.Name} wants to clear the browsing data",
                "History, sign ins, cookies and website data of the PdaNet browser go, so you are signed out of websites");
            if (!ok) return ToolResult.Fail("The person did not approve clearing the browsing data. Nothing was cleared.");
            Presence.Act(call.Agent, "Clearing the browsing data");
            if (!await w.AutoClearDataAsync().WaitAsync(call.Ct)) throw new McpToolException("The browsing data could not be cleared.");
            ResourceChanged?.Invoke("history");
            Logger.Instance.Info($"Agent {call.Agent.Name} cleared the browsing data, approved by the person");
            return ToolResult.Ok("Cleared the browsing data: history, sign ins, cookies and website data.");
        });

    // ============================ The person ============================
    public Task<ToolResult> AskHumanAsync(ToolCall call, string message, bool wantsReply, double timeoutSeconds) =>
        RunAsync(call, "Waiting for the person", async () =>
        {
            if (!Presence.CanAsk())
                throw new McpToolException("Nobody can see this browser (it runs hidden), so nobody can help. Run PdaNet without --hidden, or open the PdaNet app.");
            var w = Win();
            w.AutoWindow("focus");
            Logger.Instance.Info($"Agent {call.Agent.Name} asks the person: {Short(message, 120)}");
            var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds <= 0 ? 300 : timeoutSeconds, 10, 1800));
            var (choice, reply) = await Presence.AskAsync(call, message, wantsReply, timeout);
            var said = string.IsNullOrWhiteSpace(reply) ? "" : $" They wrote: \"{reply!.Trim()}\"";
            string line = choice switch
            {
                PromptChoice.Done => "The person is done." + said,
                PromptChoice.Cant => "The person says they cannot do it." + said,
                PromptChoice.TimedOut => $"Nobody answered within {(int)timeout.TotalSeconds} s.",
                _ => "The question was withdrawn.",
            };
            var sb = new StringBuilder(line);
            if (choice == PromptChoice.Done)
            {
                // The outline is of the agent's tab, where its next action works, wherever the person ended up.
                var mine = AgentTab(w);
                if (w.AutoActive is { } on && !ReferenceEquals(on, mine))
                    sb.Append($"\nThe person is on tab [{IndexOf(on)}] {Short(on.Title, 60)} now. Your tab is [{IndexOf(mine)}]; your next action brings it back to the front.");
                if (PageOf(mine) is { Dialog: null } p)
                {
                    try { sb.Append("\n\n").Append(await OutlineAsync(call, p, SnapMode.Full)); } catch (McpToolException) { }
                }
            }
            var data = new JsonObject { ["answer"] = choice.ToString().ToLowerInvariant(), ["reply"] = reply };
            return choice == PromptChoice.Done || choice == PromptChoice.Cant ? ToolResult.Ok(sb.ToString(), data) : ToolResult.Fail(sb.ToString());
        });

    // ============================ Console and network ============================
    public Task<ToolResult> ConsoleAsync(ToolCall call, string level, int limit, bool clear) =>
        RunTextAsync(call, "Reading the console", async () =>
        {
            var p = await ActivePageAsync(call, allowDialog: true, front: false);
            IEnumerable<string> items = p.Console;
            if (level == "error") items = items.Where(l => l.StartsWith("[error]") || l.StartsWith("[uncaught]") || l.StartsWith("[assert]"));
            else if (level == "warning") items = items.Where(l => l.StartsWith("[error]") || l.StartsWith("[uncaught]") || l.StartsWith("[warning]") || l.StartsWith("[warn]") || l.StartsWith("[assert]"));
            var list = items.TakeLast(Math.Clamp(limit <= 0 ? 50 : limit, 1, 200)).ToList();
            if (clear) p.Console.Clear();
            return list.Count == 0
                ? "No console messages" + (level != "all" ? $" at level {level}" : "") + " since the agent started watching this page."
                : string.Join("\n", list);
        });

    public Task<ToolResult> NetworkAsync(ToolCall call, int limit, string? filter, bool failedOnly) =>
        RunAsync(call, "Looking at the network", async () =>
        {
            var w = Win();
            var (route, through) = RouteText(w);
            var sb = new StringBuilder(route).Append("\n\n");
            var arr = new JsonArray();
            var p = PageOf(AgentTab(w));
            if (p == null) sb.Append("No website is open in the agent's tab.");
            else
            {
                IEnumerable<NetEntry> items = p.Net;
                if (!string.IsNullOrWhiteSpace(filter)) items = items.Where(e => e.Url.Contains(filter!, StringComparison.OrdinalIgnoreCase));
                if (failedOnly) items = items.Where(e => e.Failed != null || e.Status >= 400);
                var list = items.TakeLast(Math.Clamp(limit <= 0 ? 30 : limit, 1, 300)).ToList();
                if (list.Count == 0) sb.Append("No matching requests since the agent started watching this page. Reload it (browser_reload) to see all of its requests.");
                else sb.Append("Requests, oldest first:\n");
                foreach (var e in list)
                {
                    var status = e.Failed != null ? "FAIL" : e.Done || e.Status > 0 ? e.Status.ToString() : "…";
                    sb.Append($"{status,-4} {e.Method,-6} {e.Type,-10} {Short(e.Url, 110)}{(e.Size > 0 ? "  " + Size(e.Size) : "")}{(e.Failed != null ? "  (" + e.Failed + ")" : "")}\n");
                    arr.Add(new JsonObject
                    {
                        ["method"] = e.Method, ["url"] = e.Url, ["type"] = e.Type, ["status"] = e.Status, ["bytes"] = e.Size,
                        ["failed"] = e.Failed, ["done"] = e.Done,
                    });
                }
            }
            await Task.CompletedTask;
            return ToolResult.Ok(sb.ToString().TrimEnd(), new JsonObject
            {
                ["route"] = new JsonObject { ["text"] = route, ["throughPdaNet"] = through }, ["requests"] = arr,
            });
        });

    /// <summary>
    /// How the browser goes online. In the PdaNet app the window knows: PdaNet's own records show the
    /// browser's connections. On its own (--mcp without the app) it reads the Windows proxy PdaNet sets.
    /// </summary>
    private static (string Text, bool Through) RouteText(BrowserWindow w)
    {
        if (w.AutoInApp)
        {
            var r = w.AutoRoute;
            bool through = r.Key == "B.Accent";
            var data = w.AutoBytes;
            return ($"Route: {r.Title}. {r.Body}." + (data > 0 ? $" The browser has used {Size(data)} through PdaNet since it opened." : ""), through);
        }
        try
        {
            var cfg = ConfigManager.Instance.Config;
            var (enabled, server) = SystemProxy.GetWinInet();
            string local = cfg.LocalProxyPort.ToString();
            if (enabled && server != null && (server.Contains("127.0.0.1:" + local) || server.Contains("localhost:" + local)))
                return ($"Route: through PdaNet ({server}), so the browser goes online through the phone's connection.", true);
            if (enabled && !string.IsNullOrWhiteSpace(server))
                return ($"Route: through a Windows proxy ({server}) that is not PdaNet's. Connect PdaNet to go online through the phone.", false);
            return ("Route: this PC's own connection, PdaNet is not connected. Connect PdaNet to go online through the phone.", false);
        }
        catch { return ("Route: could not be told.", false); }
    }

    // ============================ Resources ============================
    /// <summary>Reads a browser:// resource for an agent: tabs, the text or outline of a tab, web apps, history, downloads, status.</summary>
    public Task<(string Mime, string Text)> ReadResourceAsync(ToolCall call, string uri) =>
        RunResourceAsync(call, uri);

    private async Task<(string, string)> RunResourceAsync(ToolCall call, string uri)
    {
        var r = await RunAsync(call, "Reading " + uri, async () =>
        {
            var w = Win();
            var u = uri.Trim();
            switch (u)
            {
                case "browser://tabs": return Json(TabList(w).Data);
                case "browser://web-apps": return Json(WebAppsBody());
                case "browser://history": return Json(HistoryBody());
                case "browser://downloads": return Json(DownloadList().Data);
                case "browser://status":
                    var (route, through) = RouteText(w);
                    return Json(new JsonObject { ["route"] = route, ["throughPdaNet"] = through, ["tabs"] = w.AutoTabs.Count, ["activeIndex"] = IndexOf(w.AutoActive) });
            }
            int? index = null;
            string kind;
            if (u.StartsWith("browser://active/")) kind = u["browser://active/".Length..];
            else if (u.StartsWith("browser://tab/"))
            {
                var rest = u["browser://tab/".Length..].Split('/');
                if (rest.Length != 2 || !int.TryParse(rest[0], out var i)) throw new McpToolException("Unknown resource " + uri);
                index = i;
                kind = rest[1];
            }
            else throw new McpToolException("Unknown resource " + uri);
            var p = index is int ix ? await TabPageAsync(ix, call.Ct)
                : await PageOfAsync(w, w.AutoActive ?? AgentTab(w), create: false, call.Ct)
                  ?? throw new McpToolException("The tab in front shows the start page, no website is open in it.");
            if (kind == "snapshot") return ToolResult.Ok(await OutlineAsync(call, p, SnapMode.Full));
            if (kind == "text")
            {
                var rd = await Js(p, "read", new JsonObject { ["format"] = "markdown" }, call.Ct);
                var text = Str(rd, "text");
                if (text.Length > 100_000) text = text[..100_000] + "\n… (cut)";
                return ToolResult.Ok($"# {Str(rd, "title")}\n{Str(rd, "url")}\n\n{text}");
            }
            throw new McpToolException("Unknown resource " + uri);
        });
        var body = r.Content.Count > 0 ? r.Content[0].ToJson()["text"]?.GetValue<string>() ?? "" : "";
        bool json = body.StartsWith("{");
        return (json ? "application/json" : uri.EndsWith("/text") ? "text/markdown" : "text/plain", body);

        static ToolResult Json(JsonObject o) => ToolResult.Ok(o.ToJsonString());
    }

    private static JsonObject WebAppsBody()
    {
        var arr = new JsonArray();
        foreach (var a in BrowserStore.Instance.State.WebApps) arr.Add(new JsonObject { ["name"] = a.Name, ["url"] = a.Url });
        return new JsonObject { ["webApps"] = arr };
    }

    private static JsonObject HistoryBody()
    {
        var arr = new JsonArray();
        foreach (var r in BrowserStore.Instance.State.Recent.Take(100))
            arr.Add(new JsonObject { ["title"] = r.Title, ["url"] = r.Url, ["visits"] = r.Visits, ["lastVisit"] = r.Last.ToString("o") });
        return new JsonObject { ["pages"] = arr };
    }
}
