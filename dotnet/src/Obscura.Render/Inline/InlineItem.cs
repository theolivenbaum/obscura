using Obscura.Dom;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>A <c>-webkit-background-clip: text</c> fill: a gradient angle and its stops.</summary>
public readonly record struct ClipTextFill(float Angle, List<(RgbaColor Color, float? Position)> Stops);

/// <summary>A shaped byte range owned by one ordinary inline DOM element.</summary>
internal readonly record struct OwnerTextRange(NodeId Owner, int Start, int End);

/// <summary>One inline-axis edge (margin, border, padding) of an inline box.</summary>
internal readonly record struct InlineEdge(float Margin, float Border, float Padding)
{
    public float Advance => Margin + Border + Padding;

    public float BorderPadding => Border + Padding;
}

internal readonly record struct InlineOwnerBox(
    NodeId Owner,
    int Start,
    int End,
    InlineEdge StartEdge,
    InlineEdge EndEdge,
    int StartEvent,
    int EndEvent);

internal readonly record struct InlineBoundaryEvent(
    NodeId Owner,
    int Position,
    bool IsStart,
    InlineEdge Edge);

internal readonly record struct ActiveInlineOwner(
    NodeId Owner,
    int Start,
    InlineEdge StartEdge,
    InlineEdge EndEdge,
    int StartEvent);

internal readonly record struct RelativeOwnerTextRange(int Start, int End, (float X, float Y) Offset);

/// <summary>
/// One ordinary inline owner's horizontal continuation on a finalized visual line.
/// </summary>
/// <remarks>
/// DOM layout supplies the owner's font box and decorations around this baseline-relative
/// shaped extent.
/// </remarks>
public readonly record struct InlineOwnerLineFragment(
    NodeId Owner,
    int ItemIndex,
    int LineIndex,
    float X,
    float BaselineY,
    float Width);

internal readonly record struct MarkerPlacement(int LineIndex, float X, float Y, float ContentEnd);

/// <summary>
/// One inline formatting context: a shaped buffer plus where to paint it.
/// </summary>
public sealed class InlineItem
{
    /// <summary>The shaped content, reshaped at each measured width.</summary>
    public required TextBuffer Buffer { get; set; }

    /// <summary>Wrapping used for definite-width layout and final paint.</summary>
    internal Wrap LayoutWrap { get; init; }

    /// <summary>
    /// Wrapping used only for an intrinsic min-content query. This differs from
    /// <see cref="LayoutWrap"/> for <c>overflow-wrap: break-word</c>: emergency breaks are
    /// available during reflow but do not reduce min-content.
    /// </summary>
    internal Wrap MinContentWrap { get; init; }

    /// <summary>
    /// Unmodified shaped content retained only when <c>text-indent</c> is nonzero or ordinary
    /// inline owners are present. Each measurement can then derive a first-line-only wrap
    /// boundary without accumulating synthetic hard breaks across repeated probes.
    /// </summary>
    internal TextBuffer? SourceBuffer { get; set; }

    internal Dimension TextIndent { get; init; }

    /// <summary>
    /// Used LTR paint offset for the first formatted line at the most recently shaped width.
    /// Later lines retain the ordinary content-box origin.
    /// </summary>
    public float FirstLineOffset { get; internal set; }

    /// <summary>
    /// Whether final shaping should tighten the wrap width while preserving the natural line
    /// count (<c>text-wrap-style: balance</c>).
    /// </summary>
    internal bool BalanceWrap { get; init; }

    /// <summary>
    /// Alignment is applied against the original content width. Wrapping and alignment share
    /// the buffer width, so a balanced (narrower) buffer needs a corresponding origin inset.
    /// </summary>
    internal Align? Align { get; init; }

    /// <summary>
    /// Minimum block-size contributed by explicit <c>&lt;br&gt;</c> breaks. The shaped buffer
    /// omits the final empty run for a trailing newline, while CSS still gives a break-only or
    /// consecutive-break line the parent's used line-height.
    /// </summary>
    internal float ForcedMinHeight { get; init; }

    /// <summary>Content-box top-left in viewport coordinates, set by finalize.</summary>
    public (float X, float Y) Origin { get; internal set; }

    /// <summary>Ancestor <c>overflow: hidden</c> clip, set by finalize.</summary>
    public Rect? Clip { get; internal set; }

    /// <summary>
    /// Per-span <c>-webkit-background-clip: text</c> fills. Glyph metadata selects one entry,
    /// allowing an inline accent span to own a gradient without recoloring the rest of its
    /// heading.
    /// </summary>
    internal List<ClipTextFill> ClipFills { get; init; } = [];

