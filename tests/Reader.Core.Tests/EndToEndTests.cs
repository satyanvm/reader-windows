using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Reader.Core.Data;
using Reader.Core.Library;
using Reader.Core.Model;
using Reader.Core.Reader;
using Reader.Core.Sync;
using Reader.Core.Telemetry;
using Xunit;
using static Reader.Core.Tests.T;

namespace Reader.Core.Tests;

/// <summary>Runs only when READER_E2E_SERVER points at a running sync server (e.g. http://127.0.0.1:8787).</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("READER_E2E_SERVER"))) Skip = "set READER_E2E_SERVER to run against a sync server";
    }
}

/// <summary>
/// Windows against the real sync server (server/ in the Android repository), using the same Core
/// classes the app wires together: SyncAccount, SyncEngine, ReaderController, LibrarySync.
/// "Another device" talks to the server over plain HTTP, the way the Android app does.
/// </summary>
public class EndToEndTests
{
    private static Uri Server => new(Environment.GetEnvironmentVariable("READER_E2E_SERVER") ?? "http://127.0.0.1:8787");
    private static readonly HttpClient Http = new();

    /// <summary>A Windows device: its own database, device id, sync engine and account.</summary>
    private sealed class WindowsDevice
    {
        public readonly string Dir = TempDir();
        public readonly ReaderDatabase Db;
        public readonly ReadingPositionRepository Positions;
        public readonly DocumentRepository Documents;
        public readonly SyncEngine Engine;
        public readonly SyncScheduler Scheduler;
        public readonly SyncAccount Account;
        public readonly LibrarySync Library;
        public readonly string DeviceId;

        public WindowsDevice(HttpMessageHandler? libraryServer = null)
        {
            Db = new ReaderDatabase(Path.Combine(Dir, "reader.sqlite"));
            DeviceId = Core.App.AppPaths.DeviceId(Dir);
            Positions = new ReadingPositionRepository(Db, new HybridClock(), DeviceId);
            Documents = new DocumentRepository(Db, Path.Combine(Dir, "documents"));
            Engine = new SyncEngine(Positions, null, new StaticTokenSession(""));
            Scheduler = new SyncScheduler(Engine);
            Account = new SyncAccount(Server, new InMemorySessionStore(), Engine, Scheduler, Positions);
            Library = new LibrarySync(Documents, Path.Combine(Dir, "library-sync.json"),
                () => Account.Token is { } t ? new HttpLibraryApi(Server.AbsoluteUri, () => t, libraryServer) : null,
                () => Account.IsSignedIn ? Account.Email : null);
        }

        public ReaderController Open(string documentId, int pageCount, IScheduler scheduler, RecordingTelemetry telemetry) =>
            new(documentId, () => Task.FromResult(new LoadedDocument("Book", new FakeDocument(pageCount))), Positions, Engine, telemetry, scheduler);
    }

    private static async Task<string> SignIn(string email, string password)
    {
        var info = await new AccountApi(Server).SignInAsync(email, password);
        return info.Token;
    }

