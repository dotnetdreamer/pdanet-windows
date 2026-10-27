using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>
/// In window sheet that gathers a hotspot name and password for WiFi and iPhone modes.
/// Scans for nearby networks in the background and remembers saved passwords.
/// </summary>
public partial class WifiSheet : UserControl, ISheet
{
    private readonly ConnectionMode _mode;
    private bool _scanning;

    public event Action<string?, string?>? Submitted;
    public event Action? Cancelled;

    public WifiSheet(ConnectionMode mode)
    {
        InitializeComponent();
        _mode = mode;

        bool iphone = mode == ConnectionMode.Iphone;
        HeaderIcon.Text = iphone ? "" : "";
        Title.Text = iphone ? "Join your iPhone" : "Join your hotspot";
        Subtitle.Text = iphone
            ? "Turn on Personal Hotspot in iPhone settings first"
            : "Keep the hotspot running on your phone";

        var preset = WifiManager.GetCurrentSsid();
        if (!string.IsNullOrEmpty(preset))
        {
            SsidCombo.Items.Add(preset);
            SsidCombo.Text = preset;
            var saved = SecretStore.GetPassword(preset);
            if (!string.IsNullOrEmpty(saved)) Pwd.Password = saved;
        }

        Pwd.KeyDown += OnKeyDown;
        PwdPlain.KeyDown += OnKeyDown;
        Loaded += (_, _) => RunScan();
    }

    public void FocusFirst()
    {
        if (string.IsNullOrEmpty(SsidCombo.Text)) SsidCombo.Focus();
        else Pwd.Focus();
    }

    public void OnClosing() => SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnConnect(sender, e);
    }

    private string CurrentPassword => ShowPwd.IsChecked == true ? PwdPlain.Text : Pwd.Password;

    private void OnTogglePassword(object sender, RoutedEventArgs e)
    {
        if (ShowPwd.IsChecked == true)
        {
            PwdPlain.Text = Pwd.Password;
            PwdPlain.Visibility = Visibility.Visible;
            Pwd.Visibility = Visibility.Collapsed;
            ShowPwd.Content = "";
            PwdPlain.Focus();
            PwdPlain.CaretIndex = PwdPlain.Text.Length;
        }
        else
        {
            Pwd.Password = PwdPlain.Text;
            Pwd.Visibility = Visibility.Visible;
            PwdPlain.Visibility = Visibility.Collapsed;
            ShowPwd.Content = "";
            Pwd.Focus();
        }
    }

    private void OnScan(object sender, RoutedEventArgs e) => RunScan();

    private void RunScan()
    {
        if (_scanning) return;
        _scanning = true;
        ScanBtn.IsEnabled = false;
        StatusPanel.Visibility = Visibility.Visible;
        ScanSpinner.Visibility = Visibility.Visible;
        StatusText.Text = "Looking for nearby hotspots";
        StatusText.Foreground = Ui.Brush("B.Text3");
        StatusText.Margin = new Thickness(8, 0, 0, 0);
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, Ui.Loop(0, 360, 0.9, 60));

        var typed = SsidCombo.Text;
        Task.Run(() => WifiManager.ScanNetworks()).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            _scanning = false;
            ScanBtn.IsEnabled = true;
            ScanSpinner.Visibility = Visibility.Collapsed;
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            StatusText.Margin = new Thickness(0);

            var nets = t.IsCompletedSuccessfully ? t.Result : new List<string>();
            SsidCombo.Items.Clear();
            foreach (var n in nets) SsidCombo.Items.Add(n);
            if (!string.IsNullOrEmpty(typed)) SsidCombo.Text = typed;

            if (nets.Count > 0)
            {
                StatusText.Text = $"Found {nets.Count} nearby, pick yours or type its name";
                StatusText.Foreground = Ui.Brush("B.Text3");
            }
            else
            {
                StatusText.Text = "No hotspots found, type your hotspot name";
                StatusText.Foreground = Ui.Brush("B.Amber");
            }
        }));
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var ssid = SsidCombo.Text?.Trim();
        if (string.IsNullOrEmpty(ssid))
        {
            // Managed PCs can block SSID control. If Windows is already on WiFi, let the
            // connection manager use that network rather than forcing a name here.
            var nic = InterfaceDetector.DetectWifiInterface()?.Nic;
            if (nic is { OperationalStatus: OperationalStatus.Up })
            {
                Submitted?.Invoke(null, null);
                return;
            }
            StatusPanel.Visibility = Visibility.Visible;
            ScanSpinner.Visibility = Visibility.Collapsed;
            StatusText.Text = "Type your hotspot name, or join it from the Windows WiFi menu first";
            StatusText.Foreground = Ui.Brush("B.Red");
            StatusText.Margin = new Thickness(0);
            Flash(SsidCombo);
            return;
        }

        var pwd = CurrentPassword;
        Submitted?.Invoke(ssid, string.IsNullOrEmpty(pwd) ? null : pwd);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    private static void Flash(UIElement el)
    {
        var t = new TranslateTransform();
        el.RenderTransform = t;
        var a = new DoubleAnimationUsingKeyFrames();
        foreach (var (time, x) in new[] { (0, 0.0), (60, -5.0), (120, 5.0), (180, -3.0), (240, 0.0) })
            a.KeyFrames.Add(new LinearDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(time))));
        t.BeginAnimation(TranslateTransform.XProperty, a);
    }
}
