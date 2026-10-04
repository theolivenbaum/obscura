using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The HTML element interfaces against Chromium's. The shim used to alias HTMLElement and
/// some thirty HTML*Element interfaces to Element, so every element was an instance of all
/// of them: a &lt;div&gt; was an HTMLScriptElement and &lt;head&gt; an HTMLIFrameElement.
/// Every expected value here was measured in Chromium 141.0.7390.37 (headless, Playwright).
/// </summary>
public sealed class HtmlElementInterfacesTests
{
    private static string Eval(PocketCalculatorJsRuntime runtime, string expression) =>
        runtime.Evaluate(expression)!.GetValue<string>();

    // Interface -> a tag that creates it.
    private const string Interfaces = """
        {HTMLScriptElement:"script",HTMLUnknownElement:"foo",HTMLDivElement:"div",HTMLSpanElement:"span",
         HTMLParagraphElement:"p",HTMLAnchorElement:"a",HTMLButtonElement:"button",HTMLLabelElement:"label",
         HTMLTableElement:"table",HTMLIFrameElement:"iframe",HTMLCanvasElement:"canvas",HTMLStyleElement:"style",
         HTMLLinkElement:"link",HTMLMetaElement:"meta",HTMLHeadElement:"head",HTMLBodyElement:"body",
         HTMLHtmlElement:"html",HTMLBRElement:"br",HTMLHRElement:"hr",HTMLUListElement:"ul",HTMLOListElement:"ol",
         HTMLLIElement:"li",HTMLPreElement:"pre",HTMLHeadingElement:"h1",HTMLTemplateElement:"template",
         HTMLSlotElement:"slot",HTMLOptionElement:"option",HTMLDataListElement:"datalist",
         HTMLFieldSetElement:"fieldset",HTMLLegendElement:"legend",HTMLProgressElement:"progress",
         HTMLDetailsElement:"details",HTMLDialogElement:"dialog"}
        """;

