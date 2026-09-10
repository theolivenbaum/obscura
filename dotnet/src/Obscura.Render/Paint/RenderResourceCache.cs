// Port of `RenderResourceLoader`, `RenderResourceCache`, and the resource/URL helpers in
// crates/obscura-render/src/paint.rs.
using System.Globalization;
using System.Text;
using Obscura.Dom;
using NodeId = Obscura.Dom.NodeId;

namespace Obscura.Render;

/// <summary>
/// Synchronous byte loader used by <see cref="RenderResourceCache"/>.
/// </summary>
/// <remarks>
/// The default implementation uses Obscura's pooled image agent; tests and embedding callers
/// can provide a local loader without changing preparation or paint.
/// </remarks>
public interface IRenderResourceLoader
{
    byte[]? Load(string url);
}

/// <summary>Adapts a plain delegate, standing in for Rust's blanket <c>FnMut</c> impl.</summary>
internal sealed class DelegateResourceLoader(Func<string, byte[]?> load) : IRenderResourceLoader
{
    public byte[]? Load(string url) => load(url);
}

internal sealed class HttpResourceLoader : IRenderResourceLoader
{
    public byte[]? Load(string url) => ImageAgent.Get(url);
}

/// <summary>
/// One script-created <c>FontFace</c> registered with the document's <c>FontFaceSet</c>.
/// </summary>
public sealed record DynamicFontFace
{
    public required string Family { get; init; }

    public required string Source { get; init; }

    public required string Style { get; init; }

    public required string Weight { get; init; }

    public required string UnicodeRange { get; init; }
}

/// <summary>The exact responsive image candidate chosen during preparation.</summary>
public sealed record SelectedImage(string ResolvedUrl, float Density, ImageRequestProfile Profile);

/// <summary>
/// A resolved cache outcome. Rust models this as <c>Option&lt;Option&lt;..&gt;&gt;</c>: the outer
/// absence means "no live cache entry", while a present outcome with a null value is a retained
/// load/decode failure.
/// </summary>
public readonly record struct CachedImageOutcome((string Url, float Width, float Height)? Value);

internal abstract record CachedResource
{
    private CachedResource()
    {
    }

    internal sealed record Bytes(byte[] Value) : CachedResource;

    internal sealed record Missing(long At) : CachedResource;
}

internal readonly record struct RememberedContentImageIntrinsic(
    string ResolvedUrl,
    ReplacedIntrinsic Intrinsic);

/// <summary>
/// Page-scoped raw resource bytes shared by layout preparation and repeated paints.
/// </summary>
/// <remarks>
/// Entries are FIFO-bounded by both count and retained byte size.
/// </remarks>
public sealed class RenderResourceCache
{
    internal const int DefaultResourceCacheEntries = 512;
    internal const int DefaultResourceCacheBytes = 64 * 1024 * 1024;

    /// <summary>
    /// CSS <c>content:url(...)</c> is only discoverable after cascade. Remember a bounded set
    /// of successful selections so repeated prepares can seed their intrinsic geometry.
    /// </summary>
    internal const int DefaultContentImageIntrinsicEntries = 256;

    /// <summary>
    /// How many decoded web faces to keep. A page rarely uses more than a handful;
    /// the bound only exists so a document that cycles through faces cannot grow
    /// this without limit.
    /// </summary>
    internal const int DefaultDecodedFontEntries = 32;

    internal static readonly TimeSpan MissingResourceRetryAfter = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Exact formats the renderer can decode. Do not use <c>image/*</c> or <c>*/*</c> here:
    /// either wildcard permits a content-negotiating server to choose AVIF, JPEG-XL, or another
    /// format that this build cannot rasterize.
    /// </summary>
    internal const string ImageAccept =
        "image/webp,image/apng,image/svg+xml,image/png,image/jpeg,image/gif,image/bmp,image/x-icon,image/vnd.microsoft.icon";

    private readonly Dictionary<string, CachedResource> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<NodeId, RememberedContentImageIntrinsic> _contentImageIntrinsics = [];
    private readonly Dictionary<string, (byte[] Compressed, byte[] Decoded)> _decodedFonts =
        new(StringComparer.Ordinal);

