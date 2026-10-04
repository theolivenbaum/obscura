// Chromium-verified facts for the `font` shorthand given a CSS-wide keyword, and for the font
// a form control computes. Every expectation was measured on Chromium 141 (getComputedStyle on
// the same markup). These have no counterpart in crates/obscura-render; see "Known deviations"
// in todo.md.
using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class FontShorthandKeywordTests
{
    private const string Parent =
        "font: italic small-caps 700 condensed 20px/30px Georgia, serif";

    private static Dictionary<string, Dictionary<string, string>> Computed(string html, params string[] ids)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, new RenderResourceCache())
            ?? throw new InvalidOperationException("layout did not prepare");
        Dictionary<string, Dictionary<string, string>> result = [];
        foreach (string id in ids)
        {
            NodeId node = tree.GetElementById(id) ?? throw new InvalidOperationException($"no element #{id}");
            result[id] = prepared.ComputedStyle(node) ?? throw new InvalidOperationException($"no style for #{id}");
        }

        return result;
    }

    private static void AssertFont(
        Dictionary<string, string> computed,
        string family,
        string size,
        string weight,
        string style,
        string lineHeight,
        string variant,
        string stretch)
    {
        Assert.Equal(family, computed["font-family"]);
        Assert.Equal(size, computed["font-size"]);
        Assert.Equal(weight, computed["font-weight"]);
        Assert.Equal(style, computed["font-style"]);
        Assert.Equal(lineHeight, computed["line-height"]);
        Assert.Equal(variant, computed["font-variant"]);
        Assert.Equal(stretch, computed["font-stretch"]);
    }

    /// <summary>
    /// The ubiquitous reset `button, input, optgroup, select, textarea { font: inherit }`. The
    /// shorthand parser found no font-size token in `inherit` and dropped the declaration, so
    /// every control stayed on the user-agent 13.333px Arial. A drop-down select still
    /// computes `line-height: normal` (LayoutTheme::AdjustMenuListStyle).
    /// </summary>
    [Fact]
    public void FontInheritOnFormControlsInheritsEveryLonghand()
    {
        var computed = Computed(
            $$"""
            <!doctype html><html><head><style>
            button, input, optgroup, select, textarea { font: inherit }
            </style></head><body>
            <div style="{{Parent}}">
            <button id="b">B</button><input id="i"><select id="s"><option>x</option></select><textarea id="t"></textarea>
            </div></body></html>
            """,
            "b", "i", "s", "t");

        AssertFont(computed["b"], "Georgia, serif", "20px", "700", "italic", "30px", "small-caps", "75%");
        AssertFont(computed["i"], "Georgia, serif", "20px", "700", "italic", "30px", "small-caps", "75%");
        AssertFont(computed["s"], "Georgia, serif", "20px", "700", "italic", "normal", "small-caps", "75%");
        AssertFont(computed["t"], "Georgia, serif", "20px", "700", "italic", "30px", "small-caps", "75%");
    }

    /// <summary>
    /// `initial` is `normal 400 16px/normal "Times New Roman"`, `unset` is `inherit` (every
    /// font longhand is inherited), `revert` returns to the user-agent font, and a later
    /// `font: inherit` overrides an earlier `font` value.
    /// </summary>
    [Fact]
    public void FontShorthandTakesEveryCssWideKeyword()
    {
        var computed = Computed(
            $$"""
            <!doctype html><html><head><style>
            #initial { font: initial }
            #unset { font: unset }
            #revert { font: revert }
            #later { font: 12px/2 monospace }
            #later { font: inherit }
            </style></head><body>
            <div style="{{Parent}}">
            <button id="initial">B</button><button id="unset">B</button><button id="revert">B</button>
            <button id="later">B</button><button id="inline" style="font:inherit">B</button>
            </div></body></html>
            """,
            "initial", "unset", "revert", "later", "inline");

        AssertFont(computed["initial"], "\"Times New Roman\"", "16px", "400", "normal", "normal", "normal", "100%");
        AssertFont(computed["unset"], "Georgia, serif", "20px", "700", "italic", "30px", "small-caps", "75%");
        AssertFont(computed["revert"], "Arial", "13.3333px", "400", "normal", "normal", "normal", "100%");
        AssertFont(computed["later"], "Georgia, serif", "20px", "700", "italic", "30px", "small-caps", "75%");
        AssertFont(computed["inline"], "Georgia, serif", "20px", "700", "italic", "30px", "small-caps", "75%");
    }

    /// <summary>
    /// The user-agent `font: -webkit-small-control` resets weight, style, variant and stretch
    /// too, so a control inside a bold italic parent that inherits only its family is still
    /// 400 / normal. The port let weight and style inherit.
    /// </summary>
    [Fact]
    public void FormControlFontResetsWeightAndStyle()
    {
        var computed = Computed(
            $$"""
            <!doctype html><html><body>
            <div style="{{Parent}}"><button id="b" style="font-family:inherit">B</button></div>
            </body></html>
            """,
            "b");

        AssertFont(computed["b"], "Georgia, serif", "13.3333px", "400", "normal", "normal", "normal", "100%");
    }

    /// <summary>
    /// The font longhands' own CSS-wide keywords: `font-style: inherit` computed to `normal`
    /// and `line-height: initial` to the parent's value.
    /// </summary>
    [Fact]
    public void FontLonghandKeywords()
    {
        var computed = Computed(
            $$"""
            <!doctype html><html><body>
            <div style="{{Parent}}"><b id="b" style="font-style:inherit;line-height:initial;font-family:initial;font-stretch:inherit;font-variant:inherit">x</b>
            <span id="s" style="font-stretch:semi-expanded;font-variant-caps:all-small-caps">x</span></div>
            </body></html>
            """,
            "b", "s");

        Assert.Equal("italic", computed["b"]["font-style"]);
        Assert.Equal("normal", computed["b"]["line-height"]);
        Assert.Equal("\"Times New Roman\"", computed["b"]["font-family"]);
        Assert.Equal("75%", computed["b"]["font-stretch"]);
        Assert.Equal("small-caps", computed["b"]["font-variant"]);
        Assert.Equal("112.5%", computed["s"]["font-stretch"]);
        Assert.Equal("all-small-caps", computed["s"]["font-variant"]);
    }
}
