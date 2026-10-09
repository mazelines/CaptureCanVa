using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CaptureCanva.Capture;
using CaptureCanva.Accounts;
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
    private CancellationTokenSource? _uploadCancellation;

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
        UploadYouTubeCheck.IsChecked = _settings.UploadYouTube;
        // Drive is feature-gated off: never restore the stale persisted preference onto the
        // hidden checkbox, so a saved UploadDrive=true cannot silently activate the hidden path.
        UploadDriveCheck.IsChecked = _settings.UploadDrive && DriveFeatureGate.Enabled;
        YouTubePrivateRadio.IsChecked = !_settings.YouTubePublic;
        RefreshUploadUi();


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
        _settings.UploadYouTube = UploadYouTubeCheck.IsChecked == true;
        // While Drive is gated off, persist false to heal any stale saved true.
        _settings.UploadDrive = UploadDriveCheck.IsChecked == true && DriveFeatureGate.Enabled;
        _settings.YouTubePublic = YouTubePrivateRadio.IsChecked != true;
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

            if (!_closeAfterStop && (UploadYouTubeCheck.IsChecked == true
                || (UploadDriveCheck.IsChecked == true && DriveFeatureGate.Enabled)))
            {
                string baseStatus = StatusText.Text;
                await UploadRecordingAsync(path, baseStatus);
            }
        }
        catch (OperationCanceledException)
        {
            // Uploads were cancelled; the local MP4/GIF files are unaffected.
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
        UploadYouTubeCheck.IsEnabled = UploadPrivacyPanel.IsEnabled = !recording;
        // Feature gate: the hidden Drive checkbox never becomes interactive.
        UploadDriveCheck.IsEnabled = !recording && DriveFeatureGate.Enabled;
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

    // ───────────────────────── accounts & upload ─────────────────────────

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        // The settings window sees the CURRENTLY ACTIVE pair (which matches the saved file after
        // apply, and the effective defaults before the first apply), so display and reality agree.
        var window = new SettingsWindow(GoogleClientConfig.ClientId, GoogleClientConfig.ClientSecret,
            _activeRecordHotkey, _activePauseHotkey,
            tryApply: TryApplyHotkeys,
            setSuspended: _ => { })
        { Owner = this };
        _hotkeysSuspended = true;
        Native.UnregisterHotKey(Handle, HotkeyRecord);
        Native.UnregisterHotKey(Handle, HotkeyPause);
        try { window.ShowDialog(); }
        finally
        {
            _hotkeysSuspended = false;
            Native.UnregisterHotKey(Handle, HotkeyRecord);
            Native.UnregisterHotKey(Handle, HotkeyPause);
            if (!TryRegisterPair(_activeRecordHotkey, _activePauseHotkey)) RegisterHotkeys();
        }
        RefreshUploadUi();
    }

    /// <summary>Upload checkboxes only offer services whose account is actually connected.
    /// Drive is feature-gated: while hidden, its checkbox and token file are never touched, so
    /// a locked stale Drive token cannot surface errors in the YouTube-only UI.</summary>
    private void RefreshUploadUi()
    {
        var errors = new List<string>();
        bool Available(GoogleService service, string scope)
        {
            if (GoogleClientConfig.ClientId == null) return false;
            try { return TokenStore.Load(service) is { } tokens && GoogleOAuthClient.HasScope(tokens, scope); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"[{service}] 토큰 파일을 읽을 수 없습니다: {ex.Message}");
                return false;
            }
        }
        bool youtube = Available(GoogleService.YouTube, GoogleOAuthClient.ScopeYouTube);
        UploadYouTubeCheck.IsEnabled = youtube;
        if (!youtube) UploadYouTubeCheck.IsChecked = false;

        if (DriveFeatureGate.Enabled)
        {
            bool drive = Available(GoogleService.Drive, GoogleOAuthClient.ScopeDrive);
            UploadDriveCheck.IsEnabled = drive;
            if (!drive) UploadDriveCheck.IsChecked = false;
        }
        else
        {
            // Gate off: the checkbox may be Collapsed in XAML but its default IsEnabled is true,
            // so disable and uncheck it explicitly.
            UploadDriveCheck.IsEnabled = false;
            UploadDriveCheck.IsChecked = false;
        }
        // Gate off: leave the hidden checkbox exactly as XAML made it (Collapsed, unchecked).

        if (errors.Count > 0) StatusText.Text = string.Join("\n", errors);
    }

    /// <summary>
    /// Uploads the finished MP4 to every service the user ticked. Per-service errors are
    /// reported inline; one failing service never blocks the other.
    /// </summary>
    private async Task UploadRecordingAsync(string videoPath, string baseStatus)
    {
        string? clientId = GoogleClientConfig.ClientId;
        if (clientId == null)
            return;

        var progress = new Progress<string>(stage =>
        {
            if (_uploadCancellation is { IsCancellationRequested: false })
                StatusText.Text = "저장 완료. " + stage;
        });
        var cancellation = new CancellationTokenSource();
        _uploadCancellation = cancellation;
        CancelUploadButton.IsEnabled = true;
        var jobs = new List<(string Service, Func<GoogleOAuthClient, Task<UploadResult>> Run)>();
        if (UploadYouTubeCheck.IsChecked == true)
        {
            bool isPublic = YouTubePrivateRadio.IsChecked != true;
            string title = Path.GetFileNameWithoutExtension(videoPath);
            jobs.Add(("YouTube", async oauth =>
            {
                var tokens = TokenStore.Load(GoogleService.YouTube);
                if (tokens == null || !GoogleOAuthClient.HasScope(tokens, GoogleOAuthClient.ScopeYouTube))
                    throw new InvalidOperationException("YouTube 업로드 권한이 없습니다. 환경설정에서 다시 연결해 주세요.");
                using var uploader = new YouTubeUploader(tokens);
                return await uploader.UploadAsync(oauth, videoPath, title, "CaptureCanva로 녹화했습니다.",
                    isPublic, progress, cancellation.Token);
            }));
        }
        // Last-line-of-defense: even a stale checked hidden checkbox cannot create a Drive job
        // while the feature gate is off (recording must never touch a hidden integration).
        if (UploadDriveCheck.IsChecked == true && DriveFeatureGate.Enabled)
        {
            jobs.Add(("Google Drive", async oauth =>
            {
                var tokens = TokenStore.Load(GoogleService.Drive);
                if (tokens == null || !GoogleOAuthClient.HasScope(tokens, GoogleOAuthClient.ScopeDrive))
                    throw new InvalidOperationException("Google Drive 업로드 권한이 없습니다. 환경설정에서 다시 연결해 주세요.");
                using var uploader = new GoogleDriveUploader(tokens);
                return await uploader.UploadAsync(oauth, videoPath, null, false, progress, cancellation.Token);
            }));
        }
        if (jobs.Count == 0)
        {
            _uploadCancellation = null;
            cancellation.Dispose();
            return;
        }

        var lines = new List<string>();
        var links = new List<(string Service, string Url)>();
        UploadCancelPanel.Visibility = Visibility.Visible;
        try
        {
            foreach (var (service, run) in jobs)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    using var oauth = new GoogleOAuthClient(clientId, GoogleClientConfig.ClientSecret);
                    UploadResult result = await run(oauth);
                    links.Add((service, result.Url));
                    lines.Add($"[{service}] 업로드 완료 — 아래 링크를 클릭해 열 수 있습니다.");
                }
                catch (OperationCanceledException)
                {
                    lines.Add($"[{service}] 업로드 취소됨");
                    break; // user asked to stop; do not start the remaining service
                }
                catch (Exception ex)
                {
                    lines.Add($"[{service}] 업로드 실패: {ex.Message}");
                }
            }
        }
        finally
        {
            _uploadCancellation = null;
            UploadCancelPanel.Visibility = Visibility.Collapsed;
            CancelUploadButton.IsEnabled = true;
            cancellation.Dispose();
        }

        // A run can fail because the grant was lost mid-flight (token narrowed/removed after the
        // checkboxes were enabled). Re-evaluate availability now so the UI never keeps offering a
        // service whose saved token can no longer upload.
        RefreshUploadUi();

        // Completed uploads render as real, openable hyperlinks (P3: a bare string is not a link).
        if (links.Count > 0)
        {
            UploadLink2Panel.Visibility = Visibility.Collapsed;
            UploadLink1Text.Text = links[0].Url;
            if (links.Count > 1)
            {
                UploadLink2Panel.Visibility = Visibility.Visible;
                UploadLink2Text.Text = links[1].Url;
            }
            UploadLinksText.Visibility = Visibility.Visible;
        }
        if (lines.Count > 0)
            StatusText.Text = baseStatus + "\n" + string.Join("\n", lines);
    }

    private void OnOpenUploadLink(object sender, RoutedEventArgs e)
    {
        string? url = ReferenceEquals(sender, UploadLink1) ? UploadLink1Text.Text : UploadLink2Text.Text;
        if (string.IsNullOrEmpty(url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "링크를 열 수 없습니다.\n" + url, "CaptureCanva",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnCancelUpload(object sender, RoutedEventArgs e)
    {
        _uploadCancellation?.Cancel();
        CancelUploadButton.IsEnabled = false;
        StatusText.Text = "업로드 취소 중…";
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

    private void OnOpenMazelineWebsite(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.mazeline.tech/") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "홈페이지를 열 수 없습니다.\n" + ex.Message, "메이즈라인",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnCancelGif(object sender, RoutedEventArgs e)
    {
        _gifConversionCancellation?.Cancel();
        CancelGifButton.IsEnabled = false;
        StatusText.Text = "MP4는 저장되었습니다. GIF 변환 취소 중…";
    }

    // ───────────────────────── hotkeys & lifetime ─────────────────────────

    private readonly record struct Hotkey(uint Modifiers, uint Vk, string Name);

    private bool _hotkeysSuspended; // true while the settings window captures a new key

    /// <summary>Currently active pair; null = nothing registered for that slot.</summary>
    private HotkeySetting? _activeRecordHotkey;
    private HotkeySetting? _activePauseHotkey;

    /// <summary>
    /// Registers the user's saved pair when present, otherwise the built-in defaults. A saved
    /// pair that collides with another program falls back to the defaults so the app is never
    /// left without a start key. "없음" (cleared in settings) persists as no registration.
    /// </summary>
    private void RegisterHotkeys()
    {
        Native.UnregisterHotKey(Handle, HotkeyRecord);
        Native.UnregisterHotKey(Handle, HotkeyPause);
        _activeRecordHotkey = _activePauseHotkey = null;

        if (HotkeySettings.TryLoad(out var saved) && TryRegisterPair(saved.Record, saved.Pause))
            return;

        // Saved pair failed (OS conflict) → fall back to the built-in candidate list.
        const uint F9 = 0x78, F10 = 0x79, F12 = 0x7B;
        const uint Ctrl = Native.MOD_CONTROL, Shift = Native.MOD_SHIFT;
        var candidates = new (Hotkey Record, Hotkey Pause)[]
        {
            (new(0, F12, "F12"), new(Shift, F12, "Shift+F12")),
            (new(0, F9, "F9"), new(Shift, F9, "Shift+F9")),
            (new(0, F9, "F9"), new(Ctrl, F9, "Ctrl+F9")),
            (new(Ctrl | Shift, F9, "Ctrl+Shift+F9"), new(Ctrl | Shift, F10, "Ctrl+Shift+F10")),
        };
        foreach (var (record, pause) in candidates)
        {
            if (TryRegisterPair(
                    new HotkeySetting(record.Modifiers, record.Vk, record.Name),
                    new HotkeySetting(pause.Modifiers, pause.Vk, pause.Name)))
                return;
        }
        foreach (var (record, _) in candidates)
        {
            if (TryRegisterPair(new HotkeySetting(record.Modifiers, record.Vk, record.Name), null))
                return;
        }
        HotkeyText.Text = "단축키 등록 실패 (다른 프로그램이 사용 중)";
    }

    /// <summary>Registers record+pause atomically; rolls the first back when the second fails.</summary>
    private bool TryRegisterPair(HotkeySetting? record, HotkeySetting? pause)
    {
        bool recordOk = record is null || Native.RegisterHotKey(Handle, HotkeyRecord,
            Native.MOD_NOREPEAT | record.Modifiers, record.VirtualKey);
        if (!recordOk)
            return false;

        bool pauseOk = pause is null || Native.RegisterHotKey(Handle, HotkeyPause,
            Native.MOD_NOREPEAT | pause.Modifiers, pause.VirtualKey);
        if (!pauseOk)
        {
            if (record is not null)
                Native.UnregisterHotKey(Handle, HotkeyRecord);
            return false;
        }

        _activeRecordHotkey = record;
        _activePauseHotkey = pause;
        HotkeyText.Text = "단축키: " + (record?.Name ?? "없음") + " 녹화 시작/중지  ·  " + (pause?.Name ?? "없음") + " 일시정지";
        return true;
    }

    /// <summary>
    /// Called by the settings window: registers and persists a candidate pair together.
    /// On failure the previous pair is restored and an error is returned to the dialog.
    /// </summary>
    private string? TryApplyHotkeys(HotkeySetting? record, HotkeySetting? pause)
    {
        var previousRecord = _activeRecordHotkey;
        var previousPause = _activePauseHotkey;
        string? Finish(string? error)
        {
            if (_hotkeysSuspended)
            {
                Native.UnregisterHotKey(Handle, HotkeyRecord);
                Native.UnregisterHotKey(Handle, HotkeyPause);
            }
            return error;
        }

        Native.UnregisterHotKey(Handle, HotkeyRecord);
        Native.UnregisterHotKey(Handle, HotkeyPause);
        _activeRecordHotkey = _activePauseHotkey = null;

        string? failure = null;
        if (TryRegisterPair(record, pause))
        {
            try
            {
                HotkeySettings.Save(new HotkeySettings
                {
                    RecordModifiers = record?.Modifiers, RecordVirtualKey = record?.VirtualKey, RecordName = record?.Name,
                    PauseModifiers = pause?.Modifiers, PauseVirtualKey = pause?.VirtualKey, PauseName = pause?.Name,
                });
                return Finish(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = "단축키를 저장하지 못했습니다. 기존 단축키를 유지합니다: " + ex.Message;
                Native.UnregisterHotKey(Handle, HotkeyRecord);
                Native.UnregisterHotKey(Handle, HotkeyPause);
            }
        }

        // Rollback to the previous pair (which was registered and worked before).
        if (!TryRegisterPair(previousRecord, previousPause))
            RegisterHotkeys(); // even the rollback hit a conflict: rebuild from defaults
        return Finish(failure ?? "키 등록에 실패했습니다 (다른 프로그램이 사용 중이거나 조합이 허용되지 않습니다). 기존 단축키를 유지합니다.");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            handled = true;
            if (_hotkeysSuspended)
                return IntPtr.Zero; // capturing a new key: a stray press must not start a recording
            if (wParam.ToInt32() == HotkeyRecord)
                _ = ToggleRecordingAsync();
            else if (wParam.ToInt32() == HotkeyPause)
                TogglePause();
        }
        return IntPtr.Zero;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        _uploadCancellation?.Cancel();
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
