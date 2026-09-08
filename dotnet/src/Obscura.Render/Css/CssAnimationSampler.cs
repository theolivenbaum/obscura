using System.Globalization;
using Obscura.Dom;

namespace Obscura.Render.Css;

/// <summary>A sampled length that keeps <c>auto</c> and deferred expressions apart.</summary>
internal abstract record AnimatedLength
{
    private AnimatedLength()
    {
    }

    public sealed record AutoLength : AnimatedLength
    {
        public static readonly AutoLength Instance = new();
    }

    public sealed record DimensionLength(Dimension Value) : AnimatedLength;

    public sealed record ExpressionLength(string Value) : AnimatedLength;
}

/// <summary>One animatable property's value in the form the sampler interpolates.</summary>
internal abstract record AnimationValue
{
    private AnimationValue()
    {
    }

    public sealed record Length(AnimatedLength Value) : AnimationValue;

    public sealed record Number(float Value) : AnimationValue;

    public sealed record Color(RgbaColor Value) : AnimationValue;

    public sealed record Transform(List<TransformOp> Operations) : AnimationValue;

    public sealed record Translate(TransformLength X, TransformLength Y) : AnimationValue;

    public sealed record Rotate(float Degrees) : AnimationValue;

    public sealed record Scale(float X, float Y) : AnimationValue;

    public sealed record BgPosition(BackgroundPosition Value) : AnimationValue;

    public sealed record Visibility(bool Visible) : AnimationValue;
}

/// <summary>Which timing phase a local animation time falls in.</summary>
internal enum AnimationPhase
{
    Before,
    Active,
    After,
}

/// <summary>
/// Transform-only Web Animations replayed from their exact underlying cascade
/// value. Callers apply the result only after every target has passed preflight.
/// </summary>
public sealed class ResampledVisualWaapi
{
    public required List<TransformOp> TransformOps { get; init; }

    public required float? Opacity { get; init; }

    public required bool HasTransformEffect { get; init; }

    public required bool HasOpacityEffect { get; init; }

    public required bool EstablishesTransformContainingBlock { get; init; }
}

/// <summary>
/// The CSS Animations and Web Animations sampler: resolves keyframe tracks to
/// concrete values at one sample time and writes them into a
/// <see cref="LayoutStyle"/> at the animation cascade origin.
/// </summary>
public static class CssAnimationSampler
{
    /// <summary>Rust <c>css::sample_animation_properties</c>.</summary>
    internal static void SampleAnimationProperties(
        Keyframes keyframes,
        LayoutStyle style,
        AnimationTiming timing,
        AnimationSampleTime sampleTime,
        IReadOnlyDictionary<string, string> props)
    {
        if (DirectedProgress(timing, sampleTime) is not { } progress)
        {
            return;
        }

        // Keep the base style immutable while resolving every property, so one
        // sampled property never becomes another track's implicit endpoint.
        var underlying = style.Clone();
        foreach (var (property, track) in keyframes.Tracks)
        {
            if (SamplePropertyTrack(property, track, underlying, progress, props) is { } value)
            {
                ApplyAnimationValue(style, property, value);
            }
        }
    }

    /// <summary>Rust <c>css::sample_property_track</c>.</summary>
    private static AnimationValue? SamplePropertyTrack(
        AnimatedProperty property,
        IReadOnlyList<PropertyTrackStop> track,
        LayoutStyle underlying,
        float progress,
        IReadOnlyDictionary<string, string> props)
    {
        if (AnimationValueFromStyle(property, underlying) is not { } underlyingValue)
        {
            return null;
        }

        var resolved = new List<(float Offset, AnimationValue Value)>(track.Count + 2);
        foreach (var stop in track)
        {
            var substituted = CssVariables.SubstituteVarValue(stop.Declaration.Value, props, 0);
            if (substituted is null)
            {
                continue;
            }

            if (!CssKeyframes.SupportsAnimationDeclaration(stop.Declaration.Name, substituted))
            {
                continue;
            }

            var endpoint = underlying.Clone();
            ComputedStyle.ApplyAnimationPropertyValue(endpoint, stop.Declaration.Name, substituted);
            if (AnimationValueFromStyle(property, endpoint) is { } endpointValue)
            {
                resolved.Add((stop.Offset, endpointValue));
            }
        }

        if (resolved.Count == 0)
        {
            return null;
        }

        if (resolved[0].Offset > 0f)
        {
            resolved.Insert(0, (0f, underlyingValue));
        }

        if (resolved[^1].Offset < 1f)
        {
            resolved.Add((1f, underlyingValue));
        }

        if (progress <= resolved[0].Offset)
        {
            return resolved[0].Value;
        }

        for (var index = 0; index + 1 < resolved.Count; index++)
        {
            var (fromOffset, fromValue) = resolved[index];
            var (toOffset, toValue) = resolved[index + 1];
            if (progress <= toOffset)
            {
                if (progress == toOffset || toOffset == fromOffset)
                {
                    return toValue;
                }

                var position = (progress - fromOffset) / (toOffset - fromOffset);
                return InterpolateAnimationValue(fromValue, toValue, position);
            }
        }

        return resolved[^1].Value;
    }

