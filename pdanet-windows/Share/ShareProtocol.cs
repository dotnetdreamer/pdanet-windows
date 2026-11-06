using System.Buffers.Binary;
using System.Text;

namespace PdaNetPortable.Share;

/// <summary>
/// The remote control messages between the two PCs, on the WebRTC data channels. Each message is
/// one type byte and a small little endian body. "ctl" is reliable and ordered: keys, clicks,
/// clipboard, which screen. "live" drops late messages: mouse moves and the pointer, where only
/// the newest one matters. Positions are 0 to 65535 across the shared screen, so they do not
/// depend on either PC's resolution.
/// </summary>
internal static class Msg
{
    // From the PC that views, on "ctl".
    public const byte Hello = 0x01;       // u8 version, u16 screen width, u16 screen height of the viewer
    public const byte Key = 0x02;         // u16 scan code, u8 flags (1 down, 2 extended), u8 virtual key for keys without a scan code
    public const byte Button = 0x03;      // u8 button, u8 down, u16 x, u16 y
    public const byte Wheel = 0x04;       // i16 vertical, i16 horizontal, u16 x, u16 y
    public const byte Clipboard = 0x05;   // UTF-8 text, both ways
    public const byte PickScreen = 0x06;  // u8 screen
    public const byte Keyframe = 0x07;    // a full picture please
    public const byte Quality = 0x08;     // u8 preset
    public const byte ReleaseAll = 0x09;  // let go of every key and button

    // From the PC that views, on "live".
    public const byte Move = 0x20;        // u16 x, u16 y

    // From the PC that shares, on "ctl".
    public const byte Screens = 0x81;     // u8 count, u8 current, per screen u16 width, u16 height, u8 primary
    public const byte Cursor = 0x82;      // see CursorShape
    public const byte HostState = 0x84;   // u8 flags, see HostFlags
    public const byte Rotation = 0x86;    // u16 degrees the picture is turned clockwise from how the screen shows it
    public const byte Bye = 0x85;         // the other PC is leaving

    // From the PC that shares, on "live".
    public const byte Pointer = 0x90;     // u16 x, u16 y, u8 visible

    public const byte Version = 1;
}

[Flags]
internal enum HostFlags : byte
{
    None = 0,
    ControlAllowed = 1,
    // The other PC shows a screen Windows keeps private, like a UAC prompt or the lock screen.
    Hidden = 2,
    // The other PC shares with easy access, its code stays the same, so the code connects again.
    EasyAccess = 4,
}

internal enum MouseButton : byte { Left, Right, Middle, X1, X2 }

internal enum Quality : byte { Auto, Sharp, Smooth }

/// <summary>A screen on the PC that shares, as the other PC sees it.</summary>
internal sealed record ScreenInfo(int Width, int Height, bool Primary);

/// <summary>Writes the small messages without allocations beyond the message itself.</summary>
internal static class MsgWriter
{
    public static byte[] Hello(int width, int height)
    {
        var b = new byte[6];
        b[0] = Msg.Hello;
        b[1] = Msg.Version;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)Math.Clamp(width, 0, 65535));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)Math.Clamp(height, 0, 65535));
        return b;
    }

    public static byte[] Key(ushort scan, bool down, bool extended, byte vk) =>
        new[] { Msg.Key, (byte)scan, (byte)(scan >> 8), (byte)((down ? 1 : 0) | (extended ? 2 : 0)), vk };

    public static byte[] Button(MouseButton button, bool down, ushort x, ushort y) =>
        new[] { Msg.Button, (byte)button, (byte)(down ? 1 : 0), (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8) };

    public static byte[] Wheel(short vertical, short horizontal, ushort x, ushort y) =>
        new[]
        {
            Msg.Wheel, (byte)vertical, (byte)(vertical >> 8), (byte)horizontal, (byte)(horizontal >> 8),
            (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8),
        };

    public static byte[] Move(ushort x, ushort y) => new[] { Msg.Move, (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8) };

    public static byte[] Pointer(ushort x, ushort y, bool visible) =>
        new[] { Msg.Pointer, (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8), (byte)(visible ? 1 : 0) };

    public static byte[] Byte(byte type, byte value) => new[] { type, value };

    public static byte[] Bare(byte type) => new[] { type };

    public static byte[] Text(byte type, string text)
    {
        var b = new byte[1 + Encoding.UTF8.GetByteCount(text)];
        b[0] = type;
        Encoding.UTF8.GetBytes(text, b.AsSpan(1));
        return b;
    }

    public static byte[] Screens(IReadOnlyList<ScreenInfo> screens, int current)
    {
        var b = new byte[3 + screens.Count * 5];
        b[0] = Msg.Screens;
        b[1] = (byte)screens.Count;
        b[2] = (byte)current;
        for (int i = 0; i < screens.Count; i++)
        {
            var s = b.AsSpan(3 + i * 5);
            BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)screens[i].Width);
            BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)screens[i].Height);
            s[4] = (byte)(screens[i].Primary ? 1 : 0);
        }
        return b;
    }

    public static List<ScreenInfo> ReadScreens(ReadOnlySpan<byte> b, out int current)
    {
        current = b.Length > 2 ? b[2] : 0;
        var list = new List<ScreenInfo>();
        int count = b.Length > 1 ? b[1] : 0;
        for (int i = 0; i < count && 3 + i * 5 + 5 <= b.Length; i++)
        {
            var s = b[(3 + i * 5)..];
            list.Add(new ScreenInfo(BinaryPrimitives.ReadUInt16LittleEndian(s), BinaryPrimitives.ReadUInt16LittleEndian(s[2..]), s[4] != 0));
        }
        return list;
    }

    public static ushort U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);

    public static short I16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt16LittleEndian(b[at..]);
}
