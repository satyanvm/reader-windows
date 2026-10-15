using System.Text.Json;
using Reader.Core.Model;
using Xunit;
using static Reader.Core.Tests.T;

namespace Reader.Core.Tests;

/// <summary>
/// The Windows, Mac and Android apps must mean exactly the same thing by a position, and agree on
/// which of two positions wins.
/// </summary>
public class InteropTests
{
    /// <summary>
    /// What Android's <c>HttpSyncApi.toJson</c> (org.json) produces for page 186, 40% down:
    /// floats are widened to double, so 0.4f is written as 0.4000000059604645.
    /// </summary>
    private const string AndroidJson =
        "{\"zoom\":1.5,\"pageOffset\":0.4000000059604645,\"horizontalOffset\":0.25,\"pageCount\":500," +
        "\"documentId\":\"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08\",\"hlc\":1760000000123," +
        "\"deviceId\":\"3f1c2a9e-0b1d-4c55-9a77-2f7d1c0e8b11\",\"pageIndex\":186,\"schemaVersion\":1}";

    private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

    [Fact]
    public void DecodesAnAndroidPositionBitForBit()
    {
        var p = PositionJson.Decode(AndroidJson);
        Assert.Equal("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", p.DocumentId);
        Assert.Equal(186, p.PageIndex);
        Assert.Equal(Bits(0.4f), Bits(p.PageOffset));
        Assert.Equal(0.25f, p.HorizontalOffset);
        Assert.Equal(1.5f, p.Zoom);
        Assert.Equal(500, p.PageCount);
        Assert.Equal(new Stamp(1_760_000_000_123, "3f1c2a9e-0b1d-4c55-9a77-2f7d1c0e8b11"), p.Stamp);
        Assert.Equal(1, p.SchemaVersion);
    }

    [Fact]
    public void EncodesWhatAndroidWouldAndRoundTrips()
    {
        var p = PositionJson.Decode(AndroidJson);
        var data = PositionJson.Encode(p);
        using var mine = JsonDocument.Parse(data);
        using var theirs = JsonDocument.Parse(AndroidJson);
        // Same keys and numerically identical values.
        var a = mine.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetRawText());
        var b = theirs.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetRawText());
        Assert.Equal(b.OrderBy(x => x.Key), a.OrderBy(x => x.Key));
        Assert.Contains("\"pageOffset\":0.4000000059604645", System.Text.Encoding.UTF8.GetString(data));
        Assert.Equal(p, PositionJson.Decode(data));
    }

    [Fact]
    public void OptionalFieldsDefaultLikeAndroid()
    {
        const string minimal = """{"documentId":"d","pageIndex":3,"pageOffset":0.5,"pageCount":10,"hlc":7,"deviceId":"a"}""";
        var p = PositionJson.Decode(minimal);
        Assert.Equal(0f, p.HorizontalOffset);
        Assert.Equal(1f, p.Zoom);
        Assert.Equal(1, p.SchemaVersion);
        Assert.ThrowsAny<Exception>(() => PositionJson.Decode("""{"pageIndex":3}"""));
    }

    [Fact]
    public void RandomFractionsSurviveAJsonRoundTripExactly()
    {
        var rng = new Random(1234);
        for (var i = 0; i < 1_000; i++)
        {
            var f = rng.NextSingle();
            var p = Pos(rng.Next(PAGES), f).WithStamp(new Stamp(rng.NextInt64(0, 1L << 50), "x"));
            var back = PositionJson.Decode(PositionJson.Encode(p));
            Assert.Equal(Bits(f), Bits(back.PageOffset));
            Assert.Equal(p, back);
        }
    }

    /// <summary>
    /// Kotlin compares device ids by UTF-16 code units. .NET's culture-aware comparison doesn't,
    /// so <see cref="Stamp"/> must use ordinal (UTF-16) order to pick the same winner.
    /// </summary>
    [Fact]
    public void StampTieBreakMatchesKotlinStringOrder()
    {
        var bmpHigh = new Stamp(5, "�");
        var astral = new Stamp(5, "\U0001F600"); // UTF-16 D83D DE00 < FFFD
        Assert.True(astral < bmpHigh);
        Assert.True(new Stamp(5, "a") < new Stamp(5, "b"));
        Assert.True(new Stamp(4, "z") < new Stamp(5, "a")); // clock first
        Assert.True(new Stamp(5, "B") < new Stamp(5, "a")); // case-sensitive, like Kotlin
    }

    [Fact]
    public void PositionsAreClampedOrRejectedForTheDocument()
    {
        var p = Pos(600, 1.5f, zoom: 9) with { PageCount = 700 };
        Assert.Null(p.ValidFor(500)); // different page count: a different edition
        p = p with { PageCount = PAGES };
        var v = p.ValidFor(PAGES);
        Assert.Equal(PAGES - 1, v?.PageIndex);
        Assert.Equal(1f, v?.PageOffset);
        Assert.Equal(4f, v?.Zoom);
    }
}
