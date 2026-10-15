using Reader.Core.Text;

namespace Reader.Core.Flow;

/// <summary>What one unit of flowing text shows.</summary>
public abstract record FlowContent
{
    public sealed record Blocks(IReadOnlyList<Block> Items, bool ChapterStart) : FlowContent;
    /// <summary>A page with no text even after OCR (a figure, a photo): shown as the page image.</summary>
    public sealed record PageImage(int Page) : FlowContent;
}

/// <summary>
/// What the flowing-text view shows, in "units": one unit per PDF page, or one per EPUB block. A
/// position is (page, fraction); <see cref="ToUnit"/>/<see cref="FromUnit"/> convert to and from
/// (unit, fraction within the unit), exactly like the Mac's <c>FlowSource</c> and Android's
/// <c>TrackPosition</c> <c>toItem</c>/<c>fromItem</c>.
/// </summary>
public interface IFlowSource
{
    int UnitCount { get; }
    /// <summary>Null while the unit's text is still being built (shown as a "…" placeholder).</summary>
    FlowContent? Content(int unit);
    (int Unit, float Fraction) ToUnit(int page, float fraction);
    (int Page, float Fraction) FromUnit(int unit, float fraction);
    /// <summary>Makes sure the unit for <paramref name="page"/> has its real content before a restore scrolls to it.</summary>
    Task PrepareAsync(int page);
    /// <summary>The unit's text came from OCR (show the "may contain mistakes" banner).</summary>
    bool IsRecognized(int unit);
    /// <summary>Called on the UI thread with units whose content became available.</summary>
    Action<int[]>? OnUnitsLoaded { get; set; }
    /// <summary>The page or chapter a unit belongs to.</summary>
    int PageOf(int unit);
}

/// <summary>PDF in flowing text: one unit per source page.</summary>
public sealed class PdfFlowSource : IFlowSource
{
    public TextDocument Text { get; }
    public Action<int[]>? OnUnitsLoaded { get; set; }

    public PdfFlowSource(TextDocument text)
    {
        Text = text;
        text.OnPageLoaded = i => OnUnitsLoaded?.Invoke([i]);
    }

    public int UnitCount => Text.PageCount;

    public FlowContent? Content(int unit) => Text.LoadedPage(unit) switch
    {
        null => null,
        { IsEmpty: true } => new FlowContent.PageImage(unit),
        var page => new FlowContent.Blocks(page.Blocks, ChapterStart: false),
    };

    public (int Unit, float Fraction) ToUnit(int page, float fraction) => (page, fraction);

    public (int Page, float Fraction) FromUnit(int unit, float fraction) =>
        Text.LoadedPage(unit) is not null ? (unit, fraction) : (unit, 0);

    public Task PrepareAsync(int page) => Text.PageAsync(Math.Min(Math.Max(0, page), Math.Max(0, Text.PageCount - 1)));

    public bool IsRecognized(int unit) => Text.LoadedPage(unit)?.Recognized == true;

    public int PageOf(int unit) => unit;
}

/// <summary>EPUB: one unit per block; positions are counted per chapter (<see cref="EpubPositions"/>).</summary>
public sealed class EpubFlowSource : IFlowSource
{
    public EpubBook Book { get; }
    private readonly Block[] _blocks;
    private readonly HashSet<int> _chapterStarts;
    private readonly int[] _chapterOf;
    public Action<int[]>? OnUnitsLoaded { get; set; }

    public EpubFlowSource(EpubBook book)
    {
        Book = book;
        _blocks = book.Chapters.SelectMany(c => c).ToArray();
        _chapterStarts = Enumerable.Range(0, book.Chapters.Count).Select(book.Positions.ChapterStart).ToHashSet();
        _chapterOf = book.Chapters.SelectMany((c, i) => Enumerable.Repeat(i, c.Count)).ToArray();
    }

    public int UnitCount => _blocks.Length;

    public FlowContent? Content(int unit) =>
        new FlowContent.Blocks([_blocks[unit]], ChapterStart: unit > 0 && _chapterStarts.Contains(unit));

    public (int Unit, float Fraction) ToUnit(int page, float fraction) => Book.Positions.ToFlat(page, fraction);

    public (int Page, float Fraction) FromUnit(int unit, float fraction) => Book.Positions.FromFlat(unit, fraction);

    public Task PrepareAsync(int page) => Task.CompletedTask;

    public bool IsRecognized(int unit) => false;

    public int PageOf(int unit) => unit >= 0 && unit < _chapterOf.Length ? _chapterOf[unit] : 0;
}
