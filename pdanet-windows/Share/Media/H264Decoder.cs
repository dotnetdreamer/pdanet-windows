using PdaNetPortable.Services;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace PdaNetPortable.Share.Media;

/// <summary>
/// H.264 to NV12 with the decoder that comes with Windows, in low latency mode so each frame comes
/// out as soon as it goes in. The picture size comes from the stream itself, so the other PC can
/// switch screens or sizes at any keyframe.
/// </summary>
internal sealed unsafe class H264Decoder : IDisposable
{
    public string Name { get; }

    /// <summary>The size of the picture that is shown, without the padding H.264 adds.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    private readonly IMFActivate _activate;
    private readonly IMFTransform _mft;
    private IMFSample? _out;
    private IMFMediaBuffer? _outBuffer;
    private bool _providesSamples, _typed;
    private int _stride, _codedHeight;
    private bool _disposed;

    private H264Decoder(IMFActivate activate)
    {
        _activate = activate;
        Name = Mf.Name(activate);
        _mft = activate.ActivateObject<IMFTransform>();
        try { _mft.Attributes.Set(CodecApi.LowLatencyMode, 1u); } catch (SharpGenException) { }
        using (var api = CodecApi.Of(_mft)) api?.Set(CodecApi.LowLatencyMode, true);
        using (var input = Mf.VideoType(VideoFormatGuids.H264, 0, 0, 0))
            _mft.SetInputType(0, input, 0);
        TryOutputType();
        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public static H264Decoder Create()
    {
        Mf.Start();
        var tried = new List<string>();
        foreach (var activate in Mf.Find(TransformCategoryGuids.VideoDecoder, EnumFlag.EnumFlagSyncmft, VideoFormatGuids.H264, Mf.NV12))
        {
            try { return new H264Decoder(activate); }
            catch (Exception e)
            {
                tried.Add($"{Mf.Name(activate)}: {e.Message}");
                activate.Dispose();
            }
        }
        Mf.Stop();
        throw new InvalidOperationException(tried.Count == 0
            ? "This PC has no H.264 decoder. On Windows N, install the Media Feature Pack"
            : "No H.264 decoder would start: " + string.Join("; ", tried));
    }

    /// <summary>
    /// Decodes one access unit. When a picture comes out it is copied into <paramref name="frame"/>,
    /// which is made anew when the picture size changes. Returns true when there is a new picture.
    /// </summary>
    public bool Decode(ReadOnlySpan<byte> au, long time, ref Nv12Frame? frame)
    {
        if (au.IsEmpty) return false;
        // Each frame gets its own sample: the decoder works on several threads and may still read
        // a frame after it took it, so a sample is never written twice.
        using var buffer = MediaFactory.MFCreateMemoryBuffer(au.Length);
        buffer.Lock(out var p, out _, out _);
        au.CopyTo(new Span<byte>((void*)p, au.Length));
        buffer.Unlock();
        buffer.CurrentLength = au.Length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;

        bool got = false;
        try { _mft.ProcessInput(0, sample, 0); }
        catch (SharpGenException e) when (e.ResultCode.Code != Mf.E_NOTACCEPTING)
        {
            throw new InvalidOperationException($"ProcessInput {e.ResultCode}", e);
        }
        catch (SharpGenException e) when (e.ResultCode.Code == Mf.E_NOTACCEPTING)
        {
            // It still holds a picture: take it out, then the frame goes in.
            got |= Drain(ref frame);
            _mft.ProcessInput(0, sample, 0);
        }
        got |= Drain(ref frame);
        return got;
    }

    private bool Drain(ref Nv12Frame? frame)
    {
        bool got = false;
        for (int guard = 0; guard < 8; guard++)
        {
            if (!_typed && !TryOutputType()) return got;
            var db = new OutputDataBuffer { StreamID = 0, Sample = _providesSamples ? null! : _out! };
            // The decoder only writes into an empty buffer.
            if (!_providesSamples) _outBuffer!.CurrentLength = 0;
            var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _);
            try
            {
                if (hr.Code == Mf.E_NEED_MORE_INPUT) return got;
                if (hr.Code == Mf.E_STREAM_CHANGE)
                {
                    _typed = false;
                    continue;
                }
                if (hr.Failure) throw new InvalidOperationException($"ProcessOutput {hr} provides={_providesSamples}");
                if (db.Sample != null)
                {
                    CopyOut(db.Sample, ref frame);
                    got = true;
                }
            }
            finally
            {
                if (_providesSamples) db.Sample?.Dispose();
                db.Events?.Dispose();
            }
        }
        return got;
    }

