using System.Net;
using System.Text;

namespace Obscura.Net.Tests;

/// <summary>Port of the <c>#[cfg(test)] mod ssrf_tests</c> block in <c>client.rs</c>.</summary>
public class SsrfTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    [Fact]
    public void Ipv4PrivateAndSpecialRangesAreForbidden()
    {
        string[] samples =
        [
            "127.0.0.1",
            "127.5.6.7",
            "10.0.0.1",
            "172.16.0.1",
            "192.168.1.1",
            "169.254.169.254", // cloud-metadata endpoint
            "0.0.0.0",         // unspecified -> localhost (was a bypass)
            "255.255.255.255", // broadcast
            "192.0.2.1",       // documentation
        ];
        foreach (var s in samples)
        {
            Assert.True(SsrfGuard.IsForbiddenIp(Ip(s)), $"{s} should be forbidden");
        }
    }

    [Fact]
    public void PublicIpv4IsAllowed()
    {
        foreach (var s in new[] { "1.1.1.1", "8.8.8.8", "93.184.216.34" })
        {
            Assert.False(SsrfGuard.IsForbiddenIp(Ip(s)), $"{s} should be allowed");
        }
    }

    // SEC-401 / #810 - std's is_private() only covers RFC1918, so CGNAT and other
    // IANA special-purpose ranges (which host cloud metadata) must be blocked
    // explicitly, including their IPv4-mapped IPv6 forms.
    [Fact]
    public void Ipv4CgnatAndIanaSpecialRangesAreForbidden()
    {
        string[] samples =
        [
            "100.64.0.1",             // CGNAT / RFC 6598 start
            "100.100.100.200",        // Alibaba Cloud metadata (CGNAT)
            "100.127.255.255",        // CGNAT end
            "198.18.0.1",             // benchmarking / RFC 2544 start
            "198.19.255.255",         // benchmarking end
            "192.88.99.1",            // 6to4 relay anycast / RFC 7526
            "::ffff:100.100.100.200", // v4-mapped CGNAT
        ];
        foreach (var s in samples)
        {
            Assert.True(SsrfGuard.IsForbiddenIp(Ip(s)), $"{s} should be forbidden");
        }
    }

    // Addresses just outside those prefixes must stay allowed (no over-block).
    [Fact]
    public void Ipv4AddressesAdjacentToSpecialRangesStayAllowed()
    {
        string[] samples =
        [
            "100.63.255.255", // just below 100.64.0.0/10
            "100.128.0.0",    // just above 100.127.255.255
            "198.17.255.255", // just below 198.18.0.0/15
            "198.20.0.0",     // just above 198.19.255.255
            "192.88.98.255",  // just below 192.88.99.0/24
            "192.88.100.0",   // just above 192.88.99.0/24
        ];
        foreach (var s in samples)
        {
            Assert.False(SsrfGuard.IsForbiddenIp(Ip(s)), $"{s} should be allowed");
        }
    }

    [Fact]
    public void RemainingNonGlobalIpv4RangesAreForbiddenWithoutBlockingExceptions()
    {
        string[] forbidden =
        [
            "0.1.2.3",
            "192.0.0.8",
            "192.0.0.192",
            "224.0.0.1",
            "239.255.255.255",
            "240.0.0.1",
            "255.255.255.254",
        ];
        foreach (var s in forbidden)
        {
            Assert.True(SsrfGuard.IsForbiddenIp(Ip(s)), $"{s} should be forbidden");
        }

        // IANA marks these specific protocol anycast addresses and these
        // special-purpose /24s globally reachable. Blocking them would be a network
        // compatibility regression, not an SSRF hardening win.
        string[] allowed =
        [
            "192.0.0.9",
            "192.0.0.10",
            "192.31.196.1",
            "192.52.193.1",
            "192.175.48.1",
        ];
        foreach (var s in allowed)
        {
            Assert.False(SsrfGuard.IsForbiddenIp(Ip(s)), $"{s} should be allowed");
        }
    }

    [Fact]
    public void Ipv6LoopbackUlaLinklocalAndMappedAreForbidden()
    {
        string[] samples =
        [
            "::1",                    // loopback
            "::",                     // unspecified
            "fc00::1",                // unique-local (was a bypass)
            "fd12:3456:789a::1",      // unique-local
            "fe80::1",                // link-local
            "::ffff:127.0.0.1",       // v4-mapped loopback (was a bypass)
            "::ffff:169.254.169.254", // v4-mapped metadata
        ];
        foreach (var s in samples)
        {
            Assert.True(SsrfGuard.IsForbiddenIp(Ip(s)), $"{s} should be forbidden");
        }
    }

    [Fact]
    public void PublicIpv6IsAllowed()
    {
        string[] samples =
        [
            "2606:4700:4700::1111", // Cloudflare DNS
            "3ffe::1",              // below 3fff::/20
            "3fff:1000::1",         // above 3fff:fff::/20
        ];
        foreach (var s in samples)
        {
            Assert.False(SsrfGuard.IsForbiddenIp(Ip(s)), $"{s} should be allowed");
        }
    }

    [Fact]
    public void Ipv6TranslationCannotHideForbiddenIpv4()
    {
        string[] forbidden =
        [
            "2002:7f00:1::",      // 6to4 loopback
            "2002:a9fe:a9fe::",   // 6to4 link-local metadata
            "2002:6464:64c8::",   // 6to4 CGNAT metadata
            "64:ff9b::7f00:1",    // NAT64 loopback
            "64:ff9b::a9fe:a9fe", // NAT64 link-local metadata
            "64:ff9b::6464:64c8", // NAT64 CGNAT metadata
            "64:ff9b:1::1",       // local-use translation prefix
        ];
        foreach (var s in forbidden)
        {
            Assert.True(SsrfGuard.IsForbiddenIp(Ip(s)), $"{s} should be forbidden");
        }

        foreach (var s in new[] { "2002:808:808::", "64:ff9b::808:808" })
        {
            Assert.False(SsrfGuard.IsForbiddenIp(Ip(s)), $"{s} should be allowed");
        }
    }

    [Fact]
    public void NativeNonGlobalIpv6RangesAreForbidden()
    {
        string[] samples =
        [
            "100::1",      // discard-only
            "2001:db8::1", // documentation
            "3fff::1",     // documentation
            "ff02::1",     // link-local multicast
            "ff0e::1",     // global-scope multicast
        ];
        foreach (var s in samples)
        {
            Assert.True(SsrfGuard.IsForbiddenIp(Ip(s)), $"{s} should be forbidden");
        }
    }

    [Fact]
    public void ValidateUrlBlocksUnspecifiedAndAllowsPublic()
    {
        // 0.0.0.0 previously slipped through the literal-host check.
        AssertBlocked("http://0.0.0.0:8080/");
        AssertBlocked("http://127.0.0.1/");
        SsrfGuard.ValidateUrl(new Uri("http://example.com/"), false);
        AssertBlocked("http://[64:ff9b::7f00:1]/");
        AssertBlocked("http://[2002:a9fe:a9fe::]/");
        SsrfGuard.ValidateUrl(new Uri("http://192.0.0.9/"), false);
        SsrfGuard.ValidateUrl(new Uri("http://[64:ff9b::808:808]/"), false);
        // The allow flag bypasses the guard (local-dev escape hatch).
        SsrfGuard.ValidateUrl(new Uri("http://127.0.0.1/"), true);
    }

    private static void AssertBlocked(string url) =>
        Assert.Throws<ObscuraNetException>(() => SsrfGuard.ValidateUrl(new Uri(url), false));

    [Fact]
    public void ResourceProfilesUseTypeSpecificFetchMetadata()
    {
        var document = new Uri("https://app.example/page?q=1#fragment");
        var image = ResourceRequest.Subresource(ResourceType.Image, document);
        Assert.Equal(RequestMode.NoCors, image.Mode);
        Assert.Equal(RequestCredentials.Include, image.Credentials);
        Assert.Equal("image", image.Destination());
        Assert.StartsWith("image/webp", image.Accept(), StringComparison.Ordinal);

        var stylesheet = ResourceRequest.Subresource(ResourceType.Stylesheet, document);
        Assert.Equal("style", stylesheet.Destination());
        Assert.Equal("text/css,*/*;q=0.1", stylesheet.Accept());

        var font = ResourceRequest.Subresource(ResourceType.Font, document);
        Assert.Equal(RequestMode.Cors, font.Mode);
        Assert.Equal(RequestCredentials.SameOrigin, font.Credentials);
        Assert.Equal("font", font.Destination());
        Assert.Equal("*/*", font.Accept());

        Assert.True(image.SendsCredentialsTo(new Uri("https://cdn.example/image.png")));
        Assert.True(font.SendsCredentialsTo(new Uri("https://app.example/font.woff2")));
        Assert.False(font.SendsCredentialsTo(new Uri("https://cdn.example/font.woff2")));

        var module = ResourceRequest.ModuleScript(document, document);
        Assert.Equal(ResourceType.Script, module.ResourceType);
        Assert.Equal(RequestMode.Cors, module.Mode);
        Assert.Equal(RequestCredentials.SameOrigin, module.Credentials);
        Assert.Equal("script", module.Destination());
        Assert.Equal("*/*", module.Accept());
        Assert.True(module.SendsCredentialsTo(new Uri("https://app.example/chunk.js")));
        Assert.False(module.SendsCredentialsTo(new Uri("https://cdn.example/chunk.js")));
    }

    [Fact]
    public void SubresourceReferrerAndFetchSiteFollowDefaultBrowserPolicy()
    {
        var source = new Uri("https://user:secret@app.example/path?q=1#frag");
        var request = ResourceRequest.Subresource(ResourceType.Image, source);
        var sameOrigin = new Uri("https://app.example/image.png");
        var crossOrigin = new Uri("https://cdn.example/image.png");
        var downgrade = new Uri("http://cdn.example/image.png");

        Assert.Equal("same-origin", ObscuraHttpClient.RequestFetchSite(request, sameOrigin));
        Assert.Equal("cross-site", ObscuraHttpClient.RequestFetchSite(request, crossOrigin));
        Assert.Equal(
            "https://app.example/path?q=1",
            ObscuraHttpClient.RequestReferrer(request, sameOrigin));
        Assert.Equal(
            "https://app.example/",
            ObscuraHttpClient.RequestReferrer(request, crossOrigin));
        Assert.Null(ObscuraHttpClient.RequestReferrer(request, downgrade));
    }

    // WPT fetch/api/redirect/redirect-count: the 20th redirect must still be
    // followed, the 21st must fail. Guards the inclusive redirect bound in the fetch
    // loop; an exclusive bound regressed this to 19.
    [Fact]
    public async Task NavigationFollowsTwentyRedirectsButNotTwentyOne()
    {
        var pass = Enumerable.Repeat(HttpFixture.RedirectToSelf(), 20)
            .Append(HttpFixture.OkResponse(string.Empty, "arrived"))
            .ToList();
        using var passFixture = HttpFixture.Serve(pass);
        using var client = new ObscuraHttpClient(new CookieJar(), null, true);
        var response = await client.FetchAsync(passFixture.Url);
        Assert.Equal("arrived"u8.ToArray(), response.Body);

        var fail = Enumerable.Repeat(HttpFixture.RedirectToSelf(), 21).ToList();
        using var failFixture = HttpFixture.Serve(fail);
        using var failClient = new ObscuraHttpClient(new CookieJar(), null, true);
        var error = await Assert.ThrowsAsync<ObscuraNetException>(
            () => failClient.FetchAsync(failFixture.Url));
        Assert.Equal(ObscuraNetErrorKind.TooManyRedirects, error.Kind);
    }

    [Fact]
    public async Task CrossOriginFontSendsOriginAndOmitsCrossOriginCookies()
    {
        using var fixture = HttpFixture.Serve([
            HttpFixture.OkResponse(
                "Access-Control-Allow-Origin: *\r\nSet-Cookie: rejected=1; Path=/\r\n",
                "font"),
        ]);
        var initiator = new Uri("http://127.0.0.1:1/page");
        var jar = new CookieJar();
        jar.SetCookie("seed=1; Path=/", fixture.Url);
        using var client = new ObscuraHttpClient(jar, null, true);

        var response = await client.FetchResourceWithCallbacksAsync(
            fixture.Url,
            ResourceRequest.Subresource(ResourceType.Font, initiator),
            null);
        Assert.Equal("font"u8.ToArray(), response.Body);
        var request = (await fixture.NextRequestAsync()).ToLowerInvariant();
        Assert.Contains("origin: http://127.0.0.1:1\r\n", request, StringComparison.Ordinal);
        Assert.Contains("sec-fetch-mode: cors\r\n", request, StringComparison.Ordinal);
        Assert.Contains("sec-fetch-dest: font\r\n", request, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie:", request, StringComparison.Ordinal);
        Assert.Equal("seed=1", jar.GetCookieHeader(fixture.Url));
    }

    // #849 - the OBSCURA_FETCH_MAX_BODY_BYTES override #581 gave fetch()/XHR must
    // also reach the module-script cap; a large SPA bundle otherwise dies silently at
    // a hardcoded 32 MiB while fetch() of the same URL succeeds.
    [Fact]
    public void ModuleScriptCapHonoursTheFetchBodyEnvOverride()
    {
        var u = new Uri("https://example.com/app.mjs");
        using var env = new EnvironmentScope();

        env.Set("OBSCURA_FETCH_MAX_BODY_BYTES", null);
        Assert.Equal(32L * 1024 * 1024, ResourceRequest.ModuleScript(u, u).MaxResponseBytes);

        Environment.SetEnvironmentVariable("OBSCURA_FETCH_MAX_BODY_BYTES", "134217728");
        Assert.Equal(128L * 1024 * 1024, ResourceRequest.ModuleScript(u, u).MaxResponseBytes);

        // Garbage stays on the default rather than throwing.
        Environment.SetEnvironmentVariable("OBSCURA_FETCH_MAX_BODY_BYTES", "not-a-number");
        Assert.Equal(32L * 1024 * 1024, ResourceRequest.ModuleScript(u, u).MaxResponseBytes);
    }

    [Fact]
    public async Task CrossOriginModuleUsesCorsScriptProfileWithoutCredentials()
    {
        using var fixture = HttpFixture.Serve([
            HttpFixture.OkResponse(
                "Access-Control-Allow-Origin: *\r\nSet-Cookie: rejected=1; Path=/\r\n",
                "export default 1;"),
        ]);
        var initiator = new Uri("http://127.0.0.1:1/page");
        var importingModule = new Uri(fixture.Url, "/parent.js");
        var jar = new CookieJar();
        jar.SetCookie("seed=1; Path=/", fixture.Url);
        using var client = new ObscuraHttpClient(jar, null, true);

        var response = await client.FetchResourceWithCallbacksAsync(
            fixture.Url,
            ResourceRequest.ModuleScript(initiator, importingModule),
            null);
        Assert.Equal("export default 1;"u8.ToArray(), response.Body);
        var request = (await fixture.NextRequestAsync()).ToLowerInvariant();
        Assert.Contains("origin: http://127.0.0.1:1\r\n", request, StringComparison.Ordinal);
        Assert.Contains("sec-fetch-mode: cors\r\n", request, StringComparison.Ordinal);
        Assert.Contains("sec-fetch-dest: script\r\n", request, StringComparison.Ordinal);
        Assert.Contains(
            $"referer: {importingModule.AbsoluteUri.ToLowerInvariant()}\r\n",
            request,
            StringComparison.Ordinal);
        Assert.DoesNotContain("cookie:", request, StringComparison.Ordinal);
        Assert.Equal("seed=1", jar.GetCookieHeader(fixture.Url));
    }

    [Fact]
    public async Task CredentialedCorsRejectsWildcardAndAcceptsExactOrigin()
    {
        var initiator = new Uri("http://127.0.0.1:1/page");
        var wildcard = HttpFixture.OkResponse(
            "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Credentials: true\r\n",
            "blocked");
        var exact = HttpFixture.OkResponse(
            "Access-Control-Allow-Origin: http://127.0.0.1:1\r\n"
            + "Access-Control-Allow-Credentials: true\r\nSet-Cookie: accepted=1; Path=/\r\n",
            "allowed");
        using var fixture = HttpFixture.Serve([wildcard, exact]);
        var jar = new CookieJar();
        jar.SetCookie("seed=1; Path=/", fixture.Url);
        using var client = new ObscuraHttpClient(jar, null, true);
        var request = ResourceRequest.Subresource(ResourceType.Image, initiator);
        request.Mode = RequestMode.Cors;
        request.Credentials = RequestCredentials.Include;

        var error = await Assert.ThrowsAsync<ObscuraNetException>(
            () => client.FetchResourceWithCallbacksAsync(fixture.Url, request.Copy(), null));
        Assert.Equal(ObscuraNetErrorKind.Cors, error.Kind);
        await client.FetchResourceWithCallbacksAsync(fixture.Url, request, null);

        var first = (await fixture.NextRequestAsync()).ToLowerInvariant();
        var second = (await fixture.NextRequestAsync()).ToLowerInvariant();
        Assert.Contains("cookie: seed=1\r\n", first, StringComparison.Ordinal);
        Assert.Contains("cookie: seed=1\r\n", second, StringComparison.Ordinal);
        var cookies = jar.GetCookieHeader(fixture.Url);
        Assert.Contains("seed=1", cookies, StringComparison.Ordinal);
        Assert.Contains("accepted=1", cookies, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameOriginFontNeedsNoCorsHeaderAndSendsCookies()
    {
        using var fixture = HttpFixture.Serve([HttpFixture.OkResponse(string.Empty, "same")]);
        var initiator = new Uri(fixture.Url, "/page");
        var jar = new CookieJar();
        jar.SetCookie("same=1; Path=/", fixture.Url);
        using var client = new ObscuraHttpClient(jar, null, true);
        await client.FetchResourceWithCallbacksAsync(
            fixture.Url,
            ResourceRequest.Subresource(ResourceType.Font, initiator),
            null);
        var request = (await fixture.NextRequestAsync()).ToLowerInvariant();
        Assert.DoesNotContain("origin:", request, StringComparison.Ordinal);
        Assert.Contains("cookie: same=1\r\n", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptLanguageOverrideReachesTheWire()
    {
        using var fixture = HttpFixture.Serve([HttpFixture.OkResponse(string.Empty, "ok")]);
        using var client = new ObscuraHttpClient(new CookieJar(), null, true);
        client.SetAcceptLanguage("de-DE,de;q=0.9");

        await client.FetchAsync(fixture.Url);

        var request = await fixture.NextRequestAsync();
        Assert.Contains(
            request.Split("\r\n"),
            line => line.Equals("accept-language: de-DE,de;q=0.9", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResponseLimitsRejectContentLengthAndStreamedOverflow()
    {
        const string advertised = "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n";
        const string chunked =
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
            + "4\r\nabcd\r\n4\r\nefgh\r\n0\r\n\r\n";
        using var fixture = HttpFixture.Serve([advertised, chunked]);
        using var client = new ObscuraHttpClient(new CookieJar(), null, true);
        var initiator = fixture.Url;
        var request = ResourceRequest.Subresource(ResourceType.Image, initiator)
            .WithMaxResponseBytes(6);

        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<ResponseTooLargeException>(
                () => client.FetchResourceWithCallbacksAsync(fixture.Url, request.Copy(), null));
            Assert.Equal(6, error.Limit);
            Assert.Equal(0, client.ActiveRequests);
        }
    }

    [Fact]
    public async Task CancellationReturnsActiveRequestsToZero()
    {
        using var fixture = HangingFixture.Serve();
        using var client = new ObscuraHttpClient(new CookieJar(), null, true);
        using var cancellation = new CancellationTokenSource();
        var task = Task.Run(() => client.FetchAsync(fixture.Url, cancellation.Token));
        await fixture.Started.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, client.ActiveRequests);
        await cancellation.CancelAsync();
        try
        {
            await task;
        }
        catch (Exception error) when (error is OperationCanceledException or ObscuraNetException)
        {
            // expected
        }

        Assert.Equal(0, client.ActiveRequests);
    }

    [Fact]
    public async Task CancelledSharedSubresourceLeaderWakesFollowerAndClearsSlot()
    {
        using var fixture = CancelledSharedFetchFixture.Serve();
        var initiator = new Uri(fixture.Url, "/page.html");
        var request = ResourceRequest.Subresource(ResourceType.Script, initiator);
        using var client = new ObscuraHttpClient(new CookieJar(), null, true);

        using var leaderCancellation = new CancellationTokenSource();
        var leader = Task.Run(() => client.FetchResourceWithCallbacksAsync(
            fixture.Url, request.Copy(), null, leaderCancellation.Token));
        await fixture.Started.WaitAsync(TimeSpan.FromSeconds(10));

        var follower = Task.Run(() => client.FetchResourceWithCallbacksAsync(
            fixture.Url, request.Copy(), null));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!client.HasWaitingSharedFollowers())
        {
            Assert.True(DateTime.UtcNow < deadline, "follower did not join the shared fetch");
            await Task.Delay(5);
        }

        await leaderCancellation.CancelAsync();
        try
        {
            await leader;
        }
        catch (Exception error) when (error is OperationCanceledException or ObscuraNetException)
        {
            // expected
        }

        var response = await follower.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("shared"u8.ToArray(), response.Body);

        // The follower intentionally retried without populating the cache. A
        // subsequent request must be able to install a fresh leader, and its
        // successful response is then reusable.
        await client.FetchResourceWithCallbacksAsync(fixture.Url, request.Copy(), null);
        await client.FetchResourceWithCallbacksAsync(fixture.Url, request.Copy(), null);
        Assert.Equal(3, fixture.NetworkRequests);
    }

    [Fact]
    public async Task TransportTimeoutReturnsActiveRequestsToZero()
    {
        using var fixture = HangingFixture.Serve();
        using var client = new ObscuraHttpClient(new CookieJar(), null, true)
        {
            Timeout = TimeSpan.FromMilliseconds(250),
        };
        var error = await Assert.ThrowsAsync<ObscuraNetException>(() => client.FetchAsync(fixture.Url));
        Assert.Equal(ObscuraNetErrorKind.Network, error.Kind);
        Assert.Equal(0, client.ActiveRequests);
    }

    [Fact]
    public async Task CallbacksFireOnceAcrossRedirects()
    {
        const string redirect =
            "HTTP/1.1 302 Found\r\nLocation: /final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        using var fixture = HttpFixture.Serve([redirect, HttpFixture.OkResponse(string.Empty, "done")]);
        using var client = new ObscuraHttpClient(new CookieJar(), null, true);
        var callbacks = new CallbackRegistry();
        var requests = 0;
        var responses = 0;
        callbacks.AddRequest(_ => Interlocked.Increment(ref requests));
        callbacks.AddResponse((_, _) => Interlocked.Increment(ref responses));

        await client.FetchWithCallbacksAsync(fixture.Url, callbacks);
        Assert.Equal(1, requests);
        Assert.Equal(1, responses);
    }

    [Fact]
    public async Task CacheableIdenticalSubresourcesShareOneInFlightRequest()
    {
        using var fixture = CacheableResourceFixture.Serve(
            200,
            "Cache-Control: public, max-age=3600\r\nVary: Accept-Language\r\n");
        var initiator = new Uri(fixture.Url, "/page.html");
        using var client = new ObscuraHttpClient(new CookieJar(), null, true);
        var callbacks = new CallbackRegistry();
        var callbackRequests = 0;
        var callbackResponses = 0;
        callbacks.AddRequest(_ => Interlocked.Increment(ref callbackRequests));
        callbacks.AddResponse((_, _) => Interlocked.Increment(ref callbackResponses));

        var fetches = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            client.FetchResourceWithCallbacksAsync(
                fixture.Url,
                ResourceRequest.Subresource(ResourceType.Script, initiator),
                callbacks))).ToArray();
        var responses = await Task.WhenAll(fetches);

        Assert.Equal(32, responses.Length);
        Assert.All(responses, response => Assert.Equal(200, response.Status));
        Assert.Equal(1, fixture.NetworkRequests);
        Assert.Equal(32, callbackRequests);
        Assert.Equal(32, callbackResponses);
    }

    [Fact]
    public async Task CacheableIdenticalModuleScriptsShareOneInFlightRequest()
    {
        using var fixture = CacheableResourceFixture.Serve(
            200,
            "Cache-Control: public, max-age=3600\r\n");
        var initiator = new Uri(fixture.Url, "/app.js");
        using var client = new ObscuraHttpClient(new CookieJar(), null, true);

        var fetches = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            client.FetchResourceWithCallbacksAsync(
                fixture.Url,
                ResourceRequest.ModuleScript(initiator, initiator),
                null))).ToArray();
        var responses = await Task.WhenAll(fetches);

        Assert.Equal(16, responses.Length);
        Assert.All(responses, response => Assert.Equal(200, response.Status));
        Assert.Equal(1, fixture.NetworkRequests);
    }

    [Fact]
    public async Task DistinctSubresourceUrlsDoNotCoalesce()
    {
        using var fixture = CacheableResourceFixture.Serve(
            200,
            "Cache-Control: public, max-age=3600\r\n");
        var initiator = new Uri(fixture.Url, "/page.html");
        using var client = new ObscuraHttpClient(new CookieJar(), null, true);

        var fetches = Enumerable.Range(0, 24).Select(index => Task.Run(() =>
            client.FetchResourceWithCallbacksAsync(
                new Uri(fixture.Url, $"/distinct/{index}.js"),
                ResourceRequest.Subresource(ResourceType.Script, initiator),
                null))).ToArray();
        var responses = await Task.WhenAll(fetches);

        Assert.Equal(24, responses.Length);
        Assert.Equal(24, fixture.NetworkRequests);
    }

    [Fact]
    public async Task NoStoreVaryStarAndErrorResponsesAreNotReused()
    {
        (int Status, string Headers)[] cases =
        [
            (200, "Cache-Control: no-store\r\n"),
            (200, "Cache-Control: public, max-age=3600\r\nVary: *\r\n"),
            (500, "Cache-Control: public, max-age=3600\r\n"),
        ];
        foreach (var (status, headers) in cases)
        {
            using var fixture = CacheableResourceFixture.Serve(status, headers);
            var initiator = new Uri(fixture.Url, "/page.html");
            using var client = new ObscuraHttpClient(new CookieJar(), null, true);
            var request = ResourceRequest.Subresource(ResourceType.Script, initiator);
            await client.FetchResourceWithCallbacksAsync(fixture.Url, request.Copy(), null);
            await client.FetchResourceWithCallbacksAsync(fixture.Url, request, null);
            Assert.Equal(2, fixture.NetworkRequests);
        }
    }

    [Fact]
    public async Task AuthorizationAndCookieBearingRequestsBypassResourceCache()
    {
        (string Name, string Value)[] cases =
        [
            ("Authorization", "Bearer secret"),
            ("Cookie", "session=secret"),
        ];
        foreach (var (name, value) in cases)
        {
            using var fixture = CacheableResourceFixture.Serve(
                200,
                "Cache-Control: public, max-age=3600\r\n");
            var initiator = new Uri(fixture.Url, "/page.html");
            using var client = new ObscuraHttpClient(new CookieJar(), null, true);
            client.SetExtraHeaders(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [name] = value,
            });
            var request = ResourceRequest.Subresource(ResourceType.Script, initiator);
            await client.FetchResourceWithCallbacksAsync(fixture.Url, request.Copy(), null);
            await client.FetchResourceWithCallbacksAsync(fixture.Url, request, null);
            Assert.Equal(2, fixture.NetworkRequests);
        }
    }

    [Fact]
    public async Task ResolverBlocksHostnameThatResolvesToLoopback()
    {
        // localtest.me is a public DNS name that resolves to 127.0.0.1 - the canonical
        // DNS-rebinding test. The guard must reject it. If DNS is unavailable the
        // lookup itself errors (also a throw), so the assertion holds either way.
        var resolver = new SsrfGuardResolver(false);
        await Assert.ThrowsAnyAsync<Exception>(() => resolver.ResolveAsync("localtest.me"));
    }

    [Fact]
    public async Task ResolverDoesNotSsrfBlockPublicHost()
    {
        // A public host must not be SSRF-blocked. Tolerate a no-network sandbox by only
        // failing on an actual SSRF rejection, not a lookup failure.
        var resolver = new SsrfGuardResolver(false);
        try
        {
            await resolver.ResolveAsync("example.com");
        }
        catch (Exception error)
        {
            Assert.DoesNotContain("SSRF blocked", error.Message, StringComparison.Ordinal);
        }
    }

    // The two configured-roots tests set SSL_CERT_FILE / SSL_CERT_DIR, which the
    // client reads when its transport is first built. Test parallelization is off for
    // this assembly and each test restores the environment, so they do not race.
    [Fact]
    public async Task ConfiguredRootsTrustAPrivateCaViaSslCertFile()
    {
        using var fixture = PrivateCaHttpsFixture.Serve();
        var caFile = Path.Combine(Path.GetTempPath(), $"obscura-ca-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(caFile, fixture.CaPem);
        try
        {
            using var env = new EnvironmentScope().Set("SSL_CERT_FILE", caFile).Set("SSL_CERT_DIR", null);
            using var client = new ObscuraHttpClient(new CookieJar(), null, true);
            var url = new Uri($"https://127.0.0.1:{fixture.Port}/");
            var response = await client.FetchAsync(url);
            Assert.Equal(200, response.Status);
            Assert.Equal("private ca ok", response.Text());
        }
        finally
        {
            File.Delete(caFile);
        }
    }

    [Fact]
    public async Task ConfiguredRootsTrustAPrivateCaViaSslCertDir()
    {
        using var fixture = PrivateCaHttpsFixture.Serve();
        var caDir = Directory.CreateTempSubdirectory("obscura-ca");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(caDir.FullName, "private-ca.pem"), fixture.CaPem);
            using var env = new EnvironmentScope()
                .Set("SSL_CERT_DIR", caDir.FullName)
                .Set("SSL_CERT_FILE", null);
            using var client = new ObscuraHttpClient(new CookieJar(), null, true);
            var url = new Uri($"https://127.0.0.1:{fixture.Port}/");
            var response = await client.FetchAsync(url);
            Assert.Equal(200, response.Status);
            Assert.Equal("private ca ok", response.Text());
        }
        finally
        {
            caDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PrivateCaIsStillRejectedWithoutSslCertFile()
    {
        // The same fixture that the SSL_CERT_FILE test trusts must fail here. The
        // listener is reachable (same setup), so an error can only be TLS.
        using var fixture = PrivateCaHttpsFixture.Serve();
        using var env = new EnvironmentScope().Set("SSL_CERT_FILE", null).Set("SSL_CERT_DIR", null);
        using var client = new ObscuraHttpClient(new CookieJar(), null, true);
        var url = new Uri($"https://127.0.0.1:{fixture.Port}/");
        await Assert.ThrowsAsync<ObscuraNetException>(() => client.FetchAsync(url));
    }
}

/// <summary>Port of the <c>#[cfg(test)] mod cert_env_tests</c> block in <c>client.rs</c>.</summary>
public class CertEnvTests
{
    [Fact]
    public void EmptySslCertEnvIsTreatedAsUnset()
    {
        // Set-but-empty must NOT request a custom store: for a transport that replaces
        // its bundled roots that would build a near-empty store and break all HTTPS.
        Assert.False(CertificateRoots.CustomCertStoreRequested(string.Empty, null));
        Assert.False(CertificateRoots.CustomCertStoreRequested(null, string.Empty));
        Assert.False(CertificateRoots.CustomCertStoreRequested(string.Empty, string.Empty));
        // Genuinely unset: no custom store.
        Assert.False(CertificateRoots.CustomCertStoreRequested(null, null));
        // Set and non-empty: build the custom store (behavior unchanged).
        Assert.True(CertificateRoots.CustomCertStoreRequested("/etc/corp/ca.pem", null));
        Assert.True(CertificateRoots.CustomCertStoreRequested(null, "/etc/ssl/certs"));
    }
}
