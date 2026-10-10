using System.Diagnostics;
using System.IO;

namespace CaptureCanva.Recording;

public enum GifPreset
{
    Small,
    Standard,
    HighQuality,
    OriginalSize,
}

/// <summary>Uses movie2gif's two-pass palette pipeline and web presets with the bundled FFmpeg.</summary>
internal static class GifConverter
{
    public static async Task<string> ConvertAsync(string ffmpegPath, string videoPath, GifPreset preset,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string outputPath = Path.ChangeExtension(videoPath, ".gif");
        string temporaryBase = Path.Combine(Path.GetDirectoryName(outputPath)!, ".gif-" + Guid.NewGuid().ToString("N"));
        string palettePath = temporaryBase + ".palette.png";
        string temporaryGifPath = temporaryBase + ".tmp";
        var (width, fps, dither) = preset switch
        {
            GifPreset.Small => (360, 10, "bayer:bayer_scale=5"),
            GifPreset.HighQuality => (640, 15, "sierra2_4a"),
            GifPreset.OriginalSize => (0, 15, "sierra2_4a"),
            _ => (480, 12, "bayer:bayer_scale=5"),
        };
        // Preserve aspect ratio and avoid enlarging small recordings.
        // A one-frame recording can be shorter than half a GIF frame. The default EOF
        // rounding drops that frame and palettegen exits successfully without a palette.
        string videoFilter = $"fps={fps}:eof_action=pass" + (width > 0 ? $",scale=w='min(iw,{width})':h=-1:flags=lanczos" : "");

        try
        {
            progress?.Report("GIF 색상 분석 중 (1/2)…");
            await RunAsync(ffmpegPath,
                ["-i", videoPath, "-an", "-vf", videoFilter + ",palettegen=stats_mode=diff",
                    "-frames:v", "1", "-update", "1", palettePath], cancellationToken);

            if (!File.Exists(palettePath) || new FileInfo(palettePath).Length == 0)
                throw new InvalidOperationException("영상에서 GIF 색상을 분석하지 못했습니다. MP4가 정상적으로 재생되는지 확인해 주세요.");

            progress?.Report("GIF 변환 중 (2/2)…");
            await RunAsync(ffmpegPath,
                ["-i", videoPath, "-i", palettePath, "-an", "-filter_complex",
                    $"[0:v]{videoFilter}[v];[v][1:v]paletteuse=dither={dither}:diff_mode=rectangle",
                    "-loop", "0", "-f", "gif", temporaryGifPath], cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporaryGifPath) || new FileInfo(temporaryGifPath).Length == 0)
                throw new InvalidOperationException("GIF 파일이 생성되지 않았습니다.");
            // Only replace the final GIF after a successful conversion; never modify the MP4.
            File.Move(temporaryGifPath, outputPath, overwrite: true);
            return outputPath;
        }
        finally
        {
            TryDelete(palettePath);
            TryDelete(temporaryGifPath);
        }
    }

    private static async Task RunAsync(string ffmpegPath, string[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            },
        };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(arguments))
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await output;
            string errorText = await errors;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("GIF 변환에 실패했습니다.\n" + errorText);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* FFmpeg already exited. */ }
            await process.WaitForExitAsync();
            await Task.WhenAll(errors, output);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