    /// <summary>
    /// Canonical axis tuples, populated only when this IFC actually contains variable text.
    /// Glyph metadata stores a one-based index.
    /// </summary>
    internal List<FontVariations> VariationSets { get; init; } = [];

    /// <summary>Direct pure-text legacy clamp.</summary>
    internal int? LineClamp { get; init; }

    /// <summary>
    /// Ordinary single-value ellipsis is active only when inline-axis overflow is non-visible.
    /// The full buffer remains intact for natural overflow metrics and overflow-visible clamp
    /// painting.
    /// </summary>
    internal bool EllipsisOverflow { get; init; }

    /// <summary>Separately shaped marker using the same final text attributes.</summary>
    internal TextBuffer? MarkerBuffer { get; init; }

    internal MarkerPlacement? Marker { get; set; }

    /// <summary>
    /// Canonical collapsed/transformed text used to map shaped ranges back to ordinary inline
    /// DOM owners. Absent for the overwhelmingly common IFC with no nested inline owner.
    /// </summary>
    internal string? OwnerText { get; init; }

    internal List<OwnerTextRange> OwnerRanges { get; init; } = [];

    internal List<InlineOwnerBox> OwnerBoxes { get; init; } = [];

    internal List<InlineBoundaryEvent> BoundaryEvents { get; init; } = [];

    /// <summary>
    /// Nonzero used relative-position offsets, projected onto text ranges. Empty for ordinary
    /// IFCs and for nested inlines that remain at their normal-flow position.
    /// </summary>
    internal List<RelativeOwnerTextRange> RelativeOwnerRanges { get; set; } = [];
}

/// <summary>Free helpers shared by measurement, finalization, and paint.</summary>
internal static class InlineGeometry
{
    /// <summary>
    /// Per-glyph flags carried through shaping metadata. The common non-variable path stays
    /// allocation-free: underline and fill remain packed directly, while the upper half is a
    /// one-based index into an optional variation-set table.
    /// </summary>
    public const ulong MetaUnderline = 1;

    public const int MetaFillShift = 1;

    public const int MetaVariationBits = 32;

    public const int MetaVariationShift = 64 - MetaVariationBits;

    public const ulong MetaVariationMask = ((1UL << MetaVariationBits) - 1) << MetaVariationShift;

    public const ulong MetaFillMask = ((1UL << MetaVariationShift) - 1) & ~MetaUnderline;

    public static int? MetadataFill(ulong metadata)
    {
        ulong value = (metadata & MetaFillMask) >> MetaFillShift;
        return value == 0 ? null : (int)(value - 1);
    }

    public static int? MetadataVariation(ulong metadata)
    {
        ulong value = (metadata & MetaVariationMask) >> MetaVariationShift;
        return value == 0 ? null : (int)(value - 1);
    }

    public static bool BoundaryEventOnLine(InlineItem item, InlineBoundaryEvent evt, int lineStart, int lineEnd)
    {
        int sourceEnd = item.OwnerText?.Length ?? 0;
        bool empty = false;
        foreach (InlineOwnerBox owner in item.OwnerBoxes)
        {
            if (owner.Owner == evt.Owner)
            {
                empty = owner.Start == owner.End;
                break;
            }
        }

        if (empty)
        {
            return (evt.Position >= lineStart && evt.Position < lineEnd)
                || (lineEnd == sourceEnd && evt.Position == lineEnd);
        }

        return evt.IsStart
            ? evt.Position >= lineStart && evt.Position < lineEnd
            : evt.Position > lineStart && evt.Position <= lineEnd;
    }

    public static float LineEdgeAdvance(InlineItem item, int lineStart, int lineEnd)
    {
        float sum = 0f;
        foreach (InlineBoundaryEvent evt in item.BoundaryEvents)
        {
            if (BoundaryEventOnLine(item, evt, lineStart, lineEnd))
            {
                sum += evt.Edge.Advance;
            }
        }

        return sum;
    }

    public static float LineEdgeAlignmentShift(InlineItem item, int lineStart, int lineEnd) =>
        -LineEdgeAdvance(item, lineStart, lineEnd) * item.Align switch
        {
            Obscura.Render.Align.Center => 0.5f,
            Obscura.Render.Align.End or Obscura.Render.Align.Right => 1f,
            _ => 0f,
        };

