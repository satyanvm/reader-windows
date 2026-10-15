using System.Globalization;
using System.Text;

namespace Reader.Core.Text;

/// <summary>The handful of Swift <c>String</c>/<c>Character</c> operations the text pipeline relies on.</summary>
public static class StringExtensions
{
    /// <summary>All characters are whitespace (an empty string is blank).</summary>
    public static bool IsBlank(this string s)
    {
        foreach (var c in s) if (!char.IsWhiteSpace(c)) return false;
        return true;
    }

    public static string TrimLeadingWhitespace(this string s)
    {
        var i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i == 0 ? s : s[i..];
    }

    public static string TrimTrailingWhitespace(this string s)
    {
        var end = s.Length;
        while (end > 0 && char.IsWhiteSpace(s[end - 1])) end--;
        return end == s.Length ? s : s[..end];
    }

    /// <summary>Replaces each run of characters from <paramref name="set"/> with <paramref name="replacement"/>.</summary>
    public static string Collapsing(this string s, string set, string replacement)
    {
        var sb = new StringBuilder(s.Length);
        var inRun = false;
        foreach (var c in s)
        {
            if (set.Contains(c))
            {
                if (!inRun) sb.Append(replacement);
                inRun = true;
            }
            else
            {
                sb.Append(c);
                inRun = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>Swift's <c>String.count</c>: user-perceived characters (grapheme clusters).</summary>
    public static int GraphemeCount(this string s) => new StringInfo(s).LengthInTextElements;

    public static Rune? FirstRune(this string s)
    {
        foreach (var r in s.EnumerateRunes()) return r;
        return null;
    }

    public static Rune? LastRune(this string s)
    {
        if (s.Length == 0) return null;
        return Rune.TryGetRuneAt(s, s.Length - 1, out var r) ? r
            : s.Length >= 2 && Rune.TryGetRuneAt(s, s.Length - 2, out r) ? r : null;
    }

    /// <summary>Java's <c>\s</c>: space, tab, newline, vertical tab, form feed, carriage return.</summary>
    public const string JavaWhitespace = " \t\n\u000B\u000C\r";
}
