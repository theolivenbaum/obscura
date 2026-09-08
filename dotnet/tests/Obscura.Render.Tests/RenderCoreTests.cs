using Xunit;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render.Tests;

/// <summary>
/// xUnit port of the in-file test modules of <c>crates/obscura-render/src/lib.rs</c>
/// (<c>mod image_capability_tests</c> and <c>mod tests</c>) plus
/// <c>crates/obscura-render/src/border.rs</c>'s <c>mod tests</c>, and contract tests over the
/// core render types those files declare.
/// </summary>
public class RenderCoreTests
{
    // ---------------------------------------------------------------- lib.rs
    // mod image_capability_tests

    [Fact]
    public void ImageMimeFilterUsesCaseInsensitiveEssenceAndParameters()
    {
        foreach (string supported in new[]
        {
            "image/apng",
            "image/bmp",
            "image/gif",
            "image/jpeg",
            "image/jpg",
            "image/png",
            "image/svg+xml",
            "image/vnd.microsoft.icon",
            "image/webp",
            "image/x-icon",
            " IMAGE/WEBP ; codecs=lossless ",
            "Image/Svg+Xml;charset=utf-8",
        })
        {
            Assert.True(ImageCapability.SourceTypeSupported(supported), supported);
        }

        foreach (string unsupported in new[]
        {
            "",
            "image/avif",
            "image/jxl",
            "image/png,image/webp",
            "text/html",
            ";image/png",
        })
        {
            Assert.False(ImageCapability.SourceTypeSupported(unsupported), unsupported);
        }
    }

    // ---------------------------------------------------------------- lib.rs
    // mod tests
    //
    // These six exercise `layout()`, which is blocked on the vendored taffy port
    // (Obscura.Render.Layout.TaffyTree) and, for the grid case, on style.rs `compute_style`.
    // The bodies are the faithful ports; drop the Skip once `RenderLayout.Layout` is real.

    private static LayoutStyle MakeBox(Display display, float w, float h) => new()
    {
        Display = display,
        Width = Dimension.Px(w),
        Height = Dimension.Px(h),
    };

    [Fact]
    public void BlockChildrenStackVertically()
    {
        // A 1000px-wide viewport, two fixed-size block children: they should stack top-to-bottom
        // at the expected y offsets.
        LayoutNode root = new(
            MakeBox(Display.Block, 1000f, 800f),
            null,
            [
                LayoutNode.Leaf(MakeBox(Display.Block, 1000f, 50f)),
                LayoutNode.Leaf(MakeBox(Display.Block, 1000f, 30f)),
            ]);
        NodeRect output = RenderLayout.Layout(root, (1000f, 800f));
        Assert.Equal(1000f, output.BorderBox.Width);
        Assert.Equal(2, output.Children.Count);
        Assert.Equal(50f, output.Children[0].BorderBox.Height);
        Assert.Equal(30f, output.Children[1].BorderBox.Height);
        // Second block begins where the first ended.
        Assert.True(
            MathF.Abs(output.Children[1].BorderBox.Y - output.Children[0].BorderBox.Y)
                >= output.Children[0].BorderBox.Height - 0.01f,
            $"blocks should stack: c0.y={output.Children[0].BorderBox.Y} c1.y={output.Children[1].BorderBox.Y}");
    }

    [Fact(Skip = "blocked on the taffy port and on style.rs compute_style")]
    public void GridCalcTracksResolveAgainstContainerWidth()
    {
        // Rust builds the grid style with `crate::style::compute_style("div", Some(...))`.
        // Port this once Obscura.Render.Style lands; it asserts that
        //   minmax(0,calc((100% - (50rem + 20vw))/2)) 1fr minmax(0,...)
        // resolves to 176 / 1088 at a 1440px viewport and 220 / 1000 at a 1000px viewport.
        Assert.Fail("port pending: needs style.rs compute_style and the taffy layout engine");
    }

