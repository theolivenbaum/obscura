namespace Obscura.Net;

/// <summary>
/// robots.txt fetch/cache/match (port of <c>crates/obscura-net/src/robots.rs</c>).
/// </summary>
public sealed class RobotsCache
{
    private readonly Dictionary<string, RobotsRules> _cache = new(StringComparer.Ordinal);
    private readonly System.Threading.Lock _lock = new();

    private sealed record RobotsRules(List<string> Disallowed, List<string> Allowed);

    /// <summary>Parse a robots.txt body for <paramref name="ourAgent"/> and cache it under a domain.</summary>
    public void ParseAndStore(string domain, string body, string ourAgent)
    {
        var rules = ParseRobotsTxt(body, ourAgent);
        lock (_lock)
        {
            _cache[domain] = rules;
        }
    }

    /// <summary>True when a robots.txt has already been parsed for this origin.</summary>
    public bool Contains(string origin)
    {
        lock (_lock)
        {
            return _cache.ContainsKey(origin);
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is allowed for <paramref name="domain"/>.
    /// A domain with no cached rules is allowed.
    /// </summary>
    public bool IsAllowed(string domain, string path)
    {
        RobotsRules? rules;
        lock (_lock)
        {
            if (!_cache.TryGetValue(domain, out rules))
            {
                return true;
            }
        }

        foreach (var pattern in rules.Allowed)
        {
            if (PathMatches(path, pattern))
            {
                return true;
            }
        }

        foreach (var pattern in rules.Disallowed)
        {
            if (PathMatches(path, pattern))
            {
                return false;
            }
        }

        return true;
    }

    private static RobotsRules ParseRobotsTxt(string body, string ourAgent)
    {
        var ourAgentLower = ourAgent.ToLowerInvariant();
        var disallowed = new List<string>();
        var allowed = new List<string>();
        var inMatchingSection = false;
        var foundSpecific = false;

        foreach (var raw in Lines(body))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!TrySplitOnce(line, ':', out var rawKey, out var rawValue))
            {
                continue;
            }

            var key = rawKey.Trim().ToLowerInvariant();
            var value = rawValue.Trim();

            switch (key)
            {
                case "user-agent":
                    var agent = value.ToLowerInvariant();
                    inMatchingSection = agent == "*"
                        || ourAgentLower.Contains(agent, StringComparison.Ordinal)
                        || agent.Contains(ourAgentLower, StringComparison.Ordinal);
                    if (agent != "*" && inMatchingSection)
                    {
                        foundSpecific = true;
                    }

                    break;
                case "disallow" when inMatchingSection && value.Length != 0:
                    disallowed.Add(value);
                    break;
                case "allow" when inMatchingSection && value.Length != 0:
                    allowed.Add(value);
                    break;
                default:
                    break;
            }
        }

        if (!foundSpecific)
        {
            disallowed.Clear();
            allowed.Clear();
            inMatchingSection = false;

            foreach (var raw in Lines(body))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (!TrySplitOnce(line, ':', out var rawKey, out var rawValue))
                {
                    continue;
                }

                var key = rawKey.Trim().ToLowerInvariant();
                var value = rawValue.Trim();
                switch (key)
                {
                    case "user-agent":
                        inMatchingSection = value.Trim() == "*";
                        break;
                    case "disallow" when inMatchingSection && value.Length != 0:
                        disallowed.Add(value);
                        break;
                    case "allow" when inMatchingSection && value.Length != 0:
                        allowed.Add(value);
                        break;
                    default:
                        break;
                }
            }
        }

        return new RobotsRules(disallowed, allowed);
    }

    private static bool PathMatches(string path, string pattern)
    {
        if (pattern.EndsWith('*'))
        {
            return path.StartsWith(pattern[..^1], StringComparison.Ordinal);
        }

        if (pattern.EndsWith('$'))
        {
            return string.Equals(path, pattern[..^1], StringComparison.Ordinal);
        }

        return path.StartsWith(pattern, StringComparison.Ordinal);
    }

    /// <summary>Rust's <c>str::lines</c>: split on '\n' and drop a trailing '\r'.</summary>
    internal static IEnumerable<string> Lines(string body)
    {
        var start = 0;
        while (start <= body.Length)
        {
            var end = body.IndexOf('\n', start);
            if (end < 0)
            {
                if (start < body.Length)
                {
                    yield return body[start..].TrimEnd('\r');
                }

                yield break;
            }

            var line = body[start..end];
            yield return line.EndsWith('\r') ? line[..^1] : line;
            start = end + 1;
        }
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
