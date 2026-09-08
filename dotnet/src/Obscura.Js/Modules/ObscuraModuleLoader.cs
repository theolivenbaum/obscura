using System.Globalization;

using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

using Obscura.Js.Url;
using Obscura.Net;

namespace Obscura.Js.Modules;

/// <summary>
/// Raised when a module cannot be resolved or fetched.
/// </summary>
/// <remarks>
/// This is the port of deno_core's <c>ModuleLoaderError</c>. ClearScript invokes
/// the loader through its own managed wrapper and converts a thrown exception
/// into a script error, so the failure reaches JavaScript as a rejected import
/// rather than crossing the interop boundary raw. Nothing else in this namespace
/// throws.
/// </remarks>
public sealed class ModuleLoadException(string message) : Exception(message);

/// <summary>
/// The network context one module fetch runs on: the page's client, cookie jar,
/// identity, interception and callbacks, or the reason there is none.
/// </summary>
/// <remarks>
/// The Rust loader holds a <c>Weak&lt;RefCell&lt;ObscuraState&gt;&gt;</c> and reaches
/// into it per load. Keeping that as a provider delegate keeps the seam with the
/// op layer narrow: the loader never names <c>ObscuraState</c>, and the runtime
/// supplies a closure that reads it (and reports the same failure strings when
/// the state is gone or already borrowed).
/// </remarks>
public readonly record struct ModuleNetworkContext
{
    /// <summary>The page's HTTP client, including its SSRF gate.</summary>
    public ObscuraHttpClient? Client { get; private init; }

    /// <summary>
    /// The stealth transport, when the page has one. A module must travel the same
    /// transport as the document that referenced it, or the cross-transport
    /// mismatch is trivially detectable.
    /// </summary>
    public IStealthHttpClient? Stealth { get; private init; }

    /// <summary>The page's passive request/response callbacks.</summary>
    public CallbackRegistry? Callbacks { get; private init; }

    /// <summary>Why there is no usable network context, or null.</summary>
    public string? Error { get; private init; }

    /// <summary>A usable context.</summary>
    public static ModuleNetworkContext From(
        ObscuraHttpClient client,
        IStealthHttpClient? stealth,
        CallbackRegistry? callbacks) =>
        new() { Client = client, Stealth = stealth, Callbacks = callbacks };

    /// <summary>An unusable context carrying the reason.</summary>
    public static ModuleNetworkContext Failed(string error) => new() { Error = error };
}

