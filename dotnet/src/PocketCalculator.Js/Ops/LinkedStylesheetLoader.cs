using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PocketCalculator.Dom;
using PocketCalculator.Net;
using PocketCalculator.Js.Url;
using PocketCalculator.Render.Css;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// <c>op_load_stylesheet</c>: fetches a dynamically inserted <c>&lt;link rel=stylesheet&gt;</c>
/// and its <c>@import</c> graph, and installs the result in the host stylesheet store.
/// </summary>
/// <remarks>
/// <para>
/// Port addition, and a DEVIATION from crates/obscura-js/js/bootstrap.js, where
/// <c>_fetchLinkedCss</c> does all of this in the page realm: it parses the op's JSON with
/// the global <c>JSON.parse</c>, strips <c>@import</c>s with <c>String.prototype.replace</c>,
/// and judges the sheet origin-clean with the page's <c>URL</c>. A page that replaced any of
/// those read the text of every cross-origin sheet, or had it stored as clean and read it
/// back through CSSOM (SECURITY.md C3). The same steps run here, on bytes page script never
/// sees, and the origin-clean bit comes from the response URLs the transport followed.
/// </para>
/// <para>
/// The steps are the shim's: at most five levels and no cycle, an <c>@import</c> dropped when
/// its media cannot apply, <c>url()</c> references rebased onto the sheet's response URL, and
/// the imported sheets first. The one difference is the media check for an import that
/// names a width or a <c>prefers-</c> feature, which the shim answered with its own
/// <c>matchMedia</c> and this answers with the renderer's <see cref="CssMediaQuery"/> against
/// the document's viewport.
/// </para>
/// </remarks>
internal static partial class LinkedStylesheetLoader
{
    private const int MaxImportDepth = 4;

