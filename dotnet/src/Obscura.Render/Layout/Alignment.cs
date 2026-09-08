// Port of vendor/taffy/src/style/alignment.rs
namespace Obscura.Render.Layout;

/// <summary>
/// The position-keyword half of <see cref="AlignItems"/> (and its aliases align-self,
/// justify-items, justify-self).
/// </summary>
public enum AlignItemsKeyword : byte
{
    /// <summary>Use the layout mode's normal alignment behavior.</summary>
    Normal,

    /// <summary>Items are packed toward the start of the axis.</summary>
    Start,

    /// <summary>Items are packed toward the end of the axis.</summary>
    End,

    /// <summary>Items are packed towards the flex-relative start of the axis.</summary>
    FlexStart,

    /// <summary>Items are packed towards the flex-relative end of the axis.</summary>
    FlexEnd,

    /// <summary>Items are packed along the center of the cross axis.</summary>
    Center,

    /// <summary>Items are aligned such that their baselines align.</summary>
    Baseline,

    /// <summary>Stretch to fill the container.</summary>
    Stretch,
}

/// <summary>
/// The position-keyword half of <see cref="AlignContent"/> (and its alias justify-content).
/// </summary>
public enum AlignContentKeyword : byte
{
    /// <summary>Items are packed toward the start of the axis.</summary>
    Start,

    /// <summary>Items are packed toward the end of the axis.</summary>
    End,

    /// <summary>Items are packed towards the flex-relative start of the axis.</summary>
    FlexStart,

    /// <summary>Items are packed towards the flex-relative end of the axis.</summary>
    FlexEnd,

    /// <summary>Items are centered around the middle of the axis.</summary>
    Center,

    /// <summary>Items are stretched to fill the container.</summary>
    Stretch,

    /// <summary>First and last items flush with the edges; gaps distributed evenly.</summary>
    SpaceBetween,

    /// <summary>Edge gaps equal to inter-item gaps.</summary>
    SpaceEvenly,

    /// <summary>Edge gaps half of inter-item gaps.</summary>
    SpaceAround,
}

/// <summary>Helpers over <see cref="AlignContentKeyword"/>.</summary>
public static class AlignContentKeywordExtensions
{
    /// <summary>
    /// Returns the reversed keyword for RTL contexts: Start&lt;-&gt;End, FlexStart&lt;-&gt;FlexEnd.
    /// Stretch maps to End to preserve the layout algorithms' historical handling.
    /// </summary>
    public static AlignContentKeyword Reversed(this AlignContentKeyword self) => self switch
    {
        AlignContentKeyword.Start => AlignContentKeyword.End,
        AlignContentKeyword.End => AlignContentKeyword.Start,
        AlignContentKeyword.FlexStart => AlignContentKeyword.FlexEnd,
        AlignContentKeyword.FlexEnd => AlignContentKeyword.FlexStart,
        AlignContentKeyword.Stretch => AlignContentKeyword.End,
        _ => self,
    };
}

/// <summary>
/// The overflow-position modifier per
/// <see href="https://www.w3.org/TR/css-align-3/#overflow-values">CSS Box Alignment §4.3</see>.
/// </summary>
public enum AlignmentSafety : byte
{
    /// <summary>Default; keeps the requested alignment even when the subject overflows.</summary>
    Unsafe,

    /// <summary>Falls back to the start edge when the subject would overflow.</summary>
    Safe,
}

