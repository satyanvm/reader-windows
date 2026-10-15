using Reader.Core.Model;

namespace Reader.Core.Reader;

/// <summary>Page dimensions in PDF points.</summary>
public readonly record struct PageSize(int Width, int Height);

/// <summary>Where a vertical scroll lands: page index and point offset into it.</summary>
public readonly record struct ScrollTarget(int Index, int Offset);

/// <summary>
/// Converts between device-independent page fractions and scroll offsets for a given layout.
/// Pages are laid out at <see cref="ContentWidth"/> wide, so their heights (and every point
/// offset) depend on the window while the fractions do not. Same integer math as Android's and
/// the Mac's <c>PageLayout</c>.
/// </summary>
public sealed class PageLayout
{
    public int ContentWidth { get; }
    /// <summary>Space below each page.</summary>
    public int Gap { get; }
    public int[] PageHeights { get; }
    /// <summary>Top of each page in document coordinates (y down), starting at <c>topInset</c>.</summary>
    public int[] PageTops { get; }
    public int TotalHeight { get; }
    public int PageCount => PageHeights.Length;

    public PageLayout(IReadOnlyList<PageSize> pages, int contentWidth, int gap, int topInset = 0)
    {
        ContentWidth = contentWidth;
        Gap = gap;
        PageHeights = pages.Select(p => Math.Max(1, (int)((long)contentWidth * p.Height / Math.Max(1, p.Width)))).ToArray();
        PageTops = new int[PageHeights.Length];
        var y = topInset;
        for (var i = 0; i < PageHeights.Length; i++)
        {
            PageTops[i] = y;
            y += PageHeights[i] + gap;
        }
        TotalHeight = y;
    }

    /// <summary>Fraction of page <paramref name="index"/> above the viewport top, given an offset into that page's slot.</summary>
    public float Fraction(int index, int offset)
    {
        if (index < 0 || index >= PageHeights.Length) return 0;
        return ReadingPosition.Clamp((float)offset / PageHeights[index], 0, 1);
    }

    public ScrollTarget ScrollTargetFor(int pageIndex, float fraction)
    {
        if (PageCount == 0) return new ScrollTarget(0, 0);
        var index = Math.Clamp(pageIndex, 0, PageCount - 1);
        var offset = (int)MathF.Round(ReadingPosition.Clamp(fraction, 0, 1) * PageHeights[index], MidpointRounding.AwayFromZero);
        return new ScrollTarget(index, offset);
    }

    /// <summary>The page slot (page plus its gap) containing document y, and the offset into it.</summary>
    public (int Index, int Offset) Slot(double y)
    {
        if (PageCount == 0) return (0, 0);
        int lo = 0, hi = PageCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (PageTops[mid] <= y) lo = mid; else hi = mid - 1;
        }
        return (lo, Math.Max(0, (int)Math.Round(y - PageTops[lo], MidpointRounding.AwayFromZero)));
    }

    /// <summary>Document y of a saved (page, fraction).</summary>
    public int Y(int pageIndex, float fraction)
    {
        var t = ScrollTargetFor(pageIndex, fraction);
        if (PageCount == 0) return 0;
        return PageTops[t.Index] + t.Offset;
    }

    public static int ContentWidthFor(int viewportWidth, float zoom) =>
        Math.Max(1, (int)MathF.Round(viewportWidth * zoom, MidpointRounding.AwayFromZero));
}
