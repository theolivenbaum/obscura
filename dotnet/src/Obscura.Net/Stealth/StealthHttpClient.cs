namespace Obscura.Net;

/// <summary>
/// The stealth transport seam. The Rust engine backs this with <c>wreq</c> +
/// BoringSSL to impersonate a Chrome TLS ClientHello; that has no managed
/// equivalent and adding a native TLS stack is out of scope for the port (see
/// "Known deviations" in todo.md), so the interface is kept and the only
/// implementation in tree reports that it is unavailable. Obscura.Js can still
/// call <c>SetStealthClient</c> against this type, and a real transport can be
/// dropped in later without touching its callers.
/// </summary>
public interface IStealthHttpClient
{
    /// <summary>True when this client can actually issue requests.</summary>
    bool IsAvailable { get; }

    /// <summary>The cookie jar the stealth transport shares with the page.</summary>
    CookieJar CookieJar { get; }

    /// <summary>Requests currently on the wire.</summary>
    int ActiveRequests { get; }

    /// <summary>True when nothing is on the wire.</summary>
    bool IsNetworkIdle { get; }

    /// <summary>Replace the extra header set.</summary>
    void SetExtraHeaders(IReadOnlyDictionary<string, string> headers);

    /// <summary>GET with the navigation profile.</summary>
    Task<Response> FetchAsync(Uri url, CancellationToken cancellationToken = default);

    /// <summary>GET firing the page's passive callbacks.</summary>
    Task<Response> FetchWithCallbacksAsync(
        Uri url,
        CallbackRegistry? callbacks,
        CancellationToken cancellationToken = default);

    /// <summary>Fetch a subresource with an explicit fetch profile.</summary>
    Task<Response> FetchResourceWithCallbacksAsync(
        Uri url,
        ResourceRequest request,
        CallbackRegistry? callbacks,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stealth identity constants. The JS-visible half of stealth ports even though
/// the TLS half does not, and navigator must report the same identity the
/// transport would have sent or a site cross-checks the mismatch as a bot signal.
/// </summary>
public static class StealthIdentity
{
    /// <summary>The User-Agent the Chrome 145 / Windows emulation sends.</summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36";

    /// <summary><c>navigator.platform</c> for that profile.</summary>
    public const string NavigatorPlatform = "Win32";

    /// <summary><c>sec-ch-ua-platform</c> for that profile.</summary>
    public const string UaPlatform = "Windows";

    /// <summary><c>sec-ch-ua-platform-version</c> for that profile.</summary>
    public const string UaPlatformVersion = "15.0.0";
}

/// <summary>
/// The "not available" stealth transport. Every request path throws so a caller
/// cannot silently downgrade to the ordinary transport while believing it has a
/// stealth fingerprint.
/// </summary>
public sealed class UnavailableStealthHttpClient(CookieJar cookieJar) : IStealthHttpClient
{
    private const string Reason =
        "the stealth TLS transport is not available in the .NET port: "
        + "ClientHello impersonation needs a native TLS stack, which the port does not take";

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public CookieJar CookieJar { get; } = cookieJar;

    /// <inheritdoc />
    public int ActiveRequests => 0;

    /// <inheritdoc />
    public bool IsNetworkIdle => true;

    /// <inheritdoc />
    public void SetExtraHeaders(IReadOnlyDictionary<string, string> headers)
    {
        // No transport to configure.
    }

    /// <inheritdoc />
    public Task<Response> FetchAsync(Uri url, CancellationToken cancellationToken = default) =>
        Task.FromException<Response>(ObscuraNetException.Network(Reason));

    /// <inheritdoc />
    public Task<Response> FetchWithCallbacksAsync(
        Uri url,
        CallbackRegistry? callbacks,
        CancellationToken cancellationToken = default) =>
        Task.FromException<Response>(ObscuraNetException.Network(Reason));

    /// <inheritdoc />
    public Task<Response> FetchResourceWithCallbacksAsync(
        Uri url,
        ResourceRequest request,
        CallbackRegistry? callbacks,
        CancellationToken cancellationToken = default) =>
        Task.FromException<Response>(ObscuraNetException.Network(Reason));
}
