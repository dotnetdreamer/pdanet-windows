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
/// Video waits while the screen is still: Desktop Duplication wakes the thread when something
/// changes and says what changed, only those rectangles are converted, and FramePlanner decides
/// when the next frame goes and at which quantizer: moving content at what the link carries
/// right then, and once the screen rests, refine frames that bring it to near lossless, so text
/// ends up crisp. The bitrate follows what the link can take.
/// </summary>
internal sealed class HostPipeline : IDisposable
{
    private readonly RtcHelper _rtc;
    private readonly Thread _thread;
    private readonly CursorPolling _cursor;
    private volatile bool _stop, _reopen, _keyframe;
    private volatile int _screenIndex, _bitrate = 1_000_000, _viewerWidth, _viewerHeight; // the helper's start, rtc/peer.go
    private volatile Quality _quality = Quality.Auto;
    private volatile bool _gray;
    private volatile int _sourceWidth, _sourceHeight, _scale = 1;
    private volatile bool _hardware = true;
    private volatile FramePlanner? _planner;

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
        var cursor = new CursorWatch(rtc);
        _cursor = new CursorPolling(cursor.Poll, cursor.Reset);
        _thread = new Thread(Run) { IsBackground = true, Name = "Screen sharing", Priority = ThreadPriority.AboveNormal };
    }

    public void Start()
    {
        _cursor.Start();
        _thread.Start();
    }

    public void RequestKeyframe() => _keyframe = true;

    /// <summary>What the link can take, from the congestion control in the helper.</summary>
    public void SetBitrate(int bps)
    {
        _bitrate = Math.Clamp(bps, 150_000, 40_000_000);
        if (_planner is { } p) p.LinkBps = _bitrate;
    }

    public void PickScreen(int index)
    {
        if (index == _screenIndex || index < 0 || index >= Screens.Count) return;
        _screenIndex = index;
        _reopen = true;
    }

    /// <summary>The trade of frames for sharpness the viewer picked. The planner follows at once; only a new size opens the encoder again.</summary>
    public void SetQuality(Quality q)
    {
        if (q == _quality) return;
        _quality = q;
        if (_planner is { } p) p.Quality = q;
        if (_sourceWidth > 0 && ScaleFor(_sourceWidth, _sourceHeight, _hardware) != _scale) _reopen = true;
    }

    /// <summary>
    /// Black and white, or color, as the viewer picked. Black and white sends the brightness only:
    /// the color plane stays neutral, which the encoder turns into almost nothing, so each frame
    /// is smaller and more of them fit through the same link. The pipeline opens again with it,
    /// so the first frame after is a keyframe with the whole screen in the new look.
    /// </summary>
    public void SetGray(bool on)
    {
        if (on == _gray) return;
        _gray = on;
        _reopen = true;
    }

    /// <summary>The viewer's screen size: a screen twice as big is sent at half size, the viewer could not show more.</summary>
    public void SetViewerSize(int width, int height)
    {
        _viewerWidth = width;
        _viewerHeight = height;
        if (_sourceWidth > 0 && ScaleFor(_sourceWidth, _sourceHeight, _hardware) != _scale) _reopen = true;
    }

    /// <summary>
    /// The frame rate the encoder is set up for, the most the planner sends: 60, or 30 for a frame
    /// too big for H.264 level 5.1 at 60, which decoders may hold the stream to.
    /// </summary>
    private static int EncoderFps(int width, int height) => (width + 15) / 16 * ((height + 15) / 16) * 60 <= 983_040 ? 60 : 30;

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
        var pending = new List<Dirty>(64); // what changed since the last frame, for the processor path
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
                bool gray = _gray;
                int scale = 1;
                (source, gpu, encoder, scale) = OpenGpu(screen, gray);
                if (encoder == null)
                {
                    // The processor path: the picture comes to memory, ColorConvert turns it into NV12.
                    source = ScreenSources.Open(screen);
                    scale = ScaleFor(source.Width, source.Height, hardware: true);
                    int w = (source.Width / scale) & ~1, h = (source.Height / scale) & ~1;
                    var mf = H264Encoder.Create(w, h, EncoderFps(w, h), Target());
                    if (!mf.Hardware && scale == 1 && ScaleFor(source.Width, source.Height, hardware: false) == 2)
                    {
                        scale = 2;
                        mf.Dispose();
                        (w, h) = ((source.Width / 2) & ~1, (source.Height / 2) & ~1);
                        mf = H264Encoder.Create(w, h, EncoderFps(w, h), Target(), allowHardware: false);
                    }
                    encoder = mf;
                    frame = new Nv12Frame(encoder.Width, encoder.Height);
                }
                var planner = new FramePlanner(encoder.Width, encoder.Height, scale, encoder.ConstantQp, EncoderFps(encoder.Width, encoder.Height))
                {
                    Quality = _quality,
                    LinkBps = _bitrate,
                };
                _planner = planner;
                encoder.Output = (au, duration, key) =>
                {
                    planner.Encoded(au.Length * 8);
                    Send(au, duration, key);
                };
                (_sourceWidth, _sourceHeight, _scale, _hardware) = (source!.Width, source.Height, scale, encoder.Hardware);
                Encoder = encoder.Name + (encoder.Hardware ? "" : " (software)");
                Capture = source.Kind + (gpu != null ? ", all on the GPU" : "");
                Size = $"{encoder.Width}x{encoder.Height}";
                Rotation = source.Rotation;
                _cursor.SetScreen(screen);
                Changed?.Invoke();
                string rate = encoder is D3D12H264Encoder d ? d.RateMode : encoder.ConstantQp ? "constant QP" : "constant bitrate";
                Logger.Instance.Info($"Screen sharing {screen.DeviceName} at {Size}, up to {EncoderFps(encoder.Width, encoder.Height)} fps, with {Encoder} ({rate}) and {Capture}{(gray ? ", in black and white" : "")}");

                pending.Clear();
                var clock = Stopwatch.StartNew();
                long last = -1;
                bool captured = false;
                _keyframe = true;
                while (!_stop && !_reopen)
                {
                    long now = clock.ElapsedMilliseconds;
                    // A newly opened surface has no desktop in it yet. In particular, do not
                    // spend the opening keyframe's link budget on a black picture.
                    var plan = captured ? planner.Next(now, _keyframe) : FramePlan.Wait(16);
                    if (plan.Kind == FrameKind.None)
                    {
                        // Sleep until the planner wants to look again, or something changes.
                        dirty.Clear();
                        var r = source.Next(plan.WaitMs, dirty);
                        if (r == CaptureResult.Lost)
                        {
                            lost = true;
                            break;
                        }
                        if (r == CaptureResult.Frame)
                        {
                            captured = true;
                            // GPU capture already applies changes to its picture. Only the CPU
                            // converter needs to remember them while the link budget recovers.
                            if (frame != null) AccumulateChanges(pending, dirty, source.Width, source.Height);
                            planner.Changed(dirty, clock.ElapsedMilliseconds);
                            // The screen shows again after a private one.
                            failures = 0;
                            if (Hidden)
                            {
                                Hidden = false;
                                Changed?.Invoke();
                            }
                        }
                        continue;
                    }

                    if (pending.Count > 0)
                    {
                        // On the GPU path the changes are already in the GPU picture.
                        if (frame != null) Convert(source, pending, frame, scale, gray);
                        pending.Clear();
                    }
                    if (plan.Kind == FrameKind.Key)
                    {
                        _keyframe = false;
                        encoder.RequestKeyframe();
                    }
                    if (encoder.ConstantQp) encoder.Qp = plan.Qp;
                    else encoder.SetBitrate(Target());
                    long durationMs = last < 0 ? 33 : Math.Clamp(now - last, 1, 1000);
                    planner.Sent(plan, now);
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
                _cursor.SetScreen(null);
                _planner = null;
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

    /// <summary>What went over the last <paramref name="seconds"/>, for the log: frames, quantizers and bits.</summary>
    public string? Describe(double seconds) => _planner?.Describe(seconds);

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
    private (IScreenSource?, GpuConverter?, IVideoEncoder?, int) OpenGpu(ShareScreen screen, bool gray)
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
                    d3d12 = new D3D12H264Encoder(source.Device, source.Context, w, h, EncoderFps(w, h), Target());
                    gpu = new GpuConverter(source.Device, source.Context, null, source.Width, source.Height, w, h, d3d12.Input, gray);
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
            gpu = new GpuConverter(source.Device, source.Context, source.Picture, source.Width, source.Height, w, h, gray: gray);
            var encoder = H264Encoder.Create(w, h, EncoderFps(w, h), Target(), device: source.Device);
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

    /// <summary>The bitrate for an encoder that keeps it itself: what the link takes, less the packet overhead.</summary>
    private int Target()
    {
        double share = _quality == Quality.Sharp ? 0.92 : 0.88;
        return (int)(_bitrate * share);
    }

    /// <summary>Bound capture metadata while encoding waits for the link to drain.</summary>
    internal static void AccumulateChanges(List<Dirty> pending, List<Dirty> changes, int width, int height)
    {
        var whole = new Dirty(0, 0, width, height);
        if (pending.Count == 1 && pending[0] == whole) return;
        if (pending.Count + changes.Count > 48 || changes.Contains(whole))
        {
            pending.Clear();
            pending.Add(whole);
        }
        else pending.AddRange(changes);
    }

    private static unsafe void Convert(IScreenSource source, List<Dirty> rects, Nv12Frame frame, int scale, bool gray)
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
                ColorConvert.BgraToNv12(bgra, pitch, 0, 0, w, h, frame, scale, gray);
                return;
            }
            foreach (var d in rects)
            {
                int x0 = d.X & ~(align - 1), y0 = d.Y & ~(align - 1);
                int x1 = Math.Min(w, (d.X + d.W + align - 1) & ~(align - 1));
                int y1 = Math.Min(h, (d.Y + d.H + align - 1) & ~(align - 1));
                if (x1 > x0 && y1 > y0) ColorConvert.BgraToNv12(bgra, pitch, x0, y0, x1 - x0, y1 - y0, frame, scale, gray);
            }
        }
        finally { source.Unlock(); }
    }

    public void Dispose()
    {
        _stop = true;
        _cursor.Dispose();
        if (_thread.IsAlive) _thread.Join(3000);
    }
}

