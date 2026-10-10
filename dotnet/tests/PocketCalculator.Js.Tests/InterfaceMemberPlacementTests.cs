using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Members the shim kept on Element.prototype after the HTML interfaces became distinct, and the
/// node-level fixes found with them on component sites (YouTube's Polymer, MSN's FAST). Every
/// expected value was measured in Chromium 141.0.7390.37 (headless, Playwright).
/// </summary>
public sealed class InterfaceMemberPlacementTests
{
    private static string Eval(PocketCalculatorJsRuntime runtime, string expression) =>
        runtime.Evaluate(expression)!.GetValue<string>();

    [Fact]
    public void ElementPrototypeHasNoInterfaceMembers()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        // A sample of Element.prototype's former extras: none is Element's in Chromium.
        Assert.Equal("", Eval(fixture.Runtime, """
            ['href', 'src', 'value', 'disabled', 'type', 'name', 'options', 'form', 'checked', 'sandbox',
             'text', 'open', 'content', 'labels', 'files', 'target', 'rel', 'relList', 'download', 'action',
             'select', 'submit', 'reset', 'show', 'close', 'sheet', 'validity', 'checkValidity',
             'contentWindow', 'origin', 'host', 'getBBox', 'selectionStart', 'selected', 'placeholder']
              .filter((k) => Object.prototype.hasOwnProperty.call(Element.prototype, k)
                || k in document.createElement('div')).join(',')
            """));
    }

    [Fact]
    public void MembersSitOnChromiumsInterfacesWithItsDescriptors()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        Assert.Equal(
            "HTMLIFrameElement.sandbox:gsEC|HTMLSelectElement.options:gEC|HTMLDataListElement.options:gEC|"
            + "HTMLAnchorElement.href:gsEC|HTMLLinkElement.href:gsEC|HTMLInputElement.value:gsEC|"
            + "HTMLOptionElement.selected:gsEC|HTMLFormElement.submit:vECW|HTMLDialogElement.close:vECW|"
            + "HTMLInputElement.select:vECW|HTMLTextAreaElement.setSelectionRange:vECW|"
            + "HTMLAnchorElement.relList:gsEC|HTMLFormElement.relList:gsEC|HTMLLinkElement.sizes:gsEC|"
            + "HTMLImageElement.sizes:gsEC|HTMLOutputElement.htmlFor:gsEC|HTMLScriptElement.htmlFor:gsEC|"
            + "HTMLTemplateElement.content:gEC|HTMLMetaElement.content:gsEC|HTMLSelectElement.type:gEC|"
            + "Element.classList:gsEC|HTMLSlotElement.assignedNodes:vECW|Element.assignedSlot:gEC|"
            + "Text.assignedSlot:gEC|SVGAElement.href:gEC|SVGGraphicsElement.getBBox:vECW|"
            + "SVGTextContentElement.getComputedTextLength:vECW",
            Eval(fixture.Runtime, """
                [['HTMLIFrameElement', 'sandbox'], ['HTMLSelectElement', 'options'], ['HTMLDataListElement', 'options'],
                 ['HTMLAnchorElement', 'href'], ['HTMLLinkElement', 'href'], ['HTMLInputElement', 'value'],
                 ['HTMLOptionElement', 'selected'], ['HTMLFormElement', 'submit'], ['HTMLDialogElement', 'close'],
                 ['HTMLInputElement', 'select'], ['HTMLTextAreaElement', 'setSelectionRange'],
                 ['HTMLAnchorElement', 'relList'], ['HTMLFormElement', 'relList'], ['HTMLLinkElement', 'sizes'],
                 ['HTMLImageElement', 'sizes'], ['HTMLOutputElement', 'htmlFor'], ['HTMLScriptElement', 'htmlFor'],
                 ['HTMLTemplateElement', 'content'], ['HTMLMetaElement', 'content'], ['HTMLSelectElement', 'type'],
                 ['Element', 'classList'], ['HTMLSlotElement', 'assignedNodes'], ['Element', 'assignedSlot'],
                 ['Text', 'assignedSlot'], ['SVGAElement', 'href'], ['SVGGraphicsElement', 'getBBox'],
                 ['SVGTextContentElement', 'getComputedTextLength']].map(([i, k]) => {
                  const d = Object.getOwnPropertyDescriptor(globalThis[i].prototype, k);
                  return i + '.' + k + ':' + (d ? (d.get ? 'g' : '') + (d.set ? 's' : '') + ('value' in d ? 'v' : '')
                    + (d.enumerable ? 'E' : '') + (d.configurable ? 'C' : '') + (d.writable ? 'W' : '') : 'missing');
                }).join('|')
                """));
    }

    [Fact]
    public void PutForwardsAttributesTakeAssignment()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        // YouTube's `iframe.sandbox = '...'` threw "which has only a getter".
        Assert.Equal(
            "allow-scripts allow-forms|2|noopener x|p q|16x16 32x32|stylesheet|a b|string:100vw|string:|q|string:|red|color: red;",
            Eval(fixture.Runtime, """
                (() => {
                  'use strict';
                  const f = document.createElement('iframe'); f.sandbox = 'allow-scripts allow-forms';
                  const a = document.createElement('a'); a.relList = 'noopener x';
                  const d = document.createElement('div'); d.classList = 'p q';
                  const l = document.createElement('link'); l.sizes = '16x16 32x32'; l.relList = 'stylesheet';
                  const o = document.createElement('output'); o.htmlFor = 'a b';
                  const img = document.createElement('img'); img.sizes = '100vw';
                  const s = document.createElement('script'); const before = typeof s.htmlFor + ':' + s.htmlFor; s.htmlFor = 'q';
                  const body = document.body; const text = typeof body.text + ':' + body.text; body.text = 'red';
                  d.style = 'color: red';
                  return [f.getAttribute('sandbox'), f.sandbox.length, a.getAttribute('rel'), d.className,
                    l.getAttribute('sizes'), l.rel, o.getAttribute('for'), typeof img.sizes + ':' + img.sizes,
                    before, s.getAttribute('for'), text, body.getAttribute('text'), d.getAttribute('style')].join('|');
                })()
                """));
    }

    [Fact]
    public void CustomElementPropertiesAreOwnProperties()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        // FAST sets `this.options = [...]` on its own element; Chromium has no options (nor value,
        // disabled, ...) on HTMLElement, so each assignment makes an own property and writes no
        // attribute.
        Assert.Equal(
            "1,2||action,checked,content,disabled,files,form,href,labels,name,open,options,placeholder,sandbox,selected,src,text,type,validity,value|",
            Eval(fixture.Runtime, """
                (() => {
                  'use strict';
                  class X extends HTMLElement {}
                  customElements.define('x-opt', X);
                  const x = new X();
                  const errs = [];
                  for (const [k, v] of [['options', [1, 2]], ['value', 'v'], ['disabled', true], ['type', 't'],
                    ['name', 'n'], ['href', 'h'], ['src', 's'], ['checked', true], ['form', 'f'], ['selected', true],
                    ['open', true], ['text', 'tx'], ['labels', 'l'], ['files', 'fl'], ['sandbox', 'sb'],
                    ['content', 'c'], ['validity', 'v'], ['action', 'a'], ['placeholder', 'p']]) {
                    try { x[k] = v; } catch (e) { errs.push(k + ': ' + e.message); }
                  }
                  return [x.options.join(','), x.getAttributeNames().join(','),
                    Object.keys(x).filter((k) => k[0] !== '_').sort().join(','), errs.join(';')].join('|');
                })()
                """));
    }

    [Fact]
    public void SvgElementsHaveTheirInterfacesMembersOnly()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        Assert.Equal(
            "SVGAElement:href,getBBox:[object SVGAnimatedString]:#q:[object SVGAnimatedString]:[object DOMTokenList]|"
            + "rect=SVGRectElement getBBox|g=SVGGElement getBBox|"
            + "text=SVGTextElement getBBox,getComputedTextLength,getSubStringLength,getExtentOfChar|"
            + "textPath=SVGTextPathElement href,getBBox,getComputedTextLength,getSubStringLength,getExtentOfChar|"
            + "image=SVGImageElement href,getBBox|use=SVGUseElement href,getBBox|linearGradient=SVGLinearGradientElement href|"
            + "script=SVGScriptElement href,type|style=SVGStyleElement disabled,type,sheet|clipPath=SVGClipPathElement |"
            + "title=SVGTitleElement ",
            Eval(fixture.Runtime, """
                (() => {
                  const ns = 'http://www.w3.org/2000/svg';
                  const keys = ['href', 'getBBox', 'getComputedTextLength', 'getSubStringLength', 'getExtentOfChar',
                    'src', 'disabled', 'type', 'sheet', 'value', 'download', 'ping', 'protocol'];
                  const a = document.createElementNS(ns, 'a');
                  a.setAttribute('href', '#q');
                  const tag = (e) => Object.prototype.toString.call(e).slice(8, -1);
                  const out = [tag(a) + ':' + keys.filter((k) => k in a).join(',') + ':' + Object.prototype.toString.call(a.href)
                    + ':' + a.href.baseVal + ':' + Object.prototype.toString.call(a.target) + ':' + Object.prototype.toString.call(a.relList)];
                  for (const t of ['rect', 'g', 'text', 'textPath', 'image', 'use', 'linearGradient', 'script', 'style', 'clipPath', 'title']) {
                    const e = document.createElementNS(ns, t);
                    out.push(t + '=' + tag(e) + ' ' + ['href', 'getBBox', 'getComputedTextLength', 'getSubStringLength',
                      'getExtentOfChar', 'src', 'disabled', 'type', 'sheet', 'target'].filter((k) => k in e).join(','));
                  }
                  return out.join('|');
                })()
                """));
    }

    [Fact]
    public void FormControlsKeepWorkingAfterTheMove()
    {
        using var fixture = RuntimeFixture.Setup("""
            <html><body><form id=f action="/go"><input id=i name=q value=hi><select id=s name=c><option>a</option><option selected>b</option></select>
            <textarea id=t>tx</textarea><button id=b>go</button></form><a id=a href="/x?y#z">l</a></body></html>
            """);
        Assert.Equal(
            "hi|b|1|tx|submit|text|select-one|textarea|http://example.com/go|http://example.com/x?y#z|/x|?y|2|q=hi&c=b",
            Eval(fixture.Runtime, """
                (() => {
                  const $ = (id) => document.getElementById(id);
                  const fd = new FormData($('f'));
                  return [$('i').value, $('s').value, $('s').selectedIndex, $('t').value, $('b').type,
                    $('i').type, $('s').type, $('t').type, $('f').action, $('a').href, $('a').pathname, $('a').search,
                    $('s').options.length, [...fd].map(([k, v]) => k + '=' + v).join('&')].join('|');
                })()
                """));
    }
}
