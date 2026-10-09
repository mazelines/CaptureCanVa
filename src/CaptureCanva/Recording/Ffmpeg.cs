using System.IO;
using System.Diagnostics;
using System.Text;
using CaptureCanva.Capture;

namespace CaptureCanva.Recording;

public enum VideoEncoder
{
    Nvenc,
    Qsv,
    Amf,
    X264,
}

public enum VideoQuality
{
    High,
    Normal,
    Low,
}

internal static class Ffmpeg
{
    /// <summary>ffmpeg.exe next to the app wins over the one on PATH.</summary>
    public static string? FindExecutable()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local))
            return local;

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    /// <summary>Picks the first hardware encoder that actually works on this machine, else libx264.</summary>
    public static async Task<VideoEncoder> DetectEncoderAsync(string ffmpeg)
    {
        foreach (var encoder in new[] { VideoEncoder.Nvenc, VideoEncoder.Qsv, VideoEncoder.Amf })
        {
            string args = $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=256x256:r=30 -frames:v 3 {EncoderArgs(encoder, VideoQuality.Normal)} -f null -";
            var (exitCode, _) = await RunAsync(ffmpeg, args, TimeSpan.FromSeconds(10));
            if (exitCode == 0)
                return encoder;
        }
        return VideoEncoder.X264;
    }

    public static string DisplayName(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.Nvenc => "NVIDIA NVENC (H.264)",
        VideoEncoder.Qsv => "Intel Quick Sync (H.264)",
        VideoEncoder.Amf => "AMD AMF (H.264)",
        _ => "소프트웨어 x264 (H.264)",
    };

    public static string EncoderArgs(VideoEncoder encoder, VideoQuality quality)
    {
        int q = quality switch
        {
            VideoQuality.High => 19,
            VideoQuality.Normal => 24,
            _ => 30,
        };
        return encoder switch
        {
            VideoEncoder.Nvenc => $"-c:v h264_nvenc -preset p4 -tune hq -rc vbr -cq {q} -b:v 0",
            VideoEncoder.Qsv => $"-c:v h264_qsv -preset medium -global_quality {q}",
            VideoEncoder.Amf => $"-c:v h264_amf -quality balanced -rc cqp -qp_i {q} -qp_p {q}",
            _ => $"-c:v libx264 -preset veryfast -crf {q}",
        };
    }

    public static async Task<(int ExitCode, string Errors)> RunAsync(string ffmpeg, string args, TimeSpan timeout)
    {
        using var process = Process.Start(new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        })!;
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            return (-1, "ffmpeg 시간 초과");
        }
        return (process.ExitCode, await stderr);
    }

    public static string Quote(string path) => "\"" + path + "\"";
}

/// <summary>Feeds raw frames (NV12 or BGRA) to an ffmpeg process over stdin.</summary>
internal sealed class FfmpegVideoWriter : IDisposable
{
    // Both paths produce BT.709 limited-range 4:2:0 and tag the stream accordingly.
    private const string ColorTags = "-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv";

    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly StringBuilder _errors = new();

    public FfmpegVideoWriter(string ffmpeg, string outputPath, int width, int height, int fps,
        FramePixelFormat pixelFormat, VideoEncoder encoder, VideoQuality quality)
    {
        // NV12 is already converted on the GPU and goes to the encoder as-is. The BGRA fallback is
        // converted by ffmpeg on the CPU, with the same BT.709 matrix so both paths look identical.
        // The NV12 input must be tagged too: untagged YUV is assumed to be BT.601, and ffmpeg would
        // then silently insert a BT.601 → BT.709 conversion to match the output tags.
        string input = pixelFormat == FramePixelFormat.Nv12 ? $"nv12 {ColorTags}" : "bgra";
        string convert = pixelFormat == FramePixelFormat.Nv12
            ? ""
            : "-vf scale=out_color_matrix=bt709:out_range=tv,format=yuv420p,setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv ";
        string args =
            $"-hide_banner -loglevel error -y " +
            $"-f rawvideo -pix_fmt {input} -video_size {width}x{height} -framerate {fps} -i - " +
            $"{convert}{Ffmpeg.EncoderArgs(encoder, quality)} {ColorTags} -g {fps * 2} {Ffmpeg.Quote(outputPath)}";

        _process = Process.Start(new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        })!;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                lock (_errors) _errors.AppendLine(e.Data);
        };
        _process.BeginErrorReadLine();
        _stdin = _process.StandardInput.BaseStream;
    }

    public string Errors
    {
        get { lock (_errors) return _errors.ToString(); }
    }

    public void WriteFrame(byte[] frame) => _stdin.Write(frame, 0, frame.Length);

    /// <summary>Closes stdin and waits for ffmpeg to flush the file. Returns false on encoder failure.</summary>
    public async Task<bool> FinishAsync()
    {
        try { _stdin.Close(); } catch (IOException) { }
        await _process.WaitForExitAsync();
        return _process.ExitCode == 0;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(true);
        }
        catch { }
        _process.Dispose();
    }
}
