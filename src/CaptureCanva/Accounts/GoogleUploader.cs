using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CaptureCanva.Accounts;

/// <summary>Wraps a stream and reports each Read's byte count — the plumbing behind real
/// upload-progress feedback (P3: stage strings alone are not progress). When a cancellation
/// token is supplied, reads throw OperationCanceledException as soon as it fires, so an
/// in-flight upload aborts promptly (this is what makes upload-cancel real).</summary>
internal sealed class ReportingStream(Stream inner, Action<int> onRead, CancellationToken token = default) : Stream
{
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count)
    {
        token.ThrowIfCancellationRequested();
        int read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            onRead(read);
            token.ThrowIfCancellationRequested();
        }
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Shared plumbing for Google REST uploads: token lifecycle and authenticated calls.</summary>
public abstract class GoogleUploader : IDisposable
{
    protected readonly HttpClient Http;
    protected GoogleTokens Tokens;

    /// <summary>handler is for tests only: run uploads against a fake endpoint, never real Google.</summary>
    protected GoogleUploader(GoogleTokens tokens, HttpMessageHandler? handler = null)
    {
        Tokens = tokens;
        Http = handler is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(30) }
                               : new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("CaptureCanva/0.2");
    }

    /// <summary>Human-readable account label for status lines.</summary>
    public string AccountEmail => Tokens.Email;

    /// <summary>Refreshes the access token when it is at (or near) expiry.</summary>
    protected async Task EnsureAccessTokenAsync(GoogleOAuthClient oauth, CancellationToken cancellation)
    {
        if (DateTime.UtcNow >= Tokens.AccessTokenExpiry)
        {
            await oauth.RefreshAsync(Tokens, cancellation);
            TokenStore.Save(TokenService, Tokens);
        }
    }

    /// <summary>Reads a Google API error body into a short Korean-friendly message.</summary>
    protected static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        string body = await response.Content.ReadAsStringAsync(cancellation);
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? body;
                if (error.TryGetProperty("message", out var message))
                    return message.GetString() ?? body;
            }
        }
        catch { /* Not JSON. */ }
        return GoogleOAuthClient.Summarize(body);
    }

    protected abstract GoogleService TokenService { get; }

    public void Dispose() => Http.Dispose();

    /// <summary>JSON body POST with the bearer token attached.</summary>
    protected async Task<HttpResponseMessage> PostJsonAsync(string url, string json,
        GoogleOAuthClient oauth, CancellationToken cancellation)
    {
        await EnsureAccessTokenAsync(oauth, cancellation);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.AccessToken);
        return await Http.SendAsync(request, cancellation);
    }
}
