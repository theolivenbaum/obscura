using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// SECURITY.md I10: the global object's API shape against Chromium 141's. Every expected
/// value here was measured in Chromium 141.0.7390.37 (headless, Playwright) on the same
/// markup.
/// </summary>
public sealed class GlobalObjectShape
{
    private static string Eval(PocketCalculatorJsRuntime runtime, string expression) =>
        runtime.Evaluate(expression)!.GetValue<string>();

    [Fact]
    public void ThePrototypeChainIsChromiums()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        Assert.Equal(
            "[object Window]|[object Window]|[object WindowProperties]|[object EventTarget]|true",
            Eval(fixture.Runtime, """
                (() => {
                  const ts = (o) => Object.prototype.toString.call(o);
                  const wp = Object.getPrototypeOf(Window.prototype);
                  return [ts(window), ts(Object.getPrototypeOf(window)), ts(wp),
                    ts(Object.getPrototypeOf(wp)),
                    Object.getPrototypeOf(Object.getPrototypeOf(wp)) === Object.prototype].join('|');
                })()
                """));
        Assert.Equal(
            "true,true,true,true,false,true,true,false",
            Eval(fixture.Runtime, """
                [Object.getPrototypeOf(window) === Window.prototype, window instanceof Window,
                 window instanceof EventTarget, window.constructor === Window,
                 Object.prototype.hasOwnProperty.call(window, 'constructor'),
                 Object.getPrototypeOf(Window) === EventTarget,
                 Object.getPrototypeOf(Node.prototype) === EventTarget.prototype,
                 Object.prototype.hasOwnProperty.call(Object.getPrototypeOf(Window.prototype), 'constructor')].join(',')
                """));
        Assert.Equal(
            "TypeError: Failed to construct 'Window': Illegal constructor|TypeError: Illegal constructor|0,1|function Window() { [native code] }",
            Eval(fixture.Runtime, """
                (() => {
                  const e = (f) => { try { f(); return 'ok'; } catch (x) { return x.constructor.name + ': ' + x.message; } };
                  return [e(() => new Window()), e(() => Window()),
                    [window.TEMPORARY, window.PERSISTENT].join(','), Function.prototype.toString.call(Window)].join('|');
                })()
                """));
    }

    [Fact]
    public void ListenerMethodsComeFromEventTargetPrototype()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><button id=b>b</button></body></html>");
        var runtime = fixture.Runtime;
        Assert.Equal(
            "false,false,false,false|true,true,true|2,2,1|addEventListener,removeEventListener,dispatchEvent,constructor",
            Eval(runtime, """
                (() => {
                  const own = (o) => ['addEventListener', 'removeEventListener', 'dispatchEvent']
                    .some((n) => Object.prototype.hasOwnProperty.call(o, n));
                  const ET = EventTarget.prototype;
                  return [[own(window), own(Node.prototype), own(Element.prototype), own(Document.prototype)].join(','),
                    [window.addEventListener === ET.addEventListener, document.dispatchEvent === ET.dispatchEvent,
                     document.body.removeEventListener === ET.removeEventListener].join(','),
                    [ET.addEventListener.length, ET.removeEventListener.length, ET.dispatchEvent.length].join(','),
                    Object.getOwnPropertyNames(ET).sort((a, b) => a === 'constructor' ? 1 : b === 'constructor' ? -1 : 0).join(',')].join('|');
                })()
                """));
        // Each kind of target still gets its own listeners, and an element's event still
        // bubbles to the document.
        Assert.Equal(
            "window,bare,button,document,plain,false",
            Eval(runtime, """
                (() => {
                  const seen = [];
                  window.addEventListener('probe', () => seen.push('window'));
                  (0, EventTarget.prototype.addEventListener)('bare', () => seen.push('bare'));
                  window.dispatchEvent(new Event('probe'));
                  dispatchEvent(new Event('bare'));
                  const b = document.getElementById('b');
                  b.addEventListener('click', () => seen.push('button'));
                  document.addEventListener('click', () => seen.push('document'));
                  b.click();
                  const t = new EventTarget();
                  t.addEventListener('x', () => seen.push('plain'));
                  t.dispatchEvent(new Event('x'));
                  seen.push(String(t instanceof Node));
                  return seen.join(',');
                })()
                """));
    }

    [Fact]
    public void FrameIndicesExistOnlyForFrames()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var runtime = fixture.Runtime;
        const string Probe = """
            [window.length, '0' in window, '1' in window,
             Object.getOwnPropertyNames(window).filter((k) => /^\d+$/.test(k)).join('+'),
             Object.getOwnPropertyDescriptor(window, '0') ? 'own' : 'none'].join(',')
            """;
        Assert.Equal("0,false,false,,none", Eval(runtime, Probe));
        runtime.ExecuteScript("add", "globalThis.probeFrame = document.body.appendChild(document.createElement('iframe'));");
        Assert.Equal("1,true,false,0,own", Eval(runtime, Probe));
        Assert.True(runtime.Evaluate("window[0] === probeFrame.contentWindow && frames[0] === window[0]")!.GetValue<bool>());
        runtime.ExecuteScript("remove", "probeFrame.remove();");
        Assert.Equal("0,false,false,,none", Eval(runtime, Probe));
    }

    [Fact]
    public void NamedElementsResolveOnWindowProperties()
    {
        using var fixture = RuntimeFixture.Setup(
            "<html><body><div id=foo></div><div id=dispatchEvent></div></body></html>");
        var runtime = fixture.Runtime;
        Assert.Equal(
            "DIV,true,false,false,false,,function",
            Eval(runtime, """
                [window.foo.tagName, 'foo' in window, Object.prototype.hasOwnProperty.call(window, 'foo'),
                 Object.getOwnPropertyNames(window).includes('foo'), Object.keys(window).includes('foo'),
                 Object.getOwnPropertyNames(Object.getPrototypeOf(Window.prototype)).join('+'),
                 typeof window.dispatchEvent].join(',')
                """));
        // An assignment makes an ordinary own property, as in Chromium; the getter-only
        // own accessor used to drop it.
        Assert.Equal(
            "5,true,true",
            Eval(runtime, """
                (() => {
                  window.foo = 5;
                  const d = Object.getOwnPropertyDescriptor(window, 'foo');
                  return [window.foo, d.writable, d.enumerable].join(',');
                })()
                """));
    }

    [Fact]
    public void GlobalsChromiumDoesNotExposeAreAbsent()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var runtime = fixture.Runtime;
        Assert.Equal(
            "undefined,undefined,undefined,undefined,EventTarget",
            Eval(runtime, """
                [typeof SharedArrayBuffer, typeof ContentIndex, typeof FontFaceSet,
                 typeof webkitAudioContext, document.fonts.constructor.name].join(',')
                """));
        Assert.Equal(
            "onfocusin:false/false/false onfocusout:false/false/false onpaste:true/true/false oncopy:true/true/false oncut:true/true/false",
            Eval(runtime, """
                ['onfocusin', 'onfocusout', 'onpaste', 'oncopy', 'oncut']
                  .map((n) => n + ':' + (n in document) + '/' + (n in document.body) + '/' + (n in window)).join(' ')
                """));
        // structuredClone still passes a shared buffer through, from shared wasm memory.
        Assert.Equal(
            "true",
            Eval(runtime, """
                (() => {
                  const m = new WebAssembly.Memory({ initial: 1, maximum: 1, shared: true });
                  return String(structuredClone(m.buffer) === m.buffer);
                })()
                """));
    }

    [Fact]
    public void InterfaceObjectsAreNotEnumerableAndInterfacesAreTagged()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><p>x</p></body></html>");
        var runtime = fixture.Runtime;
        Assert.Equal(
            "|true",
            Eval(runtime, """
                [Object.keys(window).filter((k) => /^[A-Z]/.test(k)).join(','),
                 Object.getOwnPropertyDescriptor(window, 'history').enumerable].join('|')
                """));
        Assert.Equal(
            // The document is an HTMLDocument, as in Chromium (GlobalInterfaceObjects).
            "[object Text]|[object HTMLDocument]|[object EventTarget]|[object DOMRect]|[object HTMLCollection]",
            Eval(runtime, """
                [document.createTextNode('x'), document, new EventTarget(),
                 new DOMRect(1, 2, 3, 4), document.getElementsByTagName('p')]
                  .map((o) => Object.prototype.toString.call(o)).join('|')
                """));
    }

    [Fact]
    public void AFrameRealmHasTheSameShape()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        using var frame = FrameRealm.Create(
            page.Runtime, 1, 0, "https://child.example/f", "<html><body><p>frame</p></body></html>");
        Assert.NotNull(frame);
        Assert.Equal(
            "true,true,false,false",
            frame.Evaluate("""
                [Object.getPrototypeOf(window) === Window.prototype, window instanceof EventTarget,
                 Object.prototype.hasOwnProperty.call(window, 'addEventListener'), '0' in window].join(',')
                """)!.GetValue<string>());
    }
}
