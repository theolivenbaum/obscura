using System.Globalization;
using System.Text;

namespace Obscura.Render;

/// <summary>UAX#14 line-break classes, as far as the engine distinguishes them.</summary>
public enum BreakClass
{
    Mandatory,          // BK
    CarriageReturn,     // CR
    LineFeed,           // LF
    CombiningMark,      // CM
    NextLine,           // NL
    Surrogate,          // SG
    WordJoiner,         // WJ
    ZeroWidth,          // ZW
    Glue,               // GL
    Space,              // SP
    ZeroWidthJoiner,    // ZWJ
    BreakBoth,          // B2
    BreakAfter,         // BA
    BreakBefore,        // BB
    Hyphen,             // HY
    ContingentBreak,    // CB
    ClosePunctuation,   // CL
    CloseParenthesis,   // CP
    Exclamation,        // EX
    Inseparable,        // IN
    NonStarter,         // NS
    OpenPunctuation,    // OP
    Quotation,          // QU
    InfixSeparator,     // IS
    Numeric,            // NU
    Postfix,            // PO
    Prefix,             // PR
    Symbol,             // SY
    Ambiguous,          // AI
    Alphabetic,         // AL
    ConditionalJapaneseStarter, // CJ
    EmojiBase,          // EB
    EmojiModifier,      // EM
    HangulLvSyllable,   // H2
    HangulLvtSyllable,  // H3
    HebrewLetter,       // HL
    Ideographic,        // ID
    HangulLJamo,        // JL
    HangulVJamo,        // JV
    HangulTJamo,        // JT
    RegionalIndicator,  // RI
    ComplexContext,     // SA
    Unknown,            // XX
}

/// <summary>
/// The line-breaking opportunities the inline layer needs.
/// </summary>
/// <remarks>
/// PORT NOTE. The Rust engine gets its ordinary opportunities from the <c>unicode-linebreak</c>
/// crate, a complete UAX#14 pair-table implementation, and then applies the Blink tailorings in
/// <c>css_break_data</c>. .NET ships no line breaker and the dependency set is closed, so this
/// is a hand-written UAX#14 <em>subset</em>: it implements LB2-LB8a, LB9-LB12a, LB13-LB19,
/// LB21-LB25, LB26-LB28, LB30a and LB30b over a range-derived class table. It is exact for
/// Latin, Cyrillic, Greek, numeric, and punctuation text, for CJK ideographs, for Hangul, and
/// for spaces, hyphens, and emoji sequences. It does not implement the LB25 numeric
/// regex-expansion introduced in Unicode 15.1, the LB20a "hyphen after a space" tailoring, or
/// Southeast Asian (SA) dictionary breaking. Those cases can produce a break where the Rust
/// engine does not, and are the first thing to look at if a page's wrapping drifts.
/// The Blink tailorings (<c>blink_normal_break</c>, <c>break_all_pair</c>,
/// <c>keep_all_word_class</c>) are ported exactly.
/// </remarks>
public static class LineBreaking
{
    /// <summary>Offsets (exclusive ends) where UAX#14 permits a line break.</summary>
    public static List<int> Breaks(string text)
    {
        List<int> result = [];
        if (text.Length == 0)
        {
            return result;
        }

        // Class and end offset of every grapheme cluster; breaks land on cluster boundaries.
        List<(int End, BreakClass Class, bool IsSpace)> clusters = [];
        foreach ((int start, int length) in GraphemeClusters(text))
        {
            clusters.Add((start + length, ClusterClass(text, start, length), IsSpaceCluster(text, start)));
        }

        for (int i = 0; i + 1 < clusters.Count; i++)
        {
            if (CanBreakBetween(clusters, i))
            {
                result.Add(clusters[i].End);
            }
        }

        result.Add(text.Length);
        return result;
    }

    private static bool IsSpaceCluster(string text, int start) => text[start] == ' ';

