using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;
using Reader.Core.App;
using Reader.Core.Flow;
using Reader.Core.Text;
using Reader.Core.Text.Hyphenation;

namespace Reader.App.Reading;

/// <summary>
/// Play Books-style typography for flowing text: serif body text at 18 × the text scale, line
/// height × 1.3, justified with hyphenation, a 1.6 em first-line indent (none on a paragraph
/// continued from the previous page); headings 1.25 ×, semibold and centred with space around
/// them; notes 0.8 × in the muted colour; extra space above a chapter's start.
/// </summary>
internal sealed class FlowStyle
{
    public float Scale { get; }
    public ReaderTheme Theme { get; }
    public double BodySize => 18 * Scale;
    public Brush Text { get; }
    public Brush Muted { get; }
    public FontFamily Family { get; } = new("Georgia, Cambria, Times New Roman");
    public CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("en-US");
    public double PixelsPerDip { get; }

    public FlowStyle(float scale, ReaderTheme theme, double pixelsPerDip)
    {
        Scale = scale;
        Theme = theme;
        PixelsPerDip = pixelsPerDip;
        var c = ThemeColors.For(theme);
        Text = Frozen(ThemeManager.ToColor(c.Text));
        Muted = Frozen(ThemeManager.ToColor(c.Muted));
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public double PlaceholderHeight => 320 * Scale;
    public const double ChapterSpace = 48;
}

/// <summary>The text of one block as shown: soft hyphens added, with a map back to the original characters.</summary>
internal sealed class BlockText
{
    public required Block Block { get; init; }
    public required string Display { get; init; }
    /// <summary>Original character i is at <c>Map[i]</c> in <see cref="Display"/>.</summary>
    public required int[] Map { get; init; }
    /// <summary>For each span, its start in <see cref="Display"/>.</summary>
    public required int[] SpanStarts { get; init; }

    public static BlockText Of(Block block, bool hyphenate)
    {
        var plain = block.Text;
        var display = plain;
        int[] map;
        if (hyphenate) display = Hyphenator.EnglishUS.InsertSoftHyphens(plain, out map);
        else map = Enumerable.Range(0, plain.Length + 1).ToArray();
        var starts = new int[block.Spans.Count];
        var at = 0;
        for (var i = 0; i < block.Spans.Count; i++)
        {
            starts[i] = map[at];
            at += block.Spans[i].Text.Length;
        }
        return new BlockText { Block = block, Display = display, Map = map, SpanStarts = starts };
    }
}

internal sealed class RunProps(Typeface typeface, double size, Brush foreground, CultureInfo culture) : TextRunProperties
{
    public override Typeface Typeface { get; } = typeface;
    public override double FontRenderingEmSize { get; } = size;
    public override double FontHintingEmSize { get; } = size;
    public override TextDecorationCollection? TextDecorations => null;
    public override Brush ForegroundBrush { get; } = foreground;
    public override Brush? BackgroundBrush => null;
    public override CultureInfo CultureInfo { get; } = culture;
    public override TextEffectCollection? TextEffects => null;
}

internal sealed class ParagraphProps(TextRunProperties defaults, TextAlignment alignment, double lineHeight, double indent, bool first)
    : TextParagraphProperties
{
    public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
    public override TextAlignment TextAlignment { get; } = alignment;
    public override double LineHeight { get; } = lineHeight;
    public override bool FirstLineInParagraph { get; } = first;
    public override TextRunProperties DefaultTextRunProperties { get; } = defaults;
    public override TextWrapping TextWrapping => TextWrapping.Wrap;
    public override TextMarkerProperties? TextMarkerProperties => null;
    public override double Indent { get; } = indent;
}

/// <summary>Feeds one block's display text to the TextFormatter, one styled span at a time; a line break (<c>&lt;br&gt;</c>) ends the line.</summary>
internal sealed class BlockTextSource(BlockText text, Func<Span, TextRunProperties> props) : TextSource
{
    public override TextRun GetTextRun(int cp)
    {
        var display = text.Display;
        if (cp >= display.Length) return new TextEndOfParagraph(1);
        if (display[cp] == '\n') return new TextEndOfLine(1);
        var spans = text.Block.Spans;
        var span = 0;
        for (var i = 0; i < spans.Count; i++)
        {
            if (text.SpanStarts[i] <= cp) span = i;
            else break;
        }
        var end = span + 1 < spans.Count ? text.SpanStarts[span + 1] : display.Length;
        var newline = display.IndexOf('\n', cp, end - cp);
        if (newline >= 0) end = newline;
        return new TextCharacters(display, cp, Math.Max(1, end - cp), props(spans.Count > 0 ? spans[span] : new Span("")));
    }

