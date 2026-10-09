using System.IO;

namespace CaptureCanva.Accounts;

/// <summary>Persisted hotkey pair (modifiers + Win32 virtual-key + display name).</summary>
public sealed record HotkeySetting(uint Modifiers, uint VirtualKey, string Name);

/// <summary>Persisted hotkey pair with (de)serialization; a cleared key stays null on disk.</summary>
public sealed class HotkeySettings
{
    public uint? RecordModifiers { get; set; }
    public uint? RecordVirtualKey { get; set; }
    public string? RecordName { get; set; }
    public uint? PauseModifiers { get; set; }
    public uint? PauseVirtualKey { get; set; }
    public string? PauseName { get; set; }

    public HotkeySetting? Record => RecordVirtualKey is null ? null :
        new HotkeySetting(RecordModifiers ?? 0, RecordVirtualKey.Value, RecordName ?? "단축키");
    public HotkeySetting? Pause => PauseVirtualKey is null ? null :
        new HotkeySetting(PauseModifiers ?? 0, PauseVirtualKey.Value, PauseName ?? "단축키");

    /// <summary>Test-only storage override (internal: shared assembly with the test harness).
    /// Null in the app; when set, Save/Load touch ONLY the given directory.</summary>
    internal static string? DirectoryOverride { get; set; }

    private static string FilePath => Path.Combine(
        DirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaptureCanva"),
        "hotkeys.json");

    public static HotkeySettings Load()
        => TryLoad(out var settings) ? settings : new HotkeySettings();

    public static bool TryLoad(out HotkeySettings settings)
    {
        settings = new HotkeySettings();
        try
        {
            if (File.Exists(FilePath))
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<HotkeySettings>(File.ReadAllText(FilePath));
                if (saved != null) { settings = saved; return true; }
            }
        }
        catch { /* Corrupt settings fall back to defaults. */ }
        return false;
    }

    /// <exception cref="IOException">The hotkey file could not be written; callers must surface
    /// this instead of pretending the new binding was persisted.</exception>
    public static void Save(HotkeySettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string tempPath = FilePath + ".new";
        File.WriteAllText(tempPath, System.Text.Json.JsonSerializer.Serialize(settings,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, FilePath, overwrite: true); // atomic, same as the token store
    }
}
