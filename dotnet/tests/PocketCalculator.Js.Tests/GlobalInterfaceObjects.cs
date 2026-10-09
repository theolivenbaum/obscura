using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The global interface objects bootstrap.js adds for objects the shim already hands out
/// (navigator, location, performance, document.implementation, the plugin list, records,
/// rects, stylesheet rules, ...) and the constructible ones pages feature-test. Every
/// expected value was measured in Chromium 141 (headless, Playwright) on the same markup,
/// except where a fact says the port answers as desktop Chrome does.
/// </summary>
public sealed class GlobalInterfaceObjects
{
    private const string Markup =
        "<html><head><style>@media screen and (min-width:100px), print{p{color:red}}"
        + "@supports (display:grid){p{color:blue}}@font-face{font-family:x}"
        + "@keyframes k{from{color:red}50%{color:green}to{color:blue}}@layer a, b;"
        + "@import url(x.css) screen;</style></head>"
        + "<body><p id=p>x</p><math><mi>x</mi></math></body></html>";

    private static string Eval(PocketCalculatorJsRuntime runtime, string expression) =>
        runtime.Evaluate(expression)!.GetValue<string>();

    [Fact]
    public void ExistingObjectsAreInstancesOfTheirInterfaces()
    {
        using var fixture = RuntimeFixture.Setup(Markup);
        Assert.Equal(
            "true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true",
            Eval(fixture.Runtime, """
                [Object.getPrototypeOf(navigator) === Navigator.prototype, location instanceof Location,
                 performance instanceof Performance, performance instanceof EventTarget,
                 document instanceof HTMLDocument, Object.getPrototypeOf(HTMLDocument.prototype) === Document.prototype,
                 document.implementation instanceof DOMImplementation, document.implementation === document.implementation,
                 screen instanceof Screen, screen.orientation instanceof ScreenOrientation,
                 visualViewport instanceof VisualViewport, new XMLHttpRequest().upload instanceof XMLHttpRequestUpload,
                 external instanceof External, locationbar instanceof BarProp,
                 navigator.permissions instanceof Permissions, navigator.geolocation instanceof Geolocation,
                 performance.timing instanceof PerformanceTiming, performance.navigation instanceof PerformanceNavigation].join()
                """));
        // Chromium keeps the members on the prototypes: these objects have no own properties.
        Assert.Equal("0,0,0,0,0", Eval(fixture.Runtime, """
            [navigator, performance, screen, visualViewport, document.implementation]
              .map((o) => Object.getOwnPropertyNames(o).length).join()
            """));
    }

    [Fact]
    public void InterfacesLookNativeAndRefuseConstructionAsChromiumDoes()
    {
        using var fixture = RuntimeFixture.Setup(Markup);
        Assert.Equal(
            "TypeError: Failed to construct 'Navigator': Illegal constructor|TypeError: Illegal constructor|"
            + "function Navigator() { [native code] }|0|Object|TypeError: Illegal invocation|"
            + "function get userAgent() { [native code] }|true|false",
            Eval(fixture.Runtime, """
                (() => {
                  const e = (f) => { try { f(); return 'ok'; } catch (x) { return x.constructor.name + ': ' + x.message; } };
                  return [e(() => new Navigator()), e(() => Navigator()), String(Navigator), Navigator.length,
                    Object.getPrototypeOf(Navigator.prototype).constructor.name,
                    e(() => Navigator.prototype.userAgent),
                    String(Object.getOwnPropertyDescriptor(Navigator.prototype, 'userAgent').get),
                    Object.getOwnPropertyDescriptor(Navigator.prototype, 'userAgent').enumerable,
                    Object.keys(window).includes('Navigator')].join('|');
                })()
                """));
        Assert.Equal(
            "TypeError: Failed to construct 'DOMImplementation': Illegal constructor|EventTarget|Document|"
            + "XMLHttpRequestEventTarget|Netscape,Mozilla,",
            Eval(fixture.Runtime, """
                (() => {
                  const e = (f) => { try { f(); return 'ok'; } catch (x) { return x.constructor.name + ': ' + x.message; } };
                  return [e(() => new DOMImplementation()), Object.getPrototypeOf(Performance).name,
                    Object.getPrototypeOf(HTMLDocument).name, Object.getPrototypeOf(XMLHttpRequestUpload).name,
                    [navigator.appName, navigator.appCodeName, navigator.vendorSub].join()].join('|');
                })()
                """));
    }

