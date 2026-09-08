using Obscura.Dom;
using Obscura.Render;
using Obscura.Render.Css;
using Xunit;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render.Tests;

/// <summary>
/// xUnit port of the in-file <c>#[cfg(test)] mod tests</c> of
/// <c>crates/obscura-render/src/style.rs</c>, in source order and with the same
/// names.
/// </summary>
public class ComputedStyleTests
{
    private static LayoutStyle Compute(string tag, string? css) => ComputedStyle.Compute(tag, css);

    private static bool Supports(string name, string value) => ComputedStyle.SupportsDeclaration(name, value);

    private static Layout.Line<Layout.GridPlacement> Line(Layout.GridPlacement start, Layout.GridPlacement end) =>
        new(start, end);

    private static Layout.GridPlacement LineIndex(short index) => Layout.GridPlacement.FromLineIndex(index);

    private static Layout.GridPlacement Span(ushort span) => Layout.GridPlacement.FromSpan(span);

    private static GridCalcExpression Owner(LayoutStyle style, int bucket, int index) =>
        (GridCalcExpression)ComputedStyle.GridCalcBuckets(style)[bucket][index];

    [Fact]
    public void DirectionParsesInheritedStateAndSupportsOnlyRealValues()
    {
        Assert.Equal(Layout.Direction.Rtl, Compute("div", "direction:rtl").Direction);
        Assert.Equal(Layout.Direction.Ltr, Compute("div", "direction:rtl;direction:initial").Direction);
        Assert.Null(Compute("div", "direction:rtl;direction:inherit").Direction);
        Assert.True(Supports("direction", "rtl"));
        Assert.True(Supports("direction", "ltr"));
        Assert.False(Supports("direction", "auto"));
    }

    [Fact]
    public void LogicalBordersResolveFinalDirectionAndCascadeWithPhysicalSides()
    {
        LayoutStyle laterLogical = Compute(
            "div",
            "direction:rtl;border-right:4px solid #2f9e44;border-inline-start:12px solid #862e9c");
        Assert.Equal(12.0f, laterLogical.Border.Right);
        Assert.Equal(new RgbaColor(0x86, 0x2e, 0x9c, 255), laterLogical.BorderModel.Colors.Right);

        LayoutStyle laterPhysical = Compute(
            "div",
            "direction:rtl;border-inline-start:12px solid #c2255c;border-right:4px solid #0b7285");
        Assert.Equal(4.0f, laterPhysical.Border.Right);
        Assert.Equal(new RgbaColor(0x0b, 0x72, 0x85, 255), laterPhysical.BorderModel.Colors.Right);

        LayoutStyle lateDirection = Compute("div", "border-inline-start:10px solid red;direction:rtl");
        Assert.Equal(0.0f, lateDirection.Border.Left);
        Assert.Equal(10.0f, lateDirection.Border.Right);
    }

    [Fact]
    public void LogicalBorderPairsExpandStartEndComponentsAndSupportsGrammar()
    {
        LayoutStyle style = Compute(
            "div",
            "border-inline:8px solid #f08c00;"
            + "border-inline-width:5px 9px;"
            + "border-block:3px dashed #1971c2;"
            + "border-block-color:#e03131 #2f9e44");
        Assert.Equal((5.0f, 9.0f), (style.Border.Left, style.Border.Right));
        Assert.Equal((3.0f, 3.0f), (style.Border.Top, style.Border.Bottom));
        Assert.Equal(BorderStyle.Solid, style.BorderModel.Styles.Left);
        Assert.Equal(BorderStyle.Dashed, style.BorderModel.Styles.Top);
        Assert.Equal(new RgbaColor(0xe0, 0x31, 0x31, 255), style.BorderModel.Colors.Top);
        Assert.Equal(new RgbaColor(0x2f, 0x9e, 0x44, 255), style.BorderModel.Colors.Bottom);

        foreach ((string property, string value) in new[]
        {
            ("border-inline", "1px solid red"),
            ("border-block-width", "1px 2px"),
            ("border-inline-start-style", "dashed"),
            ("border-block-end-color", "currentcolor"),
        })
        {
            Assert.True(Supports(property, value), $"{property}:{value}");
        }

        Assert.False(Supports("border-inline-start-width", "1px 2px"));
        Assert.False(Supports("border-block-color", "red blue green"));
        Assert.False(Supports("border-inline-style", "solid banana"));
    }

    [Fact]
    public void TableLayoutParsesResetsAndReportsOnlySupportedValues()
    {
        LayoutStyle fixedLayout = Compute("table", "table-layout:fixed");
        Assert.True(fixedLayout.TableLayoutFixed);

        LayoutStyle reset = Compute("table", "table-layout:fixed;table-layout:auto;table-layout:banana");
        Assert.False(reset.TableLayoutFixed);
        Assert.True(Supports("table-layout", "fixed"));
        Assert.True(Supports("table-layout", "auto"));
        Assert.False(Supports("table-layout", "fixed-ish"));
    }

    [Fact]
    public void SupportsReportsOnlyImplementedValueSubsets()
    {
        foreach (string value in new[] { "normal", "break-all", "keep-all", "break-word" })
        {
            Assert.True(Supports("word-break", value), value);
        }

        foreach (string property in new[] { "filter", "backdrop-filter", "-webkit-backdrop-filter" })
        {
            Assert.True(Supports(property, "none"), property);
            Assert.False(Supports(property, "blur(2px)"), property);
        }

        Assert.False(Supports("perspective", "800px"));
        Assert.False(Supports("contain", "paint"));
        Assert.False(Supports("content-visibility", "auto"));
        foreach (string content in new[]
        {
            "none",
            "\"new\"",
            "attr(data-label)",
            "counter(item) '. '",
            "url(icon.svg)",
        })
        {
            Assert.True(Supports("content", content), content);
        }

        Assert.False(Supports("content", "unknown-function(x)"));
        Assert.False(Supports("content", "counter(item, banana)"));
        Assert.False(Supports("content", "url(icon.svg) garbage()"));
        Assert.False(Supports("display", "grid;"));
        Assert.False(Supports("color", "red !important"));
        Assert.False(Supports("display", "banana"));
    }

    [Fact]
    public void SupportsVariableValuesAtParseTimeForImplementedProperties()
    {
        foreach ((string property, string value) in new[]
        {
            ("grid", "var(--tw)"),
            ("color", "var(--brand-color)"),
            ("width", "calc(100% - var(--gutter))"),
            ("transform", "var(--transform, garbage(1px))"),
            ("transform", "translateX(var(--offset, garbage(1px)))"),
        })
        {
            Assert.True(Supports(property, value), $"{property}:{value}");
        }

        foreach (string property in new[]
        {
            "filter",
            "backdrop-filter",
            "-webkit-backdrop-filter",
            "perspective",
            "contain",
            "content-visibility",
        })
        {
            Assert.False(
                Supports(property, "var(--effect)"),
                $"{property} must not advertise an unimplemented effect");
        }
    }

    [Fact]
    public void SupportsVariableValuesRejectMalformedVariableSyntax()
    {
        foreach (string value in new[]
        {
            "var(color)",
            "var(--)",
            "var(--x,!)",
            "var(--x,foo;bar)",
            "var(--x,})",
            "calc(1px + var(x))",
        })
        {
            Assert.False(Supports("grid", value), value);
        }

        Assert.True(Supports("--theme", "var(--base, red)"));
        Assert.False(Supports("--theme", "var(base)"));
        Assert.False(Supports("--", "red"));
    }

    [Fact]
    public void TransformSupportRejectsInvalidZTypesNonfiniteNumbersAndFakeMath()
    {
        foreach (string value in new[]
        {
            "translateZ(10px)",
            "translate3d(1px, 2px, 3rem)",
            "scale3d(1, 2, 3)",
            "rotate3d(0, 0, 1, 45deg)",
            "translateX(calc(10px + 5%))",
            "translateX(var(--offset))",
            "translateX(var(--offset, 10px))",
        })
        {
            Assert.True(Supports("transform", value), value);
        }

        foreach (string value in new[]
        {
            "translateZ(10%)",
            "translate3d(1px, 2px, 10%)",
            "scale3d(1, 2, garbage)",
            "scale3d(1, 2, NaN)",
            "scale(NaN)",
            "rotate(NaNdeg)",
            "rotate(calc(1deg / 0))",
            "rotate3d(NaN, 0, 1, 45deg)",
            "translateX(garbage(10px))",
            "translateX(calc(garbage))",
            "translateX(var(x))",
        })
        {
            Assert.False(Supports("transform", value), value);
        }
    }

    [Fact]
    public void ClipPathPolygonSupportsOnlyGeometryItCanResolve()
    {
        Assert.True(Supports("clip-path", "polygon(0 0, 100% 0, 50% 90px)"));
        Assert.True(Supports("-webkit-clip-path", "polygon(evenodd, 0 0, 10rem 0, 10rem 10vh) border-box"));
        foreach (string unsupported in new[]
        {
            "polygon(0 0, 100% 0, 50% 100%) content-box",
            "content-box polygon(0 0, 100% 0, 50% 100%)",
            "polygon(0, 100% 0)",
            "polygon(evenodd 0 0, 100% 0)",
            "polygon(0 0, calc(100% - 1px) 0, 0 100%)",
            "circle(50%)",
        })
        {
            Assert.False(
                Supports("clip-path", unsupported),
                $"@supports must not advertise unpainted clip geometry: {unsupported}");
        }

        LayoutStyle style = Compute("div", "clip-path:polygon(evenodd, -10px 0, 100% 0, 50% 2em)");
        ClipPathPolygon polygon = Assert.IsType<ClipPathPolygon>(style.ClipPath);
        Assert.Equal(ClipPathFillRule.Evenodd, polygon.FillRule);
        Assert.Equal(
            new List<(Dimension, Dimension)>
            {
                (Dimension.Px(-10.0f), Dimension.Px(0.0f)),
                (Dimension.Percent(1.0f), Dimension.Px(0.0f)),
                (Dimension.Percent(0.5f), Dimension.Em(2.0f)),
            },
            polygon.Points);
    }

