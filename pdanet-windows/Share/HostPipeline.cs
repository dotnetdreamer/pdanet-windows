using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using PdaNetPortable.Services;
using PdaNetPortable.Share.Media;

namespace PdaNetPortable.Share;

/// <summary>
/// The screen of the PC that shares, from capture to the link, on one thread.
///
/// Nothing runs while the screen is still: Desktop Duplication wakes the thread when something
/// changes and says what changed, only those rectangles are converted, and the encoder gets a
/// frame at most at the frame rate. When the screen settles, two more frames of the same picture
/// let the encoder sharpen it, so text ends up crisp. The bitrate follows what the link can take.
/// </summary>
internal sealed class HostPipeline : IDisposable
{
    private readonly RtcHelper _rtc;
    private readonly Thread _thread;
    private readonly CursorWatch _cursor;
    private volatile bool _stop, _reopen, _keyframe;
    private volatile int _screenIndex, _bitrate = 2_500_000, _viewerWidth, _viewerHeight;
    private volatile Quality _quality = Quality.Auto;
    private volatile int _sourceWidth, _sourceHeight, _scale = 1;
    private volatile bool _hardware = true;

    public List<ShareScreen> Screens { get; private set; } = ShareScreen.All();
    public ShareScreen Screen => Screens[Math.Clamp(_screenIndex, 0, Screens.Count - 1)];
    public int ScreenIndex => Math.Clamp(_screenIndex, 0, Screens.Count - 1);
    public string Encoder { get; private set; } = "";
    public string Capture { get; private set; } = "";
    public string Size { get; private set; } = "";
    public bool Hidden { get; private set; }
    /// <summary>Degrees the sent picture is turned clockwise from how the screen shows it, see IScreenSource.Rotation.</summary>
    public int Rotation { get; private set; }

    // Measured while running, read by the session for the stats.
    public int FramesSent;
    public long BytesSent;

    /// <summary>The screen, its size or the encoder changed, or the screen became hidden or visible. Raised on the pipeline thread.</summary>
    public event Action? Changed;

