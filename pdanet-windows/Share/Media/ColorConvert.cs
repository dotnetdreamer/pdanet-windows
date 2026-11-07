using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;

namespace PdaNetPortable.Share.Media;

/// <summary>
/// One picture in NV12, the layout H.264 encoders and decoders work in: a full size plane of
/// brightness, then a half size plane of color, U and V side by side. It lives in native memory
/// so the garbage collector never moves or copies it.
/// </summary>
internal sealed unsafe class Nv12Frame : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public byte* Data { get; private set; }
    public int Length => Width * Height * 3 / 2;
    public byte* UV => Data + Width * Height;

    public Nv12Frame(int width, int height)
    {
        Width = width & ~1;
        Height = height & ~1;
        Data = (byte*)NativeMemory.AlignedAlloc((nuint)Length, 64);
        // Black: no brightness, no color.
        NativeMemory.Fill(Data, (nuint)(Width * Height), 16);
        NativeMemory.Fill(UV, (nuint)(Width * Height / 2), 128);
    }

    public Span<byte> AsSpan() => new(Data, Length);

    public void Dispose()
    {
        if (Data == null) return;
        NativeMemory.AlignedFree(Data);
        Data = null;
        GC.SuppressFinalize(this);
    }

    ~Nv12Frame()
    {
        if (Data != null) NativeMemory.AlignedFree(Data);
    }
}

/// <summary>
/// Converts between the screen's BGRA and NV12 with BT.709 at video range (16 to 235), what every
/// GPU's video processor makes too, so the GPU and these converters give the same picture. Black
/// and white come back exact. Integer math with 16 fractional bits. Large areas are split across
/// the processor's cores; the usual small change, a caret or a typed word, runs on one.
/// </summary>
internal static unsafe class ColorConvert
{
    // RGB to YUV, BT.709, video range, times 65536. The color rows sum to exactly 0.
    private const int YR = 11966, YG = 40254, YB = 4064;
    private const int UR = -6596, UG = -22188, UB = 28784;
    private const int VR = 28784, VG = -26145, VB = -2639;

    // YUV to RGB, the inverse, times 65536.
    private const int YK = 76309, RV = 117489, GU = 13975, GV = 34925, BU = 138438;

    private const int ParallelRows = 256;

    /// <summary>
    /// Converts a rectangle of a BGRA screen into the NV12 frame. x, y, w and h are in screen
    /// pixels and must be even, multiples of 4 at scale 2, where each 2 by 2 block of the screen
    /// becomes one pixel of the frame. In black and white (gray) only the brightness is written,
    /// the color stays as the frame has it: neutral in a new frame.
    /// </summary>
    public static void BgraToNv12(byte* src, int pitch, int x, int y, int w, int h, Nv12Frame dst, int scale, bool gray = false)
    {
        if (w <= 0 || h <= 0) return;
        int step = scale * 2; // screen rows per row of color samples
        int bands = h >= ParallelRows * 2 ? Math.Min(Environment.ProcessorCount, h / ParallelRows) : 1;
        if (bands <= 1)
        {
            Rows(src, pitch, x, y, w, y, y + h, dst, scale, gray);
            return;
        }
        // Rounded up, so the bands cover every row: rounded down, 1050 rows in 4 bands left 2 out.
        int rowsPerBand = ((h + bands - 1) / bands + step - 1) / step * step;
        var srcAddr = (nint)src;
        Parallel.For(0, bands, b =>
        {
            int from = y + b * rowsPerBand, to = Math.Min(y + h, from + rowsPerBand);
            if (from < to) Rows((byte*)srcAddr, pitch, x, y, w, from, to, dst, scale, gray);
        });
    }

