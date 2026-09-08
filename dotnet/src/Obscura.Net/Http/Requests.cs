namespace Obscura.Net;

/// <summary>An HTTP response as obscura models it.</summary>
public sealed class Response
{
    /// <summary>The final URL the response came from, after redirects.</summary>
    public required Uri Url { get; init; }

    /// <summary>HTTP status code, or 0 for a tracker-blocked request.</summary>
    public required int Status { get; init; }

    /// <summary>Response headers, lowercased names.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>The decoded response body bytes.</summary>
    public required byte[] Body { get; init; }

    /// <summary>Every URL in the redirect chain that led here, in order.</summary>
    public required IReadOnlyList<Uri> RedirectedFrom { get; init; }

    /// <summary>
    /// Decode the body as text, honoring the response charset.
    ///
    /// Uses the HTTP <c>Content-Type</c> header's <c>charset=</c> parameter, then for
    /// HTML responses falls back to sniffing <c>&lt;meta charset&gt;</c> in the first
    /// 1KB, then UTF-8. Mirrors browser behaviour per the HTML5 spec.
    /// </summary>
    public string Text() => IsHtml()
        ? ContentEncoding.DecodeResponse(Body, ContentType())
        : ContentEncoding.DecodeNonHtml(Body, ContentType());

    /// <summary>Look up a response header by (case-insensitive) name.</summary>
    public string? Header(string name) =>
        Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;

    /// <summary>The raw <c>content-type</c> header value, if any.</summary>
    public string? ContentType() => Header("content-type");

    /// <summary>True when the response declares itself as HTML.</summary>
    public bool IsHtml() =>
        ContentType()?.Contains("text/html", StringComparison.Ordinal) ?? false;
}

/// <summary>The request as passed to interceptors and passive callbacks.</summary>
public sealed class RequestInfo
{
    /// <summary>Target URL for this hop.</summary>
    public required Uri Url { get; init; }

    /// <summary>HTTP method name.</summary>
    public required string Method { get; init; }

    /// <summary>The client's extra headers at the time the request was built.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>What kind of resource this request is for.</summary>
    public required ResourceType ResourceType { get; init; }
}

/// <summary>Resource classification, used for fetch metadata and cache keys.</summary>
public enum ResourceType
{
    /// <summary>A top-level or frame document.</summary>
    Document,

    /// <summary>A classic or module script.</summary>
    Script,

    /// <summary>A CSS stylesheet.</summary>
    Stylesheet,

    /// <summary>An image.</summary>
    Image,

    /// <summary>A web font.</summary>
    Font,

    /// <summary>An XMLHttpRequest.</summary>
    Xhr,

    /// <summary>A scripted fetch().</summary>
    Fetch,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>
/// Fetch metadata for a browser-owned request. Navigation keeps its existing
/// profile; render resources use this type so they do not masquerade as HTML
/// documents when they move onto the page's asynchronous transport.
/// </summary>
public enum RequestMode
{
    /// <summary>A navigation.</summary>
    Navigate,

    /// <summary>A no-CORS subresource load.</summary>
    NoCors,

    /// <summary>A CORS-enabled load.</summary>
    Cors,

    /// <summary>A same-origin-only load.</summary>
    SameOrigin,
}

/// <summary>Credentials mode for a request.</summary>
public enum RequestCredentials
{
    /// <summary>Never send cookies.</summary>
    Omit,

    /// <summary>Send cookies only to the initiator's origin.</summary>
    SameOrigin,

    /// <summary>Always send cookies.</summary>
    Include,
}

/// <summary>The per-resource fetch profile.</summary>
public sealed record ResourceRequest
{
    /// <summary>What kind of resource this is.</summary>
    public ResourceType ResourceType { get; set; }

    /// <summary>
    /// Origin-bearing environment that owns the request. This controls CORS,
    /// credentials, and Sec-Fetch-Site and must remain the document/realm for every
    /// descendant in a module graph.
    /// </summary>
    public Uri? Initiator { get; set; }

    /// <summary>
    /// URL used to derive the Referer header. Usually the same as
    /// <see cref="Initiator"/>, but a module dependency is referred by its importing
    /// module while its credentials mode is still relative to the owning document.
    /// </summary>
    public Uri? Referrer { get; set; }

    /// <summary>Fetch mode.</summary>
    public RequestMode Mode { get; set; }

