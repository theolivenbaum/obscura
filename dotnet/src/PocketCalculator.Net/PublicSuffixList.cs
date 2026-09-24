using System.Globalization;
using System.Reflection;

namespace PocketCalculator.Net;

/// <summary>
/// The public suffix and registrable-domain ("eTLD+1") lookups. <c>op_document_domain_candidate</c>
/// uses the registrable domain; the cookie jar uses both, to reject a public-suffix Domain
/// attribute and to decide whether a request is same-site.
/// </summary>
/// <remarks>
/// <para>
/// The rules are the full Mozilla Public Suffix List
/// (<c>Resources/public_suffix_list.dat</c>, MPL-2.0, its license header kept), as the Rust
/// engine's <c>psl</c> crate embeds it. Both the ICANN and the PRIVATE sections are used,
/// as Chromium does for cookies and for the "site" of SameSite
/// (<c>INCLUDE_PRIVATE_REGISTRIES</c>), so <c>alice.github.io</c> and <c>bob.github.io</c>
/// are different sites and <c>github.io</c> cannot be a cookie Domain.
/// </para>
/// <para>
/// Matching is the list's own algorithm: the longest matching rule wins, <c>*.</c> rules
/// match any one label, <c>!</c> exception rules win outright, and a TLD the list does not
/// know takes the implicit <c>*</c> rule, as in the <c>psl</c> crate. (Chromium instead
/// treats a host under an unknown TLD as having no registrable domain; the implicit rule
/// is kept so hosts like <c>a.example.test</c> keep their current site.)
/// </para>
/// <para>
/// The list is parsed once, on first use, into a hash of rule strings with an
/// alternate span lookup, so a lookup allocates nothing (about 20 ms and 2 MB on first use). IDN rules are stored in both
/// their Unicode and their ACE (<c>xn--</c>) form, because hosts reach here punycoded.
/// </para>
/// <para>
/// It lives in <c>PocketCalculator.Net</c> rather than beside the URL ops so the cookie jar can use it;
/// <c>PocketCalculator.Js</c> references this assembly, not the other way round.
/// </para>
/// </remarks>
public static class PublicSuffixList
{
    private const string ResourceName = "PocketCalculator.Net.public_suffix_list.dat";

    [Flags]
    private enum RuleKind : byte
    {
        Normal = 1,
        Wildcard = 2,
        Exception = 4,
    }

    private static readonly Lazy<Dictionary<string, RuleKind>> Rules = new(Load, isThreadSafe: true);

    /// <summary>Number of distinct rule keys loaded. Test hook.</summary>
    internal static int RuleCount => Rules.Value.Count;

    /// <summary>
    /// The registrable domain of <paramref name="host"/> (its public suffix plus one label),
    /// or null when the host <em>is</em> a public suffix or has no registrable part.
    /// </summary>
    public static string? RegistrableDomain(string host)
    {
        if (!TryGetRegistrableDomain(host, out var domain))
        {
            return null;
        }

        return domain.Length == host.Length ? host : domain.ToString();
    }

    /// <summary>
    /// The registrable domain of <paramref name="host"/> as a slice of it, without
    /// allocating. False when the host is a public suffix or has no registrable part.
    /// </summary>
    public static bool TryGetRegistrableDomain(ReadOnlySpan<char> host, out ReadOnlySpan<char> domain)
    {
        domain = default;
        var suffix = PublicSuffixLength(host, out var trimmed);
        if (suffix < 0 || suffix >= trimmed.Length)
        {
            return false;
        }

        // suffix < trimmed.Length, so trimmed[..^suffix] ends with the dot before it.
        var head = trimmed[..(trimmed.Length - suffix - 1)];
        var start = head.LastIndexOf('.') + 1;
        if (start == head.Length)
        {
            // An empty label ("a..com"): no registrable domain.
            return false;
        }

        domain = host[start..];
        return true;
    }

    /// <summary>
    /// True when <paramref name="host"/> is itself a public suffix (the <c>psl</c> crate's
    /// <c>suffix_str(host) == host</c>). Every single-label name is one, through the implicit
    /// <c>*</c> rule, which is why <c>localhost</c> and <c>com</c> both answer true.
    /// </summary>
    public static bool IsPublicSuffix(string host) =>
        host.Length != 0 && !TryGetRegistrableDomain(host, out _);

