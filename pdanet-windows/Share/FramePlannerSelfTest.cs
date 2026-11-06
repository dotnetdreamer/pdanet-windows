namespace PdaNetPortable.Share;

/// <summary>Deterministic link-serialization checks without a GPU or a network.</summary>
internal static class FramePlannerSelfTest
{
    private const int Width = 1280, Height = 720, Bps = 1_000_000;
    private const long MotionAt = 10_000;
    private static readonly List<Dirty> Whole = new() { new(0, 0, Width, Height) };

    public static string Run()
    {
        foreach (var quality in new[] { Quality.Auto, Quality.Smooth, Quality.Sharp })
        {
            SavedCreditDoesNotQueueFrames(quality);
            StaticSharpness(quality);
        }
        foreach (bool constantQp in new[] { true, false })
        {
            ActualFrameSize(constantQp, 270_000, mustWait: true);
            ActualFrameSize(constantQp, 9_000, mustWait: false);
            RefinementWaitsForMotion(constantQp);
        }
        SlowerLink();
        KeyStorm();
        return "idle credit cannot build a motion queue; actual sizes and slower links delay sends, static sharpness and keyframe backoff survive";
    }

    private static FramePlanner Rested(bool constantQp = true, Quality quality = Quality.Auto)
    {
        var p = new FramePlanner(Width, Height, 1, constantQp, 60) { LinkBps = Bps, Quality = quality };
        p.Changed(Whole, 0);
        Send(p, p.Next(0, true), 0);
        Send(p, p.Next(1, false), 1);
        return p;
    }

    private static void SavedCreditDoesNotQueueFrames(Quality quality)
    {
        var p = Rested(quality: quality);
        p.Changed(Whole, MotionAt);
        var first = p.Next(MotionAt, false);
        Require(first.Kind == FrameKind.Motion, "the first change after a rest was delayed");
        int bits = Send(p, first, MotionAt);
        int interval = quality == Quality.Sharp ? 50 : quality == Quality.Smooth ? 16 : 33;
        p.Changed(Whole, MotionAt + interval + 1);
        Require(bits * 1000.0 / (Bps * 0.9) > interval + 1,
            "the simulated motion frame is too small to exercise the queue");
        Require(p.Next(MotionAt + interval + 1, false).Kind == FrameKind.None,
            $"{quality}: saved idle credit allowed a frame before the previous one drained");
        long drained = MotionAt + (long)Math.Ceiling(bits * 1000.0 / (Bps * 0.9));
        Require(p.Next(drained, false).Kind == FrameKind.Motion,
            $"{quality}: motion did not resume once the previous frame drained");
    }

    private static void ActualFrameSize(bool constantQp, int bits, bool mustWait)
    {
        var p = Rested(constantQp);
        p.Changed(Whole, MotionAt);
        var frame = p.Next(MotionAt, false);
        p.Sent(frame, MotionAt);
        p.Encoded(bits);
        p.Changed(Whole, MotionAt + 34);
        bool waiting = p.Next(MotionAt + 34, false).Kind == FrameKind.None;
        Require(waiting == mustWait,
            $"{(constantQp ? "constant QP" : "fixed bitrate")}: actual frame size {bits} did not correct serialization work");
        long drained = MotionAt + Math.Max(34, (long)Math.Ceiling(bits * 1000.0 / (Bps * 0.9)));
        Require(p.Next(drained, false).Kind == FrameKind.Motion,
            "motion remained blocked after the actual encoded frame drained");
    }

    private static void RefinementWaitsForMotion(bool constantQp)
    {
        var p = Rested(constantQp);
        p.Changed(Whole, MotionAt);
        var frame = p.Next(MotionAt, false);
        p.Sent(frame, MotionAt);
        p.Encoded(270_000); // 300 ms, still within the saved quality budget.
        var seal = p.Next(MotionAt + 1, false);
        Require(seal.Kind == FrameKind.Seal, "a small compatibility seal was held behind its picture");
        Send(p, seal, MotionAt + 1);
        Require(p.Next(MotionAt + 180, false).Kind == FrameKind.None,
            $"{(constantQp ? "constant QP" : "fixed bitrate")}: refinement queued behind a moving frame");
        Require(p.Next(MotionAt + 350, false).Kind == FrameKind.Refine,
            "a resting screen did not resume sharpening after the link drained");
    }

    private static void SlowerLink()
    {
        var p = Rested();
        p.Changed(Whole, MotionAt);
        int bits = Send(p, p.Next(MotionAt, false), MotionAt);
        p.LinkBps = Bps / 2;
        p.Changed(Whole, MotionAt + 1);
        long formerlyDrained = MotionAt + (long)Math.Ceiling(bits * 1000.0 / (Bps * 0.9));
        Require(p.Next(formerlyDrained, false).Kind == FrameKind.None,
            "a bitrate decrease did not extend the time needed to drain outstanding work");
        long drained = MotionAt + (long)Math.Ceiling(bits * 1000.0 / (Bps * 0.45));
        Require(p.Next(drained, false).Kind == FrameKind.Motion,
            "motion did not resume at the lower link rate");
    }

    private static void StaticSharpness(Quality quality)
    {
        var p = Rested(quality: quality);
        int sharpest = 51;
        for (long now = 2; now <= 8_000;)
        {
            var plan = p.Next(now, false);
            if (plan.Kind == FrameKind.None) { now += plan.WaitMs; continue; }
            if (plan.Kind == FrameKind.Refine) sharpest = Math.Min(sharpest, plan.Qp);
            Send(p, plan, now);
            now++;
        }
        int target = quality == Quality.Sharp ? 17 : quality == Quality.Smooth ? 21 : 19;
        Require(sharpest <= target + 3, $"{quality}: serialization pacing prevented static text from getting sharp");
    }

    private static void KeyStorm()
    {
        var p = new FramePlanner(16, 16, 1, true, 60) { LinkBps = Bps };
        Send(p, p.Next(0, true), 0);
        var recovery = p.Next(100, true);
        Require(recovery.Kind == FrameKind.Key, "a recovery keyframe was unexpectedly suppressed");
        Send(p, recovery, 100);
        Require(p.Next(101, true).Kind == FrameKind.None, "repeated recovery requests bypassed keyframe backoff");
    }

    private static int Send(FramePlanner p, FramePlan plan, long now)
    {
        Require(plan.Kind != FrameKind.None, "the simulation attempted to send a wait plan");
        int bits = (int)Math.Ceiling(plan.Bits);
        p.Sent(plan, now);
        p.Encoded(bits);
        return bits;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
