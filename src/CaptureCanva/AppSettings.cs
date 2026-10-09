using System.IO;
using System.Text.Json;
using CaptureCanva.Capture;
using CaptureCanva.Recording;

namespace CaptureCanva;

public sealed class AppSettings
{
    public CaptureMode Mode { get; set; } = CaptureMode.Monitor;
    public string? MonitorDevice { get; set; }
    public string? RegionMonitorDevice { get; set; }
    /// <summary>Last region, relative to <see cref="RegionMonitorDevice"/>.</summary>
    public PixelRect? Region { get; set; }
    public int Fps { get; set; } = 60;
    public VideoQuality Quality { get; set; } = VideoQuality.High;
    public bool CaptureCursor { get; set; } = true;
    public bool SystemAudio { get; set; } = true;
    public bool Microphone { get; set; }
    public bool HideFromCapture { get; set; } = true;
    public bool CreateGif { get; set; } = true;
    public GifPreset GifPreset { get; set; } = GifPreset.Standard;
    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "CaptureCanva");

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaptureCanva", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
        }
    }
}
