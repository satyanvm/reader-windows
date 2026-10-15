using Reader.Core.Text;
using Xunit;

namespace Reader.Core.Tests;

/// <summary>Builds a page the way a typeset book looks: 12pt body, left margin 72, right margin 540.</summary>
public sealed class PageBuilder
{
    public readonly List<Line> Lines = new();
    private float _y = 72;

    public PageBuilder Line(string text, bool indent = false, bool full = true, float size = 12, float gap = 0,
        bool italic = false, bool bold = false, float? x = null)
    {
        _y += 15 * (size / 12) + gap;
        var startX = x ?? (indent ? 90 : 72);
        Lines.Add(new Line([new Span(text, bold, italic)], startX,
            full ? 540 : startX + text.GraphemeCount() * 5.5f, _y, size));
        return this;
    }

    public PageBuilder Spans(params Span[] spans)
    {
        _y += 15;
        Lines.Add(new Line(spans, 72, 540, _y, 12));
        return this;
    }

    public static List<Line> Page(Action<PageBuilder> build)
    {
        var b = new PageBuilder();
        build(b);
        return b.Lines;
    }
}

public class ReflowTests
{
    private static List<string> Texts(IEnumerable<Block> blocks) => blocks.Select(b => b.Text).ToList();
    private static List<Line> Page(Action<PageBuilder> build) => PageBuilder.Page(build);

    [Fact]
    public void IndentedFirstLinesStartParagraphsWrappedLinesJoin()
    {
        var blocks = Reflow.Blocks(Page(p => p
            .Line("Be not uneasy, discouraged, or out of humour, because practice falls", indent: true)
            .Line("short of precept in some particulars. If you happen to be beaten, come")
            .Line("on again.", full: false)
            .Line("Things are so much perplexed and in the dark that several great", indent: true)
            .Line("philosophers have thought it impossible to arrive at certainty.", full: false)));
        Assert.Equal([
            "Be not uneasy, discouraged, or out of humour, because practice falls short of precept " +
            "in some particulars. If you happen to be beaten, come on again.",
            "Things are so much perplexed and in the dark that several great philosophers have " +
            "thought it impossible to arrive at certainty.",
        ], Texts(blocks));
        Assert.All(blocks, b => Assert.True(b.Indented));
    }

    [Fact]
    public void AFullWidthLineEndingInAFullStopDoesNotEndTheParagraph()
    {
        var blocks = Reflow.Blocks(Page(p => p
            .Line("Depend upon it, this had never been sent you, if the universe had not.", indent: true)
            .Line("Neither does nature act at random, or order anything which is not")
            .Line("suitable.", full: false)));
        Assert.Single(blocks);
    }

    [Fact]
    public void ExtraVerticalSpaceSeparatesUnindentedParagraphs()
    {
        var blocks = Reflow.Blocks(Page(p => p
            .Line("First block paragraph that wraps onto")
            .Line("a second line.", full: false)
            .Line("Second block paragraph.", full: false, gap: 12)));
        Assert.Equal(["First block paragraph that wraps onto a second line.", "Second block paragraph."], Texts(blocks));
    }