/// <summary>
/// ES module loading and the module graph.
/// </summary>
/// <remarks>
/// <para>
/// Port of <c>crates/obscura-js/src/module_loader.rs</c>. The Rust engine implements
/// deno_core's <c>ModuleLoader</c> trait, which has a separate <c>resolve</c> hook and
/// a <c>load</c> hook returning a future. ClearScript has no separate resolve hook: a
/// <see cref="DocumentLoader"/> receives the raw specifier together with the
/// requesting document's <see cref="DocumentInfo"/> and must do both. Resolution is
/// therefore kept as its own public method, <see cref="TryResolve"/>,
/// and the loader overrides call it. The resolution rules, the import map, the
/// loaded/redirected specifier bookkeeping and the dynamic-import activity signal
/// are ported unchanged; only the plumbing is ClearScript's.
/// </para>
/// <para>
/// Module fetches go through <see cref="ObscuraHttpClient"/> so a module graph uses
/// the same cookie jar, identity, redirect and security policy, SSRF gate,
/// interception and callbacks as the entry module.
/// </para>
/// </remarks>
public sealed class ObscuraModuleLoader : DocumentLoader, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Document> _documents = new(StringComparer.Ordinal);

    /// <summary>
    /// ClearScript hands the referrer back as a <see cref="Uri"/>, whose
    /// serialization is not WHATWG's. Resolution must see the exact href the module
    /// was loaded under, so the canonical form is kept alongside.
    /// </summary>
    private readonly Dictionary<string, string> _canonicalHrefs = new(StringComparer.Ordinal);

    private readonly ObscuraHttpClient? _standaloneClient;
    private readonly Func<ModuleNetworkContext>? _pageNetwork;
    private int _staticGraphDepth;
    private bool _disposed;

    /// <summary>A loader with its own cookie jar and a direct connection.</summary>
    public ObscuraModuleLoader(string baseUrl)
        : this(baseUrl, null)
    {
    }

    /// <summary>
    /// A directly-constructed loader. It still uses Obscura's network policy and
    /// connection pool; it simply has an isolated cookie jar.
    /// </summary>
    public ObscuraModuleLoader(string baseUrl, string? proxyUrl)
        : this(baseUrl, proxyUrl, new ImportMap())
    {
        _standaloneClient = new ObscuraHttpClient(new CookieJar(), proxyUrl);
    }

    /// <summary>
    /// The production shape: the owning page supplies the network context per load,
    /// and the import map is shared with the runtime so a later
    /// <c>&lt;script type="importmap"&gt;</c> merges into the same map the loader reads.
    /// </summary>
    public ObscuraModuleLoader(
        string baseUrl,
        string? proxyUrl,
        ImportMap importMap,
        Func<ModuleNetworkContext> pageNetwork)
        : this(baseUrl, proxyUrl, importMap)
    {
        ArgumentNullException.ThrowIfNull(pageNetwork);
        _pageNetwork = pageNetwork;
    }

    private ObscuraModuleLoader(string baseUrl, string? proxyUrl, ImportMap importMap)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(importMap);
        BaseUrl = baseUrl;
        ProxyUrl = proxyUrl;
        ImportMap = importMap;
    }

    /// <summary>The owning document's URL. Relative specifiers resolve against it.</summary>
    public string BaseUrl { get; }

    /// <summary>Proxy URL threaded through to every dynamic ES-module fetch.</summary>
    public string? ProxyUrl { get; }

    /// <summary>The import map this loader resolves through, shared with the runtime.</summary>
    public ImportMap ImportMap { get; }

    /// <summary>
    /// Pending dynamic-import graph fetches. Deliberately separate from the page's
    /// fetch/XHR counters so analytics does not hold screenshot readiness open.
    /// </summary>
    public ModuleLoadActivity Activity { get; } = new();

    /// <summary>
    /// Canonical and requested specifiers taken into the module map, append-only and
    /// shared by reference. The runtime keeps a cursor into it to associate a
    /// prepared root with the dependencies its successful evaluation also evaluates.
    /// </summary>
    public List<string> LoadedSpecifiers { get; } = [];

    /// <summary>
    /// Whether the loads arriving right now belong to a statically declared graph.
    /// </summary>
    /// <remarks>
    /// deno_core reports <c>is_dyn_import</c> per load and propagates it down every
    /// dependency edge. ClearScript reports nothing of the sort, so the runtime
    /// brackets the compilation and evaluation of a static root with
    /// <see cref="BeginStaticGraph"/> instead, and every load that arrives outside
    /// such a bracket - which is what an <c>import()</c> continuation is - counts as
    /// dynamic-import activity.
    /// </remarks>
    public bool InStaticGraph => Volatile.Read(ref _staticGraphDepth) > 0;

    /// <summary>
    /// Mark the enclosing region as a static module graph load. Nestable; dispose to
    /// leave.
    /// </summary>
    public IDisposable BeginStaticGraph()
    {
        Interlocked.Increment(ref _staticGraphDepth);
        return new StaticGraphScope(this);
    }

    /// <summary>Wire this loader into a V8 engine's module machinery.</summary>
    public void Install(V8ScriptEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.DocumentSettings.Loader = this;
        // Every load goes through this loader, so ClearScript's own file and web
        // loaders are never reached; the flag only unblocks its access check.
        engine.DocumentSettings.AccessFlags = DocumentAccessFlags.EnableAllLoading;
    }

    /// <summary>
    /// Resolve one specifier against one referrer, exactly as the Rust
    /// <c>ModuleLoader::resolve</c> does. Returns false with a message instead of
    /// throwing, because this is reachable from a module callback.
    /// </summary>
    /// <param name="specifier">The specifier as written in the import.</param>
    /// <param name="referrer">
    /// The importing module's URL. The synthetic value <c>"."</c> marks the graph
    /// root: a browser resolves <c>&lt;script type=module src&gt;</c> as a resource URL
    /// before it starts a graph, so the document import map must not remap it.
    /// </param>
    /// <param name="resolved">The resolved module URL, or null on failure.</param>
    /// <param name="error">The failure message, or null on success.</param>
    public bool TryResolve(
        string specifier,
        string referrer,
        out UrlRecord? resolved,
        out string? error)
    {
        resolved = null;

        if (string.Equals(referrer, ".", StringComparison.Ordinal))
        {
            return TryResolveImport(specifier, BaseUrl, out resolved, out error);
        }

        var baseHref = referrer.Length == 0
            || referrer.StartsWith('<')
            || string.Equals(referrer, "about:blank", StringComparison.Ordinal)
                ? BaseUrl
                : referrer;

        var baseRecord = UrlRecord.Parse(baseHref);
        if (baseRecord is null)
        {
            error = string.Format(
                CultureInfo.InvariantCulture,
                "Invalid module referrer {0}: relative URL without a base",
                baseHref);
            return false;
        }

        return ImportMap.TryResolve(specifier, baseRecord, out resolved, out error);
    }

    /// <inheritdoc/>
    public override Document LoadDocument(
        DocumentSettings settings,
        DocumentInfo? sourceInfo,
        string specifier,
        DocumentCategory category,
        DocumentContextCallback contextCallback) =>
        // V8 resolves a static graph synchronously and gives ClearScript no
        // asynchronous escape, so the engine thread blocks here. The fetch itself
        // runs on the thread pool with no synchronization context, so this cannot
        // deadlock against its own continuation.
        LoadDocumentAsync(settings, sourceInfo, specifier, category, contextCallback)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc/>
    public override async Task<Document> LoadDocumentAsync(
        DocumentSettings settings,
        DocumentInfo? sourceInfo,
        string specifier,
        DocumentCategory category,
        DocumentContextCallback contextCallback)
    {
        var referrerHref = ReferrerHref(sourceInfo);
        if (!TryResolve(specifier, referrerHref, out var resolved, out var resolveError)
            || resolved is null)
        {
            throw new ModuleLoadException(resolveError ?? "Module specifier could not be resolved");
        }

        var url = resolved.Href;
        lock (_gate)
        {
            // deno_core never calls `load` twice for a specifier already in its
            // module map. Mirroring that keeps one fetch per URL per page and keeps
            // LoadedSpecifiers matching what the Rust runtime observes.
            if (_documents.TryGetValue(url, out var cached))
            {
                return cached;
            }
        }

        // Module-graph CORS and same-origin credentials are relative to the owning
        // document, not to the importing module. The importer remains the HTTP
        // referrer for a dependency; keeping these URLs distinct prevents a
        // cross-origin parent module from gaining CDN cookies when it imports a
        // sibling on that CDN.
        var documentUrl = UrlRecord.Parse(BaseUrl) ?? resolved;
        var referrer = (referrerHref.Length == 0 || referrerHref == ".")
            ? documentUrl
            : UrlRecord.Parse(referrerHref) ?? documentUrl;

        // Register before the fetch starts. The lifecycle can inspect the runtime
        // between the load being accepted and the first byte moving.
        lock (_gate)
        {
            LoadedSpecifiers.Add(url);
        }

        using ModuleLoadGuard? activityGuard = InStaticGraph ? null : Activity.Begin();

        var network = _pageNetwork is not null
            ? Invoke(_pageNetwork)
            : _standaloneClient is not null
                ? ModuleNetworkContext.From(_standaloneClient, null, null)
                : ModuleNetworkContext.Failed("No network context wired to module loader");

        if (network.Error is not null || network.Client is null)
        {
            throw new ModuleLoadException(
                network.Error ?? "No http_client wired to module loader");
        }

        var requested = new Uri(url);
        var request = ResourceRequest.ModuleScript(
            new Uri(documentUrl.Href),
            new Uri(referrer.Href));

        Response response;
        try
        {
            // Fork from upstream: in stealth mode an ES module must be fetched over
            // the same transport as the document, or a `type="module"` script
            // arrives with a different TLS fingerprint and none of the browser
            // identity headers. The in-tree stealth transport reports itself
            // unavailable (the TLS half of stealth is a tracked gap), and falling
            // back to the page's own client is better than failing the graph.
            response = network.Stealth is { IsAvailable: true } stealth
                ? await stealth
                    .FetchResourceWithCallbacksAsync(requested, request, network.Callbacks)
                    .ConfigureAwait(false)
                : await network.Client
                    .FetchResourceWithCallbacksAsync(requested, request, network.Callbacks)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ModuleLoadException)
        {
            throw new ModuleLoadException(string.Format(
                CultureInfo.InvariantCulture,
                "Failed to fetch module {0}: {1}",
                url,
                ex.Message));
        }

        if (response.Status is < 200 or > 299)
        {
            throw new ModuleLoadException(string.Format(
                CultureInfo.InvariantCulture,
                "Module {0} returned HTTP {1}",
                url,
                response.Status));
        }

        var found = UrlRecord.Parse(response.Url.AbsoluteUri);
        if (found is null)
        {
            throw new ModuleLoadException(string.Format(
                CultureInfo.InvariantCulture,
                "Invalid final module URL {0}: relative URL without a base",
                response.Url));
        }

        if (!string.Equals(found.Href, url, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                LoadedSpecifiers.Add(found.Href);
            }
        }

        var code = ContentEncoding.DecodeNonHtml(response.Body, response.ContentType());
        var info = new DocumentInfo(new Uri(found.Href))
        {
            Category = ModuleCategory.Standard,
            ContextCallback = contextCallback,
        };
        var document = new StringDocument(info, code);

        lock (_gate)
        {
            // The redirect target is the module's identity, which is what
            // ModuleSource::new_with_redirect records on the Rust side.
            _canonicalHrefs[info.Uri!.AbsoluteUri] = found.Href;
            _documents[url] = document;
            _documents[found.Href] = document;
        }

        return document;
    }

    /// <summary>
    /// The href to resolve against for a load requested by <paramref name="sourceInfo"/>.
    /// A null source info is ClearScript's graph root and maps to deno_core's
    /// synthetic <c>"."</c> referrer.
    /// </summary>
    private string ReferrerHref(DocumentInfo? sourceInfo)
    {
        if (sourceInfo is not { Uri: { } uri })
        {
            return ".";
        }

        lock (_gate)
        {
            if (_canonicalHrefs.TryGetValue(uri.AbsoluteUri, out var href))
            {
                return href;
            }
        }

        return UrlRecord.Parse(uri.AbsoluteUri)?.Href ?? uri.AbsoluteUri;
    }

    private static ModuleNetworkContext Invoke(Func<ModuleNetworkContext> provider)
    {
        try
        {
            return provider();
        }
        catch (Exception ex)
        {
            return ModuleNetworkContext.Failed("Module loader page state was dropped: " + ex.Message);
        }
    }

    private static bool TryResolveImport(
        string specifier,
        string baseHref,
        out UrlRecord? resolved,
        out string? error)
    {
        resolved = UrlRecord.Parse(specifier);
        if (resolved is not null)
        {
            error = null;
            return true;
        }

        var relative = specifier.StartsWith('/')
            || specifier.StartsWith("./", StringComparison.Ordinal)
            || specifier.StartsWith("../", StringComparison.Ordinal);
        if (!relative)
        {
            error = string.Format(
                CultureInfo.InvariantCulture,
                "Relative import path \"{0}\" not prefixed with / or ./ or ../",
                specifier);
            return false;
        }

        var baseRecord = UrlRecord.Parse(baseHref);
        if (baseRecord is null)
        {
            error = string.Format(
                CultureInfo.InvariantCulture,
                "Invalid module referrer {0}: relative URL without a base",
                baseHref);
            return false;
        }

        resolved = baseRecord.Join(specifier);
        if (resolved is null)
        {
            error = string.Format(
                CultureInfo.InvariantCulture,
                "Module specifier \"{0}\" could not be resolved against {1}",
                specifier,
                baseHref);
            return false;
        }

        error = null;
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _standaloneClient?.Dispose();
    }

    private sealed class StaticGraphScope(ObscuraModuleLoader owner) : IDisposable
    {
        private ObscuraModuleLoader? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                Interlocked.Decrement(ref owner._staticGraphDepth);
            }
        }
    }
}
