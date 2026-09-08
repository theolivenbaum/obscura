using Microsoft.ClearScript.V8;
using Obscura.Js;
using Obscura.Js.Runtime;

using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
BootstrapLoader.Install(engine, ops =>
{
    foreach (var name in BootstrapSource.OpNames)
    {
        var n = name;
        ops.SetProperty(n, new Func<object?, object?, object?, object?, object?>((a, b, c, d) =>
            n is "op_async_runtime_available" or "op_runtime_events_enabled" ? false : ""));
    }
});
Console.WriteLine("== V8 / bootstrap.js ==");
Console.WriteLine("window/document = " + engine.Evaluate("typeof window + '/' + typeof document"));
Console.WriteLine("Deno hidden     = " + engine.Evaluate("typeof globalThis.Deno"));
Console.WriteLine();
Console.WriteLine("== Skia ==");
Console.WriteLine(Obscura.Render.SkiaSmoke.Probe());
return 0;
