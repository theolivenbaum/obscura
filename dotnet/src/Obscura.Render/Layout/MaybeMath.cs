// Port of vendor/taffy/src/util/math.rs
namespace Obscura.Render.Layout;

/// <summary>
/// Conveniently calculate minimums and maximums when some data may not be defined.
/// </summary>
/// <remarks>
/// If the left-hand value is null, these operations return null. If the right-hand value is null,
/// it is treated as an identity (the left value is returned unchanged).
/// </remarks>
public static class MaybeMath
{
    // ------------------------------------------------- lhs float?, rhs float?

    /// <summary>Returns the minimum of the two values.</summary>
    public static float? MaybeMin(this float? self, float? rhs) =>
        self.HasValue && rhs.HasValue ? Sys.F32Min(self.Value, rhs.Value) : self.HasValue ? self : null;

    /// <summary>Returns the maximum of the two values.</summary>
    public static float? MaybeMax(this float? self, float? rhs) =>
        self.HasValue && rhs.HasValue ? Sys.F32Max(self.Value, rhs.Value) : self.HasValue ? self : null;

    /// <summary>Returns the value clamped between min and max.</summary>
    public static float? MaybeClamp(this float? self, float? min, float? max)
    {
        if (!self.HasValue)
        {
            return null;
        }

        float value = self.Value;
        if (max.HasValue)
        {
            value = Sys.F32Min(value, max.Value);
        }

        if (min.HasValue)
        {
            value = Sys.F32Max(value, min.Value);
        }

        return value;
    }

    /// <summary>Adds the two values.</summary>
    public static float? MaybeAdd(this float? self, float? rhs) =>
        self.HasValue && rhs.HasValue ? self.Value + rhs.Value : self.HasValue ? self : null;

    /// <summary>Subtracts rhs from self.</summary>
    public static float? MaybeSub(this float? self, float? rhs) =>
        self.HasValue && rhs.HasValue ? self.Value - rhs.Value : self.HasValue ? self : null;

    // ------------------------------------------------- lhs float?, rhs float

    /// <summary>Returns the minimum of the two values.</summary>
    public static float? MaybeMin(this float? self, float rhs) =>
        self.HasValue ? Sys.F32Min(self.Value, rhs) : null;

    /// <summary>Returns the maximum of the two values.</summary>
    public static float? MaybeMax(this float? self, float rhs) =>
        self.HasValue ? Sys.F32Max(self.Value, rhs) : null;

    /// <summary>Returns the value clamped between min and max.</summary>
    public static float? MaybeClamp(this float? self, float min, float max) =>
        self.HasValue ? Sys.F32Max(Sys.F32Min(self.Value, max), min) : null;

    /// <summary>Adds the two values.</summary>
    public static float? MaybeAdd(this float? self, float rhs) => self.HasValue ? self.Value + rhs : null;

    /// <summary>Subtracts rhs from self.</summary>
    public static float? MaybeSub(this float? self, float rhs) => self.HasValue ? self.Value - rhs : null;

    // ------------------------------------------------- lhs float, rhs float?

    /// <summary>Returns the minimum of the two values.</summary>
    public static float MaybeMin(this float self, float? rhs) => rhs.HasValue ? Sys.F32Min(self, rhs.Value) : self;

    /// <summary>Returns the maximum of the two values.</summary>
    public static float MaybeMax(this float self, float? rhs) => rhs.HasValue ? Sys.F32Max(self, rhs.Value) : self;

    /// <summary>Returns the value clamped between min and max.</summary>
    public static float MaybeClamp(this float self, float? min, float? max)
    {
        float value = self;
        if (max.HasValue)
        {
            value = Sys.F32Min(value, max.Value);
        }

        if (min.HasValue)
        {
            value = Sys.F32Max(value, min.Value);
        }

        return value;
    }

    /// <summary>Adds the two values.</summary>
    public static float MaybeAdd(this float self, float? rhs) => rhs.HasValue ? self + rhs.Value : self;

    /// <summary>Subtracts rhs from self.</summary>
    public static float MaybeSub(this float self, float? rhs) => rhs.HasValue ? self - rhs.Value : self;

    // ------------------------------------------- lhs AvailableSpace, rhs float

