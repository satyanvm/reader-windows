using System.Net.NetworkInformation;

namespace Reader.Core.Sync;

/// <summary>
/// Background delivery of pending positions while the app runs: shortly after each local change,
/// with exponential backoff on failure, every 15 minutes, and as soon as the network comes back.
/// Pending rows live in the database, so anything not delivered before quitting goes out on the
/// next launch (which calls <c>RequestSync(0)</c>).
/// </summary>
public sealed class SyncScheduler : IDisposable
{
    private readonly SyncEngine _engine;
    private readonly object _queue = new();
    private Timer? _scheduled;
    private bool _running;
    private bool _rerun;
    private TimeSpan _backoff = TimeSpan.FromSeconds(30);
    private Timer? _periodic;
    private bool _started;
    private readonly SynchronizationContext? _main;

    public static readonly TimeSpan Period = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    /// <summary>Called on the UI context after every push attempt: (succeeded, when).</summary>
    public Action<bool, DateTimeOffset>? OnResult { get; set; }
    /// <summary>Called on the UI context when the network comes back (library sync listens too).</summary>
    public Action? OnReconnect { get; set; }

    public SyncScheduler(SyncEngine engine)
    {
        _engine = engine;
        _main = SynchronizationContext.Current;
    }

    /// <summary>Starts the periodic and on-reconnect pushes. Safe to call again after signing in.</summary>
    public void Start()
    {
        if (!_engine.IsEnabled) return;
        lock (_queue)
        {
            if (_started)
            {
                RequestSync(TimeSpan.Zero);
                return;
            }
            _started = true;
            _periodic = new Timer(_ => RequestSync(TimeSpan.Zero), null, Period, Period);
        }
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        RequestSync(TimeSpan.Zero);
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        RequestSync(TimeSpan.Zero);
        Post(() => OnReconnect?.Invoke());
    }

    public void RequestSync() => RequestSync(TimeSpan.FromSeconds(5));

    public void RequestSync(TimeSpan delay)
    {
        if (!_engine.IsEnabled) return;
        lock (_queue)
        {
            if (_running)
            {
                // A run reads every pending row when it starts; rerun to cover this change.
                _rerun = true;
                return;
            }
            _scheduled?.Dispose();
            _scheduled = new Timer(_ => Run(), null, delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Run()
    {
        lock (_queue)
        {
            if (_running) return;
            _running = true;
        }
        Task.Run(async () =>
        {
            bool ok;
            try { ok = await _engine.PushPendingAsync().ConfigureAwait(false); }
            catch { ok = false; }
            var report = OnResult;
            var at = DateTimeOffset.Now;
            Post(() => report?.Invoke(ok, at));
            lock (_queue)
            {
                _running = false;
                if (ok)
                {
                    _backoff = TimeSpan.FromSeconds(30);
                    if (_rerun)
                    {
                        _rerun = false;
                        RequestSync(TimeSpan.Zero);
                    }
                }
                else
                {
                    var wait = _backoff;
                    _backoff = TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
                    _rerun = false;
                    RequestSync(wait);
                }
            }
        });
    }

    private void Post(Action a)
    {
        if (_main is null) a();
        else _main.Post(_ => a(), null);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        lock (_queue)
        {
            _scheduled?.Dispose();
            _periodic?.Dispose();
        }
    }
}
