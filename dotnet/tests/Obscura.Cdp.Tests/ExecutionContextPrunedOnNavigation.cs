using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/execution_context_pruned_on_navigation.rs</c>.
/// </summary>
/// <remarks>
/// Regression for issue #407: navigation must retire a page's old execution context ids
/// without deleting live contexts owned by another page.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class ExecutionContextPrunedOnNavigation
{
    [Fact]
    public async Task NavigationPrunesOnlyTheNavigatedPagesStaleContextIds()
    {
        var ctx = CdpContext.New();
        string firstPage = ctx.CreatePage();
        string secondPage = ctx.CreatePage();
        ctx.Sessions["first"] = firstPage;
        ctx.Sessions["second"] = secondPage;

        long stale = (await CoreCdp.CdpAsync(
            ctx, 1, "Page.createIsolatedWorld",
            new JsonObject { ["worldName"] = "first-world" }, "first"))["executionContextId"]
            .AsI64()!.Value;
        long otherPage = (await CoreCdp.CdpAsync(
            ctx, 2, "Page.createIsolatedWorld",
            new JsonObject { ["worldName"] = "second-world" }, "second"))["executionContextId"]
            .AsI64()!.Value;

        await CoreCdp.DispatchAsync(
            ctx,
            3,
            "Page.navigate",
            new JsonObject
            {
                ["url"] = "data:text/html,<p>replacement</p>",
                ["waitUntil"] = "load",
            },
            "first");

        CdpResponse staleResult = await CoreCdp.DispatchAsync(
            ctx, 4, "Runtime.evaluate",
            new JsonObject { ["expression"] = "1", ["contextId"] = stale }, "first");
        Assert.NotNull(staleResult.Error);
        Assert.Contains("Cannot find context", staleResult.Error!.Message, StringComparison.Ordinal);

        CdpResponse otherResult = await CoreCdp.DispatchAsync(
            ctx, 5, "Runtime.evaluate",
            new JsonObject { ["expression"] = "1", ["contextId"] = otherPage }, "second");
        Assert.True(otherResult.Error is null, "other page context was pruned");
    }
}
