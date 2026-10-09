using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CaptureCanva.Capture;
using CaptureCanva.Interop;
using CaptureCanva.Recording;
using CaptureCanva.UI;

namespace CaptureCanva;

public partial class MainWindow : Window
{
    private const int HotkeyRecord = 1;
    private const int HotkeyPause = 2;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private string? _ffmpegPath;
    private VideoEncoder _encoder = VideoEncoder.X264;
    private RecordingSession? _session;
    private RecordingFrameWindow? _frameWindow;
    private RegionSelection? _region;
    private bool _busy;
    private bool _closeAfterStop;
    private bool _lagWarned;
    private readonly DispatcherTimer _windowWatch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private IntPtr _watchedWindow;
    private PixelRect _watchedRect;
    private PixelRect? _recordingFrameRect;
    private bool _windowClipped;
    private GifPreset? _recordingGifPreset;
    private CancellationTokenSource? _gifConversionCancellation;

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateTimer();
        _windowWatch.Tick += OnWindowWatchTick;
        Loaded += OnLoaded;
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(Handle).AddHook(WndProc);
        RegisterHotkeys();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplySettingsToControls();

        if (!ScreenCapturer.IsSupported)
        {
            StatusText.Text = "이 Windows 버전은 화면 캡처 API(Windows.Graphics.Capture)를 지원하지 않습니다.";
            return;
        }

        _ffmpegPath = Ffmpeg.FindExecutable();
        if (_ffmpegPath == null)
        {
            EncoderText.Text = "ffmpeg 없음";
            StatusText.Text = "ffmpeg.exe를 찾을 수 없습니다. CaptureCanva.exe와 같은 폴더에 두거나 PATH에 추가한 뒤 다시 실행하세요.";
            return;
        }

