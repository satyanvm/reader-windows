using System.Globalization;
using System.Text;

namespace Reader.Core.Text.Hyphenation;

/// <summary>
/// Liang's hyphenation (as in TeX) with the US English patterns, used to put soft hyphens
/// (U+00AD) into flowing text so justified lines can break inside long words. WPF's public
/// TextFormatter breaks at soft hyphens and draws a hyphen there; it has no hyphenation of its own.
/// At least 2 letters stay before a break and 3 after it, as TeX does for English.
/// </summary>
public sealed class Hyphenator
{
    public const char SoftHyphen = '­';
    private const int LeftMin = 2;
    private const int RightMin = 3;
    private const int MinWord = 6;

    private readonly Dictionary<string, byte[]> _patterns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> _exceptions = new(StringComparer.Ordinal);
    private readonly int _maxPattern;

    private static readonly Lazy<Hyphenator> English = new(() => FromResources("hyph-en-us.pat.txt", "hyph-en-us.hyp.txt"));

    public static Hyphenator EnglishUS => English.Value;

    public Hyphenator(IEnumerable<string> patterns, IEnumerable<string> exceptions)
    {
        foreach (var raw in patterns)
        {
            var p = raw.Trim();
            if (p.Length == 0 || p.StartsWith('%')) continue;
            var letters = new StringBuilder();
            var values = new List<byte> { 0 };
            foreach (var c in p)
            {
                if (c is >= '0' and <= '9') values[^1] = (byte)(c - '0');
                else
                {
                    letters.Append(c);
                    values.Add(0);
                }
            }
            var key = letters.ToString();
            _patterns[key] = values.ToArray();
            _maxPattern = Math.Max(_maxPattern, key.Length);
        }
        foreach (var raw in exceptions)
        {
            var e = raw.Trim();
            if (e.Length == 0 || e.StartsWith('%')) continue;
            var word = e.Replace("-", "");
            var breaks = new List<int>();
            var n = 0;
            foreach (var c in e)
            {
                if (c == '-') breaks.Add(n);
                else n++;
            }
            _exceptions[word.ToLowerInvariant()] = breaks.ToArray();
        }
    }

    public static Hyphenator FromResources(string patterns, string exceptions)
    {
        static IEnumerable<string> Lines(string name)
        {
            using var s = typeof(Hyphenator).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"missing resource {name}");
            using var r = new StreamReader(s);
            return r.ReadToEnd().Split('\n');
        }
        return new Hyphenator(Lines(patterns), Lines(exceptions));
    }

    /// <summary>Where <paramref name="word"/> may break: indexes of the letters a hyphen goes before.</summary>
    public int[] Breaks(string word)
    {
        if (word.Length < MinWord) return [];
        var lower = word.ToLowerInvariant();
        if (_exceptions.TryGetValue(lower, out var known)) return known;
        var dotted = "." + lower + ".";
        var points = new byte[dotted.Length + 1];
        for (var i = 0; i < dotted.Length; i++)
        {
            for (var len = 1; len <= _maxPattern && i + len <= dotted.Length; len++)
            {
                if (!_patterns.TryGetValue(dotted.Substring(i, len), out var values)) continue;
                for (var k = 0; k < values.Length; k++)
                {
                    if (values[k] > points[i + k]) points[i + k] = values[k];
                }
            }
        }
        var breaks = new List<int>();
        // points[i + 1] is the value before letter i of the word (after the leading dot).
        for (var i = LeftMin; i <= word.Length - RightMin; i++)
        {
            if (points[i + 1] % 2 == 1) breaks.Add(i);
        }
        return breaks.ToArray();
    }

    /// <summary>The word with soft hyphens at its break points.</summary>
    public string Hyphenate(string word)
    {
        var breaks = Breaks(word);
        if (breaks.Length == 0) return word;
        var sb = new StringBuilder(word.Length + breaks.Length);
        var b = 0;
        for (var i = 0; i < word.Length; i++)
        {
            if (b < breaks.Length && breaks[b] == i)
            {
                sb.Append(SoftHyphen);
                b++;
            }
            sb.Append(word[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Soft hyphens added to every plain ASCII-letter word of <paramref name="text"/>.
    /// <paramref name="map"/>[i] is where character i of the input ended up in the output
    /// (with one extra entry for the end), so positions in the original text can be found again.
    /// </summary>
    public string InsertSoftHyphens(string text, out int[] map)
    {
        map = new int[text.Length + 1];
        var sb = new StringBuilder(text.Length + text.Length / 6);
        var i = 0;
        while (i < text.Length)
        {
            if (!IsWordLetter(text[i]))
            {
                map[i] = sb.Length;
                sb.Append(text[i]);
                i++;
                continue;
            }
            var start = i;
            while (i < text.Length && IsWordLetter(text[i])) i++;
            var word = text[start..i];
            var breaks = word.Length >= MinWord && IsPlainWord(word) ? Breaks(word) : [];
            var b = 0;
            for (var k = 0; k < word.Length; k++)
            {
                if (b < breaks.Length && breaks[b] == k)
                {
                    sb.Append(SoftHyphen);
                    b++;
                }
                map[start + k] = sb.Length;
                sb.Append(word[k]);
            }
        }
        map[text.Length] = sb.Length;
        return sb.ToString();
    }

    private static bool IsWordLetter(char c) => char.IsLetter(c);

    /// <summary>Only English-looking words are hyphenated with English patterns.</summary>
    private static bool IsPlainWord(string w)
    {
        foreach (var c in w) if (c > 'z' || !char.IsAsciiLetter(c)) return false;
        return true;
    }
}
