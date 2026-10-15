using System.IO.Compression;
using System.Text;
using Reader.Core.Text;
using Xunit;
using static Reader.Core.Tests.T;

namespace Reader.Core.Tests;

public static class Zips
{
    /// <summary>Writes a ZIP in tests: entries stored or deflated (mimetype always stored, as the EPUB spec asks).</summary>
    public static void Write(IEnumerable<(string Name, byte[] Body)> files, string path, bool deflate)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, body) in files)
        {
            var stored = name == "mimetype" || !deflate;
            var entry = zip.CreateEntry(name, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var s = entry.Open();
            s.Write(body);
        }
    }
}

/// <summary>Port of the Mac EpubTests (Android's EpubTest).</summary>
public class EpubTests
{
    public static readonly (string, string)[] Book =
    [
        ("mimetype", "application/epub+zip"),
        ("META-INF/container.xml", """
            <?xml version="1.0"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """),
        ("OEBPS/content.opf", """
            <?xml version="1.0"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>The Meditations</dc:title></metadata>
              <manifest>
                <item id="c1" href="text/one.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="text/two%20b.xhtml" media-type="application/xhtml+xml"/>
                <item id="css" href="style.css" media-type="text/css"/>
                <item id="cover" href="text/cover.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine>
                <itemref idref="cover" linear="no"/>
                <itemref idref="c1"/>
                <itemref idref="c2"/>
              </spine>
            </package>
            """),
        ("OEBPS/text/cover.xhtml", "<html><body><p>Cover</p></body></html>"),
        ("OEBPS/text/one.xhtml", """
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>x</title><style>p{}</style></head>
            <body>
              <h1>Book   One</h1>
              <p>You have two reasons, therefore, to be
                 contented with your condition. <em>First</em>, because it has befallen you.</p>
              <p><i>Secondly</i>, the lot even of <b>individuals</b> is destined&#160;for the whole.</p>
              <div><img src="../images/fig%201.png" alt=""/></div>
            </body></html>
            """),
        ("OEBPS/text/two b.xhtml", "<html><body><p>Line one<br/>Line two</p><p>  </p></body></html>"),
        ("OEBPS/images/fig 1.png", "PNG"),
    ];

