// xUnit port of taffy's in-file `#[cfg(test)] mod tests` blocks for the files
// ported in dotnet/src/Obscura.Render/Layout:
//
//   vendor/taffy/src/style/alignment.rs
//   vendor/taffy/src/style/flex.rs
//   vendor/taffy/src/style/mod.rs
//   vendor/taffy/src/style_helpers.rs
//   vendor/taffy/src/util/math.rs
//   vendor/taffy/src/util/resolve.rs
//   vendor/taffy/src/tree/taffy_tree.rs
//   vendor/taffy/src/compute/mod.rs
//   vendor/taffy/src/compute/float.rs
//
// Tests appear in the same order and with the same names as the Rust originals.
// Tests gated on the `parse` or `serde` cargo features are not ported: neither
// feature is part of this port (Obscura parses CSS in Obscura.Render.Css, and
// styles never cross a serialization boundary).
using System.Runtime.CompilerServices;
using Obscura.Render.Layout;
using Xunit;

namespace Obscura.Render.Tests;

// ---------------------------------------------------------------------------
// vendor/taffy/src/style/alignment.rs
// ---------------------------------------------------------------------------

public class AlignmentTests
{
    // Size budget. Rust asserts `<= 2` for the structs and `<= 3` for the Options
    // (Rust niche-packs the safety byte). .NET's Nullable<T> is `{ bool, T }` so
    // Option<AlignItems> is 4 bytes here; the bare structs match Rust exactly.
    [Fact]
    public void AlignTypesWithinSizeBudget()
    {
        Assert.True(Unsafe.SizeOf<AlignItems>() <= 2, $"AlignItems grew to {Unsafe.SizeOf<AlignItems>()}");
        Assert.True(Unsafe.SizeOf<AlignContent>() <= 2, $"AlignContent grew to {Unsafe.SizeOf<AlignContent>()}");
        Assert.True(Unsafe.SizeOf<AlignItems?>() <= 4);
        Assert.True(Unsafe.SizeOf<AlignContent?>() <= 4);
    }

    [Fact]
    public void AlignItemsIsSafe()
    {
        Assert.True(AlignItems.SafeStart.IsSafe);
        Assert.True(AlignItems.SafeEnd.IsSafe);
        Assert.True(AlignItems.SafeFlexStart.IsSafe);
        Assert.True(AlignItems.SafeFlexEnd.IsSafe);
        Assert.True(AlignItems.SafeCenter.IsSafe);
        Assert.False(AlignItems.Start.IsSafe);
        Assert.False(AlignItems.End.IsSafe);
        Assert.False(AlignItems.FlexStart.IsSafe);
        Assert.False(AlignItems.FlexEnd.IsSafe);
        Assert.False(AlignItems.Center.IsSafe);
        Assert.False(AlignItems.Baseline.IsSafe);
        Assert.False(AlignItems.Stretch.IsSafe);
    }

    [Fact]
    public void AlignItemsKeywordStripsSafe()
    {
        Assert.Equal(AlignItemsKeyword.Start, AlignItems.SafeStart.GetKeyword());
        Assert.Equal(AlignItemsKeyword.End, AlignItems.SafeEnd.GetKeyword());
        Assert.Equal(AlignItemsKeyword.FlexStart, AlignItems.SafeFlexStart.GetKeyword());
        Assert.Equal(AlignItemsKeyword.FlexEnd, AlignItems.SafeFlexEnd.GetKeyword());
        Assert.Equal(AlignItemsKeyword.Center, AlignItems.SafeCenter.GetKeyword());
    }

    [Fact]
    public void AlignItemsKeywordPassthrough()
    {
        Assert.Equal(AlignItemsKeyword.Start, AlignItems.Start.GetKeyword());
        Assert.Equal(AlignItemsKeyword.Stretch, AlignItems.Stretch.GetKeyword());
        Assert.Equal(AlignItemsKeyword.Baseline, AlignItems.Baseline.GetKeyword());
        Assert.Equal(AlignItemsKeyword.FlexStart, AlignItems.FlexStart.GetKeyword());
    }

    [Fact]
    public void AlignContentIsSafe()
    {
        Assert.True(AlignContent.SafeStart.IsSafe);
        Assert.True(AlignContent.SafeCenter.IsSafe);
        Assert.False(AlignContent.SpaceBetween.IsSafe);
        Assert.False(AlignContent.Stretch.IsSafe);
    }

    [Fact]
    public void AlignContentKeywordStripsSafe()
    {
        Assert.Equal(AlignContentKeyword.Start, AlignContent.SafeStart.GetKeyword());
        Assert.Equal(AlignContentKeyword.FlexEnd, AlignContent.SafeFlexEnd.GetKeyword());
        Assert.Equal(AlignContentKeyword.Center, AlignContent.SafeCenter.GetKeyword());
        Assert.Equal(AlignContentKeyword.SpaceBetween, AlignContent.SpaceBetween.GetKeyword());
    }

    [Fact]
    public void AlignContentKeywordReversedSwapsStartEnd()
    {
        Assert.Equal(AlignContentKeyword.End, AlignContentKeyword.Start.Reversed());
        Assert.Equal(AlignContentKeyword.Start, AlignContentKeyword.End.Reversed());
        Assert.Equal(AlignContentKeyword.FlexEnd, AlignContentKeyword.FlexStart.Reversed());
        Assert.Equal(AlignContentKeyword.FlexStart, AlignContentKeyword.FlexEnd.Reversed());

        // Stretch reverses to End - preserves pre-refactor behaviour.
        Assert.Equal(AlignContentKeyword.End, AlignContentKeyword.Stretch.Reversed());
        Assert.Equal(AlignContentKeyword.Center, AlignContentKeyword.Center.Reversed());
        Assert.Equal(AlignContentKeyword.SpaceBetween, AlignContentKeyword.SpaceBetween.Reversed());
        Assert.Equal(AlignContentKeyword.SpaceEvenly, AlignContentKeyword.SpaceEvenly.Reversed());
        Assert.Equal(AlignContentKeyword.SpaceAround, AlignContentKeyword.SpaceAround.Reversed());
    }
}

// ---------------------------------------------------------------------------
// vendor/taffy/src/style/flex.rs
// ---------------------------------------------------------------------------

public class FlexDirectionTests
{
    [Fact]
    public void FlexDirectionIsRow()
    {
        Assert.True(FlexDirection.Row.IsRow());
        Assert.True(FlexDirection.RowReverse.IsRow());
        Assert.False(FlexDirection.Column.IsRow());
        Assert.False(FlexDirection.ColumnReverse.IsRow());
    }

