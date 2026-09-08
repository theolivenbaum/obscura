namespace Obscura.Render.Css;

/// <summary>Properties the animation sampler can interpolate.</summary>
public enum AnimatedProperty
{
    Transform,
    Translate,
    Rotate,
    Scale,
    Width,
    Height,
    MinWidth,
    MinHeight,
    MaxWidth,
    MaxHeight,
    Top,
    Right,
    Bottom,
    Left,
    MarginTop,
    MarginRight,
    MarginBottom,
    MarginLeft,
    PaddingTop,
    PaddingRight,
    PaddingBottom,
    PaddingLeft,
    RowGap,
    ColumnGap,
    FlexBasis,
    Opacity,
    Color,
    BackgroundColor,
    BorderTopColor,
    BorderRightColor,
    BorderBottomColor,
    BorderLeftColor,
    BackgroundPosition,
    Visibility,
}

/// <summary>One <c>@keyframes</c> stop.</summary>
/// <remarks>
/// CSS keyframes always provide an offset. Keeping this optional makes the
/// normalization rule explicit and reusable by future script-created keyframes
/// without changing the sampler.
/// </remarks>
public sealed record KeyframeStop(float? Offset, string Declarations, int SourceOrder);

public sealed record AnimatedDeclaration(string Name, string Value);

public sealed record PropertyTrackStop(float Offset, int SourceOrder, AnimatedDeclaration Declaration);

/// <summary>A compiled <c>@keyframes</c> body: every stop plus sparse per-property tracks.</summary>
public sealed class Keyframes
{
    public List<KeyframeStop> Stops { get; init; } = [];

    public Dictionary<AnimatedProperty, List<PropertyTrackStop>> Tracks { get; init; } = [];
}

/// <summary>
/// <c>@keyframes</c> compilation, offset normalization and easing sampling.
/// </summary>
public static class CssKeyframes
{
    public static AnimationEffectImpact EffectImpact(AnimatedProperty property) => property switch
    {
        AnimatedProperty.Opacity
            or AnimatedProperty.Color
            or AnimatedProperty.BackgroundColor
            or AnimatedProperty.BorderTopColor
            or AnimatedProperty.BorderRightColor
            or AnimatedProperty.BorderBottomColor
            or AnimatedProperty.BorderLeftColor
            or AnimatedProperty.BackgroundPosition
            or AnimatedProperty.Visibility => AnimationEffectImpact.Paint,
        _ => AnimationEffectImpact.Geometry,
    };

    /// <summary>
    /// Compile a keyframes body into sparse per-property tracks.
    /// </summary>
    /// <remarks>
    /// Values remain in specified form because <c>var()</c> and color-scheme
    /// resolution are element dependent, but declaration splitting,
    /// shorthand-to-longhand membership, offset distribution, and
    /// duplicate-offset ordering are all paid once.
    /// </remarks>
    public static Keyframes CompileBody(string css)
    {
        var stops = new List<KeyframeStop>();
        var parsed = CssParser.ParseStylesheetForViewport(css, (1280f, 720f));
        for (var sourceOrder = 0; sourceOrder < parsed.Count; sourceOrder++)
        {
            var (selector, declarations) = parsed[sourceOrder];
            foreach (var part in selector.Split(','))
            {
                if (ParseKeyframeOffset(part) is { } offset)
                {
                    stops.Add(new KeyframeStop(offset, declarations, sourceOrder));
                }
            }
        }

        if (stops.Count == 0)
        {
            return new Keyframes();
        }

        var tracks = new Dictionary<AnimatedProperty, List<PropertyTrackStop>>();
        foreach (var (offset, stop) in NormalizedOffsets(stops))
        {
            // CSS Animations ignores important declarations in keyframes.
            var (normal, _) = CssDeclarations.Partition(stop.Declarations);
            var declarations = new Dictionary<AnimatedProperty, AnimatedDeclaration>();
            foreach (var raw in CssDeclarations.Split(normal))
            {
                var trimmed = raw.Trim();
                var separator = trimmed.IndexOf(':');
                if (separator < 0)
                {
                    continue;
                }

                var name = CssText.AsciiLower(trimmed[..separator].Trim());
                var value = trimmed[(separator + 1)..].Trim();
                if (value.Length == 0
                    || (!value.Contains("var(", StringComparison.Ordinal)
                        && !SupportsAnimationDeclaration(name, value)))
                {
                    continue;
                }

                var declaration = new AnimatedDeclaration(name, value);
                foreach (var property in PropertiesForDeclaration(name))
                {
                    declarations[property] = declaration;
                }
            }

            foreach (var (property, declaration) in declarations)
            {
                if (!tracks.TryGetValue(property, out var track))
                {
                    track = [];
                    tracks[property] = track;
                }

                track.Add(new PropertyTrackStop(offset, stop.SourceOrder, declaration));
            }
        }

        foreach (var property in tracks.Keys.ToList())
        {
            var track = tracks[property];
            track.Sort(static (left, right) =>
            {
                var byOffset = left.Offset.CompareTo(right.Offset);
                return byOffset != 0 ? byOffset : left.SourceOrder.CompareTo(right.SourceOrder);
            });

            var merged = new List<PropertyTrackStop>(track.Count);
            foreach (var stop in track)
            {
                if (merged.Count != 0 && merged[^1].Offset == stop.Offset)
                {
                    merged[^1] = stop;
                }
                else
                {
                    merged.Add(stop);
                }
            }

            tracks[property] = merged;
        }

        return new Keyframes { Stops = stops, Tracks = tracks };
    }

