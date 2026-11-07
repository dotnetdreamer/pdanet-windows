using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdaNetPortable.Core;
using PdaNetPortable.Mcp;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
using PdaNetPortable.Share;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>
/// Settings as a single scrolling page with a sticky Save bar. Nothing is written until the
/// user presses Save; number boxes accept digits only and are clamped on save.
/// </summary>
public partial class SettingsView : UserControl, IPage
{
    private readonly AppConfig _cfg = ConfigManager.Instance.Config;
    private MainWindow? _shell;
    private CancellationTokenSource? _relayTest;
    private bool _mcpHooked;

    public SettingsView()
    {
        InitializeComponent();
        foreach (var box in new[] { ReconnectAttempts, DataWarning, DataCritical, ProxyPort, LocalPort, AgentPort, McpPort })
            box.PreviewTextInput += DigitsOnly;
    }

    public void Attach(MainWindow shell) => _shell = shell;

    public void OnShown() => Load();

    public void Refresh()
    {
        Load();
        _shell?.Toast("Settings reloaded", ToastKind.Info);
    }

    private void Load()
    {

        Stealth.IsChecked = _cfg.StealthMode;
        (_cfg.StealthLevel switch { 1 => Lv1, 2 => Lv2, _ => Lv3 }).IsChecked = true;
        SystemBypass.IsChecked = _cfg.UseSystemBypass;
        RequireBypass.IsChecked = _cfg.RequireBypass;
        UpdateStealthEnabled();

        AutoReconnect.IsChecked = _cfg.AutoReconnect;
        ReconnectAttempts.Text = _cfg.ReconnectAttempts.ToString();
        DataWarning.Text = _cfg.DataWarningMb.ToString();
        DataCritical.Text = _cfg.DataCriticalMb.ToString();
        UpdateReconnectEnabled();

        WatchApps.IsChecked = _cfg.WatchApps;
        RefreshBlocked();

        ShowRelay(ShareRelays.Read(_cfg));

        McpAccess.IsChecked = _cfg.AgentAccess;
        McpHttp.IsChecked = _cfg.AgentHttp;
        McpPort.Text = _cfg.AgentHttpPort.ToString();
        ShowMcp();

        StartWithWindows.IsChecked = AutoStart.IsEnabled();
        StartMinimized.IsChecked = _cfg.StartMinimized;
        RefreshClose();
        Notifications.IsChecked = _cfg.EnableNotifications;
        SingleInstance.IsChecked = _cfg.SingleInstance;

        ProxyIp.Text = _cfg.ProxyIp;
        ProxyPort.Text = _cfg.ProxyPort.ToString();
        LocalPort.Text = _cfg.LocalProxyPort.ToString();
        AgentPort.Text = _cfg.AgentPort.ToString();
        AdbPath.Text = _cfg.AdbPath;

        DataDirText.Text = AppPaths.IsPortable
            ? $"Your settings live next to the app, in {AppPaths.DataDir}"
            : $"Your settings live in {AppPaths.DataDir}";

        RefreshLocation();

    }

    private void OnOpenLocation(object sender, RoutedEventArgs e) => _shell?.OpenLocation();

    private void OnManageBlocked(object sender, RoutedEventArgs e) => _shell?.OpenBlocked();

    // The block list changes on its own sheet, right away, not with the Save button.
    public void RefreshBlocked()
    {
        var rules = TrafficRules.Instance.All;
        int apps = rules.Count(r => r.Kind == BlockKind.App), sites = rules.Count - apps;
        var parts = new List<string>();
        if (apps > 0) parts.Add(apps == 1 ? "1 app" : $"{apps} apps");
        if (sites > 0) parts.Add(sites == 1 ? "1 website" : $"{sites} websites");
        BlockedSummary.Text = parts.Count == 0
            ? "Nothing is blocked, right click a line on the Apps page to start"
            : $"{string.Join(" and ", parts)} blocked";
    }

    // The question when the window closes can keep its answer, right away, not with the Save button.
    public void RefreshClose() => ShowClose(_cfg.CloseAction);

    private void ShowClose(string action) => (action switch { "tray" => CloseTray, "quit" => CloseQuit, _ => CloseAsk }).IsChecked = true;

