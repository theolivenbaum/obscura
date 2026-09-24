using System.Net;

namespace PocketCalculator.Net;

/// <summary>
/// HTTP Strict Transport Security state for one browser context (RFC 6797), kept by its
/// <see cref="PocketCalculatorHttpClient"/>. The Rust engine has none (SECURITY.md I7):
/// a site that sent <c>Strict-Transport-Security</c> could still be reached over plain
/// http on the next visit, where an on-path attacker can serve anything.
/// </summary>
/// <remarks>
/// <para>
/// Behaviour follows Chromium's <c>TransportSecurityState</c>:
/// </para>
/// <list type="bullet">
/// <item>the header is honoured only on an https response that passed certificate
/// validation, and never for an IP-literal host;</item>
/// <item>directive names are case-insensitive, <c>max-age</c> is required, a repeated
/// directive or a malformed value invalidates the whole header, unknown directives are
/// ignored, and only the first header counts;</item>
/// <item><c>max-age</c> is capped at one year, and <c>max-age=0</c> removes the entry;</item>
/// <item>a known host, or a subdomain of a host that set <c>includeSubDomains</c>, has
/// its http URLs rewritten to https before the request leaves (port 80 becomes 443,
/// any other explicit port is kept). The request paths record the rewrite as a redirect
/// hop, Chromium's internal 307.</item>
/// </list>
/// <para>
/// The store is in memory only and starts empty: there is no preload list. A host that
/// wants one can call <see cref="Add"/> before the first navigation.
/// </para>
/// </remarks>
public sealed class HstsStore
{
    /// <summary>Chromium's cap on <c>max-age</c> (<c>kMaxHSTSAgeSecs</c>): one year.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(365);

    private const int MaxEntries = 10_000;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly System.Threading.Lock _lock = new();
    private readonly Func<DateTimeOffset> _now;

    /// <summary>A store on the system clock.</summary>
    public HstsStore()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>A store on the given clock. Test hook.</summary>
    internal HstsStore(Func<DateTimeOffset> now) => _now = now;

    private readonly record struct Entry(DateTimeOffset Expiry, bool IncludeSubdomains);

    /// <summary>Number of live and expired entries held. Test hook.</summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Record a host as HSTS for <paramref name="maxAge"/> (capped at <see cref="MaxAge"/>).
    /// A zero or negative age removes it. IP literals and empty hosts are ignored.
    /// </summary>
    public void Add(string host, TimeSpan maxAge, bool includeSubdomains)
    {
        var key = Canonical(host);
        if (key is null)
        {
            return;
        }

        lock (_lock)
        {
            if (maxAge <= TimeSpan.Zero)
            {
                _entries.Remove(key);
                return;
            }

            if (maxAge > MaxAge)
            {
                maxAge = MaxAge;
            }

            if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
            {
                PruneExpired();
                if (_entries.Count >= MaxEntries)
                {
                    return;
                }
            }

            _entries[key] = new Entry(_now() + maxAge, includeSubdomains);
        }
    }

