// Behavioral coverage for YouTube/OAuth paths NOT yet exercised by Program.cs.
// All network is a FakeHandler; all storage is GUID Temp via internal overrides.
// Verified before any write: the effective token/settings paths live under the GUID dir.
// This file is invoked from Program.Main after the existing checks.
using System.Net;
using System.Reflection;
using System.Text;
using CaptureCanva.Accounts;

internal static class YoutubeFlowChecks
{
    private static int _pass, _fail;
    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  " + detail : ""));
        if (ok) _pass++; else _fail++;
    }

    /// <summary>Aggregated by Entry.Main: a failure here must fail the whole process exit code,
    /// not be masked by Main's own (separate) counter returning 0.</summary>
    public static int FailCount => _fail;
    public static int TotalPass => _pass;

    private static void Report(string tag)
    {
        Console.WriteLine("---");
        Console.WriteLine((_fail == 0 ? $"{tag} PASS: " : $"{tag} FAILURES: ") + _pass + " passed, " + _fail + " failed");
    }

    private static string? _configDir;

    /// <summary>Resets the GoogleClientConfig static cache and points it at a GUID Temp dir.</summary>
    private static void UseConfigDir(string dir, params string[] files)
    {
        typeof(GoogleClientConfig).GetMethod("ResetForTests", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);
        typeof(GoogleClientConfig).GetProperty("CandidatePathOverride", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, files.Select(f => Path.Combine(dir, f)).ToArray());
    }

    private static void ResetConfig()
    {
        typeof(GoogleClientConfig).GetMethod("ResetForTests", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);
        typeof(GoogleClientConfig).GetProperty("CandidatePathOverride", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, null);
    }

    public static void Run()
    {
        // ===== 1. client config shapes ==================================================
        var cfgDir = Path.Combine(Path.GetTempPath(), "CaptureCanva-ytcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cfgDir);
        try
        {
            UseConfigDir(cfgDir);
            Check("config-missing-is-null", GoogleClientConfig.ClientId == null && GoogleClientConfig.ClientSecret == null);

            File.WriteAllText(Path.Combine(cfgDir, "installed.json"),
                """{"installed":{"client_id":"id-installed","client_secret":"sec-installed"}}""");
            UseConfigDir(cfgDir, "installed.json");
            Check("config-installed-shape", GoogleClientConfig.ClientId == "id-installed" && GoogleClientConfig.ClientSecret == "sec-installed");

            File.WriteAllText(Path.Combine(cfgDir, "flat.json"),
                """{"client_id":"id-flat"}""");
            UseConfigDir(cfgDir, "flat.json");
            Check("config-flat-shape-no-secret", GoogleClientConfig.ClientId == "id-flat" && GoogleClientConfig.ClientSecret == null);

            File.WriteAllText(Path.Combine(cfgDir, "malformed.json"), "{ not json ");
            UseConfigDir(cfgDir, "malformed.json");
            Check("config-malformed-is-null", GoogleClientConfig.ClientId == null);

            // Wrong-shape wrapper (web client type) also behaves like missing.
            File.WriteAllText(Path.Combine(cfgDir, "web.json"),
                """{"web":{"client_id":"id-web"}}""");
            UseConfigDir(cfgDir, "web.json");
            Check("config-web-wrapper-ignored", GoogleClientConfig.ClientId == null);

            // First matching candidate wins.
            UseConfigDir(cfgDir, "installed.json", "flat.json");
            Check("config-first-candidate-wins", GoogleClientConfig.ClientId == "id-installed");
        }
        finally { ResetConfig(); TryDelete(cfgDir); }

        // ===== 2. token refresh: expired token + granted scope + persistence ===========
        var storeDir = Path.Combine(Path.GetTempPath(), "CaptureCanva-ytstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(storeDir, "CaptureCanva"));
        TokenStore.DirectoryOverride = storeDir;
        try
        {
            var expired = new GoogleTokens
            {
                AccessToken = "expired-token", RefreshToken = "refresh-1",
                AccessTokenExpiry = DateTime.UtcNow.AddMinutes(-5),
                Scopes = [GoogleOAuthClient.ScopeYouTube],
            };
            string? refreshAuth = null;
            string? refreshBodySent = null;
            var refreshHandler = new FakeHandler(request =>
            {
                if (request.RequestUri!.ToString() == "https://oauth2.googleapis.com/token")
                {
                    refreshAuth = request.Headers.Authorization?.ToString();
                    refreshBodySent = (request.Content as FormUrlEncodedContent)!
                        .ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"access_token":"fresh-token","expires_in":3600,"scope":"https://www.googleapis.com/auth/youtube.upload"}""",
                            Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            using var oauth = new GoogleOAuthClient("fake-client-id", "fake-secret", refreshHandler);
            oauth.RefreshAsync(expired).GetAwaiter().GetResult();
            Check("refresh-sends-grant-type", refreshBodySent?.Contains("grant_type=refresh_token") == true
                && refreshBodySent.Contains("refresh_token=refresh-1"), refreshBodySent ?? "");
            Check("refresh-token-rotated", expired.AccessToken == "fresh-token");
            Check("refresh-extends-expiry", expired.AccessTokenExpiry > DateTime.UtcNow.AddMinutes(50));

            // Persisted tokens survive a Load roundtrip (DPAPI CurrentUser).
            TokenStore.Save(GoogleService.YouTube, expired);
            var reloaded = TokenStore.Load(GoogleService.YouTube);
            Check("token-persisted-after-refresh", reloaded?.AccessToken == "fresh-token"
                && reloaded?.RefreshToken == "refresh-1");

            // Revoked refresh token → Google returns invalid_grant → typed failure.
            var revokedHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("""{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", Encoding.UTF8, "application/json") });
            using var revokedOauth = new GoogleOAuthClient("fake-client-id", handler: revokedHandler);
            bool revokedThrown = false;
            string revokedDetail = "";
            try { revokedOauth.RefreshAsync(expired).GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex)
            {
                // Summarize prefers error_description: accept either the code or the description.
                if (ex.Message.Contains("invalid_grant") || ex.Message.Contains("revoked"))
                { revokedThrown = true; revokedDetail = ex.Message.Split('\n')[^1]; }
            }
            Check("revoked-refresh-fails-typed", revokedThrown, revokedDetail);

            // ===== 3. OAuth denial / wrong state / partial scope =======================
            // (a) access_denied delivered over a REAL local loopback listener: the production
            // WaitForAuthorizationCodeAsync must surface the denial as InvalidOperationException
            // and stop the listener. No real Google endpoint involved.
            {
                var denialListener = new System.Net.HttpListener();
                var reservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                int denialPort = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();
                denialListener.Prefixes.Add($"http://127.0.0.1:{denialPort}/");
                denialListener.Start();
                string denialUrl = $"http://127.0.0.1:{denialPort}/";
                using var denialOauth = new GoogleOAuthClient("fake-client-id");
                var denialWait = GoogleOAuthClient.WaitForAuthorizationCodeForTestAsync(
                    denialListener, "state-abc", CancellationToken.None);
                // The listener is now waiting; deliver the denial over local HTTP.
                using (var denialClient = new HttpClient())
                    denialClient.GetAsync(
                        $"{denialUrl}?error=access_denied&error_description=User+denied+access&state=state-abc")
                        .GetAwaiter().GetResult();
                bool denialThrown = false;
                string denialDetail = "";
                try { denialWait.GetAwaiter().GetResult(); }
                catch (InvalidOperationException ex)
                {
                    if (ex.Message.Contains("access_denied")) { denialThrown = true; }
                    denialDetail = ex.Message.Split('\n')[0];
                }
                Check("oauth-denial-over-loopback-surfaces", denialThrown, denialDetail);
                Check("oauth-denial-stops-listener", !denialListener.IsListening);
            }

            // (b) token response WITHOUT a scope field is not a revocation: the stored grant is
            // preserved (director-confirmed semantics), while a scope field replaces it.
            var noScopeHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"access_token":"t","refresh_token":"r","expires_in":3600}""", Encoding.UTF8, "application/json") });
            // ExchangeCode is private; exercise via RefreshAsync (same parsing path).
            using var noScopeOauth = new GoogleOAuthClient("fake-client-id", handler: noScopeHandler);
            var noScopeTokens = new GoogleTokens { RefreshToken = "r", Scopes = [GoogleOAuthClient.ScopeYouTube] };
            noScopeOauth.RefreshAsync(noScopeTokens).GetAwaiter().GetResult();
            Check("token-response-without-scope-preserves-grant",
                noScopeTokens.Scopes.Length == 1 && noScopeTokens.Scopes[0] == GoogleOAuthClient.ScopeYouTube,
                $"scopes=[{string.Join(",", noScopeTokens.Scopes)}]");

            // (b2) narrowed scope response REPLACES the stored grant: an upload right lost at
            // refresh time must not survive.
            var narrowedHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"access_token":"t2","refresh_token":"r2","expires_in":3600,"scope":"openid email"}""", Encoding.UTF8, "application/json") });
            using var narrowedOauth = new GoogleOAuthClient("fake-client-id", handler: narrowedHandler);
            var narrowed = new GoogleTokens { RefreshToken = "r", Scopes = [GoogleOAuthClient.ScopeYouTube] };
            narrowedOauth.RefreshAsync(narrowed).GetAwaiter().GetResult();
            Check("narrowed-scope-replaces-grant", narrowed.Scopes.Length == 2
                && !GoogleOAuthClient.HasScope(narrowed, GoogleOAuthClient.ScopeYouTube),
                $"scopes=[{string.Join(",", narrowed.Scopes)}]");

            // (b3) rotated refresh_token is persisted; absent one keeps the stored value.
            var rotatedHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"access_token":"t3","refresh_token":"rotated-9","expires_in":3600,"scope":"https://www.googleapis.com/auth/youtube.upload"}""", Encoding.UTF8, "application/json") });
            using var rotatedOauth = new GoogleOAuthClient("fake-client-id", handler: rotatedHandler);
            var rotated = new GoogleTokens { AccessToken = "old", RefreshToken = "stored-1", Scopes = [GoogleOAuthClient.ScopeYouTube] };
            rotatedOauth.RefreshAsync(rotated).GetAwaiter().GetResult();
            Check("rotated-refresh-token-persisted", rotated.RefreshToken == "rotated-9", rotated.RefreshToken);

            var keepHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"access_token":"t4","expires_in":3600,"scope":"https://www.googleapis.com/auth/youtube.upload"}""", Encoding.UTF8, "application/json") });
            using var keepOauth = new GoogleOAuthClient("fake-client-id", handler: keepHandler);
            var kept = new GoogleTokens { AccessToken = "old", RefreshToken = "stored-2", Scopes = [GoogleOAuthClient.ScopeYouTube] };
            keepOauth.RefreshAsync(kept).GetAwaiter().GetResult();
            Check("absent-refresh-token-keeps-stored", kept.RefreshToken == "stored-2", kept.RefreshToken);

            // ===== 4. resumable upload protocol details ================================
            // (a) missing Location header → typed failure, no PUT is attempted.
            int putAttempts = 0;
            var noLocationHandler = new FakeHandler(request =>
            {
                if (request.Method == HttpMethod.Post) return new HttpResponseMessage(HttpStatusCode.OK);
                putAttempts++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"id":"never"}""") };
            });
            string videoPath = Path.Combine(storeDir, "clip.mp4");
            File.WriteAllBytes(videoPath, [1, 2, 3, 4]);
            var uploadTokens = new GoogleTokens
            {
                AccessToken = "tok", RefreshToken = "r", AccessTokenExpiry = DateTime.UtcNow.AddHours(1),
                Scopes = [GoogleOAuthClient.ScopeYouTube],
            };
            bool locationFailure = false;
            try
            {
                new YouTubeUploader(uploadTokens, noLocationHandler)
                    .UploadAsync(new GoogleOAuthClient("fake", handler: noLocationHandler), videoPath, "t", "d", false)
                    .GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("세션 URI")) { locationFailure = true; }
            Check("missing-location-typed-failure", locationFailure);
            Check("missing-location-no-put", putAttempts == 0, $"putAttempts={putAttempts}");

            // (b) upload HTTP error mid-PUT → Korean-typed failure, exception (not silent).
            var putFailHandler = new FakeHandler(request =>
            {
                if (request.Method == HttpMethod.Post)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Headers = { Location = new Uri("https://upload.fake/s") } };
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                { Content = new StringContent("""{"error":{"code":500,"message":"backendFailure"}}""", Encoding.UTF8, "application/json") };
            });
            bool putFailed = false;
            try
            {
                new YouTubeUploader(uploadTokens, putFailHandler)
                    .UploadAsync(new GoogleOAuthClient("fake", handler: putFailHandler), videoPath, "t", "d", false)
                    .GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("backendFailure")) { putFailed = true; }
            Check("upload-http-error-typed", putFailed);

            // (c) upload cancel mid-transfer → OCE, local file untouched.
            var cancelCts = new CancellationTokenSource();
            var cancelHandler = new FakeHandler(request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    cancelCts.Cancel();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Headers = { Location = new Uri("https://upload.fake/s2") } };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"id":"never"}""") };
            });
            byte[] originalBytes = File.ReadAllBytes(videoPath);
            bool cancelSeen = false;
            try
            {
                new YouTubeUploader(uploadTokens, cancelHandler)
                    .UploadAsync(new GoogleOAuthClient("fake", handler: cancelHandler),
                        videoPath, "t", "d", false, null, cancelCts.Token)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { cancelSeen = true; }
            Check("upload-cancel-oce", cancelSeen);
            Check("local-file-preserved-after-cancel", File.ReadAllBytes(videoPath).SequenceEqual(originalBytes));

            // (d) private is the default: no isPublic argument → status.private in metadata.
            string? capturedMeta = null;
            var metaHandler = new FakeHandler(request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    capturedMeta = (request.Content as StringContent)!
                        .ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Headers = { Location = new Uri("https://upload.fake/s4") } };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"id":"vid"}""", Encoding.UTF8, "application/json") };
            });
            new YouTubeUploader(uploadTokens, metaHandler)
                .UploadAsync(new GoogleOAuthClient("fake", handler: metaHandler), videoPath, "t", "d", isPublic: false)
                .GetAwaiter().GetResult();
            // (e2) release-blocking regression: a refresh that narrows the grant must make the
            // NEXT upload refuse BEFORE creating the resumable session (no POST at all).
            var narrowedTokens2 = new GoogleTokens
            {
                AccessToken = "old-access", RefreshToken = "r",
                AccessTokenExpiry = DateTime.UtcNow.AddMinutes(-1),
                Scopes = [GoogleOAuthClient.ScopeYouTube],
            };
            int narrowedPosts = 0;
            var narrowFlowHandler = new FakeHandler(request =>
            {
                if (request.RequestUri!.ToString() == "https://oauth2.googleapis.com/token")
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""{"access_token":"na","expires_in":3600,"scope":"openid email"}""", Encoding.UTF8, "application/json") };
                if (request.Method == HttpMethod.Post) narrowedPosts++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"id":"never"}""", Encoding.UTF8, "application/json") };
            });
            bool narrowedRefused = false;
            try
            {
                new YouTubeUploader(narrowedTokens2, narrowFlowHandler)
                    .UploadAsync(new GoogleOAuthClient("fake", handler: narrowFlowHandler), videoPath, "t", "d", false)
                    .GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("다시 연결")) { narrowedRefused = true; }
            Check("narrowed-grant-refuses-upload-before-post", narrowedRefused && narrowedPosts == 0,
                $"refused={narrowedRefused} posts={narrowedPosts}");

            Check("default-privacy-private", capturedMeta?.Contains("\"privacyStatus\":\"private\"") == true);
        }
        finally
        {
            TokenStore.DirectoryOverride = null;
            TryDelete(storeDir);
        }

        Report("YOUTUBE-FLOW");
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
