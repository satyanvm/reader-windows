using System.Collections.Concurrent;
using Reader.Core.Data;
using Reader.Core.Model;
using Reader.Core.Reader;
using Reader.Core.Sync;
using Reader.Core.Telemetry;

namespace Reader.Core.Tests;

public static class T
{
    public const string DOC = "doc-sha256";
    public const int PAGES = 500;

    public static ReadingPosition Pos(int page, float offset = 0, float zoom = 1) => new()
    {
        DocumentId = DOC,
        PageIndex = page,
        PageOffset = offset,
        Zoom = zoom,
        PageCount = PAGES,
        Stamp = new Stamp(0, ""),
    };

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reader-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Runs <paramref name="body"/> on one thread with its own synchronization context, the way
    /// the controller runs on the UI thread (Swift's @MainActor in the Mac tests).
    /// </summary>
    public static void Run(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        var context = new SingleThreadContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = body();
            task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.RunUntilComplete();
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class SingleThreadContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            try { _queue.Add((d, state)); }
            catch (InvalidOperationException) { /* finished */ }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public void Complete() => _queue.CompleteAdding();

        public void RunUntilComplete()
        {
            foreach (var (d, state) in _queue.GetConsumingEnumerable()) d(state);
        }
    }
}

/// <summary>Like Kotlin's CompletableDeferred&lt;Unit&gt;: waiters suspend until <see cref="Open"/>.</summary>
public sealed class Gate
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Wait() => _tcs.Task;
    public void Open() => _tcs.TrySetResult();
}

/// <summary>Server with the contract's compare-and-store semantics, plus knobs for failure tests.</summary>
public sealed class FakeServer
{
    public readonly Dictionary<string, ReadingPosition> Rows = new();
    public bool Online = true;
    /// <summary>When set, <c>GetAsync</c> waits on it, simulating a slow response.</summary>
    public Gate? GetGate;

    public ReadingPosition Put(ReadingPosition p)
    {
        if (Rows.TryGetValue(p.DocumentId, out var existing) && !(p.Stamp > existing.Stamp)) return existing;
        Rows[p.DocumentId] = p;
        return p;
    }

    private sealed class Api(FakeServer server) : ISyncApi
    {
        public async Task<ReadingPosition?> GetAsync(string documentId)
        {
            if (server.GetGate is { } gate) await gate.Wait();
            if (!server.Online) throw new SyncUnavailableException("offline");
            return server.Rows.GetValueOrDefault(documentId);
        }

        public Task<ReadingPosition> PutAsync(ReadingPosition position)
        {
            if (!server.Online) throw new SyncUnavailableException("offline");
            return Task.FromResult(server.Put(position));
        }
    }

    public ISyncApi CreateApi() => new Api(this);
}

public sealed class SignedIn : IAccountSession
{
    public bool IsSignedIn => true;
    public string? Token() => "t";
}

/// <summary>One device: its own durable store, clock and id, sharing a server with other devices.</summary>
public sealed class Device
{
    public string Id { get; }
    private readonly FakeServer? _server;
    private sealed class Wall { public long Ms; }
    private readonly Wall _wall = new();
    public long WallClock
    {
        get => _wall.Ms;
        set => _wall.Ms = value;
    }
    public IPositionStore Store { get; }
    public HybridClock Clock { get; }
    public ReadingPositionRepository Repo { get; }
    public SyncEngine Sync { get; }

    public Device(string id, FakeServer? server, long wallClock = 1_000_000, IPositionStore? store = null)
    {
        Id = id;
        _server = server;
        Store = store ?? new InMemoryPositionStore();
        _wall.Ms = wallClock;
        var wall = _wall;
        Clock = new HybridClock(() => wall.Ms);
        Repo = new ReadingPositionRepository(Store, Clock, id);
        Sync = new SyncEngine(Repo, server?.CreateApi(), new SignedIn());
    }

    /// <summary>Same device after an app restart: same disk, fresh process state.</summary>
    public Device Restarted() => new(Id, _server, WallClock, Store);
}

public sealed class FakeDocument(int pageCount = T.PAGES) : IReaderDocument
{
    public int PageCount { get; } = pageCount;
    public void Close() { }
}

public sealed class RecordingTelemetry : IRestoreTelemetry
{
    public readonly List<RestoreEvent> Events = new();
    public void Record(RestoreEvent e, string detail = "") => Events.Add(e);
}
