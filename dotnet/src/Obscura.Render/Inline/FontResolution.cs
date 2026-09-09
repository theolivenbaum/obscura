namespace Obscura.Render;

/// <summary>One face registered under a CSS family name.</summary>
public sealed record LoadedFace(
    string Name,
    FontId? FontId,
    FaceMetrics Metrics,
    ushort MinWeight,
    ushort MaxWeight,
    bool Italic);

/// <summary>Every face registered under one CSS family name.</summary>
public sealed class LoadedFamily
{
    public List<LoadedFace> Faces { get; init; } = [];
}

/// <summary>The face a span will actually shape with, plus the metrics that go with it.</summary>
public sealed record ResolvedFont(string Family, FontId? FontId, FaceMetrics Metrics, bool SyntheticItalic);

/// <summary>A page-provided <c>@font-face</c> resource and its descriptors.</summary>
public sealed class WebFont
{
    public required byte[] Data { get; init; }

    public string? Family { get; init; }

    /// <summary>The descriptor's <c>font-weight</c> range, inclusive.</summary>
    public (ushort Min, ushort Max)? Weight { get; init; }

    public bool? Italic { get; init; }
}

/// <summary>CSS font selection over the loaded families.</summary>
public static class FontResolution
{
    public static ResolvedFont ResolveLoadedFont(
        string? family,
        ushort requestedWeight,
        bool requestedItalic,
        IReadOnlyDictionary<string, LoadedFamily> loaded)
    {
        if (family is not null)
        {
            foreach (string token in family.Split(','))
            {
                string name = token.Trim().Trim('"', '\'').Trim();
                LoadedFamily? candidate = null;
                if (!loaded.TryGetValue(name.ToLowerInvariant(), out candidate))
                {
                    string? bundled = FontAssets.BundledFamilyForCssToken(name);
                    if (bundled is not null)
                    {
                        loaded.TryGetValue(bundled.ToLowerInvariant(), out candidate);
                    }
                }

                if (candidate is not null
                    && SelectLoadedFace(candidate, requestedWeight, requestedItalic) is { } resolved)
                {
                    return resolved;
                }
            }
        }

        string fallback = FontAssets.ResolveFontFamily(family);
        return new ResolvedFont(fallback, null, FontAssets.BundledFaceMetrics(fallback), false);
    }

    public static ResolvedFont? SelectLoadedFace(
        LoadedFamily family,
        ushort requestedWeight,
        bool requestedItalic)
    {
        List<LoadedFace> exactStyle = [];
        foreach (LoadedFace face in family.Faces)
        {
            if (face.Italic == requestedItalic)
            {
                exactStyle.Add(face);
            }
        }

        List<LoadedFace> candidates = exactStyle.Count == 0 ? [.. family.Faces] : exactStyle;
        foreach (LoadedFace face in candidates)
        {
            if (requestedWeight >= face.MinWeight && requestedWeight <= face.MaxWeight)
            {
                // The named-family matcher uses the database's default weight for this
                // resource, while a variable face commonly advertises `100 900` in CSS.
                // Preserve the descriptor-selected file and its database weight; the authored
                // coordinate enters the canonical axis tuple separately.
                return new ResolvedFont(
                    face.Name,
                    face.FontId,
                    face.Metrics,
                    requestedItalic && !face.Italic);
            }
        }

        List<ushort> available = new(candidates.Count);
        foreach (LoadedFace face in candidates)
        {
            available.Add(face.MinWeight);
        }

        ushort matched = FontAssets.MatchFontWeight(requestedWeight, available);
        foreach (LoadedFace face in candidates)
        {
            if (face.MinWeight == matched)
            {
                return new ResolvedFont(
                    face.Name,
                    face.FontId,
                    face.Metrics,
                    requestedItalic && !face.Italic);
            }
        }

        return null;
    }

    public static float UsedLineHeightForFont(LayoutStyle style, ResolvedFont font) =>
        UsedLineHeightWithMetrics(style, font.Metrics);

    public static float UsedLineHeightWithMetrics(LayoutStyle style, FaceMetrics metrics)
    {
        float fontSize = style.FontSize ?? 16f;
        if (style.LineHeight is not { } lineHeight)
        {
            return FontAssets.NormalLineHeight(fontSize, metrics);
        }

        switch (lineHeight.Kind)
        {
            case LineHeightKind.Px:
                return lineHeight.Number;
            case LineHeightKind.Ratio:
                return fontSize * lineHeight.Number;
            case LineHeightKind.Relative:
                Dimension relative = lineHeight.Length;
                if (relative.Kind == DimensionKind.Percent)
                {
                    return fontSize * relative.Value;
                }

                Dimension resolved = relative.Resolve(fontSize, 16f, 0f, 0f);
                return resolved.Kind == DimensionKind.Px ? resolved.Value : fontSize;
            default:
                return FontAssets.NormalLineHeight(fontSize, metrics);
        }
    }

    /// <summary>
    /// Computed used line-height shared by shaped inline runs and forced-break sentinels that
    /// cannot join a run.
    /// </summary>
    public static float UsedLineHeight(LayoutStyle style)
    {
        string family = FontAssets.ResolveFontFamily(style.FontFamily);
        return UsedLineHeightWithMetrics(style, FontAssets.BundledFaceMetrics(family));
    }
}
