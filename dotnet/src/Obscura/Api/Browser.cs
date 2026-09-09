using System.Globalization;
using Obscura.Browser;
using Obscura.Net;
using InnerPage = Obscura.Browser.Page;

namespace Obscura.Api;

/// <summary>The embeddable Obscura browser session.</summary>
/// <remarks>
/// Port of <c>crates/obscura/src/browser.rs</c>. Rust returns
/// <c>Result&lt;Self, Error&gt;</c> from every constructor even though none of
/// them can fail today; the port keeps the shape without the pointless result,
/// and throws <see cref="ObscuraException"/> if construction ever does fail.
/// </remarks>
public sealed class Browser
{
    private static long _nextPageId;

    private readonly BrowserContext _context;
    private readonly CookieJar _cookieJar;

    private Browser(BrowserContext context)
    {
        _context = context;
        _cookieJar = context.CookieJar;
    }

    /// <summary>Create a browser with the default configuration.</summary>
    public static Browser New() => Build(new BrowserConfig());

    /// <summary>Create a browser from an explicit configuration.</summary>
    public static Browser Build(BrowserConfig config)
    {
        var context = config.StorageDir is { } dir
            ? BrowserContext.WithStorageFull("api", config.Proxy, config.Stealth, config.UserAgent, dir)
            : BrowserContext.WithFullOptions("api", config.Proxy, config.Stealth, config.UserAgent);
        return new Browser(context);
    }

    /// <summary>Start a fluent builder, matching <c>Browser::builder()</c>.</summary>
    public static BrowserBuilder Builder() => new();

    /// <summary>The context every page opened from this browser shares.</summary>
    public BrowserContext Context => _context;

    /// <summary>Open a new page.</summary>
    /// <remarks>
    /// Page ids start at 1 and increase for the process, as the Rust
    /// <c>AtomicU64</c> counter does.
    /// </remarks>
    public Task<Page> NewPageAsync()
    {
        var id = Interlocked.Increment(ref _nextPageId);
        var page = new InnerPage($"page-{id.ToString(CultureInfo.InvariantCulture)}", _context);
        return Task.FromResult(new Page(page));
    }

    /// <summary>The cookie store for this browser session.</summary>
    public CookieStore Cookies() => new(_cookieJar);
}

/// <summary>Fluent builder for <see cref="Browser"/>.</summary>
public sealed class BrowserBuilder
{
    private readonly BrowserConfig _config = new();

    /// <summary>Set the proxy URL.</summary>
    public BrowserBuilder Proxy(string proxy)
    {
        _config.Proxy = proxy;
        return this;
    }

    /// <summary>Toggle stealth mode.</summary>
    public BrowserBuilder Stealth(bool stealth)
    {
        _config.Stealth = stealth;
        return this;
    }

    /// <summary>Override the User-Agent.</summary>
    public BrowserBuilder UserAgent(string userAgent)
    {
        _config.UserAgent = userAgent;
        return this;
    }

    /// <summary>Persist cookies under this directory.</summary>
    public BrowserBuilder StorageDir(string dir)
    {
        _config.StorageDir = dir;
        return this;
    }

    /// <summary>Finish, constructing the browser.</summary>
    public Browser Build() => Browser.Build(_config);
}