    [Fact]
    public void FlexDirectionIsColumn()
    {
        Assert.False(FlexDirection.Row.IsColumn());
        Assert.False(FlexDirection.RowReverse.IsColumn());
        Assert.True(FlexDirection.Column.IsColumn());
        Assert.True(FlexDirection.ColumnReverse.IsColumn());
    }

    [Fact]
    public void FlexDirectionIsReverse()
    {
        Assert.False(FlexDirection.Row.IsReverse());
        Assert.True(FlexDirection.RowReverse.IsReverse());
        Assert.False(FlexDirection.Column.IsReverse());
        Assert.True(FlexDirection.ColumnReverse.IsReverse());
    }
}

// ---------------------------------------------------------------------------
// vendor/taffy/src/style/mod.rs
// ---------------------------------------------------------------------------

public class StyleTests
{
    [Fact]
    public void DefaultsMatch()
    {
        var oldDefaults = new Style
        {
            Display = Display.Flex,
            ItemIsTable = false,
            ItemIsReplaced = false,
            ItemAspectRatioIsIntrinsic = false,
            IntrinsicSizeContainment = new Size<bool>(false, false),
            BoxSizing = BoxSizing.BorderBox,
            Float = Float.None,
            Clear = Clear.None,
            Direction = Direction.Ltr,
            Overflow = new Point<Overflow>(Overflow.Visible, Overflow.Visible),
            ScrollbarWidth = 0.0f,
            Position = Position.Relative,
            FlexDirection = FlexDirection.Row,
            FlexWrap = FlexWrap.NoWrap,
            AlignItems = null,
            AlignSelf = null,
            JustifyItems = null,
            JustifySelf = null,
            AlignContent = null,
            JustifyContent = null,
            Inset = new Rect<LengthPercentageAuto>(
                LengthPercentageAuto.Auto, LengthPercentageAuto.Auto,
                LengthPercentageAuto.Auto, LengthPercentageAuto.Auto),
            Margin = new Rect<LengthPercentageAuto>(
                LengthPercentageAuto.Zero, LengthPercentageAuto.Zero,
                LengthPercentageAuto.Zero, LengthPercentageAuto.Zero),
            Padding = new Rect<LengthPercentage>(
                LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero),
            Border = new Rect<LengthPercentage>(
                LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero),
            Gap = GeometryExtensions.SizeLengthPercentageZero,
            TextAlign = TextAlign.Auto,
            FlexGrow = 0.0f,
            FlexShrink = 1.0f,
            FlexBasis = Dimension.Auto,
            Size = GeometryExtensions.SizeDimensionAuto,
            MinSize = GeometryExtensions.SizeDimensionAuto,
            MaxSize = GeometryExtensions.SizeDimensionAuto,
            AspectRatio = null,
            GridTemplateRows = [],
            GridTemplateColumns = [],
            GridTemplateRowNames = [],
            GridTemplateColumnNames = [],
            GridTemplateAreas = [],
            GridAutoRows = [],
            GridAutoColumns = [],
            GridAutoFlow = GridAutoFlow.Row,
            GridRow = new Line<GridPlacement>(GridPlacement.Auto, GridPlacement.Auto),
            GridColumn = new Line<GridPlacement>(GridPlacement.Auto, GridPlacement.Auto),
        };

        Assert.Equal(Style.Default, new Style());
        Assert.Equal(Style.Default, oldDefaults);
    }

    // NOTE: as in Rust, this test exists to prevent unintentional size changes.
    // The Rust assertions that depend on Rust's enum niche packing or on `Style`
    // being a value type (Option<AlignItems>, Style<String>, Vec<..>) do not
    // transfer; the value types that do transfer are asserted exactly.
    [Fact]
    public void StyleSizes()
    {
        // Display and Position
        Assert.Equal(1, Unsafe.SizeOf<Display>());
        Assert.Equal(1, Unsafe.SizeOf<BoxSizing>());
        Assert.Equal(1, Unsafe.SizeOf<Position>());
        Assert.Equal(1, Unsafe.SizeOf<Overflow>());

        // Dimensions and aggregations of Dimensions
        Assert.Equal(4, Unsafe.SizeOf<float>());
        Assert.Equal(8, Unsafe.SizeOf<LengthPercentage>());
        Assert.Equal(8, Unsafe.SizeOf<LengthPercentageAuto>());
        Assert.Equal(8, Unsafe.SizeOf<Dimension>());
        Assert.Equal(16, Unsafe.SizeOf<Size<LengthPercentage>>());
        Assert.Equal(16, Unsafe.SizeOf<Size<LengthPercentageAuto>>());
        Assert.Equal(16, Unsafe.SizeOf<Size<Dimension>>());
        Assert.Equal(32, Unsafe.SizeOf<Rect<LengthPercentage>>());
        Assert.Equal(32, Unsafe.SizeOf<Rect<LengthPercentageAuto>>());
        Assert.Equal(32, Unsafe.SizeOf<Rect<Dimension>>());

        // Alignment
        Assert.Equal(1, Unsafe.SizeOf<AlignContentKeyword>());
        Assert.Equal(1, Unsafe.SizeOf<AlignItemsKeyword>());
        Assert.Equal(1, Unsafe.SizeOf<AlignmentSafety>());
        Assert.Equal(2, Unsafe.SizeOf<AlignContent>());
        Assert.Equal(2, Unsafe.SizeOf<AlignItems>());

        // Flexbox Container
        Assert.Equal(1, Unsafe.SizeOf<FlexDirection>());
        Assert.Equal(1, Unsafe.SizeOf<FlexWrap>());

        // CSS Grid Container
        Assert.Equal(1, Unsafe.SizeOf<GridAutoFlow>());
        Assert.Equal(8, Unsafe.SizeOf<MinTrackSizingFunction>());
        Assert.Equal(8, Unsafe.SizeOf<MaxTrackSizingFunction>());
        Assert.Equal(16, Unsafe.SizeOf<TrackSizingFunction>());
    }
}

// ---------------------------------------------------------------------------
// vendor/taffy/src/style_helpers.rs (repeat_fn_tests)
// ---------------------------------------------------------------------------

public class RepeatFnTests
{
    private static List<TrackSizingFunction> TestVec() => [];

