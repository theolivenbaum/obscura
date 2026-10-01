using System.Runtime.CompilerServices;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// M7 leftover: V8 reserves WebAssembly memory itself, outside the <c>ArrayBuffer</c>
/// allocator the per-isolate ceiling counts, so <c>new WebAssembly.Memory({initial: 20000})</c>
/// reserved 1.3 GB. Memory created or grown through the JavaScript API now counts against
/// a per-isolate budget, and V8 caps each memory (including a grow run inside wasm).
/// </summary>
public sealed class WasmMemoryBudgetTests
{
    private const long Limit = 64L * 1024 * 1024;

    // (module (memory (export "m") 1)
    //   (func (export "g") (param i32) (result i32) local.get 0 memory.grow))
    private const string GrowModule =
        "new Uint8Array([0,97,115,109,1,0,0,0, 1,6,1,96,1,127,1,127, 3,2,1,0, 5,3,1,0,1,"
        + " 7,9,2,1,109,2,0,1,103,0,0, 10,8,1,6,0,32,0,64,0,11])";

    /// <summary>
    /// V8's flag can only be set before the first isolate of the process; a test elsewhere
    /// in this assembly creates a bare <c>V8ScriptEngine</c>, so set it before any test runs.
    /// </summary>
    [ModuleInitializer]
    internal static void ApplyNativeCapBeforeAnyEngine() =>
        V8Flags.ApplyWasmMemoryCap(PocketCalculatorJsRuntime.DefaultWasmMemoryLimitBytes);

    private static RuntimeFixture Budgeted()
    {
        PocketCalculatorJsRuntime.WasmMemoryLimitForTests.Value = Limit;
        try
        {
            return RuntimeFixture.Setup("<html><body></body></html>");
        }
        finally
        {
            PocketCalculatorJsRuntime.WasmMemoryLimitForTests.Value = null;
        }
    }

    private static string Eval(RuntimeFixture fixture, string script) =>
        fixture.Runtime.Evaluate(script)?.ToString() ?? "null";

    [Fact]
    public void TheDefaultCeilingMatchesTheArrayBufferOne() =>
        Assert.Equal(PocketCalculatorJsRuntime.DefaultArrayBufferLimitBytes, PocketCalculatorJsRuntime.DefaultWasmMemoryLimitBytes);

    [Fact]
    public void AMemoryOverTheDefaultCeilingThrowsRangeError()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        Assert.Equal(
            "RangeError: WebAssembly.Memory(): could not allocate memory",
            Eval(fixture, "(() => { try { new WebAssembly.Memory({initial: 20000}); return 'allocated'; } catch (e) { return e.name + ': ' + e.message; } })()"));
    }

    [Fact]
    public void MemoriesShareOneBudgetAndCollectedOnesGiveItBack()
    {
        using var fixture = Budgeted();
        Assert.Equal("ok", Eval(fixture, "(globalThis.__m = new WebAssembly.Memory({initial: 640}), 'ok')"));
        Assert.Equal(
            "RangeError: WebAssembly.Memory(): could not allocate memory",
            Eval(fixture, "(() => { try { new WebAssembly.Memory({initial: 640}); return 'allocated'; } catch (e) { return e.name + ': ' + e.message; } })()"));
        Assert.Equal(
            "RangeError: WebAssembly.Memory.grow(): Maximum memory size exceeded",
            Eval(fixture, "(() => { try { __m.grow(640); return 'grown'; } catch (e) { return e.name + ': ' + e.message; } })()"));

        Eval(fixture, "(globalThis.__m = null, 0)");
        fixture.Runtime.Engine.CollectGarbage(true);
        Assert.Equal("41943040", Eval(fixture, "String(new WebAssembly.Memory({initial: 640}).buffer.byteLength)"));
    }

    [Fact]
    public void OrdinaryMemoriesAndInstancesStillWork()
    {
        using var fixture = Budgeted();
        Assert.Equal(
            "1,131072,true,true,Memory,function Memory() { [native code] }",
            Eval(fixture, """
                (() => {
                  const m = new WebAssembly.Memory({initial: 1, maximum: 4});
                  const old = m.grow(1);
                  return [old, m.buffer.byteLength, m instanceof WebAssembly.Memory,
                    WebAssembly.Memory.prototype.constructor === WebAssembly.Memory,
                    WebAssembly.Memory.name, Function.prototype.toString.call(WebAssembly.Memory)].join();
                })()
                """));
        Assert.Equal("TypeError", Eval(fixture, "(() => { try { WebAssembly.Memory({initial: 1}); } catch (e) { return e.name; } })()"));

        // A module's exported memory counts once it exists.
        Assert.Equal(
            "1,RangeError",
            Eval(fixture, $$"""
                (() => {
                  const instance = new WebAssembly.Instance(new WebAssembly.Module({{GrowModule}}));
                  globalThis.__i = instance;
                  const before = instance.exports.g(639);
                  let error = 'none';
                  try { new WebAssembly.Memory({initial: 400}); } catch (e) { error = e.name; }
                  return [before, error].join();
                })()
                """));
    }

    [Fact]
    public void AGrowInsideWasmStopsAtTheNativeCap()
    {
        if (V8Flags.NativeWasmMemoryCapBytes is null)
        {
            Assert.Skip("V8's flag setter is not exported on this platform.");
        }

        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        Assert.Equal(
            "-1",
            Eval(fixture, $"String(new WebAssembly.Instance(new WebAssembly.Module({GrowModule})).exports.g(20000))"));
    }
}