    /// <summary>Credentials mode.</summary>
    public RequestCredentials Credentials { get; set; }

    /// <summary>
    /// Hard limit for the decoded response body retained by this request. Callers
    /// can lower it for especially constrained resource consumers.
    /// </summary>
    public long MaxResponseBytes { get; set; }

    /// <summary>The navigation profile.</summary>
    public static ResourceRequest Navigation() => new()
    {
        ResourceType = ResourceType.Document,
        Initiator = null,
        Referrer = null,
        Mode = RequestMode.Navigate,
        Credentials = RequestCredentials.Include,
        MaxResponseBytes = 64L * 1024 * 1024,
    };

    /// <summary>The profile for a subresource loaded by <paramref name="initiator"/>.</summary>
    public static ResourceRequest Subresource(ResourceType resourceType, Uri initiator)
    {
        var mode = resourceType switch
        {
            ResourceType.Font or ResourceType.Xhr or ResourceType.Fetch => RequestMode.Cors,
            ResourceType.Document => RequestMode.Navigate,
            _ => RequestMode.NoCors,
        };
        var credentials = resourceType switch
        {
            ResourceType.Font or ResourceType.Xhr or ResourceType.Fetch =>
                RequestCredentials.SameOrigin,
            _ => RequestCredentials.Include,
        };
        return new ResourceRequest
        {
            ResourceType = resourceType,
            Initiator = initiator,
            Referrer = initiator,
            Mode = mode,
            Credentials = credentials,
            MaxResponseBytes = resourceType switch
            {
                ResourceType.Stylesheet or ResourceType.Font => 16L * 1024 * 1024,
                ResourceType.Script or ResourceType.Other => 32L * 1024 * 1024,
                _ => 64L * 1024 * 1024,
            },
        };
    }

    /// <summary>
    /// Fetch profile for JavaScript modules. Unlike classic scripts, module scripts
    /// are CORS-enabled and use <c>same-origin</c> credentials by default. Keep this
    /// separate from <see cref="Subresource"/> with <see cref="ResourceType.Script"/>,
    /// whose no-CORS, include-credentials profile is still correct for classic
    /// scripts.
    /// </summary>
    public static ResourceRequest ModuleScript(Uri initiator, Uri referrer) => new()
    {
        ResourceType = ResourceType.Script,
        Initiator = initiator,
        Referrer = referrer,
        // OBSCURA_FETCH_MAX_BODY_BYTES (the fetch()/XHR override from #581) also
        // raises this cap: a large SPA bundle otherwise dies silently at 32 MiB
        // while fetch() of the same URL succeeds (#849).
        Mode = RequestMode.Cors,
        Credentials = RequestCredentials.SameOrigin,
        MaxResponseBytes =
            long.TryParse(
                Environment.GetEnvironmentVariable("OBSCURA_FETCH_MAX_BODY_BYTES"),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var configured)
                ? configured
                : 32L * 1024 * 1024,
    };

    /// <summary>Return a copy with a different body cap.</summary>
    public ResourceRequest WithMaxResponseBytes(long maxResponseBytes) =>
        this with { MaxResponseBytes = maxResponseBytes };

    internal string Destination() => ResourceType switch
    {
        ResourceType.Document => "document",
        ResourceType.Script => "script",
        ResourceType.Stylesheet => "style",
        ResourceType.Image => "image",
        ResourceType.Font => "font",
        _ => "empty",
    };

    internal string Accept() => ResourceType switch
    {
        ResourceType.Document =>
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7",
        ResourceType.Stylesheet => "text/css,*/*;q=0.1",
        // AVIF is intentionally omitted until obscura's decoder can paint it.
        // Advertising a format and then discarding the selected body is less
        // faithful than negotiating the best format we can use.
        ResourceType.Image => "image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8",
        _ => "*/*",
    };

    internal bool SendsCredentialsTo(Uri target) => Credentials switch
    {
        RequestCredentials.Omit => false,
        RequestCredentials.Include => true,
        _ => Initiator is not null && UrlOrigin.SameOrigin(Initiator, target),
    };
}

/// <summary>Header value for a <see cref="RequestMode"/>.</summary>
internal static class RequestModeExtensions
{
    internal static string HeaderValue(this RequestMode mode) => mode switch
    {
        RequestMode.Navigate => "navigate",
        RequestMode.NoCors => "no-cors",
        RequestMode.Cors => "cors",
        _ => "same-origin",
    };
}