    public HostPipeline(RtcHelper rtc)
    {
        _rtc = rtc;
        _cursor = new CursorWatch(rtc);
        _thread = new Thread(Run) { IsBackground = true, Name = "Screen sharing", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    public void RequestKeyframe() => _keyframe = true;

    /// <summary>What the link can take, from the congestion control in the helper.</summary>
    public void SetBitrate(int bps) => _bitrate = Math.Clamp(bps, 150_000, 40_000_000);

    public void PickScreen(int index)
    {
        if (index == _screenIndex || index < 0 || index >= Screens.Count) return;
        _screenIndex = index;
        _reopen = true;
    }

    public void SetQuality(Quality q)
    {
        if (q == _quality) return;
        _quality = q;
        _reopen = true;
    }

    /// <summary>The viewer's screen size: a screen twice as big is sent at half size, the viewer could not show more.</summary>
    public void SetViewerSize(int width, int height)
    {
        _viewerWidth = width;
        _viewerHeight = height;
        if (_sourceWidth > 0 && ScaleFor(_sourceWidth, _sourceHeight, _hardware) != _scale) _reopen = true;
    }

    private int Fps => _quality switch { Quality.Smooth => 60, Quality.Sharp => 20, _ => 30 };

    private int ScaleFor(int width, int height, bool hardware)
    {
        int vw = _viewerWidth, vh = _viewerHeight;
        if (vw > 0 && vh > 0 && width >= vw * 1.8 && height >= vh * 1.8) return 2;
        if (!hardware && width > 2560) return 2;
        if (_quality == Quality.Smooth && width > 2560) return 2;
        return 1;
    }

    private void Run()
    {
        var dirty = new List<Dirty>(64);
        var pending = new List<Dirty>(64);
        int failures = 0;
        while (!_stop)
        {
            IScreenSource? source = null;
            IVideoEncoder? encoder = null;
            GpuConverter? gpu = null;
            Nv12Frame? frame = null;
            bool lost = false;
            try
            {
                Screens = ShareScreen.All();
                var screen = Screen;
                _reopen = false;
                int scale = 1;
                (source, gpu, encoder, scale) = OpenGpu(screen);
                if (encoder == null)
                {
                    // The processor path: the picture comes to memory, ColorConvert turns it into NV12.
                    source = ScreenSources.Open(screen);
                    scale = ScaleFor(source.Width, source.Height, hardware: true);
                    var mf = H264Encoder.Create((source.Width / scale) & ~1, (source.Height / scale) & ~1, Fps, Target());
                    if (!mf.Hardware && scale == 1 && ScaleFor(source.Width, source.Height, hardware: false) == 2)
                    {
                        scale = 2;
                        mf.Dispose();
                        mf = H264Encoder.Create((source.Width / 2) & ~1, (source.Height / 2) & ~1, Fps, Target(), allowHardware: false);
                    }
                    encoder = mf;
                    frame = new Nv12Frame(encoder.Width, encoder.Height);
                }
                encoder.Output = Send;
                (_sourceWidth, _sourceHeight, _scale, _hardware) = (source!.Width, source.Height, scale, encoder.Hardware);
                Encoder = encoder.Name + (encoder.Hardware ? "" : " (software)");
                Capture = source.Kind + (gpu != null ? ", all on the GPU" : "");
                Size = $"{encoder.Width}x{encoder.Height}";
                Rotation = source.Rotation;
                _cursor.Reset();
                Changed?.Invoke();
                Logger.Instance.Info($"Screen sharing {screen.DeviceName} at {Size}, {Fps} fps, with {Encoder} and {Capture}");

                pending.Clear();
                var clock = Stopwatch.StartNew();
                long last = -1, lastChange = -1;
                int refines = 2, interval = 1000 / Fps;
                _keyframe = true;
                while (!_stop && !_reopen)
                {
                    long now = clock.ElapsedMilliseconds;
                    // Sleep until the next frame may go, the next refine is due, or something changes.
                    int wait = pending.Count > 0 ? (int)Math.Max(0, last + interval - now)
                        : refines < 2 ? (int)Math.Max(0, lastChange + RefineDelay(refines) - now)
                        : 100;
                    dirty.Clear();
                    var r = source.Next(Math.Min(wait, 100), dirty);
                    _cursor.Poll(screen);
                    if (r == CaptureResult.Lost)
                    {
                        lost = true;
                        break;
                    }
                    now = clock.ElapsedMilliseconds;
                    if (r == CaptureResult.Frame)
                    {
                        pending.AddRange(dirty);
                        lastChange = now;
                        refines = 0;
                        // The screen shows again after a private one.
                        failures = 0;
                        if (Hidden)
                        {
                            Hidden = false;
                            Changed?.Invoke();
                        }
                    }

                    bool due = pending.Count > 0 && now - last >= interval;
                    bool refine = pending.Count == 0 && refines < 2 && lastChange >= 0 && now - lastChange >= RefineDelay(refines);
                    bool key = _keyframe;
                    if (!due && !refine && !key) continue;

                    if (pending.Count > 0)
                    {
                        // On the GPU path the changes are already in the GPU picture.
                        if (frame != null) Convert(source, pending, frame, scale);
                        pending.Clear();
                    }
                    if (refine) refines++;
                    if (key)
                    {
                        _keyframe = false;
                        encoder.RequestKeyframe();
                    }
                    encoder.SetBitrate(Target());
                    long durationMs = last < 0 ? interval : Math.Clamp(now - last, 1, 1000);
                    if (gpu != null) encoder.Encode(gpu.Convert(), now * 10_000, durationMs * 10_000);
                    else encoder.Encode(frame!, now * 10_000, durationMs * 10_000);
                    last = now;
                }
            }
            catch (Exception e)
            {
                lost = true;
                if (++failures == 1 || failures % 20 == 0)
                    Logger.Instance.Warning($"Screen sharing is waiting for the screen: {e.Message}");
            }
            finally
            {
                frame?.Dispose();
                encoder?.Dispose();
                gpu?.Dispose();
                source?.Dispose();
            }

            if (_stop) break;
            if (lost)
            {
                // A screen Windows keeps private (a UAC prompt, the lock screen) or a changed display
                // mode: try again in a moment, the other PC sees a note meanwhile. A display mode
                // change comes back on the first try, a private screen keeps failing.
                if (failures == 0) failures = 1;
                else if (!Hidden && failures >= 2)
                {
                    Hidden = true;
                    Changed?.Invoke();
                }
                Thread.Sleep(failures == 1 ? 50 : 400);
                failures++;
            }
        }
    }

    private static int RefineDelay(int refines) => refines == 0 ? 160 : 700;

    private static bool _gpuChecked, _gpuWorks;
    private static string? _gpuProblem;

    /// <summary>
    /// The GPU path: Desktop Duplication keeps the picture on the GPU, the video processor turns it
    /// into NV12 there and the GPU's encoder reads it there. Nothing passes through the processor
    /// or the app's memory. The encoder is Direct3D 12's where the GPU shares the PC's memory, it
    /// needs a fraction of what Media Foundation's keeps, else Media Foundation's. When any part
    /// is missing on this PC, everything is closed again and the encoder comes back null, so the
    /// processor path takes over.
    /// </summary>
    private (IScreenSource?, GpuConverter?, IVideoEncoder?, int) OpenGpu(ShareScreen screen)
    {
        if (_gpuChecked && !_gpuWorks) return (null, null, null, 1);
        DuplicationSource? source = null;
        GpuConverter? gpu = null;
        try
        {
            // Direct3D 12's encoder takes each frame's changes straight from Desktop Duplication;
            // Media Foundation's needs a picture of the whole screen kept on the GPU.
            bool direct = !D3D12H264Encoder.Broken;
            source = new DuplicationSource(screen, gpu: true, picture: !direct);
            if (!_gpuChecked)
            {
                _gpuProblem = GpuConverter.Check(source.Device, source.Context);
                _gpuChecked = true;
                _gpuWorks = _gpuProblem == null;
                if (!_gpuWorks) throw new NotSupportedException(_gpuProblem);
            }
            int scale = ScaleFor(source.Width, source.Height, hardware: true);
            int w = (source.Width / scale) & ~1, h = (source.Height / scale) & ~1;
            if (direct)
            {
                D3D12H264Encoder? d3d12 = null;
                try
                {
                    d3d12 = new D3D12H264Encoder(source.Device, source.Context, w, h, Fps, Target());
                    gpu = new GpuConverter(source.Device, source.Context, null, source.Width, source.Height, w, h, d3d12.Input);
                    source.FrameArrived = gpu.Convert;
                    return (source, gpu, d3d12, scale);
                }
                catch (Exception e) when (e is NotSupportedException or SharpGen.Runtime.SharpGenException or InvalidOperationException)
                {
                    gpu?.Dispose();
                    gpu = null;
                    d3d12?.Dispose();
                    D3D12H264Encoder.MarkBroken();
                    Logger.Instance.Info($"Screen sharing uses Media Foundation's encoder: {e.Message}");
                    source.Dispose();
                    source = new DuplicationSource(screen, gpu: true);
                }
            }
            gpu = new GpuConverter(source.Device, source.Context, source.Picture, source.Width, source.Height, w, h);
            var encoder = H264Encoder.Create(w, h, Fps, Target(), device: source.Device);
            return (source, gpu, encoder, scale);
        }
        catch (Exception e)
        {
            gpu?.Dispose();
            source?.Dispose();
            if (!_gpuChecked || _gpuWorks)
                Logger.Instance.Info($"Screen sharing works through the processor: {e.Message}");
            // A GPU that cannot do one of the steps will not do them later either.
            if (e is NotSupportedException or InvalidOperationException) (_gpuChecked, _gpuWorks) = (true, false);
            return (null, null, null, 1);
        }
    }

    /// <summary>Sends an encoded frame to the helper: u32 duration in microseconds, u8 keyframe, H.264.</summary>
    private void Send(ReadOnlyMemory<byte> au, long duration, bool key)
    {
        Span<byte> header = stackalloc byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)Math.Clamp(duration / 10, 1_000, 1_000_000));
        header[4] = (byte)(key ? 1 : 0);
        _rtc.Send(RtcHelper.KindVideo, header, au.Span);
        Interlocked.Increment(ref FramesSent);
        Interlocked.Add(ref BytesSent, au.Length);
    }