    [Fact]
    public void TestRepeatU16()
    {
        var expected = GridTemplateComponent.FromRepeat(new GridTemplateRepetition
        {
            Count = RepetitionCount.FromCount(123),
            Tracks = TestVec(),
            LineNames = [],
        });
        Assert.Equal(expected, StyleHelpers.Repeat((ushort)123, TestVec()));
    }

    [Fact]
    public void TestRepeatAutoFitStr()
    {
        var expected = GridTemplateComponent.FromRepeat(new GridTemplateRepetition
        {
            Count = RepetitionCount.AutoFit,
            Tracks = TestVec(),
            LineNames = [],
        });
        Assert.Equal(expected, StyleHelpers.Repeat("auto-fit", TestVec()));
    }

    [Fact]
    public void TestRepeatAutoFillStr()
    {
        var expected = GridTemplateComponent.FromRepeat(new GridTemplateRepetition
        {
            Count = RepetitionCount.AutoFill,
            Tracks = TestVec(),
            LineNames = [],
        });
        Assert.Equal(expected, StyleHelpers.Repeat("auto-fill", TestVec()));
    }
}

// ---------------------------------------------------------------------------
// vendor/taffy/src/util/math.rs
// ---------------------------------------------------------------------------

public class MaybeMathLhsOptionRhsOptionTests
{
    [Fact]
    public void TestMaybeMin()
    {
        Assert.Equal((float?)3.0f, ((float?)3.0f).MaybeMin((float?)5.0f));
        Assert.Equal((float?)3.0f, ((float?)5.0f).MaybeMin((float?)3.0f));
        Assert.Equal((float?)3.0f, ((float?)3.0f).MaybeMin((float?)null));
        Assert.Null(((float?)null).MaybeMin((float?)3.0f));
        Assert.Null(((float?)null).MaybeMin((float?)null));
    }

    [Fact]
    public void TestMaybeMax()
    {
        Assert.Equal((float?)5.0f, ((float?)3.0f).MaybeMax((float?)5.0f));
        Assert.Equal((float?)5.0f, ((float?)5.0f).MaybeMax((float?)3.0f));
        Assert.Equal((float?)3.0f, ((float?)3.0f).MaybeMax((float?)null));
        Assert.Null(((float?)null).MaybeMax((float?)3.0f));
        Assert.Null(((float?)null).MaybeMax((float?)null));
    }

    [Fact]
    public void TestMaybeAdd()
    {
        Assert.Equal((float?)8.0f, ((float?)3.0f).MaybeAdd((float?)5.0f));
        Assert.Equal((float?)8.0f, ((float?)5.0f).MaybeAdd((float?)3.0f));
        Assert.Equal((float?)3.0f, ((float?)3.0f).MaybeAdd((float?)null));
        Assert.Null(((float?)null).MaybeAdd((float?)3.0f));
        Assert.Null(((float?)null).MaybeAdd((float?)null));
    }

    [Fact]
    public void TestMaybeSub()
    {
        Assert.Equal((float?)(-2.0f), ((float?)3.0f).MaybeSub((float?)5.0f));
        Assert.Equal((float?)2.0f, ((float?)5.0f).MaybeSub((float?)3.0f));
        Assert.Equal((float?)3.0f, ((float?)3.0f).MaybeSub((float?)null));
        Assert.Null(((float?)null).MaybeSub((float?)3.0f));
        Assert.Null(((float?)null).MaybeSub((float?)null));
    }
}

public class MaybeMathLhsOptionRhsFloatTests
{
    [Fact]
    public void TestMaybeMin()
    {
        Assert.Equal((float?)3.0f, ((float?)3.0f).MaybeMin(5.0f));
        Assert.Equal((float?)3.0f, ((float?)5.0f).MaybeMin(3.0f));
        Assert.Null(((float?)null).MaybeMin(3.0f));
    }

    [Fact]
    public void TestMaybeMax()
    {
        Assert.Equal((float?)5.0f, ((float?)3.0f).MaybeMax(5.0f));
        Assert.Equal((float?)5.0f, ((float?)5.0f).MaybeMax(3.0f));
        Assert.Null(((float?)null).MaybeMax(3.0f));
    }

    [Fact]
    public void TestMaybeAdd()
    {
        Assert.Equal((float?)8.0f, ((float?)3.0f).MaybeAdd(5.0f));
        Assert.Equal((float?)8.0f, ((float?)5.0f).MaybeAdd(3.0f));
        Assert.Null(((float?)null).MaybeAdd(3.0f));
    }

    [Fact]
    public void TestMaybeSub()
    {
        Assert.Equal((float?)(-2.0f), ((float?)3.0f).MaybeSub(5.0f));
        Assert.Equal((float?)2.0f, ((float?)5.0f).MaybeSub(3.0f));
        Assert.Null(((float?)null).MaybeSub(3.0f));
    }
}

public class MaybeMathLhsFloatRhsOptionTests
{
    [Fact]
    public void TestMaybeMin()
    {
        Assert.Equal(3.0f, 3.0f.MaybeMin((float?)5.0f));
        Assert.Equal(3.0f, 5.0f.MaybeMin((float?)3.0f));
        Assert.Equal(3.0f, 3.0f.MaybeMin((float?)null));
    }

    [Fact]
    public void TestMaybeMax()
    {
        Assert.Equal(5.0f, 3.0f.MaybeMax((float?)5.0f));
        Assert.Equal(5.0f, 5.0f.MaybeMax((float?)3.0f));
        Assert.Equal(3.0f, 3.0f.MaybeMax((float?)null));
    }

    [Fact]
    public void TestMaybeAdd()
    {
        Assert.Equal(8.0f, 3.0f.MaybeAdd((float?)5.0f));
        Assert.Equal(8.0f, 5.0f.MaybeAdd((float?)3.0f));
        Assert.Equal(3.0f, 3.0f.MaybeAdd((float?)null));
    }

    [Fact]
    public void TestMaybeSub()
    {
        Assert.Equal(-2.0f, 3.0f.MaybeSub((float?)5.0f));
        Assert.Equal(2.0f, 5.0f.MaybeSub((float?)3.0f));
        Assert.Equal(3.0f, 3.0f.MaybeSub((float?)null));
    }
}

// ---------------------------------------------------------------------------
// vendor/taffy/src/util/resolve.rs
// ---------------------------------------------------------------------------

public class MaybeResolveDimensionTests
{
    private static readonly CalcResolver Calc = static (_, _) => 42.42f;

