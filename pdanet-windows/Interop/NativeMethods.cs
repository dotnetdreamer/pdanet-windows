using System.Runtime.InteropServices;
using System.Text;

namespace PdaNetPortable.Interop;

internal static class NativeMethods
{
    // WinINET option refresh so proxy changes take effect immediately.
    public const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    public const int INTERNET_OPTION_REFRESH = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public static void NotifyProxyChanged()
    {
        try
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        catch { /* best effort */ }
    }

    // ---- IP Helper: which process owns a TCP connection (normal user rights are enough) ----
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf,
        int tableClass, uint reserved);

    /// <summary>
    /// Maps local port to process id for every IPv4 TCP connection on this PC whose remote port
    /// is <paramref name="remotePort"/>. For connections into our loopback relay that is the
    /// client app's side of each connection.
    /// </summary>
    public static Dictionary<int, int> TcpOwnersByLocalPort(int remotePort)
    {
        var map = new Dictionary<int, int>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
        for (int attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                uint rc = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
                if (rc == ERROR_INSUFFICIENT_BUFFER) continue; // the table grew, try with the new size
                if (rc != 0) return map;

                // MIB_TCPTABLE_OWNER_PID: a count, then rows of six DWORDs
                // (state, local addr, local port, remote addr, remote port, owning pid).
                int count = Marshal.ReadInt32(buf);
                const int rowSize = 24;
                for (int i = 0; i < count; i++)
                {
                    int row = 4 + i * rowSize;
                    if (NetPort(Marshal.ReadInt32(buf, row + 16)) != remotePort) continue;
                    map[NetPort(Marshal.ReadInt32(buf, row + 8))] = Marshal.ReadInt32(buf, row + 20);
                }
                return map;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return map;
    }

    // Ports sit in the low 16 bits in network byte order.
    private static int NetPort(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    // ---- Process image path, works for most processes without admin rights ----
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static string? ProcessImagePath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    // ---- Desktop Window Manager: dark non client area + rounded corners (Windows 10/11) ----
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// Makes the window's system drawn parts match the dark theme: a dark border and shadow,
    /// and rounded corners on Windows 11. All attributes are best effort and ignored on
    /// versions that do not support them.
    /// </summary>
    public static void StyleWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch { /* best effort */ }
    }
}