    /// <summary>
    /// Whether a break is allowed after cluster <paramref name="index"/>.
    /// </summary>
    private static bool CanBreakBetween(List<(int End, BreakClass Class, bool IsSpace)> clusters, int index)
    {
        BreakClass before = clusters[index].Class;
        BreakClass after = clusters[index + 1].Class;

        // LB4/LB5: mandatory breaks. Buffer lines are already split on newlines, but a lone
        // control can still appear inside a preformatted run.
        if (before is BreakClass.Mandatory or BreakClass.LineFeed or BreakClass.NextLine)
        {
            return true;
        }

        if (before == BreakClass.CarriageReturn)
        {
            return after != BreakClass.LineFeed;
        }

        // LB6: never break before a hard break.
        if (after is BreakClass.Mandatory or BreakClass.CarriageReturn or BreakClass.LineFeed
            or BreakClass.NextLine)
        {
            return false;
        }

        // LB7: do not break before spaces or zero width space.
        if (after is BreakClass.Space or BreakClass.ZeroWidth)
        {
            return false;
        }

        // LB8: break after a zero width space, even when spaces follow.
        if (before == BreakClass.ZeroWidth)
        {
            return true;
        }

        // LB8a: do not break after a zero width joiner.
        if (before == BreakClass.ZeroWidthJoiner)
        {
            return false;
        }

        // LB9: combining marks attach to the preceding character.
        if (after is BreakClass.CombiningMark or BreakClass.ZeroWidthJoiner)
        {
            return false;
        }

        // LB11: never break around a word joiner.
        if (before == BreakClass.WordJoiner || after == BreakClass.WordJoiner)
        {
            return false;
        }

        // LB12/LB12a: do not break after glue, nor before it except after a space or another
        // break opportunity class.
        if (before == BreakClass.Glue)
        {
            return false;
        }

        if (after == BreakClass.Glue
            && before is not (BreakClass.Space or BreakClass.BreakAfter or BreakClass.Hyphen))
        {
            return false;
        }

        // LB13: do not break before closing punctuation, exclamation, separators, or symbols.
        if (after is BreakClass.ClosePunctuation or BreakClass.CloseParenthesis
            or BreakClass.Exclamation or BreakClass.InfixSeparator or BreakClass.Symbol)
        {
            return false;
        }

        // Walk back over a run of spaces so the LB14-LB17 contexts see the real character.
        int anchor = index;
        bool sawSpace = false;
        while (anchor >= 0 && clusters[anchor].Class == BreakClass.Space)
        {
            sawSpace = true;
            anchor--;
        }

        BreakClass anchorClass = anchor >= 0 ? clusters[anchor].Class : BreakClass.Unknown;

        // LB14: do not break after opening punctuation, even across spaces.
        if (anchorClass == BreakClass.OpenPunctuation)
        {
            return false;
        }

        // LB15: do not break within quotation followed by opening punctuation.
        if (anchorClass == BreakClass.Quotation && after == BreakClass.OpenPunctuation)
        {
            return false;
        }

        // LB16: do not break between closing punctuation and a non-starter.
        if (anchorClass is BreakClass.ClosePunctuation or BreakClass.CloseParenthesis
            && after is BreakClass.NonStarter or BreakClass.ConditionalJapaneseStarter)
        {
            return false;
        }

        // LB17: do not break within an em dash pair.
        if (anchorClass == BreakClass.BreakBoth && after == BreakClass.BreakBoth)
        {
            return false;
        }

        // LB18: break after spaces.
        if (before == BreakClass.Space || sawSpace)
        {
            return true;
        }

        // LB19: do not break before or after a quotation mark.
        if (before == BreakClass.Quotation || after == BreakClass.Quotation)
        {
            return false;
        }

        // LB21: do not break before a break-after class or a hyphen, nor after a break-before.
        if (after is BreakClass.BreakAfter or BreakClass.Hyphen or BreakClass.NonStarter
            or BreakClass.ConditionalJapaneseStarter)
        {
            return false;
        }

        if (before == BreakClass.BreakBefore)
        {
            return false;
        }

        // LB21b: do not break between a Hebrew letter and a following hyphen's letter.
        if (before == BreakClass.Symbol && after == BreakClass.HebrewLetter)
        {
            return false;
        }

        // LB22: do not break before an inseparable.
        if (after == BreakClass.Inseparable)
        {
            return false;
        }

        // LB23/LB23a: keep letters with digits, and prefixes/postfixes with ideographs.
        if (before is BreakClass.Alphabetic or BreakClass.HebrewLetter or BreakClass.Ambiguous
            && after == BreakClass.Numeric)
        {
            return false;
        }

        if (before == BreakClass.Numeric
            && after is BreakClass.Alphabetic or BreakClass.HebrewLetter or BreakClass.Ambiguous)
        {
            return false;
        }

        if (before == BreakClass.Prefix
            && after is BreakClass.Ideographic or BreakClass.EmojiBase or BreakClass.EmojiModifier)
        {
            return false;
        }

        if (before is BreakClass.Ideographic or BreakClass.EmojiBase or BreakClass.EmojiModifier
            && after == BreakClass.Postfix)
        {
            return false;
        }

        // LB24: keep prefix and postfix with letters.
        if (before is BreakClass.Prefix or BreakClass.Postfix
            && after is BreakClass.Alphabetic or BreakClass.HebrewLetter or BreakClass.Ambiguous)
        {
            return false;
        }

        if (before is BreakClass.Alphabetic or BreakClass.HebrewLetter or BreakClass.Ambiguous
            && after is BreakClass.Prefix or BreakClass.Postfix)
        {
            return false;
        }

        // LB25: do not break inside a numeric expression.
        if (IsNumericContext(before) && IsNumericContext(after))
        {
            return false;
        }

        // LB26/LB27: Hangul syllables and their jamo.
        if (before == BreakClass.HangulLJamo
            && after is BreakClass.HangulLJamo or BreakClass.HangulVJamo
                or BreakClass.HangulLvSyllable or BreakClass.HangulLvtSyllable)
        {
            return false;
        }

        if (before is BreakClass.HangulVJamo or BreakClass.HangulLvSyllable
            && after is BreakClass.HangulVJamo or BreakClass.HangulTJamo)
        {
            return false;
        }

        if (before is BreakClass.HangulTJamo or BreakClass.HangulLvtSyllable
            && after == BreakClass.HangulTJamo)
        {
            return false;
        }

        if (IsHangul(before) && after is BreakClass.Inseparable or BreakClass.Postfix)
        {
            return false;
        }

        if (before == BreakClass.Prefix && IsHangul(after))
        {
            return false;
        }

        // LB28: do not break between alphabetics.
        if (before is BreakClass.Alphabetic or BreakClass.HebrewLetter or BreakClass.Ambiguous
                or BreakClass.ComplexContext
            && after is BreakClass.Alphabetic or BreakClass.HebrewLetter or BreakClass.Ambiguous
                or BreakClass.ComplexContext)
        {
            return false;
        }

        // LB29: do not break between a numeric separator and a letter.
        if (before == BreakClass.InfixSeparator
            && after is BreakClass.Alphabetic or BreakClass.HebrewLetter or BreakClass.Ambiguous
                or BreakClass.Numeric)
        {
            return false;
        }

        // LB30: do not break between letters/numerals and opening or closing brackets.
        if (before is BreakClass.Alphabetic or BreakClass.HebrewLetter or BreakClass.Numeric
                or BreakClass.Ambiguous
            && after == BreakClass.OpenPunctuation)
        {
            return false;
        }

        if (before == BreakClass.CloseParenthesis
            && after is BreakClass.Alphabetic or BreakClass.HebrewLetter or BreakClass.Numeric
                or BreakClass.Ambiguous)
        {
            return false;
        }

        // LB30a: do not break between paired regional indicators.
        if (before == BreakClass.RegionalIndicator && after == BreakClass.RegionalIndicator)
        {
            int run = 0;
            for (int i = index; i >= 0 && clusters[i].Class == BreakClass.RegionalIndicator; i--)
            {
                run++;
            }

            if (run % 2 == 1)
            {
                return false;
            }
        }

        // LB30b: do not break between an emoji base and its modifier.
        if (before == BreakClass.EmojiBase && after == BreakClass.EmojiModifier)
        {
            return false;
        }

        // LB31: break everywhere else.
        return true;
    }

