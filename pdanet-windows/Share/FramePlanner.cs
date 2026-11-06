namespace PdaNetPortable.Share;

/// <summary>
/// Seal: the same picture again at the coarsest quantizer, a frame of a few hundred bytes that
/// changes nothing. The PC that views hands a frame on only once the next frame starts to
/// arrive (Pion's sample builder, see rtc/samplebuilder_test.go), so the last frame before the
/// screen rests, a typed letter or the sharpest refine, would wait for the next change without it.
/// </summary>
internal enum FrameKind { None, Motion, Refine, Key, Seal }

/// <summary>What goes next: a frame of this kind at this quantizer, expected to take this many bits; or nothing for WaitMs.</summary>
internal readonly record struct FramePlan(FrameKind Kind, int Qp, double Bits, int WaitMs)
{
    public static FramePlan Wait(long ms) => new(FrameKind.None, 0, 0, (int)Math.Clamp(ms, 1, 100));
}

/// <summary>
/// Decides when the next frame of the screen goes and how sharp it is. A screen is not a film:
/// mostly nothing moves, then a window opens or a page scrolls, then it rests again with text
/// that should be perfectly sharp. A fixed bitrate gets both wrong: every frame gets the same
/// few bits, so moving content is mush at a high frame rate, and a resting screen never gets
/// the bits to sharpen.
///
/// So each frame goes at its own quantizer (constant QP), picked for what the link carries
/// right then. Moving content: as many frames as the link takes at a clear quantizer, fewer
/// frames before a blurrier picture, down to the style's lowest frame rate. A resting screen:
/// refine frames bring everything that moved down to a near lossless quantizer, in steps the
/// link takes without delaying the next change by more than a fraction of a second.
///
/// The link has a quality budget that fills at the measured rate, and a separate estimate of the
/// bits still being sent. Saving quality budget while idle must not let frames queue behind one
/// another when motion starts. A frame's cost is learned as bits per macroblock it
/// codes, which halve with every 6 steps of quantizer, as H.264 has it. Each 16x16 block keeps
/// the quantizer it was last coded at: a frame codes the blocks that changed, plus every block
/// that sits well above its own quantizer, since the encoder sees the difference to the blurrier
/// picture there. That prices a refine by what still needs refining, and a blinking cursor does
/// not keep the rest of the screen from getting sharp.
///
/// Encoders without constant QP follow the link's bitrate themselves; then the planner only
/// paces frames and sends two sharpening frames after a change, as before.
/// </summary>
internal sealed class FramePlanner
{
    private const int Mb = 16;
    private const double Share = 0.9;      // of the link for the picture, the rest for resends, the pointer and control
    private const double SaveUp = 0.4;     // seconds of the link a resting screen saves up for the next change
    private const double KeyDebt = 0.5, RefineDebt = 0.3; // seconds of the link a keyframe and a refine frame may take
    private const int Recode = 3;          // a block this many steps above a frame's quantizer is coded again by it
    private const int MaxQp = 44, MaxMotionQp = 46, MinQp = 12;
    private const long FirstRefine = 120, RefineGap = 200; // ms after the last change, and between refine frames
    private const double Overhead = 600;   // bits of a frame's headers and skipped blocks

    /// <summary>
    /// How a quality setting trades frames for sharpness. MotionMax is the most seconds of the link
    /// one moving frame may hold it, which bounds how late the picture can run behind the screen;
    /// a frame that would take longer goes blurrier, past HardQp if it must.
    /// </summary>
    private readonly record struct Style(int MaxFps, int MinFps, int SoftQp, int HardQp, int RestQp, double MotionMax);

    private static Style StyleOf(Quality q) => q switch
    {
        // Text first: few frames while it moves, every pixel crisp once it rests.
        Quality.Sharp => new(20, 5, 26, 34, 17, 0.20),
        // Motion first: many frames, a softer picture while it moves, still sharp at rest.
        Quality.Smooth => new(60, 24, 34, 42, 21, 0.06),
        _ => new(30, 10, 30, 38, 19, 0.10),
    };

    private readonly object _gate = new();
    private readonly int _mbw, _mbh, _scale, _maxFps;
    private readonly byte[] _qp;          // each block's quantizer as last coded, 0 before the first frame
    private readonly bool[] _changed;     // changed since the last frame
    private readonly int[] _hist = new int[52], _histChanged = new int[52]; // blocks at each quantizer, all and changed
    private int _changedCount;
    private readonly bool _constantQp;

