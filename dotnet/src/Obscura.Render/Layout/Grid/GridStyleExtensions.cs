// The behavioural half of vendor/taffy/src/style/grid.rs that GridTypes.cs does
// not carry: the predicates and resolution methods on MinTrackSizingFunction,
// MaxTrackSizingFunction, TrackSizingFunction, GridAutoFlow and
// GridTemplateComponent.
//
// GridTypes.cs already carries the data shapes (it is the seam the rest of the
// foundation compiles against). These are extension methods rather than members
// so that file stays untouched.
namespace Obscura.Render.Layout;

/// <summary>Predicates and resolution helpers over the grid track sizing functions.</summary>
internal static class GridStyleExtensions
{
    // ------------------------------------------------------- MinTrackSizingFunction

    /// <summary>Returns true if the min track sizing function is MinContent, MaxContent or Auto.</summary>
    public static bool IsIntrinsic(this MinTrackSizingFunction self) => self.IntoRaw().IsIntrinsic;

    /// <summary>Returns true if the min track sizing function is MinContent or MaxContent.</summary>
    public static bool IsMinOrMaxContent(this MinTrackSizingFunction self) => self.IntoRaw().IsMinOrMaxContent;

    /// <summary>Returns true if the value is an fr value.</summary>
    public static bool IsFr(this MinTrackSizingFunction self) => self.IntoRaw().IsFr;

    /// <summary>Returns true if the value is auto.</summary>
    public static bool IsAuto(this MinTrackSizingFunction self) => self.IntoRaw().IsAuto;

    /// <summary>Returns true if the value is min-content.</summary>
    public static bool IsMinContent(this MinTrackSizingFunction self) => self.IntoRaw().IsMinContent;

    /// <summary>Returns true if the value is max-content.</summary>
    public static bool IsMaxContent(this MinTrackSizingFunction self) => self.IntoRaw().IsMaxContent;

    /// <summary>
    /// Returns fixed point values directly and resolves percentage values against the passed parent
    /// size. All other kinds of track sizing function return null.
    /// </summary>
    public static float? DefiniteValue(this MinTrackSizingFunction self, float? parentSize, CalcResolver calc) =>
        DefiniteValueRaw(self.IntoRaw(), parentSize, calc);

    /// <summary>Resolve percentage values against the passed parent size.</summary>
    public static float? ResolvedPercentageSize(
        this MinTrackSizingFunction self,
        float parentSize,
        CalcResolver calc) => self.IntoRaw().ResolvedPercentageSize(parentSize, calc);

    /// <summary>Whether the track sizing function depends on the size of the parent node.</summary>
    public static bool UsesPercentage(this MinTrackSizingFunction self)
    {
        var raw = self.IntoRaw();
        return raw.Tag == CompactLength.PercentTag || raw.IsCalc;
    }

    // ------------------------------------------------------- MaxTrackSizingFunction

    /// <summary>
    /// Returns true if the max track sizing function is MinContent, MaxContent, FitContent or Auto.
    /// </summary>
    public static bool IsIntrinsic(this MaxTrackSizingFunction self) => self.IntoRaw().IsIntrinsic;

    /// <summary>Returns true if the max track sizing function is MaxContent, FitContent or Auto.</summary>
    public static bool IsMaxContentAlike(this MaxTrackSizingFunction self) => self.IntoRaw().IsMaxContentAlike;

    /// <summary>Returns true if the value is an fr value.</summary>
    public static bool IsFr(this MaxTrackSizingFunction self) => self.IntoRaw().IsFr;

    /// <summary>Returns true if the value is auto.</summary>
    public static bool IsAuto(this MaxTrackSizingFunction self) => self.IntoRaw().IsAuto;

    /// <summary>Returns true if the value is min-content.</summary>
    public static bool IsMinContent(this MaxTrackSizingFunction self) => self.IntoRaw().IsMinContent;

    /// <summary>Returns true if the value is max-content.</summary>
    public static bool IsMaxContent(this MaxTrackSizingFunction self) => self.IntoRaw().IsMaxContent;

    /// <summary>Returns true if the value is a fit-content(...) value.</summary>
    public static bool IsFitContent(this MaxTrackSizingFunction self) => self.IntoRaw().IsFitContent;

    /// <summary>Returns true if the value is max-content or fit-content(...).</summary>
    public static bool IsMaxOrFitContent(this MaxTrackSizingFunction self) => self.IntoRaw().IsMaxOrFitContent;

