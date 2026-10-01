using PocketCalculator.Dom;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Text control selection and the form-control interfaces, as Chromium 141 answers them.
/// </summary>
/// <remarks>
/// The Rust shim answers <c>selectionStart</c> null until something sets a selection, so
/// typing after <c>focus()</c> appended where Chromium inserts at the start, and aliases
/// <c>HTMLInputElement</c> and <c>HTMLSelectElement</c> to <c>Element</c>, so every element
/// was an instance of both and Puppeteer's <c>::-p-text()</c> read every element's
/// (empty) value instead of its text.
/// </remarks>
public class TextControlSelectionTests
{
    private static PocketCalculatorJsRuntime Setup(string html)
    {
        DomTree dom = HtmlParsing.ParseHtml(html);
        PocketCalculatorJsRuntime runtime = new();
        runtime.SetDom(dom);
        runtime.SetUrl("http://example.com/test");
        runtime.RunPageInit();
        return runtime;
    }

    /// <summary>Runs <paramref name="body"/> as a function body and answers what it returns.</summary>
    private static string Eval(PocketCalculatorJsRuntime runtime, string body) =>
        runtime.Evaluate("(function () { " + body + " })()")!.GetValue<string>();

    [Fact]
    public void ATextControlWithNoSelectionHasItsCaretAtTheStart()
    {
        using PocketCalculatorJsRuntime runtime = Setup(
            "<html><body><input id=a value=pre><textarea id=t>pre</textarea>"
            + "<input id=c type=checkbox><input id=e type=email value=x></body></html>");
        Assert.Equal("0,0", Eval(runtime, "const a = document.getElementById('a'); a.focus(); return a.selectionStart + ',' + a.selectionEnd"));
        Assert.Equal("0,0", Eval(runtime, "const t = document.getElementById('t'); return t.selectionStart + ',' + t.selectionEnd"));
        Assert.Equal("null,null", Eval(runtime, "const c = document.getElementById('c'); return String(c.selectionStart) + ',' + String(c.selectionEnd)"));
        Assert.Equal("null", Eval(runtime, "return String(document.getElementById('e').selectionStart)"));
    }

    [Fact]
    public void SettingTheValueMovesTheCaretToTheEnd()
    {
        using PocketCalculatorJsRuntime runtime = Setup("<html><body><input id=a value=pre></body></html>");
        Assert.Equal("5,5", Eval(runtime, "const a = document.getElementById('a'); a.value = 'hello'; return a.selectionStart + ',' + a.selectionEnd"));
        Assert.Equal("1,2", Eval(runtime, "const b = document.getElementById('a'); b.setSelectionRange(1, 2); b.value = 'hello'; return b.selectionStart + ',' + b.selectionEnd"));
    }

    [Fact]
    public void InputAndSelectInterfacesDiscriminate()
    {
        using PocketCalculatorJsRuntime runtime = Setup(
            "<html><body><input id=a><select id=s></select><p id=p></p></body></html>");
        Assert.Equal(
            "true,false,false,true,false,false,true,true",
            Eval(runtime, "const i = document.getElementById('a'), s = document.getElementById('s'), p = document.getElementById('p');"
                + "return [i instanceof HTMLInputElement, s instanceof HTMLInputElement, p instanceof HTMLInputElement,"
                + " s instanceof HTMLSelectElement, i instanceof HTMLSelectElement, p instanceof HTMLSelectElement,"
                + " document.createElement('input') instanceof HTMLInputElement, i instanceof HTMLElement].join(',')"));
    }
}