    [Fact]
    public void BlockAutoMarginsAbsorbHorizontalFreeSpace()
    {
        LayoutStyle centered = new()
        {
            Display = Display.Block,
            Width = Dimension.Px(300f),
            Height = Dimension.Px(40f),
            MarginAuto = [false, true, false, true],
        };
        LayoutStyle pushedEnd = new()
        {
            Display = Display.Block,
            Width = Dimension.Px(200f),
            Height = Dimension.Px(40f),
            Margin = new Edges(0f, 50f, 0f, 0f),
            MarginAuto = [false, false, false, true],
        };
        LayoutNode root = new(
            MakeBox(Display.Block, 900f, 200f),
            null,
            [LayoutNode.Leaf(centered), LayoutNode.Leaf(pushedEnd)]);
        NodeRect output = RenderLayout.Layout(root, (900f, 200f));
        Assert.True(MathF.Abs(output.Children[0].BorderBox.X - 300f) < 0.01f);
        Assert.True(MathF.Abs(output.Children[1].BorderBox.X - 650f) < 0.01f);
    }

    [Fact]
    public void NegativeFlexMarginOverlaysWithoutShiftingItems()
    {
        LayoutStyle main = MakeBox(Display.Block, 900f, 200f);
        LayoutStyle sidebar = new()
        {
            Display = Display.Flex,
            Width = Dimension.Px(225f),
            Height = Dimension.Px(180f),
            Margin = new Edges(0f, 0f, 0f, -900f),
        };
        LayoutNode root = new(
            MakeBox(Display.Flex, 900f, 220f),
            null,
            [LayoutNode.Leaf(main), LayoutNode.Leaf(sidebar)]);
        NodeRect output = RenderLayout.Layout(root, (900f, 220f));
        Assert.True(
            MathF.Abs(output.Children[0].BorderBox.X) < 0.01f,
            $"main shifted to {output.Children[0].BorderBox}");
        Assert.True(
            MathF.Abs(output.Children[1].BorderBox.X) < 0.01f,
            $"overlay shifted to {output.Children[1].BorderBox}");
    }

    [Fact]
    public void FlexRowLaysOutHorizontally()
    {
        LayoutNode root = new(
            new LayoutStyle
            {
                Display = Display.Flex,
                Width = Dimension.Px(600f),
                Height = Dimension.Px(100f),
            },
            null,
            [
                LayoutNode.Leaf(MakeBox(Display.Block, 200f, 100f)),
                LayoutNode.Leaf(MakeBox(Display.Block, 200f, 100f)),
            ]);
        NodeRect output = RenderLayout.Layout(root, (600f, 400f));
        Assert.Equal(600f, output.BorderBox.Width);
        Assert.Equal(2, output.Children.Count);
        // In a row the second child is to the right of the first.
        Assert.True(
            output.Children[1].BorderBox.X > output.Children[0].BorderBox.X,
            $"flex row should place children horizontally: c0.x={output.Children[0].BorderBox.X} c1.x={output.Children[1].BorderBox.X}");
    }

    [Fact]
    public void PaddingExpandsContentBoxButNotBorderBox()
    {
        LayoutNode contentBox = new(
            new LayoutStyle
            {
                Display = Display.Block,
                Width = Dimension.Px(100f),
                Height = Dimension.Px(100f),
                Padding = new Edges(10f, 10f, 10f, 10f),
            },
            null,
            []);
        NodeRect contentOut = RenderLayout.Layout(contentBox, (1000f, 800f));
        Assert.Equal(120f, contentOut.BorderBox.Width);

        LayoutNode borderBox = contentBox.Clone();
        borderBox.Style.BoxSizing = BoxSizing.BorderBox;
        NodeRect borderOut = RenderLayout.Layout(borderBox, (1000f, 800f));
        Assert.Equal(100f, borderOut.BorderBox.Width);
    }

    // -------------------------------------------------------------- border.rs
    // mod tests (ported alongside the border types LayoutStyle depends on)

    [Fact]
    public void SideExpansionFollowsCssTrblRules()
    {
        Assert.Equal(Sides<int>.All(1), BorderSides.ExpandSides<int>([1])!.Value);
        Assert.Equal(new Sides<int>(1, 2, 3, 2), BorderSides.ExpandSides<int>([1, 2, 3])!.Value);
        Assert.Null(BorderSides.ExpandSides<int>([]));
        Assert.Null(BorderSides.ExpandSides<int>([1, 2, 3, 4, 5]));
    }

    [Fact]
    public void HiddenStyleZeroesOnlyTheUsedWidth()
    {
        BorderModel border = BorderModel.Default with
        {
            SpecifiedWidths = Sides<float>.All(10f),
            Styles = Sides<BorderStyle>.All(BorderStyle.None),
        };
        Assert.Equal(Sides<float>.All(0f), border.UsedWidths());

        border = border with { Styles = border.Styles with { Left = BorderStyle.Solid } };
        Assert.Equal(10f, border.UsedWidths().Left);
        Assert.Equal(10f, border.SpecifiedWidths.Left);
    }

