using System.Globalization;
using System.Text;

namespace PocketCalculator.Render;

/// <summary>
/// The bidirectional support the inline layer needs: paragraph direction, embedding levels,
/// and visual reordering of level runs.
/// </summary>
/// <remarks>
/// PORT NOTE. The Rust engine uses the <c>unicode-bidi</c> crate, a full UAX#9 implementation.
/// This is a reduced resolver: paragraph level from CSS <c>direction</c> (HL1) or else the first
/// strong character (P2/P3), strong L/R/AL to their own levels, European and Arabic numbers one
/// level above an RTL context (W2/W7 approximated), neutrals by N1/N2, the L1 whitespace reset,
/// and the L2 reordering. It does <em>not</em> implement explicit embedding controls
/// (RLE/LRE/PDF) or isolates (LRI/RLI/FSI/PDI).
/// Purely left-to-right text - which takes an explicit fast path here - is exact; mixed-
/// direction paragraphs can order differently from the Rust engine, and that is the first
/// thing to check if bidi text drifts.
/// </remarks>
internal static class Bidi
{
    private enum Strong
    {
        Neutral,
        LeftToRight,
        RightToLeft,
        ArabicLetter,
        EuropeanNumber,
        ArabicNumber,
        Whitespace,
        Separator,
    }

    /// <summary>Level runs over one paragraph, plus whether the paragraph is right-to-left.</summary>
    /// <summary>
    /// Whether <see cref="LevelRuns"/> would leave its all-LTR fast path for any line cut from
    /// <paramref name="text"/>: some character classifies as right-to-left.
    /// </summary>
    internal static bool AnyRightToLeft(string text)
    {
        int position = 0;
        while (position < text.Length)
        {
            Rune.DecodeFromUtf16(text.AsSpan(position), out Rune rune, out int consumed);
            if (Classify(rune.Value) is Strong.RightToLeft or Strong.ArabicLetter)
            {
                return true;
            }

            position += consumed;
        }

        return false;
    }

    /// <param name="baseRtl">
    /// The paragraph level from CSS <c>direction</c> (HL1), or <c>null</c> for the first strong
    /// character's (P2/P3).
    /// </param>
    public static List<(int Start, int End, byte Level)> LevelRuns(string line, out bool rtl, bool? baseRtl = null)
    {
        rtl = baseRtl == true;
        if (line.Length == 0)
        {
            return [(0, 0, (byte)(rtl ? 1 : 0))];
        }

        var classes = new Strong[line.Length];
        bool anyRtl = false;
        int position = 0;
        while (position < line.Length)
        {
            Rune.DecodeFromUtf16(line.AsSpan(position), out Rune rune, out int consumed);
            Strong strong = Classify(rune.Value);
            for (int i = 0; i < consumed; i++)
            {
                classes[position + i] = strong;
            }

            if (strong is Strong.RightToLeft or Strong.ArabicLetter)
            {
                anyRtl = true;
            }

            position += consumed;
        }

        if (!anyRtl && baseRtl != true)
        {
            // Fast path: no right-to-left character anywhere, so the whole line is one LTR run.
            return [(0, line.Length, 0)];
        }

        // DEVIATION from crates/obscura-render/src/inline.rs (cosmic-text), whose paragraph
        // level always comes from the first strong character. HTML's dir and CSS direction set
        // it (UAX#9 HL1, CSS Writing Modes 2.4): Chromium 141 starts a dir=rtl paragraph of
        // Latin text at the right, and an LTR paragraph that opens with Hebrew at the left.
        byte paragraph = (byte)(baseRtl == true ? 1 : 0);
        foreach (Strong strong in classes)
        {
            if (baseRtl is not null)
            {
                break;
            }

            if (strong == Strong.LeftToRight)
            {
                paragraph = 0;
                break;
            }

            if (strong is Strong.RightToLeft or Strong.ArabicLetter)
            {
                paragraph = 1;
                break;
            }
        }

        rtl = paragraph == 1;
        var levels = new byte[line.Length];
        for (int i = 0; i < line.Length; i++)
        {
            levels[i] = classes[i] switch
            {
                Strong.LeftToRight => (byte)(paragraph == 0 ? 0 : 2),
                Strong.RightToLeft or Strong.ArabicLetter => 1,
                Strong.EuropeanNumber or Strong.ArabicNumber => (byte)(paragraph == 0 ? 0 : 2),
                _ => paragraph,
            };
        }

        ResolveNeutrals(classes, levels, paragraph);

        // L1: reset trailing whitespace and separators to the paragraph level.
        int? resetFrom = 0;
        int? resetTo = null;
        for (int i = 0; i < line.Length; i++)
        {
            switch (classes[i])
            {
                case Strong.Separator:
                    resetTo = i + 1;
                    resetFrom ??= i;
                    break;
                case Strong.Whitespace:
                    resetFrom ??= i;
                    break;
                default:
                    resetFrom = null;
                    break;
            }

            if (resetFrom is { } from && resetTo is { } to)
            {
                for (int j = from; j < to; j++)
                {
                    levels[j] = paragraph;
                }

                resetFrom = null;
                resetTo = null;
            }
        }

        if (resetFrom is { } tail)
        {
            for (int j = tail; j < line.Length; j++)
            {
                levels[j] = paragraph;
            }
        }

        var runs = new List<(int Start, int End, byte Level)>();
        int start = 0;
        byte runLevel = levels[0];
        for (int i = 1; i < line.Length; i++)
        {
            if (levels[i] != runLevel)
            {
                runs.Add((start, i, runLevel));
                start = i;
                runLevel = levels[i];
            }
        }

        runs.Add((start, line.Length, runLevel));
        return runs;
    }

