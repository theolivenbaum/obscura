using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PocketCalculator.Net;

/// <summary>
/// Obscura's own cookie store (port of <c>crates/obscura-net/src/cookies.rs</c>).
/// The HTTP stack's cookie container is never used; every request's Cookie header
/// and every Set-Cookie response goes through this jar.
/// </summary>
public sealed class CookieJar
{
    private const string DefaultSameSite = "Lax";

    // Chromium's limits (net/cookies/cookie_monster.h and parsed_cookie.h).
    private const int MaxCookieNameValueBytes = 4096;
    private const int MaxCookiesPerDomain = 180;
    private const int PurgedCookiesPerDomain = 150;
    private const int MaxCookiesTotal = 3300;
    private const int PurgedCookiesTotal = 3000;

    /// <summary>
    /// domain -> (name, path) -> entry. RFC 6265 section 5.3 identifies a cookie by
    /// (name, domain, path); the outer map scopes by domain and the inner key
    /// carries name+path so same-name cookies on different paths coexist instead of
    /// clobbering each other.
    /// </summary>
    private readonly Dictionary<string, Dictionary<(string Name, string Path), CookieEntry>> _cookies =
        new(StringComparer.Ordinal);

    private readonly System.Threading.Lock _lock = new();

    /// <summary>Monotonic use counter behind the least-recently-used eviction order.</summary>
    private long _accessClock;

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

        /// <summary>When the cookie was last stored or sent, on the jar's use counter.</summary>
        public long LastAccess { get; set; }
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

        // Deviation from cookies.rs, which stores a cookie of any size (SECURITY.md M5):
        // Chromium drops one whose name and value together exceed 4096 bytes.
        if (System.Text.Encoding.UTF8.GetByteCount(name) + System.Text.Encoding.UTF8.GetByteCount(value)
            > MaxCookieNameValueBytes)
        {
            return;
        }

        var requestHost = HostOf(url).ToLowerInvariant();
        string? domainAttr = null;
        var path = DefaultCookiePath(url.AbsolutePath);
        var hasPathAttr = false;
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
                            hasPathAttr = true;
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
        // public-suffix Domain rejects the cookie so a response from attacker.test
        // cannot scope a cookie to victim.test (GHSA-f22c-8v6q-v6h6).
        if (!TryResolveCookieDomain(requestHost, domainAttr, out var domain, out var hostOnly))
        {
            return;
        }

        // RFC 6265bis: an insecure origin cannot set a Secure cookie, and
        // SameSite=None is only accepted together with Secure.
        var sourceIsSecure = string.Equals(url.Scheme, "https", StringComparison.Ordinal);
        if ((secure && !sourceIsSecure) || (sameSite == "None" && !secure))
        {
            return;
        }

        if (!PrefixRulesHold(name, value, secure, sourceIsSecure, domainAttr, hasPathAttr ? path : null))
        {
            return;
        }