    internal static bool SupportsAnimationDeclaration(string name, string value) =>
        CssHost.SupportsDeclaration(name, value)
        || (string.Equals(name, "background", StringComparison.Ordinal) && CssColor.Parse(value) is not null);

    public static List<AnimatedProperty> PropertiesForDeclaration(string name) => name switch
    {
        "transform" => [AnimatedProperty.Transform],
        "translate" => [AnimatedProperty.Translate],
        "rotate" => [AnimatedProperty.Rotate],
        "scale" => [AnimatedProperty.Scale],
        "width" => [AnimatedProperty.Width],
        "height" => [AnimatedProperty.Height],
        "min-width" => [AnimatedProperty.MinWidth],
        "min-height" => [AnimatedProperty.MinHeight],
        "max-width" => [AnimatedProperty.MaxWidth],
        "max-height" => [AnimatedProperty.MaxHeight],
        "top" or "inset-block-start" => [AnimatedProperty.Top],
        "right" or "inset-inline-end" => [AnimatedProperty.Right],
        "bottom" or "inset-block-end" => [AnimatedProperty.Bottom],
        "left" or "inset-inline-start" => [AnimatedProperty.Left],
        "inset" => [AnimatedProperty.Top, AnimatedProperty.Right, AnimatedProperty.Bottom, AnimatedProperty.Left],
        "inset-inline" => [AnimatedProperty.Left, AnimatedProperty.Right],
        "inset-block" => [AnimatedProperty.Top, AnimatedProperty.Bottom],
        "margin" =>
        [
            AnimatedProperty.MarginTop, AnimatedProperty.MarginRight,
            AnimatedProperty.MarginBottom, AnimatedProperty.MarginLeft,
        ],
        "margin-top" or "margin-block-start" => [AnimatedProperty.MarginTop],
        "margin-right" or "margin-inline-end" => [AnimatedProperty.MarginRight],
        "margin-bottom" or "margin-block-end" => [AnimatedProperty.MarginBottom],
        "margin-left" or "margin-inline-start" => [AnimatedProperty.MarginLeft],
        "margin-inline" => [AnimatedProperty.MarginLeft, AnimatedProperty.MarginRight],
        "margin-block" => [AnimatedProperty.MarginTop, AnimatedProperty.MarginBottom],
        "padding" =>
        [
            AnimatedProperty.PaddingTop, AnimatedProperty.PaddingRight,
            AnimatedProperty.PaddingBottom, AnimatedProperty.PaddingLeft,
        ],
        "padding-top" or "padding-block-start" => [AnimatedProperty.PaddingTop],
        "padding-right" or "padding-inline-end" => [AnimatedProperty.PaddingRight],
        "padding-bottom" or "padding-block-end" => [AnimatedProperty.PaddingBottom],
        "padding-left" or "padding-inline-start" => [AnimatedProperty.PaddingLeft],
        "padding-inline" => [AnimatedProperty.PaddingLeft, AnimatedProperty.PaddingRight],
        "padding-block" => [AnimatedProperty.PaddingTop, AnimatedProperty.PaddingBottom],
        "gap" or "grid-gap" => [AnimatedProperty.RowGap, AnimatedProperty.ColumnGap],
        "row-gap" or "grid-row-gap" => [AnimatedProperty.RowGap],
        "column-gap" or "grid-column-gap" or "-webkit-column-gap" => [AnimatedProperty.ColumnGap],
        "flex-basis" => [AnimatedProperty.FlexBasis],
        "opacity" => [AnimatedProperty.Opacity],
        "color" or "-webkit-text-fill-color" => [AnimatedProperty.Color],
        "background-color" => [AnimatedProperty.BackgroundColor],
        "background" => [AnimatedProperty.BackgroundColor, AnimatedProperty.BackgroundPosition],
        "border-color" or "border" =>
        [
            AnimatedProperty.BorderTopColor, AnimatedProperty.BorderRightColor,
            AnimatedProperty.BorderBottomColor, AnimatedProperty.BorderLeftColor,
        ],
        "border-top" or "border-top-color" => [AnimatedProperty.BorderTopColor],
        "border-right" or "border-right-color" => [AnimatedProperty.BorderRightColor],
        "border-bottom" or "border-bottom-color" => [AnimatedProperty.BorderBottomColor],
        "border-left" or "border-left-color" => [AnimatedProperty.BorderLeftColor],
        "background-position" => [AnimatedProperty.BackgroundPosition],
        "visibility" => [AnimatedProperty.Visibility],
        _ => [],
    };

