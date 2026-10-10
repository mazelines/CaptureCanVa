using System.IO;
using System.Text.Json;
using CaptureCanva;
using CaptureCanva.Preferences;
using CaptureCanva.Recording;

string directory = Path.Combine(Path.GetTempPath(), "CaptureCanva-SettingsChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
AppSettings.DirectoryOverride = HotkeySettings.DirectoryOverride = directory;
int passed = 0;
try
{
    Check(Path.GetFullPath(AppSettings.FilePath).StartsWith(directory + Path.DirectorySeparatorChar), "storage stays in isolated Temp directory");
    Check(Path.IsPathFullyQualified(AppSettings.DefaultOutputDirectory), "default recording folder is absolute");
    Check(Path.GetFileName(AppSettings.DefaultOutputDirectory) == "CaptureCanva", "default folder is CaptureCanva under Videos");
    Check(AppSettings.Load().CreateGif, "GIF output is enabled by default");
    Check(new AppSettings { OutputDirectory = "relative-folder" }.EffectiveOutputDirectory == AppSettings.DefaultOutputDirectory,
        "relative persisted folder recovers to an absolute default");
    File.WriteAllText(AppSettings.FilePath, "{\"OutputDirectory\":\"  \",\"UploadYouTube\":true,\"UploadDrive\":true,\"YouTubePublic\":true}");
    var settings = AppSettings.Load();
    Check(settings.OutputDirectory == AppSettings.DefaultOutputDirectory, "empty legacy save folder recovers to default");
    settings.Save();
    using (var document = JsonDocument.Parse(File.ReadAllText(AppSettings.FilePath)))
    {
        Check(!document.RootElement.TryGetProperty("UploadYouTube", out _) && !document.RootElement.TryGetProperty("UploadDrive", out _), "legacy upload preferences are discarded on save");
        Check(!document.RootElement.TryGetProperty("EffectiveOutputDirectory", out _), "computed directory is not persisted");
    }
    string custom = Path.Combine(directory, "영상 저장 폴더");
    settings.OutputDirectory = custom;
    settings.CreateGif = false;
    settings.GifPreset = GifPreset.HighQuality;
    settings.Save();
    var restored = AppSettings.Load();
    Check(restored.OutputDirectory == custom && !restored.CreateGif && restored.GifPreset == GifPreset.HighQuality, "custom Unicode folder and GIF preferences survive restart");
    string saved = File.ReadAllText(AppSettings.FilePath);
    using (var locked = new FileStream(AppSettings.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        settings.OutputDirectory = "changed";
        bool failed = false;
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed, "locked settings report write failure");
    }
    Check(File.ReadAllText(AppSettings.FilePath) == saved, "failed save preserves previous configuration");
    Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "failed save removes owned temporary file");
    File.WriteAllText(AppSettings.FilePath, "{\"Fps\":0,\"Quality\":99,\"GifPreset\":99,\"Mode\":99}");
    settings = AppSettings.Load();
    Check(settings.Fps == 60 && settings.Quality == VideoQuality.High && settings.GifPreset == GifPreset.Standard, "invalid persisted encoder options recover to defaults");
    File.WriteAllText(AppSettings.FilePath, "{broken");
    Check(AppSettings.Load().OutputDirectory == AppSettings.DefaultOutputDirectory, "corrupt settings recover to default save folder");
    HotkeySettings.Save(new HotkeySettings { RecordModifiers = 2, RecordVirtualKey = 0x78, RecordName = "Ctrl+F9" });
    Check(HotkeySettings.TryLoad(out var keys) && keys.Record == new HotkeySetting(2, 0x78, "Ctrl+F9"), "custom shortcut survives restart");
    HotkeySettings.Save(new HotkeySettings());
    Check(HotkeySettings.TryLoad(out keys) && keys.Record == null && keys.Pause == null, "explicitly cleared shortcuts stay disabled after restart");
    string hotkeyPath = Path.Combine(directory, "hotkeys.json");
    string previousKeys = File.ReadAllText(hotkeyPath);
    using (var locked = new FileStream(hotkeyPath, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        bool failed = false;
        try { HotkeySettings.Save(new HotkeySettings { RecordVirtualKey = 0x7b }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed, "locked hotkey settings report write failure");
    }
    Check(File.ReadAllText(hotkeyPath) == previousKeys && Directory.GetFiles(directory, "*.tmp").Length == 0, "failed hotkey save preserves configuration and cleans temporary files");
    File.WriteAllText(hotkeyPath, "{broken");
    Check(!HotkeySettings.TryLoad(out _), "corrupt hotkey file allows registration fallback");
    Console.WriteLine($"All {passed} settings checks passed. Artifacts: {directory}");
}
finally
{
    AppSettings.DirectoryOverride = HotkeySettings.DirectoryOverride = null;
}

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine("PASS " + name);
}
