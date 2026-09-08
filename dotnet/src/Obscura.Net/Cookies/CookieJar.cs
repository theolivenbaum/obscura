using System.Text;
using System.Text.Json;

namespace Obscura.Net;

/// <summary>
/// Obscura's own cookie store (port of <c>crates/obscura-net/src/cookies.rs</c>).
/// The HTTP stack's cookie container is never used; every request's Cookie header
/// and every Set-Cookie response goes through this jar.
/// </summary>
public sealed class CookieJar
{
    private const string DefaultSameSite = "Lax";

    /// <summary>
    /// domain -> (name, path) -> entry. RFC 6265 section 5.3 identifies a cookie by
    /// (name, domain, path); the outer map scopes by domain and the inner key
    /// carries name+path so same-name cookies on different paths coexist instead of
    /// clobbering each other.
    /// </summary>
    private readonly Dictionary<string, Dictionary<(string Name, string Path), CookieEntry>> _cookies =
        new(StringComparer.Ordinal);

    private readonly System.Threading.Lock _lock = new();

    private sealed class CookieEntry
    {
        public required string Name { get; init; }
        public required string Value { get; init; }
        public required string Path { get; init; }
        public required string Domain { get; init; }

        /// <summary>
        /// Cookies set without a Domain attribute are host-only: sent to the exact
        /// origin host and never to subdomains.
        /// </summary>
        public bool HostOnly { get; init; }

        public bool Secure { get; init; }
        public bool HttpOnly { get; init; }
        public long? Expires { get; init; }
        public required string SameSite { get; init; }
    }

    /// <summary>
    /// SameSite is case-insensitive per RFC 6265bis; normalize a present value to
    /// title-case so stored cookies compare equal regardless of how they were sent.
    /// Unrecognized values fall back to Lax per spec.
    /// </summary>
    private static string NormalizeSameSite(string value) => value.Trim().ToLowerInvariant() switch
    {
        "strict" => "Strict",
        "none" => "None",
        _ => "Lax",
    };

    /// <summary>
    /// The jar key for a domain. RFC 6265 4.1.2.3 ignores a leading dot, and hosts
    /// are case-insensitive, so both spellings have to collapse before they reach
    /// the map. Not intended for domain matching, which compares without allocating
    /// on the per-request path.
    /// </summary>
    public static string CanonicalDomain(string domain) =>
        domain.Trim().TrimStart('.').ToLowerInvariant();

    /// <summary>
    /// RFC 6265 5.1.4 default-path: the path a cookie is scoped to when its
    /// Set-Cookie carries no Path attribute. It is the request URI's directory - the
    /// path up to but not including the right-most '/' - NOT the full request path.
    /// Using the full path scopes a session cookie to the exact URL that set it: a
    /// cookie set on <c>/app/login</c> would then not match <c>/app/dashboard</c>,
    /// silently logging the user out on the next navigation. Browsers store
    /// <c>/app</c> here.
    /// </summary>
    public static string DefaultCookiePath(string requestPath)
    {
        // "If the uri-path is empty or if the first character is not '/', output /."
        if (!requestPath.StartsWith('/'))
        {
            return "/";
        }

        var index = requestPath.LastIndexOf('/');
        // "If the uri-path contains no more than one '/', output /."
        return index <= 0 ? "/" : requestPath[..index];
    }

    /// <summary>Store a cookie from a <c>Set-Cookie</c> response header.</summary>
    public void SetCookie(string setCookieString, Uri url) =>
        Store(setCookieString, url, fromJavaScript: false);

    /// <summary>Store a cookie from a <c>document.cookie</c> assignment.</summary>
    public void SetCookieFromJs(string cookieString, Uri url) =>
        Store(cookieString, url, fromJavaScript: true);

