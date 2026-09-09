using Obscura.Dom;
using Obscura.Net;

namespace Obscura.Browser;

internal static partial class PageHelpers
{
    /// <summary>
    /// Discover linked author sheets in document order.
    /// </summary>
    /// <remarks>
    /// Media queries control whether a loaded sheet participates in the cascade; they
    /// do not suppress its fetch or <c>load</c> event. The index counts all
    /// stylesheet links so the materialization script addresses the same node.
    /// </remarks>
    internal static List<(int LinkIndex, string Href)> LinkedStylesheetRequests(DomTree dom)
    {
        ArgumentNullException.ThrowIfNull(dom);
        List<NodeId> linkIds = dom.TryQuerySelectorAll(
            "link[rel~=\"stylesheet\"]",
            out List<NodeId> found,
            out _) ? found : [];
        List<(int, string)> links = [];
        for (int linkIndex = 0; linkIndex < linkIds.Count; linkIndex++)
        {
            Node? node = dom.GetNode(linkIds[linkIndex]);
            if (node is null)
            {
                continue;
            }
            // Disabled alternate sheets stay dormant until script enables them.
            // Media-gated sheets are different: they still load.
            if (node.GetAttribute("disabled") is not null)
            {
                continue;
            }
            if (node.GetAttribute("href") is { } href)
            {
                links.Add((linkIndex, href));
            }
        }
        return links;
    }

    /// <summary>
    /// Discover fetchable <c>@import</c> rules in inline author sheets.
    /// </summary>
    /// <remarks>
    /// The source index excludes Obscura's own materialized sheets so it stays stable
    /// while imports are inserted before their source nodes.
    /// </remarks>
    internal static List<(int StyleIndex, StylesheetImport Import)> InlineStylesheetImportRequests(DomTree dom)
    {
        ArgumentNullException.ThrowIfNull(dom);
        List<NodeId> styleIds = dom.TryQuerySelectorAll("style", out List<NodeId> found, out _) ? found : [];
        List<(int, StylesheetImport)> imports = [];
        int authorIndex = 0;
        foreach (NodeId styleId in styleIds)
        {
            Node? node = dom.GetNode(styleId);
            if (node is null)
            {
                continue;
            }
            if (node.GetAttribute("data-obscura-external-stylesheets") is not null
                || node.GetAttribute("data-obscura-inline-import") is not null)
            {
                continue;
            }
            (List<StylesheetImport> styleImports, _) = SplitCssImports(dom.TextContent(styleId));
            foreach (StylesheetImport import in styleImports)
            {
                imports.Add((authorIndex, import));
            }
            authorIndex += 1;
        }
        return imports;
    }
}

