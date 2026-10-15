using System.Globalization;
using System.Text;

namespace Reader.Core.Text;

/// <summary>
/// A forgiving HTML parser for EPUB chapters that aren't well-formed XML (a port of the Mac app's
/// LenientHTML.swift). It follows the HTML rules that matter for reading text: void elements,
/// <c>&lt;p&gt;</c> closed by the next block, <c>&lt;li&gt;</c> closed by the next item, stray end
/// tags ignored, named and numeric character references.
/// </summary>
internal static class LenientHtml
{
    private static readonly HashSet<string> VoidTags =
    [
        "area", "base", "br", "col", "embed", "hr", "img", "image", "input", "link", "meta", "param",
        "source", "track", "wbr",
    ];
    private static readonly HashSet<string> RawTextTags = ["script", "style", "title", "textarea"];
    /// <summary>Opening one of these closes an open <c>&lt;p&gt;</c>.</summary>
    private static readonly HashSet<string> ClosesParagraph =
        XhtmlBlocks.BlockTags.Except(["body", "li", "td", "th", "tr", "dd", "dt"]).ToHashSet();

    private sealed class Builder(string name, Dictionary<string, string> attributes)
    {
        public readonly string Name = name;
        public readonly Dictionary<string, string> Attributes = attributes;
        public List<Markup.Node> Children = new();
        public Markup.Element Build() => new(Name, Attributes, Children);
    }

    private static bool IsSpace(int c) => c is 0x0A or 0x0B or 0x0C or 0x0D or 0x85 || (c <= 0xFFFF && char.IsWhiteSpace((char)c)) ||
        (c <= 0x10FFFF && CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);

    private static bool IsLetter(int c) => CharUnicodeInfo.GetUnicodeCategory(c) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
        UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark or
        UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static string FromScalars(int[] s, int start, int end)
    {
        var sb = new StringBuilder(Math.Max(0, end - start));
        for (var k = start; k < end; k++) sb.Append(char.ConvertFromUtf32(s[k]));
        return sb.ToString();
    }

    public static Markup.Element Parse(string html)
    {
        var s = html.EnumerateRunes().Select(r => r.Value).ToArray();
        var i = 0;
        var stack = new List<Builder> { new("#root", new()) };
        var text = new StringBuilder();

        void FlushText()
        {
            if (text.Length > 0)
            {
                stack[^1].Children.Add(new Markup.TextNode(Decode(text.ToString())));
                text.Clear();
            }
        }
        void Close(string name)
        {
            var at = stack.FindLastIndex(b => b.Name == name);
            if (at <= 0) return;
            while (stack.Count > at)
            {
                var b = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                stack[^1].Children.Add(b.Build());
            }
        }
        bool Starts(string prefix, int j)
        {
            var p = prefix.EnumerateRunes().Select(r => r.Value).ToArray();
            if (j + p.Length > s.Length) return false;
            for (var k = 0; k < p.Length; k++)
            {
                if (Lower(s[j + k]) != Lower(p[k])) return false;
            }
            return true;
        }
        int SkipPast(string end, int j)
        {
            var k = j;
            while (k < s.Length && !Starts(end, k)) k += 1;
            return Math.Min(s.Length, k + end.EnumerateRunes().Count());
        }
        void AppendScalar(int c) => text.Append(char.ConvertFromUtf32(c));

        while (i < s.Length)
        {
            if (s[i] != '<' || i + 1 >= s.Length)
            {
                AppendScalar(s[i]);
                i += 1;
                continue;
            }
            if (Starts("<!--", i)) { FlushText(); i = SkipPast("-->", i + 4); continue; }
            if (Starts("<![CDATA[", i))
            {
                var start = i + 9;
                var end = SkipPast("]]>", start);
                for (var k = start; k < Math.Max(start, end - 3); k++) AppendScalar(s[k]);
                i = end;
                continue;
            }
            if (s[i + 1] == '!' || s[i + 1] == '?') { FlushText(); i = SkipPast(">", i); continue; }
            var closing = s[i + 1] == '/';
            var j = i + (closing ? 2 : 1);
            var nameSb = new StringBuilder();
            while (j < s.Length && !IsSpace(s[j]) && s[j] != '>' && s[j] != '/')
            {
                nameSb.Append(char.ConvertFromUtf32(s[j]));
                j += 1;
            }
            var name = nameSb.ToString();
            if (name.Length == 0 || !IsLetter(name.EnumerateRunes().First().Value))
            {
                AppendScalar(s[i]); // a literal "<"
                i += 1;
                continue;
            }
            name = LocalName(name);
            var attrs = new Dictionary<string, string>();
            var selfClosing = false;
            // Attributes: name, name=value, name="value", name='value'.
            while (j < s.Length && s[j] != '>')
            {
                if (s[j] == '/') { selfClosing = true; j += 1; continue; }
                if (IsSpace(s[j])) { j += 1; continue; }
                var key = new StringBuilder();
                while (j < s.Length && !IsSpace(s[j]) && s[j] != '=' && s[j] != '>' && s[j] != '/')
                {
                    key.Append(char.ConvertFromUtf32(s[j]));
                    j += 1;
                }
                while (j < s.Length && IsSpace(s[j])) j += 1;
                var value = new StringBuilder();
                if (j < s.Length && s[j] == '=')
                {
                    j += 1;
                    while (j < s.Length && IsSpace(s[j])) j += 1;
                    if (j < s.Length && (s[j] == '"' || s[j] == '\''))
                    {
                        var q = s[j];
                        j += 1;
                        while (j < s.Length && s[j] != q) { value.Append(char.ConvertFromUtf32(s[j])); j += 1; }
                        j += 1;
                    }
                    else
                    {
                        while (j < s.Length && !IsSpace(s[j]) && s[j] != '>')
                        {
                            value.Append(char.ConvertFromUtf32(s[j]));
                            j += 1;
                        }
                    }
                }
                if (key.Length > 0)
                {
                    selfClosing = false;
                    var k = key.ToString().ToLowerInvariant();
                    attrs[k] = Decode(value.ToString());
                    var local = LocalName(k);
                    attrs.TryAdd(local, Decode(value.ToString()));
                }
            }
            i = Math.Min(s.Length, j + 1);
            FlushText();

            if (closing)
            {
                Close(name);
                continue;
            }
            if (ClosesParagraph.Contains(name) && stack[^1].Name == "p") Close("p");
            if (name == "p" && stack[^1].Name == "p") Close("p");
            if (name == "li")
            {
                var li = stack.FindLastIndex(b => b.Name == "li");
                if (li >= 0 && !stack.Skip(li).Any(b => b.Name is "ul" or "ol")) Close("li");
            }
            var element = new Builder(name, attrs);
            if (VoidTags.Contains(name) || selfClosing)
            {
                stack[^1].Children.Add(element.Build());
            }
            else if (RawTextTags.Contains(name))
            {
                // Skip to the matching end tag; contents aren't markup.
                var start = i;
                var k = i;
                while (k < s.Length && !Starts("</" + name, k)) k += 1;
                var raw = FromScalars(s, start, k);
                element.Children = [new Markup.TextNode(name is "title" or "textarea" ? Decode(raw) : raw)];
                stack[^1].Children.Add(element.Build());
                i = SkipPast(">", k);
            }
            else
            {
                stack.Add(element);
            }
        }
        FlushText();
        while (stack.Count > 1)
        {
            var b = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            stack[^1].Children.Add(b.Build());
        }
        var root = stack[0].Build();
        return root.First("html") ?? new Markup.Element("html", new(), root.Children);
    }