    [Fact]
    public void ShortLineEndingASentenceEndsTheParagraphEvenWithoutAnIndent()
    {
        var blocks = Reflow.Blocks(Page(p => p
            .Line("A paragraph whose last line is short and ends a sentence, as a ragged")
            .Line("last line does.", full: false)
            .Line("The next paragraph starts here without indentation or extra spacing")
            .Line("and goes on.", full: false)));
        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void LargerTypeIsAHeading()
    {
        var blocks = Reflow.Blocks(Page(p => p
            .Line("BOOK THE SECOND", full: false, size: 18, x: 200)
            .Line("Begin the morning by saying to thyself, I shall meet with the busy-", indent: true)
            .Line("body, the ungrateful, arrogant, deceitful, envious, unsocial.", full: false)));
        Assert.Equal(BlockKind.Heading, blocks[0].Kind);
        Assert.Equal("BOOK THE SECOND", blocks[0].Text);
        Assert.Equal(BlockKind.Paragraph, blocks[1].Kind);
        // "busy-" + "body": the next line starts lowercase, so the hyphen is a line-break hyphen.
        Assert.Contains("busybody", blocks[1].Text);
    }

    [Fact]
    public void HyphensAreKeptBeforeACapitalOrInsideALine()
    {
        var blocks = Reflow.Blocks(Page(p => p
            .Line("a well-known, long-")
            .Line("Term plan", full: false)));
        Assert.Single(blocks);
        Assert.Equal("a well-known, long- Term plan", blocks[0].Text);
    }

    [Fact]
    public void ItalicsSurviveAndSameStyleRunsMerge()
    {
        var blocks = Reflow.Blocks(Page(p => p
            .Spans(new Span("contented with your condition. "), new Span("First", Italic: true), new Span(", because it has"))
            .Spans(new Span("befallen you. "), new Span("Secondly", Italic: true), new Span(", the lot."))));
        Assert.Single(blocks);
        var spans = blocks[0].Spans;
        Assert.Equal([false, true, false, true, false], spans.Select(s => s.Italic));
        Assert.Equal("First", spans[1].Text);
        Assert.Equal(", because it has befallen you. ", spans[2].Text);
    }

    [Fact]
    public void PageNumbersAtTheTopOrBottomAreDropped()
    {
        var blocks = Reflow.Blocks(Page(p => p
            .Line("72", full: false, x: 300)
            .Line("The text of the page.", full: false)
            .Line("xiv", full: false, x: 300)));
        Assert.Equal(["The text of the page."], Texts(blocks));
        Assert.Empty(Reflow.Blocks(Page(p => p.Line("12", full: false))));
    }

    [Fact]
    public void NoBreakSpacesCollapseLikeOrdinarySpaces()
    {
        var blocks = Reflow.Blocks(Page(p => p.Line("a   \tb", full: false)));
        Assert.Equal("a b", blocks[0].Text);
    }
}

public class PageAssemblyTests
{
    private static List<Line> Page(Action<PageBuilder> build) => PageBuilder.Page(build);

    private static List<Line> RunningPage(int n, string body) => Page(p => p
        .Line(n % 2 == 0 ? $"{n} THE MEDITATIONS" : $"BOOK II {n}", full: false)
        .Line(body, indent: true, full: false)
        .Line($"{n}", full: false, x: 300));

    [Fact]
    public void RunningHeadersRepeatedOnNearbyPagesAreRemoved()
    {
        var pages = Enumerable.Range(70, 5).Select(n => RunningPage(n, $"Body text of page {n}.")).ToList();
        var neighbours = pages.Where((_, i) => i != 2).Select(p => (IReadOnlyList<Line>)p);
        var stripped = PageAssembly.StripRunningLines(pages[2], neighbours);
        Assert.Equal(["Body text of page 72."], Reflow.Blocks(stripped).Select(b => b.Text));
    }

    [Fact]
    public void AUniqueFirstLineIsKept()
    {
        var a = Page(p => p.Line("Chapter One", full: false).Line("Text.", full: false));
        var b = Page(p => p.Line("Something else entirely", full: false).Line("More.", full: false));
        Assert.Equal(2, PageAssembly.StripRunningLines(a, [b]).Count);
    }

    private static Block Para(string text, bool indented = true) => new(BlockKind.Paragraph, [new Span(text)], indented);

    [Fact]
    public void AParagraphSplitByAPageBreakIsMovedWholeOntoTheEarlierPage()
    {
        var p1 = new[] { Para("Start."), Para("Now, if you are displeased with your circumstances, you") };
        var p2 = new[] { Para("injure the whole of which you are a part.", indented: false), Para("Next one.") };

        var first = PageAssembly.Assemble(null, p1, p2);
        Assert.Equal("Now, if you are displeased with your circumstances, you injure the whole of which you are a part.", first[^1].Text);
        var second = PageAssembly.Assemble(p1, p2, null);
        Assert.Equal(["Next one."], second.Select(b => b.Text));
    }