    /// <summary>The raw f32 payload of the sizing function (its fr factor, length, ...).</summary>
    public static float RawValue(this MaxTrackSizingFunction self) => self.IntoRaw().Value;

    /// <summary>Returns whether the value can be resolved using <see cref="DefiniteValue"/>.</summary>
    public static bool HasDefiniteValue(this MaxTrackSizingFunction self, float? parentSize)
    {
        var raw = self.IntoRaw();
        if (raw.Tag == CompactLength.LengthTag)
        {
            return true;
        }

        if (raw.Tag == CompactLength.PercentTag)
        {
            return parentSize.HasValue;
        }

        return raw.IsCalc && parentSize.HasValue;
    }

    /// <summary>
    /// Returns fixed point values directly and resolves percentage values against the passed parent
    /// size. All other kinds of track sizing function return null.
    /// </summary>
    public static float? DefiniteValue(this MaxTrackSizingFunction self, float? parentSize, CalcResolver calc) =>
        DefiniteValueRaw(self.IntoRaw(), parentSize, calc);

    /// <summary>
    /// Resolve the maximum size of the track as defined by a fixed or percentage track sizing
    /// function or by a fit-content sizing function's argument.
    /// </summary>
    public static float? DefiniteLimit(this MaxTrackSizingFunction self, float? parentSize, CalcResolver calc)
    {
        var raw = self.IntoRaw();
        if (raw.Tag == CompactLength.FitContentPxTag)
        {
            return raw.Value;
        }

        if (raw.Tag == CompactLength.FitContentPercentTag)
        {
            return parentSize.HasValue ? raw.Value * parentSize.Value : null;
        }

        return DefiniteValueRaw(raw, parentSize, calc);
    }

    /// <summary>Resolve percentage values against the passed parent size.</summary>
    public static float? ResolvedPercentageSize(
        this MaxTrackSizingFunction self,
        float parentSize,
        CalcResolver calc) => self.IntoRaw().ResolvedPercentageSize(parentSize, calc);

    /// <summary>Whether the track sizing function depends on the size of the parent node.</summary>
    public static bool UsesPercentage(this MaxTrackSizingFunction self) => self.IntoRaw().UsesPercentage;

    // ------------------------------------------------------- TrackSizingFunction

    /// <summary>Extract the min track sizing function.</summary>
    public static MinTrackSizingFunction MinSizingFunction(this TrackSizingFunction self) => self.Min;

    /// <summary>Extract the max track sizing function.</summary>
    public static MaxTrackSizingFunction MaxSizingFunction(this TrackSizingFunction self) => self.Max;

    /// <summary>Determine whether at least one of the min/max components is a fixed sizing function.</summary>
    public static bool HasFixedComponent(this TrackSizingFunction self) =>
        self.Min.IntoRaw().IsLengthOrPercentage || self.Max.IntoRaw().IsLengthOrPercentage;

    // ------------------------------------------------------- GridAutoFlow

    /// <summary>
    /// Whether grid auto placement uses the sparse placement algorithm or the dense one.
    /// </summary>
    public static bool IsDense(this GridAutoFlow self) =>
        self is GridAutoFlow.RowDense or GridAutoFlow.ColumnDense;

    /// <summary>Whether grid auto placement fills areas row-wise or column-wise.</summary>
    public static AbsoluteAxis PrimaryAxis(this GridAutoFlow self) =>
        self is GridAutoFlow.Row or GridAutoFlow.RowDense ? AbsoluteAxis.Horizontal : AbsoluteAxis.Vertical;

    // ------------------------------------------------------- GridTemplateComponent

    /// <summary>Whether the track definition is an auto-repeated fragment.</summary>
    public static bool IsAutoRepetition(this GridTemplateComponent self) =>
        self.Kind == GridTemplateComponentKind.Repeat
        && self.Repetition!.Count.Kind is RepetitionCountKind.AutoFill or RepetitionCountKind.AutoFit;

    /// <summary>The number of tracks in a repetition.</summary>
    public static ushort TrackCount(this GridTemplateRepetition self) => (ushort)self.Tracks.Count;

    private static float? DefiniteValueRaw(CompactLength raw, float? parentSize, CalcResolver calc)
    {
        if (raw.Tag == CompactLength.LengthTag)
        {
            return raw.Value;
        }

        if (raw.Tag == CompactLength.PercentTag)
        {
            return parentSize.HasValue ? raw.Value * parentSize.Value : null;
        }

        if (raw.IsCalc)
        {
            return parentSize.HasValue ? calc(raw.CalcValue, parentSize.Value) : null;
        }

        return null;
    }
}
