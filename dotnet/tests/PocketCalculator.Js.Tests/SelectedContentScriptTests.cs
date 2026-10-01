using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// <c>&lt;selectedcontent&gt;</c> (customizable select, Chromium 141): the first one in a single
/// select mirrors the selected option's children. crates/obscura-dom and crates/obscura-js
/// have no such element. Expected values are Chromium 141's.
/// </summary>
public sealed class SelectedContentScriptTests
{
    private static string[] Evaluate(string html, string script)
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(html));
        rt.SetViewport(1280.0, 720.0);
        rt.RunPageInit();
        var result = rt.Evaluate(script) ?? throw new InvalidOperationException("no result");
        return [.. result.AsArray().Select(static node => node!.GetValue<string>())];
    }

    [Fact]
    public void ParsedAndScriptedSelectionIsMirrored()
    {
        string[] result = Evaluate(
            """
            <select id="s"><button><selectedcontent id="c"></selectedcontent></button><option>X<b>x</b></option><option value="Y">Y</option></select>
            <select multiple><button><selectedcontent id="m"></selectedcontent></button><option selected>A</option></select>
            <select><button><selectedcontent id="l"></selectedcontent></button><option>P<option selected>Q</select>
            """,
            """
            (() => {
              const c = document.getElementById("c"), s = document.getElementById("s"), out = [c.innerHTML];
              out.push(document.getElementById("m").innerHTML, document.getElementById("l").innerHTML);
              s.value = "Y"; out.push(c.innerHTML);
              s.options[0].selected = true; out.push(c.innerHTML);
              return out;
            })()
            """);
        Assert.Equal(["X<b>x</b>", "", "Q", "Y", "X<b>x</b>"], result);
    }
}
