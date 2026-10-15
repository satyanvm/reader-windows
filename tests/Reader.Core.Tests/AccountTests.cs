using System.Net;
using System.Text;
using System.Text.Json;
using Reader.Core.App;
using Reader.Core.Data;
using Reader.Core.Model;
using Reader.Core.Sync;
using Xunit;
using static Reader.Core.Tests.T;

namespace Reader.Core.Tests;

/// <summary>The account endpoints of the sync server, in memory (auth.js's behaviour, minus crypto).</summary>
public sealed class FakeAccountServer : HttpMessageHandler
{
    public bool EmailCodes;
    public bool Google;
    public readonly Dictionary<string, (string Password, bool Verified)> Accounts = new();
    public readonly Dictionary<string, string> Codes = new();
    public readonly HashSet<string> Tokens = new();
    public readonly List<string> Log = new();
    private int _n;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        Log.Add($"{request.Method} {path}");
        var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement;
        string S(string k) => body.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
        switch (path)
        {
            case "/v1/auth/providers":
                return Json(200, new { apple = false, google = Google, emailCodes = EmailCodes });
            case "/v1/auth/signup":
                if (Accounts.TryGetValue(S("email"), out var existing) && existing.Verified) return Json(409, new { error = "An account with this email already exists.", code = "account_exists" });
                Accounts[S("email")] = (S("password"), !EmailCodes);
                if (!EmailCodes) return Session(S("email"));
                Codes[S("email")] = "123456";
                return Json(202, new { status = "code_sent" });
            case "/v1/auth/verify":
                if (Codes.GetValueOrDefault(S("email")) != S("code")) return Json(400, new { error = "That code isn't right.", code = "invalid_code" });
                Accounts[S("email")] = (Accounts[S("email")].Password, true);
                return Session(S("email"));
            case "/v1/auth/signin":
                if (!Accounts.TryGetValue(S("email"), out var a) || a.Password != S("password")) return Json(401, new { error = "Wrong email or password.", code = "invalid_credentials" });
                if (!a.Verified) { Codes[S("email")] = "123456"; return Json(403, new { error = "Confirm your email first.", code = "verification_required" }); }
                return Session(S("email"));
            case "/v1/auth/password/forgot":
                Codes[S("email")] = "654321";
                return Json(200, new { status = "code_sent" });
            case "/v1/auth/password/reset":
                if (Codes.GetValueOrDefault(S("email")) != S("code")) return Json(400, new { error = "That code isn't right.", code = "invalid_code" });
                Accounts[S("email")] = (S("password"), true);
                return Session(S("email"));
            case "/v1/auth/signout":
                Tokens.Remove(request.Headers.Authorization?.Parameter ?? "");
                return Json(200, new { status = "signed_out" });
        }
        if (path.StartsWith("/v1/reading-positions/"))
        {
            if (!Tokens.Contains(request.Headers.Authorization?.Parameter ?? "")) return Json(401, new { error = "missing or invalid token", code = "unauthorized" });
            return Json(404, new { error = "none" });
        }
        return Json(404, new { error = "not found" });
    }

    private HttpResponseMessage Session(string email)
    {
        var token = $"tok{++_n}";
        Tokens.Add(token);
        return Json(200, new { token, account = new { email } });
    }

    private static HttpResponseMessage Json(int status, object o) =>
        new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
}

public class AccountTests
{
    private readonly FakeAccountServer _server = new();
    private readonly InMemorySessionStore _store = new();
    private readonly ReadingPositionRepository _positions = new(new InMemoryPositionStore(), new HybridClock(), "win");

    private SyncAccount Make(out SyncEngine engine)
    {
        engine = new SyncEngine(_positions, null, new StaticTokenSession(""));
        return new SyncAccount(new Uri("https://reader.test"), _store, engine, new SyncScheduler(engine), _positions, _server);
    }

