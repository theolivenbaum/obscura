using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using ApiBrowser = Obscura.Api.Browser;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura/tests/stealth_transport.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// The Rust file is <c>#![cfg(feature = "stealth")]</c>, so it compiles to
/// nothing unless the reference is built with that feature. What it asserts is
/// that opting into stealth at runtime swaps the whole transport, observed
/// through the User-Agent on the wire: the <c>wreq</c>/BoringSSL transport sends
/// its own <c>STEALTH_USER_AGENT</c> (Chrome 145 / Windows), while the ordinary
/// <c>reqwest</c> transport sends the selected profile's UA (Chrome 143 / Windows
/// at <c>OBSCURA_PROFILE=0</c>, which is also the default profile).
/// </para>
/// <para>
/// That transport is the port's one recorded deliberate gap: ClientHello
/// impersonation needs a native TLS stack and the port's native dependencies are
/// a closed set (V8, Skia, HarfBuzz). So the stealth-UA half is skipped with the
/// blocker named, and the half that does port is asserted: the ordinary
/// transport's UA. A reference binary built without the <c>stealth</c> feature
/// behaves identically, including under <c>--stealth</c>.
/// </para>
/// </remarks>
public sealed class StealthTransportTests
{
    /// <summary>
    /// <c>obscura_net::wreq_client::STEALTH_USER_AGENT</c>, mirrored by the port
    /// as <c>StealthIdentity.UserAgent</c>.
    /// </summary>
    private const string StealthUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36";

    /// <summary>The default profile's UA, which is also <c>OBSCURA_PROFILE=0</c>.</summary>
    private const string OrdinaryUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36";

    /// <summary>
    /// The ordinary transport's identity, the half of the Rust assertion that
    /// does not depend on the stealth transport existing.
    /// </summary>
    [Fact]
    public async Task Ordinary_transport_sends_the_selected_profiles_user_agent()
    {
        Assert.Equal(OrdinaryUserAgent, await NavigateUserAgentAsync(stealth: false));
    }

    /// <summary>
    /// Stealth is a runtime opt-in on top of a compile-time one. Without the
    /// compile-time half the reference sends the ordinary UA even with
    /// <c>--stealth</c>, and so does the port, whose stealth transport reports
    /// itself unavailable rather than silently downgrading.
    /// </summary>
    [Fact]
    public async Task Runtime_stealth_alone_does_not_change_the_transport_identity()
    {
        Assert.Equal(OrdinaryUserAgent, await NavigateUserAgentAsync(stealth: true));
        Assert.NotEqual(StealthUserAgent, OrdinaryUserAgent);
    }

    /// <summary>
    /// The compile-time half of the Rust test's subject: with the <c>stealth</c>
    /// feature the reference swaps in <c>wreq</c>/BoringSSL, which sends
    /// <see cref="StealthUserAgent"/> and a matching Chrome ClientHello.
    /// </summary>
    [Fact(Skip = "the stealth transport (wreq/BoringSSL) has no managed equivalent: TLS "
        + "ClientHello impersonation needs a native TLS stack and the port's native "
        + "dependencies are a closed set (V8, Skia, HarfBuzz). Recorded under \"Known "
        + "deviations\" in todo.md; Obscura.Net.UnavailableStealthHttpClient is the seam.")]
    public void Stealth_transport_sends_its_own_user_agent_and_client_hello()
    {
    }

    /// <summary>
    /// Serve one request, capture its header block, and return the User-Agent
    /// the engine sent, as the Rust <c>navigate_user_agent</c> helper does.
    /// </summary>
    private static async Task<string> NavigateUserAgentAsync(bool stealth)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var captured = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var serving = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                var stream = client.GetStream();
                var request = new StringBuilder();
                var buffer = new byte[1024];
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    request.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
                captured.TrySetResult(request.ToString());

                const string body = "<!doctype html><html><body>ok</body></html>";
                var response =
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\n"
                    + $"Connection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response)).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException
                or ObjectDisposedException or InvalidOperationException)
            {
                captured.TrySetException(error);
            }
        });

        try
        {
            var browser = ApiBrowser.Builder().Stealth(stealth).Build();
            using var page = await browser.NewPageAsync();
            await page.GotoAsync(url);

            var request = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            foreach (var line in request.Split("\r\n"))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0
                    && line[..colon].Equals("user-agent", StringComparison.OrdinalIgnoreCase))
                {
                    return line[(colon + 1)..].Trim();
                }
            }
            Assert.Fail("request should include a user-agent header");
            return string.Empty;
        }
        finally
        {
            listener.Stop();
            await serving.ConfigureAwait(false);
        }
    }
}