    /// <summary>Rust <c>css::animation_value_from_style</c>.</summary>
    private static AnimationValue? AnimationValueFromStyle(AnimatedProperty property, LayoutStyle style)
    {
        switch (property)
        {
            case AnimatedProperty.Transform:
                return new AnimationValue.Transform([.. style.TransformOps]);
            case AnimatedProperty.Translate:
            {
                var (x, y) = style.IndividualTranslate ?? (Dimension.Px(0f), Dimension.Px(0f));
                return new AnimationValue.Translate(
                    new TransformLength(x, style.IndividualTranslateExpressions[0]),
                    new TransformLength(y, style.IndividualTranslateExpressions[1]));
            }

            case AnimatedProperty.Rotate:
                return new AnimationValue.Rotate(style.IndividualRotate ?? 0f);
            case AnimatedProperty.Scale:
            {
                var (x, y) = style.IndividualScale ?? (1f, 1f);
                return new AnimationValue.Scale(x, y);
            }

            case AnimatedProperty.Width:
            case AnimatedProperty.Height:
            case AnimatedProperty.MinWidth:
            case AnimatedProperty.MinHeight:
            case AnimatedProperty.MaxWidth:
            case AnimatedProperty.MaxHeight:
            {
                if (property == AnimatedProperty.Width && style.WidthFitContent)
                {
                    return null;
                }

                var index = SizeIndex(property);
                var dimension = property switch
                {
                    AnimatedProperty.Width => style.Width,
                    AnimatedProperty.Height => style.Height,
                    AnimatedProperty.MinWidth => style.MinWidth,
                    AnimatedProperty.MinHeight => style.MinHeight,
                    AnimatedProperty.MaxWidth => style.MaxWidth,
                    _ => style.MaxHeight,
                };
                return new AnimationValue.Length(LengthWithExpression(dimension, style.SizeExpressions[index]));
            }

            case AnimatedProperty.Top:
            case AnimatedProperty.Right:
            case AnimatedProperty.Bottom:
            case AnimatedProperty.Left:
            {
                var index = PhysicalSideIndex(property);
                AnimatedLength length = style.InsetExpressions[index] is { } expression
                    ? new AnimatedLength.ExpressionLength(expression)
                    : style.Inset[index] is { } dimension
                        ? new AnimatedLength.DimensionLength(dimension)
                        : AnimatedLength.AutoLength.Instance;
                return new AnimationValue.Length(length);
            }

            case AnimatedProperty.MarginTop:
            case AnimatedProperty.MarginRight:
            case AnimatedProperty.MarginBottom:
            case AnimatedProperty.MarginLeft:
                return new AnimationValue.Length(MarginLength(style, PhysicalSideIndex(property)));

            case AnimatedProperty.PaddingTop:
            case AnimatedProperty.PaddingRight:
            case AnimatedProperty.PaddingBottom:
            case AnimatedProperty.PaddingLeft:
                return new AnimationValue.Length(PaddingLength(style, PhysicalSideIndex(property)));

            case AnimatedProperty.RowGap:
            case AnimatedProperty.ColumnGap:
            {
                var isRow = property == AnimatedProperty.RowGap;
                var value = isRow ? style.RowGap : style.ColumnGap;
                var expression = isRow ? style.RowGapExpression : style.ColumnGapExpression;
                AnimatedLength length = expression is not null
                    ? new AnimatedLength.ExpressionLength(expression)
                    : value is { } pixels
                        ? new AnimatedLength.DimensionLength(Dimension.Px(pixels))
                        : AnimatedLength.AutoLength.Instance;
                return new AnimationValue.Length(length);
            }

            case AnimatedProperty.FlexBasis:
                return new AnimationValue.Length(new AnimatedLength.DimensionLength(style.FlexBasis));

            case AnimatedProperty.Opacity:
                return new AnimationValue.Number(Math.Clamp(style.Opacity ?? 1f, 0f, 1f));

            case AnimatedProperty.Color:
                return new AnimationValue.Color(style.Color ?? new RgbaColor(0, 0, 0, 255));

            case AnimatedProperty.BackgroundColor:
                return new AnimationValue.Color(style.BackgroundColor ?? new RgbaColor(0, 0, 0, 0));

            case AnimatedProperty.BorderTopColor:
            case AnimatedProperty.BorderRightColor:
            case AnimatedProperty.BorderBottomColor:
            case AnimatedProperty.BorderLeftColor:
            {
                var colors = style.BorderModel.Colors;
                var side = property switch
                {
                    AnimatedProperty.BorderTopColor => colors.Top,
                    AnimatedProperty.BorderRightColor => colors.Right,
                    AnimatedProperty.BorderBottomColor => colors.Bottom,
                    _ => colors.Left,
                };
                return new AnimationValue.Color(side ?? style.Color ?? new RgbaColor(0, 0, 0, 255));
            }

            case AnimatedProperty.BackgroundPosition:
                return new AnimationValue.BgPosition(style.BackgroundPosition);

            case AnimatedProperty.Visibility:
                return new AnimationValue.Visibility(!(style.VisibilityHidden ?? false));

            default:
                return null;
        }
    }