    [Fact]
    public void MathMLElementsAndOptionFactory()
    {
        using var fixture = RuntimeFixture.Setup(Markup);
        Assert.Equal(
            "MathMLElement,true,MathMLElement,true,Illegal constructor",
            Eval(fixture.Runtime, """
                [document.querySelector('math').constructor.name, document.querySelector('mi') instanceof MathMLElement,
                 document.createElementNS('http://www.w3.org/1998/Math/MathML', 'mi').constructor.name,
                 Object.getPrototypeOf(MathMLElement.prototype) === Element.prototype,
                 (() => { try { new MathMLElement(); } catch (e) { return e.message.replace(/^.*: /, ''); } })()].join()
                """));
        Assert.Equal(
            "HTMLOptionElement|t|v|false|<option value=\"v\">t</option>|<option></option>|true|0",
            Eval(fixture.Runtime, """
                (() => {
                  const o = new Option('t', 'v', false, false), b = new Option();
                  return [o.constructor.name, o.text, o.value, o.selected, o.outerHTML, b.outerHTML,
                    Option.prototype === HTMLOptionElement.prototype, Option.length].join('|');
                })()
                """));
    }

    [Fact]
    public void PluginListIsChromiumsPdfList()
    {
        using var fixture = RuntimeFixture.Setup(Markup);
        // Desktop Chrome's fixed list: five PDF plugins, each with two MIME types.
        Assert.Equal(
            "5,2,application/pdf,true,true,true,true,false,PDF Viewer,true,0,1,2,3,4",
            Eval(fixture.Runtime, """
                [navigator.plugins.length, navigator.plugins[0].length, navigator.plugins[0][0].type,
                 navigator.mimeTypes[0].enabledPlugin === navigator.plugins[0],
                 navigator.plugins instanceof PluginArray, navigator.plugins[0] instanceof Plugin,
                 navigator.plugins === navigator.plugins, Array.isArray(navigator.plugins),
                 navigator.plugins.item(0).name, navigator.plugins['PDF Viewer'] === navigator.plugins[0],
                 Object.keys(navigator.plugins).join()].join()
                """));
    }

    [Fact]
    public async Task RecordsIteratorsAndXPathResultsAreInterfaceInstances()
    {
        using var fixture = RuntimeFixture.Setup(Markup);
        var rt = fixture.Runtime;
        rt.ExecuteScript("records", """
            globalThis.__rec = null;
            new MutationObserver((recs) => {
              __rec = [recs[0] instanceof MutationRecord, Object.getOwnPropertyNames(recs[0]).length, recs[0].type,
                recs[0].addedNodes.length, recs[0].attributeNamespace, Object.prototype.toString.call(recs[0])].join();
            }).observe(document.body, { childList: true });
            document.body.appendChild(document.createElement('i'));
            """);
        await EventLoopWait.UntilIdleAsync(rt);
        Assert.Equal("true,0,childList,1,,[object MutationRecord]", Eval(rt, "__rec"));
        Assert.Equal("true,0,BODY,true,1,BODY/P/MATH/MI/I", Eval(rt, """
            (() => {
              const it = document.createNodeIterator(document.body, NodeFilter.SHOW_ELEMENT);
              const out = []; let n;
              while ((n = it.nextNode())) out.push(n.nodeName);
              return [it instanceof NodeIterator, Object.getOwnPropertyNames(it).length, it.root.nodeName,
                it.pointerBeforeReferenceNode === false, it.whatToShow, out.join('/').toUpperCase()].join();
            })()
            """));
        Assert.Equal("true,true,1,p,1", Eval(rt, """
            [document.createTreeWalker(document.body) instanceof TreeWalker,
             document.evaluate('//p', document, null, 7, null) instanceof XPathResult,
             document.evaluate('//p', document, null, 7, null).snapshotLength,
             document.evaluate('//p', document, null, 9, null).singleNodeValue.id,
             new XPathEvaluator().createExpression('//p').evaluate(document, 7, null).snapshotLength].join()
            """));
    }

