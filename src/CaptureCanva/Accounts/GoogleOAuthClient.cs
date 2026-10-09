using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CaptureCanva.Accounts;

/// <summary>OAuth tokens for one Google account, kept in memory and persisted encrypted.</summary>
public sealed class GoogleTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    /// <summary>UTC instant when <see cref="AccessToken"/> expires (with a safety margin applied).</summary>
    public DateTime AccessTokenExpiry { get; set; }
    /// <summary>Google account e-mail, shown in the UI.</summary>
    public string Email { get; set; } = "";
    /// <summary>Scopes the user consented to; the token must not be used for anything else.</summary>
    public string[] Scopes { get; set; } = [];
}

/// <summary>
/// Minimal Google OAuth 2.0 client for a desktop app: opens the system browser for consent,
/// catches the redirect on a short-lived localhost loopback listener (RFC 8252 loopback IP
/// redirect) and exchanges the code with PKCE (RFC 7636). No redirect URIs need to be
/// pre-registered beyond <c>http://localhost:PORT/</c> which is allowed for installed apps.
/// </summary>
public sealed class GoogleOAuthClient : IDisposable
{
    public const string ScopeYouTube = "https://www.googleapis.com/auth/youtube.upload";
    public const string ScopeDrive = "https://www.googleapis.com/auth/drive.file";
    public const string ScopeUserInfo = "openid email";

    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string? _clientSecret; // optional for installed-app clients
    private readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

