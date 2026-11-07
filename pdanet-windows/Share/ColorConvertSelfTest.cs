using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using PdaNetPortable.Share.Media;

namespace PdaNetPortable.Share;

/// <summary>Byte-exact color, tail, alignment and pitch checks for the viewer's SIMD converter.</summary>
internal static unsafe class ColorConvertSelfTest
{
    public static string Run()
    {
        int rowCases = CompleteRowCoverage();
        ExhaustiveColors();
        var random = new Random(0x709);
        int cases = 0;
        foreach (int width in new[] { 2, 4, 6, 8, 10, 14, 16, 18, 30, 32, 34, 66, 70, 126, 128, 130, 514 })
        foreach (int height in new[] { 2, 6, 66, 130 })
        foreach (int padding in new[] { 0, 1, 7, 32 })
        {
            using var frame = new Nv12Frame(width, height);
            random.NextBytes(frame.AsSpan());
            Compare(frame, padding, prefix: 13);
            cases++;
        }
        string path = Avx2.IsSupported && Ssse3.IsSupported ? "AVX2" : Sse41.IsSupported && Ssse3.IsSupported ? "SSE4.1" : "scalar";
        return $"all 16,777,216 Y/U/V combinations and {cases} tail/alignment/pitch cases match the scalar reference; {rowCases} complete-row checks; {path}\n"
               + Benchmark(1920, 1080) + "; " + Benchmark(3840, 2160);
    }

    private static int CompleteRowCoverage()
    {
        // Scalar and SIMD use the same parallel row split, so comparing them cannot detect
        // rows skipped by both. Uniform black/white has an independent expected value at
        // every pixel. Dynamic heights exercise the current CPU's last partial band; the
        // fixed heights cover the recorded 1050/4 and 1200/18 failures as well.
        int processors = Math.Clamp(Environment.ProcessorCount, 1, 64);
        int checks = 0;
        foreach (int height in new[] { 1050, 1200, 1284, 64 * processors + 2, 256 * processors + 2, 256 * processors + 4 }.Distinct())
        {
            foreach (int scale in new[] { 1, 2 })
            {
                if (height % (2 * scale) != 0) continue;
                const int width = 64;
                var white = new byte[width * height * 4];
                Array.Fill(white, (byte)255);
                using var frame = new Nv12Frame(width / scale, height / scale);
                frame.AsSpan().Fill(0xA5);
                fixed (byte* source = white)
                    ColorConvert.BgraToNv12(source, width * 4, 0, 0, width, height, frame, scale);
                for (int i = 0; i < frame.Length; i++)
                {
                    byte expected = i < frame.Width * frame.Height ? (byte)235 : (byte)128;
                    if (frame.Data[i] != expected)
                        throw new InvalidOperationException($"BGRA to NV12 missed or changed a row at height {height}, scale {scale}, byte {i}: expected {expected}, got {frame.Data[i]}");
                }
                checks++;
            }

            using var picture = new Nv12Frame(32, height);
            foreach (byte luma in new byte[] { 16, 235 })
            foreach (bool useSimd in new[] { false, true })
            {
                picture.AsSpan()[..(picture.Width * picture.Height)].Fill(luma);
                picture.AsSpan()[(picture.Width * picture.Height)..].Fill(128);
                const int prefix = 13, padding = 7;
                int pitch = picture.Width * 4 + padding;
                var output = new byte[prefix + pitch * height + 32];
                Array.Fill(output, (byte)0xA5);
                fixed (byte* converted = output)
                    ColorConvert.Nv12ToBgra(picture, converted + prefix, pitch, useSimd);
                for (int i = 0; i < output.Length; i++)
                {
                    int offset = i - prefix;
                    bool pixel = offset >= 0 && offset < pitch * height && offset % pitch < picture.Width * 4;
                    byte expected = pixel ? (luma == 235 || offset % pitch % 4 == 3 ? (byte)255 : (byte)0) : (byte)0xA5;
                    if (output[i] != expected)
                        throw new InvalidOperationException($"NV12 to BGRA missed or changed a row at height {height}, SIMD {useSimd}, byte {i}: expected {expected}, got {output[i]}");
                }
                checks++;
            }
        }
        return checks;
    }