    private static void Rows(byte* src, int pitch, int x, int y, int w, int from, int to, Nv12Frame dst, int scale, bool gray)
    {
        int dw = dst.Width;
        byte* yPlane = dst.Data, uv = dst.UV;
        if (scale == 1)
        {
            for (int sy = from; sy < to; sy += 2)
            {
                byte* r0 = src + sy * pitch + x * 4;
                byte* r1 = r0 + pitch;
                byte* y0 = yPlane + sy * dw + x;
                byte* y1 = y0 + dw;
                byte* c = uv + (sy >> 1) * dw + x;
                for (int i = 0; i < w; i += 2, r0 += 8, r1 += 8, y0 += 2, y1 += 2, c += 2)
                {
                    int b00 = r0[0], g00 = r0[1], q00 = r0[2];
                    int b01 = r0[4], g01 = r0[5], q01 = r0[6];
                    int b10 = r1[0], g10 = r1[1], q10 = r1[2];
                    int b11 = r1[4], g11 = r1[5], q11 = r1[6];
                    y0[0] = Luma(q00, g00, b00);
                    y0[1] = Luma(q01, g01, b01);
                    y1[0] = Luma(q10, g10, b10);
                    y1[1] = Luma(q11, g11, b11);
                    if (!gray) Chroma(q00 + q01 + q10 + q11, g00 + g01 + g10 + g11, b00 + b01 + b10 + b11, 2, c);
                }
            }
            return;
        }

        // Half size: each frame pixel is the average of a 2 by 2 block of the screen.
        for (int sy = from; sy < to; sy += 4)
        {
            int dy = sy >> 1;
            byte* y0 = yPlane + dy * dw + (x >> 1);
            byte* y1 = y0 + dw;
            byte* c = uv + (dy >> 1) * dw + (x >> 1);
            byte* s = src + sy * pitch + x * 4;
            for (int i = 0; i < w; i += 4, s += 16, y0 += 2, y1 += 2, c += 2)
            {
                int rs = 0, gs = 0, bs = 0;
                for (int by = 0; by < 2; by++)
                for (int bx = 0; bx < 2; bx++)
                {
                    byte* p = s + by * 2 * pitch + bx * 8;
                    int b = p[0] + p[4] + p[pitch] + p[pitch + 4];
                    int g = p[1] + p[5] + p[pitch + 1] + p[pitch + 5];
                    int r = p[2] + p[6] + p[pitch + 2] + p[pitch + 6];
                    (by == 0 ? y0 : y1)[bx] = (byte)(16 + ((YR * r + YG * g + YB * b + (1 << 17)) >> 18));
                    rs += r; gs += g; bs += b;
                }
                if (!gray) Chroma(rs, gs, bs, 4, c);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Luma(int r, int g, int b) => (byte)(16 + ((YR * r + YG * g + YB * b + 32768) >> 16));

    /// <summary>U and V from sums of 2^shift squared pixels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Chroma(int r, int g, int b, int shift, byte* c)
    {
        int s = 16 + shift, half = 1 << (s - 1);
        c[0] = Clamp(((UR * r + UG * g + UB * b + half) >> s) + 128);
        c[1] = Clamp(((VR * r + VG * g + VB * b + half) >> s) + 128);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>Converts a whole NV12 frame to BGRA with full alpha, into a buffer of the same size.</summary>
    public static void Nv12ToBgra(Nv12Frame src, byte* dst, int pitch) => Nv12ToBgra(src, dst, pitch, useSimd: true);

    // Retain the integer reference path for checking every output byte, including clipping and
    // rounding. SIMD changes how many pixels are calculated together, never their color values.
    internal static void Nv12ToBgra(Nv12Frame src, byte* dst, int pitch, bool useSimd)
    {
        int h = src.Height;
        int bands = Math.Clamp(Environment.ProcessorCount, 1, Math.Max(1, h / 64));
        // Rounded up, so the bands cover every row: rounded down, 1200 rows in 18 bands (a PC with
        // 18 or more logical processors) left the bottom 12 black.
        int rowsPerBand = ((h + bands - 1) / bands + 1) & ~1;
        var dstAddr = (nint)dst;
        if (bands == 1)
        {
            ToBgraRows(src, dst, pitch, 0, h, useSimd);
            return;
        }
        Parallel.For(0, bands, b =>
        {
            int from = b * rowsPerBand, to = Math.Min(h, from + rowsPerBand);
            if (from < to) ToBgraRows(src, (byte*)dstAddr, pitch, from, to, useSimd);
        });
    }

    private static readonly Vector128<byte> ChromaU = Vector128.Create(
        (byte)0, 0, 2, 2, 4, 4, 6, 6, 128, 128, 128, 128, 128, 128, 128, 128);
    private static readonly Vector128<byte> ChromaV = Vector128.Create(
        (byte)1, 1, 3, 3, 5, 5, 7, 7, 128, 128, 128, 128, 128, 128, 128, 128);

    private static void ToBgraRows(Nv12Frame src, byte* dst, int pitch, int from, int to, bool useSimd)
    {
        int w = src.Width;
        for (int y = from; y < to; y += 2)
        {
            byte* y0 = src.Data + y * w;
            byte* y1 = y0 + w;
            byte* c = src.UV + (y >> 1) * w;
            uint* d0 = (uint*)(dst + y * pitch);
            uint* d1 = (uint*)(dst + (y + 1) * pitch);
            int x = 0;
            if (useSimd && Avx2.IsSupported && Ssse3.IsSupported)
            {
                for (; x <= w - 8; x += 8, c += 8)
                {
                    // Four interleaved chroma samples cover eight pixels on each of two rows.
                    // Read only these eight bytes: a narrow final row has no readable padding.
                    var uv = Sse2.LoadScalarVector128((ulong*)c).AsByte();
                    var u = Avx2.Subtract(Avx2.ConvertToVector256Int32(Ssse3.Shuffle(uv, ChromaU)), Vector256.Create(128));
                    var v = Avx2.Subtract(Avx2.ConvertToVector256Int32(Ssse3.Shuffle(uv, ChromaV)), Vector256.Create(128));
                    var rd = Avx2.Add(Avx2.MultiplyLow(v, Vector256.Create(RV)), Vector256.Create(32768));
                    var gd = Avx2.Subtract(Vector256.Create(32768), Avx2.Add(
                        Avx2.MultiplyLow(u, Vector256.Create(GU)), Avx2.MultiplyLow(v, Vector256.Create(GV))));
                    var bd = Avx2.Add(Avx2.MultiplyLow(u, Vector256.Create(BU)), Vector256.Create(32768));
                    Avx.Store(d0 + x, Pixels256(y0 + x, rd, gd, bd));
                    Avx.Store(d1 + x, Pixels256(y1 + x, rd, gd, bd));
                }
            }
            else if (useSimd && Sse41.IsSupported && Ssse3.IsSupported)
            {
                for (; x <= w - 4; x += 4, c += 4)
                {
                    var uv = Sse2.LoadScalarVector128((uint*)c).AsByte();
                    var u = Sse2.Subtract(Sse41.ConvertToVector128Int32(Ssse3.Shuffle(uv, ChromaU)), Vector128.Create(128));
                    var v = Sse2.Subtract(Sse41.ConvertToVector128Int32(Ssse3.Shuffle(uv, ChromaV)), Vector128.Create(128));
                    var rd = Sse2.Add(Sse41.MultiplyLow(v, Vector128.Create(RV)), Vector128.Create(32768));
                    var gd = Sse2.Subtract(Vector128.Create(32768), Sse2.Add(
                        Sse41.MultiplyLow(u, Vector128.Create(GU)), Sse41.MultiplyLow(v, Vector128.Create(GV))));
                    var bd = Sse2.Add(Sse41.MultiplyLow(u, Vector128.Create(BU)), Vector128.Create(32768));
                    Sse2.Store(d0 + x, Pixels128(y0 + x, rd, gd, bd));
                    Sse2.Store(d1 + x, Pixels128(y1 + x, rd, gd, bd));
                }
            }
            for (; x < w; x += 2, c += 2)
            {
                int u = c[0] - 128, v = c[1] - 128;
                int rd = RV * v + 32768, gd = -GU * u - GV * v + 32768, bd = BU * u + 32768;
                d0[x] = Pixel(y0[x], rd, gd, bd);
                d0[x + 1] = Pixel(y0[x + 1], rd, gd, bd);
                d1[x] = Pixel(y1[x], rd, gd, bd);
                d1[x + 1] = Pixel(y1[x + 1], rd, gd, bd);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Pixels256(byte* row, Vector256<int> rd, Vector256<int> gd, Vector256<int> bd)
    {
        var luma = Avx2.MultiplyLow(Avx2.Subtract(
            Avx2.ConvertToVector256Int32(Sse2.LoadScalarVector128((ulong*)row).AsByte()), Vector256.Create(16)), Vector256.Create(YK));
        var r = Clip256(Avx2.ShiftRightArithmetic(Avx2.Add(luma, rd), 16)).AsUInt32();
        var g = Clip256(Avx2.ShiftRightArithmetic(Avx2.Add(luma, gd), 16)).AsUInt32();
        var b = Clip256(Avx2.ShiftRightArithmetic(Avx2.Add(luma, bd), 16)).AsUInt32();
        return Avx2.Or(Vector256.Create(0xFF000000u), Avx2.Or(
            Avx2.ShiftLeftLogical(r, 16), Avx2.Or(Avx2.ShiftLeftLogical(g, 8), b)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Clip256(Vector256<int> value) =>
        Avx2.Min(Avx2.Max(value, Vector256<int>.Zero), Vector256.Create(255));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Pixels128(byte* row, Vector128<int> rd, Vector128<int> gd, Vector128<int> bd)
    {
        var luma = Sse41.MultiplyLow(Sse2.Subtract(
            Sse41.ConvertToVector128Int32(Sse2.LoadScalarVector128((uint*)row).AsByte()), Vector128.Create(16)), Vector128.Create(YK));
        var r = Clip128(Sse2.ShiftRightArithmetic(Sse2.Add(luma, rd), 16)).AsUInt32();
        var g = Clip128(Sse2.ShiftRightArithmetic(Sse2.Add(luma, gd), 16)).AsUInt32();
        var b = Clip128(Sse2.ShiftRightArithmetic(Sse2.Add(luma, bd), 16)).AsUInt32();
        return Sse2.Or(Vector128.Create(0xFF000000u), Sse2.Or(
            Sse2.ShiftLeftLogical(r, 16), Sse2.Or(Sse2.ShiftLeftLogical(g, 8), b)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Clip128(Vector128<int> value) =>
        Sse41.Min(Sse41.Max(value, Vector128<int>.Zero), Vector128.Create(255));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Pixel(int luma, int rd, int gd, int bd)
    {
        int l = YK * (luma - 16);
        return 0xFF000000u
               | (uint)Clamp((l + rd) >> 16) << 16
               | (uint)Clamp((l + gd) >> 16) << 8
               | Clamp((l + bd) >> 16);
    }
}