    private void Store(string cookieString, Uri url, bool fromJavaScript)
    {
        var (nameValue, attributes) = SplitOnce(cookieString, ';');
        nameValue = nameValue.Trim();
        if (!TrySplitOnce(nameValue, '=', out var rawName, out var rawValue))
        {
            return;
        }

        var name = rawName.Trim();
        var value = rawValue.Trim();

        var requestHost = HostOf(url).ToLowerInvariant();
        string? domainAttr = null;
        var path = DefaultCookiePath(url.AbsolutePath);
        var secure = false;
        var httpOnly = false;
        long? expires = null;
        var sameSite = "Lax";

        if (attributes is not null)
        {
            foreach (var rawAttr in attributes.Split(';'))
            {
                var attr = rawAttr.Trim();
                if (TrySplitOnce(attr, '=', out var key, out var val))
                {
                    switch (key.Trim().ToLowerInvariant())
                    {
                        case "domain":
                            domainAttr = CanonicalDomain(val);
                            break;
                        case "path":
                            path = val.Trim();
                            break;
                        case "expires":
                            if (TryParseHttpDate(val.Trim(), out var ts))
                            {
                                expires = ts;
                            }

                            break;
                        case "max-age":
                            if (long.TryParse(
                                    val.Trim(),
                                    System.Globalization.NumberStyles.AllowLeadingSign,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out var secs))
                            {
                                expires = secs <= 0 ? 0 : Now() + secs;
                            }

                            break;
                        case "samesite":
                            sameSite = NormalizeSameSite(val);
                            break;
                        default:
                            break;
                    }
                }
                else
                {
                    switch (attr.ToLowerInvariant())
                    {
                        case "secure":
                            secure = true;
                            break;
                        case "httponly" when !fromJavaScript:
                            httpOnly = true;
                            break;
                        default:
                            break;
                    }
                }
            }
        }

        // Validate Domain against the response origin (RFC 6265): an unrelated or
        // public-suffix Domain is ignored so a response from attacker.test cannot
        // scope a cookie to victim.test (GHSA-f22c-8v6q-v6h6).
        if (!TryResolveCookieDomain(requestHost, domainAttr, out var domain, out var hostOnly))
        {
            return;
        }

        if (expires is { } exp && exp <= Now())
        {
            lock (_lock)
            {
                if (_cookies.TryGetValue(domain, out var domainCookies))
                {
                    domainCookies.Remove((name, path));
                }
            }

            return;
        }

        var entry = new CookieEntry
        {
            Name = name,
            Value = value,
            Path = path,
            Domain = domain,
            HostOnly = hostOnly,
            Secure = secure,
            HttpOnly = httpOnly,
            Expires = expires,
            SameSite = sameSite,
        };

        lock (_lock)
        {
            if (!_cookies.TryGetValue(domain, out var domainCookies))
            {
                domainCookies = [];
                _cookies[domain] = domainCookies;
            }

            domainCookies[(name, path)] = entry;
        }
    }

    /// <summary>The <c>Cookie</c> request header value for <paramref name="url"/>.</summary>
    public string GetCookieHeader(Uri url) => Collect(url, jsVisibleOnly: false);

    /// <summary>The <c>document.cookie</c> value for <paramref name="url"/>: HttpOnly cookies are hidden.</summary>
    public string GetJsVisibleCookies(Uri url) => Collect(url, jsVisibleOnly: true);

