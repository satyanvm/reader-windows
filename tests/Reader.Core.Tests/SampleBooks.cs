using System.Text;
using Xunit;

namespace Reader.Core.Tests;

/// <summary>Runs only when READER_SAMPLE_OUT names a folder: writes sample books for the self-test (CI uses it).</summary>
public sealed class SampleFactAttribute : FactAttribute
{
    public SampleFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("READER_SAMPLE_OUT"))) Skip = "set READER_SAMPLE_OUT to write sample books";
    }
}

public class SampleBooks
{
    private static readonly string[] Sentences =
    [
        "Begin the morning by saying to thyself, I shall meet with the busybody, the ungrateful, arrogant,",
        "deceitful, envious, unsocial. All these things happen to them by reason of their ignorance of",
        "what is good and evil. But I who have seen the nature of the good that it is beautiful, and of",
        "the bad that it is ugly, and the nature of him who does wrong, that it is akin to me, not only",
        "of the same blood or seed, but that it participates in the same intelligence and the same portion",
        "of the divinity, I can neither be injured by any of them, for no one can fix on me what is ugly.",
    ];

    [SampleFact]
    public void WriteSampleBooks()
    {
        var dir = Environment.GetEnvironmentVariable("READER_SAMPLE_OUT")!;
        Directory.CreateDirectory(dir);

        // A 60-page typeset PDF: running headers, indented paragraphs, page numbers.
        var pages = Enumerable.Range(1, 60).Select(n =>
            (IEnumerable<PdfText>)Pdfs.BookPage(n,
                Sentences.Select((s, i) => i == 0 ? $"{n}. {s}" : s).ToArray(),
                Sentences.Reverse().ToArray(),
                Sentences.Take(4).ToArray()));
        Pdfs.Make(pages, Path.Combine(dir, "sample.pdf"), title: "The Sample Meditations");

        // A long EPUB: 12 chapters of 60 paragraphs, with headings, italics and bold.
        var files = new List<(string, string)>
        {
            ("mimetype", "application/epub+zip"),
            ("META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>"""),
        };
        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        for (var c = 1; c <= 12; c++)
        {
            var body = new StringBuilder($"<h1>Book {c}</h1>");
            for (var p = 1; p <= 60; p++)
            {
                body.Append($"<p>{p}. {string.Join(" ", Sentences.Skip(p % 3))} <em>So it is</em> with <b>everything</b>.</p>");
            }
            files.Add(($"OEBPS/c{c}.xhtml", $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Book {c}</title></head><body>{body}</body></html>"));
            manifest.Append($"<item id=\"c{c}\" href=\"c{c}.xhtml\" media-type=\"application/xhtml+xml\"/>");
            spine.Append($"<itemref idref=\"c{c}\"/>");
        }
        files.Add(("OEBPS/content.opf",
            $"<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>The Sample Epub</dc:title></metadata><manifest>{manifest}</manifest><spine>{spine}</spine></package>"));
        Zips.Write(files.Select(f => (f.Item1, Encoding.UTF8.GetBytes(f.Item2))), Path.Combine(dir, "sample.epub"), deflate: true);

        Assert.True(File.Exists(Path.Combine(dir, "sample.pdf")));
        Assert.True(File.Exists(Path.Combine(dir, "sample.epub")));
    }
}
