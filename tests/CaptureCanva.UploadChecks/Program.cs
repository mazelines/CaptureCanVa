// Offline checks for the Accounts layer, run against fake HttpMessageHandlers and GUID Temp
// directories only. Two isolation invariants are asserted BEFORE any store call:
//   1. the stores are re-pointed at the GUID Temp dir (not %APPDATA%),
//   2. the real %APPDATA%\CaptureCanva path is never read or written by these tests.
// Real Google endpoints are never contacted: HTTP is faked end to end.
using System.Net;
using System.Reflection;
using System.Text;
using CaptureCanva.Accounts;

public static class Entry
{
    private static int _pass, _fail;
    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  " + detail : ""));
        if (ok) _pass++; else _fail++;
    }

    public static int Main()
    {
        string realAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string fakeDir = Path.Combine(Path.GetTempPath(), "CaptureCanva-upcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeDir);
        string fakeCaptureCanva = Path.Combine(fakeDir, "CaptureCanva");
        Directory.CreateDirectory(fakeCaptureCanva);

        // ---- isolation-first: point every store at the GUID Temp dir ----
        TokenStore.DirectoryOverride = fakeCaptureCanva;
        HotkeySettings.DirectoryOverride = fakeCaptureCanva;
        GoogleClientConfig.CandidatePathOverride = [Path.Combine(fakeCaptureCanva, "google-oauth-client.json")];

        string safeRoot = Path.GetFullPath(fakeCaptureCanva) + Path.DirectorySeparatorChar;
        var tokenPath = typeof(TokenStore).GetMethod("FilePath", BindingFlags.Static | BindingFlags.NonPublic)!;
        string[] effectivePaths = [
            (string)tokenPath.Invoke(null, [GoogleService.YouTube])!,
            (string)tokenPath.Invoke(null, [GoogleService.Drive])!,
            (string)typeof(HotkeySettings).GetProperty("FilePath", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!,
        ];
        if (effectivePaths.Any(path => !Path.GetFullPath(path).StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Test storage must be inside the GUID Temp directory.");

        try
        {
            // ---- invariant 1: the override is actually in effect ----
            Check("isolation-tokenstore-override",
                (string?)typeof(TokenStore)
                    .GetProperty("DirectoryOverride", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!
                    .GetValue(null) == fakeCaptureCanva, fakeCaptureCanva);
            Check("isolation-real-appdata-untouched",
                !Directory.Exists(Path.Combine(realAppData, "CaptureCanva-upcheck")) &&
                !File.Exists(Path.Combine(realAppData, "CaptureCanva", "auth-youtube.bin.upcheck-probe")),
                "real APPDATA path is only probed for absence of test artifacts");

            // ---- 1. YouTube resumable upload (fake endpoint): initiate + PUT both carry Bearer,
            //        metadata content type is bare JSON, privacy default maps to status ----
            string? capturedInitiateAuth = null, capturedPutAuth = null, capturedInitiateBody = null;
            string? capturedInitiateContentType = null;
            byte[]? capturedPutBody = null;
            var handler = new FakeHandler(request =>
            {
                string uri = request.RequestUri!.ToString();
                if (uri.StartsWith("https://www.googleapis.com/upload/youtube/v3/videos") && request.Method == HttpMethod.Post)
                {
                    capturedInitiateAuth = request.Headers.Authorization?.ToString();
                    capturedInitiateContentType = request.Content?.Headers.ContentType?.MediaType;
                    capturedInitiateBody = request.Content is StringContent sc
                        ? sc.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult() : null;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Headers = { Location = new Uri("https://upload.fake/session/abc") },
                    };
                }
                if (uri == "https://upload.fake/session/abc" && request.Method == HttpMethod.Put)
                {
                    capturedPutAuth = request.Headers.Authorization?.ToString();
                    capturedPutBody = request.Content is StreamContent streamContent
                        ? ReadFully(streamContent) : null;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":\"vid123\"}", Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var tokens = new GoogleTokens
            {
                AccessToken = "token-1",
                RefreshToken = "refresh-1",
                AccessTokenExpiry = DateTime.UtcNow.AddHours(1),
                Email = "tester@example.com",
                Scopes = ["https://www.googleapis.com/auth/youtube.upload"],
            };
            using var oauth = new GoogleOAuthClient("fake-client-id", handler: handler);
            var uploader = new YouTubeUploader(tokens, handler);
            string videoPath = Path.Combine(fakeDir, "clip.mp4");
            byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
            File.WriteAllBytes(videoPath, payload);

            var progressReports = new List<string>();
            var progress = new SynchronousProgress(s => progressReports.Add(s));
            var result = uploader.UploadAsync(oauth, videoPath, "테스트", "설명", isPublic: false, progress)
                .GetAwaiter().GetResult();
            Check("youtube-upload-returns-id", result.FileId == "vid123" && result.Url == "https://youtu.be/vid123", result.Url);
            Check("initiate-has-bearer", capturedInitiateAuth == "Bearer token-1", capturedInitiateAuth ?? "");
            Check("initiate-bare-json-content-type", capturedInitiateContentType == "application/json", capturedInitiateContentType ?? "");
            Check("put-has-bearer", capturedPutAuth == "Bearer token-1", capturedPutAuth ?? "");
            Check("put-carries-file-bytes", capturedPutBody is { Length: 8 } b && b[0] == 1 && b[7] == 8, $"len={capturedPutBody?.Length}");
            Check("youtube-privacy-private", capturedInitiateBody?.Contains("\"privacyStatus\":\"private\"") == true);
            Check("upload-progress-reported", progressReports.Count >= 1 && progressReports[^1].Contains("완료"),
                string.Join(" | ", progressReports));

            // public flag maps through
            string? publicBody = null;
            var publicHandler = new FakeHandler(request =>
            {
                if (request.Method == HttpMethod.Post && request.RequestUri!.ToString().Contains("youtube"))
                {
                    publicBody = (request.Content as StringContent)!
                        .ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Headers = { Location = new Uri("https://upload.fake/s2") },
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"id\":\"v2\"}", Encoding.UTF8, "application/json") };
            });
            new YouTubeUploader(tokens, publicHandler)
                .UploadAsync(new GoogleOAuthClient("fake-client-id", handler: publicHandler),
                    videoPath, "t", "d", isPublic: true)
                .GetAwaiter().GetResult();
            Check("youtube-privacy-public-flag", publicBody?.Contains("\"privacyStatus\":\"public\"") == true);

            // ---- HTTP failure surfaces a Korean-friendly error, not a raw dump ----
            var failingHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("{\"error\":{\"code\":403,\"message\":\"quotaExceeded\"}}", Encoding.UTF8, "application/json") });
            try
            {
                new YouTubeUploader(tokens, failingHandler)
                    .UploadAsync(new GoogleOAuthClient("fake-client-id", handler: failingHandler), videoPath, "t", "d", false)
                    .GetAwaiter().GetResult();
                Check("http-failure-throws", false, "no exception");
            }
            catch (Exception ex)
            {
                Check("http-failure-throws", ex.Message.Contains("quotaExceeded"), ex.Message.Split('\n')[0]);
            }

            // ---- upload cancellation: cancelling mid-send must surface as OCE ----
            var cancelCts = new CancellationTokenSource();
            var cancelHandler = new FakeHandler(request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    // User hits 취소 between the initiate handshake and the byte transfer.
                    cancelCts.Cancel();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Headers = { Location = new Uri("https://upload.fake/s3") } };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"id\":\"never\"}") };
            });
            bool cancelObserved = false;
            try
            {
                new YouTubeUploader(tokens, cancelHandler)
                    .UploadAsync(new GoogleOAuthClient("fake-client-id", handler: cancelHandler),
                        videoPath, "t", "d", false, null, cancelCts.Token)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { cancelObserved = true; }
            catch (Exception ex) when (ex.InnerException is OperationCanceledException) { cancelObserved = true; }
            Check("upload-cancel-observed", cancelObserved);

            // ---- 2. Drive multipart upload ----
            string? driveAuth = null; string? driveBoundary = null; byte[]? driveBody = null;
            var driveHandler = new FakeHandler(request =>
            {
                if (request.RequestUri!.ToString().Contains("/upload/drive/v3/files"))
                {
                    driveAuth = request.Headers.Authorization?.ToString();
                    driveBoundary = request.Content?.Headers.ContentType?.Parameters
                        .FirstOrDefault(p => p.Name == "boundary")?.Value?.Trim('"');
                    driveBody = ReadFully(request.Content!);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":\"file9\",\"webViewLink\":\"https://drive.fake/file9\"}",
                            Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            var driveTokens = new GoogleTokens
            {
                AccessToken = "drive-1", RefreshToken = "r", AccessTokenExpiry = DateTime.UtcNow.AddHours(1),
                Scopes = ["https://www.googleapis.com/auth/drive.file"],
            };
            using var driveOauth = new GoogleOAuthClient("fake-client-id", handler: driveHandler);
            var driveResult = new GoogleDriveUploader(driveTokens, driveHandler)
                .UploadAsync(driveOauth, videoPath).GetAwaiter().GetResult();
            Check("drive-upload-returns-link", driveResult.FileId == "file9" && driveResult.Url == "https://drive.fake/file9", driveResult.Url);
            Check("drive-has-bearer", driveAuth == "Bearer drive-1", driveAuth ?? "");
            Check("drive-multipart-boundary", !string.IsNullOrEmpty(driveBoundary), driveBoundary ?? "");
            Check("drive-body-has-metadata-and-bytes", driveBody is { Length: > 8 }
                && Encoding.UTF8.GetString(driveBody).Contains("\"name\":\"clip.mp4\""),
                $"len={driveBody?.Length}");

            // ---- 3. scope gate: granted scopes decide, never file existence ----
            var narrow = new GoogleTokens { Scopes = ["openid", "email"] };
            Check("scope-missing-rejected", !GoogleOAuthClient.HasScope(narrow, GoogleOAuthClient.ScopeYouTube));
            Check("scope-present-accepted", GoogleOAuthClient.HasScope(tokens, GoogleOAuthClient.ScopeYouTube));
            var noScopes = new GoogleTokens { Scopes = [] };
            Check("scope-empty-rejected", !GoogleOAuthClient.HasScope(noScopes, GoogleOAuthClient.ScopeDrive));

            // ---- 4. TokenStore: DPAPI roundtrip in GUID Temp, atomic replace, corruption, delete ----
            TokenStore.Save(GoogleService.YouTube, tokens);
            Check("tokenstore-file-in-temp-dir",
                File.Exists(Path.Combine(fakeCaptureCanva, "auth-youtube.bin")));
            var loaded = TokenStore.Load(GoogleService.YouTube);
            Check("tokenstore-roundtrip", loaded is { } l && l.AccessToken == "token-1" && l.Email == "tester@example.com");

            // Windows sharing violations must surface to the UI; a failed delete must not
            // be mistaken for successful sign-out while credentials remain on disk.
            using (var lockedToken = new FileStream(Path.Combine(fakeCaptureCanva, "auth-youtube.bin"),
                FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool loadFailed = false, deleteFailed = false;
                try { TokenStore.Load(GoogleService.YouTube); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { loadFailed = true; }
                try { TokenStore.Delete(GoogleService.YouTube); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { deleteFailed = true; }
                Check("tokenstore-read-lock-surfaces", loadFailed);
                Check("tokenstore-delete-lock-preserves-connection", deleteFailed && TokenStore.IsConnected(GoogleService.YouTube));
            }

            var refreshed = new GoogleTokens
            {
                AccessToken = "token-2", RefreshToken = tokens.RefreshToken,
                AccessTokenExpiry = tokens.AccessTokenExpiry, Email = tokens.Email, Scopes = tokens.Scopes,
            };
            TokenStore.Save(GoogleService.YouTube, refreshed);
            Check("tokenstore-atomic-replace", TokenStore.Load(GoogleService.YouTube)?.AccessToken == "token-2");

            File.WriteAllBytes(Path.Combine(fakeCaptureCanva, "auth-drive.bin"), [0xDE, 0xAD]);
            Check("tokenstore-corrupt-blob-signs-out", TokenStore.Load(GoogleService.Drive) == null);

            TokenStore.Delete(GoogleService.YouTube);
            Check("tokenstore-delete", TokenStore.Load(GoogleService.YouTube) == null);

            // ---- 5. HotkeySettings: roundtrip + cleared-stays-null + failure surfaces ----
            HotkeySettings.Save(new HotkeySettings
            {
                RecordModifiers = 0x0001, RecordVirtualKey = 0x70, RecordName = "F1",
            });
            var reloadedHotkeys = HotkeySettings.Load();
            Check("hotkey-record-roundtrip", reloadedHotkeys.Record is { } rec && rec.VirtualKey == 0x70 && rec.Name == "F1");
            Check("hotkey-cleared-stays-null", reloadedHotkeys.Pause == null);
            // Failure surfaces: point the store at an impossible path and expect an exception.
            string realHotkeyPath = Path.Combine(fakeCaptureCanva, "hotkeys.json");
            File.SetAttributes(realHotkeyPath, FileAttributes.ReadOnly);
            HotkeySettings.DirectoryOverride = Path.Combine(fakeDir, "readonly-dir");
            Directory.CreateDirectory(Path.Combine(fakeDir, "readonly-dir"));
            Directory.CreateDirectory(Path.Combine(fakeDir, "readonly-dir", "hotkeys.json")); // blocks the file write
            bool saveThrew = false;
            try { HotkeySettings.Save(new HotkeySettings()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { saveThrew = true; }
            Check("hotkey-save-failure-surfaces", saveThrew);
            File.SetAttributes(realHotkeyPath, FileAttributes.Normal);
            HotkeySettings.DirectoryOverride = fakeCaptureCanva;

            // ---- 6. OAuth wait: state validation + cancellation semantics (no browser) ----
            // Refresh rejection uses fake HTTP; state/cancellation tests below use a real
            // loopback listener through the same callback wait that the app uses.
            var exchangeHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json") });
            try
            {
                new GoogleOAuthClient("fake-client-id", handler: exchangeHandler)
                    .RefreshAsync(new GoogleTokens { RefreshToken = "bad" })
                    .GetAwaiter().GetResult();
                Check("oauth-refresh-invalid-grant-throws", false, "no exception");
            }
            catch (InvalidOperationException ex)
            {
                Check("oauth-refresh-invalid-grant-throws", ex.Message.Contains("invalid_grant"), ex.Message.Split('\n')[^1]);
            }

            // Cancelled wait must surface OperationCanceledException, not a timeout error.
            using var cancelListener = StartListener(out _);
            using var cancelWaitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var waitTask = GoogleOAuthClient.WaitForAuthorizationCodeForTestAsync(cancelListener, "state", cancelWaitCts.Token);
            bool sawOce = false;
            try { waitTask.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { sawOce = true; }
            Check("oauth-wait-cancel-is-oce", sawOce);
            Check("oauth-cancel-closes-listener", !cancelListener.IsListening);

            using var callbackListener = StartListener(out string callbackUrl);
            using var callbackCancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var callbackTask = GoogleOAuthClient.WaitForAuthorizationCodeForTestAsync(callbackListener, "expected", callbackCancel.Token);
            using var localHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            localHttp.GetStringAsync(callbackUrl + "?state=wrong&error=access_denied").GetAwaiter().GetResult();
            Check("oauth-wrong-state-error-keeps-waiting", !callbackTask.IsCompleted);
            localHttp.GetStringAsync(callbackUrl + "?state=expected&code=approved").GetAwaiter().GetResult();
            Check("oauth-valid-state-returns-code", callbackTask.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult() == "approved");
            Check("oauth-success-closes-listener", !callbackListener.IsListening);

            var partialHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"access_token\":\"partial\",\"refresh_token\":\"refresh\",\"expires_in\":3600,\"scope\":\"openid email\"}") });
            using var partialOAuth = new GoogleOAuthClient("fake-client-id", handler: partialHandler);
            var partial = partialOAuth.ExchangeCodeForTestAsync([GoogleOAuthClient.ScopeYouTube]).GetAwaiter().GetResult();
            Check("oauth-partial-response-does-not-invent-scope", !GoogleOAuthClient.HasScope(partial, GoogleOAuthClient.ScopeYouTube));
            Check("isolation-all-effective-paths-are-temp", effectivePaths.All(path => Path.GetFullPath(path).StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            // Reset overrides even on failure so a rerun cannot inherit stale state.
            TokenStore.DirectoryOverride = null;
            HotkeySettings.DirectoryOverride = null;
            GoogleClientConfig.CandidatePathOverride = null;
            try { Directory.Delete(fakeDir, recursive: true); } catch { /* best effort */ }
        }

        // Behavioral coverage for the YouTube/OAuth release review. Its failures MUST fail the
        // process: aggregate the counts so the new-suite result cannot be masked by this counter.
        YoutubeFlowChecks.Run();

        int totalFail = _fail + YoutubeFlowChecks.FailCount;
        Console.WriteLine("---");
        Console.WriteLine((totalFail == 0 ? "ALL PASS: " : "FAILURES: ") + (_pass + YoutubeFlowChecks.TotalPass) +
            " passed, " + totalFail + " failed  (offline harness: fake HTTP + GUID Temp dirs, no real Google access, no user files)");
        return totalFail == 0 ? 0 : 1;
    }

    private static byte[] ReadFully(HttpContent content)
    {
        using var stream = content.ReadAsStream(CancellationToken.None);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static HttpListener StartListener(out string url)
    {
        var reservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var listener = new HttpListener();
        url = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(url);
        listener.Start();
        return listener;
    }

    private sealed class SynchronousProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }
}

/// <summary>Scripted HttpMessageHandler; asserts requests without any real network.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}
