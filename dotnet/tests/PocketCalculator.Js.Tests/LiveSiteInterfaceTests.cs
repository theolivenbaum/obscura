using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Interface members whose absence stopped a live site from booting, against Chromium 141
/// (headless, Playwright; every expected value below was measured there).
/// </summary>
public sealed class LiveSiteInterfaceTests
{
    private static string Eval(PocketCalculatorJsRuntime runtime, string expression) =>
        runtime.Evaluate(expression)!.GetValue<string>();

    /// <summary>
    /// duolingo.com's inline browser check requires <c>"isIntersecting" in
    /// IntersectionObserverEntry.prototype</c>; the port's entry interface was an empty class, so
    /// the page went to /errors/not-supported.html (and then to /errors/404.html).
    /// </summary>
    [Fact]
    public void IntersectionObserverEntryIsAnInterfaceWithPrototypeAccessors()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        Assert.Equal(
            "time:gec,rootBounds:gec,boundingClientRect:gec,intersectionRect:gec,isIntersecting:gec,isVisible:gec,intersectionRatio:gec,target:gec"
            + "|TypeError: Failed to construct 'IntersectionObserverEntry': Illegal constructor|true|[object IntersectionObserverEntry]",
            Eval(fixture.Runtime, """
                (() => {
                  const P = IntersectionObserverEntry.prototype;
                  const descs = Object.getOwnPropertyNames(P).filter((n) => n !== 'constructor').map((n) => {
                    const d = Object.getOwnPropertyDescriptor(P, n);
                    return n + ':' + ('value' in d ? 'v' : 'g' + (d.set ? 's' : '')) + (d.enumerable ? 'e' : '') + (d.configurable ? 'c' : '');
                  });
                  let ctor = 'ok';
                  try { new IntersectionObserverEntry({}); } catch (e) { ctor = e.constructor.name + ': ' + e.message; }
                  const supports = 'IntersectionObserver' in window && 'IntersectionObserverEntry' in window
                    && 'intersectionRatio' in IntersectionObserverEntry.prototype && 'isIntersecting' in IntersectionObserverEntry.prototype;
                  return [descs.join(','), ctor, supports, Object.prototype.toString.call(P)].join('|');
                })()
                """));
    }

    [Fact]
    public async Task IntersectionObserverDeliversEntryInstances()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body><div id=t style='height:10px'></div></body></html>");
        fixture.Runtime.Evaluate("""
            new IntersectionObserver((es, io) => {
              io.disconnect();
              const e = es[0];
              globalThis.got = [Object.getPrototypeOf(e) === IntersectionObserverEntry.prototype,
                Object.getOwnPropertyNames(e).length, JSON.stringify(e),
                Object.prototype.toString.call(e.boundingClientRect), e.boundingClientRect instanceof DOMRectReadOnly,
                Object.prototype.toString.call(e.rootBounds), Object.prototype.toString.call(e.intersectionRect),
                e.isVisible, typeof e.time, e.target === document.getElementById('t')].join('|');
            }).observe(document.getElementById('t'));
            """);
        await fixture.Runtime.RunEventLoopBoundedAsync(500);
        Assert.Equal(
            "true|0|{}|[object DOMRectReadOnly]|true|[object DOMRectReadOnly]|[object DOMRectReadOnly]|false|number|true",
            Eval(fixture.Runtime, "String(globalThis.got)"));
    }

    /// <summary>
    /// Reflected content attributes. mail.ru's Svelte hydration calls
    /// <c>source.srcset.split(",")</c> on a &lt;picture&gt;'s &lt;source&gt;; the port answered
    /// undefined and the page never rendered its content.
    /// </summary>
    [Fact]
    public void ReflectedAttributesMatchChromium()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        Assert.Equal(
            """["a.png 1x, b.png 2x","(min-width: 1px)",12,0,7,20,"dv",true,true,"abc",3,-1,"use-credentials",true,"style","anonymous","text/plain","text/plain",-3,"https://example.com/y",2,"B","B",1,"B",1,"LB","A","open",0,null,0,true,true,"IndexSizeError: Failed to set the 'maxLength' property on 'HTMLInputElement': The value provided (-2) is not positive or 0.","IndexSizeError: Failed to set the 'size' property on 'HTMLInputElement': The value provided is 0, which is an invalid size.",1,3,"5",false]""",
            Eval(fixture.Runtime, """
                (() => {
                  document.body.innerHTML = '<picture><source srcset="a.png 1x, b.png 2x" media="(min-width: 1px)" width="12" height="x"></picture>'
                    + '<input id=i maxlength=" 7" size="0" value="dv" checked readonly><textarea id=t>abc</textarea>'
                    + '<script id=s crossorigin="USE-CREDENTIALS" nomodule></' + 'script><link id=l as="STYLE" crossorigin>'
                    + '<form id=f enctype="TEXT/PLAIN"></form><ol id=o start="-3x"></ol><blockquote id=q cite="HTTPS://Example.com/x/../y"></blockquote>'
                    + '<select id=sel><option id=o1>A</option><optgroup><option name=n2 selected label=LB>B</option></optgroup></select>'
                    + '<template id=tp shadowrootmode="OPEN"></template>' + '<img id=im hspace="99999999999">';
                  const $ = (id) => document.getElementById(id);
                  const src = document.querySelector('source');
                  const opts = document.querySelectorAll('option');
                  const out = [src.srcset, src.media, src.width, src.height, $('i').maxLength, $('i').size, $('i').defaultValue,
                    $('i').defaultChecked, $('i').readOnly, $('t').defaultValue, $('t').textLength, $('t').maxLength,
                    $('s').crossOrigin, $('s').noModule, $('l').as, $('l').crossOrigin, $('f').enctype, $('f').encoding,
                    $('o').start, $('q').cite, $('sel').length, $('sel').item(1).text,
                    $('sel').namedItem('n2').text, $('sel').selectedOptions.length, $('sel').selectedOptions[0].text,
                    opts[1].index, opts[1].label, opts[0].label, $('tp').shadowRootMode, $('im').hspace,
                    document.createTextNode('x').nextElementSibling, document.createDocumentFragment().childElementCount,
                    $('i').hasAttributeNS(null, 'size'), $('i').webkitMatchesSelector('input')];
                  try { $('i').maxLength = -2; out.push('nothrow'); } catch (e) { out.push(e.name + ': ' + e.message); }
                  try { $('i').size = 0; out.push('nothrow'); } catch (e) { out.push(e.name + ': ' + e.message); }
                  $('sel').length = 1; out.push($('sel').options.length);
                  $('sel').length = 3; out.push($('sel').options.length);
                  src.width = 5; out.push(src.getAttribute('width'));
                  $('s').crossOrigin = null; out.push($('s').hasAttribute('crossorigin'));
                  return JSON.stringify(out);
                })()
                """));
    }

    [Fact]
    public void ReflectedAttributesLiveOnTheirInterfaces()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        Assert.Equal(
            "true|true|false|false|true|gsec",
            Eval(fixture.Runtime, """
                (() => {
                  const d = Object.getOwnPropertyDescriptor(HTMLSourceElement.prototype, 'srcset');
                  return [Object.hasOwn(HTMLSourceElement.prototype, 'srcset'), Object.hasOwn(HTMLSourceElement.prototype, 'media'),
                    'srcset' in document.createElement('div'), 'maxLength' in document.createElement('div'),
                    Object.hasOwn(HTMLInputElement.prototype, 'maxLength'),
                    (d.get ? 'g' : '') + (d.set ? 's' : '') + (d.enumerable ? 'e' : '') + (d.configurable ? 'c' : '')].join('|');
                })()
                """));
    }
}