    [Fact]
    public void ParsesDisplayAndSize()
    {
        LayoutStyle style = Compute("div", "display: flex; width: 200px; height: 50px");
        Assert.Equal(Display.Flex, style.Display);
        Assert.Equal(Dimension.Px(200.0f), style.Width);
        Assert.Equal(Dimension.Px(50.0f), style.Height);

        LayoutStyle flowRoot = Compute("div", "display: flow-root");
        Assert.Equal(Display.Block, flowRoot.Display);
        Assert.True(flowRoot.FlowRoot);

        LayoutStyle table = Compute("div", "display:none; display:table");
        Assert.Equal(Display.Block, table.Display);
        Assert.True(table.FlowRoot);
        Assert.True(table.IsTableBox);

        LayoutStyle inlineTable = Compute("div", "display:block; display:inline-table");
        Assert.Equal(Display.Inline, inlineTable.Display);
        Assert.True(inlineTable.IsInlineBlock);
        Assert.True(inlineTable.FlowRoot);
        Assert.True(inlineTable.IsTableBox);

        LayoutStyle tableCell = Compute("div", "display:block; display:table-cell");
        Assert.Equal(Display.Flex, tableCell.Display);
        Assert.True(tableCell.InternalFlexContainer);
        Assert.True(tableCell.IsTableCellBox);

        LayoutStyle minBefore = Compute("div", "min-width:50px;display:table-cell");
        LayoutStyle minAfter = Compute("div", "display:table-cell;min-width:50px");
        Assert.Equal(Dimension.Px(50.0f), minBefore.MinWidth);
        Assert.Equal(Dimension.Px(50.0f), minAfter.MinWidth);

        LayoutStyle resetTable = Compute("div", "display:table; display:grid");
        Assert.Equal(Display.Grid, resetTable.Display);
        Assert.False(resetTable.IsTableBox);

        LayoutStyle resetCell = Compute("div", "display:table-cell; display:block");
        Assert.Equal(Display.Block, resetCell.Display);
        Assert.False(resetCell.InternalFlexContainer);
        Assert.False(resetCell.IsTableCellBox);
    }

    [Fact]
    public void ParsesMulticolCountShorthandAndBreakAvoidance()
    {
        LayoutStyle shorthand = Compute("div", "columns: 240px 3; break-inside: avoid-column");
        Assert.Equal((ushort)3, shorthand.ColumnCount);
        Assert.True(shorthand.BreakInsideAvoid);

        LayoutStyle reset = Compute("div", "column-count: 4; columns: auto");
        Assert.Null(reset.ColumnCount);
    }

    [Fact]
    public void AnimationShorthandParsesTheFirstTimingContract()
    {
        LayoutStyle finite = Compute("div", "animation: dismiss-overlay .6s ease-out forwards");
        Assert.Equal("dismiss-overlay", finite.AnimationName);
        Assert.Equal(600.0f, finite.AnimationTiming.DurationMs);
        Assert.Equal(AnimationFillMode.Forwards, finite.AnimationTiming.FillMode);
        Assert.Equal(1.0f, finite.AnimationTiming.IterationCount);

        LayoutStyle infinite = Compute("div", "animation: pulse 1s linear infinite");
        Assert.Equal("pulse", infinite.AnimationName);
        Assert.Equal(1000.0f, infinite.AnimationTiming.DurationMs);
        Assert.Equal(AnimationFillMode.None, infinite.AnimationTiming.FillMode);
        Assert.True(float.IsInfinity(infinite.AnimationTiming.IterationCount));

        LayoutStyle calculated = Compute(
            "div",
            "animation:wave 1.2s linear infinite;"
            + "animation-delay:calc(.1s * -2.5);"
            + "animation-direction:alternate-reverse;"
            + "animation-fill-mode:both;"
            + "animation-play-state:paused");
        Assert.Equal(-250.0f, calculated.AnimationTiming.DelayMs);
        Assert.Equal(AnimationDirection.AlternateReverse, calculated.AnimationTiming.Direction);
        Assert.Equal(AnimationFillMode.Both, calculated.AnimationTiming.FillMode);
        Assert.Equal(AnimationPlayState.Paused, calculated.AnimationTiming.PlayState);
        Assert.Equal(525.0f, ComputedStyle.ParseAnimationTimeMs("calc(1s / 2 + 25ms)"));
        Assert.Null(ComputedStyle.ParseAnimationTimeMs("1s * 2"));

        LayoutStyle reset = Compute("div", "animation:fade 2s -1s 3 reverse both paused;animation:initial");
        Assert.Null(reset.AnimationName);
        Assert.Equal(AnimationTiming.Default, reset.AnimationTiming);
    }

    [Fact]
    public void DisplayContentsOverridesAnEarlierDisplayNone()
    {
        LayoutStyle style = Compute("div", "display:none; display:contents");
        Assert.Equal(Display.Block, style.Display);
        Assert.True(style.DisplayContents);
    }

    [Fact]
    public void DisplayCssWideValuesReplaceUaAndPriorProvenance()
    {
        LayoutStyle initial = Compute("td", "display:initial");
        Assert.Equal(Display.Inline, initial.Display);
        Assert.False(initial.InternalFlexContainer);
        Assert.False(initial.IsInlineBlock);
        Assert.False(initial.DisplayContents);

        LayoutStyle unset = Compute("div", "display:flex;display:unset");
        Assert.Equal(Display.Inline, unset.Display);
        Assert.False(unset.IsInlineBlock);

        LayoutStyle inherited = Compute("td", "display:contents;display:inherit");
        Assert.Equal(Display.Inline, inherited.Display);
        Assert.True(inherited.DisplayInherit);
        Assert.False(inherited.InternalFlexContainer);
        Assert.False(inherited.DisplayContents);

        LayoutStyle important = Compute("div", "display:block!important;display:contents");
        Assert.Equal(Display.Block, important.Display);
        Assert.False(important.DisplayContents);
    }

    [Fact]
    public void AuthoredDisplayReplacesInternalFlexProvenance()
    {
        LayoutStyle nativeCell = Compute("td", null);
        Assert.Equal(Display.Flex, nativeCell.Display);
        Assert.True(nativeCell.InternalFlexContainer);

        LayoutStyle authoredCell = Compute("td", "display:flex");
        Assert.Equal(Display.Flex, authoredCell.Display);
        Assert.False(authoredCell.InternalFlexContainer);

        LayoutStyle invalid = Compute("td", "display:bogus");
        Assert.True(invalid.InternalFlexContainer);

        LayoutStyle nativeImage = Compute("img", null);
        Assert.Equal(Display.Inline, nativeImage.Display);
        Assert.False(nativeImage.IsInlineBlock);

        LayoutStyle blockImage = Compute("img", "display:block");
        Assert.Equal(Display.Block, blockImage.Display);
        Assert.False(blockImage.IsInlineBlock);
    }

    [Fact]
    public void TableUaGeometryAndBorderCollapseParse()
    {
        LayoutStyle table = Compute("table", null);
        Assert.Equal(BoxSizing.BorderBox, table.BoxSizing);
        Assert.Equal((2.0f, 2.0f), table.BorderSpacing);
        Assert.False(table.BorderCollapse);

        LayoutStyle cell = Compute("td", null);
        Assert.Equal(new Edges(1.0f, 1.0f, 1.0f, 1.0f), cell.Padding);
        Assert.Null(cell.VerticalAlign);

        LayoutStyle collapsed = Compute("table", "border-spacing:8px; border-collapse:collapse");
        Assert.Equal((8.0f, 8.0f), collapsed.BorderSpacing);
        Assert.True(collapsed.BorderCollapse);
    }

    [Fact]
    public void ButtonUaStyleIsACenteredAtomicInlineBox()
    {
        LayoutStyle button = ComputedStyle.UaStyle("button");
        Assert.Equal(Display.Inline, button.Display);
        Assert.True(button.IsInlineBlock);
        Assert.Equal(Layout.AlignItems.Center, button.TextAlign);
        Assert.Equal(BoxSizing.BorderBox, button.BoxSizing);
        Assert.Equal(new Edges(1.0f, 6.0f, 1.0f, 6.0f), button.Padding);
    }

    [Fact]
    public void ImageUaStyleDoesNotInventAResponsiveSizeCap()
    {
        LayoutStyle image = ComputedStyle.UaStyle("img");
        Assert.Equal(Display.Inline, image.Display);
        Assert.False(image.IsInlineBlock);
        Assert.Equal(Dimension.Auto, image.MaxWidth);

        LayoutStyle authored = Compute("img", "max-width:100%");
        Assert.Equal(Dimension.Percent(1.0f), authored.MaxWidth);
    }

    [Fact]
    public void BorderNoneClearsNativeAndPerSideWidths()
    {
        LayoutStyle input = Compute("input", "border:none");
        Assert.Equal(Edges.Zero, input.Border);
        Assert.Equal(Sides<float>.All(BorderSides.MediumBorderWidth), input.BorderModel.SpecifiedWidths);

        LayoutStyle side = Compute("div", "border:3px solid red;border-left:none");
        Assert.Equal(3.0f, side.Border.Top);
        Assert.Equal(3.0f, side.Border.Right);
        Assert.Equal(3.0f, side.Border.Bottom);
        Assert.Equal(0.0f, side.Border.Left);
    }

    [Fact]
    public void BorderRadiusExpandsEllipsesAndScalesOverlaps()
    {
        LayoutStyle percentage = Compute("div", "border-radius:50%");
        Assert.Equal((40.0f, 20.0f), percentage.BorderModel.Radii.Resolve(80.0f, 40.0f).TopLeft);

        LayoutStyle elliptical = Compute("div", "border-radius:80px 60px 40px 20px/50px 40px 30px 10px");
        ResolvedBorderRadii resolved = elliptical.BorderModel.Radii.Resolve(100.0f, 50.0f);
        Assert.True(MathF.Abs(resolved.TopLeft.X - (80.0f * 5.0f / 7.0f)) < 0.001f);
        Assert.True(MathF.Abs(resolved.BottomRight.Y - (30.0f * 5.0f / 7.0f)) < 0.001f);

        LayoutStyle reset = Compute("div", "border-radius:50%;border-radius:revert");
        Assert.True(reset.BorderModel.Radii.IsZero());
    }

