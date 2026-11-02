using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;

namespace PdaNetPortable.Share;

/// <summary>
/// The pointer of the PC that shares. Standard pointers (arrow, text, hand and so on) travel as
/// one byte and show as the viewer's own pointer, so they look native and cost nothing. Other
/// pointers travel once as a picture and are then named by a hash.
/// </summary>
internal static class CursorShapes
{
    public const byte Hidden = 0, Standard = 1, Picture = 2, Known = 3;

    private static readonly (int Idc, byte Id)[] StandardIds =
    {
        (32512, 1), (32513, 2), (32514, 3), (32515, 4), (32516, 5), (32642, 6), (32643, 7),
        (32644, 8), (32645, 9), (32646, 10), (32648, 11), (32649, 12), (32650, 13), (32651, 14),
    };

    private static Dictionary<IntPtr, byte>? _standard;

    /// <summary>The standard pointer a handle is, or 0. Windows shares one handle per standard pointer.</summary>
    public static byte StandardId(IntPtr cursor)
    {
        _standard ??= StandardIds
            .Select(s => (Handle: ShareNative.LoadCursor(IntPtr.Zero, s.Idc), s.Id))
            .Where(s => s.Handle != IntPtr.Zero)
            .GroupBy(s => s.Handle).ToDictionary(g => g.Key, g => g.First().Id);
        return _standard.TryGetValue(cursor, out var id) ? id : (byte)0;
    }

    public static Cursor? WpfStandard(byte id) => id switch
    {
        1 => Cursors.Arrow, 2 => Cursors.IBeam, 3 => Cursors.Wait, 4 => Cursors.Cross, 5 => Cursors.UpArrow,
        6 => Cursors.SizeNWSE, 7 => Cursors.SizeNESW, 8 => Cursors.SizeWE, 9 => Cursors.SizeNS,
        10 => Cursors.SizeAll, 11 => Cursors.No, 12 => Cursors.Hand, 13 => Cursors.AppStarting, 14 => Cursors.Help,
        _ => null,
    };

    /// <summary>The handle of a standard pointer on this PC, to draw it where the other PC's pointer is.</summary>
    public static IntPtr StandardHandle(byte id)
    {
        foreach (var (idc, sid) in StandardIds)
            if (sid == id) return ShareNative.LoadCursor(IntPtr.Zero, idc);
        return IntPtr.Zero;
    }

