using System.IO;
using System.Windows;
using System.Windows.Threading;
using Reader.App.Platform;
using Reader.Core.App;
using Reader.Core.Data;
using Reader.Core.Library;
using Reader.Core.Model;
using Reader.Core.Reader;
using Reader.Core.Sync;
using Reader.Core.Telemetry;
using Reader.App.Reading;

namespace Reader.App;

/// <summary>Hand-wired dependencies; small enough not to need a DI framework. Created on the UI thread.</summary>
internal sealed class AppGraph
{
    public static AppGraph Shared { get; private set; } = null!;

    public string DataDir { get; }
    public ReaderDatabase Database { get; }
    public string DeviceId { get; }
    public LocalRestoreTelemetry Telemetry { get; }
    public ReaderSettings Settings { get; }
    public ReadingPositionRepository Positions { get; }
    public DocumentRepository Documents { get; }
    public SyncEngine SyncEngine { get; }
    public SyncScheduler SyncScheduler { get; }
    public SyncAccount Account { get; }
    public LibrarySync LibrarySync { get; }
    public IScheduler Scheduler { get; }
    public WindowsPdf Pdf { get; } = new();
    public WindowsOcr Ocr { get; } = new();
    public Dispatcher Dispatcher { get; }

    /// <summary>The book open in the main window, if any.</summary>
    public ReaderModel? OpenReader { get; set; }

    /// <summary>The library list changed (an import, a removal, a sync, a position saved). Raised on the UI thread, debounced.</summary>
    public event Action? LibraryChanged;
    /// <summary>Library sync started or finished. UI thread.</summary>
    public event Action? LibrarySyncChanged;

    private readonly DispatcherTimer _libraryChangedDebounce;

    private AppGraph()
    {
        Dispatcher = Dispatcher.CurrentDispatcher;
        DataDir = AppPaths.DataDir();
        Database = new ReaderDatabase(AppPaths.Database(DataDir));
        DeviceId = AppPaths.DeviceId(DataDir);
        Telemetry = new LocalRestoreTelemetry(AppPaths.Logs(DataDir));
        Settings = ReaderSettings.Load(AppPaths.Settings(DataDir));
        Positions = new ReadingPositionRepository(Database, new HybridClock(), DeviceId);
        Documents = new DocumentRepository(Database, AppPaths.Documents(DataDir));
        Scheduler = new ContextScheduler();
        // Sync is off until an account is signed in (Settings, Ctrl+,).
        SyncEngine = new SyncEngine(Positions, null, new StaticTokenSession(""));
        SyncScheduler = new SyncScheduler(SyncEngine);
        Account = new SyncAccount(SyncAccount.ConfiguredServer(), new DpapiSessionStore(AppPaths.Session(DataDir)),
            SyncEngine, SyncScheduler, Positions);
        Positions.OnPendingChange = () => SyncScheduler.RequestSync();

        LibrarySync = new LibrarySync(Documents, AppPaths.LibrarySyncState(DataDir),
            () => Account.Token is { } token ? new HttpLibraryApi(Account.ServerUrl.AbsoluteUri, () => token) : null,
            () => Account.IsSignedIn ? Account.Email : null);
        LibrarySync.OnUnauthorized = () => Dispatcher.BeginInvoke(Account.Unauthorized);
        LibrarySync.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            LibrarySyncChanged?.Invoke();
            if (!LibrarySync.IsSyncing && LibrarySync.LastResult is { } r) Account.RecordLibraryResult(r.Ok, r.Errors.FirstOrDefault());
        });

        Account.OnSessionChanged = signedIn =>
        {
            if (signedIn) SyncLibrary();
            LibrarySyncChanged?.Invoke();
        };
        Account.OnSyncNow = () =>
        {
            OpenReader?.Controller.RefreshFromServer();
            SyncLibrary();
        };
        SyncScheduler.OnReconnect = SyncLibrary;

        _libraryChangedDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _libraryChangedDebounce!.Stop();
            LibraryChanged?.Invoke();
        }, Dispatcher);
        _libraryChangedDebounce.Stop();
        ReaderDatabase.DidChange += () => Dispatcher.BeginInvoke(() =>
        {
            _libraryChangedDebounce.Stop();
            _libraryChangedDebounce.Start();
        });
    }

    public static AppGraph Create() => Shared = new AppGraph();

    public void SyncLibrary()
    {
        if (!Account.IsSignedIn) return;
        _ = LibrarySync.SyncAsync();
    }

    /// <summary>
    /// Copies files into the library (hashing them off the UI thread). Returns the books that were
    /// added (or were already there), and a message for any that failed.
    /// </summary>
    public async Task<(List<DocumentRecord> Added, List<string> Failures)> ImportAsync(IEnumerable<string> paths)
    {
        var added = new List<DocumentRecord>();
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var doc = await Task.Run(() => Documents.ImportFile(path));
                LibrarySync.NoteLocalAdd(doc.Id);
                added.Add(doc);
            }
            catch (Exception e)
            {
                failures.Add($"{Path.GetFileName(path)}: {e.Message}");
            }
        }
        if (added.Count > 0) SyncLibrary();
        return (added, failures);
    }

    /// <summary>Removes a book here, and from the account (every device) when signed in.</summary>
    public void Remove(string documentId)
    {
        if (OpenReader?.DocumentId == documentId) return;
        Documents.Remove(documentId);
        Pdf.Forget(Path.Combine(AppPaths.Documents(DataDir), documentId + ".pdf"));
        if (Account.IsSignedIn)
        {
            LibrarySync.NoteLocalRemove(documentId);
            SyncLibrary();
        }
    }

    /// <summary>Exit, sleep, session end, going back to the library: write the open book's position now.</summary>
    public void FlushAll() => OpenReader?.Flush();

    public string TextCacheBase => DataDir;

    public static void ApplyTheme(ReaderTheme theme)
    {
        Shared.Settings.Theme = theme;
        Shared.Settings.Save();
        ThemeManager.Apply(theme);
    }
}