    private static bool IsNumericContext(BreakClass value) => value is BreakClass.Numeric
        or BreakClass.Symbol or BreakClass.InfixSeparator or BreakClass.Prefix
        or BreakClass.Postfix or BreakClass.Hyphen or BreakClass.ClosePunctuation
        or BreakClass.CloseParenthesis;

    private static bool IsHangul(BreakClass value) => value is BreakClass.HangulLJamo
        or BreakClass.HangulVJamo or BreakClass.HangulTJamo or BreakClass.HangulLvSyllable
        or BreakClass.HangulLvtSyllable;

    /// <summary>Extended grapheme clusters, as UAX#29 defines them.</summary>
    public static IEnumerable<(int Start, int Length)> GraphemeClusters(string text)
    {
        int position = 0;
        while (position < text.Length)
        {
            int length = StringInfo.GetNextTextElementLength(text.AsSpan(position));
            if (length <= 0)
            {
                length = 1;
            }

            yield return (position, length);
            position += length;
        }
    }

    /// <summary>
    /// The class of a grapheme cluster: its first character whose class is neither a combining
    /// mark nor a zero-width joiner.
    /// </summary>
    public static BreakClass ClusterClass(string text, int start, int length)
    {
        int position = start;
        int end = start + length;
        while (position < end)
        {
            Rune.DecodeFromUtf16(text.AsSpan(position, end - position), out Rune rune, out int consumed);
            BreakClass value = ClassOf(rune.Value);
            if (value is not (BreakClass.CombiningMark or BreakClass.ZeroWidthJoiner))
            {
                return value;
            }

            position += consumed;
        }

        return BreakClass.CombiningMark;
    }