    /// <summary>A pointer as BGRA with straight alpha, or null when Windows will not give it.</summary>
    public static unsafe PointerPicture? Read(IntPtr cursor)
    {
        if (!ShareNative.GetIconInfo(cursor, out var ii)) return null;
        var dc = ShareNative.GetDC(IntPtr.Zero);
        try
        {
            if (ii.hbmColor != IntPtr.Zero)
            {
                if (ShareNative.GetObject(ii.hbmColor, sizeof(ShareNative.BITMAP), out var bm) == 0) return null;
                int w = bm.bmWidth, h = bm.bmHeight;
                if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;
                var color = Bits(dc, ii.hbmColor, w, h);
                var mask = Bits(dc, ii.hbmMask, w, h);
                if (color == null) return null;
                bool alpha = false;
                for (int i = 3; i < color.Length; i += 4) alpha |= color[i] != 0;
                if (!alpha)
                    for (int i = 0; i < color.Length; i += 4)
                        color[i + 3] = (byte)(mask != null && mask[i] != 0 ? 0 : 255);
                return new PointerPicture(w, h, ii.xHotspot, ii.yHotspot, color);
            }

            // Black and white: the mask holds the AND half on top and the XOR half below.
            if (ShareNative.GetObject(ii.hbmMask, sizeof(ShareNative.BITMAP), out var mbm) == 0) return null;
            int mw = mbm.bmWidth, mh = mbm.bmHeight / 2;
            if (mw <= 0 || mh <= 0 || mw > 256 || mh > 256) return null;
            var both = Bits(dc, ii.hbmMask, mw, mh * 2);
            if (both == null) return null;
            var px = new byte[mw * mh * 4];
            var invert = new bool[mw * mh];
            for (int i = 0; i < mw * mh; i++)
            {
                bool and = both[i * 4] != 0, xor = both[(mw * mh + i) * 4] != 0;
                byte v = (byte)(xor && !and ? 255 : 0);
                px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = v;
                px[i * 4 + 3] = (byte)(and && !xor ? 0 : 255);
                invert[i] = and && xor;
            }
            // Pixels that invert the screen cannot be drawn as such: they become black with a white
            // edge, which reads on any background.
            for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
            {
                int i = y * mw + x;
                if (invert[i] || px[i * 4 + 3] != 0) continue;
                bool edge = (x > 0 && invert[i - 1]) || (x < mw - 1 && invert[i + 1])
                            || (y > 0 && invert[i - mw]) || (y < mh - 1 && invert[i + mw]);
                if (!edge) continue;
                px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = 255;
                px[i * 4 + 3] = 255;
            }
            return new PointerPicture(mw, mh, ii.xHotspot, ii.yHotspot, px);
        }
        finally
        {
            ShareNative.ReleaseDC(IntPtr.Zero, dc);
            if (ii.hbmColor != IntPtr.Zero) ShareNative.DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) ShareNative.DeleteObject(ii.hbmMask);
        }
    }

    private static unsafe byte[]? Bits(IntPtr dc, IntPtr bitmap, int w, int h)
    {
        if (bitmap == IntPtr.Zero) return null;
        var header = new ShareNative.BITMAPINFOHEADER
        {
            biSize = sizeof(ShareNative.BITMAPINFOHEADER),
            biWidth = w,
            biHeight = -h, // top down
            biPlanes = 1,
            biBitCount = 32,
        };
        var bits = new byte[w * h * 4];
        return ShareNative.GetDIBits(dc, bitmap, 0, (uint)h, bits, ref header, 0) == h ? bits : null;
    }

    // ============================ Messages ============================

    public static byte[] HiddenMessage() => new[] { Msg.Cursor, Hidden };

    public static byte[] StandardMessage(byte id) => new[] { Msg.Cursor, Standard, id };

    public static byte[] KnownMessage(uint hash)
    {
        var b = new byte[6];
        b[0] = Msg.Cursor;
        b[1] = Known;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), hash);
        return b;
    }

    public static byte[] PictureMessage(PointerPicture p)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(Msg.Cursor);
        ms.WriteByte(Picture);
        Span<byte> head = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(head, p.Hash);
        BinaryPrimitives.WriteUInt16LittleEndian(head[4..], (ushort)p.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(head[6..], (ushort)p.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(head[8..], (ushort)p.HotX);
        BinaryPrimitives.WriteUInt16LittleEndian(head[10..], (ushort)p.HotY);
        ms.Write(head);
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(p.Bgra);
        return ms.ToArray();
    }

    /// <summary>Reads a picture message, or null when it is damaged.</summary>
    public static PointerPicture? ReadPicture(ReadOnlySpan<byte> m)
    {
        if (m.Length < 14) return null;
        uint hash = BinaryPrimitives.ReadUInt32LittleEndian(m[2..]);
        int w = BinaryPrimitives.ReadUInt16LittleEndian(m[6..]), h = BinaryPrimitives.ReadUInt16LittleEndian(m[8..]);
        int hx = BinaryPrimitives.ReadUInt16LittleEndian(m[10..]), hy = BinaryPrimitives.ReadUInt16LittleEndian(m[12..]);
        if (w is <= 0 or > 256 || h is <= 0 or > 256) return null;
        var px = new byte[w * h * 4];
        try
        {
            using var z = new DeflateStream(new MemoryStream(m[14..].ToArray()), CompressionMode.Decompress);
            z.ReadExactly(px);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { return null; }
        return new PointerPicture(w, h, hx, hy, px) { HashOverride = hash };
    }
}

/// <summary>A pointer picture, BGRA with straight alpha.</summary>
internal sealed class PointerPicture
{
    public int Width { get; }
    public int Height { get; }
    public int HotX { get; }
    public int HotY { get; }
    public byte[] Bgra { get; }
    public uint? HashOverride { get; init; }
    public uint Hash => HashOverride ?? Fnv(Bgra, (uint)(Width << 16 | Height) ^ (uint)(HotX << 8 | HotY));

    /// <summary>FNV-1a, enough to tell pointers apart within one session.</summary>
    private static uint Fnv(byte[] data, uint seed)
    {
        uint h = 2166136261u ^ seed;
        foreach (var b in data) h = (h ^ b) * 16777619u;
        return h;
    }

    private Cursor? _cursor;
    private ImageSource? _image;

    public PointerPicture(int width, int height, int hotX, int hotY, byte[] bgra)
    {
        Width = width;
        Height = height;
        HotX = Math.Clamp(hotX, 0, width - 1);
        HotY = Math.Clamp(hotY, 0, height - 1);
        Bgra = bgra;
    }

    /// <summary>The picture as a WPF pointer, made once: a .cur file in memory with a 32 bit picture.</summary>
    public Cursor ToCursor()
    {
        if (_cursor != null) return _cursor;
        int maskStride = ((Width + 31) / 32) * 4;
        int image = 40 + Width * Height * 4 + maskStride * Height;
        using var ms = new MemoryStream(22 + image);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write((ushort)0);
            w.Write((ushort)2); // a cursor
            w.Write((ushort)1);
            w.Write((byte)(Width >= 256 ? 0 : Width));
            w.Write((byte)(Height >= 256 ? 0 : Height));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)HotX);
            w.Write((ushort)HotY);
            w.Write(image);
            w.Write(22);
            // BITMAPINFOHEADER, twice the height for the picture and the mask.
            w.Write(40);
            w.Write(Width);
            w.Write(Height * 2);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(0);
            w.Write(Width * Height * 4 + maskStride * Height);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            for (int y = Height - 1; y >= 0; y--) w.Write(Bgra, y * Width * 4, Width * 4); // bottom up
            w.Write(new byte[maskStride * Height]); // the alpha decides, the mask stays empty
        }
        ms.Position = 0;
        return _cursor = new Cursor(ms, false);
    }

    /// <summary>The picture to draw where the other PC's pointer is, when this PC does not control it.</summary>
    public ImageSource ToImage()
    {
        if (_image != null) return _image;
        var b = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Bgra, Width * 4);
        b.Freeze();
        return _image = b;
    }
}
