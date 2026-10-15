using Reader.Core.Data;

namespace Reader.Core.Sync;

/// <summary>Where the signed-in session is kept between runs (DPAPI-encrypted on Windows).</summary>
public interface ISessionStore
{
    AccountSessionInfo? Load();
    /// <summary>Saves the session, or deletes it for null.</summary>
    void Save(AccountSessionInfo? session);
}

public sealed class InMemorySessionStore : ISessionStore
{
    public AccountSessionInfo? Session;
    public AccountSessionInfo? Load() => Session;
    public void Save(AccountSessionInfo? session) => Session = session;
}

public enum CodePurpose { ConfirmEmail, ResetPassword }

public abstract record AccountStage
{
    public sealed record SignedOut : AccountStage;
    /// <summary>A code was emailed: to confirm a new account, or to reset the password.</summary>
    public sealed record AwaitingCode(string Email, CodePurpose Purpose) : AccountStage;
    public sealed record SignedIn(string Email) : AccountStage;
}

/// <summary>
/// The Reader account on this PC (a port of the Mac app's SyncAccount). Signing in (email and
/// password, with an emailed code when the server has codes on, or Google) gets a session from
/// the sync server; every device signed in to the same account keeps the same reading positions
/// and the same library. Signing in or out reconfigures the running sync engine.
///
/// UI-thread object: methods are called and <see cref="Changed"/> is raised on the UI thread.
/// </summary>
public sealed class SyncAccount
{
    public const string SignedOutMessage =
        "You were signed out (your password changed, or you signed out everywhere). Sign in again to keep syncing.";
    public const string OfflineMessage =
        "Couldn't reach the server. Your place is saved on this PC and will sync when it's back.";

    public AccountStage Stage { get; private set; } = new AccountStage.SignedOut();
    public AccountApi.Providers Providers { get; private set; } = new(false, false);
    public DateTimeOffset? LastSuccess { get; private set; }
    public string? LastError { get; private set; }
    public int PendingCount { get; private set; }
    public bool IsSyncing { get; private set; }

    public Uri ServerUrl { get; }
    public event Action? Changed;
    /// <summary>Signed in (true) or out (false): the app starts or stops library sync and refreshes open books.</summary>
    public Action<bool>? OnSessionChanged { get; set; }
    /// <summary>"Sync Now": the app also refreshes open books and syncs the library.</summary>
    public Action? OnSyncNow { get; set; }

    private readonly AccountApi _api;
    private readonly ISessionStore _store;
    private readonly SyncEngine _engine;
    private readonly SyncScheduler _scheduler;
    private readonly ReadingPositionRepository _positions;
    private readonly SynchronizationContext? _main = SynchronizationContext.Current;
    private AccountSessionInfo? _session;

    public bool IsSignedIn => Stage is AccountStage.SignedIn;

    public string? Email => Stage switch
    {
        AccountStage.SignedIn s => s.Email,
        AccountStage.AwaitingCode c => c.Email,
        _ => null,
    };

    /// <summary>The bearer token, while signed in.</summary>
    public string? Token => IsSignedIn ? _session?.Token : null;

    public SyncAccount(Uri serverUrl, ISessionStore store, SyncEngine engine, SyncScheduler scheduler,
        ReadingPositionRepository positions, HttpMessageHandler? handler = null)
    {
        ServerUrl = serverUrl;
        _api = new AccountApi(serverUrl, handler: handler);
        _store = store;
        _engine = engine;
        _scheduler = scheduler;
        _positions = positions;
        _handler = handler;
        if (store.Load() is { } saved && !string.IsNullOrEmpty(saved.Token))
        {
            _session = saved;
            Stage = new AccountStage.SignedIn(saved.Email);
        }
        engine.OnUnauthorized = () => Post(Unauthorized);
        scheduler.OnResult = (ok, at) => Record(ok, at);
        Apply();
    }

    private readonly HttpMessageHandler? _handler;

    /// <summary>The server's address: READER_SYNC_BASE_URL, else the deployed Worker.</summary>
    public static Uri ConfiguredServer(string? overrideUrl = null)
    {
        foreach (var c in new[] { overrideUrl, Environment.GetEnvironmentVariable("READER_SYNC_BASE_URL"), DefaultServer })
        {
            if (!string.IsNullOrWhiteSpace(c) && Uri.TryCreate(c.Trim(), UriKind.Absolute, out var u)) return u;
        }
        return new Uri(DefaultServer);
    }

    public const string DefaultServer = "https://reader-sync.satyanarayanv2050.workers.dev";

    private void Post(Action a)
    {
        if (_main is null || SynchronizationContext.Current == _main) a();
        else _main.Post(_ => a(), null);
    }

    private void Raise() => Changed?.Invoke();

    /// <summary>Any endpoint answered 401 for the saved session.</summary>
    public void Unauthorized()
    {
        if (!IsSignedIn) return;
        SignOut();
        LastError = SignedOutMessage;
        Raise();
    }

