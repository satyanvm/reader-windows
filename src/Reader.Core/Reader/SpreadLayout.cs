namespace Reader.Core.Reader;

/// <summary>
/// Original pages two at a time, side by side like an open book: rows of (1–2, 3–4, …), each row
/// as tall as its taller page, with a small gutter between the pages. Pages are laid out at the
/// column width, so the integer math is the same as <see cref="PageLayout"/>'s.
///
/// A position is the row's left page plus the fraction down the row, so it stays a valid
/// (page, fraction) in the shared format; Next and Previous move by two pages.
/// </summary>
public sealed class SpreadLayout
{
    public int ColumnWidth { get; }
    public int Gutter { get; }
    public int Gap { get; }
    public int[] PageWidths { get; }
    public int[] PageHeights { get; }
    public int[] RowHeights { get; }
    public int[] RowTops { get; }
    public int TotalHeight { get; }
    public int PageCount => PageHeights.Length;
    public int RowCount => RowHeights.Length;

    /// <summary>Below this window width, original pages show one page at a time.</summary>
    public const int MinSpreadWidth = 700;

    public static int ColumnWidthFor(int contentWidth, int gutter) => Math.Max(1, (contentWidth - gutter) / 2);

    public SpreadLayout(IReadOnlyList<PageSize> pages, int contentWidth, int gutter, int gap, int topInset = 0)
    {
        ColumnWidth = ColumnWidthFor(contentWidth, gutter);
        Gutter = gutter;
        Gap = gap;
        PageWidths = Enumerable.Repeat(ColumnWidth, pages.Count).ToArray();
        PageHeights = pages.Select(p => Math.Max(1, (int)((long)ColumnWidth * p.Height / Math.Max(1, p.Width)))).ToArray();
        var rows = (pages.Count + 1) / 2;
        RowHeights = new int[rows];
        RowTops = new int[rows];
        var y = topInset;
        for (var r = 0; r < rows; r++)
        {
            var left = PageHeights[r * 2];
            var right = r * 2 + 1 < pages.Count ? PageHeights[r * 2 + 1] : 0;
            RowHeights[r] = Math.Max(left, right);
            RowTops[r] = y;
            y += RowHeights[r] + gap;
        }
        TotalHeight = y;
    }

    public static int RowOf(int pageIndex) => Math.Max(0, pageIndex) / 2;

    /// <summary>The left page of the row holding <paramref name="pageIndex"/>.</summary>
    public static int LeftPageOf(int pageIndex) => RowOf(pageIndex) * 2;

    /// <summary>Fraction of row <paramref name="row"/> above the viewport top, given an offset into the row's slot.</summary>
    public float Fraction(int row, int offset)
    {
        if (row < 0 || row >= RowCount) return 0;
        return Model.ReadingPosition.Clamp((float)offset / RowHeights[row], 0, 1);
    }

    /// <summary>Document y of a saved (page, fraction): the row of that page, that far down the row.</summary>
    public int Y(int pageIndex, float fraction)
    {
        if (RowCount == 0) return 0;
        var row = Math.Clamp(RowOf(pageIndex), 0, RowCount - 1);
        var offset = (int)MathF.Round(Model.ReadingPosition.Clamp(fraction, 0, 1) * RowHeights[row], MidpointRounding.AwayFromZero);
        return RowTops[row] + offset;
    }

    /// <summary>The row at document y as (left page, fraction down the row).</summary>
    public (int PageIndex, float Fraction) PositionAt(double y)
    {
        if (RowCount == 0) return (0, 0);
        int lo = 0, hi = RowCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (RowTops[mid] <= y) lo = mid; else hi = mid - 1;
        }
        var offset = Math.Max(0, (int)Math.Round(y - RowTops[lo], MidpointRounding.AwayFromZero));
        return (lo * 2, Fraction(lo, offset));
    }

    /// <summary>The left edge of page <paramref name="pageIndex"/> within its row (its column, centred if it's alone).</summary>
    public int X(int pageIndex) => pageIndex % 2 == 0 ? 0 : ColumnWidth + Gutter;
}
