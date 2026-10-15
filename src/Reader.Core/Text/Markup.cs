using System.Xml;
using System.Xml.Linq;

namespace Reader.Core.Text;

/// <summary>
/// A tiny read-only DOM for EPUB files. Well-formed XHTML (the norm in EPUBs) parses as XML;
/// anything else falls back to <see cref="LenientHtml"/>. External entities and DTDs are never loaded.
/// </summary>
internal static class Markup
{
    internal abstract class Node;

    internal sealed class TextNode(string text) : Node
    {
        public string Text { get; } = text;
    }

    internal sealed class Element(string name, Dictionary<string, string> attributes, List<Node> children) : Node
    {
        /// <summary>Lowercased local name ("dc:title" → "title").</summary>
        public string Name { get; } = name;
        /// <summary>
        /// By lowercased qualified name ("xlink:href") and local name ("href" for a namespaced
        /// attribute only if no plain one exists).
        /// </summary>
        public Dictionary<string, string> Attributes { get; } = attributes;
        public List<Node> Children { get; } = children;

        public string Attr(string name) => Attributes.TryGetValue(name, out var v) ? v : "";

        public IEnumerable<Element> Elements => Children.OfType<Element>();

        /// <summary>Direct element children called <paramref name="name"/>.</summary>
        public List<Element> ChildrenNamed(string name) => Elements.Where(e => e.Name == name).ToList();

        /// <summary>This element or the first descendant called <paramref name="name"/>, depth first.</summary>
        public Element? First(string name)
        {
            if (Name == name) return this;
            foreach (var e in Elements)
            {
                if (e.First(name) is { } hit) return hit;
            }
            return null;
        }

        public string Text => string.Concat(Children.Select(n => n switch
        {
            TextNode t => t.Text,
            Element e => e.Text,
            _ => "",
        }));
    }

    public static Element? Parse(byte[] data, bool html)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CheckCharacters = false,
            };
            using var stream = new MemoryStream(data);
            using var reader = XmlReader.Create(stream, settings);
            var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            if (doc.Root is { } root) return Convert(root);
        }
        catch (XmlException)
        {
            // Not well-formed (HTML entities like &nbsp;, unclosed <p> or <br>): parse leniently.
        }
        return LenientHtml.Parse(DecodeUtf8(data));
    }

    internal static string DecodeUtf8(byte[] data)
    {
        var start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        return System.Text.Encoding.UTF8.GetString(data, start, data.Length - start);
    }

    private static Element Convert(XElement el)
    {
        var attrs = new Dictionary<string, string>();
        foreach (var a in el.Attributes())
        {
            if (a.IsNamespaceDeclaration) continue;
            var prefix = a.Name.Namespace == XNamespace.None ? null : el.GetPrefixOfNamespace(a.Name.Namespace)
                ?? (a.Name.Namespace == XNamespace.Xml ? "xml" : null);
            var qualified = prefix is null ? a.Name.LocalName : $"{prefix}:{a.Name.LocalName}";
            attrs[qualified.ToLowerInvariant()] = a.Value;
        }
        foreach (var a in el.Attributes())
        {
            if (a.IsNamespaceDeclaration) continue;
            var local = a.Name.LocalName.ToLowerInvariant();
            attrs.TryAdd(local, a.Value);
        }
        var children = new List<Node>();
        foreach (var node in el.Nodes())
        {
            switch (node)
            {
                case XElement e: children.Add(Convert(e)); break;
                case XText t: children.Add(new TextNode(t.Value)); break; // includes CDATA
            }
        }
        return new Element(el.Name.LocalName.ToLowerInvariant(), attrs, children);
    }
}
