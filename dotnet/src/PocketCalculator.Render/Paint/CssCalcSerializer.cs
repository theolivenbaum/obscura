// The computed-value serialization of a CSS math expression (css-values-4 "Serializing
// calculations"), for the getComputedStyle() values that keep a percentage: grid tracks.
// crates/obscura-render has no getComputedStyle for them at all; see GridCssValues.
using System.Globalization;
using System.Text;

namespace PocketCalculator.Render;

/// <summary>The sizes relative units resolve against at computed-value time.</summary>
internal readonly record struct CalcUnits(FontUnits Font, float RemPx, float VwPx, float VhPx);

/// <summary>
/// Serializes a <c>calc()</c>/<c>min()</c>/<c>max()</c>/<c>clamp()</c>/<c>round()</c>
/// expression the way Chromium 141 serializes a computed value: every absolute or font- and
/// viewport-relative length is made px, like terms are summed, a sum is ordered percentage
/// then px then functions, zero terms are dropped, a function whose arguments are all of one
/// kind is evaluated, and a lone term loses its <c>calc()</c>: <c>calc(50px + 10%)</c> is
/// <c>calc(10% + 50px)</c>, <c>calc(1em + 10px)</c> at 40px is <c>50px</c>.
/// </summary>
internal static class CssCalcSerializer
{
    /// <summary>
    /// The computed serialization of <paramref name="expression"/>, or the expression as
    /// written (lowercased) when it holds something this does not model.
    /// </summary>
    public static string Serialize(string expression, in CalcUnits units)
    {
        string lower = Css.CssText.AsciiLower(expression.Trim());
        if (!Css.CssLength.CssMathNestingIsSafe(lower))
        {
            return lower;
        }

        Parser parser = new(lower, units);
        Node? node = parser.ParseSum();
        parser.SkipWhitespace();
        if (node is null || !parser.AtEnd || node.Kind == NodeKind.Number)
        {
            return lower;
        }

        return Top(node);
    }

    private enum NodeKind : byte
    {
        Number,
        Linear,
        Function,
    }

    /// <summary>
    /// A number, or <c>Pct% + Px px + sum(Coefficient * Function)</c>. A function node has
    /// its name, its round() strategy and its simplified arguments.
    /// </summary>
    private sealed class Node
    {
        public NodeKind Kind;
        public float Value;
        public float Pct;
        public float Px;
        public List<(float Coefficient, Node Function)>? Terms;
        public string? Name;
        public string? Strategy;
        public List<Node>? Arguments;

        public static Node Number(float value) => new() { Kind = NodeKind.Number, Value = value };

        public static Node Linear(float pct, float px) => new() { Kind = NodeKind.Linear, Pct = pct, Px = px };

        public bool IsPureLinear => Kind == NodeKind.Linear && Terms is not { Count: > 0 };
    }

    private static Node? Add(Node a, Node b, float sign)
    {
        if (a.Kind == NodeKind.Number && b.Kind == NodeKind.Number)
        {
            return Node.Number(a.Value + (sign * b.Value));
        }

        if (a.Kind == NodeKind.Number || b.Kind == NodeKind.Number)
        {
            return null;
        }

        Node left = AsLinear(a);
        Node right = AsLinear(b);
        Node sum = Node.Linear(left.Pct + (sign * right.Pct), left.Px + (sign * right.Px));
        if (left.Terms is { Count: > 0 } || right.Terms is { Count: > 0 })
        {
            sum.Terms = [.. left.Terms ?? []];
            foreach ((float coefficient, Node function) in right.Terms ?? [])
            {
                sum.Terms.Add((sign * coefficient, function));
            }
        }

        return sum;
    }

    /// <summary>A function node as the sum <c>1 * f</c>; a linear node as itself.</summary>
    private static Node AsLinear(Node node)
    {
        if (node.Kind == NodeKind.Function)
        {
            Node sum = Node.Linear(0f, 0f);
            sum.Terms = [(1f, node)];
            return sum;
        }

        return node;
    }

    private static Node? Scale(Node node, float factor)
    {
        switch (node.Kind)
        {
            case NodeKind.Number:
                return Node.Number(node.Value * factor);
            case NodeKind.Function:
                return Scale(AsLinear(node), factor);
            default:
                Node scaled = Node.Linear(node.Pct * factor, node.Px * factor);
                if (node.Terms is { Count: > 0 } terms)
                {
                    scaled.Terms = [];
                    foreach ((float coefficient, Node function) in terms)
                    {
                        scaled.Terms.Add((coefficient * factor, function));
                    }
                }

                return scaled;
        }
    }

