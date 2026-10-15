using Reader.Core.Model;

namespace Reader.Core.Data;

public sealed record StoredPosition(ReadingPosition Position, bool PendingSync);

/// <summary>
/// Durable per-document position storage. All read-compare-write sequences go through
/// <see cref="Transaction{T}"/> so a merge decision is never made on stale data.
/// </summary>
public interface IPositionStore
{
    T Transaction<T>(Func<IPositionTx, T> body);
    List<StoredPosition> Pending();
    long MaxHlc();
}

public interface IPositionTx
{
    StoredPosition? Get(string documentId);
    void Put(StoredPosition stored);
}

/// <summary>
/// The app's database: reading positions plus the library. Same tables and columns as the
/// Android Room schema and the Mac app, so the meaning of every stored field is identical.
/// </summary>
public sealed class ReaderDatabase : IPositionStore, IDisposable
{
    public SqliteDatabase Sql { get; }

    /// <summary>Raised (on the writing thread) after any write, so lists can refresh.</summary>
    public static event Action? DidChange;

    public ReaderDatabase(string path)
    {
        Sql = new SqliteDatabase(path);
        Migrate();
    }

    public void Dispose() => Sql.Dispose();

    private void Migrate()
    {
        if (Sql.UserVersion < 1)
        {
            Sql.Transaction(() =>
            {
                Sql.Execute("""
                    CREATE TABLE IF NOT EXISTS reading_positions (
                        documentId TEXT NOT NULL PRIMARY KEY,
                        pageIndex INTEGER NOT NULL,
                        pageOffset REAL NOT NULL,
                        horizontalOffset REAL NOT NULL,
                        zoom REAL NOT NULL,
                        pageCount INTEGER NOT NULL,
                        hlc INTEGER NOT NULL,
                        deviceId TEXT NOT NULL,
                        schemaVersion INTEGER NOT NULL,
                        pendingSync INTEGER NOT NULL)
                    """);
                Sql.Execute("""
                    CREATE TABLE IF NOT EXISTS documents (
                        id TEXT NOT NULL PRIMARY KEY,
                        title TEXT NOT NULL,
                        localPath TEXT NOT NULL,
                        sizeBytes INTEGER NOT NULL,
                        pageCount INTEGER NOT NULL,
                        lastOpenedAt INTEGER NOT NULL,
                        readingMode TEXT NOT NULL DEFAULT 'PAGES',
                        format TEXT NOT NULL DEFAULT 'PDF')
                    """);
                return 0;
            });
            Sql.UserVersion = 1;
        }
    }

    private sealed class Tx(SqliteDatabase sql) : IPositionTx
    {
        public StoredPosition? Get(string documentId) =>
            sql.Query("SELECT * FROM reading_positions WHERE documentId = ?", documentId)
                .Select(Stored).FirstOrDefault();

        public void Put(StoredPosition s)
        {
            var p = s.Position;
            sql.Execute("""
                INSERT OR REPLACE INTO reading_positions (documentId, pageIndex, pageOffset,
                    horizontalOffset, zoom, pageCount, hlc, deviceId, schemaVersion, pendingSync)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                p.DocumentId, (long)p.PageIndex, (double)p.PageOffset, (double)p.HorizontalOffset,
                (double)p.Zoom, (long)p.PageCount, p.Stamp.Hlc, p.Stamp.DeviceId,
                (long)p.SchemaVersion, s.PendingSync ? 1L : 0L);
        }
    }

    public T Transaction<T>(Func<IPositionTx, T> body)
    {
        var result = Sql.Transaction(() => body(new Tx(Sql)));
        PostChange();
        return result;
    }

    public List<StoredPosition> Pending() =>
        Sql.Query("SELECT * FROM reading_positions WHERE pendingSync = 1").Select(Stored).ToList();

    public long MaxHlc() =>
        Sql.Query("SELECT COALESCE(MAX(hlc), 0) AS m FROM reading_positions").FirstOrDefault()?.Int64("m") ?? 0;

    internal static StoredPosition Stored(SqliteDatabase.Row r) => new(
        new ReadingPosition
        {
            DocumentId = r.String("documentId") ?? "",
            PageIndex = r.Int("pageIndex") ?? 0,
            PageOffset = (float)(r.Double("pageOffset") ?? 0),
            HorizontalOffset = (float)(r.Double("horizontalOffset") ?? 0),
            Zoom = (float)(r.Double("zoom") ?? 1),
            PageCount = r.Int("pageCount") ?? 0,
            Stamp = new Stamp(r.Int64("hlc") ?? 0, r.String("deviceId") ?? ""),
            SchemaVersion = r.Int("schemaVersion") ?? ReadingPosition.CurrentSchemaVersion,
        },
        (r.Int("pendingSync") ?? 0) != 0);

    public static void PostChange() => DidChange?.Invoke();
}

/// <summary>Non-durable store used by tests; same semantics as <see cref="ReaderDatabase"/>.</summary>
public sealed class InMemoryPositionStore : IPositionStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, StoredPosition> _rows = new();

    private sealed class Tx(InMemoryPositionStore store) : IPositionTx
    {
        public StoredPosition? Get(string documentId) => store._rows.GetValueOrDefault(documentId);
        public void Put(StoredPosition stored) => store._rows[stored.Position.DocumentId] = stored;
    }

    public T Transaction<T>(Func<IPositionTx, T> body)
    {
        lock (_lock) return body(new Tx(this));
    }

    public List<StoredPosition> Pending()
    {
        lock (_lock) return _rows.Values.Where(r => r.PendingSync).ToList();
    }

    public long MaxHlc()
    {
        lock (_lock) return _rows.Count == 0 ? 0 : _rows.Values.Max(r => r.Position.Stamp.Hlc);
    }
}