        _encoder = await Ffmpeg.DetectEncoderAsync(_ffmpegPath);
        EncoderText.Text = Ffmpeg.DisplayName(_encoder);
        RecordButton.IsEnabled = true;
    }

    // ───────────────────────── settings ↔ controls ─────────────────────────

    private void ApplySettingsToControls()
    {
        RefreshMonitors();
        RefreshWindows();

        var monitors = (List<MonitorInfo>)MonitorList.ItemsSource;
        MonitorList.SelectedItem = monitors.FirstOrDefault(m => m.DeviceName == _settings.MonitorDevice)
                                   ?? monitors.FirstOrDefault(m => m.IsPrimary)
                                   ?? monitors.FirstOrDefault();

        if (_settings.Region is { } savedRegion &&
            monitors.FirstOrDefault(m => m.DeviceName == _settings.RegionMonitorDevice) is { } regionMonitor)
        {
            var screenRect = savedRegion with { X = savedRegion.X + regionMonitor.Bounds.X, Y = savedRegion.Y + regionMonitor.Bounds.Y };
            if (screenRect.Right <= regionMonitor.Bounds.Right && screenRect.Bottom <= regionMonitor.Bounds.Bottom)
                SetRegion(new RegionSelection(regionMonitor, screenRect));
        }

        SelectByTag(FpsList, _settings.Fps.ToString());
        SelectByTag(QualityList, _settings.Quality.ToString());
        CursorCheck.IsChecked = _settings.CaptureCursor;
        SystemAudioCheck.IsChecked = _settings.SystemAudio;
        MicCheck.IsChecked = _settings.Microphone;
        HideSelfCheck.IsChecked = _settings.HideFromCapture;
        CreateGifCheck.IsChecked = _settings.CreateGif;
        SelectByTag(GifPresetList, (Enum.IsDefined(_settings.GifPreset) ? _settings.GifPreset : GifPreset.Standard).ToString());
        OutputDirText.Text = _settings.OutputDirectory;

        (_settings.Mode switch
        {
            CaptureMode.Window => ModeWindow,
            CaptureMode.Region => ModeRegion,
            _ => ModeMonitor,
        }).IsChecked = true;
    }

    private void SaveSettingsFromControls()
    {
        _settings.Mode = CurrentMode;
        _settings.MonitorDevice = (MonitorList.SelectedItem as MonitorInfo)?.DeviceName;
        if (_region != null)
        {
            _settings.RegionMonitorDevice = _region.Monitor.DeviceName;
            _settings.Region = _region.MonitorRelative;
        }
        _settings.Fps = SelectedFps;
        _settings.Quality = SelectedQuality;
        _settings.CaptureCursor = CursorCheck.IsChecked == true;
        _settings.SystemAudio = SystemAudioCheck.IsChecked == true;
        _settings.Microphone = MicCheck.IsChecked == true;
        _settings.HideFromCapture = HideSelfCheck.IsChecked == true;
        _settings.CreateGif = CreateGifCheck.IsChecked == true;
        _settings.GifPreset = SelectedGifPreset;
        _settings.OutputDirectory = OutputDirText.Text;
        _settings.Save();
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? combo.Items[^1];
    }

    private CaptureMode CurrentMode =>
        ModeWindow.IsChecked == true ? CaptureMode.Window :
        ModeRegion.IsChecked == true ? CaptureMode.Region : CaptureMode.Monitor;

    private int SelectedFps => int.Parse((string)((ComboBoxItem)FpsList.SelectedItem).Tag);

    private VideoQuality SelectedQuality => Enum.Parse<VideoQuality>((string)((ComboBoxItem)QualityList.SelectedItem).Tag);

    private GifPreset SelectedGifPreset => Enum.Parse<GifPreset>((string)((ComboBoxItem)GifPresetList.SelectedItem).Tag);

    private void OnCreateGifChanged(object sender, RoutedEventArgs e)
    {
        if (GifPresetList != null)
            GifPresetList.IsEnabled = CreateGifCheck.IsChecked == true && _session == null && !_busy;
    }

    // ───────────────────────── capture target UI ─────────────────────────

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (MonitorList == null)
            return; // still inside InitializeComponent
        MonitorList.Visibility = CurrentMode == CaptureMode.Monitor ? Visibility.Visible : Visibility.Collapsed;
        WindowPanel.Visibility = CurrentMode == CaptureMode.Window ? Visibility.Visible : Visibility.Collapsed;
        RegionPanel.Visibility = CurrentMode == CaptureMode.Region ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshMonitors()
    {
        MonitorList.ItemsSource = MonitorInfo.GetAll();
    }

    private void RefreshWindows()
    {
        var previous = (WindowList.SelectedItem as WindowInfo)?.Handle;
        var windows = WindowInfo.GetCapturable();
        WindowList.ItemsSource = windows;
        WindowList.SelectedItem = windows.FirstOrDefault(w => w.Handle == previous) ?? windows.FirstOrDefault();
    }

    private void OnWindowListOpened(object? sender, EventArgs e) => RefreshWindows();

    private void OnRefreshWindows(object sender, RoutedEventArgs e) => RefreshWindows();

    private async void OnSelectRegion(object sender, RoutedEventArgs e)
    {
        var selection = await RegionSelectorWindow.SelectAsync(HideSelfCheck.IsChecked == true);
        Activate();
        if (selection != null)
            SetRegion(selection);
    }

    private void SetRegion(RegionSelection selection)
    {
        _region = selection;
        RegionText.Text = $"{selection.ScreenRect.Width} × {selection.ScreenRect.Height}  ·  모니터 {selection.Monitor.Index + 1}  ({selection.MonitorRelative.X}, {selection.MonitorRelative.Y})";
    }

    private void OnHideSelfChanged(object sender, RoutedEventArgs e)
    {
        if (Handle != IntPtr.Zero)
            Native.SetWindowDisplayAffinity(Handle, HideSelfCheck.IsChecked == true ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }

    /// <summary>Builds the capture target from the UI, or returns null with a message in the status line.</summary>
    private CaptureTarget? BuildTarget()
    {
        switch (CurrentMode)
        {
            case CaptureMode.Window:
                if (WindowList.SelectedItem is not WindowInfo window || !Native.IsWindow(window.Handle))
                {
                    StatusText.Text = "녹화할 창을 선택하세요.";
                    RefreshWindows();
                    return null;
                }
                if (Native.IsIconic(window.Handle))
                {
                    StatusText.Text = "최소화된 창은 녹화할 수 없습니다. 창을 복원한 뒤 다시 시도하세요.";
                    return null;
                }
                // Record the window as a screen region: popups, menus and tooltips that open on
                // top of it live in the screen, so a region capture keeps them in the video while
                // a window-texture capture (CreateItemForWindow) would exclude them.
                if (WindowInfo.ResolveCaptureRegion(window.Handle) is not { } region)
                {
                    StatusText.Text = "창이 화면에 표시되지 않아 녹화할 수 없습니다.";
                    return null;
                }
                _watchedWindow = window.Handle;
                _watchedRect = region.ScreenRect;
                _recordingFrameRect = region.ScreenRect;
                _windowClipped = region.Clipped;
                return new CaptureTarget(CaptureMode.Region, region.Monitor.Handle, region.MonitorRelative, window.Title);

            case CaptureMode.Region:
                if (_region == null)
                {
                    StatusText.Text = "먼저 \"영역 선택…\"으로 녹화할 영역을 지정하세요.";
                    return null;
                }
                // Monitor handles can change after display reconfiguration; resolve by device name.
                var monitor = MonitorInfo.GetAll().FirstOrDefault(m => m.DeviceName == _region.Monitor.DeviceName);
                if (monitor == null || monitor.Bounds != _region.Monitor.Bounds)
                {
                    StatusText.Text = "모니터 구성이 바뀌었습니다. 영역을 다시 선택하세요.";
                    return null;
                }
                _watchedWindow = IntPtr.Zero;
                _windowClipped = false;
                _recordingFrameRect = _region.ScreenRect;
                return new CaptureTarget(CaptureMode.Region, monitor.Handle, _region.MonitorRelative, "영역");

            default:
                if (MonitorList.SelectedItem is not MonitorInfo selected)
                {
                    StatusText.Text = "녹화할 모니터를 선택하세요.";
                    return null;
                }
                var current = MonitorInfo.GetAll().FirstOrDefault(m => m.DeviceName == selected.DeviceName) ?? selected;
                _watchedWindow = IntPtr.Zero;
                _windowClipped = false;
                _recordingFrameRect = null;
                return new CaptureTarget(CaptureMode.Monitor, current.Handle, null, current.Display);
        }
    }

    // ───────────────────────── recording ─────────────────────────

    private async void OnRecordClick(object sender, RoutedEventArgs e) => await ToggleRecordingAsync();

    private void OnPauseClick(object sender, RoutedEventArgs e) => TogglePause();

    private async Task ToggleRecordingAsync()
    {
        if (_busy || _ffmpegPath == null)
            return;
        if (_session == null)
            await StartRecordingAsync();
        else
            await StopRecordingAsync(null);
    }

    private async Task StartRecordingAsync()
    {
        var target = BuildTarget();
        if (target == null)
            return;

        SaveSettingsFromControls();
        _recordingGifPreset = _settings.CreateGif ? _settings.GifPreset : null;
        var options = new RecordingOptions(target, _ffmpegPath!, _settings.OutputDirectory, _settings.Fps, _settings.Quality,
            _encoder, _settings.CaptureCursor, _settings.SystemAudio, _settings.Microphone);

        _busy = true;
        RecordButton.IsEnabled = false;
        StatusText.Text = "녹화 준비 중…";
        LastFileLink.Visibility = Visibility.Collapsed;
        LastGifFileLink.Visibility = Visibility.Collapsed;
        var session = new RecordingSession(options);
        session.Interrupted += message => Dispatcher.InvokeAsync(() => StopRecordingAsync(message));
        try
        {
            await session.StartAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "녹화를 시작하지 못했습니다: " + ex.Message;
            _busy = false;
            RecordButton.IsEnabled = true;
            return;
        }

        _session = session;
        _busy = false;
        _lagWarned = false;
        RecordButton.IsEnabled = true;
        if (_closeAfterStop)
        {
            // The window was closed while the recording was starting.
            await StopRecordingAsync(null);
            return;
        }
        if (_recordingFrameRect is { } frameRect)
        {
            _frameWindow = new RecordingFrameWindow(frameRect);
            _frameWindow.Show();
        }
        if (_watchedWindow != IntPtr.Zero)
            _windowWatch.Start();

        SetRecordingUi(true);
        string status = $"녹화 중 — {target.Description}  ({session.Width} × {session.Height}, {options.Fps} fps)";
        if (_windowClipped)
            status += "\n⚠ 창이 모니터 화면 밖으로 나가 있어 보이는 부분만 녹화됩니다.";
        StatusText.Text = status + string.Concat(session.Warnings.Select(w => "\n⚠ " + w));
    }

    private async Task StopRecordingAsync(string? reason)
    {
        var session = _session;
        if (session == null || _busy)
            return;

        _busy = true;
        _session = null;
        _windowWatch.Stop();
        _watchedWindow = IntPtr.Zero;
        _windowClipped = false;
        _recordingFrameRect = null;
        _frameWindow?.Close();
        _frameWindow = null;
        SetRecordingUi(false);
        RecordButton.IsEnabled = false;
        CreateGifCheck.IsEnabled = GifPresetList.IsEnabled = false;
        StatusText.Text = (reason != null ? reason + "\n" : "") + "파일 저장 중…";

        try
        {
            string path = await session.StopAsync();
            StatusText.Text = (reason != null ? reason + "\n" : "") + "저장 완료:";
            LastFileText.Text = path;
            LastFileLink.Visibility = Visibility.Visible;
            if (_recordingGifPreset is { } preset)
            {
                using var cancellation = new CancellationTokenSource();
                _gifConversionCancellation = cancellation;
                GifProgressPanel.Visibility = Visibility.Visible;
                CancelGifButton.IsEnabled = true;
                var progress = new Progress<string>(stage =>
                {
                    if (ReferenceEquals(_gifConversionCancellation, cancellation) && !cancellation.IsCancellationRequested)
                        StatusText.Text = (reason != null ? reason + "\n" : "") + "MP4 저장 완료. " + stage;
                });
                try
                {
                    string gifPath = await GifConverter.ConvertAsync(_ffmpegPath!, path, preset, progress, cancellation.Token);
                    LastGifFileText.Text = gifPath;
                    LastGifFileLink.Visibility = Visibility.Visible;
                    double sizeMb = new FileInfo(gifPath).Length / (1024.0 * 1024.0);
                    StatusText.Text = (reason != null ? reason + "\n" : "") + $"MP4·GIF 저장 완료 (GIF {sizeMb:F1} MB):";
                }
                catch (OperationCanceledException)
                {
                    StatusText.Text = (reason != null ? reason + "\n" : "") + "MP4 저장 완료. GIF 변환을 취소했습니다.";
                }
                catch (Exception ex)
                {
                    StatusText.Text = (reason != null ? reason + "\n" : "") + "MP4는 저장되었습니다. " + ex.Message;
                }
                finally
                {
                    _gifConversionCancellation = null;
                    GifProgressPanel.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            _recordingGifPreset = null;
            RecordButton.IsEnabled = true;
            CreateGifCheck.IsEnabled = true;
            GifPresetList.IsEnabled = CreateGifCheck.IsChecked == true;
        }

        if (_closeAfterStop)
            Close();
    }

    private void TogglePause()
    {
        if (_session == null)
            return;
        if (_session.IsPaused)
            _session.Resume();
        else
            _session.Pause();
        PauseButton.Content = _session.IsPaused ? "계속" : "일시정지";
        UpdateTimer();
    }

    /// <summary>
    /// Window mode records a fixed screen region. If the watched window closes, is minimized or
    /// moves, that region no longer shows the window — stop instead of recording the wrong thing.
    /// </summary>
    private void OnWindowWatchTick(object? sender, EventArgs e)
    {
        if (_session == null || _watchedWindow == IntPtr.Zero)
        {
            _windowWatch.Stop();
            return;
        }

        PixelRect? rect = Native.IsWindow(_watchedWindow) && !Native.IsIconic(_watchedWindow)
            ? WindowInfo.GetScreenRect(_watchedWindow)
            : null;
        if (rect == null)
        {
            _windowWatch.Stop();
            _ = StopRecordingAsync("녹화 대상 창이 닫히거나 최소화되어 녹화를 종료합니다.");
        }
        else if (rect.Value != _watchedRect)
        {
            _windowWatch.Stop();
            _ = StopRecordingAsync("녹화 대상 창이 이동되어 녹화를 종료합니다. 창을 고정한 뒤 다시 녹화하세요.");
        }
    }

    private void SetRecordingUi(bool recording)
    {
        RecordButton.Content = recording ? "■ 녹화 중지" : "● 녹화 시작";
        PauseButton.IsEnabled = recording;
        PauseButton.Content = "일시정지";
        ModeMonitor.IsEnabled = ModeWindow.IsEnabled = ModeRegion.IsEnabled = !recording;
        MonitorList.IsEnabled = WindowPanel.IsEnabled = RegionPanel.IsEnabled = !recording;
        FpsList.IsEnabled = QualityList.IsEnabled = !recording;
        CursorCheck.IsEnabled = SystemAudioCheck.IsEnabled = MicCheck.IsEnabled = !recording;
        CreateGifCheck.IsEnabled = !recording;
        GifPresetList.IsEnabled = !recording && CreateGifCheck.IsChecked == true;
        if (recording)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            RecDot.Opacity = 0.25;
        }
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        var elapsed = _session?.Elapsed ?? TimeSpan.Zero;
        TimerText.Text = elapsed.ToString(@"hh\:mm\:ss");
        if (_session == null)
            return;
        // Solid while paused, blinking while recording.
        RecDot.Opacity = _session.IsPaused ? 0.6 : (DateTime.Now.Millisecond < 500 ? 1 : 0.25);

        // The encoder can't keep up when it's more than a second behind: tell the user once.
        if (!_lagWarned && _session.FramesBehind > _session.Fps)
        {
            _lagWarned = true;
            StatusText.Text += "\n⚠ 인코더가 실시간을 따라가지 못하고 있습니다. 영상이 소리보다 늦어질 수 있으니 프레임(fps)이나 녹화 범위를 줄여 보세요.";
        }
    }

    // ───────────────────────── output folder ─────────────────────────

    private void OnChangeOutputDir(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = OutputDirText.Text, Title = "저장 폴더 선택" };
        if (dialog.ShowDialog(this) == true)
        {
            OutputDirText.Text = dialog.FolderName;
            SaveSettingsFromControls();
        }
    }

    private void OnOpenOutputDir(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(OutputDirText.Text);
        Process.Start(new ProcessStartInfo("explorer.exe", Ffmpeg.Quote(OutputDirText.Text)) { UseShellExecute = true });
    }

    private void OnOpenLastFile(object sender, RoutedEventArgs e)
    {
        // Select the file in Explorer.
        Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Ffmpeg.Quote(LastFileText.Text)) { UseShellExecute = true });
    }

    private void OnOpenLastGifFile(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Ffmpeg.Quote(LastGifFileText.Text)) { UseShellExecute = true });

    private void OnCancelGif(object sender, RoutedEventArgs e)
    {
        _gifConversionCancellation?.Cancel();
        CancelGifButton.IsEnabled = false;
        StatusText.Text = "MP4는 저장되었습니다. GIF 변환 취소 중…";
    }

    // ───────────────────────── hotkeys & lifetime ─────────────────────────

    private readonly record struct Hotkey(uint Modifiers, uint Vk, string Name);

    private void RegisterHotkeys()
    {
        const uint F9 = 0x78, F10 = 0x79, F12 = 0x7B;
        const uint Ctrl = Native.MOD_CONTROL, Shift = Native.MOD_SHIFT;

        // (record, pause) pairs in order of preference. F12 matches Bandicam; the others are
        // fallbacks for when another app already owns a key. A pair is used only if both register.
        var candidates = new (Hotkey Record, Hotkey Pause)[]
        {
            (new(0, F12, "F12"), new(Shift, F12, "Shift+F12")),
            (new(0, F9, "F9"), new(Shift, F9, "Shift+F9")),
            (new(0, F9, "F9"), new(Ctrl, F9, "Ctrl+F9")),
            (new(Ctrl | Shift, F9, "Ctrl+Shift+F9"), new(Ctrl | Shift, F10, "Ctrl+Shift+F10")),
        };

        foreach (var (record, pause) in candidates)
        {
            if (!Native.RegisterHotKey(Handle, HotkeyRecord, Native.MOD_NOREPEAT | record.Modifiers, record.Vk))
                continue;
            if (Native.RegisterHotKey(Handle, HotkeyPause, Native.MOD_NOREPEAT | pause.Modifiers, pause.Vk))
            {
                HotkeyText.Text = $"단축키: {record.Name} 녹화 시작/중지  ·  {pause.Name} 일시정지";
                return;
            }
            Native.UnregisterHotKey(Handle, HotkeyRecord);
        }

        // No complete pair is free: keep at least a start/stop key.
        foreach (var (record, _) in candidates)
        {
            if (Native.RegisterHotKey(Handle, HotkeyRecord, Native.MOD_NOREPEAT | record.Modifiers, record.Vk))
            {
                HotkeyText.Text = $"단축키: {record.Name} 녹화 시작/중지  ·  일시정지 단축키 없음 (다른 프로그램이 사용 중)";
                return;
            }
        }
        HotkeyText.Text = "단축키 등록 실패 (다른 프로그램이 사용 중)";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            handled = true;
            if (wParam.ToInt32() == HotkeyRecord)
                _ = ToggleRecordingAsync();
            else if (wParam.ToInt32() == HotkeyPause)
                TogglePause();
        }
        return IntPtr.Zero;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_session != null || _busy)
        {
            // Finish the file first, then close.
            e.Cancel = true;
            _closeAfterStop = true;
            await StopRecordingAsync(null);
            return;
        }
        SaveSettingsFromControls();
        Native.UnregisterHotKey(Handle, HotkeyRecord);
        Native.UnregisterHotKey(Handle, HotkeyPause);
        base.OnClosing(e);
    }
}
