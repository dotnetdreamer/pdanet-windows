using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PdaNetPortable.Core;
using PdaNetPortable.Dialogs;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace PdaNetPortable;

public partial class MainWindow : Window
{
    private readonly ConnectionManager _cm = ConnectionManager.Instance;
    private readonly AppConfig _config = ConfigManager.Instance.Config;
    private readonly Logger _log = Logger.Instance;
    private readonly StatsCollector _stats = StatsCollector.Instance;

    private readonly ObservableCollection<LogEntry> _logs = new();
    private ICollectionView? _logView;
    private DispatcherTimer? _uiTimer;
    private WinForms.NotifyIcon? _tray;
    private Drawing.Icon? _iconRed, _iconOrange, _iconGreen;
    private bool _reallyExit;
    private bool _dataWarned;
    private int _pingCounter;
    private ConnectionProfile? _selectedProfile;

    private readonly List<double> _downHist = new();
    private readonly List<double> _upHist = new();
    private const int GraphPoints = 60;

    public MainWindow()
    {
        InitializeComponent();

        Width = _config.WindowWidth;
        Height = _config.WindowHeight;
        EndpointValue.Text = $"{_config.ProxyIp}:{_config.ProxyPort}";
        WarningValue.Text = $"{_config.DataWarningMb} MB";
        StealthStatus.Text = _config.StealthMode ? "[AUTO]" : "[OFF]";

        // Log panel
        foreach (var e in _log.GetRecent(500)) _logs.Add(e);
        _logView = CollectionViewSource.GetDefaultView(_logs);
        _logView.Filter = LogFilter;
        LogList.ItemsSource = _logView;
        _log.EntryAdded += OnLogEntry;

        // Operations state
        ModeCombo.SelectedIndex = ConnectionModeExtensions.FromKey(_config.ConnectionMode) switch
        {
            ConnectionMode.Usb => 0,
            ConnectionMode.Iphone => 2,
            ConnectionMode.AdbAgent => 3,
            _ => 1
        };
        AutoReconnectSwitch.IsChecked = _config.AutoReconnect;
        AutostartSwitch.IsChecked = AutoStart.IsEnabled();
        _cm.AutoReconnectEnabled = _config.AutoReconnect;
        LoadProfiles();

        // Connection events (marshal to UI thread)
        _cm.StateChanged += s => Dispatcher.Invoke(() => OnStateChanged(s));
        _cm.ErrorOccurred += m => Dispatcher.Invoke(() => OnError(m));
        _cm.Tick += () => Dispatcher.Invoke(UpdateMetrics);

        // UI heartbeat (clock + periodic ping + metric refresh)
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => OnUiTick();
        _uiTimer.Start();

        SetupTray();
        SetupShortcuts();

        Loaded += (_, _) =>
        {
            if (Program.StartMinimizedRequested || _config.StartMinimized)
                HideToTray(initial: true);
        };

        _log.Info("GUI ready");
    }

    private ConnectionMode SelectedMode() => ModeCombo.SelectedIndex switch
    {
        0 => ConnectionMode.Usb,
        2 => ConnectionMode.Iphone,
        3 => ConnectionMode.AdbAgent,
        _ => ConnectionMode.Wifi
    };

    // ======================= Connection actions =======================
    private void OnConnectClicked(object sender, RoutedEventArgs e) => DoConnect();
    private void OnDisconnectClicked(object sender, RoutedEventArgs e) => DoDisconnect();

    private void DoConnect()
    {
        if (_cm.IsConnected) return;
        var mode = SelectedMode();
        _config.ConnectionMode = mode.ToKey();
        ConfigManager.Instance.SaveConfig();

        if (mode == ConnectionMode.Usb || mode == ConnectionMode.AdbAgent)
        {
            _cm.Connect(mode);
            return;
        }

        // WiFi / iPhone: gather SSID + password
        var preset = _selectedProfile?.Ssid ?? WifiManager.GetCurrentSsid();
        var dlg = new WifiCredentialsDialog(mode, preset) { Owner = this };
        if (dlg.ShowDialog() == true)
            _cm.Connect(mode, dlg.Ssid, dlg.Password);
    }

    private void DoDisconnect()
    {
        if (!_cm.IsConnected && _cm.State != ConnectionState.Error) return;
        _cm.Disconnect();
    }

