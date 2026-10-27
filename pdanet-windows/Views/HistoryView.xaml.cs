using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PdaNetPortable.Core;
using PdaNetPortable.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>One past session, already formatted for display.</summary>
public sealed record HistoryRow(string When, string Duration, string Down, string Up, string Connection, string Ping);

/// <summary>Totals plus a table of past sessions read from connection_history.json.</summary>
public partial class HistoryView : UserControl, IPage
{
    private sealed class Session
    {
        public string? timestamp { get; set; }
        public long duration { get; set; }
        public long downloaded { get; set; }
        public long uploaded { get; set; }
        public string? @interface { get; set; }
        public double avg_latency { get; set; }
    }

    private MainWindow? _shell;
    private readonly DispatcherTimer _confirmTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _confirming;

    public HistoryView()
    {
        InitializeComponent();
        _confirmTimer.Tick += (_, _) => ResetClearButton();
    }

    public void Attach(MainWindow shell) => _shell = shell;

    public void OnShown() => Load();

    public void Refresh()
    {
        Load();
        _shell?.Toast("History refreshed", ToastKind.Info);
    }

    private void Load()
    {
        var sessions = Read();
        TotSessions.Text = sessions.Count.ToString();
        TotTime.Text = Fmt.Uptime(sessions.Sum(s => s.duration));
        TotDown.Text = Fmt.Bytes(sessions.Sum(s => s.downloaded));
        TotUp.Text = Fmt.Bytes(sessions.Sum(s => s.uploaded));

        Sessions.ItemsSource = Enumerable.Reverse(sessions).Select(s => new HistoryRow(
            DateTime.TryParse(s.timestamp, out var dt) ? dt.ToString("ddd d MMM, HH:mm") : "Unknown time",
            Fmt.Uptime(s.duration),
            Fmt.Bytes(s.downloaded),
            Fmt.Bytes(s.uploaded),
            string.IsNullOrEmpty(s.@interface) ? "Phone VPN" : Friendly.Adapter(s.@interface),
            s.avg_latency > 0 ? $"{s.avg_latency:0} ms" : "None")).ToList();

        bool empty = sessions.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ListScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ClearBtn.IsEnabled = !empty;
    }

    private static List<Session> Read()
    {
        try
        {
            if (!File.Exists(AppPaths.ConnectionHistory)) return new();
            return JsonSerializer.Deserialize<List<Session>>(File.ReadAllText(AppPaths.ConnectionHistory)) ?? new();
        }
        catch { return new(); }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    // Clearing deletes the history file, so it takes a second click to confirm.
    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (!_confirming)
        {
            _confirming = true;
            ClearBtn.Content = "Click again to clear";
            _confirmTimer.Start();
            return;
        }

        ResetClearButton();
        try
        {
            if (File.Exists(AppPaths.ConnectionHistory)) File.Delete(AppPaths.ConnectionHistory);
            _shell?.Toast("History cleared");
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Could not clear history: {ex.Message}");
            _shell?.Toast("Could not clear the history file", ToastKind.Error);
        }
        Load();
    }

    private void ResetClearButton()
    {
        _confirmTimer.Stop();
        _confirming = false;
        ClearBtn.Content = "Clear history";
    }
}
