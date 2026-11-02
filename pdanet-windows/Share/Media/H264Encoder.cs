using System.Buffers;
using System.Threading;
using PdaNetPortable.Services;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace PdaNetPortable.Share.Media;

/// <summary>
/// H.264 from NV12 frames, for the screen. Prefers the GPU's encoder, which costs the processor
/// almost nothing, and falls back to Microsoft's encoder that comes with Windows. Set up for
/// remote desktop: one frame in, one frame out, no B frames, keyframes only on request (a lost
/// picture, a new viewer), and a bitrate that follows the link while it runs.
/// </summary>
internal sealed unsafe class H264Encoder : IVideoEncoder
{
    public string Name { get; }
    public bool Hardware { get; }
    /// <summary>The encoder reads GPU textures, see Encode(ID3D11Texture2D...).</summary>
    public bool OnGpu => _manager != null;

    /// <summary>
    /// Each encoded frame: an H.264 access unit with start codes, its duration in 100 ns units and
    /// whether it is a keyframe. Valid during the call only. Raised the moment the encoder finishes
    /// a frame: on the encoder's own thread for a GPU encoder, inside Encode for the others.
    /// </summary>
    public Action<ReadOnlyMemory<byte>, long, bool>? Output { get; set; }
    public int Width { get; }
    public int Height { get; }

    private readonly IMFActivate _activate;
    private readonly IMFTransform _mft;
    private readonly IMFDXGIDeviceManager? _manager; // set when frames come as GPU textures
    private readonly CodecApi? _api;
    private readonly int _inId, _outId;
    private readonly bool _async;
    private readonly IMFMediaEventGenerator? _events;
    private readonly SemaphoreSlim _needInput = new(0);
    private readonly Thread? _eventThread;
    private readonly bool _providesSamples;
    private IMFSample? _outSample;
    private IMFMediaBuffer? _outBuffer;
    private readonly IMFSample[] _inSamples;
    private readonly IMFMediaBuffer[] _inBuffers;
    private int _next;
    private bool _forceKey = true;
    private byte[]? _sps, _pps;
    private readonly ArrayBufferWriter<byte> _encoded = new(256 << 10);
    private int _bitrate;
    private volatile bool _disposed;

