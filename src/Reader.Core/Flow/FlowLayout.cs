namespace Reader.Core.Flow;

/// <summary>
/// One laid-out line (or an unbreakable item such as an image) of a unit, in device-independent
/// pixels from the unit's top. <see cref="CharStart"/>..<see cref="CharEnd"/> is its text range
/// within the unit.
/// </summary>
public readonly record struct FlowLine(double Top, double Bottom, int CharStart, int CharEnd);

/// <summary>A unit (a PDF page's text, or an EPUB block) laid out at the column width.</summary>
public sealed class UnitLayout(double height, IReadOnlyList<FlowLine> lines, int charCount)
{
    /// <summary>Total height, including the space before and after its paragraphs.</summary>
    public double Height { get; } = Math.Max(1, height);
    public IReadOnlyList<FlowLine> Lines { get; } = lines;
    public int CharCount { get; } = charCount;
}

/// <summary>Lays out units at the current column width and text style. The app's is WPF's TextFormatter; tests use a fake.</summary>
public interface IUnitMeasurer
{
    int UnitCount { get; }
    UnitLayout Measure(int unit);
}

/// <summary>A place in the flow: <see cref="Offset"/> pixels below the top of <see cref="Unit"/>.</summary>
public readonly record struct FlowPos(int Unit, double Offset);

/// <summary>A character in the flow: index <see cref="Char"/> within <see cref="Unit"/>'s text.</summary>
public readonly record struct FlowChar(int Unit, int Char);

/// <summary>
/// One page (one column) of flowing text: it shows the flow from <see cref="Start"/> down to
/// <see cref="VisibleHeight"/>. The line cut by the bottom edge is hidden and starts the next
/// page, at <see cref="Next"/> / <see cref="NextChar"/>.
/// </summary>
public sealed record PageSlice(FlowPos Start, FlowChar StartChar, double VisibleHeight, FlowPos? Next, FlowChar? NextChar, bool AtEnd);

/// <summary>
/// Geometry of the flowing text, without global coordinates: everything is measured from an
/// anchor (a unit and an offset into it), so only the units around the reader are ever laid out,
/// and text above changing height (pages finishing building) never moves what the reader sees.
///
/// Page-by-page reading is built on whole lines: a page ends above the first line that doesn't
/// fit; that line starts the next page. "Back" is computed from the same line geometry.
/// </summary>
public sealed class FlowLayout
{
    public const double Eps = 0.5;

    private readonly IUnitMeasurer _measurer;
    private readonly Dictionary<int, UnitLayout> _cache = new();

    public FlowLayout(IUnitMeasurer measurer) => _measurer = measurer;

    public int UnitCount => _measurer.UnitCount;

    public UnitLayout Unit(int unit)
    {
        if (!_cache.TryGetValue(unit, out var u))
        {
            u = _measurer.Measure(unit);
            _cache[unit] = u;
        }
        return u;
    }

    public double Height(int unit) => Unit(unit).Height;

    /// <summary>Forget a unit's layout (its content changed).</summary>
    public void Invalidate(int unit) => _cache.Remove(unit);

    /// <summary>Forget every layout (width, text size or theme changed).</summary>
    public void InvalidateAll() => _cache.Clear();

    public bool IsMeasured(int unit) => _cache.ContainsKey(unit);

    public static FlowPos DocumentStart => new(0, 0);

    /// <summary>
    /// The same place with <see cref="FlowPos.Offset"/> inside its unit (0 ≤ offset &lt; height),
    /// clamped to the document.
    /// </summary>
    public FlowPos Normalize(FlowPos p)
    {
        if (UnitCount == 0) return DocumentStart;
        var unit = Math.Clamp(p.Unit, 0, UnitCount - 1);
        var offset = unit == p.Unit ? p.Offset : (p.Unit < 0 ? 0 : Height(unit));
        while (offset < 0 && unit > 0)
        {
            unit--;
            offset += Height(unit);
        }
        if (offset < 0) return DocumentStart;
        while (offset >= Height(unit) && unit < UnitCount - 1)
        {
            offset -= Height(unit);
            unit++;
        }
        if (offset > Height(unit)) offset = Height(unit);
        return new FlowPos(unit, offset);
    }

