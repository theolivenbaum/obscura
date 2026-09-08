// The `animation` shorthand and its longhands, including the small time-valued
// calc() evaluator style.rs uses for `animation-delay: calc(.1s * -2.5)`.
using Obscura.Render.Css;

namespace Obscura.Render;

public static partial class ComputedStyle
{
    /// <summary>Rust <c>apply_animation_shorthand</c>.</summary>
    internal static void ApplyAnimationShorthand(LayoutStyle style, string value)
    {
        List<string> layers = SplitTopLevel(value, ',');
        string first = (layers.Count > 0 ? layers[0] : string.Empty).Trim();
        if (first.Length == 0)
        {
            return;
        }

        if (CssText.AsciiLower(first) is "initial" or "inherit" or "unset" or "revert" or "revert-layer")
        {
            style.AnimationName = null;
            style.AnimationTiming = AnimationTiming.Default;
            return;
        }

        string? name = null;
        AnimationTiming timing = AnimationTiming.Default;
        int timeCount = 0;
        foreach (string token in SplitWsParen(first))
        {
            string lower = CssText.AsciiLower(token);
            bool timingKeyword = lower is "linear" or "ease" or "ease-in" or "ease-out" or "ease-in-out"
                || lower.StartsWith("cubic-bezier(", StringComparison.Ordinal)
                || lower.StartsWith("steps(", StringComparison.Ordinal)
                || lower.StartsWith("linear(", StringComparison.Ordinal);

            if (ParseAnimationTimeMs(lower) is { } time)
            {
                if (timeCount == 0)
                {
                    if (time < 0f)
                    {
                        return;
                    }

                    timing = timing with { DurationMs = time };
                }
                else if (timeCount == 1)
                {
                    timing = timing with { DelayMs = time };
                }
                else
                {
                    return;
                }

                timeCount++;
            }
            else if (ParseAnimationDirection(lower) is { } direction)
            {
                timing = timing with { Direction = direction };
            }
            else if (ParseAnimationFillMode(lower) is { } fill)
            {
                timing = timing with { FillMode = fill };
            }
            else if (ParseAnimationPlayState(lower) is { } playState)
            {
                timing = timing with { PlayState = playState };
            }
            else if (ParseAnimationIterationCount(lower) is { } iterations)
            {
                timing = timing with { IterationCount = iterations };
            }
            else if (timingKeyword)
            {
                continue;
            }
            else if (lower == "none")
            {
                if (name is not null)
                {
                    return;
                }
            }
            else if (name is null)
            {
                name = token;
            }
            else
            {
                return;
            }
        }

        style.AnimationName = name;
        style.AnimationTiming = timing;
    }

    /// <summary>Rust <c>parse_animation_direction</c>.</summary>
    internal static AnimationDirection? ParseAnimationDirection(string value) =>
        CssText.AsciiLower(value.Trim()) switch
        {
            "normal" => AnimationDirection.Normal,
            "reverse" => AnimationDirection.Reverse,
            "alternate" => AnimationDirection.Alternate,
            "alternate-reverse" => AnimationDirection.AlternateReverse,
            _ => null,
        };

    /// <summary>Rust <c>parse_animation_fill_mode</c>.</summary>
    internal static AnimationFillMode? ParseAnimationFillMode(string value) =>
        CssText.AsciiLower(value.Trim()) switch
        {
            "none" => AnimationFillMode.None,
            "forwards" => AnimationFillMode.Forwards,
            "backwards" => AnimationFillMode.Backwards,
            "both" => AnimationFillMode.Both,
            _ => null,
        };

    /// <summary>Rust <c>parse_animation_play_state</c>.</summary>
    internal static AnimationPlayState? ParseAnimationPlayState(string value) =>
        CssText.AsciiLower(value.Trim()) switch
        {
            "running" => AnimationPlayState.Running,
            "paused" => AnimationPlayState.Paused,
            _ => null,
        };

    /// <summary>Rust <c>parse_animation_iteration_count</c>.</summary>
    internal static float? ParseAnimationIterationCount(string value)
    {
        string trimmed = value.Trim();
        if (CssText.EqualsAscii(trimmed, "infinite"))
        {
            return float.PositiveInfinity;
        }

        return ParseF32(trimmed) is { } count && float.IsFinite(count) && count >= 0f ? count : null;
    }

    private enum AnimationCalcUnit
    {
        Number,
        Milliseconds,
    }