    /// <summary>Rust <c>css::apply_animation_value</c>.</summary>
    internal static void ApplyAnimationValue(LayoutStyle style, AnimatedProperty property, AnimationValue value)
    {
        switch (property)
        {
            case AnimatedProperty.Transform when value is AnimationValue.Transform transform:
                style.TransformOps = [.. transform.Operations];
                SetContainingBlockTrigger(style, ContainingBlockTrigger.Transform, style.TransformOps.Count != 0);
                break;
            case AnimatedProperty.Translate when value is AnimationValue.Translate translate:
                style.IndividualTranslate = (translate.X.Value, translate.Y.Value);
                style.IndividualTranslateExpressions[0] = translate.X.Expression;
                style.IndividualTranslateExpressions[1] = translate.Y.Expression;
                SetContainingBlockTrigger(style, ContainingBlockTrigger.Translate, true);
                break;
            case AnimatedProperty.Rotate when value is AnimationValue.Rotate rotate:
                style.IndividualRotate = rotate.Degrees;
                SetContainingBlockTrigger(style, ContainingBlockTrigger.Rotate, true);
                break;
            case AnimatedProperty.Scale when value is AnimationValue.Scale scale:
                style.IndividualScale = (scale.X, scale.Y);
                SetContainingBlockTrigger(style, ContainingBlockTrigger.Scale, true);
                break;

            case AnimatedProperty.Width or AnimatedProperty.Height or AnimatedProperty.MinWidth
                or AnimatedProperty.MinHeight or AnimatedProperty.MaxWidth or AnimatedProperty.MaxHeight
                when value is AnimationValue.Length size:
                SetSizeAnimationValue(style, property, size.Value);
                break;

            case AnimatedProperty.Top or AnimatedProperty.Right or AnimatedProperty.Bottom
                or AnimatedProperty.Left when value is AnimationValue.Length inset:
                SetInsetAnimationValue(style, PhysicalSideIndex(property), inset.Value);
                break;

            case AnimatedProperty.MarginTop or AnimatedProperty.MarginRight
                or AnimatedProperty.MarginBottom or AnimatedProperty.MarginLeft
                when value is AnimationValue.Length margin:
                SetMarginAnimationValue(style, PhysicalSideIndex(property), margin.Value);
                break;

            case AnimatedProperty.PaddingTop or AnimatedProperty.PaddingRight
                or AnimatedProperty.PaddingBottom or AnimatedProperty.PaddingLeft
                when value is AnimationValue.Length padding:
                SetPaddingAnimationValue(style, PhysicalSideIndex(property), padding.Value);
                break;

            case AnimatedProperty.RowGap or AnimatedProperty.ColumnGap when value is AnimationValue.Length gap:
                SetGapAnimationValue(style, property == AnimatedProperty.RowGap, gap.Value);
                break;

            case AnimatedProperty.FlexBasis
                when value is AnimationValue.Length { Value: AnimatedLength.DimensionLength basis }:
                style.FlexBasis = basis.Value;
                break;

            case AnimatedProperty.Opacity when value is AnimationValue.Number number:
                style.Opacity = Math.Clamp(number.Value, 0f, 1f);
                break;

            case AnimatedProperty.Color when value is AnimationValue.Color color:
                style.Color = color.Value;
                break;
            case AnimatedProperty.BackgroundColor when value is AnimationValue.Color background:
                style.BackgroundColor = background.Value;
                break;
            case AnimatedProperty.BorderTopColor when value is AnimationValue.Color top:
                style.BorderModel = style.BorderModel with
                {
                    Colors = style.BorderModel.Colors with { Top = top.Value },
                };
                break;
            case AnimatedProperty.BorderRightColor when value is AnimationValue.Color right:
                style.BorderModel = style.BorderModel with
                {
                    Colors = style.BorderModel.Colors with { Right = right.Value },
                };
                break;
            case AnimatedProperty.BorderBottomColor when value is AnimationValue.Color bottom:
                style.BorderModel = style.BorderModel with
                {
                    Colors = style.BorderModel.Colors with { Bottom = bottom.Value },
                };
                break;
            case AnimatedProperty.BorderLeftColor when value is AnimationValue.Color left:
                style.BorderModel = style.BorderModel with
                {
                    Colors = style.BorderModel.Colors with { Left = left.Value },
                };
                break;

            case AnimatedProperty.BackgroundPosition when value is AnimationValue.BgPosition position:
                style.BackgroundPosition = position.Value;
                break;

            case AnimatedProperty.Visibility when value is AnimationValue.Visibility visibility:
                style.VisibilityHidden = !visibility.Visible;
                break;

            default:
                return;
        }

        SyncUniformBorderColor(style);
    }

