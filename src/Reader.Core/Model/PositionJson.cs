using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Reader.Core.Model;

/// <summary>
/// The <c>position</c> JSON from the server contract (docs/position-sync.md), byte-compatible with
/// the Android and Mac apps: fractions are written as the <c>double</c> value of the <c>float</c>,
/// the way org.json does, so 0.4f goes out as 0.4000000059604645 and comes back bit for bit.
/// </summary>
public static class PositionJson
{
    public sealed class DecodeException(string message) : Exception(message);

    public static byte[] Encode(ReadingPosition p)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            Write(w, p);
        }
        return ms.ToArray();
    }

    public static string EncodeString(ReadingPosition p) => Encoding.UTF8.GetString(Encode(p));

    /// <summary>Keys sorted, like the Mac app's <c>.sortedKeys</c>.</summary>
    public static void Write(Utf8JsonWriter w, ReadingPosition p)
    {
        w.WriteStartObject();
        w.WriteString("deviceId", p.Stamp.DeviceId);
        w.WriteString("documentId", p.DocumentId);
        w.WriteNumber("hlc", p.Stamp.Hlc);
        w.WriteNumber("horizontalOffset", (double)p.HorizontalOffset);
        w.WriteNumber("pageCount", p.PageCount);
        w.WriteNumber("pageIndex", p.PageIndex);
        w.WriteNumber("pageOffset", (double)p.PageOffset);
        w.WriteNumber("schemaVersion", p.SchemaVersion);
        w.WriteNumber("zoom", (double)p.Zoom);
        w.WriteEndObject();
    }

    public static ReadingPosition Decode(byte[] data)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch (JsonException e) { throw new DecodeException("position is not JSON: " + e.Message); }
        using (doc) return Decode(doc.RootElement);
    }

    public static ReadingPosition Decode(string json) => Decode(Encoding.UTF8.GetBytes(json));

    /// <summary>Same required/optional fields and defaults as Android's <c>HttpSyncApi.fromJson</c>.</summary>
    public static ReadingPosition Decode(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) throw new DecodeException("position is not a JSON object");
        return new ReadingPosition
        {
            DocumentId = Req(o, "documentId", AsString),
            PageIndex = Req(o, "pageIndex", AsInt),
            PageOffset = Req(o, "pageOffset", AsFloat),
            HorizontalOffset = Opt(o, "horizontalOffset", AsFloat, 0f),
            Zoom = Opt(o, "zoom", AsFloat, 1f),
            PageCount = Req(o, "pageCount", AsInt),
            Stamp = new Stamp(Req(o, "hlc", AsLong), Req(o, "deviceId", AsString)),
            SchemaVersion = Opt(o, "schemaVersion", AsInt, ReadingPosition.CurrentSchemaVersion),
        };
    }

    private static T Req<T>(JsonElement o, string key, Func<JsonElement, T?> get) where T : struct =>
        o.TryGetProperty(key, out var v) && get(v) is { } r ? r : throw new DecodeException($"missing or bad {key}");

    private static string Req(JsonElement o, string key, Func<JsonElement, string?> get) =>
        o.TryGetProperty(key, out var v) && get(v) is { } r ? r : throw new DecodeException($"missing or bad {key}");

    private static T Opt<T>(JsonElement o, string key, Func<JsonElement, T?> get, T fallback) where T : struct =>
        o.TryGetProperty(key, out var v) && get(v) is { } r ? r : fallback;

    private static string? AsString(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    /// <summary>NSNumber.intValue: integral JSON numbers, or a double truncated toward zero.</summary>
    private static int? AsInt(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Number) return null;
        if (e.TryGetInt32(out var i)) return i;
        return e.TryGetDouble(out var d) ? (int)d : null;
    }

    private static long? AsLong(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Number) return null;
        if (e.TryGetInt64(out var l)) return l;
        return e.TryGetDouble(out var d) ? (long)d : null;
    }

    /// <summary><c>Float(number.doubleValue)</c>: the nearest float to the JSON double.</summary>
    private static float? AsFloat(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) ? (float)d : null;

    /// <summary>The shortest text that reads back as the same double (what org.json and Swift write).</summary>
    public static string FormatDouble(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}
