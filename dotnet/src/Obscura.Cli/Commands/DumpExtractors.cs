using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Cli.Json;
using Obscura.Dom;
using Obscura.Js.Runtime;
using Obscura.Js.Url;
using Page = Obscura.Browser.Page;

namespace Obscura.Cli.Commands;

/// <summary>
/// The pure <c>--dump</c> extractors from <c>main.rs</c>.
/// </summary>
/// <remarks>
/// Everything here is a function of a <see cref="DomTree"/> (plus a base URL),
/// exactly as in the reference, so the unit tests can drive them from fixture
/// HTML without standing up a browser.
/// </remarks>
public static class DumpExtractors
{
    /// <summary>
    /// Selectors paired with the attribute whose URL is extracted and the asset
    /// kind surfaced. The order is stable so <c>--dump assets</c> output is
    /// deterministic across runs.
    /// </summary>
    public static readonly (string Selector, string Attribute, string Kind)[] AssetSelectors =
    [
        ("script[src]", "src", "script"),
        ("link[href]", "href", "link"),
        ("img[src]", "src", "image"),
        ("iframe[src]", "src", "iframe"),
        ("source[src]", "src", "media"),
        ("video[src]", "src", "video"),
        ("audio[src]", "src", "audio"),
        ("embed[src]", "src", "embed"),
        ("object[data]", "data", "object"),
    ];

    /// <summary>The five characters HTML calls whitespace.</summary>
    public static bool IsHtmlWhitespace(char c) =>
        c is '\t' or '\n' or '\f' or '\r' or ' ';

    private static readonly char[] HtmlWhitespace = ['\t', '\n', '\f', '\r', ' '];

    /// <summary>
    /// Append one text node's contents to the readable-text accumulator,
    /// collapsing runs of HTML whitespace into single spaces across nodes.
    /// </summary>
    public static void AppendReadableTextSegment(StringBuilder result, ref bool pendingSpace, string contents)
    {
        var trimmed = contents.AsSpan().Trim(HtmlWhitespace);
        if (trimmed.IsEmpty)
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

    /// <summary>Elements whose subtree is boilerplate rather than content.</summary>
    private static bool IsSkipped(string tag) =>
        tag is "script" or "style" or "nav" or "header" or "footer" or "aside";

    /// <summary>Elements that start and end a line in readable text.</summary>
    private static bool IsBlock(string tag) => tag is
        "div" or "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li" or "tr" or
        "br" or "hr" or "blockquote" or "pre" or "section" or "article" or "header" or
        "footer" or "nav" or "main" or "aside" or "figure" or "figcaption" or "table" or
        "thead" or "tbody" or "tfoot" or "dl" or "dt" or "dd" or "ul" or "ol";

    /// <summary>
    /// Defense-in-depth cap mirroring <c>DomTree.Descendants</c>; never reached
    /// on a valid tree, since append/insert reject cycles.
    /// </summary>
    private const int MaxNodes = 5_000_000;

    /// <summary>
    /// Readable text for a subtree, boilerplate elements dropped and block
    /// elements newline-delimited.
    /// </summary>
    /// <remarks>
    /// An iterative DFS over an explicit work stack. A recursive walk overflowed
    /// the call stack (a hard abort, not a catchable exception) on deeply nested
    /// pages, taking the process down on <c>--dump text</c>. The
    /// <c>Newline</c> work item emits a block element's trailing newline after
    /// its children, matching the pre/post-recursion output exactly.
    /// </remarks>
    public static string ExtractReadableText(DomTree dom, NodeId nodeId)
    {
        var result = new StringBuilder();
        var pendingSpace = false;
        // A null entry is the Newline work item; the stack is LIFO, so children
        // are pushed in reverse to pop in document order.
        var stack = new Stack<NodeId?>();
        stack.Push(nodeId);
        var visited = 0;

        while (stack.Count > 0)
        {
            var work = stack.Pop();
            if (work is not { } id)
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
                    AppendReadableTextSegment(result, ref pendingSpace, text.Contents);
                    break;
                case ElementData element:
                {
                    var tag = element.Name.Local;
                    if (IsSkipped(tag))
                    {
                        continue;
                    }
                    if (IsBlock(tag))
                    {
                        result.Append('\n');
                        pendingSpace = false;
                        stack.Push(null);
                    }
                    PushChildrenReversed(stack, dom, id);
                    break;
                }
                default:
                    PushChildrenReversed(stack, dom, id);
                    break;
            }
        }

        return result.ToString();
    }

