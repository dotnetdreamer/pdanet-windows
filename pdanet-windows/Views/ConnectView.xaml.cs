using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PdaNetPortable.Core;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
using Brush = System.Windows.Media.Brush;
using CheckState = PdaNetPortable.Services.CheckState;
using Clipboard = System.Windows.Clipboard;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using Rectangle = System.Windows.Shapes.Rectangle;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>
/// Home page. Left column: step 01 (pick a mode) and step 02 (a live checklist that turns
/// into progress, connection details or a fix it guide). Right column: step 03, the power
/// orb with a plain language status line, and live traffic.
/// </summary>
public partial class ConnectView : UserControl, IPage
{
    private const int GraphPoints = 60;
    private static readonly Regex StepRx = new(@"^\[(\d+)/(\d+)\]\s*(.*)$", RegexOptions.Compiled);

    private readonly ConnectionManager _cm = ConnectionManager.Instance;
    private readonly AppConfig _cfg = ConfigManager.Instance.Config;
    private readonly DispatcherTimer _probeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<double> _down = new();
    private readonly List<double> _up = new();

    private MainWindow? _shell;
    private ConnectionState _state = ConnectionState.Disconnected;
    private bool _probing, _probeAgain, _ready;
    private string _checkSignature = "";
    private int _step;

    // Screenshot only demo overrides (see Demo), never set during normal use.
    private bool _demo;
    private ConnectionMode _demoMode;
    private string? _demoSsid;

    public ConnectionMode SelectedMode { get; private set; } = ConnectionMode.AdbAgent;

    private ConnectionMode CurMode => _demo ? _demoMode : _cm.CurrentMode;
    private string? CurSsid => _demo ? _demoSsid : _cm.CurrentSsid;

    public ConnectView()
    {
        InitializeComponent();
    }

    public void Attach(MainWindow shell)
    {
        _shell = shell;
        _state = _cm.State;
        SelectedMode = ConnectionModeExtensions.FromKey(_cfg.ConnectionMode);
        ModeRadio(SelectedMode).IsChecked = true;

        _probeTimer.Tick += (_, _) => Probe();
        _probeTimer.Start();
        IsVisibleChanged += (_, _) =>
        {
            ApplyMotion();
            if (IsVisible) Probe(force: true);
        };
        SizeChanged += (_, _) => LeftCol.Width = new GridLength(ActualWidth < 1000 ? 340 : 390);
        ApplyState(_cm.State);
    }

    public void OnShown() => Probe(force: true);

    public void Refresh()
    {
        Probe(force: true);
        _shell?.Toast("Checking your phone again", ToastKind.Info);
    }

    // ============================== Mode ==============================
    private RadioButton ModeRadio(ConnectionMode mode) => mode switch
    {
        ConnectionMode.Usb => ModeUsb,
        ConnectionMode.Wifi => ModeWifi,
        ConnectionMode.Iphone => ModeIphone,
        _ => ModeAdb,
    };

    private IEnumerable<RadioButton> ModeRadios() => new[] { ModeAdb, ModeUsb, ModeWifi, ModeIphone };

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        var mode = sender == ModeUsb ? ConnectionMode.Usb
                 : sender == ModeWifi ? ConnectionMode.Wifi
                 : sender == ModeIphone ? ConnectionMode.Iphone
                 : ConnectionMode.AdbAgent;
        SelectedMode = mode;

        if (_cfg.ConnectionMode != mode.ToKey())
        {
            _cfg.ConnectionMode = mode.ToKey();
            ConfigManager.Instance.SaveConfig();
        }

        HowToList.ItemsSource = Numbered(Friendly.HowTo(mode));
        _shell?.OnModeChanged(mode);