    /// <summary>Dimension.Auto should always return null; context should not matter.</summary>
    [Fact]
    public void ResolveAuto()
    {
        Assert.Null(Dimension.Auto.MaybeResolve((float?)null, Calc));
        Assert.Null(Dimension.Auto.MaybeResolve((float?)5.0f, Calc));
        Assert.Null(Dimension.Auto.MaybeResolve((float?)(-5.0f), Calc));
        Assert.Null(Dimension.Auto.MaybeResolve((float?)0.0f, Calc));
    }

    /// <summary>A length always returns its inner absolute length.</summary>
    [Fact]
    public void ResolveLength()
    {
        Assert.Equal((float?)1.0f, Dimension.FromLength(1.0f).MaybeResolve((float?)null, Calc));
        Assert.Equal((float?)1.0f, Dimension.FromLength(1.0f).MaybeResolve((float?)5.0f, Calc));
        Assert.Equal((float?)1.0f, Dimension.FromLength(1.0f).MaybeResolve((float?)(-5.0f), Calc));
        Assert.Equal((float?)1.0f, Dimension.FromLength(1.0f).MaybeResolve((float?)0.0f, Calc));
    }

    /// <summary>A percentage returns null without a context, else percent * context.</summary>
    [Fact]
    public void ResolvePercent()
    {
        Assert.Null(Dimension.FromPercent(1.0f).MaybeResolve((float?)null, Calc));
        Assert.Equal((float?)5.0f, Dimension.FromPercent(1.0f).MaybeResolve((float?)5.0f, Calc));
        Assert.Equal((float?)(-5.0f), Dimension.FromPercent(1.0f).MaybeResolve((float?)(-5.0f), Calc));
        Assert.Equal((float?)50.0f, Dimension.FromPercent(1.0f).MaybeResolve((float?)50.0f, Calc));
    }
}

public class MaybeResolveSizeDimensionTests
{
    private static readonly CalcResolver Calc = static (_, _) => 42.42f;

    [Fact]
    public void MaybeResolveAuto()
    {
        Assert.Equal(
            GeometryExtensions.SizeNone,
            GeometryExtensions.SizeDimensionAuto.MaybeResolve(GeometryExtensions.SizeNone, Calc));
        Assert.Equal(
            GeometryExtensions.SizeNone,
            GeometryExtensions.SizeDimensionAuto.MaybeResolve(GeometryExtensions.SizeSome(5.0f, 5.0f), Calc));
        Assert.Equal(
            GeometryExtensions.SizeNone,
            GeometryExtensions.SizeDimensionAuto.MaybeResolve(GeometryExtensions.SizeSome(-5.0f, -5.0f), Calc));
        Assert.Equal(
            GeometryExtensions.SizeNone,
            GeometryExtensions.SizeDimensionAuto.MaybeResolve(GeometryExtensions.SizeSome(0.0f, 0.0f), Calc));
    }

    [Fact]
    public void MaybeResolveLength()
    {
        var lengths = GeometryExtensions.SizeFromLengths(5.0f, 5.0f);
        Assert.Equal(
            GeometryExtensions.SizeSome(5.0f, 5.0f), lengths.MaybeResolve(GeometryExtensions.SizeNone, Calc));
        Assert.Equal(
            GeometryExtensions.SizeSome(5.0f, 5.0f),
            lengths.MaybeResolve(GeometryExtensions.SizeSome(5.0f, 5.0f), Calc));
        Assert.Equal(
            GeometryExtensions.SizeSome(5.0f, 5.0f),
            lengths.MaybeResolve(GeometryExtensions.SizeSome(-5.0f, -5.0f), Calc));
        Assert.Equal(
            GeometryExtensions.SizeSome(5.0f, 5.0f),
            lengths.MaybeResolve(GeometryExtensions.SizeSome(0.0f, 0.0f), Calc));
    }

    [Fact]
    public void MaybeResolvePercent()
    {
        var percents = GeometryExtensions.SizeFromPercent(5.0f, 5.0f);
        Assert.Equal(
            GeometryExtensions.SizeNone, percents.MaybeResolve(GeometryExtensions.SizeNone, Calc));
        Assert.Equal(
            GeometryExtensions.SizeSome(25.0f, 25.0f),
            percents.MaybeResolve(GeometryExtensions.SizeSome(5.0f, 5.0f), Calc));
        Assert.Equal(
            GeometryExtensions.SizeSome(-25.0f, -25.0f),
            percents.MaybeResolve(GeometryExtensions.SizeSome(-5.0f, -5.0f), Calc));
        Assert.Equal(
            GeometryExtensions.SizeSome(0.0f, 0.0f),
            percents.MaybeResolve(GeometryExtensions.SizeSome(0.0f, 0.0f), Calc));
    }
}

public class ResolveOrZeroDimensionToF32Tests
{
    private static readonly CalcResolver Calc = static (_, _) => 42.42f;

    [Fact]
    public void ResolveOrZeroAuto()
    {
        Assert.Equal(0.0f, Dimension.Auto.ResolveOrZero((float?)null, Calc));
        Assert.Equal(0.0f, Dimension.Auto.ResolveOrZero((float?)5.0f, Calc));
        Assert.Equal(0.0f, Dimension.Auto.ResolveOrZero((float?)(-5.0f), Calc));
        Assert.Equal(0.0f, Dimension.Auto.ResolveOrZero((float?)0.0f, Calc));
    }

    [Fact]
    public void ResolveOrZeroLength()
    {
        Assert.Equal(5.0f, Dimension.FromLength(5.0f).ResolveOrZero((float?)null, Calc));
        Assert.Equal(5.0f, Dimension.FromLength(5.0f).ResolveOrZero((float?)5.0f, Calc));
        Assert.Equal(5.0f, Dimension.FromLength(5.0f).ResolveOrZero((float?)(-5.0f), Calc));
        Assert.Equal(5.0f, Dimension.FromLength(5.0f).ResolveOrZero((float?)0.0f, Calc));
    }

    [Fact]
    public void ResolveOrZeroPercent()
    {
        Assert.Equal(0.0f, Dimension.FromPercent(5.0f).ResolveOrZero((float?)null, Calc));
        Assert.Equal(25.0f, Dimension.FromPercent(5.0f).ResolveOrZero((float?)5.0f, Calc));
        Assert.Equal(-25.0f, Dimension.FromPercent(5.0f).ResolveOrZero((float?)(-5.0f), Calc));
        Assert.Equal(0.0f, Dimension.FromPercent(5.0f).ResolveOrZero((float?)0.0f, Calc));
    }
}

