using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// WebIDL property shapes the shim got wrong: members that were instance data properties,
/// non-enumerable class members, Array-subclass collections, constants that were writable or
/// static getters, and window attributes that were data properties. Scripts read members
/// through their descriptors (Transcend's airgap.js runs
/// <c>Object.getOwnPropertyDescriptor(HTMLCollection.prototype, "length").get</c> and some
/// sixty more at load, which threw on grammarly.com and mozilla.org). Every expected value was
/// measured in Chromium 141.0.7390.37 (headless, Playwright).
/// </summary>
public sealed class WebIdlDescriptorTests
{
    private static string Eval(PocketCalculatorJsRuntime runtime, string expression) =>
        runtime.Evaluate(expression)!.GetValue<string>();

    /// <summary>
    /// Descriptor kind (g = getter, s = setter, f = function value, v = other value) and the
    /// E/C/W flags of members scripts commonly feature-test, on the interface prototype
    /// (<c>I.m</c>), the interface object (<c>I::m</c>) and the window.
    /// </summary>
    [Fact]
    public void CommonlyFeatureTestedMembersHaveChromiumsDescriptors()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body><p>x</p></body></html>");
        Assert.Equal(
            """
            HTMLCollection.length=gEC
            HTMLCollection.item=fECW
            HTMLCollection.namedItem=fECW
            NodeList.length=gEC
            NodeList.item=fECW
            NodeList.forEach=fECW
            DOMRectList.length=gEC
            Node.parentNode=gEC
            Node.firstChild=gEC
            Node.childNodes=gEC
            Node.nodeType=gEC
            Node.baseURI=gEC
            Node.isConnected=gEC
            Node.ownerDocument=gEC
            Node.textContent=gsEC
            Node.appendChild=fECW
            Node.insertBefore=fECW
            Node.contains=fECW
            Node.ELEMENT_NODE=vE
            Element.tagName=gEC
            Element.namespaceURI=gEC
            Element.innerHTML=gsEC
            Element.outerHTML=gsEC
            Element.className=gsEC
            Element.classList=gsEC
            Element.id=gsEC
            Element.setAttribute=fECW
            Element.getAttribute=fECW
            Element.attachShadow=fECW
            Element.firstElementChild=gEC
            HTMLElement.dataset=gEC
            HTMLElement.click=fECW
            HTMLElement.style=gsEC
            SVGElement.dataset=gEC
            SVGElement.className=gEC
            ShadowRoot.innerHTML=gsEC
            DocumentFragment.innerHTML=missing
            Document.cookie=gsEC
            Document.body=gsEC
            Document.head=gEC
            Document.documentElement=gEC
            Document.readyState=gEC
            Document.currentScript=gEC
            Document.defaultView=gEC
            Document.implementation=gEC
            Document.createElement=fECW
            Document.adoptedStyleSheets=gsEC
            EventTarget.addEventListener=fECW
            Event.preventDefault=fECW
            Event.AT_TARGET=vE
            HTMLScriptElement.src=gsEC
            HTMLImageElement.src=gsEC
            HTMLImageElement.currentSrc=gEC
            HTMLIFrameElement.src=gsEC
            HTMLIFrameElement.srcdoc=gsEC
            HTMLAnchorElement.href=gsEC
            HTMLInputElement.value=gsEC
            HTMLBaseElement.href=gsEC
            HTMLMediaElement.src=gsEC
            HTMLMediaElement.HAVE_NOTHING=vE
            HTMLSelectElement.remove=fECW
            XMLHttpRequest.readyState=gEC
            XMLHttpRequest.response=gEC
            XMLHttpRequest.responseText=gEC
            XMLHttpRequest.responseType=gsEC
            XMLHttpRequest.withCredentials=gsEC
            XMLHttpRequest.status=gEC
            XMLHttpRequest.open=fECW
            XMLHttpRequest.DONE=vE
            XMLHttpRequest.onreadystatechange=gsEC
            XMLHttpRequestEventTarget.onload=gsEC
            Request.url=gEC
            Request.method=gEC
            Request.headers=gEC
            Response.status=gEC
            Response.ok=gEC
            Response.url=gEC
            Response.json=fECW
            Attr.name=gEC
            Attr.value=gsEC
            Attr.ownerElement=gEC
            ValidityState.valid=gEC
            DOMException.name=gEC
            DOMException.message=gEC
            DOMException.code=gEC
            DOMException.NOT_FOUND_ERR=vE
            Navigator.userAgent=gEC
            Navigator.onLine=gEC
            Navigator.languages=gEC
            Navigator.sendBeacon=fECW
            Range.commonAncestorContainer=gEC
            Range.START_TO_END=vE
            CSSRule.STYLE_RULE=vE
            CSSStyleRule.style=gsEC
            CSSMediaRule.media=gsEC
            MutationObserver.observe=fECW
            URLSearchParams.size=gEC
            URLSearchParams.get=fECW
            MessagePort.postMessage=fECW
            History.replaceState=fECW
            DOMParser.parseFromString=fECW
            XMLSerializer.serializeToString=fECW
            VTTCue.getCueAsHTML=fECW
            TextTrackCue.getCueAsHTML=missing
            PerformanceResourceTiming.toJSON=fECW
            Node::ELEMENT_NODE=vE
            Range::START_TO_START=vE
            XMLHttpRequest::DONE=vE
            Notification::permission=gEC
            PerformanceObserver::supportedEntryTypes=gEC
            URL::createObjectURL=fECW
            window.window=gE
            window.self=gsEC
            window.document=gE
            window.top=gE
            window.parent=gsEC
            window.frames=gsEC
            window.length=gsEC
            window.closed=gEC
            window.location=gsE
            window.navigator=gEC
            window.history=gEC
            window.localStorage=gEC
            window.sessionStorage=gEC
            window.customElements=gEC
            window.performance=gsEC
            window.screen=gsEC
            window.innerWidth=gsEC
            window.innerHeight=gsEC
            window.devicePixelRatio=gsEC
            window.scrollX=gsEC
            window.pageYOffset=gsEC
            window.visualViewport=gsEC
            window.crypto=gEC
            window.indexedDB=gEC
            window.opener=gsEC
            window.frameElement=gEC
            window.origin=gsEC
            window.isSecureContext=gEC
            """.ReplaceLineEndings("\n"),
            Eval(fixture.Runtime, """
                (() => {
                  const d2s = (d) => d ? (d.get ? 'g' : '') + (d.set ? 's' : '') + ('value' in d ? (typeof d.value === 'function' ? 'f' : 'v') : '')
                    + (d.enumerable ? 'E' : '') + (d.configurable ? 'C' : '') + (d.writable ? 'W' : '') : 'missing';
                  const list = [
                    ['HTMLCollection', 'length'], ['HTMLCollection', 'item'], ['HTMLCollection', 'namedItem'],
                    ['NodeList', 'length'], ['NodeList', 'item'], ['NodeList', 'forEach'], ['DOMRectList', 'length'],
                    ['Node', 'parentNode'], ['Node', 'firstChild'], ['Node', 'childNodes'], ['Node', 'nodeType'], ['Node', 'baseURI'],
                    ['Node', 'isConnected'], ['Node', 'ownerDocument'], ['Node', 'textContent'], ['Node', 'appendChild'],
                    ['Node', 'insertBefore'], ['Node', 'contains'], ['Node', 'ELEMENT_NODE'],
                    ['Element', 'tagName'], ['Element', 'namespaceURI'], ['Element', 'innerHTML'], ['Element', 'outerHTML'],
                    ['Element', 'className'], ['Element', 'classList'], ['Element', 'id'], ['Element', 'setAttribute'],
                    ['Element', 'getAttribute'], ['Element', 'attachShadow'], ['Element', 'firstElementChild'],
                    ['HTMLElement', 'dataset'], ['HTMLElement', 'click'], ['HTMLElement', 'style'], ['SVGElement', 'dataset'],
                    ['SVGElement', 'className'], ['ShadowRoot', 'innerHTML'], ['DocumentFragment', 'innerHTML'],
                    ['Document', 'cookie'], ['Document', 'body'], ['Document', 'head'], ['Document', 'documentElement'],
                    ['Document', 'readyState'], ['Document', 'currentScript'], ['Document', 'defaultView'],
                    ['Document', 'implementation'], ['Document', 'createElement'], ['Document', 'adoptedStyleSheets'],
                    ['EventTarget', 'addEventListener'], ['Event', 'preventDefault'], ['Event', 'AT_TARGET'],
                    ['HTMLScriptElement', 'src'], ['HTMLImageElement', 'src'], ['HTMLImageElement', 'currentSrc'],
                    ['HTMLIFrameElement', 'src'], ['HTMLIFrameElement', 'srcdoc'], ['HTMLAnchorElement', 'href'],
                    ['HTMLInputElement', 'value'], ['HTMLBaseElement', 'href'], ['HTMLMediaElement', 'src'],
                    ['HTMLMediaElement', 'HAVE_NOTHING'], ['HTMLSelectElement', 'remove'],
                    ['XMLHttpRequest', 'readyState'], ['XMLHttpRequest', 'response'], ['XMLHttpRequest', 'responseText'],
                    ['XMLHttpRequest', 'responseType'], ['XMLHttpRequest', 'withCredentials'], ['XMLHttpRequest', 'status'],
                    ['XMLHttpRequest', 'open'], ['XMLHttpRequest', 'DONE'], ['XMLHttpRequest', 'onreadystatechange'],
                    ['XMLHttpRequestEventTarget', 'onload'],
                    ['Request', 'url'], ['Request', 'method'], ['Request', 'headers'], ['Response', 'status'], ['Response', 'ok'],
                    ['Response', 'url'], ['Response', 'json'],
                    ['Attr', 'name'], ['Attr', 'value'], ['Attr', 'ownerElement'], ['ValidityState', 'valid'],
                    ['DOMException', 'name'], ['DOMException', 'message'], ['DOMException', 'code'], ['DOMException', 'NOT_FOUND_ERR'],
                    ['Navigator', 'userAgent'], ['Navigator', 'onLine'], ['Navigator', 'languages'], ['Navigator', 'sendBeacon'],
                    ['Range', 'commonAncestorContainer'], ['Range', 'START_TO_END'], ['CSSRule', 'STYLE_RULE'],
                    ['CSSStyleRule', 'style'], ['CSSMediaRule', 'media'], ['MutationObserver', 'observe'],
                    ['URLSearchParams', 'size'], ['URLSearchParams', 'get'], ['MessagePort', 'postMessage'],
                    ['History', 'replaceState'], ['DOMParser', 'parseFromString'], ['XMLSerializer', 'serializeToString'],
                    ['VTTCue', 'getCueAsHTML'], ['TextTrackCue', 'getCueAsHTML'], ['PerformanceResourceTiming', 'toJSON'],
                  ];
                  const ctor = [['Node', 'ELEMENT_NODE'], ['Range', 'START_TO_START'], ['XMLHttpRequest', 'DONE'],
                    ['Notification', 'permission'], ['PerformanceObserver', 'supportedEntryTypes'], ['URL', 'createObjectURL']];
                  const win = ['window', 'self', 'document', 'top', 'parent', 'frames', 'length', 'closed', 'location', 'navigator',
                    'history', 'localStorage', 'sessionStorage', 'customElements', 'performance', 'screen', 'innerWidth',
                    'innerHeight', 'devicePixelRatio', 'scrollX', 'pageYOffset', 'visualViewport', 'crypto', 'indexedDB',
                    'opener', 'frameElement', 'origin', 'isSecureContext'];
                  return list.map(([i, k]) => i + '.' + k + '=' + d2s(Object.getOwnPropertyDescriptor(globalThis[i].prototype, k)))
                    .concat(ctor.map(([i, k]) => i + '::' + k + '=' + d2s(Object.getOwnPropertyDescriptor(globalThis[i], k))))
                    .concat(win.map((k) => 'window.' + k + '=' + d2s(Object.getOwnPropertyDescriptor(window, k))))
                    .join('\n');
                })()
                """));
    }

    /// <summary>
    /// The reads Transcend's airgap.js makes at load, each of which threw "Cannot read
    /// properties of undefined (reading 'get')" or "e is not a constructor" on grammarly.com
    /// and mozilla.org: HTMLCollection's length getter, window.closed and window.document
    /// getters, Request's url, XMLHttpRequest's response, Attr's name and ownerElement,
    /// ValidityState's valid, and a SecurityPolicyViolationEvent built to take isTrusted.
    /// </summary>
    [Fact]
    public void TranscendAirgapDescriptorReadsSucceed()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body><p>a</p><p>b</p></body></html>");
        Assert.Equal("2|false|true|https://a.example/x|0||x||true|false|securitypolicyviolation", Eval(fixture.Runtime, """
            (() => {
              const H = (I, k) => Object.getOwnPropertyDescriptor(I.prototype, k);
              const xhr = new XMLHttpRequest();
              const attr = document.createAttribute('x');
              const ev = new SecurityPolicyViolationEvent('securitypolicyviolation');
              return [
                H(HTMLCollection, 'length').get.call(document.body.children),
                Object.getOwnPropertyDescriptor(window, 'closed').get.call(window),
                Object.getOwnPropertyDescriptor(window, 'document').get.call(window) === document,
                H(Request, 'url').get.call(new Request('https://a.example/x')),
                H(XMLHttpRequest, 'readyState').get.call(xhr),
                H(XMLHttpRequest, 'response').get.call(xhr),
                H(Attr, 'name').get.call(attr),
                H(Attr, 'ownerElement').get.call(attr),
                H(ValidityState, 'valid').get.call(document.createElement('input').validity),
                ev.isTrusted,
                ev.type,
              ].join('|');
            })()
            """));
    }

    [Fact]
    public void CollectionsAreArrayLikesNotArrays()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body><p id=x>a</p><p>b</p></body></html>");
        // Chromium: no own length, iteration and forEach are Array.prototype's, and a length
        // getter off an instance throws Illegal invocation.
        Assert.Equal(
            "false|0,1|2|x|true|false|0,1|true|true|2|1|Illegal invocation|[object HTMLCollection]|[object NodeList]",
            Eval(fixture.Runtime, """
                (() => {
                  const c = document.body.children;
                  const nl = document.querySelectorAll('p');
                  let thrown = '';
                  try { Object.getOwnPropertyDescriptor(NodeList.prototype, 'length').get.call({}); }
                  catch (e) { thrown = e.message; }
                  return [
                    Array.isArray(c), Object.keys(c).join(','), [...c].length, c.x.id,
                    HTMLCollection.prototype[Symbol.iterator] === Array.prototype.values,
                    Array.isArray(nl), Object.keys(nl).join(','),
                    NodeList.prototype.forEach === Array.prototype.forEach,
                    NodeList.prototype[Symbol.iterator] === Array.prototype.values,
                    [...nl.entries()].length, document.body.getClientRects().length, thrown,
                    Object.prototype.toString.call(c), Object.prototype.toString.call(nl),
                  ].join('|');
                })()
                """));
    }

    [Fact]
    public void InterfaceMembersEnumerateAndConstantsAreReadOnly()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        Assert.Equal("true|true|true|true|1|1|0|1|appendChild,click,innerHTML", Eval(fixture.Runtime, """
            (() => {
              const seen = [];
              for (const k in document.createElement('div')) if (k === 'appendChild' || k === 'click' || k === 'innerHTML') seen.push(k);
              Node.ELEMENT_NODE = 7; Node.prototype.ELEMENT_NODE = 7;
              Range.START_TO_END = 9; CSSRule.prototype.STYLE_RULE = 9;
              return [
                Object.keys(Node.prototype).includes('appendChild'),
                Object.keys(EventTarget.prototype).includes('addEventListener'),
                Object.keys(HTMLElement.prototype).includes('click'),
                Object.keys(Document.prototype).includes('body'),
                Node.ELEMENT_NODE, Node.prototype.ELEMENT_NODE,
                HTMLMediaElement.prototype.HAVE_NOTHING, Range.START_TO_END,
                seen.sort().join(','),
              ].join('|');
            })()
            """));
        Assert.Equal("1", Eval(fixture.Runtime, "String(CSSRule.prototype.STYLE_RULE)"));
    }

    /// <summary>
    /// Members that sat on another interface or had another shape than Chromium's, with the
    /// behaviour that comes with the move.
    /// </summary>
    [Fact]
    public void MovedMembersBehaveAsChromiums()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        Assert.Equal(
            "|NotFoundError: boom|8|Illegal invocation|Error||"
            + "[object SVGAnimatedString]|[object SVGAnimatedString]|a b|"
            + "true|true|1,false|<b>x</b>,false|function,false|"
            + "HierarchyRequestError|nb,2|"
            + "color: red;|print|true",
            Eval(fixture.Runtime, """
                (() => {
                  const e = new DOMException('boom', 'NotFoundError');
                  let illegal = '';
                  try { Object.getOwnPropertyDescriptor(DOMException.prototype, 'name').get.call({}); }
                  catch (x) { illegal = x.message; }
                  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                  svg.setAttribute('class', 'a b');
                  const cn = svg.className;
                  const sel = document.createElement('select');
                  sel.innerHTML = '<option>a<option>b';
                  document.body.append(sel);
                  sel.remove(0);
                  const left = sel.options.length;
                  sel.remove();
                  const root = document.createElement('div').attachShadow({ mode: 'open' });
                  root.innerHTML = '<b>x</b>';
                  let bodyError = '';
                  try { document.body = document.createElement('div'); } catch (x) { bodyError = x.name; }
                  const nb = document.createElement('body'); nb.id = 'nb'; document.body = nb;
                  const sheet = new CSSStyleSheet();
                  sheet.insertRule('p { color: blue }');
                  sheet.insertRule('@media screen { p { color: blue } }', 1);
                  sheet.cssRules[0].style = 'color: red';
                  sheet.cssRules[1].media = 'print';
                  return [
                    Object.getOwnPropertyNames(e).join(','), String(e), e.code, illegal, new DOMException().name,
                    new DOMException().message,
                    Object.prototype.toString.call(cn), String(cn), cn.animVal,
                    PerformanceObserver.supportedEntryTypes === PerformanceObserver.supportedEntryTypes,
                    Object.isFrozen(PerformanceObserver.supportedEntryTypes),
                    [left, sel.isConnected].join(','),
                    [root.innerHTML, 'innerHTML' in document.createDocumentFragment()].join(','),
                    [typeof VTTCue.prototype.getCueAsHTML, 'getCueAsHTML' in TextTrackCue.prototype].join(','),
                    bodyError, [document.body.id, document.documentElement.children.length].join(','),
                    sheet.cssRules[0].style.cssText, sheet.cssRules[1].media.mediaText,
                    typeof Object.getOwnPropertyDescriptor(Navigator.prototype, 'onLine').set === 'undefined',
                  ].join('|');
                })()
                """));
    }

    /// <summary>
    /// Document.prototype's members called on a createHTMLDocument document act on that
    /// document. They used to reach the page's own document: airgap.js's sanitizing
    /// open/write/close on such a sandbox replaced grammarly.com's whole page.
    /// </summary>
    [Fact]
    public void DocumentPrototypeMembersOnAParsedDocumentStayInIt()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body><p>page</p></body></html>");
        Assert.Equal("true|true|true|written|true|true|http://www.w3.org/1999/xhtml", Eval(fixture.Runtime, """
            (() => {
              const D = Document.prototype;
              const g = (n) => Object.getOwnPropertyDescriptor(D, n).get;
              const d = document.implementation.createHTMLDocument('');
              const before = document.body.innerHTML;
              D.open.call(d);
              D.write.call(d, '<!doctype html><html><head><title>t</title></head><body><p id=w>written</p></body></html>');
              D.close.call(d);
              return [
                g('documentElement').call(d) === d.documentElement,
                g('body').call(d) === d.body,
                g('head').call(d) === d.head,
                d.querySelector('#w') ? d.querySelector('#w').textContent : 'none',
                document.body.innerHTML === before,
                document.querySelector('#w') === null,
                Node.prototype.lookupNamespaceURI.call(d, null),
              ].join('|');
            })()
            """));
    }

    [Fact]
    public void WindowAttributesAreAccessors()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        // [Replaceable] innerWidth takes the assigned value; the read-only and unforgeable
        // ones ignore assignment (sloppy mode) and keep their accessor.
        Assert.Equal("true|true|1234|true|true|false|true", Eval(fixture.Runtime, """
            (() => {
              const doc = document, nav = navigator;
              (0, eval)('window.document = 1; window.navigator = 2; window.innerWidth = 1234; window.closed = true;');
              return [
                document === doc, navigator === nav, innerWidth, closed === false,
                typeof Object.getOwnPropertyDescriptor(window, 'document').get === 'function',
                Object.getOwnPropertyDescriptor(window, 'top').configurable,
                Object.getOwnPropertyDescriptor(location, 'href').configurable === false,
              ].join('|');
            })()
            """));
    }
}
