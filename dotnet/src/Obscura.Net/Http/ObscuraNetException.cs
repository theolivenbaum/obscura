namespace Obscura.Net;

/// <summary>Which arm of the Rust <c>ObscuraNetError</c> enum an error is.</summary>
public enum ObscuraNetErrorKind
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

/// <summary>Port of the Rust <c>ObscuraNetError</c> enum.</summary>
public class ObscuraNetException : Exception
{
    /// <summary>Create an error of the given kind.</summary>
    public ObscuraNetException(ObscuraNetErrorKind kind, string message)
        : base(message) => Kind = kind;

    /// <summary>Create an error of the given kind wrapping an inner exception.</summary>
    public ObscuraNetException(ObscuraNetErrorKind kind, string message, Exception? inner)
        : base(message, inner) => Kind = kind;

    /// <summary>Which enum arm this error corresponds to.</summary>
    public ObscuraNetErrorKind Kind { get; }

    /// <summary><c>ObscuraNetError::Network</c>.</summary>
    public static ObscuraNetException Network(string message, Exception? inner = null) =>
        new(ObscuraNetErrorKind.Network, $"Network error: {message}", inner);

    /// <summary><c>ObscuraNetError::TooManyRedirects</c>.</summary>
    public static ObscuraNetException TooManyRedirects(string url) =>
        new(ObscuraNetErrorKind.TooManyRedirects, $"Too many redirects: {url}");

    /// <summary><c>ObscuraNetError::Blocked</c>.</summary>
    public static ObscuraNetException Blocked(string url) =>
        new(ObscuraNetErrorKind.Blocked, $"Request blocked: {url}");

    /// <summary><c>ObscuraNetError::Cors</c>.</summary>
    public static ObscuraNetException Cors(string message) =>
        new(ObscuraNetErrorKind.Cors, $"CORS error: {message}");

    /// <summary><c>ObscuraNetError::ResponseTooLarge</c>.</summary>
    public static ResponseTooLargeException ResponseTooLarge(Uri url, long limit) =>
        new(url.ToString(), limit);
}

/// <summary><c>ObscuraNetError::ResponseTooLarge { url, limit }</c>.</summary>
public sealed class ResponseTooLargeException : ObscuraNetException
{
    /// <summary>Create the error for a URL and the limit it blew past.</summary>
    public ResponseTooLargeException(string url, long limit)
        : base(ObscuraNetErrorKind.ResponseTooLarge, $"Response body exceeded {limit} byte limit: {url}")
    {
        Url = url;
        Limit = limit;
    }

    /// <summary>The URL whose body was too large.</summary>
    public string Url { get; }

    /// <summary>The byte cap that was exceeded.</summary>
    public long Limit { get; }
}
