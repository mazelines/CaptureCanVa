using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CaptureCanva.Accounts;

/// <summary>Which Google service a stored credential belongs to.</summary>
public enum GoogleService
{
    YouTube,
    Drive,
}

/// <summary>
/// Persists Google OAuth tokens per service. Refresh tokens are secrets, so they are encrypted
/// with DPAPI (CurrentUser scope) - the file is only decryptable by this Windows user.
/// </summary>
public static class TokenStore
{
    /// <summary>Test-only storage override (internal: the UploadChecks harness links these
    /// sources into the same assembly). Null in the app; when set, Save/Load/Delete touch ONLY
    /// this directory and never the real user profile.</summary>
    internal static string? DirectoryOverride { get; set; }

    private static string DirectoryPath => DirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaptureCanva");

    private static string FilePath(GoogleService service) =>
        Path.Combine(DirectoryPath, $"auth-{service.ToString().ToLowerInvariant()}.bin");

    public static void Save(GoogleService service, GoogleTokens tokens)
    {
        string payload = JsonSerializer.Serialize(tokens);
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(payload), null,
            DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(DirectoryPath);
        // Write to a temp file and replace atomically so a crash mid-write cannot leave a
        // half-written token blob that would read back as "signed out".
        string path = FilePath(service);
        string tempPath = path + ".new";
        File.WriteAllBytes(tempPath, encrypted);
        File.Move(tempPath, path, overwrite: true);
    }

    public static GoogleTokens? Load(GoogleService service)
    {
        try
        {
            string path = FilePath(service);
            if (!File.Exists(path))
                return null;
            byte[] encrypted = File.ReadAllBytes(path);
            string payload = Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null,
                DataProtectionScope.CurrentUser));
            return JsonSerializer.Deserialize<GoogleTokens>(payload);
        }
        catch (CryptographicException)
        {
            // Different Windows user or corrupted blob: treat as signed out.
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        // IOException / UnauthorizedAccessException propagate: the UI must show an access
        // problem instead of silently reporting "not connected" while the file still exists.
    }

    /// <exception cref="IOException">The token file could not be removed; the account is still
    /// connected on disk, so callers must not report the account as disconnected.</exception>
    public static void Delete(GoogleService service) => File.Delete(FilePath(service));

    public static bool IsConnected(GoogleService service) => File.Exists(FilePath(service));
}
