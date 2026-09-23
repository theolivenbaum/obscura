using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Mcp.Tests;

/// <summary>
/// SECURITY.md H3: every MCP navigation takes the <c>file:</c> gate
/// <c>browser_navigate</c> has, and a click on a <c>file:</c> link a page shows the
/// agent does not open the file.
/// </summary>
public sealed class FileNavigationGate : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-mcp-file-").FullName;

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

    private string SecretFile()
    {
        string path = Path.Combine(_dir, "id_rsa.html");
        File.WriteAllText(path, "<p>PRIVATE KEY</p>");
        return new Uri(path).AbsoluteUri;
    }

    [Fact]
    public async Task TabNewRefusesFileWithoutOpeningATab()
    {
        using var state = new BrowserState(null, null, false);
        int before = state.Tabs.Count;
        var error = await Assert.ThrowsAsync<ToolException>(() =>
            Tools.TabNewAsync(new JsonObject { ["url"] = SecretFile() }, state));
        Assert.Contains("file:// navigation is disabled", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, state.Tabs.Count);
    }

    [Fact]
    public async Task HistoryAndReloadRefuseFile()
    {
        string secret = SecretFile();
        using var state = new BrowserState(null, null, false);
        await Tools.NavigateAsync(new JsonObject { ["url"] = "data:text/html,<p>one</p>" }, state);
        var page = state.PageMut();

        page.History.Clear();
        page.History.Add(secret);
        page.History.Add("data:text/html,<p>one</p>");
        page.HistoryIndex = 1;
        await Assert.ThrowsAsync<ToolException>(() => Tools.BackAsync(state));
        Assert.Equal(1, page.HistoryIndex);

        page.History.Clear();
        page.History.Add("data:text/html,<p>one</p>");
        page.History.Add(secret);
        page.HistoryIndex = 0;
        await Assert.ThrowsAsync<ToolException>(() => Tools.ForwardAsync(state));
        Assert.Equal(0, page.HistoryIndex);

        Assert.DoesNotContain("PRIVATE KEY", Tools.Snapshot(null, state), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClickOnFileLinkDoesNotOpenTheFile()
    {
        string secret = SecretFile();
        using var state = new BrowserState(null, null, false);
        await Tools.NavigateAsync(
            new JsonObject { ["url"] = $"data:text/html,<a id=l href=\"{secret}\">open</a>" },
            state);

        await Tools.ClickAsync(new JsonObject { ["selector"] = "#l" }, state);

        string snapshot = Tools.Snapshot(null, state);
        Assert.DoesNotContain("PRIVATE KEY", snapshot, StringComparison.Ordinal);
        Assert.StartsWith("data:", state.PageMut().UrlString(), StringComparison.Ordinal);
    }
}
