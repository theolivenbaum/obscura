using Obscura.Dom;
using Obscura.Render.Css;
using Xunit;
using NodeId = Obscura.Dom.NodeId;

namespace Obscura.Render.Tests;

/// <summary>
/// The native form-control widgets the port paints from its own code: a range input's thumb, a
/// checkbox/radio box, and a date/time input's read-only field text. All three are deviations
/// from <c>crates/obscura-render/src/paint.rs</c>, which paints none of them, so the expected
/// values here were read off Chromium 141 rather than off the reference engine.
/// </summary>
public class NativeControlPaintTests
{
    private static DomTree Parse(string html) => HtmlParsing.ParseHtml(html);

    private static NodeId Selector(DomTree tree, string selector) =>
        tree.QuerySelector(selector) ?? throw new InvalidOperationException($"no match for {selector}");

    /// <summary>Count pixels close to <paramref name="colour"/> inside a box.</summary>
    private static int CountNear(
        Pixmap pixmap,
        (int R, int G, int B) colour,
        int x0,
        int y0,
        int x1,
        int y1,
        int tolerance = 40)
    {
        int found = 0;
        for (uint y = (uint)Math.Max(y0, 0); y < (uint)Math.Max(y1, 0) && y < pixmap.Height; y++)
        {
            for (uint x = (uint)Math.Max(x0, 0); x < (uint)Math.Max(x1, 0) && x < pixmap.Width; x++)
            {
                if (pixmap.Pixel(x, y) is not { } pixel)
                {
                    continue;
                }

                if (Math.Abs(pixel.R - colour.R) + Math.Abs(pixel.G - colour.G)
                    + Math.Abs(pixel.B - colour.B) < tolerance)
                {
                    found++;
                }
            }
        }

        return found;
    }

