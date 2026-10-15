using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Reader.Core.Reader;

namespace Reader.Core.Text;

/// <summary>
/// An EPUB, read into flowing blocks. EPUBs already carry structure (paragraphs, headings,
/// italics, images), so unlike a PDF nothing has to be inferred.
///
/// Positions use chapter = page: <c>pageIndex</c> is the spine item and <c>pageOffset</c> is how
/// far through that chapter's blocks the reader is (see <see cref="EpubPositions"/>). Chapter and
/// block counting match the Android and Mac apps, so the same EPUB syncs to the same place.
/// </summary>
public sealed class EpubBook : IReaderDocument
{
    private readonly ZipArchive _zip;
    private readonly object _zipLock = new();
    public string? Title { get; }
    public IReadOnlyList<IReadOnlyList<Block>> Chapters { get; }
    public EpubPositions Positions { get; }

    public int PageCount => Chapters.Count;

    private EpubBook(ZipArchive zip, string? title, IReadOnlyList<IReadOnlyList<Block>> chapters)
    {
        _zip = zip;
        Title = title;
        Chapters = chapters;
        Positions = new EpubPositions(chapters.Select(c => c.Count).ToArray());
    }

    /// <summary>Raw bytes of an image referenced by an image block, or null if missing. Thread-safe.</summary>
    public byte[]? ImageBytes(string path)
    {
        lock (_zipLock) return Read(_zip, path);
    }

    public void Close()
    {
        lock (_zipLock) _zip.Dispose();
    }

    private static byte[]? Read(ZipArchive zip, string name)
    {
        try
        {
            var entry = zip.GetEntry(name);
            if (entry is null) return null;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException or ObjectDisposedException)
        {
            return null;
        }
    }

