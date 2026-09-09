using System.Net;
using System.Net.Sockets;

namespace Obscura.Net;

/// <summary>
/// The SSRF deny-set and the URL gate built on it (port of the guard half of
/// <c>crates/obscura-net/src/client.rs</c>). Loopback, RFC1918, link-local and the
/// other non-globally-routable ranges are blocked by default; the escape hatch is
/// <c>--allow-private-network</c> or <c>OBSCURA_ALLOW_PRIVATE_NETWORK</c>.
/// </summary>
public static class SsrfGuard
{
    /// <summary>
    /// Process-wide opt-in via env var. Older flow that issue #4 introduced. The new
    /// <c>--allow-private-network</c> CLI flag (issue #33) sets a per-client field
    /// that is OR'd with this so existing scripts and Docker setups that pin the env
    /// var keep working unchanged.
    /// </summary>
    public static bool EnvAllowsPrivateNetwork()
    {
        var raw = Environment.GetEnvironmentVariable("OBSCURA_ALLOW_PRIVATE_NETWORK");
        if (raw is null)
        {
            return false;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            _ => false,
        };
    }

    /// <summary>
    /// True when <paramref name="ip"/> must never be the target of an outbound
    /// request from the engine: loopback, RFC1918 private, link-local (incl. the
    /// 169.254.169.254 cloud-metadata endpoint), broadcast, documentation, the
    /// unspecified address (0.0.0.0 / ::, which the OS routes to localhost), IPv6
    /// unique-local (fc00::/7), and any IPv4-mapped/compatible IPv6 form of the
    /// above. Centralizes the SSRF deny-set so the literal-host check and the
    /// DNS-resolution check (<see cref="SsrfGuardResolver"/>) can never disagree.
    /// </summary>
    public static bool IsForbiddenIp(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> o = stackalloc byte[4];
            ip.TryWriteBytes(o, out _);
            return IsForbiddenIpv4(o);
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return true;
        }

        Span<byte> bytes = stackalloc byte[16];
        ip.TryWriteBytes(bytes, out _);
        Span<ushort> s = stackalloc ushort[8];
        for (var i = 0; i < 8; i++)
        {
            s[i] = (ushort)((bytes[i * 2] << 8) | bytes[(i * 2) + 1]);
        }

        if (IPAddress.IsLoopback(ip)
            || ip.Equals(IPAddress.IPv6Any)
            || IsUniqueLocalV6(bytes)
            || ip.IsIPv6LinkLocal
            || ip.IsIPv6Multicast)
        {
            return true;
        }

        // Unwrap IPv4-mapped (::ffff:a.b.c.d) and IPv4-compatible (::a.b.c.d) forms
        // and re-check the embedded v4 so e.g. [::ffff:127.0.0.1] or
        // [::ffff:169.254.169.254] cannot slip past the v6 arm.
        if (TryUnwrapEmbeddedIpv4(bytes, s, out var embedded))
        {
            return IsForbiddenIpv4(embedded);
        }

        // IPv4/IPv6 translation prefix (RFC 6052). Only /96 has a fixed
        // embedded-address position; the local-use /48 is therefore blocked outright
        // below.
        if (s[0] == 0x64 && s[1] == 0xff9b && s[2] == 0 && s[3] == 0 && s[4] == 0 && s[5] == 0)
        {
            Span<byte> v4 = [(byte)(s[6] >> 8), (byte)s[6], (byte)(s[7] >> 8), (byte)s[7]];
            return IsForbiddenIpv4(v4);
        }

        // 6to4 carries its IPv4 endpoint in bits 16..48.
        if (s[0] == 0x2002)
        {
            Span<byte> v4 = [(byte)(s[1] >> 8), (byte)s[1], (byte)(s[2] >> 8), (byte)s[2]];
            return IsForbiddenIpv4(v4);
        }