    // Learned bits per block at quantizer 26: blocks that changed, blocks a frame sharpens, keyframes.
    private double _kMotion = 120, _kRefine = 150, _kKey = 300;

    private double _credit;
    private double _queuedBits; // serialized work remaining; idle quality credit cannot pay this away
    private long _creditAt = -1;
    private volatile int _linkBps = 1_000_000; // the helper's start, see rtc/peer.go startBitrate
    private long _lastFrame = -1, _lastChange = -1; // the last frame with a picture in it (any but a seal), the last change
    private FrameKind _lastKind;                     // the kind of that frame
    private bool _sealed;                            // a seal frame followed it
    private long _lastKeyAt = -1;                    // when the last keyframe went, see KeyHeld
    private double _lastKeyBits;                     // and how large it was
    private int _keyStreak;                          // keyframes in a row with less than KeyQuiet between them
    private const int SealQp = 51;                   // every block of a seal frame comes out skipped
    private int _refinesSinceChange;
    private readonly Queue<(FrameKind Kind, double Bits, double Motion, double Refine)> _inFlight = new();

    // For the log: what went since the last Describe.
    private int _nMotion, _nRefine, _nKey, _sumMotionQp, _lowestRefine = 99;
    private long _bits;

    public volatile Quality Quality = Quality.Auto;

    /// <param name="width">The frame's width as encoded.</param>
    /// <param name="scale">Screen pixels per frame pixel: changes come in screen pixels.</param>
    /// <param name="maxFps">The most frames a second the encoder was set up for.</param>
    public FramePlanner(int width, int height, int scale, bool constantQp, int maxFps)
    {
        _mbw = (width + Mb - 1) / Mb;
        _mbh = (height + Mb - 1) / Mb;
        _scale = Math.Max(1, scale);
        _constantQp = constantQp;
        _maxFps = Math.Max(1, maxFps);
        _qp = new byte[_mbw * _mbh];
        _changed = new bool[_mbw * _mbh];
        _credit = Rate * SaveUp;
    }

    /// <summary>The bitrate the helper measured for the link.</summary>
    public int LinkBps
    {
        get => _linkBps;
        set => _linkBps = Math.Max(50_000, value);
    }

    private double Rate => _linkBps * Share;

    /// <summary>What changed on the screen, in screen pixels.</summary>
    public void Changed(List<Dirty> rects, long now)
    {
        lock (_gate)
        {
            foreach (var d in rects)
            {
                int x0 = Math.Clamp(d.X / _scale / Mb, 0, _mbw - 1), y0 = Math.Clamp(d.Y / _scale / Mb, 0, _mbh - 1);
                int x1 = Math.Clamp((d.X + d.W - 1) / _scale / Mb, 0, _mbw - 1), y1 = Math.Clamp((d.Y + d.H - 1) / _scale / Mb, 0, _mbh - 1);
                for (int y = y0; y <= y1; y++)
                for (int i = y * _mbw + x0, end = y * _mbw + x1; i <= end; i++)
                {
                    if (_changed[i]) continue;
                    _changed[i] = true;
                    _changedCount++;
                    _histChanged[_qp[i]]++;
                }
            }
            if (rects.Count > 0) _lastChange = now;
        }
    }

