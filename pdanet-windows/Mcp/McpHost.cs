using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using PdaNetPortable.Services;
using PdaNetPortable.Views;

namespace PdaNetPortable.Mcp;

/// <summary>
/// Entry point for MCP mode ("PdaNetPortable.exe --mcp"), the command MCP clients start.
///
/// When PdaNet already runs, the process is a thin relay: it connects to the running PdaNet over a
/// named pipe that only this Windows user can open, and passes the client's stdio through, so the
/// agent drives the person's own PdaNet browser, live, with its tabs and its route through the phone.
///
/// When PdaNet does not run (or with --standalone or --hidden), the process is the server itself:
/// a minimal WPF app with a PdaNet browser window of its own, and the MCP server on stdio.
///
/// stdout carries only protocol messages: every log line goes to stderr and the file log.
/// </summary>
public static class McpHost
{
    public static int Run(string[] args)
    {
        bool hidden = args.Contains("--hidden");
        if (!hidden && !args.Contains("--standalone"))
        {
            var relayed = Relay();
            if (relayed is int code) return code;
        }
        return RunStandalone(visible: !hidden);
    }

    // ============================ Relay to the running PdaNet ============================
    /// <summary>Relays stdio to the running PdaNet. Null when no PdaNet listens, so the caller runs its own browser.</summary>
    private static int? Relay()
    {
        var pipe = new NamedPipeClientStream(".", AgentPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { pipe.Connect(500); }
        catch
        {
            pipe.Dispose();
            return null;
        }
        Console.Error.WriteLine($"PdaNet Browser MCP: connected to the running PdaNet (v{Core.Constants.AppVersion}), the agent drives its browser.");
        using (pipe)
        {
            var stdin = Console.OpenStandardInput();
            var stdout = Console.OpenStandardOutput();
            var up = Task.Run(async () =>
            {
                try { await stdin.CopyToAsync(pipe); }
                catch { /* either side went away */ }
            });
            var down = Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                try
                {
                    int n;
                    while ((n = await pipe.ReadAsync(buffer)) > 0)
                    {
                        stdout.Write(buffer, 0, n);
                        stdout.Flush();
                    }
                }
                catch { /* either side went away */ }
            });
            // The client closing stdin, or PdaNet closing the pipe (Stop, quit), ends the relay.
            Task.WaitAny(up, down);
        }
        Console.Error.WriteLine("PdaNet Browser MCP: the connection to PdaNet ended.");
        return 0;
    }

    // ============================ A browser of its own ============================
    private sealed class OwnWindow : IBrowserHost
    {
        public BrowserWindow? Window { get; set; }
        public bool Hidden { get; init; }
        public BrowserWindow Open() => Window ?? throw new McpToolException("The browser window was closed.");
    }

    private static int RunStandalone(bool visible)
    {
        ConfigManager.Instance.ToString(); // the data folder and the log are ready
        Logger.Instance.SetLevel(ConfigManager.Instance.Config.LogLevel);
        Console.Error.WriteLine($"PdaNet Browser MCP server (v{Core.Constants.AppVersion}) starting over stdio, with a browser window of its own…");

        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var (server, host, win) = Build(Dispatcher.CurrentDispatcher, visible);

        var cts = new CancellationTokenSource();
        void Shutdown() => app.Dispatcher.BeginInvoke(() => { try { app.Shutdown(); } catch { } });
        win.Closed += (_, _) => Shutdown();

        var channel = LineChannel.Stdio();
        _ = Task.Run(async () =>
        {
            try
            {
                await server.ServeAsync(channel.ReadAsync, m => channel.Write(m), "stdio", cts.Token);
                await channel.CloseAsync();
            }
            catch (Exception e) { Logger.Instance.Error($"MCP server stopped: {e.Message}"); }
            finally { Shutdown(); }
        });

        int code = app.Run();
        cts.Cancel();
        Console.Error.WriteLine("PdaNet Browser MCP server stopped.");
        return code;
    }

    /// <summary>The browser window, the agent bar's state, the engine and the server, for a process of its own.</summary>
    /// <param name="ownBrowser">
    /// True for a browser that belongs to the agent. The built in check passes false, so it drives the
    /// engine the way the PdaNet app does, with the person's tabs kept apart from the agent's.
    /// </param>
    internal static (McpServer Server, IBrowserHost Host, BrowserWindow Window) Build(Dispatcher ui, bool visible, bool ownBrowser = true)
    {
        var host = new OwnWindow { Hidden = !visible };
        // The client that started this process is the person's choice: it is allowed without asking.
        var presence = new AgentPresence(ui) { AutoAllow = true, CanAsk = () => !host.Hidden, Enabled = AccessOn };
        AgentPresence.Current = presence;
        var session = new BrowserSession(ui, host, presence, standalone: ownBrowser);
        var server = new McpServer(BrowserTools.Build(session), BrowserTools.Resources(), BrowserTools.Prompts(), presence)
        {
            Instructions = BrowserTools.Instructions,
            Aliases = BrowserTools.Aliases,
            ReadResource = session.ReadResourceAsync,
        };
        session.ResourceChanged += kind => server.NotifyResource("browser://" + kind);
        session.Notice += server.Broadcast;
        presence.Notice += server.Broadcast;

        var win = new BrowserWindow(null);
        if (visible)
        {
            win.Show();
            win.Activate();
        }
        else
        {
            // Off screen but on, so pages still render and screenshots work. Not at -32000: Windows
            // keeps that place for minimized windows and then ignores size changes.
            win.AutoHidden = true;
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            if (win.WindowState != WindowState.Normal) win.WindowState = WindowState.Normal;
            win.Left = SystemParameters.VirtualScreenLeft - win.Width - 400;
            win.Top = SystemParameters.VirtualScreenTop;
            win.ShowInTaskbar = false;
            win.ShowActivated = false;
            win.Show();
        }
        host.Window = win;
        presence.AttentionNeeded += () => { if (visible) win.Reveal(); };
        // Stop in a browser of its own closes it, which ends the server.
        presence.StopRequested += () => { try { win.Close(); } catch { } };
        return (server, host, win);
    }

    private static DateTime _configStamp;
    private static bool _accessOn = true;

    /// <summary>
    /// Whether "Let AI agents use the browser" is on in PdaNet's settings, read again whenever the
    /// settings file changes, so turning it off in PdaNet also stops a browser of its own.
    /// </summary>
    private static bool AccessOn()
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(Core.AppPaths.Config);
            if (stamp != _configStamp)
            {
                _configStamp = stamp;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Core.AppPaths.Config));
                _accessOn = !(doc.RootElement.TryGetProperty("agent_access", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.False);
            }
        }
        catch { /* no settings file yet: the default, on */ }
        return _accessOn;
    }

    /// <summary>The built in check of the whole pipeline (--mcp-smoke). Exit code 0 when everything passes.</summary>
    public static int Smoke(bool visible) => McpSmoke.Run(visible);
}
