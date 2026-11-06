using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using PdaNetPortable.Share.Media;

namespace PdaNetPortable.Share;

/// <summary>
/// "--selftest-share": checks screen sharing on this PC without a window. First the picture path,
/// capture, convert, encode, decode and back, with its quality, speed and sizes. Then a whole
/// session between two helpers on this PC, with the real pipelines, until the viewer shows the
/// screen. "--selftest-share online" meets through the public relays instead of the local network.
/// Nothing is clicked or typed on this PC.
/// </summary>
internal static class ShareSelfTest
{
    /// <summary>
    /// "--selftest-share memory [d3d12|gpu|cpu|soft|view]": what each part of a pipeline costs in
    /// memory, step by step, in Task Manager's measure, the private working set, with the larger
    /// allocations each step made. d3d12 is the GPU path with Direct3D 12's encoder, gpu the GPU
    /// path with Media Foundation's, cpu and soft the processor path with the GPU's encoder and
    /// with Microsoft's, view the viewer's decoder.
    /// </summary>
    public static unsafe int MemorySteps(string mode)
    {
        var before = Memory();
        var regions = Regions();
        void Step(string what)
        {
            var now = Memory();
            Console.WriteLine($"{what,-44} +{(now.PrivateWs - before.PrivateWs) >> 20,3} MB private working set, +{(now.Private - before.Private) >> 20,3} MB committed");
            var after = Regions();
            var added = after.Where(r => !regions.TryGetValue(r.Key, out var old) || old.Size != r.Value.Size).Select(r => r.Value).ToList();
            foreach (var g in added.Where(r => r.Resident >= 1 << 20).GroupBy(r => (r.Type, Mb: Math.Round(r.Size / 1048576.0, 1))).OrderByDescending(g => g.Key.Mb * g.Count()).Take(4))
                Console.WriteLine($"      {g.Count(),3} x {g.Key.Mb,5:0.0} MB {g.Key.Type}  resident {g.Sum(r => r.Resident) / 1048576.0:0.0} MB");
            regions = after;
        }
        var screen = ShareScreen.All()[0];
        Mf.Start();
        Step("Media Foundation started");
        if (mode == "view")
        {
            // A second of the screen as H.264 first, then only the decoder is measured.
            var aus = new List<byte[]>();
            using (var src = new DuplicationSource(screen))
            using (var frame = new Nv12Frame(src.Width, src.Height))
            using (var enc = H264Encoder.Create(frame.Width, frame.Height, 30, 4_000_000, allowHardware: false))
            {
                enc.Output = (au, _, _) => aus.Add(au.ToArray());
                var d = new List<Dirty>();
                var t = Stopwatch.StartNew();
                while (src.Next(100, d) != CaptureResult.Frame && t.ElapsedMilliseconds < 3000) { }
                for (int i = 0; i < 60; i++)
                {
                    byte* p = src.Lock(out int pitch);
                    ColorConvert.BgraToNv12(p, pitch, 0, 0, frame.Width, frame.Height, frame, 1);
                    src.Unlock();
                    // Something changes in every frame, as when a window moves.
                    NativeMemory.Fill(frame.Data + (i * 8 % frame.Height) * frame.Width, (nuint)(frame.Width * 8), (byte)(i * 4));
                    enc.Encode(frame, i * 333_333L, 333_333);
                }
            }
            before = Memory();
            regions = Regions();
            Step($"{aus.Count} frames encoded, {aus.Sum(a => a.Length) >> 10} KB; the encoder closed");
            var decoder = H264Decoder.Create();
            Step($"decoder ({decoder.Name})");
            Nv12Frame? picture = null;
            int shown = 0;
            for (int i = 0; i < aus.Count; i++)
                if (decoder.Decode(aus[i], i * 333_333L, ref picture)) shown++;
            Step($"after {aus.Count} frames ({shown} shown)");
            picture?.Dispose();
            decoder.Dispose();
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            Step("decoder closed, collected");
            Mf.Stop();
            Step("Media Foundation stopped");
            return 0;
        }
        using var source = new DuplicationSource(screen, gpu: mode is "gpu" or "d3d12", picture: mode != "d3d12");
        Step($"device and Desktop Duplication ({mode})");
        var dirty = new List<Dirty>();
        var sw = Stopwatch.StartNew();
        int frames = 0;
        if (mode == "d3d12")
        {
            using var encoder = new D3D12H264Encoder(source.Device, source.Context, source.Width & ~1, source.Height & ~1, 30, 4_000_000);
            Step($"encoder ({encoder.Name})");
            using var gpu = new GpuConverter(source.Device, source.Context, null, source.Width, source.Height, encoder.Width, encoder.Height, encoder.Input);
            source.FrameArrived = gpu.Convert;
            Step("converter into the encoder's texture");
            while (source.Next(100, dirty) != CaptureResult.Frame && sw.ElapsedMilliseconds < 3000) { }
            Step("first picture, converted");
            var aus = new List<(byte[] Au, bool Key)>();
            encoder.Output = (au, _, key) => aus.Add((au.ToArray(), key));
            var time = Stopwatch.StartNew();
            for (int i = 0; i < 60; i++)
            {
                // A new rate halfway, as the link changes, and a keyframe on it.
                if (i == 30) encoder.SetBitrate(10_000_000);
                if (i == 40) encoder.RequestKeyframe();
                encoder.Encode(gpu.Convert(), i * 333_333L, 333_333);
                if (i == 0) Step("first frame");
                dirty.Clear();
                source.Next(16, dirty);
            }
            Step($"after 60 frames ({aus.Count} out, {aus.Sum(a => a.Au.Length) >> 10} KB, {aus.Count(a => a.Key)} keyframes)");
            string Units(byte[] au) => string.Join(" ", H264Encoder.Nalus(au).Select(n => $"{au[n.Start] & 31}:{n.End - n.Start}"));
            Console.WriteLine("NAL units (type:bytes) of the first frame: " + Units(aus[0].Au) + ", of the second: " + Units(aus[1].Au));
            Console.WriteLine("keyframes at 4 and 10 Mbit/s: " + string.Join(", ", aus.Where(a => a.Key).Select(a => $"{a.Au.Length >> 10} KB")));
            using var decoder = H264Decoder.Create();
            Nv12Frame? picture = null;
            int shown = 0;
            var problems = new List<string>();
            for (int i = 0; i < aus.Count; i++)
            {
                try { if (decoder.Decode(aus[i].Au, i * 333_333L, ref picture)) shown++; }
                catch (Exception e) { problems.Add($"frame {i}: {e.Message}"); }
            }
            Console.WriteLine($"decoded {shown} of {aus.Count}, {picture?.Width}x{picture?.Height}{(problems.Count > 0 ? "; " + string.Join("; ", problems.Take(3)) : "")}");
            picture?.Dispose();
        }
        else if (mode == "gpu")
        {
            while (source.Next(100, dirty) != CaptureResult.Frame && sw.ElapsedMilliseconds < 3000) { }
            Step("first picture");
            using var gpu = new GpuConverter(source.Device, source.Context, source.Picture, source.Width, source.Height, source.Width & ~1, source.Height & ~1);
            Step("converter and 4 NV12 textures");
            using var encoder = H264Encoder.Create(gpu.Width, gpu.Height, 30, 4_000_000, device: source.Device);
            encoder.Output = (_, _, _) => Interlocked.Increment(ref frames);
            Step($"encoder ({encoder.Name})");
            for (int i = 0; i < 60; i++)
            {
                encoder.Encode(gpu.Convert(), i * 333_333L, 333_333);
                Thread.Sleep(16);
            }
            Thread.Sleep(300);
            Step($"after 60 frames ({frames} out)");
        }
        else
        {
            while (source.Next(100, dirty) != CaptureResult.Frame && sw.ElapsedMilliseconds < 3000) { }
            Step("first picture");
            using var frame = new Nv12Frame(source.Width, source.Height);
            Step("NV12 frame");
            using var encoder = H264Encoder.Create(frame.Width, frame.Height, 30, 4_000_000, allowHardware: mode == "cpu");
            encoder.Output = (_, _, _) => Interlocked.Increment(ref frames);
            Step($"encoder ({encoder.Name})");
            for (int i = 0; i < 60; i++)
            {
                byte* p = source.Lock(out int pitch);
                ColorConvert.BgraToNv12(p, pitch, 0, 0, frame.Width, frame.Height, frame, 1);
                source.Unlock();
                encoder.Encode(frame, i * 333_333L, 333_333);
                Thread.Sleep(16);
            }
            Thread.Sleep(300);
            Step($"after 60 frames ({frames} out)");
        }
        Mf.Stop();
        return 0;
    }

