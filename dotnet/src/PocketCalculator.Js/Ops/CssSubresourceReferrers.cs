using PocketCalculator.Net;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// Which external stylesheet referenced each image and font URL of the current document,
/// with that sheet's referrer policy, so the load of a CSS subresource can carry the sheet
/// as its referrer.
/// </summary>
/// <remarks>
/// Port addition (Rust sends these loads with the document as referrer, or none). Measured
/// on Chromium 141: a <c>background-image</c> or <c>@font-face</c> URL in an external sheet
/// is fetched with the sheet's URL as referrer under the sheet's own
/// <c>Referrer-Policy</c> response header, else the default
/// strict-origin-when-cross-origin; the document's policy (header or meta) does not apply.
/// A layout miss carries only a URL, which is why this table exists. It belongs to one
/// document generation, is bounded, and the first sheet to name a URL keeps it, as the
/// first fetch would in Chromium's memory cache. Thread-safe: sheets are recorded from the
/// page's loader while layout reads it.
/// </remarks>
public sealed class CssSubresourceReferrers
{
    /// <summary>The most URLs one document records.</summary>
    public const int MaxEntries = 8192;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (Uri Sheet, ReferrerPolicy Policy)> _entries = new(StringComparer.Ordinal);
    private ulong _generation;

    /// <summary>
    /// Record every URL in <paramref name="urls"/> as referenced by <paramref name="sheet"/>
    /// under <paramref name="policy"/> for document <paramref name="generation"/>.
    /// </summary>
    public void Record(ulong generation, Uri sheet, ReferrerPolicy policy, IEnumerable<string> urls)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(urls);
        lock (_gate)
        {
            if (_generation != generation)
            {
                _entries.Clear();
                _generation = generation;
            }

            foreach (var url in urls)
            {
                if (_entries.Count >= MaxEntries)
                {
                    break;
                }

                _entries.TryAdd(Key(url), (sheet, policy));
            }
        }
    }

    /// <summary>The sheet that referenced <paramref name="url"/> in document <paramref name="generation"/>, if any.</summary>
    public (Uri Sheet, ReferrerPolicy Policy)? Find(ulong generation, string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        lock (_gate)
        {
            return _generation == generation && _entries.TryGetValue(Key(url), out var entry) ? entry : null;
        }
    }

    private static string Key(string url)
    {
        var hash = url.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? url : url[..hash];
    }
}
