using System.Text.RegularExpressions;

namespace Reader.Core.Text;

/// <summary>
/// Cross-page cleanup, which needs to see neighbouring pages:
///  - running headers and footers (book title, chapter name, "Page 72") repeat on nearby pages
///    and are dropped;
///  - a paragraph split by a page break is rejoined onto the earlier page.
/// </summary>
public static class PageAssembly
{
    /// <summary>How many pages either side are compared when looking for running headers/footers.</summary>
    public const int Window = 2;

    /// <summary>Removes a first/last line that repeats (ignoring digits) at the same edge of a nearby page.</summary>
    public static List<Line> StripRunningLines(IReadOnlyList<Line> page, IEnumerable<IReadOnlyList<Line>> neighbours)
    {
        var lines = page.Where(l => !l.Text.IsBlank()).ToList();
        if (lines.Count < 2) return lines;
        var ns = neighbours.ToList();
        var tops = ns.Select(n => n.FirstOrDefault(l => !l.Text.IsBlank())).Where(l => l is not null).Select(l => Key(l!)).ToHashSet();
        var bottoms = ns.Select(n => n.LastOrDefault(l => !l.Text.IsBlank())).Where(l => l is not null).Select(l => Key(l!)).ToHashSet();
        var output = lines;
        if (IsRunning(output[0], tops)) output.RemoveAt(0);
        if (output.Count > 1 && IsRunning(output[^1], bottoms)) output.RemoveAt(output.Count - 1);
        return output;
    }

    /// <summary>
    /// Final blocks for a page given the reflowed blocks of the page before, this one and the one
    /// after. A paragraph that continues across a break is moved whole onto the earlier page,
    /// so both pages agree on who owns it no matter which is assembled first. Pages that would be
    /// left empty (a page that is one long paragraph) keep their text and are marked as
    /// continuations instead.
    /// </summary>
    public static List<Block> Assemble(IReadOnlyList<Block>? prev, IReadOnlyList<Block> page, IReadOnlyList<Block>? next)
    {
        if (page.Count == 0) return page.ToList();
        var output = page.ToList();
        // Footnotes sit at the end of a page; paragraphs continue around them.
        var mainCount = page.Count(b => b.Kind != BlockKind.Note);
        var continuesPrev = prev is not null &&
            Reflow.Continues(prev.LastOrDefault(b => b.Kind != BlockKind.Note), page[0], nextPageUsesIndents: UsesIndents(page));
        if (continuesPrev && mainCount >= 2)
        {
            output.RemoveAt(0);
        }
        else if (continuesPrev)
        {
            output[0] = output[0] with { Continuation = true };
        }
        if (next is not null && next.Count(b => b.Kind != BlockKind.Note) >= 2)
        {
            var last = output.FindLastIndex(b => b.Kind != BlockKind.Note);
            if (last >= 0 && Reflow.Continues(output[last], next.Count > 0 ? next[0] : null, nextPageUsesIndents: UsesIndents(next)))
            {
                output[last] = Reflow.Merge(output[last], next[0]);
            }
        }
        return output;
    }

    private static bool UsesIndents(IReadOnlyList<Block> blocks) => blocks.Any(b => b.Indented);

    private static readonly Regex Digits = new(@"\d+", RegexOptions.CultureInvariant);

    private static string Key(Line line)
    {
        var lower = line.Text.ToLowerInvariant();
        var hashed = Digits.Replace(lower, "#");
        // Java's \s: no-break spaces are not collapsed.
        return hashed.Collapsing(StringExtensions.JavaWhitespace, " ").Trim();
    }

    private static bool IsRunning(Line line, HashSet<string> edges)
    {
        if (line.Text.Length > 100) return false;
        var k = Key(line);
        return k.Length > 0 && edges.Contains(k);
    }
}
