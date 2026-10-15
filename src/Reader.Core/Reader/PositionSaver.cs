using Reader.Core.Model;

namespace Reader.Core.Reader;

/// <summary>
/// Turns a stream of scroll positions into a small number of durable writes.
///
/// A write happens <c>debounceMs</c> after scrolling settles, and at least every <c>maxWaitMs</c>
/// during continuous scrolling, so a long reading session is never more than a few seconds from
/// disk. <see cref="Flush"/> writes immediately and is called on going back to the library,
/// window close, exit, suspend and sleep. Nothing relies on it, because the app can be killed
/// without warning.
///
/// Writes are synchronous (SQLite with WAL), so a flush has finished when it returns.
/// </summary>
public sealed class PositionSaver
{
    private readonly IScheduler _scheduler;
    private readonly Action<ReadingPosition> _persist;
    private readonly long _debounceMs;
    private readonly long _maxWaitMs;
    private ReadingPosition? _pending;
    private long _pendingSince;
    private ReadingPosition? _lastWritten;
    private ICancellable? _timer;

    public PositionSaver(IScheduler scheduler, Action<ReadingPosition> persist, long debounceMs = 500, long maxWaitMs = 3_000)
    {
        _scheduler = scheduler;
        _persist = persist;
        _debounceMs = debounceMs;
        _maxWaitMs = maxWaitMs;
    }

    public void Submit(ReadingPosition position)
    {
        if (_pending is null) _pendingSince = _scheduler.NowMs;
        _pending = position;
        var waited = _scheduler.NowMs - _pendingSince;
        var wait = Math.Min(_debounceMs, Math.Max(0, _maxWaitMs - waited));
        _timer?.Cancel();
        _timer = _scheduler.Schedule(wait, Write);
    }

    /// <summary>
    /// Tells the saver what's already stored (after a restore), so a later write is skipped only
    /// if it matches what's on disk now, not something written before a remote position replaced it.
    /// </summary>
    public void MarkStored(ReadingPosition? position) => _lastWritten = position;

    /// <summary>Writes any pending position now.</summary>
    public void Flush()
    {
        _timer?.Cancel();
        _timer = null;
        Write();
    }

    private void Write()
    {
        if (_pending is not { } next) return;
        _pending = null;
        if (next.SamePlace(_lastWritten)) return;
        _persist(next);
        _lastWritten = next;
    }
}
