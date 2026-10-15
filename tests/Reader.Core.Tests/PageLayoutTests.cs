using Reader.Core.Reader;
using Xunit;
using static Reader.Core.Tests.T;

namespace Reader.Core.Tests;

public class PageLayoutTests
{
    private readonly PageSize[] _letter = Enumerable.Repeat(new PageSize(612, 792), PAGES).ToArray();

    [Fact]
    public void SameFractionMapsToProportionalPointsOnAnyWindowAndZoom()
    {
        foreach (var viewport in new[] { 720, 1080, 1440, 2560 })
        {
            foreach (var zoom in new[] { 1f, 1.5f, 2f, 4f })
            {
                var layout = new PageLayout(_letter, PageLayout.ContentWidthFor(viewport, zoom), 16);
                var t = layout.ScrollTargetFor(186, 0.5f);
                Assert.Equal(186, t.Index);
                Assert.True(MathF.Abs(layout.Fraction(t.Index, t.Offset) - 0.5f) <= 0.002f);
            }
        }
    }

    [Fact]
    public void APositionFromANarrowWindowRestoresToTheSameSpotInAWideOne()
    {
        var narrow = new PageLayout(_letter, 1080, 16);
        var wide = new PageLayout(_letter, 2560, 16);
        var fraction = narrow.Fraction(186, 700);
        var onWide = wide.ScrollTargetFor(186, fraction);
        Assert.True(Math.Abs(700.0 / narrow.PageHeights[186] - (double)onWide.Offset / wide.PageHeights[186]) <= 0.002);
    }

    [Fact]
    public void OutOfRangeTargetsAreClamped()
    {
        var layout = new PageLayout(_letter, 1080, 16);
        Assert.Equal(PAGES - 1, layout.ScrollTargetFor(10_000, 0.5f).Index);
        Assert.Equal(0, layout.ScrollTargetFor(-3, 2).Index);
        Assert.Equal(1f, layout.Fraction(5, 99_999)); // inside the gap below the page
    }

    [Fact]
    public void MixedPageSizesUseEachPagesOwnHeight()
    {
        var pages = new[] { new PageSize(612, 792), new PageSize(792, 612), new PageSize(612, 1584) };
        var layout = new PageLayout(pages, 1000, 0);
        Assert.Equal(1294, layout.PageHeights[0]);
        Assert.Equal(772, layout.PageHeights[1]);
        Assert.Equal(1294, layout.ScrollTargetFor(2, 0.5f).Offset);
    }

    [Fact]
    public void DocumentYAndSlotLookupRoundTrip()
    {
        var layout = new PageLayout(_letter, 800, 10, topInset: 10);
        foreach (var page in new[] { 0, 1, 186, PAGES - 1 })
        {
            foreach (var fraction in new[] { 0f, 0.25f, 0.5f, 0.999f })
            {
                var y = layout.Y(page, fraction);
                var slot = layout.Slot(y);
                Assert.Equal(page, slot.Index);
                Assert.True(MathF.Abs(layout.Fraction(slot.Index, slot.Offset) - fraction) <= 0.002f);
            }
        }
        Assert.Equal(0, layout.Slot(0).Index); // above the first page
    }
}
