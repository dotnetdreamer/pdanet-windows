using System.Text;
using System.Windows;
using System.Windows.Threading;
using PdaNetPortable.Services;

namespace PdaNetPortable.Mcp;

/// <summary>
/// Entry point for MCP mode ("PdaNetPortable.exe --mcp"). Boots a minimal WPF application so
/// WebView2 has a UI thread and message pump, opens a real PdaNet browser window, and runs the
/// stdio MCP server on a background thread. The dispatcher thread stays free to pump the browser,
/// so the window is live and watchable while the agent drives it.
///
/// stdout is reserved for the JSON-RPC stream — every log line goes to stderr and the file log,
/// never stdout.
/// </summary>
public static class McpHost
{
    public static int Run(bool visible)
    {
        ConfigManager.Instance.ToString();                   // touch config so the data dir / log are ready
        Logger.Instance.SetLevel(ConfigManager.Instance.Config.LogLevel);
        Console.Error.WriteLine($"PdaNet Browser MCP server (v{Core.Constants.AppVersion}) starting over stdio…");

        var app = new PdaNetPortable.App();
        app.InitializeComponent();                           // merge the Cyberpunk theme
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;  // outlive the single browser window

        var session = new BrowserSession(Dispatcher.CurrentDispatcher, visible);
        session.Start();

        var cts = new CancellationTokenSource();
        void Shutdown() => app.Dispatcher.BeginInvoke(() => { try { app.Shutdown(); } catch { } });
        session.Closed += Shutdown;

        var server = new McpServer(new StdioTransport(), BrowserTools.Build(session), Shutdown);
        _ = Task.Run(async () =>
        {
            try { await server.RunAsync(cts.Token); }
            catch (Exception e) { Logger.Instance.Error($"MCP server stopped: {e.Message}"); }
            finally { Shutdown(); }
        });

        int code = app.Run();
        cts.Cancel();
        Console.Error.WriteLine("PdaNet Browser MCP server stopped.");
        return code;
    }

    /// <summary>
    /// Offline end-to-end check of the automation pipeline ("--mcp-smoke"): builds a page in
    /// about:blank, then exercises snapshot, evaluate, type, read, screenshot and click. No
    /// network and no MCP client needed. Pass --show to watch it. Exit code 0 = pass, 2 = fail.
    /// </summary>
    public static int Smoke(bool visible)
    {
        var app = new PdaNetPortable.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var session = new BrowserSession(Dispatcher.CurrentDispatcher, visible);
        session.Start();

        int exit = 2;
        _ = app.Dispatcher.BeginInvoke(async () =>
        {
            exit = await RunSmokeAsync(session);
            try { app.Shutdown(); } catch { }
        }, DispatcherPriority.ApplicationIdle);

        app.Run();
        return exit;
    }

    private static async Task<int> RunSmokeAsync(BrowserSession s)
    {
        bool pass = true;
        void Check(bool ok, string what)
        {
            Console.Error.WriteLine($"{(ok ? "OK  " : "FAIL")}: {what}");
            pass &= ok;
        }

        Console.Error.WriteLine("==== PdaNet Browser MCP smoke test ====");
        try
        {
            await s.NavigateAsync("about:blank", newTab: false, 10000);

            // Build a known page without needing the network (data: URLs are blocked by design).
            await s.EvaluateAsync(
                "document.open();" +
                "document.write('<title>Hello MCP</title><h1>Hello MCP</h1><p>Award winning browser automation.</p>" +
                "<input id=q placeholder=\"Search\"><button id=b>Go</button>');" +
                "document.close();" +
                "document.getElementById('b').onclick=function(){document.body.setAttribute('data-done','yes');};" +
                "return 'built'", null);

            var snap = await s.SnapshotAsync();
            Check(snap.Contains("[ref=e") && snap.Contains("button"), "snapshot lists elements with ref handles");
            Check(snap.Contains("textbox"), "snapshot sees the input as a textbox");

            var answer = (await s.EvaluateAsync("return 6*7", null)).Trim();
            Check(answer == "42", $"evaluate returns a value (6*7 = {answer})");

            var idx = (await s.EvaluateAsync("return (window.__pdaEls||[]).findIndex(e=>e.tagName==='INPUT')+1", null)).Trim();
            Check(int.TryParse(idx, out var n) && n > 0, $"input resolved to a ref (e{idx})");
            if (int.TryParse(idx, out n) && n > 0)
            {
                await s.TypeAsync("e" + n, "hello world", submit: false, clear: true);
                var val = await s.EvaluateAsync("return document.querySelector('input').value", null);
                Check(val.Contains("hello world"), $"type put text into the field ({val})");
            }

            var read = await s.ReadAsync("text");
            Check(read.Contains("Award winning"), "read extracts page text");

            var bidx = (await s.EvaluateAsync("return (window.__pdaEls||[]).findIndex(e=>e.tagName==='BUTTON')+1", null)).Trim();
            if (int.TryParse(bidx, out var b) && b > 0)
            {
                await s.ClickAsync("e" + b, "left", doubleClick: false);
                var done = await s.EvaluateAsync("return document.body.getAttribute('data-done')", null);
                Check(done.Contains("yes"), "click fired the button's handler (trusted input)");
            }
            else Check(false, "button resolved to a ref");

            var (png, _) = await s.ScreenshotAsync(fullPage: false);
            Check(png.Length > 1000, $"screenshot captured ({png.Length} bytes)");
        }
        catch (Exception e)
        {
            Check(false, "no exception — " + e.Message);
        }

        Console.Error.WriteLine(pass ? "==== MCP SMOKE PASS ====" : "==== MCP SMOKE FAIL ====");
        return pass ? 0 : 2;
    }
}
