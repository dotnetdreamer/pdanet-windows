using System.IO;
using System.Text.Json;
using System.Windows;
using PdaNetPortable.Core;
using PdaNetPortable.Services;

namespace PdaNetPortable.Dialogs;

public partial class HistoryWindow : Window
{
    private class Session
    {
        public string? timestamp { get; set; }
        public long duration { get; set; }
        public long downloaded { get; set; }
        public long uploaded { get; set; }
        public string? @interface { get; set; }
        public double avg_latency { get; set; }
    }

    public HistoryWindow()
    {
        InitializeComponent();
        LoadHistory();
    }

    private void LoadHistory()
    {
        HistoryList.Items.Clear();
        var sessions = Read();
        if (sessions.Count == 0)
        {
            SummaryText.Text = "No connection history available";
            return;
        }

        long totalDown = sessions.Sum(s => s.downloaded);
        long totalUp = sessions.Sum(s => s.uploaded);
        long totalTime = sessions.Sum(s => s.duration);
        SummaryText.Text =
            $"{sessions.Count} sessions  |  total {Fmt.Uptime(totalTime)}  |  ↓ {Fmt.Bytes(totalDown)}  ↑ {Fmt.Bytes(totalUp)}";

        foreach (var s in Enumerable.Reverse(sessions))
        {
            var when = DateTime.TryParse(s.timestamp, out var dt) ? dt.ToString("yyyy-MM-dd HH:mm") : (s.timestamp ?? "?");
            HistoryList.Items.Add(
                $"{when}   {Fmt.Uptime(s.duration),8}   ↓ {Fmt.Bytes(s.downloaded),-10} ↑ {Fmt.Bytes(s.uploaded),-10}   {s.@interface}");
        }
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

    private void OnClear(object sender, RoutedEventArgs e)
    {
        try { if (File.Exists(AppPaths.ConnectionHistory)) File.Delete(AppPaths.ConnectionHistory); } catch { }
        LoadHistory();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
