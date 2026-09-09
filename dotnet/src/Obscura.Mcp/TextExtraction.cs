using System.Globalization;
using System.Text;
using Obscura.Dom;

namespace Obscura.Mcp;

/// <summary>
/// The text helpers <c>lib.rs</c> keeps beside the tools: the DOM-to-text walk
/// <c>browser_snapshot</c> and <c>browser_search</c> read the page through, and
/// the truncation marker every text tool ends with.
/// </summary>
internal static class TextExtraction
{
    private static readonly char[] HtmlWhitespace = ['\t', '\n', '\f', '\r', ' '];

    /// <summary>The five HTML whitespace characters.</summary>
    private static bool IsHtmlWhitespace(char c) => c is '\t' or '\n' or '\f' or '\r' or ' ';

    /// <summary>
    /// Clamp text to <paramref name="maxChars"/> and tack on a
    /// <c>...(truncated, N more chars)</c> marker so the agent can ask for more if
    /// needed. Default ceiling is 4 KiB to prevent a single tool call from consuming
    /// a window of context.
    /// </summary>
    /// <remarks>
    /// Rust counts <c>char</c>s, i.e. Unicode scalar values, so the count is over
    /// runes and not UTF-16 code units; an astral character counts once, as it does
    /// in Rust.
    /// </remarks>
    internal static string Truncate(string text, int maxChars)
    {
        var total = CharCount(text);
        if (total <= maxChars)
        {
            return text;
        }

        var head = new StringBuilder();
        var taken = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (taken == maxChars)
            {
                break;
            }

            head.Append(rune.ToString());
            taken++;
        }

        var remaining = total - maxChars;
        return $"{head}\n...(truncated, {remaining.ToString(CultureInfo.InvariantCulture)} more chars)";
    }

    private static int CharCount(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    internal static void AppendTextSegment(StringBuilder result, ref bool pendingSpace, string contents)
    {
        var trimmed = contents.Trim(HtmlWhitespace);
        if (trimmed.Length == 0)
        {
            foreach (var c in contents)
            {
                if (IsHtmlWhitespace(c))
                {
                    pendingSpace = true;
                    break;
                }
            }

            return;
        }

        var beginsWithSpace = contents.Length > 0 && IsHtmlWhitespace(contents[0]);
        var resultEndsWithSpace = result.Length > 0 && char.IsWhiteSpace(result[^1]);
        if ((pendingSpace || beginsWithSpace) && result.Length > 0 && !resultEndsWithSpace)
        {
            result.Append(' ');
        }

        result.Append(trimmed);
        pendingSpace = contents.Length > 0 && IsHtmlWhitespace(contents[^1]);
    }

    private const int MaxNodes = 5_000_000;

    /// <summary>
    /// The readable text of a subtree: block elements break lines, script / style /
    /// noscript subtrees are dropped, and runs of HTML whitespace collapse to one
    /// space.
    /// </summary>
    internal static string ExtractText(DomTree dom, NodeId nodeId)
    {
        var result = new StringBuilder();
        var pendingSpace = false;
        var stack = new Stack<(NodeId Id, bool Newline)>();
        stack.Push((nodeId, false));
        var visited = 0;

        while (stack.Count > 0)
        {
            var (id, newline) = stack.Pop();
            if (newline)
            {
                result.Append('\n');
                pendingSpace = false;
                continue;
            }

            visited++;
            if (visited > MaxNodes)
            {
                break;
            }

            if (dom.GetNode(id) is not { } node)
            {
                continue;
            }

            switch (node.Data)
            {
                case TextData text:
                    AppendTextSegment(result, ref pendingSpace, text.Contents);
                    break;

                case ElementData element:
                {
                    var tag = element.Name.Local;
                    if (tag is "script" or "style" or "noscript")
                    {
                        continue;
                    }

                    var isBlock = tag is "div" or "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
                        or "li" or "tr" or "br" or "hr" or "section" or "article"
                        or "header" or "footer" or "nav" or "main" or "aside"
                        or "blockquote" or "pre" or "ul" or "ol" or "table";

                    if (isBlock)
                    {
                        result.Append('\n');
                        pendingSpace = false;
                        stack.Push((id, true));
                    }

                    PushChildrenReversed(dom, id, stack);
                    break;
                }

                default:
                    PushChildrenReversed(dom, id, stack);
                    break;
            }
        }

        return result.ToString();
    }

    private static void PushChildrenReversed(DomTree dom, NodeId id, Stack<(NodeId, bool)> stack)
    {
        var children = dom.Children(id);
        for (var i = children.Count - 1; i >= 0; i--)
        {
            stack.Push((children[i], false));
        }
    }

    /// <summary>
    /// Rust's <c>{:?}</c> for a string: double quotes, backslash escapes for the
    /// quote, the backslash itself and the usual control characters, and
    /// <c>\u{...}</c> for anything else non-printable.
    /// </summary>
    /// <remarks>
    /// <c>browser_interactive_elements</c> formats element labels with <c>{:?}</c>,
    /// so the escaping is part of the tool's output. The port escapes the C0/C1
    /// control blocks and DEL; Rust additionally escapes the wider non-printable
    /// Unicode categories (unassigned, format, private use), which do not occur in a
    /// label read out of a rendered page.
    /// </remarks>
    internal static string RustDebugString(string value)
    {
        var output = new StringBuilder(value.Length + 2);
        output.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                case '\0':
                    output.Append("\\0");
                    break;
                default:
                    if (c < 0x20 || (c >= 0x7F && c <= 0x9F))
                    {
                        output.Append("\\u{");
                        output.Append(((int)c).ToString("x", CultureInfo.InvariantCulture));
                        output.Append('}');
                    }
                    else
                    {
                        output.Append(c);
                    }

                    break;
            }
        }

        output.Append('"');
        return output.ToString();
    }

    /// <summary>Rust's <c>{:&lt;width$}</c>: left-aligned, space padded, never truncated.</summary>
    internal static string PadRight(string value, int width) =>
        value.Length >= width ? value : value + new string(' ', width - value.Length);
}