/// <summary>
/// Poll the pointer independently of capture and encoding. A busy encoder must not hold a moving
/// pointer until its next picture. Only this thread owns CursorWatch; screen changes coalesce to
/// their latest immutable value and reset the shape before polling that screen.
/// </summary>
internal sealed class CursorPolling : IDisposable
{
    private sealed record Target(ShareScreen Screen);
    private readonly Action<ShareScreen> _poll;
    private readonly Action _reset;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _state = new();
    private readonly Thread _thread;
    private Target? _target;
    private bool _started, _stopped;

    public CursorPolling(Action<ShareScreen> poll, Action reset)
    {
        _poll = poll;
        _reset = reset;
        _thread = new Thread(Run) { IsBackground = true, Name = "Screen pointer" };
    }

    public void Start()
    {
        lock (_state)
        {
            if (_started || _stopped) return;
            _started = true;
            _thread.Start();
        }
    }

    public void SetScreen(ShareScreen? screen)
    {
        lock (_state)
        {
            if (_stopped) return;
            _target = screen == null ? null : new Target(screen);
            _wake.Set();
        }
    }

    private void Run()
    {
        Target? previous = null;
        bool warned = false;
        try
        {
            while (true)
            {
                Target? target;
                lock (_state)
                {
                    if (_stopped) return;
                    target = _target;
                }
                if (target != null)
                {
                    try
                    {
                        if (!ReferenceEquals(target, previous)) _reset();
                        _poll(target.Screen);
                        warned = false;
                    }
                    catch (Exception e)
                    {
                        if (!warned) Logger.Instance.Warning($"Screen sharing could not read the pointer: {e.Message}");
                        warned = true;
                    }
                }
                previous = target;
                _wake.WaitOne(target == null ? Timeout.Infinite : 16);
            }
        }
        finally { _wake.Dispose(); }
    }