    private void OnStateChanged(ConnectionState state)
    {
        switch (state)
        {
            case ConnectionState.Connected:
                StatusValue.Text = "● ACTIVE";
                StatusValue.Foreground = Brush("B_Green");
                HeaderStatus.Text = "[CONNECTED]";
                HeaderStatus.Foreground = Brush("B_Green");
                InterfaceValue.Text = _cm.CurrentInterface?.Alias ?? "--";
                EndpointValue.Text = _cm.CurrentMode switch
                {
                    ConnectionMode.Usb => $"{_config.ProxyIp}:{_config.ProxyPort}",
                    ConnectionMode.AdbAgent => $"adb :{_config.AgentPort} (VPN)",
                    _ => _cm.CurrentSsid ?? "WiFi"
                };
                StealthValue.Text = _cm.StealthStatusString();
                StealthValue.Foreground = _cm.BypassActive ? Brush("B_Green") : Brush("B_TextGray");
                ShowNotification("Connected", $"{_cm.CurrentMode.Display()} connection active");
                break;

            case ConnectionState.Connecting:
                StatusValue.Text = "◐ CONNECTING";
                StatusValue.Foreground = Brush("B_Orange");
                HeaderStatus.Text = "[CONNECTING]";
                HeaderStatus.Foreground = Brush("B_Orange");
                break;

            case ConnectionState.Disconnecting:
                StatusValue.Text = "◐ DISCONNECTING";
                StatusValue.Foreground = Brush("B_Orange");
                HeaderStatus.Text = "[DISCONNECTING]";
                HeaderStatus.Foreground = Brush("B_Orange");
                break;

            case ConnectionState.Error:
                StatusValue.Text = "● ERROR";
                StatusValue.Foreground = Brush("B_Red");
                HeaderStatus.Text = "[ERROR]";
                HeaderStatus.Foreground = Brush("B_Red");
                break;

            default: // Disconnected
                StatusValue.Text = "● INACTIVE";
                StatusValue.Foreground = Brush("B_Red");
                HeaderStatus.Text = "[DISCONNECTED]";
                HeaderStatus.Foreground = Brush("B_TextGray");
                InterfaceValue.Text = "NOT DETECTED";
                StealthValue.Text = "DISABLED";
                StealthValue.Foreground = Brush("B_TextGray");
                _dataWarned = false;
                ShowNotification("Disconnected", "Tethering stopped");
                break;
        }

        UpdateButtons(state);
        UpdateTrayIcon(state);
        SbStatus.Text = $"SYS: {(state == ConnectionState.Connected ? "ACTIVE" : state.ToString().ToUpperInvariant())}";
    }

    private void OnError(string message)
    {
        ShowNotification("Error", message);
    }

    private void UpdateButtons(ConnectionState state)
    {
        bool connected = state == ConnectionState.Connected;
        bool busy = state is ConnectionState.Connecting or ConnectionState.Disconnecting;
        ConnectBtn.IsEnabled = !connected && !busy;
        DisconnectBtn.IsEnabled = connected || state == ConnectionState.Error;
        ModeCombo.IsEnabled = !connected && !busy;
    }

    // ======================= Metrics =======================
    private void OnUiTick()
    {
        HeaderClock.Text = DateTime.Now.ToString("HH:mm:ss");

        // Periodic latency probe while connected (off the UI thread).
        if (_cm.IsConnected && (_pingCounter++ % 3 == 0))
            Task.Run(() => _stats.PingTest());

        UpdateMetrics();
    }

    private void UpdateMetrics()
    {
        bool connected = _cm.IsConnected;

        DownValue.Text = Fmt.Bandwidth(connected ? _stats.DownloadRate : 0);
        UpValue.Text = Fmt.Bandwidth(connected ? _stats.UploadRate : 0);
        LatencyValue.Text = _stats.Latency.HasValue ? $"{_stats.Latency:0} ms" : "-- ms";
        LossValue.Text = $"{_stats.PacketLoss:0} %";
        TotalValue.Text = $"↓ {Fmt.Bytes(_stats.TotalDownloaded)}  ↑ {Fmt.Bytes(_stats.TotalUploaded)}";

        SessionDown.Text = Fmt.Bytes(_stats.TotalDownloaded);
        SessionUp.Text = Fmt.Bytes(_stats.TotalUploaded);
        SessionTotal.Text = Fmt.Bytes(_stats.TotalDownloaded + _stats.TotalUploaded);

        var up = Fmt.Uptime(_stats.UptimeSeconds);
        UptimeValue.Text = up;

        // Quality
        int q = connected ? _stats.Quality() : 0;
        QualityBar.Value = q;
        if (!connected) { QualityValue.Text = "● UNKNOWN"; QualityValue.Foreground = Brush("B_TextGray"); }
        else if (q >= Constants.QualityExcellent) { QualityValue.Text = "● EXCELLENT"; QualityValue.Foreground = Brush("B_Green"); }
        else if (q >= Constants.QualityGood) { QualityValue.Text = "● GOOD"; QualityValue.Foreground = Brush("B_GreenDim"); }
        else if (q >= Constants.QualityFair) { QualityValue.Text = "● FAIR"; QualityValue.Foreground = Brush("B_Orange"); }
        else { QualityValue.Text = "● POOR"; QualityValue.Foreground = Brush("B_Red"); }

        // Status bar
        SbNetwork.Text = $"NET: {Fmt.Bandwidth(connected ? _stats.DownloadRate + _stats.UploadRate : 0)}";
        SbStealth.Text = $"STEALTH: {(_cm.BypassActive ? "ON" : "OFF")}";
        SbUptime.Text = $"UPTIME: {up}";

        UpdateGraph(connected);
        CheckDataWarning();
    }