public class ResolveOrZeroRectDimensionToRectTests
{
    private static readonly CalcResolver Calc = static (_, _) => 42.42f;

    [Fact]
    public void ResolveOrZeroAuto()
    {
        var auto = GeometryExtensions.RectDimensionAuto;
        Assert.Equal(GeometryExtensions.RectZero, auto.ResolveOrZero(GeometryExtensions.SizeNone, Calc));
        Assert.Equal(
            GeometryExtensions.RectZero, auto.ResolveOrZero(GeometryExtensions.SizeSome(5.0f, 5.0f), Calc));
        Assert.Equal(
            GeometryExtensions.RectZero, auto.ResolveOrZero(GeometryExtensions.SizeSome(-5.0f, -5.0f), Calc));
        Assert.Equal(
            GeometryExtensions.RectZero, auto.ResolveOrZero(GeometryExtensions.SizeSome(0.0f, 0.0f), Calc));
    }

    [Fact]
    public void ResolveOrZeroLength()
    {
        var lengths = GeometryExtensions.RectFromLength(5.0f, 5.0f, 5.0f, 5.0f);
        var expected = new Rect<float>(5.0f, 5.0f, 5.0f, 5.0f);
        Assert.Equal(expected, lengths.ResolveOrZero(GeometryExtensions.SizeNone, Calc));
        Assert.Equal(expected, lengths.ResolveOrZero(GeometryExtensions.SizeSome(5.0f, 5.0f), Calc));
        Assert.Equal(expected, lengths.ResolveOrZero(GeometryExtensions.SizeSome(-5.0f, -5.0f), Calc));
        Assert.Equal(expected, lengths.ResolveOrZero(GeometryExtensions.SizeSome(0.0f, 0.0f), Calc));
    }

    [Fact]
    public void ResolveOrZeroPercent()
    {
        var percents = GeometryExtensions.RectFromPercent(5.0f, 5.0f, 5.0f, 5.0f);
        Assert.Equal(GeometryExtensions.RectZero, percents.ResolveOrZero(GeometryExtensions.SizeNone, Calc));
        Assert.Equal(
            new Rect<float>(25.0f, 25.0f, 25.0f, 25.0f),
            percents.ResolveOrZero(GeometryExtensions.SizeSome(5.0f, 5.0f), Calc));
        Assert.Equal(
            new Rect<float>(-25.0f, -25.0f, -25.0f, -25.0f),
            percents.ResolveOrZero(GeometryExtensions.SizeSome(-5.0f, -5.0f), Calc));
        Assert.Equal(
            GeometryExtensions.RectZero, percents.ResolveOrZero(GeometryExtensions.SizeSome(0.0f, 0.0f), Calc));
    }
}

public class ResolveOrZeroRectDimensionToRectViaOptionTests
{
    private static readonly CalcResolver Calc = static (_, _) => 42.42f;

    [Fact]
    public void ResolveOrZeroAuto()
    {
        var auto = GeometryExtensions.RectDimensionAuto;
        Assert.Equal(GeometryExtensions.RectZero, auto.ResolveOrZero((float?)null, Calc));
        Assert.Equal(GeometryExtensions.RectZero, auto.ResolveOrZero((float?)5.0f, Calc));
        Assert.Equal(GeometryExtensions.RectZero, auto.ResolveOrZero((float?)(-5.0f), Calc));
        Assert.Equal(GeometryExtensions.RectZero, auto.ResolveOrZero((float?)0.0f, Calc));
    }

    [Fact]
    public void ResolveOrZeroLength()
    {
        var lengths = GeometryExtensions.RectFromLength(5.0f, 5.0f, 5.0f, 5.0f);
        var expected = new Rect<float>(5.0f, 5.0f, 5.0f, 5.0f);
        Assert.Equal(expected, lengths.ResolveOrZero((float?)null, Calc));
        Assert.Equal(expected, lengths.ResolveOrZero((float?)5.0f, Calc));
        Assert.Equal(expected, lengths.ResolveOrZero((float?)(-5.0f), Calc));
        Assert.Equal(expected, lengths.ResolveOrZero((float?)0.0f, Calc));
    }

    [Fact]
    public void ResolveOrZeroPercent()
    {
        var percents = GeometryExtensions.RectFromPercent(5.0f, 5.0f, 5.0f, 5.0f);
        Assert.Equal(GeometryExtensions.RectZero, percents.ResolveOrZero((float?)null, Calc));
        Assert.Equal(new Rect<float>(25.0f, 25.0f, 25.0f, 25.0f), percents.ResolveOrZero((float?)5.0f, Calc));
        Assert.Equal(
            new Rect<float>(-25.0f, -25.0f, -25.0f, -25.0f), percents.ResolveOrZero((float?)(-5.0f), Calc));
        Assert.Equal(GeometryExtensions.RectZero, percents.ResolveOrZero((float?)0.0f, Calc));
    }
}

// ---------------------------------------------------------------------------
// vendor/taffy/src/tree/taffy_tree.rs
// ---------------------------------------------------------------------------

public class TaffyTreeTests
{
    private static Size<float> SizeMeasureFunction(
        Size<float?> knownDimensions,
        Size<AvailableSpace> availableSpace,
        NodeId nodeId,
        Size<float> nodeContext,
        Style style) => knownDimensions.UnwrapOr(nodeContext);

    [Fact]
    public void NewShouldAllocateDefaultCapacity()
    {
        const int DefaultCapacity = 16;
        var taffy = new TaffyTree<object>();

        Assert.True(taffy.ChildrenCapacity >= DefaultCapacity);
        Assert.True(taffy.ParentsCapacity >= DefaultCapacity);
        Assert.True(taffy.NodesCapacity >= DefaultCapacity);
    }

    [Fact]
    public void TestWithCapacity()
    {
        const int Capacity = 8;
        var taffy = new TaffyTree<object>(Capacity);

        Assert.True(taffy.ChildrenCapacity >= Capacity);
        Assert.True(taffy.ParentsCapacity >= Capacity);
        Assert.True(taffy.NodesCapacity >= Capacity);
    }

    [Fact]
    public void TestNewLeaf()
    {
        var taffy = new TaffyTree<object>();

        var node = taffy.NewLeaf(new Style());

        // node should be in the taffy tree and have no children
        Assert.Equal(0, taffy.ChildCount(node));
    }

    [Fact]
    public void NewLeafWithContext()
    {
        var taffy = new TaffyTree<Size<float>>();

        var node = taffy.NewLeafWithContext(new Style(), GeometryExtensions.SizeZero);

        Assert.Equal(0, taffy.ChildCount(node));
    }

