using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reader.Core.Data;
using Reader.Core.Library;
using Reader.Core.Sync;
using Xunit;
using static Reader.Core.Tests.T;

namespace Reader.Core.Tests;

/// <summary>
/// The library half of the sync server, in memory, behind <see cref="HttpLibraryApi"/>'s real
/// HTTP code path: the documented routes, status codes, the 100 MB limit, Content-Length and the
/// SHA-256 check on uploads. Tombstones delete the file; PUT un-removes.
/// </summary>
public sealed class FakeLibraryServer : HttpMessageHandler
{
    public sealed class Book
    {
        public required string Id;
        public string Title = "";
        public string Format = "PDF";
        public long SizeBytes;
        public int PageCount;
        public byte[]? File;
        public long AddedAt;
        public long? RemovedAt;
        public RemoteBook ToRemote() => new(Id, Title, Format, SizeBytes, PageCount, File is not null, AddedAt, RemovedAt);
    }

    public readonly Dictionary<string, Book> Books = new();
    public readonly List<string> Log = new();
    public bool Online = true;
    public string? RejectToken;
    public long MaxBytes = HttpLibraryApi.MaxFileBytes;
    public long Now = 1_000;
    private readonly object _lock = new();

    /// <summary>Another device adds a book through the API.</summary>
    public void AddFromOtherDevice(string title, string format, byte[] bytes, int pageCount)
    {
        var id = Sha(bytes);
        lock (_lock) Books[id] = new Book { Id = id, Title = title, Format = format, SizeBytes = bytes.Length, PageCount = pageCount, File = bytes, AddedAt = ++Now };
    }

    public void RemoveFromOtherDevice(string id)
    {
        lock (_lock)
        {
            Books[id].RemovedAt = ++Now;
            Books[id].File = null;
        }
    }

    public static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!Online) throw new HttpRequestException("offline");
        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method.Method;
        lock (_lock) Log.Add($"{method} {path}");
        var token = request.Headers.Authorization?.Parameter;
        if (token is null || token == RejectToken) return Json(401, new { error = "missing or invalid token", code = "unauthorized" });

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is ["v1", "library"] && method == "GET")
        {
            lock (_lock) return Raw(200, $"{{\"books\":[{string.Join(",", Books.Values.Select(b => HttpLibraryApi.BookJson(b.ToRemote())))}]}}");
        }
        if (segments.Length >= 3 && segments[0] == "v1" && segments[1] == "library")
        {
            var id = Uri.UnescapeDataString(segments[2]);
            var file = segments.Length == 4 && segments[3] == "file";
            if (!file && method == "PUT")
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                var o = doc.RootElement;
                var format = o.GetProperty("format").GetString();
                if (format is not ("PDF" or "EPUB")) return Json(422, new { error = "format" });
                lock (_lock)
                {
                    if (!Books.TryGetValue(id, out var b)) Books[id] = b = new Book { Id = id, AddedAt = ++Now };
                    b.Title = o.GetProperty("title").GetString() ?? "";
                    b.Format = format;
                    b.SizeBytes = o.GetProperty("sizeBytes").GetInt64();
                    b.PageCount = o.GetProperty("pageCount").GetInt32();
                    b.RemovedAt = null;
                    return Raw(200, HttpLibraryApi.BookJson(b.ToRemote()));
                }
            }
            if (!file && method == "DELETE")
            {
                lock (_lock)
                {
                    if (!Books.TryGetValue(id, out var b)) return Json(404, new { error = "not found" });
                    b.RemovedAt ??= ++Now;
                    b.File = null;
                    return Json(200, new { removed = true });
                }
            }
            if (file && method == "PUT")
            {
                if (request.Content?.Headers.ContentLength is not { } length) return Json(411, new { error = "length required" });
                if (length > MaxBytes) return Json(413, new { error = "too large" });
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length != length) return Json(400, new { error = "length mismatch" });
                if (Sha(bytes) != id) return Json(422, new { error = "sha256 must equal the id" });
                lock (_lock)
                {
                    if (!Books.TryGetValue(id, out var b) || b.RemovedAt is not null) return Json(404, new { error = "add the book first" });
                    b.File = bytes;
                    return Raw(200, HttpLibraryApi.BookJson(b.ToRemote()));
                }
            }
            if (file && method == "GET")
            {
                lock (_lock)
                {
                    if (!Books.TryGetValue(id, out var b) || b.File is null) return Json(404, new { error = "no file" });
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b.File) };
                }
            }
        }
        return Json(404, new { error = "not found" });
    }

    private static HttpResponseMessage Json(int status, object body) => Raw(status, JsonSerializer.Serialize(body));

    private static HttpResponseMessage Raw(int status, string json) =>
        new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>One device's library: its own database, books folder and sync state.</summary>