    private void UpdateGraph(bool connected)
    {
        _downHist.Add(connected ? _stats.DownloadRate : 0);
        _upHist.Add(connected ? _stats.UploadRate : 0);
        while (_downHist.Count > GraphPoints) _downHist.RemoveAt(0);
        while (_upHist.Count > GraphPoints) _upHist.RemoveAt(0);

        double w = BandwidthCanvas.ActualWidth;
        double h = BandwidthCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double max = Math.Max(1, Math.Max(_downHist.DefaultIfEmpty(0).Max(), _upHist.DefaultIfEmpty(0).Max()));
        DownLine.Points = BuildPoints(_downHist, w, h, max);
        UpLine.Points = BuildPoints(_upHist, w, h, max);
    }

    private static PointCollection BuildPoints(List<double> data, double w, double h, double max)
    {
        var pts = new PointCollection();
        if (data.Count < 2) return pts;
        double stepX = w / (GraphPoints - 1);
        for (int i = 0; i < data.Count; i++)
        {
            double x = i * stepX;
            double y = h - (data[i] / max) * (h - 2) - 1;
            pts.Add(new System.Windows.Point(x, y));
        }
        return pts;
    }

    private void CheckDataWarning()
    {
        if (_dataWarned || !_cm.IsConnected) return;
        long totalMb = (_stats.TotalDownloaded + _stats.TotalUploaded) / (1024 * 1024);
        if (totalMb >= _config.DataWarningMb)
        {
            _dataWarned = true;
            _log.Warning($"Data usage warning: {totalMb} MB exceeds threshold of {_config.DataWarningMb} MB");
            ShowNotification("Data Usage Warning", $"{totalMb} MB used this session");
        }
    }

