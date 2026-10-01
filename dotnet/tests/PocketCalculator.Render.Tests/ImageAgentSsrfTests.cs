using System.Net;
using System.Net.Sockets;
using Xunit;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// todo.md "ImageAgent has no SSRF check": a standalone render cache (no page, no
/// transport) fetched images from loopback, private and metadata addresses. Every image
/// fetch path now goes through the SSRF guard.
/// </summary>
public class ImageAgentSsrfTests
{
    [Fact]
    public void StandaloneImageFetchRefusesLoopback()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Null(ImageAgent.Get($"http://127.0.0.1:{port}/secret.png"));
        Assert.Null(new HttpResourceLoader().Load($"http://[::ffff:127.0.0.1]:{port}/secret.png"));
        Assert.Null(ImageAgent.Get($"http://localhost:{port}/secret.png"));

        // Nothing ever connected: the guard refused before dialling.
        Assert.False(listener.Pending());
    }

    [Fact]
    public void StandaloneImageFetchRefusesMetadataAndFileUrls()
    {
        Assert.Null(ImageAgent.Get("http://169.254.169.254/latest/meta-data/"));
        Assert.Null(ImageAgent.Get("file:///etc/passwd"));
    }

    [Fact]
    public void DefaultRenderCacheUsesTheGuardedLoader()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        RenderResourceCache cache = new();
        Assert.Null(PaintResources.FetchBytes($"http://127.0.0.1:{port}/a.png", null, cache));
        Assert.False(listener.Pending());
    }
}
