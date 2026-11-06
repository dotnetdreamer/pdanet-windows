using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using PdaNetPortable.Services;
using PdaNetPortable.Views;

namespace PdaNetPortable.Mcp;

/// <summary>
/// The built in end to end check of the MCP server ("--mcp-smoke", "--show" to watch it). It needs
/// no network: test pages are served from a temp folder at https://pdanet.test/. It talks real MCP
/// to the server (both protocol eras), drives every kind of tool against the real browser, checks
/// the person's side (approvals, questions, pause, stop, cancellation), the HTTP transport's
/// safety checks, and the pipe with a real "--mcp" relay process.
///
/// History and web apps stay in memory and the download folder is put back, so running it never
/// changes the person's browser data. Exit code 0 when every check passes.
/// </summary>
internal static class McpSmoke
{
    public static int Run(bool visible)
    {
        Console.Error.WriteLine("==== PdaNet Browser MCP smoke test ====");
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        BrowserStore.Instance.Sandbox();

        var dir = Path.Combine(Path.GetTempPath(), "pdanet-mcp-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        Pages.Write(dir);
        BrowserWindow.AutoTestHost = ("pdanet.test", dir);
        var (server, _, win) = McpHost.Build(Dispatcher.CurrentDispatcher, visible, ownBrowser: false);
        var presence = AgentPresence.Current!;
        presence.CanAsk = () => true; // the checks answer the questions themselves

        int exit = 2;
        _ = Task.Run(async () =>
        {
            var run = new Runner(server, presence, win, dir, app.Dispatcher);
            try { exit = await run.RunAsync(); }
            catch (Exception e)
            {
                Console.Error.WriteLine("FAIL: the check crashed: " + e);
                exit = 2;
            }
            finally
            {
                await run.RestoreAsync();
                _ = app.Dispatcher.BeginInvoke(() =>
                {
                    try { win.Close(); } catch { }
                    try { app.Shutdown(); } catch { }
                });
            }
        });
        app.Run();
        BrowserWindow.AutoTestHost = null;
        try { Directory.Delete(dir, recursive: true); } catch { }
        return exit;
    }

    private sealed class Runner
    {
        private readonly McpServer _server;
        private readonly AgentPresence _presence;
        private readonly BrowserWindow _win;
        private readonly string _dir;
        private readonly Dispatcher _ui;
        private readonly McpConnection _conn;
        private readonly List<JsonObject> _notes = new();
        private int _id, _ok, _failed;
        private string? _downloadsBefore;

        public Runner(McpServer server, AgentPresence presence, BrowserWindow win, string dir, Dispatcher ui)
        {
            _server = server;
            _presence = presence;
            _win = win;
            _dir = dir;
            _ui = ui;
            _conn = new McpConnection("stdio", m => { lock (_notes) _notes.Add(m); });
            _server.Open(_conn);
        }

        public async Task<int> RunAsync()
        {
            await ProtocolAsync();
            await PagesAsync();
            await PeopleAsync();
            await HttpAsync();
            await PipeAsync();
            Console.Error.WriteLine($"==== {_ok} passed, {_failed} failed ====");
            Console.Error.WriteLine(_failed == 0 ? "==== MCP SMOKE PASS ====" : "==== MCP SMOKE FAIL ====");
            return _failed == 0 ? 0 : 2;
        }

        public async Task RestoreAsync()
        {
            if (_downloadsBefore == null) return;
            try
            {
                await _ui.InvokeAsync(() =>
                {
                    var core = _win.AutoTabs.Select(t => t.View?.CoreWebView2).FirstOrDefault(c => c != null);
                    if (core != null) core.Profile.DefaultDownloadFolderPath = _downloadsBefore;
                });
            }
            catch { }
        }

        // ============================ Protocol ============================
        private async Task ProtocolAsync()
        {
            Section("Protocol");
            var init = await Rpc("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "mcp-smoke", ["version"] = "1" },
            });
            Check(init?["result"]?["protocolVersion"]?.GetValue<string>() == "2025-06-18", "initialize echoes a known version");
            Check(init?["result"]?["capabilities"]?["tools"] != null && init?["result"]?["capabilities"]?["resources"]?["subscribe"]?.GetValue<bool>() == true,
                "initialize offers tools and resource subscriptions");

            var discover = await Rpc("server/discover", meta: Modern());
            var versions = discover?["result"]?["supportedVersions"] as JsonArray;
            Check(versions?.Any(v => v?.GetValue<string>() == "2026-07-28") == true, "server/discover lists 2026-07-28");
            Check(discover?["result"]?["_meta"]?["io.modelcontextprotocol/serverInfo"]?["name"]?.GetValue<string>() == "pdanet-browser", "server/discover names the server");

            var noCaps = await Rpc("tools/list", meta: new JsonObject { ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28" });
            Check(noCaps?["error"]?["code"]?.GetValue<int>() == -32602, "a 2026 request without clientCapabilities is refused (-32602)");
            var bad = await Rpc("tools/list", meta: new JsonObject { ["io.modelcontextprotocol/protocolVersion"] = "1999-01-01" });
            Check(bad?["error"]?["code"]?.GetValue<int>() == -32022 && bad?["error"]?["data"]?["supported"] is JsonArray,
                "an unknown protocol version gets -32022 with the supported list");

            var modernList = await Rpc("tools/list", meta: Modern());
            Check(modernList?["result"]?["resultType"]?.GetValue<string>() == "complete", "2026 results carry resultType");
            Check(modernList?["result"]?["ttlMs"] != null && modernList?["result"]?["cacheScope"]?.GetValue<string>() == "public", "2026 lists carry a caching hint");
            var modernRead = await Rpc("resources/read", new JsonObject { ["uri"] = "browser://tabs" }, Modern());
            Check(modernRead?["result"]?["ttlMs"]?.GetValue<int>() == 0 && modernRead?["result"]?["cacheScope"]?.GetValue<string>() == "private",
                "a 2026 read is never cached and stays private", modernRead?.ToJsonString());
            var noneModern = await Rpc("resources/read", new JsonObject { ["uri"] = "browser://nothing-here" }, Modern());
            Check(noneModern?["error"]?["code"]?.GetValue<int>() == -32602, "a 2026 read of a missing resource is -32602", noneModern?.ToJsonString());
            var noneLegacy = await Rpc("resources/read", new JsonObject { ["uri"] = "browser://nothing-here" });
            Check(noneLegacy?["error"]?["code"]?.GetValue<int>() == -32002, "an older client still gets -32002 for it", noneLegacy?.ToJsonString());
            var list = await Rpc("tools/list");
            var tools = list?["result"]?["tools"] as JsonArray;
            Check(tools?.Count == 32, $"tools/list has every tool ({tools?.Count})");
            Check(tools?.All(t => t?["inputSchema"]?["type"]?.GetValue<string>() == "object" && t["annotations"] != null) == true, "every tool has a schema and annotations");
            var unknown = await Rpc("tools/call", new JsonObject { ["name"] = "browser_fly" });
            Check(unknown?["error"]?["code"]?.GetValue<int>() == -32602, "an unknown tool is a protocol error");
            var nomethod = await Rpc("browser/fly");
            Check(nomethod?["error"]?["code"]?.GetValue<int>() == -32601, "an unknown method gets -32601");
            var ping = await Rpc("ping");
            Check(ping?["result"] is JsonObject, "ping answers");

            var res = await Rpc("resources/list");
            Check((res?["result"]?["resources"] as JsonArray)?.Any(r => r?["uri"]?.GetValue<string>() == "browser://tabs") == true, "resources/list has browser://tabs");
            var tpl = await Rpc("resources/templates/list");
            Check((tpl?["result"]?["resourceTemplates"] as JsonArray)?.Count == 2, "two resource templates");
            var prompt = await Rpc("prompts/get", new JsonObject { ["name"] = "research", ["arguments"] = new JsonObject { ["topic"] = "tides" } });
            Check(prompt?["result"]?["messages"]?[0]?["content"]?["text"]?.GetValue<string>().Contains("tides") == true, "prompts/get renders a prompt");
            var level = await Rpc("logging/setLevel", new JsonObject { ["level"] = "info" });
            Check(level?["result"] is JsonObject, "logging/setLevel");
        }

        // ============================ The browser ============================
        private async Task PagesAsync()
        {
            Section("Browsing");
            var nav = await Call("browser_navigate", new() { ["url"] = "https://pdanet.test/index.html" });
            Check(!nav.Error && nav.Text.Contains("Page: Test page") && nav.Text.Contains("textbox \"Email\""), "navigate returns the outline", nav.Text);
            await SetDownloadFolderAsync();

            var o = nav.Text;
            Check(Regex.IsMatch(o, @"button ""Frame button"".*\[ref=e\d+\]"), "the outline sees into a same-origin iframe", o);
            Check(Regex.IsMatch(o, @"button ""Shadow button"".*\[ref=e\d+\]"), "the outline sees into an open shadow root");
            Check(o.Contains("[ref=e") && Regex.IsMatch(o, @"combobox ""Country"" value=""Germany"" options=\[Germany\|France\|Spain\]"), "dropdowns show their value and options");
            Check(Regex.IsMatch(o, @"clickable ""Pointer card"""), "a div with a pointer cursor is clickable, with its name");
            Check(o.Contains("text \"A needle in text."), "text reads as text");
            Check(!o.Contains("Hidden menu entry") && !o.Contains("Folded label"), "text of hidden elements stays out of the outline");

            var again = await Call("browser_snapshot");
            Check(Ref(again.Text, "textbox \"Email\"") == Ref(o, "textbox \"Email\""), "a handle stays the same across snapshots");
            var diff = await Call("browser_snapshot", new() { ["changesOnly"] = true });
            Check(diff.Text.Contains("nothing on the page changed"), "changesOnly reports no change", diff.Text);

            Section("Forms");
            var fill = await Call("browser_fill_form", new()
            {
                ["fields"] = new JsonArray(
                    F(Ref(o, "textbox \"Email\""), "ada@example.com"),
                    F(Ref(o, "textbox \"Password\""), "s3cret-pass"),
                    F(Ref(o, "combobox \"Country\""), "France"),
                    F(Ref(o, "checkbox \"Newsletter\""), true),
                    F(Ref(o, "radio \"Large\""), true),
                    F(Ref(o, "textbox \"Message\""), "Hello there")),
            });
            Check(!fill.Error && !fill.Text.Contains("s3cret-pass"), "fill_form fills fields and never echoes a password", fill.Text);
            Check(fill.Text.Contains("Changes since your last look"), "an action returns the changed lines");
            var values = await Eval("() => [email.value, pw.value, country.value, news.checked, document.querySelector('[value=l]').checked, msg.value].join('|')");
            Check(values.Contains("ada@example.com|s3cret-pass|fr|true|true|Hello there"), "the fields hold the values", values);

            var typed = await Call("browser_type", new() { ["ref"] = Ref(o, "textbox \"Email\""), ["text"] = "grace@example.com", ["clear"] = true, ["submit"] = true });
            Check(!typed.Error && (await Out()).Contains("sent grace@example.com"), "type with clear and submit sends the form", typed.Text);
            var slow = await Call("browser_type", new() { ["ref"] = Ref(o, "textbox \"Message\""), ["text"] = "two words\nnext line", ["clear"] = true, ["slowly"] = true });
            Check(!slow.Error && (await Eval("() => msg.value")).Contains("two words\\nnext line"), "typing key by key handles spaces and new lines", slow.Text);

            var keys = await Call("browser_press_key", new() { ["key"] = "Control+A", ["ref"] = Ref(o, "textbox \"Message\"") });
            await Call("browser_press_key", new() { ["key"] = "Backspace" });
            Check(!keys.Error && (await Eval("() => msg.value")).Trim('"').Length == 0, "Control+A then Backspace empties a field");

            var sel = await Call("browser_select_option", new() { ["ref"] = Ref(o, "combobox \"Country\""), ["values"] = new JsonArray("Spain") });
            Check(!sel.Error && (await Eval("() => country.value")).Contains("es"), "select_option chooses an option");
            var nosel = await Call("browser_select_option", new() { ["ref"] = Ref(o, "combobox \"Country\""), ["values"] = new JsonArray("Narnia") });
            Check(nosel.Error && nosel.Text.Contains("\"Germany\""), "a missing option lists the options", nosel.Text);

            Section("Clicks");
            var shadow = await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Shadow button\"") });
            Check(!shadow.Error && (await Out()).Contains("shadow clicked"), "click inside a shadow root", shadow.Text);
            var frame = await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Frame button\"") });
            Check(!frame.Error && (await Out()).Contains("frame clicked"), "click inside an iframe lands on it", frame.Text);
            var glass = await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Covered frame button\"") });
            Check(glass.Error && glass.Text.Contains("covered by") && !(await Out()).Contains("covered frame clicked"),
                "a click on a frame's button under a layer of the page around the frame is refused", glass.Text);
            var oldFrame = Ref(o, "button \"Old frame button\"");
            await Eval("() => { document.querySelector('iframe[title=\"Nav frame\"]').contentWindow.location.href = 'second.html'; return 1 }");
            for (int i = 0; i < 50 && !(await Eval("() => document.querySelector('iframe[title=\"Nav frame\"]').contentDocument.title")).Contains("Second page"); i++)
                await Task.Delay(100);
            var stale = await Call("browser_click", new() { ["ref"] = oldFrame });
            Check(stale.Error && stale.Text.Contains("no longer on the page") && !(await Out()).Contains("old frame clicked"),
                "a handle from a frame's earlier page is refused", stale.Text);
            var card = await Call("browser_click", new() { ["ref"] = Ref(o, "clickable \"Pointer card\"") });
            Check(!card.Error && (await Out()).Contains("card clicked"), "click a script-driven div");

            await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Show banner\"") });
            var covered = await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Under button\"") });
            Check(covered.Error && covered.Text.Contains("covered by"), "a click on a covered element is refused with what covers it", covered.Text);
            var now = await Call("browser_snapshot");
            await Call("browser_click", new() { ["ref"] = Ref(now.Text, "button \"Accept cookies\"") });
            var under = await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Under button\"") });
            Check(!under.Error && (await Out()).Contains("under clicked"), "after closing the banner the click goes through", under.Text);

            await Call("browser_window", new() { ["zoom"] = 150 });
            var zoomed = await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Shadow button\"") });
            await Eval("() => { out.textContent = 'reset'; return 1 }");
            var zoomClick = await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Under button\"") });
            Check(!zoomed.Error && !zoomClick.Error && (await Out()).Contains("under clicked"), "clicks land at 150% zoom", zoomClick.Text);
            var reset = await Call("browser_window", new() { ["zoom"] = "reset" });
            Check(reset.Structured?["zoom"]?.GetValue<double>() == 1, "zoom reset to 100%", reset.Text);

            var hover = await Call("browser_hover", new() { ["ref"] = Ref(o, "Hover target") });
            Check(!hover.Error && (await Out()).Contains("hovered"), "hover fires mouse enter", hover.Text);

            Section("Dialogs");
            var confirm = await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Delete item\"") });
            Check(confirm.Text.Contains("Really delete?") && confirm.Text.Contains("browser_handle_dialog"), "a confirm dialog is reported and held", confirm.Text);
            var blocked = await Call("browser_snapshot");
            Check(blocked.Error && blocked.Text.Contains("browser_handle_dialog"), "reading the page waits for the dialog answer", blocked.Text);
            var answered = await Call("browser_handle_dialog", new() { ["accept"] = true });
            Check(!answered.Error && (await Out()).Contains("confirmed"), "answering OK lets the page go on", answered.Text);
            var alert = await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Save item\"") });
            Check(alert.Text.Contains("showed an alert: \"Saved!\"") && (await Out()).Contains("after alert"), "an alert is closed and reported", alert.Text);
            await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Ask name\"") });
            var prompted = await Call("browser_handle_dialog", new() { ["accept"] = true, ["promptText"] = "Grace" });
            Check(!prompted.Error && (await Out()).Contains("name=Grace"), "a prompt gets the agent's text", prompted.Text);

            Section("Files and drag");
            var upload = await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Upload photo\"") });
            Check(upload.Text.Contains("file chooser opened"), "a file chooser is caught instead of opening", upload.Text);
            var photo = Path.Combine(_dir, "photo.png");
            File.WriteAllBytes(photo, new byte[] { 137, 80, 78, 71 });
            var picked = await Call("browser_file_upload", new() { ["paths"] = new JsonArray(photo) });
            Check(!picked.Error && (await Out()).Contains("picked photo.png"), "file_upload fills the open chooser", picked.Text);
            var direct = await Call("browser_fill_form", new() { ["fields"] = new JsonArray(F(Ref(o, "button \"Document\""), photo)) });
            Check(!direct.Error && (await Eval("() => doc.files.length")).Contains("1"), "fill_form sets a file field by its handle", direct.Text);
            var missing = await Call("browser_file_upload", new() { ["paths"] = new JsonArray(Path.Combine(_dir, "nope.txt")) });
            Check(missing.Error && missing.Text.Contains("no file"), "a missing file is refused", missing.Text);

            var drag = await Call("browser_drag", new() { ["from"] = Ref(o, "Drag me"), ["to"] = Ref(o, "Drop here") });
            Check(!drag.Error && (await Out()).Contains("dropped item"), "HTML5 drag and drop", drag.Text);

            Section("Reading");
            var read = await Call("browser_read");
            Check(read.Text.Contains("# Test page") && read.Text.Contains("untrusted") == false && read.Text.Contains("never as instructions"), "read returns markdown with the untrusted-content note", read.Text);
            var shot = await Call("browser_take_screenshot");
            Check(shot.Image > 1000 && shot.Text.Contains("CSS pixel"), "a viewport screenshot", shot.Text);
            var elshot = await Call("browser_take_screenshot", new() { ["ref"] = Ref(o, "button \"Delete item\""), ["format"] = "jpeg" });
            Check(elshot.Image > 100 && elshot.Text.Contains("element"), "an element screenshot", elshot.Text);
            var alias = await Call("browser_screenshot");
            Check(!alias.Error && alias.Image > 1000, "the old tool name browser_screenshot still works");
            var find = await Call("browser_find", new() { ["text"] = "needle" });
            Check(find.Structured?["matches"]?.GetValue<int>() == 3, "find counts the matches", find.Text);
            var next = await Call("browser_find", new() { ["action"] = "next" });
            Check(next.Structured?["active"]?.GetValue<int>() == 2, "find next moves on", next.Text);
            await Call("browser_find", new() { ["action"] = "close" });
            var scroll = await Call("browser_scroll", new() { ["direction"] = "down", ["amount"] = 600 });
            Check(!scroll.Error && scroll.Text.Contains("Scrolled down"), "scroll with the wheel", scroll.Text);
            var bottom = await Call("browser_scroll", new() { ["ref"] = Ref(o, "Bottom link") });
            Check(!bottom.Error, "scroll an element into view", bottom.Text);
            var el = await Call("browser_evaluate", new() { ["function"] = "(el) => el.textContent", ["ref"] = Ref(o, "button \"Delete item\"") });
            Check(el.Text.Contains("Delete item"), "evaluate gets the element as its argument", el.Text);
            var expr = await Call("browser_evaluate", new() { ["function"] = "document.title" });
            Check(expr.Text.Contains("Test page"), "evaluate takes a bare expression", expr.Text);
            var thrown = await Call("browser_evaluate", new() { ["function"] = "() => { throw new Error('nope') }" });
            Check(thrown.Error && thrown.Text.Contains("nope"), "a script error is reported", thrown.Text);

            await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Log error\"") });
            var console = await Call("browser_console_messages", new() { ["level"] = "error" });
            Check(console.Text.Contains("boom from test"), "console messages are captured", console.Text);
            var net = await Call("browser_network_requests", new() { ["filter"] = "pdanet.test" });
            Check(net.Text.Contains("index.html") && net.Structured?["route"] != null, "network requests and the route", net.Text);

            await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Load later\"") });
            var wait = await Call("browser_wait_for", new() { ["text"] = "Late text arrived", ["timeout"] = 10 });
            Check(!wait.Error && wait.Text.Contains("is on the page"), "wait_for text", wait.Text);
            var res = await Rpc("resources/read", new JsonObject { ["uri"] = "browser://active/text" });
            Check(res?["result"]?["contents"]?[0]?["text"]?.GetValue<string>().Contains("Test page") == true, "resources/read of the active tab's text");

            Section("Downloads and print");
            var dl = await Call("browser_click", new() { ["ref"] = Ref(o, "link \"Download file\"") });
            Check(dl.Text.Contains("A download started"), "a download is reported", dl.Text);
            var waited = await Call("browser_downloads", new() { ["action"] = "wait", ["timeout"] = 20 });
            var dlPath = waited.Structured?["downloads"]?[0]?["path"]?.GetValue<string>();
            Check(waited.Text.Contains("finished") && dlPath != null && File.Exists(dlPath), "downloads wait until the file is there", waited.Text);
            var pdf = await Call("browser_print", new() { ["path"] = Path.Combine(_dir, "page.pdf") });
            Check(!pdf.Error && File.Exists(Path.Combine(_dir, "page.pdf")) && new FileInfo(Path.Combine(_dir, "page.pdf")).Length > 1000, "save the page as a PDF", pdf.Text);

            Section("Tabs");
            var pop = await Call("browser_click", new() { ["ref"] = Ref(o, "link \"Open second page\"") });
            Check(pop.Text.Contains("A new tab opened") && pop.Text.Contains("Page: Second page"), "a link that opens a new tab is followed", pop.Text);
            var tabs = await Call("browser_tabs", new() { ["action"] = "list" });
            Check(tabs.Structured?["tabs"] is JsonArray { Count: 2 } && tabs.Structured?["activeIndex"]?.GetValue<int>() == 1, "tabs lists both tabs", tabs.Text);
            var other = await Call("browser_click", new() { ["ref"] = Ref(o, "button \"Delete item\"") });
            Check(other.Error && other.Text.Contains("belongs to another tab"), "a handle from another tab is refused", other.Text);
            await _ui.InvokeAsync(() => _win.AutoPick(_win.AutoTabs[0])); // the person looks at another tab
            var second = await Call("browser_snapshot");
            Check(second.Text.Contains("Page: Second page"), "a read follows the agent's tab while the person looks at another", second.Text);
            var back = await Call("browser_click", new() { ["ref"] = Ref(second.Text, "link \"Back to test\"") });
            Check(back.Text.Contains("Navigated to"), "a link navigates", back.Text);
            Check(back.Text.Contains("back to the front"), "an action first brings the agent's tab back to the front", back.Text);
            var old = await Call("browser_click", new() { ["ref"] = Ref(second.Text, "link \"Back to test\"") });
            Check(old.Error && old.Text.Contains("earlier page"), "a handle from an earlier page is refused", old.Text);
            var hist = await Call("browser_navigate_back");
            Check(!hist.Error && hist.Text.Contains("Second page"), "back", hist.Text);
            var fwd = await Call("browser_navigate_forward");
            Check(!fwd.Error && fwd.Text.Contains("Test page"), "forward", fwd.Text);
            var reload = await Call("browser_reload");
            Check(!reload.Error && reload.Text.Contains("Reloaded"), "reload", reload.Text);
            var dup = await Call("browser_tabs", new() { ["action"] = "duplicate" });
            Check(dup.Structured?["tabs"] is JsonArray { Count: 3 }, "duplicate a tab", dup.Text);
            var moved = await Call("browser_tabs", new() { ["action"] = "move", ["index"] = 2, ["to"] = 0 });
            Check(!moved.Error, "move a tab", moved.Text);
            var closed = await Call("browser_tabs", new() { ["action"] = "close_others", ["index"] = 0 });
            Check(closed.Structured?["tabs"] is JsonArray { Count: 1 }, "close the other tabs", closed.Text);
            var reopen = await Call("browser_tabs", new() { ["action"] = "reopen" });
            Check(reopen.Structured?["tabs"] is JsonArray { Count: 2 }, "reopen a closed tab", reopen.Text);
            var sel2 = await Call("browser_tabs", new() { ["action"] = "select", ["index"] = 0 });
            Check(!sel2.Error && sel2.Text.Contains("Switched to tab [0]"), "select a tab", sel2.Text);
            var newTab = await Call("browser_tabs", new() { ["action"] = "new", ["url"] = "https://pdanet.test/second.html" });
            Check(newTab.Text.Contains("Second page") && newTab.Structured?["tabs"] is JsonArray { Count: 3 }, "open a new tab at an address", newTab.Text);
            var close = await Call("browser_tabs", new() { ["action"] = "close" });
            Check(close.Structured?["tabs"] is JsonArray { Count: 2 }, "close a tab", close.Text);
            // A page the person opened is never replaced: the agent's navigate opens a tab of its own.
            await _ui.InvokeAsync(() => _win.Open("https://pdanet.test/second.html"));
            for (int i = 0; i < 100 && await _ui.InvokeAsync(() => _win.AutoActive?.Loading ?? true); i++) await Task.Delay(50);
            var person = await _ui.InvokeAsync(() => _win.AutoTabs.ToList().IndexOf(_win.AutoActive!));
            await Call("browser_tabs", new() { ["action"] = "select", ["index"] = person });
            var keep = await Call("browser_navigate", new() { ["url"] = "https://pdanet.test/index.html" });
            var personTab = await _ui.InvokeAsync(() => _win.AutoTabs[person]);
            var agentTab = await _ui.InvokeAsync(() => _win.AutoActive!);
            Check(keep.Text.Contains("holds a page the person opened") && personTab.Url.EndsWith("second.html"),
                "navigate never replaces a page the person opened", keep.Text);

            // The person moves to their tab while an action runs: the agent stays in its own tab.
            var fresh = await Call("browser_snapshot");
            var busy = CallAsync("browser_click", new() { ["ref"] = Ref(fresh.Text, "button \"Busy button\"") });
            await Task.Delay(600);
            await _ui.InvokeAsync(() => _win.AutoPick(personTab));
            var busyDone = await busy;
            var stay = await Call("browser_snapshot");
            Check(busyDone.Text.Contains("The person is on tab") && stay.Text.Contains("Page: Test page"),
                "when the person switches tabs during an action, the agent stays in its own tab", busyDone.Text + "\n----\n" + stay.Text);

            // Acting in a tab the person opened asks them first, and a no keeps the agent out.
            await Call("browser_tabs", new() { ["action"] = "select", ["index"] = person });
            var theirs = await Call("browser_snapshot");
            var intrude = CallAsync("browser_click", new() { ["ref"] = Ref(theirs.Text, "link \"Back to test\"") });
            var tq = await PromptAsync();
            Check(tq?.Kind == PromptKind.Sensitive && tq.Title.Contains("your tab"), "acting in a tab the person opened asks them first", tq?.Title);
            if (tq != null) _presence.Answer(tq, PromptChoice.Deny);
            var refusedTab = await intrude;
            Check(refusedTab.Error && refusedTab.Text.Contains("did not let") && personTab.Url.EndsWith("second.html"),
                "a no keeps the agent out of that tab", refusedTab.Text);
            var lend = CallAsync("browser_press_key", new() { ["key"] = "End" });
            var lq = await PromptAsync();
            if (lq != null) _presence.Answer(lq, PromptChoice.Allow);
            var lent = await lend;
            bool once;
            try { once = !(await Call("browser_press_key", new() { ["key"] = "Home" }).WaitAsync(TimeSpan.FromSeconds(10))).Error; }
            catch (TimeoutException) { once = false; }
            Check(lq != null && !lent.Error && once, "once the person allows it, the agent acts there without asking again", lent.Text);

            // Closing a tab of the person's asks too; the agent's own tabs close without a question.
            await Call("browser_tabs", new() { ["action"] = "close", ["index"] = await _ui.InvokeAsync(() => _win.AutoTabs.ToList().IndexOf(agentTab)) });
            var closing = CallAsync("browser_tabs", new() { ["action"] = "close" });
            var cq = await PromptAsync();
            Check(cq?.Kind == PromptKind.Sensitive && cq.Title.Contains("close"), "closing a tab the person opened asks them first", cq?.Title);
            if (cq != null) _presence.Answer(cq, PromptChoice.Allow);
            var closedTheirs = await closing;
            Check(!closedTheirs.Error && !await _ui.InvokeAsync(() => _win.AutoTabs.Contains(personTab)), "and closes it once they allow it", closedTheirs.Text);

            var readTab = await Call("browser_read", new() { ["tab"] = 1 });
            Check(!readTab.Error && readTab.Text.Length > 20, "read another tab without switching", readTab.Text);

            Section("Browser features");
            var add = await Call("browser_web_apps", new() { ["action"] = "add", ["url"] = "https://pdanet.test/second.html", ["name"] = "Smoke app" });
            Check(add.Structured?["webApps"] is JsonArray a1 && a1.Any(x => x?["name"]?.GetValue<string>() == "Smoke app"), "pin a web app", add.Text);
            var rename = await Call("browser_web_apps", new() { ["action"] = "rename", ["name"] = "Smoke app", ["newName"] = "Smoke two" });
            Check(rename.Text.Contains("Smoke two"), "rename a web app", rename.Text);
            var openApp = await Call("browser_web_apps", new() { ["action"] = "open", ["name"] = "Smoke two" });
            Check(!openApp.Error && openApp.Text.Contains("Opened the web app"), "open a web app", openApp.Text);
            var remove = await Call("browser_web_apps", new() { ["action"] = "remove", ["name"] = "Smoke two" });
            Check(remove.Structured?["webApps"] is JsonArray a2 && a2.All(x => x?["name"]?.GetValue<string>() != "Smoke two"), "remove a web app", remove.Text);
            var history = await Call("browser_history", new() { ["action"] = "search", ["query"] = "pdanet.test" });
            Check(history.Structured?["pages"] is JsonArray { Count: > 0 }, "history finds the visited pages", history.Text);
            var clearing = CallAsync("browser_history", new() { ["action"] = "clear" });
            var hq = await PromptAsync();
            if (hq != null) _presence.Answer(hq, PromptChoice.Deny);
            var kept = await clearing;
            var still = await Call("browser_history", new() { ["action"] = "search", ["query"] = "pdanet.test" });
            Check(hq?.Kind == PromptKind.Sensitive && kept.Error && still.Structured?["pages"] is JsonArray { Count: > 0 },
                "clearing the history asks the person first, and a no keeps it", kept.Text);
            var forgetting = CallAsync("browser_history", new() { ["action"] = "forget", ["host"] = "pdanet.test" });
            var fq = await PromptAsync();
            if (fq != null) _presence.Answer(fq, PromptChoice.Allow);
            var forget = await forgetting;
            Check(fq?.Kind == PromptKind.Sensitive && forget.Text.Contains("Forgot"), "forgetting a website asks first, then forgets it", forget.Text);
            var safer = await Call("browser_settings", new() { ["openSitesWithWarnings"] = false });
            Check(!safer.Error && _presence.Prompt == null, "turning the safety warnings back on needs no question", safer.Text);
            var unsafeOn = CallAsync("browser_settings", new() { ["openSitesWithWarnings"] = true });
            var uq = await PromptAsync();
            if (uq != null) _presence.Answer(uq, PromptChoice.Deny);
            var unsafeRes = await unsafeOn;
            var safe = await _ui.InvokeAsync(() => !BrowserStore.Instance.State.OpenUnsafeSites);
            Check(uq?.Kind == PromptKind.Sensitive && unsafeRes.Error && safe, "turning the safety warnings off asks the person first", unsafeRes.Text);
            var settings = await Call("browser_settings", new() { ["websiteColors"] = "dark", ["searchEngine"] = "duckduckgo" });
            Check(settings.Structured?["websiteColors"]?.GetValue<string>() == "dark" && settings.Structured?["searchEngine"]?.GetValue<string>() == "duckduckgo", "change settings", settings.Text);
            await Call("browser_settings", new() { ["websiteColors"] = "auto", ["searchEngine"] = "google" });
            var size = await Call("browser_window", new() { ["width"] = 800, ["height"] = 600 });
            var vw = size.Structured?["viewport"]?["width"]?.GetValue<double>() ?? 0;
            Check(Math.Abs(vw - 800) <= 2, $"resize the page area ({vw})", size.Text);
            var full = await Call("browser_window", new() { ["fullScreen"] = true });
            Check(full.Structured?["fullScreen"]?.GetValue<bool>() == true, "full screen on");
            await Call("browser_window", new() { ["fullScreen"] = false });
        }

        // ============================ The person ============================
        private async Task PeopleAsync()
        {
            Section("The person");
            var page = await Call("browser_navigate", new() { ["url"] = "https://pdanet.test/index.html" });
            // While helping, the person ends up in a tab of their own: the answer still shows the agent's tab.
            var helper = await _ui.InvokeAsync(() => { _win.Open("https://pdanet.test/second.html"); return _win.AutoActive!; });
            var ask = CallAsync("browser_ask_human", new() { ["message"] = "Type the code from your phone", ["reply"] = true, ["timeout"] = 30 });
            var p = await PromptAsync();
            Check(p?.Kind == PromptKind.Ask && p.WantsReply, "ask_human shows a question with a reply box");
            if (p != null) _presence.Answer(p, PromptChoice.Done, "482910");
            var asked = await ask;
            Check(!asked.Error && asked.Text.Contains("482910") && asked.Structured?["answer"]?.GetValue<string>() == "done", "the person's reply comes back", asked.Text);
            Check(asked.Text.Contains("The person is on tab") && asked.Text.Contains("Page: Test page"),
                "after ask_human the handles are the agent's tab's, wherever the person is", asked.Text);
            await _ui.InvokeAsync(() => _win.AutoClose(helper));

            // Pause holds a call that is typing: no key goes in until the person resumes.
            var typing = CallAsync("browser_type", new() { ["ref"] = Ref(page.Text, "textbox \"Message\""), ["text"] = new string('x', 300), ["clear"] = true, ["slowly"] = true });
            for (int i = 0; i < 300 && await MessageLength() < 5; i++) await Task.Delay(20);
            _presence.Pause();
            await Task.Delay(300); // the key on its way lands
            int len1 = await MessageLength();
            await Task.Delay(1000);
            int len2 = await MessageLength();
            _presence.Resume();
            var typedAll = await typing;
            int len3 = await MessageLength();
            Check(len1 == len2 && len1 is > 0 and < 300, $"pause stops a call that is typing ({len1}, then {len2} of 300 characters)");
            Check(!typedAll.Error && len3 == 300, $"and the rest goes in after resume ({len3})", typedAll.Text);

            var clear = CallAsync("browser_clear_data", new JsonObject());
            var q = await PromptAsync();
            Check(q?.Kind == PromptKind.Sensitive, "clearing data asks the person first");
            if (q != null) _presence.Answer(q, PromptChoice.Deny);
            var cleared = await clear;
            Check(cleared.Error && cleared.Text.Contains("did not approve"), "a no keeps the data", cleared.Text);

            _presence.Pause();
            var paused = CallAsync("browser_tabs", new() { ["action"] = "list" });
            await Task.Delay(800);
            Check(!paused.IsCompleted, "a paused agent waits");
            _presence.Resume();
            var resumed = await paused;
            Check(!resumed.Error, "and goes on after resume");
            bool logged;
            lock (_notes) logged = _notes.Any(n => n["method"]?.GetValue<string>() == "notifications/message" && (n["params"]?["data"]?.GetValue<string>() ?? "").Contains("paused"));
            Check(logged, "pausing is sent to the client as a log message");

            // Cancellation: a long wait ends at once and gets no response.
            var sw = Stopwatch.StartNew();
            int id = ++_id;
            var waiting = _server.HandleAsync(Msg(id, "tools/call", new JsonObject
            {
                ["name"] = "browser_wait_for", ["arguments"] = new JsonObject { ["text"] = "this never shows up", ["timeout"] = 30 },
            }), _conn, _conn.Send, CancellationToken.None);
            await Task.Delay(700);
            await _server.HandleAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled",
                ["params"] = new JsonObject { ["requestId"] = id, ["reason"] = "smoke" },
            }, _conn, null, CancellationToken.None);
            var cancelled = await waiting;
            Check(cancelled == null && sw.Elapsed < TimeSpan.FromSeconds(6), $"cancellation stops a call ({sw.ElapsedMilliseconds} ms)");

            int hung = ++_id;
            var never = _server.HandleAsync(Msg(hung, "tools/call", new JsonObject
            {
                ["name"] = "browser_evaluate", ["arguments"] = new JsonObject { ["function"] = "() => new Promise(() => {})" },
            }), _conn, _conn.Send, CancellationToken.None);
            await Task.Delay(800);
            await _server.HandleAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = hung },
            }, _conn, null, CancellationToken.None);
            bool freed;
            try
            {
                var gone = await never.WaitAsync(TimeSpan.FromSeconds(5));
                var next = await Call("browser_tabs", new() { ["action"] = "list" }).WaitAsync(TimeSpan.FromSeconds(10));
                freed = gone == null && !next.Error;
            }
            catch (TimeoutException) { freed = false; }
            Check(freed, "a call that never finishes, once cancelled, no longer holds the browser");

            // A call cancelled while it waits for the UI thread never runs, even once the thread is free.
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                _ = _ui.InvokeAsync(() => { entered.Set(); release.Wait(8000); }, DispatcherPriority.Send);
                entered.Wait(5000);
                int queued = ++_id;
                var pending = _server.HandleAsync(Msg(queued, "tools/call", new JsonObject
                {
                    ["name"] = "browser_settings", ["arguments"] = new JsonObject { ["searchEngine"] = "bing" },
                }), _conn, _conn.Send, CancellationToken.None);
                await Task.Delay(400);
                await _server.HandleAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = queued },
                }, _conn, null, CancellationToken.None);
                JsonNode? answer = new JsonObject();
                try { answer = await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
                release.Set();
                await _ui.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(300);
                var engine = await _ui.InvokeAsync(() => BrowserStore.Instance.State.Search);
                Check(answer == null && engine != "bing", $"a call cancelled while it waited for its turn never runs (search engine: {engine})");
            }

            // Approvals for an app that asks: allow always, then no question; Stop refuses.
            var asking = new AgentPresence(_ui) { CanAsk = () => true };
            var trusted = new List<string>();
            asking.IsTrusted = n => trusted.Contains(n);
            asking.Trust = n => trusted.Add(n);
            var agent = asking.Client("approval-test", null, "pipe");
            var call = new ToolCall { Tool = "x", Args = new JsonObject(), Ct = CancellationToken.None, Agent = agent };
            var gate = asking.AuthorizeAsync(call);
            var cp = await PromptAsync(asking);
            Check(cp?.Kind == PromptKind.Connect && cp.Title.Contains("Approval Test"), "a new agent waits for the person's yes", cp?.Title);
            if (cp != null) asking.Answer(cp, PromptChoice.AlwaysAllow);
            await gate;
            Check(trusted.Contains("approval-test") && agent.Access == AgentAccess.Allowed, "always allow remembers the agent");
            asking.Stop();
            string? refused = null;
            try { await asking.AuthorizeAsync(call); }
            catch (McpToolException e) { refused = e.Message; }
            Check(refused != null, "after Stop the agent is refused", refused);
            agent.DeniedUntil = DateTime.Now.AddSeconds(-1); // the short hold is over
            var again = asking.AuthorizeAsync(call);
            var ap = await PromptAsync(asking);
            Check(ap != null && ap.Title.Contains("again"), "after Stop even an always-allowed agent must be allowed again", ap?.Title);
            if (ap != null) asking.Answer(ap, PromptChoice.Allow);
            await again;
            Check(agent.Access == AgentAccess.Allowed, "and is back once the person allows it");
        }

        // ============================ HTTP ============================
        private async Task HttpAsync()
        {
            Section("HTTP transport");
            int port = FreePort();
            var http = new HttpMcpListener(_server, port, () => "smoke-key");
            Check(http.Start(out var err), "the HTTP listener starts", err);
            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var url = $"http://127.0.0.1:{port}/mcp";
            try
            {
                var init = await Post(hc, url, Msg(1, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "mcp-smoke-http", ["version"] = "1" },
                }));
                var session = init.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.First() : null;
                Check(init.StatusCode == HttpStatusCode.OK && session != null, "initialize starts a session");
                var list = await Post(hc, url, Msg(2, "tools/list"), session: session);
                Check((await Json(list))?["result"]?["tools"] is JsonArray { Count: 32 }, "tools/list over HTTP");
                var noKey = await Post(hc, url, Msg(3, "tools/list"), session: session, key: null);
                Check(noKey.StatusCode == HttpStatusCode.Unauthorized, "no key is 401");
                var wrongKey = await Post(hc, url, Msg(3, "tools/list"), session: session, key: "nope");
                Check(wrongKey.StatusCode == HttpStatusCode.Unauthorized, "a wrong key is 401");
                var rebind = await Post(hc, url, Msg(4, "tools/list"), session: session, host: $"evil.example:{port}");
                Check(rebind.StatusCode == HttpStatusCode.Forbidden, "a foreign Host is 403 (DNS rebinding)");
                var site = await Post(hc, url, Msg(5, "tools/list"), session: session, origin: "https://evil.example");
                Check(site.StatusCode == HttpStatusCode.Forbidden, "a website's Origin is 403");
                var stray = await Post(hc, url, Msg(6, "tools/list"));
                Check(stray.StatusCode == HttpStatusCode.BadRequest, "a 2025 request without a session is 400");
                var discover = await Post(hc, url, Msg(7, "server/discover", meta: Modern()), version: "2026-07-28", method: "server/discover");
                Check(discover.StatusCode == HttpStatusCode.OK && (await Json(discover))?["result"]?["supportedVersions"] != null, "a stateless 2026 discover");
                var mismatch = await Post(hc, url, Msg(8, "tools/list", meta: Modern()), version: "2025-11-25", method: "tools/list");
                Check(mismatch.StatusCode == HttpStatusCode.BadRequest && (await Json(mismatch))?["error"]?["code"]?.GetValue<int>() == -32020, "a header that disagrees with the body is 400 -32020");
                var stateless = await Post(hc, url, Msg(9, "tools/call", new JsonObject
                {
                    ["name"] = "browser_tabs", ["arguments"] = new JsonObject { ["action"] = "list" },
                }, Modern(withProgress: true)), version: "2026-07-28", method: "tools/call", name: "browser_tabs", sse: true);
                var body = await stateless.Content.ReadAsStringAsync();
                Check(stateless.Content.Headers.ContentType?.MediaType == "text/event-stream" && body.Contains("\"result\"") && body.Contains("resultType"),
                    "a 2026 tool call answers as server-sent events", body.Length > 300 ? body[..300] : body);
                var noMethod = await Post(hc, url, Msg(11, "tools/list", meta: Modern()), version: "2026-07-28");
                Check(noMethod.StatusCode == HttpStatusCode.BadRequest && (await Json(noMethod))?["error"]?["code"]?.GetValue<int>() == -32020,
                    "a 2026 request without its Mcp-Method header is 400 -32020");
                var noVersion = await Post(hc, url, Msg(12, "tools/list", meta: Modern()), method: "tools/list");
                Check(noVersion.StatusCode == HttpStatusCode.BadRequest && (await Json(noVersion))?["error"]?["code"]?.GetValue<int>() == -32020,
                    "a 2026 request without its MCP-Protocol-Version header is 400 -32020");
                var noName = await Post(hc, url, Msg(13, "tools/call", new JsonObject
                {
                    ["name"] = "browser_tabs", ["arguments"] = new JsonObject { ["action"] = "list" },
                }, Modern()), version: "2026-07-28", method: "tools/call");
                Check(noName.StatusCode == HttpStatusCode.BadRequest && (await Json(noName))?["error"]?["code"]?.GetValue<int>() == -32020,
                    "a 2026 tool call without its Mcp-Name header is 400 -32020");
                var gone = new HttpRequestMessage(HttpMethod.Delete, url);
                gone.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "smoke-key");
                gone.Headers.Add("Mcp-Session-Id", session);
                var del = await hc.SendAsync(gone);
                Check(del.StatusCode == HttpStatusCode.NoContent, "DELETE ends the session");
                var after = await Post(hc, url, Msg(10, "tools/list"), session: session);
                Check(after.StatusCode == HttpStatusCode.NotFound, "an ended session is 404");
            }
            finally { http.Stop(); }
        }

        // ============================ The pipe and the relay ============================
        private async Task PipeAsync()
        {
            Section("Pipe and --mcp relay");
            using var cts = new CancellationTokenSource();
            var serving = AgentPipe.ServeAsync(_server, AgentPipe.Name, cts.Token);
            await Task.Delay(300);
            if (serving.IsCompleted)
            {
                Console.Error.WriteLine("SKIP: another PdaNet listens on the agent pipe, the relay check needs it free.");
                return;
            }
            var psi = new ProcessStartInfo(Environment.ProcessPath!, "--mcp")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false),
            };
            using var relay = Process.Start(psi)!;
            var stderr = relay.StandardError.ReadToEndAsync();
            try
            {
                await relay.StandardInput.WriteLineAsync(Msg(1, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "mcp-smoke-relay", ["version"] = "1" },
                }).ToJsonString());
                await relay.StandardInput.FlushAsync();
                var line = await relay.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Check(line != null && line.Contains("pdanet-browser"), "--mcp relays to the running PdaNet over the pipe", line);
                await relay.StandardInput.WriteLineAsync(Msg(2, "tools/call", new JsonObject
                {
                    ["name"] = "browser_tabs", ["arguments"] = new JsonObject { ["action"] = "list" },
                }).ToJsonString());
                await relay.StandardInput.FlushAsync();
                var tabs = await relay.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Check(tabs != null && tabs.Contains("tabs") && tabs.Contains("Test page"), "a tool call through the relay drives this browser", tabs);
                relay.StandardInput.Close();
                bool ended = relay.WaitForExit(8000);
                Check(ended, "the relay ends when its client goes");
                var err = await stderr.WaitAsync(TimeSpan.FromSeconds(5));
                Check(err.Contains("connected to the running PdaNet"), "the relay says where it connected", err);
            }
            finally
            {
                try { if (!relay.HasExited) relay.Kill(); } catch { }
                cts.Cancel();
            }
        }

        // ============================ Helpers ============================
        private sealed record Reply(string Text, bool Error, JsonObject? Structured, int Image);

        private Task<Reply> Call(string tool, JsonObject? args = null) => CallAsync(tool, args ?? new JsonObject());

        private async Task<Reply> CallAsync(string tool, JsonObject args)
        {
            var r = await Rpc("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args });
            if (r?["error"] is JsonNode e) return new Reply("RPC error: " + e.ToJsonString(), true, null, 0);
            var res = r?["result"];
            var content = res?["content"] as JsonArray ?? new JsonArray();
            var text = string.Join("\n", content.Where(c => c?["type"]?.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()));
            var image = content.Where(c => c?["type"]?.GetValue<string>() == "image").Select(c => c!["data"]!.GetValue<string>().Length).FirstOrDefault();
            return new Reply(text, res?["isError"]?.GetValue<bool>() == true, res?["structuredContent"] as JsonObject, image);
        }

        private async Task<JsonNode?> Rpc(string method, JsonObject? p = null, JsonObject? meta = null)
        {
            var msg = Msg(++_id, method, p, meta);
            return await _server.HandleAsync(msg, _conn, _conn.Send, CancellationToken.None);
        }

        private static JsonObject Msg(int id, string method, JsonObject? p = null, JsonObject? meta = null)
        {
            p = (JsonObject?)p?.DeepClone() ?? new JsonObject();
            if (meta != null) p["_meta"] = meta;
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p };
        }

        private static JsonObject Modern(bool withProgress = false)
        {
            var m = new JsonObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
                ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "mcp-smoke-modern", ["version"] = "1" },
            };
            if (withProgress) m["progressToken"] = "p1";
            return m;
        }

        private static JsonObject F(string @ref, JsonNode value) => new() { ["ref"] = @ref, ["value"] = value };

        /// <summary>The handle on the first outline line that contains <paramref name="pattern"/>.</summary>
        private static string Ref(string outline, string pattern)
        {
            foreach (var line in outline.Split('\n'))
            {
                if (!line.Contains(pattern)) continue;
                var m = Regex.Match(line, @"\[ref=(e\d+)\]");
                if (m.Success) return m.Groups[1].Value;
            }
            return "e0";
        }

        private async Task<string> Eval(string fn) => (await Call("browser_evaluate", new() { ["function"] = fn })).Text;

        private Task<string> Out() => Eval("() => out.textContent");

        /// <summary>How much the Message box holds, read straight from the page, past the tool calls that may be busy.</summary>
        private async Task<int> MessageLength()
        {
            var json = await _ui.InvokeAsync(async () =>
            {
                var core = _win.AutoActive?.View?.CoreWebView2;
                return core == null ? "-1" : await core.ExecuteScriptAsync("document.getElementById('msg') ? document.getElementById('msg').value.length : -1");
            }).Task.Unwrap();
            return int.TryParse(json, out var n) ? n : -1;
        }

        private async Task<AgentPrompt?> PromptAsync(AgentPresence? presence = null)
        {
            var p = presence ?? _presence;
            for (int i = 0; i < 100; i++)
            {
                if (p.Prompt is { } prompt) return prompt;
                await Task.Delay(50);
            }
            return null;
        }

        private async Task SetDownloadFolderAsync()
        {
            await _ui.InvokeAsync(() =>
            {
                var core = _win.AutoActive?.View?.CoreWebView2;
                if (core == null) return;
                _downloadsBefore = core.Profile.DefaultDownloadFolderPath;
                core.Profile.DefaultDownloadFolderPath = Path.Combine(_dir, "downloads");
            });
        }

        private static async Task<HttpResponseMessage> Post(HttpClient hc, string url, JsonObject msg, string? session = null, string? key = "smoke-key",
            string? host = null, string? origin = null, string? version = null, string? method = null, string? name = null, bool sse = false)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json") };
            if (key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (session != null) req.Headers.Add("Mcp-Session-Id", session);
            if (host != null) req.Headers.Host = host;
            if (origin != null) req.Headers.Add("Origin", origin);
            if (version != null) req.Headers.Add("MCP-Protocol-Version", version);
            if (method != null) req.Headers.Add("Mcp-Method", method);
            if (name != null) req.Headers.Add("Mcp-Name", name);
            req.Headers.Accept.ParseAdd(sse ? "application/json, text/event-stream" : "application/json");
            return await hc.SendAsync(req);
        }

        private static async Task<JsonNode?> Json(HttpResponseMessage r)
        {
            try { return JsonNode.Parse(await r.Content.ReadAsStringAsync()); } catch { return null; }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static void Section(string name) => Console.Error.WriteLine($"-- {name}");

        private void Check(bool ok, string what, string? detail = null)
        {
            if (ok) _ok++; else _failed++;
            Console.Error.WriteLine($"{(ok ? "OK  " : "FAIL")}: {what}");
            if (!ok && detail != null)
                Console.Error.WriteLine("      " + (detail.Length > 1500 ? detail[..1500] + " …" : detail).Replace("\n", "\n      "));
        }
    }

    // ============================ Test pages ============================
    private static class Pages
    {
        public static void Write(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "index.html"), Index);
            File.WriteAllText(Path.Combine(dir, "second.html"),
                "<!doctype html><title>Second page</title><h1>Second page</h1><p><a href=\"index.html\">Back to test</a></p>");
            File.WriteAllText(Path.Combine(dir, "frame.html"),
                "<!doctype html><button onclick=\"parent.document.getElementById('out').textContent='frame clicked'\">Frame button</button>");
            File.WriteAllText(Path.Combine(dir, "frame2.html"),
                "<!doctype html><button onclick=\"parent.document.getElementById('out').textContent='covered frame clicked'\">Covered frame button</button>");
            File.WriteAllText(Path.Combine(dir, "frame3.html"),
                "<!doctype html><button onclick=\"parent.document.getElementById('out').textContent='old frame clicked'\">Old frame button</button>");
            File.WriteAllText(Path.Combine(dir, "file.txt"), "hello from the download check");
        }

        private const string Index = """
            <!doctype html><html><head><meta charset="utf-8"><title>Test page</title>
            <style>
              body { font-family: sans-serif; margin: 16px; }
              #banner { position: fixed; inset: 0; background: rgba(0,0,0,.45); z-index: 10; display: none; }
              #banner div { position: absolute; left: 40%; top: 40%; background: #fff; padding: 12px; }
              #zone { width: 200px; height: 70px; border: 2px dashed #888; }
              #item { width: 120px; padding: 8px; background: #cde; }
              .tall { height: 1400px; }
              label { margin-right: 6px; }
            </style></head><body>
            <h1>Test page</h1>
            <p>A needle in text. Another needle here. And a third needle.</p>
            <div style="visibility:hidden;height:20px"><a href="#x">Hidden menu entry</a> <span>Folded label</span></div>
            <form id="f">
              <label for="email">Email</label><input id="email" type="email">
              <label for="pw">Password</label><input id="pw" type="password">
              <label for="country">Country</label><select id="country"><option value="de">Germany</option><option value="fr">France</option><option value="es">Spain</option></select>
              <label><input type="checkbox" id="news"> Newsletter</label>
              <label><input type="radio" name="size" value="s"> Small</label><label><input type="radio" name="size" value="l"> Large</label>
              <label for="msg">Message</label><textarea id="msg"></textarea>
              <label for="doc">Document</label><input id="doc" type="file">
              <button type="submit">Send form</button>
            </form>
            <div id="out">nothing yet</div>
            <button onclick="out.textContent = confirm('Really delete?') ? 'confirmed' : 'cancelled'">Delete item</button>
            <button onclick="alert('Saved!'); out.textContent = 'after alert'">Save item</button>
            <button onclick="out.textContent = 'name=' + prompt('Your name?', 'anon')">Ask name</button>
            <a href="second.html" target="_blank">Open second page</a>
            <iframe src="frame.html" title="Inner frame" style="width:300px;height:60px"></iframe>
            <div style="position:relative;display:inline-block"><iframe src="frame2.html" title="Covered frame" style="width:300px;height:60px"></iframe>
              <div style="position:absolute;inset:0;background:rgba(255,255,255,.02)">Glass pane</div></div>
            <iframe src="frame3.html" title="Nav frame" style="width:300px;height:60px"></iframe>
            <button onclick="const t = Date.now(); while (Date.now() - t < 1500) {} out.textContent = 'busy done'">Busy button</button>
            <shadow-box></shadow-box>
            <div style="cursor:pointer;display:inline-block;padding:6px;border:1px solid #ccc" onclick="out.textContent='card clicked'">Pointer card</div>
            <button onclick="banner.style.display='block'">Show banner</button>
            <button onclick="out.textContent='under clicked'">Under button</button>
            <div id="banner"><div><span>Cookie banner</span> <button onclick="banner.style.display='none'">Accept cookies</button></div></div>
            <input type="file" id="photoInput" style="display:none" onchange="out.textContent = 'picked ' + this.files[0].name">
            <button onclick="document.getElementById('photoInput').click()">Upload photo</button>
            <div id="item" draggable="true" ondragstart="event.dataTransfer.setData('text/plain', 'item')">Drag me</div>
            <div id="zone" role="region" aria-label="Drop here" ondragover="event.preventDefault()" ondrop="event.preventDefault(); out.textContent = 'dropped ' + event.dataTransfer.getData('text/plain')">Drop here</div>
            <a href="file.txt" download="file.txt">Download file</a>
            <div tabindex="0" role="button" onmouseenter="out.textContent='hovered'">Hover target</div>
            <button onclick="setTimeout(() => { const p = document.createElement('p'); p.textContent = 'Late text arrived'; document.body.appendChild(p); }, 700)">Load later</button>
            <button onclick="console.error('boom from test')">Log error</button>
            <div class="tall"></div>
            <a href="#top">Bottom link</a>
            <script>
              customElements.define('shadow-box', class extends HTMLElement {
                constructor() { super(); const r = this.attachShadow({ mode: 'open' }); const b = document.createElement('button');
                  b.textContent = 'Shadow button'; b.onclick = () => { document.getElementById('out').textContent = 'shadow clicked'; }; r.appendChild(b); }
              });
              document.getElementById('f').onsubmit = e => { e.preventDefault(); out.textContent = 'sent ' + email.value; };
            </script>
            </body></html>
            """;
    }
}
