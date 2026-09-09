// Background layers, gradients, masks and box-shadow from style.rs.
using Obscura.Render.Css;

namespace Obscura.Render;

public static partial class ComputedStyle
{
    /// <summary>Rust <c>set_background_gradients</c>.</summary>
    internal static void SetBackgroundGradients(LayoutStyle style, string value)
    {
        (List<BackgroundGradientLayer> layers, List<RadialGradientGeometry?> radialGeometries) =
            ParseBackgroundGradientLayers(value, style.ColorSchemeDark);

        style.BackgroundGradient = null;
        style.BackgroundRadialGradient = null;
        style.BackgroundRadialGradientGeometry = null;
        style.BackgroundConicGradient = null;

        for (int index = 0; index < layers.Count; index++)
        {
            if (style.BackgroundGradient is null && layers[index] is BackgroundGradientLayer.Linear linear)
            {
                style.BackgroundGradient = (linear.Angle, [.. linear.Stops]);
            }
        }

        for (int index = 0; index < layers.Count; index++)
        {
            if (style.BackgroundRadialGradient is null && layers[index] is BackgroundGradientLayer.Radial radial)
            {
                style.BackgroundRadialGradient = (radial.Center, [.. radial.Stops]);
                break;
            }
        }

        for (int index = 0; index < layers.Count && index < radialGeometries.Count; index++)
        {
            if (layers[index] is BackgroundGradientLayer.Radial && radialGeometries[index] is { } geometry)
            {
                style.BackgroundRadialGradientGeometry = geometry;
                break;
            }
        }

        for (int index = 0; index < layers.Count; index++)
        {
            if (style.BackgroundConicGradient is null && layers[index] is BackgroundGradientLayer.Conic conic)
            {
                style.BackgroundConicGradient = (conic.Angle, conic.Center, [.. conic.Stops]);
                break;
            }
        }

        style.BackgroundGradientLayers = layers;
        style.BackgroundGradientLayerRadialGeometries = radialGeometries;
    }

    /// <summary>Rust <c>parse_background_gradient_layers</c>.</summary>
    internal static (List<BackgroundGradientLayer> Layers, List<RadialGradientGeometry?> Geometries)
        ParseBackgroundGradientLayers(string value, bool darkScheme)
    {
        List<BackgroundGradientLayer> layers = [];
        List<RadialGradientGeometry?> radialGeometries = [];
        foreach (string authoredLayer in SplitTopLevel(value, ','))
        {
            if (ParseLinearGradient(authoredLayer, darkScheme) is { } linear)
            {
                layers.Add(new BackgroundGradientLayer.Linear(
                    linear.Angle,
                    linear.Stops,
                    linear.StopPositions,
                    linear.Repeating));
                radialGeometries.Add(null);
            }
            else if (ParseRadialGradient(authoredLayer, darkScheme) is { } radial)
            {
                layers.Add(new BackgroundGradientLayer.Radial(
                    radial.Center,
                    radial.Stops,
                    radial.StopPositions));
                radialGeometries.Add(radial.Geometry);
            }
            else if (ParseConicGradient(authoredLayer, darkScheme) is { } conic)
            {
                layers.Add(new BackgroundGradientLayer.Conic(conic.Angle, conic.Center, conic.Stops));
                radialGeometries.Add(null);
            }
        }

        return (layers, radialGeometries);
    }

    private readonly record struct ParsedLinearGradient(
        float Angle,
        List<GradientStop> Stops,
        List<string?> StopPositions,
        bool Repeating);