    public void RefreshLocation()
    {
        var p = LocationMask.Find(LocationMask.ActiveCity);
        LocationStatus.Text = LocationMask.Active && p != null
            ? $"On, appearing in {p.City}  {p.OffsetLabel}"
            : "Off, using your real time zone";
    }

    public void Save()
    {
        var relay = TypedRelay();
        if (ShareRelays.Problem(relay) is { } problem)
        {
            _shell?.Toast(problem, ToastKind.Error);
            return;
        }

        _cfg.StealthMode = Stealth.IsChecked == true;
        _cfg.StealthLevel = Lv1.IsChecked == true ? 1 : Lv2.IsChecked == true ? 2 : 3;
        _cfg.UseSystemBypass = SystemBypass.IsChecked == true;
        _cfg.RequireBypass = RequireBypass.IsChecked == true;

        _cfg.AutoReconnect = AutoReconnect.IsChecked == true;
        _cfg.ReconnectAttempts = Clamp(ReconnectAttempts, _cfg.ReconnectAttempts, 1, 10);
        _cfg.DataWarningMb = Clamp(DataWarning, _cfg.DataWarningMb, 1, 1_000_000);
        _cfg.DataCriticalMb = Clamp(DataCritical, _cfg.DataCriticalMb, 1, 1_000_000);
        if (_cfg.DataCriticalMb < _cfg.DataWarningMb) _cfg.DataCriticalMb = _cfg.DataWarningMb;

        _cfg.WatchApps = WatchApps.IsChecked == true;

        ShareRelays.Write(_cfg, relay);

        _cfg.AgentAccess = McpAccess.IsChecked == true;
        _cfg.AgentHttp = McpHttp.IsChecked == true;
        _cfg.AgentHttpPort = Clamp(McpPort, _cfg.AgentHttpPort, 1024, 65535);

        _cfg.StartMinimized = StartMinimized.IsChecked == true;
        _cfg.CloseAction = CloseTray.IsChecked == true ? "tray" : CloseQuit.IsChecked == true ? "quit" : "ask";
        _cfg.EnableNotifications = Notifications.IsChecked == true;
        _cfg.SingleInstance = SingleInstance.IsChecked == true;

        _cfg.ProxyIp = string.IsNullOrWhiteSpace(ProxyIp.Text) ? "192.168.49.1" : ProxyIp.Text.Trim();
        _cfg.ProxyPort = Clamp(ProxyPort, _cfg.ProxyPort, 1, 65535);
        _cfg.LocalProxyPort = Clamp(LocalPort, _cfg.LocalProxyPort, 1, 65535);
        _cfg.AgentPort = Clamp(AgentPort, _cfg.AgentPort, 1, 65535);
        _cfg.AdbPath = AdbPath.Text.Trim();

        bool wantStart = StartWithWindows.IsChecked == true;
        if (wantStart != AutoStart.IsEnabled())
        {
            if (wantStart) AutoStart.Enable(); else AutoStart.Disable();
        }
        _cfg.AutoStart = wantStart;

        ConfigManager.Instance.SaveConfig();
        Logger.Instance.Ok("Settings saved");
        _shell?.OnSettingsSaved();
        Load();
        _shell?.Toast("Settings saved");
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        Load();
        _shell?.Toast("Changes undone", ToastKind.Info);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var fresh = new AppConfig();
        Stealth.IsChecked = fresh.StealthMode;
        (fresh.StealthLevel switch { 1 => Lv1, 2 => Lv2, _ => Lv3 }).IsChecked = true;
        SystemBypass.IsChecked = fresh.UseSystemBypass;
        RequireBypass.IsChecked = fresh.RequireBypass;
        AutoReconnect.IsChecked = fresh.AutoReconnect;
        ReconnectAttempts.Text = fresh.ReconnectAttempts.ToString();
        DataWarning.Text = fresh.DataWarningMb.ToString();
        DataCritical.Text = fresh.DataCriticalMb.ToString();
        WatchApps.IsChecked = fresh.WatchApps;
        StartWithWindows.IsChecked = false;
        StartMinimized.IsChecked = fresh.StartMinimized;
        ShowClose(fresh.CloseAction);
        Notifications.IsChecked = fresh.EnableNotifications;
        SingleInstance.IsChecked = fresh.SingleInstance;
        ProxyIp.Text = fresh.ProxyIp;
        ProxyPort.Text = fresh.ProxyPort.ToString();
        LocalPort.Text = fresh.LocalProxyPort.ToString();
        AgentPort.Text = fresh.AgentPort.ToString();
        AdbPath.Text = fresh.AdbPath;
        ShowRelay(ShareRelays.Read(fresh));
        McpAccess.IsChecked = fresh.AgentAccess;
        McpHttp.IsChecked = fresh.AgentHttp;
        McpPort.Text = fresh.AgentHttpPort.ToString();
        ShowMcp();
        UpdateStealthEnabled();
        UpdateReconnectEnabled();
        _shell?.Toast("Defaults loaded, press Save to keep them", ToastKind.Info);
    }