    [Fact]
    public void HyphenatedWordsSplitAcrossPagesAreRejoined()
    {
        var p1 = new[] { Para("A"), Para("the universe had not found its advan-") };
        var p2 = new[] { Para("tage in it.", indented: false), Para("B") };
        Assert.Equal("the universe had not found its advantage in it.", PageAssembly.Assemble(null, p1, p2)[^1].Text);
    }

    [Fact]
    public void APageThatIsAllOneParagraphKeepsItsTextAndIsMarkedAsAContinuation()
    {
        var p1 = new[] { Para("A"), Para("it goes on and") };
        var p2 = new[] { Para("on for the whole page without stopping", indented: false) };
        Assert.Equal("it goes on and", PageAssembly.Assemble(null, p1, p2)[^1].Text);
        var second = PageAssembly.Assemble(p1, p2, null);
        Assert.True(second.Count == 1 && second[0].Continuation);
    }

    [Fact]
    public void AnIndentedParagraphAtTheTopOfAPageIsANewParagraph()
    {
        var p1 = new[] { Para("A"), Para("ends a sentence.") };
        var p2 = new[] { Para("New paragraph."), Para("B") };
        Assert.Equal("ends a sentence.", PageAssembly.Assemble(null, p1, p2)[^1].Text);
        Assert.Equal(2, PageAssembly.Assemble(p1, p2, null).Count);
    }

    [Fact]
    public void InBooksWithoutIndentsANewSentenceAfterAFullStopIsANewParagraph()
    {
        var p1 = new[] { Para("A", indented: false), Para("ends a sentence.", indented: false) };
        var p2 = new[] { Para("Starts fresh.", indented: false), Para("B", indented: false) };
        Assert.False(Reflow.Continues(p1[^1], p2[0], nextPageUsesIndents: false));
        var p3 = new[] { Para("but this continues.", indented: false), Para("C", indented: false) };
        Assert.True(Reflow.Continues(p1[^1], p3[0], nextPageUsesIndents: false));
    }
}

public class PageTextCacheTests
{
    [Fact]
    public void JsonRoundTripsAndMatchesTheAndroidCacheShape()
    {
        var page = new PageText([
            new Block(BlockKind.Heading, [new Span("One", Bold: true)]),
            new Block(BlockKind.Paragraph, [new Span("a "), new Span("b", Italic: true)], indented: true, continuation: true),
            new Block(BlockKind.Image, image: "OEBPS/x.png"),
        ], Recognized: true);
        var decoded = PageText.FromJson(page.Json());
        Assert.Equal(page, decoded);
        // Android writes the same keys: k, i, c, img, s[t, b, it], recognized.
        const string android = """{"recognized":false,"blocks":[{"k":"PARAGRAPH","i":true,"c":false,"img":null,"s":[{"t":"Hi","b":false,"it":true}]}]}""";
        var parsed = PageText.FromJson(System.Text.Encoding.UTF8.GetBytes(android));
        Assert.Equal(new PageText([new Block(BlockKind.Paragraph, [new Span("Hi", Italic: true)], indented: true)]), parsed);
    }
}

/// <summary>
/// Pages laid out like the two-column book the reflow once got wrong: 12pt body in columns at
/// x=72 and x=316.8, 15.9pt leading, a 9.96pt footnote at the foot of the right column.
/// </summary>
public class ColumnAndFootnoteTests
{
    private static Line L(string text, float x, float y, float endX = 0, float size = 12, bool italic = false) =>
        new([new Span(text, Italic: italic)], x, endX > 0 ? endX : x + (size > 11 ? 210 : 190), y, size);