    /// <summary>Rust <c>css::interpolate_animation_value</c>.</summary>
    private static AnimationValue InterpolateAnimationValue(AnimationValue from, AnimationValue to, float position)
    {
        switch (from, to)
        {
            case (AnimationValue.Length left, AnimationValue.Length right):
                return new AnimationValue.Length(InterpolateAnimatedLength(left.Value, right.Value, position));
            case (AnimationValue.Number left, AnimationValue.Number right):
                return new AnimationValue.Number(left.Value + ((right.Value - left.Value) * position));
            case (AnimationValue.Color left, AnimationValue.Color right):
                return new AnimationValue.Color(InterpolateColor(left.Value, right.Value, position));
            case (AnimationValue.Transform left, AnimationValue.Transform right):
                return new AnimationValue.Transform(
                    InterpolateTransformList(left.Operations, right.Operations, position));
            case (AnimationValue.Translate left, AnimationValue.Translate right):
            {
                var x = InterpolateTransformLength(left.X, right.X, position);
                var y = InterpolateTransformLength(left.Y, right.Y, position);
                if (x is { } resolvedX && y is { } resolvedY)
                {
                    return new AnimationValue.Translate(resolvedX, resolvedY);
                }

                return position < 0.5f ? from : to;
            }

            case (AnimationValue.Rotate left, AnimationValue.Rotate right):
                return new AnimationValue.Rotate(left.Degrees + ((right.Degrees - left.Degrees) * position));
            case (AnimationValue.Scale left, AnimationValue.Scale right):
                return new AnimationValue.Scale(
                    left.X + ((right.X - left.X) * position),
                    left.Y + ((right.Y - left.Y) * position));
            case (AnimationValue.BgPosition left, AnimationValue.BgPosition right):
                return new AnimationValue.BgPosition(left.Value.Interpolate(right.Value, position));
            case (AnimationValue.Visibility left, AnimationValue.Visibility right):
                // visibility has a special discrete interpolation: if either end
                // is visible, every interior value is visible.
                return new AnimationValue.Visibility(
                    left.Visible || right.Visible
                        ? (position > 0f && position < 1f) || (position <= 0f ? left.Visible : right.Visible)
                        : false);
            default:
                return position < 0.5f ? from : to;
        }
    }

    private static int SizeIndex(AnimatedProperty property) => property switch
    {
        AnimatedProperty.Width => 0,
        AnimatedProperty.Height => 1,
        AnimatedProperty.MinWidth => 2,
        AnimatedProperty.MinHeight => 3,
        AnimatedProperty.MaxWidth => 4,
        _ => 5,
    };

    /// <summary>Rust <c>css::physical_side_index</c>.</summary>
    private static int PhysicalSideIndex(AnimatedProperty property) => property switch
    {
        AnimatedProperty.Top or AnimatedProperty.MarginTop or AnimatedProperty.PaddingTop
            or AnimatedProperty.BorderTopColor => 0,
        AnimatedProperty.Right or AnimatedProperty.MarginRight or AnimatedProperty.PaddingRight
            or AnimatedProperty.BorderRightColor => 1,
        AnimatedProperty.Bottom or AnimatedProperty.MarginBottom or AnimatedProperty.PaddingBottom
            or AnimatedProperty.BorderBottomColor => 2,
        _ => 3,
    };

    private static AnimatedLength LengthWithExpression(Dimension dimension, string? expression) =>
        expression is not null
            ? new AnimatedLength.ExpressionLength(expression)
            : new AnimatedLength.DimensionLength(dimension);

    private static AnimatedLength MarginLength(LayoutStyle style, int index)
    {
        if (style.MarginAuto[index])
        {
            return AnimatedLength.AutoLength.Instance;
        }

        if (style.MarginExpressions[index] is { } expression)
        {
            return new AnimatedLength.ExpressionLength(expression);
        }

        if (style.MarginPercent[index] is { } percentage)
        {
            return new AnimatedLength.DimensionLength(Dimension.Percent(percentage));
        }

        if (style.MarginRelative[index] is { } relative)
        {
            return new AnimatedLength.DimensionLength(relative);
        }

        return new AnimatedLength.DimensionLength(Dimension.Px(EdgeValue(style.Margin, index)));
    }

    private static AnimatedLength PaddingLength(LayoutStyle style, int index)
    {
        if (style.PaddingExpressions[index] is { } expression)
        {
            return new AnimatedLength.ExpressionLength(expression);
        }

        if (style.PaddingPercent[index] is { } percentage)
        {
            return new AnimatedLength.DimensionLength(Dimension.Percent(percentage));
        }

        if (style.PaddingRelative[index] is { } relative)
        {
            return new AnimatedLength.DimensionLength(relative);
        }

        return new AnimatedLength.DimensionLength(Dimension.Px(EdgeValue(style.Padding, index)));
    }

    private static void SetSizeAnimationValue(LayoutStyle style, AnimatedProperty property, AnimatedLength value)
    {
        var index = SizeIndex(property);
        var (dimension, expression) = value switch
        {
            AnimatedLength.AutoLength => (Dimension.Auto, (string?)null),
            AnimatedLength.DimensionLength length => (length.Value, null),
            AnimatedLength.ExpressionLength text => (Dimension.Auto, text.Value),
            _ => (Dimension.Auto, null),
        };
        style.SizeExpressions[index] = expression;
        switch (property)
        {
            case AnimatedProperty.Width:
                style.Width = dimension;
                style.WidthSet = true;
                style.WidthFitContent = false;
                break;
            case AnimatedProperty.Height:
                style.Height = dimension;
                style.HeightSet = true;
                break;
            case AnimatedProperty.MinWidth:
                style.MinWidth = dimension;
                break;
            case AnimatedProperty.MinHeight:
                style.MinHeight = dimension;
                break;
            case AnimatedProperty.MaxWidth:
                style.MaxWidth = dimension;
                break;
            default:
                style.MaxHeight = dimension;
                break;
        }
    }

