using System.Buffers;
using System.Buffers.Binary;
using System.Threading;
using PdaNetPortable.Services;
using PdaNetPortable.Share.Media;

namespace PdaNetPortable.Share;

/// <summary>
/// The other PC's screen, from the link to a picture, on its own thread. Frames wait in a queue
/// that holds a short run of frames a resent packet lets go at once (each one is needed to decode
/// the next); when this PC falls behind, it starts again from a keyframe instead of piling up delay. After
/// a lost frame the picture holds still until the next keyframe, it never shows a damaged picture.
/// The window takes the newest picture when it is ready to draw, from three buffers that rotate,
/// so decoding never waits for drawing.
/// </summary>
internal sealed class ViewerPipeline : IDisposable
{
    private readonly ViewerFrameQueue _queue = new();
    private readonly Thread _thread;
    private readonly Action _askKeyframe;
    private readonly object _gate = new();
    private Nv12Frame? _decoding, _ready, _shown;
    private bool _hasReady, _signaled;
    private long _lastAsk;
    private int _disposeRequested;

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
        if (frame.Length >= 6 && Volatile.Read(ref _disposeRequested) == 0
            && !_queue.Push(frame, Environment.TickCount64)) AskKeyframe();
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
            while (_queue.Take(out var item))
            {
                try
                {
                    // A busy decoder must not spend another frame on an old picture. Discarding
                    // any predicted frame breaks its successors, so ask for a fresh keyframe.
                    if (item.IsStale(Environment.TickCount64))
                    {
                        waitKey = true;
                        AskKeyframe();
                        continue;
                    }
                    var f = item.Buffer.AsSpan(0, item.Length);
                    byte flags = f[0];
                    uint ts = BinaryPrimitives.ReadUInt32LittleEndian(f[1..]);
                    bool key = (flags & 1) != 0;
                    if ((flags & 2) != 0 || item.Discontinuity)
                    {
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
                finally { item.Dispose(); }
            }
        }
        finally
        {
            decoder?.Dispose();
            if (Volatile.Read(ref _disposeRequested) != 0) FreePictures();
        }
    }

    private void AskKeyframe()
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastAsk);
        if (now - previous < 300 || Interlocked.CompareExchange(ref _lastAsk, now, previous) != previous) return;
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
        Volatile.Write(ref _disposeRequested, 1);
        _queue.Complete();
        // Never free native picture memory while a slow decoder is still writing to it.
        if (_thread.Join(2000)) FreePictures();
    }

    private void FreePictures()
    {
        lock (_gate)
        {
            _decoding?.Dispose();
            _ready?.Dispose();
            _shown?.Dispose();
            _decoding = _ready = _shown = null;
        }
    }
}

/// <summary>
/// Keeps the decoder close to the live edge, with memory bounded independently of frame size.
/// A discarded frame marks the first retained frame, rather than an unrelated frame the decoder
/// happens to be reading at the time. All queue operations and resets share the same lock.
/// </summary>
internal sealed class ViewerFrameQueue : IDisposable
{
    internal const int MaxFrames = 8, MaxBytes = 16 << 20, MaxAgeMs = 100;
    private readonly object _gate = new();
    private readonly Queue<QueuedVideoFrame> _frames = new();
    private int _bytes;
    private bool _complete, _discontinuity;

    public bool Push(ReadOnlySpan<byte> frame, long now)
    {
        lock (_gate)
        {
            if (_complete) return false;
            if (frame.Length > MaxBytes)
            {
                Clear();
                _discontinuity = true;
                return false;
            }
            bool reset = _frames.Count >= MaxFrames || (long)_bytes + frame.Length > MaxBytes
                || (_frames.TryPeek(out var oldest) && now - oldest.QueuedAt > MaxAgeMs);
            if (reset) TrimFor(frame, now);
            var buffer = ArrayPool<byte>.Shared.Rent(frame.Length);
            frame.CopyTo(buffer);
            _frames.Enqueue(new(buffer, frame.Length, now, false));
            _bytes += frame.Length;
            Monitor.Pulse(_gate);
            return true;
        }
    }

    public bool Take(out QueuedVideoFrame frame)
    {
        lock (_gate)
        {
            while (_frames.Count == 0 && !_complete) Monitor.Wait(_gate);
            return TryTake(Environment.TickCount64, out frame);
        }
    }

    public bool TryTake(long now, out QueuedVideoFrame frame)
    {
        lock (_gate)
        {
            while (_frames.Count > 1 && now - _frames.Peek().QueuedAt > MaxAgeMs)
            {
                var old = _frames.Dequeue();
                _bytes -= old.Length;
                old.Dispose();
                _discontinuity = true;
            }
            if (!_frames.TryDequeue(out frame)) return false;
            _bytes -= frame.Length;
            if (_discontinuity)
            {
                frame = frame with { Discontinuity = true };
                _discontinuity = false;
            }
            return true;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _complete = true;
            Clear();
            Monitor.PulseAll(_gate);
        }
    }

    private void Clear()
    {
        while (_frames.TryDequeue(out var frame)) frame.Dispose();
        _bytes = 0;
    }

    private void TrimFor(ReadOnlySpan<byte> incoming, long now)
    {
        // A recent keyframe already in the burst can repair the stream immediately. Preserve
        // that keyframe and its dependent frames only if the entire suffix still fits.
        int keepFrom = _frames.Count, suffixBytes = 0, index = 0;
        if (incoming.IsEmpty || (incoming[0] & 1) == 0)
        {
            foreach (var frame in _frames)
            {
                if (frame.Length > 0 && (frame.Buffer[0] & 1) != 0 && !frame.IsStale(now))
                {
                    keepFrom = index;
                    suffixBytes = 0;
                }
                suffixBytes += frame.Length;
                index++;
            }
            if (_frames.Count - keepFrom >= MaxFrames || (long)suffixBytes + incoming.Length > MaxBytes)
                keepFrom = _frames.Count;
        }
        for (int i = 0; i < keepFrom; i++)
        {
            var old = _frames.Dequeue();
            _bytes -= old.Length;
            old.Dispose();
        }
        _discontinuity = true;
    }

    public void Dispose() => Complete();
}

internal readonly record struct QueuedVideoFrame(byte[] Buffer, int Length, long QueuedAt, bool Discontinuity) : IDisposable
{
    public bool IsStale(long now) => now - QueuedAt > ViewerFrameQueue.MaxAgeMs;
    public void Dispose() => ArrayPool<byte>.Shared.Return(Buffer);
}
