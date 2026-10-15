using System.Text.Json;
using Reader.Core.Data;
using Reader.Core.Reader;
using Reader.Core.Sync;

namespace Reader.Core.Library;

/// <summary>
/// What library sync remembers between runs, in <c>library-sync.json</c>:
///  - <see cref="SeenAlive"/>: books this device has seen alive in the account. Only these are
///    removed here when the account has a tombstone for them; a book this device added without
///    ever seeing it alive (offline, after another device removed it) is brought back instead.
///  - <see cref="PendingRemovals"/>: books removed here that the server hasn't been told about.
///  - <see cref="TooLarge"/>: books the server refused (over 100 MB), so they aren't retried forever.
/// Everything belongs to one <see cref="Account"/>; signing in to another account starts afresh.
/// </summary>
public sealed class LibrarySyncState
{
    public string? Account { get; set; }
    public HashSet<string> SeenAlive { get; set; } = new();
    public HashSet<string> PendingRemovals { get; set; } = new();
    public HashSet<string> TooLarge { get; set; } = new();

    public static LibrarySyncState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<LibrarySyncState>(File.ReadAllText(path), JsonOptions) ?? new();
            }
        }
        catch
        {
            // A damaged file only costs a re-check against the server.
        }
        return new();
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

/// <summary>The outcome of one library sync.</summary>
public sealed record LibrarySyncResult(int Uploaded, int Downloaded, int RemovedHere, int RemovedThere, IReadOnlyList<string> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Makes the books follow the account: every signed-in device ends up with the same library.
///
///  - Books added on this device are uploaded: first the metadata (<c>PUT /v1/library/{id}</c>),
///    then the file (<c>PUT /v1/library/{id}/file</c>).
///  - Books in the account that this device lacks are downloaded and imported under the
///    account's title.
///  - A book removed here (while signed in) is removed from the account: a tombstone on the
///    server, which other devices then apply. Removals made offline wait in
///    <see cref="LibrarySyncState.PendingRemovals"/>.
///  - A tombstone removes the local copy only if this device had already seen that book alive in
///    the account. A book added on this device after another device removed it brings it back.
///
/// Runs entirely off the UI thread (hashing, file copies and network); one run at a time, and a
/// request during a run schedules one more run after it.
/// </summary>
public sealed class LibrarySync
{
    private readonly DocumentRepository _documents;
    private readonly string _statePath;
    private readonly string _downloadDir;
    private readonly Func<ILibraryApi?> _api;
    private readonly Func<string?> _account;
    private readonly object _lock = new();
    private Task<LibrarySyncResult>? _running;
    private bool _rerun;

    /// <summary>Called (on any thread) when the server rejects the session.</summary>
    public Action? OnUnauthorized { get; set; }
    /// <summary>Called (on any thread) when <see cref="IsSyncing"/> or <see cref="LastError"/> change.</summary>
    public event Action? Changed;

    public bool IsSyncing { get; private set; }
    public string? LastError { get; private set; }
    public LibrarySyncResult? LastResult { get; private set; }

    public LibrarySync(DocumentRepository documents, string statePath, Func<ILibraryApi?> api, Func<string?> account)
    {
        _documents = documents;
        _statePath = statePath;
        _api = api;
        _account = account;
        _downloadDir = Path.Combine(documents.Directory, "downloads");
    }

    private LibrarySyncState? _state;

    /// <summary>The state for the signed-in account. Call under <see cref="_lock"/>.</summary>
    private LibrarySyncState State()
    {
        _state ??= LibrarySyncState.Load(_statePath);
        var account = _account();
        if (account is not null && _state.Account != account)
        {
            // Another account: what was seen or removed belongs to the previous one.
            _state = new LibrarySyncState { Account = account };
            SaveLocked();
        }
        return _state;
    }

    /// <summary>Writes the state. Call under <see cref="_lock"/>.</summary>
    private void SaveLocked()
    {
        try { _state?.Save(_statePath); } catch { }
    }

    private T Locked<T>(Func<LibrarySyncState, T> body)
    {
        lock (_lock)
        {
            var result = body(State());
            SaveLocked();
            return result;
        }
    }

    private void Locked(Action<LibrarySyncState> body) => Locked(s => { body(s); return 0; });

    /// <summary>
    /// The user added <paramref name="documentId"/> on this device (Open, drop, Explorer). It
    /// belongs in the account even if another device removed it meanwhile.
    /// </summary>
    public void NoteLocalAdd(string documentId) => Locked(s =>
    {
        s.PendingRemovals.Remove(documentId);
        s.SeenAlive.Remove(documentId);
        s.TooLarge.Remove(documentId);
    });

    /// <summary>
    /// The user removed <paramref name="documentId"/> here while signed in: remove it from the
    /// account on the next sync (and keep trying until the server has it).
    /// </summary>
    public void NoteLocalRemove(string documentId) => Locked(s =>
    {
        s.PendingRemovals.Add(documentId);
        s.TooLarge.Remove(documentId);
    });

    /// <summary>Starts a sync, or (if one is running) one more right after it. Never throws.</summary>
    public Task<LibrarySyncResult> SyncAsync()
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false } running)
            {
                _rerun = true;
                return running;
            }
            _running = Task.Run(RunLoopAsync);
            return _running;
        }
    }

    private async Task<LibrarySyncResult> RunLoopAsync()
    {
        while (true)
        {
            SetSyncing(true);
            LibrarySyncResult result;
            try
            {
                result = await RunOnceAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                result = new LibrarySyncResult(0, 0, 0, 0, [e.Message]);
            }
            lock (_lock)
            {
                LastResult = result;
                LastError = result.Errors.FirstOrDefault();
                if (!_rerun)
                {
                    IsSyncing = false;
                    Changed?.Invoke();
                    return result;
                }
                _rerun = false;
            }
        }
    }

    private void SetSyncing(bool value)
    {
        IsSyncing = value;
        Changed?.Invoke();
    }

    private async Task<LibrarySyncResult> RunOnceAsync()
    {
        var api = _api();
        if (api is null) return new LibrarySyncResult(0, 0, 0, 0, []);
        var errors = new List<string>();
        int uploaded = 0, downloaded = 0, removedHere = 0, removedThere = 0;

        try
        {
            var remote = await api.ListAsync().ConfigureAwait(false);
            var remoteById = remote.GroupBy(b => b.DocumentId).ToDictionary(g => g.Key, g => g.Last());

            // 1. Removals made here go to the account first, so they win over the listing.
            foreach (var id in Locked(s => s.PendingRemovals.ToList()))
            {
                if (_documents.Get(id) is not null)
                {
                    // Added back here since: keep it.
                    Locked(s => s.PendingRemovals.Remove(id));
                    continue;
                }
                if (remoteById.TryGetValue(id, out var rb) && rb.IsAlive)
                {
                    await api.RemoveAsync(id).ConfigureAwait(false);
                    remoteById[id] = rb with { RemovedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), HasFile = false };
                    removedThere++;
                }
                Locked(s =>
                {
                    s.PendingRemovals.Remove(id);
                    s.SeenAlive.Remove(id);
                });
            }

            var local = _documents.All().ToDictionary(d => d.Id);

            // 2. What the account has.
            foreach (var rb in remoteById.Values.OrderByDescending(b => b.AddedAt))
            {
                local.TryGetValue(rb.DocumentId, out var mine);
                if (Locked(s => s.PendingRemovals.Contains(rb.DocumentId))) continue; // removed here during this run
                if (rb.IsAlive)
                {
                    if (mine is not null)
                    {
                        Locked(s => s.SeenAlive.Add(rb.DocumentId));
                        if (!rb.HasFile && await UploadFileAsync(api, mine, errors).ConfigureAwait(false)) uploaded++;
                    }
                    else if (rb.HasFile)
                    {
                        if (await DownloadAsync(api, rb, errors).ConfigureAwait(false))
                        {
                            Locked(s => s.SeenAlive.Add(rb.DocumentId));
                            downloaded++;
                        }
                    }
                    // Alive without a file: another device is still uploading it. Next time.
                }
                else if (mine is not null)
                {
                    if (Locked(s => s.SeenAlive.Contains(rb.DocumentId)))
                    {
                        // Removed on another device after this one had it from the account.
                        _documents.Remove(rb.DocumentId);
                        Locked(s => s.SeenAlive.Remove(rb.DocumentId));
                        removedHere++;
                    }
                    else if (await UploadAsync(api, mine, errors).ConfigureAwait(false))
                    {
                        // Added here without ever seeing it alive (offline, after another device
                        // removed it): this device wants it, so it comes back.
                        uploaded++;
                    }
                }
                else
                {
                    Locked(s => s.SeenAlive.Remove(rb.DocumentId));
                }
            }

            // 3. Books only this device has.
            foreach (var mine in local.Values.Where(d => !remoteById.ContainsKey(d.Id)).OrderBy(d => d.LastOpenedAt))
            {
                if (_documents.Get(mine.Id) is null) continue; // removed meanwhile
                if (await UploadAsync(api, mine, errors).ConfigureAwait(false)) uploaded++;
            }
        }
        catch (SyncUnauthorizedException)
        {
            OnUnauthorized?.Invoke();
            errors.Add("You were signed out. Sign in again to keep syncing.");
        }
        catch (SyncUnavailableException e)
        {
            System.Diagnostics.Trace.WriteLine($"library sync: {e.Message}");
            errors.Add("Couldn't reach the server. Your books are safe on this PC and will sync when it's back.");
        }
        return new LibrarySyncResult(uploaded, downloaded, removedHere, removedThere, errors);
    }

    /// <summary>Metadata, then the file. False if it didn't make it (the reason is in <paramref name="errors"/>).</summary>
    private async Task<bool> UploadAsync(ILibraryApi api, DocumentRecord doc, List<string> errors)
    {
        if (Locked(s => s.TooLarge.Contains(doc.Id))) return false;
        if (TooLarge(doc))
        {
            Locked(s => s.TooLarge.Add(doc.Id));
            errors.Add(TooLargeMessage(doc));
            return false;
        }
        var book = await api.PutAsync(doc.Id, doc.Title, doc.Format, doc.SizeBytes, doc.PageCount).ConfigureAwait(false);
        Locked(s => s.SeenAlive.Add(doc.Id));
        return book.HasFile || await UploadFileAsync(api, doc, errors).ConfigureAwait(false);
    }

    private async Task<bool> UploadFileAsync(ILibraryApi api, DocumentRecord doc, List<string> errors)
    {
        if (Locked(s => s.TooLarge.Contains(doc.Id))) return false;
        if (!File.Exists(doc.LocalPath)) return false;
        try
        {
            await api.UploadFileAsync(doc.Id, doc.LocalPath).ConfigureAwait(false);
            return true;
        }
        catch (LibraryTooLargeException)
        {
            Locked(s => s.TooLarge.Add(doc.Id));
            errors.Add(TooLargeMessage(doc));
            return false;
        }
    }

    private static bool TooLarge(DocumentRecord doc)
    {
        try { return new FileInfo(doc.LocalPath).Length > HttpLibraryApi.MaxFileBytes; }
        catch { return doc.SizeBytes > HttpLibraryApi.MaxFileBytes; }
    }

    public static string TooLargeMessage(DocumentRecord doc) => $"“{doc.Title}” is too large to sync (over 100 MB).";

    private async Task<bool> DownloadAsync(ILibraryApi api, RemoteBook rb, List<string> errors)
    {
        Directory.CreateDirectory(_downloadDir);
        var tmp = Path.Combine(_downloadDir, $"{rb.DocumentId}.{Guid.NewGuid():N}.part");
        try
        {
            if (!await api.DownloadFileAsync(rb.DocumentId, tmp).ConfigureAwait(false)) return false;
            if (DocumentRepository.Sha256Hex(tmp) != rb.DocumentId)
            {
                // The bytes aren't the book the account says they are; never keep them.
                errors.Add($"“{rb.Title}” didn't download correctly. It will be tried again.");
                return false;
            }
            _documents.ImportFile(tmp, title: string.IsNullOrWhiteSpace(rb.Title) ? null : rb.Title, openedAt: rb.AddedAt);
            return true;
        }
        catch (Exception e) when (e is ReaderException or UnsupportedFormatException or IOException)
        {
            errors.Add($"“{rb.Title}” couldn't be added: {e.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
