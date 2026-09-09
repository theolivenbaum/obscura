using System.Globalization;
using System.Text;

namespace Obscura.Render.Css;

/// <summary>
/// Token kinds produced by <see cref="CssTokenizer"/>. The set mirrors the
/// <c>cssparser</c> crate's <c>Token</c> enum, which the Rust engine uses, so
/// the two tokenizers can be compared case by case.
/// </summary>
public enum CssTokenKind
{
    EndOfFile,
    Whitespace,
    Comment,
    Ident,
    AtKeyword,
    Hash,
    /// <summary>A <c>#</c> token whose value is a valid identifier (<c>#id</c>).</summary>
    IdHash,
    QuotedString,
    BadString,
    Url,
    BadUrl,
    Function,
    Number,
    Percentage,
    Dimension,
    UnicodeRange,
    Delim,
    Colon,
    Semicolon,
    Comma,
    IncludeMatch,
    DashMatch,
    PrefixMatch,
    SuffixMatch,
    SubstringMatch,
    Cdo,
    Cdc,
    ParenthesisBlock,
    SquareBracketBlock,
    CurlyBracketBlock,
    CloseParenthesis,
    CloseSquareBracket,
    CloseCurlyBracket,
}

/// <summary>One CSS token. Small enough to stay on the stack.</summary>
public readonly record struct CssToken(
    CssTokenKind Kind,
    string Value,
    string Unit,
    double Number,
    bool IsInteger,
    bool HasSign,
    char Delim,
    uint RangeStart,
    uint RangeEnd)
{
    public static CssToken Simple(CssTokenKind kind) =>
        new(kind, string.Empty, string.Empty, 0d, false, false, '\0', 0, 0);

    public static CssToken Text(CssTokenKind kind, string value) =>
        new(kind, value, string.Empty, 0d, false, false, '\0', 0, 0);

    public static CssToken Numeric(CssTokenKind kind, double number, bool isInteger, bool hasSign, string unit) =>
        new(kind, string.Empty, unit, number, isInteger, hasSign, '\0', 0, 0);

    public static CssToken Delimiter(char delim) =>
        new(CssTokenKind.Delim, string.Empty, string.Empty, 0d, false, false, delim, 0, 0);

    public override string ToString() => Kind switch
    {
        CssTokenKind.Delim => $"Delim({Delim})",
        CssTokenKind.Dimension => string.Create(
            CultureInfo.InvariantCulture,
            $"Dimension({Number}{Unit})"),
        CssTokenKind.Number => string.Create(CultureInfo.InvariantCulture, $"Number({Number})"),
        CssTokenKind.Percentage => string.Create(CultureInfo.InvariantCulture, $"Percentage({Number})"),
        CssTokenKind.UnicodeRange => $"UnicodeRange({RangeStart:X}-{RangeEnd:X})",
        _ => Value.Length == 0 ? Kind.ToString() : $"{Kind}({Value})",
    };
}

