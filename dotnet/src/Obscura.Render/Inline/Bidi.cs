using System.Globalization;
using System.Text;

namespace Obscura.Render;

/// <summary>
/// The bidirectional support the inline layer needs: paragraph direction, embedding levels,
/// and visual reordering of level runs.
/// </summary>
/// <remarks>
/// PORT NOTE. The Rust engine uses the <c>unicode-bidi</c> crate, a full UAX#9 implementation.
/// This is a reduced resolver: paragraph level from the first strong character (P2/P3), strong
/// L/R/AL to their own levels, European and Arabic numbers one level above an RTL context
/// (W2/W7 approximated), neutrals to the paragraph level, the L1 whitespace reset, and the L2
/// reordering. It does <em>not</em> implement explicit embedding controls (RLE/LRE/PDF),
/// isolates (LRI/RLI/FSI/PDI), or the N1/N2 neutral resolution from surrounding strong types.
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
    public static List<(int Start, int End, byte Level)> LevelRuns(string line, out bool rtl)
    {
        rtl = false;
        if (line.Length == 0)
        {
            return [(0, 0, 0)];
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

        if (!anyRtl)
        {
            // Fast path: no right-to-left character anywhere, so the whole line is one LTR run.
            return [(0, line.Length, 0)];
        }

        byte paragraph = 0;
        foreach (Strong strong in classes)
        {
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
