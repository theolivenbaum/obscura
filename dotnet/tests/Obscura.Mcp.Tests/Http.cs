using System.IO.Pipelines;
using System.Text;
using Xunit;

namespace Obscura.Mcp.Tests;

/// <summary>
/// The xUnit port of <c>mod mcp_hardening_tests</c> in
/// <c>crates/obscura-mcp/src/http.rs</c>.
/// </summary>
public sealed class HttpHardeningTests
{
    [Fact]
    public void NoAllowlistIsPermissive()
    {
        Assert.True(Http.OriginAllowed("https://evil.example", null));
        Assert.True(Http.OriginAllowed(null, null));
    }

    [Fact]
    public void AllowlistMatchesCaseInsensitivelyAndRejectsOthers()
    {
        const string list = "http://localhost:3000, https://app.example.com";
        Assert.True(Http.OriginAllowed("http://localhost:3000", list));
        Assert.True(Http.OriginAllowed("https://APP.example.com", list));
        Assert.False(Http.OriginAllowed("https://evil.example", list));
        // A native client (no Origin header) is always allowed.
        Assert.True(Http.OriginAllowed(null, list));
    }

    [Fact]
    public void BodyCapIsSane()
    {
        // Far above a real JSON-RPC tool call, far below an OOM-inducing value.
        Assert.True(Http.MaxBodyBytes >= 1 << 20);
        Assert.True(Http.MaxBodyBytes <= 64 << 20);
    }

    [Fact]
    public void RequestReadTimeoutIsGenerousButBounded()
    {
        Assert.True(Http.RequestReadTimeout >= TimeSpan.FromSeconds(10));
        Assert.True(Http.RequestReadTimeout <= TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// <c>tokio::io::duplex(64)</c>: a client half a test writes into and a server
    /// half the transport reads from, with no socket underneath.
    /// </summary>
    private static (Stream Client, Http.LineReader Server) Duplex()
    {
        var pipe = new Pipe();
        return (pipe.Writer.AsStream(), new Http.LineReader(pipe.Reader.AsStream()));
    }

    [Fact]
    public async Task StalledRequestLineHitsDeadline()
    {
        var (_, server) = Duplex();
        var error = await Assert.ThrowsAsync<Http.HttpTransportException>(() =>
            Http.ReadRequestWithTimeoutAsync(server, null, TimeSpan.FromMilliseconds(20)));
        Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StalledRequestBodyHitsDeadline()
    {
        var (client, server) = Duplex();
        var request = Encoding.UTF8.GetBytes(
            "POST /mcp HTTP/1.1\r\n"
            + "Content-Length: 8\r\n"
            + "\r\n"
            + "{}");
        await client.WriteAsync(request);
        await client.FlushAsync();

        var error = await Assert.ThrowsAsync<Http.HttpTransportException>(() =>
            Http.ReadRequestWithTimeoutAsync(server, null, TimeSpan.FromMilliseconds(20)));
        Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
    }
}