    private readonly List<string> _decodedFontOrder = [];
    private readonly List<NodeId> _contentImageIntrinsicOrder = [];
    private readonly int _maxEntries;
    private readonly int _maxBytes;
    private readonly int _maxContentImageIntrinsics;
    private long _retainedBytes;
    private bool _syncLoadingEnabled = true;
    private IRenderResourceLoader _loader;

    private RenderResourceCache(IRenderResourceLoader loader, int maxEntries, int maxBytes)
    {
        _loader = loader;
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
        _maxContentImageIntrinsics = Math.Min(maxEntries, DefaultContentImageIntrinsicEntries);
    }

    public RenderResourceCache()
        : this(new HttpResourceLoader(), DefaultResourceCacheEntries, DefaultResourceCacheBytes)
    {
    }

    public static RenderResourceCache Default() => new();

    public static RenderResourceCache WithLoader(IRenderResourceLoader loader) =>
        WithLoaderAndLimits(loader, DefaultResourceCacheEntries, DefaultResourceCacheBytes);

    public static RenderResourceCache WithLoader(Func<string, byte[]?> loader) =>
        WithLoaderAndLimits(new DelegateResourceLoader(loader), DefaultResourceCacheEntries, DefaultResourceCacheBytes);

    public static RenderResourceCache WithLoaderAndLimits(
        IRenderResourceLoader loader,
        int maxEntries,
        int maxBytes) => new(loader, maxEntries, maxBytes);

    public static RenderResourceCache WithLoaderAndLimits(
        Func<string, byte[]?> loader,
        int maxEntries,
        int maxBytes) => new(new DelegateResourceLoader(loader), maxEntries, maxBytes);

    /// <summary>
    /// The sfnt bytes previously decoded from <paramref name="compressed"/> for
    /// <paramref name="key"/>, if the same fetched array is still the one in hand.
    /// </summary>
    /// <remarks>
    /// DEVIATION from <c>crates/obscura-render</c>: <c>fetch_and_decode_font</c> there
    /// decodes on every prepare and caches only the compressed bytes. The port caches
    /// the decoded result too, because its WOFF2 path is far slower than Rust's
    /// <c>wuff</c>: on a page with three faces, re-decoding cost about 600ms of every
    /// prepare, and a prepare runs on the first layout read after any style mutation.
    /// The decode is a pure function of the fetched bytes, so memoizing it cannot
    /// change what is rendered. Reference equality against the fetched array is the
    /// validity check: <c>FetchBytes</c> hands back the cached instance, so a re-fetch
    /// or an eviction produces a different array and misses.
    /// </remarks>
    internal bool TryGetDecodedFont(string key, byte[] compressed, out byte[]? decoded)
    {
        if (_decodedFonts.TryGetValue(key, out (byte[] Compressed, byte[] Decoded) entry)
            && ReferenceEquals(entry.Compressed, compressed))
        {
            decoded = entry.Decoded;
            return true;
        }

        decoded = null;
        return false;
    }

    /// <summary>Remember <paramref name="decoded"/> for <paramref name="key"/>.</summary>
    internal void StoreDecodedFont(string key, byte[] compressed, byte[] decoded)
    {
        if (!_decodedFonts.ContainsKey(key))
        {
            _decodedFontOrder.Add(key);
        }

        _decodedFonts[key] = (compressed, decoded);
        while (_decodedFontOrder.Count > DefaultDecodedFontEntries)
        {
            _decodedFonts.Remove(_decodedFontOrder[0]);
            _decodedFontOrder.RemoveAt(0);
        }
    }

    /// <summary>Test-only counter for the CSS content-image correction relayout.</summary>
    internal int ContentImageLayoutRetries { get; set; }

    internal IReadOnlyDictionary<NodeId, RememberedContentImageIntrinsic> ContentImageIntrinsics =>
        _contentImageIntrinsics;

    /// <summary>
    /// Temporarily control the compatibility loader used by synchronous layout and paint.
    /// </summary>
    public bool SetSyncLoadingEnabled(bool enabled)
    {
        bool previous = _syncLoadingEnabled;
        _syncLoadingEnabled = enabled;
        return previous;
    }

    public int RetainedEntryCount() => _entries.Count;

    public long RetainedByteLen() => _retainedBytes;

    public bool HasLiveOutcome(string url) =>
        _entries.TryGetValue(NetworkResourceUrl(url), out CachedResource? entry)
        && entry switch
        {
            CachedResource.Bytes => true,
            CachedResource.Missing missing => Elapsed(missing.At) < MissingResourceRetryAfter,
            _ => false,
        };