    /// <summary>Returns the minimum of the two values.</summary>
    public static AvailableSpace MaybeMin(this AvailableSpace self, float rhs) => self.Kind switch
    {
        AvailableSpaceKind.Definite => AvailableSpace.Definite(Sys.F32Min(self.Unwrap(), rhs)),
        _ => AvailableSpace.Definite(rhs),
    };

    /// <summary>Returns the maximum of the two values.</summary>
    public static AvailableSpace MaybeMax(this AvailableSpace self, float rhs) => self.Kind switch
    {
        AvailableSpaceKind.Definite => AvailableSpace.Definite(Sys.F32Max(self.Unwrap(), rhs)),
        _ => self,
    };

    /// <summary>Returns the value clamped between min and max.</summary>
    public static AvailableSpace MaybeClamp(this AvailableSpace self, float min, float max) => self.Kind switch
    {
        AvailableSpaceKind.Definite => AvailableSpace.Definite(Sys.F32Max(Sys.F32Min(self.Unwrap(), max), min)),
        _ => self,
    };

    /// <summary>Adds the two values.</summary>
    public static AvailableSpace MaybeAdd(this AvailableSpace self, float rhs) => self.Kind switch
    {
        AvailableSpaceKind.Definite => AvailableSpace.Definite(self.Unwrap() + rhs),
        _ => self,
    };

    /// <summary>Subtracts rhs from self.</summary>
    public static AvailableSpace MaybeSub(this AvailableSpace self, float rhs) => self.Kind switch
    {
        AvailableSpaceKind.Definite => AvailableSpace.Definite(self.Unwrap() - rhs),
        _ => self,
    };

    // ------------------------------------------ lhs AvailableSpace, rhs float?

    /// <summary>Returns the minimum of the two values.</summary>
    public static AvailableSpace MaybeMin(this AvailableSpace self, float? rhs)
    {
        if (self.Kind == AvailableSpaceKind.Definite)
        {
            return rhs.HasValue ? AvailableSpace.Definite(Sys.F32Min(self.Unwrap(), rhs.Value)) : self;
        }

        return rhs.HasValue ? AvailableSpace.Definite(rhs.Value) : self;
    }

    /// <summary>Returns the maximum of the two values.</summary>
    public static AvailableSpace MaybeMax(this AvailableSpace self, float? rhs)
    {
        if (self.Kind == AvailableSpaceKind.Definite && rhs.HasValue)
        {
            return AvailableSpace.Definite(Sys.F32Max(self.Unwrap(), rhs.Value));
        }

        return self;
    }

    /// <summary>Returns the value clamped between min and max.</summary>
    public static AvailableSpace MaybeClamp(this AvailableSpace self, float? min, float? max)
    {
        if (self.Kind != AvailableSpaceKind.Definite)
        {
            return self;
        }

        float value = self.Unwrap();
        if (max.HasValue)
        {
            value = Sys.F32Min(value, max.Value);
        }

        if (min.HasValue)
        {
            value = Sys.F32Max(value, min.Value);
        }

        return AvailableSpace.Definite(value);
    }

    /// <summary>Adds the two values.</summary>
    public static AvailableSpace MaybeAdd(this AvailableSpace self, float? rhs) =>
        self.Kind == AvailableSpaceKind.Definite && rhs.HasValue
            ? AvailableSpace.Definite(self.Unwrap() + rhs.Value)
            : self;

    /// <summary>Subtracts rhs from self.</summary>
    public static AvailableSpace MaybeSub(this AvailableSpace self, float? rhs) =>
        self.Kind == AvailableSpaceKind.Definite && rhs.HasValue
            ? AvailableSpace.Definite(self.Unwrap() - rhs.Value)
            : self;

    // ---------------------------------------------------------- Size variants

    /// <summary>Component-wise <see cref="MaybeMin(float?, float?)"/>.</summary>
    public static Size<float?> MaybeMin(this Size<float?> self, Size<float?> rhs) =>
        new(self.Width.MaybeMin(rhs.Width), self.Height.MaybeMin(rhs.Height));

