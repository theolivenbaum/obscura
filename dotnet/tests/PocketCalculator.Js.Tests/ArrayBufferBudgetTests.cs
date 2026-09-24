using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// M7: <c>ArrayBuffer</c> backing stores live outside the V8 heap, so the heap cap never saw
/// them and six 512 MB <c>Uint8Array</c>s succeeded. Each isolate now has a backing-store
/// ceiling, and going over it is Chromium's <c>RangeError</c>, not a dead process.
/// </summary>
public sealed class ArrayBufferBudgetTests
{
    private const long Limit = 64L * 1024 * 1024;

    private static RuntimeFixture Budgeted()
    {
        PocketCalculatorJsRuntime.ArrayBufferLimitForTests.Value = Limit;
        try
        {
            return RuntimeFixture.Setup("<html><body><canvas width=10 height=10></canvas></body></html>");
        }
        finally
        {
            PocketCalculatorJsRuntime.ArrayBufferLimitForTests.Value = null;
        }
    }

    private static string Eval(RuntimeFixture fixture, string script) =>
        fixture.Runtime.Evaluate(script)?.ToString() ?? "null";

    [Fact]
    public void AnUnconfiguredRuntimeHasADefaultCeiling()
    {
        Assert.True(PocketCalculatorJsRuntime.DefaultArrayBufferLimitBytes > 0);
        Assert.True(PocketCalculatorJsRuntime.DefaultArrayBufferLimitBytes <= PocketCalculatorJsRuntime.DefaultHeapLimitBytes);
    }

    [Fact]
    public void AnAllocationOverTheCeilingThrowsRangeError()
    {
        using var fixture = Budgeted();
        Assert.Equal(
            "RangeError: Array buffer allocation failed",
            Eval(fixture, "(() => { try { new ArrayBuffer(128 * 1024 * 1024); return 'allocated'; } catch (e) { return e.name + ': ' + e.message; } })()"));
        Assert.Equal(
            "RangeError",
            Eval(fixture, "(() => { try { new Uint8Array(128 * 1024 * 1024); return 'allocated'; } catch (e) { return e.name; } })()"));
    }

    [Fact]
    public void RetainedBuffersStopAtTheCeilingAndTheRuntimeRecovers()
    {
        using var fixture = Budgeted();
        string held = Eval(fixture, """
            (() => {
              globalThis.__held = [];
              try {
                for (let i = 0; i < 64; i++) globalThis.__held.push(new Uint8Array(8 * 1024 * 1024));
                return 'all';
              } catch (e) { return e.name + ' ' + globalThis.__held.length; }
            })()
            """);
        Assert.StartsWith("RangeError ", held);
        int count = int.Parse(held["RangeError ".Length..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(count, 1, (int)(Limit / (8 * 1024 * 1024)));

        // Dropping the references gives the budget back.
        Eval(fixture, "(globalThis.__held = null, 0)");
        fixture.Runtime.Engine.CollectGarbage(true);
        Assert.Equal("32", Eval(fixture, "String(new Uint8Array(32 * 1024 * 1024).length / (1024 * 1024))"));
    }

    [Fact]
    public void BuffersTheShimAllocatesOverTheCeilingThrowRangeError()
    {
        using var fixture = Budgeted();
        Eval(fixture, "(globalThis.__big = new Uint8Array(60 * 1024 * 1024), 0)");
        Assert.Equal(
            "RangeError: Array buffer allocation failed",
            Eval(fixture, "(() => { try { new TextEncoder().encode('x'.repeat(8 * 1024 * 1024)); return 'encoded'; } catch (e) { return e.name + ': ' + e.message; } })()"));
        Assert.Equal(
            "RangeError",
            Eval(fixture, "(() => { try { new Blob(['x'.repeat(8 * 1024 * 1024)]); return 'blob'; } catch (e) { return e.name; } })()"));
        Assert.Equal(
            "RangeError",
            Eval(fixture, "(() => { try { document.querySelector('canvas').getContext('2d').getImageData(0, 0, 2000, 2000); return 'image'; } catch (e) { return e.name; } })()"));

        // A canvas whose backing store cannot be allocated has no context, as when
        // context creation fails for any other reason.
        Assert.Equal(
            "null",
            Eval(fixture, "(() => { const c = document.createElement('canvas'); c.width = 2000; c.height = 2000; return String(c.getContext('2d')); })()"));

        Eval(fixture, "(globalThis.__big = null, 0)");
        fixture.Runtime.Engine.CollectGarbage(true);
        Assert.Equal("33554432", Eval(fixture, "String(new TextEncoder().encode('x'.repeat(32 * 1024 * 1024)).length)"));
    }

    [Fact]
    public async Task HostOpResultsOverTheCeilingRejectCleanly()
    {
        using var fixture = Budgeted();
        Eval(fixture, "(globalThis.__big = new Uint8Array(48 * 1024 * 1024), 0)");
        Eval(fixture, """
            ((async () => {
              try {
                const data = new Uint8Array(8 * 1024 * 1024);
                const key = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 128 }, false, ['encrypt']);
                const out = await crypto.subtle.encrypt({ name: 'AES-GCM', iv: new Uint8Array(12) }, key, data);
                globalThis.__result = 'encrypted ' + out.byteLength;
              } catch (e) { globalThis.__result = e.name + ': ' + e.message; }
            })(), 0)
            """);
        await fixture.Runtime.RunEventLoopBoundedAsync(2000);
        Assert.Equal("OperationError: RangeError: Array buffer allocation failed", Eval(fixture, "String(globalThis.__result)"));

        // The page keeps running once the memory is given back.
        Eval(fixture, "(globalThis.__big = null, 0)");
        fixture.Runtime.Engine.CollectGarbage(true);
        Assert.Equal("2", Eval(fixture, "1 + 1"));
    }
}