    /// <summary>Picks NV12 for the output and reads the picture size the stream announced.</summary>
    private bool TryOutputType()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _mft.GetOutputAvailableType(0, i); }
            catch (SharpGenException) { return false; } // no size known yet, or no NV12
            using (t)
            {
                if (t.GetGUID(MediaTypeAttributeKeys.Subtype) != Mf.NV12) continue;
                try { _mft.SetOutputType(0, t, 0); }
                catch (SharpGenException) { return false; }

                var (w, h) = Mf.Unpack(t.GetUInt64(MediaTypeAttributeKeys.FrameSize));
                _codedHeight = h;
                _stride = w;
                try
                {
                    int s = (int)t.GetUInt32(MediaTypeAttributeKeys.DefaultStride);
                    if (s >= w) _stride = s;
                }
                catch (SharpGenException) { }
                Width = w;
                Height = h;
                try
                {
                    // MFVideoArea: two 32 bit offsets, then the visible width and height.
                    var area = t.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture);
                    if (area.Length >= 16)
                    {
                        int cw = BitConverter.ToInt32(area, 8), ch = BitConverter.ToInt32(area, 12);
                        if (cw > 0 && cw <= w && ch > 0 && ch <= h)
                        {
                            Width = cw;
                            Height = ch;
                        }
                    }
                }
                catch (SharpGenException) { }

                var info = _mft.GetOutputStreamInfo(0);
                _providesSamples = (info.Flags & (Mf.ProvidesSamples | Mf.CanProvideSamples)) != 0;
                _out?.Dispose();
                _outBuffer?.Dispose();
                _out = null;
                _outBuffer = null;
                if (!_providesSamples)
                {
                    _out = MediaFactory.MFCreateSample();
                    _outBuffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, _stride * h * 3 / 2));
                    _out.AddBuffer(_outBuffer);
                }
                _typed = true;
                Logger.Instance.Debug($"Screen sharing decoder: {Width}x{Height}, coded {w}x{h}, stride {_stride}");
                return true;
            }
        }
    }

    private void CopyOut(IMFSample sample, ref Nv12Frame? frame)
    {
        if (frame == null || frame.Width != (Width & ~1) || frame.Height != (Height & ~1))
        {
            frame?.Dispose();
            frame = new Nv12Frame(Width, Height);
        }
        using var buffer = sample.GetBufferByIndex(0);
        var flat = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        byte* scan0;
        int pitch;
        if (flat != null)
        {
            flat.Lock2D(out var p, out pitch);
            scan0 = (byte*)p;
        }
        else
        {
            buffer.Lock(out var p, out _, out _);
            scan0 = (byte*)p;
            pitch = _stride;
        }
        try
        {
            int w = frame.Width, h = frame.Height;
            // A negative pitch means the rows run bottom up, which NV12 from a decoder never does.
            if (pitch < w) pitch = _stride;
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(scan0 + (long)y * pitch, frame.Data + y * w, w, w);
            byte* uv = scan0 + (long)pitch * _codedHeight;
            for (int y = 0; y < h / 2; y++)
                Buffer.MemoryCopy(uv + (long)y * pitch, frame.UV + y * w, w, w);
        }
        finally
        {
            if (flat != null)
            {
                flat.Unlock2D();
                flat.Dispose();
            }
            else buffer.Unlock();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        _out?.Dispose();
        _outBuffer?.Dispose();
        _mft.Dispose();
        try { _activate.ShutdownObject(); } catch { }
        _activate.Dispose();
        Mf.Stop();
    }
}
