using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Cli.Json;
using Xunit;
using ApiBrowser = Obscura.Api.Browser;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura/tests/child_frame_scripts.rs</c>.
/// </summary>
/// <remarks>
/// Issue #600: a child iframe's document was fetched and parsed, but the frame
/// never got a scripting context, so a <c>&lt;script&gt;</c> inside it stayed an
/// inert node. This is the reporter's loopback repro, reduced to the part that
/// needs no network: two documents on one local server.
/// </remarks>
public sealed class ChildFrameScriptsTests
{
    private const string ParentHtml = """
        <!doctype html><html><head><title>parent</title></head><body>
        <script>
          var f = document.createElement('iframe');
          f.src = '/child.html';
          document.body.appendChild(f);
        </script>
        </body></html>
        """;

    private const string AttributeParentHtml = """
        <!doctype html><html><head><title>parent</title></head><body>
        <script>
          var f = document.createElement('iframe');
          f.setAttribute('src', '/child.html');
          document.body.appendChild(f);
        </script>
        </body></html>
        """;

    private const string ShadowParentHtml = """
        <!doctype html><html><head><title>parent</title></head><body>
        <script>
          var host = document.createElement('div');
          var root = host.attachShadow({mode: 'open'});
          var f = document.createElement('iframe');
          f.setAttribute('src', '/child.html');
          root.appendChild(f);
          document.body.appendChild(host);
        </script>
        </body></html>
        """;

    private const string ResetParentHtml = """
        <!doctype html><html><head><title>parent</title></head><body>
        <script>
          var f = document.createElement('iframe');
          f.src = '/child.html';
          document.body.appendChild(f);
          setTimeout(function () { f.src = 'about:blank'; }, 150);
        </script>
        </body></html>
        """;

    private const string StaticParentHtml = """
        <!doctype html><html><head><title>parent</title></head><body>
        <iframe src="/child.html"></iframe>
        </body></html>
        """;

    private const string ChildHtml = """
        <!doctype html><html><head><title>BEFORE</title></head><body>
        <p>child</p>
        <script>
          window.__ran = "YES";
          document.title = "RAN-IN-CHILD";
        </script>
        </body></html>
        """;

    /// <summary>
    /// The reporter's original pair: the child reports to its parent over
    /// postMessage, which is how every embedded widget returns a result.
    /// </summary>
    private const string MessagingParentHtml = """
        <!doctype html><html><head><title>parent</title></head><body>
        <script>
          window.__res = {parentGot: [], trusted: [], fromChildWindow: []};
          window.addEventListener('message', function (e) {
            window.__res.parentGot.push(String(e.data));
            window.__res.trusted.push(e.isTrusted === true);
            window.__res.fromChildWindow.push(e.source === document.querySelector('iframe').contentWindow);
          });
          var f = document.createElement('iframe');
          f.src = '/child-messaging.html';
          document.body.appendChild(f);
        </script>
        </body></html>
        """;

    private const string MessagingChildHtml = """
        <!doctype html><html><body>
        <script>
          try { parent.postMessage("FROM-CHILD", "*"); }
          catch (e) { document.title = "POST-THREW:" + e.message; }
          window.addEventListener('message', function (e) {
            window.__heard = String(e.data) + ':' + (e.isTrusted === true);
          });
        </script>
        </body></html>
        """;

    /// <summary>
    /// The parent document at <c>/</c>, the child at <c>/child.html</c>, and the
    /// messaging child at <c>/child-messaging.html</c>.
    /// </summary>
    private static LocalHttpServer Serve(string parentHtml) => new(target => (
        "text/html",
        Encoding.UTF8.GetBytes(target switch
        {
            "/child.html" => ChildHtml,
            "/child-messaging.html" => MessagingChildHtml,
            _ => parentHtml,
        })));

    private static string Json(JsonNode? node) => SerdeJson.ToJson(node);

    [Fact]
    public async Task A_child_frame_runs_its_own_script()
    {
        using var server = Serve(ParentHtml);
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);
        await page.SettleAsync(2000);

        Assert.Equal([$"{server.Base}/child.html"], page.FrameUrls());
        Assert.Equal("YES", PageProbe.Text(page.EvaluateInFrame(0, "window.__ran")));
        // The child's own script wrote this over the static <title>, so it
        // proves the script ran against the frame's document rather than
        // anywhere else.
        Assert.Equal("RAN-IN-CHILD", PageProbe.Text(page.EvaluateInFrame(0, "document.title")));
        // The frame's writes must not reach the parent's document.
        Assert.Equal("parent", PageProbe.Text(page.Evaluate("document.title")));
        var leaked = page.Evaluate("window.__ran");
        Assert.True(leaked is null || leaked.GetValueKind() == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_child_frame_set_by_attribute_runs_its_own_script()
    {
        using var server = Serve(AttributeParentHtml);
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);
        await page.SettleAsync(2000);

        Assert.Equal([$"{server.Base}/child.html"], page.FrameUrls());
        Assert.Equal("YES", PageProbe.Text(page.EvaluateInFrame(0, "window.__ran")));
    }

    [Fact]
    public async Task A_shadow_dom_child_frame_stays_alive_and_runs_its_script()
    {
        using var server = Serve(ShadowParentHtml);
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);
        await page.SettleAsync(2000);