    [Fact]
    public void TestNewWithChildren()
    {
        var taffy = new TaffyTree<object>();
        var child0 = taffy.NewLeaf(new Style());
        var child1 = taffy.NewLeaf(new Style());
        var node = taffy.NewWithChildren(new Style(), [child0, child1]);

        Assert.Equal(2, taffy.ChildCount(node));
        Assert.Equal(child0, taffy.Children(node)[0]);
        Assert.Equal(child1, taffy.Children(node)[1]);
    }

    [Fact]
    public void RemoveNodeShouldRemove()
    {
        var taffy = new TaffyTree<object>();
        var node = taffy.NewLeaf(new Style());
        _ = taffy.Remove(node);
    }

    [Fact]
    public void RemoveNodeShouldDetachHierarchy()
    {
        var taffy = new TaffyTree<object>();

        // Build a linear tree layout: <0> <- <1> <- <2>
        var node2 = taffy.NewLeaf(new Style());
        var node1 = taffy.NewWithChildren(new Style(), [node2]);
        var node0 = taffy.NewWithChildren(new Style(), [node1]);

        Assert.Equal(new List<NodeId> { node1 }, taffy.Children(node0));
        Assert.Equal(new List<NodeId> { node2 }, taffy.Children(node1));

        // Disconnect the tree: <0> <2>
        _ = taffy.Remove(node1);

        Assert.Empty(taffy.Children(node0));
        Assert.Empty(taffy.Children(node2));
    }

    [Fact]
    public void RemoveLastNode()
    {
        var taffy = new TaffyTree<object>();

        var parent = taffy.NewLeaf(new Style());
        var child = taffy.NewLeaf(new Style());
        taffy.AddChild(parent, child);

        taffy.Remove(child);
        taffy.Remove(parent);
    }

    [Fact]
    public void SetMeasure()
    {
        var taffy = new TaffyTree<Size<float>>();
        var node = taffy.NewLeafWithContext(new Style(), new Size<float>(200.0f, 200.0f));
        taffy.ComputeLayoutWithMeasure(node, GeometryExtensions.SizeMaxContent, SizeMeasureFunction);
        Assert.Equal(200.0f, taffy.GetLayout(node).Size.Width);

        taffy.SetNodeContext(node, new Size<float>(100.0f, 100.0f));
        taffy.ComputeLayoutWithMeasure(node, GeometryExtensions.SizeMaxContent, SizeMeasureFunction);
        Assert.Equal(100.0f, taffy.GetLayout(node).Size.Width);
    }

    [Fact]
    public void SetMeasureOfPreviouslyUnmeasuredNode()
    {
        var taffy = new TaffyTree<Size<float>>();
        var node = taffy.NewLeaf(new Style());
        taffy.ComputeLayoutWithMeasure(node, GeometryExtensions.SizeMaxContent, SizeMeasureFunction);
        Assert.Equal(0.0f, taffy.GetLayout(node).Size.Width);

        taffy.SetNodeContext(node, new Size<float>(100.0f, 100.0f));
        taffy.ComputeLayoutWithMeasure(node, GeometryExtensions.SizeMaxContent, SizeMeasureFunction);
        Assert.Equal(100.0f, taffy.GetLayout(node).Size.Width);
    }

    [Fact]
    public void AddChild()
    {
        var taffy = new TaffyTree<object>();
        var node = taffy.NewLeaf(new Style());
        Assert.Equal(0, taffy.ChildCount(node));

        var child0 = taffy.NewLeaf(new Style());
        taffy.AddChild(node, child0);
        Assert.Equal(1, taffy.ChildCount(node));

        var child1 = taffy.NewLeaf(new Style());
        taffy.AddChild(node, child1);
        Assert.Equal(2, taffy.ChildCount(node));
    }

    [Fact]
    public void InsertChildAtIndex()
    {
        var taffy = new TaffyTree<object>();

        var child0 = taffy.NewLeaf(new Style());
        var child1 = taffy.NewLeaf(new Style());
        var child2 = taffy.NewLeaf(new Style());

        var node = taffy.NewLeaf(new Style());
        Assert.Equal(0, taffy.ChildCount(node));

        taffy.InsertChildAtIndex(node, 0, child0);
        Assert.Equal(1, taffy.ChildCount(node));
        Assert.Equal(child0, taffy.Children(node)[0]);

        taffy.InsertChildAtIndex(node, 0, child1);
        Assert.Equal(2, taffy.ChildCount(node));
        Assert.Equal(child1, taffy.Children(node)[0]);
        Assert.Equal(child0, taffy.Children(node)[1]);

        taffy.InsertChildAtIndex(node, 1, child2);
        Assert.Equal(3, taffy.ChildCount(node));
        Assert.Equal(child1, taffy.Children(node)[0]);
        Assert.Equal(child2, taffy.Children(node)[1]);
        Assert.Equal(child0, taffy.Children(node)[2]);
    }

    [Fact]
    public void SetChildren()
    {
        var taffy = new TaffyTree<object>();

        var child0 = taffy.NewLeaf(new Style());
        var child1 = taffy.NewLeaf(new Style());
        var node = taffy.NewWithChildren(new Style(), [child0, child1]);

        Assert.Equal(2, taffy.ChildCount(node));
        Assert.Equal(child0, taffy.Children(node)[0]);
        Assert.Equal(child1, taffy.Children(node)[1]);

        var child2 = taffy.NewLeaf(new Style());
        var child3 = taffy.NewLeaf(new Style());
        taffy.SetChildren(node, [child2, child3]);

        Assert.Equal(2, taffy.ChildCount(node));
        Assert.Equal(child2, taffy.Children(node)[0]);
        Assert.Equal(child3, taffy.Children(node)[1]);
    }

    [Fact]
    public void RemoveChild()
    {
        var taffy = new TaffyTree<object>();
        var child0 = taffy.NewLeaf(new Style());
        var child1 = taffy.NewLeaf(new Style());
        var node = taffy.NewWithChildren(new Style(), [child0, child1]);

        Assert.Equal(2, taffy.ChildCount(node));

        taffy.RemoveChild(node, child0);
        Assert.Equal(1, taffy.ChildCount(node));
        Assert.Equal(child1, taffy.Children(node)[0]);

        taffy.RemoveChild(node, child1);
        Assert.Equal(0, taffy.ChildCount(node));
    }