        if (_state == ConnectionState.Disconnected)
        {
            _checkSignature = "";
            RenderChecklist(Readiness.Pending());
            Probe(force: true);
        }
    }

    // ======================== Readiness checks ========================
    private void Probe(bool force = false)
    {
        if (_state != ConnectionState.Disconnected || !IsVisible) return;
        if (_probing)
        {
            if (force) _probeAgain = true;
            return;
        }

        _probing = true;
        var mode = SelectedMode;
        var adbPath = _cfg.AdbPath;
        Task.Run(() => Readiness.Check(mode, adbPath)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            _probing = false;
            if (t.IsCompletedSuccessfully && mode == SelectedMode && _state == ConnectionState.Disconnected)
                RenderChecklist(t.Result);
            if (_probeAgain)
            {
                _probeAgain = false;
                Probe();
            }
        }));
    }

    private void RenderChecklist(ReadinessReport report)
    {
        var signature = string.Join("|", report.Items.Select(i => $"{i.State}{i.Title}{i.Detail}"));
        _ready = report.Ready;
        if (signature != _checkSignature)
        {
            _checkSignature = signature;
            CheckList.ItemsSource = report.Items.Select(ToRow).ToList();
        }

        bool checking = report.Items.Any(i => i.State == CheckState.Checking);
        ReadyBanner.Visibility = Show(report.Ready && !checking);
        ReadyText.Text = SelectedMode is ConnectionMode.Wifi or ConnectionMode.Iphone
            ? "Ready, press Connect and pick your hotspot"
            : "All set, press Connect";
        UpdateIdleCopy();
    }

    private static CheckRow ToRow(CheckItem i) => i.State switch
    {
        CheckState.Done => new CheckRow("", Ui.Brush("B.Accent"), i.Title, Ui.Brush("B.Text"), i.Detail),
        CheckState.Todo => new CheckRow("", Ui.Brush("B.Amber"), i.Title, Ui.Brush("B.Text"), i.Detail),
        CheckState.Checking => new CheckRow("", Ui.Brush("B.Text3"), i.Title, Ui.Brush("B.Text2"), i.Detail, Spin: true),
        _ => new CheckRow("", Ui.Brush("B.Text3"), i.Title, Ui.Brush("B.Text2"), i.Detail),
    };

    private void UpdateIdleCopy()
    {
        if (_state != ConnectionState.Disconnected) return;
        Headline.Text = "You are not connected";
        SubText.Text = _ready
            ? $"Ready to go online with {Friendly.ModeName(SelectedMode)}"
            : "Follow step 02, then press the button";
    }

    // ============================== State =============================
    public void ApplyState(ConnectionState state)
    {
        var prev = _state;
        _state = state;
        bool busy = state is ConnectionState.Connecting or ConnectionState.Disconnecting;
        bool online = state == ConnectionState.Connected;

        foreach (var rb in ModeRadios()) rb.IsEnabled = !busy && !online;
        if (online || busy || state == ConnectionState.Error)
            ModeRadio(CurMode).IsChecked = true;

        ChecklistPanel.Visibility = Show(state == ConnectionState.Disconnected);
        ProgressPanel.Visibility = Show(busy);
        DetailsPanel.Visibility = Show(online);
        ErrorPanel.Visibility = Show(state == ConnectionState.Error);

        Step1Title.Text = online ? "Connected with" : busy ? "Working with" : "Choose how to connect";
        Step2Title.Text = state switch
        {
            ConnectionState.Connecting => "Getting you online",
            ConnectionState.Connected => "Connection details",
            ConnectionState.Disconnecting => "Disconnecting",
            ConnectionState.Error => "How to fix it",
            _ => "Get your phone ready",
        };
        Step3Title.Text = state switch
        {
            ConnectionState.Connected => "Online",
            ConnectionState.Connecting => "Connecting",
            ConnectionState.Disconnecting => "Disconnecting",
            ConnectionState.Error => "Needs your attention",
            _ => "Connect",
        };

        switch (state)
        {
            case ConnectionState.Connecting:
                Headline.Text = "Getting you online";
                if (prev != ConnectionState.Connecting)
                {
                    _step = 0;
                    SubText.Text = "Starting up";
                }
                RenderProgress();
                break;

            case ConnectionState.Connected:
                Headline.Text = "You are online";
                SubText.Text = Friendly.OnlineSummary(CurMode, CurSsid);
                RenderDetails();
                break;

            case ConnectionState.Disconnecting:
                Headline.Text = "Disconnecting";
                SubText.Text = "Putting your network settings back to normal";
                ProgressList.ItemsSource = new[]
                {
                    new CheckRow("", Ui.Brush("B.Amber"), "Stopping the connection", Ui.Brush("B.Text"), "", Spin: true),
                    new CheckRow("", Ui.Brush("B.Line2"), "Restoring your network settings", Ui.Brush("B.Text3"), ""),
                };
                ProgressHint.Text = "This only takes a moment";
                break;

            case ConnectionState.Error:
                var f = Friendly.Explain(_demo ? "No phone found, plug it in with a USB cable and turn on USB debugging" : _cm.LastError, CurMode);
                Headline.Text = f.Title;
                SubText.Text = f.Summary;
                ErrSummary.Text = f.Summary;
                FixList.ItemsSource = Numbered(f.Steps);
                ErrRaw.Text = string.IsNullOrEmpty(f.Raw) ? "No extra details were recorded" : f.Raw;
                break;

            default:
                _checkSignature = "";
                RenderChecklist(Readiness.Pending());
                Probe(force: true);
                break;
        }

        ApplyMotion();
        if (prev != state && IsVisible)
        {
            UIElement panel = state switch
            {
                ConnectionState.Connected => DetailsPanel,
                ConnectionState.Error => ErrorPanel,
                ConnectionState.Disconnected => ChecklistPanel,
                _ => ProgressPanel,
            };
            Ui.Enter(panel, rise: 8);
        }
    }

    /// <summary>Colors and animations of the orb for the current state. Paused while hidden.</summary>
    private void ApplyMotion()
    {
        bool visible = IsVisible;
        CaretBrush.BeginAnimation(Brush.OpacityProperty, visible ? Ui.Blink() : null);

        switch (_state)
        {
            case ConnectionState.Connecting:
                SetOrb("B.Amber", "CONNECTING", spin: visible, pulse: visible ? 1.6 : 0);
                break;
            case ConnectionState.Disconnecting:
                SetOrb("B.Amber", "STOPPING", spin: visible, pulse: 0);
                break;
            case ConnectionState.Connected:
                SetOrb("B.Accent", "DISCONNECT", spin: false, pulse: visible ? 2.8 : 0, full: true);
                break;
            case ConnectionState.Error:
                SetOrb("B.Red", "TRY AGAIN", spin: false, pulse: 0);
                break;
            default:
                SetOrb("B.Accent", "CONNECT", spin: false, pulse: 0, idle: true);
                break;
        }
    }

    private void SetOrb(string key, string label, bool spin, double pulse, bool full = false, bool idle = false)
    {
        var brush = Ui.Brush(key);
        var color = ((SolidColorBrush)brush).Color;

        OrbIcon.Foreground = brush;
        OrbLabel.Text = label;
        OrbArc.Stroke = brush;
        PulseRing.Stroke = brush;
        OrbTrack.Stroke = full ? new SolidColorBrush(Ui.WithAlpha(color, 0xCC)) : Ui.Brush("B.Line2");
        OrbCore.Stroke = new SolidColorBrush(Ui.WithAlpha(color, idle ? (byte)0x55 : (byte)0x80));
        OrbGlowStop.Color = Ui.WithAlpha(color, full ? (byte)0x40 : idle ? (byte)0x1E : (byte)0x30);

        bool busy = _state is ConnectionState.Connecting or ConnectionState.Disconnecting;
        Orb.Cursor = busy ? Cursors.Arrow : Cursors.Hand;
        AutomationProperties.SetName(Orb, label switch
        {
            "DISCONNECT" => "Disconnect",
            "TRY AGAIN" => "Try again",
            "CONNECT" => "Connect",
            _ => "Working, please wait",
        });

        OrbArc.Visibility = Show(busy);
        ArcRotate.BeginAnimation(RotateTransform.AngleProperty, spin ? Ui.Loop(0, 360, 1.1, 60) : null);

        if (pulse > 0)
        {
            var grow = Ui.Loop(1, 1.32, pulse, 30, Ui.EaseOut);
            PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            PulseRing.BeginAnimation(OpacityProperty, Ui.Loop(0.6, 0, pulse, 30));
        }
        else
        {
            PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PulseRing.BeginAnimation(OpacityProperty, null);
            PulseRing.Opacity = 0;
        }
    }

    // ======================= Progress and details =====================
    /// <summary>Called for every log line; "[n/total]" lines move the progress list on.</summary>
    public void OnLog(LogEntry entry)
    {
        if (_state != ConnectionState.Connecting) return;
        var m = StepRx.Match(entry.Message);
        if (!m.Success) return;
        _step = int.Parse(m.Groups[1].Value);
        SubText.Text = $"Step {_step} of {m.Groups[2].Value} · {Friendly.Clean(m.Groups[3].Value)}";
        RenderProgress();
    }

    private void RenderProgress()
    {
        var steps = Friendly.ConnectSteps(CurMode);
        int current = Math.Max(1, _step);
        var rows = new List<CheckRow>();
        for (int i = 0; i < steps.Count; i++)
        {
            int n = i + 1;
            if (n < current)
                rows.Add(new CheckRow("", Ui.Brush("B.Accent"), steps[i], Ui.Brush("B.Text2"), ""));
            else if (n == current)
                rows.Add(new CheckRow("", Ui.Brush("B.Amber"), steps[i], Ui.Brush("B.Text"), "", Spin: true));
            else
                rows.Add(new CheckRow("", Ui.Brush("B.Line2"), steps[i], Ui.Brush("B.Text3"), ""));
        }
        ProgressList.ItemsSource = rows;
        ProgressHint.Text = CurMode == ConnectionMode.AdbAgent
            ? "The first time can take up to 20 seconds"
            : "This usually takes a few seconds";
    }

    private void RenderDetails()
    {
        var mode = CurMode;
        DetMode.Text = Friendly.ModeName(mode);
        DetNetwork.Text = mode switch
        {
            ConnectionMode.AdbAgent => "Through your phone over USB",
            ConnectionMode.Usb => Friendly.Adapter(_demo ? "Ethernet 3" : _cm.CurrentInterface?.Alias),
            _ => CurSsid ?? Friendly.Adapter(_cm.CurrentInterface?.Alias),
        };

        bool bypass = _demo || _cm.BypassActive;
        bool systemBypass = !_demo && _cm.SystemBypassActive;
        (DetStealth.Text, DetStealthDot.Fill) =
            mode == ConnectionMode.AdbAgent ? ("Yes, your phone does it", Ui.Brush("B.Accent"))
            : !bypass ? ("No, stealth is off", Ui.Brush("B.Text3"))
            : systemBypass ? ($"Yes, every app, level {_cfg.StealthLevel}", Ui.Brush("B.Accent"))
            : ("Yes, for Windows apps", Ui.Brush("B.Accent"));

        var (on, server) = _demo ? (true, $"127.0.0.1:{_cfg.LocalProxyPort}") : SystemProxy.GetWinInet();
        bool hasProxy = on && !string.IsNullOrEmpty(server);
        DetProxy.Text = hasProxy ? server! : "Not used";
        DetProxy.Foreground = hasProxy ? Ui.Brush("B.Text") : Ui.Brush("B.Text3");
        CopyProxyBtn.Visibility = Show(hasProxy);
    }

    /// <summary>Screenshot only: render a state with sample data, with no real connection.</summary>
    public void DemoState(ConnectionState state, ConnectionMode mode, string? ssid = null)
    {
        _demo = true;
        _demoMode = mode;
        _demoSsid = ssid;
        _probeTimer.Stop();
        _step = mode == ConnectionMode.AdbAgent ? 3 : 2;

        // Pre fill the chart so a connected screenshot shows an established trace.
        if (state == ConnectionState.Connected && mode != ConnectionMode.AdbAgent)
        {
            _down.Clear();
            _up.Clear();
            for (int i = 0; i < GraphPoints; i++)
            {
                double w = (Math.Sin(i / 4.0) + 1) / 2;
                _down.Add((2.2 + w * 3.0 + (i % 7) * 0.15) * 1024 * 1024);
                _up.Add((0.3 + w * 0.5) * 1024 * 1024);
            }
        }

        ApplyState(state);
    }

    // ============================ Telemetry ===========================
    public void UpdateTelemetry(Telemetry t)
    {
        if (t.Connected) Step3Title.Text = $"Online for {Fmt.Uptime(t.Uptime)}";

        bool phone = t.Connected && t.Mode == ConnectionMode.AdbAgent;
        LiveStandard.Visibility = Show(!phone);
        LivePhone.Visibility = Show(phone);

        _down.Add(t.Connected ? t.Down : 0);
        _up.Add(t.Connected ? t.Up : 0);
        while (_down.Count > GraphPoints) _down.RemoveAt(0);
        while (_up.Count > GraphPoints) _up.RemoveAt(0);
        if (phone) return;

        string unit;
        (DownValue.Text, unit) = Fmt.BandwidthParts(t.Down);
        DownUnit.Text = " " + unit;
        (UpValue.Text, unit) = Fmt.BandwidthParts(t.Up);
        UpUnit.Text = " " + unit;

        long total = t.Connected ? t.TotalDown + t.TotalUp : 0;
        (DataValue.Text, unit) = Fmt.BytesParts(total);
        DataUnit.Text = " " + unit;
        double pct = _cfg.DataWarningMb > 0 ? total / (1024.0 * 1024) / _cfg.DataWarningMb * 100 : 0;
        DataBar.Value = Math.Min(100, pct);
        DataBar.Foreground = pct >= 100 ? Ui.Brush("B.Red") : pct >= 80 ? Ui.Brush("B.Amber") : Ui.Brush("B.Accent");
        DataCaption.Text = $"Alert at {Fmt.Megabytes(_cfg.DataWarningMb)}";

        var (word, bars) = Friendly.Quality(t.Quality, t.Connected);
        var barBrush = bars >= 3 ? Ui.Brush("B.Accent") : bars == 2 ? Ui.Brush("B.Amber") : Ui.Brush("B.Red");
        for (int i = 0; i < SignalBars.Children.Count; i++)
            ((Rectangle)SignalBars.Children[i]).Fill = i < bars ? barBrush : Ui.Brush("B.Line2");
        SignalText.Text = word;
        SignalText.Foreground = t.Connected ? Ui.Brush("B.Text") : Ui.Brush("B.Text3");
        PingText.Text = t.Latency.HasValue ? $"Ping {t.Latency:0} ms"
            : t.Connected ? "Measuring ping" : "Ping shows once online";

        ChartEmpty.Visibility = Show(!t.Connected);
        DrawChart();
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void DrawChart()
    {
        double w = Chart.ActualWidth, h = Chart.ActualHeight;
        if (w < 10 || h < 10) return;

        double peak = Math.Max(_down.DefaultIfEmpty(0).Max(), _up.DefaultIfEmpty(0).Max());
        double max = Math.Max(48 * 1024, peak * 1.2);
        DownLine.Data = Ui.Smooth(_down, GraphPoints, w, h, max, area: false);
        DownArea.Data = Ui.Smooth(_down, GraphPoints, w, h, max, area: true);
        UpLine.Data = Ui.Smooth(_up, GraphPoints, w, h, max, area: false);
        ChartMax.Text = peak > 0 ? Fmt.Bandwidth(max) : "";
    }

    // ============================= Actions ============================
    private void OnOrbClick(object sender, RoutedEventArgs e)
    {
        switch (_state)
        {
            case ConnectionState.Connected: _shell?.RequestDisconnect(); break;
            case ConnectionState.Disconnected:
            case ConnectionState.Error: _shell?.RequestConnect(); break;
        }
    }

    private void OnRetry(object sender, RoutedEventArgs e) => _shell?.RequestConnect();
    private void OnDismiss(object sender, RoutedEventArgs e) => _shell?.RequestDisconnect();
    private void OnDisconnectClick(object sender, RoutedEventArgs e) => _shell?.RequestDisconnect();
    private void OnSpeedTest(object sender, RoutedEventArgs e) => _shell?.OpenSpeedTest();
    private void OnLocation(object sender, RoutedEventArgs e) => _shell?.OpenLocation();

    private void OnCopyProxy(object sender, RoutedEventArgs e) => CopyText(DetProxy.Text, "Proxy address copied");
    private void OnCopyError(object sender, RoutedEventArgs e) => CopyText(ErrRaw.Text, "Details copied");

    private void CopyText(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            _shell?.Toast(done);
        }
        catch
        {
            _shell?.Toast("Could not reach the clipboard, try again", ToastKind.Error);
        }
    }

    // ============================= Helpers ============================
    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private static List<NumberedRow> Numbered(IEnumerable<string> lines) =>
        lines.Select((l, i) => new NumberedRow((i + 1).ToString("00"), l)).ToList();
}