    [Fact]
    public void InvalidBorderDeclarationsRetainThePreviousCascadeValue()
    {
        LayoutStyle style = Compute(
            "div",
            "border:4px dashed red;"
            + "border-width:10%;border-style:solid nonsense;"
            + "border-color:red green blue purple orange;"
            + "border-radius:12px;border-radius:10px/");
        Assert.Equal(new Edges(4.0f, 4.0f, 4.0f, 4.0f), style.Border);
        Assert.Equal(Sides<BorderStyle>.All(BorderStyle.Dashed), style.BorderModel.Styles);
        Assert.Equal(Sides<RgbaColor?>.All(new RgbaColor(255, 0, 0, 255)), style.BorderModel.Colors);
        Assert.Equal(RadiusValue.Pixels(12.0f), style.BorderModel.Radii.TopLeft.X);
        Assert.False(Supports("border-width", "10%"));
        Assert.False(Supports("border-width", "4"));
    }

    [Fact]
    public void BorderAndOutlineShorthandsResetOmittedLonghands()
    {
        LayoutStyle style = Compute(
            "div",
            "border:10px dashed red;border:solid;outline:8px dotted blue;outline:green");
        Assert.Equal(new Edges(3.0f, 3.0f, 3.0f, 3.0f), style.Border);
        Assert.Equal(Sides<BorderStyle>.All(BorderStyle.Solid), style.BorderModel.Styles);
        Assert.Equal(Sides<RgbaColor?>.All(null), style.BorderModel.Colors);
        Assert.Equal(3.0f, style.Outline.SpecifiedWidth);
        Assert.Equal(BorderStyle.None, style.Outline.Style);
        Assert.Equal(new RgbaColor(0, 128, 0, 255), style.Outline.Color);
    }

    [Fact]
    public void ItemSelfAlignmentParsesAndResets()
    {
        LayoutStyle aligned = Compute("div", "align-self:safe center;justify-self:flex-end");
        Assert.Equal(Layout.AlignItems.SafeCenter, aligned.AlignSelf);
        Assert.Equal(Layout.AlignItems.FlexEnd, aligned.JustifySelf);

        LayoutStyle reset = Compute(
            "div",
            "align-self:center;align-self:auto;justify-self:end;justify-self:auto");
        Assert.Null(reset.AlignSelf);
        Assert.Null(reset.JustifySelf);

        LayoutStyle normal = Compute("div", "align-self:normal;justify-self:normal");
        Assert.Equal(Layout.AlignItems.Normal, normal.AlignSelf);
        Assert.Equal(Layout.AlignItems.Normal, normal.JustifySelf);

        LayoutStyle shorthand = Compute("div", "place-self:safe center flex-end");
        Assert.Equal(Layout.AlignItems.SafeCenter, shorthand.AlignSelf);
        Assert.Equal(Layout.AlignItems.FlexEnd, shorthand.JustifySelf);

        LayoutStyle parent = Compute(
            "div",
            "align-items:start;justify-items:safe end;place-items:end center");
        Assert.Equal(Layout.AlignItems.End, parent.AlignItems);
        Assert.Equal(Layout.AlignItems.Center, parent.JustifyItems);

        LayoutStyle content = Compute(
            "div",
            "align-content:space-between;place-content:safe center end");
        Assert.Equal(Layout.AlignContent.SafeCenter, content.AlignContent);
        Assert.Equal(Layout.AlignContent.End, content.JustifyContent);
    }

    [Fact]
    public void FontShorthandExpandsLayoutFieldsAndResetsOmissions()
    {
        LayoutStyle style = Compute(
            "div",
            "font-style:italic;font-weight:bold;line-height:2;"
            + "font:normal small-caps 500 64px/60px \"Google Sans\", sans-serif");
        Assert.Equal(64.0f, style.FontSize);
        Assert.Equal(LineHeight.Px(60.0f), style.LineHeight);
        Assert.Equal("500", style.FontWeight);
        Assert.Equal(false, style.FontStyleItalic);
        Assert.Equal("\"google sans\", sans-serif", style.FontFamily);

        LayoutStyle reset = Compute(
            "div",
            "font-style:italic;font-weight:bold;line-height:2;font:20px Arial");
        Assert.Equal(20.0f, reset.FontSize);
        Assert.Equal(LineHeight.Normal, reset.LineHeight);
        Assert.Equal("400", reset.FontWeight);
        Assert.Equal(false, reset.FontStyleItalic);
    }

    [Fact]
    public void FontWeightPreservesNumericValuesAndResolvesRelativeKeywords()
    {
        Assert.Equal("500", Compute("div", "font-weight:500").FontWeight);
        Assert.Equal("600", Compute("div", "font-weight:600").FontWeight);
        Assert.Equal("400", Compute("strong", "font-weight:normal").FontWeight);

        Assert.Equal(400, ComputedStyle.ComputedFontWeight("bolder", 99));
        Assert.Equal(400, ComputedStyle.ComputedFontWeight("bolder", 349));
        Assert.Equal(700, ComputedStyle.ComputedFontWeight("bolder", 350));
        Assert.Equal(900, ComputedStyle.ComputedFontWeight("bolder", 550));
        Assert.Equal(900, ComputedStyle.ComputedFontWeight("bolder", 900));
        Assert.Equal(99, ComputedStyle.ComputedFontWeight("lighter", 99));
        Assert.Equal(100, ComputedStyle.ComputedFontWeight("lighter", 100));
        Assert.Equal(100, ComputedStyle.ComputedFontWeight("lighter", 350));
        Assert.Equal(400, ComputedStyle.ComputedFontWeight("lighter", 550));
        Assert.Equal(700, ComputedStyle.ComputedFontWeight("lighter", 750));
        Assert.Equal(700, ComputedStyle.ComputedFontWeight("lighter", 900));
    }

    [Fact]
    public void FontWeightCssWideKeywordsOverrideHeadingUaWeight()
    {
        LayoutStyle inherited = Compute("h1", "font-weight:inherit");
        Assert.Equal("inherit", inherited.FontWeight);
        Assert.Equal(500, ComputedStyle.ComputedFontWeight(inherited.FontWeight, 500));

        LayoutStyle unset = Compute("h1", "font-weight:unset");
        Assert.Equal("inherit", unset.FontWeight);
        Assert.Equal(500, ComputedStyle.ComputedFontWeight(unset.FontWeight, 500));

        LayoutStyle initial = Compute("h1", "font-weight:initial");
        Assert.Equal("400", initial.FontWeight);
        Assert.Equal(400, ComputedStyle.ComputedFontWeight(initial.FontWeight, 500));
    }

    [Fact]
    public void VariableFontPropertiesParseCanonicallyAndAtomically()
    {
        LayoutStyle style = Compute(
            "span",
            "font-optical-sizing:none;\n"
            + "                   font-variation-settings:\"wght\" 500, \"\\6f psz\" 14, \"wght\" 650");
        Assert.Equal(FontOpticalSizing.None, style.FontOpticalSizing);
        Assert.Equal(
            new List<FontVariationSetting>
            {
                new("opsz", 14.0f),
                new("wght", 650.0f),
            },
            style.FontVariationSettings);

        LayoutStyle caseSensitive = Compute(
            "span",
            "font-variation-settings:\"wght\" 400, \"WGHT\" 700");
        Assert.Equal(
            new List<FontVariationSetting>
            {
                new("WGHT", 700.0f),
                new("wght", 400.0f),
            },
            caseSensitive.FontVariationSettings);

        foreach (string malformed in new[]
        {
            "\"abc\" 1",
            "wght 1",
            "\"wght\" 1,",
            "\"wght\" calc(1px)",
            "\"wght\" 1e999",
            "\"wégt\" 1",
        })
        {
            string css = $"font-variation-settings:\"opsz\" 20;font-variation-settings:{malformed}";
            LayoutStyle unchanged = Compute("span", css);
            Assert.Equal(
                new List<FontVariationSetting> { new("opsz", 20.0f) },
                unchanged.FontVariationSettings);
        }
    }

    [Fact]
    public void VariableFontCssWideValuesAndFontShorthandObeyCascade()
    {
        LayoutStyle inherited = Compute(
            "span",
            "font-optical-sizing:none;font-optical-sizing:unset;\n"
            + "                   font-variation-settings:\"wght\" 700;font-variation-settings:inherit");
        Assert.Null(inherited.FontOpticalSizing);
        Assert.Null(inherited.FontVariationSettings);

        LayoutStyle reset = Compute(
            "span",
            "font-optical-sizing:none;font-optical-sizing:initial;\n"
            + "                   font-variation-settings:\"wght\" 700;font-variation-settings:normal");
        Assert.Equal(FontOpticalSizing.Auto, reset.FontOpticalSizing);
        Assert.Equal([], reset.FontVariationSettings);

        LayoutStyle reverted = Compute(
            "span",
            "font-optical-sizing:none;font-optical-sizing:revert;\n"
            + "                   font-variation-settings:\"wght\" 700;font-variation-settings:revert-layer");
        Assert.Null(reverted.FontOpticalSizing);
        Assert.Null(reverted.FontVariationSettings);

        LayoutStyle shorthand = Compute(
            "span",
            "font-optical-sizing:none;font-variation-settings:\"opsz\" 22;\n"
            + "                   font:italic 500 20px/1.2 Inter");
        Assert.Equal(FontOpticalSizing.Auto, shorthand.FontOpticalSizing);
        Assert.Equal([], shorthand.FontVariationSettings);

        Assert.True(Supports("font-optical-sizing", "auto"));
        Assert.False(Supports("font-optical-sizing", "enabled"));
        Assert.True(Supports("font-variation-settings", "\"opsz\" 18, \"wght\" 500"));
        Assert.True(Supports("font-variation-settings", "\"opsz\" calc(18)"));

        LayoutStyle calculated = Compute(
            "span",
            "font-variation-settings:\"opsz\" calc(10 * 2), \"wght\" max(400, 500)");
        Assert.Equal(
            new List<FontVariationSetting>
            {
                new("opsz", 20.0f),
                new("wght", 500.0f),
            },
            calculated.FontVariationSettings);
        Assert.False(Supports("font-variation-settings", "\"opsz\" calc(18px)"));
    }

