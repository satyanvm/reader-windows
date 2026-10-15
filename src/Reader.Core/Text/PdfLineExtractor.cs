using System.Text;
using UglyToad.PdfPig.Content;

namespace Reader.Core.Text;

/// <summary>
/// Pulls positioned, styled lines out of a PDF page with PdfPig. It plays the role of PDFKit's
/// <c>selectionsByLine</c> on the Mac and PdfBox on Android: glyphs are taken in content-stream
/// order (which is reading order in almost every PDF), grouped into lines where the baseline
/// changes or the text jumps far along the line, with spaces inserted at word gaps.
///
/// Coordinates are PDF points relative to the crop box, with y growing downward (the line's
/// bottom edge), like the Mac app and PdfBox's <c>yDirAdj</c>. Reflow only uses differences and
/// ratios.
/// </summary>
public static class PdfLineExtractor
{
    /// <summary>The part of the font size below the baseline that PDFKit's line bounds include.</summary>
    private const float Descent = 0.2f;

    private sealed class Builder
    {
        public readonly List<Span> Spans = new();
        public readonly StringBuilder Run = new();
        public bool Bold, Italic;
        public double MinX = double.MaxValue, MaxX = double.MinValue;
        public double Baseline, LastEndX;
        public double SizeSum;
        public int SizeCount;
        public bool PendingSpace;
        public bool Empty => SizeCount == 0;

        public void FlushRun()
        {
            if (Run.Length > 0) Spans.Add(new Span(Run.ToString(), Bold, Italic));
            Run.Clear();
        }
    }

    public static List<Line> Lines(Page page)
    {
        var box = page.CropBox.Bounds;
        var output = new List<Line>();
        Builder? cur = null;

        void Finish()
        {
            if (cur is null || cur.Empty) { cur = null; return; }
            cur.FlushRun();
            var spans = cur.Spans
                .Select(s => s with { Text = s.Text.Replace("\r", "").Replace("\n", "") })
                .Compact();
            if (spans.Count > 0 && !string.Concat(spans.Select(s => s.Text)).IsBlank())
            {
                var size = (float)(cur.SizeSum / cur.SizeCount);
                output.Add(new Line(
                    spans,
                    X: (float)(cur.MinX - box.Left),
                    EndX: (float)(cur.MaxX - box.Left),
                    Y: (float)(box.Top - cur.Baseline) + size * Descent,
                    FontSize: size));
            }
            cur = null;
        }

        foreach (var letter in page.Letters)
        {
            var value = letter.Value ?? "";
            if (value.Length == 0) continue;
            var size = letter.PointSize > 0 ? letter.PointSize : Math.Max(1, letter.GlyphRectangle.Height);
            var x0 = Math.Min(letter.StartBaseLine.X, letter.EndBaseLine.X);
            var x1 = Math.Max(letter.StartBaseLine.X, letter.EndBaseLine.X);
            var baseline = letter.StartBaseLine.Y;

            if (value.IsBlank())
            {
                if (cur is not null && !cur.Empty) cur.PendingSpace = true;
                continue;
            }

            if (cur is not null && !cur.Empty)
            {
                var avg = cur.SizeSum / cur.SizeCount;
                var small = Math.Min(avg, size);
                var gap = x0 - cur.LastEndX;
                var sameRow = Math.Abs(baseline - cur.Baseline) < small * 0.5;
                if (!sameRow || gap > small * 3 || gap < -small * 3) Finish();
                else if (cur.PendingSpace || gap > small * 0.15)
                {
                    cur.Run.Append(' ');
                }
            }
            cur ??= new Builder { Baseline = baseline };
            cur.PendingSpace = false;

            var (bold, italic) = StyleOf(letter);
            if (cur.Run.Length > 0 && (bold != cur.Bold || italic != cur.Italic))
            {
                // A space typed before a style change belongs to the earlier run.
                cur.FlushRun();
            }
            if (cur.Run.Length == 0)
            {
                cur.Bold = bold;
                cur.Italic = italic;
            }
            cur.Run.Append(value);
            cur.MinX = Math.Min(cur.MinX, x0);
            cur.MaxX = Math.Max(cur.MaxX, x1);
            cur.LastEndX = x1;
            cur.SizeSum += size * value.Length;
            cur.SizeCount += value.Length;
        }
        Finish();
        return output;
    }

    /// <summary>(bold, italic) from the font's flags and name ("ABCDEF+Garamond-BoldItalic").</summary>
    public static (bool Bold, bool Italic) StyleOf(Letter letter)
    {
        var raw = letter.FontName ?? letter.Font?.Name ?? "";
        var plus = raw.IndexOf('+');
        var name = (plus >= 0 ? raw[(plus + 1)..] : raw).ToLowerInvariant();
        var bold = (letter.Font?.IsBold ?? false) ||
                   new[] { "bold", "black", "heavy", "semibold" }.Any(name.Contains);
        var italic = (letter.Font?.IsItalic ?? false) || name.Contains("italic") || name.Contains("oblique");
        return (bold, italic);
    }
}
