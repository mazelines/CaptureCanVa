using System.IO;
using System.Diagnostics;
using CaptureCanva.Capture;
using CaptureCanva.Interop;

namespace CaptureCanva.Recording;

public sealed record RecordingOptions(
    CaptureTarget Target,
    string FfmpegPath,
    string OutputDirectory,
    int Fps,
    VideoQuality Quality,
    VideoEncoder Encoder,
    bool CaptureCursor,
    bool SystemAudio,
    bool Microphone,
    bool GpuColorConversion = true);

/// <summary>
/// One recording: capture → constant-frame-rate raw video into ffmpeg, audio into WAV files,
/// then a final mux into a single MP4.
/// </summary>
public sealed class RecordingSession
{
    private readonly RecordingOptions _options;
    private readonly string _baseName;
    private readonly Stopwatch _clock = new();
    private readonly List<AudioRecorder> _audio = new();
    private readonly List<string> _warnings = new();
    private ScreenCapturer? _capturer;
    private FfmpegVideoWriter? _videoWriter;
    private Thread? _writerThread;
    private volatile bool _stopRequested;
    private volatile string? _writerError;
    private long _framesWritten;

    public bool IsPaused { get; private set; }
    public TimeSpan Elapsed => _clock.Elapsed;
    public IReadOnlyList<string> Warnings => _warnings;
    public int Width => _capturer?.Width ?? 0;
    public int Height => _capturer?.Height ?? 0;
    public FramePixelFormat PixelFormat => _capturer?.PixelFormat ?? FramePixelFormat.Bgra;

    /// <summary>How many frames the encoder is behind real time. Above ~1 s the video starts to lag the audio.</summary>
    public long FramesBehind => Math.Max(0, (long)(_clock.Elapsed.TotalSeconds * _options.Fps) - Interlocked.Read(ref _framesWritten));
    public int Fps => _options.Fps;

    /// <summary>Raised on a worker thread when recording can't continue (window closed, encoder died).</summary>
    public event Action<string>? Interrupted;

    public RecordingSession(RecordingOptions options)
    {
        _options = options;
        _baseName = $"CaptureCanva_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    private string TempVideoPath => Path.Combine(_options.OutputDirectory, _baseName + ".part.mkv");

    public async Task StartAsync()
    {
        try
        {
            await StartCoreAsync();
        }
        catch
        {
            _stopRequested = true;
            _capturer?.Dispose();
            _videoWriter?.Dispose();
            foreach (var a in _audio) a.Dispose();
            TryDelete(TempVideoPath);
            throw;
        }
    }

    private async Task StartCoreAsync()
    {
        Directory.CreateDirectory(_options.OutputDirectory);

        _capturer = new ScreenCapturer(_options.Target, _options.CaptureCursor, showBorder: false, _options.GpuColorConversion);
        if (_options.GpuColorConversion && _capturer.GpuConversionUnavailableReason is { } reason)
            _warnings.Add($"GPU 색 변환을 사용할 수 없어 CPU로 변환합니다: {reason}");
        _capturer.TargetClosed += () => Interrupted?.Invoke("녹화 대상 창이 닫혀서 녹화를 종료합니다.");
        _capturer.Start();
        await Task.Run(() => _capturer.WaitForFirstFrame(TimeSpan.FromSeconds(2)));

        _videoWriter = new FfmpegVideoWriter(_options.FfmpegPath, TempVideoPath,
            _capturer.Width, _capturer.Height, _options.Fps, _capturer.PixelFormat, _options.Encoder, _options.Quality);

        if (_options.SystemAudio)
            TryStartAudio(() => AudioRecorder.CreateSystemAudio(Path.Combine(_options.OutputDirectory, _baseName + ".system.wav")), "시스템 소리");
        if (_options.Microphone)
            TryStartAudio(() => AudioRecorder.CreateMicrophone(Path.Combine(_options.OutputDirectory, _baseName + ".mic.wav")), "마이크");

        Native.timeBeginPeriod(1);
        _clock.Start();
        _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "CaptureCanva video writer", Priority = ThreadPriority.AboveNormal };
        _writerThread.Start();
    }

