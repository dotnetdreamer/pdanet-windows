using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PdaNetPortable.Interop;
using PdaNetPortable.Share;

namespace PdaNetPortable.Views;

/// <summary>
/// A small bar at the top of the shared screen while someone can see it: who it is, whether they
/// may control this PC, and Stop. It is kept out of the capture, so it never covers what the other
/// person sees, and it never takes the focus from the app in use.
/// </summary>
public partial class ShareBar : Window
{
    private readonly ShareSession _s = ShareSession.Instance;

    public ShareBar()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.StyleWindow(hwnd);
            ShareNative.SetWindowDisplayAffinity(hwnd, ShareNative.WDA_EXCLUDEFROMCAPTURE);
        };
        Loaded += (_, _) =>
        {
            Place();
            var grow = Ui.Loop(0.8, 1.8, 1.8, 24, Ui.EaseOut);
            PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            Pulse.BeginAnimation(OpacityProperty, Ui.Loop(0.5, 0, 1.8, 24, Ui.EaseOut));
        };
        _s.Changed += Refresh;
        Closed += (_, _) => _s.Changed -= Refresh;
        Refresh();
    }

    public void Refresh()
    {
        string peer = _s.Peer ?? "the other PC";
        Text.Text = _s.Phase switch
        {
            SharePhase.Live => $"{peer} sees your screen",
            SharePhase.Reconnecting => $"Reconnecting with {peer}",
            _ => $"Connecting with {peer}",
        };
        Control.IsChecked = _s.AllowControl;
    }

    /// <summary>At the top middle of the screen this PC shares, out of the way of title bars.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 6;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }

    private void OnControl(object sender, RoutedEventArgs e) => _s.SetAllowControl(Control.IsChecked == true);

    private void OnStop(object sender, RoutedEventArgs e) => _s.Stop();
}
