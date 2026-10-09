using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CaptureCanva.Recording;

/// <summary>Records system audio (loopback) or the default microphone to a WAV file.</summary>
internal sealed class AudioRecorder : IDisposable
{
    private readonly IWaveIn _capture;
    private readonly WaveFileWriter _writer;
    private readonly bool _padSilence;
    private readonly Stopwatch _clock = new();
    private readonly object _lock = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _bytesWritten;
    private bool _paused;

    public string FilePath { get; }

    private AudioRecorder(IWaveIn capture, string filePath, bool padSilence)
    {
        FilePath = filePath;
        _capture = capture;
        _padSilence = padSilence;
        _writer = new WaveFileWriter(filePath, capture.WaveFormat);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, _) => _stopped.TrySetResult();
    }

    /// <summary>
    /// WASAPI loopback delivers no packets while nothing is playing, so the gaps are filled with
    /// silence based on wall-clock time to keep the track aligned with the video.
    /// </summary>
    public static AudioRecorder CreateSystemAudio(string filePath) =>
        Create(() => new WasapiLoopbackCapture(), filePath, padSilence: true);

    public static AudioRecorder CreateMicrophone(string filePath) =>
        Create(() => new WasapiCapture(), filePath, padSilence: false);

    private static AudioRecorder Create(Func<IWaveIn> createCapture, string filePath, bool padSilence)
    {
        var capture = createCapture();
        try
        {
            return new AudioRecorder(capture, filePath, padSilence);
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    public bool Paused
    {
        get { lock (_lock) return _paused; }
        set
        {
            lock (_lock)
            {
                if (_paused == value)
                    return;
                _paused = value;
                if (value) _clock.Stop(); else _clock.Start();
            }
        }
    }

    public void Start()
    {
        _clock.Start();
        _capture.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_paused || e.BytesRecorded <= 0)
                return;
            if (_padSilence)
                PadSilence(e.BytesRecorded);
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _bytesWritten += e.BytesRecorded;
        }
    }

    /// <summary>Writes zeros so that, after <paramref name="incomingBytes"/>, the file length matches the clock.</summary>
    private void PadSilence(int incomingBytes)
    {
        var format = _writer.WaveFormat;
        long expected = (long)(_clock.Elapsed.TotalSeconds * format.AverageBytesPerSecond);
        long missing = expected - _bytesWritten - incomingBytes;
        long threshold = format.AverageBytesPerSecond / 20; // ignore jitter under 50 ms
        if (missing <= threshold)
            return;

        missing -= missing % format.BlockAlign;
        var zeros = new byte[Math.Min(missing, format.AverageBytesPerSecond)];
        while (missing > 0)
        {
            int chunk = (int)Math.Min(missing, zeros.Length);
            _writer.Write(zeros, 0, chunk);
            _bytesWritten += chunk;
            missing -= chunk;
        }
    }

    public async Task StopAsync()
    {
        _capture.StopRecording();
        await Task.WhenAny(_stopped.Task, Task.Delay(2000));
        lock (_lock)
        {
            if (_padSilence && !_paused)
                PadSilence(0);
            _clock.Stop();
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        _capture.Dispose();
        _writer.Dispose();
    }
}
