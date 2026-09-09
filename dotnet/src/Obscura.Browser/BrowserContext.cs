using Obscura.Net;

namespace Obscura.Browser;

/// <summary>
/// The browser-level state a set of pages shares: cookies, the HTTP client, the
/// browser identity, and the policy switches that gate what a page may reach.
/// </summary>
public sealed class BrowserContext
{
    private BrowserContext(
        string id,
        string? proxyUrl,
        bool stealth,
        string? userAgent,
        string? storageDir,
        bool allowPrivateNetwork,
        bool restoreCookiesFromDisk = true)
    {
        Id = id;
        var cookieJar = new CookieJar();

        // Restore cookies from disk if storageDir is configured.
        if (restoreCookiesFromDisk && storageDir is not null)
        {
            string cookiePath = Path.Combine(storageDir, "cookies.json");
            if (File.Exists(cookiePath))
            {
                try
                {
                    cookieJar.LoadFromFile(cookiePath);
                }
                catch (Exception)
                {
                    // A malformed or unreadable jar must not stop the context from starting.
                }
            }
        }

        var client = new ObscuraHttpClient(cookieJar, proxyUrl, allowPrivateNetwork);
        if (stealth)
        {
            client.BlockTrackers = true;
        }

        BrowserProfile profile = Profiles.SelectProfile();
        string resolvedUa = userAgent ?? profile.UserAgent;
        Platform = profile.Platform;
        UaPlatform = profile.UaPlatform;
        UaPlatformVersion = profile.UaPlatformVersion;
        // Sync the http client's UA at construction so navigation requests pick it
        // up before any async setup runs.
        client.SetUserAgent(resolvedUa);

        CookieJar = cookieJar;
        HttpClient = client;
        UserAgent = resolvedUa;
        ProxyUrl = proxyUrl;
        RobotsCache = new RobotsCache();
        Stealth = stealth;
        StorageDir = storageDir;
        AllowPrivateNetwork = allowPrivateNetwork;
    }

    public string Id { get; }

    public CookieJar CookieJar { get; }

    public ObscuraHttpClient HttpClient { get; }

    public string UserAgent { get; }

    public string Platform { get; }

    public string UaPlatform { get; }

    public string UaPlatformVersion { get; }

    public string? ProxyUrl { get; }

    public RobotsCache RobotsCache { get; }

    public bool ObeyRobots { get; set; }

    public bool Stealth { get; }

    /// <summary>
    /// When true, CDP-driven navigation to <c>file://</c> URLs is permitted.
    /// </summary>
    /// <remarks>
    /// The default is false: a remote CDP client cannot point the browser at
    /// <c>/etc/shadow</c> even if Obscura runs as a privileged user. Flip it on with
    /// <c>obscura serve --allow-file-access</c> for local-HTML testing. The CLI's own
    /// <c>obscura fetch file://...</c> path is unaffected because it does not go
    /// through the CDP server.
    /// </remarks>
    public bool AllowFileAccess { get; set; }

    public string? StorageDir { get; }

    /// <summary>
    /// When true, the http client allows fetching localhost / RFC1918 / link-local
    /// addresses.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="AllowFileAccess"/> because they cover different
    /// threat models: <c>file://</c> is a local file-system read, private-network is
    /// the broader SSRF gate.
    /// </remarks>
    public bool AllowPrivateNetwork { get; }

    public static BrowserContext New(string id) => new(id, null, false, null, null, false);

    /// <summary>
    /// Create a context with an optional storage directory. When set, cookies are
    /// loaded from <c>{storageDir}/cookies.json</c> on creation.
    /// </summary>
    public static BrowserContext WithStorage(string id, string? storageDir) =>
        new(id, null, false, null, storageDir, false);

    /// <summary>Create a context with full options including the storage directory.</summary>
    public static BrowserContext WithStorageFull(
        string id,
        string? proxyUrl,
        bool stealth,
        string? userAgent,
        string? storageDir) =>
        new(id, proxyUrl, stealth, userAgent, storageDir, false);

    /// <summary>
    /// Variant that also accepts the <c>allow_private_network</c> opt-in. Every other
    /// constructor defaults it to false.
    /// </summary>
    public static BrowserContext WithStorageAndNetwork(
        string id,
        string? proxyUrl,
        bool stealth,
        string? userAgent,
        string? storageDir,
        bool allowPrivateNetwork) =>
        new(id, proxyUrl, stealth, userAgent, storageDir, allowPrivateNetwork);

    public static BrowserContext WithOptions(string id, string? proxyUrl, bool stealth) =>
        WithFullOptions(id, proxyUrl, stealth, null);

    public static BrowserContext WithFullOptions(
        string id,
        string? proxyUrl,
        bool stealth,
        string? userAgent) =>
        new(id, proxyUrl, stealth, userAgent, null, false);

    public static BrowserContext WithProxy(string id, string? proxyUrl) =>
        WithOptions(id, proxyUrl, false);

    /// <summary>
    /// Create a context with the same browser configuration but independent mutable
    /// network state.
    /// </summary>
    /// <remarks>
    /// Persistent copies start with the template's current cookies; incognito copies
    /// start empty and never write to the template's storage directory.
    /// </remarks>
    public BrowserContext IsolatedCopy(string id, bool persistent)
    {
        var copy = new BrowserContext(
            id,
            ProxyUrl,
            Stealth,
            UserAgent,
            persistent ? StorageDir : null,
            AllowPrivateNetwork,
            restoreCookiesFromDisk: false)
        {
            ObeyRobots = ObeyRobots,
            AllowFileAccess = AllowFileAccess,
        };
        if (persistent)
        {
            copy.CookieJar.SetCookiesFromCdp(CookieJar.GetAllCookies());
        }
        return copy;
    }

    /// <summary>Persist cookies to disk when a storage directory is configured.</summary>
    public void SaveCookies()
    {
        if (StorageDir is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(StorageDir);
            CookieJar.SaveToFile(Path.Combine(StorageDir, "cookies.json"));
        }
        catch (Exception)
        {
            // Shutdown must not fail because the cookie file could not be written.
        }
    }
}