/// <summary>
/// A CSS Syntax Level 3 tokenizer, ported in-tree rather than delegated to a
/// CSS package: the cascade needs our own token internals, and matching
/// <c>cssparser</c>'s exact treatment of escapes, unicode ranges, number and
/// dimension parsing, and error recovery is the point.
/// </summary>
/// <remarks>
/// Never throws. Malformed input produces <see cref="CssTokenKind.BadString"/>
/// / <see cref="CssTokenKind.BadUrl"/> / <see cref="CssTokenKind.Delim"/>
/// exactly as the spec's error recovery prescribes.
/// </remarks>
public ref struct CssTokenizer
{
    private readonly ReadOnlySpan<char> _input;
    private int _position;

    public CssTokenizer(ReadOnlySpan<char> input)
    {
        _input = input;
        _position = 0;
    }

    public readonly int Position => _position;

    public readonly bool IsEndOfFile => _position >= _input.Length;

    /// <summary>Whether only whitespace and comments remain (cssparser's <c>is_exhausted</c>).</summary>
    public bool IsExhausted()
    {
        var saved = _position;
        var exhausted = !TryNext(out _);
        _position = saved;
        return exhausted;
    }

    /// <summary>
    /// The next token, skipping whitespace and comments the way
    /// <c>cssparser::Parser::next</c> does.
    /// </summary>
    public bool TryNext(out CssToken token)
    {
        while (TryNextIncludingWhitespaceAndComments(out token))
        {
            if (token.Kind is not (CssTokenKind.Whitespace or CssTokenKind.Comment))
            {
                return true;
            }
        }

        token = CssToken.Simple(CssTokenKind.EndOfFile);
        return false;
    }

    public bool TryNextIncludingWhitespaceAndComments(out CssToken token)
    {
        if (_position >= _input.Length)
        {
            token = CssToken.Simple(CssTokenKind.EndOfFile);
            return false;
        }

        var current = _input[_position];
        switch (current)
        {
            case ' ':
            case '\t':
            case '\n':
            case '\r':
            case '\f':
                token = ConsumeWhitespace();
                return true;
            case '"':
            case '\'':
                token = ConsumeString(current);
                return true;
            case '#':
                token = ConsumeHash();
                return true;
            case '(':
                _position++;
                token = CssToken.Simple(CssTokenKind.ParenthesisBlock);
                return true;
            case ')':
                _position++;
                token = CssToken.Simple(CssTokenKind.CloseParenthesis);
                return true;
            case '[':
                _position++;
                token = CssToken.Simple(CssTokenKind.SquareBracketBlock);
                return true;
            case ']':
                _position++;
                token = CssToken.Simple(CssTokenKind.CloseSquareBracket);
                return true;
            case '{':
                _position++;
                token = CssToken.Simple(CssTokenKind.CurlyBracketBlock);
                return true;
            case '}':
                _position++;
                token = CssToken.Simple(CssTokenKind.CloseCurlyBracket);
                return true;
            case ',':
                _position++;
                token = CssToken.Simple(CssTokenKind.Comma);
                return true;
            case ':':
                _position++;
                token = CssToken.Simple(CssTokenKind.Colon);
                return true;
            case ';':
                _position++;
                token = CssToken.Simple(CssTokenKind.Semicolon);
                return true;
            case '+':
                if (StartsNumber(_position))
                {
                    token = ConsumeNumeric();
                    return true;
                }

                _position++;
                token = CssToken.Delimiter('+');
                return true;
            case '-':
                if (StartsNumber(_position))
                {
                    token = ConsumeNumeric();
                    return true;
                }

                if (Peek(1) == '-' && Peek(2) == '>')
                {
                    _position += 3;
                    token = CssToken.Simple(CssTokenKind.Cdc);
                    return true;
                }

                if (StartsIdentifier(_position))
                {
                    token = ConsumeIdentLike();
                    return true;
                }

                _position++;
                token = CssToken.Delimiter('-');
                return true;
            case '.':
                if (StartsNumber(_position))
                {
                    token = ConsumeNumeric();
                    return true;
                }

                _position++;
                token = CssToken.Delimiter('.');
                return true;
            case '/':
                if (Peek(1) == '*')
                {
                    token = ConsumeComment();
                    return true;
                }

                _position++;
                token = CssToken.Delimiter('/');
                return true;
            case '<':
                if (Peek(1) == '!' && Peek(2) == '-' && Peek(3) == '-')
                {
                    _position += 4;
                    token = CssToken.Simple(CssTokenKind.Cdo);
                    return true;
                }

                _position++;
                token = CssToken.Delimiter('<');
                return true;
            case '@':
                if (StartsIdentifier(_position + 1))
                {
                    _position++;
                    token = CssToken.Text(CssTokenKind.AtKeyword, ConsumeName());
                    return true;
                }

                _position++;
                token = CssToken.Delimiter('@');
                return true;
            case '\\':
                if (IsValidEscape(_position))
                {
                    token = ConsumeIdentLike();
                    return true;
                }

                _position++;
                token = CssToken.Delimiter('\\');
                return true;
            case '~':
                if (Peek(1) == '=')
                {
                    _position += 2;
                    token = CssToken.Simple(CssTokenKind.IncludeMatch);
                    return true;
                }

                _position++;
                token = CssToken.Delimiter('~');
                return true;
            case '|':
                if (Peek(1) == '=')
                {
                    _position += 2;
                    token = CssToken.Simple(CssTokenKind.DashMatch);
                    return true;
                }

                _position++;
                token = CssToken.Delimiter('|');
                return true;
            case '^':
                if (Peek(1) == '=')
                {
                    _position += 2;
                    token = CssToken.Simple(CssTokenKind.PrefixMatch);
                    return true;
                }

                _position++;
                token = CssToken.Delimiter('^');
                return true;
            case '$':
                if (Peek(1) == '=')
                {
                    _position += 2;
                    token = CssToken.Simple(CssTokenKind.SuffixMatch);
                    return true;
                }

                _position++;
                token = CssToken.Delimiter('$');
                return true;
            case '*':
                if (Peek(1) == '=')
                {
                    _position += 2;
                    token = CssToken.Simple(CssTokenKind.SubstringMatch);
                    return true;
                }

                _position++;
                token = CssToken.Delimiter('*');
                return true;
            case 'u':
            case 'U':
                if (TryConsumeUnicodeRange(out token))
                {
                    return true;
                }

                token = ConsumeIdentLike();
                return true;
            default:
                if (CssText.IsAsciiDigit(current))
                {
                    token = ConsumeNumeric();
                    return true;
                }

                if (IsNameStart(current))
                {
                    token = ConsumeIdentLike();
                    return true;
                }

                _position++;
                token = CssToken.Delimiter(current);
                return true;
        }
    }

    /// <summary>Materialize every token, whitespace and comments included. For tests and tooling.</summary>
    public static List<CssToken> Tokenize(string input)
    {
        var tokens = new List<CssToken>();
        var tokenizer = new CssTokenizer(input.AsSpan());
        while (tokenizer.TryNextIncludingWhitespaceAndComments(out var token))
        {
            tokens.Add(token);
        }

        return tokens;
    }

    /// <summary>
    /// The single identifier the input consists of, or null when the input is
    /// not exactly one ident token. This is <c>expect_ident_cloned</c> followed
    /// by <c>is_exhausted</c>, the shape css.rs uses for custom idents.
    /// </summary>
    public static string? SoleIdent(string input)
    {
        var tokenizer = new CssTokenizer(input.AsSpan());
        if (!tokenizer.TryNext(out var token) || token.Kind != CssTokenKind.Ident)
        {
            return null;
        }

        return tokenizer.IsExhausted() ? token.Value : null;
    }

    /// <summary>Whether the first token is an ident or a function (CSS general-enclosed syntax).</summary>
    public static bool StartsWithIdentOrFunction(string input)
    {
        var tokenizer = new CssTokenizer(input.AsSpan());
        return tokenizer.TryNext(out var token)
            && token.Kind is CssTokenKind.Ident or CssTokenKind.Function;
    }

    private readonly char Peek(int offset)
    {
        var index = _position + offset;
        return index < _input.Length ? _input[index] : '\0';
    }

    private CssToken ConsumeWhitespace()
    {
        var start = _position;
        while (_position < _input.Length && CssText.IsAsciiWhitespace(_input[_position]))
        {
            _position++;
        }

        return CssToken.Text(CssTokenKind.Whitespace, new string(_input[start.._position]));
    }

    private CssToken ConsumeComment()
    {
        _position += 2;
        var start = _position;
        while (_position < _input.Length)
        {
            if (_input[_position] == '*' && _position + 1 < _input.Length && _input[_position + 1] == '/')
            {
                var value = new string(_input[start.._position]);
                _position += 2;
                return CssToken.Text(CssTokenKind.Comment, value);
            }

            _position++;
        }

        // An unterminated comment runs to end of input; the spec calls this a
        // parse error but keeps the comment.
        return CssToken.Text(CssTokenKind.Comment, new string(_input[start.._position]));
    }

    private CssToken ConsumeString(char quote)
    {
        _position++;
        var builder = new StringBuilder();
        while (_position < _input.Length)
        {
            var current = _input[_position];
            if (current == quote)
            {
                _position++;
                return CssToken.Text(CssTokenKind.QuotedString, builder.ToString());
            }

            if (current is '\n' or '\r' or '\f')
            {
                // Do not consume the newline: it re-tokenizes as whitespace.
                return CssToken.Text(CssTokenKind.BadString, builder.ToString());
            }

            if (current == '\\')
            {
                if (_position + 1 >= _input.Length)
                {
                    _position++;
                    continue;
                }

                var next = _input[_position + 1];
                if (next is '\n' or '\f')
                {
                    _position += 2;
                    continue;
                }

                if (next == '\r')
                {
                    _position += 2;
                    if (_position < _input.Length && _input[_position] == '\n')
                    {
                        _position++;
                    }

                    continue;
                }

                _position++;
                ConsumeEscape(builder);
                continue;
            }

            builder.Append(current);
            _position++;
        }

        // EOF inside a string is a parse error but yields the string so far.
        return CssToken.Text(CssTokenKind.QuotedString, builder.ToString());
    }

    private CssToken ConsumeHash()
    {
        _position++;
        if (_position < _input.Length && (IsNameChar(_input[_position]) || IsValidEscape(_position)))
        {
            var isIdentifier = StartsIdentifier(_position);
            var name = ConsumeName();
            return CssToken.Text(isIdentifier ? CssTokenKind.IdHash : CssTokenKind.Hash, name);
        }

        return CssToken.Delimiter('#');
    }

    private CssToken ConsumeIdentLike()
    {
        var name = ConsumeName();
        if (_position < _input.Length && _input[_position] == '(')
        {
            _position++;
            if (CssText.EqualsAscii(name, "url"))
            {
                var probe = _position;
                while (probe < _input.Length && CssText.IsAsciiWhitespace(_input[probe]))
                {
                    probe++;
                }

                if (probe >= _input.Length || (_input[probe] != '"' && _input[probe] != '\''))
                {
                    return ConsumeUnquotedUrl();
                }
            }

            return CssToken.Text(CssTokenKind.Function, name);
        }

        return CssToken.Text(CssTokenKind.Ident, name);
    }

    private CssToken ConsumeUnquotedUrl()
    {
        while (_position < _input.Length && CssText.IsAsciiWhitespace(_input[_position]))
        {
            _position++;
        }

        var builder = new StringBuilder();
        while (_position < _input.Length)
        {
            var current = _input[_position];
            if (current == ')')
            {
                _position++;
                return CssToken.Text(CssTokenKind.Url, builder.ToString());
            }

            if (CssText.IsAsciiWhitespace(current))
            {
                while (_position < _input.Length && CssText.IsAsciiWhitespace(_input[_position]))
                {
                    _position++;
                }

                if (_position < _input.Length && _input[_position] == ')')
                {
                    _position++;
                    return CssToken.Text(CssTokenKind.Url, builder.ToString());
                }

                if (_position >= _input.Length)
                {
                    return CssToken.Text(CssTokenKind.Url, builder.ToString());
                }

                ConsumeBadUrlRemnants();
                return CssToken.Text(CssTokenKind.BadUrl, builder.ToString());
            }

            if (current is '"' or '\'' or '(' || IsNonPrintable(current))
            {
                ConsumeBadUrlRemnants();
                return CssToken.Text(CssTokenKind.BadUrl, builder.ToString());
            }

            if (current == '\\')
            {
                if (IsValidEscape(_position))
                {
                    _position++;
                    ConsumeEscape(builder);
                    continue;
                }

                ConsumeBadUrlRemnants();
                return CssToken.Text(CssTokenKind.BadUrl, builder.ToString());
            }

            builder.Append(current);
            _position++;
        }

        return CssToken.Text(CssTokenKind.Url, builder.ToString());
    }

    private void ConsumeBadUrlRemnants()
    {
        while (_position < _input.Length)
        {
            if (_input[_position] == ')')
            {
                _position++;
                return;
            }

            if (IsValidEscape(_position))
            {
                _position++;
                var sink = new StringBuilder();
                ConsumeEscape(sink);
                continue;
            }

            _position++;
        }
    }

    private CssToken ConsumeNumeric()
    {
        var hasSign = _input[_position] is '+' or '-';
        var start = _position;
        var isInteger = true;

        if (hasSign)
        {
            _position++;
        }

        while (_position < _input.Length && CssText.IsAsciiDigit(_input[_position]))
        {
            _position++;
        }

        if (_position < _input.Length && _input[_position] == '.'
            && _position + 1 < _input.Length && CssText.IsAsciiDigit(_input[_position + 1]))
        {
            isInteger = false;
            _position += 2;
            while (_position < _input.Length && CssText.IsAsciiDigit(_input[_position]))
            {
                _position++;
            }
        }

        if (_position < _input.Length && (_input[_position] is 'e' or 'E'))
        {
            var exponent = _position + 1;
            if (exponent < _input.Length && (_input[exponent] is '+' or '-'))
            {
                exponent++;
            }

            if (exponent < _input.Length && CssText.IsAsciiDigit(_input[exponent]))
            {
                isInteger = false;
                _position = exponent;
                while (_position < _input.Length && CssText.IsAsciiDigit(_input[_position]))
                {
                    _position++;
                }
            }
        }

        var text = _input[start.._position];
        _ = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number);

        if (_position < _input.Length && _input[_position] == '%')
        {
            _position++;
            return CssToken.Numeric(CssTokenKind.Percentage, number / 100d, isInteger, hasSign, string.Empty);
        }

        if (StartsIdentifier(_position))
        {
            var unit = ConsumeName();
            return CssToken.Numeric(CssTokenKind.Dimension, number, isInteger, hasSign, unit);
        }

        return CssToken.Numeric(CssTokenKind.Number, number, isInteger, hasSign, string.Empty);
    }

    private bool TryConsumeUnicodeRange(out CssToken token)
    {
        token = default;
        if (Peek(1) != '+')
        {
            return false;
        }

        var third = Peek(2);
        if (!CssText.IsAsciiHexDigit(third) && third != '?')
        {
            return false;
        }

        var saved = _position;
        _position += 2;

        Span<char> low = stackalloc char[6];
        Span<char> high = stackalloc char[6];
        var digits = 0;
        while (digits < 6 && _position < _input.Length && CssText.IsAsciiHexDigit(_input[_position]))
        {
            low[digits] = _input[_position];
            high[digits] = _input[_position];
            digits++;
            _position++;
        }

        var questionMarks = 0;
        while (digits + questionMarks < 6 && _position < _input.Length && _input[_position] == '?')
        {
            low[digits + questionMarks] = '0';
            high[digits + questionMarks] = 'F';
            questionMarks++;
            _position++;
        }

        var total = digits + questionMarks;
        if (total == 0)
        {
            _position = saved;
            return false;
        }

        var start = ParseHex(low[..total]);
        var end = ParseHex(high[..total]);

        if (questionMarks == 0 && _position < _input.Length && _input[_position] == '-'
            && _position + 1 < _input.Length && CssText.IsAsciiHexDigit(_input[_position + 1]))
        {
            _position++;
            Span<char> upper = stackalloc char[6];
            var upperDigits = 0;
            while (upperDigits < 6 && _position < _input.Length && CssText.IsAsciiHexDigit(_input[_position]))
            {
                upper[upperDigits] = _input[_position];
                upperDigits++;
                _position++;
            }

            end = ParseHex(upper[..upperDigits]);
        }

        token = new CssToken(CssTokenKind.UnicodeRange, string.Empty, string.Empty, 0d, false, false, '\0', start, end);
        return true;

        static uint ParseHex(ReadOnlySpan<char> digits)
        {
            uint value = 0;
            foreach (var digit in digits)
            {
                value = (value * 16) + (uint)Convert.ToInt32(digit.ToString(), 16);
            }

            return value;
        }
    }

    private string ConsumeName()
    {
        var start = _position;
        StringBuilder? builder = null;
        while (_position < _input.Length)
        {
            var current = _input[_position];
            if (IsNameChar(current))
            {
                builder?.Append(current);
                _position++;
                continue;
            }

            if (IsValidEscape(_position))
            {
                builder ??= new StringBuilder().Append(_input[start.._position]);
                _position++;
                ConsumeEscape(builder);
                continue;
            }

            break;
        }

        return builder?.ToString() ?? CssIdentCache.Intern(_input[start.._position]);
    }

    private void ConsumeEscape(StringBuilder builder)
    {
        if (_position >= _input.Length)
        {
            builder.Append('\uFFFD');
            return;
        }

        var current = _input[_position];
        if (!CssText.IsAsciiHexDigit(current))
        {
            builder.Append(current);
            _position++;
            return;
        }

        var value = 0u;
        var digits = 0;
        while (digits < 6 && _position < _input.Length && CssText.IsAsciiHexDigit(_input[_position]))
        {
            value = (value * 16) + HexValue(_input[_position]);
            digits++;
            _position++;
        }

        if (_position < _input.Length && CssText.IsAsciiWhitespace(_input[_position]))
        {
            if (_input[_position] == '\r' && _position + 1 < _input.Length && _input[_position + 1] == '\n')
            {
                _position++;
            }

            _position++;
        }

        if (value == 0 || value > 0x10FFFF || value is >= 0xD800 and <= 0xDFFF)
        {
            builder.Append('\uFFFD');
            return;
        }

        builder.Append(char.ConvertFromUtf32((int)value));
    }

    private static uint HexValue(char digit) => digit switch
    {
        >= '0' and <= '9' => (uint)(digit - '0'),
        >= 'a' and <= 'f' => (uint)(digit - 'a' + 10),
        _ => (uint)(digit - 'A' + 10),
    };

    private readonly bool IsValidEscape(int index) =>
        index < _input.Length
        && _input[index] == '\\'
        && (index + 1 >= _input.Length || _input[index + 1] is not ('\n' or '\r' or '\f'));

    private readonly bool StartsIdentifier(int index)
    {
        if (index >= _input.Length)
        {
            return false;
        }

        var current = _input[index];
        if (current == '-')
        {
            if (index + 1 >= _input.Length)
            {
                return false;
            }

            var next = _input[index + 1];
            return next == '-' || IsNameStart(next) || IsValidEscape(index + 1);
        }

        return IsNameStart(current) || IsValidEscape(index);
    }

    private readonly bool StartsNumber(int index)
    {
        if (index >= _input.Length)
        {
            return false;
        }

        var current = _input[index];
        if (CssText.IsAsciiDigit(current))
        {
            return true;
        }

        if (current is '+' or '-')
        {
            if (index + 1 >= _input.Length)
            {
                return false;
            }

            if (CssText.IsAsciiDigit(_input[index + 1]))
            {
                return true;
            }

            return _input[index + 1] == '.'
                && index + 2 < _input.Length
                && CssText.IsAsciiDigit(_input[index + 2]);
        }

        if (current == '.')
        {
            return index + 1 < _input.Length && CssText.IsAsciiDigit(_input[index + 1]);
        }

        return false;
    }

    private static bool IsNameStart(char value) =>
        CssText.IsAsciiAlphabetic(value) || value == '_' || value > 0x7F;

    private static bool IsNameChar(char value) =>
        IsNameStart(value) || CssText.IsAsciiDigit(value) || value == '-';

    private static bool IsNonPrintable(char value) =>
        value <= '\u0008' || value == '\u000B' || value is >= '\u000E' and <= '\u001F' || value == '\u007F';
}

/// <summary>
/// Interns the identifiers a real stylesheet repeats thousands of times so the
/// tokenizer hot path does not allocate a fresh string per token.
/// </summary>
internal static class CssIdentCache
{
    private static readonly string[] Common =
    [
        "width", "height", "color", "display", "grid", "flex", "block", "inline", "none", "auto",
        "px", "em", "rem", "vw", "vh", "%", "not", "and", "or", "min-width", "max-width",
        "min-height", "max-height", "inline-size", "block-size", "screen", "print", "all",
        "url", "var", "calc", "attr", "counter", "counters", "initial", "inherit", "unset",
        "revert", "revert-layer", "normal", "container", "main", "solid", "transparent",
    ];

    private static readonly Dictionary<string, string> Table = BuildTable();

    private static Dictionary<string, string> BuildTable()
    {
        var table = new Dictionary<string, string>(Common.Length, StringComparer.Ordinal);
        foreach (var value in Common)
        {
            table[value] = value;
        }

        return table;
    }

    public static string Intern(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return string.Empty;
        }

        var lookup = Table.GetAlternateLookup<ReadOnlySpan<char>>();
        return lookup.TryGetValue(value, out var interned) ? interned : new string(value);
    }
}
