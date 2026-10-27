using System.Windows;
using PdaNetPortable.Core;
using PdaNetPortable.Services;

namespace PdaNetPortable.Dialogs;

public partial class SettingsWindow : Window
{
    private readonly Models.AppConfig _cfg = ConfigManager.Instance.Config;

    public SettingsWindow()
    {
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        ProxyIp.Text = _cfg.ProxyIp;
        ProxyPort.Text = _cfg.ProxyPort.ToString();
        StealthLevel.SelectedIndex = Math.Clamp(_cfg.StealthLevel, 1, 3) - 1;
        StealthMode.IsChecked = _cfg.StealthMode;
        UseSystemBypass.IsChecked = _cfg.UseSystemBypass;
        SetWinHttp.IsChecked = _cfg.SetWinHttpProxy;
        RequireBypass.IsChecked = _cfg.RequireBypass;
        LocalPort.Text = _cfg.LocalProxyPort.ToString();
        AgentPort.Text = _cfg.AgentPort.ToString();
        AdbPathBox.Text = _cfg.AdbPath;
        ReconnectAttempts.Text = _cfg.ReconnectAttempts.ToString();
        ReconnectDelay.Text = _cfg.ReconnectDelay.ToString();

        LogLevel.SelectedIndex = _cfg.LogLevel.ToUpperInvariant() switch
        {
            "DEBUG" => 0,
            "WARNING" => 2,
            "ERROR" => 3,
            "CRITICAL" => 4,
            _ => 1
        };
        DataWarning.Text = _cfg.DataWarningMb.ToString();
        DataCritical.Text = _cfg.DataCriticalMb.ToString();
        Notifications.IsChecked = _cfg.EnableNotifications;
        StartMinimized.IsChecked = _cfg.StartMinimized;
        SingleInstance.IsChecked = _cfg.SingleInstance;

        DataDirLabel.Text = $"Config stored in: {AppPaths.DataDir}" +
                            (AppPaths.IsPortable ? "  (portable)" : "  (user profile)");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _cfg.ProxyIp = ProxyIp.Text.Trim();
        if (int.TryParse(ProxyPort.Text, out var pp)) _cfg.ProxyPort = pp;
        _cfg.StealthLevel = StealthLevel.SelectedIndex + 1;
        _cfg.StealthMode = StealthMode.IsChecked == true;
        _cfg.UseSystemBypass = UseSystemBypass.IsChecked == true;
        _cfg.SetWinHttpProxy = SetWinHttp.IsChecked == true;
        _cfg.RequireBypass = RequireBypass.IsChecked == true;
        if (int.TryParse(LocalPort.Text, out var lp)) _cfg.LocalProxyPort = lp;
        if (int.TryParse(AgentPort.Text, out var agp)) _cfg.AgentPort = agp;
        _cfg.AdbPath = AdbPathBox.Text.Trim();
        if (int.TryParse(ReconnectAttempts.Text, out var ra)) _cfg.ReconnectAttempts = ra;
        if (int.TryParse(ReconnectDelay.Text, out var rd)) _cfg.ReconnectDelay = rd;

        _cfg.LogLevel = (LogLevel.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "INFO";
        if (int.TryParse(DataWarning.Text, out var dw)) _cfg.DataWarningMb = dw;
        if (int.TryParse(DataCritical.Text, out var dc)) _cfg.DataCriticalMb = dc;
        _cfg.EnableNotifications = Notifications.IsChecked == true;
        _cfg.StartMinimized = StartMinimized.IsChecked == true;
        _cfg.SingleInstance = SingleInstance.IsChecked == true;

        ConfigManager.Instance.SaveConfig();
        Logger.Instance.Ok("Settings saved");
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