    [Fact]
    public void EllipticalRadiiUseOneOverlapScale()
    {
        BorderRadii radii = new(
            new CornerRadius(RadiusValue.Pixels(80f), RadiusValue.Pixels(50f)),
            new CornerRadius(RadiusValue.Pixels(60f), RadiusValue.Pixels(40f)),
            new CornerRadius(RadiusValue.Pixels(40f), RadiusValue.Pixels(30f)),
            new CornerRadius(RadiusValue.Pixels(20f), RadiusValue.Pixels(10f)));
        ResolvedBorderRadii resolved = radii.Resolve(100f, 50f);
        const float scale = 5.0f / 7.0f;
        Assert.True(MathF.Abs(resolved.TopLeft.X - (80f * scale)) < 0.001f);
        Assert.True(MathF.Abs(resolved.TopLeft.Y - (50f * scale)) < 0.001f);
        Assert.True(MathF.Abs(resolved.BottomRight.X - (40f * scale)) < 0.001f);
        Assert.True(MathF.Abs(resolved.BottomLeft.Y - (10f * scale)) < 0.001f);
    }

    [Fact]
    public void BorderAndOutlineDefaultsAreMediumNoneCurrentColor()
    {
        Assert.Equal(Sides<float>.All(3f), BorderModel.Default.SpecifiedWidths);
        Assert.Equal(Sides<BorderStyle>.All(BorderStyle.None), BorderModel.Default.Styles);
        Assert.Equal(Sides<RgbaColor?>.All(null), BorderModel.Default.Colors);
        Assert.True(BorderModel.Default.Radii.IsZero());
        Assert.Equal(3f, OutlineModel.Default.SpecifiedWidth);
        Assert.Equal(BorderStyle.None, OutlineModel.Default.Style);
        Assert.Null(OutlineModel.Default.Color);
        Assert.Equal(0f, OutlineModel.Default.Offset);
        Assert.Equal(0f, OutlineModel.Default.UsedWidth());
    }

    // ------------------------------------------------------- geometry / math

    [Fact]
    public void AffineDefaultIsIdentityAndComposesLikeCssMatrix()
    {
        Assert.Equal(Affine2.Identity, Affine2.Default);
        Assert.True(Affine2.Identity.IsIdentity());
        Assert.True(Affine2.Translate(3f, 4f).IsTranslation());

        Affine2 composed = Affine2.Translate(10f, 20f).Then(Affine2.Scale(2f, 3f));
        (float x, float y) = composed.MapPoint(1f, 1f);
        Assert.True(MathF.Abs(x - 12f) < 1e-5f, $"x={x}");
        Assert.True(MathF.Abs(y - 23f) < 1e-5f, $"y={y}");
    }

    [Fact]
    public void AffineRotateAndInverseRoundTrip()
    {
        Affine2 rotate = Affine2.Rotate(90f);
        (float x, float y) = rotate.MapPoint(1f, 0f);
        Assert.True(MathF.Abs(x) < 1e-6f);
        Assert.True(MathF.Abs(y - 1f) < 1e-6f);

        Affine2? maybeInverse = rotate.Inverse();
        Assert.NotNull(maybeInverse);
        (float bx, float by) = maybeInverse.Value.MapPoint(x, y);
        Assert.True(MathF.Abs(bx - 1f) < 1e-5f);
        Assert.True(MathF.Abs(by) < 1e-5f);

        Assert.Null(Affine2.Scale(0f, 0f).Inverse());
    }

    [Fact]
    public void AffineMapRectReturnsTheAxisAlignedBound()
    {
        Rect mapped = Affine2.Rotate(45f).MapRect(new Rect(0f, 0f, 10f, 10f));
        float diagonal = 10f * MathF.Sqrt(2f);
        Assert.True(MathF.Abs(mapped.Width - diagonal) < 0.001f);
        Assert.True(MathF.Abs(mapped.Height - diagonal) < 0.001f);
    }

    [Fact]
    public void RectIntersectAndUnion()
    {
        Rect a = new(0f, 0f, 10f, 10f);
        Rect b = new(5f, 5f, 10f, 10f);
        Assert.Equal(new Rect(5f, 5f, 5f, 5f), a.Intersect(b)!.Value);
        Assert.Equal(new Rect(0f, 0f, 15f, 15f), a.Union(b));
        // Touching edges do not intersect: the overlap would be degenerate.
        Assert.Null(a.Intersect(new Rect(10f, 0f, 5f, 5f)));
        Assert.Null(a.Intersect(new Rect(20f, 20f, 5f, 5f)));
    }