    private static List<Line> TwoColumnPage()
    {
        var l = new List<Line> { L("105", 294.8f, 757, 317.2f) }; // folio, extracted first
        // Left column: a paragraph ending, then a new indented one running to the column foot.
        l.Add(L("roots of plants and trees appeared through a", 72, 182));
        l.Add(L("inward flow of their sap.", 72, 198, 190));
        l.Add(L("The whole vicinity lay bare before me.", 93.6f, 220, 280));
        l.Add(L("My ordinary frontal vision was now changed", 72, 236));
        l.Add(L("to a vast spherical sight, simultaneously all-", 72, 252));
        // Right column: continues the paragraph, then a new one that runs onto the next page.
        l.Add(L("perceptive. Through the back of my head I", 316.8f, 182));
        l.Add(L("saw men strolling far down the road.", 316.8f, 198, 470));
        l.Add(L("resounding as Aum,", 338.4f, 220, 411));
        l.Add(L("* the vibration of the", 411.1f, 220, 511.7f)); // same row, split by the extractor
        l.Add(L("Cosmic Motor, which I had long passionately", 316.8f, 236));
        l.Add(L("*“In the beginning was the Word, and the Word was", 316.8f, 263, size: 9.96f));
        l.Add(L("with God, and the Word was God.”—John 1:1.", 316.8f, 275, size: 9.96f));
        return l;
    }

    [Fact]
    public void ColumnsAreReadInOrderAndFootnotesGoLast()
    {
        var blocks = Reflow.Blocks(TwoColumnPage());
        Assert.Equal([BlockKind.Paragraph, BlockKind.Paragraph, BlockKind.Paragraph, BlockKind.Note], blocks.Select(b => b.Kind));
        Assert.Equal("roots of plants and trees appeared through a inward flow of their sap.", blocks[0].Text);
        // The paragraph runs on from the left column into the right one (a line-end hyphen before
        // a lowercase word is dropped, as on Android).
        Assert.Equal("The whole vicinity lay bare before me. My ordinary frontal vision was now changed " +
            "to a vast spherical sight, simultaneously allperceptive. Through the back of my head I saw men strolling far down the road.",
            blocks[1].Text);
        Assert.True(blocks[1].Indented);
        // An indented line in the right column starts a paragraph; a split row is one line.
        Assert.Equal("resounding as Aum,* the vibration of the Cosmic Motor, which I had long passionately", blocks[2].Text);
        Assert.Equal("*“In the beginning was the Word, and the Word was with God, and the Word was God.”—John 1:1.", blocks[3].Text);
    }

    [Fact]
    public void AParagraphContinuesOntoTheNextPagePastTheFootnote()
    {
        var next = new List<Line>
        {
            L("sought. He lifted me and said quietly,", 72, 87),
            L("You must not get overdrunk with ecstasy.", 93.6f, 103),
            L("Much work yet remains for you in the world.", 72, 119),
        };
        var page = Reflow.Blocks(TwoColumnPage());
        var nextBlocks = Reflow.Blocks(next);
        var assembled = PageAssembly.Assemble(null, page, nextBlocks);
        Assert.EndsWith("which I had long passionately sought. He lifted me and said quietly,", assembled[2].Text);
        Assert.Equal(BlockKind.Note, assembled[^1].Kind); // the footnote stays at the end
        Assert.Equal(["You must not get overdrunk with ecstasy. Much work yet remains for you in the world."],
            PageAssembly.Assemble(page, nextBlocks, null).Select(b => b.Text));
    }

    [Fact]
    public void SingleColumnPagesAreUnchanged()
    {
        var lines = PageBuilder.Page(p => p
            .Line("A paragraph whose last line is short and ends a sentence, as a ragged")
            .Line("last line does.", full: false)
            .Line("The next paragraph starts here without indentation or extra spacing")
            .Line("and goes on.", full: false));
        Assert.Single(Reflow.Columns(lines, 12));
        Assert.Equal(2, Reflow.Blocks(lines).Count);
    }

    [Fact]
    public void SmallTypeWithoutAMarkerOrGapIsNotAFootnote()
    {
        var lines = new List<Line>
        {
            L("Body text that runs along the full width of", 72, 100),
            L("the column and keeps going down the page", 72, 116),
            L("in slightly smaller type for a quotation", 72, 130, size: 10),
        };
        Assert.All(Reflow.Blocks(lines), b => Assert.NotEqual(BlockKind.Note, b.Kind));
    }
}
