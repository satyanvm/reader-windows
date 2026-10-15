using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Reader.Core.Sync;

/// <summary>A signed-in session: the bearer token for the sync API and who it belongs to.</summary>
public sealed record AccountSessionInfo(string Token, string Email);

/// <summary>What the server said went wrong, with its stable <see cref="Code"/> (e.g. "verification_required").</summary>
public sealed class AccountException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The account endpoints of the sync server (server/src/worker.js, auth.js).</summary>
public sealed class AccountApi
{
    public Uri BaseUrl { get; }
    private readonly HttpClient _http;

    public AccountApi(Uri baseUrl, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        // Paths are resolved relative to the base, so it must end in a slash.
        BaseUrl = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Which sign-in options to show. <see cref="EmailCodes"/>: new accounts confirm their email
    /// with a code, and passwords can be reset. Servers that don't say have codes on.
    /// </summary>
    public sealed record Providers(bool Apple, bool Google, bool EmailCodes = true);

    public async Task<Providers> ProvidersAsync()
    {
        using var doc = JsonDocument.Parse(await CallAsync(HttpMethod.Get, "v1/auth/providers"));
        var o = doc.RootElement;
        bool Get(string k, bool? fallback)
        {
            if (o.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)) return v.GetBoolean();
            return fallback ?? throw new AccountException("bad_response", $"providers: missing {k}");
        }
        return new Providers(Get("apple", null), Get("google", null), Get("emailCodes", true));
    }

    /// <summary>
    /// Creates the account (or refreshes an unconfirmed one) and emails a code; returns null then.
    /// A server with email codes off signs the new account in at once and returns its session.
    /// </summary>
    public async Task<AccountSessionInfo?> SignUpAsync(string email, string password)
    {
        var data = await CallAsync(HttpMethod.Post, "v1/auth/signup", new() { ["email"] = email, ["password"] = password });
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("token", out var t) || t.ValueKind != JsonValueKind.String) return null;
        }
        catch (JsonException) { return null; }
        return SessionInfo(data);
    }

    public async Task<AccountSessionInfo> VerifyAsync(string email, string code) =>
        SessionInfo(await CallAsync(HttpMethod.Post, "v1/auth/verify", new() { ["email"] = email, ["code"] = code }));

    public async Task ResendCodeAsync(string email) =>
        await CallAsync(HttpMethod.Post, "v1/auth/resend", new() { ["email"] = email });

    /// <summary>Throws <see cref="AccountException"/> with code "verification_required" for an unconfirmed email (a new code has been sent).</summary>
    public async Task<AccountSessionInfo> SignInAsync(string email, string password) =>
        SessionInfo(await CallAsync(HttpMethod.Post, "v1/auth/signin", new() { ["email"] = email, ["password"] = password }));

    public async Task ForgotPasswordAsync(string email) =>
        await CallAsync(HttpMethod.Post, "v1/auth/password/forgot", new() { ["email"] = email });

    public async Task<AccountSessionInfo> ResetPasswordAsync(string email, string code, string password) =>
        SessionInfo(await CallAsync(HttpMethod.Post, "v1/auth/password/reset",
            new() { ["email"] = email, ["code"] = code, ["password"] = password }));

    /// <summary>Exchanges an Apple or Google ID token for a session.</summary>
    public async Task<AccountSessionInfo> SignInWithProviderAsync(string provider, string idToken) =>
        SessionInfo(await CallAsync(HttpMethod.Post, $"v1/auth/{provider}", new() { ["idToken"] = idToken }));

    public async Task SignOutAsync(string token)
    {
        try { await CallAsync(HttpMethod.Post, "v1/auth/signout", new(), token); }
        catch { /* the session is dropped locally either way */ }
    }

    private static AccountSessionInfo SessionInfo(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var o = doc.RootElement;
            var token = o.GetProperty("token").GetString() ?? throw new AccountException("bad_response", "no token");
            var email = o.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.Object &&
                        a.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() ?? "" : "";
            return new AccountSessionInfo(token, email);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AccountException("bad_response", "The sync server sent an answer Reader doesn't understand.");
        }
    }

    private async Task<byte[]> CallAsync(HttpMethod method, string path, Dictionary<string, string>? body = null, string? token = null)
    {
        using var req = new HttpRequestMessage(method, new Uri(BaseUrl, path));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        HttpResponseMessage response;
        byte[] data;
        try
        {
            response = await _http.SendAsync(req).ConfigureAwait(false);
            data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch
        {
            throw new AccountException("offline", "Couldn't reach the sync server. Check your connection and try again.");
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300) return data;
            string? code = null, error = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                if (doc.RootElement.TryGetProperty("error", out var m) && m.ValueKind == JsonValueKind.String) error = m.GetString();
            }
            catch (JsonException) { }
            throw new AccountException(code ?? $"http_{status}", error ?? $"The sync server answered with an error ({status}).");
        }
    }
}
