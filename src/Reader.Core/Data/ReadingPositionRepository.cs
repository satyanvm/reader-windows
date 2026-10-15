using Reader.Core.Model;

namespace Reader.Core.Data;

/// <summary>
/// The single place that decides which reading position wins.
///
/// Policy: last writer wins, ordered by <see cref="Stamp"/> (hybrid-logical-clock time, then
/// device id). Local saves and server responses both go through the same compare-and-write, so a
/// stale write from anywhere (a late network response, an old offline save, a retried push) can
/// never replace a newer position. "Start from the beginning" is an ordinary save and therefore
/// also propagates.
///
/// Calls are synchronous and thread-safe: a save is on disk when <see cref="SaveLocal"/> returns.
/// </summary>
public sealed class ReadingPositionRepository
{
    private readonly IPositionStore _store;
    private readonly HybridClock _clock;
    public string DeviceId { get; }
    /// <summary>Called after a local change that the server hasn't seen yet.</summary>
    public Action OnPendingChange { get; set; }

    private readonly object _clockInit = new();
    private bool _clockReady;

    public ReadingPositionRepository(IPositionStore store, HybridClock clock, string deviceId, Action? onPendingChange = null)
    {
        _store = store;
        _clock = clock;
        DeviceId = deviceId;
        OnPendingChange = onPendingChange ?? (() => { });
    }

    /// <summary>The clock must never go backwards across restarts, so seed it from what's on disk.</summary>
    private HybridClock ReadyClock()
    {
        lock (_clockInit)
        {
            if (!_clockReady)
            {
                _clock.Observe(_store.MaxHlc());
                _clockReady = true;
            }
        }
        return _clock;
    }

    public ReadingPosition? Load(string documentId) => _store.Transaction(tx => tx.Get(documentId))?.Position;

    /// <summary>
    /// Records that the user is now at <paramref name="position"/> on this device. The stamp on
    /// <paramref name="position"/> is ignored and replaced with a fresh one. Returns the stamped position.
    /// </summary>
    public ReadingPosition SaveLocal(ReadingPosition position)
    {
        var stamped = position.WithStamp(new Stamp(ReadyClock().Now(), DeviceId));
        var written = _store.Transaction(tx =>
        {
            var existing = tx.Get(stamped.DocumentId)?.Position;
            // A fresh local stamp beats anything already observed; the check only matters if
            // the store was written by someone who didn't go through this repository.
            if (!stamped.IsNewerThan(existing)) return false;
            tx.Put(new StoredPosition(stamped, true));
            return true;
        });
        if (written) OnPendingChange();
        return stamped;
    }

    /// <summary>Merges a position that came from the server. Returns whichever position is now stored.</summary>
    public ReadingPosition MergeRemote(ReadingPosition remote)
    {
        ReadyClock().Observe(remote.Stamp.Hlc);
        return _store.Transaction(tx =>
        {
            var existing = tx.Get(remote.DocumentId);
            if (existing is null || remote.IsNewerThan(existing.Position))
            {
                tx.Put(new StoredPosition(remote, false));
                return remote;
            }
            // The server already has exactly what we have: nothing left to push.
            if (existing.Position.Stamp == remote.Stamp && existing.PendingSync)
            {
                tx.Put(existing with { PendingSync = false });
            }
            return existing.Position;
        });
    }

    /// <summary>Positions saved on this device that the server hasn't acknowledged.</summary>
    public List<ReadingPosition> Pending() => _store.Pending().Select(s => s.Position).ToList();
}
