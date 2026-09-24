using System.Collections.Concurrent;

namespace PocketCalculator.Net.Tests;

/// <summary>
/// SECURITY.md I7: an HSTS store per client (browser context) and mixed-content
/// blocking for requests from https documents. The Rust engine has neither.
/// </summary>
public sealed class HstsAndMixedContentTests
{
    // ---------------------------------------------------------------- header grammar

    [Theory]
    [InlineData("max-age=31536000", true, 31536000, false)]
    [InlineData("max-age=600; includeSubDomains", true, 600, true)]
    [InlineData("MAX-AGE=\"600\" ; INCLUDESUBDOMAINS ; preload", true, 600, true)]
    [InlineData("max-age=99999999999999999999", true, 31536000, false)]
    [InlineData("max-age=0", true, 0, false)]
    [InlineData("includeSubDomains", false, 0, false)]
    [InlineData("max-age=1; max-age=2", false, 0, false)]
    [InlineData("max-age=-1", false, 0, false)]
    [InlineData("max-age=1.5", false, 0, false)]
    [InlineData("max-age=", false, 0, false)]
    [InlineData("max-age=1; includeSubDomains; includeSubDomains", false, 0, false)]
    [InlineData("max-age=1; includeSubDomains=1", false, 0, false)]
    [InlineData("", false, 0, false)]
    public void HeaderGrammarFollowsRfc6797(string header, bool valid, long seconds, bool subdomains)
    {
        Assert.Equal(valid, HstsStore.TryParseHeader(header, out var maxAge, out var include));
        if (valid)
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), maxAge);
            Assert.Equal(subdomains, include);
        }
    }

    [Fact]
    public void StoreHonoursScopeExpiryAndRemoval()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new HstsStore(() => now);

        Assert.True(store.ProcessHeader(new Uri("https://a.example/"), "max-age=100"));
        Assert.True(store.ProcessHeader(new Uri("https://b.example/"), "max-age=100; includeSubDomains"));
        Assert.True(store.ShouldUpgrade("a.example"));
        Assert.True(store.ShouldUpgrade("A.Example."));
        Assert.False(store.ShouldUpgrade("x.a.example"));
        Assert.True(store.ShouldUpgrade("x.y.b.example"));
        Assert.False(store.ShouldUpgrade("example"));

        // Over plain http, and for an IP host, the header means nothing.
        Assert.False(store.ProcessHeader(new Uri("http://c.example/"), "max-age=100"));
        store.ProcessHeader(new Uri("https://10.0.0.1/"), "max-age=100");
        store.ProcessHeader(new Uri("https://[::1]/"), "max-age=100");
        Assert.False(store.ShouldUpgrade("c.example"));
        Assert.Equal(2, store.Count);

        Assert.Equal(new Uri("https://a.example/p?q=1"), store.Upgrade(new Uri("http://a.example/p?q=1")));
        Assert.Equal(new Uri("https://a.example/"), store.Upgrade(new Uri("http://a.example:80/")));
        Assert.Equal(new Uri("https://a.example:8080/"), store.Upgrade(new Uri("http://a.example:8080/")));
        Assert.Null(store.Upgrade(new Uri("https://a.example/")));

        // max-age=0 removes the entry; time passing expires the other.
        store.ProcessHeader(new Uri("https://a.example/"), "max-age=0");
        Assert.False(store.ShouldUpgrade("a.example"));
        now = now.AddSeconds(101);
        Assert.False(store.ShouldUpgrade("x.b.example"));
    }

    [Fact]
    public void MaxAgeIsCappedAtOneYear()
    {
        var now = DateTimeOffset.UnixEpoch;
        var store = new HstsStore(() => now);
        store.Add("long.example", TimeSpan.FromDays(4000), includeSubdomains: false);
        now = now.AddDays(364);
        Assert.True(store.ShouldUpgrade("long.example"));
        now = now.AddDays(2);
        Assert.False(store.ShouldUpgrade("long.example"));
    }

    // ---------------------------------------------------------------- HSTS end to end

    [Fact]
    public async Task StsHeaderUpgradesLaterHttpRequestsAsAnInternalRedirect()
    {
        ConcurrentQueue<string> seen = new();
        using var fixture = PrivateCaHttpsFixture.Serve(respond: request =>
        {
            seen.Enqueue(request.Split("\r\n")[0]);
            const string body = "secure";
            return "HTTP/1.1 200 OK\r\ncontent-type: text/plain\r\n"
                + "strict-transport-security: max-age=600\r\n"
                + $"content-length: {body.Length}\r\nconnection: close\r\n\r\n{body}";
        });
        var caFile = Path.Combine(Path.GetTempPath(), $"obscura-ca-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(caFile, fixture.CaPem);
        try
        {
            using var env = new EnvironmentScope().Set("SSL_CERT_FILE", caFile).Set("SSL_CERT_DIR", null);
            using var client = new PocketCalculatorHttpClient(new CookieJar(), null, true);

            Assert.Equal(200, (await client.FetchAsync(new Uri($"https://localhost:{fixture.Port}/"))).Status);
            Assert.True(client.Hsts.ShouldUpgrade("localhost"));

            // Plain http to the TLS port only works if the request is upgraded first.
            var insecure = new Uri($"http://localhost:{fixture.Port}/next");
            var response = await client.FetchAsync(insecure);
            Assert.Equal(200, response.Status);
            Assert.Equal("secure", response.Text());
            Assert.Equal(new Uri($"https://localhost:{fixture.Port}/next"), response.Url);
            Assert.Equal([insecure], response.RedirectedFrom);

            // A different client (another browser context) knows nothing of it.
            using var other = new PocketCalculatorHttpClient(new CookieJar(), null, true);
            Assert.False(other.Hsts.ShouldUpgrade("localhost"));
        }
        finally
        {
            File.Delete(caFile);
        }
    }

    [Fact]
    public async Task StsHeaderOnAnIpHostIsIgnored()
    {
        using var fixture = PrivateCaHttpsFixture.Serve(respond: _ =>
            "HTTP/1.1 200 OK\r\nstrict-transport-security: max-age=600\r\ncontent-length: 0\r\nconnection: close\r\n\r\n");
        var caFile = Path.Combine(Path.GetTempPath(), $"obscura-ca-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(caFile, fixture.CaPem);
        try
        {
            using var env = new EnvironmentScope().Set("SSL_CERT_FILE", caFile).Set("SSL_CERT_DIR", null);
            using var client = new PocketCalculatorHttpClient(new CookieJar(), null, true);
            Assert.Equal(200, (await client.FetchAsync(new Uri($"https://127.0.0.1:{fixture.Port}/"))).Status);
            Assert.Equal(0, client.Hsts.Count);
        }
        finally
        {
            File.Delete(caFile);
        }
    }

    // ---------------------------------------------------------------- mixed content

    private sealed class FulfillAll : IRequestInterceptor
    {
        public ConcurrentQueue<Uri> Seen { get; } = new();

        public Task<InterceptAction> InterceptAsync(RequestInfo request)
        {
            Seen.Enqueue(request.Url);
            return Task.FromResult<InterceptAction>(new InterceptAction.Fulfill(new Response
            {
                Url = request.Url,
                Status = 200,
                Headers = new Dictionary<string, string>(StringComparer.Ordinal),
                Body = "ok"u8.ToArray(),
                RedirectedFrom = [],
            }));
        }
    }

    private static (PocketCalculatorHttpClient Client, FulfillAll Interceptor, CallbackRegistry Callbacks, List<(string, string)> Console)
        MixedContentClient()
    {
        var interceptor = new FulfillAll();
        var client = new PocketCalculatorHttpClient(new CookieJar(), null, true) { Interceptor = interceptor };
        var callbacks = new CallbackRegistry();
        List<(string, string)> console = [];
        callbacks.ConsoleSink = (level, text) =>
        {
            lock (console)
            {
                console.Add((level, text));
            }
        };
        return (client, interceptor, callbacks, console);
    }

    private static readonly Uri SecurePage = new("https://secure.example/page");

    [Theory]
    [InlineData(ResourceType.Script, "script")]
    [InlineData(ResourceType.Stylesheet, "stylesheet")]
    [InlineData(ResourceType.Font, "font")]
    [InlineData(ResourceType.Fetch, "resource")]
    public async Task ActiveMixedContentIsBlocked(ResourceType type, string kind)
    {
        var (client, interceptor, callbacks, console) = MixedContentClient();
        using (client)
        {
            var target = new Uri("http://insecure.example/x");
            var error = await Assert.ThrowsAsync<PocketCalculatorNetException>(() =>
                client.FetchResourceWithCallbacksAsync(target, ResourceRequest.Subresource(type, SecurePage), callbacks));
            Assert.Equal(PocketCalculatorNetErrorKind.Blocked, error.Kind);
            Assert.Empty(interceptor.Seen);
            var expected =
                $"Mixed Content: The page at 'https://secure.example/page' was loaded over HTTPS, but requested an insecure {kind} "
                + "'http://insecure.example/x'. This request has been blocked; the content must be served over HTTPS.";
            Assert.Equal([("error", expected)], console);
        }
    }

    [Fact]
    public async Task FrameNavigationToHttpIsBlockedButTopLevelIsNot()
    {
        var (client, interceptor, callbacks, console) = MixedContentClient();
        using (client)
        {
            var target = new Uri("http://insecure.example/");
            await Assert.ThrowsAsync<PocketCalculatorNetException>(() => client.FetchNavigationAsync(
                HttpMethod.Get, target, null, ResourceRequest.FrameNavigation(SecurePage), callbacks));
            Assert.Contains("insecure frame", console[0].Item2, StringComparison.Ordinal);

            var top = await client.FetchNavigationAsync(
                HttpMethod.Get, target, null, ResourceRequest.PageNavigation(SecurePage, false), callbacks);
            Assert.Equal(200, top.Status);
            Assert.Equal([target], interceptor.Seen);
        }
    }

    [Fact]
    public async Task PassiveMixedContentIsUpgraded()
    {
        var (client, interceptor, callbacks, console) = MixedContentClient();
        using (client)
        {
            var response = await client.FetchResourceWithCallbacksAsync(
                new Uri("http://insecure.example/a.png"),
                ResourceRequest.Subresource(ResourceType.Image, SecurePage),
                callbacks);
            Assert.Equal(200, response.Status);
            Assert.Equal([new Uri("https://insecure.example/a.png")], interceptor.Seen);
            Assert.Equal("warning", console[0].Item1);
            Assert.StartsWith(
                "Mixed Content: The page at 'https://secure.example/page' was loaded over HTTPS, but requested an insecure element "
                + "'http://insecure.example/a.png'. This request was automatically upgraded to HTTPS",
                console[0].Item2,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("https://secure.example/", "http://127.0.0.1:9/x.js")]
    [InlineData("https://secure.example/", "http://localhost:9/x.js")]
    [InlineData("https://secure.example/", "http://app.localhost/x.js")]
    [InlineData("https://secure.example/", "https://other.example/x.js")]
    [InlineData("http://plain.example/", "http://insecure.example/x.js")]
    public async Task TrustworthyTargetsAndInsecurePagesAreNotMixed(string page, string target)
    {
        var (client, interceptor, callbacks, console) = MixedContentClient();
        using (client)
        {
            var response = await client.FetchResourceWithCallbacksAsync(
                new Uri(target), ResourceRequest.Subresource(ResourceType.Script, new Uri(page)), callbacks);
            Assert.Equal(200, response.Status);
            Assert.Equal([new Uri(target)], interceptor.Seen);
            Assert.Empty(console);
        }
    }

    [Fact]
    public async Task AllowInsecureContentTurnsTheCheckOff()
    {
        var (client, interceptor, callbacks, console) = MixedContentClient();
        using (client)
        {
            client.AllowInsecureContent = true;
            var target = new Uri("http://insecure.example/x.js");
            var response = await client.FetchResourceWithCallbacksAsync(
                target, ResourceRequest.Subresource(ResourceType.Script, SecurePage), callbacks);
            Assert.Equal(200, response.Status);
            Assert.Equal([target], interceptor.Seen);
            Assert.Empty(console);
        }
    }

    [Fact]
    public async Task MixedContentBeatsHsts()
    {
        // Chromium decides mixed content in the renderer, before the network stack
        // consults HSTS, so a known HSTS host requested over http is still blocked.
        var (client, interceptor, callbacks, _) = MixedContentClient();
        using (client)
        {
            client.Hsts.Add("insecure.example", TimeSpan.FromHours(1), includeSubdomains: false);
            await Assert.ThrowsAsync<PocketCalculatorNetException>(() => client.FetchResourceWithCallbacksAsync(
                new Uri("http://insecure.example/x.js"),
                ResourceRequest.Subresource(ResourceType.Script, SecurePage),
                callbacks));
            Assert.Empty(interceptor.Seen);

            // A top-level navigation is upgraded instead (the interceptor answers the
            // https hop, so the fulfilled response carries no redirect list).
            _ = await client.FetchNavigationAsync(
                HttpMethod.Get, new Uri("http://insecure.example/"), null, ResourceRequest.Navigation(), callbacks);
            Assert.Equal([new Uri("https://insecure.example/")], interceptor.Seen);
        }
    }
}
