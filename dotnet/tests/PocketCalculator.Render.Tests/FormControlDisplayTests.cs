// Chromium-verified facts for the display adjustments Chromium makes to buttons and form
// controls, and for the inline-level layout of a button. Every expectation was measured on
// Chromium 141 at a 1280x720 viewport. These have no counterpart in crates/obscura-render;
// see "Known deviations" in todo.md.
using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class FormControlDisplayTests
{
    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException($"fixture node {id}");

    private static (DomTree Tree, PreparedRender Prepared) Prepare(string html)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, new RenderResourceCache())
            ?? throw new InvalidOperationException("layout did not prepare");
        return (tree, prepared);
    }

    private static string Display(DomTree tree, PreparedRender prepared, string id) =>
        (prepared.ComputedStyle(Id(tree, id)) ?? throw new InvalidOperationException($"no style for #{id}"))["display"];

    /// <summary>
    /// LayoutTheme::AdjustStyle: a control that keeps its native appearance turns
    /// <c>inline</c> and every table display but <c>table</c> into <c>inline-block</c>, and
    /// <c>table</c> into <c>block</c>; block, flex, grid, contents and none are kept.
    /// </summary>
    [Fact]
    public void ButtonDisplayIsAdjustedLikeChromium()
    {
        (DomTree tree, PreparedRender prepared) = Prepare(
            """
            <!doctype html><html><body>
            <button id="inline" style="display:inline">a</button>
            <button id="block" style="display:block">a</button>
            <button id="flex" style="display:flex">a</button>
            <button id="grid" style="display:grid">a</button>
            <button id="contents" style="display:contents">a</button>
            <button id="none" style="display:none">a</button>
            <button id="iflex" style="display:inline-flex">a</button>
            <button id="table" style="display:table">a</button>
            <button id="itable" style="display:inline-table">a</button>
            <button id="cell" style="display:table-cell">a</button>
            <button id="row" style="display:table-row-group">a</button>
            <button id="caption" style="display:table-caption">a</button>
            <button id="flowroot" style="display:flow-root">a</button>
            </body></html>
            """);

        Assert.Equal("inline-block", Display(tree, prepared, "inline"));
        Assert.Equal("block", Display(tree, prepared, "block"));
        Assert.Equal("flex", Display(tree, prepared, "flex"));
        Assert.Equal("grid", Display(tree, prepared, "grid"));
        Assert.Equal("contents", Display(tree, prepared, "contents"));
        Assert.Equal("none", Display(tree, prepared, "none"));
        Assert.Equal("inline-flex", Display(tree, prepared, "iflex"));
        Assert.Equal("block", Display(tree, prepared, "table"));
        Assert.Equal("inline-block", Display(tree, prepared, "itable"));
        Assert.Equal("inline-block", Display(tree, prepared, "cell"));
        Assert.Equal("inline-block", Display(tree, prepared, "row"));
        Assert.Equal("inline-block", Display(tree, prepared, "caption"));
        Assert.Equal("flow-root", Display(tree, prepared, "flowroot"));
    }

    /// <summary>
    /// The other controls with native appearance take the same adjustment; a file or image
    /// input has none, and <c>appearance: none</c> switches it off (the computed display
    /// stays as written). <c>display: contents</c> on a replaced element or a form control
    /// computes to <c>none</c>; a button keeps it.
    /// </summary>
    [Fact]
    public void FormControlsFollowTheirAppearance()
    {
        (DomTree tree, PreparedRender prepared) = Prepare(
            """
            <!doctype html><html><body>
            <input id="text" style="display:inline"><select id="select" style="display:inline"><option>x</option></select>
            <textarea id="textarea" style="display:inline"></textarea>
            <input id="cell" style="display:table-cell"><select id="sflex" style="display:flex"><option>x</option></select>
            <textarea id="trow" style="display:table-row"></textarea><input id="submit" type=submit style="display:table">
            <meter id="meter"></meter><progress id="progress"></progress>
            <input id="file" type=file style="display:inline">
            <button id="none" style="display:inline;appearance:none">a</button>
            <button id="wnone" style="display:inline;-webkit-appearance:none">a</button>
            <button id="tnone" style="display:table;appearance:none">a</button>
            <button id="unset" style="display:inline;appearance:unset">a</button>
            <button id="revert" style="display:inline;appearance:none;appearance:revert">a</button>
            <input id="icontents" style="display:contents"><select id="scontents" style="display:contents"></select>
            <img id="imgcontents" style="display:contents"><video id="vcontents" style="display:contents"></video>
            <fieldset id="fcontents" style="display:contents"></fieldset>
            </body></html>
            """);

        Assert.Equal("inline-block", Display(tree, prepared, "text"));
        Assert.Equal("inline-block", Display(tree, prepared, "select"));
        Assert.Equal("inline-block", Display(tree, prepared, "textarea"));
        Assert.Equal("inline-block", Display(tree, prepared, "cell"));
        Assert.Equal("flex", Display(tree, prepared, "sflex"));
        Assert.Equal("inline-block", Display(tree, prepared, "trow"));
        Assert.Equal("block", Display(tree, prepared, "submit"));
        Assert.Equal("inline-block", Display(tree, prepared, "meter"));
        Assert.Equal("inline-block", Display(tree, prepared, "progress"));
        Assert.Equal("inline", Display(tree, prepared, "file"));
        Assert.Equal("inline", Display(tree, prepared, "none"));
        Assert.Equal("inline", Display(tree, prepared, "wnone"));
        Assert.Equal("table", Display(tree, prepared, "tnone"));
        Assert.Equal("inline", Display(tree, prepared, "unset"));
        Assert.Equal("inline-block", Display(tree, prepared, "revert"));
        Assert.Equal("none", Display(tree, prepared, "icontents"));
        Assert.Equal("none", Display(tree, prepared, "scontents"));
        Assert.Equal("none", Display(tree, prepared, "imgcontents"));
        Assert.Equal("none", Display(tree, prepared, "vcontents"));
        Assert.Equal("contents", Display(tree, prepared, "fcontents"));

        Dictionary<string, string> none = prepared.ComputedStyle(Id(tree, "none"))!;
        Assert.Equal("none", none["appearance"]);
        Assert.Equal("auto", prepared.ComputedStyle(Id(tree, "text"))!["appearance"]);
        Assert.Equal("none", prepared.ComputedStyle(Id(tree, "file"))!["appearance"]);
    }

    /// <summary>
    /// wikipedia.org's language button: <c>.lang-list-button { display: inline; margin: 0
    /// auto }</c> inside a <c>text-align: center</c> wrapper, holding an icon, a label and a
    /// second icon. Chromium lays it out as one atomic box, centred, on one line (493 of 900
    /// from a 190px offset, 30px tall). The port flowed it as an inline box, resolved its auto
    /// margins against the line (right edge), and measured its label one space short, so the
    /// trailing icon wrapped onto a second line.
    /// </summary>
    [Fact]
    public void InlineButtonIsOneCentredAtomicBox()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            """
            <!doctype html><html style="font-size:62.5%"><body style="margin:0;font-family:sans-serif;font-size:1.6rem">
            <div style="width:900px;margin:0 auto">
            <div id="wrap" style="text-align:center">
            <button id="btn" style="display:inline;margin:0 auto;padding:.6rem 1.2rem;border:1px solid #a2a9b1;font-size:1.4rem;font-weight:700;line-height:1">
            <i id="i1" style="display:inline-block;vertical-align:middle;width:16px;height:16px"></i>
            <span style="padding:0 .64rem;vertical-align:middle">Read Wikipedia in your language </span>
            <i id="i2" style="display:inline-block;vertical-align:middle;width:12px;height:12px"></i>
            </button>
            </div>
            </div>
            </body></html>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        Rect button = laid.Rects[Id(tree, "btn")];
        Rect first = laid.Rects[Id(tree, "i1")];
        Rect last = laid.Rects[Id(tree, "i2")];

        // Centred in the 900px wrapper at x=190 (Chromium: 493.09 for a 293.81px box; the
        // label's width depends on the face, so assert the centring itself).
        float centre = button.X + (button.Width / 2f);
        Assert.True(MathF.Abs(centre - 640f) < 1f, $"button centre {centre}, rect {button}");

        // One line: Chromium's box is 30.3px tall and both icons share its middle.
        Assert.True(button.Height < 32f, $"button wrapped: height {button.Height}");
        Assert.True(MathF.Abs(first.Y + (first.Height / 2f) - (last.Y + (last.Height / 2f))) < 1f,
            $"icons on different lines: {first} / {last}");
    }

    /// <summary>
    /// CSS 2.1 10.3.9: an inline-block's auto margins are 0. A centred span with
    /// <c>display: inline-block; margin: 0 auto</c> sat at the right edge of its line.
    /// </summary>
    [Fact]
    public void InlineBlockAutoMarginsAreZero()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            """
            <!doctype html><html><body style="margin:0">
            <div style="width:900px;text-align:center"><span id="ib" style="display:inline-block;width:200px;height:10px;margin:0 auto"></span></div>
            <div style="width:900px"><span id="left" style="display:inline-block;width:200px;height:10px;margin:0 auto"></span></div>
            </body></html>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));

        Assert.Equal(350f, laid.Rects[Id(tree, "ib")].X, 0.5f);
        Assert.Equal(0f, laid.Rects[Id(tree, "left")].X, 0.5f);
    }
}