    private static Node? MakeFunction(string name, string? strategy, List<Node> arguments)
    {
        bool allPx = true;
        bool allPct = true;
        foreach (Node argument in arguments)
        {
            if (!argument.IsPureLinear)
            {
                allPx = false;
                allPct = false;
                break;
            }

            allPx &= argument.Pct == 0f;
            allPct &= argument.Px == 0f;
        }

        if (name is "min" or "max" && (allPx || allPct))
        {
            float best = allPx ? arguments[0].Px : arguments[0].Pct;
            foreach (Node argument in arguments)
            {
                float value = allPx ? argument.Px : argument.Pct;
                best = name == "min" ? MathF.Min(best, value) : MathF.Max(best, value);
            }

            return allPx ? Node.Linear(0f, best) : Node.Linear(best, 0f);
        }

        if (name == "clamp" && arguments.Count == 3 && (allPx || allPct))
        {
            float Pick(Node n) => allPx ? n.Px : n.Pct;
            float value = MathF.Max(MathF.Min(Pick(arguments[1]), Pick(arguments[2])), Pick(arguments[0]));
            return allPx ? Node.Linear(0f, value) : Node.Linear(value, 0f);
        }

        if (name == "round" && arguments.Count is 1 or 2 && allPx)
        {
            float step = arguments.Count == 2 ? arguments[1].Px : 1f;
            if (ComputedStyle.RoundCssValue(arguments[0].Px, step, strategy ?? "nearest") is { } rounded)
            {
                return Node.Linear(0f, rounded);
            }

            return null;
        }

        return new Node { Kind = NodeKind.Function, Name = name, Strategy = strategy, Arguments = arguments };
    }

    /// <summary>A value at the top level: a lone term bare, a sum in <c>calc()</c>.</summary>
    private static string Top(Node node)
    {
        if (node.Kind == NodeKind.Function)
        {
            return FunctionText(node);
        }

        List<string> terms = SumTerms(node);
        return terms.Count switch
        {
            0 => "0px",
            1 when !terms[0].StartsWith('-') || node.Terms is not { Count: > 0 } => terms[0],
            _ => "calc(" + JoinTerms(terms) + ")",
        };
    }

    /// <summary>A value inside a function's argument list: a sum without <c>calc()</c>.</summary>
    private static string Argument(Node node)
    {
        if (node.Kind == NodeKind.Number)
        {
            return PaintCssValues.CssNumber(node.Value);
        }

        if (node.Kind == NodeKind.Function)
        {
            return FunctionText(node);
        }

        List<string> terms = SumTerms(node);
        return terms.Count == 0 ? "0px" : JoinTerms(terms);
    }

    private static string FunctionText(Node node)
    {
        StringBuilder sb = new();
        sb.Append(node.Name).Append('(');
        if (node.Strategy is { } strategy)
        {
            sb.Append(strategy).Append(", ");
        }

        for (int i = 0; i < node.Arguments!.Count; i++)
        {
            sb.Append(i == 0 ? string.Empty : ", ").Append(Argument(node.Arguments[i]));
        }

        return sb.Append(')').ToString();
    }

    /// <summary>The signed terms of a sum, in css-values-4 order: percentage, px, functions.</summary>
    private static List<string> SumTerms(Node node)
    {
        List<string> terms = [];
        if (node.Pct != 0f)
        {
            terms.Add(PaintCssValues.CssNumber(node.Pct) + "%");
        }

        if (node.Px != 0f)
        {
            terms.Add(PaintCssValues.CssNumber(node.Px) + "px");
        }

        foreach ((float coefficient, Node function) in node.Terms ?? [])
        {
            if (coefficient == 0f)
            {
                continue;
            }

            string text = FunctionText(function);
            terms.Add(coefficient switch
            {
                1f => text,
                -1f => "-" + text,
                _ => PaintCssValues.CssNumber(coefficient) + " * " + text,
            });
        }

        return terms;
    }

    private static string JoinTerms(List<string> terms)
    {
        StringBuilder sb = new(terms[0]);
        for (int i = 1; i < terms.Count; i++)
        {
            string term = terms[i];
            sb.Append(term.StartsWith('-') ? " - " : " + ").Append(term.StartsWith('-') ? term[1..] : term);
        }

        return sb.ToString();
    }

    private ref struct Parser(string text, CalcUnits units)
    {
        private readonly string _text = text;
        private readonly CalcUnits _units = units;
        private int _pos;

        public readonly bool AtEnd => _pos >= _text.Length;

        public void SkipWhitespace()
        {
            while (_pos < _text.Length && Css.CssText.IsWhitespace(_text[_pos]))
            {
                _pos++;
            }
        }

        public Node? ParseSum()
        {
            Node? node = ParseProduct();
            while (node is not null)
            {
                int start = _pos;
                SkipWhitespace();
                // `+` and `-` are operators only between whitespace.
                if (_pos == start
                    || _pos + 1 >= _text.Length
                    || _text[_pos] is not ('+' or '-')
                    || !Css.CssText.IsWhitespace(_text[_pos + 1]))
                {
                    _pos = start;
                    break;
                }

                float sign = _text[_pos] == '-' ? -1f : 1f;
                _pos++;
                if (ParseProduct() is not { } right)
                {
                    return null;
                }

                node = Add(node, right, sign);
            }

            return node;
        }

        private Node? ParseProduct()
        {
            Node? node = ParseFactor();
            while (node is not null)
            {
                int start = _pos;
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] is not ('*' or '/'))
                {
                    _pos = start;
                    break;
                }

                bool divide = _text[_pos] == '/';
                _pos++;
                if (ParseFactor() is not { } right)
                {
                    return null;
                }

                if (right.Kind == NodeKind.Number)
                {
                    node = right.Value == 0f && divide ? null : Scale(node, divide ? 1f / right.Value : right.Value);
                }
                else if (node.Kind == NodeKind.Number && !divide)
                {
                    node = Scale(right, node.Value);
                }
                else
                {
                    return null;
                }
            }