    private static int Lower(int c) => c <= 0xFFFF ? char.ToLowerInvariant((char)c) : c;

    private static string LocalName(string qualified)
    {
        var lower = qualified.ToLowerInvariant();
        var parts = lower.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : lower;
    }

    /// <summary>Replaces <c>&amp;name;</c>, <c>&amp;#123;</c> and <c>&amp;#x7B;</c> with their characters; unknown ones are kept.</summary>
    internal static string Decode(string s)
    {
        if (!s.Contains('&')) return s;
        var u = s.EnumerateRunes().Select(r => r.Value).ToArray();
        var output = new StringBuilder(s.Length);
        var i = 0;
        while (i < u.Length)
        {
            var semi = -1;
            if (u[i] == '&')
            {
                for (var k = i + 1; k < Math.Min(u.Length, i + 34); k++)
                {
                    if (u[k] == ';') { semi = k; break; }
                }
            }
            if (semi < 0)
            {
                output.Append(char.ConvertFromUtf32(u[i]));
                i += 1;
                continue;
            }
            var reference = FromScalars(u, i + 1, semi);
            int? scalar = null;
            if (reference.StartsWith("#x") || reference.StartsWith("#X"))
            {
                if (uint.TryParse(reference.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v)) scalar = Scalar(v);
            }
            else if (reference.StartsWith('#'))
            {
                var digits = reference.AsSpan(1);
                if (digits.Length > 0 && digits[0] == '+') digits = digits[1..];
                if (digits.Length > 0 && uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) scalar = Scalar(v);
            }
            else if (HtmlEntities.Map.TryGetValue(reference, out var named))
            {
                scalar = named;
            }
            if (scalar is { } sc)
            {
                output.Append(char.ConvertFromUtf32(sc));
                i = semi + 1;
            }
            else
            {
                output.Append(char.ConvertFromUtf32(u[i]));
                i += 1;
            }
        }
        return output.ToString();
    }

    /// <summary>A valid Unicode scalar value, or null (surrogates and values past U+10FFFF), like Swift's <c>UnicodeScalar(UInt32)</c>.</summary>
    private static int? Scalar(uint v) => v <= 0x10FFFF && v is < 0xD800 or > 0xDFFF ? (int)v : null;
}
