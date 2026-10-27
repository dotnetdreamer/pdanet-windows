using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using PdaNetPortable.Core;
using PdaNetPortable.Services;

namespace PdaNetPortable.Dialogs;

public partial class SpeedTestDialog : Window
{
    // ~15 MB download through the active (tethered) connection.
    private const string TestUrl = "https://speed.cloudflare.com/__down?bytes=15000000";
    private const long ExpectedBytes = 15_000_000;

    public SpeedTestDialog()
    {
        InitializeComponent();
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        StartBtn.IsEnabled = false;
        ResultText.Text = "-- Mbps";
        StatusText.Text = "Testing download speed...";
        Progress.Value = 0;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var sw = Stopwatch.StartNew();
            long total = 0;

            using var resp = await http.GetAsync(TestUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync();

            var buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                total += read;
                var pct = Math.Min(100, (double)total / ExpectedBytes * 100);
                Progress.Value = pct;
                if (sw.Elapsed.TotalSeconds > 0)
                    StatusText.Text = $"{Fmt.Bytes(total)} in {sw.Elapsed.TotalSeconds:0.0}s";
            }
            sw.Stop();

            double mbps = total * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000.0;
            ResultText.Text = $"{mbps:0.0} Mbps";
            StatusText.Text = $"Downloaded {Fmt.Bytes(total)} in {sw.Elapsed.TotalSeconds:0.0}s";
            Logger.Instance.Ok($"Speed test: {mbps:0.0} Mbps ({Fmt.Bytes(total)})");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Speed test failed (no connection?)";
            ResultText.Text = "-- Mbps";
            Logger.Instance.Error($"Speed test failed: {ex.Message}");
        }
        finally
        {
            StartBtn.IsEnabled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