    public static float? ParseKeyframeOffset(string value)
    {
        var lower = CssText.AsciiLower(value.Trim());
        if (string.Equals(lower, "from", StringComparison.Ordinal))
        {
            return 0f;
        }

        if (string.Equals(lower, "to", StringComparison.Ordinal))
        {
            return 1f;
        }

        if (!lower.EndsWith('%'))
        {
            return null;
        }

        var offset = CssNumber.ParseFloat(lower[..^1].Trim());
        if (offset is not { } value2 || !float.IsFinite(value2) || value2 < 0f || value2 > 100f)
        {
            return null;
        }

        return value2 / 100f;
    }

    /// <summary>Distribute missing offsets per the Web Animations rules.</summary>
    public static List<(float Offset, KeyframeStop Stop)> NormalizedOffsets(IReadOnlyList<KeyframeStop> stops)
    {
        if (stops.Count == 0)
        {
            return [];
        }

        var offsets = new float?[stops.Count];
        for (var index = 0; index < stops.Count; index++)
        {
            offsets[index] = stops[index].Offset;
        }

        if (offsets.Length == 1)
        {
            offsets[0] ??= 1f;
        }
        else
        {
            offsets[0] ??= 0f;
            offsets[^1] ??= 1f;
        }

        var position = 0;
        while (position < offsets.Length)
        {
            if (offsets[position] is not null)
            {
                position++;
                continue;
            }

            var start = position - 1;
            var end = position + 1;
            while (offsets[end] is null)
            {
                end++;
            }

            var from = offsets[start]!.Value;
            var to = offsets[end]!.Value;
            var span = (float)(end - start);
            for (var missing = position; missing < end; missing++)
            {
                offsets[missing] = from + ((to - from) * (missing - start) / span);
            }

            position = end + 1;
        }

        var result = new List<(float, KeyframeStop)>(stops.Count);
        for (var index = 0; index < stops.Count; index++)
        {
            result.Add((offsets[index]!.Value, stops[index]));
        }

        return result;
    }

    public static float SampleLinearEasing(IReadOnlyList<float> samples, float progress)
    {
        if (samples.Count < 2)
        {
            return progress;
        }

        var scaled = Math.Clamp(progress, 0f, 1f) * (samples.Count - 1);
        var index = Math.Min((int)MathF.Floor(scaled), samples.Count - 2);
        var local = scaled - index;
        return samples[index] + ((samples[index + 1] - samples[index]) * local);
    }

    public static float SampleCubicBezier(float x1, float y1, float x2, float y2, float progress)
    {
        var target = Math.Clamp(progress, 0f, 1f);

        static float Component(float t, float first, float second)
        {
            var inverse = 1f - t;
            return (3f * inverse * inverse * t * first) + (3f * inverse * t * t * second) + (t * t * t);
        }

        // x control points are constrained to [0,1], so bisection is stable even
        // for flat derivatives at the ends.
        var low = 0f;
        var high = 1f;
        for (var iteration = 0; iteration < 14; iteration++)
        {
            var middle = (low + high) * 0.5f;
            if (Component(middle, x1, x2) < target)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return Math.Clamp(Component((low + high) * 0.5f, y1, y2), 0f, 1f);
    }

    /// <summary>
    /// Sample a Web Animations track, filling missing endpoints from the
    /// underlying value.
    /// </summary>
    /// <remarks>
    /// At a duplicate offset the later keyframe is the outgoing value. The first
    /// duplicate remains the incoming interpolation endpoint just before the
    /// boundary, so the track is deliberately not deduplicated.
    /// </remarks>
    public static bool TrySampleWaapiTrack<T>(
        IReadOnlyList<(float Offset, T Value)> track,
        T underlying,
        float progress,
        Func<T, T, float, T> interpolate,
        out T result)
    {
        result = default!;
        if (track.Count == 0)
        {
            return false;
        }

        var resolved = new List<(float Offset, T Value)>(track);
        resolved.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        if (resolved[0].Offset > 0f)
        {
            resolved.Insert(0, (0f, underlying));
        }

        if (resolved[^1].Offset < 1f)
        {
            resolved.Add((1f, underlying));
        }

        for (var index = resolved.Count - 1; index >= 0; index--)
        {
            if (progress == resolved[index].Offset)
            {
                result = resolved[index].Value;
                return true;
            }
        }

        if (progress < resolved[0].Offset)
        {
            result = resolved[0].Value;
            return true;
        }

        for (var index = 0; index + 1 < resolved.Count; index++)
        {
            var (fromOffset, from) = resolved[index];
            var (toOffset, to) = resolved[index + 1];
            if (progress <= toOffset)
            {
                result = progress == toOffset || fromOffset == toOffset
                    ? to
                    : interpolate(from, to, (progress - fromOffset) / (toOffset - fromOffset));
                return true;
            }
        }

        result = resolved[^1].Value;
        return true;
    }
}