public sealed partial class Page
{
    internal async Task<List<(AuthorStylesheetTarget Target, string Css)>> FetchStylesheetsAsync(
        CancellationToken cancellationToken)
    {
        if (Js is not { } js)
        {
            return [];
        }
        var discovered = js.WithDom(dom => (
            Links: PageHelpers.LinkedStylesheetRequests(dom),
            Imports: PageHelpers.InlineStylesheetImportRequests(dom)));
        List<(int LinkIndex, string Href)> allLinks = discovered.Links ?? [];
        List<(int StyleIndex, StylesheetImport Import)> inlineImports = discovered.Imports ?? [];

        if (Url is not { } documentUrl)
        {
            return [];
        }
        Uri documentBase = ResolveBaseUrl() ?? documentUrl;

        List<(AuthorStylesheetTarget Target, string Key, string? Media)> roots = [];
        HashSet<string> scheduled = new(StringComparer.Ordinal);
        List<(string Key, Uri Url, byte Depth)> pending = [];

        foreach ((int linkIndex, string href) in allLinks)
        {
            if (PageUrl.TryJoin(documentBase, href) is not { } joined)
            {
                continue;
            }
            (string key, Uri resolved) = PageHelpers.CanonicalStylesheetUrl(joined);
            if (!PageHelpers.SubresourceAllowed(documentUrl, resolved.AbsoluteUri))
            {
                continue;
            }
            if (ShouldBlockUrl(resolved.AbsoluteUri))
            {
                continue;
            }
            roots.Add((new AuthorStylesheetTarget.Linked(linkIndex), key, null));
            if (scheduled.Add(key) && scheduled.Count <= PageHelpers.MaxStylesheetResources)
            {
                pending.Add((key, resolved, 0));
            }
        }

        foreach ((int styleIndex, StylesheetImport import) in inlineImports)
        {
            if (PageUrl.TryJoin(documentBase, import.Url) is not { } joined)
            {
                continue;
            }
            (string key, Uri resolved) = PageHelpers.CanonicalStylesheetUrl(joined);
            if (!PageHelpers.SubresourceAllowed(documentUrl, resolved.AbsoluteUri)
                || ShouldBlockUrl(resolved.AbsoluteUri))
            {
                continue;
            }
            roots.Add((new AuthorStylesheetTarget.InlineImport(styleIndex), key, import.Media));
            if (scheduled.Add(key) && scheduled.Count <= PageHelpers.MaxStylesheetResources)
            {
                pending.Add((key, resolved, 1));
            }
        }

        Dictionary<string, LoadedStylesheet> sheets = new(StringComparer.Ordinal);
        Dictionary<string, string> aliases = new(StringComparer.Ordinal);
        while (pending.Count != 0)
        {
            List<(string Key, Uri Url, byte Depth)> batch = pending;
            pending = [];
            var factories = new List<Func<Task<(string Key, Uri Url, byte Depth, Response? Response)>>>(batch.Count);
            foreach ((string key, Uri requestedUrl, byte depth) in batch)
            {
                factories.Add(async () =>
                {
                    ResourceRequest request = ResourceRequest.Subresource(ResourceType.Stylesheet, documentUrl);
                    try
                    {
                        Response response = await HttpClient
                            .FetchResourceWithCallbacksAsync(requestedUrl, request, _callbacks, cancellationToken)
                            .ConfigureAwait(false);
                        return (key, requestedUrl, depth, (Response?)response);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        return (key, requestedUrl, depth, null);
                    }
                });
            }

            var results = await Buffered
                .AllAsync(factories, 16, cancellationToken)
                .ConfigureAwait(false);

            foreach ((string key, Uri _, byte depth, Response? maybeResponse) in results)
            {
                if (maybeResponse is not { } response)
                {
                    continue;
                }
                Uri responseUrl = response.Url;
                RecordNetworkEventWithBody(
                    responseUrl.AbsoluteUri,
                    "GET",
                    "Stylesheet",
                    response.Status,
                    response.Headers,
                    response.Body,
                    base64Encoded: false);

                (string responseKey, Uri canonicalResponseUrl) = PageHelpers.CanonicalStylesheetUrl(responseUrl);
                if (aliases.TryGetValue(responseKey, out string? existing))
                {
                    aliases[key] = existing;
                    continue;
                }
                string css = ContentEncoding.DecodeNonHtml(response.Body, response.ContentType());
                (List<StylesheetImport> imports, string rules) = PageHelpers.SplitCssImports(css);
                if (depth >= PageHelpers.MaxStylesheetImportDepth)
                {
                    imports = [];
                }
                aliases[key] = key;
                aliases[responseKey] = key;
                sheets[key] = new LoadedStylesheet(canonicalResponseUrl, imports, rules);

                if (depth >= PageHelpers.MaxStylesheetImportDepth)
                {
                    continue;
                }
                foreach (StylesheetImport import in imports)
                {
                    if (PageUrl.TryJoin(canonicalResponseUrl, import.Url) is not { } joined)
                    {
                        continue;
                    }
                    (string importKey, Uri importUrl) = PageHelpers.CanonicalStylesheetUrl(joined);
                    if (aliases.ContainsKey(importKey) || scheduled.Contains(importKey))
                    {
                        continue;
                    }
                    if (scheduled.Count >= PageHelpers.MaxStylesheetResources)
                    {
                        continue;
                    }
                    if (!PageHelpers.SubresourceAllowed(documentUrl, importUrl.AbsoluteUri)
                        || ShouldBlockUrl(importUrl.AbsoluteUri))
                    {
                        continue;
                    }
                    scheduled.Add(importKey);
                    pending.Add((importKey, importUrl, (byte)(depth + 1)));
                }
            }
        }

        List<(AuthorStylesheetTarget, string)> materialized = [];
        foreach ((AuthorStylesheetTarget target, string key, string? media) in roots)
        {
            string? css = PageHelpers.MaterializeStylesheetGraph(key, sheets, aliases, []);
            if (css is null)
            {
                continue;
            }
            materialized.Add((target, media is null ? css : $"@media {media} {{\n{css}\n}}\n"));
        }
        return materialized;
    }
}
