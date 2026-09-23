using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PocketCalculator.Net.Tests;

/// <summary>
/// Regression tests for the transport findings in SECURITY.md: ambient proxies and
/// proxied targets (M2), body read deadlines (M3), tracker blocking on every hop (L7),
/// request-scoped interceptor headers (L8) and the SSRF deny-set (I9).
/// </summary>
public sealed class TransportHardeningTests
{
    // ---------------------------------------------------------------- I9

    [Theory]
    [InlineData("fec0::1")]
    [InlineData("feff:ffff::1")]
    [InlineData("2001::1")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    public void SiteLocalAndTeredoAreForbidden(string address) =>
        Assert.True(SsrfGuard.IsForbiddenIp(IPAddress.Parse(address)), $"{address} should be forbidden");

    [Theory]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2001:1::1")]
    [InlineData("fe00::1")]
    public void NeighboursOfTheNewRangesStayAllowed(string address) =>
        Assert.False(SsrfGuard.IsForbiddenIp(IPAddress.Parse(address)), $"{address} should be allowed");

    // ---------------------------------------------------------------- M2

    [Fact]
    public async Task AmbientProxyEnvironmentIsIgnored()
    {
        // HttpClient.DefaultProxy is where HTTP_PROXY / HTTPS_PROXY land; it is read
        // once per process, so the test sets it directly rather than the environment.
        using var proxy = new RecordingProxy();
        var previous = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy(proxy.Url);
        try
        {
            using var client = new PocketCalculatorHttpClient(new CookieJar(), null, false)
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            try
            {
                // .example never resolves, so a direct request fails; only a proxy answers.
                await client.FetchAsync(new Uri("http://ambient-proxy-target.example/"));
            }
            catch (PocketCalculatorNetException)
            {
            }

            Assert.Equal(0, proxy.Requests);
        }
        finally
        {
            HttpClient.DefaultProxy = previous;
        }
    }

    [Fact]
    public async Task ProxiedTargetResolvingPrivateIsRefused()
    {
        using var proxy = new RecordingProxy();
        using var client = new PocketCalculatorHttpClient(new CookieJar(), proxy.Url, allowPrivateNetwork: false)
        {
            ProxyTargetResolver = (host, _) => Task.FromResult(
                host == "metadata.internal.example"
                    ? new[] { IPAddress.Parse("169.254.169.254") }
                    : [IPAddress.Parse("93.184.216.34")]),
        };

        var error = await Assert.ThrowsAsync<PocketCalculatorNetException>(
            () => client.FetchAsync(new Uri("http://metadata.internal.example/latest/meta-data/")));
        Assert.Contains("resolves to forbidden address 169.254.169.254", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, proxy.Requests);

        var ok = await client.FetchAsync(new Uri("http://public.example/"));
        Assert.Equal(200, ok.Status);
        Assert.Equal(1, proxy.Requests);
    }

    [Fact]
    public async Task ProxiedTargetThatDoesNotResolveIsRefused()
    {
        using var proxy = new RecordingProxy();
        using var client = new PocketCalculatorHttpClient(new CookieJar(), proxy.Url, allowPrivateNetwork: false)
        {
            ProxyTargetResolver = (_, _) => throw new SocketException((int)SocketError.HostNotFound),
        };

        var error = await Assert.ThrowsAsync<PocketCalculatorNetException>(
            () => client.FetchAsync(new Uri("http://only-the-proxy-knows.example/")));
        Assert.Contains("could not be resolved", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, proxy.Requests);
    }

    [Fact]
    public async Task AllowPrivateNetworkSkipsProxiedTargetVetting()
    {
        using var proxy = new RecordingProxy();
        using var client = new PocketCalculatorHttpClient(new CookieJar(), proxy.Url, allowPrivateNetwork: true)
        {
            ProxyTargetResolver = (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
        };

        var ok = await client.FetchAsync(new Uri("http://intranet.example/"));
        Assert.Equal(200, ok.Status);
    }

    // ---------------------------------------------------------------- M3

    [Fact]
    public async Task TricklingBodyHitsTheRequestTimeout()
    {
        using var listener = new TcpListenerScope();
        _ = Task.Run(async () =>
        {
            using var socket = await listener.Listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            await HttpFixture.ReadRequestAsync(stream, CancellationToken.None);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 1000\r\n\r\n"));
            for (var i = 0; i < 30 && !listener.Stopping; i++)
            {
                try
                {
                    await stream.WriteAsync("x"u8.ToArray());
                    await Task.Delay(500);
                }
                catch (IOException)
                {
                    return;
                }
            }
        });

        using var client = new PocketCalculatorHttpClient(new CookieJar(), null, true)
        {
            Timeout = TimeSpan.FromSeconds(1),
        };
        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<PocketCalculatorNetException>(
            () => client.FetchAsync(new Uri($"http://127.0.0.1:{listener.Port}/slow")));
        Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(8), $"took {started.Elapsed}");
    }

    // ---------------------------------------------------------------- L7

    [Fact]
    public async Task RedirectIntoTrackerIsBlocked()
    {
        using var fixture = HttpFixture.Serve([
            "HTTP/1.1 302 Found\r\nLocation: http://www.google-analytics.com/collect\r\n"
                + "Content-Length: 0\r\nConnection: close\r\n\r\n",
        ]);
        using var client = new PocketCalculatorHttpClient(new CookieJar(), null, true)
        {
            BlockTrackers = true,
            Timeout = TimeSpan.FromSeconds(5),
        };

        var response = await client.FetchAsync(fixture.Url);
        Assert.Equal(0, response.Status);
        Assert.Equal("www.google-analytics.com", response.Url.Host);
        Assert.Equal(fixture.Url, Assert.Single(response.RedirectedFrom));
    }

    [Fact]
    public async Task RequestClientRefusesTrackers()
    {
        // The op_fetch_url transport sends through RequestClient directly.
        using var client = new PocketCalculatorHttpClient(new CookieJar(), null, true) { BlockTrackers = true };
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://www.google-analytics.com/collect");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.RequestClient.SendAsync(request));
        Assert.Equal("net::ERR_BLOCKED_BY_CLIENT", error.Message);
    }

    // ---------------------------------------------------------------- L8

    private sealed class AddHeaderOnce : IRequestInterceptor
    {
        private int _calls;

        public Task<InterceptAction> InterceptAsync(RequestInfo request) =>
            Task.FromResult<InterceptAction>(Interlocked.Increment(ref _calls) == 1
                ? new InterceptAction.ModifyHeaders(new Dictionary<string, string> { ["X-Secret"] = "token" })
                : InterceptAction.Continue.Instance);
    }

    [Fact]
    public async Task ModifyHeadersAppliesToThatRequestOnly()
    {
        using var fixture = HttpFixture.Serve([
            HttpFixture.OkResponse(string.Empty, "one"),
            HttpFixture.OkResponse(string.Empty, "two"),
        ]);
        using var client = new PocketCalculatorHttpClient(new CookieJar(), null, true)
        {
            Interceptor = new AddHeaderOnce(),
        };

        await client.FetchAsync(fixture.Url);
        await client.FetchAsync(fixture.Url);

        var first = (await fixture.NextRequestAsync()).ToLowerInvariant();
        var second = (await fixture.NextRequestAsync()).ToLowerInvariant();
        Assert.Contains("x-secret: token\r\n", first, StringComparison.Ordinal);
        Assert.DoesNotContain("x-secret", second, StringComparison.Ordinal);
        Assert.Empty(client.ExtraHeaders);
    }

    // ---------------------------------------------------------------- fixtures

    private sealed class TcpListenerScope : IDisposable
    {
        public TcpListenerScope()
        {
            Listener = new TcpListener(IPAddress.Loopback, 0);
            Listener.Start();
            Port = ((IPEndPoint)Listener.LocalEndpoint).Port;
        }

        public TcpListener Listener { get; }

        public int Port { get; }

        public bool Stopping { get; private set; }

        public void Dispose()
        {
            Stopping = true;
            Listener.Stop();
        }
    }

    /// <summary>An HTTP proxy on loopback that answers every request itself and counts them.</summary>
    private sealed class RecordingProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        public RecordingProxy()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptAsync);
        }

        public string Url { get; }

        public int Requests => Volatile.Read(ref _requests);

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        await HttpFixture.ReadRequestAsync(stream, _stop.Token);
                        Interlocked.Increment(ref _requests);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\nproxied"), _stop.Token);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
