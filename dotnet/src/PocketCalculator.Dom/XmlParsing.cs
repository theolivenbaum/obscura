using System.Text.RegularExpressions;
using System.Xml;

namespace PocketCalculator.Dom;

/// <summary>
/// XML parsing into the arena tree, for <c>DOMParser</c> with an XML type and
/// <c>XMLHttpRequest.responseXML</c>.
/// </summary>
/// <remarks>
/// Port addition: the Rust engine has no XML parser, and bootstrap.js parsed XML input as HTML
/// (so <c>&lt;a/&gt;</c> swallowed its following siblings and every name was lower-cased).
/// <see cref="XmlReader"/> is in-box managed code. A document that is not well-formed is
/// reported as Chromium (libxml2) reports it: a <c>&lt;parsererror&gt;</c> element in the XHTML
/// namespace as the first child of the root element parsed so far, or under a synthesized
/// <c>html</c>/<c>body</c> when no root element was reached. The message text is
/// <see cref="XmlReader"/>'s, not libxml2's. DTDs are parsed for their internal entities, never
/// fetched, and entity expansion is capped.
/// </remarks>
public static partial class XmlParsing
{
    private const string ParserErrorStyle =
        "display: block; white-space: pre; border: 2px solid #c77; padding: 0 1em 0 1em; margin: 1em; background-color: #fdd; color: black";

    /// <summary>Parse <paramref name="xml"/> into a new tree whose document holds the result.</summary>
    public static DomTree Parse(string xml, long contentByteBudget)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var tree = new DomTree { ContentByteBudget = contentByteBudget };
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            MaxCharactersFromEntities = 1L << 20,
            IgnoreWhitespace = false,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            CheckCharacters = true,
        };

        var open = new Stack<NodeId>();
        NodeId? root = null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read())
            {
                WorkCancellation.ThrowIfCancellationRequested();
                var parent = open.Count > 0 ? open.Peek() : tree.Document;
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var prefix = reader.Prefix.Length == 0 ? null : reader.Prefix;
                        var name = new QualName(prefix, reader.NamespaceURI, reader.LocalName);
                        var attrs = new List<Attribute>(reader.AttributeCount);
                        if (reader.MoveToFirstAttribute())
                        {
                            do
                            {
                                var attrPrefix = reader.Prefix.Length == 0 ? null : reader.Prefix;
                                attrs.Add(new Attribute(new QualName(attrPrefix, reader.NamespaceURI, reader.LocalName), reader.Value));
                            }
                            while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }

                        var element = tree.NewNode(NodeData.Element(name, attrs));
                        tree.AppendChild(parent, element);
                        root ??= element;
                        if (!reader.IsEmptyElement)
                        {
                            open.Push(element);
                        }

                        break;
                    }

                    case XmlNodeType.EndElement:
                        if (open.Count > 0)
                        {
                            open.Pop();
                        }

                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.Whitespace:
                        // Whitespace outside the root element is not part of the DOM.
                        if (parent != tree.Document)
                        {
                            AppendText(tree, parent, reader.Value);
                        }

                        break;

                    case XmlNodeType.Comment:
                        tree.AppendChild(parent, tree.NewNode(NodeData.Comment(reader.Value)));
                        break;

                    case XmlNodeType.ProcessingInstruction:
                        tree.AppendChild(parent, tree.NewNode(NodeData.ProcessingInstruction(reader.Name, reader.Value)));
                        break;

                    case XmlNodeType.DocumentType:
                        tree.AppendChild(parent, tree.NewNode(NodeData.Doctype(
                            reader.Name,
                            reader.GetAttribute("PUBLIC") ?? string.Empty,
                            reader.GetAttribute("SYSTEM") ?? string.Empty)));
                        break;
                }
            }

            if (root is null)
            {
                throw new XmlException("Start tag expected, '<' not found", null, 1, 1);
            }
        }
        catch (XmlException e)
        {
            InsertParserError(tree, root, e);
        }

        return tree;
    }

    /// <summary>Adjacent text (a CDATA section next to text, say) is one text node in the tree.</summary>
    private static void AppendText(DomTree tree, NodeId parent, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (tree.GetNode(parent)?.LastChild is { } last && tree.GetNode(last)?.Data is TextData existing)
        {
            existing.Contents += text;
            return;
        }

        tree.AppendChild(parent, tree.NewNode(NodeData.Text(text)));
    }

    private static void InsertParserError(DomTree tree, NodeId? root, XmlException error)
    {
        var message = TrailingPosition().Replace(error.Message, string.Empty).TrimEnd();
        var line = Math.Max(1, error.LineNumber);
        var column = Math.Max(1, error.LinePosition);

        var parserError = tree.NewNode(NodeData.Element(QualName.Html("parsererror"),
            [new Attribute(QualName.Attr("style"), ParserErrorStyle)]));
        var heading = tree.NewNode(NodeData.Element(QualName.Html("h3")));
        tree.AppendChild(heading, tree.NewNode(NodeData.Text("This page contains the following errors:")));
        tree.AppendChild(parserError, heading);
        var detail = tree.NewNode(NodeData.Element(QualName.Html("div"),
            [new Attribute(QualName.Attr("style"), "font-family:monospace;font-size:12px")]));
        tree.AppendChild(detail, tree.NewNode(NodeData.Text($"error on line {line} at column {column}: {message}\n")));
        tree.AppendChild(parserError, detail);
        var footer = tree.NewNode(NodeData.Element(QualName.Html("h3")));
        tree.AppendChild(footer, tree.NewNode(NodeData.Text("Below is a rendering of the page up to the first error.")));
        tree.AppendChild(parserError, footer);

        if (root is { } rootId)
        {
            if (tree.GetNode(rootId)?.FirstChild is { } first)
            {
                tree.InsertBefore(first, parserError);
            }
            else
            {
                tree.AppendChild(rootId, parserError);
            }

            return;
        }

        foreach (var child in tree.Children(tree.Document))
        {
            tree.Remove(child);
        }

        var html = tree.NewNode(NodeData.Element(QualName.Html("html")));
        var body = tree.NewNode(NodeData.Element(QualName.Html("body")));
        tree.AppendChild(tree.Document, html);
        tree.AppendChild(html, body);
        tree.AppendChild(body, parserError);
    }

    [GeneratedRegex(@"\s*Line \d+, position \d+\.?\s*$")]
    private static partial Regex TrailingPosition();
}
