using System.Globalization;

namespace Obscura.Browser;

/// <summary>One browser identity: user agent plus the platform fields that must agree with it.</summary>
public sealed record BrowserProfile(
    string UserAgent,
    string Platform,
    string UaPlatform,
    string UaPlatformVersion);

/// <summary>The browser identities a context can present.</summary>
public static class Profiles
{
    public static IReadOnlyList<BrowserProfile> All { get; } =
    [
        new("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36",
            "Win32", "Windows", "10.0.0"),
        new("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36",
            "Win32", "Windows", "10.0.0"),
        new("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36",
            "Win32", "Windows", "15.0.0"),
        new("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36",
            "Win32", "Windows", "15.0.0"),
        new("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36",
            "MacIntel", "macOS", "13.6.7"),
        new("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36",
            "MacIntel", "macOS", "14.4.1"),
        new("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36",
            "MacIntel", "macOS", "14.5.0"),
        new("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36",
            "MacIntel", "macOS", "14.6.0"),
    ];

    /// <summary>Rust seeds from the nanosecond part of the wall clock.</summary>
    public static BrowserProfile RandomProfile()
    {
        long nanoseconds =
            (DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) % TimeSpan.TicksPerSecond
            * (1_000_000_000L / TimeSpan.TicksPerSecond);
        return All[(int)((ulong)nanoseconds % (ulong)All.Count)];
    }

    /// <summary>
    /// Pick the profile for a new browser context.
    /// </summary>
    /// <remarks>
    /// The default is a single stable profile. Cycling through different browser
    /// identities from one address is itself a bot signal (a real address maps to a
    /// stable device), and the rotated profile does not yet carry a matching TLS or
    /// timezone fingerprint, so rotation is opt-in:
    /// <c>OBSCURA_PROFILE=&lt;index&gt;</c> pins a specific profile,
    /// <c>OBSCURA_ROTATE_PROFILE=1</c> picks a random profile per context.
    /// </remarks>
    public static BrowserProfile SelectProfile()
    {
        string? raw = Environment.GetEnvironmentVariable("OBSCURA_PROFILE")?.Trim();
        if (raw is not null
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            && index >= 0
            && index < All.Count)
        {
            return All[index];
        }

        return EnvEnabled("OBSCURA_ROTATE_PROFILE") ? RandomProfile() : All[0];
    }

    internal static bool EnvEnabled(string key) =>
        Environment.GetEnvironmentVariable(key)?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            _ => false,
        };
}