    [Fact]
    public void QuantizeScrollValueSnapsHalfAwayFromZeroAndGuardsBadInput()
    {
        Assert.Equal(1f, RenderMath.QuantizeScrollValue(0.5f, 1f));
        Assert.Equal(-1f, RenderMath.QuantizeScrollValue(-0.5f, 1f));
        Assert.Equal(2f, RenderMath.QuantizeScrollValue(1.5f, 1f));
        Assert.Equal(10.5f, RenderMath.QuantizeScrollValue(10.4f, 2f));
        // Non-finite values and non-positive scales fall back rather than propagating NaN.
        Assert.Equal(0f, RenderMath.QuantizeScrollValue(float.NaN, 1f));
        Assert.Equal(0f, RenderMath.QuantizeScrollValue(float.PositiveInfinity, 1f));
        Assert.Equal(3f, RenderMath.QuantizeScrollValue(2.6f, 0f));
        Assert.Equal(3f, RenderMath.QuantizeScrollValue(2.6f, float.NaN));
    }

    [Fact]
    public void DimensionDefaultsToAutoAndResolvesRelativeUnits()
    {
        Assert.Equal(DimensionKind.Auto, default(Dimension).Kind);
        Assert.True(default(Dimension).IsAuto);

        Assert.Equal(Dimension.Px(32f), Dimension.Em(2f).Resolve(16f, 10f, 12f, 8f));
        Assert.Equal(Dimension.Px(20f), Dimension.Rem(2f).Resolve(16f, 10f, 12f, 8f));
        Assert.Equal(Dimension.Px(24f), Dimension.Vw(2f).Resolve(16f, 10f, 12f, 8f));
        Assert.Equal(Dimension.Px(16f), Dimension.Vh(2f).Resolve(16f, 10f, 12f, 8f));
        Assert.Equal(Dimension.Px(16f), Dimension.Vmin(2f).Resolve(16f, 10f, 12f, 8f));
        Assert.Equal(Dimension.Px(24f), Dimension.Vmax(2f).Resolve(16f, 10f, 12f, 8f));
        Assert.Equal(Dimension.Px(16f * Dimension.ExPerEm), Dimension.Ex(1f).Resolve(16f, 10f, 12f, 8f));
        // Px, Percent and Auto pass through untouched.
        Assert.Equal(Dimension.Px(5f), Dimension.Px(5f).Resolve(16f, 10f, 12f, 8f));
        Assert.Equal(Dimension.Percent(0.5f), Dimension.Percent(0.5f).Resolve(16f, 10f, 12f, 8f));
        Assert.Equal(Dimension.Auto, Dimension.Auto.Resolve(16f, 10f, 12f, 8f));
    }

    [Fact]
    public void BackgroundPositionAxisKeepsLengthAndPercentageTerms()
    {
        Assert.Equal(24f, BackgroundPositionAxis.Pixels(24f).Resolve(100f));
        Assert.Equal(50f, BackgroundPositionAxis.Percentage(0.5f).Resolve(100f));
        // `right 10px` is `100% - 10px`.
        BackgroundPositionAxis fromEnd =
            BackgroundPositionAxis.FromEndOffset(BackgroundPositionAxis.Pixels(10f));
        Assert.Equal(90f, fromEnd.Resolve(100f));
        Assert.Equal(35f, BackgroundPositionAxis.LengthPercentage(10f, 0.25f).Resolve(100f));

        // The derived default is the CSS initial value `0% 0%`.
        Assert.Equal(0f, default(BackgroundPosition).X.Resolve(100f));
        Assert.Equal(0f, default(BackgroundPosition).Y.Resolve(100f));
        // ...while object-position's initial value is centered.
        Assert.Equal(50f, ObjectPosition.Default.X.Resolve(100f));
        Assert.Equal(50f, ObjectPosition.Default.Y.Resolve(100f));
    }

