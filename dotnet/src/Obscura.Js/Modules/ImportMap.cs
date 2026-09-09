using System.Globalization;
using System.Text.Json;

using Obscura.Js.Url;

namespace Obscura.Js.Modules;

/// <summary>
/// A parsed WHATWG import map and the specifier resolution it drives.
/// </summary>
/// <remarks>
/// <para>
/// Port of <c>crates/obscura-js/src/import_map.rs</c>. The resolution order is
/// observable and is reproduced exactly: the most specific applicable scope is
/// consulted first, an exact key beats a prefix key, and among prefix keys the
/// longest wins because the prefix list is kept sorted in descending ordinal
/// order and the first match returns.
/// </para>
/// <para>
/// Every URL operation goes through <see cref="UrlRecord"/>, the WHATWG URL
/// implementation, and never through <c>System.Uri</c>: import-map resolution
/// <em>is</em> URL resolution, and the two disagree observably on backtracking,
/// on default ports, and on percent-encoding.
/// </para>
/// <para>
/// Nothing here throws for malformed input. Parsing and resolution both report
/// failure as a message string, exactly as the Rust <c>Result&lt;_, String&gt;</c>
/// does, because both are reachable from a module callback that V8 invoked.
/// </para>
/// </remarks>
public sealed class ImportMap
{
    private SpecifierMap _imports = new();
    private List<ScopeEntry> _scopes = [];
    private readonly List<ResolvedModule> _resolvedModules = [];

    /// <summary>An import map with no rules. Every specifier resolves as a plain URL.</summary>
    public ImportMap()
    {
    }

    /// <summary>
    /// Parse an import map document. Returns false and sets <paramref name="error"/>
    /// when the map is malformed; a malformed <c>scopes</c> or <c>integrity</c>
    /// member invalidates the complete map rather than leaving <c>imports</c> live,
    /// which is what Chromium and the HTML algorithm do.
    /// </summary>
    public static bool TryParse(
        string input,
        string baseUrl,
        out ImportMap map,
        out string? error)
    {
        map = new ImportMap();

        var baseRecord = UrlRecord.Parse(baseUrl);
        if (baseRecord is null)
        {
            error = string.Format(
                CultureInfo.InvariantCulture,
                "Invalid import map base URL {0}: relative URL without a base",
                baseUrl);
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input);
        }
        catch (JsonException ex)
        {
            error = "Invalid import map JSON: " + ex.Message;
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Import map top level must be an object";
                return false;
            }

            var parsed = new ImportMap();

            if (root.TryGetProperty("imports", out var importsValue))
            {
                if (importsValue.ValueKind != JsonValueKind.Object)
                {
                    error = "Import map \"imports\" must be an object";
                    return false;
                }

                parsed._imports = SpecifierMap.Parse(importsValue, baseRecord);
            }

            if (root.TryGetProperty("scopes", out var scopesValue))
            {
                if (scopesValue.ValueKind != JsonValueKind.Object)
                {
                    error = "Import map \"scopes\" must be an object";
                    return false;
                }

                foreach (var scope in scopesValue.EnumerateObject())
                {
                    // Unlike keys and addresses in a module specifier map, scope
                    // prefixes use ordinary URL parsing. In particular, a bare
                    // relative value such as "feature/" is valid here.
                    var normalizedScope = baseRecord.Join(scope.Name);
                    if (normalizedScope is null)
                    {
                        continue;
                    }

                    if (scope.Value.ValueKind != JsonValueKind.Object)
                    {
                        error = string.Format(
                            CultureInfo.InvariantCulture,
                            "Import map scope \"{0}\" must contain an object",
                            scope.Name);
                        return false;
                    }

                    parsed._scopes.Add(new ScopeEntry(
                        normalizedScope.Href,
                        SpecifierMap.Parse(scope.Value, baseRecord)));
                }

                // The most specific applicable scope is consulted first.
                parsed.SortScopes();
            }

