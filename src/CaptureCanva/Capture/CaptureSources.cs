using System.Diagnostics;
using System.Runtime.InteropServices;
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

    /// <summary>Visible screen bounds in physical pixels — DWM extended frame bounds, i.e. without
    /// the invisible resize border that GetWindowRect includes.</summary>
    public static PixelRect? GetScreenRect(IntPtr hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT rect,
                Marshal.SizeOf<Native.RECT>()) != 0
            && !Native.GetWindowRect(hwnd, out rect))
            return null;
        return new PixelRect(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    /// <summary>
    /// Resolves a window into a screen region to record: the monitor showing the largest part of
    /// the window, with the window rect clipped to that monitor. Recording the region instead of
    /// the window texture keeps popups, menus and tooltips (separate HWNDs on top) in the video.
    /// </summary>
    public static (MonitorInfo Monitor, PixelRect ScreenRect, PixelRect MonitorRelative, bool Clipped)?
        ResolveCaptureRegion(IntPtr hwnd)
    {
        if (GetScreenRect(hwnd) is not { } window)
            return null;

        MonitorInfo? best = null;
        long bestArea = -1;
        foreach (var monitor in MonitorInfo.GetAll())
        {
            int left = Math.Max(window.X, monitor.Bounds.X);
            int top = Math.Max(window.Y, monitor.Bounds.Y);
            long area = (long)Math.Max(0, Math.Min(window.Right, monitor.Bounds.Right) - left)
                      * Math.Max(0, Math.Min(window.Bottom, monitor.Bounds.Bottom) - top);
            if (area > bestArea)
            {
                bestArea = area;
                best = monitor;
            }
        }
        if (best == null || bestArea <= 0)
            return null;

        int x = Math.Max(window.X, best.Bounds.X);
        int y = Math.Max(window.Y, best.Bounds.Y);
        int right = Math.Min(window.Right, best.Bounds.Right);
        int bottom = Math.Min(window.Bottom, best.Bounds.Bottom);
        var screen = new PixelRect(x, y, right - x, bottom - y);
        return (best, screen, screen with { X = screen.X - best.Bounds.X, Y = screen.Y - best.Bounds.Y },
            screen != window);
    }

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