    [Fact]
    public void RemoveChildAtIndex()
    {
        var taffy = new TaffyTree<object>();
        var child0 = taffy.NewLeaf(new Style());
        var child1 = taffy.NewLeaf(new Style());
        var node = taffy.NewWithChildren(new Style(), [child0, child1]);

        Assert.Equal(2, taffy.ChildCount(node));

        taffy.RemoveChildAtIndex(node, 0);
        Assert.Equal(1, taffy.ChildCount(node));
        Assert.Equal(child1, taffy.Children(node)[0]);

        taffy.RemoveChildAtIndex(node, 0);
        Assert.Equal(0, taffy.ChildCount(node));
    }

    [Fact]
    public void RemoveChildrenRange()
    {
        var taffy = new TaffyTree<object>();
        var child0 = taffy.NewLeaf(new Style());
        var child1 = taffy.NewLeaf(new Style());
        var child2 = taffy.NewLeaf(new Style());
        var child3 = taffy.NewLeaf(new Style());
        var node = taffy.NewWithChildren(new Style(), [child0, child1, child2, child3]);

        Assert.Equal(4, taffy.ChildCount(node));

        taffy.RemoveChildrenRange(node, 1, 3);
        Assert.Equal(2, taffy.ChildCount(node));
        Assert.Equal(new List<NodeId> { child0, child3 }, taffy.Children(node));
        foreach (var child in new[] { child0, child3 })
        {
            Assert.Equal(node, taffy.Parent(child));
        }

        foreach (var child in new[] { child1, child2 })
        {
            Assert.Null(taffy.Parent(child));
        }
    }

    // Related to: https://github.com/DioxusLabs/taffy/issues/510
    [Fact]
    public void RemoveChildUpdatesParents()
    {
        var taffy = new TaffyTree<object>();

        var parent = taffy.NewLeaf(new Style());
        var child = taffy.NewLeaf(new Style());

        taffy.AddChild(parent, child);

        taffy.Remove(parent);

        // Once the parent is removed this shouldn't throw.
        taffy.SetChildren(child, []);
    }

    [Fact]
    public void ReplaceChildAtIndex()
    {
        var taffy = new TaffyTree<object>();

        var child0 = taffy.NewLeaf(new Style());
        var child1 = taffy.NewLeaf(new Style());

        var node = taffy.NewWithChildren(new Style(), [child0]);
        Assert.Equal(1, taffy.ChildCount(node));
        Assert.Equal(child0, taffy.Children(node)[0]);

        taffy.ReplaceChildAtIndex(node, 0, child1);
        Assert.Equal(1, taffy.ChildCount(node));
        Assert.Equal(child1, taffy.Children(node)[0]);
    }

    [Fact]
    public void TestChildAtIndex()
    {
        var taffy = new TaffyTree<object>();
        var child0 = taffy.NewLeaf(new Style());
        var child1 = taffy.NewLeaf(new Style());
        var child2 = taffy.NewLeaf(new Style());
        var node = taffy.NewWithChildren(new Style(), [child0, child1, child2]);

        Assert.Equal(child0, taffy.ChildAtIndex(node, 0));
        Assert.Equal(child1, taffy.ChildAtIndex(node, 1));
        Assert.Equal(child2, taffy.ChildAtIndex(node, 2));
    }

    [Fact]
    public void TestChildCount()
    {
        var taffy = new TaffyTree<object>();
        var child0 = taffy.NewLeaf(new Style());
        var child1 = taffy.NewLeaf(new Style());
        var node = taffy.NewWithChildren(new Style(), [child0, child1]);

        Assert.Equal(2, taffy.ChildCount(node));
        Assert.Equal(0, taffy.ChildCount(child0));
        Assert.Equal(0, taffy.ChildCount(child1));
    }

    [Fact]
    public void TestChildren()
    {
        var taffy = new TaffyTree<object>();
        var child0 = taffy.NewLeaf(new Style());
        var child1 = taffy.NewLeaf(new Style());
        var node = taffy.NewWithChildren(new Style(), [child0, child1]);

        var children = new List<NodeId> { child0, child1 };

        Assert.Equal(children, taffy.Children(node));
        Assert.Empty(taffy.Children(child0));
    }

    [Fact]
    public void TestSetStyle()
    {
        var taffy = new TaffyTree<object>();

        var node = taffy.NewLeaf(new Style());
        Assert.Equal(Display.Flex, taffy.GetStyle(node).Display);

        taffy.SetStyle(node, new Style { Display = Display.None });
        Assert.Equal(Display.None, taffy.GetStyle(node).Display);
    }

    [Fact]
    public void TestStyle()
    {
        var taffy = new TaffyTree<object>();

        var style = new Style { Display = Display.None, FlexDirection = FlexDirection.RowReverse };

        var node = taffy.NewLeaf(style.Clone());

        Assert.Equal(style, taffy.GetStyle(node));
    }

    [Fact]
    public void TestLayout()
    {
        var taffy = new TaffyTree<object>();
        var node = taffy.NewLeaf(new Style());

        _ = taffy.GetLayout(node);
    }

    [Fact]
    public void TestMarkDirty()
    {
        var taffy = new TaffyTree<object>();
        var child0 = taffy.NewLeaf(new Style());
        var child1 = taffy.NewLeaf(new Style());
        var node = taffy.NewWithChildren(new Style(), [child0, child1]);

        taffy.ComputeLayout(node, GeometryExtensions.SizeMaxContent);

        Assert.False(taffy.IsDirty(child0));
        Assert.False(taffy.IsDirty(child1));
        Assert.False(taffy.IsDirty(node));

        taffy.MarkDirty(node);
        Assert.False(taffy.IsDirty(child0));
        Assert.False(taffy.IsDirty(child1));
        Assert.True(taffy.IsDirty(node));

        taffy.ComputeLayout(node, GeometryExtensions.SizeMaxContent);
        taffy.MarkDirty(child0);
        Assert.True(taffy.IsDirty(child0));
        Assert.False(taffy.IsDirty(child1));
        Assert.True(taffy.IsDirty(node));
    }

    [Fact]
    public void ComputeLayoutShouldProduceValidResult()
    {
        var taffy = new TaffyTree<object>();
        var node = taffy.NewLeaf(new Style
        {
            Size = new Size<Dimension>(Dimension.FromLength(10.0f), Dimension.FromLength(10.0f)),
        });
        taffy.ComputeLayout(
            node,
            new Size<AvailableSpace>(AvailableSpace.Definite(100.0f), AvailableSpace.Definite(100.0f)));
    }

