using Reader.Core.Data;
using Reader.Core.Model;
using Reader.Core.Sync;
using Reader.Core.Telemetry;

namespace Reader.Core.Reader;

public enum ReadingMode
{
    /// <summary>The PDF's own page images.</summary>
    Pages,
    /// <summary>Extracted text reflowed to the window, like an ebook.</summary>
    Text,
}

public static class ReadingModes
{
    public static string RawValue(this ReadingMode m) => m == ReadingMode.Pages ? "PAGES" : "TEXT";
    public static ReadingMode? Parse(string? raw) => raw switch { "PAGES" => ReadingMode.Pages, "TEXT" => ReadingMode.Text, _ => null };
}

/// <summary>What the reader needs from any document; lets the restore logic be tested without a file.</summary>
public interface IReaderDocument
{
    /// <summary>PDF pages, or EPUB chapters: the unit a <see cref="ReadingPosition"/> counts in.</summary>
    int PageCount { get; }
    void Close();
}

/// <summary>
/// Layout inputs that change point offsets. A change means the position must be re-applied.
/// <see cref="ViewportWidth"/> is the width the content is laid out at: in two-page spreads that
/// is the column width, so switching spreads counts as a new layout. <see cref="Paged"/> tells
/// page-by-page flowing text from continuous scroll (as on Android).
/// </summary>
public readonly record struct LayoutKey(int ViewportWidth, float Zoom, ReadingMode Mode = ReadingMode.Pages, float TextScale = 1, bool Paged = false);

/// <summary>What opening a document produces.</summary>
public sealed record LoadedDocument(string Title, IReaderDocument Document, ReadingMode Mode = ReadingMode.Pages, float TextScale = 1);

public enum RestoreReason { Initial, Remote, Relayout, User }

public abstract record Phase
{
    public sealed record Loading : Phase;
    public sealed record Failed(string Message) : Phase;
    /// <summary>
    /// The UI must scroll to <see cref="Target"/> (null = the beginning) and then call
    /// <see cref="ReaderController.OnRestoreApplied"/> with the same <see cref="Token"/>.
    /// </summary>
    public sealed record Restoring(int Token, ReadingPosition? Target, RestoreReason Reason) : Phase;
    /// <summary>Scroll reports are user navigation and get saved.</summary>
    public sealed record Reading : Phase;

    public static readonly Phase LoadingPhase = new Loading();
    public static readonly Phase ReadingPhase = new Reading();

    public Restoring? AsRestoring => this as Restoring;
    public bool IsReading => this is Reading;
}

public abstract record Notice
{
    /// <summary>Opened where the user left off.</summary>
    public sealed record Resumed(int PageIndex) : Notice;
    /// <summary>A newer position from another device arrived and was applied before the user moved.</summary>
    public sealed record SyncedFromOtherDevice(int PageIndex) : Notice;
    /// <summary>A newer position from another device arrived after the user started reading here.</summary>
    public sealed record OtherDeviceAvailable(ReadingPosition Position) : Notice;
}

public sealed record ReaderUiState
{
    public string Title { get; init; } = "";
    public IReaderDocument? Document { get; init; }
    public Phase Phase { get; init; } = Phase.LoadingPhase;
    public float Zoom { get; init; } = 1;
    public ReadingMode Mode { get; init; } = ReadingMode.Pages;
    /// <summary>Text size multiplier in flowing-text mode.</summary>
    public float TextScale { get; init; } = 1;
    public int PageIndex { get; init; }
    public Notice? Notice { get; init; }

    public LayoutKey LayoutKeyFor(int viewportWidth, bool paged = false) => new(viewportWidth, Zoom, Mode, TextScale, paged);
}