    /// <summary>
    /// UAX#9 N1/N2: a run of neutrals between strong text of one direction takes that
    /// direction (numbers count as right to left, except where this resolver already treats
    /// them as left to right: in a left-to-right paragraph); any other run takes the paragraph's.
    /// </summary>
    /// <remarks>
    /// Without it every neutral sat at the paragraph level, so the space between two words of
    /// the other direction split them into two runs that reordering then put in logical order:
    /// "hello world" in a dir=rtl paragraph read "world hello", which Chromium 141 does not.
    /// </remarks>
    private static void ResolveNeutrals(Strong[] classes, byte[] levels, byte paragraph)
    {
        bool paragraphRtl = paragraph == 1;
        int i = 0;
        while (i < classes.Length)
        {
            if (!IsNeutral(classes[i]))
            {
                i++;
                continue;
            }

            int end = i;
            while (end < classes.Length && IsNeutral(classes[end]))
            {
                end++;
            }

            bool before = i == 0 ? paragraphRtl : IsRtlForNeutrals(classes[i - 1], paragraphRtl);
            bool after = end == classes.Length ? paragraphRtl : IsRtlForNeutrals(classes[end], paragraphRtl);
            if (before == after && before != paragraphRtl)
            {
                // Left to right inside a right-to-left paragraph sits at 2, right to left
                // inside a left-to-right one at 1.
                byte level = (byte)(paragraphRtl ? 2 : 1);
                for (int j = i; j < end; j++)
                {
                    levels[j] = level;
                }
            }

            i = end;
        }
    }

    private static bool IsNeutral(Strong strong) => strong is Strong.Neutral or Strong.Whitespace or Strong.Separator;

    private static bool IsRtlForNeutrals(Strong strong, bool paragraphRtl) => strong switch
    {
        Strong.RightToLeft or Strong.ArabicLetter or Strong.ArabicNumber => true,
        Strong.EuropeanNumber => paragraphRtl,
        _ => false,
    };

    /// <summary>UAX#9 L2 reordering of the level runs that make up one visual line.</summary>
    public static List<(int Start, int End)> Reorder(
        ShapeLine shape,
        List<(int SpanIndex, (int Word, int Glyph) Start, (int Word, int Glyph) End)> ranges)
    {
        var line = new byte[ranges.Count];
        for (int i = 0; i < ranges.Count; i++)
        {
            line[i] = shape.Spans[ranges[i].SpanIndex].Level;
        }

        var runs = new List<(int Start, int End)>();
        if (line.Length == 0)
        {
            return runs;
        }

        int start = 0;
        byte runLevel = line[0];
        byte minLevel = runLevel;
        byte maxLevel = runLevel;
        for (int i = 1; i < line.Length; i++)
        {
            if (line[i] != runLevel)
            {
                runs.Add((start, i));
                start = i;
                runLevel = line[i];
                minLevel = Math.Min(runLevel, minLevel);
                maxLevel = Math.Max(runLevel, maxLevel);
            }
        }

        runs.Add((start, line.Length));

        // Stop at the lowest odd level.
        int lowestOdd = (minLevel & 1) != 0 ? minLevel : minLevel + 1;
        int level = maxLevel;
        while (level >= lowestOdd)
        {
            int sequenceStart = 0;
            while (sequenceStart < runs.Count)
            {
                if (line[runs[sequenceStart].Start] < level)
                {
                    sequenceStart++;
                    continue;
                }

                int sequenceEnd = sequenceStart + 1;
                while (sequenceEnd < runs.Count && line[runs[sequenceEnd].Start] >= level)
                {
                    sequenceEnd++;
                }

                runs.Reverse(sequenceStart, sequenceEnd - sequenceStart);
                sequenceStart = sequenceEnd;
            }

            level--;
        }

        return runs;
    }

    private static Strong Classify(int ch)
    {
        if (ch is 0x000A or 0x000D or 0x001C or 0x001D or 0x001E or 0x0085 or 0x2029)
        {
            return Strong.Separator;
        }

        if (ch is 0x0009 or 0x000B or 0x001F)
        {
            return Strong.Separator;
        }

        if (ch is >= 0x0590 and <= 0x05FF or >= 0x07C0 and <= 0x089F or >= 0xFB1D and <= 0xFB4F)
        {
            return Strong.RightToLeft;
        }

        if (ch is >= 0x0600 and <= 0x07BF or >= 0x08A0 and <= 0x08FF
            or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF)
        {
            return Strong.ArabicLetter;
        }

        if (ch is >= 0x0660 and <= 0x0669 or >= 0x06F0 and <= 0x06F9)
        {
            return Strong.ArabicNumber;
        }

        if (!Rune.IsValid(ch))
        {
            return Strong.Neutral;
        }

        return CharUnicodeInfo.GetUnicodeCategory(ch) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter => Strong.LeftToRight,
            UnicodeCategory.DecimalDigitNumber => Strong.EuropeanNumber,
            UnicodeCategory.SpaceSeparator => Strong.Whitespace,
            _ => Strong.Neutral,
        };
    }
}