    public void Dispose()
    {
        bool started;
        lock (_state)
        {
            if (_stopped) return;
            _stopped = true;
            started = _started;
            _wake.Set();
        }
        // An IPC write may still be completing. The worker owns its wait handle and releases it
        // when it exits, including after this bounded wait; no capture or GPU resource is shared.
        if (started) _thread.Join(3000);
        else _wake.Dispose();
    }
}

/// <summary>
/// The pointer of the PC that shares: where it is goes on the live channel, its shape on the
/// reliable one. Changed positions go immediately; idle heartbeats repair lost live datagrams.
/// </summary>
internal sealed class CursorWatch
{
    private readonly RtcHelper _rtc;
    private readonly HashSet<uint> _sent = new();
    private readonly PointerUpdates _pointer = new();
    private IntPtr _shape;
    private bool _fresh = true;

    public CursorWatch(RtcHelper rtc) => _rtc = rtc;

    /// <summary>Sends the pointer again in full, after a new screen or a new viewer.</summary>
    public void Reset()
    {
        _fresh = true;
        _shape = IntPtr.Zero;
        _pointer.Reset();
    }

    public void Poll(ShareScreen screen)
    {
        var ci = new ShareNative.CURSORINFO { cbSize = Marshal.SizeOf<ShareNative.CURSORINFO>() };
        if (!ShareNative.GetCursorInfo(ref ci)) return;
        int px = ci.ptScreenPos.X - screen.X, py = ci.ptScreenPos.Y - screen.Y;
        bool visible = (ci.flags & ShareNative.CURSOR_SHOWING) != 0 && px >= 0 && py >= 0 && px < screen.Width && py < screen.Height;
        ushort x = (ushort)Math.Clamp(px * 65535L / Math.Max(1, screen.Width - 1), 0, 65535);
        ushort y = (ushort)Math.Clamp(py * 65535L / Math.Max(1, screen.Height - 1), 0, 65535);
        if (_pointer.Next(x, y, visible, Environment.TickCount64) is { } position)
            _rtc.Send(RtcHelper.KindLive, position);
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

/// <summary>
/// Live pointer datagrams are not retransmitted. Repeat the latest position and visibility
/// occasionally so a lost final movement cannot leave the viewer's pointer stuck indefinitely.
/// </summary>
internal sealed class PointerUpdates
{
    private (ushort X, ushort Y, bool Visible)? _last;
    private long _sentAt;

    public void Reset() => _last = null;

    public byte[]? Next(ushort x, ushort y, bool visible, long now)
    {
        var value = (x, y, visible);
        if (_last == value && now - _sentAt < 250) return null;
        _last = value;
        _sentAt = now;
        return MsgWriter.Pointer(x, y, visible);
    }
}
