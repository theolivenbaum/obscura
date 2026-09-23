using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Origin decisions the host makes rather than the shim (SECURITY.md C1, C2, C3, H4, L9):
/// a page that replaces <c>URL</c>, <c>JSON.parse</c> or an iframe expando must not
/// change which origin a request, a message or a frame belongs to.
/// </summary>
public sealed partial class RuntimeTests
{
    /// <summary>
    /// C1: a page replacing <c>window.URL</c> so its document URL reports the target's
    /// origin must still fetch as its own origin: no cookies, and the CORS check fails.
    /// </summary>
    [Fact]
    public async Task FetchOriginIsTheDocumentsNotThePageComputedOne()
    {
        string? victimOrigin = null;
        using var server = new RawHttpServer(request =>
        {
            // Only the victim's own origin is allowed to read, with credentials.
            return "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n"
                + $"Access-Control-Allow-Origin: {victimOrigin}\r\n"
                + "Access-Control-Allow-Credentials: true\r\n"
                + "Content-Length: 6\r\nConnection: close\r\n\r\nsecret";
        });
        victimOrigin = server.Origin;
        var jar = new CookieJar();
        jar.SetCookie("sid=victim-session; Path=/", new Uri(server.Origin + "/"));
        using var fixture = RuntimeFixture.Blank();
        var runtime = fixture.Runtime;
        runtime.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        runtime.SetUrl("http://attacker.example/page");
        runtime.SetHttpClient(new PocketCalculatorHttpClient(jar, null, allowPrivateNetwork: true));
        runtime.RunPageInit();

        var result = await runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                const Real = URL;
                globalThis.URL = class extends Real {
                    get origin() { return "{{server.Origin}}"; }
                };
                try {
                    const r = await fetch("{{server.Origin}}/account");
                    return "read:" + await r.text();
                } catch (e) {
                    return "blocked";
                }
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        Assert.Equal("blocked", result.Value!.GetValue<string>());
        var request = Assert.Single(server.Requests);
        Assert.DoesNotContain("victim-session", request, StringComparison.Ordinal);
        Assert.Contains("Origin: http://attacker.example\r\n", request, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// M3: the fetch deadline covers the body as well as the headers. A server that sends
    /// its headers at once and then one byte every 200 ms used to hold the op open for as
    /// long as it liked.
    /// </summary>
    [Fact]
    public async Task FetchTimeoutCoversATricklingBody()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        new Thread(() =>
        {
            using (listener)
            {
                try
                {
                    using var socket = listener.AcceptSocket();
                    using var stream = new NetworkStream(socket, ownsSocket: false);
                    var header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 200\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length);
                    for (var i = 0; i < 200 && !stop.IsCancellationRequested; i++)
                    {
                        stream.Write("a"u8);
                        Thread.Sleep(200);
                    }
                }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
                {
                }
            }
        })
        {
            IsBackground = true,
            Name = "trickle-body",
        }.Start();

        var state = new PocketCalculatorState
        {
            Url = $"http://127.0.0.1:{port}/",
            HttpClient = new PocketCalculatorHttpClient(new CookieJar(), null, allowPrivateNetwork: true),
        };
        FetchOps.FetchTimeoutOverride.Value = TimeSpan.FromSeconds(1);
        var clock = Stopwatch.StartNew();
        try
        {
            var error = await Assert.ThrowsAsync<OpException>(() => FetchOps.OpFetchUrlAsync(
                state, $"http://127.0.0.1:{port}/slow", "GET", "{}", [], "", "cors", "same-origin"));
            Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            FetchOps.FetchTimeoutOverride.Value = null;
            stop.Cancel();
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    private const string SpoofBankOrigin =
        "const RealURL = URL; globalThis.URL = class extends RealURL {"
        + " get origin() { return 'https://bank.example'; } };";

    /// <summary>
    /// H4: a frame that replaces <c>URL</c> still posts as its own origin. The host fills
    /// <c>event.origin</c> in from the sending realm.
    /// </summary>
    [Fact]
    public void PostMessageOriginIsTheSendersNotAPageComputedOne()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        using var frame = FrameRealm.Create(parent, 1, 0, "https://evil.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);

        frame.ExecuteScript(SpoofBankOrigin + "parent.postMessage('hi', '*');");

        var queued = Assert.Single(parent.TakePendingFrameMessages());
        Assert.Equal("https://evil.example", queued.Origin);
        Assert.Equal(1u, queued.SourceFrameId);
    }

    /// <summary>
    /// H4, the receiving side: a frame that replaces <c>URL</c> to claim an origin does not
    /// receive a message whose targetOrigin names that origin.
    /// </summary>
    [Fact]
    public void PostMessageTargetOriginIsCheckedAgainstTheReceiversRealOrigin()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        using var frame = FrameRealm.Create(
            page.Runtime, 1, 0, "https://evil.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);
        frame.ExecuteScript(
            SpoofBankOrigin
            + "globalThis.got = []; addEventListener('message', (e) => globalThis.got.push(e.data));");

        frame.DeliverMessage("{\"v\":\"secret\"}", "https://parent.example", 0, "https://bank.example");
        frame.DeliverMessage("{\"v\":\"public\"}", "https://parent.example", 0, "*");

        Assert.Equal("[\"public\"]", frame.Evaluate("globalThis.got")!.ToJsonString());
        // And the shim's own check agrees with the host when called directly.
        frame.ExecuteHostScript(
            "__obscura_host.deliverMessage('{\"v\":\"direct\"}', 'https://parent.example', 0, 'https://bank.example');");
        Assert.Equal("[\"public\"]", frame.Evaluate("globalThis.got")!.ToJsonString());
    }
}