    public static int Run(bool online)
    {
        Console.WriteLine("==== PdaNet screen sharing self-test ====");
        int failed = 0;
        void Check(string name, Func<string> test)
        {
            try
            {
                var detail = test();
                Console.WriteLine($"ok   {name}{(detail.Length > 0 ? "\n     " + detail.Replace("\n", "\n     ") : "")}");
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine($"FAIL {name}: {e.Message}");
                // A bug, not a missing part: where it happened.
                if (e is NullReferenceException or IndexOutOfRangeException or InvalidCastException)
                    Console.WriteLine("     " + string.Join("\n     ", (e.StackTrace ?? "").Split('\n').Take(6).Select(l => l.Trim())));
            }
        }
        Check("the picture path: capture, convert, encode, decode", Picture);
        Check("the GPU path: capture, convert and encode on the GPU", Gpu);
        Check("the encoder a session picks here, and its quantizer", EncoderQp);
        Check("a turned screen comes out the right way round", Turned);
        Check(online ? "a whole session through the public relays" : "a whole session on the local network", () => Session(online));
        Console.WriteLine(failed == 0 ? "all checks passed" : $"{failed} check(s) failed");
        return failed == 0 ? 0 : 1;
    }

    private static unsafe string Picture()
    {
        var lines = new List<string>();
        var screen = ShareScreen.All()[0];
        using var source = ScreenSources.Open(screen);
        var dirty = new List<Dirty>();
        var sw = Stopwatch.StartNew();
        var r = CaptureResult.Timeout;
        while (r == CaptureResult.Timeout && sw.ElapsedMilliseconds < 3000) r = source.Next(100, dirty);
        if (r != CaptureResult.Frame) throw new Exception($"no first picture ({r})");
        lines.Add($"capture   {source.Kind}, {source.Width}x{source.Height}, first picture in {sw.ElapsedMilliseconds} ms");

        using var frame = new Nv12Frame(source.Width, source.Height);
        int w = frame.Width, h = frame.Height;
        var original = new byte[w * h * 4];
        byte* bgra = source.Lock(out int pitch);
        try
        {
            fixed (byte* o = original)
                for (int y = 0; y < h; y++) Buffer.MemoryCopy(bgra + y * pitch, o + y * w * 4, w * 4, w * 4);
            sw.Restart();
            for (int i = 0; i < 10; i++) ColorConvert.BgraToNv12(bgra, pitch, 0, 0, w, h, frame, 1);
            lines.Add($"convert   whole screen to NV12 in {sw.Elapsed.TotalMilliseconds / 10:0.0} ms");
            sw.Restart();
            for (int i = 0; i < 100; i++) ColorConvert.BgraToNv12(bgra, pitch, 640, 480, 128, 32, frame, 1);
            lines.Add($"convert   a typed word, 128x32, in {sw.Elapsed.TotalMilliseconds * 10:0} us");
        }
        finally { source.Unlock(); }

        using var encoder = H264Encoder.Create(w, h, 30, 6_000_000);
        lines.Add($"encoder   {encoder.Name}, {(encoder.Hardware ? "on the GPU" : "software")}");
        var frames = new BlockingCollection<(byte[] Data, bool Key)>();
        encoder.Output = (au, _, key) => frames.Add((au.ToArray(), key));
        (byte[] Data, bool Key) Next(long t)
        {
            encoder.Encode(frame, t, 333_333);
            if (!frames.TryTake(out var f, 3000)) throw new Exception($"{encoder.Name} gave no frame");
            return f;
        }
        sw.Restart();
        var first = Next(0);
        if (!first.Key)
        {
            var types = string.Join(",", H264Encoder.Nalus(first.Data).Select(n => first.Data[n.Start] & 0x1f));
            throw new Exception($"the first frame was not a keyframe: {first.Data.Length} bytes, NAL types {types}");
        }
        var keyframe = first.Data;
        lines.Add($"encode    keyframe {keyframe.Length / 1024} KB in {sw.ElapsedMilliseconds} ms");

        using var decoder = H264Decoder.Create();
        Nv12Frame? decoded = null;
        sw.Restart();
        if (!decoder.Decode(keyframe, 0, ref decoded) || decoded == null) throw new Exception("the decoder gave no picture");
        lines.Add($"decode    {decoder.Name}, {decoded.Width}x{decoded.Height} in {sw.ElapsedMilliseconds} ms");
        try
        {
            if (decoded.Width != w || decoded.Height != h) throw new Exception($"decoded {decoded.Width}x{decoded.Height}, sent {w}x{h}");
            var back = new byte[w * h * 4];
            fixed (byte* b = back)
            {
                sw.Restart();
                ColorConvert.Nv12ToBgra(decoded, b, w * 4);
                lines.Add($"convert   NV12 back to the screen in {sw.Elapsed.TotalMilliseconds:0.0} ms");
            }
            double psnr = Psnr(original, back);
            lines.Add($"quality   {psnr:0.0} dB PSNR for the first keyframe");
            if (psnr < 25) throw new Exception($"the picture came back wrong, PSNR {psnr:0.0} dB");

            // A typed word: a small change makes a small frame. Then the same picture again sharpens it.
            for (int y = 500; y < 532; y++)
            for (int x = 640; x < 768; x++)
                frame.Data[y * w + x] = (byte)(((x / 4) + (y / 4)) % 2 == 0 ? 20 : 235);
            // Every frame after the first keyframe, in order, through one decoder like the viewer's:
            // a typed word, the sharpening frames, then a run with a keyframe on request in the middle.
            var seen = new List<string>();
            static string Hex(byte[] d, (int Start, int End) n) => Convert.ToHexString(d, n.Start, Math.Min(24, n.End - n.Start));
            static string Layout(byte[] d) => string.Join(" ", H264Encoder.Nalus(d).Select(n =>
                $"{d[n.Start] & 0x1f}:{((d[n.Start] & 0x1f) is 7 or 8 ? Hex(d, n) : (n.End - n.Start).ToString())}"));
            seen.Add("key0: " + Layout(keyframe));
            int small = 0, refine = 0;
            double ms = 0;
            for (int i = 0; i < 15; i++)
            {
                if (i >= 3) frame.Data[i * 7 * w + 100 + i] ^= 0x55;
                if (i == 9) encoder.RequestKeyframe();
                sw.Restart();
                var f = Next((i + 1) * 333_333L);
                if (i == 0) { small = f.Data.Length; ms = sw.Elapsed.TotalMilliseconds; }
                if (i == 1) refine = f.Data.Length;
                seen.Add($"f{i}{(f.Key ? "K" : "")}: {Layout(f.Data)}");
                try { decoder.Decode(f.Data, (i + 1) * 333_333L, ref decoded); }
                catch (Exception e) { throw new Exception($"frame {i} did not decode: {e.Message.Split('\n')[0]}\n" + string.Join("\n", seen)); }
            }
            lines.Add($"encode    a typed word {small / 1024.0:0.0} KB in {ms:0.0} ms, sharpening {refine / 1024.0:0.0} KB");
            lines.Add("stream    15 frames in a row decoded, a keyframe on request among them");
        }
        finally { decoded?.Dispose(); }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The encoder a session uses on this PC, picked as HostPipeline picks it: Direct3D 12's where
    /// it works, else Media Foundation's GPU encoder. With constant QP the same picture goes out as
    /// a keyframe at QP 40 and at QP 22, and the second must be at least twice as large: an encoder
    /// that takes the quantizer and ignores it would never make the picture sharper.
    /// </summary>
    private static string EncoderQp()
    {
        var screen = ShareScreen.All()[0];
        using var source = new DuplicationSource(screen, gpu: true, picture: true);
        if (GpuConverter.Check(source.Device, source.Context) is { } problem)
            return $"not the GPU path here ({problem}): sessions use the encoder of the picture path above";
        var dirty = new List<Dirty>();
        var sw = Stopwatch.StartNew();
        while (source.Next(100, dirty) != CaptureResult.Frame && sw.ElapsedMilliseconds < 3000) { }
        int w = source.Width & ~1, h = source.Height & ~1;
        IVideoEncoder encoder;
        GpuConverter gpu;
        string? notD3d12 = null;
        try
        {
            if (D3D12H264Encoder.Broken) throw new NotSupportedException("it failed earlier in this run");
            var d3d12 = new D3D12H264Encoder(source.Device, source.Context, w, h, 60, 2_500_000);
            encoder = d3d12;
            try { gpu = new GpuConverter(source.Device, source.Context, source.Picture, source.Width, source.Height, w, h, d3d12.Input); }
            catch { d3d12.Dispose(); throw; }
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or SharpGen.Runtime.SharpGenException)
        {
            notD3d12 = e.Message;
            gpu = new GpuConverter(source.Device, source.Context, source.Picture, source.Width, source.Height, w, h);
            try { encoder = H264Encoder.Create(w, h, 60, 2_500_000, device: source.Device); }
            catch (InvalidOperationException x)
            {
                gpu.Dispose();
                return $"no GPU encoder takes textures here ({x.Message}): sessions use the encoder of the picture path above";
            }
        }
        using var e1 = encoder;
        using var g1 = gpu;
        string name = encoder is D3D12H264Encoder d ? $"{d.Name} ({d.RateMode})"
            : $"{encoder.Name} ({(encoder.ConstantQp ? "constant QP" : "constant bitrate")})";
        if (notD3d12 != null) name += $"; Direct3D 12's encoder is not used here: {notD3d12}";
        if (!encoder.ConstantQp)
            return $"{name}: no constant QP, sessions here keep a constant bitrate and text does not sharpen the new way";
        var sizes = new BlockingCollection<int>();
        encoder.Output = (au, _, _) => sizes.Add(au.Length);
        int Keyframe(int qp, int i)
        {
            encoder.Qp = qp;
            encoder.RequestKeyframe();
            encoder.Encode(gpu.Convert(), i * 333_333L, 333_333);
            if (!sizes.TryTake(out int n, 3000)) throw new Exception($"{encoder.Name} gave no frame");
            return n;
        }
        int coarse = Keyframe(40, 0), fine = Keyframe(22, 1);
        if (fine < coarse * 2)
            throw new Exception($"{name} takes a quantizer but ignores it: keyframe {coarse >> 10} KB at QP 40, {fine >> 10} KB at QP 22");
        return $"{name}\nkeyframe {coarse >> 10} KB at QP 40, {fine >> 10} KB at QP 22: the quantizer takes effect";
    }

    /// <summary>
    /// The GPU path as the sharing PC runs it: the picture stays on the GPU, the video processor
    /// makes NV12 and the GPU's encoder reads it. The decoded result is compared with the picture.
    /// </summary>
    private static unsafe string Gpu()
    {
        var lines = new List<string>();
        var screen = ShareScreen.All()[0];
        using var source = new DuplicationSource(screen, gpu: true);
        var problem = GpuConverter.Check(source.Device, source.Context);
        if (problem != null) return $"not on this PC ({problem}), the processor path takes over";
        lines.Add("colors    the GPU's conversion matches ColorConvert");
        var dirty = new List<Dirty>();
        var sw = Stopwatch.StartNew();
        var r = CaptureResult.Timeout;
        while (r == CaptureResult.Timeout && sw.ElapsedMilliseconds < 3000) r = source.Next(100, dirty);
        if (r != CaptureResult.Frame) throw new Exception($"no first picture ({r})");
        int w = source.Width & ~1, h = source.Height & ~1;

        // The picture as it was, read back only for this comparison.
        var original = new byte[w * h * 4];
        var context = source.Context;
        using (var staging = source.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
               {
                   Width = (uint)source.Width, Height = (uint)source.Height, MipLevels = 1, ArraySize = 1,
                   Format = Vortice.DXGI.Format.B8G8R8A8_UNorm, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                   Usage = Vortice.Direct3D11.ResourceUsage.Staging, CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read,
               }))
        {
            context.CopyResource(staging, source.Picture);
            var map = context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            fixed (byte* o = original)
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + y * (int)map.RowPitch, o + y * w * 4, w * 4, w * 4);
            context.Unmap(staging, 0);
        }

