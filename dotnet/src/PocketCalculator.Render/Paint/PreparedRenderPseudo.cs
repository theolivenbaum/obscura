using PocketCalculator.Dom;
using PocketCalculator.Render.Layout;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render;

// PORT NOTE. crates/obscura-js exposes op_computed_style over a node id alone, so its
// getComputedStyle() ignores the pseudoElt argument entirely and answers every call with the
// originating element's style. The cascade already computes ::before, ::after, ::placeholder and
// ::-webkit-slider-thumb (CssCascade.AllPseudoStyles), so the data was there and only unexposed.
// This is a deliberate C# deviation measured against Chromium and recorded in todo.md.
public sealed partial class PreparedRender
{
    /// <summary>
    /// Pseudo-elements the cascade does not compute, but which are real CSS pseudo-elements and
    /// so must answer with a style rather than an empty declaration. Chromium answers an
    /// unmatched pseudo with the initial style plus what it inherits from its originating
    /// element, which is what <see cref="UnmatchedPseudoStyle"/> builds.
    /// </summary>
    private static readonly HashSet<string> RecognisedUncomputedPseudoElements = new(StringComparer.Ordinal)
    {
        "first-line", "first-letter", "marker", "selection", "backdrop", "file-selector-button",
        "target-text", "spelling-error", "grammar-error", "cue", "cue-region", "details-content",
        "-webkit-scrollbar", "-webkit-scrollbar-thumb", "-webkit-scrollbar-track",
        "-webkit-scrollbar-track-piece", "-webkit-scrollbar-corner", "-webkit-scrollbar-button",
        "-webkit-resizer", "-webkit-slider-runnable-track", "-webkit-progress-bar",
        "-webkit-progress-value", "-webkit-progress-inner-element", "-webkit-meter-bar",
        "-webkit-meter-inner-element", "-webkit-file-upload-button",
        "-webkit-search-cancel-button", "-webkit-search-decoration",
        "-webkit-inner-spin-button", "-webkit-outer-spin-button",
        "-webkit-calendar-picker-indicator", "-webkit-details-marker",
        "-webkit-textfield-decoration-container", "-webkit-color-swatch",
        "-webkit-color-swatch-wrapper", "-webkit-media-controls",
    };

    /// <summary>
    /// The style and used box of one pseudo-element of <paramref name="id"/>, or null when the
    /// name is not a pseudo-element at all.
    /// </summary>
    private (LayoutStyle Style, Rect? Rect, bool GeneratesContent)? ResolvePseudoElement(
        NodeId id,
        LayoutStyle host,
        string pseudoElement)
    {
        string name = NormalisePseudoElementName(pseudoElement);
        if (name.Length == 0)
        {
            return null;
        }

        switch (name)
        {
            case "before":
                return (host.BeforePseudo ?? UnmatchedPseudoStyle(host),
                    GeneratedBoxRect(id, GeneratedBoxKind.Before), true);
            case "after":
                return (host.AfterPseudo ?? UnmatchedPseudoStyle(host),
                    GeneratedBoxRect(id, GeneratedBoxKind.After), true);

            // A placeholder and a slider thumb are laid out inside their native control rather
            // than as boxes of their own, so neither has a rect to report.
            case "placeholder":
            case "-webkit-input-placeholder":
                return (host.PlaceholderPseudo ?? UnmatchedPseudoStyle(host), null, false);
            case "-webkit-slider-thumb":
                return (host.SliderThumbPseudo ?? UnmatchedPseudoStyle(host), null, false);
        }

        return RecognisedUncomputedPseudoElements.Contains(name)
            ? (UnmatchedPseudoStyle(host), null, false)
            : null;
    }

    /// <summary>
    /// Lowercases the name and drops its leading colons. One colon is the legacy spelling of
    /// <c>::before</c> and friends, and getComputedStyle accepts it.
    /// </summary>
    private static string NormalisePseudoElementName(string pseudoElement)
    {
        // No trimming: Chromium parses pseudoElt as a selector, and a padded one is not one.
        ReadOnlySpan<char> span = pseudoElement.AsSpan();
        if (span.StartsWith("::", StringComparison.Ordinal))
        {
            span = span[2..];
        }
        else if (span.Length != 0 && span[0] == ':')
        {
            span = span[1..];
        }

        return span.ToString().ToLowerInvariant();
    }

    /// <summary>The used box of one generated pseudo box, when it produced one.</summary>
    private Rect? GeneratedBoxRect(NodeId id, GeneratedBoxKind kind)
    {
        foreach (GeneratedBox generated in Layout.GeneratedBoxes)
        {
            if (generated.Kind == kind && generated.Host.Equals(id))
            {
                return generated.Rect;
            }
        }

        return null;
    }

