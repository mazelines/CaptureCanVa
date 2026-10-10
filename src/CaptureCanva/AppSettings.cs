using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public string OutputDirectory { get; set; } = DefaultOutputDirectory;

    /// <summary>Default save folder: (MyVideos)\CaptureCanva, created on demand.</summary>
    public static string DefaultOutputDirectory
    {
        get
        {
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos))
                videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            return Path.Combine(videos, "CaptureCanva");
        }
    }

    /// <summary>Effective save folder: an absolute configured path, otherwise the default.
    /// Recording must never target an empty path.</summary>
    [JsonIgnore]
    public string EffectiveOutputDirectory =>
        string.IsNullOrWhiteSpace(OutputDirectory) || !Path.IsPathFullyQualified(OutputDirectory)
            ? DefaultOutputDirectory : OutputDirectory;

    /// <summary>Test-only storage override (same pattern as HotkeySettings). Null in the app;
    /// when set, Load/Save touch ONLY the given directory and never the real user profile.</summary>
    internal static string? DirectoryOverride { get; set; }

    internal static string FilePath => Path.Combine(
        DirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaptureCanva"),
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
                settings.OutputDirectory = settings.EffectiveOutputDirectory;
                if (settings.Fps is not (15 or 24 or 30 or 60)) settings.Fps = 60;
                if (!Enum.IsDefined(settings.Mode)) settings.Mode = CaptureMode.Monitor;
                if (!Enum.IsDefined(settings.Quality)) settings.Quality = VideoQuality.High;
                if (!Enum.IsDefined(settings.GifPreset)) settings.GifPreset = GifPreset.Standard;
                return settings;
            }
        }
        catch
        {
            // Corrupt settings fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        OutputDirectory = EffectiveOutputDirectory;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