    public static string Epub(IEnumerable<(string, string)> files, bool deflate = true)
    {
        var path = Path.Combine(TempDir(), "book.epub");
        Zips.Write(files.Select(f => (f.Item1, Encoding.UTF8.GetBytes(f.Item2))), path, deflate);
        return path;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsChaptersInSpineOrderWithHeadingsItalicsAndImages(bool deflate)
    {
        var b = EpubBook.Open(Epub(Book, deflate));
        Assert.Equal("The Meditations", b.Title);
        Assert.Equal(2, b.PageCount); // non-linear cover is skipped

        var one = b.Chapters[0];
        Assert.Equal(BlockKind.Heading, one[0].Kind);
        Assert.Equal("Book One", one[0].Text);
        Assert.Equal("You have two reasons, therefore, to be contented with your condition. First, because it has befallen you.", one[1].Text);
        Assert.Equal([false, true, false], one[1].Spans.Select(s => s.Italic));
        Assert.True(one[2].Spans[0].Italic);
        Assert.True(one[2].Spans.First(s => s.Text == "individuals").Bold);
        Assert.Contains("destined for", one[2].Text); // a no-break space is kept, as Jsoup does
        Assert.Equal(BlockKind.Image, one[3].Kind);
        Assert.Equal("OEBPS/images/fig 1.png", one[3].Image);
        Assert.Equal("PNG", Encoding.UTF8.GetString(b.ImageBytes(one[3].Image!)!));
        Assert.Equal(4, one.Count);

        Assert.Equal(["Line one\nLine two"], b.Chapters[1].Select(x => x.Text));
        b.Close();
    }

    [Fact]
    public void ToleratesHtmlThatIsNotWellFormedXml()
    {
        var files = Book.ToArray();
        files[5] = ("OEBPS/text/two b.xhtml", "<html><body><p>Caf&eacute; &nbsp;open<br>late<p>Second</body></html>");
        var b = EpubBook.Open(Epub(files));
        Assert.Equal(["Café  open\nlate", "Second"], b.Chapters[1].Select(x => x.Text));
    }

    [Fact]
    public void DetectsEpubs()
    {
        Assert.True(EpubBook.IsEpub(Epub(Book)));
        var pdf = Path.Combine(TempDir(), "x.pdf");
        File.WriteAllText(pdf, "%PDF-1.7");
        Assert.False(EpubBook.IsEpub(pdf));
    }

    [Fact]
    public void RelativePathsResolveInsideTheZip()
    {
        Assert.Equal("OEBPS/images/a.png", EpubBook.Resolve("OEBPS/text/one.xhtml", "../images/a.png"));
        Assert.Equal("OEBPS/text/two.xhtml", EpubBook.Resolve("OEBPS/content.opf", "text/two.xhtml#c2"));
        Assert.Equal("a b.png", EpubBook.Resolve("x.xhtml", "a%20b.png"));
        Assert.Equal("root.png", EpubBook.Resolve("OEBPS/x.xhtml", "/root.png"));
        Assert.Equal("OEBPS/a+b.png", EpubBook.Resolve("OEBPS/x.xhtml", "a+b.png")); // + is literal, not a space
    }

    [Fact]
    public void PositionsRoundTripThroughTheFlatBlockList()
    {
        var p = new EpubPositions([4, 10, 2]);
        Assert.Equal(16, p.TotalBlocks);
        Assert.Equal((4 + 5, 0f), p.ToFlat(1, 0.5f));
        var (flat, within) = p.ToFlat(1, 0.53f);
        var (chapter, fraction) = p.FromFlat(flat, within);
        Assert.Equal(1, chapter);
        Assert.True(MathF.Abs(fraction - 0.53f) <= 0.0001f);
        Assert.Equal((2, 1f), p.FromFlat(15, 1));
        Assert.Equal((0, 0f), p.FromFlat(0, 0));
        Assert.Equal(3, p.ToFlat(0, 1).Flat); // fraction 1 lands in the last block
    }

    /// <summary>
    /// Same float arithmetic as Kotlin and Swift: these are the exact values the Android and Mac
    /// implementations produce, so a position saved on one device maps to the same block on another.
    /// </summary>
    [Fact]
    public void PositionMathIsBitIdenticalToAndroid()
    {
        var counts = new[] { 3, 7, 0, 5 };
        var starts = new[] { 0, 3, 10, 10 };
        var p = new EpubPositions(counts);
        for (var chapter = 0; chapter < 4; chapter++)
        {
            if (chapter == 2) continue;
            for (var step = 0; step <= 100; step++)
            {
                var f = (float)step / 100;
                var (flat, within) = p.ToFlat(chapter, f);
                var n = counts[chapter];
                var x = f * (float)n;
                var block = Math.Min((int)x, n - 1);
                Assert.Equal(starts[chapter] + block, flat);
                Assert.Equal(BitConverter.SingleToInt32Bits(MathF.Min(MathF.Max(x - (float)block, 0), 1)), BitConverter.SingleToInt32Bits(within));
                var back = p.FromFlat(flat, within);
                Assert.Equal(chapter, back.Chapter);
                Assert.True(MathF.Abs(back.Fraction - f) <= 1e-6f);
            }
        }
    }
}

public class LenientHtmlTests
{
    private static List<string> Blocks(string html) =>
        XhtmlBlocks.Parse(Encoding.UTF8.GetBytes(html), "x.xhtml").Select(b => b.Text).ToList();

    [Fact]
    public void ImplicitEndTagsAndVoidElements() =>
        Assert.Equal(["One", "Two", "A", "B", "C"], Blocks("<ul><li>One<li>Two</ul><p>A<p>B<hr>C"));

    [Fact]
    public void ScriptsStylesAndCommentsAreSkipped() =>
        Assert.Equal(["Kept & ☺ &bogus;"],
            Blocks("<body><script>if (a < b) { x() }</script><!-- note --><p>Kept &amp; &#x263A; &bogus;</p><style>p{}</style></body>"));

    [Fact]
    public void AttributesInAnyQuotingStyle()
    {
        var b = XhtmlBlocks.Parse(Encoding.UTF8.GetBytes("<p>x<img alt=a src=../i/a.png><img src='b.png'/></p>"), "t/c.xhtml");
        Assert.Equal(["i/a.png", "t/b.png"], b.Where(x => x.Image is not null).Select(x => x.Image));
    }
}