    /// <summary>
    /// The style of a pseudo-element no rule matched: every property at its initial value,
    /// except the inherited ones, which come from the originating element.
    /// </summary>
    /// <remarks>
    /// The inherited set is the one <c>LayoutDomComputed</c>'s <c>Settle</c> applies to a
    /// matched ::before/::after, so a matched and an unmatched pseudo inherit the same things.
    /// <c>Display</c> starts at <c>Inline</c> for the same reason it does in
    /// <c>CssCascade.BuildPseudo</c>: LayoutStyle defaults to block because it mostly describes
    /// ordinary DOM boxes, and a pseudo-element's initial display is inline.
    /// </remarks>
    private static LayoutStyle UnmatchedPseudoStyle(LayoutStyle host)
    {
        var style = new LayoutStyle { Display = Display.Inline };
        style.ColorSchemeDark = host.ColorSchemeDark;
        style.FontSize = host.FontSize;
        style.FontWeight = host.FontWeight;
        style.FontFamily = host.FontFamily;
        style.FontFamilySpecified = host.FontFamilySpecified;
        style.FontStyleItalic = host.FontStyleItalic;
        style.FontOpticalSizing = host.FontOpticalSizing;
        style.FontVariationSettings = host.FontVariationSettings is null
            ? null
            : [.. host.FontVariationSettings];
        style.LineHeight = host.LineHeight;
        style.LetterSpacing = host.LetterSpacing;
        style.LetterSpacingNonNormal = host.LetterSpacingNonNormal;
        style.Color = host.Color;
        style.Cursor = host.Cursor;
        style.PointerEvents = host.PointerEvents;
        style.WhiteSpace = host.WhiteSpace;
        style.OverflowWrap = host.OverflowWrap;
        style.WordBreak = host.WordBreak;
        style.TextWrapStyle = host.TextWrapStyle;
        style.TextTransform = host.TextTransform;
        style.TextAlign = host.TextAlign;
        style.TextIndent = host.TextIndent;
        style.EffectivelyInvisible = host.EffectivelyInvisible;
        return style;
    }
    /// <summary>
    /// The computed <c>content</c> of one pseudo-element, the way Chromium reports it. The typed
    /// items are preferred because they still carry <c>counter()</c> unresolved, which is what a
    /// computed value reports; the literal string is what a text-only pseudo keeps instead.
    /// </summary>
    /// <param name="generatesContent">
    /// True for ::before and ::after, the two pseudo-elements on which CSS Content 3 computes an
    /// absent <c>content</c> to <c>none</c> rather than leaving it <c>normal</c>.
    /// </param>
    private static string PseudoContentCss(LayoutStyle style, bool generatesContent)
    {
        if (style.GeneratedContent is { Count: > 0 } items)
        {
            return GeneratedContentCss(items);
        }

        if (style.ContentImage is { Length: > 0 } image)
        {
            var url = new System.Text.StringBuilder("url(");
            AppendCssString(url, image);
            return url.Append(')').ToString();
        }

        if (style.BeforeContent is { Length: > 0 } text)
        {
            var literal = new System.Text.StringBuilder();
            AppendCssString(literal, text);
            return literal.ToString();
        }

        return generatesContent ? "none" : "normal";
    }

    /// <summary>Serializes a computed <c>content</c> value the way Chromium reports it.</summary>
    private static string GeneratedContentCss(IReadOnlyList<GeneratedContentItem> items)
    {
        var sb = new System.Text.StringBuilder();
        for (int index = 0; index < items.Count; index++)
        {
            if (index != 0)
            {
                sb.Append(' ');
            }

            switch (items[index])
            {
                case GeneratedContentItem.Text text:
                    AppendCssString(sb, text.Value);
                    break;
                case GeneratedContentItem.Counter counter:
                    sb.Append("counter(").Append(counter.Name);
                    AppendCounterStyle(sb, counter.Style);
                    sb.Append(')');
                    break;
                case GeneratedContentItem.Counters counters:
                    sb.Append("counters(").Append(counters.Name).Append(", ");
                    AppendCssString(sb, counters.Separator);
                    AppendCounterStyle(sb, counters.Style);
                    sb.Append(')');
                    break;
            }
        }

        return sb.ToString();
    }

    private static void AppendCounterStyle(System.Text.StringBuilder sb, GeneratedCounterStyle style)
    {
        // Chromium omits the style when it is the initial `decimal`.
        string? keyword = style switch
        {
            GeneratedCounterStyle.DecimalLeadingZero => "decimal-leading-zero",
            GeneratedCounterStyle.LowerAlpha => "lower-alpha",
            GeneratedCounterStyle.UpperAlpha => "upper-alpha",
            GeneratedCounterStyle.LowerRoman => "lower-roman",
            GeneratedCounterStyle.UpperRoman => "upper-roman",
            _ => null,
        };

        if (keyword is not null)
        {
            sb.Append(", ").Append(keyword);
        }
    }

    private static void AppendCssString(System.Text.StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\A "); break;
                default: sb.Append(c); break;
            }
        }

        sb.Append('"');
    }
}

