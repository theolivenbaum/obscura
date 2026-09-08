using Obscura.Render.Layout;

namespace Obscura.Render;

/// <summary>CSS replaced-element sizing for one registered box.</summary>
internal readonly struct ReplacedItem
{
    public float? IntrinsicWidth { get; init; }

    public float? IntrinsicHeight { get; init; }

    public float? PreferredWidth { get; init; }

    public float? PreferredHeight { get; init; }

    public float PreferredRatio { get; init; }

    public float? MinWidth { get; init; }

    public float? MinHeight { get; init; }

    public float? MaxWidth { get; init; }

    public float? MaxHeight { get; init; }

    /// <summary>
    /// Both intrinsic axes are absent but a real preferred ratio exists. CSS replaced sizing
    /// stretch-fits this case to a definite available inline size instead of using the 300x150
    /// default-object contribution.
    /// </summary>
    public bool RatioOnly { get; init; }

    /// <summary>
    /// Definite normal-flow containing width captured before the intrinsic flex-item
    /// contribution pass. Only populated for ratio-only auto/auto replaced boxes.
    /// </summary>
    public float? RatioOnlyAvailableWidth { get; init; }

    /// <summary>
    /// CSS Sizing's cyclic-percentage rule makes a proper replaced element's inline min-content
    /// contribution zero when its preferred or maximum inline size contains a percentage. The
    /// natural size still participates in max-content sizing and in the final definite layout.
    /// </summary>
    public bool ZeroInlineMinContent { get; init; }

    public static ReplacedItem FromStyle(float width, float height, LayoutStyle style) =>
        FromIntrinsic(ReplacedIntrinsic.FromDimensions(width, height), style);

    public static ReplacedItem FromIntrinsic(ReplacedIntrinsic intrinsic, LayoutStyle style)
    {
        static float? Px(Dimension dimension) =>
            dimension.Kind == DimensionKind.Px ? F32.Max(dimension.Value, 0f) : null;

        bool ExpressionHasPercentage(int index) =>
            style.SizeExpressions[index]?.Contains('%', StringComparison.Ordinal) ?? false;

        float? explicitRatio = style.AspectRatio is { } authored && float.IsFinite(authored) && authored > 0f
            ? authored
            : (intrinsic.Ratio is { } natural && float.IsFinite(natural) && natural > 0f ? natural : null);

        float intrinsicRatio;
        if (intrinsic.Ratio is { } r && float.IsFinite(r) && r > 0f)
        {
            intrinsicRatio = r;
        }
        else if (intrinsic.NaturalSize() is { } size
            && float.IsFinite(size.Width) && float.IsFinite(size.Height)
            && size.Width > 0f && size.Height > 0f)
        {
            intrinsicRatio = size.Width / size.Height;
        }
        else
        {
            intrinsicRatio = 2f;
        }

        return new ReplacedItem
        {
            IntrinsicWidth = intrinsic.Width is { } w && float.IsFinite(w) && w > 0f ? w : null,
            IntrinsicHeight = intrinsic.Height is { } h && float.IsFinite(h) && h > 0f ? h : null,
            PreferredWidth = Px(style.Width),
            PreferredHeight = Px(style.Height),
            PreferredRatio = explicitRatio ?? intrinsicRatio,
            MinWidth = Px(style.MinWidth),
            MinHeight = Px(style.MinHeight),
            MaxWidth = Px(style.MaxWidth),
            MaxHeight = Px(style.MaxHeight),
            RatioOnly = intrinsic.Width is null && intrinsic.Height is null && explicitRatio is not null,
            RatioOnlyAvailableWidth = style.RatioOnlyAvailableWidth,
            ZeroInlineMinContent = style.Width.Kind == DimensionKind.Percent
                || style.MaxWidth.Kind == DimensionKind.Percent
                || ExpressionHasPercentage(0)
                || ExpressionHasPercentage(4),
        };
    }

    /// <summary>CSS sizing gives the minimum precedence when min &gt; max.</summary>
    private static float Clamp(float value, float? min, float? max)
    {
        float clamped = max is { } upper ? F32.Min(value, upper) : value;
        return min is { } lower ? F32.Max(clamped, lower) : clamped;
    }

    /// <summary>
    /// Apply the CSS 2.1 10.4/10.7 constraint table for a replaced element whose preferred
    /// width and height are both auto. Unlike independently clamping the two axes, this
    /// transfers a one-axis min/max constraint through the preferred aspect ratio whenever the
    /// constraints allow it.
    /// </summary>
    public Size<float> ConstrainAutoSize(Size<float> tentative)
    {
        float minWidth = MinWidth ?? 0f;
        float minHeight = MinHeight ?? 0f;
        float maxWidth = F32.Max(MaxWidth ?? float.PositiveInfinity, minWidth);
        float maxHeight = F32.Max(MaxHeight ?? float.PositiveInfinity, minHeight);
        float width = tentative.Width;
        float height = tentative.Height;

        float heightAtMaxWidth = F32.Max(maxWidth / PreferredRatio, minHeight);
        float heightAtMinWidth = F32.Min(minWidth / PreferredRatio, maxHeight);
        float widthAtMaxHeight = F32.Max(maxHeight * PreferredRatio, minWidth);
        float widthAtMinHeight = F32.Min(minHeight * PreferredRatio, maxWidth);

        if (width > maxWidth)
        {
            if (height > maxHeight)
            {
                if (maxWidth * height <= maxHeight * width)
                {
                    (width, height) = (maxWidth, heightAtMaxWidth);
                }
                else
                {
                    (width, height) = (widthAtMaxHeight, maxHeight);
                }
            }
            else
            {
                (width, height) = (maxWidth, heightAtMaxWidth);
            }
        }
        else if (width < minWidth)
        {
            if (height < minHeight)
            {
                if (minWidth * height <= minHeight * width)
                {
                    (width, height) = (widthAtMinHeight, minHeight);
                }
                else
                {
                    (width, height) = (minWidth, heightAtMinWidth);
                }
            }
            else
            {
                (width, height) = (minWidth, heightAtMinWidth);
            }
        }
        else if (height > maxHeight)
        {
            (width, height) = (widthAtMaxHeight, maxHeight);
        }
        else if (height < minHeight)
        {
            (width, height) = (widthAtMinHeight, minHeight);
        }

        return new Size<float>(width, height);
    }

    public Size<float> Size(Size<float?> known)
    {
        float width;
        float height;
        if (known.Width is { } knownWidth && known.Height is { } knownHeight)
        {
            (width, height) = (knownWidth, knownHeight);
        }
        else if (known.Width is { } onlyWidth)
        {
            (width, height) = (onlyWidth, onlyWidth / PreferredRatio);
        }
        else if (known.Height is { } onlyHeight)
        {
            (width, height) = (onlyHeight * PreferredRatio, onlyHeight);
        }
        else if (PreferredWidth is { } preferredWidth && PreferredHeight is { } preferredHeight)
        {
            (width, height) = (preferredWidth, preferredHeight);
        }
        else if (PreferredWidth is { } preferredOnlyWidth)
        {
            (width, height) = (preferredOnlyWidth, preferredOnlyWidth / PreferredRatio);
        }
        else if (PreferredHeight is { } preferredOnlyHeight)
        {
            (width, height) = (preferredOnlyHeight * PreferredRatio, preferredOnlyHeight);
        }
        else if (IntrinsicWidth is { } intrinsicWidth && IntrinsicHeight is { } intrinsicHeight)
        {
            (width, height) = (intrinsicWidth, intrinsicHeight);
        }
        else if (IntrinsicWidth is { } intrinsicOnlyWidth)
        {
            (width, height) = (intrinsicOnlyWidth, intrinsicOnlyWidth / PreferredRatio);
        }
        else if (IntrinsicHeight is { } intrinsicOnlyHeight)
        {
            (width, height) = (intrinsicOnlyHeight * PreferredRatio, intrinsicOnlyHeight);
        }
        else
        {
            // Contain the intrinsic ratio inside CSS Images' 300x150 default object size. A
            // definite authored/known axis is handled above and transfers through the same ratio.
            width = PreferredRatio >= 2f ? 300f : 150f * PreferredRatio;
            height = width / PreferredRatio;
        }

        var tentative = new Size<float>(width, height);
        if (PreferredWidth is null && PreferredHeight is null
            && (known.Width is null || known.Height is null))
        {
            return ConstrainAutoSize(tentative);
        }

        return new Size<float>(
            Clamp(width, MinWidth, MaxWidth),
            Clamp(height, MinHeight, MaxHeight));
    }
}
