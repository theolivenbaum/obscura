using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of
/// <c>crates/obscura-cdp/tests/form_submit_method_bypasses_listener.rs</c>.
/// </summary>
/// <remarks>
/// <c>HTMLFormElement.submit()</c> (the method) must submit WITHOUT firing a cancelable
/// <c>submit</c> event, so a page's <c>submit</c> listener that calls
/// <c>preventDefault()</c> cannot veto it. Only <c>requestSubmit()</c> and user-initiated
/// submits fire the cancelable event. Regression test for the invisible-reCAPTCHA login
/// pattern (listener preventDefaults; a success callback calls <c>form.submit()</c> to
/// actually send the form).
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class FormSubmitMethodBypassesListener
{
    private const string FormDocument = """
        <html><body>
        <form id="f" action="/submitted">
          <input type="hidden" name="q" value="1">
          <button id="b" type="submit">Go</button>
        </form>
        <script>
        document.getElementById('f').addEventListener('submit', function(e) { e.preventDefault(); });
        </script>
        </body></html>
        """;

    /// <summary>Serves a form whose <c>submit</c> listener always calls preventDefault().</summary>
    private static CoreCdpServer ServeForm() => CoreCdpServer.Routed(path =>
        path.StartsWith("/submitted", StringComparison.Ordinal)
            ? ("<html><body>submitted</body></html>", "text/html", 200)
            : (FormDocument, "text/html", 200));

    private static async Task<(CdpContext Ctx, string PageId, string Session, IDisposable Owned)>
        NavigateAsync(CoreCdpServer server, string sessionId)
    {
        CoreCdp.AllowLoopback();
        var ctx = CdpContext.New();
        IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        ctx.Sessions[sessionId] = pageId;
        await CoreCdp.CdpAsync(
            ctx,
            1,
            "Page.navigate",
            new JsonObject { ["url"] = server.Url, ["waitUntil"] = "load" },
            sessionId);
        return (ctx, pageId, sessionId, owned);
    }

    [Fact]
    public async Task SubmitMethodNavigatesDespitePreventDefaultListener()
    {
        using CoreCdpServer server = ServeForm();
        (CdpContext ctx, string pageId, string session, IDisposable owned) =
            await NavigateAsync(server, "session-1");
        using (owned)
        {
            // The submit() METHOD must not fire the cancelable submit event, so the page's
            // preventDefault() listener cannot stop it: navigation proceeds.
            await CoreCdp.CdpAsync(
                ctx,
                2,
                "Runtime.evaluate",
                new JsonObject { ["expression"] = "document.getElementById('f').submit()" },
                session);

            Obscura.Browser.Page page = ctx.GetPageMut(pageId)!;
            Assert.Equal("/submitted", page.Url!.Path);
            Assert.Equal("q=1", page.Url!.Query);
        }
    }

    [Fact]
    public async Task RequestSubmitIsVetoedByPreventDefaultListener()
    {
        using CoreCdpServer server = ServeForm();
        (CdpContext ctx, string pageId, string session, IDisposable owned) =
            await NavigateAsync(server, "session-2");
        using (owned)
        {
            // requestSubmit() DOES fire the cancelable submit event, so the listener's
            // preventDefault() cancels it: the page must NOT navigate.
            JsonNode has = await CoreCdp.EvalAsync(
                ctx, 2, "typeof document.getElementById('f').requestSubmit", session);
            Assert.Equal("function", has.Get("result").Get("value").AsString());

            await CoreCdp.CdpAsync(
                ctx,
                3,
                "Runtime.evaluate",
                new JsonObject { ["expression"] = "document.getElementById('f').requestSubmit()" },
                session);

            await CoreCdp.CdpAsync(
                ctx,
                4,
                "Input.dispatchMouseEvent",
                new JsonObject
                {
                    ["type"] = "mouseReleased",
                    ["x"] = 0.0,
                    ["y"] = 0.0,
                    ["button"] = "left",
                    ["clickCount"] = 1,
                },
                session);

            Obscura.Browser.Page page = ctx.GetPageMut(pageId)!;
            Assert.NotEqual("/submitted", page.Url!.Path);
        }
    }

    /// <summary>
    /// A synthetic CDP click on a submit button is a user-initiated submit, so the
    /// cancelable <c>submit</c> event must fire and a preventDefault() listener must be
    /// able to veto navigation. Before the Input-domain fix this path called
    /// <c>form.submit()</c> directly, bypassing the listener.
    /// </summary>
    [Fact]
    public async Task CdpClickSubmitButtonIsVetoedByPreventDefaultListener()
    {
        using CoreCdpServer server = ServeForm();
        (CdpContext ctx, string pageId, string session, IDisposable owned) =
            await NavigateAsync(server, "session-3");
        using (owned)
        {
            // Point the CDP click resolver at the submit button explicitly so the test does
            // not depend on layout coordinates.
            await CoreCdp.CdpAsync(
                ctx,
                2,
                "Runtime.evaluate",
                new JsonObject
                {
                    ["expression"] =
                        "globalThis.__obscura_click_target = document.getElementById('b')",
                },
                session);

            await CoreCdp.CdpAsync(
                ctx,
                3,
                "Input.dispatchMouseEvent",
                new JsonObject
                {
                    ["type"] = "mousePressed",
                    ["x"] = 0.0,
                    ["y"] = 0.0,
                    ["button"] = "left",
                    ["clickCount"] = 1,
                },
                session);

            Obscura.Browser.Page page = ctx.GetPageMut(pageId)!;
            Assert.NotEqual("/submitted", page.Url!.Path);
        }
    }

    /// <summary>
    /// <c>requestSubmit(submitter)</c> must validate its argument before doing anything
    /// else (issue #424): a TypeError if the submitter is not a submit button, and a
    /// NotFoundError DOMException if it is not owned by the form. Obscura accepted anything
    /// and silently submitted. The form under test preventDefault()s its own submit event
    /// so the valid-submitter case cannot navigate away.
    /// </summary>
    [Fact]
    public async Task RequestSubmitValidatesItsSubmitterArgument()
    {
        using CoreCdpServer server = ServeForm();
        (CdpContext ctx, _, string session, IDisposable owned) =
            await NavigateAsync(server, "session-4");
        using (owned)
        {
            JsonNode evaluated = await CoreCdp.EvalAsync(
                ctx,
                2,
                """
                (() => {
                    document.body.innerHTML =
                      '<form id="vf">' +
                        '<div id="d"></div>' +
                        '<button id="ok" type="submit">go</button>' +
                        '<button id="plain" type="button">x</button>' +
                        '<input id="inp" type="text">' +
                      '</form>' +
                      '<button id="outside" type="submit">y</button>';
                    const f = document.getElementById('vf');
                    f.addEventListener('submit', (e) => e.preventDefault());
                    const probe = (fn) => {
                        try { fn(); return "no-throw"; }
                        catch (e) {
                            return (e instanceof DOMException) ? "DOMException:" + e.name
                                                               : (e && e.constructor && e.constructor.name) || String(e);
                        }
                    };
                    return JSON.stringify({
                        div:      probe(() => f.requestSubmit(document.getElementById('d'))),
                        plain:    probe(() => f.requestSubmit(document.getElementById('plain'))),
                        text:     probe(() => f.requestSubmit(document.getElementById('inp'))),
                        outside:  probe(() => f.requestSubmit(document.getElementById('outside'))),
                        valid:    probe(() => f.requestSubmit(document.getElementById('ok'))),
                        noArg:    probe(() => f.requestSubmit()),
                        nullArg:  probe(() => f.requestSubmit(null)),
                    });
                })()
                """,
                session);
            JsonNode value = CoreCdp.ParseStringified(evaluated);
            Assert.Equal("TypeError", value["div"].AsString());
            Assert.Equal("TypeError", value["plain"].AsString());
            Assert.Equal("TypeError", value["text"].AsString());
            Assert.Equal("DOMException:NotFoundError", value["outside"].AsString());
            Assert.Equal("no-throw", value["valid"].AsString());
            Assert.Equal("no-throw", value["noArg"].AsString());
            Assert.Equal("no-throw", value["nullArg"].AsString());
        }
    }
}
