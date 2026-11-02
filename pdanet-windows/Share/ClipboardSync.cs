using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using PdaNetPortable.Services;
using Clipboard = System.Windows.Clipboard;

namespace PdaNetPortable.Share;

/// <summary>
/// Shares copied text between the two PCs: copy on one, paste on the other. Windows tells PdaNet
/// when the clipboard changes, nothing is polled. Text that just came from the other PC is not
/// sent back. Lives on the UI thread, which the clipboard needs.
/// </summary>
internal sealed class ClipboardSync : IDisposable
{
    public const int MaxChars = 512 * 1024;

    private readonly HwndSource _window;
    private string? _fromOther; // the last text the other PC sent, so it does not echo back
    private string? _sent;

    /// <summary>Text was copied on this PC.</summary>
    public event Action<string>? Copied;

    public ClipboardSync()
    {
        // A message only window: it has no screen presence, it only gets messages.
        _window = new HwndSource(new HwndSourceParameters("PdaNet clipboard")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);
        ShareNative.AddClipboardFormatListener(_window.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ShareNative.WM_CLIPBOARDUPDATE)
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, Read);
        return IntPtr.Zero;
    }

    private void Read()
    {
        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText();
        }
        catch (COMException) { return; } // another app holds the clipboard, its next change comes anyway
        if (string.IsNullOrEmpty(text) || text.Length > MaxChars || text == _fromOther || text == _sent) return;
        _sent = text;
        Copied?.Invoke(text);
    }

    /// <summary>Puts text from the other PC on this PC's clipboard, trying again while another app holds it.</summary>
    public void Apply(string text)
    {
        if (text.Length > MaxChars) return;
        _fromOther = text;
        Put(text, 0);
    }

    private void Put(string text, int attempt)
    {
        try { Clipboard.SetDataObject(text, true); }
        catch (COMException)
        {
            if (attempt >= 5)
            {
                Logger.Instance.Debug("Screen sharing could not reach the clipboard");
                return;
            }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Put(text, attempt + 1);
            };
            timer.Start();
        }
    }

    public void Dispose()
    {
        ShareNative.RemoveClipboardFormatListener(_window.Handle);
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
