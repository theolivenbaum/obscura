using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Documents without a browsing context: DOMImplementation.createHTMLDocument/createDocument,
/// DOMParser, <c>new Document()</c>, Document.cloneNode and XMLHttpRequest.responseXML, with
/// importNode, adoptNode and cloneNode across them. They used to be plain objects over a
/// detached element of the page, whose importNode/adoptNode returned their argument: dell.com's
/// bot detector ran <c>createHTMLDocument("cloner-doc").importNode(document.documentElement,
/// false)</c> and appended the result there, which moved the page's own &lt;html&gt; out of the
/// page about 20 s after load. Every expected value was measured in Chromium 141 (headless,
/// Playwright) on the same input.
/// </summary>
public sealed class ParsedDocumentTests
{
    private const string Page =
        "<!doctype html><html><head><title>pg</title></head><body><div id=main>m</div></body></html>";

    private static string Eval(PocketCalculatorJsRuntime runtime, string expression) =>
        runtime.Evaluate(expression)!.GetValue<string>();

    [Fact]
    public void ImportNodeIntoACreatedDocumentClonesAndLeavesThePageAlone()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        Assert.Equal("false|true|0|null|true|true|0|true|<div id=\"main\">m</div>", Eval(fixture.Runtime, """
            (() => {
              const html = document.documentElement;
              const d = document.implementation.createHTMLDocument("cloner-doc");
              const imp = d.importNode(html, false);
              const same = imp === html;
              // dell.com: the copy replaces the cloner document's own root.
              d.replaceChild(imp, d.documentElement);
              return [same, imp.ownerDocument === d, imp.childNodes.length, String(imp.parentNode === d ? 'null' : imp.parentNode),
                d.documentElement === imp, document.documentElement === html && html.parentNode === document,
                d.documentElement.childNodes.length, document.body.isConnected, document.body.innerHTML].join('|');
            })()
            """));
    }

    [Fact]
    public void CreateHtmlDocumentHasItsOwnTreeAndNoBrowsingContext()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        Assert.Equal(
            "2:html,HTML|cloner-doc|[object HTMLDocument] true true HTMLDocument|null|about:blank about:blank about:blank|loading"
            + "|true|true|true true|true|html true true|CSS1Compat UTF-8 text/html|\"\"|null|BODY|false|0|0",
            Eval(fixture.Runtime, """
            (() => {
              const d = document.implementation.createHTMLDocument("cloner-doc");
              return [
                d.childNodes.length + ':' + [...d.childNodes].map(n => n.nodeName).join(','),
                d.title,
                Object.prototype.toString.call(d) + ' ' + (d instanceof Document) + ' ' + (d instanceof HTMLDocument) + ' ' + d.constructor.name,
                String(d.defaultView),
                d.URL + ' ' + d.documentURI + ' ' + d.baseURI,
                d.readyState,
                d.body.ownerDocument === d,
                d.body.parentNode === d.documentElement && d.documentElement.parentNode === d,
                d.body.isConnected + ' ' + d.isConnected,
                d.body.getRootNode() === d,
                d.doctype.name + ' ' + (d.doctype.ownerDocument === d) + ' ' + (d.firstChild === d.doctype),
                d.compatMode + ' ' + d.characterSet + ' ' + d.contentType,
                JSON.stringify(d.cookie),
                String(d.location),
                d.activeElement.nodeName,
                d.hasFocus(),
                d.styleSheets.length,
                d.forms.length,
              ].join('|');
            })()
            """));
    }

    [Fact]
    public void QueriesAndScriptsStayInsideTheCreatedDocument()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        Assert.Equal(
            "true|null|hi|9|1 1|undefined|0"
            + "|<html><head><title>cloner-doc</title></head><body><div id=\"x\" class=\"c\"><p>hi</p><span>there</span></div><script>window.__ran=1</script><style>p{color:red}</style></body></html>"
            + "|<!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>cloner-doc</title></head><body><div id=\"x\" class=\"c\"><p>hi</p><span>there</span></div><script>window.__ran=1</script><style>p{color:red}</style></body></html>"
            + "|hitherewindow.__ran=1p{color:red}|null|HTML,HEAD,TITLE,BODY,DIV,P,SPAN,SCRIPT,STYLE|1|false true|1",
            Eval(fixture.Runtime, """
            (() => {
              const d = document.implementation.createHTMLDocument("cloner-doc");
              d.body.innerHTML = '<div id="x" class="c"><p>hi</p><span>there</span></div><script>window.__ran=1<\/script><style>p{color:red}</style>';
              const w = d.createTreeWalker(d, NodeFilter.SHOW_ELEMENT);
              const walked = [];
              while (w.nextNode()) walked.push(w.currentNode.nodeName);
              // A script created in such a document and inserted there never runs, not even
              // once it is moved into the page.
              const s = d.createElement('script');
              s.textContent = 'window.__moved = 1';
              d.body.appendChild(s);
              document.body.appendChild(s);
              return [
                d.getElementById('x').ownerDocument === d,
                String(document.getElementById('x')),
                d.querySelector('.c p').textContent,
                d.querySelectorAll('*').length,
                d.getElementsByTagName('span').length + ' ' + d.getElementsByClassName('c').length,
                String(window.__ran),
                d.styleSheets.length,
                d.documentElement.outerHTML,
                new XMLSerializer().serializeToString(d),
                d.body.textContent + '|' + d.textContent,
                walked.join(','),
                d.body.compareDocumentPosition(document.body) & 1,
                document.contains(d.body) + ' ' + d.contains(d.body),
                window.__moved === undefined ? 1 : 0,
              ].join('|');
            })()
            """));
    }

    [Fact]
    public void AdoptNodeMovesANodeOutOfThePage()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        Assert.Equal(
            "true|true|true|null null|NotSupportedError|NotSupportedError|HierarchyRequestError|NotSupportedError|NotSupportedError|TypeError|true|true|true",
            Eval(fixture.Runtime, """
            (() => {
              const err = (f) => { try { f(); return 'none'; } catch (e) { return e.name; } };
              const d = document.implementation.createHTMLDocument("");
              const e = d.createElement('div');
              const createdOwner = e.ownerDocument === d;
              document.body.appendChild(e);
              const adoptedOnAppend = e.ownerDocument === document;
              e.remove();
              const p = document.createElement('p'); p.id = 'adoptme'; document.body.appendChild(p);
              const a = d.adoptNode(p);
              const host = document.createElement('div');
              const sr = host.attachShadow({ mode: 'open' });
              sr.innerHTML = '<i>s</i>';
              const errors = [err(() => d.adoptNode(document)), err(() => d.adoptNode(d)), err(() => d.adoptNode(sr)),
                err(() => d.importNode(document)), err(() => d.importNode(sr)), err(() => d.importNode(null))];
              d.adoptNode(host);
              const fromD = d.createElement('section'); fromD.innerHTML = '<i>a</i>';
              const back = document.importNode(fromD, true);
              const backOk = back.ownerDocument === document && back !== fromD && back.innerHTML === '<i>a</i>';
              const ad2 = document.adoptNode(fromD);
              return [createdOwner && adoptedOnAppend, a === p, p.ownerDocument === d,
                String(p.parentNode) + ' ' + String(document.getElementById('adoptme'))]
                .concat(errors)
                .concat([host.ownerDocument === d && sr.ownerDocument === d && sr.firstChild.ownerDocument === d,
                  backOk, ad2 === fromD && fromD.ownerDocument === document && fromD.firstChild.ownerDocument === document])
                .join('|');
            })()
            """));
    }

    [Fact]
    public void CloneNodeKeepsTheSourceDocumentAndADocumentClonesToANewOne()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        Assert.Equal("true|true true 1 HTMLDocument|0 HTMLDocument text/html|true|false|typed|typed|typed|typed|typed true typed", Eval(fixture.Runtime, """
            (() => {
              const d = document.implementation.createHTMLDocument("");
              d.body.innerHTML = '<b>x</b>';
              const dc = d.body.cloneNode(true);
              const cl = d.cloneNode(true);
              const cl3 = d.cloneNode(false);
              const t = d.createTextNode('t');
              const inp = document.createElement('input'); inp.value = 'typed';
              const cb = document.createElement('input'); cb.type = 'checkbox'; cb.value = 'typed'; cb.checked = true;
              const cbi = d.importNode(cb, false);
              return [dc.ownerDocument === d,
                (cl !== d) + ' ' + (cl.body.ownerDocument === cl) + ' ' + cl.body.childNodes.length + ' ' + cl.constructor.name,
                cl3.childNodes.length + ' ' + cl3.constructor.name + ' ' + cl3.contentType,
                t.cloneNode().ownerDocument === d,
                cl.body.firstChild === d.body.firstChild,
                inp.cloneNode().value, document.importNode(inp).value, d.importNode(inp).value, inp.value,
                cbi.value + ' ' + cbi.checked + ' ' + cbi.getAttribute('value')].join('|');
            })()
            """));
    }

    [Fact]
    public void CustomElementsAreAdoptedButNotUpgradedInADocumentWithoutABrowsingContext()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        Assert.Equal("true,true 2|HTMLElement|false", Eval(fixture.Runtime, """
            (() => {
              const d = document.implementation.createHTMLDocument("");
              customElements.define('x-ad', class extends HTMLElement {
                adoptedCallback(o, n) { window.__ad = (o === document) + ',' + (n === d); }
                connectedCallback() { window.__cc = (window.__cc || 0) + 1; }
              });
              const x = document.createElement('x-ad');
              document.body.appendChild(x);
              d.body.appendChild(x);
              d.body.innerHTML += '<x-ad></x-ad>';
              return [window.__ad + ' ' + window.__cc, d.createElement('x-ad').constructor.name,
                d.body.lastChild instanceof customElements.get('x-ad')].join('|');
            })()
            """));
    }

    [Fact]
    public void MutationsInACreatedDocumentReachOnlyItsObservers()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        Assert.Equal("0 2", Eval(fixture.Runtime, """
            (() => {
              const d = document.implementation.createHTMLDocument("");
              const mo = new MutationObserver(() => {});
              mo.observe(document, { subtree: true, childList: true, attributes: true });
              const mo2 = new MutationObserver(() => {});
              mo2.observe(d, { subtree: true, childList: true, attributes: true });
              d.body.appendChild(d.createElement('em'));
              d.body.setAttribute('data-x', '1');
              return mo.takeRecords().length + ' ' + mo2.takeRecords().length;
            })()
            """));
    }

    [Fact]
    public void RangesStartInTheirOwnDocument()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        Assert.Equal("true|q true|true true", Eval(fixture.Runtime, """
            (() => {
              const d = document.implementation.createHTMLDocument("");
              d.body.innerHTML = '<b>q</b>';
              const r = d.createRange();
              const atDoc = r.startContainer === d;
              r.selectNodeContents(d.body);
              const f = r.createContextualFragment('<i>c</i>');
              return [atDoc, r.toString() + ' ' + (r.startContainer === d.body),
                (f.ownerDocument === d) + ' ' + (f.firstChild.ownerDocument === d)].join('|');
            })()
            """));
    }

    [Fact]
    public void DomParserBuildsAWholeDocument()
    {
        using var fixture = RuntimeFixture.Page("https://example.test/page", Page);
        Assert.Equal(
            "HTMLDocument true T t 2|true|html|<html><head></head><body></body></html>|<p>a</p><p>b</p> <head></head>"
            + "|https://example.test/page|complete|BackCompat|true hidden|BODY|<div><template shadowrootmode=\"open\"><b>s</b></template></div>|undefined"
            + "|TypeError",
            Eval(fixture.Runtime, """
            (() => {
              const pd = new DOMParser().parseFromString('<!doctype html><html><head><title> T  t </title></head><body><div id=q>1</div></body></html>', 'text/html');
              const q = new DOMParser().parseFromString('<p>x', 'text/html');
              const s = new DOMParser().parseFromString('<script>window.__dps=1<\/script>', 'text/html');
              document.body.appendChild(s.querySelector('script'));
              let bad = 'none';
              try { new DOMParser().parseFromString('<a/>', 'text/plain'); } catch (e) { bad = e.name; }
              return [
                pd.constructor.name + ' ' + (pd.documentElement.parentNode === pd) + ' ' + pd.title + ' ' + pd.childNodes.length,
                pd.getElementById('q').ownerDocument === pd,
                pd.doctype.name,
                new DOMParser().parseFromString('', 'text/html').documentElement.outerHTML,
                (() => { const x = new DOMParser().parseFromString('<p>a<p>b', 'text/html'); return x.body.innerHTML + ' ' + x.head.outerHTML; })(),
                q.URL, q.readyState, q.compatMode, q.hidden + ' ' + q.visibilityState, q.scrollingElement.nodeName,
                new DOMParser().parseFromString('<div><template shadowrootmode=open><b>s</b></template></div>', 'text/html').body.innerHTML,
                String(window.__dps),
                bad,
              ].join('|');
            })()
            """));
    }

    [Fact]
    public void DomParserParsesXmlAsXml()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        Assert.Equal(
            "XMLDocument root a application/xml B|<root><a x=\"1\"/><B/></root>|ac a<b>c</b> 1|ent&A 2"
            + "|SVGSVGElement SVGPathElement SVGSVGElement|XMLDocument body HTMLParagraphElement html"
            + "|3 10,7,1 s k urn:s|a parsererror h3|html body parsererror",
            Eval(fixture.Runtime, """
            (() => {
              const P = (s, t) => new DOMParser().parseFromString(s, t || 'application/xml');
              const xd = P('<root><a x="1"/><B/></root>');
              const t = P('<r>a<b>c</b></r>', 'text/xml');
              const svg = P('<svg xmlns="http://www.w3.org/2000/svg"><path d="M0"/></svg>', 'image/svg+xml');
              const el = document.importNode(svg.documentElement, true);
              const xh = P('<html xmlns="http://www.w3.org/1999/xhtml"><body><p>a</p></body></html>', 'application/xhtml+xml');
              const ns = P('<?xml version="1.0"?><!DOCTYPE r><?pi data?><r xmlns:s="urn:s"><s:k a="1">t<!--cm--></s:k></r>', 'text/xml');
              const k = ns.documentElement.firstChild;
              const err = P('<a><b></a>');
              const junk = P('junk', 'text/xml');
              return [
                xd.constructor.name + ' ' + xd.documentElement.nodeName + ' ' + xd.documentElement.firstChild.nodeName + ' ' + xd.contentType + ' ' + xd.querySelector('B').tagName,
                new XMLSerializer().serializeToString(xd),
                t.documentElement.textContent + ' ' + t.documentElement.innerHTML + ' ' + t.getElementsByTagName('b').length,
                P('<!DOCTYPE r [<!ENTITY e "ent">]><r>&e;&amp;&#65;</r>', 'text/xml').documentElement.textContent + ' ' + P('<!DOCTYPE r [<!ENTITY e "ent">]><r>&e;</r>').childNodes.length,
                el.constructor.name + ' ' + el.firstChild.constructor.name + ' ' + svg.documentElement.constructor.name,
                xh.constructor.name + ' ' + xh.body.nodeName + ' ' + xh.querySelector('p').constructor.name + ' ' + xh.documentElement.tagName,
                ns.childNodes.length + ' ' + [...ns.childNodes].map(n => n.nodeType).join(',') + ' ' + k.prefix + ' ' + k.localName + ' ' + k.namespaceURI,
                err.documentElement.nodeName + ' ' + err.documentElement.firstChild.localName + ' ' + err.documentElement.firstChild.firstChild.localName,
                junk.documentElement.localName + ' ' + junk.documentElement.firstChild.localName + ' ' + junk.body.firstChild.localName,
              ].join('|');
            })()
            """));
    }

    [Fact]
    public void NewDocumentAndCreateDocumentAreXmlDocuments()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        Assert.Equal(
            "Document 0 application/xml about:blank|true Foo null Element|r 1|HierarchyRequestError"
            + "|XMLDocument http://www.w3.org/2000/svg true image/svg+xml 1|0 XMLDocument|true html 2",
            Eval(fixture.Runtime, """
            (() => {
              const nd = new Document();
              const first = nd.constructor.name + ' ' + nd.childNodes.length + ' ' + nd.contentType + ' ' + nd.URL;
              const x = nd.createElement('Foo');
              nd.appendChild(nd.createElement('r'));
              let second = 'ok';
              try { nd.appendChild(nd.createElement('r2')); } catch (e) { second = e.name; }
              const xdoc = document.implementation.createDocument('http://www.w3.org/2000/svg', 'svg', null);
              const empty = document.implementation.createDocument(null, '', null);
              const dt = document.implementation.createDocumentType('html', '', '');
              const withDt = document.implementation.createDocument(null, 'a', dt);
              return [first,
                (x.ownerDocument === nd) + ' ' + x.tagName + ' ' + x.namespaceURI + ' ' + x.constructor.name,
                nd.documentElement.tagName + ' ' + nd.childNodes.length, second,
                xdoc.constructor.name + ' ' + xdoc.documentElement.namespaceURI + ' ' + (xdoc.documentElement.ownerDocument === xdoc) + ' ' + xdoc.contentType + ' ' + xdoc.childNodes.length,
                empty.childNodes.length + ' ' + empty.constructor.name,
                (dt.ownerDocument === withDt) + ' ' + withDt.firstChild.nodeName + ' ' + withDt.childNodes.length].join('|');
            })()
            """));
    }

    [Fact]
    public void OpenWriteCloseOnACreatedDocumentParseAsTheyGo()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        Assert.Equal(
            "1 <html><head></head><body><p><i></i></p></body></html> complete"
            + "|1 <html><head></head><body>A<div><script id=\"x\" type=\"text/gtmscript\"></script></div></body></html> complete"
            + "|1 <html><head><script></script></head><body></body></html> complete"
            + "|<html><head></head><body><p>ab</p></body></html> loading|complete"
            + "|<html><head><title>t</title></head><body><b>zz</b></body></html>",
            Eval(fixture.Runtime, """
            (() => {
              const out = [];
              for (const m of ["<P><I></P></I>", "A<div><script id=x type=\"text/gtmscript\"></script></div>", "<script></script>"]) {
                const d = document.implementation.createHTMLDocument();
                d.open(); d.write(m); d.close();
                out.push(d.childNodes.length + ' ' + d.documentElement.outerHTML + ' ' + d.readyState);
              }
              const d3 = document.implementation.createHTMLDocument();
              d3.open(); d3.write('<p>a'); d3.write('b</p>');
              out.push(d3.documentElement.outerHTML + ' ' + d3.readyState);
              d3.close();
              out.push(d3.readyState);
              const e = document.implementation.createHTMLDocument('t');
              e.write('<b>zz</b>');
              out.push(e.documentElement.outerHTML);
              return out.join('|');
            })()
            """));
    }

    /// <summary>
    /// Node.prototype's ownerDocument getter called on a document answers null, as Chromium's
    /// does. Transcend's airgap.js reads it that way to find its sanitizing sandbox's document,
    /// and took the page instead.
    /// </summary>
    [Fact]
    public void NodeOwnerDocumentGetterOnADocumentIsNull()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        Assert.Equal("null|null|true", Eval(fixture.Runtime, """
            (() => {
              const g = Object.getOwnPropertyDescriptor(Node.prototype, 'ownerDocument').get;
              const d = document.implementation.createHTMLDocument('');
              return [String(g.call(document)), String(g.call(d)), g.call(d.body) === d].join('|');
            })()
            """));
    }

    [Fact]
    public async Task ResponseXmlIsAParsedDocument()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        fixture.Runtime.Evaluate("""
            (() => {
              window.__xhr = 'pending';
              const x = new XMLHttpRequest();
              x.open('GET', 'data:text/xml,<r><a x="1"/></r>');
              x.onload = () => {
                const d = x.responseXML;
                window.__xhr = d.constructor.name + ' ' + d.documentElement.nodeName + ' ' + d.documentElement.firstChild.getAttribute('x')
                  + ' ' + (d.documentElement.ownerDocument === d) + ' ' + String(x.response === x.responseText);
              };
              x.send();
            })()
            """);
        await fixture.Runtime.RunEventLoopBoundedAsync(2000);
        Assert.Equal("XMLDocument r 1 true true", Eval(fixture.Runtime, "window.__xhr"));
    }
}
