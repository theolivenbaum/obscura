using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// textContent / nodeValue / data on every node kind, and window.origin, isSecureContext and
/// crossOriginIsolated. Polymer clears its `[[binding]]` text placeholders with
/// `text.textContent = ''`, which the shim ignored. Every expected value was printed by
/// Chromium 141.0.7390.37 (headless, Playwright) for the same script and URL.
/// </summary>
public sealed class NodeTextAndWindowOriginTests
{
    private static string Eval(PocketCalculatorJsRuntime runtime, string expression) =>
        runtime.Evaluate(expression)!.GetValue<string>();

    [Fact]
    public async Task TextContentFollowsTheNodeKind()
    {
        using var fixture = RuntimeFixture.Setup("<!doctype html><html><head></head><body><p>x</p></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript("setup", """
            globalThis.recs = [];
            const r = [];
            const t = document.createTextNode('abc'); document.body.appendChild(t);
            const mo = new MutationObserver((l) => recs.push(...l.map((x) => [x.type, x.oldValue, x.target.nodeName].join(':'))));
            mo.observe(document, { subtree: true, characterData: true, characterDataOldValue: true, childList: true });
            t.textContent = ''; r.push(t.data + '/' + (t.parentNode === document.body) + '/' + t.childNodes.length);
            t.textContent = 'q'; t.textContent = null; r.push(t.data);
            t.textContent = 'x'; t.nodeValue = null; r.push(t.data);
            t.nodeValue = 'y'; t.data = null; r.push(t.data);
            t.data = undefined; r.push(t.data);
            t.textContent = 7; r.push(t.data);
            const c = document.createComment('c'); document.body.appendChild(c); c.textContent = 'cc'; c.nodeValue = 'ccc'; r.push(c.data);
            const pi = document.createProcessingInstruction('xml', 'a'); document.body.appendChild(pi); pi.textContent = 'pp'; r.push(pi.data);
            pi.nodeValue = 'qq'; r.push(pi.data + '/' + pi.textContent + '/' + pi.nodeValue);
            r.push(String(document.textContent)); document.textContent = 'zzz';
            r.push(String(document.body !== null) + '/' + document.childNodes.length);
            r.push(String(document.doctype.textContent)); document.doctype.textContent = 'q'; document.doctype.nodeValue = 'q';
            r.push(document.doctype.name); document.nodeValue = 'x'; r.push(String(document.nodeValue));
            const frag = document.createDocumentFragment(); frag.appendChild(document.createElement('i'));
            frag.textContent = 'ff'; r.push(frag.childNodes.length + '/' + frag.firstChild.nodeType + '/' + frag.textContent);
            frag.textContent = ''; r.push(frag.childNodes.length);
            const el = document.createElement('p'); el.textContent = 'a'; el.textContent = null; r.push(el.childNodes.length);
            el.nodeValue = 'x'; r.push(el.nodeValue + '/' + el.childNodes.length);
            globalThis.result = r.join('|');
            """);
        await rt.RunEventLoopBoundedAsync(100);
        Assert.Equal("/true/0||||undefined|7|ccc|pp|qq/qq/qq|null|true/2|null|html|null|1/3/ff|0|0|null/0", Eval(rt, "result"));
        Assert.Equal(
            "characterData:abc:#text,characterData::#text,characterData:q:#text,characterData::#text,"
            + "characterData:x:#text,characterData::#text,characterData:y:#text,characterData::#text,"
            + "characterData:undefined:#text,childList::BODY,characterData:c:#comment,characterData:cc:#comment,"
            + "childList::BODY,characterData:a:xml,characterData:pp:xml",
            Eval(rt, "recs.join(',')"));
    }

    private const string WindowProbe = """
        (() => {
          const d = (o, k) => { const x = Object.getOwnPropertyDescriptor(o, k); return x ? (x.get ? 'g' : '') + (x.set ? 's' : '') + ('value' in x ? 'v' : '') + (x.enumerable ? 'E' : '') + (x.configurable ? 'C' : '') + (x.writable ? 'W' : '') : 'none'; };
          const names = ['origin', 'isSecureContext', 'crossOriginIsolated'];
          const r = [window.origin, isSecureContext, crossOriginIsolated, names.map((k) => d(window, k)).join(','),
            Object.getOwnPropertyDescriptor(window, 'origin').get.name];
          try { Object.getOwnPropertyDescriptor(window, 'origin').get.call({}); r.push('no'); } catch (e) { r.push(e.constructor.name + ': ' + e.message); }
          window.origin = 'x'; r.push(window.origin + ':' + d(window, 'origin'));
          isSecureContext = 5; crossOriginIsolated = 1; r.push(isSecureContext + ':' + crossOriginIsolated);
          return r.join('|');
        })()
        """;

    [Theory]
    [InlineData("https://www.example.com/a/b?q", "https://www.example.com|true|false|gsEC,gEC,gEC|get origin|TypeError: Illegal invocation|x:vECW|true:false")]
    [InlineData("http://www.example.com:8080/a", "http://www.example.com:8080|false|false|gsEC,gEC,gEC|get origin|TypeError: Illegal invocation|x:vECW|false:false")]
    [InlineData("http://localhost:9/x", "http://localhost:9|true|false|gsEC,gEC,gEC|get origin|TypeError: Illegal invocation|x:vECW|true:false")]
    [InlineData("http://127.0.0.1:9/x", "http://127.0.0.1:9|true|false|gsEC,gEC,gEC|get origin|TypeError: Illegal invocation|x:vECW|true:false")]
    [InlineData("data:text/html,<p>x", "null|false|false|gsEC,gEC,gEC|get origin|TypeError: Illegal invocation|x:vECW|false:false")]
    public void WindowOriginAndSecureContext(string url, string expected)
    {
        using var fixture = RuntimeFixture.Page(url, "<html><body></body></html>");
        Assert.Equal(expected, Eval(fixture.Runtime, WindowProbe));
    }
}