/// <summary>
/// Used to control how child nodes are aligned. Also aliased as align-self, justify-items and
/// justify-self.
/// </summary>
public readonly record struct AlignItems(AlignItemsKeyword Keyword, AlignmentSafety Safety)
{
    /// <summary>Use the layout mode's normal alignment behavior.</summary>
    public static readonly AlignItems Normal = new(AlignItemsKeyword.Normal, AlignmentSafety.Unsafe);

    /// <summary>Items are packed toward the start of the axis.</summary>
    public static readonly AlignItems Start = new(AlignItemsKeyword.Start, AlignmentSafety.Unsafe);

    /// <summary>Items are packed toward the end of the axis.</summary>
    public static readonly AlignItems End = new(AlignItemsKeyword.End, AlignmentSafety.Unsafe);

    /// <summary>Items are packed towards the flex-relative start of the axis.</summary>
    public static readonly AlignItems FlexStart = new(AlignItemsKeyword.FlexStart, AlignmentSafety.Unsafe);

    /// <summary>Items are packed towards the flex-relative end of the axis.</summary>
    public static readonly AlignItems FlexEnd = new(AlignItemsKeyword.FlexEnd, AlignmentSafety.Unsafe);

    /// <summary>Items are packed along the center of the cross axis.</summary>
    public static readonly AlignItems Center = new(AlignItemsKeyword.Center, AlignmentSafety.Unsafe);

    /// <summary>Items are aligned such that their baselines align.</summary>
    public static readonly AlignItems Baseline = new(AlignItemsKeyword.Baseline, AlignmentSafety.Unsafe);

    /// <summary>Stretch to fill the container.</summary>
    public static readonly AlignItems Stretch = new(AlignItemsKeyword.Stretch, AlignmentSafety.Unsafe);

    /// <summary>Like <see cref="Start"/> but falls back to Start on overflow.</summary>
    public static readonly AlignItems SafeStart = new(AlignItemsKeyword.Start, AlignmentSafety.Safe);

    /// <summary>Like <see cref="End"/> but falls back to Start on overflow.</summary>
    public static readonly AlignItems SafeEnd = new(AlignItemsKeyword.End, AlignmentSafety.Safe);

    /// <summary>Like <see cref="FlexStart"/> but falls back to Start on overflow.</summary>
    public static readonly AlignItems SafeFlexStart = new(AlignItemsKeyword.FlexStart, AlignmentSafety.Safe);

    /// <summary>Like <see cref="FlexEnd"/> but falls back to Start on overflow.</summary>
    public static readonly AlignItems SafeFlexEnd = new(AlignItemsKeyword.FlexEnd, AlignmentSafety.Safe);

    /// <summary>Like <see cref="Center"/> but falls back to Start on overflow.</summary>
    public static readonly AlignItems SafeCenter = new(AlignItemsKeyword.Center, AlignmentSafety.Safe);

    /// <summary>Returns true iff this carries the <c>safe</c> overflow-position modifier.</summary>
    public bool IsSafe => Safety == AlignmentSafety.Safe;

    /// <summary>Returns the underlying position keyword, discarding the safety modifier.</summary>
    public AlignItemsKeyword GetKeyword() => Keyword;

    /// <summary>Resolve the context-dependent <c>normal</c> keyword to its used alignment.</summary>
    public AlignItems ResolveNormal(AlignItems normal) =>
        Keyword == AlignItemsKeyword.Normal ? normal : this;
}

/// <summary>
/// Sets the distribution of space between and around content items. Also aliased as
/// justify-content.
/// </summary>
public readonly record struct AlignContent(AlignContentKeyword Keyword, AlignmentSafety Safety)
{
    /// <summary>Items are packed toward the start of the axis.</summary>
    public static readonly AlignContent Start = new(AlignContentKeyword.Start, AlignmentSafety.Unsafe);

    /// <summary>Items are packed toward the end of the axis.</summary>
    public static readonly AlignContent End = new(AlignContentKeyword.End, AlignmentSafety.Unsafe);

    /// <summary>Items are packed towards the flex-relative start of the axis.</summary>
    public static readonly AlignContent FlexStart = new(AlignContentKeyword.FlexStart, AlignmentSafety.Unsafe);

    /// <summary>Items are packed towards the flex-relative end of the axis.</summary>
    public static readonly AlignContent FlexEnd = new(AlignContentKeyword.FlexEnd, AlignmentSafety.Unsafe);

    /// <summary>Items are centered around the middle of the axis.</summary>
    public static readonly AlignContent Center = new(AlignContentKeyword.Center, AlignmentSafety.Unsafe);

    /// <summary>Items are stretched to fill the container.</summary>
    public static readonly AlignContent Stretch = new(AlignContentKeyword.Stretch, AlignmentSafety.Unsafe);

    /// <summary>First and last items flush with the edges of the container.</summary>
    public static readonly AlignContent SpaceBetween = new(AlignContentKeyword.SpaceBetween, AlignmentSafety.Unsafe);

    /// <summary>Edge gaps equal the gaps between items.</summary>
    public static readonly AlignContent SpaceEvenly = new(AlignContentKeyword.SpaceEvenly, AlignmentSafety.Unsafe);

    /// <summary>Edge gaps are half the gaps between items.</summary>
    public static readonly AlignContent SpaceAround = new(AlignContentKeyword.SpaceAround, AlignmentSafety.Unsafe);

    /// <summary>Like <see cref="Start"/> but falls back to Start on overflow.</summary>
    public static readonly AlignContent SafeStart = new(AlignContentKeyword.Start, AlignmentSafety.Safe);

    /// <summary>Like <see cref="End"/> but falls back to Start on overflow.</summary>
    public static readonly AlignContent SafeEnd = new(AlignContentKeyword.End, AlignmentSafety.Safe);

    /// <summary>Like <see cref="FlexStart"/> but falls back to Start on overflow.</summary>
    public static readonly AlignContent SafeFlexStart = new(AlignContentKeyword.FlexStart, AlignmentSafety.Safe);

    /// <summary>Like <see cref="FlexEnd"/> but falls back to Start on overflow.</summary>
    public static readonly AlignContent SafeFlexEnd = new(AlignContentKeyword.FlexEnd, AlignmentSafety.Safe);

    /// <summary>Like <see cref="Center"/> but falls back to Start on overflow.</summary>
    public static readonly AlignContent SafeCenter = new(AlignContentKeyword.Center, AlignmentSafety.Safe);

    /// <summary>Returns true iff this carries the <c>safe</c> overflow-position modifier.</summary>
    public bool IsSafe => Safety == AlignmentSafety.Safe;

    /// <summary>Returns the underlying position keyword, discarding the safety modifier.</summary>
    public AlignContentKeyword GetKeyword() => Keyword;
}