    public static bool IsEpub(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (Read(zip, "mimetype") is not { } m) return zip.GetEntry("META-INF/container.xml") is not null;
            return Markup.DecodeUtf8(m).Trim() == "application/epub+zip";
        }
        catch
        {
            return false;
        }
    }

    public static EpubBook Open(string path)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(path); }
        catch (Exception e) when (e is InvalidDataException or IOException) { throw new ReaderException("Not an EPUB: " + e.Message); }
        try
        {
            if (Read(zip, "META-INF/container.xml") is not { } container) throw new ReaderException("Not an EPUB: no container.xml");
            var opfPath = Markup.Parse(container, html: false)?.First("rootfile")?.Attr("full-path");
            if (string.IsNullOrEmpty(opfPath)) throw new ReaderException("Not an EPUB: no rootfile");
            if (Read(zip, opfPath) is not { } opfData || Markup.Parse(opfData, html: false) is not { } opf)
            {
                throw new ReaderException($"Missing {opfPath}");
            }
            var manifest = new Dictionary<string, Markup.Element>();
            foreach (var item in opf.First("manifest")?.ChildrenNamed("item") ?? new()) manifest.TryAdd(item.Attr("id"), item);
            var title = opf.First("metadata")?.ChildrenNamed("title").FirstOrDefault()?.Text.Trim();
            var chapters = (opf.First("spine")?.ChildrenNamed("itemref") ?? new())
                .Where(r => r.Attr("linear") != "no")
                .Select(r => manifest.GetValueOrDefault(r.Attr("idref")))
                .Where(m => m is not null && m.Attr("media-type").Contains("html"))
                .Select(m => Resolve(opfPath, m!.Attr("href")))
                .Select(p => Read(zip, p) is { } data ? XhtmlBlocks.Parse(data, p) : null)
                .Where(c => c is not null && c.Count > 0)
                .Select(c => (IReadOnlyList<Block>)c!)
                .ToList();
            if (chapters.Count == 0) throw new ReaderException("This EPUB has no readable text");
            return new EpubBook(zip, string.IsNullOrEmpty(title) ? null : title, chapters);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>Resolves <paramref name="href"/> relative to the file at <paramref name="base"/>, both paths inside the zip.</summary>
    public static string Resolve(string @base, string href)
    {
        var hash = href.IndexOf('#');
        var raw = hash >= 0 ? href[..hash] : href;
        var clean = RemovingPercentEncoding(raw) ?? raw;
        if (clean.Length == 0) return @base;
        var parts = new List<string>();
        var slash = @base.LastIndexOf('/');
        if (!clean.StartsWith('/') && slash >= 0)
        {
            parts = @base[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        foreach (var segment in clean.Split('/'))
        {
            switch (segment)
            {
                case "" or ".": continue;
                case "..": if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); break;
                default: parts.Add(segment); break;
            }
        }
        return string.Join('/', parts);
    }

    /// <summary>Foundation's <c>removingPercentEncoding</c>: null for a malformed escape or invalid UTF-8; '+' stays literal.</summary>
    internal static string? RemovingPercentEncoding(string s)
    {
        if (!s.Contains('%')) return s;
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '%')
            {
                if (i + 2 >= s.Length || !Uri.IsHexDigit(s[i + 1]) || !Uri.IsHexDigit(s[i + 2])) return null;
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}

/// <summary>Converts an XHTML chapter into blocks. Same rules as the Android and Mac <c>XhtmlBlocks</c>.</summary>
public static class XhtmlBlocks
{
    internal static readonly HashSet<string> BlockTags =
    [
        "p", "div", "section", "article", "blockquote", "li", "ul", "ol", "dd", "dt", "dl",
        "pre", "figure", "figcaption", "table", "tr", "td", "th", "header", "footer", "aside",
        "h1", "h2", "h3", "h4", "h5", "h6", "hr", "body", "nav",
    ];
    internal static readonly HashSet<string> HeadingTags = ["h1", "h2", "h3", "h4", "h5", "h6"];
    internal static readonly HashSet<string> ItalicTags = ["em", "i", "cite", "var", "dfn"];
    internal static readonly HashSet<string> BoldTags = ["strong", "b"];
    internal static readonly HashSet<string> SkipTags = ["script", "style", "head", "title"];

    public static List<Block> Parse(byte[] data, string path)
    {
        if (Markup.Parse(data, html: true) is not { } root) return new();
        var body = root.First("body") ?? root;
        var walker = new Walker(path);
        walker.Walk(body, bold: false, italic: false, pre: false);
        walker.Flush();
        return walker.Output;
    }

    private static readonly Regex AroundBreak = new(" ?\n ?", RegexOptions.CultureInvariant);

    private sealed class Walker(string path)
    {
        public readonly List<Block> Output = new();
        private readonly List<Span> _spans = new();
        private bool _heading;

        public void Walk(Markup.Node node, bool bold, bool italic, bool pre)
        {
            switch (node)
            {
                case Markup.TextNode t:
                    // Java's \s (no-break spaces are kept).
                    var text = pre ? t.Text : t.Text.Collapsing(StringExtensions.JavaWhitespace, " ");
                    if (text.Length > 0) _spans.Add(new Span(text, bold, italic));
                    break;
                case Markup.Element el:
                    Element(el, bold, italic, pre);
                    break;
            }
        }

        private void Element(Markup.Element el, bool bold, bool italic, bool pre)
        {
            var tag = el.Name;
            if (SkipTags.Contains(tag)) return;
            switch (tag)
            {
                case "br":
                    _spans.Add(new Span("\n", bold, italic));
                    return;
                case "img" or "image":
                    var src = new[] { el.Attr("src"), el.Attr("xlink:href"), el.Attr("href") }.FirstOrDefault(v => v.Length > 0) ?? "";
                    if (src.Length > 0 && !src.StartsWith("data:"))
                    {
                        Flush();
                        Output.Add(new Block(BlockKind.Image, image: EpubBook.Resolve(path, src)));
                    }
                    return;
            }
            var isBlock = BlockTags.Contains(tag);
            if (isBlock) Flush();
            if (HeadingTags.Contains(tag)) _heading = true;
            var b = bold || BoldTags.Contains(tag);
            var i = italic || ItalicTags.Contains(tag);
            foreach (var child in el.Children) Walk(child, b, i, pre || tag == "pre");
            if (isBlock) Flush();
        }

        public void Flush()
        {
            var compact = _spans.Compact();
            _spans.Clear();
            var wasHeading = _heading;
            _heading = false;
            if (compact.Count == 0) return;
            // Trim the block's outer whitespace and the space around line breaks.
            var trimmed = compact.Select((s, i) =>
            {
                var t = AroundBreak.Replace(s.Text, "\n");
                if (i == 0) t = t.TrimLeadingWhitespace();
                if (i == compact.Count - 1) t = t.TrimTrailingWhitespace();
                return s with { Text = t };
            }).Compact();
            if (trimmed.Count == 0 || trimmed.All(s => s.Text.IsBlank())) return;
            Output.Add(new Block(wasHeading ? BlockKind.Heading : BlockKind.Paragraph, trimmed));
        }
    }
}

/// <summary>
/// Maps an EPUB position (chapter, fraction through the chapter) to a block in the flat list the
/// reader shows, and back. Fractions are counted in blocks, so they don't depend on window size,
/// text size or theme. Float math identical to Android and the Mac.
/// </summary>
public sealed class EpubPositions
{
    private readonly int[] _blockCounts;
    private readonly int[] _starts;
    public int TotalBlocks { get; }

    public EpubPositions(IReadOnlyList<int> blockCounts)
    {
        _blockCounts = blockCounts.ToArray();
        _starts = new int[_blockCounts.Length];
        var acc = 0;
        for (var i = 0; i < _blockCounts.Length; i++)
        {
            _starts[i] = acc;
            acc += _blockCounts[i];
        }
        TotalBlocks = acc;
    }

    public int ChapterStart(int chapter) => _starts[Math.Clamp(chapter, 0, _starts.Length - 1)];

    /// <summary>(flat block index, fraction within that block) for a saved position.</summary>
    public (int Flat, float Within) ToFlat(int chapter, float fraction)
    {
        var c = Math.Clamp(chapter, 0, _blockCounts.Length - 1);
        var n = _blockCounts[c];
        var x = Model.ReadingPosition.Clamp(fraction, 0, 1) * (float)n;
        var block = Math.Min((int)x, n - 1);
        return (_starts[c] + block, Model.ReadingPosition.Clamp(x - (float)block, 0, 1));
    }

    /// <summary>(chapter, fraction through it) for the block at <paramref name="flatIndex"/>, <paramref name="withinBlock"/> of the way down.</summary>
    public (int Chapter, float Fraction) FromFlat(int flatIndex, float withinBlock)
    {
        var i = Math.Clamp(flatIndex, 0, Math.Max(0, TotalBlocks - 1));
        var last = -1;
        for (var k = 0; k < _starts.Length; k++) if (_starts[k] <= i) last = k;
        var c = Math.Max(0, last);
        while (c > 0 && _blockCounts[c] == 0) c -= 1;
        var n = Math.Max(1, _blockCounts[c]);
        return (c, Model.ReadingPosition.Clamp(((float)(i - _starts[c]) + Model.ReadingPosition.Clamp(withinBlock, 0, 1)) / (float)n, 0, 1));
    }
}
