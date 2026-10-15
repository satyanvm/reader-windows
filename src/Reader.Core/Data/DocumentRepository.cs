using System.Security.Cryptography;
using System.Text;
using Reader.Core.Model;
using Reader.Core.Reader;
using Reader.Core.Text;

namespace Reader.Core.Data;

public sealed record DocumentRecord(
    string Id,
    string Title,
    string LocalPath,
    long SizeBytes,
    int PageCount,
    long LastOpenedAt,
    /// <summary>Last mode used for this document on this device: "PAGES" or "TEXT". Not synced.</summary>
    string ReadingMode,
    /// <summary>"PDF" or "EPUB".</summary>
    string Format);

public sealed record LibraryRow(string Id, string Title, int PageCount, long LastOpenedAt, int? PageIndex, float? PageOffset, string Format)
{
    /// <summary>"Page 187 of 500 · 37%", "Chapter 2 of 12 · 40%", or "Not started · 500 pages".</summary>
    public string ProgressLabel
    {
        get
        {
            var epub = Format == "EPUB";
            if (PageIndex is not { } page) return $"Not started · {PageCount} {(epub ? "chapters" : "pages")}";
            var read = ((float)page + (PageOffset ?? 0)) / (float)Math.Max(1, PageCount);
            return $"{(epub ? "Chapter" : "Page")} {page + 1} of {PageCount} · {(int)(read * 100)}%";
        }
    }
}

public sealed class UnsupportedFormatException() : Exception("Only PDF and EPUB files are supported");

/// <summary>
/// Imports books into app storage and keeps the library list.
///
/// Each file is copied into the app's own storage and named by the SHA-256 of its bytes. The
/// hash is the document id everywhere, including sync: the same file opened on another device
/// (Mac, Android or Windows) gets the same id. The copy keeps the book readable offline, after
/// reboots, and after the original is moved or deleted.
/// </summary>
public sealed class DocumentRepository
{
    private readonly ReaderDatabase _db;
    private readonly string _dir;
    private readonly Func<long> _now;

    public string Directory => _dir;

    public DocumentRepository(ReaderDatabase db, string directory, Func<long>? now = null)
    {
        _db = db;
        _dir = directory;
        _now = now ?? HybridClock.SystemMillis;
        System.IO.Directory.CreateDirectory(directory);
    }

    public List<LibraryRow> Library() =>
        _db.Sql.Query("""
            SELECT d.id, d.title, d.pageCount, d.lastOpenedAt, p.pageIndex, p.pageOffset, d.format
            FROM documents d LEFT JOIN reading_positions p ON p.documentId = d.id
            ORDER BY d.lastOpenedAt DESC
            """).Select(r => new LibraryRow(
                r.String("id") ?? "",
                r.String("title") ?? "",
                r.Int("pageCount") ?? 0,
                r.Int64("lastOpenedAt") ?? 0,
                r.Int("pageIndex"),
                r.Double("pageOffset") is { } d ? (float)d : null,
                r.String("format") ?? "PDF")).ToList();

    public List<DocumentRecord> All() =>
        _db.Sql.Query("SELECT * FROM documents").Select(Record).ToList();

    public DocumentRecord? Get(string id) =>
        _db.Sql.Query("SELECT * FROM documents WHERE id = ?", id).Select(Record).FirstOrDefault();

    public void Touch(string id)
    {
        _db.Sql.Execute("UPDATE documents SET lastOpenedAt = ? WHERE id = ?", _now(), id);
        ReaderDatabase.PostChange();
    }

    public void SetReadingMode(string id, ReadingMode mode) =>
        _db.Sql.Execute("UPDATE documents SET readingMode = ? WHERE id = ?", mode.RawValue(), id);

    public void Remove(string id)
    {
        if (Get(id) is { } doc)
        {
            try { File.Delete(doc.LocalPath); } catch { }
        }
        _db.Sql.Execute("DELETE FROM documents WHERE id = ?", id);
        ReaderDatabase.PostChange();
    }

    /// <summary>
    /// Copies <paramref name="source"/> into app storage (hashing as it goes) and adds it to the
    /// library. Importing the same bytes again returns the existing document. A new book is titled
    /// <paramref name="title"/> if given (a book synced from the account keeps the account's
    /// title), else by its metadata, else by its file name. <paramref name="openedAt"/> places it in
    /// the last-opened order (a synced book sorts by when it was added to the account).
    /// </summary>
    public DocumentRecord ImportFile(string source, string? title = null, long? openedAt = null)
    {
        var tmp = Path.Combine(_dir, $"import-{Guid.NewGuid():N}.tmp");
        try
        {
            string id;
            try
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                using var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[1 << 16];
                int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    hasher.AppendData(buf, 0, n);
                    output.Write(buf, 0, n);
                }
                output.Flush(flushToDisk: true);
                id = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ReaderException($"Can't open {Path.GetFileName(source)}: {e.Message}");
            }

            string format;
            if (IsPdf(tmp)) format = "PDF";
            else if (EpubBook.IsEpub(tmp)) format = "EPUB";
            else throw new UnsupportedFormatException();
            var target = Path.Combine(_dir, $"{id}.{format.ToLowerInvariant()}");
            if (!File.Exists(target)) File.Move(tmp, target);

            DocumentRecord record;
            if (Get(id) is { } existing)
            {
                record = existing with { LastOpenedAt = openedAt ?? _now(), LocalPath = target };
            }
            else
            {
                int pages;
                string? bookTitle;
                if (format == "PDF")
                {
                    var pdf = new PdfBook(target);
                    (pages, bookTitle) = (pdf.PageCount, pdf.MetadataTitle);
                }
                else
                {
                    var book = EpubBook.Open(target);
                    (pages, bookTitle) = (book.PageCount, book.Title);
                    book.Close();
                }
                record = new DocumentRecord(
                    id,
                    !string.IsNullOrWhiteSpace(title) ? title : bookTitle ?? Path.GetFileNameWithoutExtension(source),
                    target,
                    new FileInfo(target).Length,
                    pages,
                    openedAt ?? _now(),
                    ReadingMode.Pages.RawValue(),
                    format);
            }
            Put(record);
            return record;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>SHA-256 hex of a file, as used for document ids.</summary>
    public static string Sha256Hex(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private void Put(DocumentRecord d)
    {
        _db.Sql.Execute("""
            INSERT OR REPLACE INTO documents (id, title, localPath, sizeBytes, pageCount, lastOpenedAt, readingMode, format)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            d.Id, d.Title, d.LocalPath, d.SizeBytes, (long)d.PageCount, d.LastOpenedAt, d.ReadingMode, d.Format);
        ReaderDatabase.PostChange();
    }

    private static bool IsPdf(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[1024];
            var n = f.Read(head, 0, head.Length);
            return Encoding.Latin1.GetString(head, 0, n).Contains("%PDF-");
        }
        catch
        {
            return false;
        }
    }

    internal static DocumentRecord Record(SqliteDatabase.Row r) => new(
        r.String("id") ?? "",
        r.String("title") ?? "",
        r.String("localPath") ?? "",
        r.Int64("sizeBytes") ?? 0,
        r.Int("pageCount") ?? 0,
        r.Int64("lastOpenedAt") ?? 0,
        r.String("readingMode") ?? "PAGES",
        r.String("format") ?? "PDF");
}