    [Fact]
    public void MakeSureLayoutLocationIsTopLeft()
    {
        var taffy = new TaffyTree<object>();

        var node = taffy.NewLeaf(new Style
        {
            Size = new Size<Dimension>(Dimension.FromPercent(1.0f), Dimension.FromPercent(1.0f)),
        });

        var root = taffy.NewWithChildren(
            new Style
            {
                Size = new Size<Dimension>(Dimension.FromLength(100.0f), Dimension.FromLength(100.0f)),
                Padding = new Rect<LengthPercentage>(
                    LengthPercentage.FromLength(10.0f),
                    LengthPercentage.FromLength(20.0f),
                    LengthPercentage.FromLength(30.0f),
                    LengthPercentage.FromLength(40.0f)),
            },
            [node]);

        taffy.ComputeLayout(root, GeometryExtensions.SizeMaxContent);

        // If Layout.Location represents the top-left corner, 'node' must be at
        // {x: 10, y: 30} because of the root's padding.
        var layout = taffy.GetLayout(node);
        Assert.Equal(10.0f, layout.Location.X);
        Assert.Equal(30.0f, layout.Location.Y);
    }

    [Fact]
    public void SetChildrenReparents()
    {
        var taffy = new TaffyTree<object>();
        var child = taffy.NewLeaf(new Style());
        var oldParent = taffy.NewWithChildren(new Style(), [child]);

        var newParent = taffy.NewLeaf(new Style());
        taffy.SetChildren(newParent, [child]);

        Assert.Empty(taffy.Children(oldParent));
    }
}

// ---------------------------------------------------------------------------
// vendor/taffy/src/compute/mod.rs
// ---------------------------------------------------------------------------

public class ComputeTests
{
    [Fact]
    public void HiddenLayoutShouldHideRecursively()
    {
        var taffy = new TaffyTree<object>();

        static Style MakeStyle() => new()
        {
            Display = Display.Flex,
            Size = GeometryExtensions.SizeFromLengths(50.0f, 50.0f),
        };

        var grandchild00 = taffy.NewLeaf(MakeStyle());
        var grandchild01 = taffy.NewLeaf(MakeStyle());
        var child00 = taffy.NewWithChildren(MakeStyle(), [grandchild00, grandchild01]);

        var grandchild02 = taffy.NewLeaf(MakeStyle());
        var child01 = taffy.NewWithChildren(MakeStyle(), [grandchild02]);

        var root = taffy.NewWithChildren(
            new Style { Display = Display.None, Size = GeometryExtensions.SizeFromLengths(50.0f, 50.0f) },
            [child00, child01]);

        Compute.ComputeHiddenLayout(taffy.AsLayoutTree(), root);

        // Whatever size and display-mode the nodes had previously, all layouts should resolve to
        // ZERO due to the root's Display.None.
        foreach (var node in new[] { root, child00, child01, grandchild00, grandchild01, grandchild02 })
        {
            var layout = taffy.GetLayout(node);
            Assert.Equal(GeometryExtensions.SizeZero, layout.Size);
            Assert.Equal(GeometryExtensions.PointZero, layout.Location);
        }
    }
}

// ---------------------------------------------------------------------------
// vendor/taffy/src/compute/float.rs
// ---------------------------------------------------------------------------

public class FloatContextTests
{
    private static Point<float> PlaceAt(
        FloatContext context,
        float width,
        float height,
        float minY,
        FloatDirection direction) =>
        context.PlaceFloatedBox(new Size<float>(width, height), minY, 0.0f, 0.0f, direction, Clear.None);

    private static void Place(FloatContext context, float width, float height, FloatDirection direction) =>
        PlaceAt(context, width, height, 0.0f, direction);

    [Fact]
    public void ClearanceKeepsTheTallestBottomAfterSegmentSubdivision()
    {
        var context = new FloatContext();
        context.SetWidth(1000.0f);

        Place(context, 241.0f, 100.0f, FloatDirection.Left);
        Place(context, 434.0f, 73.0f, FloatDirection.Left);
        Place(context, 80.0f, 73.0f, FloatDirection.Right);

        Assert.Equal((float?)100.0f, context.ClearedThreshold(Clear.Left));
        Assert.Equal((float?)73.0f, context.ClearedThreshold(Clear.Right));
        Assert.Equal((float?)100.0f, context.ClearedThreshold(Clear.Both));
    }

    [Fact]
    public void SideClearanceIsMonotonicForLeftAndRightFloats()
    {
        foreach (var direction in new[] { FloatDirection.Left, FloatDirection.Right })
        {
            var context = new FloatContext();
            context.SetWidth(1000.0f);

            Place(context, 200.0f, 120.0f, direction);
            Place(context, 200.0f, 40.0f, direction);

            var matchingClear = direction == FloatDirection.Left ? Clear.Left : Clear.Right;
            Assert.Equal((float?)120.0f, context.ClearedThreshold(matchingClear));
            Assert.Equal((float?)120.0f, context.ClearedThreshold(Clear.Both));
        }
    }

    [Fact]
    public void ClearedFloatAndContentSlotUseTheGeometricBottom()
    {
        var context = new FloatContext();
        context.SetWidth(1000.0f);

        Place(context, 241.0f, 100.0f, FloatDirection.Left);
        Place(context, 434.0f, 73.0f, FloatDirection.Left);

        var slot = context.FindContentSlot(0.0f, 0.0f, 0.0f, Clear.Left, null);
        Assert.Equal(100.0f, slot.Y);

        var cleared = context.PlaceFloatedBox(
            new Size<float>(100.0f, 20.0f), 0.0f, 0.0f, 0.0f, FloatDirection.Right, Clear.Left);
        Assert.Equal(100.0f, cleared.Y);
    }

    [Fact]
    public void SourceOrderTopSurvivesASegmentInsertedBeforeTheOldStart()
    {
        var context = new FloatContext();
        context.SetWidth(1000.0f);

        var first = PlaceAt(context, 200.0f, 50.0f, 50.0f, FloatDirection.Left);
        Assert.Equal(50.0f, first.Y);

        // This models the index shift caused when another float subdivides an earlier free segment.
        // Source-order placement must be independent of the old segment index.
        context.SubdivideSegment(0, 25.0f);
        var later = PlaceAt(context, 200.0f, 20.0f, 0.0f, FloatDirection.Left);
        Assert.Equal(50.0f, later.Y);
    }
}