    // ======================= Log panel =======================
    private void OnLogEntry(LogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _logs.Add(entry);
            while (_logs.Count > Constants.LogBufferMaxSize) _logs.RemoveAt(0);
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        });
    }

    private bool LogFilter(object obj)
    {
        if (obj is not LogEntry entry) return false;
        if (FilterErrors.IsChecked == true &&
            entry.Level is not ("ERROR" or "CRITICAL" or "WARN"))
            return false;
        var q = LogSearch.Text?.Trim();
        if (!string.IsNullOrEmpty(q))
            return entry.Display.Contains(q, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private void OnLogSearchChanged(object sender, TextChangedEventArgs e) => _logView?.Refresh();
    private void OnLogFilterChanged(object sender, RoutedEventArgs e) => _logView?.Refresh();

    private void OnCopyLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = string.Join(Environment.NewLine, _logs.Select(l => l.Display));
            System.Windows.Clipboard.SetText(text);
            _log.Ok("Logs copied to clipboard");
        }
        catch (Exception ex) { _log.Error($"Copy failed: {ex.Message}"); }
    }

    private void OnExportLogs(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"pdanet-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                File.WriteAllText(dlg.FileName, string.Join(Environment.NewLine, _logs.Select(l => l.Display)));
                _log.Ok($"Logs exported to {dlg.FileName}");
            }
            catch (Exception ex) { _log.Error($"Export failed: {ex.Message}"); }
        }
    }

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        _logs.Clear();
        _log.ClearBuffer();
    }

    // ======================= Profiles =======================
    private void LoadProfiles()
    {
        ProfilesCombo.Items.Clear();
        ProfilesCombo.Items.Add("— none —");
        foreach (var p in ConfigManager.Instance.Profiles)
            ProfilesCombo.Items.Add(p.Name);
        ProfilesCombo.SelectedIndex = 0;
    }

    private void OnRefreshProfiles(object sender, RoutedEventArgs e) => LoadProfiles();

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesCombo.SelectedIndex <= 0) { _selectedProfile = null; return; }
        var name = ProfilesCombo.SelectedItem as string;
        _selectedProfile = ConfigManager.Instance.Profiles.FirstOrDefault(p => p.Name == name);
        if (_selectedProfile != null)
        {
            ModeCombo.SelectedIndex = ConnectionModeExtensions.FromKey(_selectedProfile.Mode) switch
            {
                ConnectionMode.Usb => 0,
                ConnectionMode.Iphone => 2,
                _ => 1
            };
        }
    }

    // ======================= Toggles =======================
    private void OnAutoReconnectToggled(object sender, RoutedEventArgs e)
    {
        _config.AutoReconnect = AutoReconnectSwitch.IsChecked == true;
        _cm.AutoReconnectEnabled = _config.AutoReconnect;
        ConfigManager.Instance.SaveConfig();
        _log.Info($"Auto-reconnect {( _config.AutoReconnect ? "enabled" : "disabled")}");
    }

    private void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        if (AutostartSwitch.IsChecked == true) AutoStart.Enable();
        else AutoStart.Disable();
        _config.AutoStart = AutostartSwitch.IsChecked == true;
        ConfigManager.Instance.SaveConfig();
    }

    // ======================= Dialogs =======================
    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _log.SetLevel(_config.LogLevel);
            EndpointValue.Text = $"{_config.ProxyIp}:{_config.ProxyPort}";
            WarningValue.Text = $"{_config.DataWarningMb} MB";
            StealthStatus.Text = _config.StealthMode ? "[AUTO]" : "[OFF]";
            AutoReconnectSwitch.IsChecked = _config.AutoReconnect;
            _cm.AutoReconnectEnabled = _config.AutoReconnect;
        }
    }

    private void OnHistoryClicked(object sender, RoutedEventArgs e)
    {
        new HistoryWindow { Owner = this }.ShowDialog();
    }

    private void OnSpeedTestClicked(object sender, RoutedEventArgs e)
    {
        new SpeedTestDialog { Owner = this }.ShowDialog();
    }

    // ======================= System tray =======================
    private void SetupTray()
    {
        _iconRed = MakeDotIcon(Drawing.Color.FromArgb(255, 0, 0));
        _iconOrange = MakeDotIcon(Drawing.Color.FromArgb(255, 165, 0));
        _iconGreen = MakeDotIcon(Drawing.Color.FromArgb(0, 255, 0));

        _tray = new WinForms.NotifyIcon
        {
            Text = "PdaNet Windows — Disconnected",
            Icon = _iconRed,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowFromTray());
        menu.Items.Add("Connect", null, (_, _) => DoConnect());
        menu.Items.Add("Disconnect", null, (_, _) => DoDisconnect());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
    }

    private void UpdateTrayIcon(ConnectionState state)
    {
        if (_tray == null) return;
        _tray.Icon = state switch
        {
            ConnectionState.Connected => _iconGreen,
            ConnectionState.Connecting or ConnectionState.Disconnecting => _iconOrange,
            _ => _iconRed
        };
        _tray.Text = $"PdaNet Windows — {state}";
    }

    private static Drawing.Icon MakeDotIcon(Drawing.Color color)
    {
        using var bmp = new Drawing.Bitmap(16, 16);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var brush = new Drawing.SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 12, 12);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void ShowNotification(string title, string message)
    {
        if (!_config.EnableNotifications || _tray == null) return;
        try { _tray.ShowBalloonTip(5000, title, message, WinForms.ToolTipIcon.Info); } catch { }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        ShowInTaskbar = true;
    }

    private void HideToTray(bool initial = false)
    {
        Hide();
        ShowInTaskbar = false;
        if (!initial) ShowNotification("PdaNet Windows", "Still running in the system tray");
    }

    // ======================= Shortcuts =======================
    private void SetupShortcuts()
    {
        KeyDown += (_, e) =>
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (ctrl && e.Key == Key.C) DoConnect();
            else if (ctrl && e.Key == Key.D) DoDisconnect();
            else if (ctrl && e.Key == Key.Q) ExitApp();
            else if (ctrl && e.Key == Key.S) OnSettingsClicked(this, new RoutedEventArgs());
            else if (e.Key == Key.F5) LoadProfiles();
        };
    }

    // ======================= Lifecycle =======================
    private void ExitApp()
    {
        _reallyExit = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // Real shutdown: restore the system proxy (unprivileged) so browsing is not left broken.
        try { SystemProxy.ClearWinInet(); } catch { }
        if (_cm.IsConnected) _cm.Disconnect(); // best-effort teardown (may prompt for bypass removal)

        _config.WindowWidth = (int)Width;
        _config.WindowHeight = (int)Height;
        try { ConfigManager.Instance.SaveConfig(); } catch { }

        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnClosing(e);
        System.Windows.Application.Current.Shutdown();
    }

    private System.Windows.Media.Brush Brush(string key) =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[key];
}