    public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int cp)
    {
        var start = 0;
        var range = new CharacterBufferRange(text.Display, start, Math.Max(0, Math.Min(cp, text.Display.Length) - start));
        return new TextSpan<CultureSpecificCharacterBufferRange>(range.Length, new CultureSpecificCharacterBufferRange(CultureInfo.CurrentUICulture, range));
    }

    public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int cp) => cp;
}

/// <summary>Something drawn for a unit: a formatted text line, an image, or the "…" placeholder.</summary>
internal abstract record Drawn(double Top, double Bottom)
{
    public sealed record TextLineItem(double Top, double Bottom, TextLine Line, int CharStart, BlockText Text, int BlockCharStart) : Drawn(Top, Bottom);
    public sealed record ImageItem(double Top, double Bottom, double X, double Width, Func<ImageSource?> Source) : Drawn(Top, Bottom);
    public sealed record PlaceholderItem(double Top, double Bottom) : Drawn(Top, Bottom);
}

/// <summary>A unit laid out for drawing: its items, with lines and images positioned from its top.</summary>
internal sealed class RenderedUnit(UnitLayout layout, List<Drawn> items, List<BlockText> blocks, int[] blockStarts) : IDisposable
{
    public UnitLayout Layout { get; } = layout;
    public List<Drawn> Items { get; } = items;
    public List<BlockText> Blocks { get; } = blocks;
    /// <summary>Where each block's display text starts in the unit's character indexes.</summary>
    public int[] BlockStarts { get; } = blockStarts;

    public void Dispose()
    {
        foreach (var item in Items)
        {
            if (item is Drawn.TextLineItem t) t.Line.Dispose();
        }
    }
}

/// <summary>
/// Lays out flowing-text units with WPF's TextFormatter at the column width, for
/// <see cref="FlowLayout"/> (geometry) and for drawing (the formatted lines, kept for the units
/// near the reader).
/// </summary>
internal sealed class WpfUnitMeasurer : IUnitMeasurer, IDisposable
{
    private readonly IFlowSource _source;
    private readonly FlowStyle _style;
    private readonly double _width;
    private readonly double _firstUnitInset;
    private readonly Func<int, PageSizeInfo?> _pageSize;
    private readonly Func<string, ImageSource?> _epubImage;
    private readonly Func<int, ImageSource?> _pageImage;
    private readonly TextFormatter _formatter = TextFormatter.Create(TextFormattingMode.Ideal);
    private readonly Dictionary<int, RenderedUnit> _rendered = new();
    private readonly LinkedList<int> _order = new();
    private const int Keep = 80;

    public readonly record struct PageSizeInfo(double Width, double Height);

    public WpfUnitMeasurer(IFlowSource source, FlowStyle style, double width, double firstUnitInset,
        Func<int, PageSizeInfo?> pageSize, Func<string, ImageSource?> epubImage, Func<int, ImageSource?> pageImage)
    {
        _source = source;
        _style = style;
        _width = Math.Max(40, width);
        _firstUnitInset = firstUnitInset;
        _pageSize = pageSize;
        _epubImage = epubImage;
        _pageImage = pageImage;
    }

    public int UnitCount => _source.UnitCount;

    public UnitLayout Measure(int unit) => Rendered(unit).Layout;

    /// <summary>The unit's drawable items, formatting it again if it was evicted.</summary>
    public RenderedUnit Rendered(int unit)
    {
        if (_rendered.TryGetValue(unit, out var r))
        {
            _order.Remove(unit);
            _order.AddFirst(unit);
            return r;
        }
        r = Format(unit);
        _rendered[unit] = r;
        _order.AddFirst(unit);
        while (_order.Count > Keep)
        {
            var old = _order.Last!.Value;
            _order.RemoveLast();
            if (_rendered.Remove(old, out var gone)) gone.Dispose();
        }
        return r;
    }

    public void Forget(int unit)
    {
        if (_rendered.Remove(unit, out var r))
        {
            _order.Remove(unit);
            r.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var r in _rendered.Values) r.Dispose();
        _rendered.Clear();
        _order.Clear();
        _formatter.Dispose();
    }

