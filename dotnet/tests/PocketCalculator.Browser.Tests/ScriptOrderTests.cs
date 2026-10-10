using System.Text.Json;
using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// When parser-inserted, async, deferred, module and dynamically inserted scripts run, relative
/// to parsing and to each other, against Chromium 141. Each expected log is what Chromium
/// logged for the same page in scripts/script-order-conformance/probe.mjs; the delays are
/// wider here so a loaded test host keeps the same order. The port used to parse the whole
/// document first and then run its scripts back to back (crates/obscura-browser does).
/// </summary>
public sealed class ScriptOrderTests
{
    private const string Head =
        "<script>window.L=[];window.log=function(s){L.push(s)};"
        + "window.marks=function(){return [...document.querySelectorAll('.m')].map(e=>e.id).join(',')||'-'};"
        + "document.addEventListener('readystatechange',()=>log('rsc '+document.readyState+' '+marks()));"
        + "document.addEventListener('DOMContentLoaded',()=>log('DCL '+marks()));"
        + "window.addEventListener('load',()=>log('load'));</script>";

    /// <summary>A page server: "/" is <paramref name="html"/>, "/js/NAME?d=MS&amp;c=CODE" a script that logs NAME after MS.</summary>
    private static TestHttpServer Serve(string html) => TestHttpServer.Start(request =>
    {
        string path = request.Path;
        if (path == "/")
        {
            return TestResponse.Html(html);
        }

        if (path.StartsWith("/js/", StringComparison.Ordinal))
        {
            string name = path[4..].Split('?')[0];
            int delay = 0;
            string code = string.Empty;
            int query = path.IndexOf('?', StringComparison.Ordinal);
            if (query >= 0)
            {
                foreach (string part in path[(query + 1)..].Split('&'))
                {
                    if (part.StartsWith("d=", StringComparison.Ordinal))
                    {
                        delay = int.Parse(part[2..], System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (part.StartsWith("c=", StringComparison.Ordinal))
                    {
                        code = Uri.UnescapeDataString(part[2..]);
                    }
                }
            }

            return TestResponse.JavaScript($"log('{name} '+document.readyState+' '+marks());{code}") with { DelayMs = delay };
        }

        return TestResponse.Text("missing") with { Status = "404 Not Found" };
    });

    private static async Task<string[]> LoadAsync(string name, string html, int settleMs = 300)
    {
        using TestHttpServer server = Serve(html);
        using Page page = PageFixtures.NewPage(name);
        await page.NavigateAsync($"{server.Origin}/");
        await page.SettleForDurationAsync((ulong)settleMs);
        string json = page.Evaluate("JSON.stringify(window.L)")!.GetValue<string>();
        return JsonSerializer.Deserialize<string[]>(json)!;
    }

    [Fact]
    public async Task AParserBlockingScriptSeesOnlyWhatPrecedesIt()
    {
        string[] log = await LoadAsync(
            "script-order-basic",
            $"<!doctype html><html><head>{Head}"
            + "<script>log('inline1 '+marks()+' body='+!!document.body);Promise.resolve().then(()=>log('microtask'));setTimeout(()=>log('timeout0'),0);</script>"
            + "<script>log('inline2')</script></head><body>"
            + "<div id=a class=m></div><script>log('inline3 '+marks())</script>"
            + "<div id=b class=m></div><script src=/js/ext1?d=300></script>"
            + "<div id=c class=m></div><script>log('inline4 '+marks())</script><div id=d class=m></div></body></html>");
        Assert.Equal(
            [
                "inline1 - body=false", "microtask", "inline2", "inline3 a", "timeout0", "ext1 loading a,b",
                "inline4 a,b,c", "rsc interactive a,b,c,d", "DCL a,b,c,d", "rsc complete a,b,c,d", "load",
            ],
            log);
    }

    [Fact]
    public async Task AsyncScriptsAndTimersRunWhileTheParserWaitsForABlockingScript()
    {
        string[] log = await LoadAsync(
            "script-order-async",
            $"<!doctype html><html><head>{Head}"
            + "<script>setTimeout(()=>log('timeout100 '+marks()),100);</script>"
            + "<script src=/js/asyncFast?d=20 async></script>"
            + "<script src=/js/asyncSlow?d=1500 async></script>"
            + "</head><body><div id=a class=m></div>"
            + "<script src=/js/blocking?d=700></script>"
            + "<div id=b class=m></div><script>log('after blocking '+marks())</script><div id=c class=m></div></body></html>",
            settleMs: 1500);
        Assert.Equal(
            [
                "asyncFast loading a", "timeout100 a", "blocking loading a", "after blocking a,b",
                "rsc interactive a,b,c", "DCL a,b,c", "asyncSlow interactive a,b,c", "rsc complete a,b,c", "load",
            ],
            log);
    }

    [Fact]
    public async Task DeferredClassicAndModuleScriptsRunInDocumentOrderAfterInteractive()
    {
        string[] log = await LoadAsync(
            "script-order-defer",
            $"<!doctype html><html><head>{Head}"
            + "<script src=/js/defer1?d=400 defer></script>"
            + "<script type=module src=/js/module1?d=10></script>"
            + "<script src=/js/defer2?d=10 defer></script>"
            + "<script type=module>log('inlineModule '+document.readyState)</script>"
            + "<script defer>log('inline defer is ignored '+document.readyState)</script>"
            + "</head><body><div id=a class=m></div></body></html>",
            settleMs: 600);
        Assert.Equal(
            [
                "inline defer is ignored loading", "rsc interactive a", "defer1 interactive a", "module1 interactive a",
                "defer2 interactive a", "inlineModule interactive", "DCL a", "rsc complete a", "load",
            ],
            log);
    }

    [Fact]
    public async Task DocumentWriteParsesAtTheInsertionPoint()
    {
        string[] log = await LoadAsync(
            "script-order-write",
            $"<!doctype html><html><head>{Head}</head><body><div id=a class=m></div><script>"
            + "document.write('<div id=w1 class=m></div><script>log(\"written \"+marks())<\\/script><div id=w2 class=m></div>');"
            + "log('after write1 '+marks());"
            + "document.write('<script src=/js/writtenExt?d=200><\\/script><div id=w3 class=m></div>');"
            + "log('after write2 '+marks());"
            + "document.write('<span id=w4 cla');document.write('ss=m>x</span>');"
            + "log('after write3 '+marks());"
            + "</script><script>log('next '+marks())</script><div id=b class=m></div></body></html>");
        Assert.Equal(
            [
                "written a,w1", "after write1 a,w1,w2", "after write2 a,w1,w2", "after write3 a,w1,w2",
                "writtenExt loading a,w1,w2", "next a,w1,w2,w3,w4", "rsc interactive a,w1,w2,w3,w4,b",
                "DCL a,w1,w2,w3,w4,b", "rsc complete a,w1,w2,w3,w4,b", "load",
            ],
            log);
    }

    [Fact]
    public async Task DynamicScriptsAreAsyncUnlessAsyncIsFalse()
    {
        string[] log = await LoadAsync(
            "script-order-dynamic",
            $"<!doctype html><html><head>{Head}<script>"
            + "function add(n,d,ordered){var s=document.createElement('script');s.src='/js/'+n+'?d='+d;if(ordered)s.async=false;"
            + "s.onload=()=>log(n+' onload');document.head.appendChild(s);}"
            + "add('ordered1',600,true);add('ordered2',10,true);add('asyncDyn',250,false);"
            + "var i=document.createElement('script');i.textContent=\"log('dynamic inline')\";document.head.appendChild(i);"
            + "log('after inserting');"
            + "document.addEventListener('DOMContentLoaded',()=>add('fromDCL',10,false));"
            + "</script></head><body><div id=a class=m></div><script>log('body '+marks())</script></body></html>",
            settleMs: 800);
        Assert.Equal(
            [
                "dynamic inline", "after inserting", "body a", "rsc interactive a", "DCL a",
                "fromDCL interactive a", "fromDCL onload", "asyncDyn interactive a", "asyncDyn onload",
                "ordered1 interactive a", "ordered1 onload", "ordered2 interactive a", "ordered2 onload",
                "rsc complete a", "load",
            ],
            log);
    }

    /// <summary>
    /// grammarly.com's shape: a parser-blocking script inserts a dynamic script beside itself,
    /// which removes it again once it runs, before the deferred app scripts (which have not
    /// loaded yet) look at the root, as Next.js hydration does. The port ran the deferred
    /// scripts first and React reported a hydration mismatch (#418/#423).
    /// </summary>
    [Fact]
    public async Task AScriptInsertedByAParserScriptRunsWhileTheDeferredScriptsLoad()
    {
        string ui = Uri.EscapeDataString("document.getElementById('ui').remove();log('ui removed')");
        string gate = Uri.EscapeDataString(
            "var u=document.createElement('script');u.id='ui';u.src='/js/ui?d=50&c=" + ui + "';document.currentScript.after(u);");
        string hydrate = Uri.EscapeDataString(
            "log('hydrate '+[...document.getElementById('root').children].map(e=>e.localName+(e.id?'#'+e.id:'')).join(','))");
        string[] log = await LoadAsync(
            "script-order-hydration",
            $"<!doctype html><html><head>{Head}"
            + "<script src=/js/framework?d=700 defer></script>"
            + $"<script src='/js/main?d=10&c={hydrate}' defer></script>"
            + $"</head><body><div id=root><script id=gate src='/js/gate?d=10&c={gate}'></script>"
            + "<div id=content class=m><p>text</p></div></div></body></html>",
            settleMs: 300);
        Assert.Equal(
            [
                "gate loading -", "rsc interactive content", "ui interactive content", "ui removed",
                "framework interactive content", "main interactive content", "hydrate script#gate,div#content",
                "DCL content", "rsc complete content", "load",
            ],
            log);
    }

    [Fact]
    public async Task ATaskTheLastScriptQueuedRunsBeforeInteractive()
    {
        string[] log = await LoadAsync(
            "script-order-task-boundaries",
            $"<!doctype html><html><head>{Head}"
            + "<script>document.addEventListener('DOMContentLoaded',()=>{setTimeout(()=>log('timeout0 from DCL'),0);Promise.resolve().then(()=>log('microtask from DCL'));});</script>"
            + "</head><body><div id=a class=m></div>"
            + "<script>setTimeout(()=>log('timeout0 end of body'),0);Promise.resolve().then(()=>log('microtask end of body'));log('last script');</script>"
            + "</body></html>");
        Assert.Equal(
            [
                "last script", "microtask end of body", "timeout0 end of body", "rsc interactive a", "DCL a",
                "microtask from DCL", "rsc complete a", "load", "timeout0 from DCL",
            ],
            log);
    }

    [Fact]
    public async Task AMutationObserverSeesTheParsersInsertions()
    {
        string[] log = await LoadAsync(
            "script-order-mutations",
            $"<!doctype html><html><head>{Head}<script>"
            + "new MutationObserver(rs=>{var a=[];for(var r of rs)for(var n of r.addedNodes)if(n.nodeType===1)a.push(n.localName+(n.id?'#'+n.id:''));log('mutations '+a.join(','));})"
            + ".observe(document,{childList:true,subtree:true});"
            + "</script></head><body><div id=a class=m><p id=p1></p></div><script>log('inline '+marks())</script><div id=b class=m></div></body></html>");
        Assert.Equal(
            [
                "mutations body,div#a,p#p1,script", "inline a", "mutations div#b", "rsc interactive a,b", "DCL a,b",
                "rsc complete a,b", "load",
            ],
            log);
    }

    /// <summary>
    /// Chromium constructs a defined custom element when the parser creates it, before its
    /// children. The port upgrades it right after inserting it (its attributes are there, and
    /// it is connected, unlike in Chromium), which still precedes its children and later scripts.
    /// </summary>
    [Fact]
    public async Task ADefinedCustomElementIsUpgradedBeforeItsChildrenAreParsed()
    {
        string[] log = await LoadAsync(
            "script-order-custom-elements",
            $"<!doctype html><html><head>{Head}<script>"
            + "customElements.define('x-a',class extends HTMLElement{constructor(){super();log('constructor children='+this.childNodes.length)}"
            + "connectedCallback(){log('connected '+this.id+' children='+this.childNodes.length)}});"
            + "</script></head><body><x-a id=first class=m><span>child</span></x-a><script>log('after first '+marks())</script>"
            + "<x-a id=second class=m></x-a></body></html>");
        Assert.Equal(
            [
                "constructor children=0", "connected first children=0", "after first first", "constructor children=0",
                "connected second children=0", "rsc interactive first,second", "DCL first,second",
                "rsc complete first,second", "load",
            ],
            log);
    }

    [Fact]
    public async Task ParserScriptsFireLoadAndErrorAndNomoduleScriptsDoNotRun()
    {
        string[] log = await LoadAsync(
            "script-order-events",
            $"<!doctype html><html><head>{Head}"
            + "<script src=/js/ok?d=10 onload=\"log('ok onload')\" onerror=\"log('ok onerror')\"></script>"
            + "<script src=/missing.js onload=\"log('missing onload')\" onerror=\"log('missing onerror')\"></script>"
            + "<script nomodule src=/js/nomodule?d=0></script>"
            + "<script nomodule>log('inline nomodule')</script>"
            + "</head><body></body></html>");
        Assert.Equal(
            ["ok loading -", "ok onload", "missing onerror", "rsc interactive -", "DCL -", "rsc complete -", "load"],
            log);
    }
}
