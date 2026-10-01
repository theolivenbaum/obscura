using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// A fetched Response's headers are immutable, and XHR setRequestHeader validates
/// synchronously, with Chromium 141's error types and messages. Rust left response
/// headers writable and accepted any request header until send.
/// </summary>
public sealed partial class RuntimeTests
{
    [Fact]
    public async Task FetchedResponseHeadersAreImmutable()
    {
        using var server = new RawHttpServer(_ =>
            "HTTP/1.1 200 OK\r\nX-Test: a\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        using var fixture = RedirectRuntimeForOrigin(server.Origin);
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                const out = [];
                const t = (label, f) => { try { out.push(label + " ok " + JSON.stringify(f())); } catch (e) { out.push(label + " " + e.name + ": " + e.message); } };
                const resp = await fetch("{{server.Origin}}/x");
                t("set", () => resp.headers.set("x-a", "b"));
                t("append", () => resp.headers.append("x-a", "b"));
                t("delete", () => resp.headers.delete("x-test"));
                t("deleteBad", () => resp.headers.delete("a b"));
                t("get", () => resp.headers.get("x-test"));
                t("clone", () => resp.clone().headers.set("x-a", "b"));
                t("error", () => Response.error().headers.set("x-a", "b"));
                t("redirect", () => Response.redirect("http://a/").headers.set("x-a", "b"));
                t("constructed", () => { const r = new Response("x"); r.headers.set("x-a", "b"); return r.headers.get("x-a"); });
                return out.join("\n");
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        Assert.Equal(
            """
            set TypeError: Failed to execute 'set' on 'Headers': Headers are immutable
            append TypeError: Failed to execute 'append' on 'Headers': Headers are immutable
            delete TypeError: Failed to execute 'delete' on 'Headers': Headers are immutable
            deleteBad TypeError: Failed to execute 'delete' on 'Headers': Invalid name
            get ok "a"
            clone TypeError: Failed to execute 'set' on 'Headers': Headers are immutable
            error TypeError: Failed to execute 'set' on 'Headers': Headers are immutable
            redirect TypeError: Failed to execute 'set' on 'Headers': Headers are immutable
            constructed ok "b"
            """.ReplaceLineEndings("\n"),
            result.Value!.GetValue<string>());
    }

    [Fact]
    public void XhrSetRequestHeaderValidatesSynchronously()
    {
        using var fixture = RuntimeFixture.Blank();
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
                const out = [];
                const t = (label, f) => { try { out.push(label + " ok " + JSON.stringify(f())); } catch (e) { out.push(label + " " + e.name + ": " + e.message); } };
                const x = new XMLHttpRequest();
                t("beforeOpen", () => x.setRequestHeader("x-a", "b"));
                x.open("GET", "/x", false);
                t("badName", () => x.setRequestHeader("a b", "c"));
                t("emptyName", () => x.setRequestHeader("", "c"));
                t("badValue", () => x.setRequestHeader("x-a", "c\rd"));
                t("nonLatinName", () => x.setRequestHeader("x-Ā", "c"));
                t("nonLatinValue", () => x.setRequestHeader("x-a", "Ā"));
                t("trimmed", () => x.setRequestHeader("X-A", " c "));
                t("combined", () => { x.setRequestHeader("x-a", "d"); return x._headers; });
                t("forbidden", () => { x.setRequestHeader("Cookie", "c"); return Object.keys(x._headers); });
                x.send();
                t("afterSend", () => x.setRequestHeader("x-a", "b"));
                return out.join("\n");
            })()
            """);
        Assert.Equal(
            """
            beforeOpen InvalidStateError: Failed to execute 'setRequestHeader' on 'XMLHttpRequest': The object's state must be OPENED.
            badName SyntaxError: Failed to execute 'setRequestHeader' on 'XMLHttpRequest': 'a b' is not a valid HTTP header field name.
            emptyName SyntaxError: Failed to execute 'setRequestHeader' on 'XMLHttpRequest': '' is not a valid HTTP header field name.
            badValue SyntaxError: Failed to execute 'setRequestHeader' on 'XMLHttpRequest': 'c
            d' is not a valid HTTP header field value.
            nonLatinName TypeError: Failed to execute 'setRequestHeader' on 'XMLHttpRequest': String contains non ISO-8859-1 code point.
            nonLatinValue TypeError: Failed to execute 'setRequestHeader' on 'XMLHttpRequest': String contains non ISO-8859-1 code point.
            trimmed ok undefined
            combined ok {"X-A":"c, d"}
            forbidden ok ["X-A"]
            afterSend InvalidStateError: Failed to execute 'setRequestHeader' on 'XMLHttpRequest': The object's state must be OPENED.
            """.ReplaceLineEndings("\n").Replace("'c\nd'", "'c\rd'", StringComparison.Ordinal),
            result!.GetValue<string>());
    }
}
