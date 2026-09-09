using System.Text;

namespace Obscura.Render.Css;

/// <summary>
/// Coarse <c>@media</c> evaluation against the live layout viewport.
/// </summary>
/// <remarks>
/// Real stylesheets format media features inconsistently
/// (<c>max-width:750px</c>, <c>max-width: 750px</c>, even
/// <c>max-width : 750px</c>), so this strips whitespace before scanning: CSS
/// gives no semantic meaning to spaces inside <c>(feature: value)</c>, so it is
/// safe to discard them wholesale rather than special-case every formatting
/// variant a site might use.
/// </remarks>
public static class CssMediaQuery
{
    public static bool AppliesForViewport(string query, (float Width, float Height) viewport) =>
        AppliesForViewportAndType(query, viewport, CssMediaType.Screen);

    public static bool AppliesForViewportAndType(
        string query,
        (float Width, float Height) viewport,
        CssMediaType mediaType)
    {
        // A media-query list is an OR, not an AND. Evaluate each top-level comma
        // arm independently (commas inside functions such as rgb() / calc() are
        // not list separators). This also keeps an inapplicable `print` arm from
        // suppressing a later screen/feature arm.
        foreach (var arm in SplitList(query))
        {
            if (SingleQueryApplies(arm, viewport, mediaType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SingleQueryApplies(
        string query,
        (float Width, float Height) viewport,
        CssMediaType mediaType)
    {
        var viewportWidth = viewport.Width;
        var viewportHeight = viewport.Height;

        var trimmed = query.Trim();
        if (trimmed.StartsWith("@media", StringComparison.Ordinal))
        {
            trimmed = trimmed["@media".Length..];
        }

        trimmed = trimmed.Trim();

        var builder = new StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            if (!CssText.IsWhitespace(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        var compact = builder.ToString();

        // A leading `not` negates the complete media query. This covers both
        // Tailwind's `not all and (...)` breakpoints and ordinary `not print` /
        // `not screen` selection without treating a word inside a feature as a
        // media type.
        if (compact.StartsWith("not", StringComparison.Ordinal))
        {
            return !SingleQueryApplies(compact[3..], viewport, mediaType);
        }

        if (compact.StartsWith("only", StringComparison.Ordinal))
        {
            compact = compact[4..];
        }

        var andIndex = compact.IndexOf("and", StringComparison.Ordinal);
        var medium = andIndex < 0 ? compact : compact[..andIndex];
        var mediumMatches = medium switch
        {
            "all" => true,
            "screen" => mediaType == CssMediaType.Screen,
            "print" => mediaType == CssMediaType.Print,
            _ => medium.StartsWith('('),
            // Unknown named media such as `speech` do not match either visual
            // rendering mode.
        };

        if (!mediumMatches)
        {
            return false;
        }

        // Color-scheme: the light (default) context is rendered. A site's
        // `@media (prefers-color-scheme: dark)` block must NOT apply on top of
        // its light defaults; a `:light` block should apply.
        if (compact.Contains("prefers-color-scheme:dark", StringComparison.Ordinal))
        {
            return false;
        }

        // Reduced-motion / high-contrast / inverted: default (no preference).
        if (compact.Contains("prefers-reduced-motion:reduce", StringComparison.Ordinal)
            || compact.Contains("prefers-contrast:more", StringComparison.Ordinal)
            || compact.Contains("prefers-contrast:less", StringComparison.Ordinal)
            || compact.Contains("inverted-colors:inverted", StringComparison.Ordinal)
            || compact.Contains("forced-colors:active", StringComparison.Ordinal))
        {
            return false;
        }

        // Width constraints, both `min-width:`/`max-width:` and the modern range
        // forms `width>=Npx` / `(Npx<=width)`.
        if (Fails(compact, "max-width:", LengthAxis.Width, viewportWidth, static (actual, limit) => actual > limit, viewport)
            || Fails(compact, "min-width:", LengthAxis.Width, viewportWidth, static (actual, limit) => actual < limit, viewport)
            || Fails(compact, "width<=", LengthAxis.Width, viewportWidth, static (actual, limit) => actual > limit, viewport)
            || Fails(compact, "width>=", LengthAxis.Width, viewportWidth, static (actual, limit) => actual < limit, viewport)
            || Fails(compact, "width>", LengthAxis.Width, viewportWidth, static (actual, limit) => actual <= limit, viewport)
            || Fails(compact, "width<", LengthAxis.Width, viewportWidth, static (actual, limit) => actual >= limit, viewport)
            || FailsBefore(compact, "<=width", LengthAxis.Width, viewportWidth, static (actual, limit) => actual < limit, viewport)
            || FailsBefore(compact, "<width", LengthAxis.Width, viewportWidth, static (actual, limit) => actual <= limit, viewport)
            || FailsBefore(compact, ">=width", LengthAxis.Width, viewportWidth, static (actual, limit) => actual > limit, viewport)
            || FailsBefore(compact, ">width", LengthAxis.Width, viewportWidth, static (actual, limit) => actual >= limit, viewport))
        {
            return false;
        }

        if (Fails(compact, "max-height:", LengthAxis.Height, viewportHeight, static (actual, limit) => actual > limit, viewport)
            || Fails(compact, "min-height:", LengthAxis.Height, viewportHeight, static (actual, limit) => actual < limit, viewport)
            || Fails(compact, "height<=", LengthAxis.Height, viewportHeight, static (actual, limit) => actual > limit, viewport)
            || Fails(compact, "height>=", LengthAxis.Height, viewportHeight, static (actual, limit) => actual < limit, viewport)
            || Fails(compact, "height>", LengthAxis.Height, viewportHeight, static (actual, limit) => actual <= limit, viewport)
            || Fails(compact, "height<", LengthAxis.Height, viewportHeight, static (actual, limit) => actual >= limit, viewport)
            || FailsBefore(compact, "<=height", LengthAxis.Height, viewportHeight, static (actual, limit) => actual < limit, viewport)
            || FailsBefore(compact, "<height", LengthAxis.Height, viewportHeight, static (actual, limit) => actual <= limit, viewport)
            || FailsBefore(compact, ">=height", LengthAxis.Height, viewportHeight, static (actual, limit) => actual > limit, viewport)
            || FailsBefore(compact, ">height", LengthAxis.Height, viewportHeight, static (actual, limit) => actual >= limit, viewport))
        {
            return false;
        }

        if (compact.Contains("orientation:portrait", StringComparison.Ordinal) && viewportWidth > viewportHeight)
        {
            return false;
        }

        if (compact.Contains("orientation:landscape", StringComparison.Ordinal) && viewportHeight > viewportWidth)
        {
            return false;
        }

        return true;
    }

    private static bool Fails(
        string compact,
        string property,
        LengthAxis axis,
        float actual,
        Func<float, float, bool> rejects,
        (float Width, float Height) viewport) =>
        CssLength.ExtractLength(compact, property, viewport, axis) is { } limit && rejects(actual, limit);

    private static bool FailsBefore(
        string compact,
        string marker,
        LengthAxis axis,
        float actual,
        Func<float, float, bool> rejects,
        (float Width, float Height) viewport) =>
        CssLength.ExtractLengthBefore(compact, marker, viewport, axis) is { } limit && rejects(actual, limit);

    internal static List<string> SplitList(string query)
    {
        var parts = new List<string>();
        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var index = 0; index < query.Length; index++)
        {
            var character = query[index];
            if (quote == character)
            {
                quote = null;
                continue;
            }

            if (character is '\'' or '"' && quote is null)
            {
                quote = character;
                continue;
            }

            if (quote is not null)
            {
                continue;
            }

            switch (character)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth = Math.Max(depth - 1, 0);
                    break;
                case ',' when depth == 0:
                    parts.Add(query[start..index].Trim());
                    start = index + 1;
                    break;
            }
        }

        parts.Add(query[start..].Trim());
        return parts;
    }
}