    /// <summary>What to do now: a frame and its quantizer, or how long to wait for the next check.</summary>
    public FramePlan Next(long now, bool keyWanted)
    {
        lock (_gate)
        {
            Refill(now);
            var s = StyleOf(Quality);
            int maxFps = Math.Min(s.MaxFps, _maxFps);
            double rate = Rate;
            if (!_constantQp) return NextFixed(now, keyWanted, maxFps, rate);
            int restQp = RestQpFor(s, rate);

            // A keyframe goes at once, the viewer waits for it: as sharp as half a second of the
            // link allows, refine frames do the rest. Only a link more than half a second behind
            // holds it back, so a viewer that keeps losing packets cannot flood the link with them.
            if (keyWanted || _lastFrame < 0)
            {
                if (KeyHeld(now, rate) is { } wait) return wait;
                double behind = -_credit - rate * KeyDebt;
                if (_lastFrame >= 0 && behind > 0) return FramePlan.Wait((long)Math.Ceiling(behind / rate * 1000));
                double allow = Math.Max(_credit, 0) + rate * KeyDebt;
                // On a starved link the keyframe goes as coarse as it must to fit, up to the coarsest
                // quantizer: a keyframe larger than the link takes is what gets dropped, and then
                // asked for again.
                int q = Pick(FrameKind.Key, allow, restQp, MaxQp);
                if (Cost(FrameKind.Key, q) > allow) q = Pick(FrameKind.Key, allow, q, 51);
                return new FramePlan(FrameKind.Key, q, Cost(FrameKind.Key, q), 0);
            }

            if (_changedCount > 0)
            {
                // Moving: at most the style's frame rate, and only once the frames before drained.
                long due = Math.Max(_lastFrame + 1000 / maxFps, now + DebtMs(rate));
                if (now < due) return FramePlan.Wait(due - now);
                // A first change after a rest may spend what saved up, then each frame its share.
                double perFrame = rate / maxFps, most = rate * s.MotionMax;
                double budget = Math.Min(Math.Max(perFrame, _credit), most);
                int q = Pick(FrameKind.Motion, budget, restQp, s.HardQp);
                if (q > s.SoftQp)
                {
                    // Fewer frames before a blurrier picture: at the soft quantizer the frame takes
                    // longer to go, and the debt holds the next one back. Below the style's lowest
                    // frame rate the picture gets blurrier instead, up to the hard quantizer.
                    double fps = rate / Cost(FrameKind.Motion, s.SoftQp);
                    q = fps >= s.MinFps ? s.SoftQp : Pick(FrameKind.Motion, Math.Min(rate / s.MinFps, most), s.SoftQp, s.HardQp);
                }
                // No moving frame holds the link longer than the style allows, even past the hard quantizer.
                if (Cost(FrameKind.Motion, q) > most) q = Pick(FrameKind.Motion, most, q, MaxMotionQp);
                return new FramePlan(FrameKind.Motion, q, Cost(FrameKind.Motion, q), 0);
            }

            // Nothing changed since the last frame: a seal frame at once, so the viewer shows that frame now.
            if (!_sealed) return new FramePlan(FrameKind.Seal, SealQp, Overhead, 0);

            // Resting: what moved gets sharper, a step at a time, each once the one before drained.
            long rest = Math.Max(_lastChange + FirstRefine, _lastKind == FrameKind.Refine ? _lastFrame + RefineGap : 0);
            rest = Math.Max(rest, now + DebtMs(rate));
            if (now < rest) return FramePlan.Wait(rest - now);
            int worst = Worst();
            if (worst <= restQp + Recode) return FramePlan.Wait(100); // all sharp
            double allowRefine = Math.Max(_credit, 0) + rate * RefineDebt;
            int top = worst - Recode - 1; // the highest quantizer that still codes the blurriest blocks again
            int qr = Pick(FrameKind.Refine, allowRefine, restQp, top);
            // Not even the smallest step fits yet: the budget fills while the screen rests.
            if (Cost(FrameKind.Refine, qr) > allowRefine && _credit < Rate * SaveUp * 0.95) return FramePlan.Wait(50);
            return new FramePlan(FrameKind.Refine, qr, Cost(FrameKind.Refine, qr), 0);
        }
    }

    /// <summary>
    /// For encoders that keep the bitrate themselves: frames at most at the style's rate and once
    /// the frames before drained, then two sharpening frames after a change, each followed by a
    /// seal frame.
    /// </summary>
    private FramePlan NextFixed(long now, bool keyWanted, int maxFps, double rate)
    {
        double perFrame = rate / maxFps;
        if (keyWanted || _lastFrame < 0) return KeyHeld(now, rate) ?? new FramePlan(FrameKind.Key, 0, perFrame, 0);
        if (_changedCount > 0)
        {
            long due = Math.Max(_lastFrame + 1000 / maxFps, now + DebtMs(rate));
            return now < due ? FramePlan.Wait(due - now) : new FramePlan(FrameKind.Motion, 0, perFrame, 0);
        }
        if (!_sealed) return new FramePlan(FrameKind.Seal, SealQp, Overhead, 0);
        if (_refinesSinceChange >= 2 || _lastChange < 0) return FramePlan.Wait(100);
        long rest = Math.Max(_lastChange + (_refinesSinceChange == 0 ? 160 : 700), now + DebtMs(rate));
        return now < rest ? FramePlan.Wait(rest - now) : new FramePlan(FrameKind.Refine, 0, perFrame, 0);
    }

