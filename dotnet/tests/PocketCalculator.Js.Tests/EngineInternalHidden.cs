using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// SECURITY.md I10: ClearScript's <c>EngineInternal</c> global is visible to page
/// script, and Chromium has no such global, so it marks the engine.
/// </summary>
/// <remarks>
/// Open. ClearScript's own initialisation defines <c>EngineInternal</c> on the
/// global as non-configurable, non-writable and non-enumerable, so
/// <c>delete</c> fails and no <see cref="Microsoft.ClearScript.V8.V8ScriptEngineFlags"/>
/// option leaves it out. <c>in</c>, <c>typeof</c> and a descriptor lookup on the
/// global cannot be intercepted, so filtering <c>getOwnPropertyNames</c> would
/// hide it from one probe while adding a tell of its own. The first fact pins why
/// the obvious fix does not work; the second states the goal and is skipped until
/// ClearScript can create an engine without the global.
/// </remarks>
public sealed class EngineInternalHidden
{
    private const string Probe = """
        (() => {
          const leaks = [];
          if ('EngineInternal' in globalThis) leaks.push('in');
          if (Object.getOwnPropertyNames(globalThis).includes('EngineInternal')) leaks.push('names');
          if (Reflect.ownKeys(globalThis).includes('EngineInternal')) leaks.push('keys');
          if (Object.getOwnPropertyDescriptor(globalThis, 'EngineInternal') !== undefined) leaks.push('descriptor');
          if (typeof EngineInternal !== 'undefined') leaks.push('typeof');
          return leaks.join(',');
        })()
        """;

    [Fact]
    public void EngineInternalCannotBeDeleted()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var descriptor = fixture.Runtime.Evaluate("""
            (() => {
              const d = Object.getOwnPropertyDescriptor(globalThis, 'EngineInternal');
              return [d.configurable, d.writable, d.enumerable, delete globalThis.EngineInternal].join(',');
            })()
            """);
        Assert.Equal("false,false,false,false", descriptor!.GetValue<string>());
    }

    [Fact(Skip = "SECURITY.md I10, open: ClearScript defines EngineInternal non-configurable on the global and offers no option to omit it")]
    public void PageScriptCannotSeeEngineInternal()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        Assert.Equal(string.Empty, fixture.Runtime.Evaluate(Probe)!.GetValue<string>());

        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        using var frame = FrameRealm.Create(
            page.Runtime, 1, 0, "https://child.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);
        Assert.Equal(string.Empty, frame.Evaluate(Probe)!.GetValue<string>());
    }
}
