using PocketCalculator.Js.Ops;
using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// SECURITY.md H2/H3: a document may not navigate from a non-<c>file:</c> URL into
/// <c>file:</c>, through a script, a link click or a queued navigation. Chromium drops
/// the navigation and the page stays. Host code (the operator) and a <c>file:</c>
/// document still reach <c>file:</c>.
/// </summary>
public sealed class FileNavigationGateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-file-nav-").FullName;

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

    private string WriteFile(string name, string html)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, html);
        return new Uri(path).AbsoluteUri;
    }

    [Fact]
    public async Task ScriptOnWebPageCannotNavigateToFile()
    {
        string secret = WriteFile("secret.html", "<p id=s>top secret</p>");
        Page page = PageFixtures.NewPage("file-nav-script");
        await page.NavigateAsync("data:text/html,<p>web</p>");
        string before = page.UrlString();

        page.Evaluate($"location.href = '{secret}'");
        PageNavigationOutcome outcome = await page.ProcessPendingNavigationOutcomeAsync();

        Assert.False(outcome.Navigated);
        Assert.Equal(before, page.UrlString());
        Assert.DoesNotContain("top secret", page.Evaluate("document.body.textContent")!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkClickOnWebPageCannotNavigateToFile()
    {
        string secret = WriteFile("key.html", "<p>private key</p>");
        Page page = PageFixtures.NewPage("file-nav-click");
        await page.NavigateAsync($"data:text/html,<a id=l href=\"{secret}\">x</a>");

        page.Evaluate("document.getElementById('l').click()");
        Assert.False(await page.ProcessPendingNavigationAsync());
        Assert.StartsWith("data:", page.UrlString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageInitiatedNavigationToFileIsRefusedExplicitly()
    {
        string secret = WriteFile("env.html", "<p>env</p>");
        Page page = PageFixtures.NewPage("file-nav-explicit");
        var initiator = new PendingNavigation(secret, "GET", string.Empty) { Initiator = "https://attacker.example/" };

        var error = await Assert.ThrowsAsync<PageException>(
            () => page.NavigateWithWaitPostAsync(secret, WaitUntil.Load, "GET", string.Empty, initiator));
        Assert.Contains("Not allowed to load local resource", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileDocumentAndOperatorStillReachFile()
    {
        string second = WriteFile("second.html", "<p>second</p>");
        string first = WriteFile("first.html", $"<a id=l href=\"{second}\">next</a>");
        Page page = PageFixtures.NewPage("file-nav-local");

        await page.NavigateAsync(first);
        Assert.Equal(first, page.UrlString());

        page.Evaluate("document.getElementById('l').click()");
        Assert.True(await page.ProcessPendingNavigationAsync());
        Assert.Equal(second, page.UrlString());
    }
}
