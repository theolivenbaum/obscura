using PocketCalculator.Dom;
using RgbaColor = PocketCalculator.Render.Css.RgbaColor;

namespace PocketCalculator.Render;

/// <summary>A <c>-webkit-background-clip: text</c> fill: a gradient angle and its stops.</summary>
public readonly record struct ClipTextFill(float Angle, List<(RgbaColor Color, float? Position)> Stops);

/// <summary>
/// One run of collected text and the innermost inline owner open around it, as an index into
/// <see cref="InlineItem.OwnerChain"/> (-1 for none). The owners around a run are that node and
/// its <see cref="OwnerChainNode.Parent"/> chain, so a run costs O(1) however deep it sits.
/// </summary>
internal readonly record struct OwnerTextChunk(int Start, int End, int Node);

/// <summary>An inline owner and the owner open around it (an index, -1 for none).</summary>
internal readonly record struct OwnerChainNode(NodeId Owner, int Parent);

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
    int EndEvent,
    InlineBoxExtent Extent);

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
    int StartEvent,
    int ChainNode);

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
    /// The buffer as built, before a float layout split its lines; captured by the first
    /// layout around floats so a later layout without them starts from the original paragraphs.
    /// Null for an IFC never laid out beside a float.
    /// </summary>
    internal TextBuffer? PristineBuffer { get; set; }

    /// <summary>
    /// The float exclusions of the last final layout of this IFC, relative to its content box,
    /// which <see cref="TextEngine.Finalize"/> lays the lines out around again. Null when no
    /// float shortens its line boxes.
    /// </summary>
    internal Layout.FloatBands? FloatBands { get; set; }

    /// <summary>
    /// Floats in the inline content and the text offset each sits at, in order; the block
    /// formatting context places each on (or below) the line holding its offset. Null when the
    /// IFC holds none.
    /// </summary>
    internal List<(NodeId Float, int Offset)>? FloatAnchors { get; set; }

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

    /// <summary>The block's <c>direction</c> is <c>rtl</c>: its lines start at the right.</summary>
    internal bool Rtl { get; init; }

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

    /// <summary>
    /// Collected text runs with the owners open around them. Replaces a flat list of one
    /// (owner, range) entry per open owner per run, which was O(depth) per run and quadratic in
    /// memory for nested inline boxes.
    /// </summary>
    internal List<OwnerTextChunk> OwnerChunks { get; init; } = [];

    internal List<OwnerChainNode> OwnerChain { get; init; } = [];

    internal List<InlineOwnerBox> OwnerBoxes { get; init; } = [];

    internal List<InlineBoundaryEvent> BoundaryEvents { get; init; } = [];

    private InlineEdgeIndex? _edgeIndex;

    /// <summary>Query structure over <see cref="BoundaryEvents"/>, built on first use.</summary>
    internal InlineEdgeIndex EdgeIndex => _edgeIndex ??= new InlineEdgeIndex(this);

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

    public static float LineEdgeAdvance(InlineItem item, int lineStart, int lineEnd) =>
        item.BoundaryEvents.Count == 0 ? 0f : item.EdgeIndex.Advance(lineStart, lineEnd, int.MaxValue);

    /// <summary>
    /// How far a line moves for the inline boxes' margins, borders and padding on it, which
    /// the text was aligned without: they push the content right (see
    /// <see cref="VisualEdgeShifts"/> for a line with right-to-left text), so an aligned line
    /// moves back left by the share its alignment gives that side. A justified line was laid
    /// out in the width they leave and does not move.
    /// </summary>
    public static float LineEdgeAlignmentShift(InlineItem item, int lineStart, int lineEnd) =>
        -LineEdgeAdvance(item, lineStart, lineEnd) * item.Align switch
        {
            PocketCalculator.Render.Align.Center => 0.5f,
            PocketCalculator.Render.Align.End or PocketCalculator.Render.Align.Right => 1f,
            _ => 0f,
        };

    /// <summary>
    /// For a line holding right-to-left glyphs, how far each glyph (by index in the run)
    /// moves right for the inline-box edges visually to its left; <c>null</c> when the line
    /// has none of either, and <see cref="LineAdvanceBeforeText"/> applies.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-render/src/inline.rs, which moves every glyph right by
    /// the edges logically before it. That is the visual order only left to right: in a
    /// dir=rtl paragraph an inline box's padding pushed the text after it the wrong way, and
    /// Chromium 141 puts a padded, bordered span opening a right-to-left line at the line's
    /// right edge, its border included. An edge sits where the cursor at its text offset is,
    /// so the boxes order as the text they hold does.
    /// </remarks>
    public static float[]? VisualEdgeShifts(InlineItem item, LayoutRun run, int lineStart, int lineEnd)
    {
        List<InlineBoundaryEvent> events = item.BoundaryEvents;
        if (events.Count == 0)
        {
            return null;
        }

        bool anyRtl = false;
        foreach (LayoutGlyph glyph in run.Glyphs)
        {
            if ((glyph.Level & 1) != 0)
            {
                anyRtl = true;
                break;
            }
        }

        if (!anyRtl)
        {
            return null;
        }

        int first = 0;
        int hi = events.Count;
        while (first < hi)
        {
            int mid = (first + hi) >>> 1;
            if (events[mid].Position < lineStart)
            {
                first = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        List<(float X, float Advance)> edges = [];
        float before = item.EdgeIndex.Advance(lineStart, lineEnd, first);
        for (int index = first; index < events.Count && events[index].Position <= lineEnd; index++)
        {
            float through = item.EdgeIndex.Advance(lineStart, lineEnd, index + 1);
            float advance = through - before;
            before = through;
            if (advance != 0f)
            {
                edges.Add((EdgeX(run, events[index].Position - lineStart, events[index].IsStart), advance));
            }
        }

        if (edges.Count == 0)
        {
            return null;
        }

        var shifts = new float[run.Glyphs.Count];
        for (int g = 0; g < shifts.Length; g++)
        {
            LayoutGlyph glyph = run.Glyphs[g];
            float center = glyph.X + (glyph.W / 2f);
            float shift = 0f;
            foreach ((float x, float advance) in edges)
            {
                if (x < center)
                {
                    shift += advance;
                }
            }

            shifts[g] = shift;
        }

        return shifts;
    }

    public static float LineAdvanceBeforeEvent(InlineItem item, int eventIndex, int lineStart, int lineEnd) =>
        item.BoundaryEvents.Count == 0 ? 0f : item.EdgeIndex.Advance(lineStart, lineEnd, eventIndex);

    public static float LineAdvanceBeforeText(InlineItem item, int globalPosition, int lineStart, int lineEnd) =>
        item.BoundaryEvents.Count == 0 ? 0f : item.EdgeIndex.AdvanceBeforeText(globalPosition, lineStart, lineEnd);

    /// <summary>
    /// Where an inline box's edge at <paramref name="offset"/> sits on a line: an opening
    /// edge before the character there, a closing one after the character before it, on the
    /// side that character's direction puts first or last.
    /// </summary>
    private static float EdgeX(LayoutRun run, int offset, bool opening)
    {
        int at = opening ? offset : offset - 1;
        LayoutGlyph? found = null;
        foreach (LayoutGlyph glyph in run.Glyphs)
        {
            if (glyph.Start <= at && at < glyph.End)
            {
                found = glyph;
                break;
            }
        }

        if (found is not { } hit)
        {
            // Nothing on the line holds it (an empty box, or white space the line dropped):
            // fall back to the other neighbour, then to the logical cursor.
            int other = opening ? offset - 1 : offset;
            foreach (LayoutGlyph glyph in run.Glyphs)
            {
                if (glyph.Start <= other && other < glyph.End)
                {
                    bool rtl = (glyph.Level & 1) != 0;
                    return opening == rtl ? glyph.X : glyph.X + glyph.W;
                }
            }

            return RunCursorX(run, offset);
        }

        bool hitRtl = (hit.Level & 1) != 0;
        return opening == hitRtl ? hit.X + hit.W : hit.X;
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
            w = F32.Max(w, F32.Max(run.LineW + run.X + offset + edges, 0f));
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
        bool afterBreak = false;
        foreach (BufferLine line in buffer.Lines)
        {
            starts.Add(NextSourceLineStart(line.Text, source, ref offset, ref afterBreak));
        }

        return starts;
    }

    /// <summary>
    /// One step of <see cref="SourceLineStarts"/>: the start of the next buffer line, given the
    /// state the previous lines left. Lets a caller that splits lines as it goes keep the
    /// mapping incrementally instead of recomputing it for every line.
    /// </summary>
    public static int NextSourceLineStart(string text, string source, ref int offset, ref bool afterBreak)
    {
        // Exactly one newline separates two buffer lines, and none precedes the first.
        // DEVIATION from crates/obscura-render/src/inline.rs, which had no equivalent
        // mapping at all; the C# version skipped a *run* of newlines before *every* line,
        // so `<div><br>x</div>` put line 0 at offset 1 instead of 0 and `a\n\nb` put the
        // empty line at 3 instead of 2. Nothing read those offsets until `<br>` started
        // reporting a rect, which is what exposed it.
        if (afterBreak && offset < source.Length && source[offset] == '\n')
        {
            offset++;
        }

        afterBreak = true;
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

        offset = Math.Min(start + text.Length, source.Length);
        return start;
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
            return run.Glyphs.Count > 0 ? run.Glyphs[0].X : run.X;
        }

        return run.Glyphs.Count > 0
            ? run.Glyphs[^1].X + run.Glyphs[^1].W
            : run.X + run.LineW;
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
