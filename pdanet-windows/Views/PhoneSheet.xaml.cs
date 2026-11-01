using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PdaNetPortable.Core;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>
/// In window sheet with every phone and emulator adb can see, for Other on the Connect page.
/// The same phone shows up once per link, for example on the USB cable and over WiFi.
/// </summary>
public partial class PhoneSheet : UserControl, ISheet
{
    private readonly string? _adbPath;
    private IReadOnlyList<PhoneLink> _links = Array.Empty<PhoneLink>();
    private string? _picked;
    private bool _loading;

    public event Action<PhoneLink>? Picked;
    public event Action? PairRequested;
    public event Action? Cancelled;
    /// <summary>Raised however the sheet closes, so the Connect page can show the saved choice again.</summary>
    public event Action? Closed;

    public PhoneSheet(string? adbPath, string? pickedSerial)
    {
        InitializeComponent();
        _adbPath = adbPath;
        _picked = pickedSerial;
        Render(AdbAgent.LastLinks);
        PreviewKeyDown += OnKeyDown;
        Loaded += (_, _) => Load();
    }

    public void FocusFirst()
    {
        var radios = Enumerable.Range(0, Rows.Items.Count).Select(RadioAt).OfType<RadioButton>().ToList();
        (radios.FirstOrDefault(r => r.IsChecked == true) ?? radios.FirstOrDefault())?.Focus();
    }

    /// <summary>The card of a row, the root of the row template.</summary>
    private RadioButton? RadioAt(int index) =>
        Rows.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject box && VisualTreeHelper.GetChildrenCount(box) > 0
            ? VisualTreeHelper.GetChild(box, 0) as RadioButton
            : null;

    public void OnClosing() => Closed?.Invoke();

    private void Load()
    {
        if (_loading) return;
        _loading = true;
        RefreshBtn.IsEnabled = false;
        Task.Run(() => AdbAgent.List(_adbPath).Links).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            _loading = false;
            RefreshBtn.IsEnabled = true;
            if (t.IsCompletedSuccessfully) Render(t.Result);
        }));
    }

    private void Render(IReadOnlyList<PhoneLink> links)
    {
        _links = links;
        Rows.ItemsSource = links.Select(l => new LinkRow(l, l.Serial == _picked)).ToList();
        EmptyPanel.Visibility = links.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Subtitle.Text = links.Count switch
        {
            0 => "No phones found yet",
            1 => "PdaNet sees 1 connection",
            _ => $"PdaNet sees {links.Count} connections, pick one",
        };
        UseBtn.IsEnabled = links.Any(l => l.Serial == _picked);
    }

    private void OnRowChecked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LinkRow row) return;
        _picked = row.Serial;
        UseBtn.IsEnabled = true;
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OnRowChecked(sender, e);
        OnUse(sender, e);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Enter on a phone uses the phone, a focused button keeps its own Enter.
        if (e.Key != Key.Enter || !UseBtn.IsEnabled || !Rows.IsKeyboardFocusWithin) return;
        OnUse(sender, e);
        e.Handled = true;
    }

    private void OnUse(object sender, RoutedEventArgs e)
    {
        var link = _links.FirstOrDefault(l => l.Serial == _picked);
        if (link != null) Picked?.Invoke(link);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Load();
    private void OnPair(object sender, RoutedEventArgs e) => PairRequested?.Invoke();
    private void OnCancel(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    /// <summary>One row of the list, in plain words.</summary>
    public sealed class LinkRow
    {
        private readonly PhoneLink _link;

        public LinkRow(PhoneLink link, bool picked)
        {
            _link = link;
            Picked = picked;
        }

        public bool Picked { get; }
        public string Serial => _link.Serial;
        public string Title => _link.Label;
        public string Id => _link.Id;
        public string Glyph => _link.Kind switch
        {
            LinkKind.Wifi => "",
            LinkKind.Emulator => "",
            _ => "",
        };

        public string Detail
        {
            get
            {
                var where = Friendly.LinkWords(_link.Kind);
                where = char.ToUpperInvariant(where[0]) + where[1..];
                return _link.State switch
                {
                    "device" => where,
                    "unauthorized" => $"{where}, tap Allow on the phone",
                    "offline" => $"{where}, not responding",
                    _ => $"{where}, not ready yet",
                };
            }
        }

        public Brush DetailBrush => Ui.Brush(_link.Ready ? "B.Text2" : "B.Amber");
        public string Speech => $"{Title}, {Detail}";
    }
}
