using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>
/// In window sheet that pairs a phone with this PC over WiFi once (Android 11 and later), then
/// links the phone so Phone VPN works without the cable. While the phone shows the pairing box,
/// the phone's address shows up on this WiFi by itself, so most people only type the code.
/// </summary>
public partial class PairSheet : UserControl, ISheet
{
    private static readonly Regex CodeRx = new(@"^\d{6}$", RegexOptions.Compiled);
    private static readonly Regex AddressRx = new(@"^[A-Za-z0-9.\-:\[\]%]+:\d{1,5}$", RegexOptions.Compiled);

    private readonly string? _adbPath;
    private readonly CancellationTokenSource _closing = new();
    // _filling: the address is being filled in by PdaNet, not typed. _holding: an error stays until the next edit.
    private bool _busy, _linkOnly, _addressTyped, _filling, _holding;

    /// <summary>The phone is linked over WiFi, with the phone's link, or null when adb linked a phone it does not list yet.</summary>
    public event Action<PhoneLink?>? Linked;
    public event Action? Cancelled;
    /// <summary>Raised however the sheet closes, so the Connect page can show the saved choice again.</summary>
    public event Action? Closed;

    public PairSheet(string? adbPath)
    {
        InitializeComponent();
        _adbPath = adbPath;
        ShowMode(linkOnly: false);

        Code.TextChanged += (_, _) => _holding = false;
        Address.TextChanged += (_, _) =>
        {
            _holding = false;
            if (!_filling) _addressTyped = Address.Text.Length > 0;
        };
        PreviewKeyDown += OnKeyDown;
        Loaded += (_, _) => _ = WatchAsync(_closing.Token);
        if (Demo.Value("pair") == "typed") Code.Text = "482913";
    }

    public void FocusFirst()
    {
        if (_linkOnly) Address.Focus();
        else Code.Focus();
    }

    public void OnClosing()
    {
        _closing.Cancel();
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        Closed?.Invoke();
    }

    /// <summary>Pair mode asks for the code and the pairing address, link mode only for the address of a paired phone.</summary>
    private void ShowMode(bool linkOnly)
    {
        _linkOnly = linkOnly;
        CodePanel.Visibility = linkOnly ? Visibility.Collapsed : Visibility.Visible;
        CodeCol.Width = new GridLength(linkOnly ? 0 : 132);
        GapCol.Width = new GridLength(linkOnly ? 0 : 12);
        Title.Text = linkOnly ? "Link over WiFi" : "Pair over WiFi";
        Subtitle.Text = linkOnly ? "For a phone already paired with this PC" : "Only once, after pairing PdaNet can usually find the phone";
        GoBtn.Content = linkOnly ? "Link" : "Pair";
        SwitchBtn.Content = linkOnly ? "First time? Pair with a code" : "Paired before? Link with the address";
        Steps.ItemsSource = Numbered(linkOnly
            ? new[]
            {
                "On the phone open Developer options, then Wireless debugging",
                "Look under IP address and port on the Wireless debugging screen",
                "Type the IP address and port here, then press Link",
            }
            : new[]
            {
                "On the phone open Developer options, then Wireless debugging",
                "Turn on Wireless debugging, then tap Pair device with pairing code",
                "Type the code here, PdaNet fills in the address when the phone shows up on this WiFi",
            });
        _addressTyped = false;
        Fill("");
        _holding = false;
        SetStatus("Looking for your phone on this WiFi", "B.Text3", spin: true);
    }