    /// <summary>handler is for tests only: supply a fake to run the flow without network.</summary>
    public GoogleOAuthClient(string clientId, string? clientSecret = null, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException(
                "Google OAuth 클라이언트 ID가 설정되지 않았습니다.\n" +
                "Google Cloud Console(https://console.cloud.google.com/apis/credentials)에서\n" +
                "'데스크톱 앱' 유형의 OAuth 클라이언트를 만들고 client_id를 등록하세요.");
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    /// <summary>Runs the full authorization-code + PKCE flow and returns fresh tokens.</summary>
    public async Task<GoogleTokens> AuthorizeAsync(string[] scopes, CancellationToken cancellation = default)
    {
        string verifier = RandomBase64Url(48);
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        using var listener = new System.Net.HttpListener();
        // HttpListener cannot report a wildcard-bound port, so reserve a free one with a socket
        // first and bind the listener to it explicitly.
        int port = ReserveFreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string state = RandomBase64Url(16);
        string redirectUri = $"http://127.0.0.1:{port}/";

        var query = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(" ", scopes),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",      // needed to get a refresh_token
            ["prompt"] = "consent",           // re-issue refresh_token even on repeat consents
            ["include_granted_scopes"] = "false",
        };
        string authorizeUrl = AuthEndpoint + "?" + string.Join("&",
            query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        // Open the user's default browser. The listener must already be running.
        Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

        string? code = await WaitForAuthorizationCodeAsync(listener, state, cancellation);
        listener.Stop();

        return await ExchangeCodeAsync(code!, verifier, redirectUri, scopes, cancellation);
    }

    /// <summary>Exchanges a refresh token for a fresh access token (in place on the argument).</summary>
    public async Task RefreshAsync(GoogleTokens tokens, CancellationToken cancellation = default)
    {
        using var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret ?? "",
            ["refresh_token"] = tokens.RefreshToken,
            ["grant_type"] = "refresh_token",
        });
        using var response = await _http.PostAsync(TokenEndpoint, request, cancellation);
        string body = await response.Content.ReadAsStringAsync(cancellation);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Google 토큰 갱신에 실패했습니다.\n" + Summarize(body));
        using var json = JsonDocument.Parse(body);
        tokens.AccessToken = json.RootElement.GetProperty("access_token").GetString()!;
        int expiresIn = json.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        // Refresh a minute early so requests never start with a token that dies mid-upload.
        tokens.AccessTokenExpiry = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(60, expiresIn - 60));
        // Track the grant state exactly as Google reports it at refresh time:
        //  - a scope field (Google may NARROW it) replaces the stored set, so HasScope gates
        //    judge by the current grant, never by what we asked for at authorize time;
        //  - an ABSENT scope field is not a revocation — preserve the stored grant;
        //  - a rotated refresh_token replaces the stored one; an absent field keeps it.
        if (json.RootElement.TryGetProperty("scope", out var scope))
            tokens.Scopes = (scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (json.RootElement.TryGetProperty("refresh_token", out var rotated)
            && !string.IsNullOrEmpty(rotated.GetString()))
            tokens.RefreshToken = rotated.GetString()!;
    }

    /// <summary>Google ID tokens carry the e-mail in the payload; the userinfo endpoint is simpler.</summary>
    public async Task<string> GetEmailAsync(GoogleTokens tokens, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        request.Headers.Authorization = new("Bearer", tokens.AccessToken);
        using var response = await _http.SendAsync(request, cancellation);
        string body = await response.Content.ReadAsStringAsync(cancellation);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Google 계정 정보를 가져오지 못했습니다.\n" + Summarize(body));
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("email", out var email) ? email.GetString() ?? "" : "";
    }

    /// <summary>True when the granted scope list covers the requested scope (exact match;
    /// Google returns narrower scopes verbatim, never supersets of a different product).</summary>
    public static bool HasScope(GoogleTokens tokens, string scope) =>
        tokens.Scopes is { Length: > 0 } granted
            && granted.Contains(scope, StringComparer.Ordinal);

    public static string Summarize(string errorBody)
    {
        try
        {
            using var json = JsonDocument.Parse(errorBody);
            if (json.RootElement.TryGetProperty("error_description", out var description))
                return description.GetString() ?? errorBody;
            if (json.RootElement.TryGetProperty("error", out var error))
                return error.ToString();
        }
        catch { /* Not JSON - show as-is. */ }
        return errorBody.Length > 300 ? errorBody[..300] + "…" : errorBody;
    }

    /// <summary>Test hook for the production callback wait on a real loopback listener.</summary>
    internal static Task<string?> WaitForAuthorizationCodeForTestAsync(
        System.Net.HttpListener listener, string state, CancellationToken cancellation) =>
        WaitForAuthorizationCodeAsync(listener, state, cancellation);

    internal Task<GoogleTokens> ExchangeCodeForTestAsync(string[] scopes) =>
        ExchangeCodeAsync("test-code", "test-verifier", "http://127.0.0.1/", scopes, CancellationToken.None);

    private static void Respond(System.Net.HttpListenerContext context, string html)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    private static int ReserveFreePort()
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        int port = ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();
        return port;
    }

    /// <summary>
    /// Awaits the authorization callback. Semantics: user cancellation →
    /// <see cref="OperationCanceledException"/>; 5-minute browser timeout → TimeoutException;
    /// Google error callback (access_denied 등) → InvalidOperationException. On timeout the
    /// listener is stopped and the abandoned loop's exception is observed so nothing is swallowed.
    /// </summary>
    private static async Task<string?> WaitForAuthorizationCodeAsync(
        System.Net.HttpListener listener, string state, CancellationToken cancellation)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
          while (true)
          {
            var pending = listener.GetContextAsync();
            System.Net.HttpListenerContext context;
            try { context = await pending.WaitAsync(timeoutCts.Token); }
            catch
            {
                // Stop in finally releases this pending request; observe its resulting fault.
                _ = pending.ContinueWith(t => _ = t.Exception,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                throw;
            }
            string? code = context.Request.QueryString["code"];
            string? returnedState = context.Request.QueryString["state"];
            string? error = context.Request.QueryString["error"];
            // State must be validated before anything else, including the error parameter,
            // so a forged callback cannot make us treat an error as consent-related.
            if (returnedState != state)
            {
                Respond(context, "<html><body style='font-family:sans-serif'><h2>잘못된 응답입니다.</h2>다시 시도해 주세요.</body></html>");
                continue;
            }
            bool ok = !string.IsNullOrEmpty(code);
            Respond(context, ok
                ? "<html><body style='font-family:sans-serif'><h2>승인됨</h2>토큰을 저장하는 중… 이 창은 닫아도 됩니다.</body></html>"
                : $"<html><body style='font-family:sans-serif'><h2>연결 실패</h2>{System.Net.WebUtility.HtmlEncode(error ?? "인증 코드가 전달되지 않았습니다.")}</body></html>");
            return ok ? code : throw new InvalidOperationException(Summarize(
                System.Net.WebUtility.UrlDecode(error ?? "access_denied")));
          }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            // Browser timeout only — rethrow as a distinct, actionable failure.
            throw new TimeoutException("브라우저 승인이 5분 안에 완료되지 않았습니다. 다시 시도해 주세요.");
        }
        catch (OperationCanceledException)
        {
            // User-initiated cancellation: stop the listener and let the OCE propagate.
            throw;
        }
        catch
        {
            throw;
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<GoogleTokens> ExchangeCodeAsync(string code, string verifier, string redirectUri,
        string[] scopes, CancellationToken cancellation)
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret ?? "",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        };
        using var content = new FormUrlEncodedContent(fields);
        using var response = await _http.PostAsync(TokenEndpoint, content, cancellation);
        string body = await response.Content.ReadAsStringAsync(cancellation);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Google 인증 코드 교환에 실패했습니다.\n" + Summarize(body));

        using var json = JsonDocument.Parse(body);
        string? refreshToken = json.RootElement.TryGetProperty("refresh_token", out var refresh)
            ? refresh.GetString() : null;
        if (string.IsNullOrEmpty(refreshToken))
            throw new InvalidOperationException(
                "Google이 refresh_token을 발급하지 않았습니다. 계정 연결을 해제한 뒤 다시 연결해 보세요.");
        int expiresIn = json.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        // Persist what Google actually granted, not what we asked for: the UI and the upload
        // path must judge "connected" by granted scopes, never by the request.
        string[] granted = json.RootElement.TryGetProperty("scope", out var scope)
            ? (scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];
        return new GoogleTokens
        {
            AccessToken = json.RootElement.GetProperty("access_token").GetString()!,
            RefreshToken = refreshToken,
            AccessTokenExpiry = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(60, expiresIn - 60)),
            Scopes = granted,
        };
    }

    private string RandomBase64Url(int bytesCount)
    {
        byte[] buffer = new byte[bytesCount];
        _rng.GetBytes(buffer);
        return Base64Url(buffer);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        _http.Dispose();
        _rng.Dispose();
    }
}