    /// <summary>Rust <c>parse_linear_gradient</c>.</summary>
    private static ParsedLinearGradient? ParseLinearGradient(string value, bool darkScheme)
    {
        string trimmed = value.Trim();
        string lower = CssText.AsciiLower(trimmed);
        int start = lower.IndexOf("linear-gradient(", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        string prefix = lower[..start];
        bool repeating = prefix.EndsWith("repeating-", StringComparison.Ordinal);
        // The prefixed WebKit syntax predates the standardized angle system:
        // 0deg points right and positive angles turn counter-clockwise.
        bool legacyWebkitAngle = prefix.EndsWith("-webkit-", StringComparison.Ordinal)
            || prefix.EndsWith("-webkit-repeating-", StringComparison.Ordinal);
        int open = start + "linear-gradient(".Length;

        int depth = 1;
        int end = open;
        while (end < trimmed.Length && depth > 0)
        {
            switch (trimmed[end])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
            }

            end++;
        }

        string inner = trimmed[open..Math.Max(end - 1, open)];

        List<string> parts = [];
        System.Text.StringBuilder current = new();
        int innerDepth = 0;
        foreach (char character in inner)
        {
            switch (character)
            {
                case '(':
                    innerDepth++;
                    current.Append(character);
                    break;
                case ')':
                    innerDepth--;
                    current.Append(character);
                    break;
                case ',' when innerDepth == 0:
                    parts.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        if (current.ToString().Trim().Length != 0)
        {
            parts.Add(current.ToString());
        }

        if (parts.Count == 0)
        {
            return null;
        }

        float angle = 180.0f;
        string first = CssText.AsciiLower(parts[0].Trim());
        int stopStart = 0;
        if (first.EndsWith("deg", StringComparison.Ordinal))
        {
            if (ParseF32(first[..^3].Trim()) is { } authored)
            {
                angle = legacyWebkitAngle ? RemEuclid(90.0f - authored, 360.0f) : RemEuclid(authored, 360.0f);
            }

            stopStart = 1;
        }
        else if (first.StartsWith("to ", StringComparison.Ordinal))
        {
            angle = first switch
            {
                "to top" => 0.0f,
                "to right" => 90.0f,
                "to bottom" => 180.0f,
                "to left" => 270.0f,
                "to top right" or "to right top" => 45.0f,
                "to bottom right" or "to right bottom" => 135.0f,
                "to bottom left" or "to left bottom" => 225.0f,
                "to top left" or "to left top" => 315.0f,
                _ => 180.0f,
            };
            stopStart = 1;
        }
        else if (first.StartsWith("turn", StringComparison.Ordinal) || first.EndsWith("turn", StringComparison.Ordinal))
        {
            stopStart = 1;
        }

        List<GradientStop> stops = [];
        List<string?> stopPositions = [];
        for (int index = stopStart; index < parts.Count; index++)
        {
            string part = parts[index].Trim();
            if (part.Length == 0)
            {
                continue;
            }

            List<string> tokens = SplitWsParen(part);
            RgbaColor? color = null;
            List<string> positions = [];
            for (int colorTokens = 1; colorTokens <= tokens.Count; colorTokens++)
            {
                List<string> positionTokens = tokens.GetRange(colorTokens, tokens.Count - colorTokens);
                if (positionTokens.Count > 2 || !positionTokens.TrueForAll(GradientPositionIsValid))
                {
                    continue;
                }

                string colorText = string.Join(" ", tokens.GetRange(0, colorTokens));
                if (CssColor.ParseForScheme(colorText, darkScheme) is { } parsed)
                {
                    color = parsed;
                    positions = positionTokens;
                    break;
                }
            }

            if (color is not { } stopColor)
            {
                continue;
            }

            if (positions.Count == 0)
            {
                stops.Add(new GradientStop(stopColor, null));
                stopPositions.Add(null);
            }
            else
            {
                foreach (string position in positions)
                {
                    float? percentage = position.EndsWith('%') && ParseF32(position[..^1]) is { } number
                        ? number / 100f
                        : null;
                    stops.Add(new GradientStop(stopColor, percentage));
                    stopPositions.Add(position.Trim());
                }
            }
        }

        if (stops.Count < 2)
        {
            // A single-color "gradient" is just that color; let the caller fall
            // back to background_color.
            return null;
        }

        return new ParsedLinearGradient(angle, stops, stopPositions, repeating);
    }

    private static float RemEuclid(float value, float modulus)
    {
        float remainder = value % modulus;
        return remainder < 0f ? remainder + MathF.Abs(modulus) : remainder;
    }

    /// <summary>Rust <c>gradient_position_is_valid</c>.</summary>
    private static bool GradientPositionIsValid(string value)
    {
        string trimmed = value.Trim();
        if (trimmed is "0" or "-0" || trimmed.Contains('('))
        {
            return true;
        }

        foreach (string suffix in GradientPositionUnits)
        {
            if (trimmed.EndsWith(suffix, StringComparison.Ordinal)
                && ParseF32(trimmed[..^suffix.Length].Trim()) is { } number
                && float.IsFinite(number))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] GradientPositionUnits =
    [
        "%", "px", "em", "rem", "ex", "vw", "vh", "vmin", "vmax", "dvw", "dvh", "svw", "svh",
        "lvw", "lvh",
    ];

    /// <summary>
    /// <c>StopPositions</c> carries the authored position token for each stop, so paint can
    /// resolve a length against the gradient ray. <see cref="SplitColorStop"/> only
    /// understands percentages, which silently turned <c>transparent 32rem</c> into an
    /// unpositioned stop and spread the ramp over the whole box.
    /// </summary>
    private readonly record struct ParsedRadialGradient(
        (float X, float Y) Center,
        List<GradientStop> Stops,
        List<string?> StopPositions,
        RadialGradientGeometry Geometry);

    /// <summary>Rust <c>parse_radial_gradient</c>.</summary>
    private static ParsedRadialGradient? ParseRadialGradient(string value, bool darkScheme)
    {
        string lower = CssText.AsciiLower(value);
        int start = lower.IndexOf("radial-gradient(", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        int open = start + "radial-gradient(".Length;
        if (open > value.Length || FindMatchingParen(value[open..]) is not { } relativeEnd)
        {
            return null;
        }

        int end = relativeEnd + open;
        List<string> parts = SplitTopLevel(value[open..end], ',');
        if (parts.Count == 0)
        {
            return null;
        }

        (float X, float Y) center = (0.5f, 0.5f);
        RadialGradientGeometry geometry = RadialGradientGeometry.Default;
        int stopStart = 0;
        string prelude = CssText.AsciiLower(parts[0].Trim());
        int atIndex = prelude.IndexOf(" at ", StringComparison.Ordinal);
        if (atIndex >= 0 || prelude.StartsWith("at ", StringComparison.Ordinal))
        {
            string shape;
            string coordinates;
            if (atIndex >= 0)
            {
                shape = prelude[..atIndex];
                coordinates = prelude[(atIndex + 4)..];
            }
            else
            {
                shape = string.Empty;
                coordinates = prelude[3..];
            }

            if (ParseRadialGradientGeometry(shape) is not { } parsed)
            {
                return null;
            }

            geometry = parsed;
            center = ParseGradientCenter(coordinates);
            stopStart = 1;
        }
        else if (CssColor.ParseForScheme(SplitColorStop(parts[0].Trim()).Color, darkScheme) is null)
        {
            if (ParseRadialGradientGeometry(prelude) is not { } parsed)
            {
                return null;
            }

            geometry = parsed;
            stopStart = 1;
        }

        List<GradientStop> stops = [];
        List<string?> stopPositions = [];
        for (int index = stopStart; index < parts.Count; index++)
        {
            string part = parts[index].Trim();
            (string colorText, float? position) = SplitColorStop(part);
            if (CssColor.ParseForScheme(colorText, darkScheme) is { } color)
            {
                stops.Add(new GradientStop(color, position));
                stopPositions.Add(AuthoredStopPosition(part));
            }
        }

        return stops.Count >= 2
            ? new ParsedRadialGradient(center, stops, stopPositions, geometry)
            : null;
    }

    /// <summary>Rust <c>parse_radial_gradient_geometry</c>.</summary>
    private static RadialGradientGeometry? ParseRadialGradientGeometry(string value)
    {
        List<string> tokens = SplitWhitespace(value);
        if (tokens.Count == 0)
        {
            return RadialGradientGeometry.Default;
        }

        RadialGradientShape? explicitShape = null;
        int shapeCount = 0;
        foreach (string token in tokens)
        {
            if (token is "circle" or "ellipse")
            {
                shapeCount++;
                explicitShape ??= token == "circle" ? RadialGradientShape.Circle : RadialGradientShape.Ellipse;
            }
        }

        if (shapeCount > 1)
        {
            return null;
        }

        RadialGradientSize? extent = null;
        int extentCount = 0;
        foreach (string token in tokens)
        {
            RadialGradientSize? candidate = token switch
            {
                "closest-side" or "contain" => RadialGradientSize.ClosestSide,
                "closest-corner" => RadialGradientSize.ClosestCorner,
                "farthest-side" => RadialGradientSize.FarthestSide,
                "farthest-corner" or "cover" => RadialGradientSize.FarthestCorner,
                _ => null,
            };
            if (candidate is { } size)
            {
                extentCount++;
                extent ??= size;
            }
        }

        if (extentCount > 1)
        {
            return null;
        }

        List<Dimension> dimensions = [];
        foreach (string token in tokens)
        {
            if (token is "circle" or "ellipse" or "closest-side" or "closest-corner"
                or "farthest-side" or "farthest-corner" or "contain" or "cover")
            {
                continue;
            }

            dimensions.Add(DimensionValue(token));
        }

        foreach (Dimension dimension in dimensions)
        {
            if (!RadialRadiusIsNonNegative(dimension))
            {
                return null;
            }
        }

        if (extent is not null && dimensions.Count != 0)
        {
            return null;
        }

        switch (dimensions.Count)
        {
            case 0:
                return new RadialGradientGeometry(
                    explicitShape ?? RadialGradientShape.Ellipse,
                    extent ?? RadialGradientSize.FarthestCorner);
            case 1 when explicitShape != RadialGradientShape.Ellipse:
                // Circle radii are lengths, never percentages.
                if (dimensions[0].Kind == DimensionKind.Percent)
                {
                    return null;
                }

                return new RadialGradientGeometry(
                    RadialGradientShape.Circle,
                    RadialGradientSize.Explicit(dimensions[0], dimensions[0]));
            case 2 when explicitShape != RadialGradientShape.Circle:
                return new RadialGradientGeometry(
                    RadialGradientShape.Ellipse,
                    RadialGradientSize.Explicit(dimensions[0], dimensions[1]));
            default:
                return null;
        }
    }

    private static bool RadialRadiusIsNonNegative(Dimension value) =>
        !value.IsAuto && float.IsFinite(value.Value) && value.Value >= 0f;

    /// <summary>Rust <c>parse_gradient_center</c>.</summary>
    private static (float X, float Y) ParseGradientCenter(string value)
    {
        (float X, float Y) center = (0.5f, 0.5f);
        List<string> tokens = SplitWhitespace(value);
        int assigned = 0;
        foreach (string token in tokens)
        {
            if (PercentFraction(token) is not { } fraction)
            {
                continue;
            }

            if (assigned == 0)
            {
                center.X = fraction;
            }
            else if (assigned == 1)
            {
                center.Y = fraction;
            }
            else
            {
                break;
            }

            assigned++;
        }

        foreach (string token in tokens)
        {
            switch (token)
            {
                case "left":
                    center.X = 0.0f;
                    break;
                case "right":
                    center.X = 1.0f;
                    break;
                case "top":
                    center.Y = 0.0f;
                    break;
                case "bottom":
                    center.Y = 1.0f;
                    break;
            }
        }

        return center;
    }

    /// <summary>Rust <c>parse_conic_gradient</c>.</summary>
    private static (float Angle, (float X, float Y) Center, List<GradientStop> Stops)? ParseConicGradient(
        string value,
        bool darkScheme)
    {
        string trimmed = value.Trim();
        string lower = CssText.AsciiLower(trimmed);
        int start = lower.IndexOf("conic-gradient(", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        int open = start + "conic-gradient(".Length;
        if (open > trimmed.Length || FindMatchingParen(trimmed[open..]) is not { } relativeEnd)
        {
            return null;
        }

        int end = relativeEnd + open;
        List<string> parts = SplitTopLevel(trimmed[open..end], ',');
        if (parts.Count == 0)
        {
            return null;
        }

        float angle = 0.0f;
        (float X, float Y) center = (0.5f, 0.5f);
        int stopStart = 0;
        string prelude = CssText.AsciiLower(parts[0].Trim());
        if (prelude.StartsWith("from ", StringComparison.Ordinal) || prelude.StartsWith("at ", StringComparison.Ordinal))
        {
            int from = prelude.IndexOf("from ", StringComparison.Ordinal);
            if (from >= 0)
            {
                List<string> tokens = SplitWhitespace(prelude[(from + 5)..]);
                string token = tokens.Count > 0 ? tokens[0] : string.Empty;
                angle = RemEuclid(ParseCssAngle(token) ?? 0.0f, 360.0f);
            }

            int at = prelude.IndexOf(" at ", StringComparison.Ordinal);
            if (at >= 0)
            {
                ApplyCenter(prelude[(at + 4)..], ref center);
            }
            else if (prelude.StartsWith("at ", StringComparison.Ordinal))
            {
                ApplyCenter(prelude[3..], ref center);
            }

            stopStart = 1;
        }

        List<GradientStop> stops = [];
        for (int index = stopStart; index < parts.Count; index++)
        {
            string part = parts[index].Trim();
            if (part.Length == 0)
            {
                continue;
            }

            (string colorText, float? position) = SplitColorStop(part);
            if (CssColor.ParseForScheme(colorText, darkScheme) is { } color)
            {
                stops.Add(new GradientStop(color, position));
            }
        }

        return stops.Count >= 2 ? (angle, center, stops) : null;

        static void ApplyCenter(string source, ref (float X, float Y) center)
        {
            List<string> coordinates = SplitWhitespace(source);
            if (coordinates.Count > 0 && PercentFraction(coordinates[0]) is { } x)
            {
                center.X = x;
            }

            if (coordinates.Count > 1 && PercentFraction(coordinates[1]) is { } y)
            {
                center.Y = y;
            }
        }
    }

    /// <summary>Rust <c>split_color_stop</c>.</summary>
    /// <summary>
    /// The authored position token of a color-stop, retained verbatim so paint can resolve
    /// a length against the gradient's own ray length. Returns null when the stop carries
    /// no position.
    /// </summary>
    /// <remarks>
    /// Counts through <see cref="SplitWsParen"/> rather than splitting on whitespace so a
    /// parenthesized color such as <c>rgb(1 2 3) 40%</c> stays one token.
    /// </remarks>
    private static string? AuthoredStopPosition(string value)
    {
        List<string> tokens = SplitWsParen(value.Trim());
        if (tokens.Count <= 1)
        {
            return null;
        }

        string last = tokens[^1].Trim();
        return GradientPositionIsValid(last) ? last : null;
    }

    private static (string Color, float? Position) SplitColorStop(string value)
    {
        int index = LastIndexOfWhitespace(value);
        if (index >= 0)
        {
            string tail = value[(index + 1)..].Trim();
            if (tail.EndsWith('%') && ParseF32(tail[..^1]) is { } percent)
            {
                return (value[..index].Trim(), Math.Clamp(percent / 100f, 0f, 1f));
            }

            if (tail.EndsWith("deg", StringComparison.Ordinal) && ParseF32(tail[..^3]) is { } degrees)
            {
                return (value[..index].Trim(), Math.Clamp(degrees / 360f, 0f, 1f));
            }
        }

        return (value, null);
    }

    private static int LastIndexOfWhitespace(string value)
    {
        for (int index = value.Length - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Rust <c>parse_background_size</c>.</summary>
    internal static (float Width, float Height)? ParseBackgroundSize(string value)
    {
        List<string> tokens = SplitWhitespace(value);
        if (tokens.Count == 1)
        {
            return PxValue(tokens[0]) is { } single ? (single, single) : null;
        }

        if (tokens.Count == 2)
        {
            if (PxValue(tokens[0]) is not { } width || PxValue(tokens[1]) is not { } height)
            {
                return null;
            }

            return (width, height);
        }

        return null;
    }

    /// <summary>Rust <c>parse_background_size_fit</c>.</summary>
    internal static ObjectFit? ParseBackgroundSizeFit(string value)
    {
        int slash = value.LastIndexOf('/');
        string size = slash >= 0 ? value[(slash + 1)..] : value;
        List<string> tokens = SplitWhitespace(size);
        if (tokens.Contains("cover"))
        {
            return ObjectFit.Cover;
        }

        return tokens.Contains("contain") ? ObjectFit.Contain : null;
    }

    /// <summary>Rust <c>parse_image_repeat</c>.</summary>
    internal static (bool X, bool Y)? ParseImageRepeat(string value)
    {
        List<string> tokens = [];
        foreach (string token in SplitWsParen(value))
        {
            if (CssText.AsciiLower(token) is "repeat" or "no-repeat" or "repeat-x" or "repeat-y" or "space" or "round")
            {
                tokens.Add(token);
            }
        }

        if (tokens.Count == 0)
        {
            return null;
        }

        if (CssText.EqualsAscii(tokens[0], "repeat-x"))
        {
            return (true, false);
        }

        if (CssText.EqualsAscii(tokens[0], "repeat-y"))
        {
            return (false, true);
        }

        if (tokens.Count >= 2)
        {
            return (!CssText.EqualsAscii(tokens[0], "no-repeat"), !CssText.EqualsAscii(tokens[1], "no-repeat"));
        }

        bool repeat = !CssText.EqualsAscii(tokens[0], "no-repeat");
        return (repeat, repeat);
    }

    /// <summary>Rust <c>parse_background_origin</c>.</summary>
    internal static BackgroundOrigin? ParseBackgroundOrigin(string value) =>
        CssText.AsciiLower(value.Trim()) switch
        {
            "border-box" => BackgroundOrigin.BorderBox,
            "padding-box" => BackgroundOrigin.PaddingBox,
            "content-box" => BackgroundOrigin.ContentBox,
            _ => null,
        };

    /// <summary>Rust <c>parse_background_clip</c>.</summary>
    internal static BackgroundClip? ParseBackgroundClip(string value) =>
        CssText.AsciiLower(value.Trim()) switch
        {
            "border-box" => BackgroundClip.BorderBox,
            "padding-box" => BackgroundClip.PaddingBox,
            "content-box" => BackgroundClip.ContentBox,
            "text" => BackgroundClip.Text,
            _ => null,
        };

    /// <summary>Rust <c>parse_background_box_shorthand</c>.</summary>
    internal static (BackgroundOrigin Origin, BackgroundClip Clip)? ParseBackgroundBoxShorthand(string value)
    {
        List<string> layers = SplitTopLevel(value, ',');
        if (layers.Count == 0)
        {
            return null;
        }

        List<(string Token, BackgroundClip Clip)> boxes = [];
        foreach (string token in SplitWsParen(layers[0]))
        {
            if (ParseBackgroundClip(token) is { } clip)
            {
                boxes.Add((token, clip));
            }
        }

        switch (boxes.Count)
        {
            case 0:
                return null;
            case 1 when boxes[0].Clip == BackgroundClip.Text:
                return (BackgroundOrigin.PaddingBox, BackgroundClip.Text);
            case 1:
                return ParseBackgroundOrigin(boxes[0].Token) is { } origin ? (origin, boxes[0].Clip) : null;
            case 2:
                return ParseBackgroundOrigin(boxes[0].Token) is { } first ? (first, boxes[1].Clip) : null;
            default:
                return null;
        }
    }

    /// <summary>Rust <c>background_size_expression</c>.</summary>
    internal static string? BackgroundSizeExpression(string value)
    {
        int slash = value.LastIndexOf('/');
        if (slash < 0)
        {
            return null;
        }

        string size = value[(slash + 1)..];
        int depth = 0;
        int end = size.Length;
        for (int index = 0; index < size.Length; index++)
        {
            char character = size[index];
            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth = Math.Max(depth - 1, 0);
            }
            else if (depth == 0 && character == ',')
            {
                end = index;
                break;
            }
        }

        string trimmed = size[..end].Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Rust <c>parse_background_position</c>.</summary>
    internal static BackgroundPosition ParseBackgroundPosition(string value)
    {
        List<string> layers = SplitTopLevel(value, ',');
        string firstLayer = (layers.Count > 0 ? layers[0] : value).Trim();
        List<string> tokens = SplitWsParen(firstLayer);
        BackgroundPositionAxis center = BackgroundPositionAxis.Percentage(0.5f);
        BackgroundPositionAxis start = BackgroundPositionAxis.Percentage(0.0f);
        BackgroundPositionAxis end = BackgroundPositionAxis.Percentage(1.0f);

        if (tokens.Count == 0)
        {
            return BackgroundPosition.Zero;
        }

        if (tokens.Count == 1)
        {
            string only = tokens[0];
            if (VerticalKeyword(only) is { } verticalOnly)
            {
                return BackgroundPosition.New(center, verticalOnly);
            }

            BackgroundPositionAxis x = HorizontalKeyword(only)
                ?? (only == "center" ? center : (BackgroundPositionAxis?)null)
                ?? Numeric(only)
                ?? center;
            return BackgroundPosition.New(x, center);
        }

        if (tokens.Count == 2)
        {
            string first = tokens[0];
            string second = tokens[1];
            if (VerticalKeyword(first) is { } y)
            {
                BackgroundPositionAxis x = HorizontalKeyword(second)
                    ?? (second == "center" ? center : (BackgroundPositionAxis?)null)
                    ?? center;
                return BackgroundPosition.New(x, y);
            }

            if (HorizontalKeyword(first) is { } fx)
            {
                BackgroundPositionAxis fy = VerticalKeyword(second)
                    ?? (second == "center" ? center : (BackgroundPositionAxis?)null)
                    ?? Numeric(second)
                    ?? center;
                return BackgroundPosition.New(fx, fy);
            }

            if (HorizontalKeyword(second) is { } sx)
            {
                BackgroundPositionAxis sy = VerticalKeyword(first)
                    ?? (first == "center" ? center : (BackgroundPositionAxis?)null)
                    ?? center;
                return BackgroundPosition.New(sx, sy);
            }

            BackgroundPositionAxis lx = (first == "center" ? center : (BackgroundPositionAxis?)null) ?? Numeric(first) ?? center;
            BackgroundPositionAxis ly = VerticalKeyword(second)
                ?? (second == "center" ? center : (BackgroundPositionAxis?)null)
                ?? Numeric(second)
                ?? center;
            return BackgroundPosition.New(lx, ly);
        }

        // Three/four-value syntax anchors an offset to a named edge:
        // `right 10px bottom 20px` => `calc(100% - 10px) calc(100% - 20px)`.
        BackgroundPositionAxis? resolvedX = null;
        BackgroundPositionAxis? resolvedY = null;
        int cursor = 0;
        while (cursor < tokens.Count)
        {
            string token = tokens[cursor];
            bool? axis;
            bool fromEnd;
            switch (token)
            {
                case "left":
                    axis = false;
                    fromEnd = false;
                    break;
                case "right":
                    axis = false;
                    fromEnd = true;
                    break;
                case "top":
                    axis = true;
                    fromEnd = false;
                    break;
                case "bottom":
                    axis = true;
                    fromEnd = true;
                    break;
                case "center":
                    if (resolvedX is null)
                    {
                        resolvedX = center;
                    }
                    else if (resolvedY is null)
                    {
                        resolvedY = center;
                    }

                    cursor++;
                    continue;
                default:
                    axis = null;
                    fromEnd = false;
                    break;
            }

            if (axis is { } vertical)
            {
                BackgroundPositionAxis? offset = cursor + 1 < tokens.Count ? Numeric(tokens[cursor + 1]) : null;
                BackgroundPositionAxis position = offset is { } value2
                    ? fromEnd ? BackgroundPositionAxis.FromEndOffset(value2) : value2
                    : fromEnd ? end : start;
                if (vertical)
                {
                    resolvedY = position;
                }
                else
                {
                    resolvedX = position;
                }

                if (offset is not null)
                {
                    cursor++;
                }
            }
            else if (Numeric(token) is { } numeric)
            {
                if (resolvedX is null)
                {
                    resolvedX = numeric;
                }
                else if (resolvedY is null)
                {
                    resolvedY = numeric;
                }
            }

            cursor++;
        }

        return BackgroundPosition.New(resolvedX ?? center, resolvedY ?? center);

        static BackgroundPositionAxis? Numeric(string token)
        {
            string trimmed = token.Trim();
            if (trimmed.EndsWith('%'))
            {
                if (ParseF32(trimmed[..^1]) is not { } number)
                {
                    return null;
                }

                float percentage = number / 100f;
                return float.IsFinite(percentage) ? BackgroundPositionAxis.Percentage(percentage) : null;
            }

            return PxValue(trimmed) is { } length && float.IsFinite(length)
                ? BackgroundPositionAxis.Pixels(length)
                : null;
        }

        static BackgroundPositionAxis? HorizontalKeyword(string token) => token switch
        {
            "left" => BackgroundPositionAxis.Percentage(0.0f),
            "right" => BackgroundPositionAxis.Percentage(1.0f),
            _ => null,
        };

        static BackgroundPositionAxis? VerticalKeyword(string token) => token switch
        {
            "top" => BackgroundPositionAxis.Percentage(0.0f),
            "bottom" => BackgroundPositionAxis.Percentage(1.0f),
            _ => null,
        };
    }

    /// <summary>Rust <c>parse_box_shadow</c>.</summary>
    internal static BoxShadow? ParseBoxShadow(string value, RgbaColor? currentColor, bool darkScheme)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0 || CssText.EqualsAscii(trimmed, "none"))
        {
            return null;
        }

        List<string> layers = SplitTopLevel(trimmed, ',');
        if (layers.Count == 0)
        {
            return null;
        }

        bool inset = false;
        RgbaColor? color = null;
        List<float> lengths = [];
        foreach (string token in SplitWsParen(layers[0].Trim()))
        {
            string current = token.Trim();
            if (current.Length == 0)
            {
                continue;
            }

            if (CssText.EqualsAscii(current, "inset"))
            {
                inset = true;
                continue;
            }

            // A bare `0` must be an offset, not a failed color.
            if (lengths.Count < 4 && PxValue(current) is { } length)
            {
                lengths.Add(length);
                continue;
            }

            if (CssColor.ParseForScheme(current, darkScheme) is { } parsed)
            {
                color = parsed;
            }
        }

        if (lengths.Count < 2)
        {
            return null;
        }

        return new BoxShadow(
            lengths[0],
            lengths[1],
            lengths.Count > 2 ? lengths[2] : 0f,
            lengths.Count > 3 ? lengths[3] : 0f,
            color ?? currentColor ?? new RgbaColor(0, 0, 0, 255),
            inset);
    }
}
