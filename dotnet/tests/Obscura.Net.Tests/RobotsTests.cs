namespace Obscura.Net.Tests;

/// <summary>Port of the <c>#[cfg(test)] mod tests</c> block in <c>robots.rs</c>.</summary>
public class RobotsTests
{
    [Fact]
    public void TestParseBasicRobots()
    {
        const string body = "User-agent: *\nDisallow: /private/\nDisallow: /admin\nAllow: /admin/public\n";
        var cache = new RobotsCache();
        cache.ParseAndStore("example.com", body, "Obscura");
        Assert.True(cache.IsAllowed("example.com", "/"));
        Assert.True(cache.IsAllowed("example.com", "/page"));
        Assert.False(cache.IsAllowed("example.com", "/private/secret"));
        Assert.False(cache.IsAllowed("example.com", "/admin"));
        Assert.True(cache.IsAllowed("example.com", "/admin/public"));
    }

    [Fact]
    public void TestNoRulesMeansAllowed()
    {
        var cache = new RobotsCache();
        Assert.True(cache.IsAllowed("unknown.com", "/anything"));
        Assert.False(cache.Contains("unknown.com"));

        cache.ParseAndStore("unknown.com", string.Empty, "Obscura");
        Assert.True(cache.Contains("unknown.com"));
        Assert.True(cache.IsAllowed("unknown.com", "/anything"));
    }

    [Fact]
    public void TestDisallowAll()
    {
        const string body = "User-agent: *\nDisallow: /\n";
        var cache = new RobotsCache();
        cache.ParseAndStore("blocked.com", body, "Obscura");
        Assert.False(cache.IsAllowed("blocked.com", "/"));
        Assert.False(cache.IsAllowed("blocked.com", "/page"));
    }
}
