using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CaptureCanva.Capture;
using CaptureCanva.Interop;

namespace CaptureCanva.UI;

public sealed record RegionSelection(MonitorInfo Monitor, PixelRect ScreenRect)
{
    /// <summary>The selection relative to the monitor's top-left corner.</summary>
    public PixelRect MonitorRelative => ScreenRect with { X = ScreenRect.X - Monitor.Bounds.X, Y = ScreenRect.Y - Monitor.Bounds.Y };
}

/// <summary>
/// Full-screen dimmed overlay (one per monitor) where the user drags out the recording area.
/// Coordinates are tracked in physical pixels (PointToScreen) so mixed-DPI setups stay exact.
/// </summary>
public sealed class RegionSelectorWindow : Window
{
    private const int MinSize = 16;

    private readonly MonitorInfo _monitor;
    private readonly Action<RegionSelection?> _complete;
    private readonly System.Windows.Shapes.Path _dim;
    private readonly System.Windows.Shapes.Rectangle _selection;
    private readonly Border _label;
    private readonly TextBlock _labelText;
    private Native.POINT? _start;

    private RegionSelectorWindow(MonitorInfo monitor, Action<RegionSelection?> complete)
    {
        _monitor = monitor;
        _complete = complete;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // hit-testable but invisible
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;

        _dim = new System.Windows.Shapes.Path { Fill = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)) };
        _selection = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
        };
        _labelText = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Consolas") };
        _label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            Child = _labelText,
            Visibility = Visibility.Collapsed,
        };
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 40, 0, 0),
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 14,
                Text = "?쒕옒洹명빐???뱁솕???곸뿭???좏깮?섏꽭?? 쨌  Esc 痍⑥냼",
            },
        };

        var canvas = new Canvas();
        canvas.Children.Add(_dim);
        canvas.Children.Add(_selection);
        canvas.Children.Add(_label);
        Content = new Grid { Children = { canvas, hint } };

        SourceInitialized += (_, _) => PlaceOnMonitor();
        Loaded += (_, _) =>
        {
            PlaceOnMonitor(); // again, in case a DPI change resized us
            UpdateVisuals(null);
            Activate();
            Keyboard.Focus(this);
        };
    }

    /// <summary>Shows an overlay on every monitor and returns the selection, or null if cancelled.</summary>
    public static Task<RegionSelection?> SelectAsync(bool hideFromCapture)
    {
        var tcs = new TaskCompletionSource<RegionSelection?>();
        var windows = new List<RegionSelectorWindow>();

        void Complete(RegionSelection? result)
        {
            if (tcs.Task.IsCompleted)
                return;
            foreach (var w in windows) w.Close();
            tcs.TrySetResult(result);
        }

        foreach (var monitor in MonitorInfo.GetAll())
        {
            var window = new RegionSelectorWindow(monitor, Complete);
            if (hideFromCapture)
                window.SourceInitialized += (_, _) =>
                    Native.SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            windows.Add(window);
        }
        foreach (var w in windows) w.Show();
        return tcs.Task;
    }

    private void PlaceOnMonitor()
    {
        var b = _monitor.Bounds;
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_SHOWWINDOW);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            _complete(null);
        base.OnKeyDown(e);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) => _complete(null);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // Use the position carried by the event, not the cursor position when the handler runs:
        // a fast drag would otherwise shift the starting corner.
        _start = ScreenPoint(e);
        CaptureMouse();
        UpdateVisuals(CurrentRect(_start.Value));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_start != null)
            UpdateVisuals(CurrentRect(ScreenPoint(e)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_start == null)
            return;
        ReleaseMouseCapture();
        var rect = CurrentRect(ScreenPoint(e));
        _start = null;
        if (rect.Width < MinSize || rect.Height < MinSize)
        {
            UpdateVisuals(null);
            return;
        }
        _complete(new RegionSelection(_monitor, rect));
    }

    /// <summary>Mouse position of the event in physical screen pixels.</summary>
    private Native.POINT ScreenPoint(MouseEventArgs e)
    {
        var p = PointToScreen(e.GetPosition(this));
        return new Native.POINT { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) };
    }

    /// <summary>Drag rectangle from the start point to <paramref name="p"/> in physical screen pixels,
    /// clamped to this monitor, even-sized.</summary>
    private PixelRect CurrentRect(Native.POINT p)
    {
        var b = _monitor.Bounds;
        int x1 = Math.Clamp(Math.Min(_start!.Value.X, p.X), b.X, b.Right);
        int y1 = Math.Clamp(Math.Min(_start.Value.Y, p.Y), b.Y, b.Bottom);
        int x2 = Math.Clamp(Math.Max(_start.Value.X, p.X), b.X, b.Right);
        int y2 = Math.Clamp(Math.Max(_start.Value.Y, p.Y), b.Y, b.Bottom);
        return new PixelRect(x1, y1, (x2 - x1) & ~1, (y2 - y1) & ~1);
    }

    private void UpdateVisuals(PixelRect? rect)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        if (rect is not { } r || r.Width == 0 || r.Height == 0)
        {
            _dim.Data = new RectangleGeometry(full);
            _selection.Visibility = Visibility.Collapsed;
            _label.Visibility = Visibility.Collapsed;
            return;
        }

        var topLeft = PointFromScreen(new Point(r.X, r.Y));
        var bottomRight = PointFromScreen(new Point(r.Right, r.Bottom));
        var local = new Rect(topLeft, bottomRight);

        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(local));
        _selection.Visibility = Visibility.Visible;
        Canvas.SetLeft(_selection, local.X);
        Canvas.SetTop(_selection, local.Y);
        _selection.Width = local.Width;
        _selection.Height = local.Height;

        _labelText.Text = string.Create(CultureInfo.InvariantCulture, $"{r.Width} 횞 {r.Height}");
        _label.Visibility = Visibility.Visible;
        Canvas.SetLeft(_label, local.X);
        Canvas.SetTop(_label, local.Y >= 30 ? local.Y - 28 : local.Bottom + 6);
    }
}