    private static void PushChildrenReversed(Stack<NodeId?> stack, DomTree dom, NodeId id)
    {
        var children = dom.Children(id);
        for (var i = children.Count - 1; i >= 0; i--)
        {
            stack.Push(children[i]);
        }
    }

    /// <summary>
    /// Map a <c>&lt;link&gt;</c> element's <c>rel</c> token to a more specific
    /// asset kind. Unknown or missing <c>rel</c> falls back to the generic
    /// "link" so the caller still sees the URL.
    /// </summary>
    public static string LinkKindFromRel(string rel)
    {
        var first = FirstAsciiToken(rel).ToLowerInvariant();
        return first switch
        {
            "stylesheet" => "stylesheet",
            "icon" or "shortcut" => "icon",
            "manifest" => "manifest",
            "preload" => "preload",
            "prefetch" => "prefetch",
            "modulepreload" => "modulepreload",
            "dns-prefetch" => "dns-prefetch",
            "preconnect" => "preconnect",
            "alternate" => "alternate",
            _ => "link",
        };
    }

    /// <summary>Rust's <c>split_ascii_whitespace().next().unwrap_or("")</c>.</summary>
    private static string FirstAsciiToken(string value)
    {
        var start = 0;
        while (start < value.Length && IsAsciiWhitespace(value[start]))
        {
            start++;
        }
        var end = start;
        while (end < value.Length && !IsAsciiWhitespace(value[end]))
        {
            end++;
        }
        return value[start..end];
    }

    private static bool IsAsciiWhitespace(char c) =>
        c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';

