// Port of vendor/taffy/src/style/compact_length.rs (64-bit platform variant).
namespace Obscura.Render.Layout;

/// <summary>
/// A representation of a length as a compact 64-bit tagged pointer.
/// </summary>
/// <remarks>
/// This is a faithful port of taffy's 64-bit <c>CompactLengthInner</c>: the low 8 bits carry the
/// tag, the high 32 bits carry the raw IEEE-754 bits of the f32 payload, and a <c>calc()</c> value
/// stores an opaque handle whose low 3 bits must be zero. It is <b>not</b> NaN boxing, so the port
/// needs no special NaN handling: the f32 payload round-trips bit-for-bit through
/// <see cref="BitConverter.SingleToUInt32Bits(float)"/>.
/// </remarks>
public readonly record struct CompactLength
{
    /// <summary>The tag indicating a calc() value.</summary>
    public const ulong CalcTag = 0b000;

    /// <summary>The tag indicating a length value.</summary>
    public const ulong LengthTag = 0b0000_0001;

    /// <summary>The tag indicating a percentage value.</summary>
    public const ulong PercentTag = 0b0000_0010;

    /// <summary>The tag indicating an auto value.</summary>
    public const ulong AutoTag = 0b0000_0011;

    /// <summary>The tag indicating an fr value.</summary>
    public const ulong FrTag = 0b0000_0100;

    /// <summary>The tag indicating a min-content value.</summary>
    public const ulong MinContentTag = 0b0000_0111;

    /// <summary>The tag indicating a max-content value.</summary>
    public const ulong MaxContentTag = 0b0000_1111;

    /// <summary>The tag indicating a fit-content value with px limit.</summary>
    public const ulong FitContentPxTag = 0b0001_0111;

    /// <summary>The tag indicating a fit-content value with percent limit.</summary>
    public const ulong FitContentPercentTag = 0b0001_1111;

    /// <summary>The low byte (8 bits).</summary>
    private const ulong TagMask = 0b1111_1111;

    /// <summary>The low 3 bits.</summary>
    private const ulong CalcTagMask = 0b111;

    private readonly ulong _taggedPtr;

    private CompactLength(ulong taggedPtr) => _taggedPtr = taggedPtr;

    private static CompactLength FromVal(float val, ulong tag) =>
        new(((ulong)BitConverter.SingleToUInt32Bits(val) << 32) | tag);

    private static CompactLength FromTag(ulong tag) => new(tag);

    /// <summary>Zero pixels.</summary>
    public static CompactLength Zero => Length(0.0f);

    /// <summary>The auto value.</summary>
    public static CompactLength Auto() => FromTag(AutoTag);

    /// <summary>An absolute length in some abstract units.</summary>
    public static CompactLength Length(float val) => FromVal(val, LengthTag);

    /// <summary>
    /// A percentage length relative to the size of the containing block.
    /// Percentages are in the range [0.0, 1.0], not [0.0, 100.0].
    /// </summary>
    public static CompactLength Percent(float val) => FromVal(val, PercentTag);

    /// <summary>
    /// A <c>calc()</c> value. The handle is opaque to layout; the low 3 bits are used as a tag and
    /// must be zero.
    /// </summary>
    public static CompactLength Calc(nuint ptr)
    {
        if (ptr == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ptr), "calc() handle must be non-null");
        }

        if (((ulong)ptr & 0b111) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ptr), "calc() handle must be 8-byte aligned");
        }

        return new CompactLength((ulong)ptr | CalcTag);
    }

    /// <summary>The dimension as a fraction of the total available grid space (<c>fr</c> in CSS).</summary>
    public static CompactLength Fr(float val) => FromVal(val, FrTag);

    /// <summary>The "min-content" size.</summary>
    public static CompactLength MinContent() => FromTag(MinContentTag);

    /// <summary>The "max-content" size.</summary>
    public static CompactLength MaxContent() => FromTag(MaxContentTag);

    /// <summary>A <c>fit-content()</c> value with a px limit.</summary>
    public static CompactLength FitContentPx(float limit) => FromVal(limit, FitContentPxTag);

    /// <summary>A <c>fit-content()</c> value with a percentage limit.</summary>
    public static CompactLength FitContentPercent(float limit) => FromVal(limit, FitContentPercentTag);

    /// <summary>Get the primary tag.</summary>
    public ulong Tag => _taggedPtr & TagMask;

    /// <summary>Get the numeric value associated with the <see cref="CompactLength"/>.</summary>
    public float Value => BitConverter.UInt32BitsToSingle((uint)(_taggedPtr >> 32));

    /// <summary>Get the calc handle of the <see cref="CompactLength"/>.</summary>
    public nuint CalcValue => (nuint)_taggedPtr;

    /// <summary>The raw packed bits. Exposed for serialization and debugging only.</summary>
    public ulong RawBits => _taggedPtr;

    /// <summary>Construct from raw packed bits.</summary>
    public static CompactLength FromRawBits(ulong bits) => new(bits);

    /// <summary>Returns true if the value is a calc() value.</summary>
    public bool IsCalc => (_taggedPtr & CalcTagMask) == 0;

    /// <summary>Returns true if the value is 0 px.</summary>
    public bool IsZero => _taggedPtr == Zero._taggedPtr;

    /// <summary>Returns true if the value is a length or percentage value.</summary>
    public bool IsLengthOrPercentage => Tag is LengthTag or PercentTag;

    /// <summary>Returns true if the value is auto.</summary>
    public bool IsAuto => Tag == AutoTag;

    /// <summary>Returns true if the value is min-content.</summary>
    public bool IsMinContent => Tag == MinContentTag;

    /// <summary>Returns true if the value is max-content.</summary>
    public bool IsMaxContent => Tag == MaxContentTag;

    /// <summary>Returns true if the value is a fit-content(...) value.</summary>
    public bool IsFitContent => Tag is FitContentPxTag or FitContentPercentTag;

    /// <summary>Returns true if the value is max-content or a fit-content(...) value.</summary>
    public bool IsMaxOrFitContent => Tag is MaxContentTag or FitContentPxTag or FitContentPercentTag;

    /// <summary>
    /// Returns true if the max track sizing function is MaxContent, FitContent or Auto.
    /// </summary>
    public bool IsMaxContentAlike => Tag is AutoTag or MaxContentTag or FitContentPxTag or FitContentPercentTag;

    /// <summary>Returns true if the min track sizing function is MinContent or MaxContent.</summary>
    public bool IsMinOrMaxContent => Tag is MinContentTag or MaxContentTag;

    /// <summary>Returns true if the value is auto, min-content, max-content, or fit-content(...).</summary>
    public bool IsIntrinsic =>
        Tag is AutoTag or MinContentTag or MaxContentTag or FitContentPxTag or FitContentPercentTag;

    /// <summary>Returns true if the value is an fr value.</summary>
    public bool IsFr => Tag == FrTag;

    /// <summary>Whether the track sizing function depends on the size of the parent node.</summary>
    public bool UsesPercentage => Tag is PercentTag or FitContentPercentTag || IsCalc;

    /// <summary>
    /// Resolve percentage values against the passed parent size, returning the resolved value.
    /// Non-percentage values always return <c>null</c>.
    /// </summary>
    public float? ResolvedPercentageSize(float parentSize, CalcResolver calcResolver)
    {
        if (Tag == PercentTag)
        {
            return Value * parentSize;
        }

        if (IsCalc)
        {
            return calcResolver(CalcValue, parentSize);
        }

        return null;
    }

    /// <inheritdoc/>
    public override string ToString() => Tag switch
    {
        LengthTag => $"{Value}px",
        PercentTag => $"{Value * 100f}%",
        AutoTag => "auto",
        FrTag => $"{Value}fr",
        MinContentTag => "min-content",
        MaxContentTag => "max-content",
        FitContentPxTag => $"fit-content({Value}px)",
        FitContentPercentTag => $"fit-content({Value * 100f}%)",
        _ => $"calc(0x{CalcValue:x})",
    };
}

/// <summary>
/// Resolves an opaque <c>calc()</c> handle against its percentage basis.
/// </summary>
/// <param name="handle">The opaque handle stored in a <see cref="CompactLength"/>.</param>
/// <param name="basis">The percentage basis.</param>
public delegate float CalcResolver(nuint handle, float basis);