    /// <summary>
    /// A keyframe the viewer asks for goes at once, but one asked for again soon after waits: half
    /// a second, then 1, 2, at most 4 s, and never less than twice what the last one took the link.
    /// Five quiet seconds start over. A viewer on a lossy link asks after every frame it lost, and
    /// each keyframe costs many frames' worth: on a relay that dropped what passed 512 kbit/s,
    /// keyframes 2 to 8 times in 10 s kept the link full of drops and the picture at QP 44
    /// (2026-11-06). Null when a keyframe may go now.
    /// </summary>
    private FramePlan? KeyHeld(long now, double rate)
    {
        if (_lastKeyAt < 0 || _lastFrame < 0 || _keyStreak == 0 || now - _lastKeyAt >= KeyQuiet) return null;
        long gap = Math.Max(Math.Min(4000, 250L << Math.Min(_keyStreak, 4)), (long)(2 * _lastKeyBits / rate * 1000));
        return now - _lastKeyAt < gap ? FramePlan.Wait(_lastKeyAt + gap - now) : null;
    }

    private const long KeyQuiet = 5000; // ms without a keyframe that start the streak over

    /// <summary>The frame goes to the encoder now: the budget pays what it is expected to take, and its blocks sit at its quantizer.</summary>
    public void Sent(FramePlan plan, long now)
    {
        lock (_gate)
        {
            Refill(now);
            double f = Factor(plan.Qp);
            double motion = plan.Kind == FrameKind.Key ? 0 : _changedCount * _kMotion * f;
            double refine = plan.Kind == FrameKind.Key || !_constantQp ? 0 : RefineBits(plan.Qp) * _kRefine;
            _credit -= plan.Bits;
            _queuedBits += plan.Bits;
            _inFlight.Enqueue((plan.Kind, plan.Bits, motion, refine));
            while (_inFlight.Count > 6) _inFlight.Dequeue(); // an encoder that dropped a frame
            // A seal frame changes no block, it only lets the viewer show the frame before it.
            _sealed = plan.Kind == FrameKind.Seal;
            if (_sealed) return;
            _lastFrame = now;
            _lastKind = plan.Kind;
            if (plan.Kind == FrameKind.Key)
            {
                // The keyframe a pipeline opens with is not asked for: only those after it count.
                _keyStreak = _lastKeyAt < 0 || now - _lastKeyAt >= KeyQuiet ? 0 : _keyStreak + 1;
                (_lastKeyAt, _lastKeyBits) = (now, plan.Bits);
            }
            if (plan.Kind == FrameKind.Refine) _refinesSinceChange++;
            else _refinesSinceChange = 0;

            byte q = (byte)Math.Clamp(plan.Qp, 1, 51);
            if (!_constantQp) q = 30; // unknown, only so that blocks count as coded
            Array.Clear(_hist);
            for (int i = 0; i < _qp.Length; i++)
            {
                if (plan.Kind == FrameKind.Key || _changed[i] || _qp[i] > q + Recode || _qp[i] == 0) _qp[i] = q;
                _changed[i] = false;
                _hist[_qp[i]]++;
            }
            Array.Clear(_histChanged);
            _changedCount = 0;

            switch (plan.Kind)
            {
                case FrameKind.Motion: _nMotion++; _sumMotionQp += plan.Qp; break;
                case FrameKind.Refine: _nRefine++; _lowestRefine = Math.Min(_lowestRefine, plan.Qp); break;
                case FrameKind.Key: _nKey++; break;
            }
        }
    }

    /// <summary>
    /// A frame came out of the encoder, <paramref name="bits"/> long: the budget is put right, and
    /// the costs learn from it. Frames come out in the order they went in.
    /// </summary>
    public void Encoded(int bits)
    {
        lock (_gate)
        {
            _bits += bits;
            if (!_inFlight.TryDequeue(out var f)) return;
            _credit += f.Bits - bits;
            _queuedBits = Math.Max(0, _queuedBits + bits - f.Bits);
            if (f.Kind == FrameKind.Key) _lastKeyBits = bits;
            if (!_constantQp) return;
            // Learn in steps, in proportion to each part's share of what was expected.
            double ratio = Math.Clamp((bits - Overhead) / Math.Max(1, f.Bits - Overhead), 0.2, 5);
            const double Rate = 0.4;
            if (f.Kind == FrameKind.Key) _kKey = Math.Clamp(_kKey * Math.Pow(ratio, Rate), 10, 5000);
            else if (f.Motion + f.Refine > 0)
            {
                double m = f.Motion / (f.Motion + f.Refine);
                _kMotion = Math.Clamp(_kMotion * Math.Pow(ratio, Rate * m), 5, 5000);
                _kRefine = Math.Clamp(_kRefine * Math.Pow(ratio, Rate * (1 - m)), 5, 5000);
            }
        }
    }