    [Fact]
    public void LetterSpacingPreservesUnitsResetsAndInvalidCascadeValues()
    {
        LayoutStyle relative = Compute("span", "letter-spacing:-.05em");
        Assert.Equal(Dimension.Em(-0.05f), relative.LetterSpacingRaw);
        Assert.Equal(true, relative.LetterSpacingNonNormal);

        LayoutStyle reset = Compute("span", "letter-spacing:4px;letter-spacing:normal");
        Assert.Equal(0.0f, reset.LetterSpacing);
        Assert.Equal(false, reset.LetterSpacingNonNormal);

        LayoutStyle invalid = Compute("span", "letter-spacing:3px;letter-spacing:calc(10% + 1px)");
        Assert.Equal(3.0f, invalid.LetterSpacing);
        Assert.Equal(true, invalid.LetterSpacingNonNormal);

        LayoutStyle inherited = Compute("span", "letter-spacing:unset");
        Assert.Null(inherited.LetterSpacing);
        Assert.Null(inherited.LetterSpacingNonNormal);
    }

    [Fact]
    public void TextIndentPreservesLengthsPercentagesAndInheritedResets()
    {
        Assert.Equal(Dimension.Em(2.0f), Compute("p", "text-indent:2em").TextIndent);
        Assert.Equal(Dimension.Percent(0.25f), Compute("p", "text-indent:25%").TextIndent);

        Assert.Null(Compute("p", "text-indent:18px;text-indent:unset").TextIndent);
        Assert.Equal(Dimension.Px(0.0f), Compute("p", "text-indent:18px;text-indent:initial").TextIndent);

        Assert.Equal(
            Dimension.Px(18.0f),
            Compute("p", "text-indent:18px;text-indent:10px hanging").TextIndent);
        Assert.True(Supports("text-indent", "-9999px"));
        Assert.True(Supports("text-indent", "25%"));
        Assert.False(Supports("text-indent", "10px hanging"));
    }

    [Fact]
    public void TruncationPropertiesParseStrictlyAndShareSupportTruth()
    {
        Assert.True(Supports("text-overflow", "clip"));
        Assert.True(Supports("text-overflow", "ellipsis"));
        Assert.False(Supports("text-overflow", "clip ellipsis"));
        Assert.False(Supports("text-overflow", "fade"));

        foreach (string value in new[] { "1", "2", "4294967295", "999999999999999999999", "none" })
        {
            Assert.True(Supports("-webkit-line-clamp", value), value);
        }

        foreach (string value in new[] { "0", "-1", "1.5", "2px", string.Empty })
        {
            Assert.False(Supports("-webkit-line-clamp", value), value);
        }

        foreach (string value in new[] { "horizontal", "vertical", "inline-axis", "block-axis" })
        {
            Assert.True(Supports("-webkit-box-orient", value), value);
        }

        Assert.True(Supports("display", "-webkit-box"));
        Assert.True(Supports("display", "-webkit-inline-box"));

        LayoutStyle invalid = Compute(
            "div",
            "text-overflow:ellipsis;text-overflow:fade;-webkit-line-clamp:2;-webkit-line-clamp:0");
        Assert.Equal(TextOverflow.Ellipsis, invalid.TextOverflow);
        Assert.Equal(2u, invalid.WebkitLineClamp);

        LayoutStyle saturated = Compute("div", "-webkit-line-clamp:999999999999999999999");
        Assert.Equal((uint)int.MaxValue, saturated.WebkitLineClamp);
    }

    [Fact]
    public void WebkitClampDisplayAdjustmentIsDeclarationOrderIndependent()
    {
        foreach (string css in new[]
        {
            "display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:2",
            "-webkit-line-clamp:2;display:-webkit-box;-webkit-box-orient:block-axis",
            "-webkit-box-orient:vertical;-webkit-line-clamp:2;display:-webkit-box",
        })
        {
            LayoutStyle style = Compute("div", css);
            Assert.Equal(Display.Block, style.Display);
            Assert.True(style.FlowRoot, css);
            Assert.False(style.IsInlineBlock, css);
        }

        LayoutStyle inline = Compute(
            "span",
            "display:-webkit-inline-box;-webkit-box-orient:vertical;-webkit-line-clamp:2");
        Assert.Equal(Display.Block, inline.Display);
        Assert.True(inline.FlowRoot);
        Assert.True(inline.IsInlineBlock);

        LayoutStyle horizontal = Compute(
            "div",
            "display:-webkit-box;-webkit-box-orient:horizontal;-webkit-line-clamp:2");
        Assert.Equal(Display.Flex, horizontal.Display);
        Assert.False(horizontal.FlowRoot);

        LayoutStyle cleared = Compute(
            "div",
            "display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:2;-webkit-line-clamp:none");
        Assert.Equal(Display.Flex, cleared.Display);
        Assert.False(cleared.FlowRoot);
    }

    [Fact]
    public void TextBreakLonghandsParseAliasAndCssWideValues()
    {
        LayoutStyle values = Compute(
            "div",
            "overflow-wrap:break-word;word-wrap:anywhere;word-break:break-all");
        Assert.Equal(OverflowWrap.Anywhere, values.OverflowWrap);
        Assert.Equal(WordBreak.BreakAll, values.WordBreak);

        LayoutStyle legacy = Compute("div", "word-break:break-word");
        Assert.Equal(WordBreak.BreakWord, legacy.WordBreak);

        foreach (string keyword in new[] { "inherit", "unset", "revert", "revert-layer" })
        {
            LayoutStyle style = Compute(
                "div",
                $"overflow-wrap:anywhere;overflow-wrap:{keyword};word-break:break-all;word-break:{keyword}");
            Assert.Null(style.OverflowWrap);
            Assert.Null(style.WordBreak);
        }

        LayoutStyle initial = Compute(
            "div",
            "overflow-wrap:anywhere;overflow-wrap:initial;word-break:break-all;word-break:initial");
        Assert.Equal(OverflowWrap.Normal, initial.OverflowWrap);
        Assert.Equal(WordBreak.Normal, initial.WordBreak);

        foreach (string property in new[] { "overflow-wrap", "word-wrap" })
        {
            Assert.True(Supports(property, "normal"));
            Assert.True(Supports(property, "break-word"));
            Assert.True(Supports(property, "anywhere"));
            Assert.False(Supports(property, "break-all"));
        }

        foreach (string value in new[] { "normal", "break-all", "keep-all", "break-word" })
        {
            Assert.True(Supports("word-break", value), value);
        }

        Assert.False(Supports("word-break", "anywhere"));
    }

    [Fact]
    public void NonzeroUnitlessFontSizeIsInvalid()
    {
        Assert.Equal(14.0f, Compute("div", "font-size:14px;font-size:.813").FontSize);
        Assert.Equal(0.0f, Compute("div", "font-size:0").FontSize);
    }

    [Fact]
    public void ContainingBlockPropertyTriggersAreIndependent()
    {
        Assert.True(Compute("div", "transform:rotate(0deg);filter:none").EstablishesPositioningContainingBlock());
        Assert.True(Compute("div", "filter:blur(0);transform:none").EstablishesPositioningContainingBlock());
        Assert.True(Compute("div", "contain:layout;content-visibility:visible;filter:none")
            .EstablishesPositioningContainingBlock());
        Assert.False(Compute("div", "contain:none;content-visibility:visible;filter:none;perspective:none")
            .EstablishesPositioningContainingBlock());
    }

    [Fact]
    public void ContainerPropertiesAndShorthandPreserveValues()
    {
        LayoutStyle defaults = Compute("div", null);
        Assert.Equal(ContainerType.Normal, defaults.ContainerType);
        Assert.Empty(defaults.ContainerNames);

        LayoutStyle longhands = Compute("div", "container-name:main sidebar;container-type:inline-size");
        Assert.Equal(ContainerType.InlineSize, longhands.ContainerType);
        Assert.Equal(new[] { "main", "sidebar" }, longhands.ContainerNames);

        LayoutStyle shorthand = Compute(
            "div",
            "container-name:old;container-type:size;container:main/inline-size");
        Assert.Equal(ContainerType.InlineSize, shorthand.ContainerType);
        Assert.Equal(new[] { "main" }, shorthand.ContainerNames);

        LayoutStyle nameOnly = Compute("div", "container-type:size;container:card");
        Assert.Equal(ContainerType.Normal, nameOnly.ContainerType);

        LayoutStyle inherited = Compute("div", "container:outer/size;container:inherit");
        Assert.True(inherited.ContainerTypeInherit);
        Assert.True(inherited.ContainerNamesInherit);

        LayoutStyle reset = Compute("div", "container:outer/size;container:inherit;container:none");
        Assert.False(reset.ContainerTypeInherit);
        Assert.False(reset.ContainerNamesInherit);
    }

    [Fact]
    public void ContainerSupportsValidationIsTypedAndShorthandAtomic()
    {
        LayoutStyle style = Compute(
            "div",
            "container:main/inline-size;container:broken/unknown;container-name:also not");
        Assert.Equal(ContainerType.InlineSize, style.ContainerType);
        Assert.Equal(new[] { "main" }, style.ContainerNames);
        Assert.False(style.EstablishesPositioningContainingBlock());
        Assert.True(Supports("container-type", "inline-size"));
        Assert.False(Supports("container-type", "inline"));
        Assert.True(Supports("container-name", "main sidebar"));
        Assert.False(Supports("container-name", "main not"));
        Assert.True(Supports("container", "main/inline-size"));
        Assert.False(Supports("container", "main/unknown"));
    }

    [Fact]
    public void MarginShorthandExpands()
    {
        LayoutStyle style = Compute("div", "margin: 10px 20px");
        Assert.Equal(new Edges(10.0f, 20.0f, 10.0f, 20.0f), style.Margin);
    }

