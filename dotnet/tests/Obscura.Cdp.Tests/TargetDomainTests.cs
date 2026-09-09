using System.Text.Json.Nodes;

using Obscura.Cdp.Domains;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>tests</c> module in
/// <c>crates/obscura-cdp/src/domains/target.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class TargetDomainTests
{
    private static Task<DomainResult> HandleAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId = null) =>
        Target.HandleAsync(method, parameters, ctx, sessionId);

    [Fact]
    public async Task BrowserContextsAreRealAndDoNotClearDefaultCookies()
    {
        var ctx = CdpContext.New();
        ctx.DefaultContext.CookieJar.SetCookie("sid=default", new Uri("https://example.com"));

        JsonNode created = CdpDomainFixtures.Unwrap(
            await HandleAsync("createBrowserContext", new JsonObject(), ctx));
        string contextId = created["browserContextId"]!.GetValue<string>();
        Assert.NotEqual("default", contextId);
        Assert.Empty(ctx.BrowserContextById(contextId)!.CookieJar.GetAllCookies());
        Assert.Single(ctx.DefaultContext.CookieJar.GetAllCookies());

        JsonNode listed = CdpDomainFixtures.Unwrap(
            await HandleAsync("getBrowserContexts", new JsonObject(), ctx));
        CdpDomainFixtures.AssertJson($"[\"{contextId}\"]", listed["browserContextIds"]);
    }

    [Fact]
    public async Task DisposingContextRemovesOnlyItsPages()
    {
        var ctx = CdpContext.New();
        string contextId = ctx.CreateBrowserContext();
        Assert.True(ctx.CreatePageInContext(contextId, out string? isolatedPage, out _));
        string defaultPage = ctx.CreatePage();

        CdpDomainFixtures.Unwrap(await HandleAsync(
            "disposeBrowserContext",
            new JsonObject { ["browserContextId"] = contextId },
            ctx));

        Assert.Null(ctx.GetPage(isolatedPage!));
        Assert.NotNull(ctx.GetPage(defaultPage));
        Assert.Empty(ctx.BrowserContexts);
    }

    [Fact]
    public async Task AttachToBrowserTargetReturnsSessionId()
    {
        var ctx = CdpContext.New();
        JsonNode result = CdpDomainFixtures.Unwrap(
            await HandleAsync("attachToBrowserTarget", new JsonObject(), ctx));

        Assert.Equal("browser-session", result["sessionId"]!.GetValue<string>());
        Assert.Equal("browser", ctx.Sessions["browser-session"]);

        // Playwright/Puppeteer expect a Target.attachedToTarget event before they finish
        // wiring up the session - without it the connect promise hangs.
        CdpEvent attached = Assert.Single(
            ctx.PendingEvents.Where(e => e.Method == "Target.attachedToTarget"));
        Assert.Equal("browser-session", attached.Params!["sessionId"]!.GetValue<string>());
        Assert.Equal("browser", attached.Params!["targetInfo"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExplicitPageAttachmentIsUniqueAndScopedToItsParentSession()
    {
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        string managedSession = $"{pageId}-session";
        ctx.Sessions[managedSession] = pageId;
        const string parentSession = "browser-session";

        JsonNode first = CdpDomainFixtures.Unwrap(await HandleAsync(
            "attachToTarget",
            new JsonObject { ["targetId"] = pageId, ["flatten"] = true },
            ctx,
            parentSession));
        string firstSession = first["sessionId"]!.GetValue<string>();

        Assert.NotEqual(managedSession, firstSession);
        Assert.Equal(pageId, ctx.Sessions[firstSession]);
        CdpEvent firstEvent = ctx.PendingEvents[^1];
        Assert.Equal("Target.attachedToTarget", firstEvent.Method);
        Assert.Equal(parentSession, firstEvent.SessionId);
        Assert.Equal(firstSession, firstEvent.Params!["sessionId"]!.GetValue<string>());

        JsonNode second = CdpDomainFixtures.Unwrap(await HandleAsync(
            "attachToTarget",
            new JsonObject { ["targetId"] = pageId, ["flatten"] = true },
            ctx,
            parentSession));
        Assert.NotEqual(firstSession, second["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task DetachingExplicitSessionRemovesItsPageRoute()
    {
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        const string parentSession = "browser-session";
        JsonNode attached = CdpDomainFixtures.Unwrap(await HandleAsync(
            "attachToTarget", new JsonObject { ["targetId"] = pageId }, ctx, parentSession));
        string sessionId = attached["sessionId"]!.GetValue<string>();

        CdpDomainFixtures.Unwrap(await HandleAsync(
            "detachFromTarget", new JsonObject { ["sessionId"] = sessionId }, ctx, parentSession));
        Assert.False(ctx.Sessions.ContainsKey(sessionId));
    }

    [Fact]
    public async Task ClosingTargetDetachesEveryActualPageSession()
    {
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        const string parentSession = "browser-session";
        string first = CdpDomainFixtures.Unwrap(await HandleAsync(
            "attachToTarget",
            new JsonObject { ["targetId"] = pageId, ["flatten"] = true },
            ctx,
            parentSession))["sessionId"]!.GetValue<string>();
        string second = CdpDomainFixtures.Unwrap(await HandleAsync(
            "attachToTarget",
            new JsonObject { ["targetId"] = pageId, ["flatten"] = true },
            ctx,
            parentSession))["sessionId"]!.GetValue<string>();
        ctx.PendingEvents.Clear();

        CdpDomainFixtures.Unwrap(await HandleAsync(
            "closeTarget", new JsonObject { ["targetId"] = pageId }, ctx));

        List<string> detached = [.. ctx.PendingEvents
            .Where(e => e.Method == "Target.detachedFromTarget")
            .Select(e => e.Params!["sessionId"]!.GetValue<string>())];
        Assert.Equal([first, second], detached);
        Assert.DoesNotContain($"{pageId}-session", detached);
    }

    [Fact]
    public async Task UnknownTargetMethodStillErrors()
    {
        var ctx = CdpContext.New();
        string error = CdpDomainFixtures.ErrorOf(
            await HandleAsync("notARealMethod", new JsonObject(), ctx));
        Assert.Contains("Unknown Target method", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression for #122 item 5: every TargetInfo payload must carry the
    /// <c>canAccessOpener</c> field. The browser-target branch of getTargetInfo (no
    /// targetId passed, so no page) used to omit it; strict CDP clients like
    /// chromiumoxide panic when the field is missing.
    /// </summary>
    [Fact]
    public async Task GetTargetInfoBrowserTargetIncludesCanAccessOpener()
    {
        var ctx = CdpContext.New();
        // No targetId -> falls through to the browser-target branch.
        JsonNode result = CdpDomainFixtures.Unwrap(
            await HandleAsync("getTargetInfo", new JsonObject(), ctx));

        JsonNode info = result["targetInfo"]!;
        Assert.Equal("browser", info["type"]!.GetValue<string>());
        Assert.True(
            info.AsObject().ContainsKey("canAccessOpener"),
            $"canAccessOpener must be present on every TargetInfo, got: {result}");
        Assert.False(info["canAccessOpener"]!.GetValue<bool>());
    }
}
