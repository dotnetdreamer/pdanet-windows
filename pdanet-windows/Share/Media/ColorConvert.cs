using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    /// becomes one pixel of the frame.
    /// </summary>
    public static void BgraToNv12(byte* src, int pitch, int x, int y, int w, int h, Nv12Frame dst, int scale)
    {
        if (w <= 0 || h <= 0) return;
        int step = scale * 2; // screen rows per row of color samples
        int bands = h >= ParallelRows * 2 ? Math.Min(Environment.ProcessorCount, h / ParallelRows) : 1;
        if (bands <= 1)
        {
            Rows(src, pitch, x, y, w, y, y + h, dst, scale);
            return;
        }
        int rowsPerBand = (h / bands + step - 1) / step * step;
        var srcAddr = (nint)src;
        Parallel.For(0, bands, b =>
        {
            int from = y + b * rowsPerBand, to = Math.Min(y + h, from + rowsPerBand);
            if (from < to) Rows((byte*)srcAddr, pitch, x, y, w, from, to, dst, scale);
        });
    }

    private static void Rows(byte* src, int pitch, int x, int y, int w, int from, int to, Nv12Frame dst, int scale)
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
                    Chroma(q00 + q01 + q10 + q11, g00 + g01 + g10 + g11, b00 + b01 + b10 + b11, 2, c);
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
                Chroma(rs, gs, bs, 4, c);
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
    public static void Nv12ToBgra(Nv12Frame src, byte* dst, int pitch)
    {
        int h = src.Height;
        int bands = Math.Clamp(Environment.ProcessorCount, 1, Math.Max(1, h / 64));
        int rowsPerBand = (h / bands + 1) & ~1;
        var dstAddr = (nint)dst;
        if (bands == 1)
        {
            ToBgraRows(src, dst, pitch, 0, h);
            return;
        }
        Parallel.For(0, bands, b =>
        {
            int from = b * rowsPerBand, to = Math.Min(h, from + rowsPerBand);
            if (from < to) ToBgraRows(src, (byte*)dstAddr, pitch, from, to);
        });
    }

    private static void ToBgraRows(Nv12Frame src, byte* dst, int pitch, int from, int to)
    {
        int w = src.Width;
        for (int y = from; y < to; y += 2)
        {
            byte* y0 = src.Data + y * w;
            byte* y1 = y0 + w;
            byte* c = src.UV + (y >> 1) * w;
            uint* d0 = (uint*)(dst + y * pitch);
            uint* d1 = (uint*)(dst + (y + 1) * pitch);
            for (int x = 0; x < w; x += 2, c += 2)
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
    private static uint Pixel(int luma, int rd, int gd, int bd)
    {
        int l = YK * (luma - 16);
        return 0xFF000000u
               | (uint)Clamp((l + rd) >> 16) << 16
               | (uint)Clamp((l + gd) >> 16) << 8
               | Clamp((l + bd) >> 16);
    }
}