            return node;
        }

        private Node? ParseFactor()
        {
            SkipWhitespace();
            if (AtEnd)
            {
                return null;
            }

            if (_text[_pos] == '(')
            {
                _pos++;
                return ParseGroupRest();
            }

            if (char.IsAsciiLetter(_text[_pos]))
            {
                int start = _pos;
                while (_pos < _text.Length && (char.IsAsciiLetter(_text[_pos]) || _text[_pos] == '-'))
                {
                    _pos++;
                }

                string name = _text[start.._pos];
                if (_pos >= _text.Length || _text[_pos] != '(')
                {
                    return null;
                }

                _pos++;
                return name switch
                {
                    "calc" => ParseGroupRest(),
                    "min" or "max" or "clamp" or "round" => ParseFunctionRest(name),
                    _ => null,
                };
            }

            return ParseDimension();
        }

        /// <summary>The rest of a parenthesized sum, after its <c>(</c>.</summary>
        private Node? ParseGroupRest()
        {
            Node? inner = ParseSum();
            SkipWhitespace();
            if (inner is null || AtEnd || _text[_pos] != ')')
            {
                return null;
            }

            _pos++;
            return inner;
        }

        private Node? ParseFunctionRest(string name)
        {
            string? strategy = null;
            if (name == "round")
            {
                SkipWhitespace();
                foreach (string candidate in (ReadOnlySpan<string>)["nearest", "up", "down", "to-zero"])
                {
                    int end = _pos + candidate.Length;
                    if (string.CompareOrdinal(_text, _pos, candidate, 0, candidate.Length) == 0
                        && end < _text.Length
                        && (_text[end] == ',' || Css.CssText.IsWhitespace(_text[end])))
                    {
                        _pos = end;
                        SkipWhitespace();
                        if (AtEnd || _text[_pos] != ',')
                        {
                            return null;
                        }

                        _pos++;
                        strategy = candidate;
                        break;
                    }
                }
            }

            List<Node> arguments = [];
            while (true)
            {
                if (ParseSum() is not { } argument || argument.Kind == NodeKind.Number)
                {
                    return null;
                }

                arguments.Add(argument);
                SkipWhitespace();
                if (AtEnd)
                {
                    return null;
                }

                if (_text[_pos] == ',')
                {
                    _pos++;
                    continue;
                }

                if (_text[_pos] != ')')
                {
                    return null;
                }

                _pos++;
                break;
            }

            if (name == "clamp" && arguments.Count != 3)
            {
                return null;
            }

            return MakeFunction(name, strategy, arguments);
        }

        private Node? ParseDimension()
        {
            int start = _pos;
            if (_pos < _text.Length && _text[_pos] is '+' or '-')
            {
                _pos++;
            }

            while (_pos < _text.Length && (char.IsAsciiDigit(_text[_pos]) || _text[_pos] == '.'))
            {
                _pos++;
            }

            if (_pos < _text.Length
                && _text[_pos] == 'e'
                && _pos + 1 < _text.Length
                && (char.IsAsciiDigit(_text[_pos + 1])
                    || (_text[_pos + 1] is '+' or '-' && _pos + 2 < _text.Length && char.IsAsciiDigit(_text[_pos + 2]))))
            {
                _pos += 2;
                while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos]))
                {
                    _pos++;
                }
            }

            if (!float.TryParse(
                    _text.AsSpan(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
                || !float.IsFinite(number))
            {
                return null;
            }

            if (_pos < _text.Length && _text[_pos] == '%')
            {
                _pos++;
                return Node.Linear(number, 0f);
            }

            int unitStart = _pos;
            while (_pos < _text.Length && char.IsAsciiLetter(_text[_pos]))
            {
                _pos++;
            }

            if (_pos == unitStart)
            {
                return Node.Number(number);
            }

            float? px = _text[unitStart.._pos] switch
            {
                "px" => 1f,
                "pt" => 4f / 3f,
                "pc" => 16f,
                "in" => 96f,
                "cm" => 96f / 2.54f,
                "mm" => 96f / 25.4f,
                "q" => 96f / 101.6f,
                "em" => _units.Font.EmPx,
                "rem" => _units.RemPx,
                "ex" => _units.Font.ExPx,
                "ch" => _units.Font.ChPx,
                "vw" or "dvw" or "svw" or "lvw" => _units.VwPx,
                "vh" or "dvh" or "svh" or "lvh" => _units.VhPx,
                "vmin" => MathF.Min(_units.VwPx, _units.VhPx),
                "vmax" => MathF.Max(_units.VwPx, _units.VhPx),
                _ => null,
            };
            return px is { } scale ? Node.Linear(0f, number * scale) : null;
        }
    }
}