    [Fact]
    public void SignUpWithCodesOffSignsInAtOnceAndConfiguresSync() => Run(async () =>
    {
        var account = Make(out var engine);
        var sessions = new List<bool>();
        account.OnSessionChanged = sessions.Add;
        await account.CreateAccountAsync(" Reader@Example.com ", "correct horse");
        Assert.Equal(new AccountStage.SignedIn("reader@example.com"), account.Stage);
        Assert.True(engine.IsEnabled);
        Assert.Equal("reader@example.com", _store.Session?.Email);
        Assert.Equal([true], sessions);
    });

    [Fact]
    public void WithCodesOnANewAccountConfirmsItsEmail() => Run(async () =>
    {
        _server.EmailCodes = true;
        var account = Make(out _);
        await account.LoadProvidersAsync();
        Assert.True(account.Providers.EmailCodes);
        await account.CreateAccountAsync("a@b.c", "password1");
        Assert.Equal(new AccountStage.AwaitingCode("a@b.c", CodePurpose.ConfirmEmail), account.Stage);
        var wrong = await Assert.ThrowsAsync<AccountException>(() => account.ConfirmAsync("000000"));
        Assert.Equal("invalid_code", wrong.Code);
        await account.ConfirmAsync(" 123456 ");
        Assert.True(account.IsSignedIn);
    });

    [Fact]
    public void SigningInToAnUnconfirmedAccountGoesToTheCodeStep() => Run(async () =>
    {
        _server.EmailCodes = true;
        _server.Accounts["a@b.c"] = ("pw123456", false);
        var account = Make(out _);
        await account.SignInAsync("a@b.c", "pw123456");
        Assert.Equal(new AccountStage.AwaitingCode("a@b.c", CodePurpose.ConfirmEmail), account.Stage);
        account.CancelCode();
        Assert.Equal(new AccountStage.SignedOut(), account.Stage);
    });

    [Fact]
    public void ForgotPasswordSetsANewOneWithTheEmailedCode() => Run(async () =>
    {
        _server.EmailCodes = true;
        _server.Accounts["a@b.c"] = ("old", true);
        var account = Make(out _);
        await account.ForgotPasswordAsync("a@b.c");
        Assert.Equal(new AccountStage.AwaitingCode("a@b.c", CodePurpose.ResetPassword), account.Stage);
        await account.ResetPasswordAsync("654321", "new password");
        Assert.True(account.IsSignedIn);
        Assert.Equal("new password", _server.Accounts["a@b.c"].Password);
    });

    [Fact]
    public void WrongPasswordShowsTheServersMessage() => Run(async () =>
    {
        _server.Accounts["a@b.c"] = ("right", true);
        var account = Make(out _);
        var e = await Assert.ThrowsAsync<AccountException>(() => account.SignInAsync("a@b.c", "wrong"));
        Assert.Equal("Wrong email or password.", e.Message);
        Assert.False(account.IsSignedIn);
    });

    [Fact]
    public void AnUnauthorizedAnswerSignsOutWithAnExplanationAndKeepsUnsyncedPositions() => Run(async () =>
    {
        var account = Make(out var engine);
        await account.CreateAccountAsync("a@b.c", "pw");
        _positions.SaveLocal(Pos(42));
        _server.Tokens.Clear(); // signed out everywhere
        Assert.Null(await engine.PullAsync(DOC));
        await Task.Yield();
        Assert.False(account.IsSignedIn);
        Assert.Equal(SyncAccount.SignedOutMessage, account.LastError);
        Assert.Null(_store.Session);
        Assert.Single(_positions.Pending()); // kept for the next account
    });

    [Fact]
    public void TheSessionComesBackAfterARestart() => Run(async () =>
    {
        var first = Make(out _);
        await first.CreateAccountAsync("a@b.c", "pw");
        var again = Make(out var engine);
        Assert.Equal(new AccountStage.SignedIn("a@b.c"), again.Stage);
        Assert.True(engine.IsEnabled);
        again.SignOut();
        Assert.Null(_store.Session);
        Assert.Contains("POST /v1/auth/signout", _server.Log);
    });