    private static void SetInsetAnimationValue(LayoutStyle style, int index, AnimatedLength value)
    {
        switch (value)
        {
            case AnimatedLength.AutoLength:
                style.Inset[index] = null;
                style.InsetExpressions[index] = null;
                break;
            case AnimatedLength.DimensionLength dimension:
                style.Inset[index] = dimension.Value;
                style.InsetExpressions[index] = null;
                break;
            case AnimatedLength.ExpressionLength expression:
                style.Inset[index] = null;
                style.InsetExpressions[index] = expression.Value;
                break;
        }
    }

    private static void SetMarginAnimationValue(LayoutStyle style, int index, AnimatedLength value)
    {
        style.MarginAuto[index] = false;
        style.MarginPercent[index] = null;
        style.MarginRelative[index] = null;
        style.MarginExpressions[index] = null;
        style.Margin = SetEdge(style.Margin, index, 0f);
        switch (value)
        {
            case AnimatedLength.AutoLength:
                style.MarginAuto[index] = true;
                break;
            case AnimatedLength.DimensionLength { Value.Kind: DimensionKind.Px } pixels:
                style.Margin = SetEdge(style.Margin, index, pixels.Value.Value);
                break;
            case AnimatedLength.DimensionLength { Value.Kind: DimensionKind.Percent } percent:
                style.MarginPercent[index] = percent.Value.Value;
                break;
            case AnimatedLength.DimensionLength { Value.Kind: DimensionKind.Auto }:
                style.MarginAuto[index] = true;
                break;
            case AnimatedLength.DimensionLength relative:
                style.MarginRelative[index] = relative.Value;
                break;
            case AnimatedLength.ExpressionLength expression:
                style.MarginExpressions[index] = expression.Value;
                break;
        }
    }

    private static void SetPaddingAnimationValue(LayoutStyle style, int index, AnimatedLength value)
    {
        style.PaddingPercent[index] = null;
        style.PaddingRelative[index] = null;
        style.PaddingExpressions[index] = null;
        style.Padding = SetEdge(style.Padding, index, 0f);
        switch (value)
        {
            case AnimatedLength.AutoLength:
            case AnimatedLength.DimensionLength { Value.Kind: DimensionKind.Auto }:
                break;
            case AnimatedLength.DimensionLength { Value.Kind: DimensionKind.Px } pixels:
                style.Padding = SetEdge(style.Padding, index, pixels.Value.Value);
                break;
            case AnimatedLength.DimensionLength { Value.Kind: DimensionKind.Percent } percent:
                style.PaddingPercent[index] = percent.Value.Value;
                break;
            case AnimatedLength.DimensionLength relative:
                style.PaddingRelative[index] = relative.Value;
                break;
            case AnimatedLength.ExpressionLength expression:
                style.PaddingExpressions[index] = expression.Value;
                break;
        }
    }

    private static void SetGapAnimationValue(LayoutStyle style, bool row, AnimatedLength value)
    {
        float? slot;
        string? expression;
        switch (value)
        {
            case AnimatedLength.AutoLength:
            case AnimatedLength.DimensionLength { Value.Kind: DimensionKind.Auto }:
                slot = null;
                expression = null;
                break;
            case AnimatedLength.DimensionLength { Value.Kind: DimensionKind.Px } pixels:
                slot = pixels.Value.Value;
                expression = null;
                break;
            case AnimatedLength.DimensionLength dimension:
                slot = null;
                expression = DimensionToCss(dimension.Value);
                break;
            case AnimatedLength.ExpressionLength text:
                slot = null;
                expression = text.Value;
                break;
            default:
                return;
        }

        if (row)
        {
            style.RowGap = slot;
            style.RowGapExpression = expression;
        }
        else
        {
            style.ColumnGap = slot;
            style.ColumnGapExpression = expression;
        }
    }

    /// <summary>Rust <c>css::dimension_to_css</c>.</summary>
    internal static string DimensionToCss(Dimension value) => value.Kind switch
    {
        DimensionKind.Auto => "auto",
        DimensionKind.Px => Format(value.Value) + "px",
        DimensionKind.Percent => Format(value.Value * 100f) + "%",
        DimensionKind.Em => Format(value.Value) + "em",
        DimensionKind.Ex => Format(value.Value) + "ex",
        DimensionKind.Rem => Format(value.Value) + "rem",
        DimensionKind.Vw => Format(value.Value) + "vw",
        DimensionKind.Vh => Format(value.Value) + "vh",
        DimensionKind.Vmin => Format(value.Value) + "vmin",
        _ => Format(value.Value) + "vmax",
    };

