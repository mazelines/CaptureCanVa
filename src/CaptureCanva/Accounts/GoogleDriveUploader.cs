using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CaptureCanva.Accounts;

/// <summary>
/// Uploads files to Google Drive (multipart, drive.file scope: the app only sees files it
/// created). Videos uploaded this way are also directly shareable / streamable from Drive.
/// </summary>
public sealed class GoogleDriveUploader : GoogleUploader
{
    private const string MultipartUrl = "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id,webViewLink,size";

    public GoogleDriveUploader(GoogleTokens tokens, HttpMessageHandler? handler = null) : base(tokens, handler) { }

    protected override GoogleService TokenService => GoogleService.Drive;

    /// <param name="makeShareable">Sets anyone-with-link read permission after the upload.</param>
    public async Task<UploadResult> UploadAsync(GoogleOAuthClient oauth, string filePath, string? parentId = null,
        bool makeShareable = false, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("업로드할 파일을 찾을 수 없습니다.", filePath);

        var metadata = new Dictionary<string, object> { ["name"] = Path.GetFileName(filePath) };
        if (!string.IsNullOrEmpty(parentId))
            metadata["parents"] = new[] { parentId };

        await EnsureAccessTokenAsync(oauth, cancellation);

        long totalBytes = new FileInfo(filePath).Length;
        long sentBytes = 0;
        progress?.Report($"Google Drive 업로드 중… 0 / {totalBytes / (1024.0 * 1024.0):F1} MB");
        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, useAsync: true);
        var progressStream = new ReportingStream(fileStream, read =>
        {
            sentBytes += read;
            if (totalBytes > 0)
                progress?.Report($"Google Drive 업로드 중… {sentBytes * 100 / totalBytes}% ({sentBytes / (1024.0 * 1024.0):F1} / {totalBytes / (1024.0 * 1024.0):F1} MB)");
        }, cancellation);

        using var metadataContent = new StringContent(JsonSerializer.Serialize(metadata),
            Encoding.UTF8, "application/json");
        using var fileContent = new StreamContent(progressStream, 256 * 1024);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Post, MultipartUrl)
        {
            Content = new MultipartContent("related", "capturecanva-boundary")
            {
                metadataContent,
                fileContent,
            },
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.AccessToken);

        using var response = await Http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Google Drive 업로드에 실패했습니다.\n" + await ReadErrorAsync(response, cancellation));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        string fileId = json.RootElement.GetProperty("id").GetString()!;
        string link = json.RootElement.TryGetProperty("webViewLink", out var web)
            ? web.GetString() ?? $"https://drive.google.com/file/d/{fileId}/view"
            : $"https://drive.google.com/file/d/{fileId}/view";

        if (makeShareable)
            await CreateSharePermissionAsync(fileId, cancellation);

        progress?.Report("Google Drive 업로드 완료");
        return new UploadResult(link, fileId);
    }

    /// <summary>Anyone-with-the-link viewer permission.</summary>
    private async Task CreateSharePermissionAsync(string fileId, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://www.googleapis.com/drive/v3/files/{fileId}/permissions")
        {
            Content = new StringContent(
                """{"role":"reader","type":"anyone"}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.AccessToken);
        using var response = await Http.SendAsync(request, cancellation);
        // Sharing is best-effort: the file itself is uploaded fine when this fails.
    }
}