    [Fact]
    public void BorderWidthAcceptsCssMathTokens()
    {
        LayoutStyle style = Compute(
            "button",
            "border-style:solid;border-width:calc(1 * 1px);border-color:rgba(208,217,251,.4)");
        Assert.Equal(new Edges(1.0f, 1.0f, 1.0f, 1.0f), style.Border);
        Assert.Equal(Sides<RgbaColor?>.All(new RgbaColor(208, 217, 251, 102)), style.BorderModel.Colors);

        LayoutStyle asymmetric = Compute(
            "div",
            "border-style:solid;border-width:calc(1px * 2) 3px calc(2px + 2px) 5px");
        Assert.Equal(new Edges(2.0f, 3.0f, 4.0f, 5.0f), asymmetric.Border);
    }

    [Fact]
    public void LonghandOverridesShorthand()
    {
        LayoutStyle style = Compute("div", "padding: 5px; padding-left: 30px");
        Assert.Equal(5.0f, style.Padding.Top);
        Assert.Equal(30.0f, style.Padding.Left);
    }

    [Fact]
    public void PercentagePaddingRecordedNotPixelized()
    {
        // A percentage padding must be deferred as a 0..1 fraction, not eagerly
        // converted to a bogus px value.
        LayoutStyle style = Compute("div", "padding-top: 56.25%");
        Assert.Equal(0.5625f, style.PaddingPercent[0]);
        Assert.Equal(0.0f, style.Padding.Top);

        LayoutStyle mixed = Compute("div", "padding: 10px 25%");
        Assert.Equal(10.0f, mixed.Padding.Top);
        Assert.Null(mixed.PaddingPercent[0]);
        Assert.Equal(0.25f, mixed.PaddingPercent[1]);
    }

    [Fact]
    public void PercentageMarginRecorded()
    {
        LayoutStyle style = Compute("div", "margin-left: 10%");
        Assert.Equal(0.1f, style.MarginPercent[3]);
        Assert.False(style.MarginAuto[3]);
    }

    [Fact]
    public void RelativeBoxEdgesRemainUnresolved()
    {
        LayoutStyle style = Compute(
            "div",
            "font-size:20px;margin:15vh auto 2em 10vw;padding:1rem 2vmin");
        Assert.Equal(Dimension.Vh(15.0f), style.MarginRelative[0]);
        Assert.True(style.MarginAuto[1]);
        Assert.Equal(Dimension.Em(2.0f), style.MarginRelative[2]);
        Assert.Equal(Dimension.Vw(10.0f), style.MarginRelative[3]);
        Assert.Equal(Dimension.Rem(1.0f), style.PaddingRelative[0]);
        Assert.Equal(Dimension.Vmin(2.0f), style.PaddingRelative[1]);
    }

    [Fact]
    public void LogicalInsetsMapToLtrPhysicalEdges()
    {
        LayoutStyle style = Compute(
            "div",
            "inset-inline:10px 20%;inset-block:1rem calc(100vh - 2px);inset-inline-start:3px");

        Assert.Equal(Dimension.Px(3.0f), style.Inset[3]);
        Assert.Equal(Dimension.Percent(0.2f), style.Inset[1]);
        Assert.Equal(Dimension.Rem(1.0f), style.Inset[0]);
        Assert.Null(style.Inset[2]);
        Assert.Equal("calc(100vh - 2px)", style.InsetExpressions[2]);
        Assert.True(Supports("inset-inline", "0"));
        Assert.True(Supports("inset-block-end", "2rem"));
    }

    [Fact]
    public void LogicalSizesShareHorizontalPhysicalSizeState()
    {
        LayoutStyle logicalLast = Compute(
            "div",
            "width:11px;inline-size:12px;"
            + "height:21px;block-size:22px;"
            + "min-width:31px;min-inline-size:32px;"
            + "min-height:41px;min-block-size:42px;"
            + "max-width:51px;max-inline-size:52px;"
            + "max-height:61px;max-block-size:62px");
        Assert.Equal(Dimension.Px(12.0f), logicalLast.Width);
        Assert.Equal(Dimension.Px(22.0f), logicalLast.Height);
        Assert.Equal(Dimension.Px(32.0f), logicalLast.MinWidth);
        Assert.Equal(Dimension.Px(42.0f), logicalLast.MinHeight);
        Assert.Equal(Dimension.Px(52.0f), logicalLast.MaxWidth);
        Assert.Equal(Dimension.Px(62.0f), logicalLast.MaxHeight);

        LayoutStyle physicalLast = Compute(
            "div",
            "inline-size:70px;width:71px;block-size:80px;height:81px");
        Assert.Equal(Dimension.Px(71.0f), physicalLast.Width);
        Assert.Equal(Dimension.Px(81.0f), physicalLast.Height);

        LayoutStyle deferred = Compute(
            "div",
            "inline-size:calc(50vw - 10px);"
            + "block-size:20vh;"
            + "min-inline-size:2rem;"
            + "max-block-size:calc(100vh - 5px)");
        Assert.Equal("calc(50vw - 10px)", deferred.SizeExpressions[0]);
        Assert.Equal(Dimension.Vh(20.0f), deferred.Height);
        Assert.Equal(Dimension.Rem(2.0f), deferred.MinWidth);
        Assert.Equal("calc(100vh - 5px)", deferred.SizeExpressions[5]);

        foreach (string name in new[]
        {
            "inline-size",
            "block-size",
            "min-inline-size",
            "min-block-size",
            "max-inline-size",
            "max-block-size",
        })
        {
            Assert.True(Supports(name, "10px"), name);
        }

        Assert.True(Supports("inline-size", "fit-content"));
        Assert.False(Supports("inline-size", "definitely-invalid"));
    }

    [Fact]
    public void CounterPropertiesParseOrderedNameIntegerPairs()
    {
        LayoutStyle style = Compute(
            "div",
            "counter-reset:chapter 2 line;counter-increment:chapter line 3;counter-set:folio 9");

        Assert.Equal(
            new List<(string, int)> { ("chapter", 2), ("line", 0) },
            Pairs(style.CounterReset));
        Assert.Equal(
            new List<(string, int)> { ("chapter", 1), ("line", 3) },
            Pairs(style.CounterIncrement));
        Assert.Equal(new List<(string, int)> { ("folio", 9) }, Pairs(style.CounterSet));
        Assert.True(Supports("counter-reset", "line"));
        Assert.True(Supports("counter-increment", "line 2"));

        LayoutStyle cleared = Compute("div", "counter-reset:line 4;counter-reset:none");
        Assert.Empty(cleared.CounterReset);

        static List<(string, int)> Pairs(List<CounterDirective> directives)
        {
            List<(string, int)> result = [];
            foreach (CounterDirective directive in directives)
            {
                result.Add((directive.Name, directive.Value));
            }

            return result;
        }
    }

    [Fact]
    public void ResponsiveGridRepetitionsAndShorthandRemainTyped()
    {
        LayoutStyle auto = Compute(
            "div",
            "display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr))");
        Assert.Single(auto.GridTemplateColumns);
        Assert.Equal(Layout.GridTemplateComponentKind.Repeat, auto.GridTemplateColumns[0].Kind);
        Assert.Equal(Layout.RepetitionCount.AutoFit, auto.GridTemplateColumns[0].Repetition!.Count);

        LayoutStyle shorthand = Compute("div", "display:grid;grid:auto-flow/repeat(3,1fr)");
        Assert.Equal(Layout.GridAutoFlow.Row, shorthand.GridAutoFlow);
        Assert.Equal(3, shorthand.GridTemplateColumns.Count);
    }

    [Fact]
    public void GridTrackMathIsDeferredToTheUsedAxisBasis()
    {
        LayoutStyle style = Compute(
            "div",
            "grid-template-columns:"
            + "minmax(0,calc((100% - 84rem)/2)) 1fr "
            + "minmax(0,max(10px,min(calc(25% - 5px),clamp(20px,10%,200px))))");
        List<object> owners = ComputedStyle.GridCalcBuckets(style)[0];
        Assert.Equal(2, owners.Count);

        ComputedStyle.SetGridCalcContext(style, 16.0f, 16.0f, 14.4f, 10.0f);
        Assert.True(MathF.Abs(ComputedStyle.ResolveGridCalc(Owner(style, 0, 0).Handle, 1440.0f) - 48.0f) < 0.01f);
        Assert.True(MathF.Abs(ComputedStyle.ResolveGridCalc(Owner(style, 0, 1).Handle, 1000.0f) - 100.0f) < 0.01f);
    }

    [Fact]
    public void WinningGridTrackDeclarationReleasesOverriddenCalcOwners()
    {
        LayoutStyle style = Compute(
            "div",
            "grid-template-columns:calc(50% - 10px);"
            + "grid-template-rows:calc(25% - 5px);"
            + "grid-template-columns:1fr");
        Assert.Empty(ComputedStyle.GridCalcBuckets(style)[0]);
        // A non-minmax breadth supplies both the minimum and maximum sizing
        // function, each with its own stable opaque handle.
        Assert.Equal(2, ComputedStyle.GridCalcBuckets(style)[1].Count);
    }

    [Fact]
    public void InheritedGridCalcPreservesParentEmContext()
    {
        LayoutStyle parent = Compute("div", "grid-auto-columns:calc(1em + 10%)");
        ComputedStyle.SetGridCalcContext(parent, 20.0f, 16.0f, 10.0f, 10.0f);

        LayoutStyle child = Compute("div", "grid-auto-columns:inherit");
        child.GridAutoColumns = [.. parent.GridAutoColumns];
        ComputedStyle.GridCalcBuckets(child)[2] = [.. ComputedStyle.GridCalcBuckets(parent)[2]];
        ComputedStyle.SetGridCalcContext(child, 40.0f, 16.0f, 10.0f, 10.0f);

        Assert.True(MathF.Abs(ComputedStyle.ResolveGridCalc(Owner(child, 2, 0).Handle, 100.0f) - 30.0f) < 0.01f);
    }