    public FlowPos Advance(FlowPos p, double dy) => Normalize(p with { Offset = p.Offset + dy });

    /// <summary>Pixels from <paramref name="a"/> down to <paramref name="b"/> (negative if b is above a).</summary>
    public double Distance(FlowPos a, FlowPos b)
    {
        if (a.Unit == b.Unit) return b.Offset - a.Offset;
        if (a.Unit > b.Unit) return -Distance(b, a);
        var d = Height(a.Unit) - a.Offset;
        for (var u = a.Unit + 1; u < b.Unit; u++) d += Height(u);
        return d + b.Offset;
    }

    public bool Same(FlowPos a, FlowPos b) => Math.Abs(Distance(a, b)) < Eps;

    /// <summary>How far content runs below <paramref name="start"/>, up to <paramref name="limit"/>.</summary>
    public double ContentBelow(FlowPos start, double limit)
    {
        if (UnitCount == 0) return 0;
        var p = Normalize(start);
        var d = Height(p.Unit) - p.Offset;
        for (var u = p.Unit + 1; u < UnitCount && d < limit; u++) d += Height(u);
        return d;
    }

    /// <summary>Lines from the one at <paramref name="start"/> down to <paramref name="maxY"/>, with their top relative to <paramref name="start"/>.</summary>
    public IEnumerable<(int Unit, int Index, FlowLine Line, double Top, double Bottom)> LinesFrom(FlowPos start, double maxY)
    {
        if (UnitCount == 0) yield break;
        var p = Normalize(start);
        var unitTop = -p.Offset;
        for (var u = p.Unit; u < UnitCount && unitTop < maxY; u++)
        {
            var layout = Unit(u);
            for (var i = 0; i < layout.Lines.Count; i++)
            {
                var line = layout.Lines[i];
                var top = unitTop + line.Top;
                var bottom = unitTop + line.Bottom;
                if (bottom <= Eps) continue;
                if (top >= maxY) yield break;
                yield return (u, i, line, top, bottom);
            }
            unitTop += layout.Height;
        }
    }

    /// <summary>Lines above <paramref name="start"/> (ending at or before it), nearest first, with their top relative to <paramref name="start"/> (negative).</summary>
    public IEnumerable<(int Unit, FlowLine Line, double Top, double Bottom)> LinesAbove(FlowPos start, double maxDistance)
    {
        if (UnitCount == 0) yield break;
        var p = Normalize(start);
        var unitTop = -p.Offset;
        for (var u = p.Unit; u >= 0 && unitTop > -maxDistance - Height(u); u--)
        {
            if (u != p.Unit) unitTop -= Height(u);
            var layout = Unit(u);
            for (var i = layout.Lines.Count - 1; i >= 0; i--)
            {
                var line = layout.Lines[i];
                var top = unitTop + line.Top;
                if (top >= -Eps) continue;
                if (top < -maxDistance - Eps) yield break;
                yield return (u, line, top, unitTop + line.Bottom);
            }
        }
    }

    /// <summary>A line cut by <paramref name="p"/> (starting above it, ending below it), if any.</summary>
    public (int Unit, FlowLine Line, double Top, double Bottom)? Straddling(FlowPos p)
    {
        foreach (var l in LinesFrom(p, Eps * 2))
        {
            if (l.Top < -Eps && l.Bottom > Eps) return (l.Unit, l.Line, l.Top, l.Bottom);
        }
        return null;
    }

    /// <summary>After a jump or restore: start the page on the line the position falls in.</summary>
    public FlowPos AlignToLine(FlowPos p)
    {
        p = Normalize(p);
        return Straddling(p) is { } s ? Advance(p, s.Top) : p;
    }

    /// <summary>The first character at or after <paramref name="p"/>.</summary>
    public FlowChar CharAt(FlowPos p)
    {
        foreach (var l in LinesFrom(p, double.MaxValue))
        {
            if (l.Top > -Eps) return new FlowChar(l.Unit, l.Line.CharStart);
            if (l.Bottom > Eps) return new FlowChar(l.Unit, l.Line.CharStart);
        }
        var last = UnitCount - 1;
        return new FlowChar(Math.Max(0, last), last >= 0 ? Unit(last).CharCount : 0);
    }

