using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reader.Core.Text;

/// <summary>A run of text in one style.</summary>
public sealed record Span(string Text, bool Bold = false, bool Italic = false);

/// <summary>
/// One visual line from a page, from PDF text or OCR. Coordinates are in any consistent unit
/// (PDF points or image pixels); only their ratios to <see cref="FontSize"/> matter. <see cref="Y"/> grows downward.
/// </summary>
public sealed record Line(IReadOnlyList<Span> Spans, float X, float EndX, float Y, float FontSize)
{
    public string Text => string.Concat(Spans.Select(s => s.Text));

    public bool Equals(Line? other) =>
        other is not null && X == other.X && EndX == other.EndX && Y == other.Y && FontSize == other.FontSize &&
        Spans.SequenceEqual(other.Spans);

    public override int GetHashCode() => HashCode.Combine(X, EndX, Y, FontSize, Spans.Count);
}

public enum BlockKind { Paragraph, Heading, Image, Note }

public static class BlockKinds
{
    public static string RawValue(this BlockKind k) => k switch
    {
        BlockKind.Paragraph => "PARAGRAPH",
        BlockKind.Heading => "HEADING",
        BlockKind.Image => "IMAGE",
        _ => "NOTE",
    };

    public static BlockKind? Parse(string? raw) => raw switch
    {
        "PARAGRAPH" => BlockKind.Paragraph,
        "HEADING" => BlockKind.Heading,
        "IMAGE" => BlockKind.Image,
        "NOTE" => BlockKind.Note,
        _ => null,
    };
}

/// <summary>A unit of flowing text.</summary>
public sealed record Block
{
    /// <summary><see cref="BlockKind.Note"/> is a footnote, moved to the end of its page (PDF flowing text only).</summary>
    public BlockKind Kind { get; init; }
    public IReadOnlyList<Span> Spans { get; init; } = Array.Empty<Span>();
    /// <summary>First line was indented in the source (only meaningful for paragraphs).</summary>
    public bool Indented { get; init; }
    /// <summary>Continues a paragraph from the previous page: render without a first-line indent.</summary>
    public bool Continuation { get; init; }
    /// <summary>For <see cref="BlockKind.Image"/>: a path inside the book.</summary>
    public string? Image { get; init; }

    public Block(BlockKind kind, IReadOnlyList<Span>? spans = null, bool indented = false, bool continuation = false, string? image = null)
    {
        Kind = kind;
        Spans = spans ?? Array.Empty<Span>();
        Indented = indented;
        Continuation = continuation;
        Image = image;
    }

    public string Text => string.Concat(Spans.Select(s => s.Text));

    public bool Equals(Block? other) =>
        other is not null && Kind == other.Kind && Indented == other.Indented && Continuation == other.Continuation &&
        Image == other.Image && Spans.SequenceEqual(other.Spans);

    public override int GetHashCode() => HashCode.Combine(Kind, Indented, Continuation, Image, Spans.Count);
}

/// <summary>One source page (PDF) as flowing text. Empty means no text was found (e.g. a figure).</summary>
public sealed record PageText(IReadOnlyList<Block> Blocks, bool Recognized = false)
{
    public bool IsEmpty => Blocks.Count == 0;

    public bool Equals(PageText? other) =>
        other is not null && Recognized == other.Recognized && Blocks.SequenceEqual(other.Blocks);

    public override int GetHashCode() => HashCode.Combine(Recognized, Blocks.Count);

    /// <summary>Same JSON shape as the Android and Mac cache files.</summary>
    public byte[] Json()
    {
        var blocks = new JsonArray();
        foreach (var b in Blocks)
        {
            var spans = new JsonArray();
            foreach (var s in b.Spans) spans.Add(new JsonObject { ["t"] = s.Text, ["b"] = s.Bold, ["it"] = s.Italic });
            blocks.Add(new JsonObject
            {
                ["k"] = b.Kind.RawValue(),
                ["i"] = b.Indented,
                ["c"] = b.Continuation,
                ["img"] = b.Image,
                ["s"] = spans,
            });
        }
        var root = new JsonObject { ["recognized"] = Recognized, ["blocks"] = blocks };
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    public static PageText FromJson(byte[] data)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(data); }
        catch (JsonException) { throw new FormatException("bad page text"); }
        if (root is not JsonObject o || o["blocks"] is not JsonArray arr) throw new FormatException("bad page text");
        var blocks = new List<Block>();
        foreach (var node in arr)
        {
            if (node is not JsonObject b) throw new FormatException("bad block");
            var kind = BlockKinds.Parse(Str(b["k"])) ?? throw new FormatException("bad block kind");
            var spans = new List<Span>();
            if (b["s"] is JsonArray sa)
            {
                foreach (var sn in sa)
                {
                    if (sn is not JsonObject so) continue;
                    spans.Add(new Span(Str(so["t"]) ?? "", Bool(so["b"]), Bool(so["it"])));
                }
            }
            blocks.Add(new Block(kind, spans, Bool(b["i"]), Bool(b["c"]), Str(b["img"])));
        }
        return new PageText(blocks, Bool(o["recognized"]));
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

public static class SpanExtensions
{
    /// <summary>Merges adjacent spans with the same style, dropping empty ones.</summary>
    public static List<Span> Compact(this IEnumerable<Span> spans)
    {
        var output = new List<Span>();
        foreach (var s in spans)
        {
            if (s.Text.Length == 0) continue;
            if (output.Count > 0 && output[^1] is var last && last.Bold == s.Bold && last.Italic == s.Italic)
            {
                output[^1] = last with { Text = last.Text + s.Text };
            }
            else
            {
                output.Add(s);
            }
        }
        return output;
    }
}
