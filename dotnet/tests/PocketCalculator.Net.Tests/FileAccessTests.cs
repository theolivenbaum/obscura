using System.Text;

namespace PocketCalculator.Net.Tests;

/// <summary>
/// SECURITY.md C4: the transport serves <c>file:</c> only to an operator-started
/// top-level navigation or to a request whose initiator is itself <c>file:</c>, and
/// refuses before touching the file system so a missing path reads like a forbidden one.
/// </summary>
public sealed class FileAccessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-file-access-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Uri WriteFile(string name, string contents)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, contents);
        return new Uri(path);
    }

    [Fact]
    public async Task OperatorNavigationReadsFileUrl()
    {
        var file = WriteFile("page.html", "<p>local</p>");
        using var client = new PocketCalculatorHttpClient();
        var response = await client.FetchAsync(file);
        Assert.Equal("<p>local</p>", Encoding.UTF8.GetString(response.Body));
    }

    public static TheoryData<string> WebInitiators => new()
    {
        "https://attacker.example/page",
        "data:text/html,x",
        "about:blank",
    };

    [Theory]
    [MemberData(nameof(WebInitiators))]
    public async Task NonFileInitiatorCannotReadFileUrl(string initiator)
    {
        var file = WriteFile("secret.mjs", "export const secret = 1;");
        var missing = new Uri(Path.Combine(_dir, "missing.mjs"));
        var source = new Uri(initiator);
        using var client = new PocketCalculatorHttpClient();

        ResourceRequest[] profiles =
        [
            ResourceRequest.ModuleScript(source, source),
            ResourceRequest.Subresource(ResourceType.Script, source),
            ResourceRequest.Subresource(ResourceType.Image, source),
            ResourceRequest.PageNavigation(source, userActivated: true),
            ResourceRequest.FrameNavigation(source),
        ];
        foreach (var profile in profiles)
        {
            var found = await Assert.ThrowsAsync<PocketCalculatorNetException>(
                () => client.FetchResourceWithCallbacksAsync(file, profile.Copy(), null));
            var absent = await Assert.ThrowsAsync<PocketCalculatorNetException>(
                () => client.FetchResourceWithCallbacksAsync(missing, profile.Copy(), null));
            Assert.EndsWith($"Not allowed to load local resource: {file}", found.Message, StringComparison.Ordinal);
            // The refusal comes before the file system, so a missing file reads the same.
            Assert.Equal(
                found.Message.Replace(file.ToString(), "URL", StringComparison.Ordinal),
                absent.Message.Replace(missing.ToString(), "URL", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task FileInitiatorReadsFileSubresource()
    {
        var file = WriteFile("dep.mjs", "export default 1;");
        var page = new Uri(Path.Combine(_dir, "page.html"));
        using var client = new PocketCalculatorHttpClient();
        var response = await client.FetchResourceWithCallbacksAsync(
            file, ResourceRequest.ModuleScript(page, page), null);
        Assert.Equal("export default 1;", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public async Task RedirectIntoFileIsRefused()
    {
        var file = WriteFile("target.txt", "secret");
        using var fixture = HttpFixture.Serve([
            $"HTTP/1.1 302 Found\r\nLocation: {file}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
        ]);
        using var client = new PocketCalculatorHttpClient(new CookieJar(), null, true);
        var error = await Assert.ThrowsAsync<PocketCalculatorNetException>(() => client.FetchAsync(fixture.Url));
        Assert.EndsWith($"Not allowed to load local resource: {file}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateUrlRefusesFileUnlessAllowed()
    {
        var file = new Uri("file:///etc/passwd");
        Assert.Throws<PocketCalculatorNetException>(() => SsrfGuard.ValidateUrl(file, false));
        Assert.Throws<PocketCalculatorNetException>(() => SsrfGuard.ValidateUrl(file, true));
        SsrfGuard.ValidateUrl(file, false, allowFile: true);
    }
}
