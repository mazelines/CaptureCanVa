using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CaptureCanva.Accounts;

/// <summary>Result of a finished upload.</summary>
public sealed record UploadResult(string Url, string FileId);

/// <summary>
/// Uploads a finished recording to YouTube via the resumable videos.insert endpoint.
/// Requires the Google Cloud project to have the YouTube Data API v3 enabled and the OAuth
/// client to include the youtube.upload scope.
/// </summary>
public sealed class YouTubeUploader : GoogleUploader
{
    /// <summary>Resumable upload initiation endpoint (session URI comes from the response).</summary>
    private const string InitiateUrl = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status";

    public YouTubeUploader(GoogleTokens tokens, HttpMessageHandler? handler = null) : base(tokens, handler) { }

    protected override GoogleService TokenService => GoogleService.YouTube;

    /// <param name="title">Up to 100 characters; longer titles are truncated.</param>
    /// <param name="description">Up to 5000 characters.</param>
    /// <param name="isPublic">false requests a private video (the app's default).</param>
    public async Task<UploadResult> UploadAsync(GoogleOAuthClient oauth, string filePath, string title,
        string description, bool isPublic, IProgress<string>? progress = null,
        CancellationToken cancellation = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("업로드할 파일을 찾을 수 없습니다.", filePath);

        var metadata = new
        {
            snippet = new
            {
                title = title.Length > 100 ? title[..100] : title,
                description = description.Length > 5000 ? description[..5000] : description,
            },
            status = new
            {
                privacyStatus = isPublic ? "public" : "private",
                selfDeclaredMadeForKids = false,
            },
        };

        await EnsureAccessTokenAsync(oauth, cancellation);
        // The refresh inside EnsureAccessToken may have narrowed the grant (scope field replaced
        // the stored set). A reduced grant must not look connected nor start an API upload.
        if (!GoogleOAuthClient.HasScope(Tokens, GoogleOAuthClient.ScopeYouTube))
            throw new InvalidOperationException(
                "YouTube 업로드 권한이 더 이상 유효하지 않습니다. 환경설정에서 계정을 다시 연결해 주세요.");
        string metadataJson = JsonSerializer.Serialize(metadata);

        // 1) Initiate the resumable session.
        using var initiate = new HttpRequestMessage(HttpMethod.Post, InitiateUrl)
        {
            // StringContent mediaType must be the bare type: "application/json; charset=UTF-8"
            // is parsed as the media type and throws at runtime.
            Content = new StringContent(metadataJson, Encoding.UTF8, "application/json"),
        };
        initiate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.AccessToken);
        initiate.Headers.Add("X-Upload-Content-Length", new FileInfo(filePath).Length.ToString());
        initiate.Headers.Add("X-Upload-Content-Type", "video/mp4");

        using var initiateResponse = await Http.SendAsync(initiate, cancellation);
        if (!initiateResponse.IsSuccessStatusCode)
            throw new InvalidOperationException("YouTube 업로드 세션을 시작하지 못했습니다.\n" + await ReadErrorAsync(initiateResponse, cancellation));
        string? sessionUri = initiateResponse.Headers.Location?.ToString()
            ?? (initiateResponse.Headers.TryGetValues("Location", out var values) ? values.FirstOrDefault() : null);
        if (string.IsNullOrEmpty(sessionUri))
            throw new InvalidOperationException("YouTube가 업로드 세션 URI를 반환하지 않았습니다.");

        // 2) PUT the file bytes to the session URI. A progress-reporting stream gives the UI
        //    real transferred-bytes feedback, not just stage labels.
        long totalBytes = new FileInfo(filePath).Length;
        long sentBytes = 0;
        progress?.Report($"YouTube 업로드 중… 0 / {totalBytes / (1024.0 * 1024.0):F1} MB");
        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, useAsync: true);
        var progressStream = new ReportingStream(fileStream, read =>
        {
            sentBytes += read;
            if (totalBytes > 0)
                progress?.Report($"YouTube 업로드 중… {sentBytes * 100 / totalBytes}% ({sentBytes / (1024.0 * 1024.0):F1} / {totalBytes / (1024.0 * 1024.0):F1} MB)");
        }, cancellation);
        using var upload = new HttpRequestMessage(HttpMethod.Put, sessionUri)
        {
            Content = new StreamContent(progressStream, 256 * 1024),
        };
        // Step 3 of the resumable protocol requires the bearer token on the PUT as well.
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.AccessToken);
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");

        using var uploadResponse = await Http.SendAsync(upload, cancellation);
        if (!uploadResponse.IsSuccessStatusCode)
            throw new InvalidOperationException("YouTube 업로드에 실패했습니다.\n" + await ReadErrorAsync(uploadResponse, cancellation));

        using var json = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync(cancellation));
        string videoId = json.RootElement.GetProperty("id").GetString()!;
        progress?.Report("YouTube 업로드 완료");
        return new UploadResult($"https://youtu.be/{videoId}", videoId);
    }
}