    private void ShowRelay(ShareRelays.Settings relay)
    {
        RelayKeyId.Text = relay.KeyId;
        RelayKeyToken.Password = relay.KeyToken;
        RelayUrls.Text = string.Join(Environment.NewLine, relay.Urls);
        RelayUsername.Text = relay.Username;
        RelayPassword.Password = relay.Password;
        RelayFrom.Text = relay.From;
    }

    private ShareRelays.Settings TypedRelay() => new(
        RelayKeyId.Text.Trim(), RelayKeyToken.Password.Trim(), ShareRelays.SplitUrls(RelayUrls.Text),
        RelayUsername.Text.Trim(), RelayPassword.Password, RelayFrom.Text.Trim());

    // Tests what is typed, saved or not. While it runs the button stops it.
    private async void OnTestRelay(object sender, RoutedEventArgs e)
    {
        if (_relayTest != null)
        {
            _relayTest.Cancel();
            return;
        }
        var relay = TypedRelay();
        if (ShareRelays.Problem(relay) is { } problem)
        {
            RelayTestText.Text = problem;
            return;
        }
        if (!relay.Any)
        {
            RelayTestText.Text = "Type a Cloudflare key, a relay web address or your own relay first";
            return;
        }
        _relayTest = new CancellationTokenSource();
        RelayTestButton.Content = "Stop";
        RelayTestText.Text = "Testing, this takes up to half a minute";
        try
        {
            var (ok, text) = await ShareRelays.TestAsync(relay, _relayTest.Token);
            RelayTestText.Text = text;
            _shell?.Toast(ok ? "The relay works from this PC" : "The relay test failed, Settings says why",
                ok ? ToastKind.Success : ToastKind.Error);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("Screen sharing relay test could not run: " + ex.Message);
            RelayTestText.Text = "The test could not run: " + ex.Message;
        }
        finally
        {
            _relayTest.Dispose();
            _relayTest = null;
            RelayTestButton.Content = "Test";
        }
    }

