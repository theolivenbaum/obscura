// Port of vendor/taffy/src/util/resolve.rs
namespace Obscura.Render.Layout;

/// <summary>
/// Resolve potentially context-dependent sizes/dimensions into context-independent values.
/// </summary>
public static class Resolve
{
    /// <summary>A size whose components are both a definite zero.</summary>
    public static Size<float?> SizeOptionZero => new(0f, 0f);

    // ------------------------------------------------------------ MaybeResolve

    /// <summary>Converts the given <see cref="LengthPercentage"/> into an absolute length.</summary>
    public static float? MaybeResolve(this LengthPercentage self, float? context, CalcResolver calc)
    {
        var inner = self.IntoRaw();
        ulong tag = inner.Tag;
        if (tag == CompactLength.LengthTag)
        {
            return inner.Value;
        }

        if (tag == CompactLength.PercentTag)
        {
            return context.HasValue ? context.Value * inner.Value : null;
        }

        if (inner.IsCalc)
        {
            return context.HasValue ? calc(inner.CalcValue, context.Value) : null;
        }

        throw new InvalidOperationException("Invalid LengthPercentage tag");
    }

    /// <summary>Converts the given <see cref="LengthPercentage"/> into an absolute length.</summary>
    public static float? MaybeResolve(this LengthPercentage self, float context, CalcResolver calc) =>
        self.MaybeResolve((float?)context, calc);

    /// <summary>Converts the given <see cref="LengthPercentageAuto"/> into an absolute length.</summary>
    public static float? MaybeResolve(this LengthPercentageAuto self, float? context, CalcResolver calc)
    {
        var inner = self.IntoRaw();
        ulong tag = inner.Tag;
        if (tag == CompactLength.AutoTag)
        {
            return null;
        }

        if (tag == CompactLength.LengthTag)
        {
            return inner.Value;
        }

        if (tag == CompactLength.PercentTag)
        {
            return context.HasValue ? context.Value * inner.Value : null;
        }

        if (inner.IsCalc)
        {
            return context.HasValue ? calc(inner.CalcValue, context.Value) : null;
        }

        throw new InvalidOperationException("Invalid LengthPercentageAuto tag");
    }

    /// <summary>Converts the given <see cref="LengthPercentageAuto"/> into an absolute length.</summary>
    public static float? MaybeResolve(this LengthPercentageAuto self, float context, CalcResolver calc) =>
        self.MaybeResolve((float?)context, calc);

    /// <summary>Converts the given <see cref="Dimension"/> into an absolute length.</summary>
    public static float? MaybeResolve(this Dimension self, float? context, CalcResolver calc)
    {
        var inner = self.IntoRaw();
        ulong tag = inner.Tag;
        if (tag == CompactLength.AutoTag)
        {
            return null;
        }

        if (tag == CompactLength.LengthTag)
        {
            return inner.Value;
        }

        if (tag == CompactLength.PercentTag)
        {
            return context.HasValue ? context.Value * inner.Value : null;
        }

        if (inner.IsCalc)
        {
            return context.HasValue ? calc(inner.CalcValue, context.Value) : null;
        }

        throw new InvalidOperationException("Invalid Dimension tag");
    }

    /// <summary>Converts the given <see cref="Dimension"/> into an absolute length.</summary>
    public static float? MaybeResolve(this Dimension self, float context, CalcResolver calc) =>
        self.MaybeResolve((float?)context, calc);

    /// <summary>Converts any parent-relative values for size into an absolute size.</summary>
    public static Size<float?> MaybeResolve(this Size<Dimension> self, Size<float?> context, CalcResolver calc) =>
        new(self.Width.MaybeResolve(context.Width, calc), self.Height.MaybeResolve(context.Height, calc));

    /// <summary>Converts any parent-relative values for size into an absolute size.</summary>
    public static Size<float?> MaybeResolve(this Size<Dimension> self, Size<float> context, CalcResolver calc) =>
        new(self.Width.MaybeResolve(context.Width, calc), self.Height.MaybeResolve(context.Height, calc));