    private static void ExhaustiveColors()
    {
        // One 2x2 group per U/V pair. In 64 passes its four luma samples visit every
        // possible Y value, including out-of-video-range values the decoder can output.
        using var frame = new Nv12Frame(512, 512);
        var expected = new byte[512 * 512 * 4];
        var actual = new byte[expected.Length];
        for (int y = 0; y < 512; y += 2)
        for (int x = 0; x < 512; x += 2)
        {
            byte* uv = frame.UV + y / 2 * 512 + x;
            uv[0] = (byte)(x / 2);
            uv[1] = (byte)(y / 2);
        }
        fixed (byte* reference = expected, converted = actual)
        {
            for (int pass = 0; pass < 64; pass++)
            {
                for (int y = 0; y < 512; y += 2)
                for (int x = 0; x < 512; x += 2)
                {
                    byte* p = frame.Data + y * 512 + x;
                    p[0] = (byte)(pass * 4);
                    p[1] = (byte)(pass * 4 + 1);
                    p[512] = (byte)(pass * 4 + 2);
                    p[513] = (byte)(pass * 4 + 3);
                }
                ColorConvert.Nv12ToBgra(frame, reference, 512 * 4, useSimd: false);
                ColorConvert.Nv12ToBgra(frame, converted, 512 * 4);
                RequireEqual(expected, actual, $"Y values {pass * 4}..{pass * 4 + 3}");
            }
        }
    }

    private static void Compare(Nv12Frame frame, int padding, int prefix)
    {
        int pitch = frame.Width * 4 + padding;
        var expected = new byte[prefix + pitch * frame.Height + 32];
        var actual = new byte[expected.Length];
        Array.Fill(expected, (byte)0xA5);
        Array.Fill(actual, (byte)0xA5);
        fixed (byte* reference = expected, converted = actual)
        {
            ColorConvert.Nv12ToBgra(frame, reference + prefix, pitch, useSimd: false);
            ColorConvert.Nv12ToBgra(frame, converted + prefix, pitch);
        }
        RequireEqual(expected, actual, $"{frame.Width}x{frame.Height}, pitch {pitch}, offset {prefix}");
        // Compare padding against the sentinel as well: both implementations must leave it alone.
        for (int i = 0; i < expected.Length; i++)
        {
            int offset = i - prefix;
            bool picture = offset >= 0 && offset < pitch * frame.Height && offset % pitch < frame.Width * 4;
            if (!picture && actual[i] != 0xA5)
                throw new InvalidOperationException($"color conversion wrote outside the picture at byte {i}");
        }
    }

    private static string Benchmark(int width, int height)
    {
        using var frame = new Nv12Frame(width, height);
        new Random(709).NextBytes(frame.AsSpan());
        var output = new byte[width * height * 4];
        fixed (byte* p = output)
        {
            // Same parallel split, same buffers, same pixels. Warm both methods before timing,
            // and report medians without a timing assertion that would fail on a busy PC.
            for (int i = 0; i < 8; i++)
            {
                ColorConvert.Nv12ToBgra(frame, p, width * 4, useSimd: false);
                ColorConvert.Nv12ToBgra(frame, p, width * 4);
            }
            double scalar = Time(frame, p, false), vector = Time(frame, p, true);
            return $"{width}x{height}: {scalar:0.00} ms scalar, {vector:0.00} ms selected path ({scalar / Math.Max(vector, 0.001):0.0}x)";
        }
    }

    private static double Time(Nv12Frame frame, byte* output, bool useSimd)
    {
        var samples = new double[3];
        for (int run = 0; run < samples.Length; run++)
        {
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 12; i++) ColorConvert.Nv12ToBgra(frame, output, frame.Width * 4, useSimd);
            samples[run] = clock.Elapsed.TotalMilliseconds / 12;
        }
        Array.Sort(samples);
        return samples[1];
    }

    private static void RequireEqual(byte[] expected, byte[] actual, string scenario)
    {
        if (expected.AsSpan().SequenceEqual(actual)) return;
        int at = 0;
        while (at < expected.Length && expected[at] == actual[at]) at++;
        throw new InvalidOperationException($"color conversion differs for {scenario}, byte {at}: expected {expected[at]}, got {actual[at]}");
    }
}
