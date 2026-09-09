using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Obscura.Net.Tests;

/// <summary>
/// A configured proxy on a loopback address must not be refused by the SSRF guard.
/// </summary>
/// <remarks>
/// <para>
/// The reference installs the guard as a reqwest <c>dns_resolver</c>, so it runs for
/// a host that needs resolving and never sees an IP-literal endpoint - the socket is
/// dialled without a lookup. The port installs it as a
/// <c>SocketsHttpHandler.ConnectCallback</c>, which fires for every connection, so it
/// was inspecting endpoints the reference never inspects. The visible effect: with
/// <c>HTTPS_PROXY=http://127.0.0.1:PORT</c> in the environment - how this sandbox and
/// most corporate networks are set up - every fetch failed with
/// "SSRF blocked: '127.0.0.1' resolves to forbidden address 127.0.0.1" while the
/// reference fetched normally. The only workaround was
/// <c>--allow-private-network</c>, which turns the whole guard off, so the bug pushed
/// users towards disabling exactly the protection it was meant to provide.
/// </para>
/// <para>
/// The guard against a private *target* is unaffected, and
/// <see cref="SsrfTests"/> plus <c>ValidateUrl</c> cover it: the request URL's host
/// is checked on entry and on every redirect hop, which is where the reference
/// rejects it too.
/// </para>
/// </remarks>
public sealed class ProxyEndpointNotSsrfBlockedTests
{
    /// <summary>
    /// A loopback HTTP proxy: reads the absolute-form request line an HTTP proxy
    /// receives and answers it directly, which is all this test needs.
    /// </summary>
    private sealed class LoopbackProxy : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        public LoopbackProxy()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        public string Url => $"http://127.0.0.1:{Port}";

        public string? LastRequestLine { get; private set; }

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
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    var request = Encoding.ASCII.GetString(buffer, 0, read);
                    LastRequestLine = request.Split("\r\n")[0];
                    const string body = "proxied";
                    var response =
                        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n"
                        + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                    await stream.FlushAsync(_stop.Token);
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

    [Fact]
    public async Task AFetchThroughALoopbackProxyIsNotBlocked()
    {
        using var proxy = new LoopbackProxy();
        // allowPrivateNetwork stays false: that is the whole point.
        var client = new ObscuraHttpClient(new CookieJar(), proxy.Url, allowPrivateNetwork: false);

        // A public-looking target, so nothing about the request URL is private. It
        // never resolves here - the proxy answers on its behalf.
        Response response = await client.FetchAsync(new Uri("http://example.com/"));

        Assert.Equal(200, response.Status);
        Assert.Equal("proxied", Encoding.UTF8.GetString(response.Body));
        // Absolute-form request line is what proves the hop went through the proxy.
        Assert.StartsWith("GET http://example.com/", proxy.LastRequestLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APrivateTargetIsStillRefusedWhileProxied()
    {
        using var proxy = new LoopbackProxy();
        var client = new ObscuraHttpClient(new CookieJar(), proxy.Url, allowPrivateNetwork: false);

        // ValidateUrl runs before any connection, so configuring a proxy does not
        // open a path to a private address.
        var error = await Assert.ThrowsAsync<ObscuraNetException>(
            () => client.FetchAsync(new Uri("http://169.254.169.254/latest/meta-data/")));

        Assert.Contains("169.254.169.254", error.Message, StringComparison.Ordinal);
        Assert.Contains("is not allowed", error.Message, StringComparison.Ordinal);
    }
}