        lock (_lock)
        {
            if (!sourceIsSecure && SecureCookieConflicts(name, domain, path))
            {
                return;
            }

            StoreLocked(
                new CookieEntry
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
                },
                fromJavaScript);
        }
    }

    /// <summary>Delete or insert <paramref name="entry"/>. Caller holds the lock.</summary>
    private void StoreLocked(CookieEntry entry, bool fromJavaScript)
    {
        var (name, path, domain) = (entry.Name, entry.Path, entry.Domain);
        if (entry.Expires is { } exp && exp <= Now())
        {
            if (_cookies.TryGetValue(domain, out var doomedIn))
            {
                // RFC 6265 5.3: a non-HTTP API (document.cookie) must not delete an
                // existing HttpOnly cookie.
                if (fromJavaScript
                    && doomedIn.TryGetValue((name, path), out var doomed)
                    && doomed.HttpOnly)
                {
                    return;
                }

                doomedIn.Remove((name, path));
            }

            return;
        }

        if (!_cookies.TryGetValue(domain, out var domainCookies))
        {
            domainCookies = [];
            _cookies[domain] = domainCookies;
        }

        // RFC 6265 5.3: a non-HTTP API (document.cookie) must not overwrite an
        // existing HttpOnly cookie set by the server.
        if (fromJavaScript
            && domainCookies.TryGetValue((name, path), out var existing)
            && existing.HttpOnly)
        {
            return;
        }

        entry.LastAccess = ++_accessClock;
        var added = !domainCookies.ContainsKey((name, path));
        domainCookies[(name, path)] = entry;
        if (added)
        {
            EnforceLimitsLocked(domainCookies);
        }
    }

    /// <summary>
    /// The cookie-prefix rules of RFC 6265bis section 4.1.3, matched case-insensitively
    /// as Chromium does: <c>__Secure-</c> needs the Secure attribute from a secure
    /// origin, and <c>__Host-</c> additionally needs no Domain attribute and
    /// <c>Path=/</c>. A nameless cookie whose value carries a prefix is refused too, so
    /// it cannot pose as one in the <c>Cookie</c> header.
    /// </summary>
    /// <remarks>
    /// Deviation from cookies.rs, which does not enforce prefixes (SECURITY.md M5): a
    /// sibling subdomain could plant <c>__Host-session</c> with <c>Domain=parent</c>.
    /// </remarks>
    private static bool PrefixRulesHold(
        string name,
        string value,
        bool secure,
        bool sourceIsSecure,
        string? domainAttr,
        string? pathAttr)
    {
        var subject = name.Length == 0 ? value : name;
        var isHost = subject.StartsWith("__Host-", StringComparison.OrdinalIgnoreCase);
        var isSecure = isHost || subject.StartsWith("__Secure-", StringComparison.OrdinalIgnoreCase);
        if (name.Length == 0)
        {
            return !isSecure;
        }

        if (isSecure && !(secure && sourceIsSecure))
        {
            return false;
        }

        return !isHost || (domainAttr is null && string.Equals(pathAttr, "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Chromium's jar limits, applied after a new cookie lands. A domain over
    /// <see cref="MaxCookiesPerDomain"/> is purged to <see cref="PurgedCookiesPerDomain"/>,
    /// and the jar over <see cref="MaxCookiesTotal"/> to <see cref="PurgedCookiesTotal"/>:
    /// expired cookies go first, then the least recently used. Caller holds the lock.
    /// </summary>
    /// <remarks>
    /// Deviation from cookies.rs, whose jar is unbounded (SECURITY.md M5). Chromium
    /// counts per registrable domain and weighs priority and secureness; this counts
    /// per jar domain and orders by expiry and last use only, which the global cap
    /// backstops against a page spreading cookies over many subdomains.
    /// </remarks>
    private void EnforceLimitsLocked(Dictionary<(string Name, string Path), CookieEntry> domainCookies)
    {
        if (domainCookies.Count > MaxCookiesPerDomain)
        {
            Purge([domainCookies], PurgedCookiesPerDomain);
        }

        var total = 0;
        foreach (var entries in _cookies.Values)
        {
            total += entries.Count;
        }

        if (total > MaxCookiesTotal)
        {
            Purge([.. _cookies.Values], PurgedCookiesTotal);
        }
    }

    private static void Purge(
        List<Dictionary<(string Name, string Path), CookieEntry>> scopes,
        int keep)
    {
        var now = Now();
        var candidates = new List<(Dictionary<(string Name, string Path), CookieEntry> Scope, CookieEntry Entry)>();
        foreach (var scope in scopes)
        {
            foreach (var entry in scope.Values)
            {
                candidates.Add((scope, entry));
            }
        }

        // Expired first, then least recently used.
        candidates.Sort((a, b) =>
        {
            var aExpired = a.Entry.Expires is { } ae && ae <= now;
            var bExpired = b.Entry.Expires is { } be && be <= now;
            return aExpired != bExpired
                ? (aExpired ? -1 : 1)
                : a.Entry.LastAccess.CompareTo(b.Entry.LastAccess);
        });

        var excess = candidates.Count - keep;
        for (var i = 0; i < excess; i++)
        {
            var (scope, entry) = candidates[i];
            scope.Remove((entry.Name, entry.Path));
        }
    }

    /// <summary>
    /// RFC 6265bis "Leave Secure Cookies Alone": an insecure response must not replace
    /// or shadow a Secure cookie. Cookie writes are rare, so scanning the jar here is
    /// preferable to adding another index to every request. Caller holds the lock.
    /// </summary>
    private bool SecureCookieConflicts(string name, string domain, string path)
    {
        foreach (var (storedDomain, entries) in _cookies)
        {
            if (!DomainMatches(domain, storedDomain) && !DomainMatches(storedDomain, domain))
            {
                continue;
            }

            foreach (var entry in entries.Values)
            {
                if (entry.Secure
                    && string.Equals(entry.Name, name, StringComparison.Ordinal)
                    && (PathMatches(path, entry.Path) || PathMatches(entry.Path, path)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>Cookie</c> request header value for a same-site request to
    /// <paramref name="url"/>. Callers that know the request's initiator use
    /// <see cref="GetCookieHeaderInContext"/> so the SameSite policy is enforced.
    /// </summary>
    public string GetCookieHeader(Uri url) => GetCookieHeaderSameSite(url);

    /// <summary>The <c>Cookie</c> header for a request known to be same-site.</summary>
    public string GetCookieHeaderSameSite(Uri url) =>
        GetCookieHeaderInContext(url, SameSiteContext.SameSite);

    /// <summary>The <c>Cookie</c> header for a request in the given site context.</summary>
    public string GetCookieHeaderInContext(Uri url, SameSiteContext context) =>
        Collect(url, jsVisibleOnly: false, context);

    /// <summary>The <c>document.cookie</c> value for <paramref name="url"/>: HttpOnly cookies are hidden.</summary>
    public string GetJsVisibleCookies(Uri url) =>
        Collect(url, jsVisibleOnly: true, SameSiteContext.SameSite);

    /// <summary>
    /// Registrable-domain same-site check (port of <c>same_site</c> in
    /// <c>cookies.rs</c>): same scheme and the same eTLD+1, where a host that has no
    /// registrable domain (a public suffix itself) stands for its own site.
    /// </summary>
    /// <remarks>
    /// Deviation: Rust hands an IP address to <c>psl::domain_str</c> too, which treats
    /// the dotted quad as labels, so <c>10.0.0.1</c> and <c>192.168.0.1</c> share the
    /// "site" <c>0.1</c>. Chromium's site for an IP host is the whole address, and so is
    /// this one.
    /// </remarks>
    public static bool IsSameSite(Uri source, Uri target)
    {
        if (!string.Equals(source.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sourceHost = HostOf(source);
        var targetHost = HostOf(target);
        if (sourceHost.Length == 0 || targetHost.Length == 0)
        {
            return false;
        }

        return SiteOf(source, sourceHost).Equals(SiteOf(target, targetHost), StringComparison.OrdinalIgnoreCase);
    }

    // A slice of the host, so the per-request same-site check allocates nothing (the
    // suffix lookup is case-insensitive).
    private static ReadOnlySpan<char> SiteOf(Uri url, string host)
    {
        if (url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return host;
        }

        return PublicSuffixList.TryGetRegistrableDomain(host, out var site) ? site : host;
    }

    /// <summary>
    /// The site context of a scripted fetch/XHR from a page whose serialized origin is
    /// <paramref name="initiatorOrigin"/> (port of the context <c>op_fetch_url</c>
    /// computes). An origin that does not parse, such as <c>null</c>, is cross-site to
    /// everything.
    /// </summary>
    public static SameSiteContext ContextForInitiator(string? initiatorOrigin, Uri target) =>
        Uri.TryCreate(initiatorOrigin, UriKind.Absolute, out var source) && IsSameSite(source, target)
            ? SameSiteContext.SameSite
            : SameSiteContext.CrossSite;

    private string Collect(Uri url, bool jsVisibleOnly, SameSiteContext context)
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

                    var sameSiteAllows = context switch
                    {
                        SameSiteContext.CrossSiteTopLevelSafe => entry.SameSite != "Strict",
                        SameSiteContext.CrossSite => entry.SameSite == "None",
                        _ => true,
                    };
                    if (!sameSiteAllows)
                    {
                        continue;
                    }

                    if (!PathMatches(path, entry.Path))
                    {
                        continue;
                    }

                    entry.LastAccess = ++_accessClock;
                    matching.Add($"{entry.Name}={entry.Value}");
                }
            }
        }

        return string.Join("; ", matching);
    }

    /// <summary>Every non-expired cookie in the jar.</summary>
    public List<CookieInfo> GetAllCookies()
    {
        var all = GetAllCookiesWithScope();
        var result = new List<CookieInfo>(all.Count);
        foreach (var (cookie, _) in all)
        {
            result.Add(cookie);
        }

        return result;
    }

    /// <summary>
    /// Every non-expired cookie in the jar, with the host-only flag the public CDP
    /// model has no field for. Pair with <see cref="SetCookiesFromCdpWithScope"/> to
    /// move cookies between jars without widening a host-only cookie to subdomains.
    /// </summary>
    public List<(CookieInfo Cookie, bool HostOnly)> GetAllCookiesWithScope()
    {
        var now = Now();
        var result = new List<(CookieInfo, bool)>();
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

                    result.Add((ToInfo(entry), entry.HostOnly));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Import cookies from CDP or a persisted store. A zero/past expiry deletes. Each
    /// cookie is domain-scoped: its Domain field has always meant that.
    /// </summary>
    public void SetCookiesFromCdp(IEnumerable<CookieInfo> cookies)
    {
        var scoped = new List<(CookieInfo, bool)>();
        foreach (var cookie in cookies)
        {
            scoped.Add((cookie, false));
        }

        SetCookiesFromImport(scoped);
    }

    /// <summary>
    /// Import CDP cookies while preserving whether each was created from a URL
    /// (host-only) or from an explicit Domain field (domain-scoped).
    /// </summary>
    public void SetCookiesFromCdpWithScope(IEnumerable<(CookieInfo Cookie, bool HostOnly)> cookies) =>
        SetCookiesFromImport(cookies);

    /// <summary>
    /// Replace this jar with an independent copy of <paramref name="source"/>,
    /// including the host-only scope the public CDP model leaves out.
    /// </summary>
    public void CopyFrom(CookieJar source)
    {
        if (ReferenceEquals(this, source))
        {
            return;
        }

        // Entries are immutable, so copying the two map levels is a full copy.
        Dictionary<string, Dictionary<(string Name, string Path), CookieEntry>> snapshot =
            new(StringComparer.Ordinal);
        lock (source._lock)
        {
            foreach (var (domain, entries) in source._cookies)
            {
                snapshot[domain] = new Dictionary<(string Name, string Path), CookieEntry>(entries);
            }
        }

        lock (_lock)
        {
            _cookies.Clear();
            foreach (var (domain, entries) in snapshot)
            {
                _cookies[domain] = entries;
            }
        }
    }

    private void SetCookiesFromImport(IEnumerable<(CookieInfo Cookie, bool HostOnly)> cookies)
    {
        var now = Now();
        lock (_lock)
        {
            foreach (var (cookie, hostOnly) in cookies)
            {
                // RFC 6265 4.1.2.3: the leading dot is ignored. The Set-Cookie path
                // already strips it, but this code did not, which is why one cookie
                // became two entries.
                var domain = CanonicalDomain(cookie.Domain);
                if (domain.Length == 0
                    || (PublicSuffixList.IsPublicSuffix(domain)
                        && domain != "localhost"
                        && !System.Net.IPAddress.TryParse(domain, out _)))
                {
                    continue;
                }

                var sameSite = cookie.SameSite.Length == 0
                    ? DefaultSameSite
                    : NormalizeSameSite(cookie.SameSite);
                if (sameSite == "None" && !cookie.Secure)
                {
                    continue;
                }

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

                var entry = new CookieEntry
                {
                    Name = cookie.Name,
                    Value = cookie.Value,
                    Path = cookie.Path,
                    Domain = domain,
                    HostOnly = hostOnly,
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
    /// Create <paramref name="directory"/> (and any missing parents) owner-only
    /// (0700) on Unix. An existing directory keeps the mode it has.
    /// </summary>
    public static void CreateOwnerOnlyDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(
                directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Serialize all non-expired cookies to a JSON file. Writes atomically via a
    /// temp file then rename. The file is owner-only on Unix.
    /// </summary>
    public void SaveToFile(string path)
    {
        var all = new List<PersistedCookie>();
        foreach (var (cookie, hostOnly) in GetAllCookiesWithScope())
        {
            all.Add(PersistedCookie.From(cookie, hostOnly));
        }

        var json = JsonSerializer.Serialize(all, CookieJsonOptions);
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent))
        {
            CreateOwnerOnlyDirectory(parent);
        }

        var tmp = Path.Combine(
            string.IsNullOrEmpty(parent) ? "." : parent,
            $".obscura-cookies-{Guid.NewGuid():N}.tmp");
        // Deviation (SECURITY.md I4): Rust writes the jar with the process umask,
        // which on most systems leaves every session cookie readable by other
        // local users. The port creates the file owner-only (0600) on Unix; the
        // rename keeps that mode. Windows files inherit the directory's ACL.
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(tmp, options))
        {
            stream.Write(new UTF8Encoding(false).GetBytes(json));
        }

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
        List<PersistedCookie>? cookies;
        try
        {
            cookies = JsonSerializer.Deserialize<List<PersistedCookie>>(data, CookieJsonOptions);
        }
        catch (JsonException error)
        {
            throw new IOException($"invalid cookie store {path}", error);
        }

        cookies ??= [];
        var scoped = new List<(CookieInfo, bool)>(cookies.Count);
        foreach (var persisted in cookies)
        {
            // Absent in older and third-party files, whose Domain field has always
            // meant a domain-scoped cookie.
            scoped.Add((persisted.ToInfo(), persisted.HostOnly ?? false));
        }

        SetCookiesFromImport(scoped);
        return cookies.Count;
    }

    private static readonly JsonSerializerOptions CookieJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// One <c>cookies.json</c> entry: the <see cref="CookieInfo"/> fields, then an
    /// optional <c>hostOnly</c>, in the order serde writes Rust's
    /// <c>PersistedCookie</c> (<c>#[serde(flatten)] cookie</c> followed by
    /// <c>hostOnly</c>). Obscura always writes <c>hostOnly</c>; a file without it
    /// still loads.
    /// </summary>
    private sealed class PersistedCookie
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("domain")]
        public string Domain { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("secure")]
        public bool Secure { get; set; }

        [JsonPropertyName("httpOnly")]
        public bool HttpOnly { get; set; }

        [JsonPropertyName("sameSite")]
        public string SameSite { get; set; } = string.Empty;

        [JsonPropertyName("expires")]
        public long? Expires { get; set; }

        [JsonPropertyName("hostOnly")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? HostOnly { get; set; }

        public static PersistedCookie From(CookieInfo cookie, bool hostOnly) => new()
        {
            Name = cookie.Name,
            Value = cookie.Value,
            Domain = cookie.Domain,
            Path = cookie.Path,
            Secure = cookie.Secure,
            HttpOnly = cookie.HttpOnly,
            SameSite = cookie.SameSite,
            Expires = cookie.Expires,
            HostOnly = hostOnly,
        };

        public CookieInfo ToInfo() => new()
        {
            Name = Name,
            Value = Value,
            Domain = Domain,
            Path = Path,
            Secure = Secure,
            HttpOnly = HttpOnly,
            SameSite = SameSite,
            Expires = Expires,
        };
    }

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
    /// parent domain) and is not a public suffix. An invalid Domain rejects the cookie
    /// (returns false) rather than silently changing its scope; this is what stops a
    /// response from attacker.test planting a cookie scoped to victim.test. Per RFC
    /// 6265, a public suffix equal to the origin host is kept as a host-only cookie.
    ///
    /// The public suffix list is the full Mozilla list, private section included, as
    /// Chromium uses it for cookies.
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

        // Deviation: Rust rejects an empty Domain (`Domain=`). Chromium and RFC
        // 6265bis 5.6.3 ignore the attribute, so the cookie stays host-only.
        if (dom.Length == 0)
        {
            return true;
        }

        // Deviation: Rust runs an IP host through the suffix check too, which lets
        // 10.0.0.1 set Domain=0.0.1. Chromium accepts a Domain on an IP host only when
        // it is the address itself, and stores a host cookie.
        if (System.Net.IPAddress.TryParse(origin, out _))
        {
            return string.Equals(dom, origin, StringComparison.Ordinal);
        }

        if (PublicSuffixList.IsPublicSuffix(dom))
        {
            return string.Equals(dom, origin, StringComparison.Ordinal);
        }

        if (string.Equals(dom, origin, StringComparison.Ordinal)
            || (origin.Length > dom.Length
                && origin.EndsWith(dom, StringComparison.Ordinal)
                && origin[origin.Length - dom.Length - 1] == '.'))
        {
            domain = dom;
            hostOnly = false;
            return true;
        }

        return false;
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
