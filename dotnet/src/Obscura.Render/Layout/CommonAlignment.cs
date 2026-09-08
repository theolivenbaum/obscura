// Port of vendor/taffy/src/compute/common/alignment.rs and
// vendor/taffy/src/compute/common/content_size.rs
namespace Obscura.Render.Layout;

/// <summary>Generic CSS alignment code shared between the Flexbox and CSS Grid algorithms.</summary>
public static class CommonAlignment
{
    /// <summary>
    /// Resolve the safe/unsafe overflow-position fallback for a self-level alignment value.
    /// </summary>
    public static AlignItemsKeyword ResolveSelfAlignmentSafety(AlignItems alignment, bool overflows) =>
        alignment.Safety == AlignmentSafety.Safe && overflows ? AlignItemsKeyword.Start : alignment.Keyword;

    /// <summary>
    /// Resolve any spec-defined fallbacks for the given <see cref="AlignContent"/> value, returning
    /// the bare position keyword the alignment math should use.
    /// </summary>
    public static AlignContentKeyword ApplyAlignmentFallback(
        float freeSpace,
        int numItems,
        AlignContent alignmentMode)
    {
        var keyword = alignmentMode.Keyword;
        bool isSafe = alignmentMode.Safety == AlignmentSafety.Safe;

        // 1. With a single item or overflowing items, distributed alignment keywords fall back to a
        //    positional keyword and gain implicit `safe` semantics.
        if (numItems <= 1 || freeSpace <= 0.0f)
        {
            switch (keyword)
            {
                case AlignContentKeyword.Stretch:
                case AlignContentKeyword.SpaceBetween:
                    keyword = AlignContentKeyword.FlexStart;
                    isSafe = true;
                    break;
                case AlignContentKeyword.SpaceAround:
                case AlignContentKeyword.SpaceEvenly:
                    keyword = AlignContentKeyword.Center;
                    isSafe = true;
                    break;
                default:
                    break;
            }
        }

        // 2. Safe alignment falls back to `Start` whenever the subject would overflow.
        if (freeSpace <= 0.0f && isSafe)
        {
            keyword = AlignContentKeyword.Start;
        }

        return keyword;
    }

    /// <summary>
    /// Generic alignment function used for both align-content and justify-content, in both the
    /// Flexbox and CSS Grid algorithms.
    /// </summary>
    public static float ComputeAlignmentOffset(
        float freeSpace,
        int numItems,
        float gap,
        AlignContentKeyword alignmentMode,
        bool layoutIsFlexReversed,
        bool isFirst)
    {
        if (isFirst)
        {
            return alignmentMode switch
            {
                AlignContentKeyword.Start => 0.0f,
                AlignContentKeyword.FlexStart => layoutIsFlexReversed ? freeSpace : 0.0f,
                AlignContentKeyword.End => freeSpace,
                AlignContentKeyword.FlexEnd => layoutIsFlexReversed ? 0.0f : freeSpace,
                AlignContentKeyword.Center => freeSpace / 2.0f,
                AlignContentKeyword.Stretch => 0.0f,
                AlignContentKeyword.SpaceBetween => 0.0f,
                AlignContentKeyword.SpaceAround =>
                    freeSpace >= 0.0f ? freeSpace / numItems / 2.0f : freeSpace / 2.0f,
                AlignContentKeyword.SpaceEvenly =>
                    freeSpace >= 0.0f ? freeSpace / (numItems + 1) : freeSpace / 2.0f,
                _ => 0.0f,
            };
        }

        float clampedFreeSpace = Sys.F32Max(freeSpace, 0.0f);
        return gap + alignmentMode switch
        {
            AlignContentKeyword.SpaceBetween => clampedFreeSpace / (numItems - 1),
            AlignContentKeyword.SpaceAround => clampedFreeSpace / numItems,
            AlignContentKeyword.SpaceEvenly => clampedFreeSpace / (numItems + 1),
            _ => 0.0f,
        };
    }
}

/// <summary>Generic CSS content size code shared between all CSS algorithms.</summary>
public static class ContentSizeHelper
{
    /// <summary>
    /// Determine how much width/height a given node contributes to its parent's content size.
    /// </summary>
    public static Size<float> ComputeContentSizeContribution(
        Point<float> location,
        Size<float> size,
        Size<float> contentSize,
        Point<Overflow> overflow)
    {
        var sizeContentSizeContribution = new Size<float>(
            overflow.X == Overflow.Visible ? Sys.F32Max(size.Width, contentSize.Width) : size.Width,
            overflow.Y == Overflow.Visible ? Sys.F32Max(size.Height, contentSize.Height) : size.Height);

        if (sizeContentSizeContribution.Width > 0.0f && sizeContentSizeContribution.Height > 0.0f)
        {
            float maxX = Sys.F32Max(location.X + sizeContentSizeContribution.Width, 0.0f);
            float minX = Sys.F32Min(location.X, 0.0f);
            float maxY = Sys.F32Max(location.Y + sizeContentSizeContribution.Height, 0.0f);
            float minY = Sys.F32Min(location.Y, 0.0f);
            return new Size<float>(maxX - minX, maxY - minY);
        }

        return GeometryExtensions.SizeZero;
    }
}