    /// <summary>The bitrate for the encoder: what the link takes, less the packet overhead.</summary>
    private int Target()
    {
        double share = _quality == Quality.Sharp ? 0.92 : 0.88;
        return (int)(_bitrate * share);
    }

    private static unsafe void Convert(IScreenSource source, List<Dirty> rects, Nv12Frame frame, int scale)
    {
        int align = scale * 2;
        int w = frame.Width * scale, h = frame.Height * scale;
        long area = 0;
        foreach (var d in rects) area += (long)d.W * d.H;
        byte* bgra = source.Lock(out int pitch);
        try
        {
            if (rects.Count > 48 || area * 2 > (long)w * h)
            {
                ColorConvert.BgraToNv12(bgra, pitch, 0, 0, w, h, frame, scale);
                return;
            }
            foreach (var d in rects)
            {
                int x0 = d.X & ~(align - 1), y0 = d.Y & ~(align - 1);
                int x1 = Math.Min(w, (d.X + d.W + align - 1) & ~(align - 1));
                int y1 = Math.Min(h, (d.Y + d.H + align - 1) & ~(align - 1));
                if (x1 > x0 && y1 > y0) ColorConvert.BgraToNv12(bgra, pitch, x0, y0, x1 - x0, y1 - y0, frame, scale);
            }
        }
        finally { source.Unlock(); }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(3000);
    }
}

