using Obscura.Js.Url;
using Obscura.Net;

namespace Obscura.Api;

/// <summary>A cookie as exposed to the embeddable API.</summary>
public sealed class Cookie
{
    /// <summary>Cookie name.</summary>
    public required string Name { get; init; }

    /// <summary>Cookie value.</summary>
    public required string Value { get; init; }

    /// <summary>Scope domain.</summary>
    public required string Domain { get; init; }

    /// <summary>Scope path.</summary>
    public required string Path { get; init; }

    /// <summary>Only sent over https when true.</summary>
    public bool Secure { get; init; }

    /// <summary>Hidden from <c>document.cookie</c> when true.</summary>
    public bool HttpOnly { get; init; }

    /// <summary>Create a cookie from a name/value pair with Rust's defaults.</summary>
    public static Cookie New(string name, string value, string domain) => new()
    {
        Name = name,
        Value = value,
        Domain = domain,
        Path = "/",
        Secure = false,
        HttpOnly = false,
    };
}

/// <summary>Cookie management for a browser session.</summary>
public sealed class CookieStore
{
    private readonly CookieJar _jar;

    internal CookieStore(CookieJar jar) => _jar = jar;

    /// <summary>
    /// Set a cookie from a <c>Set-Cookie</c> header string, as it would arrive
    /// from <paramref name="url"/>.
    /// </summary>
    public void Set(string setCookieString, string url)
    {
        var parsed = ParseUrl(url);
        _jar.SetCookie(setCookieString, parsed);
    }

    /// <summary>Every cookie in the jar, including HttpOnly ones.</summary>
    public List<Cookie> GetAll()
    {
        var all = _jar.GetAllCookies();
        var result = new List<Cookie>(all.Count);
        foreach (var c in all)
        {
            result.Add(new Cookie
            {
                Name = c.Name,
                Value = c.Value,
                Domain = c.Domain,
                Path = c.Path,
                Secure = c.Secure,
                HttpOnly = c.HttpOnly,
            });
        }
        return result;
    }

    /// <summary>
    /// The cookies that would be sent to <paramref name="url"/>.
    /// </summary>
    /// <remarks>
    /// Reconstructed from the request header exactly as Rust does, so the
    /// per-cookie domain/path/flags are the request's, not the stored cookie's.
    /// </remarks>
    public List<Cookie> GetForUrl(string url)
    {
        var parsed = ParseUrl(url);
        var header = _jar.GetCookieHeader(parsed);
        var result = new List<Cookie>();
        var host = parsed.Host;
        foreach (var pair in header.Split("; "))
        {
            if (pair.Length == 0)
            {
                continue;
            }
            var split = pair.IndexOf('=');
            var name = split < 0 ? pair : pair[..split];
            var value = split < 0 ? string.Empty : pair[(split + 1)..];
            // Rust's `host_str()?` drops the cookie when the URL has no host.
            if (host.Length == 0)
            {
                continue;
            }
            result.Add(new Cookie
            {
                Name = name,
                Value = value,
                Domain = host,
                Path = "/",
                Secure = false,
                HttpOnly = false,
            });
        }
        return result;
    }

    /// <summary>Save every cookie to a JSON file.</summary>
    public void SaveToFile(string path)
    {
        try
        {
            _jar.SaveToFile(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw ObscuraException.Internal(error);
        }
    }

    /// <summary>Load cookies from a JSON file, returning how many were restored.</summary>
    public int LoadFromFile(string path)
    {
        try
        {
            return _jar.LoadFromFile(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw ObscuraException.Internal(error);
        }
    }

    private static Uri ParseUrl(string url)
    {
        // The WHATWG parser is the port of the `url` crate this API parses with,
        // so an input the reference accepts is accepted here too.
        if (UrlRecord.Parse(url) is not { } record || !Uri.TryCreate(record.Href, UriKind.Absolute, out var uri))
        {
            throw ObscuraException.Internal(new UriFormatException($"relative URL without a base: {url}"));
        }
        return uri;
    }
}