    [GeneratedRegex(
        """@import\s+(?:url\(\s*)?(?:"([^"]+)"|'([^']+)'|([^'"\s;)]+))\s*\)?\s*([^;]*);""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImportRule();

    [GeneratedRegex(@"^(?:[a-z][a-z0-9+.-]*:|//|#)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteOrFragmentReference();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    internal sealed record LoadedSheet(string Css, string ResponseUrl, bool OriginClean);

    /// <summary>
    /// Loads <paramref name="url"/> for the link element <paramref name="ownerNid"/> of
    /// <paramref name="document"/> and installs it. Returns the JSON the shim reads:
    /// <c>{"ok":true,"responseUrl":...}</c>, or <c>{"ok":false}</c> when the sheet or one of
    /// its imports failed to load.
    /// </summary>
    public static async Task<string> OpLoadStylesheetAsync(
        PocketCalculatorState transport,
        PocketCalculatorState document,
        uint ownerNid,
        string url)
    {
        try
        {
            // The link's referrerpolicy, else the document's (port addition).
            var policy = document.Dom is { } linkDom
                ? StateHelpers.ElementReferrerPolicy(linkDom, NodeId.New(ownerNid))
                : null;
            var referrer = policy is null ? null : FetchReferrer.Client with { Policy = policy };
            var loaded = await LoadAsync(transport, document, url, 0, new HashSet<string>(StringComparer.Ordinal), referrer)
                .ConfigureAwait(false);
            if (!StylesheetOps.SetLoadedExternalStylesheet(document, ownerNid, loaded.Css, loaded.OriginClean))
            {
                return """{"ok":false}""";
            }

            return "{\"ok\":true,\"responseUrl\":" + SerdeJson.String(loaded.ResponseUrl) + "}";
        }
        catch (Microsoft.ClearScript.ScriptInterruptedException)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            return """{"ok":false}""";
        }
    }

    internal static async Task<LoadedSheet> LoadAsync(
        PocketCalculatorState transport,
        PocketCalculatorState document,
        string url,
        int depth,
        HashSet<string> seen,
        FetchReferrer? referrer = null)
    {
        if (depth > MaxImportDepth || !seen.Add(url))
        {
            return new LoadedSheet(string.Empty, url, true);
        }

        var raw = await FetchOps.FetchUrlAsync(
                transport, document, url, "GET", "{}", [], "no-cors", "same-origin",
                internalLoad: true, hostConsumesBody: true, referrer: referrer)
            .ConfigureAwait(false);
        var load = TakeLoad(document, raw)
            ?? throw new InvalidOperationException("Stylesheet fetch failed: " + url);
        if (load.Status is >= 400 or <= 0)
        {
            throw new InvalidOperationException("Stylesheet fetch failed: " + url);
        }

        var imports = new List<string>();
        var baseUrl = UrlRecord.Parse(url);
        // @import is only valid before ordinary rules. Removing it here lets the renderer
        // consume the imported rules from the combined text.
        var css = ImportRule().Replace(load.Body, match =>
        {
            var target = match.Groups[1].Success ? match.Groups[1].Value
                : match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Value;
            if (ImportApplies(match.Groups[4].Value, document.Viewport)
                && baseUrl?.Join(target) is { } resolved)
            {
                imports.Add(resolved.Href);
            }

            return string.Empty;
        });

        var responseUrl = load.FinalUrl.Length != 0 ? load.FinalUrl : url;
        // An @import is referred by the sheet that imports it, under that sheet's
        // Referrer-Policy header, else the document's (Chromium 141; port addition).
        var importReferrer = new FetchReferrer(load.ReferrerPolicyHeader, responseUrl) { Trusted = true };
        var imported = await Task.WhenAll(imports.ConvertAll(importUrl =>
                LoadAsync(transport, document, importUrl, depth + 1, new HashSet<string>(seen, StringComparer.Ordinal), importReferrer)))
            .ConfigureAwait(false);

        var text = new StringBuilder(css.Length + 64);
        var originClean = !load.Tainted;
        foreach (var sheet in imported)
        {
            originClean &= sheet.OriginClean;
            if (sheet.Css.Length != 0)
            {
                Append(text, sheet.Css);
            }
        }

        var urls = new List<string>();
        var own = RebaseCssUrls(css, responseUrl, urls);
        // Its images and fonts are referred by this sheet under its header policy, else
        // the default: the document's policy does not reach them (Chromium 141).
        if (urls.Count != 0 && Uri.TryCreate(responseUrl, UriKind.Absolute, out var sheetUri))
        {
            document.CssSubresourceReferrers.Record(
                document.DocumentGeneration, sheetUri, load.ReferrerPolicyHeader ?? ReferrerPolicies.Default, urls);
        }

        if (own.Length != 0)
        {
            Append(text, own);
        }

        return new LoadedSheet(text.ToString(), responseUrl, originClean);

        static void Append(StringBuilder text, string part)
        {
            if (text.Length != 0)
            {
                text.Append('\n');
            }

            text.Append(part);
        }
    }

    private static InternalLoad? TakeLoad(PocketCalculatorState document, string raw)
    {
        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;
        if (root.TryGetProperty("blocked", out var blocked) && blocked.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        return root.TryGetProperty("bodyToken", out var token) && token.TryGetDouble(out var id)
            ? InternalLoads.Take(document, id, "no-cors")
            : null;
    }

    /// <summary>The shim's <c>_cssImportApplies</c>.</summary>
    internal static bool ImportApplies(string media, (float Width, float Height) viewport)
    {
        var compact = Whitespace().Replace(media, string.Empty).ToLowerInvariant();
        if (compact.Length == 0)
        {
            return true;
        }

        if (compact.Contains("prefers-color-scheme:dark", StringComparison.Ordinal))
        {
            return false;
        }

        if (compact.Contains("print", StringComparison.Ordinal)
            && !compact.Contains("screen", StringComparison.Ordinal)
            && !compact.Contains("all", StringComparison.Ordinal))
        {
            return false;
        }

        if (compact.Contains("min-width", StringComparison.Ordinal)
            || compact.Contains("max-width", StringComparison.Ordinal)
            || compact.Contains("prefers-", StringComparison.Ordinal))
        {
            return CssMediaQuery.AppliesForViewport(media, viewport);
        }

        return true;
    }

    /// <summary>
    /// The shim's <c>_rebaseCssUrls</c>: rewrites each relative <c>url()</c> against the
    /// sheet's URL, skipping comments and strings. A scan rather than a regular expression,
    /// because data URLs and quoted URLs can hold parentheses, quotes and whitespace.
    /// </summary>
    internal static string RebaseCssUrls(string css, string baseUrl, List<string>? urls = null)
    {
        if (css.IndexOf('(') < 0)
        {
            return css;
        }

        var baseRecord = UrlRecord.Parse(baseUrl);
        var output = new StringBuilder(css.Length + 64);
        var i = 0;
        var quote = '\0';
        var comment = false;
        while (i < css.Length)
        {
            if (comment)
            {
                if (css[i] == '*' && i + 1 < css.Length && css[i + 1] == '/')
                {
                    output.Append("*/");
                    i += 2;
                    comment = false;
                }
                else
                {
                    output.Append(css[i++]);
                }

                continue;
            }

            if (quote != '\0')
            {
                var ch = css[i++];
                output.Append(ch);
                if (ch == '\\' && i < css.Length)
                {
                    output.Append(css[i++]);
                }
                else if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (css[i] == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                output.Append("/*");
                i += 2;
                comment = true;
                continue;
            }

            if (css[i] is '"' or '\'')
            {
                quote = css[i];
                output.Append(css[i++]);
                continue;
            }

            if (i + 4 > css.Length
                || !css.AsSpan(i, 4).Equals("url(", StringComparison.OrdinalIgnoreCase))
            {
                output.Append(css[i++]);
                continue;
            }

            var end = i + 4;
            var innerQuote = '\0';
            while (end < css.Length)
            {
                var ch = css[end];
                if (innerQuote != '\0')
                {
                    if (ch == '\\')
                    {
                        end += 2;
                        continue;
                    }

                    if (ch == innerQuote)
                    {
                        innerQuote = '\0';
                    }
                }
                else if (ch is '"' or '\'')
                {
                    innerQuote = ch;
                }
                else if (ch == ')')
                {
                    break;
                }

                end++;
            }

            if (end >= css.Length)
            {
                output.Append(css, i, css.Length - i);
                break;
            }

            var raw = css[(i + 4)..end].Trim();
            var value = raw.Length >= 2
                && ((raw[0] == '"' && raw[^1] == '"') || (raw[0] == '\'' && raw[^1] == '\''))
                    ? raw[1..^1]
                    : raw;
            var resolved = value;
            if (value.Length != 0
                && !AbsoluteOrFragmentReference().IsMatch(value)
                && baseRecord?.Join(value) is { } joined)
            {
                resolved = joined.Href;
            }

            if (urls is not null
                && (resolved.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || resolved.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                urls.Add(resolved);
            }

            output.Append("url(\"")
                .Append(resolved.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal))
                .Append("\")");
            i = end + 1;
        }

        return output.ToString();
    }
}