    /// <summary>What went since the last call, for the log.</summary>
    public string Describe(double seconds)
    {
        lock (_gate)
        {
            int frames = _nMotion + _nRefine + _nKey;
            var parts = new List<string>();
            if (_nMotion > 0) parts.Add(_constantQp ? $"{_nMotion} moving at QP {_sumMotionQp / _nMotion}" : $"{_nMotion} moving");
            if (_nRefine > 0) parts.Add(_constantQp ? $"{_nRefine} sharpening down to QP {_lowestRefine}" : $"{_nRefine} sharpening");
            if (_nKey > 0) parts.Add($"{_nKey} keyframe{(_nKey > 1 ? "s" : "")}");
            string text = $"{frames / Math.Max(0.1, seconds):0.#} fps ({(parts.Count > 0 ? string.Join(", ", parts) : "screen at rest")}), "
                          + $"{_bits / Math.Max(0.1, seconds) / 1000:0} kbit/s of {_linkBps / 1000} for the link";
            if (_constantQp) text += $", sharpest block QP {Sharpest()}, blurriest QP {Worst()}";
            _nMotion = _nRefine = _nKey = _sumMotionQp = 0;
            _lowestRefine = 99;
            _bits = 0;
            return text;
        }
    }

    /// <summary>
    /// The quantizer a resting screen is refined to: the style's, and finer on a fast link, one
    /// step for each 32 % more than 2 Mbit/s, at most 5 (from about 8 Mbit/s on). A fast link
    /// carries a finer picture in a fraction of a second, a slow one would take seconds for it.
    /// </summary>
    private static int RestQpFor(Style s, double rate) =>
        s.RestQp - (int)Math.Clamp(Math.Round(2.5 * Math.Log2(Math.Max(rate, 1) / 2_000_000)), 0, 5);

    // ---- cost model ----

    private static double Factor(int qp) => Math.Pow(2, (26 - qp) / 6.0);

    /// <summary>The bits a frame of this kind is expected to take at quantizer <paramref name="q"/>.</summary>
    private double Cost(FrameKind kind, int q)
    {
        if (kind == FrameKind.Key) return Overhead + _mbw * _mbh * _kKey * Factor(q);
        return Overhead + _changedCount * _kMotion * Factor(q) + RefineBits(q) * _kRefine;
    }

    /// <summary>
    /// The blocks that did not change but sit well above <paramref name="q"/>, each weighted by
    /// how much sharper it gets: the step from its quantizer down to q, in bits at the cost of 1.
    /// </summary>
    private double RefineBits(int q)
    {
        double sum = 0, f = Factor(q);
        for (int v = q + Recode + 1; v < _hist.Length; v++)
        {
            int n = _hist[v] - _histChanged[v];
            if (n > 0) sum += n * Math.Max(0, f - 0.5 * Factor(v));
        }
        return sum;
    }

    /// <summary>The lowest quantizer from lo to hi whose frame fits <paramref name="budget"/>, else hi.</summary>
    private int Pick(FrameKind kind, double budget, int lo, int hi)
    {
        lo = Math.Clamp(lo, MinQp, 51);
        hi = Math.Clamp(hi, lo, 51);
        for (int q = lo; q < hi; q++)
            if (Cost(kind, q) <= budget) return q;
        return hi;
    }

    /// <summary>The highest quantizer any block sits at.</summary>
    private int Worst()
    {
        for (int v = _hist.Length - 1; v > 0; v--)
            if (_hist[v] > 0) return v;
        return 0;
    }

    private int Sharpest()
    {
        for (int v = 1; v < _hist.Length; v++)
            if (_hist[v] > 0) return v;
        return 0;
    }

    // ---- budget ----

    private void Refill(long now)
    {
        if (_creditAt >= 0 && now > _creditAt)
        {
            double sent = Rate * (now - _creditAt) / 1000.0;
            _credit = Math.Min(_credit + sent, Rate * SaveUp);
            _queuedBits = Math.Max(0, _queuedBits - sent);
        }
        _creditAt = Math.Max(_creditAt, now);
    }

    /// <summary>How long until previous frames drain and the quality budget is out of debt.</summary>
    private long DebtMs(double rate) => (long)Math.Ceiling(Math.Max(_queuedBits, -_credit) / rate * 1000);
}
