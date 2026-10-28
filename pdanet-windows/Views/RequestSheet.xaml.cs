using System.Text;
using System.Windows;
using System.Windows.Threading;
using PdaNetPortable.Core;
using PdaNetPortable.Services;
using Clipboard = System.Windows.Clipboard;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>
/// The full record of one line on the Apps page: which app, the address, the numbers, what
/// PdaNet can and cannot see, and the request and answer heads. Updates live while the
/// connection is still open.
/// </summary>
public partial class RequestSheet : UserControl, ISheet
{
    private readonly TrafficFlow _flow;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public event Action? Closed;

    public RequestSheet(TrafficFlow flow)
    {
        InitializeComponent();
        _flow = flow;

        var icon = AppIcons.For(flow.App.Path);
        AppIcon.Source = icon;
        AppMono.Text = icon == null ? AppsView.MonogramOf(flow.App.Name) : "";

        BadgeText.Text = flow.Secure ? "Private" : "Open";
        var (badge, back) = flow.Secure ? ("B.Cyan", "B.CyanSoft") : ("B.Amber", "B.AmberSoft");
        BadgeText.Foreground = Ui.Brush(badge);
        BadgeBox.Background = Ui.Brush(back);

        HostText.Text = flow.Host;
        var who = new List<string> { flow.App.Name };
        who.Add($"started {flow.Started:d MMM, HH:mm:ss}");
        SubText.Text = string.Join("  ·  ", who);
        UrlText.Text = flow.Url;
        UrlText.ToolTip = flow.Url;

        if (flow.Secure)
        {
            NoteIcon.Text = "";
            NoteIcon.Foreground = Ui.Brush("B.Cyan");
            NoteBox.Background = Ui.Brush("B.CyanSoft");
            NoteBox.BorderBrush = new System.Windows.Media.SolidColorBrush(Ui.WithAlpha(Ui.Color("C.Cyan"), 0x55));
            NoteTitle.Text = "Private";
            NoteBody.Text = "Only the app and the website can read the content, PdaNet sees the website name, the time and the amount of data";
        }
        else
        {
            NoteIcon.Text = "";
            NoteIcon.Foreground = Ui.Brush("B.Amber");
            NoteBox.Background = Ui.Brush("B.AmberSoft");
            NoteBox.BorderBrush = Ui.Brush("B.AmberLine");
            NoteTitle.Text = "Not private";
            NoteBody.Text = "People on the same network could read this data, if the website has an address that starts with https, use that address instead";
        }
        NoteBody.Text = Friendly.Clean(NoteBody.Text);

        RequestText.Text = flow.RequestHead;
        Refresh();
        ShowBlockButtons();
        if (flow.IsOpen)
        {
            _tick.Tick += (_, _) => Refresh();
            _tick.Start();
        }
    }

    // ============================ Blocking ============================
    private string Site => TrafficRules.SiteOf(_flow.Host);

    private void ShowBlockButtons()
    {
        var app = _flow.App;
        var appRule = TrafficRules.Instance.AppRule(app.Exe);
        BlockAppBtn.Visibility = Blocking.CanBlock(app) ? Visibility.Visible : Visibility.Collapsed;
        BlockAppBtn.Content = appRule == null ? $"Block {app.Name}" : $"Unblock {app.Name}";
        BlockAppBtn.Style = (Style)FindResource(appRule == null ? "Btn.Danger" : "Btn.Secondary");
        BlockAppBtn.ToolTip = appRule == null ? $"Stop {app.Name} from using the internet through PdaNet" : null;

        var siteRule = TrafficRules.Instance.SiteRules("", _flow.Host).FirstOrDefault();
        BlockSiteBtn.Content = siteRule == null ? $"Block {Blocking.Short(Site)}" : $"Unblock {Blocking.Short(siteRule.Site ?? Site)}";
        BlockSiteBtn.Style = (Style)FindResource(siteRule == null ? "Btn.Danger" : "Btn.Secondary");
        BlockSiteBtn.ToolTip = siteRule == null ? $"Stop every app from reaching {Site}" : null;
    }

