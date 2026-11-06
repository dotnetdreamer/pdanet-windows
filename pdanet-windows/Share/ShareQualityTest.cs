using System.Collections.Concurrent;
using PdaNetPortable.Share.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PdaNetPortable.Share;

/// <summary>
/// "--selftest-share quality [mf]": how sharp the other PC sees this screen, with the frame
/// planner and constant QP against the fixed bitrate that came before, on this PC's GPU encoder
/// (Direct3D 12's, or Media Foundation's with "mf") over a simulated link. The screen as it is now
/// rests for 2 s, scrolls for 1 s, then rests again. Every frame is encoded for real, goes over a
/// link of the given rate one after the other, is decoded, and is compared with the screen it
/// showed: PSNR of the brightness, higher is sharper, 40 dB and up looks like the screen itself.
/// Nothing leaves this PC.
/// </summary>
internal static unsafe class ShareQualityTest
{
    private const int RestMs = 2000, ScrollMs = 1000, AfterMs = 2500;

    /// <summary>How the screen moves for a second: a smooth scroll, or a page down every 100 ms, new text each time.</summary>
    private readonly record struct Motion(string Name, int EveryMs, int StepEighths);

    private static readonly Motion[] Motions = { new("scrolling smoothly", 16, 0), new("a page down every 100 ms", 100, 3) };
    private static Motion _motion = Motions[0];
    private static int Step(int h) => _motion.StepEighths == 0 ? 12 : h * _motion.StepEighths / 8;
    private static readonly int[] AfterChecks = { 250, 500, 1000, 2000 };

    private sealed record Shot(long At, FrameKind Kind, int Qp, int Bits, double Shown, byte[] Au, int Offset);

