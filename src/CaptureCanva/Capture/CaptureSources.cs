using System.Diagnostics;
using System.Text;
using CaptureCanva.Interop;

namespace CaptureCanva.Capture;

public enum CaptureMode
{
    Monitor,
    Window,
    Region,
}

/// <summary>Rectangle in physical (device) pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public override string ToString() => $"{Width} × {Height}  ({X}, {Y})";
}

public sealed record MonitorInfo(IntPtr Handle, string DeviceName, PixelRect Bounds, bool IsPrimary, int Index)
{
    public string Display => $"모니터 {Index + 1}  —  {Bounds.Width} × {Bounds.Height}{(IsPrimary ? "  (기본)" : "")}";

    public static List<MonitorInfo> GetAll()
    {
        var result = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(hMonitor, ref info))
            {
                var r = info.rcMonitor;
                result.Add(new MonitorInfo(hMonitor, info.szDevice, new PixelRect(r.Left, r.Top, r.Width, r.Height),
                    (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0, result.Count));
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName)
{
    public string Display => string.IsNullOrEmpty(ProcessName) ? Title : $"{Title}  —  {ProcessName}";

    /// <summary>Top-level windows a user would recognise as "an app window" (same idea as Alt+Tab).</summary>
    public static List<WindowInfo> GetCapturable()
    {
        var result = new List<WindowInfo>();
        IntPtr shell = Native.GetShellWindow();
        int ownPid = Environment.ProcessId;

        Native.EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || !Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero)
                return true;

            long exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0 && (exStyle & Native.WS_EX_APPWINDOW) == 0)
                return true;

            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            int length = Native.GetWindowTextLength(hwnd);
            if (length == 0)
                return true;

            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownPid)
                return true;

            var title = new StringBuilder(length + 1);
            Native.GetWindowText(hwnd, title, title.Capacity);
            result.Add(new WindowInfo(hwnd, title.ToString(), GetProcessName(pid)));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static string GetProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>What to record. For <see cref="CaptureMode.Region"/>, <see cref="Handle"/> is the monitor and
/// <see cref="Crop"/> is relative to that monitor's top-left corner.</summary>
public sealed record CaptureTarget(CaptureMode Mode, IntPtr Handle, PixelRect? Crop, string Description);
