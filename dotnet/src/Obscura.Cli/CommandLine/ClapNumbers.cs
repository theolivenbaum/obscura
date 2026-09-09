using System.Globalization;
using System.CommandLine.Parsing;

namespace Obscura.Cli.CommandLine;

/// <summary>
/// Numeric option parsers that fail the way clap's do.
/// </summary>
/// <remarks>
/// <para>
/// System.CommandLine defers a failed conversion to the point where the value
/// is read, so a bad <c>--timeout abc</c> escaped the parse-error check in
/// <c>Program.cs</c> and surfaced as an unhandled
/// <c>InvalidOperationException</c> with a stack trace, where clap prints a
/// usage error and exits 2. Parsing in the option itself turns those into
/// ordinary parse errors.
/// </para>
/// <para>
/// The messages are clap's, because they are on stderr and part of the CLI's
/// user-facing contract. clap renders
/// <c>invalid value '&lt;raw&gt;' for '&lt;--flag &lt;PLACEHOLDER&gt;&gt;': &lt;detail&gt;</c>,
/// where the detail comes from Rust's own integer parser
/// (<c>invalid digit found in string</c>,
/// <c>number too large to fit in target type</c>), from
/// <c>NonZero*</c> (<c>number would be zero for non-zero type</c>), or from
/// clap's range validator (<c>&lt;v&gt; is not in &lt;lo&gt;..&lt;hi&gt;</c>).
/// A token starting with a dash never reaches clap's parser at all, because
/// these options do not set <c>allow_hyphen_values</c>; it is an unknown flag.
/// </para>
/// </remarks>
internal static class ClapNumbers
{
    private const string InvalidDigit = "invalid digit found in string";
    private const string TooLarge = "number too large to fit in target type";
    private const string WouldBeZero = "number would be zero for non-zero type";

    /// <summary>
    /// clap's <c>u16</c> parser, used by every <c>--port</c> and by
    /// <c>--workers</c>.
    /// </summary>
    /// <remarks>
    /// clap reports a u16 overflow as a range error, not as
    /// <c>number too large to fit in target type</c>, and renders the implicit
    /// bound inclusively: <c>99999 is not in 0..=65535</c>. An explicit
    /// <c>.range(1..)</c> is a RangeFrom and renders without the <c>=</c>, which
    /// is why <see cref="U64Range"/> spells its bound differently.
    /// </remarks>
    public static Func<ArgumentResult, int> U16(string flag, string placeholder) =>
        result => (int)Parse(
            result, flag, placeholder, ushort.MaxValue, minimum: 0,
            rangeHigh: ushort.MaxValue, rangeHighInclusive: true);

    /// <summary>
    /// clap's <c>NonZeroUsize</c> parser, used by <c>--concurrency</c>.
    /// </summary>
    /// <remarks>
    /// A zero reports <c>NonZeroUsize</c>'s own message rather than a range
    /// error, because the type refuses the value after the digits parse.
    /// </remarks>
    public static Func<ArgumentResult, int> NonZeroUSize(string flag, string placeholder) =>
        result =>
        {
            var value = Parse(
                result, flag, placeholder, int.MaxValue, minimum: 0, zeroDetail: WouldBeZero);
            // clap's usize accepts the whole 64-bit range; a concurrency that
            // large is not reachable and the semaphore takes an int, so it is
            // clamped rather than rejected with a message clap never prints.
            return (int)value;
        };

    /// <summary>clap's <c>usize</c> parser, used by <c>--max-connections</c>.</summary>
    public static Func<ArgumentResult, int> USize(string flag, string placeholder) =>
        result =>
        {
            var value = Parse(result, flag, placeholder, int.MaxValue, minimum: 0);
            return (int)value;
        };

    /// <summary>
    /// clap's <c>u64</c> parser with <c>value_parser!(u64).range(lo..)</c>, used
    /// by <c>--timeout</c>.
    /// </summary>
    public static Func<ArgumentResult, long> U64Range(string flag, string placeholder, ulong low) =>
        result => Parse(result, flag, placeholder, long.MaxValue, minimum: low, rangeHigh: ulong.MaxValue);

    /// <summary>
    /// clap's plain <c>u64</c> parser for an optional value, used by
    /// <c>--wait</c>.
    /// </summary>
    /// <remarks>
    /// Null when the flag was not passed, which is what distinguishes the
    /// adaptive settle cap from a fixed delay: <c>--wait 0</c> is a real 0.
    /// </remarks>
    public static Func<ArgumentResult, long?> U64(string flag, string placeholder) =>
        result => result.Tokens.Count == 0
            ? null
            : Parse(result, flag, placeholder, long.MaxValue, minimum: 0);

    /// <summary>
    /// Parse one token, reporting clap's message and returning 0 on failure.
    /// </summary>
    /// <remarks>
    /// The return value is unused once an error is added: the parse fails and
    /// <c>Program.cs</c> exits 2 before any command reads it.
    /// </remarks>
    private static long Parse(
        ArgumentResult result,
        string flag,
        string placeholder,
        long ceiling,
        ulong minimum,
        string? zeroDetail = null,
        ulong? rangeHigh = null,
        bool rangeHighInclusive = false)
    {
        if (result.Tokens.Count == 0)
        {
            return 0;
        }
        var raw = result.Tokens[0].Value;

        // clap never hands a dash-leading token to a value parser for an option
        // without allow_hyphen_values; it is an unknown flag instead.
        if (raw.StartsWith('-'))
        {
            result.AddError($"unexpected argument '{raw}' found");
            return 0;
        }

        if (!ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            // Rust's from_str reports the two cases separately: an all-digit
            // token that does not fit is "too large", anything else is a bad
            // digit. NumberStyles.None already rejects signs and whitespace.
            var allDigits = raw.Length > 0;
            foreach (var c in raw)
            {
                if (c is < '0' or > '9')
                {
                    allDigits = false;
                    break;
                }
            }
            result.AddError(Invalid(raw, flag, placeholder, allDigits ? TooLarge : InvalidDigit));
            return 0;
        }

        if (parsed == 0 && zeroDetail is not null)
        {
            result.AddError(Invalid(raw, flag, placeholder, zeroDetail));
            return 0;
        }

        if (parsed < minimum || (rangeHigh is { } high && parsed > high))
        {
            result.AddError(Invalid(
                raw,
                flag,
                placeholder,
                $"{parsed.ToString(CultureInfo.InvariantCulture)} is not in "
                + $"{minimum.ToString(CultureInfo.InvariantCulture)}.."
                + (rangeHighInclusive ? "=" : string.Empty)
                + (rangeHigh ?? ulong.MaxValue).ToString(CultureInfo.InvariantCulture)));
            return 0;
        }

        if (parsed > (ulong)ceiling)
        {
            // clap's u64/usize accept the whole 64-bit range, so a value above
            // the port's narrower option type saturates rather than reporting an
            // error the reference never prints. A u16 never reaches this: its
            // range check above has already rejected anything over 65535.
            return ceiling;
        }

        return (long)parsed;
    }

    private static string Invalid(string raw, string flag, string placeholder, string detail) =>
        $"invalid value '{raw}' for '{flag} <{placeholder}>': {detail}";
}