    [Fact]
    public void ReplacedIntrinsicNaturalSizeFollowsCssImagesDefaultObjectSize()
    {
        Assert.Equal((40f, 20f), ReplacedIntrinsic.FromDimensions(40f, 20f).NaturalSize()!.Value);
        Assert.Equal(2f, ReplacedIntrinsic.FromDimensions(40f, 20f).Ratio!.Value);
        Assert.Equal((300f, 150f), new ReplacedIntrinsic(null, null, null).NaturalSize()!.Value);
        Assert.Equal((40f, 150f), new ReplacedIntrinsic(40f, null, null).NaturalSize()!.Value);
        Assert.Equal((300f, 40f), new ReplacedIntrinsic(null, 40f, null).NaturalSize()!.Value);
        Assert.Equal((40f, 20f), new ReplacedIntrinsic(40f, null, 2f).NaturalSize()!.Value);
        Assert.Equal((80f, 40f), new ReplacedIntrinsic(null, 40f, 2f).NaturalSize()!.Value);
        // A wide ratio-only resource is bounded by the 300px default width...
        Assert.Equal((300f, 100f), new ReplacedIntrinsic(null, null, 3f).NaturalSize()!.Value);
        // ...and a narrow one by the 150px default height.
        Assert.Equal((150f, 150f), new ReplacedIntrinsic(null, null, 1f).NaturalSize()!.Value);
    }

    // ------------------------------------------------------------- animation

    [Fact]
    public void AnimationEffectImpactOrdersNoneBelowPaintBelowGeometry()
    {
        Assert.Equal(AnimationEffectImpact.None, default(AnimationEffectImpact));
        Assert.True(AnimationEffectImpact.None < AnimationEffectImpact.Paint);
        Assert.True(AnimationEffectImpact.Paint < AnimationEffectImpact.Geometry);
    }

    [Fact]
    public void AnimationTimingDefaultRunsOnceForward()
    {
        AnimationTiming timing = AnimationTiming.Default;
        Assert.Equal(0f, timing.DurationMs);
        Assert.Equal(0f, timing.DelayMs);
        Assert.Equal(1f, timing.IterationCount);
        Assert.Equal(AnimationDirection.Normal, timing.Direction);
        Assert.Equal(AnimationFillMode.None, timing.FillMode);
        Assert.Equal(AnimationPlayState.Running, timing.PlayState);
    }

    [Fact]
    public void AnimationSampleConstructorsSelectTheClock()
    {
        Assert.Equal(AnimationSampleMode.DocumentTime, AnimationSample.Document(120f).Mode);
        Assert.Equal(120f, AnimationSample.Document(120f).Time.Milliseconds);
        Assert.Equal(AnimationSampleMode.LocalOverride, AnimationSample.LocalOverride(0f).Mode);
        Assert.Equal(AnimationSampleMode.DocumentTime, default(AnimationSample).Mode);
    }

    [Fact]
    public void WaapiControlOperationsMoveTheLocalClock()
    {
        AnimationTimelineState state = new();
        WaapiAnimation animation = new()
        {
            Id = 7,
            Node = new Obscura.Dom.NodeId(3),
            Timing = AnimationTiming.Default with { DurationMs = 1000f, IterationCount = 2f },
            PlayState = WaapiPlayState.Running,
            StartTimeMs = 0f,
        };
        state.RegisterWaapi(animation);

        Assert.Equal(new Obscura.Dom.NodeId(3), state.WaapiNode(7)!.Value);
        Assert.Contains(new Obscura.Dom.NodeId(3), state.WaapiNodes());

        Assert.True(state.SetWaapiCurrentTime(7, 500f, 250f));
        Assert.Equal(250f, animation.HoldTimeMs!.Value);
        Assert.Equal(250f, animation.StartTimeMs);

        Assert.True(state.SetWaapiPlayState(7, WaapiPlayState.Running, 900f));
        Assert.Null(animation.HoldTimeMs);
        Assert.Equal(650f, animation.StartTimeMs);

        Assert.True(state.FinishWaapi(7));
        Assert.Equal(WaapiPlayState.Finished, animation.PlayState);
        Assert.Equal(2000f, animation.HoldTimeMs!.Value);

        Assert.True(state.CancelWaapi(7));
        Assert.False(state.CancelWaapi(7));
        Assert.Null(state.WaapiNode(7));
    }

    [Fact]
    public void StartCandidatesGateTheStyleFlush()
    {
        AnimationTimelineState state = new();
        Assert.False(state.HasPendingStartCandidates());
        state.NoteStartCandidate(new Obscura.Dom.NodeId(1), 40f);
        Assert.True(state.HasPendingStartCandidates());
        // Non-finite and negative document times are ignored.
        state.ClearStartCandidates();
        state.NoteStartCandidate(new Obscura.Dom.NodeId(1), -1f);
        state.NoteSubtreeStartCandidate(new Obscura.Dom.NodeId(2), float.NaN);
        Assert.False(state.HasPendingStartCandidates());
    }