    [Fact]
    public void ServerUrlComesFromTheEnvironmentOrTheDefault()
    {
        Assert.Equal(new Uri("http://127.0.0.1:8787"), SyncAccount.ConfiguredServer("http://127.0.0.1:8787"));
        Assert.Equal(new Uri(SyncAccount.DefaultServer), SyncAccount.ConfiguredServer(Environment.GetEnvironmentVariable("READER_SYNC_BASE_URL") is null ? null : SyncAccount.DefaultServer));
    }

    [Fact]
    public void SettingsRoundTripAndDefaultToSepiaPageByPage()
    {
        var path = Path.Combine(TempDir(), "settings.json");
        var s = ReaderSettings.Load(path);
        Assert.Equal(ReaderTheme.Sepia, s.Theme);
        Assert.True(s.PageByPage);
        Assert.False(s.TwoPages);
        s.Theme = ReaderTheme.Black;
        s.TwoPages = true;
        s.TextScale = 1.15f;
        s.Save();
        var back = ReaderSettings.Load(path);
        Assert.Equal(ReaderTheme.Black, back.Theme);
        Assert.True(back.TwoPages);
        Assert.Equal(1.15f, back.TextScale);
        Assert.Equal(1.3f, ReaderSettings.NextStep(ReaderSettings.TextSteps, 1.15f));
        Assert.Equal(1f, ReaderSettings.PreviousStep(ReaderSettings.TextSteps, 1.15f));
        Assert.Equal(1f, ReaderSettings.PreviousStep(ReaderSettings.ZoomSteps, 1f));
        Assert.Equal(4f, ReaderSettings.NextStep(ReaderSettings.ZoomSteps, 4f));
        back.Theme = ReaderTheme.White;
        back.Save();
        Assert.Contains("\"White\"", File.ReadAllText(path));
        Assert.Equal(ReaderTheme.White, ReaderSettings.Load(path).Theme);
    }

    [Fact]
    public void ThemesHaveExactlyTheSpecifiedColors()
    {
        Assert.Equal(new ThemeColors(0x242427, 0xDCDCDE, 0x8A8A8E, true), ThemeColors.For(ReaderTheme.Dark));
        Assert.Equal(new ThemeColors(0xF5ECD9, 0x4A3A2A, 0x9C8B75, false), ThemeColors.For(ReaderTheme.Sepia));
        Assert.Equal(new ThemeColors(0x000000, 0xC4C4C4, 0x6E6E6E, true), ThemeColors.For(ReaderTheme.Black));
        Assert.Equal(new ThemeColors(0xFFFFFF, 0x1C1C1E, 0x86868B, false), ThemeColors.For(ReaderTheme.White));
        Assert.Equal(4, Enum.GetValues<ReaderTheme>().Length);
    }

    [Fact]
    public void TheDeviceIdIsARandomUuidKeptInTheDataFolder()
    {
        var dir = TempDir();
        var id = AppPaths.DeviceId(dir);
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(id, AppPaths.DeviceId(dir));
        Assert.NotEqual(id, AppPaths.DeviceId(TempDir()));
    }
}

public class CubicBezierTests
{
    [Fact]
    public void ThePageTurnCurveIsAnEaseOutFromZeroToOne()
    {
        var c = CubicBezier.PageTurn;
        Assert.Equal(0, c.Ease(0));
        Assert.Equal(1, c.Ease(1));
        var last = 0.0;
        for (var i = 1; i <= 100; i++)
        {
            var v = c.Ease(i / 100.0);
            Assert.True(v >= last - 1e-9, "monotonic");
            last = v;
        }
        Assert.True(c.Ease(0.25) > 0.6, "most of the movement happens early (ease-out)");
        Assert.True(Math.Abs(new CubicBezier(0, 0, 1, 1).Ease(0.37) - 0.37) < 1e-5, "the linear curve is linear");
        // CSS ease (0.25, 0.1, 0.25, 1) at 50% is about 0.8024.
        Assert.True(Math.Abs(new CubicBezier(0.25, 0.1, 0.25, 1).Ease(0.5) - 0.8024) < 0.001);
    }
}
