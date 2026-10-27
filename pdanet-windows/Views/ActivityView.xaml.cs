using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using PdaNetPortable.Core;
using PdaNetPortable.Services;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>One log entry as shown in the terminal view.</summary>
public sealed class LogLine
{
    public LogLine(LogEntry entry)
    {
        Entry = entry;
        Time = DateTime.TryParse(entry.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToString("HH:mm:ss") : entry.Timestamp;
        Text = Friendly.Clean(entry.Message);
        (Tag, TagBrush, TextBrush) = entry.Level switch
        {
            "OK" => ("OK", Ui.Brush("B.Accent"), Ui.Brush("B.Text")),
            "WARN" => ("WARN", Ui.Brush("B.Amber"), Ui.Brush("B.Text")),
            "ERROR" => ("ERROR", Ui.Brush("B.Red"), Ui.Brush("B.Text")),
            "CRITICAL" => ("CRIT", Ui.Brush("B.Red"), Ui.Brush("B.Red")),
            "DEBUG" => ("DEBUG", Ui.Brush("B.Line2"), Ui.Brush("B.Text3")),
            _ => ("INFO", Ui.Brush("B.Cyan"), Ui.Brush("B.Text2")),
        };
    }

    public LogEntry Entry { get; }
    public string Time { get; }
    public string Tag { get; }
    public string Text { get; }
    public Brush TagBrush { get; }
    public Brush TextBrush { get; }
    public bool IsProblem => Entry.Level is "WARN" or "ERROR" or "CRITICAL";

    /// <summary>Plain text form used for copy and export.</summary>
    public string Export()
    {
        var when = DateTime.TryParse(Entry.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToString("dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) : Entry.Timestamp;
        return $"{when}  {Tag,-5}  {Text}";
    }
}

/// <summary>Terminal style activity log with search, a problems filter, copy and export.</summary>
public partial class ActivityView : UserControl, IPage
{
    private readonly ObservableCollection<LogLine> _lines = new();
    private ICollectionView? _view;
    private MainWindow? _shell;

    public ActivityView()
    {
        InitializeComponent();
    }

    public void Attach(MainWindow shell)
    {
        _shell = shell;
        foreach (var e in Logger.Instance.GetRecent(500)) _lines.Add(new LogLine(e));
        _view = CollectionViewSource.GetDefaultView(_lines);
        _view.Filter = Filter;
        LogList.ItemsSource = _view;
        LogList.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelected()));
        IsVisibleChanged += (_, _) =>
        {
            PromptCaret.BeginAnimation(System.Windows.Media.Brush.OpacityProperty, IsVisible ? Ui.Blink() : null);
            if (IsVisible) ScrollToEnd();
        };
        UpdateCount();
    }

    public void OnShown() => ScrollToEnd();

    public void Refresh()
    {
        _view?.Refresh();
        UpdateCount();
        ScrollToEnd();
    }

    public void Append(LogEntry entry)
    {
        _lines.Add(new LogLine(entry));
        while (_lines.Count > Constants.LogBufferMaxSize) _lines.RemoveAt(0);
        UpdateCount();
        if (IsVisible && Follow.IsChecked == true) ScrollToEnd();
    }

    private bool Filter(object item)
    {
        if (item is not LogLine line) return false;
        if (FilterProblems.IsChecked == true && !line.IsProblem) return false;
        var q = Search.Text?.Trim();
        return string.IsNullOrEmpty(q)
               || line.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
               || line.Tag.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        _view.Refresh();
        UpdateCount();
    }

    private IEnumerable<LogLine> Visible() => _view?.Cast<LogLine>() ?? Enumerable.Empty<LogLine>();

    private void UpdateCount()
    {
        int shown = Visible().Count();
        CountText.Text = shown == _lines.Count ? $"{_lines.Count} lines" : $"{shown} of {_lines.Count} lines";
        EmptyState.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _lines.Count == 0 ? "Nothing here yet, activity appears as you use the app" : "No lines match your search";
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private void OnCopy(object sender, RoutedEventArgs e) =>
        CopyLines(Visible().ToList(), "Activity copied");

    private void CopySelected()
    {
        var selected = LogList.SelectedItems.Cast<LogLine>().OrderBy(l => _lines.IndexOf(l)).ToList();
        CopyLines(selected.Count > 0 ? selected : Visible().ToList(), "Lines copied");
    }

    private void CopyLines(List<LogLine> lines, string done)
    {
        if (lines.Count == 0)
        {
            _shell?.Toast("There is nothing to copy yet", ToastKind.Info);
            return;
        }
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines.Select(l => l.Export())));
            _shell?.Toast(done);
        }
        catch
        {
            _shell?.Toast("Could not reach the clipboard, try again", ToastKind.Error);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"PdaNet activity {DateTime.Now:yyyyMMdd HHmm}.txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllLines(dlg.FileName, _lines.Select(l => l.Export()));
            _shell?.Toast("Activity saved to a file");
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Could not save the activity file: {ex.Message}");
            _shell?.Toast("Could not save the file, try another folder", ToastKind.Error);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _lines.Clear();
        Logger.Instance.ClearBuffer();
        UpdateCount();
        _shell?.Toast("Activity cleared", ToastKind.Info);
    }
}
