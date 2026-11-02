using System.Runtime.InteropServices;
using PdaNetPortable.Services;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice;
using Vortice.Mathematics;
using WinScreen = System.Windows.Forms.Screen;

namespace PdaNetPortable.Share;

/// <summary>A rectangle of the screen that changed, in pixels of that screen.</summary>
internal readonly record struct Dirty(int X, int Y, int W, int H);

internal enum CaptureResult { Timeout, Frame, Lost }

/// <summary>A screen as PdaNet can share it: where it sits on the desktop, in physical pixels.</summary>
internal sealed record ShareScreen(string DeviceName, int X, int Y, int Width, int Height, bool Primary)
{
    /// <summary>The screens of this PC, the primary one first.</summary>
    public static List<ShareScreen> All() => WinScreen.AllScreens
        .Select(s => new ShareScreen(s.DeviceName, s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, s.Primary))
        .OrderByDescending(s => s.Primary).ThenBy(s => s.X).ThenBy(s => s.Y)
        .ToList();
}

/// <summary>
/// One screen's picture, kept up to date. Next waits for a change and lists what changed; Lock
/// gives the whole current picture in BGRA, for as long as the converter reads it.
/// </summary>
internal interface IScreenSource : IDisposable
{
    string Kind { get; }
    int Width { get; }
    int Height { get; }

    /// <summary>
    /// Degrees clockwise the picture is turned from how the screen shows it, 0 for a screen that
    /// is not turned. Desktop Duplication gives the panel's own orientation: a turned screen is
    /// sent like that, and the viewer turns it back on its GPU for free.
    /// </summary>
    int Rotation { get; }
    CaptureResult Next(int timeoutMs, List<Dirty> dirty);
    unsafe byte* Lock(out int pitch);
    void Unlock();
}

/// <summary>
/// Desktop Duplication: Windows hands over each new desktop picture on the GPU with the
/// rectangles that changed, and only those are copied to memory the processor can read. A still
/// screen costs nothing, a typed word costs a few kilobytes.
/// </summary>
internal sealed unsafe class DuplicationSource : IScreenSource
{
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int AccessLost = unchecked((int)0x887A0026);
    private const int MoreData = unchecked((int)0x887A0003);

    public string Kind => "Desktop Duplication";
    public int Width { get; }
    public int Height { get; }
    public int Rotation { get; }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _dup;
    // The picture the changes go into: on the GPU for the GPU path, else memory the processor
    // reads. None where each frame goes to FrameArrived instead.
    private readonly ID3D11Texture2D? _picture;
    private readonly bool _gpu;

    /// <summary>The GPU device of this screen, shared with the converter and the encoder on the GPU path.</summary>
    public ID3D11Device Device => _device;
    /// <summary>The device's immediate context, which this capture owns.</summary>
    public ID3D11DeviceContext Context => _context;
    /// <summary>The whole current picture as a GPU texture, on the GPU path with a picture.</summary>
    public ID3D11Texture2D Picture => _picture ?? throw new InvalidOperationException("This capture keeps no picture");

    /// <summary>
    /// Without a picture of its own: each new frame while Desktop Duplication still holds it, with
    /// what changed, in the panel's orientation. A converter takes the changes straight from it,
    /// which saves a copy of the whole screen.
    /// </summary>
    public Action<ID3D11Texture2D, List<Dirty>>? FrameArrived { get; set; }
    public bool Gpu => _gpu;
    private RawRect[] _rects = new RawRect[64];
    private OutduplMoveRect[] _moves = new OutduplMoveRect[16];
    private readonly ShareScreen _screen;
    private readonly bool _snapshot;
    private readonly long _opened = Environment.TickCount64;
    private bool _image, _real, _mapped;