/// <summary>
/// Restore-and-track state machine for one open document. A line-for-line port of the Mac and
/// Android <c>ReaderController</c>, so all three apps make the same decisions.
///
/// Rules that keep saved progress safe:
///  - Nothing is saved until the initial restore has been applied, so loading and layout (which
///    start at page 0) can never overwrite a saved position.
///  - Only scroll reports produced under the currently applied layout count. Reports made while
///    a resize, zoom, text-size or mode change is re-laying out the view are ignored until the
///    position has been re-applied to the new layout.
///  - A position arriving from the server is applied only if it's newer than what was restored
///    and the user hasn't navigated since. Otherwise it's offered, never forced.
///
/// Runs on one thread (the UI thread, or the tests' single-threaded context); every await
/// resumes there.
/// </summary>
public sealed class ReaderController
{
    private ReaderUiState _state = new();
    public ReaderUiState State
    {
        get => _state;
        private set
        {
            if (Equals(_state, value)) return;
            _state = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    public event Action? Changed;

    private readonly string _documentId;
    private readonly Func<Task<LoadedDocument>> _loadDocument;
    private readonly ReadingPositionRepository _positions;
    private readonly SyncEngine _sync;
    private readonly IRestoreTelemetry _telemetry;
    private readonly IScheduler _scheduler;
    private readonly long _firstOpenRemoteWaitMs;
    private readonly PositionSaver _saver;

    private int _pageCount;
    private int _token;
    private LayoutKey? _appliedLayout;
    /// <summary>Where the viewport is now, as last applied or reported.</summary>
    public ReadingPosition? Current { get; private set; }
    /// <summary>The stored position the reader was restored from (null if it started fresh).</summary>
    private ReadingPosition? _restoredFrom;
    private bool _userMoved;
    private bool _initialRestoreDone;
    /// <summary>Set by <see cref="RefreshFromServer"/>, cleared by any navigation.</summary>
    private bool _untouchedSinceRefresh;

    /// <summary>Loading and the initial restore target. Tests await it.</summary>
    public Task? StartTask { get; private set; }
    /// <summary>The server pull that may arrive after the initial restore. Tests await it.</summary>
    public Task? RemoteTask { get; private set; }

    /// <summary>Increases with every restore the UI is asked to apply.</summary>
    public int RestoreToken => _token;

    public const float MinTextScale = 0.7f;
    public const float MaxTextScale = 2f;

    public ReaderController(
        string documentId,
        Func<Task<LoadedDocument>> loadDocument,
        ReadingPositionRepository positions,
        SyncEngine sync,
        IRestoreTelemetry telemetry,
        IScheduler scheduler,
        long firstOpenRemoteWaitMs = 1_500)
    {
        _documentId = documentId;
        _loadDocument = loadDocument;
        _positions = positions;
        _sync = sync;
        _telemetry = telemetry;
        _scheduler = scheduler;
        _firstOpenRemoteWaitMs = firstOpenRemoteWaitMs;
        _saver = new PositionSaver(scheduler, p =>
        {
            try { _positions.SaveLocal(p); } catch { /* the next save retries */ }
        });
    }

    public string DocumentId => _documentId;

    public void Start() => StartTask = RunAsync();

    private async Task RunAsync()
    {
        LoadedDocument loaded;
        try
        {
            loaded = await _loadDocument();
        }
        catch (Exception e)
        {
            State = State with { Phase = new Phase.Failed(e is ReaderException r ? r.Message : (string.IsNullOrEmpty(e.Message) ? "Couldn't open document" : e.Message)) };
            return;
        }
        var doc = loaded.Document;
        _pageCount = doc.PageCount;
        State = State with { Title = loaded.Title, Document = doc, Mode = loaded.Mode, TextScale = loaded.TextScale };

        var count = _pageCount;
        ReadingPosition? local = null;
        try { local = _positions.Load(_documentId)?.ValidFor(count); } catch { }
        var pull = PullValidAsync(count);

        var initial = local;
        // New on this device: briefly wait for the server rather than flash page 1 and jump.
        if (local is null && _sync.IsEnabled)
        {
            initial = await FirstOf(pull, _firstOpenRemoteWaitMs);
        }
        _restoredFrom = initial;
        BeginRestore(initial, RestoreReason.Initial);

        RemoteTask = OnPulledAsync(pull);
    }

    private async Task<ReadingPosition?> PullValidAsync(int count) => (await _sync.PullAsync(_documentId))?.ValidFor(count);

    private async Task OnPulledAsync(Task<ReadingPosition?> pull)
    {
        if (await pull is { } remote) OnRemote(remote);
    }

    /// <summary>The UI's layout inputs changed (first layout, resize, zoom, mode, text size, spreads).</summary>
    public void OnLayout(LayoutKey key)
    {
        if (State.Phase.IsReading && key != _appliedLayout)
        {
            BeginRestore(Current, RestoreReason.Relayout);
        }
    }

    /// <summary>
    /// The UI finished scrolling for restore <paramref name="token"/> under <paramref name="layout"/>.
    /// <paramref name="pageIndex"/>/<paramref name="pageOffset"/> is where it actually landed,
    /// which can differ from the target near the end of the document.
    /// </summary>
    public void OnRestoreApplied(int token, LayoutKey layout, int pageIndex, float pageOffset, float horizontalOffset, bool atEnd)
    {
        if (State.Phase.AsRestoring is not { } restoring || restoring.Token != token) return;
        _appliedLayout = layout;
        var target = restoring.Target;
        Current = Position(pageIndex, pageOffset, horizontalOffset);

        if (target is not null && pageIndex != target.PageIndex && !atEnd)
        {
            _telemetry.Record(RestoreEvent.RestoreMismatch, $"target={target.PageIndex} actual={pageIndex}");
            if (pageIndex == 0) _telemetry.Record(RestoreEvent.UnexpectedReset, $"target={target.PageIndex}");
        }

        var notice = State.Notice;
        switch (restoring.Reason)
        {
            case RestoreReason.Initial:
                _initialRestoreDone = true;
                _saver.MarkStored(target);
                if (target is not null)
                {
                    _telemetry.Record(target.Stamp.DeviceId == _positions.DeviceId ? RestoreEvent.RestoredLocal : RestoreEvent.RestoredRemote);
                    if (!target.IsAtStart) notice = new Notice.Resumed(target.PageIndex);
                }
                else
                {
                    _telemetry.Record(RestoreEvent.FirstOpen);
                }
                break;
            case RestoreReason.Remote:
                _saver.MarkStored(target);
                break;
            case RestoreReason.Relayout:
            case RestoreReason.User:
                break;
        }
        State = State with { Phase = Phase.ReadingPhase, PageIndex = pageIndex, Notice = notice };
    }

    /// <summary>A scroll report from the UI. Saved only if it's real navigation under the applied layout.</summary>
    public void OnScrolled(LayoutKey layout, int pageIndex, float pageOffset, float horizontalOffset)
    {
        if (!State.Phase.IsReading || layout != _appliedLayout) return;
        var pos = Position(pageIndex, pageOffset, horizontalOffset);
        if (pos.SamePlace(Current)) return;
        Current = pos;
        _userMoved = true;
        _untouchedSinceRefresh = false;
        if (State.PageIndex != pageIndex) State = State with { PageIndex = pageIndex };
        _saver.Submit(pos);
    }

    public void SetZoom(float zoom)
    {
        var z = ReadingPosition.Clamp(zoom, ReadingPosition.MinZoom, ReadingPosition.MaxZoom);
        if (z == State.Zoom) return;
        State = State with { Zoom = z };
        if (Current is not null) Current = Current with { Zoom = z };
        if (_initialRestoreDone)
        {
            _userMoved = true;
            if (Current is { } c) _saver.Submit(c);
        }
        // The UI reports the new LayoutKey via OnLayout, which re-applies the position.
    }

    /// <summary>
    /// Switches between original pages and flowing text. The position is kept as-is (it's
    /// expressed per source page, so it means the same place in both) and re-applied to the new
    /// layout via <see cref="OnLayout"/>.
    /// </summary>
    public void SetMode(ReadingMode mode)
    {
        if (mode != State.Mode) State = State with { Mode = mode };
    }

    /// <summary>Flowing-text size. Like a mode switch, it keeps the position and re-applies it.</summary>
    public void SetTextScale(float scale)
    {
        var s = ReadingPosition.Clamp(scale, MinTextScale, MaxTextScale);
        if (s != State.TextScale) State = State with { TextScale = s };
    }

    /// <summary>Jump to the top of page <paramref name="index"/> (the scrubber, Home/End). Saved like any navigation.</summary>
    public void GoToPage(int index)
    {
        if (!_initialRestoreDone || _pageCount <= 0) return;
        _untouchedSinceRefresh = false;
        var target = Position(Math.Clamp(index, 0, _pageCount - 1), 0, 0);
        _userMoved = true;
        Current = target;
        _saver.Submit(target);
        BeginRestore(target, RestoreReason.User);
        State = State with { PageIndex = target.PageIndex };
    }

    /// <summary>
    /// Jump to an exact place (a page turn in page-by-page mode, a Find match). Saved like any
    /// navigation; the UI re-applies it like a restore.
    /// </summary>
    public void GoTo(int pageIndex, float pageOffset)
    {
        if (!_initialRestoreDone || _pageCount <= 0) return;
        _untouchedSinceRefresh = false;
        var target = Position(Math.Clamp(pageIndex, 0, _pageCount - 1), ReadingPosition.Clamp(pageOffset, 0, 1), 0);
        _userMoved = true;
        Current = target;
        _saver.Submit(target);
        BeginRestore(target, RestoreReason.User);
        State = State with { PageIndex = target.PageIndex };
    }

    /// <summary>Explicit "start from the beginning". Saved immediately so it wins everywhere.</summary>
    public void RestartFromBeginning()
    {
        _untouchedSinceRefresh = false;
        var start = Position(0, 0, 0);
        _userMoved = true;
        Current = start;
        _telemetry.Record(RestoreEvent.UserRestart);
        _saver.Submit(start);
        _saver.Flush();
        BeginRestore(start, RestoreReason.User);
        State = State with { Notice = null };
    }

    /// <summary>Accepts an <see cref="Notice.OtherDeviceAvailable"/> offer.</summary>
    public void Jump(ReadingPosition target)
    {
        _untouchedSinceRefresh = false;
        _userMoved = true;
        Current = target;
        _saver.Submit(target);
        _saver.Flush();
        BeginRestore(target, RestoreReason.User);
        State = State with { Notice = null };
    }

    public void DismissNotice() => State = State with { Notice = null };

    /// <summary>
    /// Checks the server for a newer position while the book is open (the app was activated
    /// again). Unlike the pull at open, this compares with everything saved here: local reading
    /// is flushed first, and the server's position counts only if it won the merge, i.e. it's
    /// newer than any reading done on this device.
    ///  - Not touched here since the refresh started: jump to it, with a notice.
    ///  - Scrolled while the request was out: offer it, never force it, and don't re-save here
    ///    (the other device's reading really is newer).
    /// </summary>
    public void RefreshFromServer()
    {
        if (!_initialRestoreDone || !_sync.IsEnabled || !State.Phase.IsReading) return;
        _saver.Flush();
        _untouchedSinceRefresh = true;
        var count = _pageCount;
        RemoteTask = RefreshAsync(count);
    }

    private async Task RefreshAsync(int count)
    {
        if ((await _sync.PullAsync(_documentId))?.ValidFor(count) is not { } remote) return;
        ReadingPosition? stored = null;
        try { stored = _positions.Load(_documentId); } catch { }
        if (stored is null || stored.Stamp != remote.Stamp) return;
        if (remote.SamePlace(Current)) return;
        _restoredFrom = remote;
        if (_untouchedSinceRefresh && State.Phase.IsReading)
        {
            _telemetry.Record(RestoreEvent.RemoteAppliedLate, $"page={remote.PageIndex} refresh");
            State = State with { Notice = new Notice.SyncedFromOtherDevice(remote.PageIndex) };
            BeginRestore(remote, RestoreReason.Remote);
        }
        else
        {
            State = State with { Notice = new Notice.OtherDeviceAvailable(remote) };
        }
    }

    /// <summary>Going back to the library, window close, exit, sleep: write what we have now.</summary>
    public void Flush() => _saver.Flush();

    private void OnRemote(ReadingPosition remote)
    {
        if (_restoredFrom is { } @base && !remote.IsNewerThan(@base)) return; // stale or already applied
        if (remote.SamePlace(Current)) return;
        if (!_userMoved)
        {
            _restoredFrom = remote;
            if (_initialRestoreDone || State.Phase.AsRestoring is null)
            {
                _telemetry.Record(RestoreEvent.RemoteAppliedLate, $"page={remote.PageIndex}");
                State = State with { Notice = new Notice.SyncedFromOtherDevice(remote.PageIndex) };
                BeginRestore(remote, RestoreReason.Remote);
            }
            else
            {
                // Initial restore hasn't been applied yet: just retarget it.
                BeginRestore(remote, RestoreReason.Initial);
            }
        }
        else
        {
            // The user is reading here; keep this device's position as the latest, and offer
            // the other one.
            if (Current is { } here)
            {
                try { _positions.SaveLocal(here); } catch { }
            }
            State = State with { Notice = new Notice.OtherDeviceAvailable(remote) };
        }
    }

    private void BeginRestore(ReadingPosition? target, RestoreReason reason)
    {
        _token += 1;
        var s = State with { Phase = new Phase.Restoring(_token, target, reason) };
        if (target is not null) s = s with { Zoom = target.Zoom };
        State = s;
    }

    private ReadingPosition Position(int pageIndex, float pageOffset, float horizontalOffset) => new()
    {
        DocumentId = _documentId,
        PageIndex = pageIndex,
        PageOffset = pageOffset,
        HorizontalOffset = horizontalOffset,
        Zoom = State.Zoom,
        PageCount = _pageCount,
        Stamp = new Stamp(0, ""),
    };

    /// <summary>The result of <paramref name="task"/>, or null if the scheduler's timeout fires first.</summary>
    private Task<ReadingPosition?> FirstOf(Task<ReadingPosition?> task, long timeoutMs)
    {
        var tcs = new TaskCompletionSource<ReadingPosition?>();
        var done = false;
        var timer = _scheduler.Schedule(timeoutMs, () =>
        {
            if (done) return;
            done = true;
            tcs.SetResult(null);
        });
        _ = Wait();
        return tcs.Task;

        async Task Wait()
        {
            var value = await task;
            if (done) return;
            done = true;
            timer.Cancel();
            tcs.SetResult(value);
        }
    }
}

public sealed class ReaderException(string message) : Exception(message);