    private void OnBlockApp(object sender, RoutedEventArgs e)
    {
        var shell = Window.GetWindow(this) as MainWindow;
        var rule = TrafficRules.Instance.AppRule(_flow.App.Exe);
        if (rule == null) Blocking.App(shell, _flow.App);
        else Blocking.Undo(shell, rule);
        ShowBlockButtons();
        Refresh();
    }

    private void OnBlockSite(object sender, RoutedEventArgs e)
    {
        var shell = Window.GetWindow(this) as MainWindow;
        var rule = TrafficRules.Instance.SiteRules("", _flow.Host).FirstOrDefault();
        if (rule == null) Blocking.Site(shell, Site, subdomains: true, only: null);
        else Blocking.Undo(shell, rule);
        ShowBlockButtons();
        Refresh();
    }

    public void FocusFirst() => CloseBtn.Focus();

    public void OnClosing() => _tick.Stop();

    private void Refresh()
    {
        var (text, brush, tip) = FlowRow.Describe(_flow);
        ResultValue.Text = text;
        ResultValue.Foreground = brush;
        ResultValue.ToolTip = tip;
        TookValue.Text = AppsView.Span(_flow.Duration);
        SentValue.Text = Fmt.Bytes(_flow.BytesUp);
        ReceivedValue.Text = Fmt.Bytes(_flow.BytesDown);
        var type = _flow.ContentType;
        TypeValue.Text = _flow.Secure ? "Private" : KindOf(type);
        TypeValue.ToolTip = type;
        ResponseText.Text = Answer();
        if (!_flow.IsOpen) _tick.Stop();
    }

    private string Answer()
    {
        if (_flow.BlockedBy != null)
            return (_flow.ResponseHead != null ? _flow.ResponseHead + "\n\n" : "") + _flow.BlockedBy;
        if (_flow.Error != null)
            return $"No answer\n\n{_flow.Error}";
        if (_flow.Secure)
            return (_flow.ResponseHead ?? "Connecting") + "\n\nThe data after this line is private, PdaNet does not read the data";
        return _flow.ResponseHead ?? "Waiting for the website to answer";
    }

    private string Record()
    {
        var sb = new StringBuilder();
        sb.AppendLine(_flow.App.Name);
        sb.AppendLine($"{(_flow.Secure ? "Private" : "Open")}  {_flow.Url}");
        sb.AppendLine($"Result {ResultValue.Text}, took {TookValue.Text}, sent {SentValue.Text}, received {ReceivedValue.Text}");
        sb.AppendLine($"Started {_flow.Started:dd MMM yyyy HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("Sent by the app");
        sb.AppendLine(_flow.RequestHead.Replace("\n", Environment.NewLine));
        sb.AppendLine();
        sb.AppendLine("Answer from the website");
        sb.AppendLine(Answer().Replace("\n", Environment.NewLine));
        return sb.ToString();
    }

    private void OnCopy(object sender, RoutedEventArgs e) => Copy(Record(), "Details copied");

    private void OnCopyUrl(object sender, RoutedEventArgs e) => Copy(_flow.Url, "Web address copied");

    private void Copy(string text, string done)
    {
        var shell = Window.GetWindow(this) as MainWindow;
        try
        {
            Clipboard.SetText(text);
            shell?.Toast(done);
        }
        catch
        {
            shell?.Toast("Could not reach the clipboard, try again", ToastKind.Error);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Closed?.Invoke();

    // The kind of data in plain words, the exact type stays in the tooltip.
    private static string KindOf(string? type)
    {
        if (string.IsNullOrEmpty(type)) return "None yet";
        var t = type.Split(';')[0].Trim().ToLowerInvariant();
        if (t == "text/html") return "Web page";
        if (t.StartsWith("image/")) return "Picture";
        if (t.StartsWith("video/")) return "Video";
        if (t.StartsWith("audio/")) return "Sound";
        if (t.Contains("javascript") || t == "text/css") return "Page code";
        if (t.Contains("json") || t.Contains("xml")) return "Data";
        if (t.StartsWith("text/")) return "Text";
        return "File";
    }
}