    /// <summary>
    /// Resolve a raw <c>src</c>/<c>href</c>/<c>data</c> attribute against the
    /// page's base URL. Mirrors <see cref="DumpLinks"/> so <c>--dump assets</c>
    /// and <c>--dump links</c> agree on absolute-URL semantics.
    /// </summary>
    public static string? ResolveAssetUrl(string raw, UrlRecord? baseUrl)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }
        if (trimmed.StartsWith("http://", StringComparison.Ordinal)
            || trimmed.StartsWith("https://", StringComparison.Ordinal))
        {
            return trimmed;
        }
        if (baseUrl?.Join(trimmed) is { } joined)
        {
            return joined.Href;
        }
        return trimmed;
    }

    /// <summary>
    /// Walk the rendered DOM and emit one NDJSON line per discoverable
    /// sub-resource.
    /// </summary>
    public static string ExtractAssets(DomTree dom, UrlRecord? baseUrl)
    {
        var lines = new List<string>();
        foreach (var (selector, attribute, defaultKind) in AssetSelectors)
        {
            foreach (var nodeId in QueryAll(dom, selector))
            {
                if (dom.GetNode(nodeId) is not { } node)
                {
                    continue;
                }
                var raw = node.GetAttribute(attribute) ?? string.Empty;
                if (ResolveAssetUrl(raw, baseUrl) is not { } url)
                {
                    continue;
                }
                var kind = defaultKind == "link"
                    ? LinkKindFromRel(node.GetAttribute("rel") ?? string.Empty)
                    : defaultKind;
                lines.Add(SerdeJson.ToJson(new JsonObject
                {
                    ["url"] = JsonValue.Create(url),
                    ["type"] = JsonValue.Create(kind),
                }));
            }
        }
        return string.Join('\n', lines);
    }

    /// <summary><c>dump_html</c>.</summary>
    public static string DumpHtml(Page page) =>
        page.WithDom(dom =>
        {
            if (dom.TryQuerySelector("html", out var htmlNode, out _) && htmlNode is { } id)
            {
                return $"<!DOCTYPE html>\n{dom.OuterHtml(id)}";
            }
            return dom.InnerHtml(dom.Document);
        }) ?? string.Empty;

    /// <summary><c>dump_text</c>.</summary>
    public static string DumpText(Page page) =>
        page.WithDom(dom =>
            dom.TryQuerySelector("body", out var body, out _) && body is { } id
                ? ExtractReadableText(dom, id).Trim()
                : string.Empty) ?? string.Empty;

    /// <summary><c>dump_markdown</c>: the shared extraction script, run in the page.</summary>
    public static string DumpMarkdown(Page page)
    {
        var result = page.Evaluate(MarkdownScript.HtmlToMarkdown);
        return result?.GetValueKind() == JsonValueKind.String ? result.GetValue<string>() : string.Empty;
    }

    /// <summary><c>dump_links</c>.</summary>
    public static string DumpLinks(Page page)
    {
        var baseUrl = BaseUrl(page);
        return page.WithDom(dom =>
        {
            var rendered = new List<string>();
            foreach (var linkId in QueryAll(dom, "a"))
            {
                if (dom.GetNode(linkId) is not { } node)
                {
                    continue;
                }
                var href = node.GetAttribute("href") ?? string.Empty;
                var text = dom.TextContent(linkId).Trim();

                string fullUrl;
                if (href.StartsWith("http://", StringComparison.Ordinal)
                    || href.StartsWith("https://", StringComparison.Ordinal))
                {
                    fullUrl = href;
                }
                else if (baseUrl is not null)
                {
                    fullUrl = baseUrl.Join(href)?.Href ?? href;
                }
                else
                {
                    fullUrl = href;
                }

                if (fullUrl.Length != 0)
                {
                    rendered.Add(text.Length == 0 ? fullUrl : $"{fullUrl}\t{text}");
                }
            }
            return string.Join('\n', rendered);
        }) ?? string.Empty;
    }

    /// <summary><c>dump_assets</c>: static DOM references plus scripted fetches.</summary>
    public static string DumpAssets(Page page)
    {
        var baseUrl = BaseUrl(page);
        var ndjson = page.WithDom(dom => ExtractAssets(dom, baseUrl)) ?? string.Empty;

        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in ndjson.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }
            lines.Add(line);
            // URLs already listed from static DOM attributes, so a resource the
            // script fetches that the markup also references is not emitted twice.
            try
            {
                if (JsonNode.Parse(line) is JsonObject obj
                    && obj.TryGetPropertyValue("url", out var url)
                    && url?.GetValueKind() == JsonValueKind.String)
                {
                    seen.Add(url.GetValue<string>());
                }
            }
            catch (JsonException)
            {
                // A line that will not parse contributes no URL, as in Rust.
            }
        }

        // Resources pulled in by JS fetch()/XHR, which leave no static DOM tag.
        foreach (var url in page.FetchedUrls())
        {
            if (seen.Add(url))
            {
                lines.Add(SerdeJson.ToJson(new JsonObject
                {
                    ["url"] = JsonValue.Create(url),
                    ["type"] = JsonValue.Create("fetch"),
                }));
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary><c>dump_cookies</c>: every cookie in the jar, HttpOnly included.</summary>
    public static string DumpCookies(Page page)
    {
        var array = new JsonArray();
        foreach (var cookie in page.Context.CookieJar.GetAllCookies())
        {
            array.Add(new JsonObject
            {
                ["name"] = JsonValue.Create(cookie.Name),
                ["value"] = JsonValue.Create(cookie.Value),
                ["domain"] = JsonValue.Create(cookie.Domain),
                ["path"] = JsonValue.Create(cookie.Path),
                ["secure"] = JsonValue.Create(cookie.Secure),
                ["httpOnly"] = JsonValue.Create(cookie.HttpOnly),
                ["sameSite"] = JsonValue.Create(cookie.SameSite),
                ["expires"] = cookie.Expires is { } expires ? JsonValue.Create(expires) : null,
            });
        }
        return SerdeJson.ToJsonPretty(array);
    }

    /// <summary>
    /// The page URL as the <c>url</c> crate's port sees it, which is what
    /// <c>Url::join</c> resolves relative references against.
    /// </summary>
    internal static UrlRecord? BaseUrl(Page page) => page.Url;

    /// <summary>
    /// <c>query_selector_all(...).unwrap_or_default()</c>: an invalid selector
    /// yields no nodes rather than an error.
    /// </summary>
    private static List<NodeId> QueryAll(DomTree dom, string selector) =>
        dom.TryQuerySelectorAll(selector, out var nodes, out _) ? nodes : [];
}
