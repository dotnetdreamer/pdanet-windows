using System.Runtime.InteropServices;
using System.Threading;
using PdaNetPortable.Services;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PdaNetPortable.Share.Media;

/// <summary>
/// Where the picture goes in a target, in the target's pixels, as WPF lays it out: its top left
/// corner, and the steps from there to its top right and to its bottom left. Any scale, turn and
/// scroll WPF gives the picture is such a parallelogram. Width and Height are the picture's own
/// size the layout was made for.
/// </summary>
internal readonly record struct PicturePlacement(double X, double Y, double UX, double UY, double VX, double VY, int Width, int Height)
{
    /// <summary>The picture as large as fits, in the middle, upright: until the window lays out a new size.</summary>
    public static PicturePlacement Fitted(int width, int height, int targetWidth, int targetHeight)
    {
        double scale = Math.Min((double)targetWidth / width, (double)targetHeight / height);
        double w = width * scale, h = height * scale;
        return new((targetWidth - w) / 2, (targetHeight - h) / 2, w, 0, 0, h, width, height);
    }

    /// <summary>
    /// Where WPF lays picture out, in host's pixels: its corners through every transform between
    /// the two, a Viewbox's scale, a ScrollViewer's offset, a LayoutTransform's turn, and then the
    /// screen's DPI (toDevice). Null while the picture has no size or no common ancestor yet.
    /// </summary>
    public static PicturePlacement? FromLayout(System.Windows.FrameworkElement picture, System.Windows.Media.Visual host,
        System.Windows.Media.Matrix toDevice, int width, int height)
    {
        if (picture.ActualWidth <= 0 || picture.ActualHeight <= 0 || width <= 0 || height <= 0) return null;
        System.Windows.Media.GeneralTransform toHost;
        try { toHost = picture.TransformToVisual(host); }
        catch (InvalidOperationException) { return null; }
        System.Windows.Point At(double x, double y) => toDevice.Transform(toHost.Transform(new System.Windows.Point(x, y)));
        var o = At(0, 0);
        var u = At(picture.ActualWidth, 0);
        var v = At(0, picture.ActualHeight);
        return new PicturePlacement(o.X, o.Y, u.X - o.X, u.Y - o.Y, v.X - o.X, v.Y - o.Y, width, height);
    }
}

