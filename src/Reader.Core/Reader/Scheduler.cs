namespace Reader.Core.Reader;

public interface ICancellable
{
    void Cancel();
}

/// <summary>Timers for the reader's UI-thread logic. Injected so tests run on virtual time.</summary>
public interface IScheduler
{
    /// <summary>Monotonic milliseconds.</summary>
    long NowMs { get; }
    ICancellable Schedule(long afterMs, Action work);
}

/// <summary>Real timers whose work runs on the synchronization context that created the scheduler (the UI thread).</summary>
public sealed class ContextScheduler : IScheduler
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public long NowMs => Environment.TickCount64;

    private sealed class Item : ICancellable
    {
        public Timer? Timer;
        public volatile bool Cancelled;
        public void Cancel()
        {
            Cancelled = true;
            Timer?.Dispose();
        }
    }

    public ICancellable Schedule(long afterMs, Action work)
    {
        var item = new Item();
        item.Timer = new Timer(_ =>
        {
            item.Timer?.Dispose();
            if (item.Cancelled) return;
            if (_context is null) work();
            else _context.Post(_ => { if (!item.Cancelled) work(); }, null);
        }, null, Math.Max(0, afterMs), Timeout.Infinite);
        return item;
    }
}

/// <summary>Virtual time: nothing runs until <see cref="Advance"/> or <see cref="RunUntilIdle"/>.</summary>
public sealed class TestScheduler : IScheduler
{
    public long NowMs { get; private set; }
    private int _seq;
    private readonly List<(long Due, int Seq, Box Box)> _items = new();

    private sealed class Box(Action work) : ICancellable
    {
        public Action? Work = work;
        public void Cancel() => Work = null;
    }

    public bool HasPending => _items.Any(i => i.Box.Work is not null);

    public ICancellable Schedule(long afterMs, Action work)
    {
        _seq += 1;
        var box = new Box(work);
        _items.Add((NowMs + Math.Max(0, afterMs), _seq, box));
        return box;
    }

    public void Advance(long ms)
    {
        var end = NowMs + ms;
        while (NextDue() is { } next && next.Due <= end)
        {
            NowMs = Math.Max(NowMs, next.Due);
            Fire(next);
        }
        NowMs = end;
    }

    public void RunUntilIdle()
    {
        while (NextDue() is { } next)
        {
            NowMs = Math.Max(NowMs, next.Due);
            Fire(next);
        }
    }

    private (long Due, int Seq, Box Box)? NextDue()
    {
        _items.RemoveAll(i => i.Box.Work is null);
        if (_items.Count == 0) return null;
        return _items.MinBy(i => (i.Due, i.Seq));
    }

    private void Fire((long Due, int Seq, Box Box) item)
    {
        _items.RemoveAll(i => i.Seq == item.Seq);
        var work = item.Box.Work;
        item.Box.Work = null;
        work?.Invoke();
    }
}