    [Fact]
    public void ImplicitGridTrackListsParseAndCssWideValuesResetThem()
    {
        LayoutStyle tracks = Compute(
            "div",
            "grid-auto-columns:50px minmax(20px,1fr);grid-auto-rows:min-content 25%;");
        Assert.Equal(2, tracks.GridAutoColumns.Count);
        Assert.Equal(2, tracks.GridAutoRows.Count);
        Assert.True(Supports("grid-auto-columns", "50px minmax(20px,1fr)"));
        Assert.True(Supports("grid-auto-rows", "min-content 25%"));
        Assert.False(Supports("grid-auto-columns", "repeat(2,50px)"));

        foreach (string keyword in new[] { "initial", "unset", "revert", "revert-layer" })
        {
            LayoutStyle reset = Compute(
                "div",
                $"grid-auto-columns:50px;grid-auto-columns:{keyword};grid-auto-rows:60px;grid-auto-rows:{keyword}");
            Assert.True(
                reset.GridAutoColumns.Count == 0 && reset.GridAutoRows.Count == 0,
                $"{keyword} must restore the initial automatic implicit track");
            Assert.False(reset.GridAutoColumnsInherit);
            Assert.False(reset.GridAutoRowsInherit);
        }

        LayoutStyle inherited = Compute("div", "grid-auto-columns:inherit;grid-auto-rows:inherit");
        Assert.True(inherited.GridAutoColumnsInherit);
        Assert.True(inherited.GridAutoRowsInherit);
    }

    [Fact]
    public void GridPlacementLonghandsPreserveTheOppositeSide()
    {
        LayoutStyle style = Compute(
            "div",
            "grid-column-start:2;grid-column-end:span 4;grid-row-start:3;grid-row-end:5");
        Assert.Equal(Line(LineIndex(2), Span(4)), style.GridColumn);
        Assert.Equal(Line(LineIndex(3), LineIndex(5)), style.GridRow);
    }

    [Fact]
    public void GridPlacementShorthandKeepsAutoSpanOutOfNamedLineResolution()
    {
        LayoutStyle style = Compute("div", "grid-column:auto / span 4");
        Assert.Equal(Line(Layout.GridPlacement.Auto, Span(4)), style.GridColumn);
        Assert.Null(style.GridColumnRaw);

        LayoutStyle named = Compute("div", "grid-column:content-start / content-end");
        Assert.Equal("content-start / content-end", named.GridColumnRaw);
        Assert.Null(named.GridColumn);

        LayoutStyle reset = Compute("div", "grid-column:2 / span 4;grid-column:initial");
        Assert.Equal(Line(Layout.GridPlacement.Auto, Layout.GridPlacement.Auto), reset.GridColumn);
        Assert.Null(reset.GridColumnRaw);
    }

    [Fact]
    public void GridAreaExpandsSlashPlacementsAndNamedAreaDefaults()
    {
        LayoutStyle overlay = Compute("div", "grid-area:1 / 2 / 3 / 4");
        Assert.Equal(Line(LineIndex(1), LineIndex(3)), overlay.GridRow);
        Assert.Equal(Line(LineIndex(2), LineIndex(4)), overlay.GridColumn);
        Assert.Null(overlay.GridAreaName);

        LayoutStyle named = Compute("div", "grid-area:hero");
        Assert.Equal("hero", named.GridAreaName);
        Assert.Equal("hero / hero", named.GridRowRaw);
        Assert.Equal("hero / hero", named.GridColumnRaw);

        LayoutStyle reset = Compute("div", "grid-area:hero;grid-area:initial");
        Assert.Null(reset.GridAreaName);
        Assert.Equal(Line(Layout.GridPlacement.Auto, Layout.GridPlacement.Auto), reset.GridRow);
        Assert.Equal(Line(Layout.GridPlacement.Auto, Layout.GridPlacement.Auto), reset.GridColumn);

        foreach (string value in new[] { "2 foo", "foo 2", "calc(1)" })
        {
            LayoutStyle line = Compute("div", $"grid-area:{value}");
            Assert.Null(line.GridAreaName);
            Assert.Equal($"{value} / auto", line.GridRowRaw);
            Assert.Equal(Line(Layout.GridPlacement.Auto, Layout.GridPlacement.Auto), line.GridColumn);
        }

        LayoutStyle one = Compute("div", "grid-area:1");
        Assert.Equal(Line(LineIndex(1), Layout.GridPlacement.Auto), one.GridRow);
        Assert.Equal(Line(Layout.GridPlacement.Auto, Layout.GridPlacement.Auto), one.GridColumn);

        LayoutStyle mixed = Compute("div", "grid-area:hero / 2");
        Assert.Equal("hero / hero", mixed.GridRowRaw);
        Assert.Equal(Line(LineIndex(2), Layout.GridPlacement.Auto), mixed.GridColumn);

        LayoutStyle copiedColumnIdent = Compute("div", "grid-area:1 / col / 3");
        Assert.Equal(Line(LineIndex(1), LineIndex(3)), copiedColumnIdent.GridRow);
        Assert.Equal("col / col", copiedColumnIdent.GridColumnRaw);

        LayoutStyle uppercaseSpan = Compute("div", "grid-area:SPAN 2");
        Assert.Equal(Line(Span(2), Layout.GridPlacement.Auto), uppercaseSpan.GridRow);

        foreach (string invalid in new[] { "0", "span", "span 0", "span -1", "foo bar", "1 / inherit", "1.5" })
        {
            LayoutStyle style = Compute("div", $"grid-area:7 / 8 / 9 / 10;grid-area:{invalid}");
            Assert.Equal(Line(LineIndex(7), LineIndex(9)), style.GridRow);
            Assert.Equal(Line(LineIndex(8), LineIndex(10)), style.GridColumn);
            Assert.False(Supports("grid-area", invalid));
        }

        foreach (string valid in new[] { "hero", "1", "hero / 2", "1 / col / 3", "SPAN 2", "span foo" })
        {
            Assert.True(Supports("grid-area", valid), $"valid grid-area `{valid}` should pass @supports parsing");
        }
    }

    [Fact]
    public void ContextualCssMathUsesRuntimeGeometry()
    {
        (float Em, float Rem, float Vw, float Vh, float Base) context = (20.0f, 16.0f, 9.0f, 10.0f, 900.0f);
        Assert.Equal(
            225.0f,
            ComputedStyle.ResolveContextualLength(
                "min(25vw,350px)", context.Em, context.Rem, context.Vw, context.Vh, context.Base));
        Assert.Equal(
            270.0f,
            ComputedStyle.ResolveContextualLength(
                "clamp(200px,30vw,320px)", context.Em, context.Rem, context.Vw, context.Vh, context.Base));
        Assert.Equal(
            122.0f,
            ComputedStyle.ResolveContextualLength(
                "calc(10vw + 2rem)", context.Em, context.Rem, context.Vw, context.Vh, context.Base));
        Assert.Equal(
            -140.0f,
            ComputedStyle.ResolveContextualLength(
                "calc(35rem*-1/4)", context.Em, context.Rem, context.Vw, context.Vh, context.Base));
        Assert.Equal(
            250.0f,
            ComputedStyle.ResolveContextualLength(
                "calc(round(247px * 1, 10px))", context.Em, context.Rem, context.Vw, context.Vh, context.Base));
        float grouped = ComputedStyle.ResolveContextualLength(
            "calc(clamp(128px,92px + 7vw,188px) + (100vw - 48px)*43/440)",
            context.Em,
            context.Rem,
            context.Vw,
            context.Vh,
            context.Base)!.Value;
        Assert.True(
            MathF.Abs(grouped - 238.263_64f) < 0.001f,
            $"grouped calc should preserve the nested subtraction: {grouped}");
    }

    [Fact]
    public void UaDefaultsAndIgnoreUnknown()
    {
        LayoutStyle style = Compute("span", "color: red; ; bogus: ; display: none");
        Assert.Equal(Display.None, style.Display);
    }

    [Fact]
    public void BackgroundClipTextFlag()
    {
        LayoutStyle style = Compute("h1", "color: transparent; -webkit-background-clip: text");
        Assert.True(style.BackgroundClipText);

        LayoutStyle vendorFill = Compute(
            "h1",
            "color: black; -webkit-text-fill-color: transparent;"
            + "background: linear-gradient(90deg, red, blue);"
            + "-webkit-background-clip: text");
        Assert.Equal(new RgbaColor(0, 0, 0, 0), vendorFill.Color);
        Assert.True(vendorFill.BackgroundClipText);
        Assert.NotNull(vendorFill.BackgroundGradient);

        float legacyAngle = Compute(
                "span",
                "background:-webkit-linear-gradient(315deg,#42d392 25%,#647eff)")
            .BackgroundGradient!.Value.Angle;
        Assert.Equal(135.0f, legacyAngle);

        Assert.False(Compute("h1", "background-clip: border-box").BackgroundClipText);
        Assert.True(Compute("h1", "background-clip: text").BackgroundClipText);
    }

    [Fact]
    public void LightDarkUsesLightSchemeAndValidatesBothNestedBranches()
    {
        Assert.Equal(
            new RgbaColor(16, 32, 48, 255),
            CssColor.Parse("light-dark(rgb(16, 32, 48), hsl(0 100% 50%))"));
        Assert.Equal(
            new RgbaColor(32, 64, 96, 191),
            CssColor.Parse("LIGHT-DARK(color-mix(in srgb, #204060 75%, transparent), light-dark(white, black))"));

        LayoutStyle style = Compute(
            "div",
            "color:light-dark(#123456,#ffffff);"
            + "background-color:light-dark(rgb(1, 2, 3),rgb(4, 5, 6));"
            + "border-color:light-dark(red,blue)");
        Assert.Equal(new RgbaColor(0x12, 0x34, 0x56, 255), style.Color);
        Assert.Equal(new RgbaColor(1, 2, 3, 255), style.BackgroundColor);
        Assert.Equal(Sides<RgbaColor?>.All(new RgbaColor(255, 0, 0, 255)), style.BorderModel.Colors);
        Assert.True(Supports("color", "light-dark(rgb(1, 2, 3), color-mix(in srgb, white 50%, black))"));

        foreach (string malformed in new[]
        {
            "light-dark(red)",
            "light-dark(red, blue, green)",
            "light-dark(red,)",
            "light-dark(,blue)",
            "light-dark(red, rgb(1, 2, 3)",
            "light-dark(red garbage, blue)",
            "light-dark(red, definitely-not-a-color)",
            "light-dark(red, blue) trailing",
        })
        {
            Assert.True(
                CssColor.Parse(malformed) is null,
                $"malformed light-dark() must invalidate the declaration: {malformed}");
            Assert.False(Supports("color", malformed), $"@supports must reject malformed light-dark(): {malformed}");
        }
    }

