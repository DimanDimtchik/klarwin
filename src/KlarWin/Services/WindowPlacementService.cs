using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KlarWin.Services;

public static class WindowPlacementService
{
    private const int SwRestore = 9;
    private const int SwShowMaximized = 3;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int GwOwner = 4;

    public static bool TryPlaceMainWindow(Process process, MonitorInfo monitor, bool maximize)
        => TryPlaceMainWindow(process, monitor, maximize, TimeSpan.FromSeconds(20), retries: 3);

    public static bool TryPlaceMainWindow(
        Process process,
        MonitorInfo monitor,
        bool maximize,
        TimeSpan timeout,
        int retries)
    {
        for (var attempt = 1; attempt <= Math.Max(1, retries); attempt++)
        {
            var hwnd = WaitForMainWindow(process, attempt == 1 ? timeout : TimeSpan.FromSeconds(6));
            if (hwnd == IntPtr.Zero)
            {
                Thread.Sleep(400 * attempt);
                continue;
            }

            if (PlaceHwnd(hwnd, monitor, maximize))
                return true;

            Thread.Sleep(350 * attempt);
        }

        // Last resort: any large visible window matching process name
        var fallback = FindBestVisibleWindowForProcessName(process.ProcessName);
        return fallback != IntPtr.Zero && PlaceHwnd(fallback, monitor, maximize);
    }

    private static bool PlaceHwnd(IntPtr hwnd, MonitorInfo monitor, bool maximize)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;

        ShowWindow(hwnd, SwRestore);
        Thread.Sleep(80);
        var margin = 8;
        var x = monitor.X + margin;
        var y = monitor.Y + margin;
        var w = Math.Max(400, monitor.Width - margin * 2);
        var h = Math.Max(300, monitor.Height - margin * 2);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SwpNoZOrder | SwpShowWindow);
        Thread.Sleep(80);
        if (maximize)
        {
            ShowWindow(hwnd, SwShowMaximized);
        }

        return true;
    }

    public static IntPtr WaitForMainWindow(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                process.Refresh();
                if (process.HasExited)
                {
                    // Process may have handed off to another instance
                    var handed = FindBestVisibleWindowForProcessName(process.ProcessName);
                    if (handed != IntPtr.Zero) return handed;
                    return IntPtr.Zero;
                }

                if (process.MainWindowHandle != IntPtr.Zero && IsUsableWindow(process.MainWindowHandle))
                    return process.MainWindowHandle;

                var hwnd = FindBestVisibleWindowForProcessName(process.ProcessName);
                if (hwnd != IntPtr.Zero) return hwnd;
            }
            catch
            {
                var hwnd = FindBestVisibleWindowForProcessName(process.ProcessName);
                if (hwnd != IntPtr.Zero) return hwnd;
            }

            Thread.Sleep(300);
        }

        return FindBestVisibleWindowForProcessName(process.ProcessName);
    }

    private static IntPtr FindBestVisibleWindowForProcessName(string processName)
    {
        IntPtr best = IntPtr.Zero;
        var bestArea = 0;
        EnumWindows((h, _) =>
        {
            if (!IsUsableWindow(h)) return true;
            if (NativeGetWindowThreadProcessId(h, out var pid) == 0) return true;
            try
            {
                using var p = Process.GetProcessById(unchecked((int)pid));
                if (!p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)) return true;
                if (!GetWindowRect(h, out var rect)) return true;
                var area = Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = h;
                }
            }
            catch
            {
                // ignore access denied
            }

            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static bool IsUsableWindow(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindow(h) || !IsWindowVisible(h)) return false;
        if (GetWindow(h, GwOwner) != IntPtr.Zero) return false; // owned dialogs/popups
        var ex = GetWindowLongPtr(h, GwlExStyle).ToInt64();
        if ((ex & WsExToolWindow) != 0) return false;
        var titleLen = NativeGetWindowTextLength(h);
        if (titleLen <= 0) return false;
        if (!GetWindowRect(h, out var rect)) return false;
        var w = rect.Right - rect.Left;
        var hgt = rect.Bottom - rect.Top;
        return w >= 200 && hgt >= 120;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int NativeGetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint NativeGetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
}
