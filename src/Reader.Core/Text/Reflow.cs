using System.Text;
using System.Text.RegularExpressions;

namespace Reader.Core.Text;

/// <summary>
/// Turns a page's visual lines into flowing blocks (paragraphs and headings).
///
/// PDFs only record where glyphs sit, so structure is inferred from geometry, the way a typesetter
/// would read it:
///  - a new paragraph starts at an indented line, after extra vertical space, or after a short
///    line that ends a sentence (the ragged last line of a justified paragraph);
///  - noticeably larger type is a heading;
///  - a word hyphenated at a line end is rejoined.
///
/// A direct port of the Mac and Android <c>Reflow</c>; thresholds and tie-breaks are identical.
/// </summary>
public static class Reflow
{
    private static readonly HashSet<char> SentenceEndChars = ['.', '!', '?', ':', ';', '\'', '"', '”', '’', ')', ']'];
    private static readonly Regex PageNumber = new(@"^(\d{1,4}|[ivxlcdm]{1,7}|page \d+( of \d+)?|- ?\d{1,4} ?-)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Blocks for one page, in reading order. Multi-column pages are read column by column, and
    /// footnotes (smaller type at the foot of a column) come last, as <see cref="BlockKind.Note"/>
    /// blocks, so they never split a paragraph that runs on into the next column or page.
    /// </summary>
    public static List<Block> Blocks(IReadOnlyList<Line> input)
    {
        var lines = DropPageNumbers(MergeSameRow(
            input.Select(l => l with { Spans = NormalizeSpaces(l.Spans) })
                .Where(l => !l.Text.IsBlank())
                .ToList()));
        if (lines.Count == 0) return new();

        var body = Median(lines.Select(l => l.FontSize));
        var output = new List<Block>();
        var notes = new List<Block>();
        foreach (var column in Columns(lines, body))
        {
            var (main, foot) = SplitFootnotes(column, body);
            var blocks = ColumnBlocks(main, body);
            // A paragraph that runs from the foot of one column to the head of the next is one.
            if (output.Count > 0 && blocks.Count > 0 &&
                Continues(output[^1], blocks[0], nextPageUsesIndents: blocks.Any(b => b.Indented)))
            {
                output[^1] = Merge(output[^1], blocks[0]);
                output.AddRange(blocks.Skip(1));
            }
            else
            {
                output.AddRange(blocks);
            }
            notes.AddRange(FootnoteBlocks(foot));
        }
        output.AddRange(notes);
        return output;
    }

    /// <summary>The single-column reflow (the Android algorithm), with margins measured on this column.</summary>
    internal static List<Block> ColumnBlocks(IReadOnlyList<Line> lines, float body)
    {
        if (lines.Count == 0) return new();
        var larger = lines.Where(l => !IsLarger(l.FontSize, body)).ToList();
        var bodyLines = larger.Count == 0 ? lines.ToList() : larger;
        var left = Percentile(bodyLines.Select(l => l.X), 0.1);
        var right = Percentile(bodyLines.Select(l => l.EndX), 0.75);
        var gaps = bodyLines.Zip(bodyLines.Skip(1), (a, b) => b.Y - a.Y).Where(g => g > 0).ToList();
        var lineGap = gaps.Count == 0 ? body * 1.2f : Median(gaps);
        var indentMin = body * 0.8f;

        var blocks = new List<Block>();
        var current = new List<Line>();
        var currentIndented = false;

        void Flush()
        {
            if (current.Count == 0) return;
            var heading = current.All(l => IsLarger(l.FontSize, body)) ||
                (current.Count == 1 && current[0].Spans.All(s => s.Bold) && current[0].Text.Length < 80 &&
                 current[0].X - left > indentMin); // short, bold and centred
            blocks.Add(new Block(heading ? BlockKind.Heading : BlockKind.Paragraph, Join(current), indented: currentIndented && !heading));
            current = new List<Line>();
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var prev = i > 0 ? lines[i - 1] : null;
            var indented = line.X - left > indentMin && line.X - left < (right - left) * 0.5f;
            var startsNew = true;
            if (prev is not null)
            {
                startsNew = IsLarger(line.FontSize, body) != IsLarger(prev.FontSize, body) ||
                    line.Y - prev.Y > lineGap * 1.6f ||
                    (indented && MathF.Abs(line.X - prev.X) > indentMin / 2) ||
                    (prev.EndX < right - body * 3 && EndsSentence(prev.Text.TrimTrailingWhitespace()));
            }
            if (startsNew)
            {
                Flush();
                currentIndented = indented;
            }
            current.Add(line);
        }
        Flush();
        return blocks;
    }

    /// <summary>
    /// PDF text extraction can hand back one visual line as several pieces (a font change, a
    /// footnote marker). Consecutive pieces on the same baseline, left to right, become one line.
    /// </summary>
    internal static List<Line> MergeSameRow(IReadOnlyList<Line> lines)
    {
        var output = new List<Line>();
        foreach (var line in lines)
        {
            if (output.Count > 0)
            {
                var prev = output[^1];
                var size = MathF.Min(prev.FontSize, line.FontSize);
                var gap = line.X - prev.EndX;
                if (MathF.Abs(line.Y - prev.Y) < size * 0.35f && gap > -size * 0.5f && gap < size * 2)
                {
                    var needsSpace = gap > size * 0.15f && !prev.Text.EndsWith(' ') && !line.Text.StartsWith(' ');
                    var spans = prev.Spans.Concat(needsSpace ? [new Span(" ")] : Array.Empty<Span>()).Concat(line.Spans).Compact();
                    output[^1] = prev with
                    {
                        Spans = spans,
                        EndX = MathF.Max(prev.EndX, line.EndX),
                        FontSize = MathF.Max(prev.FontSize, line.FontSize),
                    };
                    continue;
                }
            }
            output.Add(line);
        }
        return output;
    }

    /// <summary>
    /// Splits a page into columns at a vertical gutter that no body line crosses, in reading
    /// order: full-width lines above the columns (a chapter title), each column, then full-width
    /// lines below. A page without a clear gutter is one column.
    /// </summary>
    internal static List<List<Line>> Columns(IReadOnlyList<Line> lines, float body, int depth = 0)
    {
        if (depth >= 2 || lines.Count < 6) return [lines.ToList()];
        var minX = lines.Min(l => l.X);
        var maxX = lines.Max(l => l.EndX);
        var width = maxX - minX;
        if (!(width > body * 20)) return [lines.ToList()];
        // Prefer a gutter no line crosses; allow a stray line or two (a long heading) otherwise.
        (float Start, float End)? gutter = null;
        foreach (var tolerance in new[] { 0, Math.Max(1, lines.Count / 20) })
        {
            (float Start, float End)? best = null;
            (float Start, float End)? run = null;
            var g = minX + width * 0.2f;
            while (g <= minX + width * 0.8f)
            {
                var crossing = 0;
                foreach (var l in lines) if (l.X < g && l.EndX > g) crossing++;
                if (crossing <= tolerance)
                {
                    run = (run?.Start ?? g, g);
                }
                else
                {
                    if (run is { } r && r.End - r.Start > (best is { } b ? b.End - b.Start : -1)) best = r;
                    run = null;
                }
                g += 1;
            }
            if (run is { } r2 && r2.End - r2.Start > (best is { } b2 ? b2.End - b2.Start : -1)) best = r2;
            if (best is { } found && found.End - found.Start >= body * 0.8f)
            {
                gutter = found;
                break;
            }
        }
        if (gutter is not { } gut) return [lines.ToList()];
        var mid = (gut.Start + gut.End) / 2;
        var leftSide = lines.Where(l => l.EndX <= mid).ToList();
        var rightSide = lines.Where(l => l.X >= mid).ToList();
        // Both sides must read like text, not a list of page numbers or labels.
        bool IsColumn(List<Line> side) =>
            side.Count >= Math.Max(3, lines.Count / 5) &&
            side.Select(l => (float)l.Text.GraphemeCount()).Sum() / side.Count >= 15;
        if (!IsColumn(leftSide) || !IsColumn(rightSide)) return [lines.ToList()];
        var columnTop = leftSide.Concat(rightSide).Min(l => l.Y);
        var spanning = lines.Where(l => l.X < mid && l.EndX > mid).ToList();
        var above = spanning.Where(l => l.Y < columnTop).ToList();
        var below = spanning.Where(l => l.Y >= columnTop).ToList();
        var bodyColumns = Columns(leftSide, body, depth + 1).Concat(Columns(rightSide, body, depth + 1));
        return new[] { above }.Concat(bodyColumns).Concat(new[] { below }).Where(c => c.Count > 0).ToList();
    }

    private static readonly Regex NoteMarker = new(@"^\s*([*†‡§¶]+|\d{1,3}[.)]?\s|[¹²³⁴⁵⁶⁷⁸⁹⁰]+)", RegexOptions.CultureInvariant);

    private static bool StartsWithNoteMarker(string text) => NoteMarker.IsMatch(text);

    /// <summary>
    /// Footnotes: a run of smaller type at the foot of a column, set off by extra space or
    /// starting with a note marker (*, †, a number).
    /// </summary>
    internal static (List<Line> Main, List<Line> Notes) SplitFootnotes(IReadOnlyList<Line> lines, float body)
    {
        var start = lines.Count;
        while (start > 0 && lines[start - 1].FontSize < body * 0.88f) start -= 1;
        if (!(start < lines.Count && start > 0)) return (lines.ToList(), new());
        var gaps = lines.Zip(lines.Skip(1), (a, b) => b.Y - a.Y).Where(g => g > 0).ToList();
        var lineGap = gaps.Count == 0 ? body * 1.2f : Median(gaps);
        var first = lines[start];
        var setOff = first.Y - lines[start - 1].Y > lineGap * 1.3f;
        if (!(setOff || StartsWithNoteMarker(first.Text))) return (lines.ToList(), new());
        return (lines.Take(start).ToList(), lines.Skip(start).ToList());
    }

    /// <summary>One <see cref="BlockKind.Note"/> block per footnote: a new one starts at each note marker.</summary>
    internal static List<Block> FootnoteBlocks(IReadOnlyList<Line> lines)
    {
        var groups = new List<List<Line>>();
        foreach (var line in lines)
        {
            if (groups.Count == 0 || StartsWithNoteMarker(line.Text)) groups.Add([line]);
            else groups[^1].Add(line);
        }
        return groups.Select(g => new Block(BlockKind.Note, Join(g))).ToList();
    }

    /// <summary>Joins lines into one run of spans, undoing end-of-line hyphenation.</summary>
    public static List<Span> Join(IReadOnlyList<Line> lines)
    {
        var output = new List<Span>();
        foreach (var line in lines)
        {
            if (output.Count > 0)
            {
                var lastText = output[^1].Text.TrimTrailingWhitespace();
                var next = line.Spans.Count > 0 ? line.Spans[0].Text.TrimLeadingWhitespace() : "";
                if (EndsWithLetterHyphen(lastText) && next.FirstRune() is { } n && Rune.IsLower(n))
                {
                    output[^1] = output[^1] with { Text = lastText[..^1] };
                }
                else
                {
                    output[^1] = output[^1] with { Text = lastText + " " };
                }
            }
            for (var i = 0; i < line.Spans.Count; i++)
            {
                var s = line.Spans[i];
                if (i == 0) s = s with { Text = s.Text.TrimLeadingWhitespace() };
                output.Add(s);
            }
        }
        if (output.Count > 0) output[^1] = output[^1] with { Text = output[^1].Text.TrimTrailingWhitespace() };
        return output.Compact();
    }

    /// <summary>At least two characters, the last a hyphen and the one before it a letter.</summary>
    private static bool EndsWithLetterHyphen(string s)
    {
        if (s.Length < 2 || s[^1] != '-') return false;
        return s[..^1].LastRune() is { } r && Rune.IsLetter(r);
    }

    /// <summary>Can <paramref name="next"/> (first block of a page) continue <paramref name="prev"/> (last block of the page before)?</summary>
    public static bool Continues(Block? prev, Block? next, bool nextPageUsesIndents)
    {
        if (prev is null || next is null) return false;
        if (prev.Kind != BlockKind.Paragraph || next.Kind != BlockKind.Paragraph) return false;
        if (nextPageUsesIndents) return !next.Indented;
        var ends = EndsSentence(prev.Text.TrimTrailingWhitespace());
        var startsUpper = next.Text.TrimLeadingWhitespace().FirstRune() is { } f && Rune.IsUpper(f);
        return !(ends && startsUpper);
    }

    /// <summary><paramref name="prev"/> with <paramref name="next"/>'s text appended, as one paragraph.</summary>
    public static Block Merge(Block prev, Block next)
    {
        var a = new Line(prev.Spans, 0, 0, 0, 1);
        var b = new Line(next.Spans, 0, 0, 0, 1);
        return prev with { Spans = Join([a, b]) };
    }

    internal static bool EndsSentence(string s) => s.Length > 0 && SentenceEndChars.Contains(s[^1]);

    private static bool IsPageNumber(string s) => PageNumber.IsMatch(s.Trim());

    private static List<Line> DropPageNumbers(List<Line> lines)
    {
        int start = 0, end = lines.Count;
        while (start < end && IsPageNumber(lines[start].Text)) start += 1;
        while (end > start && IsPageNumber(lines[end - 1].Text)) end -= 1;
        return lines.GetRange(start, end - start);
    }

    private static bool IsLarger(float size, float body) => size > body * 1.15f;

    /// <summary>Spaces, tabs and no-break spaces collapse to one space (as on Android).</summary>
    private static List<Span> NormalizeSpaces(IReadOnlyList<Span> spans) =>
        spans.Select(s => s with { Text = s.Text.Collapsing(" \t ", " ") }).ToList();

    private static float Median(IEnumerable<float> values) => Percentile(values, 0.5);

    private static float Percentile(IEnumerable<float> values, double p)
    {
        var sorted = values.ToList();
        if (sorted.Count == 0) return 0;
        sorted.Sort();
        return sorted[(int)((sorted.Count - 1) * p)];
    }
}
