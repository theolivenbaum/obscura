using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The MutationObserver notify-set behaviour of the shared bootstrap shim
/// (<c>crates/obscura-js/js/bootstrap.js</c>).
/// </summary>
/// <remarks>
/// DOM 4.3.4 and the HTML event loop put the notify-set drain on the microtask
/// checkpoint, in the same queue as <c>queueMicrotask</c> and promise jobs and
/// ahead of the next task. These facts pin that placement plus the three
/// registration rules the shim used to get wrong: an observer is listed once
/// however many nodes it watches, <c>disconnect()</c> empties its record queue,
/// and <c>attributeFilter</c> both implies <c>attributes</c> and filters by name.
/// </remarks>
public sealed class MutationObserverTests
{
    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.Equal(JsonNode.Parse(expected)?.ToJsonString() ?? "null", actual?.ToJsonString() ?? "null");

    [Fact]
    public async Task RecordsAreDeliveredAtTheMicrotaskCheckpointBeforeTheNextTask()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=\"host\"></div></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "mutation-observer-checkpoint",
            """
            globalThis.__order = [];
            const observer = new MutationObserver(() => __order.push("observer"));
            observer.observe(document.getElementById("host"), { childList: true });
            setTimeout(() => __order.push("timer"), 0);
            queueMicrotask(() => __order.push("microtask-before"));
            document.getElementById("host").appendChild(document.createElement("span"));
            queueMicrotask(() => __order.push("microtask-after"));
            __order.push("sync");
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """["sync","microtask-before","observer","microtask-after","timer"]""",
            rt.Evaluate("__order"));
    }

    [Fact]
    public async Task ObserverWatchingTwoNodesSeesEachMutationOnce()
    {
        using var fixture = RuntimeFixture.Setup(
            "<html><body><div id=\"a\"></div><div id=\"b\"></div></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "mutation-observer-two-targets",
            """
            globalThis.__seen = { calls: 0, records: 0 };
            const observer = new MutationObserver((records) => {
              __seen.calls += 1;
              __seen.records += records.length;
            });
            observer.observe(document.getElementById("a"), { childList: true });
            observer.observe(document.getElementById("b"), { childList: true });
            document.getElementById("a").appendChild(document.createElement("i"));
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""{"calls":1,"records":1}""", rt.Evaluate("__seen"));
    }

    [Fact]
    public async Task DisconnectEmptiesTheRecordQueueBeforeTheCheckpoint()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=\"host\"></div></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "mutation-observer-disconnect",
            """
            globalThis.__calls = 0;
            const host = document.getElementById("host");
            const observer = new MutationObserver(() => { __calls += 1; });
            observer.observe(host, { childList: true });
            host.appendChild(document.createElement("i"));
            observer.disconnect();
            """);

        await rt.RunEventLoopBoundedAsync(100);
        Assert.Equal(0.0, rt.Evaluate("__calls")!.GetValue<double>());
    }

    [Fact]
    public async Task ReobservingTheSameNodeReplacesItsOptionsInsteadOfAddingARegistration()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=\"host\"></div></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "mutation-observer-reobserve",
            """
            globalThis.__seen = { calls: 0, records: 0 };
            const host = document.getElementById("host");
            const observer = new MutationObserver((records) => {
              __seen.calls += 1;
              __seen.records += records.length;
            });
            observer.observe(host, { childList: true });
            observer.observe(host, { childList: true });
            host.appendChild(document.createElement("i"));
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""{"calls":1,"records":1}""", rt.Evaluate("__seen"));
    }

    [Fact]
    public async Task AttributeFilterImpliesAttributesAndFiltersByName()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=\"host\"></div></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "mutation-observer-attribute-filter",
            """
            globalThis.__names = [];
            const host = document.getElementById("host");
            const observer = new MutationObserver((records) => {
              for (const record of records) __names.push(record.attributeName);
            });
            observer.observe(host, { attributeFilter: ["data-watched"] });
            host.setAttribute("data-watched", "1");
            host.setAttribute("data-ignored", "1");
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""["data-watched"]""", rt.Evaluate("__names"));
    }

    [Fact]
    public async Task ManyMutationsInOneTaskAreDeliveredAsOneBatch()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=\"host\"></div></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "mutation-observer-batching",
            """
            globalThis.__seen = { calls: 0, records: 0 };
            const host = document.getElementById("host");
            const observer = new MutationObserver((records) => {
              __seen.calls += 1;
              __seen.records += records.length;
            });
            observer.observe(host, { childList: true });
            for (let i = 0; i < 40; i++) host.appendChild(document.createElement("i"));
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""{"calls":1,"records":40}""", rt.Evaluate("__seen"));
    }

    [Fact]
    public async Task TakeRecordsDrainsTheQueueSoNoCallbackFollows()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=\"host\"></div></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "mutation-observer-take-records",
            """
            globalThis.__state = { taken: 0, calls: 0 };
            const host = document.getElementById("host");
            const observer = new MutationObserver(() => { __state.calls += 1; });
            observer.observe(host, { childList: true });
            host.appendChild(document.createElement("i"));
            __state.taken = observer.takeRecords().length;
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""{"taken":1,"calls":0}""", rt.Evaluate("__state"));
    }
}