    [Fact]
    public void BackgroundShorthandResetsOmittedLayers()
    {
        LayoutStyle style = Compute(
            "div",
            "background:#1971c2 url(icon.png);background-size:20px 20px;"
            + "background-position:center;background-clip:text;background:0");
        Assert.Null(style.BackgroundColor);
        Assert.Null(style.BackgroundGradient);
        Assert.Null(style.BackgroundConicGradient);
        Assert.Empty(style.BackgroundGradientLayers);
        Assert.Null(style.BackgroundImage);
        Assert.Null(style.BackgroundSize);
        Assert.Null(style.BackgroundSizeExpression);
        Assert.Null(style.BackgroundSizeFit);
        Assert.Equal(BackgroundPosition.Zero, style.BackgroundPosition);
        Assert.Equal(BackgroundOrigin.PaddingBox, style.BackgroundOrigin);
        Assert.Equal(BackgroundClip.BorderBox, style.BackgroundClip);
        Assert.False(style.BackgroundClipText);

        LayoutStyle cover = Compute("div", "background:url(hero.svg) center/cover no-repeat");
        Assert.Equal(ObjectFit.Cover, cover.BackgroundSizeFit);

        LayoutStyle contain = Compute("div", "background-size:contain");
        Assert.Equal(ObjectFit.Contain, contain.BackgroundSizeFit);

        LayoutStyle contextual = Compute(
            "a",
            "background:url(icon.svg) no-repeat 0 50% / calc(100% - 2rem) auto");
        Assert.Equal("calc(100% - 2rem) auto", contextual.BackgroundSizeExpression);
    }

    [Fact]
    public void BackgroundBoxLonghandsAndShorthandRetainIndependentGeometry()
    {
        LayoutStyle longhands = Compute("div", "background-origin:content-box;background-clip:padding-box");
        Assert.Equal(BackgroundOrigin.ContentBox, longhands.BackgroundOrigin);
        Assert.Equal(BackgroundClip.PaddingBox, longhands.BackgroundClip);
        Assert.False(longhands.BackgroundClipText);

        LayoutStyle shorthand = Compute(
            "div",
            "background:linear-gradient(90deg,red,blue) content-box padding-box no-repeat");
        Assert.Equal(BackgroundOrigin.ContentBox, shorthand.BackgroundOrigin);
        Assert.Equal(BackgroundClip.PaddingBox, shorthand.BackgroundClip);

        LayoutStyle oneBox = Compute("div", "background:red content-box");
        Assert.Equal(BackgroundOrigin.ContentBox, oneBox.BackgroundOrigin);
        Assert.Equal(BackgroundClip.ContentBox, oneBox.BackgroundClip);

        LayoutStyle text = Compute("h1", "background-origin:border-box;background-clip:text");
        Assert.Equal(BackgroundOrigin.BorderBox, text.BackgroundOrigin);
        Assert.Equal(BackgroundClip.Text, text.BackgroundClip);
        Assert.True(text.BackgroundClipText);

        Assert.True(Supports("background-origin", "border-box"));
        Assert.True(Supports("background-origin", "padding-box"));
        Assert.True(Supports("background-origin", "content-box"));
        Assert.False(Supports("background-origin", "text"));
    }

    [Fact]
    public void BackgroundGradientsKeepAuthoredLayerOrderAndKeywordCenters()
    {
        LayoutStyle style = Compute(
            "div",
            "background-image:"
            + "linear-gradient(180deg,transparent,white 85%),"
            + "radial-gradient(ellipse at top left,red,transparent 50%),"
            + "radial-gradient(ellipse at top right,blue,transparent 50%),"
            + "radial-gradient(ellipse at center right,lime,transparent 50%),"
            + "radial-gradient(ellipse at center left,fuchsia,transparent 50%)");
        Assert.Equal(5, style.BackgroundGradientLayers.Count);
        BackgroundGradientLayer.Linear linear =
            Assert.IsType<BackgroundGradientLayer.Linear>(style.BackgroundGradientLayers[0]);
        Assert.True(MathF.Abs(linear.Angle - 180.0f) < 0.001f);

        List<(float X, float Y)> centers = [];
        foreach (BackgroundGradientLayer layer in style.BackgroundGradientLayers)
        {
            if (layer is BackgroundGradientLayer.Radial radial)
            {
                centers.Add(radial.Center);
            }
        }

        Assert.Equal(
            new List<(float, float)> { (0.0f, 0.0f), (1.0f, 0.0f), (1.0f, 0.5f), (0.0f, 0.5f) },
            centers);
    }

    [Fact]
    public void RadialGradientsRetainExplicitEllipseRadiiAndExtentShape()
    {
        LayoutStyle explicitGeometry = Compute(
            "div",
            "background:radial-gradient(141.53% 114.68% at 87.46% 55.27%,#9a7cff 36.75%,#0e0aa200 100%)");
        Assert.Equal(
            new RadialGradientGeometry(
                RadialGradientShape.Ellipse,
                RadialGradientSize.Explicit(Dimension.Percent(1.4153f), Dimension.Percent(1.1468f))),
            explicitGeometry.BackgroundRadialGradientGeometry);
        Assert.Equal((0.8746f, 0.5527f), explicitGeometry.BackgroundRadialGradient!.Value.Center);

        LayoutStyle keyword = Compute(
            "div",
            "background-image:radial-gradient(circle closest-side at 25% 75%,red,blue)");
        Assert.Equal(
            new RadialGradientGeometry(RadialGradientShape.Circle, RadialGradientSize.ClosestSide),
            keyword.BackgroundRadialGradientGeometry);

        LayoutStyle lengths = Compute(
            "div",
            "background-image:radial-gradient(80px 2em at center,#fff 0%,#000 100%)");
        Assert.Equal(
            new RadialGradientGeometry(
                RadialGradientShape.Ellipse,
                RadialGradientSize.Explicit(Dimension.Px(80.0f), Dimension.Em(2.0f))),
            lengths.BackgroundRadialGradientGeometry);
    }

    [Fact]
    public void RadialGradientRejectsInvalidShapeRadiusCombinations()
    {
        foreach (string value in new[]
        {
            "radial-gradient(circle 50%,red,blue)",
            "radial-gradient(ellipse 10px,red,blue)",
            "radial-gradient(circle 10px 20px,red,blue)",
            "radial-gradient(-10px 20px,red,blue)",
        })
        {
            LayoutStyle style = Compute("div", $"background-image:{value}");
            Assert.True(
                style.BackgroundGradientLayers.Count == 0,
                $"invalid radial geometry must not become a painted layer: {value}");
        }
    }

    [Fact]
    public void RepeatingLinearGradientRetainsLengthStopsAndRepeatAxes()
    {
        LayoutStyle style = Compute(
            "div",
            "background-image:repeating-linear-gradient(315deg,"
            + "rgba(0,0,0,.05) 0,rgba(0,0,0,.05) 1px,"
            + "transparent 0,transparent 50%);"
            + "background-size:10px 10px;background-repeat:repeat-x no-repeat");
        Assert.Equal((true, false), style.BackgroundRepeat);
        BackgroundGradientLayer.Linear layer =
            Assert.IsType<BackgroundGradientLayer.Linear>(style.BackgroundGradientLayers[0]);
        Assert.True(layer.Repeating);
        Assert.Equal(new List<string?> { "0", "1px", "0", "50%" }, layer.StopPositions);
    }

    [Fact]
    public void BackgroundPositionPreservesSpriteOffsets()
    {
        LayoutStyle first = Compute("div", "background-position:0");
        Assert.Equal(
            BackgroundPosition.New(
                BackgroundPositionAxis.Pixels(0.0f),
                BackgroundPositionAxis.Percentage(0.5f)),
            first.BackgroundPosition);

        LayoutStyle selected = Compute("div", "background-position:-24px");
        Assert.Equal(
            BackgroundPosition.New(
                BackgroundPositionAxis.Pixels(-24.0f),
                BackgroundPositionAxis.Percentage(0.5f)),
            selected.BackgroundPosition);

        LayoutStyle edgeOffsets = Compute("div", "background-position:right 10px bottom 20px");
        Assert.Equal(
            BackgroundPosition.New(
                BackgroundPositionAxis.LengthPercentage(-10.0f, 1.0f),
                BackgroundPositionAxis.LengthPercentage(-20.0f, 1.0f)),
            edgeOffsets.BackgroundPosition);
    }

