using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdaNetPortable.Interop;
using PdaNetPortable.Share;
using PdaNetPortable.Share.Media;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using MouseButton = PdaNetPortable.Share.MouseButton;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using WinScreen = System.Windows.Forms.Screen;

namespace PdaNetPortable.Views;

/// <summary>
/// The other PC's screen, on the PC that views. Pictures go straight from the decoder into one
/// bitmap the GPU scales, always the newest one. The mouse, the keyboard and the clipboard go to
/// the other PC while the other person allows it. Closing the window ends the session, so while
/// one runs the window asks first.
/// </summary>
public partial class RemoteWindow : Window
{
    private readonly ShareSession _s = ShareSession.Instance;
    private readonly DispatcherTimer _moveTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
    private readonly DispatcherTimer _stripTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly KeyboardHook _hook;
    private readonly HashSet<MouseButton> _down = new();
    private readonly Dictionary<byte, ImageSource> _standardPointers = new();
    // The picture: memory WPF draws from as it is, a section shared with it. A WriteableBitmap
    // would keep a second copy of every picture, 8 MB at 1080p.
    private InteropBitmap? _bitmap;
    private IntPtr _section, _pixels;
    private ViewerPipeline? _viewer;
    private bool _fit = true, _full, _ended, _hasPicture, _viewOnly, _renderQueued;
    // The window asks before closing ends the session, and Disconnect closes it without asking.
    private bool _asking, _leaving;
    private WindowState _stateBeforeFull;
    private ushort _mx, _my;
    private bool _movePending;
    private long _lastMove;
    private double _dpi = 1;
    private Quality _quality = Quality.Auto;
    private int _shownScreens = -1, _shownScreen = -1;

