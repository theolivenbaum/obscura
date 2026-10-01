namespace PocketCalculator.Net;

/// <summary>Which arm of the Rust <c>PocketCalculatorNetError</c> enum an error is.</summary>
public enum PocketCalculatorNetErrorKind
{
    /// <summary>Transport, DNS, scheme or SSRF-guard failure.</summary>
    Network,

    /// <summary>The redirect chain exceeded the Fetch-spec limit of 20 hops.</summary>
    TooManyRedirects,

    /// <summary>An interceptor blocked the request.</summary>
    Blocked,

    /// <summary>The response failed the CORS check.</summary>
    Cors,

    /// <summary>The response body exceeded the caller's byte cap.</summary>
    ResponseTooLarge,
}

/// <summary>Port of the Rust <c>PocketCalculatorNetError</c> enum.</summary>
public class PocketCalculatorNetException : Exception
{
    /// <summary>Create an error of the given kind.</summary>
    public PocketCalculatorNetException(PocketCalculatorNetErrorKind kind, string message)
        : base(message) => Kind = kind;

    /// <summary>Create an error of the given kind wrapping an inner exception.</summary>
    public PocketCalculatorNetException(PocketCalculatorNetErrorKind kind, string message, Exception? inner)
        : base(message, inner) => Kind = kind;

    /// <summary>Which enum arm this error corresponds to.</summary>
    public PocketCalculatorNetErrorKind Kind { get; }

    /// <summary><c>PocketCalculatorNetError::Network</c>.</summary>
    public static PocketCalculatorNetException Network(string message, Exception? inner = null) =>
        new(PocketCalculatorNetErrorKind.Network, $"Network error: {message}", inner);

    /// <summary><c>PocketCalculatorNetError::TooManyRedirects</c>.</summary>
    public static PocketCalculatorNetException TooManyRedirects(string url) =>
        new(PocketCalculatorNetErrorKind.TooManyRedirects, $"Too many redirects: {url}");

    /// <summary><c>PocketCalculatorNetError::Blocked</c>.</summary>
    public static PocketCalculatorNetException Blocked(string url) =>
        new(PocketCalculatorNetErrorKind.Blocked, $"Request blocked: {url}");

    /// <summary>
    /// A request refused as blockable mixed content (SECURITY.md I7). It is a
    /// <see cref="PocketCalculatorNetErrorKind.Blocked"/> error, so callers treat it like
    /// any other refused load; the message is Chromium's console text.
    /// </summary>
    public static PocketCalculatorNetException MixedContent(string message) =>
        new(PocketCalculatorNetErrorKind.Blocked, message);

    /// <summary><c>PocketCalculatorNetError::Cors</c>.</summary>
    public static PocketCalculatorNetException Cors(string message) =>
        new(PocketCalculatorNetErrorKind.Cors, $"CORS error: {message}");

    /// <summary><c>PocketCalculatorNetError::ResponseTooLarge</c>.</summary>
    public static ResponseTooLargeException ResponseTooLarge(Uri url, long limit) =>
        new(url.ToString(), limit);
}

/// <summary><c>PocketCalculatorNetError::ResponseTooLarge { url, limit }</c>.</summary>
public sealed class ResponseTooLargeException : PocketCalculatorNetException
{
    /// <summary>Create the error for a URL and the limit it blew past.</summary>
    public ResponseTooLargeException(string url, long limit)
        : base(PocketCalculatorNetErrorKind.ResponseTooLarge, $"Response body exceeded {limit} byte limit: {url}")
    {
        Url = url;
        Limit = limit;
    }

    /// <summary>The URL whose body was too large.</summary>
    public string Url { get; }

    /// <summary>The byte cap that was exceeded.</summary>
    public long Limit { get; }
}