    // --------------------------------------------------------------- capture

    [Fact]
    public void CaptureRegionValidationRejectsInvalidAndOversizedSurfaces()
    {
        foreach (CaptureRegion invalid in new[]
        {
            CaptureRegion.New(0f, 0f, 0f, 10f, 1f),
            CaptureRegion.New(float.NaN, 0f, 10f, 10f, 1f),
            CaptureRegion.New(0f, 0f, 10f, 10f, float.PositiveInfinity),
        })
        {
            Assert.Equal(CaptureError.InvalidRegion, CaptureLimits.ValidateCaptureRegion(invalid));
        }

        Assert.Equal(
            CaptureError.AllocationLimitExceeded,
            CaptureLimits.ValidateCaptureRegion(
                CaptureRegion.New(0f, 0f, CaptureLimits.MaxCaptureDimension, 10f, 2f)));
        Assert.Equal(
            CaptureError.AllocationLimitExceeded,
            CaptureLimits.ValidateCaptureRegion(
                CaptureRegion.New(0f, 0f, 10f, 10f, CaptureLimits.MaxCaptureScale + 1f)));
        // Each surface is below the per-surface pixel bound, but their simultaneous RGBA peak
        // exceeds the byte cap.
        Assert.Equal(
            CaptureError.AllocationLimitExceeded,
            CaptureLimits.ValidateCaptureRegion(CaptureRegion.New(0f, 0f, 6000f, 6000f, 1.2f)));

        Assert.Null(CaptureLimits.ValidateCaptureRegion(CaptureRegion.New(0f, 0f, 800f, 600f, 1f)));
        Assert.Null(
            CaptureLimits.ValidateCaptureRegion(
                CaptureRegion.WithOutputSize(0f, 0f, 800f, 600f, 2f, 1600, 1200)));
    }

    [Fact]
    public void CaptureLimitsMatchTheRustConstants()
    {
        Assert.Equal(32_768u, CaptureLimits.MaxCaptureDimension);
        Assert.Equal(16UL * 1024 * 1024, CaptureLimits.MaxCapturePixels);
        Assert.Equal(16f, CaptureLimits.MaxCaptureScale);
        Assert.Equal(128UL * 1024 * 1024, CaptureLimits.MaxCapturePeakBytes);
    }

    // ----------------------------------------------------------- LayoutStyle

