// Port of vendor/taffy/src/style/dimension.rs
namespace Obscura.Render.Layout;

/// <summary>A unit of linear measurement: a length or a percentage.</summary>
public readonly record struct LengthPercentage
{
    private readonly CompactLength _inner;

    private LengthPercentage(CompactLength inner) => _inner = inner;

    /// <summary>Zero pixels.</summary>
    public static LengthPercentage Zero => new(CompactLength.Zero);

    /// <summary>An absolute length in some abstract units.</summary>
    public static LengthPercentage FromLength(float val) => new(CompactLength.Length(val));

    /// <summary>A percentage length relative to the size of the containing block ([0.0, 1.0]).</summary>
    public static LengthPercentage FromPercent(float val) => new(CompactLength.Percent(val));

    /// <summary>A <c>calc()</c> value.</summary>
    public static LengthPercentage FromCalc(nuint ptr) => new(CompactLength.Calc(ptr));

    /// <summary>
    /// Create a <see cref="LengthPercentage"/> from a raw <see cref="CompactLength"/>. The caller
    /// must ensure the CompactLength represents a valid variant.
    /// </summary>
    public static LengthPercentage FromRaw(CompactLength val) => new(val);

    /// <summary>Get the underlying <see cref="CompactLength"/> representation of the value.</summary>
    public CompactLength IntoRaw() => _inner;

    /// <inheritdoc/>
    public override string ToString() => _inner.ToString();
}

/// <summary>A unit of linear measurement: a length, a percentage or auto.</summary>
public readonly record struct LengthPercentageAuto
{
    private readonly CompactLength _inner;

    private LengthPercentageAuto(CompactLength inner) => _inner = inner;

    /// <summary>Zero pixels.</summary>
    public static LengthPercentageAuto Zero => new(CompactLength.Zero);

    /// <summary>The auto value.</summary>
    public static LengthPercentageAuto Auto => new(CompactLength.Auto());

    /// <summary>An absolute length in some abstract units.</summary>
    public static LengthPercentageAuto FromLength(float val) => new(CompactLength.Length(val));

    /// <summary>A percentage length relative to the size of the containing block ([0.0, 1.0]).</summary>
    public static LengthPercentageAuto FromPercent(float val) => new(CompactLength.Percent(val));

    /// <summary>A <c>calc()</c> value.</summary>
    public static LengthPercentageAuto FromCalc(nuint ptr) => new(CompactLength.Calc(ptr));

    /// <summary>Widen a <see cref="LengthPercentage"/>.</summary>
    public static LengthPercentageAuto From(LengthPercentage input) => new(input.IntoRaw());

    /// <summary>Create from a raw <see cref="CompactLength"/>.</summary>
    public static LengthPercentageAuto FromRaw(CompactLength val) => new(val);

    /// <summary>Get the underlying <see cref="CompactLength"/> representation of the value.</summary>
    public CompactLength IntoRaw() => _inner;

    /// <summary>Returns true if the value is auto.</summary>
    public bool IsAuto => _inner.IsAuto;

    /// <summary>
    /// Returns the length for Length variants, the resolved value for Percent variants and
    /// <c>null</c> for Auto variants.
    /// </summary>
    public float? ResolveToOption(float context, CalcResolver calcResolver)
    {
        ulong tag = _inner.Tag;
        if (tag == CompactLength.LengthTag)
        {
            return _inner.Value;
        }

        if (tag == CompactLength.PercentTag)
        {
            return context * _inner.Value;
        }

        if (tag == CompactLength.AutoTag)
        {
            return null;
        }

        if (_inner.IsCalc)
        {
            return calcResolver(_inner.CalcValue, context);
        }

        throw new InvalidOperationException(
            "LengthPercentageAuto values cannot be constructed with other tags");
    }

    /// <inheritdoc/>
    public override string ToString() => _inner.ToString();
}

/// <summary>A unit of linear measurement used for sizes: a length, a percentage or auto.</summary>
public readonly record struct Dimension
{
    private readonly CompactLength _inner;

    private Dimension(CompactLength inner) => _inner = inner;

    /// <summary>Zero pixels.</summary>
    public static Dimension Zero => new(CompactLength.Zero);

    /// <summary>The auto value.</summary>
    public static Dimension Auto => new(CompactLength.Auto());

    /// <summary>An absolute length in some abstract units.</summary>
    public static Dimension FromLength(float val) => new(CompactLength.Length(val));

    /// <summary>A percentage length relative to the size of the containing block ([0.0, 1.0]).</summary>
    public static Dimension FromPercent(float val) => new(CompactLength.Percent(val));

    /// <summary>A <c>calc()</c> value.</summary>
    public static Dimension FromCalc(nuint ptr) => new(CompactLength.Calc(ptr));

    /// <summary>Widen a <see cref="LengthPercentage"/>.</summary>
    public static Dimension From(LengthPercentage input) => new(input.IntoRaw());

    /// <summary>Widen a <see cref="LengthPercentageAuto"/>.</summary>
    public static Dimension From(LengthPercentageAuto input) => new(input.IntoRaw());

    /// <summary>Create from a raw <see cref="CompactLength"/>.</summary>
    public static Dimension FromRaw(CompactLength val) => new(val);

    /// <summary>Get the underlying <see cref="CompactLength"/> representation of the value.</summary>
    public CompactLength IntoRaw() => _inner;

    /// <summary>Get the length value if the value is a Length variant.</summary>
    public float? IntoOption() => _inner.Tag == CompactLength.LengthTag ? _inner.Value : null;

    /// <summary>Returns true if the value is auto.</summary>
    public bool IsAuto => _inner.IsAuto;

    /// <summary>Get the raw <see cref="CompactLength"/> tag.</summary>
    public ulong Tag => _inner.Tag;

    /// <summary>Get the raw numeric parameter for non-calc variants.</summary>
    public float Value => _inner.Value;

    /// <inheritdoc/>
    public override string ToString() => _inner.ToString();
}
