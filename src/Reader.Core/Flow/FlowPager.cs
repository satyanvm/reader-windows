namespace Reader.Core.Flow;

/// <summary>
/// Page-by-page flowing text: one page at a time, or two side by side (a spread, like an open
/// book). Each page shows whole lines; the line cut by the bottom edge is hidden and starts the
/// next page.
///
/// In a spread the right page continues exactly where the left page's hidden line begins. The
/// boundary is handed over as a character (<see cref="PageSlice.NextChar"/>), never as a
/// y-coordinate, and both pages' ends are computed from the same laid-out text, so the columns
/// can't disagree. A turn moves a whole spread.
///
/// "Back" returns to exactly the page a forward turn came from (the starts are remembered), and
/// otherwise computes the previous page from line geometry.
/// </summary>
public sealed class FlowPager
{
    private readonly FlowLayout _layout;
    private readonly Stack<FlowPos> _history = new();

    public FlowPager(FlowLayout layout) => _layout = layout;

    /// <summary>The top of the first (left) page.</summary>
    public FlowPos Start { get; private set; }
    /// <summary>1 for one page, 2 for a spread.</summary>
    public int Columns { get; private set; } = 1;
    /// <summary>The height of a page's text area.</summary>
    public double PageHeight { get; private set; } = 1;

    public void Configure(int columns, double pageHeight)
    {
        Columns = Math.Clamp(columns, 1, 2);
        PageHeight = Math.Max(1, pageHeight);
    }

    /// <summary>Show the page(s) starting at the line <paramref name="p"/> falls in. Forgets the turn history.</summary>
    public void Reset(FlowPos p)
    {
        _history.Clear();
        Start = _layout.AlignToLine(p);
    }

    /// <summary>The visible pages, left to right.</summary>
    public IReadOnlyList<PageSlice> Pages()
    {
        var first = _layout.Page(Start, PageHeight);
        if (Columns == 1) return [first];
        // The right page starts at the character the left page's hidden line begins with.
        if (first.NextChar is not { } handoff)
        {
            return [first, new PageSlice(first.Start, first.StartChar, 0, null, null, AtEnd: true)];
        }
        var second = _layout.Page(_layout.PosOf(handoff), PageHeight);
        return [first, second];
    }

    public bool AtEnd => Pages()[^1].AtEnd || Pages()[^1].VisibleHeight <= 0;

    public bool AtStart => _layout.PreviousPageStart(Start, PageHeight) is null;

    /// <summary>Turns forward one page (two in a spread). False at the end.</summary>
    public bool Next()
    {
        var pages = Pages();
        var last = pages[^1];
        if (last.AtEnd || last.NextChar is not { } next) return false;
        _history.Push(Start);
        Start = _layout.PosOf(next);
        return true;
    }

    /// <summary>Turns back one page (two in a spread). False at the beginning.</summary>
    public bool Previous()
    {
        while (_history.Count > 0)
        {
            var candidate = _history.Pop();
            // Still valid if a forward turn from it lands here (the layout hasn't changed).
            var saved = Start;
            Start = candidate;
            var pages = Pages();
            Start = saved;
            if (pages[^1].NextChar is { } c && _layout.Same(_layout.PosOf(c), Start))
            {
                Start = candidate;
                return true;
            }
            _history.Clear();
        }
        var p = Start;
        for (var i = 0; i < Columns; i++)
        {
            if (_layout.PreviousPageStart(p, PageHeight) is not { } prev)
            {
                if (i == 0) return false;
                break;
            }
            p = prev;
        }
        Start = p;
        return true;
    }
}
