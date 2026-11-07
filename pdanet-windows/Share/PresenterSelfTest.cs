using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdaNetPortable.Share.Media;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;

namespace PdaNetPortable.Share;

/// <summary>
/// The viewer's GPU presenter, checked without a session: its conversion against ColorConvert's
/// scalar reference for every Y, U and V there is; its placement at actual size, turned each way,
/// pixel for pixel; the viewer's own layout drawn by WPF next to the same layout drawn by the
/// GPU, fitted, enlarged, scrolled and turned; what a picture costs the presenting thread next to
/// WPF's path; and a real swap chain with its thread, on a window of its own. Skips, and passes,
/// where this PC's GPU cannot present: there the viewer draws with WPF.
/// </summary>
internal static unsafe class PresenterSelfTest
{
    public static string Run()
    {
        var renderer = Nv12Renderer.Create(out string why);
        if (renderer == null) return $"skipped, the viewer draws with WPF here: {why}";
        using (renderer)
        {
            var lines = new List<string> { $"gpu       {renderer.Adapter}" };
            lines.Add($"colors    all 16,777,216 Y/U/V combinations: {Exhaustive(renderer)}");
            lines.Add($"place     {Placements(renderer)}");
            lines.Add($"layout    {AgainstWpf(renderer)}");
            lines.AddRange(Timing(renderer));
            lines.Add($"present   {SwapChain()}");
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// In 64 pictures of 512 by 512 each color sample is one U/V pair, all 65,536 of them, and its
    /// four brightness samples step through all 256 values over the pictures.
    /// </summary>
    private static string Exhaustive(Nv12Renderer r)
    {
        const int size = 512;
        using var frame = new Nv12Frame(size, size);
        var cpu = new byte[size * size * 4];
        long off = 0;
        int worst = 0;
        for (int pass = 0; pass < 64; pass++)
        {
            byte* y = frame.Data, uv = frame.UV;
            for (int row = 0; row < size; row++)
            for (int x = 0; x < size; x++)
                y[row * size + x] = (byte)(pass * 4 + (row & 1) * 2 + (x & 1));
            for (int row = 0; row < size / 2; row++)
            for (int x = 0; x < size / 2; x++)
            {
                uv[row * size + x * 2] = (byte)x;
                uv[row * size + x * 2 + 1] = (byte)row;
            }
            r.Upload(frame);
            var gpu = r.ReadPicture();
            fixed (byte* c = cpu) ColorConvert.Nv12ToBgra(frame, c, size * 4, useSimd: false);
            for (int i = 0; i < cpu.Length; i++)
            {
                int d = Math.Abs(gpu[i] - cpu[i]);
                if (d == 0) continue;
                off++;
                worst = Math.Max(worst, d);
            }
        }
        if (worst > 1) throw new Exception($"the GPU's colors differ from ColorConvert by up to {worst} levels, in {off} bytes");
        return off == 0 ? "the same bytes as ColorConvert" : $"{off} bytes 1 level off ColorConvert, the rest the same";
    }

    /// <summary>At actual size every pixel of the target is one of the picture, turned or not, and the rest black.</summary>
    private static string Placements(Nv12Renderer r)
    {
        var random = new Random(0x51DE);
        int cases = 0;
        foreach (var (w, h) in new[] { (2, 2), (34, 18), (322, 1202), (1366, 768), (1920, 1080) })
        {
            using var frame = new Nv12Frame(w, h);
            random.NextBytes(frame.AsSpan());
            var reference = new byte[w * h * 4];
            fixed (byte* c = reference) ColorConvert.Nv12ToBgra(frame, c, w * 4, useSimd: false);
            r.Upload(frame);
            foreach (int turn in new[] { 0, 90, 180, 270 })
            {
                bool side = turn is 90 or 270;
                int bw = side ? h : w, bh = side ? w : h;
                const int x0 = 3, y0 = 2;
                int tw = bw + 7, th = bh + 5;
                // Turned clockwise, as WPF's RotateTransform turns: the picture's top left corner
                // goes to the box's top right at 90 degrees.
                var place = turn switch
                {
                    0 => new PicturePlacement(x0, y0, w, 0, 0, h, w, h),
                    90 => new PicturePlacement(x0 + h, y0, 0, w, -h, 0, w, h),
                    180 => new PicturePlacement(x0 + w, y0 + h, -w, 0, 0, -h, w, h),
                    _ => new PicturePlacement(x0, y0 + w, 0, -w, h, 0, w, h),
                };
                var got = DrawOffscreen(r, tw, th, place);
                for (int ty = 0; ty < th; ty++)
                for (int tx = 0; tx < tw; tx++)
                {
                    int bx = tx - x0, by = ty - y0;
                    uint expected = 0xFF000000;
                    if (bx >= 0 && by >= 0 && bx < bw && by < bh)
                    {
                        var (px, py) = turn switch
                        {
                            0 => (bx, by),
                            90 => (by, h - 1 - bx),
                            180 => (w - 1 - bx, h - 1 - by),
                            _ => (w - 1 - by, bx),
                        };
                        expected = BitConverter.ToUInt32(reference, (py * w + px) * 4);
                    }
                    uint actual = BitConverter.ToUInt32(got, (ty * tw + tx) * 4);
                    if (actual != expected)
                        throw new Exception($"{w}x{h} turned {turn}: pixel ({tx},{ty}) is {actual:X8}, not {expected:X8}");
                }
                cases++;
            }
        }
        return $"{cases} pictures at actual size, 2x2 to 1920x1080, turned each way: pixel for pixel, black around";
    }

    /// <summary>
    /// The viewer's layout, a Viewbox in a ScrollViewer around a turned Image, drawn by WPF's own
    /// renderer and by the GPU at PicturePlacement.FromLayout. A wrong corner, turn, flip, scale or
    /// scroll moves whole blocks; the filters differ only at the blocks' edges.
    /// </summary>
    private static string AgainstWpf(Nv12Renderer r)
    {
        const int w = 640, h = 360;
        using var frame = new Nv12Frame(w, h);
        // Blocks of 40 by 40, each its own color, so every part of the picture is told apart.
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            frame.Data[y * w + x] = (byte)(40 + (x / 40 * 37 + y / 40 * 53) % 180);
        for (int y = 0; y < h / 2; y++)
        for (int x = 0; x < w / 2; x++)
        {
            frame.UV[y * w + x * 2] = (byte)(60 + (x / 20 * 29) % 140);
            frame.UV[y * w + x * 2 + 1] = (byte)(60 + (y / 20 * 41 + x / 20 * 13) % 140);
        }
        var bgra = new byte[w * h * 4];
        fixed (byte* c = bgra) ColorConvert.Nv12ToBgra(frame, c, w * 4, useSimd: false);
        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bgra, w * 4);
        source.Freeze();
        r.Upload(frame);

        var results = new List<string>();
        foreach (var (turn, fit, stageW, stageH, dpi) in new[]
                 {
                     (0, true, 501, 333, 1.0), (90, true, 501, 333, 1.0), (180, true, 501, 333, 1.0), (270, true, 501, 333, 1.0),
                     (0, true, 1300, 900, 1.0), (90, true, 900, 1300, 1.0),
                     (0, false, 500, 300, 1.0), (90, false, 300, 500, 1.0), (270, false, 300, 500, 1.0),
                     // A screen scaled to 150 % and 125 %: the window's pixels are not WPF's units.
                     (0, true, 334, 222, 1.5), (90, true, 334, 222, 1.5), (0, false, 400, 240, 1.5), (180, true, 401, 267, 1.25),
                 })
        {
            // As RemoteWindow.SizePicture: one pixel of the picture is one of the screen at actual size.
            var picture = new Image { Source = source, Stretch = Stretch.Fill, Width = w / dpi, Height = h / dpi, LayoutTransform = new RotateTransform(turn) };
            var surface = new Grid();
            surface.Children.Add(picture);
            var fitter = new Viewbox { Stretch = fit ? Stretch.Uniform : Stretch.None, StretchDirection = StretchDirection.Both, Child = surface };
            var bars = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
            var scroller = new ScrollViewer { Content = fitter, HorizontalScrollBarVisibility = bars, VerticalScrollBarVisibility = bars };
            var host = new Border(); // stands for the GPU's window, over the scroller
            var stage = new Grid { Background = System.Windows.Media.Brushes.Black, Width = stageW, Height = stageH };
            stage.Children.Add(scroller);
            stage.Children.Add(host);
            stage.Measure(new Size(stageW, stageH));
            stage.Arrange(new Rect(0, 0, stageW, stageH));
            stage.UpdateLayout();
            if (!fit)
            {
                // Whole pixels at 150 % too, as the window's layout rounding keeps them.
                scroller.ScrollToHorizontalOffset(38);
                scroller.ScrollToVerticalOffset(24);
                stage.UpdateLayout();
            }
            var place = PicturePlacement.FromLayout(picture, host, new Matrix(dpi, 0, 0, dpi, 0, 0), w, h)
                        ?? throw new Exception($"no placement for turn {turn}");
            // The scale mode RemoteWindow.UpdateScaling gives the picture at this size.
            double shown = Math.Sqrt(place.UX * place.UX + place.UY * place.UY) / w;
            RenderOptions.SetBitmapScalingMode(picture, Math.Abs(shown - 1) < 0.01 ? BitmapScalingMode.NearestNeighbor
                : shown < 1 ? BitmapScalingMode.HighQuality : BitmapScalingMode.Linear);
            int pw = (int)Math.Round(stageW * dpi), ph = (int)Math.Round(stageH * dpi);
            var rtb = new RenderTargetBitmap(pw, ph, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            rtb.Render(stage);
            var wpf = new byte[pw * ph * 4];
            rtb.CopyPixels(wpf, pw * 4, 0);
            var gpu = DrawOffscreen(r, pw, ph, place);
            long gross = 0, sum = 0;
            for (int i = 0; i < wpf.Length; i += 4)
            {
                int d = Math.Max(Math.Abs(wpf[i] - gpu[i]), Math.Max(Math.Abs(wpf[i + 1] - gpu[i + 1]), Math.Abs(wpf[i + 2] - gpu[i + 2])));
                sum += d;
                if (d > 40) gross++;
            }
            long pixels = wpf.Length / 4;
            double grossShare = (double)gross / pixels, mean = (double)sum / pixels;
            // At actual size both take each pixel as it is; scaled, the filters differ at the block edges.
            bool exact = Math.Abs(shown - 1) < 0.01;
            if (exact ? gross > 0 || mean > 0.01 : grossShare > 0.04 || mean > 4)
                throw new Exception($"turn {turn}, {(fit ? "fitted" : "actual size, scrolled")} into {pw}x{ph} at {dpi:P0}: the GPU's picture differs from WPF's, " +
                                    $"{grossShare:P1} of pixels far off, {mean:0.00} levels on average");
            results.Add($"{turn}{(fit ? "" : " scrolled")}{(dpi != 1 ? $" at {dpi:P0}" : "")} {shown:0.00}x: {grossShare:P1} far off, {mean:0.0} levels");
        }
        return "WPF and the GPU agree: " + string.Join("; ", results);
    }

    /// <summary>What a picture costs: WPF's path on the window's thread, the presenter's on its own.</summary>
    private static List<string> Timing(Nv12Renderer r)
    {
        var lines = new List<string>();
        foreach (var (w, h) in new[] { (1920, 1080), (3840, 2160) })
        {
            using var frame = new Nv12Frame(w, h);
            new Random(7).NextBytes(frame.AsSpan());
            const int n = 30;
            double wpfMs, convertMs = 0;
            uint size = (uint)(w * h * 4);
            var section = ShareNative.CreateFileMapping(new IntPtr(-1), IntPtr.Zero, ShareNative.PAGE_READWRITE, 0, size, null);
            var pixels = section == IntPtr.Zero ? IntPtr.Zero : ShareNative.MapViewOfFile(section, ShareNative.FILE_MAP_ALL_ACCESS, 0, 0, size);
            if (pixels == IntPtr.Zero) throw new Exception("no memory section for the bitmap");
            try
            {
                var bitmap = (InteropBitmap)Imaging.CreateBitmapSourceFromMemorySection(section, w, h, PixelFormats.Bgr32, w * 4, 0);
                for (int i = 0; i < 5; i++)
                {
                    ColorConvert.Nv12ToBgra(frame, (byte*)pixels, w * 4);
                    bitmap.Invalidate();
                }
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < n; i++)
                {
                    long t = Stopwatch.GetTimestamp();
                    ColorConvert.Nv12ToBgra(frame, (byte*)pixels, w * 4);
                    convertMs += Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                    bitmap.Invalidate();
                }
                wpfMs = sw.Elapsed.TotalMilliseconds / n;
                convertMs /= n;
            }
            finally
            {
                ShareNative.UnmapViewOfFile(pixels);
                ShareNative.CloseHandle(section);
            }

            const int tw = 1600, th = 900;
            using var target = r.Device.CreateTexture2D(Target(tw, th));
            using var view = r.Device.CreateRenderTargetView(target);
            var place = PicturePlacement.Fitted(w, h, tw, th);
            using var query = r.Device.CreateQuery(new QueryDescription(QueryType.Event));
            void Frame(bool wait)
            {
                r.Upload(frame);
                r.Draw(view, tw, th, place);
                r.Context.End(query);
                r.Context.Flush();
                if (!wait) return;
                long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                while (r.Context.GetData(query, IntPtr.Zero, 0, AsyncGetDataFlags.None) == Result.False && Stopwatch.GetTimestamp() < until) Thread.SpinWait(64);
            }
            for (int i = 0; i < 5; i++) Frame(true);
            // The processor's share: the GPU catches up between pictures, as between a screen's frames.
            double cpuOnly = 0;
            for (int i = 0; i < n; i++)
            {
                long t = Stopwatch.GetTimestamp();
                Frame(false);
                cpuOnly += Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                while (r.Context.GetData(query, IntPtr.Zero, 0, AsyncGetDataFlags.DoNotFlush) == Result.False) Thread.SpinWait(64);
            }
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) Frame(true);
            double doneMs = watch.Elapsed.TotalMilliseconds / n;
            lines.Add($"cost      {w}x{h}: WPF's path {wpfMs:0.00} ms of the window's thread a picture ({convertMs:0.00} to convert, " +
                      $"{wpfMs - convertMs:0.00} in Invalidate), then WPF copies the {w * h * 4 / 1048576.0:0} MB bitmap to the GPU on its " +
                      $"render thread (not counted); the presenter {cpuOnly / n:0.00} ms of its own thread, the picture drawn on the GPU " +
                      $"{doneMs:0.00} ms after its copy starts");
        }
        return lines;
    }

