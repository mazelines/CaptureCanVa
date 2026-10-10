using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CaptureCanva;
using CaptureCanva.Capture;
using CaptureCanva.Preferences;
using CaptureCanva.Recording;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _passed;
    [STAThread]
    private static int Main(string[] args)
    {
        string directory = Path.Combine(Path.GetTempPath(), "CaptureCanva-UIChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        AppSettings.DirectoryOverride = HotkeySettings.DirectoryOverride = directory;
        App.LogDirectoryOverride = Path.Combine(directory, "logs");
        // Share the production styles without the production StartupUri or startup hooks.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        app.Resources = new ResourceDictionary { Source = new Uri("/CaptureCanva;component/UI/Styles.xaml", UriKind.Relative) };
        MainWindow? main = null;
        SettingsWindow? dialog = null;
        try
        {
            File.WriteAllText(AppSettings.FilePath, "{\"OutputDirectory\":\"\",\"UploadYouTube\":true,\"UploadDrive\":true}");
            HotkeySettings.Save(new HotkeySettings()); // Never register test global shortcuts.
            main = new MainWindow();
            Call(main, "ApplySettingsToControls");
            Check(Control<TextBox>(main, "OutputDirText").Text == AppSettings.DefaultOutputDirectory, "empty folder shows Videos/CaptureCanva");
            Check(Control<CheckBox>(main, "CreateGifCheck").IsChecked == true, "automatic GIF starts enabled");
            Check(main.FindName("UploadYouTubeCheck") == null && main.FindName("UploadDriveCheck") == null, "both upload controls are removed");
            Check(!typeof(MainWindow).Assembly.GetTypes().Any(t => t.Name is "GoogleOAuthClient" or "YouTubeUploader" or "GoogleDriveUploader" or "TokenStore"), "production assembly contains no Google integration");
            var record = new HotkeySetting(0, 0x7b, "F12");
            var pause = new HotkeySetting(4, 0x7b, "Shift+F12");
            int applied = 0;
            dialog = new SettingsWindow(record, pause, (_, _) => { applied++; return "conflict"; });
            Check(dialog.FindName("YouTubeConnectButton") == null && dialog.FindName("DriveSection") == null, "preferences contain only local settings");
            Check(Control<TextBox>(dialog, "RecordHotkeyBox").Text == "F12" && Control<TextBox>(dialog, "PauseHotkeyBox").Text == "Shift+F12", "preferences show active shortcuts");
            Call(dialog, "OnClearRecordHotkey", dialog, new RoutedEventArgs());
            Call(dialog, "OnApplyHotkeys", dialog, new RoutedEventArgs());
            Check(applied == 1 && Control<TextBlock>(dialog, "HotkeyStatusText").Text.Contains("conflict"), "shortcut conflict is shown inline");
            Check(Control<Button>(dialog, "HotkeyApplyButton").IsEnabled, "failed shortcut change can be retried");
            var cleared = new SettingsWindow(record, record);
            Call(cleared, "OnClearPauseHotkey", cleared, new RoutedEventArgs());
            Call(cleared, "OnApplyHotkeys", cleared, new RoutedEventArgs());
            Check(cleared.PendingPauseHotkey == null && !Control<Button>(cleared, "HotkeyApplyButton").IsEnabled, "cleared shortcut applies successfully");
            cleared.Close();
            Set(main, "_ffmpegPath", "not-used-by-this-check");
            Set(main, "_busy", true);
            Call(main, "SetRecordingUi", false);
            Check(!Control<Button>(main, "RecordButton").IsEnabled && !Control<WrapPanel>(main, "OutputActionsPanel").IsEnabled, "conversion freezes recording and folder/settings actions");
            Check(!Control<CheckBox>(main, "HideSelfCheck").IsEnabled && !Control<CheckBox>(main, "CreateGifCheck").IsEnabled, "conversion freezes recording options");
            Set(main, "_busy", false);
            Call(main, "SetRecordingUi", true);
            Check(Control<Button>(main, "RecordButton").IsEnabled && !Control<WrapPanel>(main, "OutputActionsPanel").IsEnabled, "active recording permits stop and freezes options");
            Call(main, "SetRecordingUi", false);
            Check(Control<WrapPanel>(main, "OutputActionsPanel").IsEnabled, "idle restores settings actions");
            string blockedFolder = Path.Combine(directory, "this-is-a-file");
            File.WriteAllText(blockedFolder, "keep me");
            // Zero HWND cannot be captured. Directory creation must fail before capture starts.
            Control<ComboBox>(main, "MonitorList").ItemsSource = new List<MonitorInfo> { new(IntPtr.Zero, "synthetic", new PixelRect(0, 0, 128, 72), true, 0) };
            Control<ComboBox>(main, "MonitorList").SelectedIndex = 0;
            Control<TextBox>(main, "OutputDirText").Text = blockedFolder;
            Await((Task)Call(main, "StartRecordingAsync")!);
            Check(Control<TextBlock>(main, "StatusText").Text.Contains("저장 폴더"), "invalid output folder stays inside the recording error flow");
            Check(Control<Button>(main, "RecordButton").IsEnabled && Control<WrapPanel>(main, "OutputActionsPanel").IsEnabled, "failed start restores controls");
            Check(File.ReadAllText(blockedFolder) == "keep me", "invalid folder preserves existing file");
            Control<TextBox>(main, "OutputDirText").Text = "";
            Call(main, "SaveSettingsFromControls");
            Check(AppSettings.Load().OutputDirectory == AppSettings.DefaultOutputDirectory, "empty folder persists the default");
            Control<Run>(main, "LastFileText").Text = Path.Combine(directory, "missing.mp4");
            Call(main, "OnOpenLastFile", main, new RoutedEventArgs());
            Check(Control<TextBlock>(main, "StatusText").Text.Contains("찾을 수 없습니다"), "missing result file gives useful feedback");
            using (var cancellation = new CancellationTokenSource())
            {
                Set(main, "_busy", true);
                Set(main, "_gifConversionCancellation", cancellation);
                var closing = new CancelEventArgs();
                Call(main, "OnClosing", closing);
                Check(closing.Cancel && cancellation.IsCancellationRequested, "close cancels GIF and defers window shutdown");
                Set(main, "_busy", false);
                Set(main, "_closeAfterStop", false);
                Set(main, "_gifConversionCancellation", null);
            }
            Control<TextBox>(main, "OutputDirText").Text = directory;
            Call(main, "SaveSettingsFromControls");
            main.SizeToContent = SizeToContent.Manual;
            main.Height = 750;
            main.Show();
            main.UpdateLayout();
            Snapshot(main, Path.Combine(directory, "main-normal.png"));
            main.Height = 480;
            main.UpdateLayout();
            var scroll = Control<ScrollViewer>(main, "SettingsScrollViewer");
            Check(scroll.ScrollableHeight > 0, "small window scrolls instead of clipping controls");
            var banner = Control<Button>(main, "MazelineBanner");
            var point = banner.TranslatePoint(new Point(), (UIElement)main.Content);
            Check(point.Y >= 0 && point.Y + banner.ActualHeight <= ((FrameworkElement)main.Content).ActualHeight + 1, "banner stays fully visible on a small window");
            scroll.ScrollToBottom();
            main.UpdateLayout();
            Snapshot(main, Path.Combine(directory, "main-small.png"));
            dialog.Show();
            dialog.UpdateLayout();
            Snapshot(dialog, Path.Combine(directory, "preferences.png"));
            dialog.Close();
            if (args.Length == 2 && args[0] == "--native")
                Await(main.Dispatcher.Invoke(() => CheckNativeRecordingAsync(main, Path.GetFullPath(args[1]), directory)));
            Console.WriteLine($"All {_passed} UI checks passed. Artifacts: {directory}");
            if (args.Length == 1 && args[0] == "--preview")
            {
                main.Height = 750;
                scroll.ScrollToTop();
                main.Closed += (_, _) => app.Shutdown();
                app.Run(main);
            }
            return 0;
        }
        finally
        {
            dialog?.Close();
            main?.Close();
            app.Shutdown();
            AppSettings.DirectoryOverride = HotkeySettings.DirectoryOverride = App.LogDirectoryOverride = null;
        }
    }

    private static async Task CheckNativeRecordingAsync(MainWindow main, string ffmpeg, string directory)
    {
        // Capture only this owned test window. No desktop, user windows or audio.
        var surface = new Window { Title = "CaptureCanva · 녹화 검증 창", Width = 360, Height = 260,
            Content = new TextBlock { Text = "CaptureCanva\nMP4 + GIF 검증", FontSize = 30, Padding = new Thickness(24), Foreground = Brushes.White,
                Background = new LinearGradientBrush(Colors.DarkBlue, Colors.Crimson, 45) } };
        surface.Show();
        try
        {
            VideoEncoder encoder = await Ffmpeg.DetectEncoderAsync(ffmpeg);
            Console.WriteLine("Native recording encoder: " + Ffmpeg.DisplayName(encoder));
            var target = new CaptureTarget(CaptureMode.Window, new WindowInteropHelper(surface).Handle, null, "isolated test window");
            var session = new RecordingSession(new RecordingOptions(target, ffmpeg, directory, 60, VideoQuality.High, encoder, false, false, false));
            await session.StartAsync();
            await Task.Delay(650);
            session.Pause();
            var pausedAt = session.Elapsed;
            await Task.Delay(200);
            Check(session.Elapsed == pausedAt && session.IsPaused, "native pause freezes elapsed time");
            session.Resume();
            await Task.Delay(650);
            Set(main, "_ffmpegPath", ffmpeg);
            Set(main, "_session", session);
            Set(main, "_recordingGifPreset", GifPreset.Standard);
            Call(main, "SetRecordingUi", true);
            await (Task)Call(main, "StopRecordingAsync", new object?[] { null })!;
            string mp4 = Control<Run>(main, "LastFileText").Text;
            string gif = Control<Run>(main, "LastGifFileText").Text;
            Check(File.Exists(mp4) && File.Exists(gif), "real window recording completes MP4 and automatic GIF in main UI");
            Check(Control<TextBlock>(main, "StatusText").Text.Contains("MP4·GIF 저장 완료"), "main UI confirms MP4 and GIF success");
            Check(Control<TextBlock>(main, "LastFileLink").Visibility == Visibility.Visible && Control<TextBlock>(main, "LastGifFileLink").Visibility == Visibility.Visible, "both result links are available");
            var decoded = await Ffmpeg.RunAsync(ffmpeg, $"-hide_banner -loglevel error -i {Ffmpeg.Quote(mp4)} -f null NUL", TimeSpan.FromSeconds(20));
            Check(decoded.ExitCode == 0, "native MP4 decodes without errors");
            decoded = await Ffmpeg.RunAsync(ffmpeg, $"-hide_banner -loglevel error -ignore_loop 1 -i {Ffmpeg.Quote(gif)} -f null NUL", TimeSpan.FromSeconds(20));
            Check(decoded.ExitCode == 0, "native GIF decodes without errors");
            Check(Directory.GetFiles(directory, "*.part.mkv").Length == 0 && Directory.GetFiles(directory, ".gif-*").Length == 0, "native recording cleans temporary files");
            main.Height = 750;
            Control<ScrollViewer>(main, "SettingsScrollViewer").ScrollToBottom();
            main.UpdateLayout();
            Snapshot(main, Path.Combine(directory, "recording-completed.png"));
            byte[] previousGif = File.ReadAllBytes(gif);
            Set(main, "_ffmpegPath", Path.Combine(directory, "missing-ffmpeg.exe"));
            await (Task)Call(main, "ConvertGifAsync", mp4, GifPreset.Standard, null)!;
            Check(Control<Button>(main, "RetryGifButton").Visibility == Visibility.Visible && previousGif.SequenceEqual(File.ReadAllBytes(gif)), "GIF failure offers retry and preserves existing GIF");
            Check(Directory.GetFiles(App.LogDirectory, "*.log").Any(p => File.ReadAllText(p).Contains("GIF conversion failed")), "conversion failure is logged");
            Set(main, "_ffmpegPath", ffmpeg);
            Call(main, "OnRetryGif", main, new RoutedEventArgs());
            while ((bool)typeof(MainWindow).GetField("_busy", Private)!.GetValue(main)!) await Task.Delay(25);
            Check(Control<TextBlock>(main, "StatusText").Text.Contains("MP4·GIF 저장 완료") && Control<Button>(main, "RetryGifButton").Visibility == Visibility.Collapsed, "GIF retry succeeds through actual click handler");
        }
        finally { surface.Close(); }
    }

    private static void Await(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => Thread.Sleep(5)));
        if (!task.IsCompleted) throw new TimeoutException("UI check exceeded 45 seconds.");
        task.GetAwaiter().GetResult();
    }
    private static object? Call(object target, string name, params object?[] args) => Dispatcher.CurrentDispatcher.Invoke(() => target.GetType().GetMethod(name, Private)!.Invoke(target, args));
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static T Control<T>(Window window, string name) => (T)window.FindName(name);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _passed++;
        Console.WriteLine("PASS " + message);
    }
    private static void Snapshot(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(SystemColors.WindowBrush, null, new Rect(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