public sealed class LibraryDevice
{
    public readonly string Dir;
    public readonly ReaderDatabase Db;
    public readonly DocumentRepository Documents;
    public readonly LibrarySync Sync;
    public bool SignedIn = true;
    public string Account = "reader@example.com";
    public int Unauthorized;

    public LibraryDevice(FakeLibraryServer server, string? dir = null)
    {
        Dir = dir ?? TempDir();
        Db = new ReaderDatabase(Path.Combine(Dir, "reader.sqlite"));
        Documents = new DocumentRepository(Db, Path.Combine(Dir, "documents"));
        var api = new HttpLibraryApi("https://reader.test", () => "t", server);
        Sync = new LibrarySync(Documents, Path.Combine(Dir, "library-sync.json"), () => SignedIn ? api : null, () => SignedIn ? Account : null);
        Sync.OnUnauthorized = () => Unauthorized++;
    }

    /// <summary>What the app does when the user opens a file.</summary>
    public DocumentRecord Add(string path)
    {
        var doc = Documents.ImportFile(path);
        Sync.NoteLocalAdd(doc.Id);
        return doc;
    }

    /// <summary>What the app does for "Remove from Library on All Devices".</summary>
    public void Remove(string id)
    {
        Documents.Remove(id);
        if (SignedIn) Sync.NoteLocalRemove(id);
    }

    public List<string> Titles() => Documents.Library().Select(r => r.Title).OrderBy(t => t).ToList();

    /// <summary>The same device after a restart: same folder, fresh objects.</summary>
    public LibraryDevice Restarted(FakeLibraryServer server) => new(server, Dir);
}

public class LibrarySyncTests
{
    private readonly FakeLibraryServer _server = new();
    private readonly string _files = TempDir();

    private string MakePdf(string name, string text, string? title = null)
    {
        // The same name is the same file (the same bytes), like opening one file twice.
        var path = Path.Combine(_files, name);
        if (!File.Exists(path)) Pdfs.Make([Pdfs.BookPage(1, [text])], path, title);
        return path;
    }

    [Fact]
    public async Task BooksAddedHereAreUploadedMetadataFirstThenTheFile()
    {
        var device = new LibraryDevice(_server);
        var doc = device.Add(MakePdf("Meditations.pdf", "One."));

        var result = await device.Sync.SyncAsync();
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Equal(1, result.Uploaded);
        var b = _server.Books[doc.Id];
        Assert.Equal("Meditations", b.Title);
        Assert.Equal("PDF", b.Format);
        Assert.Equal(1, b.PageCount);
        Assert.Equal(File.ReadAllBytes(doc.LocalPath), b.File);
        var put = _server.Log.IndexOf($"PUT /v1/library/{doc.Id}");
        var file = _server.Log.IndexOf($"PUT /v1/library/{doc.Id}/file");
        Assert.True(put >= 0 && file > put, string.Join("\n", _server.Log));

        // Nothing to do the second time.
        _server.Log.Clear();
        Assert.Equal(0, (await device.Sync.SyncAsync()).Uploaded);
        Assert.Equal(["GET /v1/library"], _server.Log);
    }

