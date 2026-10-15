namespace Reader.Core.Model;

/// <summary>
/// Ordering key for conflict resolution: hybrid-logical-clock time, then device id as a
/// deterministic tie-break. Larger wins.
///
/// Device ids compare by UTF-16 code units (<see cref="string.CompareOrdinal(string, string)"/>),
/// which is how Kotlin's <c>String.compareTo</c>, JavaScript's <c>&lt;</c> and the Mac app's
/// <c>Stamp</c> order them, so every client and the server agree on the winner.
/// </summary>
public readonly record struct Stamp(long Hlc, string DeviceId) : IComparable<Stamp>
{
    public int CompareTo(Stamp other)
    {
        if (Hlc != other.Hlc) return Hlc < other.Hlc ? -1 : 1;
        return Math.Sign(string.CompareOrdinal(DeviceId ?? "", other.DeviceId ?? ""));
    }

    public static bool operator <(Stamp a, Stamp b) => a.CompareTo(b) < 0;
    public static bool operator >(Stamp a, Stamp b) => a.CompareTo(b) > 0;
    public static bool operator <=(Stamp a, Stamp b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Stamp a, Stamp b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// Where a reader is in a document, in device-independent terms.
///
/// Offsets are fractions of the page, not pixels, so the same position restores correctly on a
/// different screen size, window size or zoom level. Field meanings match the Android and Mac
/// apps and the server contract exactly (docs/position-sync.md). Fractions are <c>float</c>, as
/// on Android and the Mac, so a value round-trips bit for bit between them.
/// </summary>
public sealed record ReadingPosition
{
    /// <summary>SHA-256 of the file bytes, so the same file has the same id on every device.</summary>
    public required string DocumentId { get; init; }
    /// <summary>0-based page (PDF) or chapter (EPUB) at the top edge of the viewport.</summary>
    public int PageIndex { get; init; }
    /// <summary>How far down <see cref="PageIndex"/> the viewport's top edge is: 0 = top, 1 = bottom.</summary>
    public float PageOffset { get; init; }
    /// <summary>Horizontal scroll as a fraction of the scrollable width (only meaningful when zoom &gt; 1).</summary>
    public float HorizontalOffset { get; init; }
    /// <summary>Zoom relative to fit-width.</summary>
    public float Zoom { get; init; } = 1;
    /// <summary>Page count at save time; used to reject positions that don't fit the document.</summary>
    public int PageCount { get; init; }
    public Stamp Stamp { get; init; } = new(0, "");
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public const int CurrentSchemaVersion = 1;
    public const float MinZoom = 1;
    public const float MaxZoom = 4;

    /// <summary>True if this position should win over <paramref name="other"/> under last-writer-wins.</summary>
    public bool IsNewerThan(ReadingPosition? other) => other is null || Stamp > other.Stamp;

    /// <summary>Same place in the document, ignoring when/where it was recorded.</summary>
    public bool SamePlace(ReadingPosition? other, float tolerance = 0.002f)
    {
        if (other is null) return false;
        return PageIndex == other.PageIndex &&
               MathF.Abs(PageOffset - other.PageOffset) <= tolerance &&
               MathF.Abs(HorizontalOffset - other.HorizontalOffset) <= tolerance &&
               Zoom == other.Zoom;
    }

    /// <summary>Clamped to a document with <paramref name="actualPageCount"/> pages, or null if it can't apply at all.</summary>
    public ReadingPosition? ValidFor(int actualPageCount)
    {
        if (actualPageCount <= 0 || PageCount != actualPageCount) return null;
        return this with
        {
            PageIndex = Math.Clamp(PageIndex, 0, actualPageCount - 1),
            PageOffset = Clamp(PageOffset, 0, 1),
            HorizontalOffset = Clamp(HorizontalOffset, 0, 1),
            Zoom = Clamp(Zoom, MinZoom, MaxZoom),
        };
    }

    public bool IsAtStart => PageIndex == 0 && PageOffset < 0.001f;

    public ReadingPosition WithStamp(Stamp stamp) => this with { Stamp = stamp };

    /// <summary>Swift's <c>clamped</c>: <c>min(max(v, lo), hi)</c>. NaN stays NaN, as there.</summary>
    internal static float Clamp(float v, float lo, float hi) => MathF.Min(MathF.Max(v, lo), hi);
}
