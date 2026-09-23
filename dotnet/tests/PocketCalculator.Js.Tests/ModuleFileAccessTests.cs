using PocketCalculator.Dom;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// SECURITY.md C4: a web page must not import local files as ES modules. Chromium lets
/// a module graph reach <c>file:</c> only from a <c>file:</c> document, and a refusal
/// must not tell a missing path from a forbidden one.
/// </summary>
public sealed class ModuleFileAccessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-module-file-").FullName;

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

    private string WriteModule(string name, string source)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, source);
        return new Uri(path).AbsoluteUri;
    }

    private static PocketCalculatorJsRuntime Page(string url)
    {
        var rt = PocketCalculatorJsRuntime.WithBaseUrl(url);
        rt.SetHttpClient(new PocketCalculatorHttpClient(new CookieJar(), null, allowPrivateNetwork: false));
        rt.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        rt.SetUrl(url);
        rt.RunPageInit();
        return rt;
    }

    private static async Task<string> ImportAsync(PocketCalculatorJsRuntime rt, string url)
    {
        rt.Evaluate(
            "(function() { globalThis.__r = null; import(" + System.Text.Json.JsonSerializer.Serialize(url) + ")"
            + ".then(m => { globalThis.__r = 'ok:' + m.secret; },"
            + " e => { globalThis.__r = 'err:' + (e && e.name) + ':' + (e && e.message); });"
            + " return 1; })()");
        for (int i = 0; i < 50; i++)
        {
            await rt.RunEventLoopBoundedAsync(100);
            if (rt.Evaluate("globalThis.__r")?.GetValue<string>() is { } done)
            {
                return done;
            }
        }

        return "pending";
    }

    [Theory]
    [InlineData("https://attacker.example/page")]
    [InlineData("data:text/html,<p>x</p>")]
    [InlineData("about:blank")]
    public async Task NonFileDocumentCannotImportFileModule(string pageUrl)
    {
        string secret = WriteModule("secret.mjs", "export const secret = 's3cret';");
        string missing = new Uri(Path.Combine(_dir, "missing.mjs")).AbsoluteUri;
        using var rt = Page(pageUrl);

        string found = await ImportAsync(rt, secret);
        string absent = await ImportAsync(rt, missing);

        Assert.StartsWith("err:", found, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", found, StringComparison.Ordinal);
        // Existence must not leak: the two rejections differ only in the URL.
        Assert.Equal(found.Replace(secret, "URL", StringComparison.Ordinal), absent.Replace(missing, "URL", StringComparison.Ordinal));
        Assert.Contains("Failed to fetch dynamically imported module: " + secret, found, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileDocumentCanStillImportFileModule()
    {
        string secret = WriteModule("local.mjs", "export const secret = 'mine';");
        string page = new Uri(Path.Combine(_dir, "page.html")).AbsoluteUri;
        using var rt = Page(page);

        Assert.Equal("ok:mine", await ImportAsync(rt, secret));
    }

    [Fact]
    public async Task FileDocumentMissingModuleGivesNoIoDetail()
    {
        string page = new Uri(Path.Combine(_dir, "page.html")).AbsoluteUri;
        string missing = new Uri(Path.Combine(_dir, "nope", "missing.mjs")).AbsoluteUri;
        using var rt = Page(page);

        string result = await ImportAsync(rt, missing);
        Assert.StartsWith("err:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not find", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebModuleImportedStaticallyCannotReachFile()
    {
        // A static import from an inline module on a web page takes the same gate.
        string secret = WriteModule("static.mjs", "export const secret = 'static';");
        using var rt = Page("https://attacker.example/page");
        string src = "import { secret } from " + System.Text.Json.JsonSerializer.Serialize(secret) + ";"
            + " globalThis.__static = secret;";
        await Assert.ThrowsAnyAsync<Exception>(
            () => rt.LoadInlineModuleAsync(src, "https://attacker.example/page", 2_000));
        Assert.Equal("undefined", rt.Evaluate("String(globalThis.__static)")!.GetValue<string>());
    }
}
