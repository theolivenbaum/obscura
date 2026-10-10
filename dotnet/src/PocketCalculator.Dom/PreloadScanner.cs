namespace PocketCalculator.Dom;

/// <summary>An external classic script the preload scanner found.</summary>
/// <param name="Src">The src attribute.</param>
/// <param name="ReferrerPolicy">The referrerpolicy attribute.</param>
/// <param name="MetaReferrer">The content of the last <c>&lt;meta name=referrer&gt;</c> before the script.</param>
public sealed record PreloadScript(string Src, string? ReferrerPolicy, string? MetaReferrer);

/// <summary>What <see cref="PreloadScanner.Scan"/> found: the first base URL, and the scripts in order.</summary>
public sealed record PreloadScan(string? BaseHref, IReadOnlyList<PreloadScript> Scripts);

/// <summary>
/// A preload scanner: the external classic scripts of a whole HTML response, found without
/// building a tree, so their fetches start before the parser, which runs scripts as it goes,
/// reaches them.
/// </summary>
/// <remarks>
/// Like Chromium's, it is a speculation: it reads tags, skips comments and the text of raw
/// text elements, and ignores template contents, but does not run the tree builder, so markup
/// the tree builder would treat differently (a script in foreign content, say) may be fetched
/// for nothing. A fetch the parser never asks for is simply unused. Port addition: the Rust
/// engine fetches the scripts of a document it has already parsed whole.
/// </remarks>
public static class PreloadScanner
{
    /// <summary>At most this many scripts are reported.</summary>
    public const int MaxScripts = 512;

    public static PreloadScan Scan(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        List<PreloadScript> scripts = [];
        string? baseHref = null;
        string? metaReferrer = null;
        var templateDepth = 0;
        var i = 0;
        var attrs = new List<(string Name, string Value)>();
        while (i < html.Length && scripts.Count < MaxScripts)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= html.Length)
            {
                break;
            }

            var next = html[lt + 1];
            if (next == '!')
            {
                if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
                {
                    var close = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    i = close < 0 ? html.Length : close + 3;
                }
                else
                {
                    var close = html.IndexOf('>', lt + 2);
                    i = close < 0 ? html.Length : close + 1;
                }

                continue;
            }

            if (next == '/')
            {
                var nameStart = lt + 2;
                var nameEnd = NameEnd(html, nameStart);
                if (nameEnd > nameStart && html.AsSpan(nameStart, nameEnd - nameStart).Equals("template", StringComparison.OrdinalIgnoreCase))
                {
                    templateDepth = Math.Max(0, templateDepth - 1);
                }

                var close = html.IndexOf('>', lt + 2);
                i = close < 0 ? html.Length : close + 1;
                continue;
            }

            if (!char.IsAsciiLetter(next))
            {
                i = lt + 1;
                continue;
            }

            var start = lt + 1;
            var end = NameEnd(html, start);
            var name = html.Substring(start, end - start).ToLowerInvariant();
            attrs.Clear();
            var tagEnd = ReadAttributes(html, end, attrs);
            i = tagEnd;
            switch (name)
            {
                case "script":
                {
                    if (templateDepth == 0 && Get(attrs, "src") is { Length: > 0 } src && IsClassic(attrs))
                    {
                        scripts.Add(new PreloadScript(src.Trim(), Get(attrs, "referrerpolicy"), metaReferrer));
                    }

                    i = SkipRawText(html, tagEnd, "script");
                    break;
                }

                case "style" or "textarea" or "title" or "xmp" or "iframe" or "noembed" or "noframes" or "noscript":
                    i = SkipRawText(html, tagEnd, name);
                    break;
                case "plaintext":
                    i = html.Length;
                    break;
                case "template":
                    templateDepth++;
                    break;
                case "meta":
                    if (templateDepth == 0
                        && string.Equals(Get(attrs, "name")?.Trim(), "referrer", StringComparison.OrdinalIgnoreCase)
                        && Get(attrs, "content") is { } content)
                    {
                        metaReferrer = content;
                    }

                    break;
                case "base":
                    if (baseHref is null && templateDepth == 0 && Get(attrs, "href") is { } href)
                    {
                        baseHref = href.Trim();
                    }

                    break;
            }
        }

        return new PreloadScan(baseHref, scripts);
    }

    private static bool IsClassic(List<(string Name, string Value)> attrs)
    {
        if (Get(attrs, "nomodule") is not null)
        {
            return false;
        }

        var type = Get(attrs, "type")?.Trim();
        return type is null or ""
            || type.Equals("text/javascript", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/javascript", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Get(List<(string Name, string Value)> attrs, string name)
    {
        foreach (var (attrName, value) in attrs)
        {
            if (string.Equals(attrName, name, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    private static int NameEnd(string html, int start)
    {
        var i = start;
        while (i < html.Length && html[i] is not (' ' or '\t' or '\n' or '\r' or '\f' or '/' or '>'))
        {
            i++;
        }

        return i;
    }

    /// <summary>Read a start tag's attributes from <paramref name="i"/>; returns the index after its <c>&gt;</c>.</summary>
    private static int ReadAttributes(string html, int i, List<(string Name, string Value)> attrs)
    {
        while (i < html.Length)
        {
            var c = html[i];
            if (c == '>')
            {
                return i + 1;
            }

            if (c is ' ' or '\t' or '\n' or '\r' or '\f' or '/')
            {
                i++;
                continue;
            }

            var nameStart = i;
            while (i < html.Length && html[i] is not (' ' or '\t' or '\n' or '\r' or '\f' or '/' or '>' or '='))
            {
                i++;
            }

            var attrName = html.Substring(nameStart, i - nameStart).ToLowerInvariant();
            while (i < html.Length && html[i] is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                i++;
            }

            var value = string.Empty;
            if (i < html.Length && html[i] == '=')
            {
                i++;
                while (i < html.Length && html[i] is ' ' or '\t' or '\n' or '\r' or '\f')
                {
                    i++;
                }

                if (i < html.Length && html[i] is '"' or '\'')
                {
                    var quote = html[i];
                    var close = html.IndexOf(quote, i + 1);
                    if (close < 0)
                    {
                        return html.Length;
                    }

                    value = html.Substring(i + 1, close - i - 1);
                    i = close + 1;
                }
                else
                {
                    var valueStart = i;
                    while (i < html.Length && html[i] is not (' ' or '\t' or '\n' or '\r' or '\f' or '>'))
                    {
                        i++;
                    }

                    value = html.Substring(valueStart, i - valueStart);
                }
            }

            if (attrs.Count < 64)
            {
                attrs.Add((attrName, System.Net.WebUtility.HtmlDecode(value)));
            }
        }

        return html.Length;
    }

    private static int SkipRawText(string html, int from, string name)
    {
        var i = from;
        while (true)
        {
            var close = html.IndexOf("</", i, StringComparison.Ordinal);
            if (close < 0)
            {
                return html.Length;
            }

            if (close + 2 + name.Length <= html.Length
                && html.AsSpan(close + 2, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase)
                && (close + 2 + name.Length == html.Length || html[close + 2 + name.Length] is ' ' or '\t' or '\n' or '\r' or '\f' or '/' or '>'))
            {
                return close;
            }

            i = close + 2;
        }
    }
}