    [Fact]
    public async Task BooksInTheAccountAreDownloadedUnderTheAccountsTitle()
    {
        var bytes = File.ReadAllBytes(MakePdf("x.pdf", "From the phone.", title: "Metadata Title"));
        _server.AddFromOtherDevice("Title From The Account", "PDF", bytes, 1);
        var fresh = new LibraryDevice(_server);

        var result = await fresh.Sync.SyncAsync();
        Assert.Equal(1, result.Downloaded);
        var row = Assert.Single(fresh.Documents.Library());
        Assert.Equal("Title From The Account", row.Title);
        Assert.Equal(FakeLibraryServer.Sha(bytes), row.Id);
        Assert.Equal(bytes, File.ReadAllBytes(fresh.Documents.Get(row.Id)!.LocalPath));
        Assert.Empty(Directory.GetFiles(Path.Combine(fresh.Documents.Directory, "downloads")));
    }

    [Fact]
    public async Task ARemovalHereReachesTheAccountAndOtherDevicesRemoveIt()
    {
        var a = new LibraryDevice(_server);
        var b = new LibraryDevice(_server);
        var doc = a.Add(MakePdf("Shared.pdf", "Shared."));
        await a.Sync.SyncAsync();
        await b.Sync.SyncAsync();
        Assert.Equal(["Shared"], b.Titles());

        a.Remove(doc.Id);
        var ra = await a.Sync.SyncAsync();
        Assert.Equal(1, ra.RemovedThere);
        Assert.NotNull(_server.Books[doc.Id].RemovedAt);
        Assert.Null(_server.Books[doc.Id].File); // the tombstone deletes the file

        var rb = await b.Sync.SyncAsync();
        Assert.Equal(1, rb.RemovedHere);
        Assert.Empty(b.Titles());
        Assert.Empty(a.Titles());
    }

    [Fact]
    public async Task ATombstoneDoesNotRemoveABookThisDeviceNeverSawAliveItBringsItBack()
    {
        // Another device had the book and removed it while this one was offline and added the
        // same file itself.
        var bytes = File.ReadAllBytes(MakePdf("Dune.pdf", "Spice."));
        _server.AddFromOtherDevice("Dune", "PDF", bytes, 1);
        var id = FakeLibraryServer.Sha(bytes);
        _server.RemoveFromOtherDevice(id);

        var offline = new LibraryDevice(_server);
        offline.Add(MakePdf("Dune.pdf", "Spice."));
        var result = await offline.Sync.SyncAsync();
        Assert.Equal(["Dune"], offline.Titles());
        Assert.Equal(1, result.Uploaded);
        Assert.Null(_server.Books[id].RemovedAt); // un-removed
        Assert.Equal(bytes, _server.Books[id].File);
    }

    [Fact]
    public async Task ABookAddedOfflineAfterAnotherDeviceRemovedItComesBackEvenIfThisDeviceHadSeenIt()
    {
        var a = new LibraryDevice(_server);
        var b = new LibraryDevice(_server);
        var doc = a.Add(MakePdf("Book.pdf", "Text."));
        await a.Sync.SyncAsync();
        await b.Sync.SyncAsync();

        // B removes it; A, offline, opens the same file again.
        b.Remove(doc.Id);
        await b.Sync.SyncAsync();
        a.Add(MakePdf("Book.pdf", "Text."));
        await a.Sync.SyncAsync();
        Assert.Equal(["Book"], a.Titles());
        Assert.Null(_server.Books[doc.Id].RemovedAt);

        await b.Sync.SyncAsync();
        Assert.Equal(["Book"], b.Titles()); // back everywhere
    }

    [Fact]
    public async Task RemovalsMadeOfflineWaitAndSurviveARestart()
    {
        var a = new LibraryDevice(_server);
        var doc = a.Add(MakePdf("Offline.pdf", "x"));
        await a.Sync.SyncAsync();

        _server.Online = false;
        a.Remove(doc.Id);
        var failed = await a.Sync.SyncAsync();
        Assert.False(failed.Ok);
        Assert.Null(_server.Books[doc.Id].RemovedAt);

        _server.Online = true;
        var restarted = a.Restarted(_server);
        var ok = await restarted.Sync.SyncAsync();
        Assert.True(ok.Ok);
        Assert.NotNull(_server.Books[doc.Id].RemovedAt);
        Assert.Empty(restarted.Titles()); // and it isn't downloaded again
        Assert.Empty(LibrarySyncState.Load(Path.Combine(a.Dir, "library-sync.json")).PendingRemovals);
    }