    /// <summary>
    /// Apply a <c>Strict-Transport-Security</c> header received from <paramref name="url"/>.
    /// Ignored unless the response came over https. Returns whether the header was valid.
    /// </summary>
    public bool ProcessHeader(Uri url, string? header)
    {
        if (header is null || !string.Equals(url.Scheme, "https", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryParseHeader(header, out var maxAge, out var includeSubdomains))
        {
            return false;
        }

        Add(url.Host, maxAge, includeSubdomains);
        return true;
    }

    /// <summary>True when an http request to <paramref name="host"/> must be upgraded.</summary>
    public bool ShouldUpgrade(string host)
    {
        lock (_lock)
        {
            if (_entries.Count == 0)
            {
                return false;
            }
        }

        var key = Canonical(host);
        if (key is null)
        {
            return false;
        }

        lock (_lock)
        {

            var now = _now();
            var candidate = key.AsSpan();
            var lookup = _entries.GetAlternateLookup<ReadOnlySpan<char>>();
            var exact = true;
            while (true)
            {
                if (lookup.TryGetValue(candidate, out var entry)
                    && entry.Expiry > now
                    && (exact || entry.IncludeSubdomains))
                {
                    return true;
                }

                var dot = candidate.IndexOf('.');
                if (dot < 0)
                {
                    return false;
                }

                candidate = candidate[(dot + 1)..];
                exact = false;
            }
        }
    }

    /// <summary>
    /// The https URL an http <paramref name="url"/> is upgraded to when its host is a
    /// known HSTS host, or null when it is not upgraded.
    /// </summary>
    public Uri? Upgrade(Uri url)
    {
        if (!string.Equals(url.Scheme, "http", StringComparison.Ordinal) || !ShouldUpgrade(url.Host))
        {
            return null;
        }

        return ToHttps(url);
    }

    /// <summary>http to https, with port 80 becoming the https default (Chromium's rewrite).</summary>
    public static Uri ToHttps(Uri url) => MixedContent.UpgradeUrl(url);

    /// <summary>
    /// Parse a <c>Strict-Transport-Security</c> value (RFC 6797 6.1, as Chromium's
    /// <c>ParseHSTSHeader</c> applies it).
    /// </summary>
    internal static bool TryParseHeader(string header, out TimeSpan maxAge, out bool includeSubdomains)
    {
        maxAge = TimeSpan.Zero;
        includeSubdomains = false;
        var sawMaxAge = false;
        foreach (var raw in header.Split(';'))
        {
            var directive = raw.Trim(' ', '\t');
            if (directive.Length == 0)
            {
                continue;
            }

            var eq = directive.IndexOf('=');
            var name = (eq < 0 ? directive : directive[..eq]).Trim(' ', '\t');
            var value = eq < 0 ? null : directive[(eq + 1)..].Trim(' ', '\t');
            if (name.Length == 0 || !IsToken(name))
            {
                return false;
            }

            if (name.Equals("max-age", StringComparison.OrdinalIgnoreCase))
            {
                if (sawMaxAge || value is null)
                {
                    return false;
                }

                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                {
                    value = value[1..^1];
                }

                if (value.Length == 0)
                {
                    return false;
                }

                // delta-seconds: digits only. A value too large for a long is clamped,
                // as Chromium clamps to its one-year cap.
                ulong seconds = 0;
                foreach (var c in value)
                {
                    if (c is < '0' or > '9')
                    {
                        return false;
                    }

                    seconds = seconds > (ulong)MaxAge.TotalSeconds ? seconds : (seconds * 10) + (ulong)(c - '0');
                }

                maxAge = TimeSpan.FromSeconds(Math.Min(seconds, (ulong)MaxAge.TotalSeconds));
                sawMaxAge = true;
            }
            else if (name.Equals("includeSubDomains", StringComparison.OrdinalIgnoreCase))
            {
                if (includeSubdomains || value is not null)
                {
                    return false;
                }

                includeSubdomains = true;
            }
            else if (value is not null && value.Length == 0)
            {
                return false;
            }
        }

        return sawMaxAge;
    }

    private static bool IsToken(string name)
    {
        foreach (var c in name)
        {
            if (c <= ' ' || c >= 0x7F || "()<>@,;:\\\"/[]?={}".Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string? Canonical(string host)
    {
        if (host.Length == 0)
        {
            return null;
        }

        var trimmed = host.TrimEnd('.');
        if (trimmed.Length == 0
            || trimmed[0] == '['
            || IPAddress.TryParse(trimmed, out _))
        {
            return null;
        }

        return trimmed.ToLowerInvariant();
    }

    private void PruneExpired()
    {
        var now = _now();
        List<string>? expired = null;
        foreach (var (host, entry) in _entries)
        {
            if (entry.Expiry <= now)
            {
                (expired ??= []).Add(host);
            }
        }

        if (expired is not null)
        {
            foreach (var host in expired)
            {
                _entries.Remove(host);
            }
        }
    }
}