    /// <summary>Looks for the phone on this WiFi every few seconds and fills in the address.</summary>
    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_busy)
            {
                bool pairing = !_linkOnly;
                var services = await Task.Run(() => AdbAgent.WifiServices(_adbPath));
                if (ct.IsCancellationRequested) return;
                // Any phone on this WiFi can announce itself, so fill in an address only when exactly one does.
                var found = services.Where(s => s.Pairing == pairing).Select(s => s.Address).Distinct().ToList();
                if (!_busy && pairing == !_linkOnly)
                {
                    if (!_addressTyped) Fill(found.Count == 1 ? found[0] : "");
                    if (!_holding)
                    {
                        if (found.Count == 1)
                            SetStatus(pairing ? "Found a phone, check the address on your phone and type the code"
                                              : "Found a phone, check the address on your phone and press Link", "B.Accent");
                        else if (found.Count > 1)
                            SetStatus($"Found {found.Count} phones on this WiFi, type the address shown on your phone", "B.Amber");
                        else
                            SetStatus("Looking for your phone on this WiFi", "B.Text3", spin: true);
                    }
                }
            }
            try { await Task.Delay(2500, ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    private async void OnGo(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var address = Address.Text.Trim();

        if (_linkOnly)
        {
            if (!AddressRx.IsMatch(address)) { Fail("Type the IP address and port from the Wireless debugging screen", Address); return; }
            Busy(true, "Linking over WiFi");
            var (linked, _) = await Task.Run(() => AdbAgent.ConnectWifi(_adbPath, address));
            if (_closing.IsCancellationRequested) return;
            if (linked)
            {
                await DoneAsync(await FindLinkAsync(address));
                return;
            }
            Busy(false);
            Fail("PdaNet could not reach the phone at that address, check the address and the WiFi", Address);
            return;
        }

        var code = new string(Code.Text.Where(char.IsDigit).ToArray());
        if (!CodeRx.IsMatch(code)) { Fail("Type the 6 digit code shown on the phone", Code); return; }
        if (!AddressRx.IsMatch(address)) { Fail("Type the IP address and port shown under the code on the phone", Address); return; }

        Busy(true, "Pairing with the phone");
        var before = AdbAgent.LastLinks.Select(l => l.Serial).ToHashSet();
        var (paired, name, output) = await Task.Run(() => AdbAgent.Pair(_adbPath, address, code));
        if (_closing.IsCancellationRequested) return;
        if (!paired)
        {
            Busy(false);
            Fail(output.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? "Wrong code, check the code on the phone and try again"
                : "The phone did not answer, keep the pairing box open on the phone and try again", Code);
            return;
        }

        SetStatus("Paired, linking over WiFi", "B.Text2", spin: true);
        var (linkedAny, found) = await WaitForPairedAsync(name, before);
        if (linkedAny) { await DoneAsync(found); return; }
        if (_closing.IsCancellationRequested) return;

        // Paired, but the phone did not link by itself: ask for the address on the main screen.
        Busy(false);
        ShowMode(linkOnly: true);
        SetStatus("Paired, now type the IP address and port shown on the Wireless debugging screen", "B.Amber");
        _holding = true;
        Address.Focus();
    }

    /// <summary>
    /// After pairing, waits for the paired phone to link over WiFi and returns the phone's link.
    /// Only the phone adb just paired counts: the link name adb pair reported, the phone serial in
    /// that name, or the address the phone announces under that name. Without a name from adb,
    /// only a single new WiFi link counts. PdaNet links the announced address itself once, for a
    /// phone adb does not link alone. Linked without a link means adb linked the address but does
    /// not list the phone yet.
    /// </summary>
    private async Task<(bool Linked, PhoneLink? Link)> WaitForPairedAsync(string? name, HashSet<string> before)
    {
        var serial = AdbAgent.SerialInWifiName(name);
        string? announced = null;
        bool linked = false;
        for (int i = 0; i < 12 && !_closing.IsCancellationRequested; i++)
        {
            var wifi = (await Task.Run(() => AdbAgent.List(_adbPath).Links))
                .Where(l => l.Kind == LinkKind.Wifi && l.Ready).ToList();
            var hits = name != null
                ? wifi.Where(l => l.Serial.StartsWith(name, StringComparison.Ordinal) || (serial != null && l.Key == serial)
                                  || l.Serial == announced).ToList()
                : wifi.Where(l => l.Serial == announced || !before.Contains(l.Serial)).ToList();
            if (name != null ? hits.Count > 0 : hits.Count == 1) return (true, hits[0]);

            if (!linked)
            {
                var phones = (await Task.Run(() => AdbAgent.WifiServices(_adbPath)))
                    .Where(s => !s.Pairing && (name == null || s.Name == name)).ToList();
                if (phones.Count == 1)
                {
                    announced = phones[0].Address;
                    (linked, _) = await Task.Run(() => AdbAgent.ConnectWifi(_adbPath, announced));
                }
            }
            try { await Task.Delay(1000, _closing.Token); }
            catch (TaskCanceledException) { return (false, null); }
        }
        return (linked, null);
    }

    /// <summary>After linking a typed address, finds the link adb lists under that address.</summary>
    private async Task<PhoneLink?> FindLinkAsync(string address)
    {
        for (int i = 0; i < 5 && !_closing.IsCancellationRequested; i++)
        {
            var link = (await Task.Run(() => AdbAgent.List(_adbPath).Links)).FirstOrDefault(l => l.Ready && l.Serial == address);
            if (link != null) return link;
            try { await Task.Delay(1000, _closing.Token); }
            catch (TaskCanceledException) { return null; }
        }
        return null;
    }

    private async Task DoneAsync(PhoneLink? link)
    {
        SetStatus(link != null ? $"{link.Label} is linked over WiFi"
            : "Linked over WiFi, pick the phone in the list that opens next", "B.Accent");
        try { await Task.Delay(800, _closing.Token); }
        catch (TaskCanceledException) { return; }
        Linked?.Invoke(link);
    }

    private void OnSwitch(object sender, RoutedEventArgs e)
    {
        ShowMode(!_linkOnly);
        FocusFirst();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Enter in a field means Pair or Link, a focused button keeps its own Enter.
        if (e.Key != System.Windows.Input.Key.Enter || !(Code.IsKeyboardFocused || Address.IsKeyboardFocused)) return;
        OnGo(sender, e);
        e.Handled = true;
    }

    private void Busy(bool on, string? text = null)
    {
        _busy = on;
        Code.IsEnabled = Address.IsEnabled = GoBtn.IsEnabled = SwitchBtn.IsEnabled = !on;
        if (on && text != null) SetStatus(text, "B.Text2", spin: true);
    }

    private void Fail(string message, UIElement field)
    {
        SetStatus(message, "B.Red");
        _holding = true;
        Ui.Shake(field);
    }

    private void Fill(string address)
    {
        _filling = true;
        Address.Text = address;
        _filling = false;
    }

    /// <summary>One status line: a spinner while waiting, else a colored dot for found, problems and success.</summary>
    private void SetStatus(string text, string brush, bool spin = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = Ui.Brush(brush == "B.Accent" ? "B.Text" : brush);
        Spinner.Visibility = spin ? Visibility.Visible : Visibility.Collapsed;
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, spin ? Ui.Loop(0, 360, 0.9, 60) : null);
        StatusDot.Visibility = !spin && brush is not ("B.Text2" or "B.Text3") ? Visibility.Visible : Visibility.Collapsed;
        StatusDot.Fill = Ui.Brush(brush);
    }

    private static List<NumberedRow> Numbered(IEnumerable<string> lines) =>
        lines.Select((l, i) => new NumberedRow((i + 1).ToString("00"), l)).ToList();
}