    [Fact]
    public async Task ABookTooLargeToSyncIsReportedAndNotRetriedEveryTime()
    {
        _server.MaxBytes = 100; // every test PDF is bigger than this
        var a = new LibraryDevice(_server);
        a.Add(MakePdf("Huge.pdf", "x"));
        var first = await a.Sync.SyncAsync();
        Assert.Equal("“Huge” is too large to sync (over 100 MB).", Assert.Single(first.Errors));
        Assert.Equal(first.Errors[0], a.Sync.LastError);

        _server.Log.Clear();
        var second = await a.Sync.SyncAsync();
        Assert.DoesNotContain(_server.Log, l => l.EndsWith("/file"));
        Assert.True(second.Ok);
    }

    [Fact]
    public async Task UnauthorizedSignsOut()
    {
        _server.RejectToken = "t";
        var a = new LibraryDevice(_server);
        a.Add(MakePdf("A.pdf", "x"));
        var result = await a.Sync.SyncAsync();
        Assert.False(result.Ok);
        Assert.Equal(1, a.Unauthorized);
        Assert.Equal(["A"], a.Titles()); // nothing local is lost
    }

    [Fact]
    public async Task ADownloadWhoseBytesDontMatchItsIdIsRejected()
    {
        var bytes = File.ReadAllBytes(MakePdf("Real.pdf", "Real."));
        var wrongId = new string('a', 64);
        _server.Books[wrongId] = new FakeLibraryServer.Book { Id = wrongId, Title = "Liar", File = bytes, SizeBytes = bytes.Length, PageCount = 1 };
        var a = new LibraryDevice(_server);
        var result = await a.Sync.SyncAsync();
        Assert.Empty(a.Titles());
        Assert.Contains(result.Errors, e => e.Contains("Liar"));
    }

    [Fact]
    public async Task AnotherAccountStartsFromAFreshState()
    {
        var a = new LibraryDevice(_server);
        var doc = a.Add(MakePdf("Mine.pdf", "x"));
        await a.Sync.SyncAsync();
        Assert.Contains(doc.Id, LibrarySyncState.Load(Path.Combine(a.Dir, "library-sync.json")).SeenAlive);

        // A different account whose library has a tombstone for the same book: it was never seen
        // alive under that account, so it isn't removed here.
        var other = new FakeLibraryServer();
        other.Books[doc.Id] = new FakeLibraryServer.Book { Id = doc.Id, Title = "Mine", RemovedAt = 5 };
        var b = new LibraryDevice(other) { Account = "someone@example.com" };
        var shared = new LibrarySync(a.Documents, Path.Combine(a.Dir, "library-sync.json"),
            () => new HttpLibraryApi("https://reader.test", () => "t", other), () => "someone@example.com");
        await shared.SyncAsync();
        Assert.Equal(["Mine"], a.Titles());
        Assert.Equal("someone@example.com", LibrarySyncState.Load(Path.Combine(a.Dir, "library-sync.json")).Account);
    }

    [Fact]
    public async Task SignedOutDoesNothing()
    {
        var a = new LibraryDevice(_server) { SignedIn = false };
        a.Add(MakePdf("A.pdf", "x"));
        var result = await a.Sync.SyncAsync();
        Assert.True(result.Ok);
        Assert.Empty(_server.Log);
    }

    [Fact]
    public async Task ConcurrentRequestsCoalesceIntoOneMoreRun()
    {
        var a = new LibraryDevice(_server);
        a.Add(MakePdf("A.pdf", "x"));
        var t1 = a.Sync.SyncAsync();
        var t2 = a.Sync.SyncAsync();
        await Task.WhenAll(t1, t2);
        Assert.Single(_server.Books);
        Assert.False(a.Sync.IsSyncing);
    }
}