            // Integrity enforcement is owned by the module fetch layer. Still
            // validate the top-level shape here: a malformed integrity member
            // invalidates the complete import map in Chromium/the HTML algorithm,
            // rather than leaving its `imports` member active.
            if (root.TryGetProperty("integrity", out var integrityValue)
                && integrityValue.ValueKind != JsonValueKind.Object)
            {
                error = "Import map \"integrity\" must be an object";
                return false;
            }

            map = parsed;
            error = null;
            return true;
        }
    }

    /// <summary>
    /// Fold a later import map into this one.
    /// </summary>
    /// <remarks>
    /// Multiple import maps are merged even after module graphs have started. New
    /// rules which could change an already-observed (referrer, specifier)
    /// resolution are removed first; unrelated rules remain available to later
    /// graphs.
    /// </remarks>
    public void Merge(ImportMap newMap)
    {
        ArgumentNullException.ThrowIfNull(newMap);

        foreach (var record in _resolvedModules)
        {
            newMap._imports.RemoveRulesAffecting(record.Specifier, record.AsUrlIsSpecial);
            foreach (var scope in newMap._scopes)
            {
                if (ScopeApplies(scope.Prefix, record.Referrer))
                {
                    scope.Imports.RemoveRulesAffecting(record.Specifier, record.AsUrlIsSpecial);
                }
            }
        }

        _imports.Merge(newMap._imports);
        foreach (var incoming in newMap._scopes)
        {
            var existing = FindScope(incoming.Prefix);
            if (existing is not null)
            {
                existing.Imports.Merge(incoming.Imports);
            }
            else
            {
                _scopes.Add(incoming);
            }
        }

        SortScopes();
    }

    /// <summary>
    /// Parse an inline document import map and merge it into this one, the shape
    /// <c>ObscuraJsRuntime::add_import_map</c> uses. A malformed map is discarded
    /// whole - it never partially applies - and reported through the return value.
    /// </summary>
    public bool MergeParsed(string source, string baseUrl, out string? error)
    {
        if (!TryParse(source, baseUrl, out var parsed, out error))
        {
            return false;
        }

        Merge(parsed);
        return true;
    }

    /// <summary>
    /// Parse and merge an inline document import map, ignoring a malformed one.
    /// </summary>
    public bool MergeParsed(string source, string baseUrl) => MergeParsed(source, baseUrl, out _);

    /// <summary>
    /// Resolve <paramref name="specifier"/> against <paramref name="referrer"/>.
    /// Returns false and sets <paramref name="error"/> for a bare specifier the map
    /// does not remap, for a specifier the map blocks, and for a prefix rule that
    /// backtracks above its own address.
    /// </summary>
    public bool TryResolve(
        string specifier,
        UrlRecord referrer,
        out UrlRecord? resolved,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(referrer);

        resolved = null;
        var asUrl = ResolveUrlLike(specifier, referrer);
        var normalized = asUrl?.Href ?? specifier;
        var serializedReferrer = referrer.Href;

        foreach (var scope in _scopes)
        {
            if (!ScopeApplies(scope.Prefix, serializedReferrer))
            {
                continue;
            }

            if (!scope.Imports.TryResolveMatch(normalized, asUrl, out var scoped, out error))
            {
                return false;
            }

            if (scoped is not null)
            {
                RememberResolution(serializedReferrer, normalized, asUrl);
                resolved = scoped;
                return true;
            }
        }

        if (!_imports.TryResolveMatch(normalized, asUrl, out var mapped, out error))
        {
            return false;
        }

        if (mapped is not null)
        {
            RememberResolution(serializedReferrer, normalized, asUrl);
            resolved = mapped;
            return true;
        }

        if (asUrl is not null)
        {
            RememberResolution(serializedReferrer, normalized, asUrl);
            resolved = asUrl;
            error = null;
            return true;
        }

        error = string.Format(
            CultureInfo.InvariantCulture,
            "Bare module specifier \"{0}\" was not remapped by the import map",
            specifier);
        return false;
    }

    private void RememberResolution(string referrer, string specifier, UrlRecord? asUrl)
    {
        var resolution = new ResolvedModule(
            referrer,
            specifier,
            asUrl is null || IsSpecialUrl(asUrl));
        if (!_resolvedModules.Contains(resolution))
        {
            _resolvedModules.Add(resolution);
        }
    }

    private ScopeEntry? FindScope(string prefix)
    {
        foreach (var scope in _scopes)
        {
            if (string.Equals(scope.Prefix, prefix, StringComparison.Ordinal))
            {
                return scope;
            }
        }

        return null;
    }

    /// <summary>
    /// Descending ordinal order, so the longest (most specific) applicable scope
    /// prefix is consulted first. <see cref="List{T}.Sort(Comparison{T})"/> is not
    /// stable, and two raw scope keys can normalize to the same prefix, so this
    /// goes through a stable ordering instead.
    /// </summary>
    private void SortScopes() =>
        _scopes = [.. _scopes.OrderByDescending(scope => scope.Prefix, StringComparer.Ordinal)];

    internal static bool ScopeApplies(string scopePrefix, string referrer) =>
        string.Equals(scopePrefix, referrer, StringComparison.Ordinal)
        || (scopePrefix.EndsWith('/') && referrer.StartsWith(scopePrefix, StringComparison.Ordinal));

    internal static bool IsSpecialUrl(UrlRecord url) => url.Scheme switch
    {
        "ftp" or "file" or "http" or "https" or "ws" or "wss" => true,
        _ => false,
    };

    /// <summary>
    /// Resolve a specifier the way an import map key or address is resolved: a
    /// path-relative form joins the base, anything else must already be an
    /// absolute URL. A bare specifier yields null and stays a candidate for
    /// remapping.
    /// </summary>
    internal static UrlRecord? ResolveUrlLike(string specifier, UrlRecord baseUrl) =>
        specifier.StartsWith('/')
        || specifier.StartsWith("./", StringComparison.Ordinal)
        || specifier.StartsWith("../", StringComparison.Ordinal)
            ? baseUrl.Join(specifier)
            : UrlRecord.Parse(specifier);

    private static string? NormalizeKey(string key, UrlRecord baseUrl) =>
        key.Length == 0 ? null : ResolveUrlLike(key, baseUrl)?.Href ?? key;

    private readonly record struct ResolvedModule(
        string Referrer,
        string Specifier,
        bool AsUrlIsSpecial);

    private sealed class ScopeEntry(string prefix, SpecifierMap imports)
    {
        public string Prefix { get; } = prefix;

        public SpecifierMap Imports { get; } = imports;
    }

    /// <summary>
    /// One module specifier map: exact keys plus the trailing-slash keys that act
    /// as prefix mappings. A null address is a deliberate block, not a miss.
    /// </summary>
    private sealed class SpecifierMap
    {
        private readonly Dictionary<string, UrlRecord?> _entries = new(StringComparer.Ordinal);
        private List<string> _prefixes = [];

        public static SpecifierMap Parse(JsonElement obj, UrlRecord baseUrl)
        {
            var map = new SpecifierMap();
            foreach (var member in obj.EnumerateObject())
            {
                var normalizedKey = NormalizeKey(member.Name, baseUrl);
                if (normalizedKey is null)
                {
                    continue;
                }

                UrlRecord? address = null;
                if (member.Value.ValueKind == JsonValueKind.String)
                {
                    address = ResolveUrlLike(member.Value.GetString() ?? string.Empty, baseUrl);
                    // A trailing-slash key may only map to a trailing-slash
                    // address. The test is against the raw key, as in Rust.
                    if (address is not null
                        && member.Name.EndsWith('/')
                        && !address.Href.EndsWith('/'))
                    {
                        address = null;
                    }
                }

                if (normalizedKey.EndsWith('/'))
                {
                    map._prefixes.Add(normalizedKey);
                }

                map._entries[normalizedKey] = address;
            }

            map.SortPrefixes();
            return map;
        }

        public void Merge(SpecifierMap newMap)
        {
            foreach (var (key, address) in newMap._entries)
            {
                if (_entries.ContainsKey(key))
                {
                    continue;
                }

                if (key.EndsWith('/'))
                {
                    _prefixes.Add(key);
                }

                _entries[key] = address;
            }

            SortPrefixes();
        }

        public void RemoveRulesAffecting(string resolved, bool asUrlIsSpecial)
        {
            List<string>? doomed = null;
            foreach (var key in _entries.Keys)
            {
                var affected = string.Equals(key, resolved, StringComparison.Ordinal)
                    || (asUrlIsSpecial
                        && key.EndsWith('/')
                        && resolved.StartsWith(key, StringComparison.Ordinal));
                if (affected)
                {
                    (doomed ??= []).Add(key);
                }
            }

            if (doomed is null)
            {
                return;
            }

            foreach (var key in doomed)
            {
                _entries.Remove(key);
            }

            _prefixes.RemoveAll(key => !_entries.ContainsKey(key));
        }

        /// <summary>
        /// Returns false with an error for a blocked or malformed match. On success
        /// <paramref name="resolved"/> is null when this map simply does not apply.
        /// </summary>
        public bool TryResolveMatch(
            string normalized,
            UrlRecord? asUrl,
            out UrlRecord? resolved,
            out string? error)
        {
            resolved = null;
            error = null;

            if (_entries.TryGetValue(normalized, out var address))
            {
                if (address is null)
                {
                    error = string.Format(
                        CultureInfo.InvariantCulture,
                        "Module specifier \"{0}\" is blocked by import map entry \"{1}\"",
                        normalized,
                        normalized);
                    return false;
                }

                resolved = address;
                return true;
            }

            // A non-special absolute URL never participates in prefix matching.
            if (asUrl is not null && !IsSpecialUrl(asUrl))
            {
                return true;
            }

            foreach (var key in _prefixes)
            {
                if (!normalized.StartsWith(key, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!_entries.TryGetValue(key, out var prefixAddress))
                {
                    error = string.Format(
                        CultureInfo.InvariantCulture,
                        "Import map prefix \"{0}\" has no matching address",
                        key);
                    return false;
                }

                if (prefixAddress is null)
                {
                    error = string.Format(
                        CultureInfo.InvariantCulture,
                        "Module specifier \"{0}\" is blocked by import map prefix \"{1}\"",
                        normalized,
                        key);
                    return false;
                }

                var afterPrefix = normalized[key.Length..];
                var joined = prefixAddress.Join(afterPrefix);
                if (joined is null)
                {
                    error = string.Format(
                        CultureInfo.InvariantCulture,
                        "Module specifier \"{0}\" could not resolve through import map prefix \"{1}\": relative URL without a base",
                        normalized,
                        key);
                    return false;
                }

                if (!joined.Href.StartsWith(prefixAddress.Href, StringComparison.Ordinal))
                {
                    error = string.Format(
                        CultureInfo.InvariantCulture,
                        "Module specifier \"{0}\" backtracks above import map prefix \"{1}\"",
                        normalized,
                        key);
                    return false;
                }

                resolved = joined;
                return true;
            }

            return true;
        }

        /// <summary>
        /// Descending ordinal order: the first matching prefix therefore wins and is
        /// the longest one, since a longer key that shares a prefix sorts ahead of a
        /// shorter one.
        /// </summary>
        private void SortPrefixes() =>
            _prefixes = [.. _prefixes.OrderByDescending(key => key, StringComparer.Ordinal)];
    }
}