    [Fact]
    public void GeometryInterfaces()
    {
        using var fixture = RuntimeFixture.Setup(Markup);
        var rt = fixture.Runtime;
        Assert.Equal(
            """{"x":1,"y":2,"width":3,"height":-4,"top":-2,"right":4,"bottom":2,"left":1}|true|true|0|NaN""",
            Eval(rt, """
                [JSON.stringify(new DOMRect(1, 2, 3, -4)),
                 document.body.getBoundingClientRect() instanceof DOMRect,
                 Object.getPrototypeOf(DOMRect.prototype) === DOMRectReadOnly.prototype,
                 Object.getOwnPropertyNames(document.body.getBoundingClientRect()).length,
                 new DOMRect(1, 1, NaN, 3).left].join('|')
                """));
        Assert.Equal(
            "matrix(1, 0, 0, 1, 0, 0)|matrix(2, 0, 0, 2, 10, 20)|matrix(0, 1, -1, 0, 0, 0)|"
            + "matrix(-2, 1, 1.5, -0.5, 1, -2)|matrix3d(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 1, 2, 3, 1)|"
            + "{\"x\":11,\"y\":22,\"z\":0,\"w\":1}|true|true|false",
            Eval(rt, """
                [String(new DOMMatrix()), String(new DOMMatrix('translate(10px, 20px) scale(2)')),
                 String(new DOMMatrix('rotate(90deg)')), String(new DOMMatrix([1, 2, 3, 4, 5, 6]).inverse()),
                 String(new DOMMatrix().translate(1, 2, 3)),
                 JSON.stringify(new DOMPoint(1, 2).matrixTransform(new DOMMatrix().translate(10, 20))),
                 WebKitCSSMatrix === DOMMatrix,
                 document.createElement('canvas').getContext('2d').getTransform() instanceof DOMMatrix,
                 new DOMMatrix().rotate(0, 10).is2D].join('|')
                """));
        Assert.Equal(
            "SyntaxError:Failed to construct 'DOMMatrix': Values must be resolvable at parse time",
            Eval(rt, "(() => { try { new DOMMatrix('translate(1em)'); } catch (e) { return e.name + ':' + e.message; } })()"));
    }

    [Fact]
    public void StylesheetRulesHaveTheirCssomTypes()
    {
        using var fixture = RuntimeFixture.Setup(Markup);
        Assert.Equal(
            string.Join("\n",
                "CSSMediaRule:4:@media screen and (min-width: 100px), print {\n  p { color: red; }\n}",
                "CSSSupportsRule:12:@supports (display:grid) {\n  p { color: blue; }\n}",
                "CSSFontFaceRule:5:@font-face { font-family: x; }",
                "CSSKeyframesRule:7:@keyframes k { \n  0% { color: red; }\n  50% { color: green; }\n  100% { color: blue; }\n}",
                "CSSLayerStatementRule:0:@layer a, b;",
                "CSSImportRule:3:@import url(\"x.css\") screen;"),
            Eval(fixture.Runtime, """
                Array.from(document.styleSheets[0].cssRules, (r) => r.constructor.name + ':' + r.type + ':' + r.cssText).join('\n')
                """));
        Assert.Equal(
            "true,MediaList,screen and (min-width: 100px), print,2,true,true,,,TypeError: Failed to construct 'CSSRule': Illegal constructor",
            Eval(fixture.Runtime, """
                (() => {
                  const s = document.styleSheets[0], m = s.cssRules[0];
                  let e; try { new CSSRule(); } catch (x) { e = x.constructor.name + ': ' + x.message; }
                  return [s instanceof StyleSheet, m.media.constructor.name, m.conditionText, m.media.length,
                    m instanceof CSSConditionRule, m instanceof CSSGroupingRule, s.title, s.media.mediaText, e].join();
                })()
                """));
        // insertRule on a grouping rule.
        Assert.Equal("1|@media screen and (min-width: 100px), print {\n  p { color: red; }\n  .a { margin: 0px; }\n}",
            Eval(fixture.Runtime, """
                (() => { const m = document.styleSheets[0].cssRules[0]; const i = m.insertRule('.a{margin:0px}', 1); return i + '|' + m.cssText; })()
                """));
    }

