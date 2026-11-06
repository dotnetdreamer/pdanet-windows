namespace PdaNetPortable.Share;

/// <summary>Deterministic checks for the viewer's latency budget and prediction-chain resets.</summary>
internal static class ViewerQueueSelfTest
{
    public static string Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Viewer queue: " + name);
            checks++;
        }
        static byte[] Frame(byte id, bool key = false) => [(byte)(key ? 1 : 0), 0, 0, 0, 0, id];
        void Take(ViewerFrameQueue queue, long now, byte id, bool gap, bool stale = false)
        {
            Check(queue.TryTake(now, out var frame), $"frame {id} exists");
            try
            {
                Check(frame.Buffer[5] == id, $"frame {id} order");
                Check(frame.Discontinuity == gap, $"frame {id} discontinuity");
                Check(frame.IsStale(now) == stale, $"frame {id} age");
            }
            finally { frame.Dispose(); }
        }

        using (var queue = new ViewerFrameQueue())
        {
            var source = Frame(1, key: true);
            Check(queue.Push(source, 0), "accept keyframe");
            source[5] = 99;
            queue.Push(Frame(2), 1);
            Take(queue, 2, 1, gap: false);
            Take(queue, 2, 2, gap: false);
            Check(!queue.TryTake(2, out _), "FIFO empty after take");
        }

        using (var queue = new ViewerFrameQueue())
        {
            queue.Push(Frame(0, key: true), 0);
            Check(queue.TryTake(0, out var alreadyDecoding), "take old keyframe before overflow");
            try
            {
                for (byte i = 1; i <= ViewerFrameQueue.MaxFrames + 1; i++) queue.Push(Frame(i), i);
                Check(!alreadyDecoding.Discontinuity, "loss does not mark an older in-flight keyframe");
                Take(queue, 10, (byte)(ViewerFrameQueue.MaxFrames + 1), gap: true);
                Check(!queue.TryTake(10, out _), "count overflow discards old predicted frames");
            }
            finally { alreadyDecoding.Dispose(); }
        }

        using (var queue = new ViewerFrameQueue())
        {
            for (byte i = 0; i < ViewerFrameQueue.MaxFrames; i++) queue.Push(Frame(i, key: i == 4), i);
            queue.Push(Frame(8), 8);
            Take(queue, 8, 4, gap: true);
            for (byte i = 5; i <= 8; i++) Take(queue, 8, i, gap: false);
            Check(!queue.TryTake(8, out _), "recent keyframe suffix retained once");
        }

        using (var queue = new ViewerFrameQueue())
        {
            for (byte i = 0; i < ViewerFrameQueue.MaxFrames; i++) queue.Push(Frame(i), i);
            queue.Push(Frame(8, key: true), 8);
            Take(queue, 8, 8, gap: true);
            Check(!queue.TryTake(8, out _), "incoming recovery keyframe replaces backlog");
        }

        using (var queue = new ViewerFrameQueue())
        {
            queue.Push(Frame(1), 0);
            queue.Push(Frame(2), 50);
            queue.Push(Frame(3), 100);
            Take(queue, 101, 2, gap: true);
            Take(queue, 101, 3, gap: false);
            queue.Push(Frame(4), 200);
            queue.Push(Frame(5, key: true), 301);
            Take(queue, 301, 5, gap: true);
            Check(!queue.TryTake(301, out _), "enqueue age limit discards old backlog");
        }

        using (var queue = new ViewerFrameQueue())
        {
            queue.Push(Frame(1, key: true), 0);
            Take(queue, ViewerFrameQueue.MaxAgeMs, 1, gap: false);
            queue.Push(Frame(2, key: true), 0);
            Take(queue, ViewerFrameQueue.MaxAgeMs + 1, 2, gap: false, stale: true);
            Check(!queue.TryTake(102, out _), "sole stale frame is exposed for rejection without waiting for another frame");
        }

        using (var queue = new ViewerFrameQueue())
        {
            var large = new byte[ViewerFrameQueue.MaxBytes / 2 + 1];
            large[5] = 1;
            queue.Push(large, 0);
            large[5] = 2;
            queue.Push(large, 1);
            Take(queue, 1, 2, gap: true);
            Check(!queue.TryTake(1, out _), "byte limit discards old backlog");
            Check(!queue.Push(new byte[ViewerFrameQueue.MaxBytes + 1], 2), "oversized frame rejected");
            queue.Push(Frame(3, key: true), 3);
            Take(queue, 3, 3, gap: true);
        }

        using (var queue = new ViewerFrameQueue())
        {
            queue.Push(Frame(1), 0);
            queue.Complete();
            Check(!queue.Push(Frame(2), 1), "complete rejects producer");
            Check(!queue.Take(out _), "complete releases reader and clears backlog");
            queue.Complete();
        }
        using (var queue = new ViewerFrameQueue())
        using (var started = new System.Threading.ManualResetEventSlim())
        {
            var reader = Task.Run(() =>
            {
                started.Set();
                return queue.Take(out _);
            });
            Check(started.Wait(2000), "waiting reader starts");
            queue.Complete();
            Check(reader.Wait(2000) && !reader.Result, "complete wakes concurrent reader");
        }
        return $"{checks} checks; 8 frames, 100 ms, 16 MiB; gaps resume at a keyframe";
    }
}