    [Fact]
    public void ConicBackgroundAndRepeatedDataSvgMaskArePreserved()
    {
        LayoutStyle style = Compute(
            "div",
            "background:conic-gradient(from 122deg at 50% 50%,"
            + "transparent 17%,#f627e3 25%,#6911d2 32%,transparent 91%);"
            + "mask-image:url(\"data:image/svg+xml,<svg viewBox='0 0 72 72'>"
            + "<g transform='translate(36 36) rotate(-60)'></g></svg>\");"
            + "mask-size:22px 22px;mask-repeat:repeat");
        (float angle, (float X, float Y) center, List<GradientStop> stops) =
            style.BackgroundConicGradient!.Value;
        Assert.Equal(122.0f, angle);
        Assert.Equal((0.5f, 0.5f), center);
        Assert.Equal(4, stops.Count);
        Assert.Equal((22.0f, 22.0f), style.MaskSize);
        Assert.Equal((true, true), style.MaskRepeat);
        string mask = Assert.IsType<string>(style.MaskImage);
        Assert.EndsWith("</svg>", mask, StringComparison.Ordinal);
        Assert.Contains("rotate(-60)", mask, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportantAndAuto()
    {
        LayoutStyle style = Compute("div", "width: 100px !important; height: auto");
        Assert.Equal(Dimension.Px(100.0f), style.Width);
        Assert.Equal(Dimension.Auto, style.Height);
    }

    [Fact]
    public void OverflowAxisCouplingRecomputesFromWinningSpecifiedValues()
    {
        LayoutStyle style = Compute(
            "div",
            "overflow-x:hidden;overflow-y:auto;overflow-y:visible;overflow-x:visible");
        Assert.False(style.OverflowClipX);
        Assert.False(style.OverflowClipY);
        Assert.False(style.OverflowHidden);
        Assert.False(style.OverflowScrollContainer);

        LayoutStyle clipped = Compute("div", "overflow-x:clip;overflow-y:visible");
        Assert.True(clipped.OverflowClipX);
        Assert.False(clipped.OverflowClipY);
        Assert.False(clipped.OverflowScrollContainer);
    }

    [Fact]
    public void OverflowRejectsInvalidValuesAndHandlesCssWideKeywordsAtomically()
    {
        LayoutStyle style = Compute(
            "div",
            "overflow-x:clip;overflow-x:nonsense;overflow:hidden;overflow:hidden visible auto");
        Assert.True(style.OverflowScrollX && style.OverflowScrollY);

        LayoutStyle reset = Compute("div", "overflow:hidden;overflow:initial");
        Assert.False(reset.OverflowHidden);
        Assert.False(reset.OverflowScrollContainer);

        LayoutStyle longhandReset = Compute("div", "overflow:hidden;overflow-x:initial");
        Assert.True(
            longhandReset.OverflowScrollX && longhandReset.OverflowScrollY,
            "visible/hidden must recompute to auto/hidden");

        foreach ((string property, string value) in new[]
        {
            ("overflow", "hidden"),
            ("overflow", "clip visible"),
            ("overflow", "initial"),
            ("overflow-x", "inherit"),
        })
        {
            Assert.True(Supports(property, value), $"{property}:{value}");
        }

        foreach ((string property, string value) in new[]
        {
            ("overflow", ""),
            ("overflow", "nonsense"),
            ("overflow", "hidden visible auto"),
            ("overflow", "inherit visible"),
            ("overflow-y", "hidden auto"),
        })
        {
            Assert.False(Supports(property, value), $"{property}:{value}");
        }
    }

    [Fact]
    public void BoxSizingParsesValuesAndCssWideKeywords()
    {
        Assert.Equal(BoxSizing.ContentBox, Compute("div", null).BoxSizing);
        Assert.Equal(BoxSizing.BorderBox, Compute("div", "box-sizing:border-box").BoxSizing);
        Assert.Equal(
            BoxSizing.ContentBox,
            Compute("div", "box-sizing:border-box;box-sizing:content-box").BoxSizing);
        Assert.Equal(BoxSizing.Inherit, Compute("div", "box-sizing:inherit").BoxSizing);
        foreach (string keyword in new[] { "initial", "unset", "revert", "revert-layer" })
        {
            Assert.Equal(
                BoxSizing.ContentBox,
                Compute("div", $"box-sizing:border-box;box-sizing:{keyword}").BoxSizing);
        }
    }

    [Fact]
    public void BoxSizingInitialRestoresContentBoxGeometryAfterUniversalReset()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            """
            <style>
                * { box-sizing: border-box }
                body { margin: 0 }
                .parent { width: 600px }
                .form {
                    box-sizing: initial;
                    width: 100%;
                    max-width: 435px;
                    padding: 15px;
                }
            </style>
            <div class="parent"><form id="form" class="form"></form></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        NodeId form = tree.QuerySelector("#form")!.Value;

        Assert.Equal(BoxSizing.ContentBox, laid.Styles[form].BoxSizing);
        Assert.Equal(465f, laid.Rects[form].Width);
    }

    [Fact]
    public void CalcWithMultiplyAndDivide()
    {
        // The exact shape MediaWiki uses to offset a TOC toggle button.
        Assert.Equal(-11.0f, ComputedStyle.ResolveLength("calc(-1 * 22px / 2)"));
        // Minifiers remove every optional space; a sign after `*` or `/` is unary.
        Assert.Equal(-8.75f, ComputedStyle.ResolveLength("calc(35px*-1/4)"));
    }

    [Fact]
    public void CalcAddAndSubtract()
    {
        Assert.Equal(749.0f, ComputedStyle.ResolveLength("calc(750px - 1px)"));
        Assert.Equal(15.0f, ComputedStyle.ResolveLength("calc(10px + 5px)"));
    }

    [Fact]
    public void VarWithFallbackResolvesToFallback()
    {
        Assert.Equal(16.0f, ComputedStyle.ResolveLength("var(--font-size-medium, 1rem)"));
    }

    [Fact]
    public void VarWithoutFallbackIsUnresolvable()
    {
        Assert.Null(ComputedStyle.ResolveLength("var(--unknown-token)"));
    }

    [Fact]
    public void MinAndMaxFunctions()
    {
        Assert.Equal(10.0f, ComputedStyle.ResolveLength("max(5px, 10px)"));
        Assert.Equal(5.0f, ComputedStyle.ResolveLength("min(5px, 10px)"));
    }

    [Fact]
    public void NestedVarCalcMaxLikeWikipediaIconSizing()
    {
        Assert.Equal(
            20.0f,
            ComputedStyle.ResolveLength("calc(max(calc(var(--font-size-medium,1rem) + 4px),10px))"));
    }

    [Fact]
    public void WidthPropertyResolvesCalcWithVar()
    {
        LayoutStyle style = Compute("div", "width: calc(var(--x, 10px) + 5px)");
        Assert.Equal(Dimension.Px(15.0f), style.Width);
    }

    [Fact]
    public void FlexShorthandTwoNumbers()
    {
        LayoutStyle style = Compute("div", "flex: 1 0");
        Assert.Equal(1.0f, style.FlexGrow);
        Assert.Equal(0.0f, style.FlexShrink);
    }

    [Fact]
    public void FlexShorthandKeywords()
    {
        LayoutStyle none = Compute("div", "flex: none");
        Assert.Equal(0.0f, none.FlexGrow);
        Assert.Equal(0.0f, none.FlexShrink);

        LayoutStyle auto = Compute("div", "flex: auto");
        Assert.Equal(1.0f, auto.FlexGrow);
        Assert.Equal(1.0f, auto.FlexShrink);
    }

    [Fact]
    public void FlexShorthandSingleNumberDefaultsShrinkToOne()
    {
        LayoutStyle style = Compute("div", "flex: 2");
        Assert.Equal(2.0f, style.FlexGrow);
        Assert.Equal(1.0f, style.FlexShrink);
    }

    [Fact]
    public void FlexFlowIsUnorderedResetsOmissionsAndRejectsAtomically()
    {
        foreach (string value in new[]
        {
            "column",
            "wrap",
            "column wrap",
            "wrap column",
            "row-reverse wrap-reverse",
            "WRAP COLUMN-REVERSE",
        })
        {
            Assert.True(Supports("flex-flow", value), value);
        }

        foreach (string value in new[]
        {
            "",
            "none",
            "row column",
            "nowrap wrap-reverse",
            "row wrap extra",
            "10px",
        })
        {
            Assert.False(Supports("flex-flow", value), value);
        }

        LayoutStyle directionOnly = Compute("div", "flex-wrap:wrap;flex-flow:column");
        Assert.Equal(Layout.FlexDirection.Column, directionOnly.FlexDirection);
        Assert.Equal(Layout.FlexWrap.NoWrap, directionOnly.FlexWrap);

        LayoutStyle wrapOnly = Compute("div", "flex-direction:column;flex-flow:wrap");
        Assert.Equal(Layout.FlexDirection.Row, wrapOnly.FlexDirection);
        Assert.Equal(Layout.FlexWrap.Wrap, wrapOnly.FlexWrap);

        LayoutStyle reversed = Compute("div", "flex-flow:wrap column-reverse");
        Assert.Equal(Layout.FlexDirection.ColumnReverse, reversed.FlexDirection);
        Assert.Equal(Layout.FlexWrap.Wrap, reversed.FlexWrap);

        LayoutStyle invalidLater = Compute("div", "flex-flow:column wrap;flex-flow:row column");
        Assert.Equal(Layout.FlexDirection.Column, invalidLater.FlexDirection);
        Assert.Equal(Layout.FlexWrap.Wrap, invalidLater.FlexWrap);
    }

    [Fact]
    public void BoxShadowOutsetParses()
    {
        LayoutStyle style = Compute("div", "box-shadow: 0 2px 8px rgba(0,0,0,.15)");
        BoxShadow shadow = style.BoxShadow!.Value;
        Assert.False(shadow.Inset);
        Assert.Equal(0.0f, shadow.OffsetX);
        Assert.Equal(2.0f, shadow.OffsetY);
        Assert.Equal(8.0f, shadow.Blur);
        Assert.Equal(0.0f, shadow.Spread);
        Assert.Equal(new RgbaColor(0, 0, 0, 38), shadow.Color);
    }

    [Fact]
    public void BoxShadowInsetParses()
    {
        LayoutStyle style = Compute("div", "box-shadow: inset 0 0 0 1px #ccc");
        BoxShadow shadow = style.BoxShadow!.Value;
        Assert.True(shadow.Inset);
        Assert.Equal(0.0f, shadow.OffsetX);
        Assert.Equal(0.0f, shadow.OffsetY);
        Assert.Equal(0.0f, shadow.Blur);
        Assert.Equal(1.0f, shadow.Spread);
        Assert.Equal(new RgbaColor(204, 204, 204, 255), shadow.Color);
    }

    [Fact]
    public void BoxShadowColorDefaultsToCurrentColor()
    {
        LayoutStyle style = Compute("div", "color: red; box-shadow: 1px 1px 2px");
        BoxShadow shadow = style.BoxShadow!.Value;
        Assert.Equal(new RgbaColor(255, 0, 0, 255), shadow.Color);
    }

    [Fact]
    public void BoxShadowNoneClears()
    {
        LayoutStyle style = Compute("div", "box-shadow: none");
        Assert.Null(style.BoxShadow);
    }
}