    [Fact]
    public void LayoutStyleDefaultsMatchTheRustDeriveDefault()
    {
        LayoutStyle style = new();

        Assert.Equal(Display.Block, style.Display);
        Assert.Null(style.Direction);
        Assert.Equal(ContainerType.Normal, style.ContainerType);
        Assert.Empty(style.ContainerNames);
        Assert.False(style.InternalFlexContainer);
        Assert.False(style.LegacyCenter);

        Assert.True(style.Width.IsAuto);
        Assert.True(style.Height.IsAuto);
        Assert.True(style.MinWidth.IsAuto);
        Assert.True(style.MinHeight.IsAuto);
        Assert.True(style.MaxWidth.IsAuto);
        Assert.True(style.MaxHeight.IsAuto);
        Assert.True(style.FlexBasis.IsAuto);
        Assert.Equal(BoxSizing.ContentBox, style.BoxSizing);
        Assert.False(style.WidthSet);
        Assert.False(style.HeightSet);
        Assert.False(style.WidthFitContent);

        Assert.Equal(6, style.SizeExpressions.Length);
        Assert.All(style.SizeExpressions, static value => Assert.Null(value));
        Assert.Equal(4, style.MarginAuto.Length);
        Assert.DoesNotContain(true, style.MarginAuto);
        Assert.Equal(4, style.MarginPercent.Length);
        Assert.Equal(4, style.MarginRelative.Length);
        Assert.Equal(4, style.MarginExpressions.Length);
        Assert.Equal(4, style.PaddingPercent.Length);
        Assert.Equal(4, style.PaddingRelative.Length);
        Assert.Equal(4, style.PaddingExpressions.Length);
        Assert.Equal(4, style.Inset.Length);
        Assert.Equal(4, style.InsetExpressions.Length);
        Assert.Equal(2, style.IndividualTranslateExpressions.Length);

        Assert.Equal(default(Edges), style.Margin);
        Assert.Equal(default(Edges), style.Padding);
        Assert.Equal(default(Edges), style.Border);

        // The two non-zero struct defaults: `medium none currentcolor`.
        Assert.Equal(BorderModel.Default, style.BorderModel);
        Assert.Equal(OutlineModel.Default, style.Outline);

        Assert.Null(style.AspectRatio);
        Assert.Null(style.IntrinsicSize);
        Assert.Null(style.BackgroundColor);
        Assert.Null(style.BackgroundGradient);
        Assert.Empty(style.BackgroundGradientLayers);
        Assert.Equal(BackgroundOrigin.PaddingBox, style.BackgroundOrigin);
        Assert.Equal(BackgroundClip.BorderBox, style.BackgroundClip);
        Assert.Equal(default(BackgroundPosition), style.BackgroundPosition);
        Assert.Null(style.BackgroundRepeat);
        Assert.False(style.BackgroundClipText);
        Assert.Null(style.Color);
        Assert.False(style.ColorSchemeDark);

        Assert.Null(style.FontSize);
        Assert.Null(style.FontFamily);
        Assert.Null(style.FontOpticalSizing);
        Assert.Null(style.FontVariationSettings);
        Assert.Null(style.TextAlign);
        Assert.Null(style.TextIndent);
        Assert.Null(style.AlignItems);
        Assert.Null(style.JustifyContent);
        Assert.Null(style.FlexGrow);
        Assert.Equal(0, style.Order);

        Assert.Empty(style.GridTemplateColumns);
        Assert.Empty(style.GridAutoRows);
        Assert.Null(style.GridAutoFlow);
        Assert.Null(style.GridAreas);
        Assert.Null(style.ColumnGap);
        Assert.Null(style.ColumnCount);
        Assert.False(style.BreakInsideAvoid);

        Assert.Null(style.Position);
        Assert.False(style.PositionFixed);
        Assert.False(style.PositionSticky);
        Assert.False(style.OverflowHidden);
        Assert.False(style.OverflowScrollContainer);
        Assert.Equal(0, (int)style.ScrollbarGutters);

        Assert.Null(style.Float);
        Assert.Null(style.VisibilityHidden);
        Assert.Null(style.Opacity);
        Assert.Null(style.AnimationName);
        Assert.Equal(AnimationTiming.Default, style.AnimationTiming);
        Assert.False(style.AnimationHasRenderEffect);
        Assert.Equal(0f, style.AnimationLocalTimeMs);
        Assert.Null(style.VerticalAlign);
        Assert.Null(style.ZIndex);
        Assert.Null(style.Clear);
        Assert.Empty(style.CounterReset);
        Assert.Empty(style.CounterIncrement);
        Assert.Empty(style.CounterSet);
        Assert.False(style.EffectivelyInvisible);

        Assert.Null(style.BeforePseudo);
        Assert.Null(style.AfterPseudo);
        Assert.Null(style.PlaceholderPseudo);
        Assert.False(style.IsInlineBlock);
        Assert.False(style.FlowRoot);
        Assert.False(style.DisplayContents);
        Assert.Null(style.ListStyle);
        Assert.Null(style.LineHeight);
        Assert.Null(style.WhiteSpace);
        Assert.Equal(TextOverflow.Clip, style.TextOverflow);
        Assert.Null(style.WebkitLineClamp);
        Assert.Null(style.OverflowWrap);
        Assert.Null(style.WordBreak);
        Assert.Null(style.TextWrapStyle);
        Assert.Null(style.TextTransform);
        Assert.Null(style.Underline);
        Assert.Null(style.FontStyleItalic);

        Assert.Equal(ObjectFit.Fill, style.ObjectFit);
        Assert.Equal(ObjectPosition.Default, style.ObjectPosition);
        Assert.Empty(style.TransformOps);
        Assert.Null(style.IndividualTranslate);
        Assert.Null(style.IndividualRotate);
        Assert.Null(style.IndividualScale);
        Assert.Equal(0, (int)style.ContainingBlockTriggers);
        Assert.Null(style.TransformOrigin);
        Assert.Null(style.BoxShadow);
    }

