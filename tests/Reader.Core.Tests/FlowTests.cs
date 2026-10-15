using Reader.Core.Flow;
using Reader.Core.Reader;
using Xunit;

namespace Reader.Core.Tests;

/// <summary>
/// Units laid out like paragraphs: <c>spaceBefore</c> px of space, then lines <c>lineHeight</c>
/// px tall holding 40 characters each. A unit can instead be one unbreakable item (an image).
/// </summary>
public sealed class FakeMeasurer : IUnitMeasurer
{
    public readonly List<(int Lines, double LineHeight, double SpaceBefore, bool Image)> Units = new();
    public int Measured;

    public FakeMeasurer Paragraphs(int count, int lines, double lineHeight = 20, double spaceBefore = 10)
    {
        for (var i = 0; i < count; i++) Units.Add((lines, lineHeight, spaceBefore, false));
        return this;
    }

    public FakeMeasurer Image(double height)
    {
        Units.Add((1, height, 0, true));
        return this;
    }

    public int UnitCount => Units.Count;

    public UnitLayout Measure(int unit)
    {
        Measured++;
        var (n, lh, before, image) = Units[unit];
        if (image) return new UnitLayout(lh, [new FlowLine(0, lh, 0, 1)], 1);
        var lines = Enumerable.Range(0, n).Select(i => new FlowLine(before + i * lh, before + (i + 1) * lh, i * 40, (i + 1) * 40)).ToList();
        return new UnitLayout(before + n * lh, lines, n * 40);
    }
}

public class FlowLayoutTests
{
    [Fact]
    public void ThePageEndsAboveTheLineCutByTheBottomEdgeWhichStartsTheNextPage()
    {
        // Units are 110 px: 10 px space, then lines at 10–30, 30–50, …, 90–110.
        var layout = new FlowLayout(new FakeMeasurer().Paragraphs(10, 5));
        var page = layout.Page(new FlowPos(0, 0), 260);
        // Unit 2 starts at 220: its first line 230–250 fits, its second 250–270 doesn't.
        Assert.Equal(250, page.VisibleHeight);
        Assert.Equal(new FlowPos(2, 30), page.Next);
        Assert.Equal(new FlowChar(2, 40), page.NextChar);
        Assert.False(page.AtEnd);
    }

    [Fact]
    public void ALineThatExactlyFitsStaysOnThePage()
    {
        var layout = new FlowLayout(new FakeMeasurer().Paragraphs(10, 5));
        var page = layout.Page(new FlowPos(0, 0), 250);
        Assert.Equal(new FlowPos(2, 30), page.Next);
    }

    [Fact]
    public void AnItemTallerThanThePageIsCutAtThePageHeight()
    {
        var layout = new FlowLayout(new FakeMeasurer().Image(900).Paragraphs(3, 5));
        var page = layout.Page(new FlowPos(0, 0), 400);
        Assert.Equal(400, page.VisibleHeight);
        Assert.Equal(new FlowPos(0, 400), page.Next);
    }

    [Fact]
    public void TheLastPageIsAtTheEnd()
    {
        var layout = new FlowLayout(new FakeMeasurer().Paragraphs(2, 3));
        var page = layout.Page(new FlowPos(0, 0), 500);
        Assert.True(page.AtEnd);
        Assert.Null(page.Next);
    }

    [Fact]
    public void ARestoredPositionStartsThePageOnTheLineItFallsIn()
    {
        var layout = new FlowLayout(new FakeMeasurer().Paragraphs(10, 5));
        Assert.Equal(new FlowPos(3, 50), layout.AlignToLine(new FlowPos(3, 61)));
        Assert.Equal(new FlowPos(3, 5), layout.AlignToLine(new FlowPos(3, 5))); // in the space between paragraphs
        Assert.Equal(new FlowPos(4, 0), layout.AlignToLine(new FlowPos(3, 110))); // normalized into the next unit
    }