/// <summary>
/// Draws decoded NV12 pictures with the GPU. Pass one turns the picture into BGRA at its own size
/// with exactly ColorConvert's integer math, so the GPU shows every pixel the processor would.
/// Pass two draws that picture into a target, placed as WPF lays it out, filtered the way WPF
/// scales a bitmap: each pixel as it is at actual size, smooth larger, and from mipmaps smaller,
/// where plain linear filtering skips pixels and thin text breaks up. All on one thread.
/// </summary>
internal sealed unsafe class Nv12Renderer : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public string Adapter { get; }

    private readonly ID3D11VertexShader _fill, _quad;
    private readonly ID3D11PixelShader _toBgra, _show;
    private readonly ID3D11SamplerState _point, _linear, _trilinear;
    private readonly ID3D11RasterizerState _raster;
    private readonly ID3D11Buffer _place;
    private ID3D11Texture2D? _luma, _chroma, _picture;
    private ID3D11ShaderResourceView? _lumaView, _chromaView, _pictureView;
    private ID3D11RenderTargetView? _pictureTarget;
    private bool _converted = true, _mips;

    /// <summary>The size of the picture uploaded last, 0 before the first.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool HasPicture => _picture != null;

    private Nv12Renderer(ID3D11Device device, ID3D11DeviceContext context, string adapter)
    {
        Device = device;
        Context = context;
        Adapter = adapter;
        try
        {
            _fill = device.CreateVertexShader(PresentShaders.Fill);
            _quad = device.CreateVertexShader(PresentShaders.Quad);
            _toBgra = device.CreatePixelShader(PresentShaders.ToBgra);
            _show = device.CreatePixelShader(PresentShaders.Show);
            _point = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, 0));
            _linear = device.CreateSamplerState(new SamplerDescription(Filter.MinMagLinearMipPoint, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, 0));
            _trilinear = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));
            _raster = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
            _place = device.CreateBuffer(new BufferDescription(32, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// A renderer on this PC's GPU, the one Windows gives apps by default; null, with why, where there
    /// is none to draw with: no Direct3D 11 hardware, or only Windows' basic display driver, as in
    /// some Remote Desktop sessions. There WPF draws the picture as before.
    /// </summary>
    public static Nv12Renderer? Create(out string why)
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        try
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 }, out device, out context);
            if (hr.Failure || device == null || context == null)
            {
                why = $"no Direct3D 11 GPU ({hr})";
                context?.Dispose();
                device?.Dispose();
                return null;
            }
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            var desc = adapter.Description;
            using var adapter1 = adapter.QueryInterfaceOrNull<IDXGIAdapter1>();
            bool software = adapter1 != null && (adapter1.Description1.Flags & AdapterFlags.Software) != 0;
            // Windows' basic render driver, a GPU in name only.
            if (software || (desc.VendorId == 0x1414 && desc.DeviceId == 0x8C))
            {
                why = $"only a software renderer ({desc.Description.Trim()})";
                context.Dispose();
                device.Dispose();
                return null;
            }
            why = "";
            return new Nv12Renderer(device, context, desc.Description.Trim());
        }
        catch (Exception e) when (e is SharpGenException or DllNotFoundException or EntryPointNotFoundException)
        {
            why = e.Message.Split('\n')[0];
            context?.Dispose();
            device?.Dispose();
            return null;
        }
    }

    /// <summary>Copies a decoded picture into the GPU's textures; pass one runs when it is drawn.</summary>
    public void Upload(Nv12Frame frame)
    {
        if (frame.Width != Width || frame.Height != Height || _picture == null) Allocate(frame.Width, frame.Height);
        Copy(_luma!, frame.Data, frame.Width, frame.Height);
        Copy(_chroma!, frame.UV, frame.Width, frame.Height / 2);
        _converted = false;
    }

    private void Copy(ID3D11Texture2D texture, byte* source, int rowBytes, int rows)
    {
        var map = Context.Map(texture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* target = (byte*)map.DataPointer;
            int pitch = (int)map.RowPitch;
            if (pitch == rowBytes) Buffer.MemoryCopy(source, target, (long)rowBytes * rows, (long)rowBytes * rows);
            else
                for (int y = 0; y < rows; y++)
                    Buffer.MemoryCopy(source + (long)y * rowBytes, target + (long)y * pitch, rowBytes, rowBytes);
        }
        finally { Context.Unmap(texture, 0); }
    }

    private void Allocate(int width, int height)
    {
        FreePicture();
        Width = width;
        Height = height;
        Texture2DDescription Plane(int w, int h, Format format) => new()
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.Write,
        };
        _luma = Device.CreateTexture2D(Plane(width, height, Format.R8_UInt));
        _chroma = Device.CreateTexture2D(Plane(width / 2, height / 2, Format.R8G8_UInt));
        _lumaView = Device.CreateShaderResourceView(_luma);
        _chromaView = Device.CreateShaderResourceView(_chroma);
        // The whole chain of mipmaps, for drawing the picture smaller.
        _picture = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 0, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource, MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _pictureView = Device.CreateShaderResourceView(_picture);
        _pictureTarget = Device.CreateRenderTargetView(_picture,
            new RenderTargetViewDescription(_picture, RenderTargetViewDimension.Texture2D, Format.B8G8R8A8_UNorm, 0, 0, 1));
    }

    /// <summary>Pass one, for a picture uploaded since the last: NV12 into the BGRA picture.</summary>
    public void Convert()
    {
        if (_converted || _pictureTarget == null) return;
        var c = Context;
        c.OMSetRenderTargets(_pictureTarget);
        c.RSSetViewport(new Viewport(0, 0, Width, Height));
        c.RSSetState(_raster);
        c.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        c.IASetInputLayout(null);
        c.VSSetShader(_fill);
        c.PSSetShader(_toBgra);
        c.PSSetShaderResource(0, _lumaView!);
        c.PSSetShaderResource(1, _chromaView!);
        c.Draw(3, 0);
        c.PSSetShaderResource(0, null!);
        c.PSSetShaderResource(1, null!);
        c.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        _converted = true;
        _mips = false;
    }

    /// <summary>
    /// Pass two: the target black, then the picture into it as placed. A placement made for another
    /// size than the picture's draws nothing but the black.
    /// </summary>
    public void Draw(ID3D11RenderTargetView target, int targetWidth, int targetHeight, PicturePlacement place)
    {
        Convert();
        var c = Context;
        c.ClearRenderTargetView(target, new Color4(0, 0, 0, 1));
        if (_pictureView == null || place.Width != Width || place.Height != Height || targetWidth <= 0 || targetHeight <= 0) return;
        // How WPF scales a bitmap, see RemoteWindow.UpdateScaling: about actual size each pixel as
        // it is, smaller from mipmaps, larger linear.
        double scale = Math.Sqrt(place.UX * place.UX + place.UY * place.UY) / Width;
        ID3D11SamplerState sampler;
        if (Math.Abs(scale - 1) < 0.01)
        {
            sampler = _point;
            // At exactly actual size, on whole pixels: each pixel of the window is one of the picture.
            if (Math.Abs(scale - 1) < 1e-4) place = place with { X = Math.Round(place.X), Y = Math.Round(place.Y) };
        }
        else if (scale < 1)
        {
            if (!_mips)
            {
                c.GenerateMips(_pictureView);
                _mips = true;
            }
            sampler = _trilinear;
        }
        else sampler = _linear;

        float sx = 2f / targetWidth, sy = -2f / targetHeight;
        var map = c.Map(_place, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        float* p = (float*)map.DataPointer;
        p[0] = (float)(place.X * sx - 1);
        p[1] = (float)(place.Y * sy + 1);
        p[2] = (float)(place.UX * sx);
        p[3] = (float)(place.UY * sy);
        p[4] = (float)(place.VX * sx);
        p[5] = (float)(place.VY * sy);
        p[6] = p[7] = 0;
        c.Unmap(_place, 0);

        c.OMSetRenderTargets(target);
        c.RSSetViewport(new Viewport(0, 0, targetWidth, targetHeight));
        c.RSSetState(_raster);
        c.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        c.IASetInputLayout(null);
        c.VSSetShader(_quad);
        c.VSSetConstantBuffer(0, _place);
        c.PSSetShader(_show);
        c.PSSetShaderResource(0, _pictureView);
        c.PSSetSampler(0, sampler);
        c.Draw(4, 0);
        c.PSSetShaderResource(0, null!);
        c.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    /// <summary>For the self-test: the converted picture's pixels, BGRA, row after row.</summary>
    public byte[] ReadPicture()
    {
        Convert();
        return ReadTexture(_picture!, Width, Height);
    }

    /// <summary>For the self-test: a BGRA texture's top mip, read back.</summary>
    public byte[] ReadTexture(ID3D11Texture2D texture, int width, int height)
    {
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        Context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0);
        var map = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var result = new byte[width * height * 4];
            fixed (byte* r = result)
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, r + (long)y * width * 4, width * 4, width * 4);
            return result;
        }
        finally { Context.Unmap(staging, 0); }
    }

    private void FreePicture()
    {
        _pictureTarget?.Dispose();
        _pictureView?.Dispose();
        _picture?.Dispose();
        _lumaView?.Dispose();
        _chromaView?.Dispose();
        _luma?.Dispose();
        _chroma?.Dispose();
        _pictureTarget = null;
        _pictureView = _lumaView = _chromaView = null;
        _picture = _luma = _chroma = null;
        Width = Height = 0;
    }

    public void Dispose()
    {
        FreePicture();
        _place?.Dispose();
        _raster?.Dispose();
        _trilinear?.Dispose();
        _linear?.Dispose();
        _point?.Dispose();
        _show?.Dispose();
        _toBgra?.Dispose();
        _quad?.Dispose();
        _fill?.Dispose();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }
}

