using PocketCalculator.Dom;

namespace PocketCalculator.Render;

/// <summary>Atomic inlines in inline formatting contexts (see <see cref="AtomicInline"/>).</summary>
public sealed partial class TextEngine
{
    /// <summary>The text an atomic inline is collected as.</summary>
    internal const string ObjectReplacement = "￼";

    /// <summary>The atomic inlines of item <paramref name="index"/>, in order; empty for none.</summary>
    internal IReadOnlyList<AtomicInline> AtomicsOf(int index) =>
        (index & ReplacedContextBit) == 0 && index >= 0 && index < _items.Count && _items[index].Atomics is { } atomics
            ? atomics
            : [];

    /// <summary>
    /// Give atomic <paramref name="atomic"/> of item <paramref name="index"/> its margin box and
    /// baseline (from the margin box's top; <c>null</c> for the bottom margin edge). The
    /// paragraph is shaped again when they change.
    /// </summary>
    internal void SetAtomic(int index, int atomic, float width, float height, float? baseline)
    {
        if ((index & ReplacedContextBit) != 0 || index < 0 || index >= _items.Count
            || _items[index].Atomics is not { } atomics || atomic < 0 || atomic >= atomics.Count)
        {
            return;
        }

        AtomicInline entry = atomics[atomic];
        width = float.IsFinite(width) ? F32.Max(width, 0f) : 0f;
        height = float.IsFinite(height) ? F32.Max(height, 0f) : 0f;
        if (entry.Width.Equals(width) && entry.Height.Equals(height) && Nullable.Equals(entry.Baseline, baseline))
        {
            return;
        }

        entry.Width = width;
        entry.Height = height;
        entry.Baseline = baseline;
        TextMetrics metrics = AtomicLineMetrics(entry);
        InlineItem item = _items[index];
        bool changed = false;
        foreach (TextBuffer? buffer in (ReadOnlySpan<TextBuffer?>)[item.Buffer, item.SourceBuffer, item.PristineBuffer])
        {
            if (buffer is null)
            {
                continue;
            }

            foreach (BufferLine line in buffer.Lines)
            {
                if (line.AttrsList.UpdateAtomic(atomic, attrs => attrs with { Metrics = metrics, AtomicWidth = width }))
                {
                    line.ResetShaping();
                    changed = true;
                }
            }
        }

        if (changed)
        {
            item.ShapedFor = null;
        }
    }

    /// <summary>
    /// What an atomic inline contributes to its line box (CSS 2.1 10.8.1): its margin box above
    /// and below the line's baseline, from its own baseline and <c>vertical-align</c> against
    /// the inline box it sits in, the formulas of <see cref="Inline.LineBoxExtent"/> with the
    /// margin box standing for the leaded font box.
    /// </summary>
    private static TextMetrics AtomicLineMetrics(AtomicInline atomic)
    {
        float height = atomic.Height;
        float ascent = atomic.Baseline is { } baseline ? F32.Min(F32.Max(baseline, 0f), height) : height;
        float descent = height - ascent;
        InlineVerticalAlignKind kind = atomic.VerticalAlign?.Kind ?? InlineVerticalAlignKind.Baseline;
        if (atomic.ParentAlign != LineBoxAlign.Baseline)
        {
            return new TextMetrics(1f, height, ascent, descent, atomic.ParentAlign, atomic.ParentShift);
        }

        if (kind is InlineVerticalAlignKind.Top or InlineVerticalAlignKind.Bottom)
        {
            return new TextMetrics(
                1f,
                height,
                ascent,
                descent,
                kind == InlineVerticalAlignKind.Top ? LineBoxAlign.LineTop : LineBoxAlign.LineBottom,
                atomic.ParentShift);
        }

        float shift = kind switch
        {
            InlineVerticalAlignKind.Sub => -FontAssets.TruncatedToLayoutUnit((atomic.ParentFontSize / 5f) + 1f),
            InlineVerticalAlignKind.Super => FontAssets.TruncatedToLayoutUnit((atomic.ParentFontSize / 3f) + 1f),
            InlineVerticalAlignKind.Middle =>
                FontAssets.RoundedToLayoutUnit(FontAssets.XHeight(atomic.ParentFontSize, atomic.ParentMetrics) / 2f)
                - ((ascent - descent) / 2f),
            InlineVerticalAlignKind.TextTop =>
                FontAssets.FittedFontBoxMetrics(atomic.ParentFontSize, atomic.ParentMetrics).Ascent - ascent,
            InlineVerticalAlignKind.TextBottom =>
                descent - FontAssets.FittedFontBoxMetrics(atomic.ParentFontSize, atomic.ParentMetrics).Descent,
            InlineVerticalAlignKind.Offset => atomic.VerticalAlign!.Value.Offset.Kind switch
            {
                DimensionKind.Percent => atomic.VerticalAlign!.Value.Offset.Value * FontAssets.QuantizedLineHeight(atomic.LineHeight),
                DimensionKind.Px => atomic.VerticalAlign!.Value.Offset.Value,
                _ => 0f,
            },
            _ => 0f,
        };

        float total = atomic.ParentShift + shift;
        return new TextMetrics(1f, height, ascent + total, descent - total, LineBoxAlign.Baseline, total);
    }

