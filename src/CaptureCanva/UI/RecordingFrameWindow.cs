using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CaptureCanva.Capture;
using CaptureCanva.Interop;

namespace CaptureCanva.UI;

/// <summary>
/// Click-through red frame drawn just outside the recorded region. It is excluded from capture,
/// so it never shows up in the video.
/// </summary>
public sealed class RecordingFrameWindow : Window
{
    private const int FrameMargin = 3;
    private readonly PixelRect _screenRect;

    public RecordingFrameWindow(PixelRect screenRect)
    {
        _screenRect = screenRect;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
            BorderThickness = new Thickness(2),
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(hwnd, Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            Place(hwnd);
        };
        Loaded += (_, _) => Place(new WindowInteropHelper(this).Handle);
    }

    private void Place(IntPtr hwnd)
    {
        var r = _screenRect;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, r.X - FrameMargin, r.Y - FrameMargin,
            r.Width + FrameMargin * 2, r.Height + FrameMargin * 2, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }
}
