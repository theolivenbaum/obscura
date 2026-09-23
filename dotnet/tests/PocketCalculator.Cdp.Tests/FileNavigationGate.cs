using System.Text.Json.Nodes;
using System.Threading.Channels;
using PocketCalculator.Js.Ops;
using Xunit;

using PageDomain = PocketCalculator.Cdp.Domains.Page;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// SECURITY.md H1/H2: every CDP navigation takes one scheme gate. A sessioned
/// <c>Page.navigate</c> (what Puppeteer and Playwright send over flattened sessions)
/// used to skip <c>--allow-file-access</c>, and a navigation a web page queued was
/// followed into <c>file:</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class FileNavigationGate : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-cdp-file-").FullName;

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
        string path = Path.Combine(_dir, "secret.html");
        File.WriteAllText(path, "<p>top secret</p>");
        return new Uri(path).AbsoluteUri;
    }

    /// <summary>Drive the sessioned navigate path the processor routes every session through.</summary>
    private static async Task<JsonNode?> SessionNavigateAsync(
        CdpContext ctx,
        string session,
        JsonObject parameters,
        bool sendCommandResponse = true,
        bool hostInitiated = false)
    {
        var reply = Channel.CreateUnbounded<string>();
        var rx = Channel.CreateUnbounded<ServerMessage>();
        var interceptRx = Channel.CreateUnbounded<InterceptedRequest>();
        string text = new JsonObject
        {
            ["id"] = 7,
            ["method"] = "Page.navigate",
            ["params"] = parameters,
            ["sessionId"] = session,
        }.ToJsonString();
        await CdpServer.ProcessWithInterceptionAsync(
            text, ctx, reply.Writer, rx.Reader, interceptRx.Reader, [], new Queue<ServerMessage>(), sendCommandResponse,
            hostInitiated);
        reply.Writer.Complete();
        await foreach (string message in reply.Reader.ReadAllAsync())
        {
            JsonNode? node = JsonNode.Parse(message);
            if (node?["id"]?.GetValue<ulong>() == 7)
            {
                return node;
            }
        }

        return null;
    }

    [Fact]
    public async Task SessionedNavigateToFileIsRefusedByDefault()
    {
        string secret = SecretFile();
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();

        JsonNode? response = await SessionNavigateAsync(ctx, session, new JsonObject { ["url"] = secret });

        Assert.NotNull(response);
        Assert.Equal(PageDomain.FileNavigationDisabled, response["error"]?["message"]?.GetValue<string>());
        Assert.NotEqual(secret, ctx.GetSessionPage(session)!.UrlString());
    }

    [Fact]
    public async Task SessionedNavigateToFileWorksWhenAllowed()
    {
        string secret = SecretFile();
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        ctx.GetSessionPageMut(session)!.Context.AllowFileAccess = true;

        JsonNode? response = await SessionNavigateAsync(ctx, session, new JsonObject { ["url"] = secret });

        Assert.Null(response?["error"]);
        Assert.Equal(secret, ctx.GetSessionPage(session)!.UrlString());
    }

    [Fact]
    public async Task PageQueuedNavigationFromWebToFileIsNotFollowed()
    {
        // The autonomous pump forwards a page-queued navigation with its initiator.
        // Even with file access on, a web document cannot take the page to file:.
        string secret = SecretFile();
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        ctx.GetSessionPageMut(session)!.Context.AllowFileAccess = true;
        var queued = new PendingNavigation(secret, "GET", string.Empty) { Initiator = "https://attacker.example/" };

        // The pump forwards as the host (Server.Processor), so the initiator survives
        // the stripping of client-supplied internal params (I6).
        await SessionNavigateAsync(
            ctx, session, PageDomain.JsNavigationParams(queued), sendCommandResponse: false, hostInitiated: true);

        Assert.NotEqual(secret, ctx.GetSessionPage(session)!.UrlString());
    }

    [Fact]
    public async Task ClientCannotForgeAnInitiatorToGetPastTheGate()
    {
        // A client-sent __initiator is stripped (I6), so it is judged as a client
        // navigation: refused without file access.
        string secret = SecretFile();
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        var queued = new PendingNavigation(secret, "GET", string.Empty) { Initiator = "file:///tmp/x.html" };

        JsonNode? response = await SessionNavigateAsync(ctx, session, PageDomain.JsNavigationParams(queued));

        Assert.Equal(PageDomain.FileNavigationDisabled, response?["error"]?["message"]?.GetValue<string>());
        Assert.NotEqual(secret, ctx.GetSessionPage(session)!.UrlString());
    }

    [Fact]
    public async Task SessionlessQueuedNavigationFromWebToFileIsRefused()
    {
        string secret = SecretFile();
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        ctx.GetSessionPageMut(session)!.Context.AllowFileAccess = true;
        var queued = new PendingNavigation(secret, "GET", string.Empty) { Initiator = "https://attacker.example/" };

        DomainResult result = await PageDomain.HandleAsync("navigate", PageDomain.JsNavigationParams(queued), ctx, session);

        Assert.Contains("Not allowed to load local resource", CdpDomainFixtures.ErrorOf(result), StringComparison.Ordinal);
        Assert.NotEqual(secret, ctx.GetSessionPage(session)!.UrlString());
    }

    [Fact]
    public async Task HistoryNavigationToFileIsGated()
    {
        string secret = SecretFile();
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        var page = ctx.GetSessionPageMut(session)!;
        page.History.Clear();
        page.History.Add("about:blank");
        page.History.Add(secret);

        DomainResult result = await PageDomain.HandleAsync(
            "navigateToHistoryEntry", new JsonObject { ["entryId"] = 1 }, ctx, session);

        Assert.Equal(PageDomain.FileNavigationDisabled, CdpDomainFixtures.ErrorOf(result));
        Assert.NotEqual(secret, ctx.GetSessionPage(session)!.UrlString());
    }
}