    /// <summary>The top of the line holding character <paramref name="c"/>.</summary>
    public FlowPos PosOf(FlowChar c)
    {
        if (UnitCount == 0) return DocumentStart;
        var unit = Math.Clamp(c.Unit, 0, UnitCount - 1);
        var layout = Unit(unit);
        FlowLine? best = null;
        foreach (var line in layout.Lines)
        {
            if (line.CharStart <= c.Char) best = line;
            else break;
        }
        return Normalize(new FlowPos(unit, best?.Top ?? 0));
    }

    /// <summary>
    /// The page that starts at <paramref name="start"/> and is <paramref name="pageHeight"/> tall:
    /// it ends above the first line whose bottom doesn't fit, and that line starts the next page.
    /// A single item taller than the page (a big image) is cut at the page height instead.
    /// </summary>
    public PageSlice Page(FlowPos start, double pageHeight)
    {
        start = Normalize(start);
        var startChar = CharAt(start);
        foreach (var l in LinesFrom(start, double.MaxValue))
        {
            if (l.Bottom <= pageHeight + Eps) continue;
            if (l.Top > Eps)
            {
                var next = Advance(start, l.Top);
                return new PageSlice(start, startChar, l.Top, next, new FlowChar(l.Unit, l.Line.CharStart), AtEnd: false);
            }
            // Taller than the page and already at its top: show what fits, continue below it.
            var cut = Advance(start, pageHeight);
            return new PageSlice(start, startChar, pageHeight, cut, CharAt(cut), AtEnd: false);
        }
        // Everything left fits.
        return new PageSlice(start, startChar, pageHeight, null, null, AtEnd: true);
    }

    /// <summary>
    /// Where the page before the one starting at <paramref name="current"/> starts, from line
    /// geometry: the earliest line top from which a page ends exactly at
    /// <paramref name="current"/>; failing that, the earliest whole line within a page above it.
    /// Null at the beginning of the document.
    /// </summary>
    public FlowPos? PreviousPageStart(FlowPos current, double pageHeight)
    {
        current = Normalize(current);
        if (current.Unit == 0 && current.Offset < Eps) return null;
        // Line tops a page could start on. Space between paragraphs above the current page can
        // put the exact start a little more than a page above, so look a little further.
        // A page can start on a line or at the top of a unit (with the space above its first line).
        var candidates = new List<(FlowPos Pos, double Top)>();
        var seenUnits = new HashSet<int>();
        foreach (var l in LinesAbove(current, pageHeight * 2))
        {
            candidates.Add((Advance(current, l.Top), l.Top));
            if (seenUnits.Add(l.Unit))
            {
                var unitTop = Distance(current, new FlowPos(l.Unit, 0));
                if (unitTop < -Eps && unitTop >= -pageHeight * 2 && unitTop < l.Top - Eps) candidates.Add((new FlowPos(l.Unit, 0), unitTop));
            }
        }
        // Earliest first: it fills the most of the page.
        candidates.Sort((a, b) => a.Top.CompareTo(b.Top));
        foreach (var c in candidates)
        {
            var page = Page(c.Pos, pageHeight);
            if (page.Next is { } next && Same(next, current)) return c.Pos;
        }
        // No line ends a page exactly here (an image in the way): the earliest whole line within a page.
        foreach (var c in candidates)
        {
            if (c.Top >= -pageHeight - Eps) return c.Pos;
        }
        var back = Advance(current, -pageHeight);
        return Same(back, current) ? null : back;
    }

    /// <summary>The unit at <paramref name="p"/> and how far down it <paramref name="p"/> is.</summary>
    public (int Unit, float Fraction) UnitFraction(FlowPos p)
    {
        if (UnitCount == 0) return (0, 0);
        p = Normalize(p);
        return (p.Unit, (float)Math.Clamp(p.Offset / Height(p.Unit), 0, 1));
    }

    /// <summary>The place <paramref name="fraction"/> of the way down <paramref name="unit"/>.</summary>
    public FlowPos PosOf(int unit, float fraction)
    {
        if (UnitCount == 0) return DocumentStart;
        unit = Math.Clamp(unit, 0, UnitCount - 1);
        return Normalize(new FlowPos(unit, Math.Clamp(fraction, 0f, 1f) * Height(unit)));
    }
}