    // ============================ AI agents ============================
    private static string ExePath => Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "PdaNetPortable.exe");

    private string McpHttpUrl => $"http://127.0.0.1:{(int.TryParse(McpPort.Text, out var p) && p is >= 1024 and <= 65535 ? p : _cfg.AgentHttpPort)}/mcp";

    /// <summary>Shows the AI agents part as it stands: what is on, who is connected, who is always allowed.</summary>
    private void ShowMcp()
    {
        var hub = AgentHub.Instance;
        if (!_mcpHooked && hub != null)
        {
            hub.Changed += () => Dispatcher.BeginInvoke(ShowMcp);
            _mcpHooked = true;
        }
        bool on = McpAccess.IsChecked == true, http = McpHttp.IsChecked == true;
        McpOptions.IsEnabled = on;
        McpOptions.Opacity = on ? 1 : 0.45;
        McpHttpOptions.Visibility = http ? Visibility.Visible : Visibility.Collapsed;
        McpCommand.Text = $"\"{ExePath}\" --mcp";
        McpUrl.Text = McpHttpUrl;

        var active = hub?.Presence.ActiveAgents ?? Array.Empty<AgentClient>();
        McpStatus.Text = !on ? "Off, agents cannot use the browser"
            : active.Count > 0 ? $"{string.Join(" and ", active.Select(a => a.Name).Distinct())} {(active.Count == 1 ? "is" : "are")} connected now"
            : "On, PdaNet asks you in the browser before a new agent starts";
        McpHttpHint.Text = !http ? "Off, agents start PdaNet with --mcp instead"
            : !_cfg.AgentHttp ? "Press Save to turn it on"
            : hub?.HttpProblem is { } problem ? problem
            : hub?.HttpUrl is { } url ? $"On at {url}, agents send the key with every request"
            : "Turns on while agents are allowed";
        McpTrusted.Text = _cfg.AgentTrusted.Count == 0
            ? "None, PdaNet asks you for each new agent"
            : string.Join(", ", _cfg.AgentTrusted.Select(n => AgentClient.Pretty(n, null)).Distinct());
        McpForget.IsEnabled = _cfg.AgentTrusted.Count > 0;
    }

    private void OnMcpToggled(object sender, RoutedEventArgs e) => ShowMcp();

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private void Copy(string text, string what)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            _shell?.Toast($"{what} copied");
        }
        catch { _shell?.Toast("Could not copy, try again", ToastKind.Error); }
    }

    private void OnCopyClaudeCode(object sender, RoutedEventArgs e) =>
        Copy($"claude mcp add pdanet-browser -- \"{ExePath}\" --mcp", "Claude Code command");

    private void OnCopyClaudeDesktop(object sender, RoutedEventArgs e) =>
        Copy(new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["pdanet-browser"] = new JsonObject { ["command"] = ExePath, ["args"] = new JsonArray("--mcp") },
            },
        }.ToJsonString(Pretty), "Claude Desktop setup");

    private void OnCopyVsCode(object sender, RoutedEventArgs e) =>
        Copy(new JsonObject
        {
            ["servers"] = new JsonObject
            {
                ["pdanet-browser"] = new JsonObject { ["type"] = "stdio", ["command"] = ExePath, ["args"] = new JsonArray("--mcp") },
            },
        }.ToJsonString(Pretty), "VS Code setup");

    private string? Key()
    {
        if (AgentHub.Instance is { } hub) return hub.Key;
        _shell?.Toast("Start PdaNet normally to make a key", ToastKind.Warning);
        return null;
    }

    private void OnCopyMcpKey(object sender, RoutedEventArgs e)
    {
        if (Key() is { } key) Copy(key, "Key");
    }

    private void OnCopyHttpClaudeCode(object sender, RoutedEventArgs e)
    {
        if (Key() is { } key)
            Copy($"claude mcp add --transport http pdanet-browser {McpHttpUrl} --header \"Authorization: Bearer {key}\"", "Claude Code command");
    }

    private void OnCopyHttpVsCode(object sender, RoutedEventArgs e)
    {
        if (Key() is not { } key) return;
        Copy(new JsonObject
        {
            ["servers"] = new JsonObject
            {
                ["pdanet-browser"] = new JsonObject
                {
                    ["type"] = "http", ["url"] = McpHttpUrl,
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + key },
                },
            },
        }.ToJsonString(Pretty), "VS Code setup");
    }

    private void OnNewMcpKey(object sender, RoutedEventArgs e)
    {
        if (AgentHub.Instance is not { } hub) return;
        hub.NewKey();
        _shell?.Toast("New key made, agents with the old key can no longer connect");
    }

    private void OnForgetMcpAgents(object sender, RoutedEventArgs e)
    {
        AgentHub.Instance?.ForgetTrusted();
        ShowMcp();
        _shell?.Toast("PdaNet asks you again for every agent");
    }

    // Stealth strength and the extra switches only matter when stealth is on.
    private void OnStealthToggled(object sender, RoutedEventArgs e) => UpdateStealthEnabled();

    private void UpdateStealthEnabled()
    {
        bool on = Stealth.IsChecked == true;
        StealthOptions.IsEnabled = on;
        StealthOptions.Opacity = on ? 1 : 0.45;
    }

    private void OnAutoReconnectToggled(object sender, RoutedEventArgs e) => UpdateReconnectEnabled();

    private void UpdateReconnectEnabled()
    {
        bool on = AutoReconnect.IsChecked == true;
        ReconnectRow.IsEnabled = on;
        ReconnectRow.Opacity = on ? 1 : 0.45;
    }

    private void OnBrowseAdb(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose adb.exe",
            Filter = "adb program (adb.exe)|adb.exe|All programs (*.exe)|*.exe",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) AdbPath.Text = dlg.FileName;
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true }); }
        catch { _shell?.Toast("Could not open the folder", ToastKind.Error); }
    }

    private static void DigitsOnly(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private static int Clamp(TextBox box, int fallback, int min, int max) =>
        int.TryParse(box.Text, out var v) ? Math.Clamp(v, min, max) : fallback;
}