    public RemoteWindow()
    {
        InitializeComponent();
        _hook = new KeyboardHook(OnHookKey);
        _moveTimer.Tick += (_, _) => FlushMove();
        _stripTimer.Tick += (_, _) => HideStrip();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.StyleWindow(hwnd);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        };
        DpiChanged += (_, e) =>
        {
            _dpi = e.NewDpi.DpiScaleX;
            if (_bitmap != null) SizePicture(_bitmap.PixelWidth, _bitmap.PixelHeight);
        };
        Fitter.SizeChanged += (_, _) => UpdateScaling();
        Surface.SizeChanged += (_, _) => UpdateScaling();
        Activated += (_, _) => UpdateHook();
        Deactivated += (_, _) =>
        {
            UpdateHook();
            ReleaseAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (!_asking || e.Key != Key.Escape) return;
            OnStay(this, e);
            e.Handled = true;
        };
        _s.Changed += OnChanged;
        _s.PointerChanged += UpdatePointer;
        _s.Ended += OnEnded;
        Closed += (_, _) =>
        {
            _s.Changed -= OnChanged;
            _s.PointerChanged -= UpdatePointer;
            _s.Ended -= OnEnded;
            _hook.Dispose();
            if (_viewer != null) _viewer.FrameReady -= OnFrameReady;
            // A window whose session ended leaves the session that came after it alone.
            if (!_ended && _s.Role == ShareRole.Viewer) _s.Stop();
            Picture.Source = null;
            FreePicture();
        };
        SizeWindow();
        OnChanged();
    }

    /// <summary>Its session ended: the window shows that and follows no other session.</summary>
    public bool HasEnded => _ended;

    public void Reveal()
    {
        if (WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(this);
        Show();
        Activate();
    }

    /// <summary>A window sized to most of this screen, so the other PC's screen is shown big from the start.</summary>
    private void SizeWindow()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, area.Width * 0.82);
        Height = Math.Max(MinHeight, area.Height * 0.86);
    }

    // ============================ Session ============================
    private bool CanControl =>
        _s.Role == ShareRole.Viewer && _s.Phase == SharePhase.Live && !_viewOnly && _s.RemoteFlags.HasFlag(HostFlags.ControlAllowed);

    private void OnChanged()
    {
        if (_ended) return;
        if (_viewer != _s.Viewer)
        {
            if (_viewer != null) _viewer.FrameReady -= OnFrameReady;
            _viewer = _s.Viewer;
            if (_viewer != null) _viewer.FrameReady += OnFrameReady;
        }

        string peer = _s.Peer ?? "the other PC";
        PeerText.Text = _s.Peer ?? "Screen share";
        Title = _s.Peer != null ? $"{_s.Peer} · PdaNet screen share" : "PdaNet screen share";
        StripText.Text = $"{peer} · {ShareSession.PathWords(_s.Path)}";
        Turn.Angle = _s.RemoteRotation;

        // The pill: where the session is and how the link does.
        var (text, key) = _s.Phase switch
        {
            SharePhase.Live => (LiveText(), "B.Accent"),
            SharePhase.Reconnecting => ("Reconnecting", "B.Amber"),
            SharePhase.Pairing => ("Checking the code", "B.Amber"),
            SharePhase.Connecting => ("Connecting", "B.Amber"),
            _ => ("Looking for the other PC", "B.Amber"),
        };
        StatusText.Text = text;
        StatusDot.Fill = Ui.Brush(key);
        var color = ((SolidColorBrush)Ui.Brush(key)).Color;
        StatusPill.Background = new SolidColorBrush(Ui.WithAlpha(color, 0x18));
        StatusPill.BorderBrush = new SolidColorBrush(Ui.WithAlpha(color, 0x55));
        StatusPill.ToolTip = _s.Phase == SharePhase.Live
            ? $"{_s.Stats.Detail}, {_s.Stats.Fps} pictures a second{ShareSession.DetailWords(_s.PathDetail)}"
            : null;

        UpdateScreens();
        UpdateOverlay();
        UpdateBanner();
        UpdateHook();
        UpdatePointer();
    }

    private string LiveText()
    {
        var st = _s.Stats;
        string rate = st.RecvKbps >= 1000 ? $"{st.RecvKbps / 1000.0:0.0} Mbit/s" : $"{st.RecvKbps} kbit/s";
        string path = ShareSession.PathWords(_s.Path);
        return $"{char.ToUpper(path[0])}{path[1..]} · {st.Rtt:0} ms · {rate}";
    }

    private void UpdateScreens()
    {
        var screens = _s.RemoteScreens;
        if (screens.Count == _shownScreens && _s.RemoteScreen == _shownScreen) return;
        _shownScreens = screens.Count;
        _shownScreen = _s.RemoteScreen;
        ScreenChips.Children.Clear();
        ScreensHost.Visibility = screens.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < screens.Count; i++)
        {
            int index = i;
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ScreenChip"),
                GroupName = "RemoteScreens",
                Content = (i + 1).ToString(),
                IsChecked = i == _s.RemoteScreen,
                ToolTip = $"Screen {i + 1}, {screens[i].Width} by {screens[i].Height}{(screens[i].Primary ? ", the main screen" : "")}",
            };
            AutomationProperties.SetName(chip, $"Screen {i + 1}");
            chip.Click += (_, _) => _s.PickRemoteScreen(index);
            ScreenChips.Children.Add(chip);
        }
    }

    private void UpdateOverlay()
    {
        string peer = _s.Peer ?? "the other PC";
        (string Title, string Text, bool Spin)? show = _s.Phase switch
        {
            SharePhase.Idle => null,
            SharePhase.Starting => ("Looking for the other PC", "PdaNet looks for the PC that shows this code", true),
            SharePhase.Pairing => ("Checking the code", "Both PCs prove they know the code, without sending it", true),
            SharePhase.Connecting => ($"Connecting to {peer}", "Finding the shortest way between the two PCs", true),
            SharePhase.Reconnecting => ("Reconnecting", "The connection dropped, PdaNet is finding a new way. The session goes on by itself", true),
            SharePhase.Live when !_hasPicture => ("Waiting for the first picture", $"{peer} is getting its screen ready", true),
            SharePhase.Live when _s.RemoteFlags.HasFlag(HostFlags.Hidden) => ($"{peer} shows a private screen",
                "Windows keeps some screens to itself, such as a permission prompt or the lock screen. The picture comes back when it closes", false),
            _ => null,
        };
        if (show == null)
        {
            Overlay.Visibility = Visibility.Collapsed;
            OverlaySpin.BeginAnimation(RotateTransform.AngleProperty, null);
            return;
        }
        ShowOverlay(show.Value.Title, show.Value.Text, show.Value.Spin, button: false);
    }

    private void ShowOverlay(string title, string text, bool spin, bool button, string glyph = "", string glyphKey = "B.Accent")
    {
        Overlay.Visibility = Visibility.Visible;
        OverlayTitle.Text = title;
        OverlayText.Text = text;
        OverlaySpinner.Visibility = spin ? Visibility.Visible : Visibility.Collapsed;
        OverlayIcon.Visibility = spin ? Visibility.Collapsed : Visibility.Visible;
        OverlayGlyph.Text = glyph;
        OverlayGlyph.Foreground = Ui.Brush(glyphKey);
        OverlaySpin.BeginAnimation(RotateTransform.AngleProperty, spin ? Ui.Loop(0, 360, 1.0, 60) : null);
        OverlayBtn.Visibility = button ? Visibility.Visible : Visibility.Collapsed;
        // A picture that is there stays visible under a short drop.
        Overlay.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(_hasPicture && !button ? 0x88 : 0xCC), 5, 7, 10));
    }

    private void UpdateBanner()
    {
        string peer = _s.Peer ?? "The other PC";
        string? text = _s.Phase != SharePhase.Live || !_hasPicture ? null
            : !_s.RemoteFlags.HasFlag(HostFlags.ControlAllowed) ? $"{peer} lets you watch only, their mouse and keyboard stay theirs"
            : _viewOnly ? "Watch only, turn on the mouse and keyboard under the keyboard button"
            : null;
        Banner.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        BannerText.Text = text ?? "";
    }

    private void OnEnded(string text, bool error)
    {
        _ended = true;
        _asking = false;
        Ask.Visibility = Visibility.Collapsed;
        _hook.Stop();
        _moveTimer.Stop();
        RemotePointer.Visibility = Visibility.Collapsed;
        Banner.Visibility = Visibility.Collapsed;
        InputLayer.Cursor = null;
        StatusText.Text = "Ended";
        StatusDot.Fill = Ui.Brush("B.Text3");
        if (_full) SetFull(false);
        ShowOverlay(error ? "The session ended" : "Session ended", text, spin: false, button: true,
            glyph: error ? "" : "", glyphKey: error ? "B.Amber" : "B.Accent");
    }

    // ============================ Pictures ============================
    private void OnFrameReady()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, Present);
    }

    /// <summary>Draws the newest picture: NV12 straight into the bitmap's own memory, the GPU scales it.</summary>
    private unsafe void Present()
    {
        _renderQueued = false;
        if (_ended || _s.Viewer is not { } viewer) return;
        var frame = viewer.Take();
        if (frame == null) return;
        if (_bitmap == null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            FreePicture();
            uint size = (uint)(frame.Width * frame.Height * 4);
            _section = ShareNative.CreateFileMapping(new IntPtr(-1), IntPtr.Zero, ShareNative.PAGE_READWRITE, 0, size, null);
            _pixels = _section == IntPtr.Zero ? IntPtr.Zero : ShareNative.MapViewOfFile(_section, ShareNative.FILE_MAP_ALL_ACCESS, 0, 0, size);
            if (_pixels == IntPtr.Zero)
            {
                FreePicture();
                return;
            }
            _bitmap = (InteropBitmap)Imaging.CreateBitmapSourceFromMemorySection(_section, frame.Width, frame.Height, PixelFormats.Bgr32, frame.Width * 4, 0);
            Picture.Source = _bitmap;
            SizePicture(frame.Width, frame.Height);
            if (!_hasPicture) FitFirst(frame.Width, frame.Height);
        }
        ColorConvert.Nv12ToBgra(frame, (byte*)_pixels, frame.Width * 4);
        _bitmap.Invalidate();
        if (!_hasPicture)
        {
            _hasPicture = true;
            UpdateOverlay();
            UpdateBanner();
            UpdatePointer();
        }
    }

    /// <summary>One pixel of the picture is one pixel of this screen at actual size.</summary>
    private void SizePicture(int width, int height)
    {
        Picture.Width = width / _dpi;
        Picture.Height = height / _dpi;
        UpdateScaling();
    }

    /// <summary>
    /// How the GPU scales the picture. Shown smaller, high quality (Fant) counts every pixel of
    /// the picture into the pixel on screen, where linear filtering skips some and thin text
    /// breaks up. At actual size, nearest neighbor keeps each pixel as it is, even where the
    /// picture sits a fraction of a pixel off. Shown larger, linear is smooth enough.
    /// </summary>
    private void UpdateScaling()
    {
        if (_bitmap == null || Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0) return;
        double shown = !_fit ? 1 : Math.Min(Fitter.ActualWidth / Surface.ActualWidth, Fitter.ActualHeight / Surface.ActualHeight);
        var mode = Math.Abs(shown - 1) < 0.01 ? BitmapScalingMode.NearestNeighbor
            : shown < 1 ? BitmapScalingMode.HighQuality
            : BitmapScalingMode.Linear;
        if (RenderOptions.GetBitmapScalingMode(Picture) != mode) RenderOptions.SetBitmapScalingMode(Picture, mode);
    }

    private void FreePicture()
    {
        _bitmap = null;
        if (_pixels != IntPtr.Zero) ShareNative.UnmapViewOfFile(_pixels);
        if (_section != IntPtr.Zero) ShareNative.CloseHandle(_section);
        _pixels = _section = IntPtr.Zero;
    }

    /// <summary>The first picture shapes the window: the other PC's screen as big as it fits, without bars.</summary>
    private void FitFirst(int width, int height)
    {
        if (WindowState != WindowState.Normal || _full) return;
        var area = SystemParameters.WorkArea;
        bool turned = _s.RemoteRotation is 90 or 270;
        double w = (turned ? height : width) / _dpi, h = (turned ? width : height) / _dpi;
        double scale = Math.Min(1, Math.Min(area.Width * 0.9 / w, (area.Height * 0.9 - BarRow.ActualHeight) / h));
        Width = Math.Max(MinWidth, w * scale + 2);
        Height = Math.Max(MinHeight, h * scale + BarRow.ActualHeight + 2);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    // ============================ Pointer ============================
    /// <summary>
    /// While this PC controls the other, this PC's own pointer takes the other's shape, so it moves
    /// without any delay. While it only watches, the other pointer is drawn where it is.
    /// </summary>
    private void UpdatePointer()
    {
        if (_ended) return;
        if (CanControl)
        {
            RemotePointer.Visibility = Visibility.Collapsed;
            InputLayer.Cursor = RemoteCursor();
            return;
        }
        InputLayer.Cursor = null;
        if (!_hasPicture || !_s.PointerVisible || _s.Phase != SharePhase.Live)
        {
            RemotePointer.Visibility = Visibility.Collapsed;
            return;
        }
        var (image, w, h, hx, hy) = PointerImage();
        if (image == null)
        {
            RemotePointer.Visibility = Visibility.Collapsed;
            return;
        }
        // Pointer pixels are the other PC's pixels, the same scale as the picture.
        RemotePointer.Source = image;
        RemotePointer.Width = w / _dpi;
        RemotePointer.Height = h / _dpi;
        Canvas.SetLeft(RemotePointer, _s.PointerX / 65535.0 * Surface.ActualWidth - hx / _dpi);
        Canvas.SetTop(RemotePointer, _s.PointerY / 65535.0 * Surface.ActualHeight - hy / _dpi);
        RemotePointer.Visibility = Visibility.Visible;
    }

    private Cursor RemoteCursor()
    {
        if (_s.RemotePicture is { } p)
        {
            try { return p.ToCursor(); }
            catch { return Cursors.Arrow; }
        }
        return CursorShapes.WpfStandard(_s.RemoteStandard) ?? Cursors.Arrow;
    }

    private (ImageSource? Image, int W, int H, int HotX, int HotY) PointerImage()
    {
        if (_s.RemotePicture is { } p) return (p.ToImage(), p.Width, p.Height, p.HotX, p.HotY);
        byte id = _s.RemoteStandard == 0 ? (byte)1 : _s.RemoteStandard;
        if (!_standardPointers.TryGetValue(id, out var img))
        {
            var own = CursorShapes.Read(CursorShapes.StandardHandle(id));
            if (own == null) return (null, 0, 0, 0, 0);
            _standardPointers[id] = img = own.ToImage();
            _standardHot[id] = (own.Width, own.Height, own.HotX, own.HotY);
        }
        var (w, h, hx, hy) = _standardHot[id];
        return (img, w, h, hx, hy);
    }

    private readonly Dictionary<byte, (int W, int H, int HotX, int HotY)> _standardHot = new();

    // ============================ Mouse ============================
    /// <summary>Where a point on the picture is on the other PC's screen, 0 to 65535 each way.</summary>
    private (ushort X, ushort Y) Norm(Point p)
    {
        double w = Math.Max(1, Surface.ActualWidth), h = Math.Max(1, Surface.ActualHeight);
        return ((ushort)Math.Clamp(p.X / w * 65535, 0, 65535), (ushort)Math.Clamp(p.Y / h * 65535, 0, 65535));
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_full)
        {
            if (e.GetPosition(Stage).Y <= 2) ShowStrip();
        }
        if (!CanControl) return;
        (_mx, _my) = Norm(e.GetPosition(InputLayer));
        _movePending = true;
        // Moves go at most about 120 times a second, the newest one wins.
        long now = Environment.TickCount64;
        if (now - _lastMove >= 8) FlushMove();
        else if (!_moveTimer.IsEnabled) _moveTimer.Start();
    }

    private void FlushMove()
    {
        _moveTimer.Stop();
        if (!_movePending) return;
        _movePending = false;
        _lastMove = Environment.TickCount64;
        _s.Send(MsgWriter.Move(_mx, _my), live: true);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanControl) return;
        InputLayer.Focus();
        if (Map(e.ChangedButton) is not { } button) return;
        var (x, y) = Norm(e.GetPosition(InputLayer));
        _movePending = false;
        _down.Add(button);
        InputLayer.CaptureMouse();
        _s.Send(MsgWriter.Button(button, true, x, y));
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Map(e.ChangedButton) is not { } button || !_down.Remove(button)) return;
        var (x, y) = Norm(e.GetPosition(InputLayer));
        _s.Send(MsgWriter.Button(button, false, x, y));
        if (_down.Count == 0) InputLayer.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!CanControl) return;
        var (x, y) = Norm(e.GetPosition(InputLayer));
        _s.Send(MsgWriter.Wheel((short)e.Delta, 0, x, y));
        e.Handled = true;
    }

    private static MouseButton? Map(System.Windows.Input.MouseButton b) => b switch
    {
        System.Windows.Input.MouseButton.Left => MouseButton.Left,
        System.Windows.Input.MouseButton.Right => MouseButton.Right,
        System.Windows.Input.MouseButton.Middle => MouseButton.Middle,
        System.Windows.Input.MouseButton.XButton1 => MouseButton.X1,
        System.Windows.Input.MouseButton.XButton2 => MouseButton.X2,
        _ => null,
    };

    /// <summary>The horizontal wheel, which WPF does not pass on.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ShareNative.WM_MOUSEHWHEEL && CanControl && InputLayer.IsMouseOver)
        {
            short delta = (short)((long)wParam >> 16);
            var (x, y) = Norm(Mouse.GetPosition(InputLayer));
            _s.Send(MsgWriter.Wheel(0, delta, x, y));
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Lets go of every key and button on the other PC, when this window loses focus.</summary>
    private void ReleaseAll()
    {
        if (_down.Count > 0)
        {
            _down.Clear();
            InputLayer.ReleaseMouseCapture();
        }
        if (_s.Role == ShareRole.Viewer && _s.Phase == SharePhase.Live) _s.Send(MsgWriter.Bare(Msg.ReleaseAll));
    }

    // ============================ Keyboard ============================
    private void UpdateHook()
    {
        if (IsActive && CanControl && !_ended && !_asking) _hook.Start();
        else _hook.Stop();
    }

    private const byte VkTab = 0x09, VkEscape = 0x1B, VkLWin = 0x5B, VkRWin = 0x5C, VkCancel = 0x03, VkPause = 0x13;
    private const byte VkControl = 0x11, VkMenu = 0x12;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Each key while this window is in front: sent to the other PC and kept from this one.</summary>
    private bool OnHookKey(KeyboardHook.Key k)
    {
        if (!IsActive || !CanControl) return false;
        // Ctrl+Alt+Break turns full screen on and off, like Remote Desktop. It stays on this PC.
        if ((k.Vk == VkCancel || k.Vk == VkPause) && Held(VkControl) && Held(VkMenu))
        {
            if (k.Down) Dispatcher.BeginInvoke(() => SetFull(!_full));
            return true;
        }
        // In a window, the shortcuts that switch apps on this PC stay on this PC. Full screen sends
        // them all, like Remote Desktop does.
        if (!_full && (k.Vk is VkLWin or VkRWin || (k.Vk == VkTab && k.Alt) || (k.Vk == VkEscape && (k.Alt || Held(VkControl)))))
            return false;
        _s.Send(MsgWriter.Key(k.Scan, k.Down, k.Extended, k.Vk));
        return true;
    }

    /// <summary>A key combination, pressed and let go on the other PC: (scan code, extended, virtual key).</summary>
    private void SendCombo(params (ushort Scan, bool Ext, byte Vk)[] keys)
    {
        foreach (var k in keys) _s.Send(MsgWriter.Key(k.Scan, true, k.Ext, k.Vk));
        foreach (var k in keys.Reverse()) _s.Send(MsgWriter.Key(k.Scan, false, k.Ext, k.Vk));
    }

    private static readonly (ushort, bool, byte) Ctrl = (0x1D, false, 0x11), Shift = (0x2A, false, 0x10), Alt = (0x38, false, 0x12);
    private static readonly (ushort, bool, byte) Win = (0x5B, true, 0x5B), Esc = (0x01, false, 0x1B), Tab = (0x0F, false, 0x09);
    private static readonly (ushort, bool, byte) PrintScreen = (0x37, true, 0x2C);

    private void OnKeysMenu(object sender, RoutedEventArgs e)
    {
        var menu = KeysBtn.ContextMenu!;
        menu.Items.Clear();
        bool allowed = _s.RemoteFlags.HasFlag(HostFlags.ControlAllowed);
        var control = new MenuItem
        {
            Header = new TextBlock { Text = allowed ? "Use the mouse and keyboard" : "Use the mouse and keyboard, not allowed" },
            IsCheckable = true,
            IsChecked = allowed && !_viewOnly,
            IsEnabled = allowed,
        };
        control.Click += (_, _) =>
        {
            _viewOnly = !control.IsChecked;
            if (_viewOnly) ReleaseAll();
            UpdateHook();
            UpdateBanner();
            UpdatePointer();
        };
        menu.Items.Add(control);
        menu.Items.Add(new Separator());
        foreach (var (text, keys) in new (string, (ushort, bool, byte)[])[]
                 {
                     ("Start menu  (Windows key)", new[] { Win }),
                     ("Switch apps  (Alt+Tab)", new[] { Alt, Tab }),
                     ("Task Manager  (Ctrl+Shift+Esc)", new[] { Ctrl, Shift, Esc }),
                     ("Print Screen", new[] { PrintScreen }),
                 })
        {
            var item = new MenuItem { Header = new TextBlock { Text = text }, IsEnabled = CanControl };
            item.Click += (_, _) => SendCombo(keys);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = KeysBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnQualityMenu(object sender, RoutedEventArgs e)
    {
        var menu = QualityBtn.ContextMenu!;
        menu.Items.Clear();
        foreach (var (q, text) in new[]
                 {
                     (Quality.Auto, "Balanced, follows the connection"),
                     (Quality.Sharp, "Sharp text, for documents and code"),
                     (Quality.Smooth, "Smooth motion, for video and scrolling"),
                 })
        {
            var item = new MenuItem { Header = new TextBlock { Text = text }, IsCheckable = true, IsChecked = _quality == q };
            item.Click += (_, _) =>
            {
                _quality = q;
                _s.SetRemoteQuality(q);
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = QualityBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ============================ View ============================
    private void OnFit(object sender, RoutedEventArgs e)
    {
        _fit = !_fit;
        Fitter.Stretch = _fit ? Stretch.Uniform : Stretch.None;
        Scroller.HorizontalScrollBarVisibility = Scroller.VerticalScrollBarVisibility =
            _fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        FitBtn.Content = _fit ? "" : "";
        FitBtn.ToolTip = _fit ? "Actual size" : "Fit to the window";
        AutomationProperties.SetName(FitBtn, (string)FitBtn.ToolTip);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdatePointer);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateScaling);
    }

    private void OnFull(object sender, RoutedEventArgs e) => SetFull(!_full);

    private void SetFull(bool on)
    {
        if (_full == on) return;
        _full = on;
        HideStrip();
        if (on)
        {
            _stateBeforeFull = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            Bar.Visibility = Visibility.Collapsed;
            BarRow.Height = new GridLength(0);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFull == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            Bar.Visibility = Visibility.Visible;
            BarRow.Height = new GridLength(40);
        }
        FullBtn.Content = on ? "" : "";
        ApplyFrame();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdatePointer);
    }

    private void ShowStrip()
    {
        FullStrip.Visibility = Visibility.Visible;
        _stripTimer.Stop();
        _stripTimer.Start();
    }

    private void HideStrip()
    {
        _stripTimer.Stop();
        if (!FullStrip.IsMouseOver) FullStrip.Visibility = Visibility.Collapsed;
    }

    private void OnStripLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _stripTimer.Stop();
        _stripTimer.Start();
    }

    private void OnLeave(object sender, RoutedEventArgs e)
    {
        _leaving = true;
        Close();
    }

    /// <summary>Closing the window ends the session, so while one runs the window asks first.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _leaving || !Running) return;
        e.Cancel = true;
        // Asked once the close is called off, a window that is closing cannot be shown.
        Dispatcher.BeginInvoke(AskLeave);
    }

    // Still looking for the other PC, closing loses nothing, so only a session that runs asks.
    private bool Running => !_ended && _s.Role == ShareRole.Viewer && _s.Phase is SharePhase.Live or SharePhase.Reconnecting;

    private void AskLeave()
    {
        if (!Running) return;
        Reveal();
        if (_asking) return;
        _asking = true;
        string peer = _s.Peer ?? "the other PC";
        AskTitle.Text = $"Disconnect from {peer}?";
        AskText.Text = _s.RemoteFlags.HasFlag(HostFlags.EasyAccess)
            ? $"Closing this window ends the session. {peer} shares with easy access, so you can connect again from Recent sessions on the Launchpad"
            : $"Closing this window ends the session. To see {peer} again, they share again and give you a new code";
        Ask.Visibility = Visibility.Visible;
        // The keys go to this window while it asks, not to the other PC.
        ReleaseAll();
        UpdateHook();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => AskLeaveBtn.Focus());
    }

    private void OnStay(object sender, RoutedEventArgs e)
    {
        _asking = false;
        Ask.Visibility = Visibility.Collapsed;
        UpdateHook();
        InputLayer.Focus();
    }

    // ============================ Window chrome ============================
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        ApplyFrame();
    }

    private void ApplyFrame()
    {
        bool max = WindowState == WindowState.Maximized;
        // A maximized custom chrome window hangs over the screen edge by its resize frame.
        var t = SystemParameters.WindowResizeBorderThickness;
        Frame.Margin = max && !_full ? new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4) : new Thickness(0);
        Frame.BorderThickness = new Thickness(max || _full ? 0 : 1);
        MaxBtn.Content = max ? "" : "";
        MaxBtn.ToolTip = max ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaxBtn, max ? "Restore" : "Maximize");
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
