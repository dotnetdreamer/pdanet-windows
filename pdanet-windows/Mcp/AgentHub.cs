using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
using PdaNetPortable.Views;

namespace PdaNetPortable.Mcp;

/// <summary>The pipe a running PdaNet takes agents on. Only this Windows user can open it.</summary>
public static class AgentPipe
{
    public static string Name { get; } = "PdaNetBrowserMcp-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

    /// <summary>
    /// Serves MCP on a named pipe, one connection per client, until cancelled. Returns false at
    /// once when another process owns the pipe already.
    /// </summary>
    public static async Task<bool> ServeAsync(McpServer server, string name, CancellationToken ct)
    {
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, options);
            }
            catch (Exception e)
            {
                Logger.Instance.Warning($"Agents started with --mcp cannot reach this PdaNet: {e.Message}");
                return false;
            }
            first = false;
            try { await pipe.WaitForConnectionAsync(ct); }
            catch
            {
                pipe.Dispose();
                break;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    using (pipe)
                    {
                        var channel = new LineChannel(pipe, pipe);
                        await server.ServeAsync(channel.ReadAsync, m => channel.Write(m), "pipe", ct);
                        await channel.CloseAsync();
                    }
                }
                catch (Exception e) { Logger.Instance.Debug($"Agent pipe: {e.Message}"); }
            });
        }
        return true;
    }
}

/// <summary>
/// The MCP server inside the running PdaNet app. Agents reach it two ways:
/// <list type="bullet">
/// <item>"PdaNetPortable.exe --mcp", which MCP clients start, finds the running PdaNet and relays
/// its stdio over a named pipe that only this Windows user can open.</item>
/// <item>Streamable HTTP on this PC's loopback address, with a key, when turned on in Settings.</item>
/// </list>
/// Either way the agent drives the person's own PdaNet browser, the same window with its tabs, and
/// the person sees it, pauses it and stops it from the bar at the top of the browser. A new agent
/// acts only once the person allowed it there.
/// </summary>
public sealed class AgentHub : IBrowserHost
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PdaNet browser agent key");

    private readonly MainWindow _shell;
    private readonly AppConfig _cfg = ConfigManager.Instance.Config;
    private readonly CancellationTokenSource _cts = new();
    private HttpMcpListener? _http;

    public static AgentHub? Instance { get; private set; }

    private AgentHub(MainWindow shell)
    {
        _shell = shell;
        var ui = shell.Dispatcher;
        Presence = new AgentPresence(ui)
        {
            Enabled = () => _cfg.AgentAccess,
            IsTrusted = name => _cfg.AgentTrusted.Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)),
            Trust = name => ui.BeginInvoke(() =>
            {
                if (_cfg.AgentTrusted.Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
                // A new list, so a reader on another thread never sees one being changed.
                _cfg.AgentTrusted = new List<string>(_cfg.AgentTrusted) { name };
                ConfigManager.Instance.SaveConfig();
                Changed?.Invoke();
            }),
        };
        Presence.AttentionNeeded += () =>
        {
            var w = _shell.OpenBrowserForAgent();
            w.Reveal();
            if (Presence.Prompt is { } p) _shell.NotifyAgent(p.Title);
        };
        Presence.Changed += () => Changed?.Invoke();
        AgentPresence.Current = Presence;

        Session = new BrowserSession(ui, this, Presence, standalone: false);
        Server = new McpServer(BrowserTools.Build(Session), BrowserTools.Resources(), BrowserTools.Prompts(), Presence)
        {
            Instructions = BrowserTools.Instructions,
            Aliases = BrowserTools.Aliases,
            ReadResource = Session.ReadResourceAsync,
        };
        Session.ResourceChanged += kind => Server.NotifyResource("browser://" + kind);
        Session.Notice += Server.Broadcast;
        Presence.Notice += Server.Broadcast;
        Presence.StopRequested += () =>
        {
            Server.CloseAll();
            _http?.DropSessions();
        };
    }

    public AgentPresence Presence { get; }
    public BrowserSession Session { get; }
    public McpServer Server { get; }

    /// <summary>Raised on the UI thread when what Settings and the Launchpad show about agents changes.</summary>
    public event Action? Changed;

    /// <summary>Why HTTP is not on although it should be, such as a port in use.</summary>
    public string? HttpProblem { get; private set; }

    public string? HttpUrl => _http?.Url;

    /// <summary>Starts taking agents, once the main window is up. Not in the screenshot demo.</summary>
    public static void Start(MainWindow shell)
    {
        if (Instance != null) return;
        try
        {
            Instance = new AgentHub(shell);
            _ = Task.Run(() => AgentPipe.ServeAsync(Instance.Server, AgentPipe.Name, Instance._cts.Token));
            Instance.ApplySettings();
        }
        catch (Exception e) { Logger.Instance.Warning($"AI agents cannot use the browser: {e.Message}"); }
    }

    // ============================ The browser ============================
    BrowserWindow? IBrowserHost.Window => _shell.Browser;

    public BrowserWindow Open() => _shell.OpenBrowserForAgent();

    public bool Hidden => false;

    // ============================ Settings ============================
    /// <summary>Applies the AI agents settings: access on or off, and HTTP on its port.</summary>
    public void ApplySettings()
    {
        if (!_cfg.AgentAccess)
        {
            Presence.Reset();
            Server.CloseAll();
        }
        bool want = _cfg.AgentAccess && _cfg.AgentHttp;
        if (want && (_http == null || _http.Port != _cfg.AgentHttpPort))
        {
            _http?.Stop();
            _ = Key; // make sure there is one before the first request
            var http = new HttpMcpListener(Server, _cfg.AgentHttpPort, () => Key);
            if (http.Start(out var error))
            {
                _http = http;
                HttpProblem = null;
            }
            else
            {
                _http = null;
                HttpProblem = error;
                Logger.Instance.Warning($"AI agents over HTTP are off: {error}");
            }
        }
        else if (!want && _http != null)
        {
            _http.Stop();
            _http = null;
            HttpProblem = null;
        }
        Changed?.Invoke();
    }

    /// <summary>The key HTTP agents send, made the first time it is needed.</summary>
    public string Key
    {
        get
        {
            var key = Unprotect(_cfg.AgentHttpKey);
            if (key.Length > 0) return key;
            return NewKey();
        }
    }

    /// <summary>Makes a new key: agents with the old one can no longer connect.</summary>
    public string NewKey()
    {
        var key = HttpMcpListener.NewKey();
        _cfg.AgentHttpKey = Protect(key);
        ConfigManager.Instance.SaveConfig();
        _http?.DropSessions();
        Changed?.Invoke();
        return key;
    }

    /// <summary>Forgets the agents the person chose to always allow.</summary>
    public void ForgetTrusted()
    {
        _cfg.AgentTrusted = new List<string>();
        ConfigManager.Instance.SaveConfig();
        Presence.Reset();
        Changed?.Invoke();
    }

    public void Shutdown()
    {
        try { _cts.Cancel(); } catch { }
        _http?.Stop();
        Server.CloseAll();
    }

    // ============================ Secrets ============================
    private static string Protect(string secret) => secret.Length == 0 ? ""
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}