    private string Collect(Uri url, bool jsVisibleOnly)
    {
        var host = HostOf(url);
        var path = url.AbsolutePath;
        var isSecure = string.Equals(url.Scheme, "https", StringComparison.Ordinal);
        var now = Now();

        var matching = new List<string>();
        lock (_lock)
        {
            foreach (var (domain, domainCookies) in _cookies)
            {
                if (!DomainMatches(host, domain))
                {
                    continue;
                }

                foreach (var entry in domainCookies.Values)
                {
                    if (entry.HostOnly && !host.Equals(domain, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (jsVisibleOnly && entry.HttpOnly)
                    {
                        continue;
                    }

                    if (entry.Expires is { } exp && exp <= now)
                    {
                        continue;
                    }

                    if (entry.Secure && !isSecure)
                    {
                        continue;
                    }

                    if (!PathMatches(path, entry.Path))
                    {
                        continue;
                    }

                    matching.Add($"{entry.Name}={entry.Value}");
                }
            }
        }

        return string.Join("; ", matching);
    }

    /// <summary>Every non-expired cookie in the jar.</summary>
    public List<CookieInfo> GetAllCookies()
    {
        var now = Now();
        var result = new List<CookieInfo>();
        lock (_lock)
        {
            foreach (var domainCookies in _cookies.Values)
            {
                foreach (var entry in domainCookies.Values)
                {
                    if (entry.Expires is { } expires && expires <= now)
                    {
                        continue;
                    }

                    result.Add(ToInfo(entry));
                }
            }
        }

        return result;
    }

    /// <summary>Import cookies from CDP or a persisted store. A zero/past expiry deletes.</summary>
    public void SetCookiesFromCdp(IEnumerable<CookieInfo> cookies)
    {
        var now = Now();
        lock (_lock)
        {
            foreach (var cookie in cookies)
            {
                // RFC 6265 4.1.2.3: the leading dot is ignored. The Set-Cookie path
                // already strips it, but this code did not, which is why one cookie
                // became two entries.
                var domain = CanonicalDomain(cookie.Domain);
                if (cookie.Expires is { } expires && (expires == 0 || (expires > 0 && expires <= now)))
                {
                    if (_cookies.TryGetValue(domain, out var existing))
                    {
                        var doomed = existing
                            .Where(pair => string.Equals(pair.Value.Name, cookie.Name, StringComparison.Ordinal)
                                && string.Equals(pair.Value.Path, cookie.Path, StringComparison.Ordinal))
                            .Select(pair => pair.Key)
                            .ToList();
                        foreach (var key in doomed)
                        {
                            existing.Remove(key);
                        }
                    }

                    continue;
                }

                var sameSite = cookie.SameSite.Length == 0
                    ? DefaultSameSite
                    : NormalizeSameSite(cookie.SameSite);
                var entry = new CookieEntry
                {
                    Name = cookie.Name,
                    Value = cookie.Value,
                    Path = cookie.Path,
                    Domain = domain,
                    // CDP/persisted import is trusted; honor the explicit domain as
                    // domain-scoped (matches the prior behavior).
                    HostOnly = false,
                    Secure = cookie.Secure,
                    HttpOnly = cookie.HttpOnly,
                    Expires = cookie.Expires is { } e && e > 0 ? e : null,
                    SameSite = sameSite,
                };

                if (!_cookies.TryGetValue(domain, out var domainCookies))
                {
                    domainCookies = [];
                    _cookies[domain] = domainCookies;
                }

                domainCookies[(cookie.Name, cookie.Path)] = entry;
            }
        }
    }

    /// <summary>Delete every cookie named <paramref name="name"/>, optionally scoped to a domain.</summary>
    public void DeleteCookie(string name, string domain) =>
        DeleteCookiesFiltered(name, domain, null);

    /// <summary>
    /// Delete cookies named <paramref name="name"/>, optionally scoped to a domain
    /// and a path. A null <paramref name="path"/> deletes regardless of path.
    /// </summary>
    public void DeleteCookiesFiltered(string name, string domain, string? path)
    {
        lock (_lock)
        {
            bool MatchesPath(string entryPath) =>
                path is null || string.Equals(entryPath, path, StringComparison.Ordinal);

            if (domain.Length == 0)
            {
                foreach (var domainCookies in _cookies.Values)
                {
                    RemoveMatching(domainCookies, name, MatchesPath);
                }
            }
            else if (_cookies.TryGetValue(CanonicalDomain(domain), out var domainCookies))
            {
                RemoveMatching(domainCookies, name, MatchesPath);
            }
        }
    }

    private static void RemoveMatching(
        Dictionary<(string Name, string Path), CookieEntry> domainCookies,
        string name,
        Func<string, bool> matchesPath)
    {
        var doomed = domainCookies
            .Where(pair => string.Equals(pair.Value.Name, name, StringComparison.Ordinal)
                && matchesPath(pair.Value.Path))
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in doomed)
        {
            domainCookies.Remove(key);
        }
    }

    /// <summary>Drop every cookie.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cookies.Clear();
        }
    }

    /// <summary>
    /// Serialize all non-expired cookies to a JSON file. Writes atomically via a
    /// temp file then rename.
    /// </summary>
    public void SaveToFile(string path)
    {
        var all = GetAllCookies();
        var json = JsonSerializer.Serialize(all, CookieJsonOptions);
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var tmp = Path.Combine(
            string.IsNullOrEmpty(parent) ? "." : parent,
            $".obscura-cookies-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Load cookies from a JSON file into the jar. Merges with existing cookies
    /// (does not clear). Returns the number of cookies loaded.
    /// </summary>
    public int LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var data = File.ReadAllText(path);
        List<CookieInfo>? cookies;
        try
        {
            cookies = JsonSerializer.Deserialize<List<CookieInfo>>(data, CookieJsonOptions);
        }
        catch (JsonException error)
        {
            throw new IOException($"invalid cookie store {path}", error);
        }

        cookies ??= [];
        SetCookiesFromCdp(cookies);
        return cookies.Count;
    }

    private static readonly JsonSerializerOptions CookieJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private static CookieInfo ToInfo(CookieEntry entry) => new()
    {
        Name = entry.Name,
        Value = entry.Value,
        Domain = entry.Domain,
        Path = entry.Path,
        Secure = entry.Secure,
        HttpOnly = entry.HttpOnly,
        SameSite = entry.SameSite,
        Expires = entry.Expires,
    };

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    internal static string HostOf(Uri url) =>
        url.HostNameType == UriHostNameType.IPv6 ? url.Host.Trim('[', ']') : url.Host;

    /// <summary>
    /// Parse the handful of HTTP date spellings Set-Cookie uses, to a Unix second
    /// count. Returns false on anything it does not understand.
    /// </summary>
    internal static bool TryParseHttpDate(string value, out long seconds)
    {
        seconds = 0;
        string[] months =
            ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

        var normalized = value.Replace('-', ' ');
        var parts = normalized.Split(
            [' ', '\t', '\n', '\r', '\f', '\v'],
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 5)
        {
            return false;
        }

        if (!ulong.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        var monthName = parts[2].ToLowerInvariant();
        var monthIndex = Array.FindIndex(months, m => monthName.StartsWith(m, StringComparison.Ordinal));
        if (monthIndex < 0)
        {
            return false;
        }

        var month = (ulong)monthIndex + 1;

        if (!ulong.TryParse(parts[3], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var year))
        {
            return false;
        }

        var timeParts = parts[4].Split(':');
        var hour = ParseOrZero(timeParts, 0);
        var minute = ParseOrZero(timeParts, 1);
        var second = ParseOrZero(timeParts, 2);

        ulong daysTotal = 0;
        for (var y = 1970UL; y < year; y++)
        {
            daysTotal += y % 4 == 0 && (y % 100 != 0 || y % 400 == 0) ? 366UL : 365UL;
        }

        ulong[] daysInMonth = [0, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
        var isLeap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
        for (var m = 1UL; m < month; m++)
        {
            daysTotal += daysInMonth[m] + (m == 2 && isLeap ? 1UL : 0UL);
        }

        daysTotal += day - 1;

        seconds = (long)((daysTotal * 86400) + (hour * 3600) + (minute * 60) + second);
        return true;
    }

    private static ulong ParseOrZero(string[] parts, int index) =>
        index < parts.Length
        && ulong.TryParse(parts[index], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0UL;

    /// <summary>
    /// Resolve the storage domain and host-only flag for a cookie being set from
    /// <paramref name="originHost"/> (RFC 6265 sections 5.2/5.3). With no Domain
    /// attribute the cookie is host-only: scoped to the exact origin host. A Domain
    /// attribute is honored only when it domain-matches the origin (equal to it or a
    /// parent domain) and is not an obvious public suffix; otherwise the attribute is
    /// ignored and the cookie is stored host-only on the origin. This is what stops a
    /// response from attacker.test planting a cookie scoped to victim.test.
    ///
    /// Returns false only when the origin host itself is absent (the cookie cannot be
    /// scoped and is dropped).
    ///
    /// Note: a full public suffix list is not bundled, so multi-label public suffixes
    /// (co.uk, github.io) are not rejected; the domain-match check still blocks the
    /// reported cross-domain attack, and single-label suffixes (com, local) are
    /// rejected.
    /// </summary>
    internal static bool TryResolveCookieDomain(
        string originHost,
        string? domainAttr,
        out string domain,
        out bool hostOnly)
    {
        var origin = CanonicalDomain(originHost);
        domain = origin;
        hostOnly = true;
        if (origin.Length == 0)
        {
            return false;
        }

        if (domainAttr is null)
        {
            return true;
        }

        var dom = CanonicalDomain(domainAttr);
        if (dom.Length == 0 || string.Equals(dom, origin, StringComparison.Ordinal))
        {
            return true;
        }

        if (dom.Contains('.', StringComparison.Ordinal)
            && origin.EndsWith($".{dom}", StringComparison.Ordinal))
        {
            domain = dom;
            hostOnly = false;
            return true;
        }

        return true;
    }

    /// <summary>
    /// RFC 6265 5.1.4 path-match. A bare "starts with" over-matches sibling paths
    /// that share a string prefix (a Path=/admin cookie leaking to /administrator),
    /// so a prefix match also requires the boundary to fall on a '/'.
    /// </summary>
    internal static bool PathMatches(string requestPath, string cookiePath)
    {
        if (string.Equals(requestPath, cookiePath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!requestPath.StartsWith(cookiePath, StringComparison.Ordinal))
        {
            return false;
        }

        // Prefix match: exact when the cookie-path already ends in '/', otherwise the
        // next char of the request-path must be the '/' boundary.
        return cookiePath.EndsWith('/')
            || (cookiePath.Length < requestPath.Length && requestPath[cookiePath.Length] == '/');
    }

    /// <summary>
    /// RFC 6265 domain-match. Runs per fetch (every subresource on a page) and walks
    /// every domain in the jar, so it avoids allocating.
    /// </summary>
    internal static bool DomainMatches(string host, string domain)
    {
        var trimmed = domain.AsSpan().TrimStart('.');
        if (host.Length < trimmed.Length)
        {
            return false;
        }

        // Exact match (case-insensitive)
        if (host.AsSpan().Equals(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Suffix match with a '.' boundary: host = "sub.example.com",
        // domain = "example.com". The character before the suffix in host must be '.'.
        var prefixLength = host.Length - trimmed.Length;
        if (prefixLength < 1)
        {
            return false;
        }

        if (host[prefixLength - 1] != '.')
        {
            return false;
        }

        return host.AsSpan(prefixLength).Equals(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static (string First, string? Remainder) SplitOnce(string value, char separator)
    {
        var index = value.IndexOf(separator);
        return index < 0 ? (value, null) : (value[..index], value[(index + 1)..]);
    }

    private static bool TrySplitOnce(string value, char separator, out string left, out string right)
    {
        var index = value.IndexOf(separator);
        if (index < 0)
        {
            left = value;
            right = string.Empty;
            return false;
        }

        left = value[..index];
        right = value[(index + 1)..];
        return true;
    }
}