    private static int CountNonWhite(Pixmap pixmap, int x0, int y0, int x1, int y1)
    {
        int found = 0;
        for (uint y = (uint)Math.Max(y0, 0); y < (uint)Math.Max(y1, 0) && y < pixmap.Height; y++)
        {
            for (uint x = (uint)Math.Max(x0, 0); x < (uint)Math.Max(x1, 0) && x < pixmap.Width; x++)
            {
                if (pixmap.Pixel(x, y) is { } pixel
                    && (pixel.R < 245 || pixel.G < 245 || pixel.B < 245))
                {
                    found++;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// A3. Script assigns <c>element.value</c>, which HTML keeps off the content attribute, so
    /// the renderer sees it only through the dirty-value mirror. Chromium paints that value in
    /// the field's own colour; before the mirror existed the control painted its placeholder.
    /// </summary>
    [Fact]
    public void ScriptAssignedValuePaintsInsteadOfThePlaceholder()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                html,body { margin:0; background:white }
                input { display:block; box-sizing:border-box; width:240px; height:40px;
                        padding:0; border:0; font-size:24px; background:white;
                        color:rgb(0,0,0) }
                input::placeholder { color:rgb(255,0,0) }
            </style></head><body>
                <input id="field" placeholder="placeholder">
            </body></html>
            """);
        NodeId field = Selector(tree, "#field");

        RenderResourceCache before = new();
        PreparedRender beforePrepared = RenderPaint.PrepareDom(tree, (260f, 60f), null, before)!;
        Pixmap beforePixmap = RenderPaint.PaintPrepared(tree, beforePrepared, before, (0f, 0f))!;
        Assert.True(
            CountNear(beforePixmap, (255, 0, 0), 0, 0, 240, 40) > 10,
            "with no value the placeholder must paint in the placeholder colour");

        // What `input.value = "typed"` leaves behind. The content attribute stays unset, which
        // is exactly why reading it was not enough.
        tree.SetDirtyFormValue(field, "typed");
        Assert.Null(tree.GetNode(field)!.GetAttribute("value"));

        RenderResourceCache after = new();
        PreparedRender afterPrepared = RenderPaint.PrepareDom(tree, (260f, 60f), null, after)!;
        Pixmap afterPixmap = RenderPaint.PaintPrepared(tree, afterPrepared, after, (0f, 0f))!;
        Assert.Equal(0, CountNear(afterPixmap, (255, 0, 0), 0, 0, 240, 40));
        Assert.True(
            CountNear(afterPixmap, (0, 0, 0), 0, 0, 240, 40) > 10,
            "the value must paint in the field's own colour");
    }

    /// <summary>
    /// A widget-shaped input has no text value to show. The general input arm used to paint the
    /// <c>value</c> attribute for every type, which put "50" beside a slider.
    /// </summary>
    [Fact]
    public void AWidgetInputDoesNotPaintItsValueAsText()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                html,body { margin:0; background:white }
                input { -webkit-appearance:none; display:block; margin:0;
                        width:200px; height:4px; background:white; border:0 }
            </style></head><body>
                <input type="range" min="0" max="100" value="50">
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (220f, 60f), null, resources)!;
        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;

        // The native track and knob are the only ink; no glyphs anywhere near the left edge,
        // which is where a painted "50" landed.
        Assert.Equal(0, CountNear(pixmap, (0, 0, 0), 0, 0, 30, 30, tolerance: 120));
    }

    /// <summary>
    /// A1. <c>::-webkit-slider-thumb</c> is matched and cascaded onto a synthesized thumb box,
    /// so the size, radius, background and border a page writes are the ones painted. The
    /// position along the track comes from <c>value</c> against <c>min</c>/<c>max</c>.
    /// </summary>
    [Theory]
    [InlineData("0", 0)]
    [InlineData("50", 92)]
    [InlineData("100", 184)]
    public void AuthorSliderThumbRulesDecideTheThumbBox(string value, int expectedLeft)
    {
        DomTree tree = Parse(
            $$"""
            <html><head><style>
                html,body { margin:0; background:white }
                .s { -webkit-appearance:none; display:block; margin:0; padding:0; border:0;
                     width:200px; height:4px; background:white }
                .s::-webkit-slider-thumb { -webkit-appearance:none; width:16px; height:24px;
                     border-radius:10px; background-color:rgb(255,255,255);
                     border:2px solid rgb(0,120,212) }
            </style></head><body>
                <input class="s" type="range" min="0" max="100" value="{{value}}">
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (220f, 60f), null, resources)!;

        LayoutStyle thumb = prepared.Layout.Styles[Selector(tree, ".s")].SliderThumbPseudo!;
        Assert.Equal(16f, thumb.Width.Value);
        Assert.Equal(24f, thumb.Height.Value);
        Assert.Equal(new RgbaColor(255, 255, 255, 255), thumb.BackgroundColor);

        // Chromium's UA sheet makes the thumb a border box, so a 16px width is the whole knob.
        Assert.Equal(BoxSizing.BorderBox, thumb.BoxSizing);

        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        int? leftmost = null;
        int? rightmost = null;
        for (uint y = 0; y < 40; y++)
        {
            for (uint x = 0; x < 220; x++)
            {
                if (pixmap.Pixel(x, y) is { } pixel
                    && Math.Abs(pixel.R - 0) + Math.Abs(pixel.G - 120) + Math.Abs(pixel.B - 212) < 60)
                {
                    leftmost = leftmost is { } left ? Math.Min(left, (int)x) : (int)x;
                    rightmost = rightmost is { } right ? Math.Max(right, (int)x) : (int)x;
                }
            }
        }

        Assert.NotNull(leftmost);

        // Within one pixel of Chromium, which places the thumb by
        // trackLeft + fraction * (trackWidth - thumbWidth).
        Assert.InRange(leftmost!.Value, expectedLeft - 1, expectedLeft + 1);
        Assert.InRange(rightmost!.Value - leftmost.Value + 1, 15, 17);
    }

    /// <summary>The thumb is the one part of a slider that is ink when the control has none.</summary>
    [Fact]
    public void AZeroHeightSliderStillPaintsItsThumb()
    {
        // The shape a page gets by drawing its own track: the input is a transparent overlay
        // with no height, inside a clipping ancestor.
        DomTree tree = Parse(
            """
            <html><head><style>
                html,body { margin:0; background:white }
                .box { position:relative; width:200px; height:40px; overflow:hidden }
                .s { -webkit-appearance:none; position:absolute; left:0; top:20px; margin:0;
                     padding:0; border:0; width:200px; height:0; background:transparent }
                .s::-webkit-slider-thumb { -webkit-appearance:none; width:16px; height:24px;
                     border-radius:10px; background-color:rgb(0,120,212); border:0 }
            </style></head><body>
                <div class="box"><input class="s" type="range" min="0" max="100" value="100"></div>
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (220f, 60f), null, resources)!;
        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;

        Assert.True(
            CountNear(pixmap, (0, 120, 212), 184, 8, 200, 32) > 100,
            "the thumb must paint at the right of the track even though the input has no height");
    }

    /// <summary>
    /// A2. A date input is filled with Chromium's read-only field text and is sized from it, so
    /// it neither paints an empty box nor collapses a shrink-to-fit ancestor.
    /// </summary>
    [Fact]
    public void ADateInputIsSizedAndPaintedFromItsFieldText()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                html,body { margin:0; background:white }
                input { display:block; margin:0 }
            </style></head><body>
                <input id="empty" type="date">
                <input id="filled" type="date" value="2024-03-07">
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (400f, 120f), null, resources)!;

        // Chromium 141 reports 125.33px for a date field at the UA font. The port shapes the
        // same text with its own engine and rounds the content width up, so it lands within a
        // pixel or two rather than exactly.
        Rect empty = prepared.Layout.Rects[Selector(tree, "#empty")];
        Assert.InRange(empty.Width, 123f, 128f);

        // Chromium's UA sheet puts the date/time controls in monospace.
        Assert.Equal("monospace", prepared.Layout.Styles[Selector(tree, "#empty")].FontFamily);

        Assert.Equal("mm/dd/yyyy", PaintNativeControls.FieldText("date", null));
        Assert.Equal("03/07/2024", PaintNativeControls.FieldText("date", "2024-03-07"));

        // A value that is not a valid date falls back to the empty-field text, as it does in
        // Chromium, rather than painting the raw attribute.
        Assert.Equal("mm/dd/yyyy", PaintNativeControls.FieldText("date", "not-a-date"));
        Assert.Equal("March 2024", PaintNativeControls.FieldText("month", "2024-03"));
        Assert.Equal("Week 07, 2024", PaintNativeControls.FieldText("week", "2024-W07"));
        Assert.Equal("01:30 PM", PaintNativeControls.FieldText("time", "13:30"));
        Assert.Equal(
            "03/07/2024, 01:30 PM",
            PaintNativeControls.FieldText("datetime-local", "2024-03-07T13:30"));

        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        Assert.True(
            CountNonWhite(pixmap, 0, 0, (int)empty.Width, (int)empty.Height) > 40,
            "the empty date field must paint its mm/dd/yyyy placeholder and picker indicator");
    }

    /// <summary>
    /// The layout half of A2: a percentage-width date control cannot resolve its width while
    /// intrinsic sizes are computed, so without an intrinsic minimum it contributes nothing and
    /// its shrink-to-fit ancestor collapses. Chromium measures 324px for the two-field picker
    /// this reduces.
    /// </summary>
    [Fact]
    public void APercentageWidthDateInputKeepsAShrinkToFitAncestorOpen()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                html,body { margin:0; background:white }
                .picker { display:inline-flex; align-items:center; gap:4px }
                .wrap { position:relative }
                input { width:100%; margin:0; padding:0; border:0; height:36px }
            </style></head><body>
                <div class="picker">
                    <div class="wrap"><input type="date"></div>
                    <span>to</span>
                    <div class="wrap"><input type="date"></div>
                </div>
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (800f, 120f), null, resources)!;

        Rect picker = prepared.Layout.Rects[Selector(tree, ".picker")];
        Rect field = prepared.Layout.Rects[Selector(tree, "input")];
        Assert.InRange(field.Width, 118f, 128f);
        Assert.True(
            picker.Width > (2f * field.Width),
            $"the picker must wrap both date fields, not collapse (was {picker.Width})");
    }

    /// <summary>
    /// The related defect: an unstyled checkbox and radio painted nothing at all, because the
    /// port had no native control painter. Chromium draws a 13x13 box with a grey border, and
    /// fills it with the accent colour when checked.
    /// </summary>
    [Fact]
    public void UnstyledCheckboxAndRadioPaintTheirNativeBox()
    {
        DomTree tree = Parse(
            """
            <html><head><style>html,body { margin:0; background:white }</style></head><body>
                <input id="off" type="checkbox"><input id="on" type="checkbox" checked>
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (120f, 60f), null, resources)!;
        Rect off = prepared.Layout.Rects[Selector(tree, "#off")];
        Rect on = prepared.Layout.Rects[Selector(tree, "#on")];
        Assert.Equal(13f, off.Width);
        Assert.Equal(13f, off.Height);

        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        Assert.True(
            CountNear(pixmap, (118, 118, 118), (int)off.X, (int)off.Y,
                (int)(off.X + off.Width), (int)(off.Y + off.Height)) > 8,
            "an unchecked checkbox paints a grey native border");
        Assert.True(
            CountNear(pixmap, (0, 117, 255), (int)on.X, (int)on.Y,
                (int)(on.X + on.Width), (int)(on.Y + on.Height)) > 40,
            "a checked checkbox paints the accent fill");
    }

    /// <summary>A script-assigned <c>checked</c> is dirty state too, and reaches paint the same way.</summary>
    [Fact]
    public void ScriptAssignedCheckedStatePaintsTheCheckedBox()
    {
        DomTree tree = Parse(
            """
            <html><head><style>html,body { margin:0; background:white }</style></head><body>
                <input id="box" type="checkbox">
            </body></html>
            """);
        NodeId box = Selector(tree, "#box");
        tree.SetDirtyFormChecked(box, true);
        Assert.Null(tree.GetNode(box)!.GetAttribute("checked"));

        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (120f, 60f), null, resources)!;
        Rect rect = prepared.Layout.Rects[box];
        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        Assert.True(
            CountNear(pixmap, (0, 117, 255), (int)rect.X, (int)rect.Y,
                (int)(rect.X + rect.Width), (int)(rect.Y + rect.Height)) > 40,
            "the accent fill must follow the dirty checked state");
    }
}