    private static Texture2DDescription Target(int w, int h) => new()
    {
        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
    };

    private static byte[] DrawOffscreen(Nv12Renderer r, int w, int h, PicturePlacement place)
    {
        using var target = r.Device.CreateTexture2D(Target(w, h));
        using var view = r.Device.CreateRenderTargetView(target);
        r.Draw(view, w, h, place);
        return r.ReadTexture(target, w, h);
    }

    /// <summary>The presenter itself, its swap chain and its thread, on a window of its own off every screen.</summary>
    private static string SwapChain()
    {
        var hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "STATIC", "", WS_POPUP | WS_VISIBLE,
            -32000, -32000, 640, 360, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new Exception("no window for the swap chain");
        try
        {
            SwapChainPresenter presenter;
            try { presenter = SwapChainPresenter.Create(hwnd); }
            catch (Exception e) when (e is NotSupportedException or SharpGenException)
            {
                return $"skipped, no swap chain here: {e.Message.Split('\n')[0]}";
            }
            using (presenter)
            {
                string? lost = null;
                (int W, int H)? asked = null;
                presenter.Lost += why => lost = why;
                presenter.NewSize += (w, h) => asked = (w, h);
                using var source = new TestSource(640, 360);
                presenter.Start(source, PicturePlacement.Fitted(640, 360, 640, 360));
                void Pictures(int n)
                {
                    for (int i = 0; i < n; i++)
                    {
                        source.Next();
                        Thread.Sleep(17);
                    }
                }
                Pictures(20);
                long until = Environment.TickCount64 + 2000;
                while (presenter.Presented == 0 && lost == null && Environment.TickCount64 < until) Thread.Sleep(10);
                long first = presenter.Presented;
                // The other PC changes its resolution: the window is told, and the pictures go on meanwhile.
                source.Resize(480, 270);
                Pictures(10);
                long whileAsking = presenter.Presented - first;
                presenter.Place(PicturePlacement.Fitted(480, 270, 640, 360));
                Pictures(10);
                long afterPlace = presenter.Presented - first - whileAsking;
                presenter.Stop();
                if (lost != null) throw new Exception($"the presenter stopped: {lost}");
                if (first == 0) throw new Exception("the presenter presented nothing in 2 s");
                if (asked != (480, 270)) throw new Exception($"a new size was not told to the window ({asked})");
                if (whileAsking == 0 || afterPlace == 0) throw new Exception($"presenting stopped at the new size ({whileAsking}, then {afterPlace})");
                var (read, draw, present) = presenter.Cost();
                return $"a flip model swap chain and its thread, in a window off screen: {first} presents for 20 pictures of 640x360 " +
                       $"(and the newest at the start); at 480x270 it told the window the new size and presented {whileAsking}, " +
                       $"then {afterPlace} once placed again; a frame costs that thread {read:0.00} ms to take and copy, " +
                       $"{draw:0.00} ms to draw, {present:0.00} ms to present";
            }
        }
        finally { DestroyWindow(hwnd); }
    }