    public static float LineAdvanceBeforeEvent(InlineItem item, int eventIndex, int lineStart, int lineEnd)
    {
        float sum = 0f;
        for (int i = 0; i < eventIndex && i < item.BoundaryEvents.Count; i++)
        {
            InlineBoundaryEvent evt = item.BoundaryEvents[i];
            if (BoundaryEventOnLine(item, evt, lineStart, lineEnd))
            {
                sum += evt.Edge.Advance;
            }
        }

        return sum;
    }

    public static float LineAdvanceBeforeText(InlineItem item, int globalPosition, int lineStart, int lineEnd)
    {
        float sum = 0f;
        foreach (InlineBoundaryEvent evt in item.BoundaryEvents)
        {
            if (BoundaryEventOnLine(item, evt, lineStart, lineEnd) && evt.Position <= globalPosition)
            {
                sum += evt.Edge.Advance;
            }
        }

        return sum;
    }

    /// <summary>Total shaped size of a buffer: widest line, and the bottom of the last line.</summary>
    public static (float Width, float Height, bool Clamped) BufferSize(InlineItem item)
    {
        float w = 0f;
        float h = 0f;
        int nonemptyLines = 0;
        float? clampHeight = null;
        List<int> lineStarts = item.OwnerText is { } source ? SourceLineStarts(item.Buffer, source) : [];
        int lineIndex = 0;
        foreach (LayoutRun run in item.Buffer.LayoutRuns())
        {
            float offset = lineIndex == 0 ? item.FirstLineOffset : 0f;
            int lineStart = run.LineIndex < lineStarts.Count ? lineStarts[run.LineIndex] : 0;
            int lineEnd = lineStart + run.Text.Length;
            float edges = LineEdgeAdvance(item, lineStart, lineEnd);
            w = F32.Max(w, F32.Max(run.LineW + offset + edges, 0f));
            h = F32.Max(h, run.LineTop + run.LineHeight);
            if (run.Glyphs.Count != 0)
            {
                nonemptyLines++;
                if (item.LineClamp == nonemptyLines)
                {
                    clampHeight = run.LineTop + run.LineHeight;
                }
            }

            lineIndex++;
        }

        bool clamped = item.LineClamp is { } limit && nonemptyLines > limit;
        return (MathF.Ceiling(w), clamped ? clampHeight ?? h : h, clamped);
    }

    /// <summary>
    /// Map each buffer line back to its start offset in the canonical collapsed source. Buffer
    /// lines normally correspond to authored hard breaks; text-indent can also split the first
    /// line synthetically.
    /// </summary>
    public static List<int> SourceLineStarts(TextBuffer buffer, string source)
    {
        List<int> starts = new(buffer.Lines.Count);
        int offset = 0;
        foreach (BufferLine line in buffer.Lines)
        {
            while (offset < source.Length && source[offset] == '\n')
            {
                offset++;
            }

            string text = line.Text;
            int start;
            if (offset <= source.Length && source.AsSpan(offset).StartsWith(text, StringComparison.Ordinal))
            {
                start = offset;
            }
            else
            {
                int relative = offset <= source.Length
                    ? source.IndexOf(text, offset, StringComparison.Ordinal)
                    : -1;
                start = relative < 0 ? offset : relative;
            }

            starts.Add(start);
            offset = Math.Min(start + text.Length, source.Length);
        }

        return starts;
    }

    public static float RunCursorX(LayoutRun run, int offset)
    {
        int index = Math.Min(offset, run.Text.Length);
        if (run.Highlight(index, index) is { } highlight)
        {
            return highlight.X;
        }

        if (index == 0)
        {
            return run.Glyphs.Count > 0 ? run.Glyphs[0].X : 0f;
        }

        return run.Glyphs.Count > 0
            ? run.Glyphs[^1].X + run.Glyphs[^1].W
            : run.LineW;
    }

    public static (float X, float Y) GlyphRelativeOffset(
        List<RelativeOwnerTextRange> ranges,
        int lineStart,
        int glyphStart,
        int glyphEnd)
    {
        if (ranges.Count == 0)
        {
            return (0f, 0f);
        }

        int start = lineStart + glyphStart;
        int end = lineStart + glyphEnd;
        for (int i = ranges.Count - 1; i >= 0; i--)
        {
            if (ranges[i].Start < end && ranges[i].End > start)
            {
                return ranges[i].Offset;
            }
        }

        return (0f, 0f);
    }

    public static float UsedTextIndent(Dimension value, float? width)
    {
        float indent = value.Kind switch
        {
            DimensionKind.Px => value.Value,
            DimensionKind.Percent => (width ?? 0f) * value.Value,
            _ => 0f,
        };

        return float.IsFinite(indent) ? indent : 0f;
    }
}
