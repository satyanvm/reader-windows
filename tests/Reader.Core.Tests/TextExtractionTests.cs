using Reader.Core.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using static Reader.Core.Tests.T;

namespace Reader.Core.Tests;

/// <summary>One line of text at a position (y from the top), in a font.</summary>
public sealed record PdfText(string Text, double X, double Y, bool Italic = false, bool Bold = false);

public static class Pdfs
{
    /// <summary>Builds a US-letter PDF; each page is a list of positioned lines (an empty page has no text).</summary>
    public static void Make(IEnumerable<IEnumerable<PdfText>> pages, string path, string? title = null)
    {
        var builder = new PdfDocumentBuilder();
        if (title is not null) builder.DocumentInformation.Title = title;
        var roman = builder.AddStandard14Font(Standard14Font.TimesRoman);
        var italic = builder.AddStandard14Font(Standard14Font.TimesItalic);
        var bold = builder.AddStandard14Font(Standard14Font.TimesBold);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(612, 792);
            foreach (var l in lines)
            {
                page.AddText(l.Text, 12, new PdfPoint(l.X, 792 - l.Y), l.Italic ? italic : l.Bold ? bold : roman);
            }
        }
        File.WriteAllBytes(path, builder.Build());
    }

    /// <summary>A typeset page: running header, indented paragraphs, page number at the foot.</summary>
    public static List<PdfText> BookPage(int n, params string[][] paragraphs)
    {
        var output = new List<PdfText> { new($"{n} THE MEDITATIONS", 200, 60) };
        double y = 90;
        foreach (var p in paragraphs)
        {
            for (var i = 0; i < p.Length; i++)
            {
                output.Add(new PdfText(p[i], i == 0 ? 90 : 72, y));
                y += 15;
            }
        }
        output.Add(new PdfText($"{n}", 300, 740));
        return output;
    }
}

/// <summary>A page renderer that returns a blank white page (OCR is faked in these tests).</summary>
public sealed class BlankRasterizer : IPageRasterizer
{
    public RasterImage? Render(string pdfPath, int pageIndex, int width)
    {
        var height = width * 792 / 612;
        var px = new byte[width * height * 4];
        Array.Fill(px, (byte)255);
        return new RasterImage(width, height, px);
    }
}

/// <summary>Port of the Mac TextExtractionTests (Android's TextExtractionTest, which used PdfBox to build PDFs).</summary>
public class TextExtractionTests
{
    private readonly string _dir = TempDir();

    private TextDocument TextDoc(string path, int pages, IOcr? ocr = null) =>
        new(path, Path.Combine(_dir, "text-test"), pages, ocr, new BlankRasterizer());

    [Fact]
    public void BuildsReflowedPagesWithoutRunningHeadersAndCachesThem() => Run(async () =>
    {
        string[] para1 =
        [
            "Be not uneasy, discouraged, or out of humour, because practice falls short",
            "of precept in some particulars. If you happen to be beaten, come on again,",
            "and be glad.",
        ];
        string[] para2 =
        [
            "Things are so much perplexed and in the dark that several great philosophers",
            "have thought it impossible to arrive at any certainty, and that which you",
        ];
        string[] para2End =
        [
            "call your circumstances is no more than what was appointed you from the",
            "beginning by the highest and most ancient causes.",
        ];
        var page71 = Pdfs.BookPage(71);
        // Continues para2 unindented, then a new paragraph.
        page71.AddRange(para2End.Select((t, i) => new PdfText(t, 72, 90 + 15 * i)));
        page71.Add(new PdfText("Remember this.", 90, 135));
        var path = Path.Combine(_dir, "sample.pdf");
        Pdfs.Make([Pdfs.BookPage(70, para1, para2), page71, Pdfs.BookPage(72, ["Short final page."])], path);

        var text = TextDoc(path, 3);
        var first = await text.PageAsync(0);
        Assert.Equal(2, first.Blocks.Count);
        Assert.StartsWith("Be not uneasy", first.Blocks[0].Text);
        Assert.EndsWith("and be glad.", first.Blocks[0].Text);
        Assert.DoesNotContain(first.Blocks, b => b.Text.Contains("MEDITATIONS")); // running header removed
        // The paragraph split by the page break is joined.
        Assert.EndsWith("that which you call your circumstances is no more than what was " +
            "appointed you from the beginning by the highest and most ancient causes.", first.Blocks[^1].Text);
        var second = await text.PageAsync(1);
        Assert.Equal(["Remember this."], second.Blocks.Select(b => b.Text));
        Assert.Equal(first, text.Loaded[0]);
        text.Close();

        // A fresh instance reads the cache instead of the PDF.
        File.Delete(path);
        Assert.Equal(first, await TextDoc(path, 3).PageAsync(0));
    });