    /// <summary>Pictures of a gray that changes, as a viewer's pipeline would hand them.</summary>
    internal sealed class TestSource : IPictureSource, IDisposable
    {
        private Nv12Frame _frame;
        private bool _new;
        private int _n;

        public TestSource(int w, int h) => _frame = new Nv12Frame(w, h);

        public event Action? FrameReady;

        /// <summary>Pictures of another size from now on, as when the other PC changes its resolution.</summary>
        public void Resize(int w, int h)
        {
            lock (this)
            {
                _frame.Dispose();
                _frame = new Nv12Frame(w, h);
            }
        }

        public void Next()
        {
            lock (this)
            {
                _frame.AsSpan()[..(_frame.Width * _frame.Height)].Fill((byte)(80 + _n++ * 37 % 140));
                _new = true;
            }
            FrameReady?.Invoke();
        }

        public bool Read(bool newest, Action<Nv12Frame> read)
        {
            lock (this)
            {
                if (!_new && !newest) return false;
                _new = false;
                read(_frame);
                return true;
            }
        }

        public void Dispose() => _frame.Dispose();
    }

    private const int WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
}

/// <summary>
/// Real mouse input through the GPU presenter's child window, see Input. Not unsafe, it awaits.
/// </summary>
internal static class PresenterInputTest
{
    /// <summary>
    /// "--selftest-share presenter-input": real mouse input over the GPU's child window, the way
    /// the viewer stacks them: an input layer, and the child window over it. Moves this PC's
    /// pointer for a second or two, and clicks only inside its own window; not part of the
    /// usual checks for that reason. The move, buttons, capture and wheel must reach the layer
    /// under the child window, and the pointer must take the layer's shape.
    /// </summary>
    public static string Input()
    {
        string? result = null;
        Exception? error = null;
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        int moves = 0, downs = 0, ups = 0, wheels = 0;
        bool captured = false;
        var layer = new Border { Background = System.Windows.Media.Brushes.Transparent, Focusable = true, Cursor = System.Windows.Input.Cursors.Hand };
        layer.MouseMove += (_, _) => moves++;
        layer.MouseDown += (_, _) =>
        {
            downs++;
            captured = layer.CaptureMouse();
        };
        layer.MouseUp += (_, _) =>
        {
            ups++;
            layer.ReleaseMouseCapture();
        };
        layer.MouseWheel += (_, _) => wheels++;
        var host = new Views.GpuPictureHost { IsHitTestVisible = false, Focusable = false };
        var grid = new Grid { Background = System.Windows.Media.Brushes.Black };
        grid.Children.Add(layer);
        grid.Children.Add(host);
        var area = SystemParameters.WorkArea;
        var window = new Window
        {
            Content = grid, Width = 480, Height = 320, Left = area.Left + 40, Top = area.Top + 40,
            Topmost = true, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow, Title = "PdaNet presenter input check",
        };
        using var source = new PresenterSelfTest.TestSource(320, 180);
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                if (host.Presenter is not { } presenter) throw new NotSupportedException($"no presenter: {host.Failure}");
                var device = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformToDevice;
                presenter.Start(source, PicturePlacement.Fitted(320, 180, (int)(host.ActualWidth * device.M11), (int)(host.ActualHeight * device.M22)));
                source.Next();
                await System.Threading.Tasks.Task.Delay(300);
                var hwnd = FindWindowEx(new WindowInteropHelper(window).Handle, IntPtr.Zero, "PdaNetSharePicture", null);
                if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) throw new Exception("the GPU's child window is not there");
                var center = host.PointToScreen(new System.Windows.Point(host.ActualWidth / 2, host.ActualHeight / 2));
                int x = (int)center.X, y = (int)center.Y;
                long hit = (long)SendMessage(hwnd, 0x0084, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)));
                GetCursorPos(out var before);
                try
                {
                    SetCursorPos(x - 20, y - 10);
                    await System.Threading.Tasks.Task.Delay(50);
                    SetCursorPos(x, y);
                    Mouse(MOUSEEVENTF_MOVE, 1, 1, 0);
                    await System.Threading.Tasks.Task.Delay(150);
                    // Only into this window: what is under the pointer must be it.
                    var under = WindowFromPoint(new POINT { X = x, Y = y });
                    if (GetAncestor(under, 2) != new WindowInteropHelper(window).Handle)
                        throw new Exception("another window is under the pointer, no click sent");
                    var info = new ShareNative.CURSORINFO { cbSize = Marshal.SizeOf<ShareNative.CURSORINFO>() };
                    ShareNative.GetCursorInfo(ref info);
                    bool hand = info.hCursor == ShareNative.LoadCursor(IntPtr.Zero, (IntPtr)32649);
                    Mouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0);
                    await System.Threading.Tasks.Task.Delay(60);
                    Mouse(MOUSEEVENTF_MOVE, 30, 0, 0); // dragged out of the layer while captured
                    await System.Threading.Tasks.Task.Delay(60);
                    Mouse(MOUSEEVENTF_LEFTUP, 0, 0, 0);
                    await System.Threading.Tasks.Task.Delay(60);
                    Mouse(MOUSEEVENTF_WHEEL, 0, 0, 120);
                    await System.Threading.Tasks.Task.Delay(150);
                    var screenshot = Capture(x - 40, y - 20, 80, 40);
                    result = $"the child window answers hit tests with {hit} (HTTRANSPARENT is -1); through it the layer got {moves} moves, " +
                             $"{downs} down, {ups} up (capture {(captured ? "taken" : "NOT taken")}), {wheels} wheel; the pointer " +
                             $"{(hand ? "took the layer's hand shape" : "did NOT take the layer's shape")}; the screen there shows the GPU's gray {screenshot}; " +
                             $"{presenter.Presented} presents";
                    if (hit != -1 || moves == 0 || downs != 1 || ups != 1 || wheels != 1 || !captured || !hand)
                        throw new Exception(result);
                }
                finally { SetCursorPos(before.X, before.Y); }
            }
            catch (Exception e) { error = e; }
            finally { window.Close(); }
        };
        window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
        window.Show();
        System.Windows.Threading.Dispatcher.Run();
        host.Dispose();
        if (error != null) throw error;
        return result ?? "no result";
    }

    /// <summary>The average gray of a patch of the screen, as Windows composes it.</summary>
    private static string Capture(int x, int y, int w, int h)
    {
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
        long sum = 0;
        for (int j = 0; j < h; j++)
        for (int i = 0; i < w; i++)
            sum += bmp.GetPixel(i, j).G;
        return $"{sum / (w * h)} of 255";
    }

    private static void Mouse(uint flags, int dx, int dy, int data)
    {
        var input = new ShareNative.INPUT { type = ShareNative.INPUT_MOUSE };
        input.u.mi = new ShareNative.MOUSEINPUT { dx = dx, dy = dy, mouseData = (uint)data, dwFlags = flags };
        ShareNative.SendInput(1, new[] { input }, Marshal.SizeOf<ShareNative.INPUT>());
    }

    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_WHEEL = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

}