    /// <param name="gpu">Keep the picture on the GPU, for the GPU converter and encoder, instead of in memory the processor reads.</param>
    /// <param name="snapshot">On a still screen, take the first picture with GDI instead of waiting for a change.</param>
    /// <param name="picture">Keep the picture, else hand each frame to FrameArrived; on the GPU path only.</param>
    public DuplicationSource(ShareScreen screen, bool gpu = false, bool snapshot = true, bool picture = true)
    {
        _screen = screen;
        _snapshot = snapshot;
        _gpu = gpu;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? adapter = null;
        IDXGIOutput? output = null;
        // The duplication has to come from the GPU that drives this screen, which matters on
        // laptops with two GPUs.
        for (uint a = 0; output == null && factory.EnumAdapters1(a, out var candidate).Success; a++)
        {
            for (uint o = 0; candidate.EnumOutputs(o, out var out1).Success; o++)
            {
                if (out1.Description.DeviceName.Equals(screen.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    output = out1;
                    break;
                }
                out1.Dispose();
            }
            if (output != null) adapter = candidate;
            else candidate.Dispose();
        }
        if (adapter == null || output == null) throw new InvalidOperationException($"Desktop Duplication does not see {screen.DeviceName}");

        try
        {
            Rotation = output.Description.Rotation switch
            {
                ModeRotation.Rotate90 => 90,
                ModeRotation.Rotate180 => 180,
                ModeRotation.Rotate270 => 270,
                _ => 0,
            };

            // The GPU path also uses the device's video processor and its encoder, from their threads.
            var flags = DeviceCreationFlags.BgraSupport | (gpu ? DeviceCreationFlags.VideoSupport : 0);
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                out _device!, out _context!).CheckError();
            if (gpu)
                using (var mt = _device.QueryInterface<ID3D11Multithread>())
                    mt.SetMultithreadProtected(true);
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            _dup = output1.DuplicateOutput(_device);
            // The picture comes in the panel's own orientation, the screen's size turned.
            (Width, Height) = Rotation is 90 or 270 ? (screen.Height, screen.Width) : (screen.Width, screen.Height);
            _picture = gpu && !picture ? null : gpu ? Texture(ResourceUsage.Default) : Texture(ResourceUsage.Staging);
        }
        catch
        {
            _dup?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            throw;
        }
        finally
        {
            output.Dispose();
            adapter.Dispose();
        }
    }