    /// <summary>
    /// Whether this URL currently retains usable bytes (as opposed to a short-lived
    /// negative-cache entry).
    /// </summary>
    public bool HasCachedBytes(string url) =>
        _entries.TryGetValue(NetworkResourceUrl(url), out CachedResource? entry)
        && entry is CachedResource.Bytes;

    public bool HasLiveImageOutcome(string url, ImageRequestProfile profile) =>
        HasLiveOutcome(ImageResourceKey(url, profile));

    public void SeedImage(string url, ImageRequestProfile profile, byte[] bytes) =>
        Seed(ImageResourceKey(url, profile), bytes);

    public void SeedImageMissing(string url, ImageRequestProfile profile) =>
        SeedMissing(ImageResourceKey(url, profile));

    /// <summary>Seed bytes fetched by the owning page's asynchronous browser transport.</summary>
    public void Seed(string url, byte[] bytes)
    {
        string key = NetworkResourceUrl(url);
        Remove(key);
        InsertBytes(key, bytes);
    }

    /// <summary>Retain a page-transport failure so capture does not repeat the request.</summary>
    public void SeedMissing(string url)
    {
        string key = NetworkResourceUrl(url);
        Remove(key);
        InsertMissing(key);
    }

    /// <summary>Resolve, fetch, and inspect one image through the byte cache paint uses.</summary>
    public (string Url, float Width, float Height)? ImageMetadata(string src, string? baseUrl)
    {
        string? resolved = PaintResources.ResolveResourceUrl(src, baseUrl);
        if (resolved is null)
        {
            return null;
        }

        byte[]? bytes = PaintResources.FetchBytes(resolved, null, this);
        if (bytes is null)
        {
            return null;
        }

        (float Width, float Height)? metadata = PaintResources.ImageMetadataFromBytes(bytes);
        return metadata is { } size ? (resolved, size.Width, size.Height) : null;
    }

    /// <summary>Inspect an image only when its renderer-cache outcome is already known.</summary>
    public CachedImageOutcome? CachedImageMetadata(string src, string? baseUrl)
    {
        string? resolved = PaintResources.ResolveResourceUrl(src, baseUrl);
        if (resolved is null)
        {
            return null;
        }

        if (resolved.StartsWith("data:", StringComparison.Ordinal))
        {
            RenderResourceCache scratch = WithLoaderAndLimits(_ => null, 0, 0);
            byte[]? bytes = PaintResources.FetchBytes(resolved, null, scratch);
            (float Width, float Height)? size = bytes is null
                ? null
                : PaintResources.ImageMetadataFromBytes(bytes);
            return new CachedImageOutcome(size is { } value ? (resolved, value.Width, value.Height) : null);
        }

        if (!_entries.TryGetValue(NetworkResourceUrl(resolved), out CachedResource? entry))
        {
            return null;
        }

        switch (entry)
        {
            case CachedResource.Bytes bytesEntry:
            {
                (float Width, float Height)? size = PaintResources.ImageMetadataFromBytes(bytesEntry.Value);
                return new CachedImageOutcome(size is { } value ? (resolved, value.Width, value.Height) : null);
            }

            case CachedResource.Missing missing when Elapsed(missing.At) < MissingResourceRetryAfter:
                return new CachedImageOutcome(null);
            default:
                return null;
        }
    }

    private CachedImageOutcome? CachedProfiledImageMetadata(
        string src,
        string? baseUrl,
        ImageRequestProfile profile)
    {
        string? resolved = PaintResources.ResolveResourceUrl(src, baseUrl);
        if (resolved is null)
        {
            return null;
        }

        if (resolved.StartsWith("data:", StringComparison.Ordinal))
        {
            return CachedImageMetadata(resolved, null);
        }

        string key = ImageResourceKey(resolved, profile);
        if (!_entries.TryGetValue(key, out CachedResource? entry))
        {
            return null;
        }

        switch (entry)
        {
            case CachedResource.Bytes bytesEntry:
            {
                (float Width, float Height)? size = PaintResources.ImageMetadataFromBytes(bytesEntry.Value);
                return new CachedImageOutcome(size is { } value ? (resolved, value.Width, value.Height) : null);
            }

            case CachedResource.Missing missing when Elapsed(missing.At) < MissingResourceRetryAfter:
                return new CachedImageOutcome(null);
            default:
                return null;
        }
    }

