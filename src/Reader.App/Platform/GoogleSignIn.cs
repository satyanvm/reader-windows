using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reader.Core.Sync;

namespace Reader.App.Platform;

/// <summary>
/// Sign in with Google the way Google recommends for desktop apps: the system browser, a
/// loopback redirect to http://127.0.0.1:{port}/, and PKCE. Needs a Google OAuth client of type
/// "Desktop app" (READER_GOOGLE_CLIENT_ID at build time), which must also be listed in the
/// server's GOOGLE_CLIENT_IDS. The id_token Google returns is exchanged for a Reader session.
/// </summary>
internal static class GoogleSignIn
{
    /// <summary>The client id built into this copy of Reader (or READER_GOOGLE_CLIENT_ID at run time).</summary>
    public static string? ClientId =>
        Clean(Environment.GetEnvironmentVariable("READER_GOOGLE_CLIENT_ID")) ?? Clean(BuildSetting("ReaderGoogleClientId"));

    /// <summary>Google issues "Desktop app" clients a secret that isn't secret; send it if configured.</summary>
    private static string? ClientSecret =>
        Clean(Environment.GetEnvironmentVariable("READER_GOOGLE_CLIENT_SECRET")) ?? Clean(BuildSetting("ReaderGoogleClientSecret"));

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? BuildSetting(string key) =>
        typeof(GoogleSignIn).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    public static async Task<string> IdTokenAsync(CancellationToken cancel)
    {
        var clientId = ClientId ?? throw new AccountException("google_unavailable", "Sign in with Google isn't set up in this build of Reader.");
        var port = FreePort();
        var redirect = $"http://127.0.0.1:{port}/";
        var verifier = RandomUrlSafe(32);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = RandomUrlSafe(16);
        var url = "https://accounts.google.com/o/oauth2/v2/auth?" + Form(new()
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = "openid email",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["prompt"] = "select_account",
        });

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        var contextTask = listener.GetContextAsync();
        var finished = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(5), cancel));
        if (finished != contextTask) throw new OperationCanceledException();
        var context = await contextTask;
        var query = context.Request.QueryString;
        var ok = query["state"] == state && !string.IsNullOrEmpty(query["code"]);
        var page = Encoding.UTF8.GetBytes(ok
            ? "<html><body style=\"font-family:Segoe UI;margin:3em\"><h2>You're signed in to Reader.</h2><p>You can close this tab and go back to the app.</p></body></html>"
            : "<html><body style=\"font-family:Segoe UI;margin:3em\"><h2>Sign-in didn't finish.</h2><p>Go back to Reader and try again.</p></body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = page.Length;
        await context.Response.OutputStream.WriteAsync(page, cancel);
        context.Response.Close();
        if (query["error"] == "access_denied") throw new OperationCanceledException();
        if (!ok) throw new AccountException("google", "Google didn't complete the sign-in.");

        // Exchange the code for tokens; PKCE stands in for a client secret.
        var form = new Dictionary<string, string>
        {
            ["code"] = query["code"]!,
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
        };
        if (ClientSecret is { } secret) form["client_secret"] = secret;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var res = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), cancel);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cancel));
        if (doc.RootElement.TryGetProperty("id_token", out var idToken) && idToken.GetString() is { Length: > 0 } token) return token;
        throw new AccountException("google", "Google didn't return a sign-in token.");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Form(Dictionary<string, string> values) =>
        string.Join("&", values.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
