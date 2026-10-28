using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PdaNetPortable.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>One rule in the blocked list.</summary>
public sealed class RuleRow
{
    public RuleRow(BlockRule rule)
    {
        Rule = rule;
        bool app = rule.Kind == BlockKind.App;
        Title = app ? rule.AppName ?? rule.Exe ?? "An app" : rule.Site ?? "A website";
        Detail = $"{rule.Scope}, blocked since {rule.Created:d MMM}";
        Hits = rule.Hits switch { 0 => "nothing stopped yet", 1 => "stopped once", var n => $"stopped {n} times" };
        Icon = app ? AppIcons.For(rule.AppPath) : null;
        // Sites get a globe, apps without an icon get their first letter.
        Glyph = app ? AppsView.MonogramOf(Title) : "";
        GlyphFont = (FontFamily)Application.Current.Resources[app ? "F.Display" : "F.Icon"];
        UnblockName = $"Unblock {rule.Label}";
    }

    public BlockRule Rule { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Hits { get; }
    public ImageSource? Icon { get; }
    public string Glyph { get; }
    public FontFamily GlyphFont { get; }
    public string UnblockName { get; }
    public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Everything you blocked, with how often each rule stepped in, and a way to undo it.</summary>
public partial class BlockedSheet : UserControl, ISheet
{
    private readonly DispatcherTimer _confirmTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _confirming;

    public event Action? Closed;

    public BlockedSheet()
    {
        InitializeComponent();
        _confirmTimer.Tick += (_, _) => ResetClear();
        Load();
    }

    public void FocusFirst() => CloseBtn.Focus();

    public void OnClosing() => _confirmTimer.Stop();

    private void Load()
    {
        var rows = TrafficRules.Instance.All
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.Kind == BlockKind.App ? r.AppName : r.Site, StringComparer.OrdinalIgnoreCase)
            .Select(r => new RuleRow(r)).ToList();
        Rules.ItemsSource = rows;
        bool empty = rows.Count == 0;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ClearBtn.IsEnabled = !empty;
    }

    private MainWindow? Shell => Window.GetWindow(this) as MainWindow;

    private void OnUnblock(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not RuleRow row) return;
        Blocking.Undo(Shell, row.Rule);
        Load();
    }

    // Unblocking everything takes a second click, like clearing the history.
    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (!_confirming)
        {
            _confirming = true;
            ClearBtn.Content = "Click again to unblock all";
            _confirmTimer.Start();
            return;
        }
        ResetClear();
        TrafficRules.Instance.Clear();
        Shell?.Toast("All blocks removed", ToastKind.Info);
        Load();
    }

    private void ResetClear()
    {
        _confirmTimer.Stop();
        _confirming = false;
        ClearBtn.Content = "Unblock all";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Closed?.Invoke();
}