    private readonly record struct AnimationCalcValue(AnimationCalcUnit Unit, float Value)
    {
        public AnimationCalcValue Negated() => this with { Value = -Value };

        public AnimationCalcValue? Add(AnimationCalcValue other, bool subtract)
        {
            float sign = subtract ? -1f : 1f;
            if (Unit != other.Unit)
            {
                return null;
            }

            return this with { Value = Value + (sign * other.Value) };
        }

        public AnimationCalcValue? Multiply(AnimationCalcValue other)
        {
            if (Unit == AnimationCalcUnit.Number && other.Unit == AnimationCalcUnit.Number)
            {
                return new AnimationCalcValue(AnimationCalcUnit.Number, Value * other.Value);
            }

            if (Unit == AnimationCalcUnit.Milliseconds && other.Unit == AnimationCalcUnit.Number)
            {
                return new AnimationCalcValue(AnimationCalcUnit.Milliseconds, Value * other.Value);
            }

            if (Unit == AnimationCalcUnit.Number && other.Unit == AnimationCalcUnit.Milliseconds)
            {
                return new AnimationCalcValue(AnimationCalcUnit.Milliseconds, other.Value * Value);
            }

            return null;
        }

        public AnimationCalcValue? Divide(AnimationCalcValue other)
        {
            if (other.Unit == AnimationCalcUnit.Number && other.Value == 0f)
            {
                return null;
            }

            if (Unit == AnimationCalcUnit.Number && other.Unit == AnimationCalcUnit.Number)
            {
                return new AnimationCalcValue(AnimationCalcUnit.Number, Value / other.Value);
            }

            if (Unit == AnimationCalcUnit.Milliseconds && other.Unit == AnimationCalcUnit.Number)
            {
                return new AnimationCalcValue(AnimationCalcUnit.Milliseconds, Value / other.Value);
            }

            if (Unit == AnimationCalcUnit.Milliseconds && other.Unit == AnimationCalcUnit.Milliseconds
                && other.Value != 0f)
            {
                return new AnimationCalcValue(AnimationCalcUnit.Number, Value / other.Value);
            }

            return null;
        }
    }

    /// <summary>Rust <c>AnimationCalcParser</c>.</summary>
    private struct AnimationCalcParser(string input)
    {
        private readonly string _input = input;
        private int _position;

        public readonly int Position => _position;

        public readonly int Length => _input.Length;

        public void SkipWhitespace()
        {
            while (_position < _input.Length && CssText.IsAsciiWhitespace(_input[_position]))
            {
                _position++;
            }
        }

        private bool Consume(char character)
        {
            SkipWhitespace();
            if (_position < _input.Length && _input[_position] == character)
            {
                _position++;
                return true;
            }

            return false;
        }

        public AnimationCalcValue? Sum()
        {
            if (Product() is not { } value)
            {
                return null;
            }

            while (true)
            {
                if (Consume('+'))
                {
                    if (Product() is not { } right || value.Add(right, false) is not { } sum)
                    {
                        return null;
                    }

                    value = sum;
                }
                else if (Consume('-'))
                {
                    if (Product() is not { } right || value.Add(right, true) is not { } difference)
                    {
                        return null;
                    }

                    value = difference;
                }
                else
                {
                    return value;
                }
            }
        }

        private AnimationCalcValue? Product()
        {
            if (Unary() is not { } value)
            {
                return null;
            }

            while (true)
            {
                if (Consume('*'))
                {
                    if (Unary() is not { } right || value.Multiply(right) is not { } product)
                    {
                        return null;
                    }

                    value = product;
                }
                else if (Consume('/'))
                {
                    if (Unary() is not { } right || value.Divide(right) is not { } quotient)
                    {
                        return null;
                    }

                    value = quotient;
                }
                else
                {
                    return value;
                }
            }
        }

        private AnimationCalcValue? Unary()
        {
            if (Consume('+'))
            {
                return Unary();
            }

            if (Consume('-'))
            {
                return Unary() is { } value ? value.Negated() : null;
            }

            return Primary();
        }

        private AnimationCalcValue? Primary()
        {
            if (Consume('('))
            {
                if (Sum() is not { } value)
                {
                    return null;
                }

                return Consume(')') ? value : null;
            }

            return Number();
        }

        private AnimationCalcValue? Number()
        {
            SkipWhitespace();
            int start = _position;
            bool sawDigit = false;
            while (_position < _input.Length)
            {
                char character = _input[_position];
                if (CssText.IsAsciiDigit(character))
                {
                    sawDigit = true;
                    _position++;
                }
                else if (character == '.')
                {
                    _position++;
                }
                else
                {
                    break;
                }
            }

            if (!sawDigit)
            {
                return null;
            }

            if (ParseF32(_input[start.._position]) is not { } number)
            {
                return null;
            }

            if (_position + 2 <= _input.Length && _input.AsSpan(_position, 2).SequenceEqual("ms"))
            {
                _position += 2;
                return new AnimationCalcValue(AnimationCalcUnit.Milliseconds, number);
            }

            if (_position < _input.Length && _input[_position] == 's')
            {
                _position++;
                return new AnimationCalcValue(AnimationCalcUnit.Milliseconds, number * 1000f);
            }

            return new AnimationCalcValue(AnimationCalcUnit.Number, number);
        }
    }

    /// <summary>Rust <c>parse_animation_time_ms</c>.</summary>
    public static float? ParseAnimationTimeMs(string value)
    {
        string normalized = CssText.AsciiLower(value.Trim());
        bool isCalc = normalized.StartsWith("calc(", StringComparison.Ordinal) && normalized.EndsWith(')');
        if (!isCalc)
        {
            foreach (char character in normalized)
            {
                if (character is '+' or '*' or '/' or '(' or ')')
                {
                    return null;
                }
            }
        }

        string expression = isCalc ? normalized[5..^1] : normalized;
        AnimationCalcParser parser = new(expression);
        if (parser.Sum() is not { } result)
        {
            return null;
        }

        parser.SkipWhitespace();
        if (parser.Position != parser.Length)
        {
            return null;
        }

        return result.Unit == AnimationCalcUnit.Milliseconds && float.IsFinite(result.Value)
            ? result.Value
            : null;
    }
}