    /// <summary>
    /// "--selftest-share quality vbr": Direct3D 12's encoder at a variable bitrate, as the GPU path ran
    /// before constant QP and still runs on GPUs without it: the size of each frame, with the bitrate
    /// set when it opens, changed before the first frame, and changed on the way.
    /// </summary>
    public static int Vbr()
    {
        var screen = ShareScreen.All()[0];
        using var source = new DuplicationSource(screen, gpu: true, picture: true);
        var d = new List<Dirty>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (source.Next(100, d) != CaptureResult.Frame && sw.ElapsedMilliseconds < 3000) { }
        int w = source.Width & ~1, h = source.Height & ~1;
        foreach (var (label, change) in new[] { ("opened at 2.2 Mbit/s", 0), ("opened at 2.5, set to 2.2 before the first frame", 1), ("opened at 2.2, set to 5 at frame 5", 5) })
        {
            var sizes = new List<string>();
            string mode = "";
            try
            {
                using var e = new D3D12H264Encoder(source.Device, source.Context, w, h, 60, change == 1 ? 2_500_000 : 2_200_000, constantQp: false);
                using var g = new GpuConverter(source.Device, source.Context, source.Picture, source.Width, source.Height, w, h, e.Input);
                mode = e.RateMode;
                e.Output = (au, _, key) => sizes.Add($"{au.Length}{(key ? "K" : "")}");
                for (int i = 0; i < 10; i++)
                {
                    if (change == 1 && i == 0) e.SetBitrate(2_200_000);
                    if (change == 5 && i == 5) e.SetBitrate(5_000_000);
                    e.Encode(g.Convert(), i * 166_666L, 166_666);
                }
            }
            catch (InvalidOperationException x) { sizes.Add($"stopped: {x.Message}"); }
            Console.WriteLine($"{label,-50} {mode}: bytes per frame {string.Join(" ", sizes)}");
        }

        // Constant QP: what a frame takes when the quantizer stays, and when it changes every frame,
        // which on a driver without rate control changes means a new encoder object each time.
        foreach (var (label, step) in new[] { ("constant QP, the same quantizer", 0), ("constant QP, a new quantizer each frame", 1) })
        {
            using var e = new D3D12H264Encoder(source.Device, source.Context, w, h, 60, 2_500_000);
            using var g = new GpuConverter(source.Device, source.Context, source.Picture, source.Width, source.Height, w, h, e.Input);
            var sizes = new List<int>();
            e.Output = (au, _, _) => sizes.Add(au.Length);
            var time = new List<double>();
            for (int i = 0; i < 30; i++)
            {
                e.Qp = step == 0 ? 26 : 22 + i % 2 * 8;
                var t = System.Diagnostics.Stopwatch.StartNew();
                e.Encode(g.Convert(), i * 166_666L, 166_666);
                time.Add(t.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"{label,-50} {e.RateMode}: {time.Skip(1).Average():0.0} ms a frame, at most {time.Skip(1).Max():0.0} ms, "
                              + $"{sizes.Count} of 30 out, keyframe {sizes.FirstOrDefault() >> 10} KB");
        }
        return 0;
    }

    public static int Run(bool mf)
    {
        var screen = ShareScreen.All()[0];
        byte[] image;
        int w, h;
        using (var gdi = new GdiSource(screen))
        {
            var d = new List<Dirty>();
            if (gdi.Next(0, d) != CaptureResult.Frame)
            {
                Console.WriteLine("FAIL no picture of the screen");
                return 1;
            }
            (w, h) = (gdi.Width & ~1, gdi.Height & ~1);
            image = new byte[w * h * 4];
            byte* p = gdi.Lock(out int pitch);
            fixed (byte* o = image)
                for (int y = 0; y < h; y++) Buffer.MemoryCopy(p + (long)y * pitch, o + (long)y * w * 4, w * 4, w * 4);
            gdi.Unlock();
        }
        Console.WriteLine($"==== Screen sharing quality, {w}x{h}, the screen as it is now: 2 s still, 1 s moving, 2.5 s still ====");

        using var dup = new DuplicationSource(screen, gpu: true, picture: false);
        var device = dup.Device;
        var context = dup.Context;
        using var texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        var frame = new byte[w * h * 4];
        void Show(int offset)
        {
            // The screen scrolled up by offset pixels.
            fixed (byte* src = image, dst = frame)
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy(src + (long)((y + offset) % h) * w * 4, dst + (long)y * w * 4, w * 4, w * 4);
        }

        (IVideoEncoder, GpuConverter) Open(bool constantQp, int bps)
        {
            // Before, the encoder opened at the bitrate it then kept: what the link takes, less the packet overhead.
            if (!constantQp) bps = (int)(bps * 0.88);
            if (mf)
            {
                var gpu = new GpuConverter(device, context, texture, w, h, w, h);
                return (H264Encoder.Create(w, h, 60, bps, device: device, constantQp: constantQp), gpu);
            }
            var d3d12 = new D3D12H264Encoder(device, context, w, h, 60, bps, constantQp);
            return (d3d12, new GpuConverter(device, context, texture, w, h, w, h, d3d12.Input));
        }

        foreach (var motion in Motions)
        {
        _motion = motion;
        Console.WriteLine();
        Console.WriteLine($"---- moving: {motion.Name} ----");
        Console.WriteLine($"{"",-26} {"still",6} | {"moving",-24} | {"after it stops, sharpness at",-34} | {"sent",6}");
        Console.WriteLine($"{"link and way",-26} {"2 s",6} | {"fps",4} {"PSNR",5} {"delay",7} {"QP",5} | {"+0.25 s",8} {"+0.5 s",7} {"+1 s",7} {"+2 s",7} | {"kbit",6}");
        foreach (int kbps in new[] { 1000, 2500, 6000 })
        {
            foreach (var (planner, quality, label) in new[]
                     {
                         (false, Quality.Auto, "before, Balanced"),
                         (false, Quality.Smooth, "before, Smooth"),
                         (true, Quality.Auto, "now, Balanced"),
                         (true, Quality.Smooth, "now, Smooth"),
                     })
            {
                try
                {
                    var (encoder, gpu) = Open(planner, kbps * 1000);
                    using var e = encoder;
                    using var g = gpu;
                    if (planner && !encoder.ConstantQp)
                    {
                        Console.WriteLine($"{kbps / 1000.0,4:0.#} Mbit/s, {label,-14} the encoder has no constant QP: {encoder.Name}");
                        continue;
                    }
                    var shots = Simulate(encoder, gpu, context, texture, frame, Show, planner, quality, kbps * 1000, h);
                    Console.WriteLine($"{kbps / 1000.0,4:0.#} Mbit/s, {label,-14} " + Measure(shots, image, w, h));
                }
                catch (Exception x) when (x is NotSupportedException or InvalidOperationException or SharpGen.Runtime.SharpGenException)
                {
                    Console.WriteLine($"{kbps / 1000.0,4:0.#} Mbit/s, {label,-14} not on this PC: {x.Message}");
                }
            }
        }
        }
        Console.WriteLine("PSNR in dB of the brightness, the picture the other PC shows against the screen; delay: until a scrolled frame is shown, after it was taken");
        return 0;
    }

    private static int Offset(long t, int h) =>
        t < RestMs ? 0 : (int)((Math.Min(t - RestMs, ScrollMs) / _motion.EveryMs + 1) * Step(h) % h);

    /// <summary>The next time the screen changes after t, or the end.</summary>
    private static long NextChange(long t)
    {
        if (t < RestMs) return RestMs;
        if (t < RestMs + ScrollMs) return RestMs + ((t - RestMs) / _motion.EveryMs + 1) * _motion.EveryMs;
        return RestMs + ScrollMs + AfterMs;
    }

    /// <summary>
    /// The session in simulated time: the screen changes as Offset says, frames go as the planner
    /// says, or as the pipeline did before (a frame at most every 1/fps after a change, two
    /// sharpening frames 160 and 700 ms after the last one, the encoder's own bitrate), and each
    /// is shown once the link carried it and every frame before it.
    /// </summary>
    private static List<Shot> Simulate(IVideoEncoder encoder, GpuConverter gpu, ID3D11DeviceContext context, ID3D11Texture2D texture,
        byte[] frame, Action<int> show, bool usePlanner, Quality quality, int bps, int h)
    {
        int w = encoder.Width;
        var outputs = new BlockingCollection<byte[]>();
        var planner = usePlanner ? new FramePlanner(w, encoder.Height, 1, encoder.ConstantQp, 60) { Quality = quality, LinkBps = bps } : null;
        encoder.Output = (au, _, _) =>
        {
            var a = au.ToArray();
            planner?.Encoded(a.Length * 8);
            outputs.Add(a);
        };
        int fps = quality switch { Quality.Smooth => 60, Quality.Sharp => 20, _ => 30 };
        var shots = new List<Shot>();
        var whole = new List<Dirty> { new(0, 0, w, encoder.Height) };
        double linkFree = 0;
        long t = 0, end = RestMs + ScrollMs + AfterMs, last = -10_000, lastChange = -1;
        int content = -1, uploaded = -1, refines = 2;
        bool pending = false, key = true;
        while (t < end)
        {
            int off = Offset(t, h);
            if (off != content)
            {
                content = off;
                planner?.Changed(whole, t);
                pending = true;
                lastChange = t;
                refines = 0;
            }
            var plan = default(FramePlan);
            var kind = FrameKind.Motion;
            if (planner != null)
            {
                plan = planner.Next(t, key);
                if (plan.Kind == FrameKind.None)
                {
                    t = Math.Min(t + plan.WaitMs, NextChange(t));
                    continue;
                }
                kind = plan.Kind;
            }
            else
            {
                bool due = pending && t - last >= 1000 / fps;
                bool refine = !pending && refines < 2 && lastChange >= 0 && t - lastChange >= (refines == 0 ? 160 : 700);
                if (!key && !due && !refine)
                {
                    t++;
                    continue;
                }
                if (refine)
                {
                    refines++;
                    kind = FrameKind.Refine;
                }
                if (key) kind = FrameKind.Key;
                pending = false;
            }

            if (off != uploaded)
            {
                show(off);
                fixed (byte* f = frame) context.UpdateSubresource(texture, 0, null, (IntPtr)f, (uint)(w * 4), 0);
                uploaded = off;
            }
            if (key) encoder.RequestKeyframe();
            if (planner != null)
            {
                encoder.Qp = plan.Qp;
                planner.Sent(plan, t);
            }
            encoder.Encode(gpu.Convert(), t * 10_000, 166_666);
            if (!outputs.TryTake(out var au, 3000)) throw new InvalidOperationException($"{encoder.Name} gave no frame");
            int bits = au.Length * 8;
            linkFree = Math.Max(t, linkFree) + bits * 1000.0 / bps;
            shots.Add(new Shot(t, kind, plan.Qp, bits, linkFree, au, off));
            key = false;
            last = t;
            t++;
        }
        return shots;
    }

    /// <summary>Decodes the frames in order and measures what the other PC would have seen.</summary>
    private static string Measure(List<Shot> shots, byte[] image, int w, int h)
    {
        // Which frame shows at each moment that is measured against the screen as it is then.
        int ShownAt(double t)
        {
            int k = -1;
            for (int i = 0; i < shots.Count; i++)
                if (shots[i].Shown <= t) k = i;
            return k;
        }
        int still = ShownAt(RestMs);
        int finalOffset = Offset(RestMs + ScrollMs, h);
        var after = AfterChecks.Select(a => ShownAt(RestMs + ScrollMs + a)).ToArray();

        var bgra = new byte[w * h * 4];
        using var screen = new Nv12Frame(w, h);
        void Screen(int offset)
        {
            fixed (byte* src = image, dst = bgra)
            {
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy(src + (long)((y + offset) % h) * w * 4, dst + (long)y * w * 4, w * 4, w * 4);
                ColorConvert.BgraToNv12(dst, w * 4, 0, 0, w, h, screen, 1);
            }
        }

        using var decoder = H264Decoder.Create();
        Nv12Frame? picture = null;
        double stillPsnr = double.NaN;
        var afterPsnr = new double[after.Length];
        Array.Fill(afterPsnr, double.NaN);
        double motionPsnr = 0, motionDelay = 0, motionQp = 0;
        int motion = 0;
        long bitsTotal = 0;
        try
        {
            for (int i = 0; i < shots.Count; i++)
            {
                var s = shots[i];
                bitsTotal += s.Bits;
                if (!decoder.Decode(s.Au, i * 166_666L, ref picture) || picture == null) continue;
                bool scrolling = s.At >= RestMs && s.At < RestMs + ScrollMs;
                if (i == still || scrolling)
                {
                    Screen(s.Offset);
                    double psnr = Psnr(picture, screen);
                    if (i == still) stillPsnr = psnr;
                    if (scrolling)
                    {
                        motion++;
                        motionPsnr += psnr;
                        motionDelay += s.Shown - s.At;
                        motionQp += s.Qp;
                    }
                }
                for (int k = 0; k < after.Length; k++)
                {
                    if (after[k] != i) continue;
                    Screen(finalOffset);
                    afterPsnr[k] = Psnr(picture, screen);
                }
            }
        }
        finally { picture?.Dispose(); }
        string Db(double v) => double.IsNaN(v) ? "-" : $"{v:0.0}";
        string qp = shots.Any(s => s.Qp > 0) && motion > 0 ? $"{motionQp / motion:0}" : "-";
        return $"{Db(stillPsnr),6} | {motion,4} {Db(motion > 0 ? motionPsnr / motion : double.NaN),5} {(motion > 0 ? motionDelay / motion : 0),5:0}ms {qp,5} | "
               + $"{Db(afterPsnr[0]),8} {Db(afterPsnr[1]),7} {Db(afterPsnr[2]),7} {Db(afterPsnr[3]),7} | {bitsTotal / 1000,6}";
    }

    /// <summary>PSNR of the brightness plane.</summary>
    private static double Psnr(Nv12Frame a, Nv12Frame b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        long sum = 0;
        for (int y = 0; y < h; y++)
        {
            byte* pa = a.Data + (long)y * a.Width, pb = b.Data + (long)y * b.Width;
            for (int x = 0; x < w; x++)
            {
                int d = pa[x] - pb[x];
                sum += d * d;
            }
        }
        double mse = sum / (double)(w * h);
        return mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
