using System.IO;
using System.Text.Json;

namespace CaptureCanva.Accounts;

/// <summary>
/// Reads the Google OAuth client registration. None of these paths are mandatory: when the file
/// is missing the app runs fine and the account panel explains how to set it up.
/// Expected shape: { "installed": { "client_id": "...", "client_secret": "..." } }
/// (the file Google Cloud Console downloads for a desktop app) or a flat
/// { "client_id": "...", "client_secret": "..." }.
/// </summary>
public static class GoogleClientConfig
{
    private static string? _clientId;
    private static string? _clientSecret;
    private static bool _loaded;

    /// <summary>Null when no OAuth client is registered; the settings window shows setup help.</summary>
    public static string? ClientId => Load();

    /// <summary>Optional for installed-app clients using PKCE.</summary>
    public static string? ClientSecret => Load() is null ? null : _clientSecret;

    /// <summary>Test-only override of the searched config paths (internal: shared assembly
    /// with the test harness); null in the app.</summary>
    internal static string[]? CandidatePathOverride { get; set; }

    private static string? Load()
    {
        if (_loaded)
            return _clientId;
        _loaded = true;

        foreach (string path in CandidatePathOverride ?? CandidatePaths())
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var root = json.RootElement;
                if (root.TryGetProperty("installed", out var installed))
                    root = installed;
                if (root.TryGetProperty("client_id", out var clientId))
                {
                    _clientId = clientId.GetString();
                    if (root.TryGetProperty("client_secret", out var secret))
                        _clientSecret = secret.GetString();
                    return _clientId;
                }
            }
            catch
            {
                // Malformed config behaves like a missing one; the UI explains setup instead.
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "google-oauth-client.json");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CaptureCanva", "google-oauth-client.json");
    }
}