    [Fact]
    public void ConstructibleEventsAndDataInterfaces()
    {
        using var fixture = RuntimeFixture.Setup(Markup);
        var rt = fixture.Runtime;
        Assert.Equal(
            """[4464,"5",true,"close",true,"[object CloseEvent]"]""",
            Eval(rt, """
                JSON.stringify((() => { const e = new CloseEvent('close', {code: 70000, reason: 5, wasClean: 1});
                  return [e.code, e.reason, e.wasClean, e.type, e instanceof Event, Object.prototype.toString.call(e)]; })())
                """));
        Assert.Equal(
            "1,2,1,0,0,true,true,false",
            Eval(rt, """
                [CloseEvent.length, FormDataEvent.length, TouchEvent.length, TouchList.length, DataTransfer.length,
                 new DragEvent('drop') instanceof MouseEvent, document.createEvent('BeforeUnloadEvent') instanceof BeforeUnloadEvent,
                 'ontouchstart' in window].join()
                """));
        Assert.Equal(
            """[["text/html","text/plain"],"yo",2,"string","text/plain"]""",
            Eval(rt, """
                JSON.stringify((() => { const d = new DataTransfer(); d.setData('text', 'hi'); d.setData('text/html', '<b>'); d.setData('Text', 'yo');
                  return [d.types, d.getData('text/plain'), d.items.length, d.items[0].kind, d.items[1].type]; })())
                """));
        Assert.Equal(
            """[3,1,7,4,true,"size",0]""",
            Eval(rt, """
                JSON.stringify((() => { const s = new CountQueuingStrategy({highWaterMark: 3}); const b = new ByteLengthQueuingStrategy({highWaterMark: '7'});
                  return [s.highWaterMark, s.size(5), b.highWaterMark, b.size(new Uint8Array(4)),
                    s.size === new CountQueuingStrategy({highWaterMark: 1}).size, s.size.name, s.size.length]; })())
                """));
        Assert.Equal("true,[object ReadableStreamDefaultReader],true", Eval(rt, """
            (() => { const rs = new ReadableStream({start(c) { c.enqueue(1); c.close(); }}); const r = rs.getReader();
              return [r instanceof ReadableStreamDefaultReader, Object.prototype.toString.call(r), rs.locked].join(); })()
            """));
        Assert.Equal("true,true,3,false", Eval(rt, """
            [IDBKeyRange.only(3) instanceof IDBKeyRange, IDBKeyRange.bound(1, 5).includes(5),
             IDBKeyRange.lowerBound('a').upper === undefined ? 3 : 0, IDBKeyRange.bound(1, 5, true).includes(1)].join()
            """));
    }

    [Fact]
    public void NewInterfaceNamesAreNotEnumerable()
    {
        using var fixture = RuntimeFixture.Setup(Markup);
        Assert.Equal("", Eval(fixture.Runtime, """
            ['Navigator', 'Location', 'Performance', 'DOMImplementation', 'HTMLDocument', 'MutationRecord', 'MathMLElement',
             'Option', 'DOMRectReadOnly', 'CSSMediaRule', 'StyleSheet', 'MediaList', 'DataTransfer', 'TouchEvent',
             'WebKitMutationObserver', 'WebKitCSSMatrix', 'IDBFactory', 'FileList']
              .filter((n) => typeof window[n] !== 'function' || Object.getOwnPropertyDescriptor(window, n).enumerable).join()
            """));
    }
}
