// Chromium-verified facts for a line box that carries an inline box whose font, line-height or
// vertical-align differs from the block's. They have no counterpart in `mod tests` in
// crates/obscura-render/src/inline.rs, which gives a line the largest `line-height` on it and so
// cannot express any of them; see "Known deviations" in todo.md.
//
// Every figure below was measured on Chromium 141 with the face named explicitly - an
// unqualified `monospace` on the capture host is DejaVu Sans Mono while this engine embeds
// Liberation Mono, so an unqualified fixture would compare two different typefaces.
using Obscura.Dom;
using Obscura.Render;
using Xunit;
using NodeId = Obscura.Dom.NodeId;

namespace Obscura.Render.Tests;

public class LineBoxFontMetricsTests
{
    private static DomTree Parse(string html) => HtmlParsing.ParseHtml(html);

    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException($"fixture node {id}");

    /// <summary>
    /// Liberation Mono's grid-fitted font box, as Chromium's canvas
    /// <c>fontBoundingBoxAscent</c>/<c>Descent</c> report it: 8px is 7/2, 10px 8/3, 12px 10/4,
    /// 14px 12/4, 16px 13/5, 20px 17/6, 24px 20/7, 32px 27/10 and 48px 40/14. Every host here is
    /// <c>16px/18px</c>, so the strut is 13 up and 5 down with no leading to spread.
    /// </summary>
    private const string HOST_STYLE = """
        <style>
            html,body{margin:0;padding:0}
            .w{width:600px;font:16px/18px 'Liberation Mono'}
            /* Isolate a case at y=0: this engine rounds layout to whole pixels, so a height
               read off a stacked box is a difference of two rounded offsets and a fractional
               Chromium figure could not be pinned to the pixel. */
            .o{position:absolute;top:0;left:0}
        </style>
        """;

    private static float Height(DomTree tree, DomLayout laid, string id) => laid.Rects[Id(tree, id)].Height;

    private static DomLayout Lay(DomTree tree) => RenderDom.LayoutDom(tree, (1000f, 400f));

