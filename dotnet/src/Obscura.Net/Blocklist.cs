using System.Reflection;

namespace Obscura.Net;

/// <summary>
/// Tracker blocklist over the shared <c>pgl_domains.txt</c> list (port of
/// <c>crates/obscura-net/src/blocklist.rs</c>). The list file is linked out of
/// the Rust tree as an embedded resource so the two engines can never drift.
/// </summary>
public static class Blocklist
{
    private const string ResourceName = "Obscura.Net.pgl_domains.txt";

    private static readonly Lazy<HashSet<string>> Domains = new(Load, isThreadSafe: true);

    private static readonly string[] ExtraDomains = [];

    /// <summary>Number of domains in the loaded blocklist. Test hook.</summary>
    internal static int Count => Domains.Value.Count;

    /// <summary>
    /// True when <paramref name="host"/>, or any parent domain of it, is on the
    /// tracker blocklist.
    /// </summary>
    public static bool IsBlocked(string host)
    {
        var blocklist = Domains.Value;

        if (blocklist.Contains(host))
        {
            return true;
        }

        var lookup = blocklist.GetAlternateLookup<ReadOnlySpan<char>>();
        var domain = host.AsSpan();
        int pos;
        while ((pos = domain.IndexOf('.')) >= 0)
        {
            domain = domain[(pos + 1)..];
            if (lookup.Contains(domain))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> Load()
    {
        var set = new HashSet<string>(4000, StringComparer.Ordinal);
        using var stream = typeof(Blocklist).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource {ResourceName} is missing");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var domain = line.Trim();
            if (domain.Length != 0 && !domain.StartsWith('#'))
            {
                set.Add(domain);
            }
        }

        foreach (var domain in ExtraDomains)
        {
            set.Add(domain);
        }

        return set;
    }
}