    public async Task LoadProvidersAsync()
    {
        try
        {
            Providers = await _api.ProvidersAsync();
            Raise();
        }
        catch
        {
            // Keep what we had; the form still offers email and password.
        }
    }

    // Email and password

    public async Task CreateAccountAsync(string email, string password)
    {
        email = Clean(email);
        if (await _api.SignUpAsync(email, password) is { } info)
        {
            SignedIn(info);
            return;
        }
        Stage = new AccountStage.AwaitingCode(email, CodePurpose.ConfirmEmail);
        Raise();
    }

    public async Task SignInAsync(string email, string password)
    {
        email = Clean(email);
        try
        {
            SignedIn(await _api.SignInAsync(email, password));
        }
        catch (AccountException e) when (e.Code == "verification_required")
        {
            // The server has emailed a fresh code; confirming it finishes signing in.
            Stage = new AccountStage.AwaitingCode(email, CodePurpose.ConfirmEmail);
            Raise();
        }
    }

    public async Task ConfirmAsync(string code)
    {
        if (Stage is not AccountStage.AwaitingCode { Purpose: CodePurpose.ConfirmEmail } s) return;
        SignedIn(await _api.VerifyAsync(s.Email, code.Trim()));
    }

    public async Task ResendCodeAsync()
    {
        if (Stage is not AccountStage.AwaitingCode s) return;
        if (s.Purpose == CodePurpose.ConfirmEmail) await _api.ResendCodeAsync(s.Email);
        else await _api.ForgotPasswordAsync(s.Email);
    }

    public async Task ForgotPasswordAsync(string email)
    {
        email = Clean(email);
        await _api.ForgotPasswordAsync(email);
        Stage = new AccountStage.AwaitingCode(email, CodePurpose.ResetPassword);
        Raise();
    }

    public async Task ResetPasswordAsync(string code, string newPassword)
    {
        if (Stage is not AccountStage.AwaitingCode { Purpose: CodePurpose.ResetPassword } s) return;
        SignedIn(await _api.ResetPasswordAsync(s.Email, code.Trim(), newPassword));
    }

    public void CancelCode()
    {
        Stage = new AccountStage.SignedOut();
        Raise();
    }

    // Google

    public async Task SignInWithProviderAsync(string provider, string idToken) =>
        SignedIn(await _api.SignInWithProviderAsync(provider, idToken));

    // Session

    public void SignOut()
    {
        if (_session?.Token is { } token) _ = _api.SignOutAsync(token);
        _session = null;
        Stage = new AccountStage.SignedOut();
        try { _store.Save(null); } catch { }
        Apply();
        LastSuccess = null;
        LastError = null;
        OnSessionChanged?.Invoke(false);
        Raise();
    }

    /// <summary>Push now (pending local positions), check the server for open books, sync the library.</summary>
    public void SyncNow()
    {
        if (!IsSignedIn) return;
        IsSyncing = true;
        Raise();
        _scheduler.RequestSync(TimeSpan.Zero);
        OnSyncNow?.Invoke();
    }

    public void RefreshPendingCount()
    {
        try { PendingCount = _positions.Pending().Count; } catch { PendingCount = 0; }
        Raise();
    }

    /// <summary>"Up to date · 3:41 PM", "Syncing…", or the error.</summary>
    public string StatusText
    {
        get
        {
            if (IsSyncing) return "Syncing…";
            if (LastError is { } e) return e;
            if (LastSuccess is not { } at) return "Up to date";
            return $"Up to date · {at.ToLocalTime():t}";
        }
    }

    private void SignedIn(AccountSessionInfo info)
    {
        _session = info;
        Stage = new AccountStage.SignedIn(info.Email);
        try { _store.Save(info); } catch { }
        LastError = null;
        Apply();
        OnSessionChanged?.Invoke(true);
        // Reading done on this PC before signing in goes up now, and open books catch up.
        SyncNow();
    }

    private void Record(bool ok, DateTimeOffset at)
    {
        IsSyncing = false;
        if (ok)
        {
            LastSuccess = at;
            if (LastError != SignedOutMessage) LastError = null;
        }
        else if (IsSignedIn)
        {
            LastError = OfflineMessage;
        }
        RefreshPendingCount();
    }

    /// <summary>The library sync finished: the status shows its outcome too.</summary>
    public void RecordLibraryResult(bool ok, string? error)
    {
        IsSyncing = false;
        if (ok) LastSuccess = DateTimeOffset.Now;
        else if (IsSignedIn && error is not null) LastError = error;
        Raise();
    }

    private void Apply()
    {
        if (_session is { } session && IsSignedIn)
        {
            var token = new StaticTokenSession(session.Token);
            _engine.Configure(new HttpSyncApi(ServerUrl.AbsoluteUri, token.Token, handler: _handler), token);
            _scheduler.Start();
        }
        else
        {
            _engine.Configure(null, new StaticTokenSession(""));
        }
        RefreshPendingCount();
    }

    private static string Clean(string email) => email.Trim().ToLowerInvariant();
}