        // Each encoder the GPU path may use: a keyframe and two frames after it, all decoded, the
        // keyframe compared with the picture.
        void Try(string label, Func<(IVideoEncoder, GpuConverter)> open)
        {
            IVideoEncoder? encoder = null;
            GpuConverter? gpu = null;
            try { (encoder, gpu) = open(); }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or SharpGen.Runtime.SharpGenException)
            {
                lines.Add($"{label,-9} not on this PC: {e.Message}");
                return;
            }
            using var e1 = encoder;
            using var g1 = gpu;
            var frames = new BlockingCollection<(byte[] Data, bool Key)>();
            encoder.Output = (au, _, key) => frames.Add((au.ToArray(), key));
            sw.Restart();
            var nv12 = gpu.Convert();
            double convert = sw.Elapsed.TotalMilliseconds;
            encoder.Encode(nv12, 0, 333_333);
            if (!frames.TryTake(out var first, 3000)) throw new Exception($"{encoder.Name} gave no frame from a GPU texture");
            long took = sw.ElapsedMilliseconds;
            for (int i = 1; i < 3; i++) encoder.Encode(gpu.Convert(), i * 333_333L, 333_333);
            using var decoder = H264Decoder.Create();
            Nv12Frame? decoded = null;
            try
            {
                if (!decoder.Decode(first.Data, 0, ref decoded) || decoded == null) throw new Exception($"{label}: the decoder gave no picture");
                var back = new byte[w * h * 4];
                fixed (byte* b = back) ColorConvert.Nv12ToBgra(decoded, b, w * 4);
                double psnr = Psnr(original, back);
                int more = 0;
                for (int i = 1; i < 3 && frames.TryTake(out var next, 3000); i++)
                    if (decoder.Decode(next.Data, i * 333_333L, ref decoded)) more++;
                lines.Add($"{label,-9} {encoder.Name}: keyframe {first.Data.Length / 1024} KB in {took} ms, conversion {convert:0.00} ms, {psnr:0.0} dB PSNR, {more} of 2 later frames decoded");
                if (psnr < 25) throw new Exception($"{label}: the picture came back wrong, PSNR {psnr:0.0} dB");
                if (more < 2) throw new Exception($"{label}: frames after the keyframe did not decode");
            }
            finally { decoded?.Dispose(); }
        }
        Try("direct3d", () =>
        {
            var d3d12 = new D3D12H264Encoder(source.Device, source.Context, w, h, 30, 6_000_000);
            try { return (d3d12, new GpuConverter(source.Device, source.Context, source.Picture, source.Width, source.Height, w, h, d3d12.Input)); }
            catch { d3d12.Dispose(); throw; }
        });
        Try("mf", () =>
        {
            var gpu = new GpuConverter(source.Device, source.Context, source.Picture, source.Width, source.Height, w, h);
            try { return (H264Encoder.Create(w, h, 30, 6_000_000, device: source.Device), gpu); }
            catch { gpu.Dispose(); throw; }
        });
        source.Dispose();
        if (!D3D12H264Encoder.Broken)
            foreach (int scale in new[] { 1, 2 })
                lines.Add(Straight(screen, scale));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The GPU path as sessions run it: no picture of the whole screen, each frame's changes go
    /// from Desktop Duplication straight to the converter and into the encoder's texture. The
    /// decoded keyframe is compared with the screen as GDI sees it, turned to the panel's way.
    /// </summary>
    private static unsafe string Straight(ShareScreen screen, int scale)
    {
        using var source = new DuplicationSource(screen, gpu: true, picture: false);
        int w = (source.Width / scale) & ~1, h = (source.Height / scale) & ~1;
        using var encoder = new D3D12H264Encoder(source.Device, source.Context, w, h, 30, 8_000_000);
        using var gpu = new GpuConverter(source.Device, source.Context, null, source.Width, source.Height, w, h, encoder.Input);
        source.FrameArrived = gpu.Convert;
        var dirty = new List<Dirty>();
        var sw = Stopwatch.StartNew();
        int frames = 0;
        // The first picture, and the changes of the next half second.
        while (sw.ElapsedMilliseconds < 3000 && (frames == 0 || sw.ElapsedMilliseconds < 500))
        {
            dirty.Clear();
            if (source.Next(50, dirty) == CaptureResult.Frame) frames++;
        }
        if (frames == 0) throw new Exception("no picture straight from Desktop Duplication");
        byte[]? key = null;
        encoder.Output = (au, _, _) => key ??= au.ToArray();
        encoder.Encode(gpu.Convert(), 0, 333_333);

        // The screen now, as GDI sees it, turned to the panel's way and brought to the frame's size.
        using var gdi = new GdiSource(screen);
        if (gdi.Next(0, dirty) != CaptureResult.Frame) throw new Exception("GDI gave no picture");
        int pw = source.Width, ph = source.Height;
        var panel = new byte[pw * ph * 4];
        byte* src = gdi.Lock(out int sp);
        try
        {
            fixed (byte* d = panel)
                for (int ny = 0; ny < ph; ny++)
                for (int nx = 0; nx < pw; nx++)
                {
                    var (x, y) = source.Rotation switch
                    {
                        90 => (ph - 1 - ny, nx),
                        180 => (pw - 1 - nx, ph - 1 - ny),
                        270 => (ny, pw - 1 - nx),
                        _ => (nx, ny),
                    };
                    ((uint*)d)[ny * pw + nx] = *(uint*)(src + y * sp + x * 4);
                }
        }
        finally { gdi.Unlock(); }
        var reference = new byte[w * h * 4];
        using (var nv12 = new Nv12Frame(w, h))
        fixed (byte* p = panel)
        fixed (byte* r = reference)
        {
            ColorConvert.BgraToNv12(p, pw * 4, 0, 0, w * scale, h * scale, nv12, scale);
            ColorConvert.Nv12ToBgra(nv12, r, w * 4);
        }

        using var decoder = H264Decoder.Create();
        Nv12Frame? decoded = null;
        try
        {
            if (key == null || !decoder.Decode(key, 0, ref decoded) || decoded == null) throw new Exception("the decoder gave no picture");
            var back = new byte[w * h * 4];
            fixed (byte* b = back) ColorConvert.Nv12ToBgra(decoded, b, w * 4);
            double psnr = Psnr(reference, back);
            if (psnr < 25) throw new Exception($"straight from Desktop Duplication at {w}x{h} the picture came back wrong, PSNR {psnr:0.0} dB");
            return $"straight   {w}x{h}{(scale > 1 ? ", half size" : "")}: {frames} frames converted as they came, keyframe {key.Length / 1024} KB, {psnr:0.0} dB PSNR against the screen";
        }
        finally { decoded?.Dispose(); }
    }
    /// <summary>
    /// On a turned screen, compares Desktop Duplication's picture with the plain capture's, which
    /// shows the screen as people see it, for each way of turning it back.
    /// </summary>
    private static unsafe string Turned()
    {
        var screen = ShareScreen.All()[0];
        using var dup = new DuplicationSource(screen, snapshot: false);
        if (dup.Rotation == 0) return $"{screen.DeviceName} is not turned, nothing to check";
        using var gdi = new GdiSource(screen);
        var d = new List<Dirty>();
        // A small window that changes color makes Windows draw, so a real picture comes.
        using (var nudge = new System.Windows.Forms.Form
               {
                   FormBorderStyle = System.Windows.Forms.FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
                   StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                   Bounds = new System.Drawing.Rectangle(screen.X + 8, screen.Y + 8, 24, 24),
               })
        {
            nudge.Show();
            var waited = Stopwatch.StartNew();
            while (true)
            {
                nudge.BackColor = waited.ElapsedMilliseconds / 100 % 2 == 0 ? System.Drawing.Color.Black : System.Drawing.Color.White;
                System.Windows.Forms.Application.DoEvents();
                if (dup.Next(50, d) == CaptureResult.Frame) break;
                if (waited.ElapsedMilliseconds > 5000) return "Windows drew nothing new within 5 s, nothing to compare";
            }
        }
        d.Clear();
        if (gdi.Next(1000, d) != CaptureResult.Frame) throw new Exception("no picture from the plain capture");
        byte* n = dup.Lock(out int np);
        byte* l = gdi.Lock(out int lp);
        try
        {
            int wn = dup.Width, hn = dup.Height, wl = gdi.Width, hl = gdi.Height;
            var scores = new List<string>();
            int best = -1;
            double bestDiff = double.MaxValue;
            foreach (int cw in new[] { 0, 90, 180, 270 })
            {
                // Logical (x, y) taken from the native picture turned clockwise by cw.
                if ((cw % 180 == 0 ? (wn, hn) : (hn, wn)) != (wl, hl)) continue;
                double diff = 0;
                int count = 0;
                for (int y = 3; y < hl; y += hl / 37)
                for (int x = 5; x < wl; x += wl / 41)
                {
                    var (nx, ny) = cw switch
                    {
                        90 => (y, hn - 1 - x),
                        180 => (wn - 1 - x, hn - 1 - y),
                        270 => (wn - 1 - y, x),
                        _ => (x, y),
                    };
                    byte* a = l + y * lp + x * 4;
                    byte* b = n + ny * np + nx * 4;
                    diff += Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]);
                    count++;
                }
                diff /= count;
                scores.Add($"{cw}: {diff:0.0}");
                if (diff < bestDiff) (best, bestDiff) = (cw, diff);
            }
            string result = $"Windows says {dup.Rotation} degrees, the picture matches turned {best} clockwise (differences {string.Join(", ", scores)})";
            if (bestDiff > 8) throw new Exception("no way round matches: " + result);
            return result;
        }
        finally
        {
            dup.Unlock();
            gdi.Unlock();
        }
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        double sum = 0;
        long n = 0;
        for (int i = 0; i < a.Length; i += 4)
        for (int c = 0; c < 3; c++)
        {
            int d = a[i + c] - b[i + c];
            sum += d * d;
            n++;
        }
        double mse = sum / n;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static string Session(bool online)
    {
        var lines = new List<string>();
        var relays = online ? null : new[] { "none" };
        var hostEvents = new BlockingCollection<JsonElement>();
        var viewEvents = new BlockingCollection<JsonElement>();
        var total = Stopwatch.StartNew();

        using var host = RtcHelper.Start(new { op = "host", name = "Selftest sharing PC", route = new { mode = "direct" }, relays });
        host.Event += e => hostEvents.Add(e);
        var code = Wait(hostEvents, "code", 15).GetProperty("code").GetString()!;
        lines.Add($"code      {code[..3]} {code[3..6]} {code[6..]} in {total.ElapsedMilliseconds} ms");

        using var view = RtcHelper.Start(new { op = "join", code, name = "Selftest viewing PC", route = new { mode = "direct" }, relays });
        view.Event += e => viewEvents.Add(e);
        var connect = Stopwatch.StartNew();
        var connected = Wait(viewEvents, "connected", 40);
        Wait(hostEvents, "connected", 20);
        lines.Add($"connected {connected.GetProperty("path").GetString()} path, {connected.GetProperty("local").GetString()} to {connected.GetProperty("remote").GetString()}, in {connect.ElapsedMilliseconds} ms after the code");

        // What this process holds before the screen pipelines start, to tell what they cost.
        var baseline = Memory();
        using var pipeline = new HostPipeline(host);
        host.Event += e =>
        {
            var ev = e.GetProperty("ev").GetString();
            if (ev == "keyframe") pipeline.RequestKeyframe();
            if (ev == "bitrate") pipeline.SetBitrate(e.GetProperty("bps").GetInt32());
        };
        host.Message += (_, m) =>
        {
            if (!m.IsEmpty && m.Span[0] == Msg.Keyframe) pipeline.RequestKeyframe();
        };
        var shown = new ManualResetEventSlim();
        int frames = 0;
        string size = "";
        // The sharing side alone first, to tell what each side costs.
        pipeline.Start();
        Thread.Sleep(1500);
        var hostOnly = Memory();
        lines.Add($"memory    the sharing PC's pipeline: +{(hostOnly.PrivateWs - baseline.PrivateWs) >> 20} MB private working set, +{(hostOnly.Private - baseline.Private) >> 20} MB committed");
        var viewer = new ViewerPipeline(() => view.Send(RtcHelper.KindCtl, MsgWriter.Bare(Msg.Keyframe)));
        try
        {
            viewer.FrameReady += () =>
            {
                var f = viewer.Take();
                if (f == null) return;
                size = $"{f.Width}x{f.Height}";
                Interlocked.Increment(ref frames);
                shown.Set();
            };
            view.Video += f => viewer.Push(f.Span);
            var first = Stopwatch.StartNew();
            view.Send(RtcHelper.KindCtl, MsgWriter.Bare(Msg.Keyframe));
            if (!shown.Wait(TimeSpan.FromSeconds(15))) throw new Exception("the viewer showed no picture");
            lines.Add($"picture   {size} on the viewer {first.ElapsedMilliseconds} ms after it asked, with {pipeline.Encoder}");

            // Keyframes on request travel the whole way: the viewer asks, the sharing PC sends.
            int before = frames;
            for (int i = 0; i < 5; i++)
            {
                view.Send(RtcHelper.KindCtl, MsgWriter.Bare(Msg.Keyframe));
                Thread.Sleep(350);
            }
            lines.Add($"frames    {frames - before} more pictures after 5 keyframe requests, {pipeline.BytesSent / 1024} KB sent in all");

            Thread.Sleep(1200);
            var now = Memory();
            lines.Add($"memory    the viewing PC's pipeline: +{(now.PrivateWs - hostOnly.PrivateWs) >> 20} MB private working set, +{(now.Private - hostOnly.Private) >> 20} MB committed");
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(RtcHelper.Extract())))
                lines.Add($"memory    helper {p.Id}: {p.PrivateMemorySize64 >> 20} MB private, {p.WorkingSet64 >> 20} MB working set");
        }
        finally
        {
            pipeline.Dispose();
            viewer.Dispose();
        }

        view.Send(RtcHelper.KindCtl, MsgWriter.Bare(Msg.Bye));
        view.Stop();
        var ended = Wait(hostEvents, "ended", 15);
        lines.Add($"ended     the sharing PC heard: {ended.GetProperty("reason").GetString()}");
        return string.Join("\n", lines);
    }

    private static (long Private, long WorkingSet, long PrivateWs) Memory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var me = Process.GetCurrentProcess();
        me.Refresh();
        return (me.PrivateMemorySize64, me.WorkingSet64, ShareNative.PrivateWorkingSet());
    }

    private record struct Region(string Type, long Size, long Resident);

    /// <summary>Committed allocations of 256 KB and up, by base address, with how much of each is in memory.</summary>
    private static unsafe Dictionary<nint, Region> Regions()
    {
        var map = new Dictionary<nint, Region>();
        nint address = 0;
        var info = new MemoryBasicInformation();
        while (VirtualQuery(address, ref info, (nuint)sizeof(MemoryBasicInformation)) != 0)
        {
            if (info.State == 0x1000 && info.Type != 0x1000000) // committed, not an image
            {
                string type = info.Type == 0x20000 ? "private" : "mapped";
                long resident = 0;
                int pages = (int)((long)info.RegionSize / 4096);
                if ((long)info.RegionSize >= 256 << 10)
                {
                    var ws = new WorkingSetEx[pages];
                    for (int i = 0; i < pages; i++) ws[i].Va = info.BaseAddress + i * 4096;
                    fixed (WorkingSetEx* p = ws)
                        if (QueryWorkingSetEx(Process.GetCurrentProcess().Handle, p, (uint)(pages * sizeof(WorkingSetEx))))
                            for (int i = 0; i < pages; i++) if ((ws[i].Attributes & 1) != 0 && (ws[i].Attributes & 0x8000) == 0) resident += 4096;
                    map.TryGetValue(info.AllocationBase, out var r);
                    map[info.AllocationBase] = new Region(type, r.Size + (long)info.RegionSize, r.Resident + resident);
                }
            }
            address = info.BaseAddress + (nint)info.RegionSize;
            if (address <= 0) break;
        }
        return map;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WorkingSetEx { public nint Va; public nuint Attributes; }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint address, ref MemoryBasicInformation info, nuint length);

    [DllImport("kernel32.dll", EntryPoint = "K32QueryWorkingSetEx")]
    private static extern unsafe bool QueryWorkingSetEx(IntPtr process, WorkingSetEx* info, uint size);


    private static JsonElement Wait(BlockingCollection<JsonElement> events, string ev, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !events.TryTake(out var e, left)) throw new Exception($"no {ev} within {seconds} s");
            var name = e.GetProperty("ev").GetString();
            if (name == ev) return e;
            if (name == "failed") throw new Exception($"failed: {e}");
        }
    }
}
