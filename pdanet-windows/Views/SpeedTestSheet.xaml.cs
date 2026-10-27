using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PdaNetPortable.Core;
using PdaNetPortable.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>In window speed test with an animated gauge. Downloads a fixed size file and times it.</summary>
public partial class SpeedTestSheet : UserControl, ISheet
{
    private const string TestUrl = "https://speed.cloudflare.com/__down?bytes=25000000";
    private const double GaugeLength = 346;   // arc length in device units
    private const double GaugeMaxMbps = 100;  // a full sweep maps to this speed

    // Animatable 0..1 fill driving the gauge's dash array (StrokeDashArray itself is not animatable).
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(double), typeof(SpeedTestSheet),
        new PropertyMetadata(0.0, OnFillChanged));

    public double Fill
    {
        get => (double)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    private CancellationTokenSource? _cts;

    public event Action? Closed;

    public SpeedTestSheet()
    {
        InitializeComponent();
    }

    public void FocusFirst() => StartBtn.Focus();

    public void OnClosing() => _cts?.Cancel();

    private static void OnFillChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var sheet = (SpeedTestSheet)d;
        double fill = (double)e.NewValue;
        // Hide the arc entirely at rest so the rounded line cap does not leave a dot.
        sheet.GaugeArc.Opacity = fill <= 0.001 ? 0 : 1;
        double len = fill * GaugeLength;
        // Dash array is in multiples of stroke thickness (14), so divide through.
        sheet.GaugeArc.StrokeDashArray = new DoubleCollection { len / 14.0, 10000 };
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        StartBtn.IsEnabled = false;
        StartBtn.Content = "Testing";
        VerdictBox.Visibility = Visibility.Collapsed;
        SetFill(0);
        StatusText.Text = "Warming up";
        StatusText.Foreground = Ui.Brush("B.Text3");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var resp = await http.GetAsync(TestUrl, HttpCompletionOption.ResponseHeadersRead, token);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(token);

            var sw = Stopwatch.StartNew();
            var buffer = new byte[65536];
            long total = 0;
            double lastShown = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                total += read;
                double secs = sw.Elapsed.TotalSeconds;
                if (secs > 0.25 && secs - lastShown >= 0.1)
                {
                    lastShown = secs;
                    ShowSpeed(total * 8.0 / secs / 1_000_000.0, animate: false);
                    StatusText.Text = $"{Fmt.Bytes(total)} so far";
                }
            }
            sw.Stop();

            double mbps = total * 8.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds) / 1_000_000.0;
            ShowSpeed(mbps, animate: true);
            StatusText.Text = $"Got {Fmt.Bytes(total)} in {sw.Elapsed.TotalSeconds:0.0} seconds";
            VerdictText.Text = Friendly.SpeedVerdict(mbps);
            VerdictBox.Visibility = Visibility.Visible;
            Ui.Enter(VerdictBox, rise: 6);
            Logger.Instance.Ok($"Speed test {mbps:0.0} Mbps ({Fmt.Bytes(total)})");
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            StatusText.Text = "Could not run the test, check that you are connected";
            StatusText.Foreground = Ui.Brush("B.Red");
            SetFill(0);
            ResultValue.Text = "0.0";
            Logger.Instance.Error($"Speed test could not run: {ex.Message}");
        }
        finally
        {
            StartBtn.IsEnabled = true;
            StartBtn.Content = "Test again";
        }
    }

    private void ShowSpeed(double mbps, bool animate)
    {
        ResultValue.Text = mbps >= 100 ? $"{mbps:0}" : $"{mbps:0.0}";
        GaugeArc.Stroke = Ui.Brush(mbps >= 25 ? "B.Accent" : mbps >= 5 ? "B.Cyan" : "B.Amber");

        double target = Math.Min(1, mbps / GaugeMaxMbps);
        if (animate)
            BeginAnimation(FillProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(450)) { EasingFunction = Ui.EaseOut });
        else
            SetFill(target);
    }

    private void SetFill(double value)
    {
        BeginAnimation(FillProperty, null);
        Fill = value;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Closed?.Invoke();
}