    /// <summary>Select and inspect the resource for one live <c>&lt;img&gt;</c>.</summary>
    public (string Url, float Density, (float Width, float Height)? Dimensions)? ImageElementMetadata(
        DomTree tree,
        NodeId id,
        (float Width, float Height) viewport,
        string? baseUrl)
    {
        (string Url, float Density)? candidate = PaintImages.ResolveImgUrl(tree, id, viewport);
        if (candidate is not { } picked)
        {
            return null;
        }

        string resolved = PaintResources.ResolveResourceUrl(picked.Url, baseUrl) ?? picked.Url;
        ImageRequestProfile profile = PaintImages.ImageRequestProfileFor(tree, id);
        byte[]? bytes = PaintResources.FetchProfiledImageBytes(resolved, null, this, profile);
        (float Width, float Height)? metadata = bytes is null
            ? null
            : PaintResources.ImageMetadataFromBytes(bytes);
        (float Width, float Height)? dimensions = metadata is { } size
            ? (size.Width / picked.Density, size.Height / picked.Density)
            : null;
        return (resolved, picked.Density, dimensions);
    }

    /// <summary>Cache-only counterpart to <see cref="ImageElementMetadata"/>.</summary>
    public (string Url, float Density, bool Known, (float Width, float Height)? Dimensions)?
        CachedImageElementMetadata(
            DomTree tree,
            NodeId id,
            (float Width, float Height) viewport,
            string? baseUrl)
    {
        (string Url, float Density)? candidate = PaintImages.ResolveImgUrl(tree, id, viewport);
        if (candidate is not { } picked)
        {
            return null;
        }

        string resolved = PaintResources.ResolveResourceUrl(picked.Url, baseUrl) ?? picked.Url;
        ImageRequestProfile profile = PaintImages.ImageRequestProfileFor(tree, id);
        var cached = CachedProfiledImageMetadata(resolved, null, profile);
        if (cached is null)
        {
            return (resolved, picked.Density, false, null);
        }

        (string Url, float Width, float Height)? value = cached.Value.Value;
        return (
            resolved,
            picked.Density,
            true,
            value is { } size ? (size.Width / picked.Density, size.Height / picked.Density) : null);
    }

    /// <summary>Cache-only poster selection for one live <c>&lt;video&gt;</c>.</summary>
    public (string Url, ImageRequestProfile Profile, bool Known, (float Width, float Height)? Dimensions)?
        CachedVideoPosterMetadata(DomTree tree, NodeId id, string? baseUrl)
    {
        Node? node = tree.GetNode(id);
        if (node is null || node.AsElement() is not { } element
            || !string.Equals(element.Name.Local, "video", StringComparison.Ordinal))
        {
            return null;
        }

        string? poster = node.GetAttribute("poster")?.Trim();
        if (string.IsNullOrEmpty(poster))
        {
            return null;
        }

        string? resolved = PaintResources.ResolveResourceUrl(poster, baseUrl);
        if (resolved is null)
        {
            return null;
        }

        ImageRequestProfile profile = PaintImages.ImageRequestProfileFor(tree, id);
        var cached = CachedProfiledImageMetadata(resolved, null, profile);
        if (cached is null)
        {
            return (resolved, profile, false, null);
        }

        (string Url, float Width, float Height)? value = cached.Value.Value;
        return (resolved, profile, true, value is { } size ? (size.Width, size.Height) : null);
    }

    internal byte[]? GetOrLoad(string url)
    {
        string key = NetworkResourceUrl(url);
        if (_entries.TryGetValue(key, out CachedResource? entry))
        {
            switch (entry)
            {
                case CachedResource.Bytes bytes:
                    return bytes.Value;
                case CachedResource.Missing missing when Elapsed(missing.At) < MissingResourceRetryAfter:
                    return null;
            }
        }

        if (!_syncLoadingEnabled)
        {
            return null;
        }

        Remove(key);
        byte[]? loaded = LoadSafely(key);
        if (loaded is null)
        {
            InsertMissing(key);
            return null;
        }

        InsertBytes(key, loaded);
        return loaded;
    }

