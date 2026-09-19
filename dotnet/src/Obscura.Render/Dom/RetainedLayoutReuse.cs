// PORT NOTE. This has no counterpart in crates/obscura-render: the Rust engine also re-lays
// the whole document on every retained restyle, and at its speed that is affordable. The port
// is slower per pass, so a forced geometry read after a style write cost a whole-document
// prepare (~25us per node). This is the incremental-invalidation gate that answers it, and is
// a deliberate C# deviation recorded under "Known deviations" in todo.md.
using System.Collections;
using System.Reflection;
using Obscura.Dom;

namespace Obscura.Render;

/// <summary>
/// A layout a retained restyle may keep, with the style object each recomputed element carried
/// when that layout was produced.
/// </summary>
internal sealed record RetainedLayoutReuseCandidate(
    DomLayout Previous,
    Dictionary<NodeId, LayoutStyle> Before,
    bool NothingRecomputed);

/// <summary>How far a retained restyle of one element reaches.</summary>
internal enum RetainedRestyleImpact : byte
{
    /// <summary>The recomputed style is identical; nothing downstream of the cascade changed.</summary>
    Unchanged = 0,

    /// <summary>
    /// Only members no box-generating, sizing, shaping, transform or clip pass reads changed,
    /// so the retained geometry is still exact and paint reads the live style map.
    /// </summary>
    PaintOnly = 1,

    /// <summary>Something layout can observe changed. The document must be laid out again.</summary>
    Layout = 2,
}

/// <summary>
/// Decides whether a retained restyle can keep the previous layout instead of running one.
/// </summary>
/// <remarks>
/// <para>
/// The gate is deliberately <b>fail-closed</b> in three separate ways, because serving stale
/// geometry is far worse than serving it slowly:
/// </para>
/// <list type="bullet">
/// <item>Only the members named in <see cref="PaintOnlyMembers"/> may differ. Every other
/// member of <see cref="LayoutStyle"/> - including any member added later, which is in no
/// list - forces a relayout.</item>
/// <item>Equality is proven, never assumed. A member whose value the comparer cannot compare
/// structurally (an unrecognised reference type, a mismatched runtime type, a nesting deeper
/// than <see cref="MaxDepth"/>) counts as changed.</item>
/// <item>The caller only offers the comparison at all when every mutation in the batch is an
/// attribute mutation, the sheet has no container queries, and the dirty set is small. A tree,
/// text, resource or animation mutation never reaches here.</item>
/// </list>
/// <para>
/// The comparison point is the end of the top-down pass, immediately before
/// <c>ResetScrollbarGutters</c>: by then the cascade, <c>ResolveComputedValues</c> and the
/// style fixups have all run on the new style objects exactly as they ran on the old ones a
/// pass earlier, so the two are comparable member for member. A member in
/// <see cref="PaintOnlyMembers"/> must therefore be one that nothing <i>after</i> that point
/// reads - not merely one that "sounds visual". Each entry below names where that was checked.
/// </para>
/// </remarks>
internal static class RetainedLayoutReuse
{
    /// <summary>Recursion limit for the structural comparison; deeper counts as changed.</summary>
    private const int MaxDepth = 6;

    /// <summary>
    /// Above this many recomputed elements the comparison stops paying for itself, and a dirty
    /// set that large is a restyle of a whole subtree rather than the forced-read pattern this
    /// exists for.
    /// </summary>
    internal const int MaxComparedNodes = 512;

    /// <summary>
    /// Members of <see cref="LayoutStyle"/> that no pass after the top-down cascade reads.
    /// </summary>
    /// <remarks>
    /// Verified by searching <c>Dom/</c>, <c>Inline/</c> and <c>Layout/</c> for each name: the
    /// only hits are in <c>LayoutDomComputed</c>, which runs <i>before</i> the comparison point
    /// and has therefore already done its work on both style objects. Everything else that
    /// reads them is under <c>Paint/</c>, which re-reads the live style map on every paint and
    /// so sees the new values without a new layout. <c>BackgroundColor</c>,
    /// <c>BackgroundImage</c>, <c>BoxShadow</c> and <c>BackgroundClipText</c> look like they
    /// belong here and do not: <c>DomBuild</c> and <c>InlineCollect</c> read them when deciding
    /// whether a box paints anything at all.
    /// </remarks>
    private static readonly string[] PaintOnlyMembers =
    [
        "Opacity",               // LayoutDomComputed only (feeds EffectivelyInvisible).
        "Color",                 // LayoutDomComputed only (inherited colour, currentcolor).
        "BorderColor",           // Paint/ only; widths live in Border/BorderModel.
        "Outline",               // Paint/ only; an outline is not in flow.
        "ZIndex",                // Paint/ only; paint order, never box geometry.
        "Cursor",                // LayoutDomComputed only (inherited), then Paint/.
        "VisibilityHidden",      // LayoutDomComputed only (feeds EffectivelyInvisible).
        "EffectivelyInvisible",  // An output of LayoutDomComputed, read only by Paint/.
    ];

