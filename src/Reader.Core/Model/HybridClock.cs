namespace Reader.Core.Model;

/// <summary>
/// Hybrid logical clock, in epoch milliseconds.
///
/// Every value it hands out is strictly greater than any value it handed out before or observed
/// from another device. That keeps "most recent reading activity wins" correct even when a
/// device's wall clock is behind: once this device has seen a position from another device, its
/// next save always orders after it.
/// </summary>
public sealed class HybridClock
{
    private readonly Func<long> _wallClock;
    private readonly object _lock = new();
    private long _last;

    public HybridClock(Func<long>? wallClock = null, long initial = 0)
    {
        _wallClock = wallClock ?? SystemMillis;
        _last = initial;
    }

    public long Now()
    {
        lock (_lock)
        {
            _last = Math.Max(_wallClock(), _last + 1);
            return _last;
        }
    }

    public void Observe(long remote)
    {
        lock (_lock) { if (remote > _last) _last = remote; }
    }

    public long Peek()
    {
        lock (_lock) return _last;
    }

    public static long SystemMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