    /// <summary>Component-wise <see cref="MaybeMax(float?, float?)"/>.</summary>
    public static Size<float?> MaybeMax(this Size<float?> self, Size<float?> rhs) =>
        new(self.Width.MaybeMax(rhs.Width), self.Height.MaybeMax(rhs.Height));

    /// <summary>Component-wise <see cref="MaybeClamp(float?, float?, float?)"/>.</summary>
    public static Size<float?> MaybeClamp(this Size<float?> self, Size<float?> min, Size<float?> max) =>
        new(self.Width.MaybeClamp(min.Width, max.Width), self.Height.MaybeClamp(min.Height, max.Height));

    /// <summary>Component-wise <see cref="MaybeAdd(float?, float?)"/>.</summary>
    public static Size<float?> MaybeAdd(this Size<float?> self, Size<float?> rhs) =>
        new(self.Width.MaybeAdd(rhs.Width), self.Height.MaybeAdd(rhs.Height));

    /// <summary>Component-wise <see cref="MaybeSub(float?, float?)"/>.</summary>
    public static Size<float?> MaybeSub(this Size<float?> self, Size<float?> rhs) =>
        new(self.Width.MaybeSub(rhs.Width), self.Height.MaybeSub(rhs.Height));

    /// <summary>Component-wise <see cref="MaybeMin(float?, float)"/>.</summary>
    public static Size<float?> MaybeMin(this Size<float?> self, Size<float> rhs) =>
        new(self.Width.MaybeMin(rhs.Width), self.Height.MaybeMin(rhs.Height));

    /// <summary>Component-wise <see cref="MaybeMax(float?, float)"/>.</summary>
    public static Size<float?> MaybeMax(this Size<float?> self, Size<float> rhs) =>
        new(self.Width.MaybeMax(rhs.Width), self.Height.MaybeMax(rhs.Height));

    /// <summary>Component-wise <see cref="MaybeClamp(float?, float, float)"/>.</summary>
    public static Size<float?> MaybeClamp(this Size<float?> self, Size<float> min, Size<float> max) =>
        new(self.Width.MaybeClamp(min.Width, max.Width), self.Height.MaybeClamp(min.Height, max.Height));

    /// <summary>Component-wise <see cref="MaybeAdd(float?, float)"/>.</summary>
    public static Size<float?> MaybeAdd(this Size<float?> self, Size<float> rhs) =>
        new(self.Width.MaybeAdd(rhs.Width), self.Height.MaybeAdd(rhs.Height));

    /// <summary>Component-wise <see cref="MaybeSub(float?, float)"/>.</summary>
    public static Size<float?> MaybeSub(this Size<float?> self, Size<float> rhs) =>
        new(self.Width.MaybeSub(rhs.Width), self.Height.MaybeSub(rhs.Height));

    /// <summary>Component-wise <see cref="MaybeMin(float, float?)"/>.</summary>
    public static Size<float> MaybeMin(this Size<float> self, Size<float?> rhs) =>
        new(self.Width.MaybeMin(rhs.Width), self.Height.MaybeMin(rhs.Height));

    /// <summary>Component-wise <see cref="MaybeMax(float, float?)"/>.</summary>
    public static Size<float> MaybeMax(this Size<float> self, Size<float?> rhs) =>
        new(self.Width.MaybeMax(rhs.Width), self.Height.MaybeMax(rhs.Height));

    /// <summary>Component-wise <see cref="MaybeClamp(float, float?, float?)"/>.</summary>
    public static Size<float> MaybeClamp(this Size<float> self, Size<float?> min, Size<float?> max) =>
        new(self.Width.MaybeClamp(min.Width, max.Width), self.Height.MaybeClamp(min.Height, max.Height));

    /// <summary>Component-wise <see cref="MaybeAdd(float, float?)"/>.</summary>
    public static Size<float> MaybeAdd(this Size<float> self, Size<float?> rhs) =>
        new(self.Width.MaybeAdd(rhs.Width), self.Height.MaybeAdd(rhs.Height));

    /// <summary>Component-wise <see cref="MaybeSub(float, float?)"/>.</summary>
    public static Size<float> MaybeSub(this Size<float> self, Size<float?> rhs) =>
        new(self.Width.MaybeSub(rhs.Width), self.Height.MaybeSub(rhs.Height));
}