    private H264Encoder(IMFActivate activate, bool hardware, int width, int height, int fps, int bitrate, ID3D11Device? device)
    {
        _activate = activate;
        Name = Mf.Name(activate);
        Hardware = hardware;
        Width = width;
        Height = height;
        _bitrate = bitrate;
        _mft = activate.ActivateObject<IMFTransform>();

        IMFAttributes? attrs = null;
        try { attrs = _mft.Attributes; } catch (SharpGenException) { }
        if (attrs != null)
        {
            try { _async = attrs.GetUInt32(TransformAttributeKeys.TransformAsync) != 0; } catch (SharpGenException) { }
            if (_async) attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            try { attrs.Set(CodecApi.LowLatencyMode, 1u); } catch (SharpGenException) { }
        }

        if (device != null)
        {
            // GPU frames: the encoder shares the capture's GPU device, it is told before the types.
            bool aware = false;
            try { aware = attrs != null && attrs.GetUInt32(TransformAttributeKeys.D3D11Aware) != 0; } catch (SharpGenException) { }
            if (!aware) throw new NotSupportedException("the encoder does not take GPU frames");
            _manager = MediaFactory.MFCreateDXGIDeviceManager();
            _manager.ResetDevice(device).CheckError();
            _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)_manager.NativePointer);
        }

        (_inId, _outId) = StreamIds(_mft);
        _api = CodecApi.Of(_mft);
        _api?.Set(CodecApi.LowLatencyMode, true);
        _api?.Set(CodecApi.RateControlMode, CodecApi.RateCbr);
        _api?.Set(CodecApi.MeanBitRate, (uint)bitrate);
        _api?.Set(CodecApi.BPictureCount, 0u);
        // Keyframes come on request, a long interval only bounds how long a damaged picture lasts.
        _api?.Set(CodecApi.GopSize, (uint)(fps * 60));
        _api?.Set(CodecApi.QualityVsSpeed, hardware ? 70u : 33u);

        // Encoders take the output type first. High profile packs text best, Main and Baseline
        // are there for encoders that lack it.
        Exception? last = null;
        foreach (var profile in new[] { Mf.H264ProfileHigh, Mf.H264ProfileMain, Mf.H264ProfileBase })
        {
            using var output = Mf.VideoType(VideoFormatGuids.H264, width, height, fps);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, (uint)profile);
            try
            {
                _mft.SetOutputType(_outId, output, 0);
                last = null;
                break;
            }
            catch (SharpGenException e) { last = e; }
        }
        if (last != null) throw last;
        using (var input = Mf.VideoType(Mf.NV12, width, height, fps))
            _mft.SetInputType(_inId, input, 0);

        var info = _mft.GetOutputStreamInfo(_outId);
        _providesSamples = (info.Flags & (Mf.ProvidesSamples | Mf.CanProvideSamples)) != 0;
        if (!_providesSamples)
        {
            _outSample = MediaFactory.MFCreateSample();
            _outBuffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, width * height * 3 / 2));
            _outSample.AddBuffer(_outBuffer);
        }

        // A GPU encoder may still read a frame or two while it takes the next, so frames rotate
        // through a few samples and none is written while the encoder may still read it. GPU
        // frames need none: each one is its own texture.
        int frames = device != null ? 0 : _async ? 4 : 1;
        _inSamples = new IMFSample[frames];
        _inBuffers = new IMFMediaBuffer[frames];
        for (int i = 0; i < frames; i++)
        {
            _inSamples[i] = MediaFactory.MFCreateSample();
            _inBuffers[i] = MediaFactory.MFCreateMemoryBuffer(width * height * 3 / 2);
            _inSamples[i].AddBuffer(_inBuffers[i]);
        }

        if (_async)
        {
            _events = _mft.QueryInterface<IMFMediaEventGenerator>();
            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "H.264 encoder events" };
            _eventThread.Start();
        }
        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    /// <summary>
    /// Opens the best encoder for this size: the GPU's first, Microsoft's when that fails. With a
    /// <paramref name="device"/>, only a GPU encoder that reads textures from that device will do.
    /// </summary>
    public static H264Encoder Create(int width, int height, int fps, int bitrate, bool allowHardware = true, ID3D11Device? device = null)
    {
        Mf.Start();
        var tried = new List<string>();
        foreach (var hardware in device != null ? new[] { true } : allowHardware ? new[] { true, false } : new[] { false })
        {
            var flags = hardware ? EnumFlag.EnumFlagHardware : EnumFlag.EnumFlagSyncmft;
            foreach (var activate in Mf.Find(TransformCategoryGuids.VideoEncoder, flags, Mf.NV12, VideoFormatGuids.H264))
            {
                try { return new H264Encoder(activate, hardware, width, height, fps, bitrate, device); }
                catch (Exception e)
                {
                    tried.Add($"{Mf.Name(activate)}: {e.Message}");
                    try { activate.ShutdownObject(); } catch { }
                    activate.Dispose();
                }
            }
        }
        Mf.Stop();
        throw new InvalidOperationException(tried.Count == 0
            ? "This PC has no H.264 encoder. On Windows N, install the Media Feature Pack"
            : "No H.264 encoder would start: " + string.Join("; ", tried));
    }

    private static (int In, int Out) StreamIds(IMFTransform mft)
    {
        var ins = new int[1];
        var outs = new int[1];
        try
        {
            mft.GetStreamIDs(1, ins, 1, outs);
            return (ins[0], outs[0]);
        }
        catch (SharpGenException) { return (0, 0); } // E_NOTIMPL: the streams are 0 and 0
    }

    public void RequestKeyframe() => _forceKey = true;

    /// <summary>Follows the bitrate the link can take, small changes are skipped.</summary>
    public void SetBitrate(int bps)
    {
        if (Math.Abs(bps - _bitrate) * 20 < _bitrate) return;
        _bitrate = bps;
        _api?.Set(CodecApi.MeanBitRate, (uint)bps);
    }

    /// <summary>
    /// Encodes one frame, times in 100 ns units. The encoded frame comes out through Output, at
    /// once for the encoders that come with Windows, a few milliseconds later for a GPU encoder.
    /// </summary>
    public void Encode(Nv12Frame frame, long time, long duration)
    {
        var sample = _inSamples[_next];
        var buffer = _inBuffers[_next];
        _next = (_next + 1) % _inSamples.Length;

        buffer.Lock(out var ptr, out _, out _);
        Buffer.MemoryCopy(frame.Data, (void*)ptr, frame.Length, frame.Length);
        buffer.Unlock();
        buffer.CurrentLength = frame.Length;
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        Submit(sample);
    }

    /// <summary>
    /// Encodes one GPU texture in NV12, made on the device the encoder was opened with. The texture
    /// must stay unchanged until the encoder is done with it, GpuConverter's pool sees to that.
    /// </summary>
    public void Encode(ID3D11Texture2D texture, long time, long duration)
    {
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, texture, 0, false);
        using (var flat = buffer.QueryInterface<IMF2DBuffer>()) buffer.CurrentLength = flat.ContiguousLength;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        Submit(sample);
    }

    private void Submit(IMFSample sample)
    {
        if (_forceKey)
        {
            _forceKey = false;
            _api?.Set(CodecApi.ForceKeyFrame, 1u);
        }

        if (_async)
        {
            // A GPU encoder says when it takes the next frame. It finishes frames on its own
            // thread, see EventLoop, so waiting here never holds a finished frame back.
            if (!_needInput.Wait(1500)) throw new TimeoutException("The H.264 encoder stopped taking frames");
            _mft.ProcessInput(_inId, sample, 0);
            return;
        }
        _mft.ProcessInput(_inId, sample, 0);
        while (Pull()) { }
    }

    /// <summary>Takes one finished frame out of the encoder and hands it to Output.</summary>
    private bool Pull()
    {
        var db = new OutputDataBuffer { StreamID = _outId, Sample = _providesSamples ? null! : _outSample! };
        if (!_providesSamples) _outBuffer!.CurrentLength = 0;
        var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _);
        try
        {
            if (hr.Code == Mf.E_NEED_MORE_INPUT) return false;
            if (hr.Code == Mf.E_STREAM_CHANGE)
            {
                using var t = _mft.GetOutputAvailableType(_outId, 0);
                _mft.SetOutputType(_outId, t, 0);
                return true;
            }
            hr.CheckError();
            if (db.Sample == null) return false;
            long duration;
            try { duration = db.Sample.SampleDuration; }
            catch (SharpGenException) { duration = 0; }
            _encoded.Clear();
            using (var contiguous = db.Sample.ConvertToContiguousBuffer())
            {
                contiguous.Lock(out var p, out _, out var length);
                try { _encoded.Write(new ReadOnlySpan<byte>((void*)p, length)); }
                finally { contiguous.Unlock(); }
            }
            bool key = Scan(_encoded.WrittenSpan);
            if (key && _sps != null && _pps != null && !HasParameterSets(_encoded.WrittenSpan))
            {
                // Some encoders send the parameter sets once at the start. Every keyframe carries
                // them here, so a viewer can always start over from one.
                var body = _encoded.WrittenSpan.ToArray();
                _encoded.Clear();
                _encoded.Write(_sps);
                _encoded.Write(_pps);
                _encoded.Write(body);
            }
            Output?.Invoke(_encoded.WrittenMemory, duration, key);
            return true;
        }
        finally
        {
            if (_providesSamples) db.Sample?.Dispose();
            db.Events?.Dispose();
        }
    }

    /// <summary>Finds an IDR slice and keeps the newest parameter sets.</summary>
    private bool Scan(ReadOnlySpan<byte> au)
    {
        bool key = false;
        foreach (var (start, end) in Nalus(au))
        {
            int type = au[start] & 0x1f;
            if (type == 5) key = true;
            else if (type == 7) _sps = WithStartCode(au[start..end]);
            else if (type == 8) _pps = WithStartCode(au[start..end]);
        }
        return key;
    }

    private static bool HasParameterSets(ReadOnlySpan<byte> au)
    {
        foreach (var (start, _) in Nalus(au))
            if ((au[start] & 0x1f) == 7) return true;
        return false;
    }

    private static byte[] WithStartCode(ReadOnlySpan<byte> nal)
    {
        var b = new byte[4 + nal.Length];
        b[3] = 1;
        nal.CopyTo(b.AsSpan(4));
        return b;
    }

    /// <summary>The NAL units of an Annex B stream, as ranges after each start code.</summary>
    internal static List<(int Start, int End)> Nalus(ReadOnlySpan<byte> b)
    {
        var list = new List<(int, int)>();
        int start = -1;
        for (int i = 0; i + 2 < b.Length; i++)
        {
            if (b[i] != 0 || b[i + 1] != 0 || b[i + 2] != 1) continue;
            if (start >= 0)
            {
                int end = i;
                while (end > start && b[end - 1] == 0) end--; // the zero of a 4 byte start code
                list.Add((start, end));
            }
            start = i + 3;
            i += 2;
        }
        if (start >= 0 && start < b.Length) list.Add((start, b.Length));
        return list;
    }

    private void EventLoop()
    {
        try
        {
            while (!_disposed)
            {
                using var e = _events!.GetEvent(0);
                switch (e.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        _needInput.Release();
                        break;
                    case MediaEventTypes.TransformHaveOutput:
                        try { Pull(); }
                        catch (Exception x) when (x is SharpGenException or InvalidOperationException)
                        {
                            Logger.Instance.Debug($"Screen sharing encoder output: {x.Message}");
                        }
                        break;
                }
            }
        }
        catch (SharpGenException) { /* shut down */ }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        // Shutting the transform down also wakes the event thread.
        try { _activate.ShutdownObject(); } catch { }
        _eventThread?.Join(500);
        _events?.Dispose();
        _api?.Dispose();
        foreach (var s in _inSamples) s.Dispose();
        foreach (var b in _inBuffers) b.Dispose();
        _outSample?.Dispose();
        _outBuffer?.Dispose();
        _mft.Dispose();
        _manager?.Dispose();
        _activate.Dispose();
        _needInput.Dispose();
        Mf.Stop();
        Logger.Instance.Debug($"Screen sharing encoder closed: {Name}");
    }
}
