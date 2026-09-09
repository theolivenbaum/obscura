namespace Obscura.Api;

/// <summary>Configuration for launching a <see cref="Browser"/> instance.</summary>
public sealed class BrowserConfig
{
    /// <summary>Proxy URL (for example <c>socks5://127.0.0.1:1080</c>).</summary>
    public string? Proxy { get; set; }

    /// <summary>Enable stealth mode (consistent browser fingerprint).</summary>
    public bool Stealth { get; set; }

    /// <summary>Custom User-Agent string.</summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Directory for persistent cookie storage. Rust models this as a
    /// <c>PathBuf</c>; the browser layer stores it as a plain path string.
    /// </summary>
    public string? StorageDir { get; set; }

    /// <summary>Start a fluent builder, matching <c>BrowserConfig::builder()</c>.</summary>
    public static BrowserConfigBuilder Builder() => new();
}

/// <summary>Fluent builder for <see cref="BrowserConfig"/>.</summary>
public sealed class BrowserConfigBuilder
{
    private readonly BrowserConfig _config = new();

    /// <summary>Set the proxy URL.</summary>
    public BrowserConfigBuilder Proxy(string proxy)
    {
        _config.Proxy = proxy;
        return this;
    }

    /// <summary>Toggle stealth mode.</summary>
    public BrowserConfigBuilder Stealth(bool stealth)
    {
        _config.Stealth = stealth;
        return this;
    }

    /// <summary>Override the User-Agent.</summary>
    public BrowserConfigBuilder UserAgent(string userAgent)
    {
        _config.UserAgent = userAgent;
        return this;
    }

    /// <summary>Persist cookies under this directory.</summary>
    public BrowserConfigBuilder StorageDir(string dir)
    {
        _config.StorageDir = dir;
        return this;
    }

    /// <summary>Finish, returning the configured value.</summary>
    public BrowserConfig Build() => _config;
}