    private static readonly (FieldInfo Field, bool PaintOnly)[] Members = BuildMembers();

    private static readonly bool Disabled =
        Environment.GetEnvironmentVariable("OBSCURA_DISABLE_RETAINED_LAYOUT_REUSE") == "1";

    /// <summary>Whether the gate is available at all. The environment switch is for A/B runs.</summary>
    internal static bool Enabled => !Disabled;

    private static (FieldInfo, bool)[] BuildMembers()
    {
        FieldInfo[] fields = typeof(LayoutStyle).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var members = new (FieldInfo, bool)[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            FieldInfo field = fields[i];
            bool paintOnly = false;
            foreach (string name in PaintOnlyMembers)
            {
                if (string.Equals(field.Name, name, StringComparison.Ordinal))
                {
                    paintOnly = true;
                    break;
                }
            }

            members[i] = (field, paintOnly);
        }

        return members;
    }

    /// <summary>Classify one element's recomputed style against the style it replaced.</summary>
    internal static RetainedRestyleImpact Classify(LayoutStyle before, LayoutStyle after) =>
        Classify(before, after, 0);

    private static RetainedRestyleImpact Classify(LayoutStyle before, LayoutStyle after, int depth)
    {
        if (ReferenceEquals(before, after))
        {
            return RetainedRestyleImpact.Unchanged;
        }

        if (depth >= MaxDepth)
        {
            return RetainedRestyleImpact.Layout;
        }

        RetainedRestyleImpact impact = RetainedRestyleImpact.Unchanged;
        foreach ((FieldInfo field, bool paintOnly) in Members)
        {
            object? left = field.GetValue(before);
            object? right = field.GetValue(after);
            if (left is LayoutStyle nestedLeft && right is LayoutStyle nestedRight)
            {
                RetainedRestyleImpact nested = Classify(nestedLeft, nestedRight, depth + 1);
                if (nested == RetainedRestyleImpact.Layout)
                {
                    return RetainedRestyleImpact.Layout;
                }

                if (nested > impact)
                {
                    impact = nested;
                }

                continue;
            }

            if (ValuesEqual(left, right, depth + 1))
            {
                continue;
            }

            if (!paintOnly)
            {
                return RetainedRestyleImpact.Layout;
            }

            impact = RetainedRestyleImpact.PaintOnly;
        }

        return impact;
    }

    /// <summary>
    /// Structural equality, proven rather than assumed: anything the comparer does not
    /// recognise is reported as different.
    /// </summary>
    private static bool ValuesEqual(object? left, object? right, int depth)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.GetType() != right.GetType())
        {
            return false;
        }

        if (depth >= MaxDepth)
        {
            return false;
        }

        switch (left)
        {
            case string:
                return left.Equals(right);

            case LayoutStyle nested when right is LayoutStyle other:
                return Classify(nested, other, depth) == RetainedRestyleImpact.Unchanged;

            case IDictionary leftMap when right is IDictionary rightMap:
            {
                if (leftMap.Count != rightMap.Count)
                {
                    return false;
                }

                foreach (DictionaryEntry entry in leftMap)
                {
                    if (!rightMap.Contains(entry.Key)
                        || !ValuesEqual(entry.Value, rightMap[entry.Key], depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            }

            case IList leftList when right is IList rightList:
            {
                if (leftList.Count != rightList.Count)
                {
                    return false;
                }

                for (int i = 0; i < leftList.Count; i++)
                {
                    if (!ValuesEqual(leftList[i], rightList[i], depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
                // A value type compares by value; a reference type the comparer does not model
                // was already rejected by the reference check above unless it overrides Equals.
                return left.GetType().IsValueType && left.Equals(right);
        }
    }

    /// <summary>
    /// Classify a whole recomputed dirty set. <paramref name="before"/> holds the style object
    /// each element carried when the retained layout was produced.
    /// </summary>
    internal static RetainedRestyleImpact ClassifyAll(
        IReadOnlyDictionary<NodeId, LayoutStyle> before,
        IReadOnlyDictionary<NodeId, LayoutStyle> after)
    {
        RetainedRestyleImpact impact = RetainedRestyleImpact.Unchanged;
        foreach ((NodeId node, LayoutStyle previous) in before)
        {
            // An element the cascade did not produce a style for this pass no longer generates
            // the box the retained geometry was measured from.
            if (!after.TryGetValue(node, out LayoutStyle? current))
            {
                return RetainedRestyleImpact.Layout;
            }

            RetainedRestyleImpact one = Classify(previous, current);
            if (one == RetainedRestyleImpact.Layout)
            {
                return RetainedRestyleImpact.Layout;
            }

            if (one > impact)
            {
                impact = one;
            }
        }

        return impact;
    }
}