    private void TryStartAudio(Func<AudioRecorder> create, string label)
    {
        AudioRecorder? recorder = null;
        try
        {
            recorder = create();
            recorder.Start();
            _audio.Add(recorder);
        }
        catch (Exception ex)
        {
            if (recorder != null)
            {
                recorder.Dispose();
                TryDelete(recorder.FilePath);
            }
            _warnings.Add($"{label} 녹음을 시작하지 못했습니다: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes exactly <c>elapsed × fps</c> frames, repeating the latest captured frame when the
    /// screen hasn't changed. This keeps the video in sync with the audio tracks.
    /// </summary>
    private void WriterLoop()
    {
        var capturer = _capturer!;
        var writer = _videoWriter!;
        var buffer = new byte[capturer.FrameBytes];
        double frameDuration = 1000.0 / _options.Fps;
        long written = 0;

        try
        {
            while (true)
            {
                bool stopping = _stopRequested;
                if (IsPaused && !stopping)
                {
                    Thread.Sleep(10);
                    continue;
                }

                double now = _clock.Elapsed.TotalMilliseconds;
                double nextFrameAt = written * frameDuration;
                if (now < nextFrameAt)
                {
                    // Caught up with the clock. When stopping, this is the end: if the encoder fell
                    // behind during recording, the missing frames were written first, so the video
                    // is never shorter than the audio.
                    if (stopping)
                        break;
                    Thread.Sleep(Math.Max(1, (int)(nextFrameAt - now)));
                    continue;
                }

                capturer.CopyLatestFrame(buffer);
                writer.WriteFrame(buffer);
                written++;
                Interlocked.Exchange(ref _framesWritten, written);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (!_stopRequested)
            {
                _writerError = "인코더(ffmpeg)가 비정상 종료되었습니다.\n" + writer.Errors;
                Interrupted?.Invoke(_writerError);
            }
        }
    }

    public void Pause()
    {
        if (IsPaused)
            return;
        IsPaused = true;
        _clock.Stop();
        foreach (var a in _audio) a.Paused = true;
    }

    public void Resume()
    {
        if (!IsPaused)
            return;
        foreach (var a in _audio) a.Paused = false;
        _clock.Start();
        IsPaused = false;
    }

    /// <summary>Stops everything and produces the final MP4. Returns its path.</summary>
    public async Task<string> StopAsync()
    {
        // Audio and the video clock stop at the same instant; then the writer finishes any frames
        // it fell behind on, which can take a while if the encoder couldn't keep up.
        _clock.Stop(); // freeze the frame count the writer has to reach
        var audioStopped = Task.WhenAll(_audio.Select(a => a.StopAsync()));
        _stopRequested = true;
        await Task.Run(() => _writerThread?.Join());
        Native.timeEndPeriod(1);

        _capturer?.Dispose();

        await audioStopped;
        foreach (var a in _audio)
            a.Dispose();

        bool encoded = _videoWriter != null && await _videoWriter.FinishAsync();
        string encoderErrors = _videoWriter?.Errors ?? "";
        _videoWriter?.Dispose();
        if (!encoded)
            throw new InvalidOperationException(_writerError ?? "영상 인코딩에 실패했습니다.\n" + encoderErrors);

        string finalPath = Path.Combine(_options.OutputDirectory, _baseName + ".mp4");
        var audioFiles = _audio.Select(a => a.FilePath).Where(p => new FileInfo(p).Length > 1024).ToList();
        var (exitCode, errors) = await Ffmpeg.RunAsync(_options.FfmpegPath, BuildMuxArgs(TempVideoPath, audioFiles, finalPath), TimeSpan.FromMinutes(30));
        if (exitCode != 0)
            throw new InvalidOperationException($"최종 파일 생성(mux)에 실패했습니다. 임시 영상은 남겨두었습니다:\n{TempVideoPath}\n\n{errors}");

        TryDelete(TempVideoPath);
        foreach (var a in _audio) TryDelete(a.FilePath);
        return finalPath;
    }

    private static string BuildMuxArgs(string video, List<string> audioFiles, string output)
    {
        var args = new List<string> { "-hide_banner -loglevel error -y", "-i " + Ffmpeg.Quote(video) };
        args.AddRange(audioFiles.Select(a => "-i " + Ffmpeg.Quote(a)));

        switch (audioFiles.Count)
        {
            case 0:
                args.Add("-map 0:v -c:v copy");
                break;
            case 1:
                args.Add("-map 0:v -map 1:a -c:v copy -c:a aac -b:a 192k -ac 2");
                break;
            default:
                string inputs = string.Concat(Enumerable.Range(1, audioFiles.Count).Select(i => $"[{i}:a]"));
                args.Add($"-filter_complex \"{inputs}amix=inputs={audioFiles.Count}:duration=longest:normalize=0[a]\"");
                args.Add("-map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k -ac 2");
                break;
        }

        args.Add("-movflags +faststart " + Ffmpeg.Quote(output));
        return string.Join(' ', args);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