    private RenderedUnit Format(int unit)
    {
        var items = new List<Drawn>();
        var lines = new List<FlowLine>();
        var blocks = new List<BlockText>();
        var blockStarts = new List<int>();
        var y = unit == 0 ? _firstUnitInset : 0;
        var chars = 0;
        var s = _style;
        switch (_source.Content(unit))
        {
            case null:
            {
                var h = s.PlaceholderHeight;
                items.Add(new Drawn.PlaceholderItem(y, y + h));
                lines.Add(new FlowLine(y, y + h, 0, 1));
                y += h;
                chars = 1;
                break;
            }
            case FlowContent.PageImage page:
            {
                var size = _pageSize(page.Page) ?? new PageSizeInfo(612, 792);
                var h = Math.Max(1, _width * size.Height / Math.Max(1, size.Width));
                y += 16;
                var p = page.Page;
                items.Add(new Drawn.ImageItem(y, y + h, 0, _width, () => _pageImage(p)));
                lines.Add(new FlowLine(y, y + h, 0, 1));
                y += h + 16;
                chars = 1;
                break;
            }
            case FlowContent.Blocks content:
            {
                for (var b = 0; b < content.Items.Count; b++)
                {
                    var extra = content.ChapterStart && b == 0 ? FlowStyle.ChapterSpace : 0;
                    var block = content.Items[b];
                    if (block.Kind == BlockKind.Image)
                    {
                        var path = block.Image ?? "";
                        var img = _epubImage(path);
                        var natW = img is BitmapSource bs ? bs.PixelWidth : img?.Width ?? 0;
                        var natH = img is BitmapSource bh ? bh.PixelHeight : img?.Height ?? 0;
                        y += 12 + extra;
                        if (img is not null && natW > 0 && natH > 0)
                        {
                            var w = Math.Min(_width, natW);
                            var h = w * natH / natW;
                            items.Add(new Drawn.ImageItem(y, y + h, (_width - w) / 2, w, () => img));
                            lines.Add(new FlowLine(y, y + h, chars, chars + 1));
                            y += h;
                        }
                        y += 12;
                        blocks.Add(BlockText.Of(new Block(BlockKind.Image), false));
                        blockStarts.Add(chars);
                        chars += 1;
                        continue;
                    }
                    var (size, weight, color, alignment, lineHeight, before, after, indent) = block.Kind switch
                    {
                        BlockKind.Heading => (s.BodySize * 1.25, FontWeights.SemiBold, s.Text, TextAlignment.Center,
                            s.BodySize * 1.25 * 1.15, 28 * s.Scale + extra, 14 * s.Scale, 0.0),
                        BlockKind.Note => (s.BodySize * 0.8, FontWeights.Normal, s.Muted, TextAlignment.Left,
                            s.BodySize * 0.8 * 1.2, 10 * s.Scale + extra, 4 * s.Scale, 0.0),
                        _ => (s.BodySize, FontWeights.Normal, s.Text, TextAlignment.Justify,
                            s.BodySize * 1.3, extra, 0.0, block.Continuation ? 0 : s.BodySize * 1.6),
                    };
                    var text = BlockText.Of(block, hyphenate: block.Kind != BlockKind.Heading);
                    blocks.Add(text);
                    blockStarts.Add(chars);
                    var regular = new RunProps(new Typeface(s.Family, FontStyles.Normal, weight, FontStretches.Normal), size, color, s.Culture);
                    var cache = new Dictionary<(bool, bool), RunProps>();
                    TextRunProperties Props(Span span)
                    {
                        if (!span.Bold && !span.Italic) return regular;
                        if (!cache.TryGetValue((span.Bold, span.Italic), out var p))
                        {
                            p = new RunProps(new Typeface(s.Family, span.Italic ? FontStyles.Italic : FontStyles.Normal,
                                span.Bold ? FontWeights.Bold : weight, FontStretches.Normal), size, color, s.Culture);
                            cache[(span.Bold, span.Italic)] = p;
                        }
                        return p;
                    }
                    var source = new BlockTextSource(text, Props);
                    var firstProps = new ParagraphProps(regular, alignment, lineHeight, indent, first: true);
                    var restProps = new ParagraphProps(regular, alignment, lineHeight, indent, first: false);
                    y += before;
                    var cp = 0;
                    TextLineBreak? lineBreak = null;
                    var firstLine = true;
                    var guard = 0;
                    while (guard++ < 100_000)
                    {
                        var line = _formatter.FormatLine(source, cp, _width, firstLine ? firstProps : restProps, lineBreak);
                        var h = Math.Max(line.Height, lineHeight);
                        items.Add(new Drawn.TextLineItem(y, y + h, line, chars + cp, text, chars));
                        lines.Add(new FlowLine(y, y + h, chars + cp, chars + Math.Min(text.Display.Length, cp + line.Length)));
                        y += h;
                        var next = line.GetTextLineBreak();
                        lineBreak = next;
                        firstLine = false;
                        if (line.Length <= 0) break;
                        cp += line.Length;
                        if (cp > text.Display.Length) break;
                    }
                    y += after;
                    chars += text.Display.Length + 1;
                }
                if (content.Items.Count == 0) y += s.BodySize;
                break;
            }
        }
        return new RenderedUnit(new UnitLayout(y, lines, Math.Max(1, chars)), items, blocks, blockStarts.ToArray());
    }
}
