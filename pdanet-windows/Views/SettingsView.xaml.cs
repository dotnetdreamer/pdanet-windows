using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdaNetPortable.Core;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
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

    public SettingsView()
    {
        InitializeComponent();
        foreach (var box in new[] { ReconnectAttempts, DataWarning, DataCritical, ProxyPort, LocalPort, AgentPort })
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

        StartWithWindows.IsChecked = AutoStart.IsEnabled();
        StartMinimized.IsChecked = _cfg.StartMinimized;
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

    public void RefreshLocation()
    {
        var p = LocationMask.Find(LocationMask.ActiveCity);
        LocationStatus.Text = LocationMask.Active && p != null
            ? $"On, appearing in {p.City}  {p.OffsetLabel}"
            : "Off, using your real time zone";
    }

    public void Save()
    {
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

        _cfg.StartMinimized = StartMinimized.IsChecked == true;
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
        Notifications.IsChecked = fresh.EnableNotifications;
        SingleInstance.IsChecked = fresh.SingleInstance;
        ProxyIp.Text = fresh.ProxyIp;
        ProxyPort.Text = fresh.ProxyPort.ToString();
        LocalPort.Text = fresh.LocalProxyPort.ToString();
        AgentPort.Text = fresh.AgentPort.ToString();
        AdbPath.Text = fresh.AdbPath;
        UpdateStealthEnabled();
        UpdateReconnectEnabled();
        _shell?.Toast("Defaults loaded, press Save to keep them", ToastKind.Info);
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