    /// <summary>
    /// CSS <c>break-all</c> adds opportunities between typographic letter units, with Blink's
    /// tailoring of PLUS SIGN into the alphabetic class.
    /// </summary>
    public static BreakClass BreakAllClass(string text, int start, int length) =>
        length == 1 && text[start] == '+' ? BreakClass.Alphabetic : ClusterClass(text, start, length);

    /// <summary>
    /// Letter/number-like classes governed by <c>word-break</c>'s "within words" rules.
    /// Complex-context scripts deliberately stay out of keep-all, matching Blink's exemption
    /// for Southeast Asian dictionary breaking.
    /// </summary>
    public static bool KeepAllWordClass(BreakClass value) => value is BreakClass.Alphabetic
        or BreakClass.Ambiguous or BreakClass.HebrewLetter or BreakClass.Numeric
        or BreakClass.Ideographic or BreakClass.ConditionalJapaneseStarter
        or BreakClass.HangulLvSyllable or BreakClass.HangulLvtSyllable or BreakClass.HangulLJamo
        or BreakClass.HangulVJamo or BreakClass.HangulTJamo;

    /// <summary>UAX#14 class of one scalar value.</summary>
    public static BreakClass ClassOf(int ch) => ch switch
    {
        0x000A => BreakClass.LineFeed,
        0x000D => BreakClass.CarriageReturn,
        0x000B or 0x000C or 0x1C or 0x1D or 0x1E or 0x85 or 0x2028 or 0x2029 => BreakClass.Mandatory,
        0x0009 => BreakClass.BreakAfter,
        0x0020 => BreakClass.Space,
        0x00A0 or 0x202F or 0x2007 or 0x2011 or 0x0F0C => BreakClass.Glue,
        0x2060 or 0xFEFF => BreakClass.WordJoiner,
        0x200B => BreakClass.ZeroWidth,
        0x200D => BreakClass.ZeroWidthJoiner,
        0x2014 => BreakClass.BreakBoth,
        0x002D => BreakClass.Hyphen,
        0x002F => BreakClass.Symbol,
        0x002C or 0x002E or 0x003A or 0x003B or 0x055D or 0x060C or 0x060D or 0x07F8
            or 0x2044 or 0xFE10 or 0xFE13 or 0xFE14 => BreakClass.InfixSeparator,
        0x0021 or 0x003F or 0x2762 or 0x2763 or 0xFE15 or 0xFE16 or 0xFF01 or 0xFF1F
            => BreakClass.Exclamation,
        0x0024 or 0x002B or 0x005C or 0x00B1 or 0x2116 or 0x00A3 or 0x00A5 or 0x20AC
            or 0x0023 => BreakClass.Prefix,
        0x0025 or 0x00A2 or 0x00B0 or 0x2030 or 0x2031 or 0x2032 or 0x2033 or 0x2034
            => BreakClass.Postfix,
        0x0028 or 0x005B or 0x007B or 0x2985 or 0x3008 or 0x300A or 0x300C or 0x300E
            or 0x3010 or 0x3014 or 0x3016 or 0x3018 or 0x301A or 0xFF08 or 0xFF3B or 0xFF5B
            => BreakClass.OpenPunctuation,
        0x0029 or 0x005D => BreakClass.CloseParenthesis,
        0x007D or 0x2986 or 0x3009 or 0x300B or 0x300D or 0x300F or 0x3011 or 0x3015
            or 0x3017 or 0x3019 or 0x301B or 0xFF09 or 0xFF3D or 0xFF5D or 0x3001 or 0x3002
            or 0xFF0C or 0xFF0E or 0xFF61 or 0xFF64 => BreakClass.ClosePunctuation,
        0x0022 or 0x0027 or 0x00AB or 0x00BB or 0x2018 or 0x2019 or 0x201C or 0x201D
            => BreakClass.Quotation,
        0x2010 or 0x2012 or 0x2013 or 0x058A or 0x05BE or 0x0F0B or 0x1361 or 0x17D8
            or 0x17DA or 0x2027 or 0x007C => BreakClass.BreakAfter,
        0x00B4 or 0x02C8 or 0x02CC or 0x1806 => BreakClass.BreakBefore,
        0x2024 or 0x2025 or 0x2026 or 0xFE19 => BreakClass.Inseparable,
        0x3005 or 0x303B or 0x309D or 0x309E or 0x30FD or 0x30FE or 0x203C or 0x2047
            or 0x2048 or 0x2049 => BreakClass.NonStarter,
        0x3041 or 0x3043 or 0x3045 or 0x3047 or 0x3049 or 0x3063 or 0x3083 or 0x3085
            or 0x3087 or 0x308E or 0x3095 or 0x3096 or 0x30A1 or 0x30A3 or 0x30A5 or 0x30A7
            or 0x30A9 or 0x30C3 or 0x30E3 or 0x30E5 or 0x30E7 or 0x30EE or 0x30F5 or 0x30F6
            or 0x30FC => BreakClass.ConditionalJapaneseStarter,
        >= 0x0030 and <= 0x0039 => BreakClass.Numeric,
        >= 0x0041 and <= 0x005A => BreakClass.Alphabetic,
        >= 0x0061 and <= 0x007A => BreakClass.Alphabetic,
        >= 0x0300 and <= 0x036F => BreakClass.CombiningMark,
        >= 0x0483 and <= 0x0489 => BreakClass.CombiningMark,
        >= 0x0591 and <= 0x05BD => BreakClass.CombiningMark,
        >= 0x0610 and <= 0x061A => BreakClass.CombiningMark,
        >= 0x064B and <= 0x065F => BreakClass.CombiningMark,
        >= 0x0E31 and <= 0x0E3A => BreakClass.ComplexContext,
        >= 0x0E40 and <= 0x0E4E => BreakClass.ComplexContext,
        >= 0x1780 and <= 0x17FF => BreakClass.ComplexContext,
        >= 0x0E00 and <= 0x0E7F => BreakClass.ComplexContext,
        >= 0x0590 and <= 0x05F4 => BreakClass.HebrewLetter,
        >= 0x1100 and <= 0x115F => BreakClass.HangulLJamo,
        >= 0x1160 and <= 0x11A7 => BreakClass.HangulVJamo,
        >= 0x11A8 and <= 0x11FF => BreakClass.HangulTJamo,
        >= 0xA960 and <= 0xA97C => BreakClass.HangulLJamo,
        >= 0xD7B0 and <= 0xD7C6 => BreakClass.HangulVJamo,
        >= 0xD7CB and <= 0xD7FB => BreakClass.HangulTJamo,
        >= 0xAC00 and <= 0xD7A3 => (ch - 0xAC00) % 28 == 0
            ? BreakClass.HangulLvSyllable
            : BreakClass.HangulLvtSyllable,
        >= 0x3040 and <= 0x30FF => BreakClass.Ideographic,
        >= 0x3400 and <= 0x4DBF => BreakClass.Ideographic,
        >= 0x4E00 and <= 0x9FFF => BreakClass.Ideographic,
        >= 0xF900 and <= 0xFAFF => BreakClass.Ideographic,
        >= 0xFF66 and <= 0xFF9F => BreakClass.Ideographic,
        >= 0x20000 and <= 0x3FFFD => BreakClass.Ideographic,
        >= 0x1F1E6 and <= 0x1F1FF => BreakClass.RegionalIndicator,
        >= 0x1F3FB and <= 0x1F3FF => BreakClass.EmojiModifier,
        >= 0x1F300 and <= 0x1FAFF => BreakClass.EmojiBase,
        >= 0x2600 and <= 0x27BF => BreakClass.Ideographic,
        >= 0xFE00 and <= 0xFE0F => BreakClass.CombiningMark,
        >= 0x1F000 and <= 0x1F0FF => BreakClass.Ideographic,
        >= 0xE0100 and <= 0xE01EF => BreakClass.CombiningMark,
        _ => ClassFromCategory(ch),
    };

    /// <summary>
    /// Fall back to the general category for anything the explicit ranges above do not name.
    /// </summary>
    private static BreakClass ClassFromCategory(int ch)
    {
        if (!Rune.IsValid(ch))
        {
            return BreakClass.Unknown;
        }

        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark)
        {
            return BreakClass.CombiningMark;
        }

        // Remaining ASCII is punctuation the ranges above already cover the interesting parts
        // of; treating the rest as alphabetic keeps `a@b` and similar from acquiring a break.
        if (ch < 0x0080)
        {
            return BreakClass.Alphabetic;
        }

        return category switch
        {
            UnicodeCategory.SpaceSeparator => BreakClass.BreakAfter,
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber
                or UnicodeCategory.OtherNumber => BreakClass.Numeric,
            UnicodeCategory.OpenPunctuation => BreakClass.OpenPunctuation,
            UnicodeCategory.ClosePunctuation => BreakClass.ClosePunctuation,
            UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
                => BreakClass.Quotation,
            UnicodeCategory.DashPunctuation => BreakClass.BreakAfter,
            UnicodeCategory.Format or UnicodeCategory.Control => BreakClass.CombiningMark,
            _ => BreakClass.Alphabetic,
        };
    }
}
