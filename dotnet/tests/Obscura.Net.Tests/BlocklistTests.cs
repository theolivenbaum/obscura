namespace Obscura.Net.Tests;

/// <summary>Port of the <c>#[cfg(test)] mod tests</c> block in <c>blocklist.rs</c>.</summary>
public class BlocklistTests
{
    [Fact]
    public void TestExactMatch()
    {
        Assert.True(Blocklist.IsBlocked("google-analytics.com"));
        Assert.True(Blocklist.IsBlocked("doubleclick.net"));
    }

    [Fact]
    public void TestSubdomainMatch()
    {
        Assert.True(Blocklist.IsBlocked("www.google-analytics.com"));
        Assert.True(Blocklist.IsBlocked("ssl.google-analytics.com"));
    }

    [Fact]
    public void TestNotBlocked()
    {
        Assert.False(Blocklist.IsBlocked("google.com"));
        Assert.False(Blocklist.IsBlocked("example.com"));
        Assert.False(Blocklist.IsBlocked("github.com"));
    }

    [Fact]
    public void TestPglDomains()
    {
        Assert.True(Blocklist.IsBlocked("adnxs.com"));
        Assert.True(Blocklist.IsBlocked("criteo.com"));
    }

    [Fact]
    public void TestBlocklistSize() => Assert.True(Blocklist.Count > 3500);
}