    [Fact]
    public void BackAfterForwardReturnsToExactlyThePageItCameFromFromGeometryAlone()
    {
        var layout = new FlowLayout(new FakeMeasurer().Paragraphs(40, 7, lineHeight: 23, spaceBefore: 14));
        var starts = new List<FlowPos> { new(0, 0) };
        while (layout.Page(starts[^1], 431).Next is { } next) starts.Add(next);
        Assert.True(starts.Count > 10);
        // Uniform lines: the earliest line a page can start on to end here is the page before.
        for (var i = starts.Count - 1; i > 0; i--)
        {
            var back = layout.PreviousPageStart(starts[i], 431);
            Assert.NotNull(back);
            Assert.Equal(starts[i], layout.Page(back!.Value, 431).Next); // a forward turn from it lands here again
        }
        Assert.Null(layout.PreviousPageStart(new FlowPos(0, 0), 431));
    }

    [Fact]
    public void TextAboveChangingHeightDoesNotMoveThePage()
    {
        var measurer = new FakeMeasurer().Paragraphs(10, 5);
        var layout = new FlowLayout(measurer);
        var at = new FlowPos(6, 30);
        var before = layout.Page(at, 300);
        measurer.Units[2] = (40, 20, 10, false); // a page above finished building
        layout.Invalidate(2);
        var after = layout.Page(at, 300);
        Assert.Equal(before, after);
    }

    [Fact]
    public void OnlyTheUnitsAroundTheReaderAreLaidOut()
    {
        var measurer = new FakeMeasurer().Paragraphs(10_000, 5);
        var layout = new FlowLayout(measurer);
        var pager = new FlowPager(layout);
        pager.Configure(2, 700);
        pager.Reset(layout.PosOf(5_000, 0.5f));
        for (var i = 0; i < 20; i++) pager.Next();
        for (var i = 0; i < 20; i++) pager.Previous();
        Assert.True(measurer.Measured < 400, $"{measurer.Measured} units measured"); // of 10,000
    }

    [Fact]
    public void UnitFractionRoundTrips()
    {
        var layout = new FlowLayout(new FakeMeasurer().Paragraphs(10, 5));
        var p = layout.PosOf(4, 0.25f);
        Assert.Equal(new FlowPos(4, 27.5), p);
        Assert.Equal((4, 0.25f), layout.UnitFraction(p));
    }
}

public class FlowPagerTests
{
    private static (FlowLayout Layout, FlowPager Pager) Make(int columns, double pageHeight = 260, int units = 30)
    {
        var layout = new FlowLayout(new FakeMeasurer().Paragraphs(units, 5));
        var pager = new FlowPager(layout);
        pager.Configure(columns, pageHeight);
        pager.Reset(new FlowPos(0, 0));
        return (layout, pager);
    }

    [Fact]
    public void EachTurnMovesExactlyOnePageAndBackReturnsToTheSamePage()
    {
        var (_, pager) = Make(1);
        var seen = new List<FlowPos> { pager.Start };
        for (var i = 0; i < 6; i++)
        {
            Assert.True(pager.Next());
            seen.Add(pager.Start);
        }
        Assert.Equal(new FlowPos(2, 30), seen[1]);
        for (var i = 5; i >= 0; i--)
        {
            Assert.True(pager.Previous());
            Assert.Equal(seen[i], pager.Start);
        }
        Assert.False(pager.Previous());
    }

    [Fact]
    public void TheRightPageContinuesExactlyAtTheCharacterTheLeftPageHid()
    {
        var (layout, pager) = Make(2);
        var pages = pager.Pages();
        Assert.Equal(2, pages.Count);
        var left = pages[0];
        var right = pages[1];
        Assert.Equal(new FlowChar(2, 40), left.NextChar); // the hidden line
        Assert.Equal(left.NextChar, right.StartChar);
        Assert.Equal(layout.PosOf(left.NextChar!.Value), right.Start);
        // Nothing is shown twice and nothing is skipped.
        Assert.Equal(layout.Distance(left.Start, right.Start), left.VisibleHeight);
    }

    [Fact]
    public void ASpreadTurnMovesTwoPagesAndBackReturnsToTheSameSpread()
    {
        var (_, single) = Make(1);
        single.Next();
        single.Next();
        var twoAhead = single.Start;

        var (_, spread) = Make(2);
        var first = spread.Start;
        Assert.True(spread.Next());
        Assert.Equal(twoAhead, spread.Start); // same text as two single-page turns
        Assert.True(spread.Previous());
        Assert.Equal(first, spread.Start);
    }