    /// <summary>
    /// CSS 2.1 10.8.1: a line box is max(above the baseline) + max(below it) over every inline
    /// box on it plus the strut, so an inline box with a different font size grows the line in
    /// both directions - including a SMALLER one, which inherits the block's fixed line-height
    /// and spreads that leading around its own shorter font box.
    /// </summary>
    [Fact]
    public void AnInlineBoxOfADifferentSizeGrowsTheLineBox()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="s8">x<span style="font-size:8px">aa</span>x</div>
            <div class="w" id="s10">x<span style="font-size:10px">aa</span>x</div>
            <div class="w" id="s12">x<span style="font-size:12px">aa</span>x</div>
            <div class="w" id="s14">x<span style="font-size:14px">aa</span>x</div>
            <div class="w" id="s16">x<span style="font-size:16px">aa</span>x</div>
            <div class="w" id="s20">x<span style="font-size:20px">aa</span>x</div>
            <div class="w" id="s24">x<span style="font-size:24px">aa</span>x</div>
            <div class="w" id="s32">x<span style="font-size:32px">aa</span>x</div>
            <div class="w" id="s48">x<span style="font-size:48px">aa</span>x</div>
            """);
        DomLayout laid = Lay(tree);

        // Chromium 141: 20, 20, 19, 18, 18, 19, 20, 22, 27.
        Assert.Equal(20f, Height(tree, laid, "s8"), 2);
        Assert.Equal(20f, Height(tree, laid, "s10"), 2);
        Assert.Equal(19f, Height(tree, laid, "s12"), 2);
        Assert.Equal(18f, Height(tree, laid, "s14"), 2);
        Assert.Equal(18f, Height(tree, laid, "s16"), 2);
        Assert.Equal(19f, Height(tree, laid, "s20"), 2);
        Assert.Equal(20f, Height(tree, laid, "s24"), 2);
        Assert.Equal(22f, Height(tree, laid, "s32"), 2);
        Assert.Equal(27f, Height(tree, laid, "s48"), 2);
    }

    /// <summary>
    /// The halves are asymmetric: Blink floors the ascent to whole pixels once the half-leading
    /// is in and takes the descent as <c>line-height - ascent</c>. Rounding the pair the same
    /// way instead computes 19.5 for both the 20px and the 8px case, where Chromium reports 19
    /// for one and 20 for the other.
    /// </summary>
    [Fact]
    public void TheSameFontAtTheSameSizeStillGivesTheBlocksOwnLineHeight()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="plain">x<span>aa</span>x</div>
            <div class="w" id="textonly">aa</div>
            <div class="w" id="lh40">x<span style="line-height:40px">aa</span>x</div>
            <div class="w" id="lh0">x<span style="line-height:0">aa</span>x</div>
            <div class="w" id="lh24">x<span style="line-height:24px">aa</span>x</div>
            <div class="w" id="padded">x<span style="padding:10px 0">aa</span>x</div>
            """);
        DomLayout laid = Lay(tree);

        // Chromium 141: 18, 18, 40, 18, 24, 18.
        Assert.Equal(18f, Height(tree, laid, "plain"), 2);
        Assert.Equal(18f, Height(tree, laid, "textonly"), 2);
        Assert.Equal(40f, Height(tree, laid, "lh40"), 2);
        Assert.Equal(18f, Height(tree, laid, "lh0"), 2);
        Assert.Equal(24f, Height(tree, laid, "lh24"), 2);
        Assert.Equal(18f, Height(tree, laid, "padded"), 2);
    }

    /// <summary>
    /// The common case on a real page: one font size, a different family. Liberation Sans and
    /// Liberation Serif are 14/3 at 16px and DejaVu Sans is 15/4, against Liberation Mono's
    /// 13/5, so all three make an 18px line 19px tall.
    /// </summary>
    [Fact]
    public void AnInlineBoxOfADifferentFamilyGrowsTheLineBox()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="sans">x<span style="font-family:'Liberation Sans'">aa</span>x</div>
            <div class="w" id="serif">x<span style="font-family:'Liberation Serif'">aa</span>x</div>
            <div class="w" id="dejavu">x<span style="font-family:'DejaVu Sans'">aa</span>x</div>
            """);
        DomLayout laid = Lay(tree);

        Assert.Equal(19f, Height(tree, laid, "sans"), 2);
        Assert.Equal(19f, Height(tree, laid, "serif"), 2);
        Assert.Equal(19f, Height(tree, laid, "dejavu"), 2);
    }

    /// <summary>
    /// The block's own <c>line-height</c> does not bound the line: a 32px span makes it four
    /// pixels taller than the block asked for whatever the block asked for, because the span's
    /// floored ascent outruns the strut's while the strut still owns the descent.
    /// </summary>
    [Fact]
    public void TheBlocksLineHeightDoesNotCapTheLineBox()
    {
        DomTree tree = Parse("""
            <style>html,body{margin:0;padding:0}.w{width:600px;font-family:'Liberation Mono';font-size:16px}</style>
            <div class="w" id="lh0" style="line-height:0">x<span style="font-size:32px">aa</span>x</div>
            <div class="w" id="lh10" style="line-height:10px">x<span style="font-size:32px">aa</span>x</div>
            <div class="w" id="lh18" style="line-height:18px">x<span style="font-size:32px">aa</span>x</div>
            <div class="w" id="lh40" style="line-height:40px">x<span style="font-size:32px">aa</span>x</div>
            <div class="w" id="lhnormal" style="line-height:normal">x<span style="font-size:32px">aa</span>x</div>
            """);
        DomLayout laid = Lay(tree);

        // Chromium 141: 4, 14, 22, 44, 37. The strut's descent is negative in the first two.
        Assert.Equal(4f, Height(tree, laid, "lh0"), 2);
        Assert.Equal(14f, Height(tree, laid, "lh10"), 2);
        Assert.Equal(22f, Height(tree, laid, "lh18"), 2);
        Assert.Equal(44f, Height(tree, laid, "lh40"), 2);
        Assert.Equal(37f, Height(tree, laid, "lhnormal"), 2);
    }

    /// <summary>
    /// Every box on the line contributes, not just the block's direct children, and a line that
    /// wraps computes each of its lines separately.
    /// </summary>
    [Fact]
    public void EveryBoxOnTheLineContributes()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="nested">x<span style="font-size:24px">a<span style="font-size:32px">b</span></span>x</div>
            <div class="w" id="two">x<span style="font-size:32px">aa</span><span style="font-size:8px">bb</span>x</div>
            <div class="w" id="wrapped" style="width:60px">aaaa <span style="font-size:32px">bb</span> cccc</div>
            """);
        DomLayout laid = Lay(tree);

        // Chromium 141: 22 (the 32px descendant wins both halves), 24 (32px up, 8px down),
        // and 58 for three lines of 18 + 22 + 18.
        Assert.Equal(22f, Height(tree, laid, "nested"), 2);
        Assert.Equal(24f, Height(tree, laid, "two"), 2);
        Assert.Equal(58f, Height(tree, laid, "wrapped"), 2);
    }

    /// <summary>
    /// A length or percentage <c>vertical-align</c> moves the box's baseline off the line's, and
    /// the line grows to keep containing it. The percentage is of the box's OWN line-height.
    /// </summary>
    [Fact]
    public void ALengthOrPercentageVerticalAlignSizesTheLineBox()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="up">x<span style="vertical-align:10px">aa</span>x</div>
            <div class="w" id="up32">x<span style="font-size:32px;vertical-align:10px">aa</span>x</div>
            <div class="w" id="down">x<span style="vertical-align:-10px">aa</span>x</div>
            <div class="w" id="down32">x<span style="font-size:32px;vertical-align:-10px">aa</span>x</div>
            <div class="w" id="half">x<span style="vertical-align:50%">aa</span>x</div>
            <div class="w" id="half32">x<span style="font-size:32px;vertical-align:50%">aa</span>x</div>
            <div class="w" id="ownlh">x<span style="line-height:10px;vertical-align:50%">aa</span>x</div>
            """);
        DomLayout laid = Lay(tree);

        // Chromium 141: 28, 32, 28, 24, 27, 31, 19.
        Assert.Equal(28f, Height(tree, laid, "up"), 2);
        Assert.Equal(32f, Height(tree, laid, "up32"), 2);
        Assert.Equal(28f, Height(tree, laid, "down"), 2);
        Assert.Equal(24f, Height(tree, laid, "down32"), 2);
        Assert.Equal(27f, Height(tree, laid, "half"), 2);
        Assert.Equal(31f, Height(tree, laid, "half32"), 2);
        Assert.Equal(19f, Height(tree, laid, "ownlh"), 2);
    }

    /// <summary>
    /// <c>sub</c> and <c>super</c> are the KHTML rule Blink still carries: the shift is the
    /// PARENT's font size over 5 (or over 3) plus one, stored as a 1/64px LayoutUnit, and does
    /// not depend on the aligned box's own size at all. Both figures are therefore identical
    /// for a 16px and a 32px span.
    /// </summary>
    [Fact]
    public void SubAndSuperShiftByTheParentsFontSize()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w o" id="super">x<span style="vertical-align:super">aa</span>x</div>
            <div class="w o" id="super32">x<span style="font-size:32px;vertical-align:super">aa</span>x</div>
            <div class="w o" id="sub">x<span style="vertical-align:sub">aa</span>x</div>
            <div class="w o" id="sub32">x<span style="font-size:32px;vertical-align:sub">aa</span>x</div>
            """);
        DomLayout laid = Lay(tree);

        // Chromium 141: 24.328125, 28.328125, 22.1875 and 18.1875 - 16/3+1 and 16/5+1 truncated
        // to 1/64. This engine rounds layout to whole pixels (taffy's `round_layout`, which is
        // why every box here starts at y=0), so the fraction is what it rounds from.
        Assert.Equal(24f, Height(tree, laid, "super"), 2);
        Assert.Equal(28f, Height(tree, laid, "super32"), 2);
        Assert.Equal(22f, Height(tree, laid, "sub"), 2);
        Assert.Equal(18f, Height(tree, laid, "sub32"), 2);
    }

    /// <summary>
    /// <c>top</c> and <c>bottom</c> align to the line box rather than to a baseline, so they
    /// leave the max(above)/max(below) set entirely and can only make the line taller. A 48px
    /// span is 27px tall baseline-aligned and 18px tall aligned to the line.
    /// </summary>
    [Fact]
    public void LineAlignedBoxesOnlyEverGrowTheLine()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="top48">x<span style="font-size:48px;vertical-align:top">aa</span>x</div>
            <div class="w" id="bottom48">x<span style="font-size:48px;vertical-align:bottom">aa</span>x</div>
            <div class="w" id="texttop48">x<span style="font-size:48px;vertical-align:text-top">aa</span>x</div>
            <div class="w" id="textbottom48">x<span style="font-size:48px;vertical-align:text-bottom">aa</span>x</div>
            <div class="w" id="baseline48">x<span style="font-size:48px">aa</span>x</div>
            <div class="w" id="toptall">x<span style="font-size:32px;line-height:50px;vertical-align:top">aa</span>x</div>
            """);
        DomLayout laid = Lay(tree);

        // Chromium 141: 18 for all four aligned cases (their 18px leaded box already fits),
        // 27 for the same span on the baseline, and 50 once the box outgrows the line.
        Assert.Equal(18f, Height(tree, laid, "top48"), 2);
        Assert.Equal(18f, Height(tree, laid, "bottom48"), 2);
        Assert.Equal(18f, Height(tree, laid, "texttop48"), 2);
        Assert.Equal(18f, Height(tree, laid, "textbottom48"), 2);
        Assert.Equal(27f, Height(tree, laid, "baseline48"), 2);
        Assert.Equal(50f, Height(tree, laid, "toptall"), 2);
    }

    /// <summary>
    /// <c>text-top</c> and <c>text-bottom</c> align the box's LEADED height against the parent's
    /// RAW font box, which is why a 16px/40px block full of them is 51px tall and not 40: the
    /// strut's leaded box is 24 up and 16 down while its font box is only 13 and 5.
    /// </summary>
    [Fact]
    public void TextTopAndTextBottomAlignAgainstTheParentsFontBox()
    {
        DomTree tree = Parse("""
            <style>html,body{margin:0;padding:0}.w{width:600px;font:16px/40px 'Liberation Mono'}</style>
            <div class="w" id="tt8">x<span style="vertical-align:text-top;font-size:8px">aa</span>x</div>
            <div class="w" id="tt32">x<span style="vertical-align:text-top;font-size:32px">aa</span>x</div>
            <div class="w" id="tb8">x<span style="vertical-align:text-bottom;font-size:8px">aa</span>x</div>
            <div class="w" id="tb32">x<span style="vertical-align:text-bottom;font-size:32px">aa</span>x</div>
            <div class="w" id="ttzero">x<span style="vertical-align:text-top;font-size:32px;line-height:0">aa</span>x</div>
            """);
        DomLayout laid = Lay(tree);

        // Chromium 141: 51, 51, 51, 51 and 40.
        Assert.Equal(51f, Height(tree, laid, "tt8"), 2);
        Assert.Equal(51f, Height(tree, laid, "tt32"), 2);
        Assert.Equal(51f, Height(tree, laid, "tb8"), 2);
        Assert.Equal(51f, Height(tree, laid, "tb32"), 2);
        Assert.Equal(40f, Height(tree, laid, "ttzero"), 2);
    }

    /// <summary>
    /// <c>middle</c> puts the box's leaded midpoint at half the PARENT's x-height, so the same
    /// markup differs by family: Liberation Mono and Liberation Sans declare 1082/2048 and
    /// Liberation Serif 940/2048.
    /// </summary>
    [Fact]
    public void MiddleAlignsAgainstHalfTheParentsXHeight()
    {
        DomTree tree = Parse("""
            <style>html,body{margin:0;padding:0}.w{width:600px;line-height:40px;font-size:16px}.o{position:absolute;top:0;left:0}</style>
            <div class="w o" id="mono" style="font-family:'Liberation Mono'">x<span style="vertical-align:middle">aa</span>x</div>
            <div class="w o" id="mono32" style="font-family:'Liberation Mono'">x<span style="vertical-align:middle;font-size:32px">aa</span>x</div>
            <div class="w o" id="sans" style="font-family:'Liberation Sans'">x<span style="vertical-align:middle">aa</span>x</div>
            <div class="w o" id="serif" style="font-family:'Liberation Serif'">x<span style="vertical-align:middle">aa</span>x</div>
            """);
        DomLayout laid = Lay(tree);

        // Chromium 141: 40.234375, 40.234375, 40.765625 and 41.328125, rounded to whole pixels
        // the way this engine rounds layout.
        Assert.Equal(40f, Height(tree, laid, "mono"), 2);
        Assert.Equal(40f, Height(tree, laid, "mono32"), 2);
        Assert.Equal(41f, Height(tree, laid, "sans"), 2);
        Assert.Equal(41f, Height(tree, laid, "serif"), 2);
    }

    /// <summary>
    /// The used <c>line-height</c> is a 1/64px LayoutUnit before the leading is split around the
    /// font box, so a fractional one lands on a fraction of a pixel and not on the CSS value.
    /// </summary>
    [Fact]
    public void AFractionalLineHeightIsQuantizedToASixtyFourthOfAPixel()
    {
        DomTree tree = Parse("""
            <style>html,body{margin:0;padding:0}.w{width:600px;font-family:'Liberation Mono';font-size:16px}.o{position:absolute;top:0;left:0}</style>
            <div class="w o" id="ratio" style="line-height:1.3">x<span style="font-size:32px">aa</span>x</div>
            <div class="w o" id="half" style="line-height:18.5px">x<span style="font-size:32px">aa</span>x</div>
            <div class="w o" id="odd" style="line-height:25.7px">x<span style="font-size:32px">aa</span>x</div>
            """);
        DomLayout laid = Lay(tree);

        // Chromium 141: 41.59375, 22.5 and 30.703125 - the last is 25.703125 rounded up from
        // 25.7, not the 25.6875 a truncation would give, and truncating puts it at 30.6875 and
        // so a whole pixel lower once layout rounds. Rounded here the way layout rounds.
        Assert.Equal(42f, Height(tree, laid, "ratio"), 2);
        Assert.Equal(23f, Height(tree, laid, "half"), 2);
        Assert.Equal(31f, Height(tree, laid, "odd"), 2);
    }
}