/// <summary>
/// The pointer of the PC that shares: where it is goes on the live channel, its shape on the
/// reliable one, and only when they change.
/// </summary>
internal sealed class CursorWatch
{
    private readonly RtcHelper _rtc;
    private readonly HashSet<uint> _sent = new();
    private IntPtr _shape;
    private ushort _x, _y;
    private bool _visible, _fresh = true;

    public CursorWatch(RtcHelper rtc) => _rtc = rtc;

    /// <summary>Sends the pointer again in full, after a new screen or a new viewer.</summary>
    public void Reset()
    {
        _fresh = true;
        _shape = IntPtr.Zero;
    }

    public void Poll(ShareScreen screen)
    {
        var ci = new ShareNative.CURSORINFO { cbSize = Marshal.SizeOf<ShareNative.CURSORINFO>() };
        if (!ShareNative.GetCursorInfo(ref ci)) return;
        int px = ci.ptScreenPos.X - screen.X, py = ci.ptScreenPos.Y - screen.Y;
        bool visible = (ci.flags & ShareNative.CURSOR_SHOWING) != 0 && px >= 0 && py >= 0 && px < screen.Width && py < screen.Height;
        ushort x = (ushort)Math.Clamp(px * 65535L / Math.Max(1, screen.Width - 1), 0, 65535);
        ushort y = (ushort)Math.Clamp(py * 65535L / Math.Max(1, screen.Height - 1), 0, 65535);
        if (_fresh || x != _x || y != _y || visible != _visible)
        {
            _x = x;
            _y = y;
            _visible = visible;
            _rtc.Send(RtcHelper.KindLive, MsgWriter.Pointer(x, y, visible));
        }
        if (visible && (ci.hCursor != _shape || _fresh)) SendShape(ci.hCursor);
        _fresh = false;
    }

    private void SendShape(IntPtr handle)
    {
        _shape = handle;
        byte id = CursorShapes.StandardId(handle);
        if (id != 0)
        {
            _rtc.Send(RtcHelper.KindCtl, CursorShapes.StandardMessage(id));
            return;
        }
        var picture = CursorShapes.Read(handle);
        if (picture == null)
        {
            _rtc.Send(RtcHelper.KindCtl, CursorShapes.StandardMessage(1));
            return;
        }
        uint hash = picture.Hash;
        _rtc.Send(RtcHelper.KindCtl, _sent.Add(hash) ? CursorShapes.PictureMessage(picture) : CursorShapes.KnownMessage(hash));
    }
}
