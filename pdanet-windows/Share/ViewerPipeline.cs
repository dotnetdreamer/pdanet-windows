using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;
using PdaNetPortable.Services;
using PdaNetPortable.Share.Media;

namespace PdaNetPortable.Share;

/// <summary>
/// The other PC's screen, from the link to a picture, on its own thread. Frames wait in a short
/// queue; when this PC falls behind, frames are dropped instead of piling up delay. After a lost
/// frame the picture holds still until the next keyframe, it never shows a damaged picture. The
/// window takes the newest picture when it is ready to draw, from three buffers that rotate, so
/// decoding never waits for drawing.
/// </summary>
internal sealed class ViewerPipeline : IDisposable
{
    private readonly BlockingCollection<(byte[] Buffer, int Length)> _queue = new(new ConcurrentQueue<(byte[], int)>(), 4);
    private readonly Thread _thread;
    private readonly Action _askKeyframe;
    private readonly object _gate = new();
    private Nv12Frame? _decoding, _ready, _shown;
    private bool _hasReady, _signaled, _dropped;
    private long _lastAsk;

    public string Decoder { get; private set; } = "";
    public int FramesShown;

    /// <summary>A new picture waits. Raised on the decode thread, once until the window takes it.</summary>
    public event Action? FrameReady;

    /// <summary>The decoder could not start, the text says why.</summary>
    public event Action<string>? Failed;

    public ViewerPipeline(Action askKeyframe)
    {
        _askKeyframe = askKeyframe;
        _thread = new Thread(Run) { IsBackground = true, Name = "Screen sharing viewer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Takes a frame from the link: u8 flags, u32 RTP timestamp, H.264. Copied, so the caller may reuse its buffer.</summary>
    public void Push(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6 || _queue.IsAddingCompleted) return;
        var buffer = ArrayPool<byte>.Shared.Rent(frame.Length);
        frame.CopyTo(buffer);
        bool added;
        try { added = _queue.TryAdd((buffer, frame.Length)); }
        catch (InvalidOperationException) { added = false; }
        if (added) return;
        ArrayPool<byte>.Shared.Return(buffer);
        _dropped = true;
    }

    private void Run()
    {
        H264Decoder? decoder = null;
        try { decoder = H264Decoder.Create(); }
        catch (Exception e)
        {
            Failed?.Invoke(e.Message);
            return;
        }
        Decoder = decoder.Name;
        bool waitKey = true;
        int errors = 0;
        try
        {
            foreach (var (buffer, length) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var f = buffer.AsSpan(0, length);
                    byte flags = f[0];
                    uint ts = BinaryPrimitives.ReadUInt32LittleEndian(f[1..]);
                    bool key = (flags & 1) != 0;
                    if ((flags & 2) != 0 || _dropped)
                    {
                        _dropped = false;
                        waitKey = true;
                    }
                    if (waitKey && !key)
                    {
                        AskKeyframe();
                        continue;
                    }
                    waitKey = false;
                    if (decoder.Decode(f[5..], ts * 1000L / 9, ref _decoding)) Publish();
                    errors = 0;
                }
                catch (Exception e)
                {
                    waitKey = true;
                    AskKeyframe();
                    if (++errors == 3)
                    {
                        // A decoder that keeps failing starts over.
                        Logger.Instance.Warning($"Screen sharing restarts the decoder: {e.Message}");
                        decoder.Dispose();
                        decoder = H264Decoder.Create();
                        errors = 0;
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
        }
        finally { decoder?.Dispose(); }
    }

    private void AskKeyframe()
    {
        long now = Environment.TickCount64;
        if (now - _lastAsk < 300) return;
        _lastAsk = now;
        _askKeyframe();
    }

    private void Publish()
    {
        bool signal = false;
        lock (_gate)
        {
            (_decoding, _ready) = (_ready, _decoding);
            _hasReady = true;
            if (!_signaled) signal = _signaled = true;
        }
        if (signal) FrameReady?.Invoke();
    }

    /// <summary>
    /// The newest picture, or null when there is none since the last call. Call from the window;
    /// the picture stays valid until the next call.
    /// </summary>
    public Nv12Frame? Take()
    {
        lock (_gate)
        {
            _signaled = false;
            if (!_hasReady) return null;
            (_ready, _shown) = (_shown, _ready);
            _hasReady = false;
            Interlocked.Increment(ref FramesShown);
            return _shown;
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(2000);
        while (_queue.TryTake(out var item)) ArrayPool<byte>.Shared.Return(item.Buffer);
        lock (_gate)
        {
            _decoding?.Dispose();
            _ready?.Dispose();
            _shown?.Dispose();
            _decoding = _ready = _shown = null;
        }
        _queue.Dispose();
    }
}