    [Fact]
    public void LayoutStyleCloneIsIndependentOfTheOriginal()
    {
        LayoutStyle style = new()
        {
            Width = Dimension.Px(10f),
            ContainerNames = ["card"],
            TransformOps = [new TransformOp.Rotate(45f)],
            CounterReset = [new CounterDirective("section", 0)],
            GridAreas = [["a", "b"]],
            BeforePseudo = new LayoutStyle { Width = Dimension.Px(1f) },
            BackgroundGradientLayers =
            [
                new BackgroundGradientLayer.Radial((0.5f, 0.5f), [new GradientStop(new RgbaColor(1, 2, 3, 4), null)]),
            ],
        };
        style.SizeExpressions[0] = "calc(100% - 1rem)";
        style.MarginPercent[3] = 0.25f;

        LayoutStyle copy = style.Clone();
        Assert.Equal(Dimension.Px(10f), copy.Width);
        Assert.Equal("calc(100% - 1rem)", copy.SizeExpressions[0]);
        Assert.Equal(0.25f, copy.MarginPercent[3]!.Value);

        copy.ContainerNames.Add("panel");
        copy.TransformOps.Clear();
        copy.CounterReset.Clear();
        copy.GridAreas![0].Add("c");
        copy.SizeExpressions[0] = null;
        copy.MarginPercent[3] = null;
        copy.BeforePseudo!.Width = Dimension.Px(99f);
        ((BackgroundGradientLayer.Radial)copy.BackgroundGradientLayers[0]).Stops.Clear();

        Assert.Single(style.ContainerNames);
        Assert.Single(style.TransformOps);
        Assert.Single(style.CounterReset);
        Assert.Equal(2, style.GridAreas![0].Count);
        Assert.Equal("calc(100% - 1rem)", style.SizeExpressions[0]);
        Assert.Equal(0.25f, style.MarginPercent[3]!.Value);
        Assert.Equal(Dimension.Px(1f), style.BeforePseudo!.Width);
        Assert.Single(((BackgroundGradientLayer.Radial)style.BackgroundGradientLayers[0]).Stops);
    }

    [Fact]
    public void InlineBoxesIgnoreUsedBoxSizesUntilBlockified()
    {
        LayoutStyle inline = new() { Display = Display.Inline };
        Assert.True(inline.IgnoresUsedBoxSizes());

        LayoutStyle inlineBlock = new() { Display = Display.Inline, IsInlineBlock = true };
        Assert.False(inlineBlock.IgnoresUsedBoxSizes());

        LayoutStyleExtensions.BlockifyOuterDisplay(inlineBlock);
        Assert.Equal(Display.Block, inlineBlock.Display);
        Assert.False(inlineBlock.IsInlineBlock);

        // Blockification preserves the inner layout mode of inline-flex / inline-grid.
        LayoutStyle inlineFlex = new() { Display = Display.Flex, IsInlineBlock = true };
        LayoutStyleExtensions.BlockifyOuterDisplay(inlineFlex);
        Assert.Equal(Display.Flex, inlineFlex.Display);
        Assert.False(inlineFlex.IsInlineBlock);

        // A plain block box is untouched.
        LayoutStyle block = new();
        LayoutStyleExtensions.BlockifyOuterDisplay(block);
        Assert.Equal(Display.Block, block.Display);
    }

    [Fact]
    public void OverflowClipQueriesFallBackToTheAggregateFlag()
    {
        LayoutStyle style = new() { OverflowHidden = true };
        Assert.True(style.ClipsOverflowX());
        Assert.True(style.ClipsOverflowY());

        style.OverflowAxesSet = true;
        style.OverflowClipX = true;
        style.OverflowClipY = false;
        Assert.True(style.ClipsOverflowX());
        Assert.False(style.ClipsOverflowY());
    }

    [Fact]
    public void ContainingBlockTriggersAreAnIndependentBitset()
    {
        LayoutStyle style = new();
        Assert.False(style.EstablishesPositioningContainingBlock());
        style.ContainingBlockTriggers |= ContainingBlockTrigger.Transform;
        style.ContainingBlockTriggers |= ContainingBlockTrigger.Filter;
        Assert.True(style.EstablishesPositioningContainingBlock());
        // Clearing one trigger leaves the others intact.
        style.ContainingBlockTriggers &= unchecked((ushort)~ContainingBlockTrigger.Filter);
        Assert.Equal(ContainingBlockTrigger.Transform, (int)style.ContainingBlockTriggers);
        Assert.True(style.EstablishesPositioningContainingBlock());
    }
}