    /// <summary>
    /// The length in chars of the public suffix of <paramref name="host"/> (without a
    /// trailing dot), or -1 for an empty or malformed host. <paramref name="trimmed"/> is
    /// the host without one trailing dot.
    /// </summary>
    private static int PublicSuffixLength(ReadOnlySpan<char> host, out ReadOnlySpan<char> trimmed)
    {
        trimmed = host.Length != 0 && host[^1] == '.' ? host[..^1] : host;
        if (trimmed.Length == 0 || trimmed[0] == '.' || trimmed[^1] == '.')
        {
            return -1;
        }

        // Rules are stored lower case and matched ordinally, which hashes several times
        // faster than a case-insensitive comparer. Hosts nearly always arrive lower case
        // already; the rare one that does not is folded into a stack buffer.
        if (trimmed.ContainsAnyInRange('A', 'Z'))
        {
            Span<char> folded = trimmed.Length <= 256 ? stackalloc char[trimmed.Length] : new char[trimmed.Length];
            trimmed.ToLowerInvariant(folded);
            return SuffixLength(folded);
        }

        return SuffixLength(trimmed);
    }

    private static int SuffixLength(ReadOnlySpan<char> trimmed)
    {
        var lookup = Rules.Value.GetAlternateLookup<ReadOnlySpan<char>>();

        // The implicit "*" rule: the last label is a public suffix.
        var lastDot = trimmed.LastIndexOf('.');
        var best = trimmed.Length - lastDot - 1;

        // Walk the candidate suffixes from the TLD outwards; each starts just after a dot
        // (or at 0). An exception rule wins outright, otherwise the longest match.
        var cursor = trimmed.Length;
        while (cursor > 0)
        {
            var dot = trimmed[..cursor].LastIndexOf('.');
            var labelStart = dot + 1;
            var candidate = trimmed[labelStart..];
            if (lookup.TryGetValue(candidate, out var kind))
            {
                if ((kind & RuleKind.Exception) != 0)
                {
                    // "!rule": the public suffix is the rule minus its leftmost label.
                    var firstDot = candidate.IndexOf('.');
                    return firstDot < 0 ? 0 : candidate.Length - firstDot - 1;
                }

                if ((kind & RuleKind.Normal) != 0)
                {
                    best = Math.Max(best, candidate.Length);
                }

                if ((kind & RuleKind.Wildcard) != 0 && dot > 0)
                {
                    // "*.rule" matches the label before it too.
                    var prevDot = trimmed[..dot].LastIndexOf('.');
                    best = Math.Max(best, trimmed.Length - prevDot - 1);
                }
            }

            if (dot < 0)
            {
                break;
            }

            cursor = dot;
        }

        return best;
    }

    private static Dictionary<string, RuleKind> Load()
    {
        var rules = new Dictionary<string, RuleKind>(11000, StringComparer.Ordinal);
        using var stream = typeof(PublicSuffixList).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource {ResourceName} is missing");
        using var reader = new StreamReader(stream);
        var idn = new IdnMapping();
        while (reader.ReadLine() is { } raw)
        {
            // Each line is only read up to the first whitespace; comments start with "//".
            var line = raw.AsSpan().Trim();
            var space = line.IndexOfAny(' ', '\t');
            if (space >= 0)
            {
                line = line[..space];
            }

            if (line.Length == 0 || line.StartsWith("//"))
            {
                continue;
            }

            var kind = RuleKind.Normal;
            if (line[0] == '!')
            {
                kind = RuleKind.Exception;
                line = line[1..];
            }
            else if (line.StartsWith("*."))
            {
                kind = RuleKind.Wildcard;
                line = line[2..];
            }

            var rule = line.ToString().ToLowerInvariant();
            Add(rules, rule, kind);
            if (!System.Text.Ascii.IsValid(rule))
            {
                try
                {
                    Add(rules, idn.GetAscii(rule), kind);
                }
                catch (ArgumentException)
                {
                    // A rule the IDNA mapping refuses cannot match a punycoded host.
                }
            }
        }

        return rules;
    }

    private static void Add(Dictionary<string, RuleKind> rules, string rule, RuleKind kind)
    {
        rules[rule] = rules.TryGetValue(rule, out var existing) ? existing | kind : kind;
    }
}