    [Fact]
    public void BackWithoutHistoryUsesGeometryAndForwardReturns()
    {
        var (layout, pager) = Make(1);
        pager.Reset(new FlowPos(12, 30)); // opened here: no turn history
        var here = pager.Start;
        Assert.True(pager.Previous());
        Assert.Equal(here, layout.Page(pager.Start, 260).Next);
        Assert.True(pager.Next());
        Assert.Equal(here, pager.Start);
        Assert.True(pager.Previous());
        Assert.Equal(here, layout.Page(pager.Start, 260).Next);
    }

    [Fact]
    public void ThereIsNoTurnPastTheEnd()
    {
        var (_, pager) = Make(2, units: 6);
        var turns = 0;
        while (pager.Next()) turns++;
        Assert.InRange(turns, 1, 3);
        Assert.True(pager.AtEnd);
        Assert.False(pager.Next());
    }

    [Fact]
    public void AStaleHistoryAfterARelayoutFallsBackToGeometry()
    {
        var measurer = new FakeMeasurer().Paragraphs(30, 5);
        var layout = new FlowLayout(measurer);
        var pager = new FlowPager(layout);
        pager.Configure(1, 260);
        pager.Reset(new FlowPos(0, 0));
        pager.Next();
        pager.Next();
        // Bigger text: every unit is taller now.
        for (var i = 0; i < measurer.Units.Count; i++) measurer.Units[i] = (8, 20, 10, false);
        layout.InvalidateAll();
        var here = pager.Start;
        Assert.True(pager.Previous());
        Assert.Equal(here, layout.Page(pager.Start, 260).Next);
    }
}

public class SpreadLayoutTests
{
    private static readonly PageSize Letter = new(612, 792);
    private static readonly PageSize Landscape = new(792, 612);

    [Fact]
    public void PagesSitInPairsAndEachRowIsAsTallAsItsTallerPage()
    {
        var layout = new SpreadLayout([Letter, Landscape, Letter, Letter, Landscape], contentWidth: 1212, gutter: 12, gap: 12, topInset: 12);
        Assert.Equal(600, layout.ColumnWidth);
        Assert.Equal(3, layout.RowCount);
        Assert.Equal(776, layout.PageHeights[0]);
        Assert.Equal(463, layout.PageHeights[1]);
        Assert.Equal(776, layout.RowHeights[0]);
        Assert.Equal(463, layout.RowHeights[2]); // the last page is alone
        Assert.Equal(12, layout.RowTops[0]);
        Assert.Equal(12 + 776 + 12, layout.RowTops[1]);
        Assert.Equal(0, layout.X(2));
        Assert.Equal(612, layout.X(3));
    }

    [Fact]
    public void APositionIsTheRowsLeftPagePlusTheFractionDownTheRow()
    {
        var layout = new SpreadLayout(Enumerable.Repeat(Letter, 500).ToArray(), 1400, 16, 12, 12);
        foreach (var page in new[] { 0, 1, 186, 187, 499 })
        {
            foreach (var fraction in new[] { 0f, 0.25f, 0.5f, 0.999f })
            {
                var (p, f) = layout.PositionAt(layout.Y(page, fraction));
                Assert.Equal(SpreadLayout.LeftPageOf(page), p);
                Assert.True(MathF.Abs(f - fraction) <= 0.002f);
            }
        }
        Assert.Equal(186, SpreadLayout.LeftPageOf(187));
        Assert.Equal(186, SpreadLayout.LeftPageOf(186));
    }

    [Fact]
    public void ARowsFractionMeansTheSamePlaceAtAnyWidth()
    {
        var narrow = new SpreadLayout(Enumerable.Repeat(Letter, 50).ToArray(), 800, 16, 12, 12);
        var wide = new SpreadLayout(Enumerable.Repeat(Letter, 50).ToArray(), 2400, 16, 12, 12);
        var at = narrow.PositionAt(narrow.Y(20, 0.4f));
        var back = wide.PositionAt(wide.Y(at.PageIndex, at.Fraction));
        Assert.Equal(20, back.PageIndex);
        Assert.True(MathF.Abs(back.Fraction - 0.4f) <= 0.003f);
    }
}