    /// <summary>What the Android app sends for a position (org.json: floats widened to double).</summary>
    private static async Task<JsonElement> AndroidPut(string token, string documentId, int page, float offset, int pageCount, long hlc, string deviceId)
    {
        var json = $"{{\"documentId\":\"{documentId}\",\"pageIndex\":{page},\"pageOffset\":{((double)offset).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}," +
                   $"\"horizontalOffset\":0,\"zoom\":1,\"pageCount\":{pageCount},\"hlc\":{hlc},\"deviceId\":\"{deviceId}\",\"schemaVersion\":1}}";
        using var req = new HttpRequestMessage(HttpMethod.Put, new Uri(Server, $"v1/reading-positions/{documentId}"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement?> AndroidGet(string token, string documentId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(Server, $"v1/reading-positions/{documentId}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await Http.SendAsync(req);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string NewEmail() => $"win-{Guid.NewGuid():N}@example.com";

    [E2EFact]
    public void PositionsRoundTripBetweenAnotherDeviceAndWindowsBitForBit() => Run(async () =>
    {
        var email = NewEmail();
        const string password = "correct horse battery";
        var windows = new WindowsDevice();
        await windows.Account.CreateAccountAsync(email, password); // EMAIL_CODES off: signed in at once
        Assert.True(windows.Account.IsSignedIn);
        var phone = await SignIn(email, password);

        // The phone read to page 187, 40% down.
        var doc = new string('d', 63) + "1";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await AndroidPut(phone, doc, 186, 0.4f, 500, now, "android-phone");

        // A fresh Windows device opens the book: it waits briefly for the server and lands there.
        var scheduler = new TestScheduler();
        var telemetry = new RecordingTelemetry();
        var c = windows.Open(doc, 500, scheduler, telemetry);
        c.Start();
        await c.StartTask!;
        var target = c.State.Phase.AsRestoring!.Target!;
        Assert.Equal(186, target.PageIndex);
        Assert.Equal(BitConverter.SingleToInt32Bits(0.4f), BitConverter.SingleToInt32Bits(target.PageOffset));
        c.ApplyRestore(new LayoutKey(900, 1));
        Assert.Contains(RestoreEvent.RestoredRemote, telemetry.Events);

        // Reading on Windows goes up and the phone sees it, exactly.
        c.OnScrolled(new LayoutKey(900, 1), 199, 0.25f, 0);
        scheduler.RunUntilIdle();
        Assert.True(await windows.Engine.PushPendingAsync());
        var seen = (await AndroidGet(phone, doc))!.Value;
        Assert.Equal(199, seen.GetProperty("pageIndex").GetInt32());
        Assert.Equal(0.25, seen.GetProperty("pageOffset").GetDouble());
        Assert.Equal(windows.DeviceId, seen.GetProperty("deviceId").GetString());
        Assert.Empty(windows.Positions.Pending());

        // Start from the beginning is just a newer save: it propagates.
        c.RestartFromBeginning();
        Assert.True(await windows.Engine.PushPendingAsync());
        Assert.Equal(0, (await AndroidGet(phone, doc))!.Value.GetProperty("pageIndex").GetInt32());

        // The phone reads on; Windows, back in the app, moves there (it hadn't moved since).
        c.ApplyRestore(new LayoutKey(900, 1));
        var later = windows.Positions.Load(doc)!.Stamp.Hlc + 1000;
        await AndroidPut(phone, doc, 321, 0.5f, 500, later, "android-phone");
        c.RefreshFromServer();
        await c.RemoteTask!;
        Assert.Equal(321, c.State.Phase.AsRestoring?.Target?.PageIndex);
        Assert.Equal(new Notice.SyncedFromOtherDevice(321), c.State.Notice);
    });

    [E2EFact]
    public void AStaleWriteFromAnotherDeviceLosesOnTheServerAndHere() => Run(async () =>
    {
        var email = NewEmail();
        var windows = new WindowsDevice();
        await windows.Account.CreateAccountAsync(email, "password123");
        var phone = await SignIn(email, "password123");
        var doc = new string('e', 64);
        windows.Positions.SaveLocal(new ReadingPosition { DocumentId = doc, PageIndex = 50, PageOffset = 0.1f, PageCount = 300 });
        Assert.True(await windows.Engine.PushPendingAsync());
        // An old offline save from the phone arrives late: the server keeps Windows' position.
        var winner = await AndroidPut(phone, doc, 10, 0f, 300, 1_000, "android-phone");
        Assert.Equal(50, winner.GetProperty("pageIndex").GetInt32());
        Assert.Equal(50, (await windows.Engine.PullAsync(doc))!.PageIndex);
    });

    [E2EFact]
    public void SigningOutEverywhereSignsWindowsOutAndKeepsUnsyncedReading() => Run(async () =>
    {
        var email = NewEmail();
        var windows = new WindowsDevice();
        await windows.Account.CreateAccountAsync(email, "password123");
        var token = windows.Account.Token!;
        // This session is ended from elsewhere.
        await new AccountApi(Server).SignOutAsync(token);
        windows.Positions.SaveLocal(new ReadingPosition { DocumentId = new string('f', 64), PageIndex = 7, PageCount = 20 });
        Assert.False(await windows.Engine.PushPendingAsync());
        await Task.Yield();
        Assert.False(windows.Account.IsSignedIn);
        Assert.Equal(SyncAccount.SignedOutMessage, windows.Account.LastError);
        Assert.Single(windows.Positions.Pending());
    });

    /// <summary>
    /// The library endpoints (not in the sync server yet) in memory, accepting exactly the tokens the
    /// real server accepts (checked with GET /v1/me), so the library flow runs with real accounts.
    /// </summary>
    private sealed class LibraryOnRealAccounts : HttpMessageHandler
    {
        public readonly FakeLibraryServer Library = new();
        private readonly HttpMessageInvoker _library;

        public LibraryOnRealAccounts() => _library = new HttpMessageInvoker(Library);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var me = new HttpRequestMessage(HttpMethod.Get, new Uri(Server, "v1/me"));
            me.Headers.Authorization = request.Headers.Authorization;
            using var res = await Http.SendAsync(me, ct);
            if (res.StatusCode != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"unauthorized\",\"code\":\"unauthorized\"}") };
            }
            return await _library.SendAsync(request, ct);
        }
    }

    [E2EFact]
    public void ABookAddedOnAnotherDeviceAppearsInAFreshWindowsLibraryWithItsPositionAndRemovalPropagates() => Run(async () =>
    {
        var server = new LibraryOnRealAccounts();
        var email = NewEmail();
        var phone = (await new AccountApi(Server).SignUpAsync(email, "password123"))!.Token;

        // "Another device" adds a book to the account through the library API: metadata, then the file.
        var path = Path.Combine(TempDir(), "phone.pdf");
        Pdfs.Make([Pdfs.BookPage(1, ["From the phone."]), Pdfs.BookPage(2, ["Page two."])], path);
        var bytes = File.ReadAllBytes(path);
        var id = FakeLibraryServer.Sha(bytes);
        var api = new HttpLibraryApi(Server.AbsoluteUri, () => phone, server);
        await api.PutAsync(id, "The Phone's Book", "PDF", bytes.Length, 2);
        await api.UploadFileAsync(id, path);
        await AndroidPut(phone, id, 1, 0.5f, 2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "android-phone");

        // A fresh Windows library signs in: the account's books arrive.
        var windows = new WindowsDevice(server);
        Assert.Empty(windows.Documents.Library());
        await windows.Account.SignInAsync(email, "password123");
        var result = await windows.Library.SyncAsync();
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        var row = Assert.Single(windows.Documents.Library());
        Assert.Equal("The Phone's Book", row.Title);
        Assert.Equal(id, row.Id);

        // Its position comes from the server when the book opens.
        var c = windows.Open(id, 2, new TestScheduler(), new RecordingTelemetry());
        c.Start();
        await c.StartTask!;
        Assert.Equal(1, c.State.Phase.AsRestoring?.Target?.PageIndex);
        Assert.Equal(0.5f, c.State.Phase.AsRestoring?.Target?.PageOffset);
        Assert.Equal("Page 2 of 2 · 75%", windows.Documents.Library()[0].ProgressLabel);

        // A book added on Windows goes up to the account.
        var mine = Path.Combine(TempDir(), "windows.pdf");
        Pdfs.Make([Pdfs.BookPage(1, ["Added on Windows."])], mine);
        var added = windows.Documents.ImportFile(mine);
        windows.Library.NoteLocalAdd(added.Id);
        await windows.Library.SyncAsync();
        Assert.Contains(await api.ListAsync(), b => b.DocumentId == added.Id && b.HasFile && b.IsAlive);

        // The phone removes its book: it goes from Windows too.
        await api.RemoveAsync(id);
        var after = await windows.Library.SyncAsync();
        Assert.Equal(1, after.RemovedHere);
        Assert.DoesNotContain(windows.Documents.Library(), r => r.Id == id);
    });
}