    private static string Format(float value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Rust <c>css::interpolate_animated_length</c>.</summary>
    private static AnimatedLength InterpolateAnimatedLength(AnimatedLength from, AnimatedLength to, float position)
    {
        switch (from, to)
        {
            case (AnimatedLength.DimensionLength left, AnimatedLength.DimensionLength right):
            {
                var interpolated = InterpolateDimension(left.Value, right.Value, position);
                return interpolated is { } value
                    ? new AnimatedLength.DimensionLength(value)
                    : new AnimatedLength.DimensionLength(position < 0.5f ? left.Value : right.Value);
            }

            case (AnimatedLength.AutoLength, AnimatedLength.AutoLength):
                return AnimatedLength.AutoLength.Instance;
            case (AnimatedLength.ExpressionLength left, AnimatedLength.ExpressionLength right)
                when string.Equals(left.Value, right.Value, StringComparison.Ordinal):
                return new AnimatedLength.ExpressionLength(left.Value);
            default:
                return position < 0.5f ? from : to;
        }
    }

    /// <summary>Rust <c>css::interpolate_dimension</c>.</summary>
    internal static Dimension? InterpolateDimension(Dimension from, Dimension to, float position)
    {
        if (from.Kind != to.Kind)
        {
            return null;
        }

        return from.Kind == DimensionKind.Auto
            ? Dimension.Auto
            : new Dimension(from.Kind, from.Value + ((to.Value - from.Value) * position));
    }

    /// <summary>Rust <c>css::interpolate_color</c>.</summary>
    internal static RgbaColor InterpolateColor(RgbaColor from, RgbaColor to, float position)
    {
        static byte Channel(byte from, byte to, float position) =>
            (byte)Math.Clamp(F32.Round(from + ((to - (float)from) * position)), 0f, 255f);

        return new RgbaColor(
            Channel(from.R, to.R, position),
            Channel(from.G, to.G, position),
            Channel(from.B, to.B, position),
            Channel(from.A, to.A, position));
    }

    /// <summary>Rust <c>css::interpolate_transform_length</c>.</summary>
    internal static TransformLength? InterpolateTransformLength(
        TransformLength from,
        TransformLength to,
        float position)
    {
        if (from.Expression is null && to.Expression is null)
        {
            return InterpolateDimension(from.Value, to.Value, position) is { } value
                ? new TransformLength(value, null)
                : null;
        }

        if (from.Expression is { } left && to.Expression is { } right
            && string.Equals(left, right, StringComparison.Ordinal))
        {
            return new TransformLength(
                InterpolateDimension(from.Value, to.Value, position) ?? from.Value,
                left);
        }

        return null;
    }

    /// <summary>Rust <c>css::interpolate_transform_list</c>.</summary>
    internal static List<TransformOp> InterpolateTransformList(
        IReadOnlyList<TransformOp> from,
        IReadOnlyList<TransformOp> to,
        float position)
    {
        List<TransformOp> fromList;
        if (from.Count == 0 && to.Count != 0)
        {
            fromList = new List<TransformOp>(to.Count);
            foreach (var operation in to)
            {
                fromList.Add(IdentityTransformOperation(operation));
            }
        }
        else
        {
            fromList = [.. from];
        }

        List<TransformOp> toList;
        if (to.Count == 0 && from.Count != 0)
        {
            toList = new List<TransformOp>(from.Count);
            foreach (var operation in from)
            {
                toList.Add(IdentityTransformOperation(operation));
            }
        }
        else
        {
            toList = [.. to];
        }

        if (fromList.Count != toList.Count)
        {
            return position < 0.5f ? fromList : toList;
        }

        var result = new List<TransformOp>(fromList.Count);
        for (var index = 0; index < fromList.Count; index++)
        {
            var operation = InterpolateTransformOperation(fromList[index], toList[index], position);
            if (operation is null)
            {
                return position < 0.5f ? fromList : toList;
            }

            result.Add(operation);
        }

        return result;
    }

    private static TransformOp IdentityTransformOperation(TransformOp operation) => operation switch
    {
        TransformOp.Translate translate => new TransformOp.Translate(
            ZeroTransformLength(translate.X),
            ZeroTransformLength(translate.Y)),
        TransformOp.Scale => new TransformOp.Scale(1f, 1f),
        TransformOp.Rotate => new TransformOp.Rotate(0f),
        TransformOp.Skew => new TransformOp.Skew(0f, 0f),
        _ => new TransformOp.Matrix(Affine2.Identity),
    };

    private static TransformLength ZeroTransformLength(TransformLength value) => new(
        value.Value.Kind switch
        {
            DimensionKind.Percent => Dimension.Percent(0f),
            DimensionKind.Em => Dimension.Em(0f),
            DimensionKind.Ex => Dimension.Ex(0f),
            DimensionKind.Rem => Dimension.Rem(0f),
            DimensionKind.Vw => Dimension.Vw(0f),
            DimensionKind.Vh => Dimension.Vh(0f),
            DimensionKind.Vmin => Dimension.Vmin(0f),
            DimensionKind.Vmax => Dimension.Vmax(0f),
            _ => Dimension.Px(0f),
        },
        value.Expression is null ? null : "0px");

    /// <summary>Rust <c>css::interpolate_transform_operation</c>.</summary>
    private static TransformOp? InterpolateTransformOperation(TransformOp from, TransformOp to, float position)
    {
        static float Lerp(float from, float to, float position) => from + ((to - from) * position);

        switch (from, to)
        {
            case (TransformOp.Translate left, TransformOp.Translate right):
            {
                var x = InterpolateTransformLength(left.X, right.X, position);
                var y = InterpolateTransformLength(left.Y, right.Y, position);
                return x is { } resolvedX && y is { } resolvedY
                    ? new TransformOp.Translate(resolvedX, resolvedY)
                    : null;
            }

            case (TransformOp.Scale left, TransformOp.Scale right):
                return new TransformOp.Scale(Lerp(left.X, right.X, position), Lerp(left.Y, right.Y, position));
            case (TransformOp.Rotate left, TransformOp.Rotate right):
                return new TransformOp.Rotate(Lerp(left.Degrees, right.Degrees, position));
            case (TransformOp.Skew left, TransformOp.Skew right):
                return new TransformOp.Skew(
                    Lerp(left.XDegrees, right.XDegrees, position),
                    Lerp(left.YDegrees, right.YDegrees, position));
            case (TransformOp.Matrix left, TransformOp.Matrix right):
                return new TransformOp.Matrix(new Affine2(
                    Lerp(left.Value.A, right.Value.A, position),
                    Lerp(left.Value.B, right.Value.B, position),
                    Lerp(left.Value.C, right.Value.C, position),
                    Lerp(left.Value.D, right.Value.D, position),
                    Lerp(left.Value.E, right.Value.E, position),
                    Lerp(left.Value.F, right.Value.F, position)));
            default:
                return null;
        }
    }

    /// <summary>Rust <c>css::set_animation_containing_block_trigger</c>.</summary>
    internal static void SetContainingBlockTrigger(LayoutStyle style, ushort trigger, bool enabled)
    {
        if (enabled)
        {
            style.ContainingBlockTriggers |= trigger;
        }
        else
        {
            style.ContainingBlockTriggers = (ushort)(style.ContainingBlockTriggers & ~trigger);
        }
    }

    /// <summary>Rust <c>css::edge_value</c>.</summary>
    private static float EdgeValue(Edges edges, int index) => index switch
    {
        0 => edges.Top,
        1 => edges.Right,
        2 => edges.Bottom,
        3 => edges.Left,
        _ => 0f,
    };

    /// <summary>Rust <c>css::edge_value_mut</c>, written back because Edges is a value type.</summary>
    private static Edges SetEdge(Edges edges, int index, float value) => index switch
    {
        0 => edges with { Top = value },
        1 => edges with { Right = value },
        2 => edges with { Bottom = value },
        3 => edges with { Left = value },
        _ => edges,
    };

    /// <summary>
    /// The uniform-<c>border-color</c> mirror css.rs refreshes after every
    /// applied animation value.
    /// </summary>
    private static void SyncUniformBorderColor(LayoutStyle style)
    {
        var colors = style.BorderModel.Colors;
        style.BorderColor = colors.Top == colors.Right
            && colors.Right == colors.Bottom
            && colors.Bottom == colors.Left
            ? colors.Top
            : null;
    }

    /// <summary>Rust <c>css::animation_directed_progress</c>.</summary>
    internal static float? DirectedProgress(AnimationTiming timing, AnimationSampleTime sampleTime)
    {
        var duration = F32.Max(timing.DurationMs, 0f);
        var iterations = F32.Max(timing.IterationCount, 0f);
        var activeDuration = duration == 0f || iterations == 0f ? 0f : duration * iterations;

        // The timeline owns pause/resume hold time, so a running animation can
        // freeze at its current progress and later resume.
        var localTime = sampleTime.Milliseconds;
        if (!float.IsFinite(localTime))
        {
            return null;
        }

        var endTime = F32.Max(timing.DelayMs + activeDuration, 0f);
        var beforeBoundary = Math.Clamp(timing.DelayMs, 0f, endTime);
        var afterBoundary = F32.Max(F32.Min(timing.DelayMs + activeDuration, endTime), 0f);
        var phase = localTime < beforeBoundary
            ? AnimationPhase.Before
            : localTime >= afterBoundary
                ? AnimationPhase.After
                : AnimationPhase.Active;

        float activeTime;
        switch (phase)
        {
            case AnimationPhase.Before:
                if (timing.FillMode is not (AnimationFillMode.Backwards or AnimationFillMode.Both))
                {
                    return null;
                }

                activeTime = F32.Max(localTime - timing.DelayMs, 0f);
                break;
            case AnimationPhase.Active:
                activeTime = localTime - timing.DelayMs;
                break;
            default:
                if (timing.FillMode is not (AnimationFillMode.Forwards or AnimationFillMode.Both))
                {
                    return null;
                }

                activeTime = Math.Clamp(localTime - timing.DelayMs, 0f, activeDuration);
                break;
        }

        var overallProgress = duration == 0f
            ? phase == AnimationPhase.Before ? 0f : iterations
            : activeTime / duration;
        if (!float.IsFinite(overallProgress))
        {
            return timing.Direction is AnimationDirection.Reverse or AnimationDirection.AlternateReverse
                ? 1f
                : 0f;
        }

        var currentIteration = F32.Max(MathF.Floor(overallProgress), 0f);
        var simpleProgress = RemEuclid(overallProgress, 1f);
        if (phase == AnimationPhase.After && iterations > 0f && simpleProgress == 0f)
        {
            simpleProgress = 1f;
            currentIteration = F32.Max(currentIteration - 1f, 0f);
        }

        var reverse = timing.Direction switch
        {
            AnimationDirection.Normal => false,
            AnimationDirection.Reverse => true,
            AnimationDirection.Alternate => RemEuclid(currentIteration, 2f) >= 1f,
            _ => RemEuclid(currentIteration, 2f) < 1f,
        };
        return reverse ? 1f - simpleProgress : simpleProgress;
    }

    /// <summary>Rust <c>f32::rem_euclid</c>.</summary>
    private static float RemEuclid(float value, float modulus)
    {
        var remainder = value % modulus;
        return remainder < 0f ? remainder + MathF.Abs(modulus) : remainder;
    }

    // ------------------------------------------------------------ Web Animations

    /// <summary>Rust <c>css::sample_waapi_properties</c>.</summary>
    internal static void SampleWaapiProperties(
        AnimationTimelineState timeline,
        NodeId node,
        LayoutStyle style,
        AnimationSample sample)
    {
        foreach (var (animation, localTime) in timeline.WaapiForNode(node, sample.Time))
        {
            if (DirectedProgress(animation.Timing, localTime) is not { } progress)
            {
                continue;
            }

            if (animation.LinearEasing is { } samples)
            {
                progress = CssKeyframes.SampleLinearEasing(samples, progress);
            }
            else if (animation.Easing is { Length: 4 } points)
            {
                progress = CssKeyframes.SampleCubicBezier(points[0], points[1], points[2], points[3], progress);
            }

            var underlying = style.Clone();

            var opacityTrack = new List<(float Offset, float Value)>();
            foreach (var frame in animation.Keyframes)
            {
                if (frame.Opacity is { } opacity)
                {
                    opacityTrack.Add((frame.Offset, opacity));
                }
            }

            if (CssKeyframes.TrySampleWaapiTrack(
                    opacityTrack,
                    underlying.Opacity ?? 1f,
                    progress,
                    static (from, to, position) => from + ((to - from) * position),
                    out var sampledOpacity))
            {
                style.Opacity = Math.Clamp(sampledOpacity, 0f, 1f);
            }

            var transformTrack = new List<(float Offset, List<TransformOp> Value)>();
            foreach (var frame in animation.Keyframes)
            {
                if (frame.Transform is not { } text)
                {
                    continue;
                }

                if (!CssHost.SupportsDeclaration("transform", text))
                {
                    continue;
                }

                var endpoint = underlying.Clone();
                ComputedStyle.ApplyAnimationPropertyValue(endpoint, "transform", text);
                transformTrack.Add((frame.Offset, endpoint.TransformOps));
            }

            if (CssKeyframes.TrySampleWaapiTrack(
                    transformTrack,
                    [.. underlying.TransformOps],
                    progress,
                    static (from, to, position) => InterpolateTransformList(from, to, position),
                    out var sampledTransform))
            {
                ApplyAnimationValue(
                    style,
                    AnimatedProperty.Transform,
                    new AnimationValue.Transform(sampledTransform));
            }
        }
    }

    /// <summary>Rust <c>css::resample_visual_waapi</c>.</summary>
    public static ResampledVisualWaapi? ResampleVisualWaapi(
        AnimationTimelineState timeline,
        NodeId node,
        LayoutStyle style,
        AnimationSample sample)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(style);
        if (style.WaapiSampleState is not { } retained)
        {
            return null;
        }

        var animations = new List<WaapiAnimation>();
        foreach (var (animation, _) in timeline.WaapiForNode(node, sample.Time))
        {
            animations.Add(animation);
        }

        if (animations.Count == 0)
        {
            return null;
        }

        var hasTransformEffect = false;
        var hasOpacityEffect = false;
        var allEffectsSupported = true;
        foreach (var animation in animations)
        {
            var supported = false;
            foreach (var frame in animation.Keyframes)
            {
                hasTransformEffect |= frame.Transform is not null;
                hasOpacityEffect |= frame.Opacity is not null;
                supported |= frame.Transform is not null || frame.Opacity is not null;
            }

            allEffectsSupported &= supported;
        }

        if (!allEffectsSupported
            || (hasTransformEffect && !retained.TransformFastPath)
            || (hasOpacityEffect && !retained.OpacityFastPath))
        {
            return null;
        }

        var sampled = style.Clone();
        sampled.TransformOps = [.. retained.UnderlyingTransformOps];
        sampled.Opacity = retained.UnderlyingOpacity;
        SetContainingBlockTrigger(
            sampled,
            ContainingBlockTrigger.Transform,
            sampled.TransformOps.Count != 0);
        SampleWaapiProperties(timeline, node, sampled, sample);
        return new ResampledVisualWaapi
        {
            TransformOps = sampled.TransformOps,
            Opacity = sampled.Opacity,
            HasTransformEffect = hasTransformEffect,
            HasOpacityEffect = hasOpacityEffect,
            EstablishesTransformContainingBlock =
                (sampled.ContainingBlockTriggers & ContainingBlockTrigger.Transform) != 0,
        };
    }
}
