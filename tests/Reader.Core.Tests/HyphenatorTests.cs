using Reader.Core.Text.Hyphenation;
using Xunit;

namespace Reader.Core.Tests;

public class HyphenatorTests
{
    private static string Show(string word) => Hyphenator.EnglishUS.Hyphenate(word).Replace(Hyphenator.SoftHyphen, '-');

    [Theory]
    [InlineData("hyphenation", "hy-phen-ation")]
    [InlineData("philosophers", "philoso-phers")]
    [InlineData("circumstances", "cir-cum-stances")]
    [InlineData("table", "table")] // too short to break
    [InlineData("associate", "as-so-ciate")] // from the exception list
    [InlineData("Meditations", "Med-i-ta-tions")]
    public void BreaksWordsTheWayTeXDoes(string word, string expected) => Assert.Equal(expected, Show(word));

    [Fact]
    public void SoftHyphensGoIntoTextWithAMapBackToTheOriginal()
    {
        const string text = "Things are perplexed, café philosophers.";
        var output = Hyphenator.EnglishUS.InsertSoftHyphens(text, out var map);
        Assert.Equal(text, output.Replace(Hyphenator.SoftHyphen.ToString(), ""));
        Assert.Contains("per­plexed", output);
        Assert.Contains("café", output); // non-English words are left alone
        for (var i = 0; i < text.Length; i++) Assert.Equal(text[i], output[map[i]]);
        Assert.Equal(output.Length, map[^1]);
    }
}
