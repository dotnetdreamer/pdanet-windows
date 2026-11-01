using System.Text.Json.Nodes;

namespace PdaNetPortable.Mcp;

/// <summary>
/// Defines the browser tool set the agent drives and wires each one to the <see cref="BrowserSession"/>.
/// The tools follow the accessibility-snapshot model used by the best browser agents: the agent reads
/// a tree of elements with stable [ref=eN] handles (browser_snapshot), then acts on those handles.
/// Most actions reply with a fresh snapshot, so one tool call both acts and shows the result.
/// </summary>
public static class BrowserTools
{
    public static List<McpTool> Build(BrowserSession s)
    {
        var tools = new List<McpTool>();

        tools.Add(new McpTool
        {
            Name = "browser_navigate",
            Title = "Open a web page",
            Description = "Open a URL (or a search) in the PdaNet browser and wait for it to load. Returns the page's " +
                          "accessibility snapshot with [ref=eN] handles you can act on. Plain words become a web search; " +
                          "a bare host like \"example.com\" becomes https. Set newTab:true to open in a new tab.",
            InputSchema = Schema.Object(
                ("url", Schema.Str("The address to open, e.g. \"https://news.ycombinator.com\", \"example.com\", or search text."), true),
                ("newTab", Schema.Bool("Open in a new tab instead of the current one. Default false."), false)),
            Handler = (a, ct) => Text(s.NavigateAsync(ReqStr(a, "url"), OptBool(a, "newTab"), 30000)),
        });

        tools.Add(new McpTool
        {
            Name = "browser_navigate_back",
            Title = "Go back",
            Description = "Go back one page in the current tab's history, then return a fresh snapshot.",
            InputSchema = Schema.Object(),
            Idempotent = true,
            Handler = (a, ct) => Text(s.BackAsync(20000)),
        });

        tools.Add(new McpTool
        {
            Name = "browser_navigate_forward",
            Title = "Go forward",
            Description = "Go forward one page in the current tab's history, then return a fresh snapshot.",
            InputSchema = Schema.Object(),
            Idempotent = true,
            Handler = (a, ct) => Text(s.ForwardAsync(20000)),
        });

        tools.Add(new McpTool
        {
            Name = "browser_reload",
            Title = "Reload the page",
            Description = "Reload the current page. Set hard:true to bypass the cache. Returns a fresh snapshot.",
            InputSchema = Schema.Object(
                ("hard", Schema.Bool("Bypass the cache (Ctrl+Shift+R). Default false."), false)),
            Idempotent = true,
            Handler = (a, ct) => Text(s.ReloadAsync(OptBool(a, "hard"), 25000)),
        });

        tools.Add(new McpTool
        {
            Name = "browser_tabs",
            Title = "Manage tabs",
            Description = "List, open, switch or close tabs. action \"list\" shows every tab with its index; \"new\" opens a " +
                          "tab (optionally at a url); \"select\" switches to the tab at index; \"close\" closes it. " +
                          "The active tab is marked with *.",
            InputSchema = Schema.Object(
                ("action", Schema.Str("What to do.", "list", "new", "select", "close"), true),
                ("index", Schema.Int("Tab index for select/close (from the list)."), false),
                ("url", Schema.Str("Optional address to open when action is \"new\"."), false)),
            Handler = (a, ct) => Text(s.TabsAsync(ReqStr(a, "action"), OptInt(a, "index", -1), OptStr(a, "url"), 30000)),
        });

        tools.Add(new McpTool
        {
            Name = "browser_snapshot",
            Title = "Snapshot the page",
            Description = "Return the current page as an accessibility tree: each actionable element (link, button, " +
                          "textbox, checkbox…) is listed with its role, name and a stable [ref=eN] handle. This is how " +
                          "you \"see\" the page; pass the handles to browser_click, browser_type, browser_hover, etc.",
            InputSchema = Schema.Object(),
            ReadOnly = true,
            Handler = (a, ct) => Text(s.SnapshotAsync()),
        });

        tools.Add(new McpTool
        {
            Name = "browser_read",
            Title = "Read page text",
            Description = "Extract the readable text of the current page as plain text or markdown — best for reading " +
                          "articles, docs and search results without the UI chrome.",
            InputSchema = Schema.Object(
                ("format", Schema.Str("Output format.", "text", "markdown"), false)),
            ReadOnly = true,
            Handler = (a, ct) => Text(s.ReadAsync(OptStr(a, "format") ?? "text")),
        });

        tools.Add(new McpTool
        {
            Name = "browser_screenshot",
            Title = "Take a screenshot",
            Description = "Capture a PNG of the current page — the visible viewport by default, or the whole page with " +
                          "fullPage:true. Use when you need to see pixels (layout, images, charts) rather than the DOM.",
            InputSchema = Schema.Object(
                ("fullPage", Schema.Bool("Capture the entire scrollable page instead of just the viewport. Default false."), false)),
            ReadOnly = true,
            Handler = async (a, ct) =>
            {
                var (png, note) = await s.ScreenshotAsync(OptBool(a, "fullPage"));
                return new ToolResult().With(ToolContent.Text(note)).With(ToolContent.Image(png));
            },
        });

        tools.Add(new McpTool
        {
            Name = "browser_click",
            Title = "Click an element",
            Description = "Click an element by its [ref=eN] handle from the latest snapshot. Supports left/right/middle " +
                          "button and double-click. Returns a fresh snapshot so you can see what changed.",
            InputSchema = Schema.Object(
                ("ref", Schema.Str("The element handle, e.g. \"e12\", from the most recent snapshot."), true),
                ("button", Schema.Str("Mouse button.", "left", "right", "middle"), false),
                ("doubleClick", Schema.Bool("Double-click instead of single. Default false."), false)),
            Handler = (a, ct) => Text(s.ClickAsync(ReqStr(a, "ref"), OptStr(a, "button") ?? "left", OptBool(a, "doubleClick"))),
        });

        tools.Add(new McpTool
        {
            Name = "browser_type",
            Title = "Type into a field",
            Description = "Focus the element with the given [ref=eN] handle and type text into it, as real keystrokes " +
                          "(works on single-page apps). Set clear:true to empty the field first and submit:true to press " +
                          "Enter afterwards. Returns a fresh snapshot.",
            InputSchema = Schema.Object(
                ("ref", Schema.Str("The textbox/field handle from the latest snapshot."), true),
                ("text", Schema.Str("The text to type."), true),
                ("submit", Schema.Bool("Press Enter after typing (e.g. to run a search). Default false."), false),
                ("clear", Schema.Bool("Clear the field before typing. Default false."), false)),
            Handler = (a, ct) => Text(s.TypeAsync(ReqStr(a, "ref"), ReqStr(a, "text"), OptBool(a, "submit"), OptBool(a, "clear"))),
        });

        tools.Add(new McpTool
        {
            Name = "browser_press_key",
            Title = "Press a key",
            Description = "Press a single key on the focused element: a character, or a named key such as Enter, Tab, " +
                          "Escape, Backspace, Delete, ArrowUp/Down/Left/Right, Home, End, PageUp, PageDown.",
            InputSchema = Schema.Object(
                ("key", Schema.Str("The key to press, e.g. \"Enter\" or \"a\"."), true)),
            Handler = (a, ct) => Text(s.PressKeyAsync(ReqStr(a, "key"))),
        });

        tools.Add(new McpTool
        {
            Name = "browser_hover",
            Title = "Hover an element",
            Description = "Move the mouse over the element with the given [ref=eN] handle — useful to reveal menus or " +
                          "tooltips. Returns a fresh snapshot.",
            InputSchema = Schema.Object(
                ("ref", Schema.Str("The element handle from the latest snapshot."), true)),
            Handler = (a, ct) => Text(s.HoverAsync(ReqStr(a, "ref"))),
        });

        tools.Add(new McpTool
        {
            Name = "browser_select_option",
            Title = "Choose dropdown options",
            Description = "Select one or more options in a <select> dropdown by their visible label or value. Pass the " +
                          "select element's [ref=eN] handle and the option(s) to choose.",
            InputSchema = Schema.Object(
                ("ref", Schema.Str("The <select> handle from the latest snapshot."), true),
                ("values", Schema.StrArray("Option labels or values to select."), true)),
            Handler = (a, ct) => Text(s.SelectAsync(ReqStr(a, "ref"), StrList(a, "values"))),
        });

        tools.Add(new McpTool
        {
            Name = "browser_scroll",
            Title = "Scroll the page",
            Description = "Scroll the page in a direction (up, down, left, right, top, bottom), or scroll a specific " +
                          "element into view by its [ref=eN] handle.",
            InputSchema = Schema.Object(
                ("direction", Schema.Str("Direction to scroll.", "down", "up", "left", "right", "top", "bottom"), false),
                ("ref", Schema.Str("Scroll this element into view instead of a direction."), false),
                ("amount", Schema.Int("Pixels to scroll (direction only). Defaults to about one screen."), false)),
            Handler = (a, ct) => Text(s.ScrollAsync(OptStr(a, "direction"), OptStr(a, "ref"), OptInt(a, "amount", 0))),
        });

        tools.Add(new McpTool
        {
            Name = "browser_drag",
            Title = "Drag and drop",
            Description = "Drag one element onto another, by their [ref=eN] handles (for sliders, sortable lists, file " +
                          "drop targets). Returns a fresh snapshot.",
            InputSchema = Schema.Object(
                ("from", Schema.Str("Handle of the element to drag."), true),
                ("to", Schema.Str("Handle of the drop target."), true)),
            Handler = (a, ct) => Text(s.DragAsync(ReqStr(a, "from"), ReqStr(a, "to"))),
        });

        tools.Add(new McpTool
        {
            Name = "browser_wait_for",
            Title = "Wait for the page",
            Description = "Wait until some text appears (text) or disappears (textGone) on the page, or simply wait a " +
                          "fixed number of milliseconds (time). Useful after an action that loads content asynchronously.",
            InputSchema = Schema.Object(
                ("text", Schema.Str("Wait until this text appears on the page."), false),
                ("textGone", Schema.Str("Wait until this text is no longer on the page."), false),
                ("time", Schema.Int("Wait this many milliseconds (max 30000)."), false)),
            ReadOnly = true,
            Handler = (a, ct) => Text(s.WaitForAsync(OptStr(a, "text"), OptStr(a, "textGone"), OptInt(a, "time", 0), 30000)),
        });

        tools.Add(new McpTool
        {
            Name = "browser_evaluate",
            Title = "Run JavaScript",
            Description = "Run a JavaScript function body in the page and return its JSON result. If you pass a ref, the " +
                          "matching element is available to the script as window.__pdaEl. Powerful and precise — use it " +
                          "when the snapshot and the standard actions are not enough. Example script: \"return document.title\".",
            InputSchema = Schema.Object(
                ("script", Schema.Str("JavaScript to run. Use 'return <value>' to get a result back."), true),
                ("ref", Schema.Str("Optional element handle, exposed to the script as window.__pdaEl."), false)),
            Handler = (a, ct) => Text(s.EvaluateAsync(ReqStr(a, "script"), OptStr(a, "ref"))),
        });

        tools.Add(new McpTool
        {
            Name = "browser_console",
            Title = "Read the console",
            Description = "Return recent console messages (and page errors/warnings) from the current page — handy for " +
                          "diagnosing why something on the page is not working.",
            InputSchema = Schema.Object(
                ("limit", Schema.Int("How many recent messages to return (default 30)."), false)),
            ReadOnly = true,
            Handler = (a, ct) => Text(s.ConsoleAsync(OptInt(a, "limit", 30))),
        });

        tools.Add(new McpTool
        {
            Name = "browser_network",
            Title = "Inspect network & route",
            Description = "Show recent network responses from the page and how the browser is reaching the internet — " +
                          "including whether traffic is flowing through the tethered phone (\"Through PdaNet\").",
            InputSchema = Schema.Object(
                ("limit", Schema.Int("How many recent responses to return (default 30)."), false)),
            ReadOnly = true,
            Handler = (a, ct) => Text(s.NetworkAsync(OptInt(a, "limit", 30))),
        });

        tools.Add(new McpTool
        {
            Name = "browser_handle_dialog",
            Title = "Handle JS dialogs",
            Description = "Decide how JavaScript dialogs (alert/confirm/prompt/beforeunload) are answered. Dialogs are " +
                          "auto-accepted by default so nothing hangs; set accept:false to dismiss them, or give promptText " +
                          "for prompt() dialogs. Also reports the last dialog that appeared.",
            InputSchema = Schema.Object(
                ("accept", Schema.Bool("Accept (true) or dismiss (false) future dialogs."), true),
                ("promptText", Schema.Str("Text to enter for prompt() dialogs when accepting."), false)),
            Handler = (a, ct) => Text(s.HandleDialogAsync(OptBool(a, "accept", true), OptStr(a, "promptText"))),
        });

        return tools;
    }

    // ============================ Argument + result helpers ============================
    private static async Task<ToolResult> Text(Task<string> work) => ToolResult.Ok(await work);

    private static string ReqStr(JsonObject a, string key) =>
        a[key]?.GetValue<string>() is { Length: > 0 } v ? v : throw new McpToolException($"Missing required argument '{key}'.");

    private static string? OptStr(JsonObject a, string key) =>
        a[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s) ? s : null;

    private static bool OptBool(JsonObject a, string key, bool fallback = false) =>
        a[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private static int OptInt(JsonObject a, string key, int fallback) =>
        a[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

    private static List<string> StrList(JsonObject a, string key)
    {
        if (a[key] is not JsonArray arr || arr.Count == 0)
            throw new McpToolException($"'{key}' must be a non-empty array of strings.");
        return arr.Select(n => n?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList();
    }
}
