using Microsoft.Data.Sqlite;

namespace Reader.Core.Data;

public sealed class SqliteException(string message) : Exception(message);

/// <summary>
/// A minimal, thread-safe wrapper over one SQLite connection. Every call runs under a lock, and
/// <see cref="Transaction{T}"/> holds it for the whole read-compare-write, so merge decisions
/// never see stale rows.
///
/// Committed writes survive process death (WAL) and sudden power loss (<c>synchronous=FULL</c>
/// fsyncs every commit). Position writes are debounced, so the fsync costs nothing noticeable.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    public string Path { get; }
    private SqliteConnection? _db;
    private readonly object _lock = new();

    public sealed class Row(Dictionary<string, object?> values)
    {
        public object? this[string name] => values.TryGetValue(name, out var v) ? v : null;
        public string? String(string name) => this[name] as string;
        public long? Int64(string name) => this[name] switch { long l => l, double d => (long)d, _ => null };
        public int? Int(string name) => this[name] switch { long l => (int)l, double d => (int)d, _ => null };
        public double? Double(string name) => this[name] switch { double d => d, long l => l, _ => null };
    }

    public SqliteDatabase(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // No pooling: closing the connection really closes the file (tests reopen it).
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
        try
        {
            _db = new SqliteConnection(cs);
            _db.Open();
        }
        catch (Exception e)
        {
            throw new SqliteException($"open {path}: {e.Message}");
        }
        Execute("PRAGMA busy_timeout = 5000");
        Execute("PRAGMA journal_mode = WAL");
        Execute("PRAGMA synchronous = FULL");
    }

    public void Dispose() => Close();

    public void Close()
    {
        lock (_lock)
        {
            _db?.Close();
            _db?.Dispose();
            _db = null;
        }
    }

    public void Execute(string sql, params object?[] args) => Query(sql, args);

    public List<Row> Query(string sql, params object?[] args)
    {
        lock (_lock)
        {
            if (_db is null) throw new SqliteException("database closed");
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            for (var i = 0; i < args.Length; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = "$" + (i + 1);
                p.Value = args[i] switch
                {
                    null => DBNull.Value,
                    bool b => b ? 1L : 0L,
                    int n => (long)n,
                    float f => (double)f,
                    var v => v,
                };
                cmd.Parameters.Add(p);
            }
            cmd.CommandText = Numbered(sql);
            var rows = new List<Row>();
            try
            {
                using var r = cmd.ExecuteReader();
                do
                {
                    while (r.Read())
                    {
                        var values = new Dictionary<string, object?>(r.FieldCount);
                        for (var c = 0; c < r.FieldCount; c++)
                        {
                            values[r.GetName(c)] = r.IsDBNull(c) ? null : r.GetValue(c) switch
                            {
                                long l => l,
                                double d => d,
                                string s => s,
                                byte[] b => b,
                                var o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture),
                            };
                        }
                        rows.Add(new Row(values));
                    }
                } while (r.NextResult());
            }
            catch (Microsoft.Data.Sqlite.SqliteException e)
            {
                throw new SqliteException($"{sql}: {e.Message}");
            }
            return rows;
        }
    }

    /// <summary>Turns positional <c>?</c> placeholders into <c>$1, $2, …</c>.</summary>
    private static string Numbered(string sql)
    {
        if (!sql.Contains('?')) return sql;
        var sb = new System.Text.StringBuilder(sql.Length + 16);
        var n = 0;
        var inString = false;
        foreach (var ch in sql)
        {
            if (ch == '\'') inString = !inString;
            if (ch == '?' && !inString) sb.Append('$').Append(++n);
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Runs <paramref name="body"/> inside one immediate (write-locked) transaction.</summary>
    public T Transaction<T>(Func<T> body)
    {
        lock (_lock)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                var result = body();
                Execute("COMMIT");
                return result;
            }
            catch
            {
                try { Execute("ROLLBACK"); } catch { /* already rolled back */ }
                throw;
            }
        }
    }

    public int UserVersion
    {
        get => Query("PRAGMA user_version").FirstOrDefault()?.Int("user_version") ?? 0;
        set => Execute($"PRAGMA user_version = {value}");
    }
}