        // Discard-only, local-use NAT64, and documentation prefixes.
        return (s[0] == 0x100 && s[1] == 0 && s[2] == 0 && s[3] == 0)
            || (s[0] == 0x64 && s[1] == 0xff9b && s[2] == 1)
            || (s[0] == 0x2001 && s[1] == 0x0db8)
            || (s[0] == 0x3fff && (s[1] & 0xf000) == 0);
    }

    private static bool IsForbiddenIpv4(ReadOnlySpan<byte> o) =>
        o[0] == 127                                        // loopback
        || o[0] == 10                                      // RFC1918
        || (o[0] == 172 && o[1] >= 16 && o[1] <= 31)       // RFC1918
        || (o[0] == 192 && o[1] == 168)                    // RFC1918
        || (o[0] == 169 && o[1] == 254)                    // link-local
        || (o[0] == 255 && o[1] == 255 && o[2] == 255 && o[3] == 255) // broadcast
        || (o[0] == 192 && o[1] == 0 && o[2] == 2)         // documentation
        || (o[0] == 198 && o[1] == 51 && o[2] == 100)      // documentation
        || (o[0] == 203 && o[1] == 0 && o[2] == 113)       // documentation
        || (o[0] >= 224 && o[0] <= 239)                    // multicast
        || o[0] == 0                                       // unspecified / 0.0.0.0/8
        // std's is_private() covers only RFC1918, so add the IANA
        // special-purpose ranges that also host internal services and are common
        // SSRF targets:
        //   100.64.0.0/10  CGNAT / RFC6598 - cloud metadata (e.g. Alibaba
        //                  100.100.100.200) lives here.
        //   198.18.0.0/15  benchmarking / RFC2544.
        //   192.88.99.0/24 6to4 relay anycast / RFC7526.
        || (o[0] == 100 && o[1] >= 64 && o[1] <= 127)
        || (o[0] == 198 && (o[1] == 18 || o[1] == 19))
        || (o[0] == 192 && o[1] == 88 && o[2] == 99)
        // Most of 192.0.0.0/24 is special-purpose and not globally reachable. Keep
        // the two globally reachable PCP anycast assignments usable rather than
        // blocking the entire /24.
        || (o[0] == 192 && o[1] == 0 && o[2] == 0 && o[3] != 9 && o[3] != 10)
        // 240.0.0.0/4 is reserved (255.255.255.255 was already covered by the
        // broadcast check).
        || o[0] >= 240;

    private static bool IsUniqueLocalV6(ReadOnlySpan<byte> bytes) => (bytes[0] & 0xFE) == 0xFC;

    private static bool TryUnwrapEmbeddedIpv4(
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<ushort> s,
        out ReadOnlySpan<byte> v4)
    {
        v4 = default;
        // ::ffff:a.b.c.d
        if (s[0] == 0 && s[1] == 0 && s[2] == 0 && s[3] == 0 && s[4] == 0 && s[5] == 0xffff)
        {
            v4 = bytes[12..16];
            return true;
        }

        // ::a.b.c.d (IPv4-compatible), excluding :: and ::1 which Rust's `to_ipv4`
        // also excludes.
        if (s[0] == 0 && s[1] == 0 && s[2] == 0 && s[3] == 0 && s[4] == 0 && s[5] == 0
            && !(s[6] == 0 && (s[7] == 0 || s[7] == 1)))
        {
            v4 = bytes[12..16];
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reject a URL whose scheme is not http/https/file, or whose literal host is in
    /// the SSRF deny-set. Hostnames are additionally checked at DNS-resolution time
    /// by <see cref="SsrfGuardResolver"/>.
    /// </summary>
    public static void ValidateUrl(Uri url, bool allowPrivateNetwork)
    {
        allowPrivateNetwork = allowPrivateNetwork || EnvAllowsPrivateNetwork();
        var scheme = url.Scheme;
        if (!string.Equals(scheme, "http", StringComparison.Ordinal)
            && !string.Equals(scheme, "https", StringComparison.Ordinal)
            && !string.Equals(scheme, "file", StringComparison.Ordinal))
        {
            throw ObscuraNetException.Network(
                $"Forbidden URL scheme '{scheme}' - only http, https, and file are allowed");
        }

        if (string.Equals(scheme, "file", StringComparison.Ordinal) || allowPrivateNetwork)
        {
            return;
        }

        var host = UrlOrigin.Host(url);
        if (host.Length == 0)
        {
            return;
        }

        switch (url.HostNameType)
        {
            case UriHostNameType.IPv4 when IPAddress.TryParse(host, out var v4):
                if (IsForbiddenIp(v4))
                {
                    throw ObscuraNetException.Network(
                        $"Access to private/internal IP address {v4} is not allowed");
                }

                break;
            case UriHostNameType.IPv6 when IPAddress.TryParse(host, out var v6):
                if (IsForbiddenIp(v6))
                {
                    throw ObscuraNetException.Network(
                        $"Access to private/internal IPv6 address {v6} is not allowed");
                }

                break;
            default:
                var lowerDomain = host.ToLowerInvariant();
                if (lowerDomain == "localhost"
                    || lowerDomain.EndsWith(".localhost", StringComparison.Ordinal)
                    || lowerDomain == "127.0.0.1"
                    || lowerDomain == "::1")
                {
                    throw ObscuraNetException.Network(
                        $"Access to localhost domain '{host}' is not allowed");
                }

                break;
        }
    }
}

/// <summary>
/// DNS resolver that performs the lookup and then rejects the whole request if ANY
/// resolved address is in the SSRF deny-set. This closes the DNS-rebinding bypass a
/// host-string check alone cannot: a public name that resolves to 127.0.0.1 /
/// 169.254.169.254 / an RFC1918 address is blocked at connect time, using the very
/// addresses the client will dial. When private access is permitted
/// (<c>--allow-private-network</c> or <c>OBSCURA_ALLOW_PRIVATE_NETWORK</c>) the
/// lookup passes through unfiltered.
///
/// The Rust engine implements this for both transports (reqwest and wreq) so
/// <c>--stealth</c> never trades the guard away for a better TLS fingerprint; the
/// port has a single transport, and every connection it makes goes through here.
/// </summary>
public sealed class SsrfGuardResolver(bool allowPrivate)
{
    /// <summary>Whether the per-client allow-private-network flag is set.</summary>
    public bool AllowPrivate { get; } = allowPrivate;

    /// <summary>
    /// Resolve <paramref name="host"/> and throw when any resolved address is
    /// forbidden. Returns the addresses to dial.
    /// </summary>
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        var allow = AllowPrivate || SsrfGuard.EnvAllowsPrivateNetwork();
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }

        if (!allow)
        {
            foreach (var address in addresses)
            {
                if (SsrfGuard.IsForbiddenIp(address))
                {
                    throw ObscuraNetException.Network(
                        $"SSRF blocked: '{host}' resolves to forbidden address {address}");
                }
            }
        }

        return addresses;
    }
}
