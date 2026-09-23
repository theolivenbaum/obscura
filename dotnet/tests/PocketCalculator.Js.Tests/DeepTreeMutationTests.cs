using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// SECURITY.md M11: building a deep chain with <c>appendChild</c> must cost time linear in its
/// depth. Two ancestor walks per append made it quadratic: 20000 levels took about 20 s
/// connected and 30000 levels took 21 s detached, against well under a second each now.
/// The bounds below are loose enough for a loaded machine and far below the old cost.
/// </summary>
public sealed class DeepTreeMutationTests
{
    [Fact]
    public void BuildingADeepConnectedChainIsLinear()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
              const t0 = Date.now();
              let p = document.body;
              for (let i = 0; i < 20000; i++) {
                const d = document.createElement('div');
                p.appendChild(d);
                p = d;
              }
              return JSON.stringify({ ms: Date.now() - t0, root: p.getRootNode() === document, connected: p.isConnected });
            })()
            """);
        var state = System.Text.Json.Nodes.JsonNode.Parse(result!.GetValue<string>())!;
        Assert.True(state["root"]!.GetValue<bool>());
        Assert.True(state["connected"]!.GetValue<bool>());
        Assert.True(state["ms"]!.GetValue<double>() < 8000, $"20000 connected appends took {state["ms"]}ms");
    }

    [Fact]
    public void BuildingADeepDetachedChainIsLinear()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
              const t0 = Date.now();
              const top = document.createElement('div');
              let p = top;
              for (let i = 0; i < 30000; i++) {
                const d = document.createElement('div');
                p.appendChild(d);
                d.appendChild(document.createTextNode('x'));
                p = d;
              }
              return JSON.stringify({ ms: Date.now() - t0, root: p.getRootNode() === top });
            })()
            """);
        var state = System.Text.Json.Nodes.JsonNode.Parse(result!.GetValue<string>())!;
        Assert.True(state["root"]!.GetValue<bool>());
        Assert.True(state["ms"]!.GetValue<double>() < 8000, $"30000 detached appends took {state["ms"]}ms");
    }

    [Fact]
    public void GetRootNodeStillFindsAShadowRoot()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=\"host\"></div></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
              const root = document.getElementById('host').attachShadow({ mode: 'open' });
              const inner = document.createElement('span');
              root.appendChild(inner);
              const outer = document.createElement('p');
              document.body.appendChild(outer);
              return (inner.getRootNode() === root) + ' ' + (outer.getRootNode() === document);
            })()
            """);
        Assert.Equal("true true", result!.GetValue<string>());
    }
}
