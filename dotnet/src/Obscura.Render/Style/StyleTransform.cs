// transform / translate / rotate / scale, box alignment and the flex shorthands.
using Obscura.Render.Css;

namespace Obscura.Render;

public static partial class ComputedStyle
{
    /// <summary>Rust <c>set_containing_block_trigger</c>.</summary>
    internal static void SetContainingBlockTrigger(LayoutStyle style, ushort trigger, bool enabled)
    {
        if (enabled)
        {
            style.ContainingBlockTriggers |= trigger;
        }
        else
        {
            style.ContainingBlockTriggers &= (ushort)~trigger;
        }
    }

    /// <summary>Rust <c>transform_functions</c>.</summary>
    internal static List<(string Name, string Arguments)> TransformFunctions(string value)
    {
        List<(string Name, string Arguments)> output = [];
        int cursor = 0;
        while (cursor < value.Length)
        {
            while (cursor < value.Length && CssText.IsAsciiWhitespace(value[cursor]))
            {
                cursor++;
            }

            if (cursor == value.Length)
            {
                break;
            }

            int nameStart = cursor;
            while (cursor < value.Length
                && (CssText.IsAsciiAlphanumeric(value[cursor]) || value[cursor] == '-'))
            {
                cursor++;
            }

            if (cursor == nameStart || cursor >= value.Length || value[cursor] != '(')
            {
                return [];
            }

            string name = value[nameStart..cursor];
            cursor++;
            int argumentsStart = cursor;
            int depth = 1;
            int? end = null;
            for (int index = cursor; index < value.Length; index++)
            {
                if (value[index] == '(')
                {
                    depth++;
                }
                else if (value[index] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = index;
                        break;
                    }
                }
            }

            if (end is not { } close)
            {
                return [];
            }

            output.Add((name, value[argumentsStart..close]));
            cursor = close + 1;
        }

