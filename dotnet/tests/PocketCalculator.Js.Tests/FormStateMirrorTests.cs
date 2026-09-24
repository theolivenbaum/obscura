using PocketCalculator.Dom;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The dirty form state bootstrap.js keeps in <c>_formValues</c> / <c>_formChecked</c> has to
/// reach the arena, or the renderer paints a field's placeholder after script set its value.
/// </summary>
/// <remarks>
/// DEVIATION from crates/obscura-js, which has no mirror; see
/// <c>PocketCalculator.Js.Runtime.FormStateMirror</c>.
/// </remarks>
public class FormStateMirrorTests
{
    private static (PocketCalculatorJsRuntime Runtime, DomTree Dom) Setup(string html)
    {
        DomTree dom = HtmlParsing.ParseHtml(html);
        PocketCalculatorJsRuntime runtime = new();
        runtime.SetDom(dom);
        runtime.SetUrl("http://example.com/test");
        runtime.RunPageInit();
        return (runtime, dom);
    }

    [Fact]
    public void AssigningValueReachesTheArena()
    {
        (PocketCalculatorJsRuntime runtime, DomTree dom) =
            Setup("<html><body><input id='f' placeholder='ph'></body></html>");
        using PocketCalculatorJsRuntime owned = runtime;

        NodeId field = dom.GetElementById("f")!.Value;
        Assert.False(dom.TryGetDirtyFormValue(field, out _));

        runtime.Evaluate("document.getElementById('f').value = 'typed'");

        // The IDL attribute went dirty; the content attribute must not have moved, which is
        // exactly why the renderer could not see it before.
        Assert.True(dom.TryGetDirtyFormValue(field, out string mirrored));
        Assert.Equal("typed", mirrored);
        Assert.Null(dom.GetNode(field)!.GetAttribute("value"));
        Assert.Equal("typed", runtime.Evaluate("document.getElementById('f').value")!.GetValue<string>());
    }

    [Fact]
    public void AssigningCheckedReachesTheArena()
    {
        (PocketCalculatorJsRuntime runtime, DomTree dom) =
            Setup("<html><body><input id='c' type='checkbox'></body></html>");
        using PocketCalculatorJsRuntime owned = runtime;

        NodeId box = dom.GetElementById("c")!.Value;
        Assert.False(dom.TryGetDirtyFormChecked(box, out _));

        runtime.Evaluate("document.getElementById('c').checked = true");
        Assert.True(dom.TryGetDirtyFormChecked(box, out bool mirrored));
        Assert.True(mirrored);

        runtime.Evaluate("document.getElementById('c').checked = false");
        Assert.True(dom.TryGetDirtyFormChecked(box, out bool cleared));
        Assert.False(cleared);
    }

    [Fact]
    public void TheMirroredMapKeepsItsOrdinaryObjectSemantics()
    {
        (PocketCalculatorJsRuntime runtime, DomTree dom) =
            Setup("<html><body><input id='f'><textarea id='t'></textarea></body></html>");
        using PocketCalculatorJsRuntime owned = runtime;

        // bootstrap.js reads the map back with `!== undefined`, so a read of an untouched node
        // has to stay undefined and a written one has to read back as itself.
        // (The map itself is closure state of bootstrap.js now, SECURITY.md I10, so the
        // untouched read is observed through the field it backs.)
        Assert.Equal(
            "",
            runtime.Evaluate("document.getElementById('f').value")!.GetValue<string>());
        runtime.Evaluate("document.getElementById('t').value = 'body text'");
        Assert.Equal(
            "body text",
            runtime.Evaluate("document.getElementById('t').value")!.GetValue<string>());

        // A textarea's value also lands in the tree as its text content, which is what the
        // renderer lays out; the mirror must agree with it.
        NodeId area = dom.GetElementById("t")!.Value;
        Assert.True(dom.TryGetDirtyFormValue(area, out string mirrored));
        Assert.Equal("body text", mirrored);
        Assert.Equal("body text", dom.TextContent(area));
    }
}
