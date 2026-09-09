using System.Net;
using System.Net.Sockets;
using System.Text;
using Obscura.Api;
using Obscura.Js.Ops;
using Obscura.Net;
using Xunit;
using ApiBrowser = Obscura.Api.Browser;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura/tests/interception.rs</c>: the request/response
/// interception API on the embeddable <c>Page</c> (issue #306), including the
/// interception channel and the passive per-page callbacks.
/// </summary>
public sealed class InterceptionTests
{
    /// <summary>
    /// Minimal HTTP/1.1 server: <c>/</c> returns HTML that fires
    /// <c>fetch('/api')</c>; <c>/api</c> returns JSON; <c>/modified</c> returns
    /// a marker body. Enough to exercise JS fetch() interception plus callbacks.
    /// </summary>
    private sealed class EchoServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();

        public EchoServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Base = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptLoopAsync);
        }

        public string Base { get; }

        private async Task AcceptLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException
                    or ObjectDisposedException or SocketException)
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private static async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[2048];
                    var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    var request = Encoding.UTF8.GetString(buffer, 0, count);
                    var parts = request.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var path = parts.Length > 1 ? parts[1] : "/";
                    var (contentType, body) =
                        path.StartsWith("/api", StringComparison.Ordinal)
                            ? ("application/json", "{\"hello\":\"world\"}")
                            : path.StartsWith("/modified", StringComparison.Ordinal)
                                ? ("text/plain", "REWRITTEN")
                                : ("text/html", "<script>fetch('/api');</script>");
                    var response =
                        $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\n" +
                        $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                        "Access-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n" + body;
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(response)).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or SocketException
                    or ObjectDisposedException)
                {
                    // A client that hangs up mid-request is not a test failure.
                }
            }
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            _listener.Stop();
            _shutdown.Dispose();
        }
    }

    /// <summary>Drain the interception channel, resolving every request.</summary>
    private static void ResolveAll(
        System.Threading.Channels.ChannelReader<InterceptedRequest> reader,
        Func<InterceptedRequest, string?> rewriteUrl)
    {
        _ = Task.Run(async () =>
        {
            await foreach (var request in reader.ReadAllAsync().ConfigureAwait(false))
            {
                request.Resolver.TrySetResult(
                    new InterceptResolution.Continue(rewriteUrl(request), null, null, null));
            }
        });
    }

    private static async Task PumpUntilAsync(Page page, Func<bool> done, int rounds, ulong sliceMs)
    {
        for (var i = 0; i < rounds && !done(); i++)
        {
            await page.SettleAsync(sliceMs);
        }
    }

    [Fact]
    public async Task Page_intercepts_and_observes_js_fetch()
    {
        using var server = new EchoServer();
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();

        // Passive request counter (fires for navigation and the JS fetch).
        var requestCount = 0;
        page.OnRequest(_ => Interlocked.Increment(ref requestCount));

        // Passive response capture for the /api body.
        var captured = string.Empty;
        page.OnResponse((info, response) =>
        {
            if (info.ResourceType == ResourceType.Fetch)
            {
                Volatile.Write(ref captured, Encoding.UTF8.GetString(response.Body));
            }
        });

        ResolveAll(page.EnableInterception(), _ => null);

        await page.GotoAsync(server.Base);
        await PumpUntilAsync(
            page, () => Volatile.Read(ref captured).Contains("hello", StringComparison.Ordinal), 20, 500);

        Assert.True(
            Volatile.Read(ref requestCount) >= 1,
            "OnRequest never fired for navigation or fetch");
        Assert.Contains("hello", Volatile.Read(ref captured), StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #408 follow-up: callbacks are page-scoped. One registered on page A
    /// must not fire for requests made by page B in the same browser context.
    /// </summary>
    [Fact]
    public async Task Callbacks_do_not_bleed_across_pages()
    {
        using var server = new EchoServer();
        var browser = ApiBrowser.New();
        var pageA = await browser.NewPageAsync();
        var pageB = await browser.NewPageAsync();

        var hitsA = 0;
        pageA.OnRequest(_ => Interlocked.Increment(ref hitsA));

        // Page B navigates; page A's callback must stay silent.
        await pageB.GotoAsync(server.Base);
        await pageB.SettleAsync(500);
        Assert.Equal(0, Volatile.Read(ref hitsA));

        // Page A navigates; its own callback fires.
        await pageA.GotoAsync(server.Base);
        Assert.True(Volatile.Read(ref hitsA) >= 1, "page A's OnRequest did not fire for its own navigation");

        // Detaching page A's callbacks must not leave them firing for page B.
        // Rust proves this by dropping the page; the managed equivalent is
        // disposing it, which releases the same per-page registry.
        var beforeDispose = Volatile.Read(ref hitsA);
        pageA.Dispose();
        await pageB.GotoAsync(server.Base);
        await pageB.SettleAsync(500);
        Assert.Equal(beforeDispose, Volatile.Read(ref hitsA));
    }

    [Fact]
    public async Task Page_rewrites_request_url_via_interception()
    {
        using var server = new EchoServer();
        var modified = $"{server.Base}/modified";
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();

        var captured = string.Empty;
        page.OnResponse((info, response) =>
        {
            if (info.ResourceType == ResourceType.Fetch)
            {
                Volatile.Write(ref captured, Encoding.UTF8.GetString(response.Body));
            }
        });

        ResolveAll(
            page.EnableInterception(),
            request => request.Url.Contains("/api", StringComparison.Ordinal) ? modified : null);

        await page.GotoAsync(server.Base);
        await PumpUntilAsync(
            page, () => Volatile.Read(ref captured).Contains("REWRITTEN", StringComparison.Ordinal), 20, 500);

        Assert.Contains("REWRITTEN", Volatile.Read(ref captured), StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #408: a callback registered with OnResponse must be detachable via
    /// the returned id, so a crawler can stop capturing after a phase.
    /// </summary>
    [Fact]
    public async Task On_response_callback_can_be_detached()
    {
        using var server = new EchoServer();
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();

        var hits = 0;
        var id = page.OnResponse((info, _) =>
        {
            if (info.ResourceType == ResourceType.Fetch)
            {
                Interlocked.Increment(ref hits);
            }
        });

        await page.GotoAsync(server.Base);
        await PumpUntilAsync(page, () => Volatile.Read(ref hits) >= 1, 20, 200);
        var afterFirst = Volatile.Read(ref hits);
        Assert.True(afterFirst >= 1, "OnResponse should fire while attached");

        Assert.True(page.OffResponse(id), "OffResponse must remove the callback");
        Assert.False(page.OffResponse(id), "removing an already-removed id returns false");

        await page.GotoAsync(server.Base);
        await PumpUntilAsync(page, () => false, 10, 200);
        Assert.Equal(afterFirst, Volatile.Read(ref hits));
    }
}