    /// <summary>
    /// Where atomic <paramref name="atomic"/>'s margin box sits on the lines of item
    /// <paramref name="index"/> as last shaped, relative to its content box.
    /// </summary>
    internal (float X, float Y) AtomicPosition(int index, int atomic)
    {
        if ((index & ReplacedContextBit) != 0 || index < 0 || index >= _items.Count
            || _items[index].Atomics is not { } atomics || atomic < 0 || atomic >= atomics.Count
            || _items[index].OwnerText is not { } source)
        {
            return (0f, 0f);
        }

        InlineItem item = _items[index];
        int offset = atomics[atomic].Offset;
        List<int> lineStarts = InlineGeometry.SourceLineStarts(item.Buffer, source);
        int lineIndex = 0;
        foreach (LayoutRun run in item.Buffer.LayoutRuns())
        {
            float lineOffset = lineIndex == 0 ? item.FirstLineOffset : 0f;
            lineIndex++;
            if (run.LineIndex >= lineStarts.Count)
            {
                continue;
            }

            int lineStart = lineStarts[run.LineIndex];
            int local = offset - lineStart;
            if (local < 0 || local >= run.Text.Length)
            {
                continue;
            }

            int lineEnd = lineStart + run.Text.Length;
            float[]? visualShifts = null;
            bool shiftsComputed = false;
            for (int g = 0; g < run.Glyphs.Count; g++)
            {
                LayoutGlyph glyph = run.Glyphs[g];
                if (glyph.Start != local || !InlineGeometry.IsAtomic(glyph.Metadata))
                {
                    continue;
                }

                if (!shiftsComputed)
                {
                    visualShifts = InlineGeometry.VisualEdgeShifts(item, run, lineStart, lineEnd);
                    shiftsComputed = true;
                }

                float x = glyph.X + lineOffset + InlineGeometry.LineEdgeAlignmentShift(item, lineStart, lineEnd)
                    + (visualShifts is not null
                        ? visualShifts[g]
                        : InlineGeometry.LineAdvanceBeforeText(item, lineStart + glyph.Start, lineStart, lineEnd));
                TextMetrics box = glyph.BoxMetrics ?? default;
                float y = box.Align switch
                {
                    LineBoxAlign.LineTop => run.LineTop,
                    LineBoxAlign.LineBottom => run.LineTop + run.LineHeight - (box.Above + box.Below),
                    _ => run.LineY - box.Above,
                };
                return (x, y);
            }
        }

        return (0f, 0f);
    }

    /// <summary>
    /// The baseline of the first or last line of item <paramref name="index"/> laid out at
    /// content width <paramref name="width"/>, from its content box's top; null for a replaced
    /// context or one with no line.
    /// </summary>
    internal float? LineBaselineAt(int index, float width, bool last)
    {
        if ((index & ReplacedContextBit) != 0 || index < 0 || index >= _items.Count)
        {
            return null;
        }

        InlineItem item = _items[index];
        ShapeWithTextIndent(item, width, item.LayoutWrap);
        float? baseline = null;
        foreach (LayoutRun run in item.Buffer.LayoutRuns())
        {
            if (!last)
            {
                return run.LineY;
            }

            baseline = run.LineY;
        }

        return baseline;
    }

    /// <summary>The atomics' current boxes are the ones item <paramref name="index"/>'s final layout used.</summary>
    internal void CommitAtomics(int index)
    {
        foreach (AtomicInline atomic in AtomicsOf(index))
        {
            atomic.Final = (atomic.Width, atomic.Height, atomic.Baseline);
        }
    }

    /// <summary>
    /// Give the atomics of item <paramref name="index"/> the boxes the same context had in
    /// <paramref name="previous"/>'s item <paramref name="previousIndex"/>, when a retained pass
    /// carries the context's layout over (<c>RetainedTaffyLayout.Transplant</c>) and so never
    /// lays its atomics out again. An item adopted from that pass is the same object already.
    /// </summary>
    internal void CarryAtomics(int index, TextEngine previous, int previousIndex)
    {
        IReadOnlyList<AtomicInline> atomics = AtomicsOf(index);
        IReadOnlyList<AtomicInline> before = previous.AtomicsOf(previousIndex);
        if (atomics.Count == 0 || atomics.Count != before.Count || ReferenceEquals(atomics, before))
        {
            return;
        }

        for (int i = 0; i < atomics.Count; i++)
        {
            AtomicInline old = before[i];
            if (old.Final is { } final)
            {
                SetAtomic(index, i, final.Width, final.Height, final.Baseline);
                atomics[i].Final = final;
            }
            else if (!float.IsNaN(old.Width))
            {
                SetAtomic(index, i, old.Width, old.Height, old.Baseline);
            }
        }
    }

    /// <summary>
    /// Put back the boxes of item <paramref name="index"/>'s final layout, which a later
    /// measurement (an intrinsic size probe) may have replaced, before it is shaped for paint.
    /// </summary>
    private void RestoreFinalAtomics(int index)
    {
        IReadOnlyList<AtomicInline> atomics = AtomicsOf(index);
        for (int i = 0; i < atomics.Count; i++)
        {
            if (atomics[i].Final is { } final)
            {
                SetAtomic(index, i, final.Width, final.Height, final.Baseline);
            }
        }
    }
}