    internal byte[]? GetOrLoadImage(string url, ImageRequestProfile profile)
    {
        string key = ImageResourceKey(url, profile);
        if (_entries.TryGetValue(key, out CachedResource? entry))
        {
            switch (entry)
            {
                case CachedResource.Bytes bytes:
                    return bytes.Value;
                case CachedResource.Missing missing when Elapsed(missing.At) < MissingResourceRetryAfter:
                    return null;
            }
        }

        if (!_syncLoadingEnabled)
        {
            return null;
        }

        Remove(key);

        // The private profile key is cache identity only and must never escape into a loader.
        byte[]? loaded = LoadSafely(NetworkResourceUrl(url));
        if (loaded is null)
        {
            InsertMissing(key);
            return null;
        }

        InsertBytes(key, loaded);
        return loaded;
    }

    // A caller-supplied loader owns its own failure policy: the Rust trait method is
    // infallible, so an exception here is a programmer error and propagates, exactly as a
    // panic in the reference loader would.
    private byte[]? LoadSafely(string url) => _loader.Load(url);

    private void InsertBytes(string url, byte[] bytes)
    {
        if (_maxEntries == 0 || bytes.Length > _maxBytes)
        {
            return;
        }

        while (_entries.Count >= _maxEntries || _retainedBytes + bytes.Length > _maxBytes)
        {
            if (_order.Count == 0)
            {
                break;
            }

            string oldest = _order[0];
            _order.RemoveAt(0);
            RemoveEntry(oldest);
        }

        _retainedBytes += bytes.Length;
        _order.Add(url);
        _entries[url] = new CachedResource.Bytes(bytes);
    }

    private void InsertMissing(string url)
    {
        if (_maxEntries == 0)
        {
            return;
        }

        while (_entries.Count >= _maxEntries)
        {
            if (_order.Count == 0)
            {
                break;
            }

            string oldest = _order[0];
            _order.RemoveAt(0);
            RemoveEntry(oldest);
        }

        _order.Add(url);
        _entries[url] = new CachedResource.Missing(Environment.TickCount64);
    }

    private void Remove(string url)
    {
        if (_entries.ContainsKey(url))
        {
            _order.RemoveAll(key => string.Equals(key, url, StringComparison.Ordinal));
            RemoveEntry(url);
        }
    }

    private void RemoveEntry(string url)
    {
        if (_entries.Remove(url, out CachedResource? entry) && entry is CachedResource.Bytes bytes)
        {
            _retainedBytes = Math.Max(0, _retainedBytes - bytes.Value.Length);
        }
    }

    /// <summary>
    /// Seed a prior stable <c>content:url(...)</c> selection into the ordinary intrinsic and
    /// selected-image maps before cascade.
    /// </summary>
    internal HashSet<NodeId> SeedContentImageIntrinsics(
        DomTree tree,
        Dictionary<NodeId, ReplacedIntrinsic> intrinsic,
        Dictionary<NodeId, SelectedImage> selected)
    {
        List<(NodeId Node, RememberedContentImageIntrinsic Value)> remembered =
            [.. _contentImageIntrinsics.Select(pair => (pair.Key, pair.Value))];
        HashSet<NodeId> seeded = new(remembered.Count);
        foreach ((NodeId nid, RememberedContentImageIntrinsic value) in remembered)
        {
            bool isLiveImage = tree.GetNode(nid)?.AsElement() is { } element
                && string.Equals(element.Name.Local, "img", StringComparison.Ordinal);
            if (!isLiveImage)
            {
                ForgetContentImageIntrinsic(nid);
                continue;
            }

            intrinsic[nid] = value.Intrinsic;
            selected[nid] = new SelectedImage(value.ResolvedUrl, 1f, ImageRequestProfile.NoCorsInclude);
            seeded.Add(nid);
        }

        return seeded;
    }

    internal void RememberContentImageIntrinsic(NodeId nid, string resolvedUrl, ReplacedIntrinsic intrinsic)
    {
        if (_maxContentImageIntrinsics == 0)
        {
            return;
        }

        bool replacing = _contentImageIntrinsics.ContainsKey(nid);
        _contentImageIntrinsicOrder.RemoveAll(remembered => remembered == nid);
        while (!replacing && _contentImageIntrinsics.Count >= _maxContentImageIntrinsics)
        {
            if (_contentImageIntrinsicOrder.Count == 0)
            {
                break;
            }

            NodeId oldest = _contentImageIntrinsicOrder[0];
            _contentImageIntrinsicOrder.RemoveAt(0);
            _contentImageIntrinsics.Remove(oldest);
        }

        _contentImageIntrinsicOrder.Add(nid);
        _contentImageIntrinsics[nid] = new RememberedContentImageIntrinsic(resolvedUrl, intrinsic);
    }