/// <summary>Where the presenter takes its pictures from: the viewer's pipeline, or a test's.</summary>
internal interface IPictureSource
{
    /// <summary>A new picture waits, raised on the decoder's thread.</summary>
    event Action? FrameReady;

    /// <summary>See ViewerPipeline.Read.</summary>
    bool Read(bool newest, Action<Nv12Frame> read);
}

/// <summary>
/// The other PC's screen in a window of its own, a child of the viewer's, through a Direct3D 11
/// flip model swap chain. A thread of its own takes each new picture from the decoder, converts
/// and draws it on the GPU and presents it, without the window's thread: no NV12 to BGRA on the
/// processor, no 8 MB bitmap for WPF to copy to the GPU again, no wait for WPF's next frame.
/// The swap chain holds at most one frame on its way to the screen and the thread waits until it
/// takes one, so the picture that goes is always the newest.
/// While it does not run, the window draws the pictures with WPF, as before.
/// </summary>
internal sealed class SwapChainPresenter : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Nv12Renderer _renderer;
    private readonly IDXGISwapChain2 _chain;
    private readonly IntPtr _waitable;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private IPictureSource? _viewer;
    private bool _active, _newest, _stop, _reading;
    private PicturePlacement _place;
    private ID3D11Texture2D? _buffer;
    private ID3D11RenderTargetView? _target;
    private int _bufferWidth, _bufferHeight, _askedWidth, _askedHeight;
    private long _presented;

    private long _readTicks, _drawTicks, _presentTicks;

    public string Adapter => _renderer.Adapter;

    /// <summary>Presents so far, for the self-test and the log.</summary>
    public long Presented => Interlocked.Read(ref _presented);

    /// <summary>
    /// What a frame costs this thread on average, in milliseconds: taking the picture and copying
    /// it to the GPU, the two passes, and the present. The GPU's own work runs after.
    /// </summary>
    public (double Read, double Draw, double Present) Cost()
    {
        lock (_gate)
        {
            double n = Math.Max(1, Interlocked.Read(ref _presented)) * System.Diagnostics.Stopwatch.Frequency / 1000.0;
            return (_readTicks / n, _drawTicks / n, _presentTicks / n);
        }
    }

    /// <summary>The GPU stopped working for this, the text says why. Raised on the presenter's thread, once.</summary>
    public event Action<string>? Lost;

    /// <summary>
    /// A picture of a new size came and the window's layout is for the old one. Raised on the
    /// presenter's thread; the window lays out the new size and places the picture again.
    /// </summary>
    public event Action<int, int>? NewSize;

    private readonly Action<Nv12Frame> _upload;

    private SwapChainPresenter(IntPtr hwnd, Nv12Renderer renderer, IDXGISwapChain2 chain)
    {
        _hwnd = hwnd;
        _renderer = renderer;
        _upload = renderer.Upload;
        _chain = chain;
        _waitable = chain.FrameLatencyWaitableObject;
        _thread = new Thread(Run) { IsBackground = true, Name = "Screen sharing presenter", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// A presenter for the child window hwnd. Throws where the GPU cannot do it, see
    /// Nv12Renderer.Create; the window then draws with WPF.
    /// </summary>
    public static SwapChainPresenter Create(IntPtr hwnd)
    {
        var renderer = Nv12Renderer.Create(out string why) ?? throw new NotSupportedException(why);
        try
        {
            using var dxgi = renderer.Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var desc = new SwapChainDescription1
            {
                Width = 0, Height = 0, // the window's own size
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.FrameLatencyWaitableObject,
            };
            IDXGISwapChain1 chain1;
            try { chain1 = factory.CreateSwapChainForHwnd(renderer.Device, hwnd, desc); }
            catch (SharpGenException)
            {
                // Windows before 10 knows only the older flip model.
                desc.SwapEffect = SwapEffect.FlipSequential;
                chain1 = factory.CreateSwapChainForHwnd(renderer.Device, hwnd, desc);
            }
            using (chain1)
            {
                // Full screen is the window's own business, never DXGI's.
                factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
                var chain = chain1.QueryInterface<IDXGISwapChain2>();
                chain.MaximumFrameLatency = 1;
                if (chain.FrameLatencyWaitableObject == IntPtr.Zero)
                {
                    chain.Dispose();
                    throw new NotSupportedException("the swap chain gives no frame latency handle");
                }
                return new SwapChainPresenter(hwnd, renderer, chain);
            }
        }
        catch
        {
            renderer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Shows viewer's pictures from now on, the newest it has first, placed as given. From the
    /// window's thread, after which the window takes no pictures of its own.
    /// </summary>
    public void Start(IPictureSource viewer, PicturePlacement place)
    {
        lock (_gate)
        {
            if (_viewer != viewer)
            {
                if (_viewer != null) _viewer.FrameReady -= OnFrame;
                _viewer = viewer;
                viewer.FrameReady += OnFrame;
            }
            _place = place;
            _active = _newest = true;
        }
        _wake.Set();
    }

    /// <summary>
    /// Stops taking pictures, from the window's thread, which takes them again: it reads the
    /// newest itself next. Returns once this thread took its last, so none goes past the window.
    /// Never call it while reading a picture through ViewerPipeline.Read: this thread may wait
    /// for that read to end.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _active = false;
            if (_viewer != null) _viewer.FrameReady -= OnFrame;
            _viewer = null;
            long until = Environment.TickCount64 + 500;
            while (_reading && Environment.TickCount64 < until) Monitor.Wait(_gate, 50);
        }
    }

    /// <summary>The window laid the picture out anew: moved, scaled, scrolled, turned.</summary>
    public void Place(PicturePlacement place)
    {
        lock (_gate)
        {
            if (_place == place) return;
            _place = place;
        }
        _wake.Set();
    }

    /// <summary>The child window changed its size: the swap chain follows before the next frame.</summary>
    public void Resized() => _wake.Set();

    private void OnFrame() => _wake.Set();

    private void Run()
    {
        try
        {
            while (true)
            {
                _wake.WaitOne();
                lock (_gate)
                {
                    if (_stop) return;
                    if (!_active) continue;
                }
                // Until the swap chain takes a frame. A window that is covered or minimized takes
                // none; this tries again a little later, and presents nothing meanwhile.
                if (WaitForSingleObjectEx(_waitable, 100, true) != 0)
                {
                    Thread.Sleep(15);
                    _wake.Set();
                    continue;
                }
                // Each wait for the swap chain is followed by exactly one present, else it would
                // wait for a frame that never went. The newest picture, taken only while this
                // runs: Stop waits for a read under way.
                IPictureSource? viewer = null;
                bool newest = false;
                lock (_gate)
                {
                    if (_active && _viewer != null)
                    {
                        (viewer, newest) = (_viewer, _newest);
                        _newest = false;
                        _reading = true;
                    }
                }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (viewer != null)
                {
                    try { viewer.Read(newest, _upload); }
                    finally
                    {
                        lock (_gate)
                        {
                            _reading = false;
                            Monitor.PulseAll(_gate);
                        }
                    }
                }
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                Resize();
                PicturePlacement place;
                lock (_gate) place = _place;
                if (_renderer.HasPicture && (place.Width != _renderer.Width || place.Height != _renderer.Height))
                {
                    // A new size, before the window laid it out: shown whole meanwhile.
                    if (_askedWidth != _renderer.Width || _askedHeight != _renderer.Height)
                    {
                        (_askedWidth, _askedHeight) = (_renderer.Width, _renderer.Height);
                        NewSize?.Invoke(_renderer.Width, _renderer.Height);
                    }
                    place = PicturePlacement.Fitted(_renderer.Width, _renderer.Height, _bufferWidth, _bufferHeight);
                }
                if (_target != null) _renderer.Draw(_target, _bufferWidth, _bufferHeight, place);
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                var hr = _chain.Present(0, PresentFlags.None);
                if (hr.Failure)
                {
                    Fail($"presenting failed ({hr}, {_renderer.Device.DeviceRemovedReason})");
                    return;
                }
                long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    _readTicks += t1 - t0;
                    _drawTicks += t2 - t1;
                    _presentTicks += t3 - t2;
                }
                Interlocked.Increment(ref _presented);
            }
        }
        catch (Exception e) when (e is SharpGenException or ObjectDisposedException or InvalidOperationException)
        {
            Fail(e.Message.Split('\n')[0]);
        }
    }

    private void Fail(string why)
    {
        lock (_gate)
        {
            _active = false;
            if (_viewer != null) _viewer.FrameReady -= OnFrame;
            _viewer = null;
        }
        try { Lost?.Invoke(why); }
        catch (Exception e) { Logger.Instance.Warning($"Screen sharing presenter: {e.Message}"); }
    }

    /// <summary>The swap chain to the child window's size, which WPF's layout sets.</summary>
    private void Resize()
    {
        if (!GetClientRect(_hwnd, out var rc)) return;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0 || (_target != null && w == _bufferWidth && h == _bufferHeight)) return;
        _target?.Dispose();
        _buffer?.Dispose();
        _target = null;
        _buffer = null;
        // Nothing may hold on to the old buffers, bound or waiting to be let go.
        _renderer.Context.ClearState();
        _renderer.Context.Flush();
        _chain.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
        _buffer = _chain.GetBuffer<ID3D11Texture2D>(0);
        _target = _renderer.Device.CreateRenderTargetView(_buffer);
        (_bufferWidth, _bufferHeight) = (w, h);
    }

    /// <summary>
    /// Stops the thread and lets go of the GPU. Before the child window goes: a swap chain must
    /// never outlive its window.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_stop) return;
            _stop = true;
            _active = false;
            if (_viewer != null) _viewer.FrameReady -= OnFrame;
            _viewer = null;
        }
        _wake.Set();
        // A thread stuck in the driver keeps what it uses: better a leak than a crash.
        if (!_thread.Join(3000)) return;
        _target?.Dispose();
        _buffer?.Dispose();
        _chain.Dispose();
        CloseHandle(_waitable);
        _renderer.Dispose();
        _wake.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObjectEx(IntPtr handle, uint milliseconds, bool alertable);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