    /// <summary>Converts any parent-relative values for size into an absolute size.</summary>
    public static Size<float?> MaybeResolve(
        this Size<LengthPercentage> self,
        Size<float?> context,
        CalcResolver calc) =>
        new(self.Width.MaybeResolve(context.Width, calc), self.Height.MaybeResolve(context.Height, calc));

    // ----------------------------------------------------------- ResolveOrZero

    /// <summary>Resolve, falling back to zero.</summary>
    public static float ResolveOrZero(this LengthPercentage self, float? context, CalcResolver calc) =>
        self.MaybeResolve(context, calc) ?? 0.0f;

    /// <summary>Resolve, falling back to zero.</summary>
    public static float ResolveOrZero(this LengthPercentageAuto self, float? context, CalcResolver calc) =>
        self.MaybeResolve(context, calc) ?? 0.0f;

    /// <summary>Resolve, falling back to zero.</summary>
    public static float ResolveOrZero(this Dimension self, float? context, CalcResolver calc) =>
        self.MaybeResolve(context, calc) ?? 0.0f;

    /// <summary>Converts any parent-relative values for size into an absolute size.</summary>
    public static Size<float> ResolveOrZero(this Size<LengthPercentage> self, Size<float?> context, CalcResolver calc) =>
        new(self.Width.ResolveOrZero(context.Width, calc), self.Height.ResolveOrZero(context.Height, calc));

    /// <summary>Converts any parent-relative values for a rect into an absolute rect.</summary>
    public static Rect<float> ResolveOrZero(this Rect<LengthPercentage> self, Size<float?> context, CalcResolver calc) =>
        new(
            self.Left.ResolveOrZero(context.Width, calc),
            self.Right.ResolveOrZero(context.Width, calc),
            self.Top.ResolveOrZero(context.Height, calc),
            self.Bottom.ResolveOrZero(context.Height, calc));

    /// <summary>Converts any parent-relative values for a rect into an absolute rect.</summary>
    public static Rect<float> ResolveOrZero(
        this Rect<LengthPercentageAuto> self,
        Size<float?> context,
        CalcResolver calc) =>
        new(
            self.Left.ResolveOrZero(context.Width, calc),
            self.Right.ResolveOrZero(context.Width, calc),
            self.Top.ResolveOrZero(context.Height, calc),
            self.Bottom.ResolveOrZero(context.Height, calc));

    /// <summary>Converts any parent-relative values for a rect into an absolute rect.</summary>
    public static Rect<float> ResolveOrZero(this Rect<Dimension> self, Size<float?> context, CalcResolver calc) =>
        new(
            self.Left.ResolveOrZero(context.Width, calc),
            self.Right.ResolveOrZero(context.Width, calc),
            self.Top.ResolveOrZero(context.Height, calc),
            self.Bottom.ResolveOrZero(context.Height, calc));

    /// <summary>Converts any parent-relative values for a rect into an absolute rect.</summary>
    public static Rect<float> ResolveOrZero(this Rect<LengthPercentage> self, float? context, CalcResolver calc) =>
        new(
            self.Left.ResolveOrZero(context, calc),
            self.Right.ResolveOrZero(context, calc),
            self.Top.ResolveOrZero(context, calc),
            self.Bottom.ResolveOrZero(context, calc));

    /// <summary>Converts any parent-relative values for a rect into an absolute rect.</summary>
    public static Rect<float> ResolveOrZero(this Rect<LengthPercentageAuto> self, float? context, CalcResolver calc) =>
        new(
            self.Left.ResolveOrZero(context, calc),
            self.Right.ResolveOrZero(context, calc),
            self.Top.ResolveOrZero(context, calc),
            self.Bottom.ResolveOrZero(context, calc));

    /// <summary>Converts any parent-relative values for a rect into an absolute rect.</summary>
    public static Rect<float> ResolveOrZero(this Rect<Dimension> self, float? context, CalcResolver calc) =>
        new(
            self.Left.ResolveOrZero(context, calc),
            self.Right.ResolveOrZero(context, calc),
            self.Top.ResolveOrZero(context, calc),
            self.Bottom.ResolveOrZero(context, calc));
}
