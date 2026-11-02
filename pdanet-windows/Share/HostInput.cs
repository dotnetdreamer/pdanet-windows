using System.Runtime.InteropServices;
using static PdaNetPortable.Share.ShareNative;

namespace PdaNetPortable.Share;

/// <summary>
/// Plays the other PC's mouse and keyboard on this PC, with SendInput, which needs no admin.
/// Keys travel as scan codes, so this PC's keyboard layout decides the characters, like Remote
/// Desktop. Every key and button held down is tracked, so taking control away or a dropped link
/// never leaves one stuck. Windows keeps apps that run as administrator out of reach of
/// SendInput, as it does for every app that is not elevated.
/// </summary>
internal sealed class HostInput
{
    private readonly object _gate = new();
    private readonly HashSet<int> _keys = new();     // scan | extended << 16 | vk << 17
    private readonly HashSet<MouseButton> _buttons = new();
    private readonly INPUT[] _one = new INPUT[1];
    private ShareScreen _screen;
    private bool _allowed;

    public HostInput(ShareScreen screen) => _screen = screen;

    public ShareScreen Screen
    {
        set { lock (_gate) _screen = value; }
    }

    /// <summary>Whether the other PC may control this one. Taking it away lets go of everything held.</summary>
    public bool Allowed
    {
        get { lock (_gate) return _allowed; }
        set
        {
            lock (_gate)
            {
                _allowed = value;
                if (!value) ReleaseAllLocked();
            }
        }
    }

    public void Handle(ReadOnlySpan<byte> m)
    {
        if (m.IsEmpty) return;
        lock (_gate)
        {
            if (!_allowed) return;
            switch (m[0])
            {
                case Msg.Move when m.Length >= 5:
                    Mouse(MsgWriter.U16(m, 1), MsgWriter.U16(m, 3), MOUSEEVENTF_MOVE, 0);
                    break;
                case Msg.Button when m.Length >= 7:
                    Button((MouseButton)m[1], m[2] != 0, MsgWriter.U16(m, 3), MsgWriter.U16(m, 5));
                    break;
                case Msg.Wheel when m.Length >= 9:
                    short v = MsgWriter.I16(m, 1), h = MsgWriter.I16(m, 3);
                    ushort x = MsgWriter.U16(m, 5), y = MsgWriter.U16(m, 7);
                    if (v != 0) Mouse(x, y, MOUSEEVENTF_MOVE | MOUSEEVENTF_WHEEL, (uint)v);
                    if (h != 0) Mouse(x, y, MOUSEEVENTF_MOVE | MOUSEEVENTF_HWHEEL, (uint)h);
                    break;
                case Msg.Key when m.Length >= 4:
                    Key(MsgWriter.U16(m, 1), (m[3] & 1) != 0, (m[3] & 2) != 0, m.Length >= 5 ? m[4] : (byte)0);
                    break;
                case Msg.ReleaseAll:
                    ReleaseAllLocked();
                    break;
            }
        }
    }

    public void ReleaseAll()
    {
        lock (_gate) ReleaseAllLocked();
    }

    private void ReleaseAllLocked()
    {
        foreach (var k in _keys.ToList())
            Key((ushort)(k & 0xffff), false, (k & 0x10000) != 0, (byte)(k >> 17));
        foreach (var b in _buttons.ToList())
            Button(b, false, null, null);
        _keys.Clear();
        _buttons.Clear();
    }

    private void Button(MouseButton button, bool down, ushort? x, ushort? y)
    {
        uint flags = button switch
        {
            MouseButton.Left => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            MouseButton.Right => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            MouseButton.Middle => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
        };
        uint data = button switch { MouseButton.X1 => XBUTTON1, MouseButton.X2 => XBUTTON2, _ => 0 };
        if (down) _buttons.Add(button);
        else _buttons.Remove(button);
        // A click carries its own position, so it lands where it was aimed even when a move before it was dropped.
        if (x.HasValue && y.HasValue) Mouse(x.Value, y.Value, flags | MOUSEEVENTF_MOVE, data);
        else Send(new MOUSEINPUT { dwFlags = flags, mouseData = data, dwExtraInfo = InjectedTag });
    }

    /// <summary>A mouse event at a position given as 0 to 65535 across the shared screen.</summary>
    private void Mouse(ushort nx, ushort ny, uint flags, uint data)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        double px = _screen.X + nx / 65535.0 * (_screen.Width - 1);
        double py = _screen.Y + ny / 65535.0 * (_screen.Height - 1);
        Send(new MOUSEINPUT
        {
            dx = (int)Math.Round((px - vx) * 65535.0 / (vw - 1)),
            dy = (int)Math.Round((py - vy) * 65535.0 / (vh - 1)),
            dwFlags = flags | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            mouseData = data,
            dwExtraInfo = InjectedTag,
        });
    }

    private void Key(ushort scan, bool down, bool extended, byte vk)
    {
        int id = scan | (extended ? 0x10000 : 0) | vk << 17;
        if (down) _keys.Add(id);
        else _keys.Remove(id);
        var ki = new KEYBDINPUT
        {
            wScan = scan,
            wVk = scan == 0 ? vk : (ushort)0,
            dwFlags = (scan != 0 ? KEYEVENTF_SCANCODE : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : KEYEVENTF_KEYUP),
            dwExtraInfo = InjectedTag,
        };
        _one[0] = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = ki } };
        SendInput(1, _one, Marshal.SizeOf<INPUT>());
    }

    private void Send(MOUSEINPUT mi)
    {
        _one[0] = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = mi } };
        SendInput(1, _one, Marshal.SizeOf<INPUT>());
    }
}
