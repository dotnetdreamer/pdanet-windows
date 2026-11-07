using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using PdaNetPortable.Share.Media;
using SharpGen.Runtime;

namespace PdaNetPortable.Views;

/// <summary>
/// A child window over the viewer's picture that the GPU presenter draws into, see
/// SwapChainPresenter. It takes no input at all: the mouse goes through it to the viewer's window,
/// where WPF finds the input layer under it as before, and it never takes the focus. A Win32
/// window always covers WPF's own drawing, so the viewer shows this one only while WPF draws
/// nothing over the picture.
/// </summary>
internal sealed class GpuPictureHost : HwndHost
{
    private const string ClassName = "PdaNetSharePicture";
    private static bool _registered;

    /// <summary>The presenter for this window, or null when the GPU cannot, see Failure.</summary>
    public SwapChainPresenter? Presenter { get; private set; }

    /// <summary>Why there is no presenter.</summary>
    public string? Failure { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        Register();
        var hwnd = CreateWindowEx(0, ClassName, "", WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 1, 1, parent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new Win32Exception();
        // Whatever goes wrong with the GPU, WPF draws the pictures: this runs inside WPF's
        // layout, where an exception would take the viewer's window down with it.
        try { Presenter = SwapChainPresenter.Create(hwnd); }
        catch (Exception e)
        {
            Failure = e is NotSupportedException or SharpGenException ? e.Message.Split('\n')[0] : $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
        }
        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // The swap chain goes first, it must never outlive its window.
        Presenter?.Dispose();
        Presenter = null;
        DestroyWindow(hwnd.Handle);
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                // Through to the window under it, the viewer's: WPF hit tests its own input layer.
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_ERASEBKGND:
                // The swap chain draws every pixel; erasing would only flash.
                handled = true;
                return new IntPtr(1);
            case WM_SIZE:
                Presenter?.Resized();
                break;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static void Register()
    {
        if (_registered) return;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            // Windows' own default handling: HwndHost puts its WndProc in front of it.
            lpfnWndProc = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "DefWindowProcW"),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS) throw new Win32Exception();
        _registered = true;
    }

    private const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    private const int WM_SIZE = 0x0005, WM_ERASEBKGND = 0x0014, WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021;
    private const int HTTRANSPARENT = -1, MA_NOACTIVATE = 3, ERROR_CLASS_ALREADY_EXISTS = 1410;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
