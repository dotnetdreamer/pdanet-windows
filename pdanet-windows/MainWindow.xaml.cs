using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PdaNetPortable.Core;
using PdaNetPortable.Interop;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
using PdaNetPortable.Views;
using Application = System.Windows.Application;
using Drawing = System.Drawing;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using WinForms = System.Windows.Forms;

namespace PdaNetPortable;

public enum ToastKind { Success, Info, Warning, Error }

public enum AppPage { Connect, Activity, History, Settings, Help }

/// <summary>
/// The app shell: custom title bar, navigation rail, page host, in window sheets, toasts,
/// status strip and the tray icon. Pages talk back to it through the public methods below.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ConnectionManager _cm = ConnectionManager.Instance;
    private readonly AppConfig _config = ConfigManager.Instance.Config;
    private readonly Logger _log = Logger.Instance;
    private readonly StatsCollector _stats = StatsCollector.Instance;

    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private WinForms.NotifyIcon? _tray;
    private Drawing.Icon? _iconOff, _iconBusy, _iconOn, _iconError;
    private FrameworkElement _currentPage;
    private IInputElement? _focusBeforeSheet;
    private ConnectionState _lastState = ConnectionState.Disconnected;
    private bool _reallyExit, _wasOnline, _dataWarned, _dataCritical, _trayHintShown, _locByConnect;
    private int _pingCounter;

    // Screenshot only demo state (see Demo), never set during normal use.
    private ConnectionState? _demoState;
    private ConnectionMode _demoMode = ConnectionMode.AdbAgent;
    private int _demoTick;

    public MainWindow()
    {
        InitializeComponent();
        FitToScreen();
        VersionText.Text = $"v{Constants.AppVersion}";

        ConnectPage.Attach(this);
        ActivityPage.Attach(this);
        HistoryPage.Attach(this);
        SettingsPage.Attach(this);
        HelpPage.Attach(this);
        _currentPage = ConnectPage;
        OnModeChanged(ConnectPage.SelectedMode);

        _log.EntryAdded += OnLogEntry;
        _cm.StateChanged += s => Dispatcher.Invoke(() => OnConnectionStateChanged(s));
        _cm.ErrorOccurred += m => Dispatcher.Invoke(() => OnError(m));
        _cm.AutoReconnectEnabled = _config.AutoReconnect;

        _uiTimer.Tick += (_, _) => OnUiTick();
        _uiTimer.Start();

        SetupTray();
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => NativeMethods.StyleWindow(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            ApplyState(_cm.State);
            OnUiTick();
            if (Demo.Active) Dispatcher.BeginInvoke(ApplyDemo, DispatcherPriority.Loaded);
            else if (Program.StartMinimizedRequested || _config.StartMinimized)
                HideToTray(initial: true);
        };

        _log.Info("Window ready");
    }

    private void FitToScreen()
    {
        int w = _config.WindowWidth, h = _config.WindowHeight;
        if (w == 900 && h == 620)
        {
            // Size saved by the previous layout, the new one needs more room.
            w = Constants.MainWindowWidth;
            h = Constants.MainWindowHeight;
        }
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(w, area.Width - 32));
        Height = Math.Max(MinHeight, Math.Min(h, area.Height - 32));
    }

    // ============================ Navigation ============================
    public void Navigate(AppPage page)
    {
        FrameworkElement target = page switch
        {
            AppPage.Activity => ActivityPage,
            AppPage.History => HistoryPage,
            AppPage.Settings => SettingsPage,
            AppPage.Help => HelpPage,
            _ => ConnectPage,
        };
        RadioButton nav = page switch
        {
            AppPage.Activity => NavActivity,
            AppPage.History => NavHistory,
            AppPage.Settings => NavSettings,
            AppPage.Help => NavHelp,
            _ => NavConnect,
        };
        nav.IsChecked = true;
        if (ReferenceEquals(_currentPage, target)) return;

        foreach (UIElement child in PageHost.Children)
            child.Visibility = ReferenceEquals(child, target) ? Visibility.Visible : Visibility.Collapsed;
        _currentPage = target;
        Ui.Enter(target, rise: 12, ms: 260);
        (target as IPage)?.OnShown();
    }

    private void OnNav(object sender, RoutedEventArgs e) => Navigate(
        sender == NavActivity ? AppPage.Activity
        : sender == NavHistory ? AppPage.History
        : sender == NavSettings ? AppPage.Settings
        : sender == NavHelp ? AppPage.Help
        : AppPage.Connect);

    // ======================== Connection actions ========================
    public void RequestConnect()
    {
        if (SheetOpen) return;
        if (_cm.State is ConnectionState.Connected or ConnectionState.Connecting or ConnectionState.Disconnecting) return;

        var mode = ConnectPage.SelectedMode;
        _config.ConnectionMode = mode.ToKey();
        ConfigManager.Instance.SaveConfig();

        if (mode is ConnectionMode.Usb or ConnectionMode.AdbAgent)
        {
            _cm.Connect(mode);
            return;
        }

        // WiFi and iPhone: ask which hotspot to join, in a sheet over the window.
        var sheet = new WifiSheet(mode);
        sheet.Submitted += (ssid, password) =>
        {
            CloseSheet();
            _cm.Connect(mode, ssid, password);
        };
        sheet.Cancelled += CloseSheet;
        ShowSheet(sheet);
    }

    public void RequestDisconnect()
    {
        if (!_cm.IsConnected && _cm.State != ConnectionState.Error) return;
        _cm.Disconnect();
    }

    public void ToggleConnection()
    {
        if (_cm.IsConnected) RequestDisconnect();
        else RequestConnect();
    }

    public void OpenSpeedTest()
    {
        if (SheetOpen) return;
        var sheet = new SpeedTestSheet();
        sheet.Closed += CloseSheet;
        ShowSheet(sheet);
    }

    public void OpenLocation()
    {
        if (SheetOpen) return;
        var sheet = new LocationSheet();
        sheet.Closed += () => { CloseSheet(); SettingsPage.RefreshLocation(); };
        ShowSheet(sheet);
    }

    public void OnModeChanged(ConnectionMode mode) => SbMode.Text = Friendly.ModeName(mode).ToUpperInvariant();

    public void OnSettingsSaved()
    {
        _log.SetLevel(_config.LogLevel);
        _cm.AutoReconnectEnabled = _config.AutoReconnect;
        OnUiTick();
    }

    // ============================ State ============================
    private void OnConnectionStateChanged(ConnectionState state)
    {
        _lastState = state;
        ApplyState(state);

        switch (state)
        {
            case ConnectionState.Connected:
                _wasOnline = true;
                Notify("You are online", $"Connected with {Friendly.ModeName(_cm.CurrentMode)}");
                if (!ReferenceEquals(_currentPage, ConnectPage))
                    Toast($"You are online with {Friendly.ModeName(_cm.CurrentMode)}");
                if (_config.LocationAutoOnConnect && !LocationMask.Active)
                {
                    _locByConnect = true;
                    Task.Run(() => LocationMask.Apply(LocationMask.Default, _config.LocationMatchRegion, out _));
                }
                break;

            case ConnectionState.Disconnected:
                _dataWarned = _dataCritical = false;
                if (_wasOnline)
                {
                    _wasOnline = false;
                    Notify("You are offline", "Your network settings are back to normal");
                }
                if (_locByConnect)
                {
                    _locByConnect = false;
                    Task.Run(() => LocationMask.Restore());
                }
                break;
        }
    }

    private void ApplyState(ConnectionState state)
    {
        ConnectPage.ApplyState(state);
        ApplyChrome(state);
    }

    /// <summary>Updates the title bar pill, status strip, ambient glow and tray for a state.</summary>
    private void ApplyChrome(ConnectionState state, ConnectionMode? mode = null)
    {
        if (mode.HasValue) OnModeChanged(mode.Value);

        var (key, text) = state switch
        {
            ConnectionState.Connected => ("B.Accent", "Online"),
            ConnectionState.Connecting => ("B.Amber", "Connecting"),
            ConnectionState.Disconnecting => ("B.Amber", "Disconnecting"),
            ConnectionState.Error => ("B.Red", "Needs attention"),
            _ => ("B.Text3", "Offline"),
        };
        var brush = Ui.Brush(key);
        var color = ((SolidColorBrush)brush).Color;
        bool busy = state is ConnectionState.Connecting or ConnectionState.Disconnecting;

        PillDot.Fill = brush;
        PillText.Text = text;
        PillTime.Visibility = state == ConnectionState.Connected ? Visibility.Visible : Visibility.Collapsed;
        StatusPill.Background = new SolidColorBrush(Ui.WithAlpha(color, 0x18));
        StatusPill.BorderBrush = new SolidColorBrush(Ui.WithAlpha(color, 0x55));
        PillDot.BeginAnimation(OpacityProperty, busy
            ? new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);

        SbDot.Fill = brush;
        SbState.Text = text.ToUpperInvariant();
        SbState.Foreground = state == ConnectionState.Disconnected ? Ui.Brush("B.Text3") : brush;

        var glow = state == ConnectionState.Disconnected ? Ui.Color("C.Accent") : color;
        AmbientStop.Color = Ui.WithAlpha(glow, state switch
        {
            ConnectionState.Connected => (byte)0x1E,
            ConnectionState.Disconnected => (byte)0x0C,
            _ => (byte)0x16,
        });

        UpdateTrayIcon(state);
    }

    private void OnError(string message)
    {
        var f = Friendly.Explain(message, _cm.CurrentMode);
        Notify(f.Title, f.Steps.FirstOrDefault() ?? f.Summary);
        if (!ReferenceEquals(_currentPage, ConnectPage) || _cm.State != ConnectionState.Connecting)
            Toast(f.Title, ToastKind.Error);
    }

    // ======================= Screenshot only demo =======================
    private void ApplyDemo()
    {
        var nav = Demo.Value("nav");
        if (!string.IsNullOrEmpty(nav))
            Navigate(Enum.TryParse<AppPage>(nav, true, out var p) ? p : AppPage.Connect);

        var sheet = Demo.Value("sheet");
        if (sheet == "wifi") RequestConnectDemo(ConnectionMode.Wifi);
        else if (sheet == "iphone") RequestConnectDemo(ConnectionMode.Iphone);
        else if (sheet == "speed") OpenSpeedTest();
        else if (sheet == "location") OpenLocation();

        var state = Demo.Value("state");
        if (!string.IsNullOrEmpty(state))
        {
            _demoMode = Demo.Value("mode") switch
            {
                "usb" => ConnectionMode.Usb,
                "wifi" => ConnectionMode.Wifi,
                "iphone" => ConnectionMode.Iphone,
                _ => ConnectionMode.AdbAgent,
            };
            _demoState = state.ToLowerInvariant() switch
            {
                "connected" or "online" => ConnectionState.Connected,
                "connecting" => ConnectionState.Connecting,
                "error" => ConnectionState.Error,
                "disconnecting" => ConnectionState.Disconnecting,
                _ => ConnectionState.Disconnected,
            };
            string? ssid = _demoMode is ConnectionMode.Wifi or ConnectionMode.Iphone ? "Galaxy S24 5G" : null;
            ConnectPage.DemoState(_demoState.Value, _demoMode, ssid);
            ApplyChrome(_demoState.Value, _demoMode);
            if (_demoState == ConnectionState.Connecting) SeedConnectingLog(_demoMode);
            OnUiTick();
        }
    }

    private void RequestConnectDemo(ConnectionMode mode)
    {
        var sheet = new WifiSheet(mode);
        sheet.Cancelled += CloseSheet;
        ShowSheet(sheet);
    }

    private void SeedConnectingLog(ConnectionMode mode)
    {
        var steps = Friendly.ConnectSteps(mode);
        int shown = Math.Min(steps.Count, mode == ConnectionMode.AdbAgent ? 3 : 2);
        for (int i = 0; i < shown; i++)
            ConnectPage.OnLog(new LogEntry(DateTime.Now.ToString("HH:mm:ss"), "INFO", $"[{i + 1}/{steps.Count}] {steps[i]}"));
    }

    private Telemetry DemoTelemetry()
    {
        _demoTick++;
        bool online = _demoState == ConnectionState.Connected;
        // A gentle sine wave so the chart and numbers look alive in screenshots.
        double wave = (Math.Sin(_demoTick / 4.0) + 1) / 2;
        double down = online && _demoMode != ConnectionMode.AdbAgent ? (2.2 + wave * 3.0) * 1024 * 1024 : 0;
        double up = online && _demoMode != ConnectionMode.AdbAgent ? (0.3 + wave * 0.5) * 1024 * 1024 : 0;
        return new Telemetry(online, _demoMode, down, up,
            (long)(1.84 * 1024 * 1024 * 1024), (long)(236.0 * 1024 * 1024),
            online ? 1_000 + _demoTick : 0, online ? 34 : null, online ? 96 : 0);
    }

    // ============================ Live data ============================
    private void OnUiTick()
    {
        SbClock.Text = DateTime.Now.ToString("HH:mm:ss");

        if (_demoState.HasValue)
        {
            var dt = DemoTelemetry();
            ConnectPage.UpdateTelemetry(dt);
            long demoTotal = dt.Connected ? dt.TotalDown + dt.TotalUp : 0;
            bool demoPhone = dt.Connected && _demoMode == ConnectionMode.AdbAgent;
            PillTime.Text = dt.Connected ? Fmt.Uptime(dt.Uptime) : "";
            SbDown.Text = Fmt.Bandwidth(dt.Down);
            SbUp.Text = Fmt.Bandwidth(dt.Up);
            SbStealth.Text = !dt.Connected ? "STEALTH IDLE" : demoPhone ? "STEALTH VIA PHONE" : "STEALTH ON";
            SbData.Text = demoPhone ? "DATA COUNTED ON PHONE" : $"DATA {Fmt.Bytes(demoTotal)}";
            return;
        }

        bool connected = _cm.IsConnected;
        bool phone = connected && _cm.CurrentMode == ConnectionMode.AdbAgent;

        // Latency probe every few seconds. Skipped in Phone VPN mode, where a ping from the PC
        // would not travel through the phone and would give a misleading number.
        if (connected && !phone && _pingCounter++ % 3 == 0)
            Task.Run(() => _stats.PingTest());

        var t = new Telemetry(
            connected,
            _cm.CurrentMode,
            connected ? _stats.DownloadRate : 0,
            connected ? _stats.UploadRate : 0,
            _stats.TotalDownloaded,
            _stats.TotalUploaded,
            connected ? _stats.UptimeSeconds : 0,
            connected ? _stats.Latency : null,
            connected ? _stats.Quality() : 0);
        ConnectPage.UpdateTelemetry(t);

        long total = connected ? t.TotalDown + t.TotalUp : 0;
        PillTime.Text = connected ? Fmt.Uptime(t.Uptime) : "";
        SbDown.Text = Fmt.Bandwidth(t.Down);
        SbUp.Text = Fmt.Bandwidth(t.Up);
        SbStealth.Text = !connected ? "STEALTH IDLE"
            : phone ? "STEALTH VIA PHONE"
            : _cm.BypassActive ? "STEALTH ON" : "STEALTH OFF";
        SbData.Text = phone ? "DATA COUNTED ON PHONE" : $"DATA {Fmt.Bytes(total)}";

        if (connected && !phone) CheckDataUse(total);
        if (_tray != null) _tray.Text = TrayText();
    }

    private void CheckDataUse(long totalBytes)
    {
        long mb = totalBytes / (1024 * 1024);
        if (!_dataWarned && mb >= _config.DataWarningMb)
        {
            _dataWarned = true;
            _log.Warning($"Data alert, {Fmt.Bytes(totalBytes)} used this session");
            Notify("Data alert", $"You have used {Fmt.Bytes(totalBytes)} this session");
            Toast($"Data alert, {Fmt.Bytes(totalBytes)} used this session", ToastKind.Warning);
        }
        if (!_dataCritical && mb >= _config.DataCriticalMb)
        {
            _dataCritical = true;
            _log.Warning($"Data limit reached, {Fmt.Bytes(totalBytes)} used this session");
            Notify("Data limit reached", $"You have used {Fmt.Bytes(totalBytes)} this session");
            Toast($"Data limit reached, {Fmt.Bytes(totalBytes)} used", ToastKind.Error);
        }
    }

    private void OnLogEntry(LogEntry entry) => Dispatcher.BeginInvoke(() =>
    {
        ActivityPage.Append(entry);
        ConnectPage.OnLog(entry);
    });

    // ============================== Toasts ==============================
    public void Toast(string text, ToastKind kind = ToastKind.Success)
    {
        var (key, glyph) = kind switch
        {
            ToastKind.Info => ("B.Cyan", ""),
            ToastKind.Warning => ("B.Amber", ""),
            ToastKind.Error => ("B.Red", ""),
            _ => ("B.Accent", ""),
        };
        var accent = Ui.Brush(key);

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)FindResource("F.Icon"),
            FontSize = 14,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = Friendly.Clean(text),
            FontSize = 13,
            Foreground = Ui.Brush("B.Text"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(label);

        // Shadow lives on its own layer so the text keeps crisp ClearType rendering.
        var shadow = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Ui.Brush("B.Bg2"),
            Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Opacity = 0.6, Color = Colors.Black },
        };
        var body = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Ui.Brush("B.Bg2"),
            BorderBrush = new SolidColorBrush(Ui.WithAlpha(((SolidColorBrush)accent).Color, 0x55)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 11, 16, 11),
            Child = row,
        };
        var card = new Grid { Margin = new Thickness(0, 0, 0, 8), Cursor = System.Windows.Input.Cursors.Hand };
        card.Children.Add(shadow);
        card.Children.Add(body);

        var slide = new TranslateTransform(28, 0);
        card.RenderTransform = slide;
        ToastHost.Children.Insert(0, card);
        while (ToastHost.Children.Count > 3) ToastHost.Children.RemoveAt(ToastHost.Children.Count - 1);

        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = Ui.EaseOut });

        void Dismiss()
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) => ToastHost.Children.Remove(card);
            card.BeginAnimation(OpacityProperty, fade);
        }

        card.MouseLeftButtonUp += (_, _) => Dismiss();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 6 : 3.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Dismiss();
        };
        timer.Start();
    }

    // ============================== Sheets ==============================
    public bool SheetOpen => OverlayHost.Visibility == Visibility.Visible;

    public void ShowSheet(FrameworkElement sheet)
    {
        _focusBeforeSheet = Keyboard.FocusedElement;
        OverlaySlot.Content = sheet;
        OverlayHost.Visibility = Visibility.Visible;
        PageHost.Effect = new BlurEffect { Radius = 6 };
        OverlayHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

        var scale = new ScaleTransform(0.96, 0.96);
        sheet.RenderTransformOrigin = new Point(0.5, 0.5);
        sheet.RenderTransform = scale;
        var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ui.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => (sheet as ISheet)?.FocusFirst());
    }

    public void CloseSheet()
    {
        if (!SheetOpen) return;
        (OverlaySlot.Content as ISheet)?.OnClosing();
        OverlaySlot.Content = null;
        OverlayHost.Visibility = Visibility.Collapsed;
        PageHost.Effect = null;
        _focusBeforeSheet?.Focus();
        _focusBeforeSheet = null;
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e) => CloseSheet();

    // ============================ Keyboard ============================
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape && SheetOpen) { CloseSheet(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Q) { ExitApp(); e.Handled = true; return; }
        if (SheetOpen) return;

        if (ctrl && e.Key == Key.Enter) ToggleConnection();
        else if (ctrl && e.Key == Key.D) RequestDisconnect();
        else if (ctrl && e.Key is Key.D1 or Key.NumPad1) Navigate(AppPage.Connect);
        else if (ctrl && e.Key is Key.D2 or Key.NumPad2) Navigate(AppPage.Activity);
        else if (ctrl && e.Key is Key.D3 or Key.NumPad3) Navigate(AppPage.History);
        else if (ctrl && e.Key is Key.D4 or Key.NumPad4) Navigate(AppPage.Settings);
        else if (ctrl && e.Key is Key.D5 or Key.NumPad5) Navigate(AppPage.Help);
        else if (ctrl && e.Key == Key.S)
        {
            if (ReferenceEquals(_currentPage, SettingsPage)) SettingsPage.Save();
            else Navigate(AppPage.Settings);
        }
        else if (e.Key == Key.F1) Navigate(AppPage.Help);
        else if (e.Key == Key.F5) (_currentPage as IPage)?.Refresh();
        else return;
        e.Handled = true;
    }

    // ============================ Window chrome ============================
    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool max = WindowState == WindowState.Maximized;
        // A maximized custom chrome window hangs over the screen edge by its resize frame.
        var t = SystemParameters.WindowResizeBorderThickness;
        Frame.Margin = max ? new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4) : new Thickness(0);
        Frame.BorderThickness = new Thickness(max ? 0 : 1);
        MaxBtn.Content = max ? "" : "";
        MaxBtn.ToolTip = max ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaxBtn, max ? "Restore" : "Maximize");
    }

    // ============================== Tray ==============================
    private void SetupTray()
    {
        _iconOff = MakeStatusIcon(Drawing.Color.FromArgb(123, 138, 157));
        _iconBusy = MakeStatusIcon(Drawing.Color.FromArgb(255, 182, 77));
        _iconOn = MakeStatusIcon(Drawing.Color.FromArgb(43, 245, 168));
        _iconError = MakeStatusIcon(Drawing.Color.FromArgb(255, 94, 123));

        _tray = new WinForms.NotifyIcon { Text = TrayText(), Icon = _iconOff, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) ShowFromTray();
        };

        var font = new Drawing.Font("Segoe UI", 9.5f);
        var menu = new WinForms.ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = new WinForms.ToolStripProfessionalRenderer(new TrayColors()),
            BackColor = Drawing.Color.FromArgb(16, 22, 29),
            ForeColor = Drawing.Color.FromArgb(231, 237, 244),
            Font = font,
        };
        var open = new WinForms.ToolStripMenuItem("Open PdaNet", null, (_, _) => ShowFromTray())
        {
            Font = new Drawing.Font(font, Drawing.FontStyle.Bold),
        };
        var connect = new WinForms.ToolStripMenuItem("Connect", null, (_, _) =>
        {
            if (ConnectPage.SelectedMode is ConnectionMode.Wifi or ConnectionMode.Iphone) ShowFromTray();
            RequestConnect();
        });
        var disconnect = new WinForms.ToolStripMenuItem("Disconnect", null, (_, _) => RequestDisconnect());
        var quit = new WinForms.ToolStripMenuItem("Quit", null, (_, _) => ExitApp());
        menu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            open, new WinForms.ToolStripSeparator(), connect, disconnect, new WinForms.ToolStripSeparator(), quit,
        });
        foreach (WinForms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = menu.ForeColor;
            item.Padding = new WinForms.Padding(6, 4, 6, 4);
        }
        menu.Opening += (_, _) =>
        {
            connect.Enabled = _cm.State is ConnectionState.Disconnected or ConnectionState.Error;
            disconnect.Enabled = _cm.IsConnected || _cm.State == ConnectionState.Error;
        };
        _tray.ContextMenuStrip = menu;
    }

    private string TrayText()
    {
        var text = _cm.State switch
        {
            ConnectionState.Connected => $"PdaNet Windows · Online {Fmt.Uptime(_stats.UptimeSeconds)}",
            ConnectionState.Connecting => "PdaNet Windows · Connecting",
            ConnectionState.Disconnecting => "PdaNet Windows · Disconnecting",
            ConnectionState.Error => "PdaNet Windows · Needs attention",
            _ => "PdaNet Windows · Offline",
        };
        return text.Length > 63 ? text[..63] : text;
    }

    private void UpdateTrayIcon(ConnectionState state)
    {
        if (_tray == null) return;
        _tray.Icon = state switch
        {
            ConnectionState.Connected => _iconOn,
            ConnectionState.Connecting or ConnectionState.Disconnecting => _iconBusy,
            ConnectionState.Error => _iconError,
            _ => _iconOff,
        };
        _tray.Text = TrayText();
    }

    /// <summary>A small radar style ring with a center dot in the state color.</summary>
    private static Drawing.Icon MakeStatusIcon(Drawing.Color color)
    {
        int s = Math.Max(16, WinForms.SystemInformation.SmallIconSize.Width);
        using var bmp = new Drawing.Bitmap(s, s);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            float stroke = Math.Max(1.6f, s / 9f);
            float inset = stroke / 2 + s * 0.05f;
            using var pen = new Drawing.Pen(color, stroke);
            g.DrawEllipse(pen, inset, inset, s - 2 * inset, s - 2 * inset);
            float r = s * 0.2f;
            using var fill = new Drawing.SolidBrush(color);
            g.FillEllipse(fill, s / 2f - r, s / 2f - r, 2 * r, 2 * r);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void Notify(string title, string message)
    {
        if (!_config.EnableNotifications || _tray == null) return;
        if (IsVisible && IsActive) return; // the window already shows it
        try { _tray.ShowBalloonTip(5000, Friendly.Clean(title), Friendly.Clean(message), WinForms.ToolTipIcon.None); }
        catch { /* notifications are best effort */ }
    }

    public void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void HideToTray(bool initial = false)
    {
        Hide();
        ShowInTaskbar = false;
        if (!initial && !_trayHintShown && _config.EnableNotifications && _tray != null)
        {
            _trayHintShown = true;
            try
            {
                _tray.ShowBalloonTip(4000, "PdaNet is still running",
                    "Find it in the tray near the clock, right click it to quit", WinForms.ToolTipIcon.None);
            }
            catch { }
        }
    }

    // ============================ Lifecycle ============================
    public void ExitApp()
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

        if (WindowState == WindowState.Normal)
        {
            _config.WindowWidth = (int)Width;
            _config.WindowHeight = (int)Height;
        }
        try { ConfigManager.Instance.SaveConfig(); } catch { }

        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnClosing(e);
        Application.Current.Shutdown();
    }

    private sealed class TrayColors : WinForms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Bg = Drawing.Color.FromArgb(16, 22, 29);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(28, 38, 50);
        private static readonly Drawing.Color Line = Drawing.Color.FromArgb(36, 49, 64);

        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color MenuItemPressedGradientBegin => Hover;
        public override Drawing.Color MenuItemPressedGradientEnd => Hover;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Bg;
    }
}
