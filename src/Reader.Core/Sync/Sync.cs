using Reader.Core.Data;
using Reader.Core.Model;

namespace Reader.Core.Sync;

/// <summary>
/// Server contract for reading positions (see docs/position-sync.md).
///
/// The server applies the same ordering as the client: it stores an incoming position only if its
/// stamp is greater than the stored one, and always answers with whatever is stored afterwards.
/// That makes <see cref="PutAsync"/> idempotent and safe to retry or deliver out of order.
/// </summary>
public interface ISyncApi
{
    /// <summary>The server's position for <paramref name="documentId"/>, or null if it has none.</summary>
    Task<ReadingPosition?> GetAsync(string documentId);
    /// <summary>Offers <paramref name="position"/> to the server; returns the server's winner after the compare.</summary>
    Task<ReadingPosition> PutAsync(ReadingPosition position);
}

/// <summary>
/// The server no longer accepts this device's session (signed out, password reset elsewhere).
/// Retrying won't help; the user has to sign in again.
/// </summary>
public sealed class SyncUnauthorizedException() : Exception("unauthorized");

/// <summary>Network or server failure that's worth retrying later.</summary>
public sealed class SyncUnavailableException(string description) : Exception(description);

/// <summary>Who is signed in. Sync runs only while <see cref="IsSignedIn"/>.</summary>
public interface IAccountSession
{
    bool IsSignedIn { get; }
    string? Token();
}

public sealed class StaticTokenSession(string token) : IAccountSession
{
    public bool IsSignedIn => !string.IsNullOrWhiteSpace(token);
    public string? Token() => IsSignedIn ? token : null;
}

/// <summary>A FIFO async mutex: holds across awaits.</summary>
public sealed class AsyncLock
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    public async Task<T> WithLock<T>(Func<Task<T>> body)
    {
        await _sem.WaitAsync();
        try { return await body(); }
        finally { _sem.Release(); }
    }
}

/// <summary>
/// Moves positions between the local store and the server. Every server answer is merged through
/// <see cref="ReadingPositionRepository.MergeRemote"/>, so ordering of responses never matters.
/// </summary>
public sealed class SyncEngine
{
    private readonly ReadingPositionRepository _repository;
    private readonly object _lock = new();
    private ISyncApi? _api;
    private IAccountSession _session;
    private readonly AsyncLock _pushLock = new();
    /// <summary>Called (on any thread) when the server rejects the session.</summary>
    public Action? OnUnauthorized { get; set; }

    public SyncEngine(ReadingPositionRepository repository, ISyncApi? api, IAccountSession session)
    {
        _repository = repository;
        _api = api;
        _session = session;
    }

    /// <summary>
    /// Signs in or out (null api) without restarting. Unacknowledged local positions are kept,
    /// so reading done while signed out goes up on the next push.
    /// </summary>
    public void Configure(ISyncApi? api, IAccountSession session)
    {
        lock (_lock)
        {
            _api = api;
            _session = session;
        }
    }

    private (ISyncApi? Api, IAccountSession Session) Current
    {
        get { lock (_lock) return (_api, _session); }
    }

    public bool IsEnabled
    {
        get
        {
            var (api, session) = Current;
            return api is not null && session.IsSignedIn;
        }
    }

    /// <summary>
    /// Pushes every unacknowledged local position. Returns false if anything failed and should
    /// be retried later.
    /// </summary>
    public async Task<bool> PushPendingAsync()
    {
        if (!IsEnabled || Current.Api is not { } api) return true;
        return await _pushLock.WithLock(async () =>
        {
            var ok = true;
            List<ReadingPosition> pending;
            try { pending = _repository.Pending(); } catch { pending = new(); }
            foreach (var local in pending)
            {
                try
                {
                    _repository.MergeRemote(await api.PutAsync(local));
                }
                catch (SyncUnauthorizedException)
                {
                    OnUnauthorized?.Invoke();
                    return false;
                }
                catch
                {
                    ok = false;
                }
            }
            return ok;
        });
    }

    /// <summary>
    /// Fetches the server's position for <paramref name="documentId"/> and merges it. Returns the
    /// server's position (which may have lost the merge), or null if there's none or the server is
    /// unreachable.
    /// </summary>
    public async Task<ReadingPosition?> PullAsync(string documentId)
    {
        if (!IsEnabled || Current.Api is not { } api) return null;
        ReadingPosition? fetched;
        try
        {
            fetched = await api.GetAsync(documentId);
        }
        catch (SyncUnauthorizedException)
        {
            OnUnauthorized?.Invoke();
            return null;
        }
        catch
        {
            return null;
        }
        if (fetched is null) return null;
        try { _repository.MergeRemote(fetched); } catch { /* kept for the next pull */ }
        return fetched;
    }
}
