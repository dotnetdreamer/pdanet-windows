using static PdaNetPortable.Share.ShareNative;

namespace PdaNetPortable.Share;

/// <summary>
/// Catches keys before Windows acts on them, while the window of the other PC is in front, so
/// shortcuts such as Alt+F4, Alt+Tab or the Windows key reach the other PC instead of this one.
/// A low level hook needs no admin. It is only installed while it is needed and the handler is
/// kept short, Windows drops hooks that take too long.
/// </summary>
internal sealed unsafe class KeyboardHook : IDisposable
{
    public readonly record struct Key(byte Vk, ushort Scan, bool Extended, bool Down, bool Alt);

    private readonly HookProc _proc;
    private readonly Func<Key, bool> _handler;
    private IntPtr _hook;

    /// <param name="handler">Gets each key, returns true to keep it from this PC.</param>
    public KeyboardHook(Func<Key, bool> handler)
    {
        _handler = handler;
        _proc = Proc; // kept in a field, so the collector never frees what Windows calls
    }

    public bool Active => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_hook == IntPtr.Zero)
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = (KBDLLHOOKSTRUCT*)lParam;
            // Keys PdaNet plays itself, when this PC also shares its screen, are not sent back.
            if (k->dwExtraInfo != InjectedTag)
            {
                int msg = (int)wParam;
                bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                try
                {
                    if (_handler(new Key((byte)k->vkCode, (ushort)k->scanCode, (k->flags & LLKHF_EXTENDED) != 0, down,
                            (k->flags & LLKHF_ALTDOWN) != 0)))
                        return 1;
                }
                catch { /* never let a key get lost to an error */ }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Stop();
}