        return output;
    }

    /// <summary>Rust <c>parse_transform_length</c>.</summary>
    internal static TransformLength? ParseTransformLength(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Contains('('))
        {
            List<(string Name, string Arguments)> functions = TransformFunctions(trimmed);
            if (functions.Count != 1)
            {
                return null;
            }

            (string name, string arguments) = functions[0];
            bool valid;
            switch (CssText.AsciiLower(name))
            {
                case "calc":
                case "min":
                case "max":
                case "clamp":
                    valid = ResolveContextualLength(trimmed, 16f, 16f, 10f, 10f, 100f) is { } resolved
                        && float.IsFinite(resolved);
                    break;
                case "var":
                    List<string> parts = SplitTopLevel(arguments, ',');
                    valid = parts.Count is >= 1 and <= 2
                        && parts[0].Trim().StartsWith("--", StringComparison.Ordinal)
                        && parts[0].Trim().Length > 2
                        && (parts.Count < 2
                            || (parts[1].Trim().Length != 0 && ParseTransformLength(parts[1]) is not null));
                    break;
                default:
                    valid = false;
                    break;
            }

            if (!valid)
            {
                return null;
            }

            return new TransformLength(Dimension.Px(0f), trimmed);
        }

        Dimension dimension = DimensionValue(trimmed);
        return dimension.IsAuto ? null : new TransformLength(dimension, null);
    }

    /// <summary>Rust <c>parse_transform_z_length</c>.</summary>
    private static TransformLength? ParseTransformZLength(string value) =>
        value.Contains('%') ? null : ParseTransformLength(value);

    /// <summary>Rust <c>parse_transform_ops</c>.</summary>
    internal static List<TransformOp>? ParseTransformOps(string value)
    {
        List<(string Name, string Arguments)> functions = TransformFunctions(value);
        if (functions.Count == 0)
        {
            return null;
        }

        List<TransformOp> operations = new(functions.Count);
        foreach ((string name, string arguments) in functions)
        {
            List<string> values = [];
            foreach (string argument in SplitTopLevel(arguments, ','))
            {
                string trimmed = argument.Trim();
                if (trimmed.Length != 0)
                {
                    values.Add(trimmed);
                }
            }

            TransformOp? operation = BuildTransformOp(CssText.AsciiLower(name), values);
            if (operation is null)
            {
                return null;
            }

            operations.Add(operation);
        }

        return operations;
    }

    private static TransformOp? BuildTransformOp(string name, List<string> values)
    {
        switch (name)
        {
            case "translate" when values.Count is 1 or 2:
            {
                TransformLength y = TransformLength.Px(0f);
                if (values.Count > 1)
                {
                    if (ParseTransformLength(values[1]) is not { } parsed)
                    {
                        return null;
                    }

                    y = parsed;
                }

                return ParseTransformLength(values[0]) is { } x ? new TransformOp.Translate(x, y) : null;
            }

            case "translatex" when values.Count == 1:
                return ParseTransformLength(values[0]) is { } tx
                    ? new TransformOp.Translate(tx, TransformLength.Px(0f))
                    : null;

            case "translatey" when values.Count == 1:
                return ParseTransformLength(values[0]) is { } ty
                    ? new TransformOp.Translate(TransformLength.Px(0f), ty)
                    : null;

            case "translate3d" when values.Count == 3:
            {
                // Without perspective, translation along Z does not change the
                // projection of the element's plane.
                if (ParseTransformZLength(values[2]) is null
                    || ParseTransformLength(values[0]) is not { } x
                    || ParseTransformLength(values[1]) is not { } y)
                {
                    return null;
                }

                return new TransformOp.Translate(x, y);
            }

            case "translatez" when values.Count == 1:
                return ParseTransformZLength(values[0]) is null
                    ? null
                    : new TransformOp.Matrix(Affine2.Identity);

            case "scale" when values.Count is 1 or 2:
            {
                if (ScaleNumber(values[0]) is not { } x)
                {
                    return null;
                }

                float y = values.Count > 1 ? ScaleNumber(values[1]) ?? x : x;
                return new TransformOp.Scale(x, y);
            }

            case "scalex" when values.Count == 1:
                return ScaleNumber(values[0]) is { } sx ? new TransformOp.Scale(sx, 1f) : null;

            case "scaley" when values.Count == 1:
                return ScaleNumber(values[0]) is { } sy ? new TransformOp.Scale(1f, sy) : null;

            case "scale3d" when values.Count == 3:
            {
                if (ScaleNumber(values[0]) is not { } x
                    || ScaleNumber(values[1]) is not { } y
                    || ScaleNumber(values[2]) is null)
                {
                    return null;
                }

                return new TransformOp.Scale(x, y);
            }

            case "scalez" when values.Count == 1 && ScaleNumber(values[0]) is not null:
                return new TransformOp.Matrix(Affine2.Identity);

            case "rotate" when values.Count == 1:
            case "rotatez" when values.Count == 1:
                return AngleDegrees(values[0]) is { } rotation ? new TransformOp.Rotate(rotation) : null;

            case "rotatex" when values.Count == 1:
                return AngleDegrees(values[0]) is { } rx
                    ? new TransformOp.Scale(1f, MathF.Cos(F32.ToRadians(rx)))
                    : null;

            case "rotatey" when values.Count == 1:
                return AngleDegrees(values[0]) is { } ry
                    ? new TransformOp.Scale(MathF.Cos(F32.ToRadians(ry)), 1f)
                    : null;

            case "rotate3d" when values.Count == 4:
            {
                if (ParseF32(values[0]) is not { } x
                    || ParseF32(values[1]) is not { } y
                    || ParseF32(values[2]) is not { } z)
                {
                    return null;
                }

                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                {
                    return null;
                }

                if (AngleDegrees(values[3]) is not { } angle)
                {
                    return null;
                }

                if (x != 0f && y == 0f && z == 0f)
                {
                    return new TransformOp.Scale(1f, MathF.Cos(F32.ToRadians(angle * MathF.CopySign(1f, x))));
                }

                if (x == 0f && y != 0f && z == 0f)
                {
                    return new TransformOp.Scale(MathF.Cos(F32.ToRadians(angle * MathF.CopySign(1f, y))), 1f);
                }

                if (x == 0f && y == 0f && z != 0f)
                {
                    return new TransformOp.Rotate(angle * MathF.CopySign(1f, z));
                }

                // A mixed 3D rotation axis cannot be represented affinely.
                return null;
            }

            case "skew" when values.Count is 1 or 2:
            {
                if (AngleDegrees(values[0]) is not { } x)
                {
                    return null;
                }

                float y = values.Count > 1 ? AngleDegrees(values[1]) ?? 0f : 0f;
                return new TransformOp.Skew(x, y);
            }

            case "skewx" when values.Count == 1:
                return AngleDegrees(values[0]) is { } skewX ? new TransformOp.Skew(skewX, 0f) : null;

            case "skewy" when values.Count == 1:
                return AngleDegrees(values[0]) is { } skewY ? new TransformOp.Skew(0f, skewY) : null;

            case "matrix" when values.Count == 6:
            {
                float[] numbers = new float[6];
                for (int index = 0; index < 6; index++)
                {
                    if (ParseF32(values[index]) is not { } number || !float.IsFinite(number))
                    {
                        return null;
                    }

                    numbers[index] = number;
                }

                return new TransformOp.Matrix(new Affine2(
                    numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]));
            }

            case "matrix3d" when values.Count == 16:
            {
                float[] numbers = new float[16];
                for (int index = 0; index < 16; index++)
                {
                    if (ParseF32(values[index]) is not { } number || !float.IsFinite(number))
                    {
                        return null;
                    }

                    numbers[index] = number;
                }

                if (numbers[2] != 0f || numbers[3] != 0f || numbers[6] != 0f || numbers[7] != 0f
                    || numbers[8] != 0f || numbers[9] != 0f || numbers[10] != 1f || numbers[11] != 0f
                    || numbers[14] != 0f || numbers[15] != 1f)
                {
                    return null;
                }

                return new TransformOp.Matrix(new Affine2(
                    numbers[0], numbers[1], numbers[4], numbers[5], numbers[12], numbers[13]));
            }

            default:
                return null;
        }
    }

    /// <summary>Rust <c>parse_transform</c>.</summary>
    internal static void ParseTransform(LayoutStyle style, string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        if (CssText.AsciiLower(trimmed) is "none" or "initial" or "unset" or "revert" or "revert-layer")
        {
            style.TransformOps.Clear();
            SetContainingBlockTrigger(style, ContainingBlockTrigger.Transform, false);
            return;
        }

        if (ParseTransformOps(trimmed) is not { } operations)
        {
            return;
        }

        style.TransformOps = operations;
        SetContainingBlockTrigger(style, ContainingBlockTrigger.Transform, true);
    }

    /// <summary>Rust <c>parse_transform_origin</c>.</summary>
    internal static (Dimension X, Dimension Y)? ParseTransformOrigin(string value)
    {
        Dimension? x = null;
        Dimension? y = null;
        List<string> tokens = SplitWhitespace(value);
        int count = Math.Min(tokens.Count, 2);
        for (int index = 0; index < count; index++)
        {
            string token = tokens[index];
            switch (CssText.AsciiLower(token))
            {
                case "left":
                    x = Dimension.Percent(0f);
                    break;
                case "right":
                    x = Dimension.Percent(1f);
                    break;
                case "top":
                    y = Dimension.Percent(0f);
                    break;
                case "bottom":
                    y = Dimension.Percent(1f);
                    break;
                case "center":
                    if (x is null)
                    {
                        x = Dimension.Percent(0.5f);
                    }
                    else
                    {
                        y = Dimension.Percent(0.5f);
                    }

                    break;
                default:
                    if (x is null)
                    {
                        x = DimensionValue(token);
                    }
                    else
                    {
                        y = DimensionValue(token);
                    }

                    break;
            }
        }

        if (x is null && y is null)
        {
            return null;
        }

        return (x ?? Dimension.Percent(0.5f), y ?? Dimension.Percent(0.5f));
    }

    /// <summary>Rust <c>parse_individual_scale</c>.</summary>
    internal static void ParseIndividualScale(LayoutStyle style, string value)
    {
        if (CssText.AsciiLower(value.Trim()) is "none" or "initial" or "unset" or "revert" or "revert-layer")
        {
            style.IndividualScale = null;
            SetContainingBlockTrigger(style, ContainingBlockTrigger.Scale, false);
            return;
        }

        List<float> values = [];
        List<string> tokens = SplitWhitespace(value);
        for (int index = 0; index < tokens.Count && index < 2; index++)
        {
            if (ScaleNumber(tokens[index]) is { } number)
            {
                values.Add(number);
            }
        }

        if (values.Count == 0)
        {
            return;
        }

        float x = values[0];
        float y = values.Count > 1 ? values[1] : x;
        style.IndividualScale = (x, y);
        SetContainingBlockTrigger(style, ContainingBlockTrigger.Scale, true);
    }

    /// <summary>Rust <c>parse_individual_rotate</c>.</summary>
    internal static void ParseIndividualRotate(LayoutStyle style, string value)
    {
        string trimmed = value.Trim();
        if (CssText.AsciiLower(trimmed) is "none" or "initial" or "unset" or "revert" or "revert-layer")
        {
            style.IndividualRotate = null;
            SetContainingBlockTrigger(style, ContainingBlockTrigger.Rotate, false);
            return;
        }

        if (AngleDegrees(trimmed) is not { } angle)
        {
            return;
        }

        style.IndividualRotate = angle;
        SetContainingBlockTrigger(style, ContainingBlockTrigger.Rotate, true);
    }

    /// <summary>Rust <c>parse_individual_translate</c>.</summary>
    internal static void ParseIndividualTranslate(LayoutStyle style, string value)
    {
        string trimmed = value.Trim();
        if (CssText.AsciiLower(trimmed) is "none" or "initial" or "unset" or "revert" or "revert-layer")
        {
            style.IndividualTranslate = null;
            style.IndividualTranslateExpressions[0] = null;
            style.IndividualTranslateExpressions[1] = null;
            SetContainingBlockTrigger(style, ContainingBlockTrigger.Translate, false);
            return;
        }

        List<string> values = SplitWsParen(trimmed);
        if (values.Count == 0 || values.Count > 3)
        {
            return;
        }

        if (Component(values[0]) is not { } first)
        {
            return;
        }

        (Dimension Value, string? Expression) second = (Dimension.Px(0f), null);
        if (values.Count > 1)
        {
            if (Component(values[1]) is not { } parsed)
            {
                return;
            }

            second = parsed;
        }

        // The optional third component is a Z translation; the renderer is 2D but
        // accepting a valid value still preserves x/y.
        if (values.Count > 2 && Component(values[2]) is null)
        {
            return;
        }

        style.IndividualTranslate = (first.Value, second.Value);
        style.IndividualTranslateExpressions[0] = first.Expression;
        style.IndividualTranslateExpressions[1] = second.Expression;
        SetContainingBlockTrigger(style, ContainingBlockTrigger.Translate, true);

        static (Dimension Value, string? Expression)? Component(string token)
        {
            if (token.Contains('('))
            {
                return (Dimension.Px(0f), token.Trim());
            }

            Dimension dimension = DimensionValue(token);
            return dimension.IsAuto ? null : (dimension, (string?)null);
        }
    }

    // ------------------------------------------------------- box alignment

    /// <summary>
    /// Rust <c>self_alignment_value</c>. The outer null is an invalid declaration;
    /// an inner null is <c>auto</c>, which resets to the inherited behavior.
    /// </summary>
    internal static (Layout.AlignItems? Value, bool Valid) SelfAlignmentValue(string value)
    {
        string normalized = CssText.AsciiLower(value.Trim());
        Layout.AlignItems alignment;
        switch (normalized)
        {
            case "auto":
                return (null, true);
            case "normal":
                alignment = Layout.AlignItems.Normal;
                break;
            case "start":
            case "self-start":
                alignment = Layout.AlignItems.Start;
                break;
            case "end":
            case "self-end":
                alignment = Layout.AlignItems.End;
                break;
            case "flex-start":
                alignment = Layout.AlignItems.FlexStart;
                break;
            case "flex-end":
                alignment = Layout.AlignItems.FlexEnd;
                break;
            case "center":
                alignment = Layout.AlignItems.Center;
                break;
            case "baseline":
            case "first baseline":
                alignment = Layout.AlignItems.Baseline;
                break;
            case "stretch":
                alignment = Layout.AlignItems.Stretch;
                break;
            case "safe start":
            case "safe self-start":
                alignment = Layout.AlignItems.SafeStart;
                break;
            case "safe end":
            case "safe self-end":
                alignment = Layout.AlignItems.SafeEnd;
                break;
            case "safe flex-start":
                alignment = Layout.AlignItems.SafeFlexStart;
                break;
            case "safe flex-end":
                alignment = Layout.AlignItems.SafeFlexEnd;
                break;
            case "safe center":
                alignment = Layout.AlignItems.SafeCenter;
                break;
            case "unsafe start":
            case "unsafe self-start":
                alignment = Layout.AlignItems.Start;
                break;
            case "unsafe end":
            case "unsafe self-end":
                alignment = Layout.AlignItems.End;
                break;
            case "unsafe flex-start":
                alignment = Layout.AlignItems.FlexStart;
                break;
            case "unsafe flex-end":
                alignment = Layout.AlignItems.FlexEnd;
                break;
            case "unsafe center":
                alignment = Layout.AlignItems.Center;
                break;
            default:
                return (null, false);
        }

        return (alignment, true);
    }

    /// <summary>Rust <c>self_alignment_pair</c>.</summary>
    internal static (Layout.AlignItems? Align, Layout.AlignItems? Justify, bool Valid) SelfAlignmentPair(string value)
    {
        (Layout.AlignItems? single, bool valid) = SelfAlignmentValue(value);
        if (valid)
        {
            return (single, single, true);
        }

        List<string> tokens = SplitWhitespace(value);
        for (int split = 1; split < tokens.Count; split++)
        {
            string alignText = string.Join(" ", tokens.GetRange(0, split));
            string justifyText = string.Join(" ", tokens.GetRange(split, tokens.Count - split));
            (Layout.AlignItems? align, bool alignValid) = SelfAlignmentValue(alignText);
            (Layout.AlignItems? justify, bool justifyValid) = SelfAlignmentValue(justifyText);
            if (alignValid && justifyValid)
            {
                return (align, justify, true);
            }
        }

        return (null, null, false);
    }

    /// <summary>Rust <c>content_alignment_value</c>.</summary>
    internal static Layout.AlignContent? ContentAlignmentValue(string value) =>
        CssText.AsciiLower(value.Trim()) switch
        {
            "normal" or "stretch" => Layout.AlignContent.Stretch,
            "start" => Layout.AlignContent.Start,
            "end" => Layout.AlignContent.End,
            "flex-start" => Layout.AlignContent.FlexStart,
            "flex-end" => Layout.AlignContent.FlexEnd,
            "center" => Layout.AlignContent.Center,
            "space-between" => Layout.AlignContent.SpaceBetween,
            "space-around" => Layout.AlignContent.SpaceAround,
            "space-evenly" => Layout.AlignContent.SpaceEvenly,
            "safe start" => Layout.AlignContent.SafeStart,
            "safe end" => Layout.AlignContent.SafeEnd,
            "safe flex-start" => Layout.AlignContent.SafeFlexStart,
            "safe flex-end" => Layout.AlignContent.SafeFlexEnd,
            "safe center" => Layout.AlignContent.SafeCenter,
            "unsafe start" => Layout.AlignContent.Start,
            "unsafe end" => Layout.AlignContent.End,
            "unsafe flex-start" => Layout.AlignContent.FlexStart,
            "unsafe flex-end" => Layout.AlignContent.FlexEnd,
            "unsafe center" => Layout.AlignContent.Center,
            _ => null,
        };

    /// <summary>Rust <c>content_alignment_pair</c>.</summary>
    internal static (Layout.AlignContent Align, Layout.AlignContent Justify)? ContentAlignmentPair(string value)
    {
        if (ContentAlignmentValue(value) is { } single)
        {
            return (single, single);
        }

        List<string> tokens = SplitWhitespace(value);
        for (int split = 1; split < tokens.Count; split++)
        {
            string alignText = string.Join(" ", tokens.GetRange(0, split));
            string justifyText = string.Join(" ", tokens.GetRange(split, tokens.Count - split));
            if (ContentAlignmentValue(alignText) is { } align && ContentAlignmentValue(justifyText) is { } justify)
            {
                return (align, justify);
            }
        }

        return null;
    }

    // -------------------------------------------------------------- flex

    /// <summary>Rust <c>parse_flex_shorthand</c>.</summary>
    internal static void ParseFlexShorthand(LayoutStyle style, string value)
    {
        switch (value.Trim())
        {
            case "none":
                style.FlexGrow = 0f;
                style.FlexShrink = 0f;
                style.FlexBasis = Dimension.Auto;
                return;
            case "auto":
                style.FlexGrow = 1f;
                style.FlexShrink = 1f;
                style.FlexBasis = Dimension.Auto;
                return;
            case "initial":
                style.FlexGrow = 0f;
                style.FlexShrink = 1f;
                style.FlexBasis = Dimension.Auto;
                return;
        }

        List<float> numbers = [];
        Dimension? basis = null;
        foreach (string token in SplitWhitespace(value))
        {
            if (ParseF32(token) is { } number)
            {
                if (numbers.Count < 2)
                {
                    numbers.Add(number);
                }
                else
                {
                    basis = DimensionValue(token);
                }
            }
            else
            {
                basis = DimensionValue(token);
            }
        }

        if (numbers.Count == 1)
        {
            style.FlexGrow = numbers[0];
            style.FlexShrink = 1f;
        }
        else if (numbers.Count >= 2)
        {
            style.FlexGrow = numbers[0];
            style.FlexShrink = numbers[1];
        }

        if (basis is { } explicitBasis)
        {
            style.FlexBasis = explicitBasis;
        }
        else if (numbers.Count != 0)
        {
            style.FlexBasis = Dimension.Px(0f);
        }
        else
        {
            style.FlexGrow = 1f;
            style.FlexShrink = 1f;
            style.FlexBasis = Dimension.Auto;
        }
    }

    /// <summary>Rust <c>parse_flex_flow_shorthand</c>.</summary>
    internal static (Layout.FlexDirection Direction, Layout.FlexWrap Wrap)? ParseFlexFlowShorthand(string value)
    {
        string lower = CssText.AsciiLower(value.Trim());
        if (lower is "initial" or "unset" or "revert" or "revert-layer")
        {
            return (Layout.FlexDirection.Row, Layout.FlexWrap.NoWrap);
        }

        List<string> tokens = SplitWhitespace(lower);
        if (tokens.Count == 0 || tokens.Count > 2)
        {
            return null;
        }

        Layout.FlexDirection? direction = null;
        Layout.FlexWrap? wrap = null;
        foreach (string token in tokens)
        {
            Layout.FlexDirection? parsedDirection = token switch
            {
                "row" => Layout.FlexDirection.Row,
                "row-reverse" => Layout.FlexDirection.RowReverse,
                "column" => Layout.FlexDirection.Column,
                "column-reverse" => Layout.FlexDirection.ColumnReverse,
                _ => null,
            };
            if (parsedDirection is { } newDirection)
            {
                if (direction is not null)
                {
                    return null;
                }

                direction = newDirection;
                continue;
            }

            Layout.FlexWrap? parsedWrap = token switch
            {
                "nowrap" => Layout.FlexWrap.NoWrap,
                "wrap" => Layout.FlexWrap.Wrap,
                "wrap-reverse" => Layout.FlexWrap.WrapReverse,
                _ => null,
            };
            if (parsedWrap is { } newWrap)
            {
                if (wrap is not null)
                {
                    return null;
                }

                wrap = newWrap;
            }
            else
            {
                return null;
            }
        }

        return (direction ?? Layout.FlexDirection.Row, wrap ?? Layout.FlexWrap.NoWrap);
    }
}
