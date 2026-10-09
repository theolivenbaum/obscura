using System.Text.Json;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// One Web Storage area set (HTML "Web storage"): a storage area per storage key, kept on
/// the host so it outlives the document that wrote it. A browser context holds one for
/// <c>localStorage</c> and each page (top-level browsing context) one for
/// <c>sessionStorage</c>. Port addition: the reference's shim kept both areas in the
/// document's realm, so a navigation, even a same-origin reload, started them empty.
/// </summary>
/// <remarks>
/// Thread-safe: frames and pages of one context run on different threads. Quota is
/// Chromium's: about 5 MiB of UTF-16 per storage key (key and value lengths summed),
/// beyond which a set fails with QuotaExceededError and changes nothing.
/// </remarks>
public sealed class WebStorage
{
    /// <summary>Chromium's per-origin quota, in UTF-16 code units of keys plus values.</summary>
    public const long QuotaChars = 5L * 1024 * 1024;

    private readonly Dictionary<string, Area> _areas = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private sealed class Area
    {
        public readonly Dictionary<string, string> Items = new(StringComparer.Ordinal);
        // Insertion order, which Chromium's key(n) does not promise but the shim's did.
        public readonly List<string> Order = [];
        public long Chars;
    }

    /// <summary>The area's items, in insertion order, as a JSON array of [key, value] pairs.</summary>
    public string SnapshotJson(string storageKey)
    {
        ArgumentNullException.ThrowIfNull(storageKey);
        lock (_gate)
        {
            if (!_areas.TryGetValue(storageKey, out var area) || area.Order.Count == 0)
            {
                return "[]";
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                foreach (var key in area.Order)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(key);
                    writer.WriteStringValue(area.Items[key]);
                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
            }

            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    /// <summary>One item's value, or null.</summary>
    public string? Get(string storageKey, string key)
    {
        lock (_gate)
        {
            return _areas.TryGetValue(storageKey, out var area) && area.Items.TryGetValue(key, out var value) ? value : null;
        }
    }

    /// <summary>The number of items in the area.</summary>
    public int Count(string storageKey)
    {
        lock (_gate)
        {
            return _areas.TryGetValue(storageKey, out var area) ? area.Order.Count : 0;
        }
    }

    /// <summary>The key at <paramref name="index"/> in insertion order, or null.</summary>
    public string? KeyAt(string storageKey, int index)
    {
        lock (_gate)
        {
            return _areas.TryGetValue(storageKey, out var area) && index >= 0 && index < area.Order.Count
                ? area.Order[index]
                : null;
        }
    }

    /// <summary>Sets one item. False, with nothing changed, when it would exceed the quota.</summary>
    public bool Set(string storageKey, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(storageKey);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (!_areas.TryGetValue(storageKey, out var area))
            {
                area = new Area();
                _areas[storageKey] = area;
            }

            long previous = area.Items.TryGetValue(key, out var old) ? key.Length + old.Length : 0;
            long next = area.Chars - previous + key.Length + value.Length;
            if (next > QuotaChars)
            {
                return false;
            }

            if (old is null)
            {
                area.Order.Add(key);
            }

            area.Items[key] = value;
            area.Chars = next;
            return true;
        }
    }

    /// <summary>Removes one item, if present.</summary>
    public void Remove(string storageKey, string key)
    {
        ArgumentNullException.ThrowIfNull(storageKey);
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_areas.TryGetValue(storageKey, out var area) && area.Items.Remove(key, out var old))
            {
                area.Order.Remove(key);
                area.Chars -= key.Length + old.Length;
            }
        }
    }

    /// <summary>Empties one area.</summary>
    public void Clear(string storageKey)
    {
        ArgumentNullException.ThrowIfNull(storageKey);
        lock (_gate)
        {
            _areas.Remove(storageKey);
        }
    }
}