    private ID3D11Texture2D Texture(ResourceUsage usage) => _device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)Width,
        Height = (uint)Height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = usage,
        BindFlags = usage == ResourceUsage.Default ? BindFlags.RenderTarget | BindFlags.ShaderResource : BindFlags.None,
        CPUAccessFlags = usage == ResourceUsage.Staging ? CpuAccessFlags.Read | CpuAccessFlags.Write : CpuAccessFlags.None,
    });

    public CaptureResult Next(int timeoutMs, List<Dirty> dirty)
    {
        var hr = _dup.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out var info, out var resource);
        if (hr.Code == WaitTimeout) return StillAtStart(dirty);
        if (hr.Failure) return CaptureResult.Lost;
        CaptureResult result;
        try { result = Take(info, resource, dirty); }
        finally
        {
            resource.Dispose();
            _dup.ReleaseFrame();
        }
        return result == CaptureResult.Timeout ? StillAtStart(dirty) : result;
    }

    private CaptureResult Take(OutduplFrameInfo info, IDXGIResource resource, List<Dirty> dirty)
    {
        // No new picture: only the pointer moved, or nothing was drawn since the start. The very
        // first frame can be like that, its picture is still empty.
        if (info.LastPresentTime == 0) return CaptureResult.Timeout;
        using var texture = resource.QueryInterface<ID3D11Texture2D>();

        // The first real picture is taken whole: its rectangles only say what changed since the
        // duplication started, not what the screen already showed.
        bool whole = !_real || info.TotalMetadataBufferSize == 0 || !Changes(dirty);
        _real = _image = true;
        if (whole)
        {
            dirty.Clear();
            dirty.Add(new Dirty(0, 0, Width, Height));
        }
        if (dirty.Count == 0) return CaptureResult.Timeout;
        if (_picture == null) FrameArrived?.Invoke(texture, dirty);
        else if (whole) _context.CopyResource(_picture, texture);
        else
            foreach (var d in dirty)
                _context.CopySubresourceRegion(_picture, 0, (uint)d.X, (uint)d.Y, 0, texture, 0, new Box(d.X, d.Y, 0, d.X + d.W, d.Y + d.H, 1));
        return CaptureResult.Frame;
    }

    /// <summary>What changed in this frame: where windows moved to, and what was drawn. False when Windows does not say.</summary>
    private bool Changes(List<Dirty> dirty)
    {
        var hr = _dup.GetFrameMoveRects((uint)(_moves.Length * sizeof(OutduplMoveRect)), _moves, out uint size);
        if (hr.Code == MoreData)
        {
            _moves = new OutduplMoveRect[size / sizeof(OutduplMoveRect) + 8];
            hr = _dup.GetFrameMoveRects((uint)(_moves.Length * sizeof(OutduplMoveRect)), _moves, out size);
        }
        int moves = hr.Success ? (int)(size / sizeof(OutduplMoveRect)) : 0;
        hr = _dup.GetFrameDirtyRects((uint)(_rects.Length * sizeof(RawRect)), _rects, out size);
        if (hr.Code == MoreData)
        {
            _rects = new RawRect[size / sizeof(RawRect) + 16];
            hr = _dup.GetFrameDirtyRects((uint)(_rects.Length * sizeof(RawRect)), _rects, out size);
        }
        if (!hr.Success) return false;
        // A moved window is a move rectangle: its new place is new on this picture.
        for (int i = 0; i < moves; i++) Add(_moves[i].DestinationRect, dirty);
        for (int i = 0, n = (int)(size / sizeof(RawRect)); i < n; i++) Add(_rects[i], dirty);
        return true;
    }
    /// <summary>
    /// A screen that stays still gives Desktop Duplication nothing to hand over, so after a moment
    /// the first picture comes from GDI instead, turned to the panel's orientation. Windows' own
    /// pictures take over with the first change.
    /// </summary>
    private CaptureResult StillAtStart(List<Dirty> dirty)
    {
        if (_image || !_snapshot || Environment.TickCount64 - _opened < 200) return CaptureResult.Timeout;
        using var gdi = new GdiSource(_screen);
        var d = new List<Dirty>();
        if (gdi.Next(0, d) != CaptureResult.Frame) return CaptureResult.Timeout;
        byte* src = gdi.Lock(out int sp);
        // On the GPU path the snapshot goes through a staging texture that only lives for this.
        using var temp = _gpu ? Texture(ResourceUsage.Staging) : null;
        var target = temp ?? _picture!;
        var map = _context.Map(target, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* dst = (byte*)map.DataPointer;
            int dp = (int)map.RowPitch, wn = Width, hn = Height;
            for (int ny = 0; ny < hn; ny++)
            {
                uint* row = (uint*)(dst + ny * dp);
                for (int nx = 0; nx < wn; nx++)
                {
                    // Where this pixel of the panel shows on the screen.
                    var (x, y) = Rotation switch
                    {
                        90 => (hn - 1 - ny, nx),
                        180 => (wn - 1 - nx, hn - 1 - ny),
                        270 => (ny, wn - 1 - nx),
                        _ => (nx, ny),
                    };
                    row[nx] = *(uint*)(src + y * sp + x * 4);
                }
            }
        }
        finally
        {
            _context.Unmap(target, 0);
            gdi.Unlock();
        }
        _image = true;
        dirty.Add(new Dirty(0, 0, Width, Height));
        if (_picture == null)
        {
            using var shown = Texture(ResourceUsage.Default);
            _context.CopyResource(shown, temp!);
            FrameArrived?.Invoke(shown, dirty);
        }
        else if (temp != null) _context.CopyResource(_picture, temp);
        return CaptureResult.Frame;
    }

    private void Add(RawRect r, List<Dirty> dirty)
    {
        int left = Math.Clamp(r.Left, 0, Width), top = Math.Clamp(r.Top, 0, Height);
        int right = Math.Clamp(r.Right, 0, Width), bottom = Math.Clamp(r.Bottom, 0, Height);
        if (right > left && bottom > top) dirty.Add(new Dirty(left, top, right - left, bottom - top));
    }

    public byte* Lock(out int pitch)
    {
        if (_gpu) throw new InvalidOperationException("The picture is on the GPU");
        var map = _context.Map(_picture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        _mapped = true;
        pitch = (int)map.RowPitch;
        return (byte*)map.DataPointer;
    }

    public void Unlock()
    {
        if (!_mapped) return;
        _context.Unmap(_picture!, 0);
        _mapped = false;
    }

    public void Dispose()
    {
        Unlock();
        _picture?.Dispose();
        _dup.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}

/// <summary>
/// The plain capture, for a PC where Desktop Duplication is not there, such as
/// some remote sessions. It copies the screen with GDI and finds what changed by comparing with
/// the picture before, in bands, so the encoder still only gets the changes.
/// </summary>
internal sealed unsafe class GdiSource : IScreenSource
{
    private const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, Band = 32;

    public string Kind => "GDI";
    public int Width { get; }
    public int Height { get; }
    public int Rotation => 0;

    private readonly ShareScreen _screen;
    private readonly IntPtr _memDc, _bitmap, _old;
    private readonly byte* _bits;
    private readonly byte* _previous;
    private readonly int _pitch;
    private bool _first = true;
    private long _due;

    public GdiSource(ShareScreen screen)
    {
        _screen = screen;
        Width = screen.Width;
        Height = screen.Height;
        _pitch = Width * 4;
        var bmi = new ShareNative.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<ShareNative.BITMAPINFOHEADER>(),
            biWidth = Width,
            biHeight = -Height, // top down
            biPlanes = 1,
            biBitCount = 32,
        };
        var screenDc = ShareNative.GetDC(IntPtr.Zero);
        try
        {
            _memDc = CreateCompatibleDC(screenDc);
            _bitmap = CreateDIBSection(screenDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("Could not make a picture for the plain capture");
            _bits = (byte*)bits;
            _old = SelectObject(_memDc, _bitmap);
        }
        finally { ShareNative.ReleaseDC(IntPtr.Zero, screenDc); }
        _previous = (byte*)NativeMemory.AllocZeroed((nuint)(_pitch * Height));
    }

    public CaptureResult Next(int timeoutMs, List<Dirty> dirty)
    {
        // GDI cannot tell when the screen changes, so look at most 30 times a second.
        long now = Environment.TickCount64;
        if (now < _due)
        {
            int wait = (int)Math.Min(_due - now, timeoutMs);
            if (wait > 0) Thread.Sleep(wait);
            if (Environment.TickCount64 < _due) return CaptureResult.Timeout;
        }
        _due = Environment.TickCount64 + 33;

        var screenDc = ShareNative.GetDC(IntPtr.Zero);
        bool ok;
        try { ok = BitBlt(_memDc, 0, 0, Width, Height, screenDc, _screen.X, _screen.Y, SRCCOPY | CAPTUREBLT); }
        finally { ShareNative.ReleaseDC(IntPtr.Zero, screenDc); }
        if (!ok) return CaptureResult.Lost; // the secure desktop, for one

        if (_first)
        {
            _first = false;
            Buffer.MemoryCopy(_bits, _previous, (long)_pitch * Height, (long)_pitch * Height);
            dirty.Add(new Dirty(0, 0, Width, Height));
            return CaptureResult.Frame;
        }

        for (int top = 0; top < Height; top += Band)
        {
            int rows = Math.Min(Band, Height - top);
            int left = Width, right = -1;
            for (int y = top; y < top + rows; y++)
            {
                var a = new ReadOnlySpan<uint>(_bits + y * _pitch, Width);
                var b = new ReadOnlySpan<uint>(_previous + y * _pitch, Width);
                if (a.SequenceEqual(b)) continue;
                int l = 0;
                while (l < Width && a[l] == b[l]) l++;
                int r = Width - 1;
                while (r > l && a[r] == b[r]) r--;
                left = Math.Min(left, l);
                right = Math.Max(right, r);
            }
            if (right < 0) continue;
            for (int y = top; y < top + rows; y++)
                Buffer.MemoryCopy(_bits + y * _pitch + left * 4, _previous + y * _pitch + left * 4,
                    (right - left + 1) * 4, (right - left + 1) * 4);
            dirty.Add(new Dirty(left, top, right - left + 1, rows));
        }
        return dirty.Count > 0 ? CaptureResult.Frame : CaptureResult.Timeout;
    }

    public byte* Lock(out int pitch)
    {
        pitch = _pitch;
        return _bits;
    }

    public void Unlock() { }

    public void Dispose()
    {
        SelectObject(_memDc, _old);
        ShareNative.DeleteObject(_bitmap);
        DeleteDC(_memDc);
        NativeMemory.Free(_previous);
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref ShareNative.BITMAPINFOHEADER bmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
}

internal static class ScreenSources
{
    /// <summary>Opens the best capture for a screen: Desktop Duplication, else the plain one.</summary>
    public static IScreenSource Open(ShareScreen screen, bool gpu = false)
    {
        try { return new DuplicationSource(screen, gpu); }
        catch (Exception e) when (e is SharpGenException or InvalidOperationException or NotSupportedException)
        {
            Logger.Instance.Info($"Screen sharing uses the plain capture for {screen.DeviceName}: {e.Message}");
            return new GdiSource(screen);
        }
    }
}