    [Fact]
    public void ItalicFontsBecomeItalicSpans() => Run(async () =>
    {
        var path = Path.Combine(_dir, "italic.pdf");
        Pdfs.Make([[
            new PdfText("You have two reasons to be content.", 90, 90),
            new PdfText("First", 72, 105, Italic: true),
            new PdfText("Secondly, the lot.", 72, 120),
            new PdfText("Strongly", 72, 135, Bold: true),
        ]], path);
        var spans = (await TextDoc(path, 1).PageAsync(0)).Blocks.SelectMany(b => b.Spans).ToList();
        Assert.Contains(spans, s => s.Italic && s.Text.Trim() == "First");
        Assert.Contains(spans, s => !s.Italic && s.Text.Contains("Secondly"));
        Assert.Contains(spans, s => s.Bold && s.Text.Contains("Strongly"));
    });

    private sealed class FakeOcr(List<string> words) : IOcr
    {
        public List<Line> Lines(RasterImage image)
        {
            var w = words[0]; // different text on every page, as in a real book
            words.RemoveAt(0);
            return
            [
                new Line([new Span($"Recognized {w} text from a scanned")], 100, 1500, 200, 30),
                new Line([new Span($"page about {w}.")], 100, 300, 240, 30),
            ];
        }
    }

    private sealed class NoText : IOcr
    {
        public List<Line> Lines(RasterImage image) => new();
    }

    [Fact]
    public void PagesWithNoTextFallBackToOcr() => Run(async () =>
    {
        var path = Path.Combine(_dir, "scan.pdf");
        Pdfs.Make([[], []], path);
        var page = await TextDoc(path, 2, new FakeOcr(["alpha", "beta", "gamma", "delta"])).PageAsync(0);
        Assert.True(page.Recognized);
        Assert.Single(page.Blocks);
        var block = page.Blocks[0].Text;
        Assert.True(block.StartsWith("Recognized ") && block.Contains(" text from a scanned page about "), block);

        Directory.Delete(Path.Combine(_dir, "text-test"), recursive: true);
        Assert.True((await TextDoc(path, 2, new NoText()).PageAsync(1)).IsEmpty); // a figure page stays an image
    });

    [Fact]
    public void PrefetchPublishesEveryPageOnTheMainThread() => Run(async () =>
    {
        var path = Path.Combine(_dir, "prefetch.pdf");
        Pdfs.Make(Enumerable.Range(0, 6).Select(i => (IEnumerable<PdfText>)Pdfs.BookPage(i, [$"Page {i} text."])), path);
        var text = TextDoc(path, 6);
        var seen = new List<int>();
        var mainThread = Environment.CurrentManagedThreadId;
        var done = new TaskCompletionSource();
        text.OnPageLoaded = i =>
        {
            Assert.Equal(mainThread, Environment.CurrentManagedThreadId);
            seen.Add(i);
            if (seen.Count == 6) done.TrySetResult();
        };
        text.Prefetch(3);
        await done.Task;
        Assert.Equal([3, 4, 5, 2, 1, 0], seen); // nearest pages first, forward then back
        Assert.Equal(["Page 4 text."], text.Loaded[4].Blocks.Select(b => b.Text));
    });

    [Fact]
    public void ExtractorGroupsGlyphsIntoLinesWithWordSpaces()
    {
        var path = Path.Combine(_dir, "lines.pdf");
        Pdfs.Make([[new PdfText("Two words here.", 90, 100), new PdfText("Next line", 72, 115)]], path);
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        var lines = PdfLineExtractor.Lines(doc.GetPage(1));
        Assert.Equal(["Two words here.", "Next line"], lines.Select(l => l.Text));
        Assert.True(MathF.Abs(lines[0].X - 90) < 1);
        Assert.True(MathF.Abs(lines[1].Y - lines[0].Y - 15) < 0.5f);
        Assert.True(MathF.Abs(lines[0].FontSize - 12) < 0.5f);
    }
}
