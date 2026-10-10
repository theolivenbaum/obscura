using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Js.Url;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Port additions: worker <c>importScripts</c> and the worker global scope, and
/// <c>data:</c> / <c>blob:</c> ES modules. Every expected value was measured in
/// Chromium (Playwright 1.56) against the same scripts.
/// </summary>
public sealed partial class RuntimeTests
{
    private static string ScriptResponse(string body, string? contentType = "text/javascript", int status = 200)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(body);
        var type = contentType is null ? string.Empty : "Content-Type: " + contentType + "\r\n";
        var reason = status == 200 ? "OK" : "Not Found";
        return $"HTTP/1.1 {status} {reason}\r\n{type}Content-Length: {bytes}\r\nConnection: close\r\n\r\n{body}";
    }

    /// <summary>A loopback origin serving <paramref name="files"/> (path to (type, body)); 404 otherwise.</summary>
    private static RawHttpServer ScriptServer(Dictionary<string, (string? Type, string Body)> files) =>
        new(request => files.TryGetValue(RawHttpServer.RequestPath(request), out var file)
            ? ScriptResponse(file.Body, file.Type)
            : ScriptResponse("not found", "text/html", 404));

    private static async Task<JsonNode?> RunWorkerAsync(PocketCalculatorJsRuntime rt, string construct)
    {
        rt.ExecuteScript(
            "worker-run",
            "globalThis.__w = undefined; { const __worker = " + construct + ";"
            + " __worker.onmessage = e => { globalThis.__w = e.data; };"
            + " __worker.onerror = e => { globalThis.__w = 'onerror:' + (e && e.message); }; }");
        await RunEventLoopUntilAsync(rt, "globalThis.__w !== undefined");
        return rt.Evaluate("globalThis.__w");
    }

    private const string ImportScriptsWorker = """
        var Module = { a: 5 };
        const out = {};
        function rec(k, f) { try { out[k] = String(f()); } catch (e) { out[k] = 'ERR ' + e.name + ': ' + e.message; } }
        rec('typeofImport', () => typeof importScripts);
        rec('noargs', () => importScripts());
        importScripts('lib.js');
        rec('Lib', () => Lib.v);
        rec('libFn', () => libFn());
        rec('selfLib', () => typeof self.Lib);
        rec('selfLibFn', () => typeof self.libFn);
        rec('lexi', () => typeof lexi);
        rec('lexiInSelf', () => 'lexi' in self);
        rec('K', () => K);
        rec('Cls', () => typeof Cls);
        importScripts('strict.js');
        rec('S', () => sf());
        importScripts('em.js');
        rec('em', () => Module.b + ':' + (Module2 === Module));
        importScripts('order1.js', '/sub/order2.js');
        rec('order', () => self.order.join(','));
        rec('missing', () => importScripts('missing.js'));
        rec('invalid', () => importScripts('http://[bad'));
        rec('throws', () => importScripts('throws.js', 'order1.js'));
        rec('afterThrow', () => self.beforeThrow + ':' + self.order.join(','));
        rec('syntax', () => importScripts('syntax.js'));
        rec('data', () => { importScripts('data:text/javascript,self.fromData%3D7'); return self.fromData; });
        rec('loc', () => location.pathname + '|' + Object.prototype.toString.call(location) + '|' + typeof WorkerLocation);
        rec('nav', () => typeof navigator.userAgent + '|' + Object.prototype.toString.call(navigator));
        rec('scope', () => [typeof WorkerGlobalScope, self instanceof WorkerGlobalScope, self instanceof DedicatedWorkerGlobalScope, Object.prototype.toString.call(self), JSON.stringify(self.name), typeof document, typeof window].join('|'));
        postMessage(out);
        """;

    private static Dictionary<string, (string? Type, string Body)> ImportScriptsFiles() => new()
    {
        ["/sub/w.js"] = ("text/javascript", ImportScriptsWorker),
        ["/sub/lib.js"] = ("text/javascript", "var Lib = { v: 1 };\nfunction libFn() { return 2; }\nlet lexi = 3;\nconst K = 4;\nclass Cls {}\n"),
        ["/sub/strict.js"] = ("application/javascript", "\"use strict\";\nvar S = 10;\nfunction sf() { return S + 1; }\n"),
        ["/sub/em.js"] = ("text/javascript", "Module.b = Module.a * 2;\nvar Module2 = Module;\n"),
        ["/sub/order1.js"] = ("text/javascript", "self.order = (self.order || []).concat('1');"),
        ["/sub/order2.js"] = ("text/javascript", "self.order = (self.order || []).concat('2');"),
        ["/sub/throws.js"] = ("text/javascript", "self.beforeThrow = 1; throw new Error('boom');"),
        ["/sub/syntax.js"] = ("text/javascript", "var = ;"),
    };

    [Fact]
    public async Task WorkerImportScriptsRunsScriptsInTheWorkerGlobalScope()
    {
        using var server = ScriptServer(ImportScriptsFiles());
        using var fixture = RedirectRuntimeForOrigin(server.Origin);
        var rt = fixture.Runtime;

        var result = await RunWorkerAsync(rt, "new Worker('sub/w.js')");

        var origin = server.Origin;
        AssertJson(
            $$"""
            {
              "typeofImport": "function",
              "noargs": "undefined",
              "Lib": "1",
              "libFn": "2",
              "selfLib": "object",
              "selfLibFn": "function",
              "lexi": "number",
              "lexiInSelf": "false",
              "K": "4",
              "Cls": "function",
              "S": "11",
              "em": "10:true",
              "order": "1,2",
              "missing": "ERR NetworkError: Failed to execute 'importScripts' on 'WorkerGlobalScope': The script at '{{origin}}/sub/missing.js' failed to load.",
              "invalid": "ERR SyntaxError: Failed to execute 'importScripts' on 'WorkerGlobalScope': The URL 'http://[bad' is invalid.",
              "throws": "ERR Error: boom",
              "afterThrow": "1:1,2",
              "syntax": "ERR SyntaxError: Failed to execute 'importScripts' on 'WorkerGlobalScope': Unexpected token '='",
              "data": "7",
              "loc": "/sub/w.js|[object WorkerLocation]|function",
              "nav": "string|[object WorkerNavigator]",
              "scope": "function|true|true|[object DedicatedWorkerGlobalScope]|\"\"|undefined|undefined"
            }
            """,
            result);
        // The imports were real requests, in order, resolved against the worker's URL.
        Assert.Contains("GET /sub/lib.js HTTP/1.1", server.RequestLines);
        Assert.Contains("GET /sub/order2.js HTTP/1.1", server.RequestLines);
        // Nothing the worker declared reached the page's window.
        Assert.Equal(
            "undefined|undefined|undefined",
            rt.Evaluate("[typeof Lib, typeof libFn, typeof Module].join('|')")!.GetValue<string>());
    }

    [Fact]
    public async Task WorkerImportScriptsRequiresAJavaScriptMimeType()
    {
        // Chromium's strict MIME check for worker-imported scripts over HTTP: no type,
        // text/plain and application/octet-stream are network errors; data: and blob: are
        // not checked.
        using var server = ScriptServer(new()
        {
            ["/w.js"] = ("text/javascript",
                "const o = {}; for (const u of ['/none.js', '/plain.js', '/oct.js', '/ecma.js',"
                + " 'data:text/plain,self.d=1', URL.createObjectURL ? '' : '']) { if (!u) continue;"
                + " try { importScripts(u); o[u] = 'ok'; } catch (e) { o[u] = e.name; } }"
                + " postMessage(o);"),
            ["/none.js"] = (null, "self.a = 1;"),
            ["/plain.js"] = ("text/plain", "self.b = 1;"),
            ["/oct.js"] = ("application/octet-stream", "self.c = 1;"),
            ["/ecma.js"] = ("text/ecmascript", "self.e = 1;"),
        });
        using var fixture = RedirectRuntimeForOrigin(server.Origin);

        var result = await RunWorkerAsync(fixture.Runtime, "new Worker('/w.js')");

        AssertJson(
            """{"/none.js":"NetworkError","/plain.js":"NetworkError","/oct.js":"NetworkError","/ecma.js":"ok","data:text/plain,self.d=1":"ok"}""",
            result);
    }

    [Fact]
    public async Task WorkerImportScriptsMutesErrorsOfCrossOriginScripts()
    {
        using var server = ScriptServer(new()
        {
            ["/w.js"] = ("text/javascript", "PLACEHOLDER"),
            ["/throwx.js"] = ("text/javascript", "self.xo = 1; throw new TypeError('cross boom');"),
        });
        // localhost and 127.0.0.1 are different origins on the same server.
        var crossOrigin = server.Origin.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        using var fixture = RedirectRuntimeForOrigin(server.Origin);
        var source =
            "const o = {};"
            + $" try {{ importScripts('{crossOrigin}/throwx.js'); }} catch (e) {{ o.cross = e.name + '|' + self.xo; }}"
            + " try { importScripts('/throwx.js'); } catch (e) { o.same = e.name + ': ' + e.message; }"
            + " postMessage(o);";

        var result = await RunWorkerAsync(
            fixture.Runtime,
            "new Worker(URL.createObjectURL(new Blob([" + System.Text.Json.JsonSerializer.Serialize(source)
                + "], { type: 'text/javascript' })))");

        // A blob: worker resolves '/throwx.js' against its blob URL, which cannot be a
        // base, so the same-origin case uses the absolute URL instead.
        AssertJson("""{"cross":"NetworkError|1","same":"SyntaxError: Failed to execute 'importScripts' on 'WorkerGlobalScope': The URL '/throwx.js' is invalid."}""", result);

        var sameOrigin =
            "const o = {};"
            + $" try {{ importScripts('{server.Origin}/throwx.js'); }} catch (e) {{ o.same = e.name + ': ' + e.message; }}"
            + " postMessage(o);";
        result = await RunWorkerAsync(
            fixture.Runtime,
            "new Worker(URL.createObjectURL(new Blob([" + System.Text.Json.JsonSerializer.Serialize(sameOrigin)
                + "], { type: 'text/javascript' })))");
        AssertJson("""{"same":"TypeError: cross boom"}""", result);
    }

    [Fact]
    public async Task WorkerImportScriptsGoesThroughThePageTransport()
    {
        // A page whose client keeps the default SSRF policy: a worker cannot reach a
        // loopback server through importScripts any more than through fetch().
        using var server = ScriptServer(new() { ["/x.js"] = ("text/javascript", "self.x = 1;") });
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        rt.SetUrl("https://example.test/page");
        rt.SetHttpClient(new PocketCalculatorHttpClient(new CookieJar(), null, allowPrivateNetwork: false));
        rt.RunPageInit();
        var source = $"try {{ importScripts('{server.Origin}/x.js'); postMessage('loaded'); }} catch (e) {{ postMessage(e.name); }}";

        var result = await RunWorkerAsync(
            rt,
            "new Worker(URL.createObjectURL(new Blob([" + System.Text.Json.JsonSerializer.Serialize(source)
                + "], { type: 'text/javascript' })))");

        Assert.Equal("NetworkError", result!.GetValue<string>());
        Assert.Empty(server.RequestLines);
    }

    [Fact]
    public async Task ModuleWorkerImportScriptsThrowsTypeError()
    {
        using var server = ScriptServer(new()
        {
            ["/mw.js"] = ("text/javascript",
                "let r; try { importScripts('lib.js'); r = 'no throw'; } catch (e) { r = e.name + ': ' + e.message; }"
                + " postMessage(typeof importScripts + '|' + r);"),
        });
        using var fixture = RedirectRuntimeForOrigin(server.Origin);

        var result = await RunWorkerAsync(fixture.Runtime, "new Worker('/mw.js', { type: 'module' })");

        Assert.Equal(
            "function|TypeError: Failed to execute 'importScripts' on 'WorkerGlobalScope': Module scripts don't support importScripts().",
            result!.GetValue<string>());
    }

    [Fact]
    public async Task WorkerAcceptsAUrlObjectAndReportsItsOwnLocation()
    {
        using var server = ScriptServer(new()
        {
            ["/js/w.js"] = ("text/javascript", "postMessage([self.location.href, self.name, self.origin, typeof self.importScripts].join('|'));"),
        });
        using var fixture = RedirectRuntimeForOrigin(server.Origin);

        var result = await RunWorkerAsync(
            fixture.Runtime,
            "new Worker(new URL('js/w.js', location.href), { name: 'calc' })");

        Assert.Equal($"{server.Origin}/js/w.js|calc|{server.Origin}|function", result!.GetValue<string>());
    }

    [Fact]
    public async Task WorkerFunctionDeclarationNamedOnmessageIsNotTheHandler()
    {
        // Chromium: a top-level `function onmessage` does not register the handler; the
        // message goes unanswered. `var onmessage = ...` assigns the handler.
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "worker-onmessage-decl",
            """
            globalThis.__replies = [];
            for (const source of ["function onmessage(e) { postMessage('decl:' + e.data); }",
                                  "var onmessage = function (e) { postMessage('var:' + e.data); };"]) {
              const w = new Worker(URL.createObjectURL(new Blob([source], { type: 'text/javascript' })));
              w.onmessage = e => __replies.push(e.data);
              w.postMessage('x');
            }
            """);
        await EventLoopWait.UntilIdleAsync(rt);
        AssertJson("""["var:x"]""", rt.Evaluate("__replies"));
    }

    [Fact]
    public async Task WorkerDeliversQueuedMessagesInTheTurnThatStartsIt()
    {
        // Forces the slow path deterministically: the worker's script runs longer than the
        // whole pump budget, so any message that waits for a later turn after the script
        // is never delivered. Messages posted before the worker was ready are delivered in
        // order in the task that runs its script, after the script's microtasks (Chromium
        // runs the worker's microtask checkpoint before its first message task: the
        // handler sees ready === true).
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "worker-slow-start",
            """
            globalThis.__slowReplies = [];
            const source = `
                let ready = false;
                Promise.resolve().then(() => { ready = true; });
                const end = Date.now() + 150;
                while (Date.now() < end) {}
                let count = 0;
                onmessage = event => postMessage([++count, event.data, ready]);
            `;
            const slowUrl = URL.createObjectURL(new Blob([source], { type: 'application/javascript' }));
            const slow = new Worker(slowUrl);
            slow.onmessage = event => __slowReplies.push(event.data);
            slow.postMessage('first');
            slow.postMessage('second');
            """);
        await EventLoopWait.UntilIdleAsync(rt);
        AssertJson("""[[1,"first",true],[2,"second",true]]""", rt.Evaluate("__slowReplies"));
    }

    // ---- data: and blob: ES modules ----

    private static async Task<string> ImportResultAsync(PocketCalculatorJsRuntime rt, string body)
    {
        rt.Evaluate(
            "(function() { globalThis.__r = null; (async () => {" + body + "})()"
            + ".then(v => { globalThis.__r = 'ok:' + v; },"
            + " e => { globalThis.__r = 'err:' + (e && e.message); });"
            + " return 1; })()");
        await RunEventLoopUntilAsync(rt, "globalThis.__r !== null");
        return rt.Evaluate("globalThis.__r")?.GetValue<string>() ?? "pending";
    }

    [Fact]
    public async Task DataUrlModulesLoadWithoutANetworkFetch()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;

        Assert.Equal(
            "ok:42",
            await ImportResultAsync(rt, "return (await import('data:text/javascript,export const a = 41 + 1;')).a;"));
        Assert.Equal(
            "ok:x%y",
            await ImportResultAsync(rt, "return (await import('data:text/javascript,export%20const%20p%20%3D%20%22x%25y%22')).p;"));
        Assert.Equal(
            "ok:7",
            await ImportResultAsync(rt, "return (await import('data:text/javascript;base64,' + btoa('export default 7'))).default;"));
        // One module per URL: the same data: URL is the same module instance.
        Assert.Equal(
            "ok:true:true",
            await ImportResultAsync(
                rt,
                "const a = await import('data:text/javascript,export const o = {};');"
                + " const b = await import('data:text/javascript,export const o = {};');"
                + " return (a === b) + ':' + (a.o === b.o);"));
        Assert.Equal(
            "ok:data:text/javascript,export const u = import.meta.url;",
            await ImportResultAsync(rt, "return (await import('data:text/javascript,export const u = import.meta.url;')).u;"));
        // A data: module can import another statically.
        Assert.Equal(
            "ok:3",
            await ImportResultAsync(
                rt,
                "return (await import('data:text/javascript,' + encodeURIComponent(\"import { a } from 'data:text/javascript,export const a = 3;'; export default a;\"))).default;"));
        // Strict MIME checking: Chromium refuses a module whose type is not JavaScript.
        Assert.Equal(
            "err:Failed to fetch dynamically imported module: data:text/plain,export default 1",
            await ImportResultAsync(rt, "return (await import('data:text/plain,export default 1')).default;"));
    }

    [Fact]
    public async Task DataUrlModuleScriptRunsFromAStaticGraph()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;

        await rt.LoadModuleAsync("data:text/javascript,globalThis.__srcModule = 'src-module';", 2_000);
        await rt.LoadInlineModuleAsync(
            "import { a } from 'data:text/javascript,export const a = 41 + 1;'; globalThis.__static = a;",
            "http://example.com/test",
            2_000);

        Assert.Equal("src-module", rt.Evaluate("globalThis.__srcModule")!.GetValue<string>());
        Assert.Equal(42, rt.Evaluate("globalThis.__static")!.GetValue<double>());
    }

    [Fact]
    public async Task BlobUrlModulesLoadWhenTheBlobIsJavaScript()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;

        Assert.Equal(
            "ok:blobmod:true",
            await ImportResultAsync(
                rt,
                "const u = URL.createObjectURL(new Blob(['export default \"blobmod\"'], { type: 'text/javascript' }));"
                + " const m = await import(u); return m.default + ':' + ((await import(u)) === m);"));
        Assert.StartsWith(
            "err:Failed to fetch dynamically imported module: blob:",
            await ImportResultAsync(rt, "return (await import(URL.createObjectURL(new Blob(['export default 1'])))).default;"));
        Assert.StartsWith(
            "err:Failed to fetch dynamically imported module: blob:",
            await ImportResultAsync(
                rt,
                "const u = URL.createObjectURL(new Blob(['export default 1'], { type: 'application/javascript' }));"
                + " URL.revokeObjectURL(u); return (await import(u)).default;"));
    }

    [Fact]
    public async Task ImportedModulesSeeTheirOwnImportMetaUrl()
    {
        using var server = ScriptServer(new() { ["/m1.mjs"] = ("text/javascript", "export const u = import.meta.url;") });
        // The module loader resolves and judges CORS against the runtime's base URL, so the
        // page is on the server's origin from construction.
        using var rt = ModuleGraphRuntime($"{server.Origin}/page", new CookieJar());
        rt.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        rt.SetUrl($"{server.Origin}/page");
        rt.RunPageInit();

        Assert.Equal(
            $"ok:{server.Origin}/m1.mjs",
            await ImportResultAsync(rt, $"return (await import('{server.Origin}/m1.mjs')).u;"));
    }

    // ---- the helpers ----

    [Fact]
    public void DataUrlProcessorDecodesPercentAndBase64Bodies()
    {
        Assert.True(DataUrl.TryProcess("data:text/javascript,a%20b%zz", out var mime, out var body));
        Assert.Equal("text/javascript", mime);
        Assert.Equal("a b%zz", System.Text.Encoding.UTF8.GetString(body));

        Assert.True(DataUrl.TryProcess("data:Text/JavaScript ; Base64 , ZXhw b3J0#frag", out mime, out body));
        Assert.Equal("text/javascript", mime);
        Assert.Equal("export", System.Text.Encoding.UTF8.GetString(body));

        Assert.True(DataUrl.TryProcess("data:,x", out mime, out _));
        Assert.Equal("text/plain", mime);
        Assert.True(DataUrl.TryProcess("data:;charset=utf-8,x", out mime, out _));
        Assert.Equal("text/plain", mime);

        Assert.False(DataUrl.TryProcess("data:text/javascript;base64,%%%", out _, out _));
        Assert.False(DataUrl.TryProcess("data:text/javascript", out _, out _));
        Assert.True(DataUrl.IsJavaScriptMimeType("application/x-javascript"));
        Assert.False(DataUrl.IsJavaScriptMimeType("text/plain"));
        Assert.Equal("text/javascript", DataUrl.Essence("text/JavaScript; charset=utf-8"));
        Assert.Null(DataUrl.Essence("javascript"));
    }

    [Fact]
    public void ScriptDeclarationsFindsTopLevelNamesOnly()
    {
        var result = ScriptDeclarations.Scan(
            """
            "use strict";
            var a = 1, b = { c: 2, d: [3, 4] }, e
            let { f, g: h, ...i } = obj, [j, , k = 5] = arr;
            const re = /var notThis = 1/g, t = `x${ (function () { var inner; })() }y`;
            function fn() { var inner2; function nested() {} }
            async function afn() {}
            function* gen() {}
            class Klass { method() { let m; } }
            !function expr() {}();
            x = function notDecl() {};
            if (a) { var hoisted = 1; }
            const q = a
            / 2 / 1
            for (var idx = 0, n = 2; idx < n; idx++) { let notThis; }
            obj.var = { var: 1 };
            var last
            """);

        Assert.True(result.Strict);
        Assert.Equal(["a", "b", "e", "hoisted", "idx", "n", "last"], result.Vars);
        Assert.Equal(["fn", "afn", "gen"], result.Functions);
        Assert.Equal(["f", "h", "i", "j", "k", "re", "t", "Klass", "q"], result.Lexicals);
        Assert.False(ScriptDeclarations.Scan("var s = 'use strict';").Strict);
        Assert.Equal(
            """{"s":false,"v":["x"],"f":["y"],"l":[]}""",
            ScriptDeclarations.ScanJson("var x = 1\nfunction y() {}"));
    }

    /// <summary>The static requests the module graph prefetch reads (port addition).</summary>
    [Fact]
    public void ModuleRequestsAreTheStaticImportAndExportFromSpecifiers()
    {
        var requests = ScriptDeclarations.ModuleRequests(
            """
            import "./side.js";
            import def from './default.js'
            import def2, { a as b, "str" as c } from "./named.js";
            import * as ns from "./ns.js";
            import{x}from"./min.js";import"./min2.js";
            export { y } from './reexport.js';
            export * from './star.js';
            export * as all from './star-as.js';
            export { local };
            export const z = 1;
            import json from './data.json' with { type: 'json' };
            const lazy = import('./lazy.js');
            const meta = import.meta.url;
            obj.import('./method.js');
            const s = "import x from './in-string.js'";
            // import a from './in-comment.js';
            const re = /import "x"/;
            function f() { return `${import('./tmpl.js')}`; }
            import "./side.js";
            """);

        Assert.Equal(
            [
                "./side.js", "./default.js", "./named.js", "./ns.js", "./min.js", "./min2.js",
                "./reexport.js", "./star.js", "./star-as.js", "./data.json",
            ],
            requests);
        Assert.Empty(ScriptDeclarations.ModuleRequests("export default function () {}"));
        Assert.Empty(ScriptDeclarations.ModuleRequests("import 'a\\u0062.js';"));
    }
}