    [Fact]
    public void EachInterfaceIsItsOwnSubclassOfHTMLElement()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        var result = Eval(fixture.Runtime, $$"""
            (() => {
              const map = {{Interfaces}};
              const bad = [];
              for (const name in map) {
                const C = globalThis[name];
                const el = document.createElement(map[name]);
                let err = '';
                try { new C(); } catch (e) { err = e.constructor.name + ': ' + e.message; }
                const others = Object.keys(map).filter((n) => n !== name && el instanceof globalThis[n]);
                const got = [C.name, C.length, C !== Element, el instanceof C, el instanceof HTMLElement,
                  Object.getPrototypeOf(C.prototype) === HTMLElement.prototype, Object.getPrototypeOf(C) === HTMLElement,
                  Object.prototype.toString.call(el), Object.prototype.toString.call(C.prototype),
                  Function.prototype.toString.call(C), err, others.join('+')].join('|');
                const want = [name, 0, true, true, true, true, true, '[object ' + name + ']', '[object ' + name + ']',
                  'function ' + name + '() { [native code] }',
                  "TypeError: Failed to construct '" + name + "': Illegal constructor", ''].join('|');
                if (got !== want) bad.push(got);
              }
              return bad.join('\n');
            })()
            """);
        Assert.Equal("", result);
    }

    [Fact]
    public void HTMLElementSitsBetweenElementAndTheHtmlInterfaces()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        Assert.Equal(
            "false|true|true|HTMLElement|0|[object HTMLElement]|TypeError: Failed to construct 'HTMLElement': Illegal constructor|TypeError: Failed to construct 'Element': Illegal constructor",
            Eval(fixture.Runtime, """
                (() => {
                  const e = (f) => { try { f(); return 'ok'; } catch (x) { return x.constructor.name + ': ' + x.message; } };
                  return [HTMLElement === Element, Object.getPrototypeOf(HTMLElement.prototype) === Element.prototype,
                    Object.getPrototypeOf(HTMLElement) === Element, HTMLElement.name, HTMLElement.length,
                    Object.prototype.toString.call(HTMLElement.prototype), e(() => new HTMLElement()),
                    e(() => new Element())].join('|');
                })()
                """));
        // The cases from live sites: Ensighten's HTMLScriptElement check and webpack
        // style-loader's iframe check both saw every element as a match.
        Assert.Equal(
            "false|false|[object HTMLHeadElement]|[object HTMLHtmlElement]|[object HTMLBodyElement]|true",
            Eval(fixture.Runtime, """
                [document.createElement('div') instanceof HTMLScriptElement,
                 document.head instanceof HTMLIFrameElement,
                 Object.prototype.toString.call(document.head),
                 Object.prototype.toString.call(document.documentElement),
                 Object.prototype.toString.call(document.body),
                 document.body instanceof HTMLBodyElement].join('|')
                """));
    }

    [Fact]
    public void TagNamesMapToChromiumsInterfaces()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        const string expected =
            "a=HTMLAnchorElement abbr=HTMLElement applet=HTMLUnknownElement area=HTMLAreaElement "
            + "article=HTMLElement audio=HTMLAudioElement b=HTMLElement blockquote=HTMLQuoteElement "
            + "canvas=HTMLCanvasElement center=HTMLElement data=HTMLDataElement del=HTMLModElement "
            + "dir=HTMLDirectoryElement font=HTMLFontElement frame=HTMLFrameElement frameset=HTMLFrameSetElement "
            + "h6=HTMLHeadingElement image=HTMLUnknownElement img=HTMLImageElement input=HTMLInputElement "
            + "ins=HTMLModElement listing=HTMLPreElement main=HTMLElement marquee=HTMLMarqueeElement "
            + "menu=HTMLMenuElement menuitem=HTMLUnknownElement nobr=HTMLElement noscript=HTMLElement "
            + "param=HTMLParamElement q=HTMLQuoteElement search=HTMLElement section=HTMLElement "
            + "select=HTMLSelectElement selectedcontent=HTMLSelectedContentElement summary=HTMLElement "
            + "svg=HTMLUnknownElement math=HTMLUnknownElement td=HTMLTableCellElement textarea=HTMLTextAreaElement "
            + "tr=HTMLTableRowElement video=HTMLVideoElement wbr=HTMLElement xmp=HTMLPreElement "
            + "x-foo=HTMLElement blah=HTMLUnknownElement";
        Assert.Equal(expected, Eval(fixture.Runtime, """
            "a abbr applet area article audio b blockquote canvas center data del dir font frame frameset h6 image img input ins listing main marquee menu menuitem nobr noscript param q search section select selectedcontent summary svg math td textarea tr video wbr xmp x-foo blah"
              .split(' ').map((t) => t + '=' + Object.prototype.toString.call(document.createElement(t)).slice(8, -1)).join(' ')
            """));
        Assert.Equal(
            "HTMLMediaElement,HTMLMediaElement,HTMLElement,HTMLElement",
            Eval(fixture.Runtime, """
                [HTMLVideoElement, HTMLAudioElement, HTMLMediaElement, HTMLImageElement]
                  .map((C) => Object.getPrototypeOf(C.prototype).constructor.name).join(',')
                """));
    }

    [Fact]
    public void EveryWayOfMakingAnElementGetsItsInterface()
    {
        using var fixture = RuntimeFixture.Setup(
            "<html><body><div id=host><p>x<b>y</b></p><svg><path d='M0 0'/></svg><my-widget></my-widget><blah></blah></div></body></html>");
        Assert.Equal(
            "p=HTMLParagraphElement b=HTMLElement svg=SVGSVGElement path=SVGPathElement my-widget=HTMLElement blah=HTMLUnknownElement",
            Eval(fixture.Runtime, """
                Array.from(document.getElementById('host').querySelectorAll('*'))
                  .map((e) => e.localName + '=' + Object.prototype.toString.call(e).slice(8, -1)).join(' ')
                """));
        Assert.Equal(
            "HTMLUListElement|HTMLParagraphElement|HTMLUListElement|HTMLTableRowElement|HTMLDivElement|Element|SVGPathElement",
            Eval(fixture.Runtime, """
                (() => {
                  const name = (e) => Object.prototype.toString.call(e).slice(8, -1);
                  const d = document.createElement('div');
                  d.innerHTML = '<ul><li>a</li></ul>';
                  const t = document.createElement('table');
                  t.innerHTML = '<tr><td>1</td></tr>';
                  return [name(d.firstChild), name(document.createElement('p').cloneNode()),
                    name(document.importNode(d.firstChild, true)), name(t.querySelector('tr')),
                    name(document.createElementNS('http://www.w3.org/1999/xhtml', 'div')),
                    name(document.createElementNS(null, 'div')),
                    name(document.createElementNS('http://www.w3.org/2000/svg', 'path'))].join('|');
                })()
                """));
        // An SVG or null-namespace element is not an HTML element.
        Assert.Equal(
            "false|true|false",
            Eval(fixture.Runtime, """
                [document.querySelector('svg') instanceof HTMLElement, document.querySelector('svg') instanceof Element,
                 document.createElementNS(null, 'p') instanceof HTMLElement].join('|')
                """));
    }

    [Fact]
    public void PatchingOneInterfacePatchesOnlyItsElements()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        Assert.Equal(
            "1|x|[object HTMLAnchorElement]",
            Eval(fixture.Runtime, """
                (() => {
                  let hits = 0;
                  HTMLScriptElement.prototype.setAttribute = function (n, v) {
                    hits++;
                    if (!(this instanceof HTMLScriptElement)) throw new Error('not a script');
                    return Element.prototype.setAttribute.call(this, n, v);
                  };
                  document.createElement('div').setAttribute('x', '1');
                  document.createElement('span').setAttribute('x', '1');
                  const s = document.createElement('script');
                  s.setAttribute('id', 'x');
                  delete HTMLScriptElement.prototype.setAttribute;
                  return [hits, s.id, Object.prototype.toString.call(document.createElement('a'))].join('|');
                })()
                """));
    }

    [Fact]
    public void HtmlOnlyMembersLiveOnHTMLElementPrototype()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=d></div><svg id=s></svg></body></html>");
        // Owner per name, as HTMLElement/Element/SVGElement: Chromium 141's.
        Assert.Equal(
            "innerText:true/false/false outerText:true/false/false hidden:true/false/false style:true/false/true "
            + "dataset:true/false/true onclick:true/false/true oninput:true/false/true offsetWidth:true/false/false "
            + "offsetTop:true/false/false offsetParent:true/false/false click:true/false/false focus:true/false/true "
            + "blur:true/false/true title:true/false/false lang:true/false/false dir:true/false/false "
            + "tabIndex:true/false/true contentEditable:true/false/false isContentEditable:true/false/false "
            + "onload:true/false/true getAttribute:false/true/false",
            Eval(fixture.Runtime, """
                ['innerText', 'outerText', 'hidden', 'style', 'dataset', 'onclick', 'oninput', 'offsetWidth',
                 'offsetTop', 'offsetParent', 'click', 'focus', 'blur', 'title', 'lang', 'dir', 'tabIndex',
                 'contentEditable', 'isContentEditable', 'onload', 'getAttribute']
                  .map((n) => n + ':' + [HTMLElement.prototype, Element.prototype, SVGElement.prototype]
                    .map((p) => Object.prototype.hasOwnProperty.call(p, n)).join('/')).join(' ')
                """));
        Assert.Equal(
            "function|function|function get innerText() { [native code] }|false|true|undefined|function|true|true|-1",
            Eval(fixture.Runtime, """
                (() => {
                  const d = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'innerText');
                  const svg = document.getElementById('s');
                  return [typeof d.get, typeof d.set, Function.prototype.toString.call(d.get),
                    'innerText' in svg, 'style' in svg, typeof svg.click, typeof svg.focus,
                    'onclick' in document.getElementById('d'), 'onhashchange' in document.body,
                    document.getElementById('d').tabIndex].join('|');
                })()
                """));
    }

    [Fact]
    public void NewHtmlMembersMatchChromium()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=w><p id=p>hi there</p></div></body></html>");
        Assert.Equal(
            "inherit|false|SyntaxError|true|true|plaintext-only|inherit",
            Eval(fixture.Runtime, """
                (() => {
                  const e = document.createElement('span');
                  const out = [e.contentEditable, e.isContentEditable];
                  try { e.contentEditable = 'bogus'; out.push('none'); } catch (x) { out.push(x.name); }
                  e.contentEditable = 'true';
                  out.push(e.contentEditable, e.isContentEditable);
                  e.setAttribute('contenteditable', 'PLAINTEXT-ONLY');
                  out.push(e.contentEditable);
                  e.setAttribute('contenteditable', 'nope');
                  out.push(e.contentEditable);
                  return out.join('|');
                })()
                """));
        Assert.Equal(
            "true|false|false|true|true|false|https://example.com/x?y#z|repl|NoModificationAllowedError",
            Eval(fixture.Runtime, """
                (() => {
                  const a = document.createElement('a');
                  a.href = 'https://example.com/x?y#z';
                  const p = document.getElementById('p');
                  p.outerText = 'repl';
                  let err = 'none';
                  try { document.createElement('i').outerText = 'x'; } catch (x) { err = x.name; }
                  return [document.createElement('img').draggable, document.createElement('a').draggable,
                    document.createElement('div').draggable, document.createElement('div').spellcheck,
                    document.createElement('div').translate, document.createElement('div').inert,
                    String(a), document.getElementById('w').innerHTML, err].join('|');
                })()
                """));
    }

    [Fact]
    public void CustomElementsConstructAndUpgrade()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><late-el></late-el></body></html>");
        Assert.Equal(
            "[object HTMLElement]|true|my-el|false|true|true|<my-el></my-el>|TypeError: Failed to construct 'HTMLElement': Illegal constructor",
            Eval(fixture.Runtime, """
                (() => {
                  class MyEl extends HTMLElement { constructor() { super(); this.made = true; } }
                  customElements.define('my-el', MyEl);
                  const a = new MyEl();
                  const b = document.createElement('my-el');
                  document.body.appendChild(a);
                  class Undefined extends HTMLElement {}
                  let err = 'none';
                  try { new Undefined(); } catch (x) { err = x.constructor.name + ': ' + x.message; }
                  return [Object.prototype.toString.call(a), a.made, a.localName, b === a, b instanceof MyEl,
                    document.body.lastChild === a, a.outerHTML, err].join('|');
                })()
                """));
        Assert.Equal(
            "true|true|1",
            Eval(fixture.Runtime, """
                (() => {
                  const x = document.querySelector('late-el');
                  const before = x instanceof HTMLElement;
                  class LateEl extends HTMLElement { constructor() { super(); this.up = 1; } }
                  customElements.define('late-el', LateEl);
                  return [before, x instanceof LateEl, x.up].join('|');
                })()
                """));
    }

    [Fact]
    public void AnIframeDocumentsOpenReturnsTheDocument()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        // Akamai mPulse's loader: iframe.contentWindow.document.open()._l = function () {...}.
        Assert.Equal(
            "true|function",
            Eval(fixture.Runtime, """
                (() => {
                  const f = document.createElement('iframe');
                  document.body.appendChild(f);
                  const d = f.contentWindow.document;
                  const opened = d.open();
                  opened._l = function () {};
                  d.close();
                  return [opened === d, typeof d._l].join('|');
                })()
                """));
    }
}