        Assert.Equal([$"{server.Base}/child.html"], page.FrameUrls());
        Assert.Equal("YES", PageProbe.Text(page.EvaluateInFrame(0, "window.__ran")));
    }

    /// <summary>
    /// The whole point of a child frame having a scripting context: it can report
    /// its result back out. This is the reporter's <c>parentGot</c> assertion.
    /// </summary>
    [Fact]
    public async Task A_child_frame_reaches_its_parent_with_post_message()
    {
        using var server = Serve(MessagingParentHtml);
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);
        await page.SettleAsync(2000);

        Assert.Equal("[\"FROM-CHILD\"]", Json(page.Evaluate("window.__res.parentGot")));
        // A widget gates on isTrusted and drops anything else without a word.
        Assert.Equal("[true]", Json(page.Evaluate("window.__res.trusted")));
        // And it replies through event.source, so that has to be the frame's window.
        Assert.Equal("[true]", Json(page.Evaluate("window.__res.fromChildWindow")));
    }

    /// <summary>The other direction: a page talking into its frame.</summary>
    [Fact]
    public async Task A_parent_reaches_its_child_with_post_message()
    {
        using var server = Serve(MessagingParentHtml);
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);
        await page.SettleAsync(2000);

        page.Evaluate("document.querySelector('iframe').contentWindow.postMessage('TO-CHILD', '*')");
        await page.SettleAsync(1000);

        Assert.Equal("TO-CHILD:true", PageProbe.Text(page.EvaluateInFrame(0, "window.__heard")));
    }

    /// <summary>
    /// <c>window.postMessage(x, '*')</c> targets the same window, and its
    /// listener has to hear it. This was a no-op stub, so a page posting to
    /// itself waited forever.
    /// </summary>
    [Fact]
    public async Task Window_post_message_delivers_to_the_same_window()
    {
        using var server = LocalHttpServer.Html("""
            <!doctype html><html><body><script>
              window.__got = [];
              window.addEventListener('message', (e) => window.__got.push([String(e.data), e.isTrusted === true]));
              window.postMessage('SELF', '*');
              window.__syncGot = window.__got.length;
            </script></body></html>
            """);

        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);
        await page.SettleAsync(1000);

        Assert.Equal("[[\"SELF\",true]]", Json(page.Evaluate("window.__got")));
        // postMessage never delivers synchronously.
        Assert.Equal(0.0, PageProbe.Number(page.Evaluate("window.__syncGot")));
    }

    /// <summary>
    /// SEC-001 / #704: a self-post must honour targetOrigin. A message restricted
    /// to a different origin is dropped, while the page's own origin and
    /// <c>"/"</c> are delivered.
    /// </summary>
    [Fact]
    public async Task Window_post_message_honours_target_origin()
    {
        using var server = LocalHttpServer.Html("""
            <!doctype html><html><body><script>
              window.__got = [];
              window.addEventListener('message', (e) => window.__got.push(String(e.data)));
              window.postMessage('LEAK', 'https://other.example');   // mismatched -> dropped
              window.postMessage('OK', window.location.origin);      // exact origin -> delivered
              window.postMessage('SLASH', '/');                      // same-origin as sender -> delivered
            </script></body></html>
            """);

        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);
        await page.SettleAsync(1000);

        Assert.Equal("[\"OK\",\"SLASH\"]", Json(page.Evaluate("window.__got")));
    }

    /// <summary>
    /// A parser-created <c>&lt;iframe src&gt;</c> never goes through the
    /// <c>src</c> setter, so nothing used to start its load at all.
    /// </summary>
    [Fact]
    public async Task A_static_child_frame_runs_its_own_script()
    {
        using var server = Serve(StaticParentHtml);
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);
        await page.SettleAsync(2000);

        Assert.Equal([$"{server.Base}/child.html"], page.FrameUrls());
        Assert.Equal("YES", PageProbe.Text(page.EvaluateInFrame(0, "window.__ran")));
    }

    [Fact]
    public async Task A_rejected_child_frame_does_not_leave_js_references()
    {
        using var server = Serve(StaticParentHtml);
        // Rust sets this process-wide and relies on process-per-test isolation;
        // here it is restored so the rest of the class still sees the default.
        var previous = Environment.GetEnvironmentVariable("OBSCURA_MAX_LIVE_FRAMES");
        Environment.SetEnvironmentVariable("OBSCURA_MAX_LIVE_FRAMES", "0");
        try
        {
            var browser = ApiBrowser.New();
            using var page = await browser.NewPageAsync();
            await page.GotoAsync(server.Base);
            await page.SettleAsync(2000);

            Assert.Empty(page.FrameUrls());
            foreach (var registry in new[]
            {
                "__obscura_frameObjects",
                "__obscura_frameWindows",
                "__obscura_frameElements",
            })
            {
                Assert.Equal(
                    0.0,
                    PageProbe.Number(page.Evaluate($"Object.keys(globalThis.{registry}).length")));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OBSCURA_MAX_LIVE_FRAMES", previous);
        }
    }

    [Fact]
    public async Task Navigating_blank_releases_child_realms()
    {
        using var server = Serve(StaticParentHtml);
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);
        await page.SettleAsync(2000);
        Assert.Single(page.FrameUrls());

        await page.GotoAsync("about:blank");
        Assert.Empty(page.FrameUrls());
    }

    [Fact]
    public async Task Changing_iframe_src_releases_the_previous_realm()
    {
        using var server = Serve(ResetParentHtml);
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);
        await page.SettleAsync(1000);

        Assert.Empty(page.FrameUrls());
        foreach (var registry in new[] { "__obscura_frameWindows", "__obscura_frameElements" })
        {
            Assert.Equal(
                0.0,
                PageProbe.Number(page.Evaluate($"Object.keys(globalThis.{registry}).length")));
        }
    }
}