    internal void ForgetContentImageIntrinsic(NodeId nid)
    {
        if (_contentImageIntrinsics.Remove(nid))
        {
            _contentImageIntrinsicOrder.RemoveAll(remembered => remembered == nid);
        }
    }

    private static TimeSpan Elapsed(long at) => TimeSpan.FromMilliseconds(Environment.TickCount64 - at);

    internal static string ImageResourceKey(string url, ImageRequestProfile profile)
    {
        string network = NetworkResourceUrl(url);
        return profile switch
        {
            ImageRequestProfile.NoCorsInclude => network,
            ImageRequestProfile.CorsSameOrigin => "\0obscura-img-cors\0" + network,
            _ => "\0obscura-img-credentials\0" + network,
        };
    }

    internal static string NetworkResourceUrl(string url)
    {
        if (url.StartsWith('\0') || url.StartsWith("data:", StringComparison.Ordinal))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            return url;
        }

        try
        {
            if (string.IsNullOrEmpty(parsed.Fragment))
            {
                return parsed.AbsoluteUri;
            }

            UriBuilder builder = new(parsed) { Fragment = string.Empty };
            return builder.Uri.AbsoluteUri;
        }
        catch (Exception)
        {
            return url;
        }
    }
}

/// <summary>
/// One shared HTTP agent for all image fetches in the process, with a browser User-Agent and
/// keep-alive connection pooling.
/// </summary>
internal static class ImageAgent
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36";

    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    });

    /// <summary>
    /// Fetch with a bounded timeout, retrying on rate-limit / transient errors with backoff.
    /// </summary>
    internal static byte[]? Get(string url)
    {
        TimeSpan backoff = TimeSpan.FromMilliseconds(200);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept", RenderResourceCache.ImageAccept);
                using HttpResponseMessage response = Client.Value.Send(request);
                int code = (int)response.StatusCode;
                if (code is 429 or 500 or 502 or 503 or 504 && attempt < 2)
                {
                    Thread.Sleep(backoff);
                    backoff *= 2;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using MemoryStream buffer = new();
                response.Content.ReadAsStream().CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (Exception) when (attempt < 2)
            {
                Thread.Sleep(backoff);
                backoff *= 2;
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }
}

/// <summary>Resource fetch/decoding helpers from paint.rs.</summary>
internal static class PaintResources
{
    /// <summary>
    /// Resolve <paramref name="src"/> (a <c>data:</c> URI, or an absolute/relative URL against
    /// <paramref name="baseUrl"/>) to raw bytes, fetching at most once per distinct URL.
    /// </summary>
    internal static byte[]? FetchBytes(string src, string? baseUrl, RenderResourceCache cache)
    {
        if (src.StartsWith("data:", StringComparison.Ordinal))
        {
            string rest = src["data:".Length..];
            int comma = rest.IndexOf(',');
            if (comma < 0)
            {
                return null;
            }

            string meta = rest[..comma];
            string data = rest[(comma + 1)..];
            if (meta.Contains("base64", StringComparison.Ordinal))
            {
                try
                {
                    return Convert.FromBase64String(data);
                }
                catch (FormatException)
                {
                    return null;
                }
            }

            return PercentDecode(data);
        }

        string? resolved = ResolveResourceUrl(src, baseUrl);
        return resolved is null ? null : cache.GetOrLoad(resolved);
    }

    internal static byte[]? FetchProfiledImageBytes(
        string src,
        string? baseUrl,
        RenderResourceCache cache,
        ImageRequestProfile profile)
    {
        if (src.StartsWith("data:", StringComparison.Ordinal))
        {
            return FetchBytes(src, baseUrl, cache);
        }

        string? resolved = ResolveResourceUrl(src, baseUrl);
        return resolved is null ? null : cache.GetOrLoadImage(resolved, profile);
    }

    internal static string? ResolveResourceUrl(string src, string? baseUrl)
    {
        if (src.StartsWith("data:", StringComparison.Ordinal))
        {
            return src;
        }

        if (src.StartsWith("http://", StringComparison.Ordinal)
            || src.StartsWith("https://", StringComparison.Ordinal))
        {
            return src;
        }

        if (src.StartsWith("//", StringComparison.Ordinal))
        {
            // Protocol-relative URL: inherit the document scheme, but never a non-network one.
            string scheme = "https";
            if (baseUrl is not null && Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? parsed)
                && (string.Equals(parsed.Scheme, "http", StringComparison.Ordinal)
                    || string.Equals(parsed.Scheme, "https", StringComparison.Ordinal)))
            {
                scheme = parsed.Scheme;
            }

            return scheme + ":" + src;
        }

        if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            return null;
        }

        return Uri.TryCreate(baseUri, src, out Uri? joined) ? joined.AbsoluteUri : null;
    }

    /// <summary>Decode a percent-escaped <c>data:</c> URI payload.</summary>
    internal static byte[] PercentDecode(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        List<byte> output = new(bytes.Length);
        int i = 0;
        while (i < bytes.Length)
        {
            if (bytes[i] == (byte)'%' && i + 2 < bytes.Length)
            {
                int hi = HexDigit((char)bytes[i + 1]);
                int lo = HexDigit((char)bytes[i + 2]);
                if (hi >= 0 && lo >= 0)
                {
                    output.Add((byte)((hi * 16) + lo));
                    i += 3;
                    continue;
                }
            }

            output.Add(bytes[i]);
            i++;
        }

        return [.. output];
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>Header-only intrinsic pixel dimensions of a raster image.</summary>
    internal static (uint Width, uint Height)? ImageDimensions(byte[] bytes)
    {
        if (bytes.Length == 0 || PaintSvg.IsSvg(bytes))
        {
            return null;
        }

        try
        {
            using SkiaSharp.SKData data = SkiaSharp.SKData.CreateCopy(bytes);
            using SkiaSharp.SKCodec? codec = SkiaSharp.SKCodec.Create(data);
            if (codec is null)
            {
                return null;
            }

            SkiaSharp.SKImageInfo info = codec.Info;
            return info.Width > 0 && info.Height > 0 ? ((uint)info.Width, (uint)info.Height) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static (float Width, float Height)? ImageMetadataFromBytes(byte[] bytes) =>
        ImageIntrinsicMetadata(bytes)?.NaturalSize();

    internal static ReplacedIntrinsic? ImageIntrinsicMetadata(byte[] bytes)
    {
        ReplacedIntrinsic? metadata = ImageDimensions(bytes) is { } dimensions
            ? ReplacedIntrinsic.FromDimensions(dimensions.Width, dimensions.Height)
            : PaintSvg.SvgImageIntrinsicMetadata(bytes);
        if (metadata is null)
        {
            return null;
        }

        (float Width, float Height)? natural = metadata.Value.NaturalSize();
        if (natural is not { } size
            || !float.IsFinite(size.Width) || !float.IsFinite(size.Height)
            || size.Width <= 0f || size.Height <= 0f)
        {
            return null;
        }

        return metadata;
    }

    /// <summary>
    /// Decode raster image bytes to a premultiplied-alpha pixmap resized to <c>w</c>x<c>h</c>.
    /// </summary>
    internal static Pixmap? RasterToPixmap(byte[] bytes, uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return null;
        }

        try
        {
            using SkiaSharp.SKBitmap? decoded = SkiaSharp.SKBitmap.Decode(bytes);
            if (decoded is null)
            {
                return null;
            }

            Pixmap? pixmap = Pixmap.New(width, height);
            if (pixmap is null)
            {
                return null;
            }

            var info = new SkiaSharp.SKImageInfo(
                (int)width,
                (int)height,
                SkiaSharp.SKColorType.Rgba8888,
                SkiaSharp.SKAlphaType.Premul);
            using SkiaSharp.SKBitmap target = new();
            target.InstallPixels(info, pixmap.PixelPointer, (int)width * 4);
            using SkiaSharp.SKCanvas canvas = new(target);
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            using SkiaSharp.SKImage image = SkiaSharp.SKImage.FromBitmap(decoded);
            canvas.DrawImage(
                image,
                new SkiaSharp.SKRect(0, 0, width, height),
                new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.None),
                null);
            return pixmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
