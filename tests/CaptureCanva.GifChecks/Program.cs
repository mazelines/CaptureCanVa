using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CaptureCanva.Recording;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Usage: CaptureCanva.GifChecks <ffmpeg.exe path>");

string ffmpeg = Path.GetFullPath(args[0]);
string directory = Path.Combine(Path.GetTempPath(), "CaptureCanva-GifChecks-" + Guid.NewGuid().ToString("N"), "GIF recordings 한글");
Directory.CreateDirectory(directory);
string source = Path.Combine(directory, "source clip.mp4");
await RunFfmpegAsync(["-f", "lavfi", "-i", "testsrc2=s=800x450:r=30", "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", source]);

foreach (var (preset, expectedWidth, expectedFps) in new[]
{
    (GifPreset.Small, 360, 10),
    (GifPreset.Standard, 480, 12),
    (GifPreset.HighQuality, 640, 15),
    (GifPreset.OriginalSize, 800, 15),
})
{
    string video = Path.Combine(directory, "recording " + preset + ".mp4");
    File.Copy(source, video);
    byte[] videoHash = SHA256.HashData(await File.ReadAllBytesAsync(video));
    string gif = await GifConverter.ConvertAsync(ffmpeg, video, preset);
    byte[] bytes = await File.ReadAllBytesAsync(gif);
    Require(Encoding.ASCII.GetString(bytes, 0, 6) == "GIF89a", "Output must be an animated GIF.");
    int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2));
    int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
    Require(width == expectedWidth, "Preset width must match: " + preset);
    Require(Math.Abs((double)height / width - 450.0 / 800) <= 1.0 / width, "Aspect ratio must be preserved.");
    int loopBlock = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
    Require(loopBlock >= 0 && bytes.AsSpan(loopBlock + 11, 5).SequenceEqual(new byte[] { 3, 1, 0, 0, 0 }), "GIF must loop indefinitely.");
    string decodeProgress = await RunFfmpegAsync(["-ignore_loop", "1", "-i", gif, "-fps_mode", "passthrough", "-progress", "pipe:1", "-f", "null", "NUL"]);
    var frameMatches = Regex.Matches(decodeProgress, @"(?m)^frame=(\d+)\r?$");
    Require(frameMatches.Count > 0, "Decoder must report GIF frames.");
    int frames = int.Parse(frameMatches[^1].Groups[1].Value);
    Require(Math.Abs(frames - expectedFps * 2) <= 1, "All recorded frames must convert at the requested FPS.");
    Require(videoHash.AsEnumerable().SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(video))), "MP4 must remain unchanged.");
    RequireNoTemporaryFiles();
    Console.WriteLine($"PASS {preset}: {width}x{height}, {frames} frames, infinite loop, MP4 preserved");
}

string smallVideo = Path.Combine(directory, "small recording.mp4");
await RunFfmpegAsync(["-f", "lavfi", "-i", "testsrc2=s=128x72:r=30", "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", smallVideo]);
string smallGif = await GifConverter.ConvertAsync(ffmpeg, smallVideo, GifPreset.Standard);
byte[] smallBytes = await File.ReadAllBytesAsync(smallGif);
Require(BinaryPrimitives.ReadUInt16LittleEndian(smallBytes.AsSpan(6, 2)) == 128, "Small recordings must not be enlarged.");
Console.WriteLine("PASS small recording: no upscaling");

string existingVideo = Path.Combine(directory, "recording Standard.mp4");
string existingGif = Path.ChangeExtension(existingVideo, ".gif");
byte[] originalGif = await File.ReadAllBytesAsync(existingGif);
byte[] originalVideo = await File.ReadAllBytesAsync(existingVideo);
using (var cancellation = new CancellationTokenSource())
{
    var progress = new InlineProgress(stage =>
    {
        if (stage.Contains("(2/2)")) cancellation.Cancel();
    });
    await ExpectCancellationAsync(() => GifConverter.ConvertAsync(ffmpeg, existingVideo, GifPreset.Standard, progress, cancellation.Token));
}
Require(originalGif.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(existingGif)), "Cancellation must preserve an existing GIF.");
Require(originalVideo.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(existingVideo)), "Cancellation must preserve the MP4.");
RequireNoTemporaryFiles();
Console.WriteLine("PASS cancellation between passes: existing GIF and MP4 preserved, temporary files removed");

using (var cancellation = new CancellationTokenSource())
{
    var progress = new InlineProgress(_ => cancellation.CancelAfter(TimeSpan.FromMilliseconds(20)));
    await ExpectCancellationAsync(() => GifConverter.ConvertAsync(ffmpeg, existingVideo, GifPreset.OriginalSize, progress, cancellation.Token));
}
Require(originalVideo.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(existingVideo)), "Interrupted FFmpeg must preserve the MP4.");
RequireNoTemporaryFiles();
Console.WriteLine("PASS cancellation while FFmpeg runs: MP4 preserved, temporary files removed");

string invalidVideo = Path.Combine(directory, "invalid recording.mp4");
await File.WriteAllTextAsync(invalidVideo, "Not a valid MP4");
string preservedGif = Path.ChangeExtension(invalidVideo, ".gif");
await File.WriteAllBytesAsync(preservedGif, originalGif);
bool failed = false;
try { await GifConverter.ConvertAsync(ffmpeg, invalidVideo, GifPreset.Standard); }
catch (InvalidOperationException) { failed = true; }
Require(failed, "Invalid input must report conversion failure.");
Require(await File.ReadAllTextAsync(invalidVideo) == "Not a valid MP4", "Conversion failure must leave the video alone.");
Require(originalGif.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(preservedGif)), "Conversion failure must preserve an existing GIF.");
RequireNoTemporaryFiles();
Console.WriteLine("PASS invalid video: failure reported, existing files preserved, temporary files removed");
Console.WriteLine("All GIF checks passed. Artifacts: " + directory);

void RequireNoTemporaryFiles() => Require(Directory.GetFiles(directory, ".gif-*").Length == 0, "Temporary GIF/palette files must be removed.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task ExpectCancellationAsync(Func<Task<string>> operation)
{
    try { await operation(); }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("GIF conversion must observe cancellation.");
}

async Task<string> RunFfmpegAsync(string[] arguments)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
        },
    };
    foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(arguments))
        process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    var errors = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    string errorText = await errors;
    if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg test failed: " + errorText);
    return await output;
}

internal sealed class InlineProgress(Action<string> callback) : IProgress<string>
{
    public void Report(string value) => callback(value);
}
