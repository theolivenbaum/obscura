using System.Text;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// The names a classic script declares at its top level: <c>var</c> and <c>function</c>
/// (which become properties of the global object) and <c>let</c>, <c>const</c> and
/// <c>class</c> (which live in the global lexical scope), plus whether the script opens
/// with a <c>"use strict"</c> directive.
/// </summary>
/// <remarks>
/// <para>
/// Port addition, for workers. A worker runs in the page's realm, under a scope object
/// that stands in for its global (bootstrap.js <c>Worker</c>), and each of its scripts is
/// a direct <c>eval</c> in that scope. A direct eval puts the script's declarations in a
/// function environment of its own rather than on the global, so the next script (an
/// <c>importScripts</c> library, or the main script after one) could not see them.
/// Knowing the names lets the shim give each the global binding Chromium gives it.
/// </para>
/// <para>
/// This is a scanner, not a parser: it tracks strings, comments, template literals,
/// regular expression literals and bracket depth, and reads declarations only at depth
/// zero. It is tolerant by construction: a name it misses stays private to its script,
/// as before, and a name it wrongly reports is checked again in the shim before it is
/// used. It never throws on malformed input.
/// </para>
/// </remarks>
public static class ScriptDeclarations
{
    /// <summary>The scan of one script.</summary>
    public sealed record Result(bool Strict, List<string> Vars, List<string> Functions, List<string> Lexicals);

    private enum Kind
    {
        End,
        Word,
        Punct,
        String,
        Template,
        Number,
        Regex,
    }

    /// <summary>The scan as the JSON object the shim reads: <c>{"s":bool,"v":[],"f":[],"l":[]}</c>.</summary>
    public static string ScanJson(string source)
    {
        var result = Scan(source);
        var sb = new StringBuilder(64);
        sb.Append("{\"s\":").Append(result.Strict ? "true" : "false");
        Append(sb, "v", result.Vars);
        Append(sb, "f", result.Functions);
        Append(sb, "l", result.Lexicals);
        sb.Append('}');
        return sb.ToString();

        static void Append(StringBuilder sb, string key, List<string> names)
        {
            sb.Append(",\"").Append(key).Append("\":[");
            for (var i = 0; i < names.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                SerdeJson.AppendString(sb, names[i]);
            }

            sb.Append(']');
        }
    }

    /// <summary>Scans <paramref name="source"/>.</summary>
    public static Result Scan(string source)
    {
        var scanner = new Scanner(source ?? string.Empty);
        var vars = new List<string>();
        var functions = new List<string>();
        var lexicals = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var strict = false;
        var directives = true;
        var statementStart = true;
        var previousEndsExpression = false;
        // A var anywhere outside a function body is the script's, so the brace kinds
        // are tracked: a block (if, for, a bare block, a class body) or a function body
        // (after a parameter list or =>). A parameter list is a ")" that does not close
        // the head of if/for/while/switch/catch/with.
        var braces = new List<bool>();
        var parens = new List<bool>();
        var functionDepth = 0;
        var closedControlHead = false;
        Token previous = default;
        while (true)
        {
            var token = scanner.Next();
            var startsStatement = statementStart || (token.NewlineBefore && previousEndsExpression);
            previousEndsExpression = scanner.EndsExpression(token);
            if (token.Kind == Kind.End)
            {
                break;
            }

            if (directives)
            {
                if (token.Kind == Kind.String && startsStatement)
                {
                    var text = scanner.Text(token);
                    if (text is "'use strict'" or "\"use strict\"")
                    {
                        var after = scanner.Peek();
                        if (after.Kind == Kind.End || after.NewlineBefore || scanner.IsPunct(after, ";") || scanner.IsPunct(after, "}"))
                        {
                            strict = true;
                        }
                    }
                }
                else if (!(token.Kind == Kind.Punct && scanner.IsPunct(token, ";")))
                {
                    directives = false;
                }
            }

            if (token.Kind == Kind.Punct)
            {
                if (scanner.IsPunct(token, "("))
                {
                    parens.Add(previous.Kind == Kind.Word
                        && scanner.Text(previous) is "if" or "for" or "while" or "switch" or "catch" or "with");
                }
                else if (scanner.IsPunct(token, ")"))
                {
                    closedControlHead = parens.Count > 0 && parens[^1];
                    if (parens.Count > 0)
                    {
                        parens.RemoveAt(parens.Count - 1);
                    }
                }
                else if (scanner.IsPunct(token, "{"))
                {
                    var body = (scanner.IsPunct(previous, ")") && !closedControlHead) || scanner.IsPunct(previous, "=>");
                    braces.Add(body);
                    functionDepth += body ? 1 : 0;
                    statementStart = true;
                    previous = token;
                    continue;
                }
                else if (scanner.IsPunct(token, "}"))
                {
                    if (braces.Count > 0)
                    {
                        functionDepth -= braces[^1] ? 1 : 0;
                        braces.RemoveAt(braces.Count - 1);
                    }

                    statementStart = true;
                    previous = token;
                    continue;
                }
            }

            if (token.Kind == Kind.Word && functionDepth == 0 && scanner.Text(token) == "var"
                && !scanner.IsPunct(previous, ".") && !scanner.IsPunct(previous, "?.")
                && !scanner.IsPunct(scanner.Peek(), ":"))
            {
                ReadDeclarators(scanner, vars, seen);
                statementStart = true;
                previous = scanner.Previous;
                continue;
            }

            if (token.Depth == 0 && token.Kind == Kind.Word && startsStatement)
            {
                var word = scanner.Text(token);
                switch (word)
                {
                    case "let" or "const":
                    {
                        var next = scanner.Peek();
                        if (next.Kind == Kind.Word && !IsReserved(scanner.Text(next))
                            || scanner.IsPunct(next, "[") || scanner.IsPunct(next, "{"))
                        {
                            ReadDeclarators(scanner, lexicals, seen);
                            statementStart = true;
                            previous = scanner.Previous;
                            continue;
                        }

                        break;
                    }

                    case "class":
                    {
                        var next = scanner.Peek();
                        if (next.Kind == Kind.Word && !IsReserved(scanner.Text(next)))
                        {
                            scanner.Next();
                            Add(lexicals, seen, scanner.Text(next));
                        }

                        break;
                    }

                    case "async":
                    {
                        var next = scanner.Peek();
                        if (next.Kind == Kind.Word && !next.NewlineBefore && scanner.Text(next) == "function")
                        {
                            scanner.Next();
                            ReadFunctionName(scanner, functions, seen);
                        }

                        break;
                    }

                    case "function":
                        ReadFunctionName(scanner, functions, seen);
                        break;
                }
            }

            statementStart = token.Kind == Kind.Punct && scanner.IsPunct(token, ";");
            previous = scanner.Previous;
        }

        // A name declared both ways is a function: its declaration initializes the binding.
        vars.RemoveAll(name => functions.Contains(name));
        return new Result(strict, vars, functions, lexicals);
    }

    private static void ReadFunctionName(Scanner scanner, List<string> functions, HashSet<string> seen)
    {
        var next = scanner.Peek();
        if (scanner.IsPunct(next, "*"))
        {
            scanner.Next();
            next = scanner.Peek();
        }

        if (next.Kind == Kind.Word && !IsReserved(scanner.Text(next)))
        {
            scanner.Next();
            Add(functions, seen, scanner.Text(next));
        }
    }

    /// <summary>Reads a declarator list after <c>var</c>, <c>let</c> or <c>const</c>.</summary>
    private static void ReadDeclarators(Scanner scanner, List<string> names, HashSet<string> seen)
    {
        while (true)
        {
            var binding = scanner.Next();
            if (binding.Kind == Kind.Word && !IsReserved(scanner.Text(binding)))
            {
                Add(names, seen, scanner.Text(binding));
            }
            else if (scanner.IsPunct(binding, "[") || scanner.IsPunct(binding, "{"))
            {
                ReadPattern(scanner, binding, names, seen);
            }
            else
            {
                return;
            }

            var next = scanner.Peek();
            if (scanner.IsPunct(next, "="))
            {
                scanner.Next();
                if (!SkipInitializer(scanner))
                {
                    return;
                }

                next = scanner.Peek();
            }

            if (!scanner.IsPunct(next, ","))
            {
                return;
            }

            scanner.Next();
        }
    }

    /// <summary>
    /// Skips an initializer expression. Returns true when it stopped at a <c>,</c> that
    /// separates declarators (left unconsumed), false at the end of the statement.
    /// </summary>
    private static bool SkipInitializer(Scanner scanner)
    {
        var depth = scanner.Depth;
        var first = true;
        while (true)
        {
            var next = scanner.Peek();
            if (next.Kind == Kind.End)
            {
                return false;
            }

            if (next.Depth == depth && !first)
            {
                if (scanner.IsPunct(next, ","))
                {
                    return true;
                }

                if (scanner.IsPunct(next, ";"))
                {
                    return false;
                }

                // Automatic semicolon insertion: a line break after a complete expression,
                // before a token that cannot continue it.
                if (next.NewlineBefore && scanner.EndsExpression(scanner.Previous) && StartsNewStatement(scanner, next))
                {
                    return false;
                }
            }

            if (next.Depth < depth)
            {
                return false;
            }

            scanner.Next();
            first = false;
        }
    }

    private static bool StartsNewStatement(Scanner scanner, Token next) => next.Kind switch
    {
        Kind.Word => scanner.Text(next) is not ("in" or "instanceof" or "of"),
        Kind.String or Kind.Number or Kind.Template or Kind.Regex => true,
        Kind.Punct => scanner.IsPunct(next, "{") || scanner.IsPunct(next, "!") || scanner.IsPunct(next, "~")
            || scanner.IsPunct(next, "++") || scanner.IsPunct(next, "--"),
        _ => true,
    };

    /// <summary>Reads a destructuring pattern whose opening bracket was just consumed.</summary>
    private static void ReadPattern(Scanner scanner, Token open, List<string> names, HashSet<string> seen)
    {
        var isObject = scanner.IsPunct(open, "{");
        var close = isObject ? "}" : "]";
        var depth = scanner.Depth;
        while (true)
        {
            var token = scanner.Next();
            if (token.Kind == Kind.End || (scanner.IsPunct(token, close) && scanner.Depth < depth))
            {
                return;
            }

            if (scanner.IsPunct(token, ","))
            {
                continue;
            }

            if (scanner.IsPunct(token, "..."))
            {
                token = scanner.Next();
            }
            else if (isObject)
            {
                // A property key: an identifier (shorthand unless a colon follows), a
                // string or number, or a computed [key].
                var isKey = false;
                if (scanner.IsPunct(token, "["))
                {
                    SkipTo(scanner, scanner.Depth - 1);
                    isKey = true;
                }
                else if (token.Kind is Kind.String or Kind.Number)
                {
                    isKey = true;
                }
                else if (token.Kind == Kind.Word && scanner.IsPunct(scanner.Peek(), ":"))
                {
                    isKey = true;
                }

                if (isKey)
                {
                    if (!scanner.IsPunct(scanner.Peek(), ":"))
                    {
                        SkipTo(scanner, depth - 1);
                        return;
                    }

                    scanner.Next();
                    token = scanner.Next();
                }
            }

            if (token.Kind == Kind.Word && !IsReserved(scanner.Text(token)))
            {
                Add(names, seen, scanner.Text(token));
            }
            else if (scanner.IsPunct(token, "[") || scanner.IsPunct(token, "{"))
            {
                ReadPattern(scanner, token, names, seen);
            }
            else if (token.Kind == Kind.End)
            {
                return;
            }

            // A default value runs to the next comma or the closing bracket.
            while (true)
            {
                var next = scanner.Peek();
                if (next.Kind == Kind.End)
                {
                    return;
                }

                if (next.Depth == depth && scanner.IsPunct(next, ","))
                {
                    break;
                }

                if (next.Depth < depth)
                {
                    scanner.Next();
                    return;
                }

                scanner.Next();
            }
        }
    }

    /// <summary>Consumes tokens until the depth drops to <paramref name="depth"/>.</summary>
    private static void SkipTo(Scanner scanner, int depth)
    {
        while (scanner.Depth > depth)
        {
            if (scanner.Next().Kind == Kind.End)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The specifiers a module's static <c>import</c> and <c>export ... from</c> declarations
    /// request, in source order and without duplicates.
    /// </summary>
    /// <remarks>
    /// Port addition, for fetching a module graph before it is evaluated (see
    /// <c>PocketCalculatorModuleLoader.PrefetchGraphAsync</c>). Only declarations at depth zero
    /// count; <c>import(...)</c> and <c>import.meta</c> do not. A specifier with an escape in
    /// it is skipped. Like the declaration scan this is tolerant: a request it misses is
    /// fetched when the graph is evaluated, as before, and one it invents costs a request.
    /// </remarks>
    public static List<string> ModuleRequests(string source)
    {
        var scanner = new Scanner(source ?? string.Empty);
        var requests = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Token previous = default;
        while (true)
        {
            var token = scanner.Next();
            if (token.Kind == Kind.End)
            {
                break;
            }

            var keyword = token.Kind == Kind.Word && token.Depth == 0
                && !(previous.Kind == Kind.Punct && (scanner.IsPunct(previous, ".") || scanner.IsPunct(previous, "?.")))
                ? scanner.Text(token)
                : null;
            previous = token;
            if (keyword is "import")
            {
                var next = scanner.Peek();
                if (next.Kind == Kind.String)
                {
                    scanner.Next();
                    previous = next;
                    AddRequest(scanner, next, requests, seen);
                }
                else if (next.Kind == Kind.Word || scanner.IsPunct(next, "{") || scanner.IsPunct(next, "*"))
                {
                    previous = ReadFromClause(scanner, requests, seen) ?? previous;
                }
            }
            else if (keyword is "export")
            {
                var next = scanner.Peek();
                if (scanner.IsPunct(next, "{") || scanner.IsPunct(next, "*"))
                {
                    previous = ReadFromClause(scanner, requests, seen) ?? previous;
                }
            }
        }

        return requests;
    }

    /// <summary>
    /// Reads an import or export clause up to <c>from "specifier"</c>. Returns the last token
    /// consumed, or null when nothing was.
    /// </summary>
    private static Token? ReadFromClause(Scanner scanner, List<string> requests, HashSet<string> seen)
    {
        Token? last = null;
        var closed = false;
        // An import clause is a handful of tokens outside its braces; the cap only bounds a
        // misread on malformed input.
        for (var budget = 4096; budget > 0; budget--)
        {
            var token = scanner.Peek();
            if (token.Kind == Kind.End)
            {
                return last;
            }

            if (token.Depth == 0)
            {
                if (token.Kind == Kind.Word && scanner.Text(token) == "from")
                {
                    scanner.Next();
                    last = token;
                    var specifier = scanner.Peek();
                    if (specifier.Kind == Kind.String)
                    {
                        scanner.Next();
                        last = specifier;
                        AddRequest(scanner, specifier, requests, seen);
                    }

                    return last;
                }

                // After the named list's closing brace only `from` may follow (`export { a };`
                // ends there).
                var clausePart = !closed
                    && (token.Kind == Kind.Word
                        || scanner.IsPunct(token, "{") || scanner.IsPunct(token, "}")
                        || scanner.IsPunct(token, "*") || scanner.IsPunct(token, ","));
                if (!clausePart)
                {
                    return last;
                }

                closed = scanner.IsPunct(token, "}");
            }
            else if (token.Kind is not (Kind.Word or Kind.String)
                && !scanner.IsPunct(token, ",") && !scanner.IsPunct(token, "}"))
            {
                // Inside the braces only names, string names, commas and the close.
                return last;
            }

            scanner.Next();
            last = token;
        }

        return last;
    }

    private static void AddRequest(Scanner scanner, Token token, List<string> requests, HashSet<string> seen)
    {
        var text = scanner.Text(token);
        if (text.Length < 2 || text[^1] != text[0] || text.Contains('\\') || text.Contains('\n'))
        {
            return;
        }

        var specifier = text[1..^1];
        if (specifier.Length > 0 && seen.Add(specifier))
        {
            requests.Add(specifier);
        }
    }

    private static void Add(List<string> names, HashSet<string> seen, string name)
    {
        if (seen.Add(name))
        {
            names.Add(name);
        }
    }

    private static bool IsReserved(string word) => word is
        "break" or "case" or "catch" or "class" or "const" or "continue" or "debugger" or "default"
        or "delete" or "do" or "else" or "export" or "extends" or "finally" or "for" or "function"
        or "if" or "import" or "in" or "instanceof" or "new" or "return" or "super" or "switch"
        or "this" or "throw" or "try" or "typeof" or "var" or "void" or "while" or "with"
        or "null" or "true" or "false" or "enum";

    private readonly record struct Token(Kind Kind, int Start, int Length, bool NewlineBefore, int Depth);

    /// <summary>A JavaScript tokenizer just precise enough to keep track of nesting.</summary>
    private sealed class Scanner(string source)
    {
        private readonly string _s = source;
        private readonly Stack<bool> _braces = new(); // true: a template substitution's brace
        private int _pos;
        private int _depth;
        private Token _previous = new(Kind.End, 0, 0, false, 0);
        private Token? _peeked;

        /// <summary>The bracket depth after the last consumed token.</summary>
        public int Depth => _peeked is { } ? _peekDepthBefore : _depth;

        private int _peekDepthBefore;

        /// <summary>The last consumed token.</summary>
        public Token Previous => _previous;

        /// <summary>Whether <paramref name="token"/> can end an expression (so a line break after it may end the statement).</summary>
        public bool EndsExpression(Token token) => token.Kind switch
        {
            Kind.Word => Text(token) is not ("return" or "typeof" or "instanceof" or "in" or "of"
                or "new" or "delete" or "void" or "throw" or "case" or "do" or "else" or "yield"
                or "await" or "var" or "let" or "const" or "export" or "extends"),
            Kind.String or Kind.Number or Kind.Template or Kind.Regex => true,
            Kind.Punct => IsPunct(token, ")") || IsPunct(token, "]") || IsPunct(token, "}")
                || IsPunct(token, "++") || IsPunct(token, "--"),
            _ => false,
        };

        public string Text(Token token) => _s.Substring(token.Start, token.Length);

        public bool IsPunct(Token token, string punct) =>
            token.Kind == Kind.Punct && token.Length == punct.Length
            && string.CompareOrdinal(_s, token.Start, punct, 0, punct.Length) == 0;

        public Token Peek()
        {
            if (_peeked is { } peeked)
            {
                return peeked;
            }

            var before = _depth;
            var previous = _previous;
            var token = Read();
            _peekDepthBefore = before;
            _previous = previous;
            _peeked = token;
            return token;
        }

        public Token Next()
        {
            if (_peeked is { } peeked)
            {
                _peeked = null;
                _previous = peeked;
                return peeked;
            }

            var token = Read();
            _previous = token;
            return token;
        }

        /// <summary>
        /// Reads one token and applies its effect on depth. A token's Depth is the depth it
        /// sits at: an opening bracket at the depth outside it, a closing one likewise.
        /// </summary>
        private Token Read()
        {
            var newline = SkipTrivia();
            if (_pos >= _s.Length)
            {
                return new Token(Kind.End, _pos, 0, newline, _depth);
            }

            var start = _pos;
            var c = _s[_pos];

            if (IsIdentStart(c))
            {
                _pos++;
                while (_pos < _s.Length && IsIdentPart(_s[_pos]))
                {
                    _pos++;
                }

                return new Token(Kind.Word, start, _pos - start, newline, _depth);
            }

            if (char.IsAsciiDigit(c) || (c == '.' && _pos + 1 < _s.Length && char.IsAsciiDigit(_s[_pos + 1])))
            {
                _pos++;
                while (_pos < _s.Length && (char.IsAsciiLetterOrDigit(_s[_pos]) || _s[_pos] is '.' or '_'
                    || (_s[_pos] is '+' or '-' && _s[_pos - 1] is 'e' or 'E' && !_s.AsSpan(start, _pos - start).Contains('x'))))
                {
                    _pos++;
                }

                return new Token(Kind.Number, start, _pos - start, newline, _depth);
            }

            switch (c)
            {
                case '"' or '\'':
                    SkipString(c);
                    return new Token(Kind.String, start, _pos - start, newline, _depth);
                case '`':
                {
                    var outside = _depth;
                    _pos++;
                    SkipTemplate();
                    return new Token(Kind.Template, start, _pos - start, newline, outside);
                }
                case '{' or '(' or '[':
                    _pos++;
                    if (c == '{')
                    {
                        _braces.Push(false);
                    }

                    return new Token(Kind.Punct, start, 1, newline, _depth++);
                case '}':
                    _pos++;
                    if (_braces.Count > 0 && _braces.Pop())
                    {
                        // The end of a template substitution: the template continues.
                        _depth--;
                        var outside = _depth;
                        SkipTemplate();
                        return new Token(Kind.Template, start, _pos - start, newline, outside);
                    }

                    _depth = Math.Max(0, _depth - 1);
                    return new Token(Kind.Punct, start, 1, newline, _depth);
                case ')' or ']':
                    _pos++;
                    _depth = Math.Max(0, _depth - 1);
                    return new Token(Kind.Punct, start, 1, newline, _depth);
                case '/' when RegexAllowed():
                    if (TrySkipRegex())
                    {
                        return new Token(Kind.Regex, start, _pos - start, newline, _depth);
                    }

                    _pos = start + 1;
                    return new Token(Kind.Punct, start, 1, newline, _depth);
            }

            // Punctuators: longest match over the ones the declaration reader names.
            foreach (var punct in Puncts)
            {
                if (string.CompareOrdinal(_s, _pos, punct, 0, punct.Length) == 0)
                {
                    _pos += punct.Length;
                    return new Token(Kind.Punct, start, punct.Length, newline, _depth);
                }
            }

            _pos++;
            return new Token(Kind.Punct, start, 1, newline, _depth);
        }

        private static readonly string[] Puncts = ["...", "=>", "===", "!==", "==", "!=", "++", "--", "&&", "||", "??", "?."];

        private bool RegexAllowed() => _previous.Kind switch
        {
            Kind.End => true,
            Kind.Word => !EndsExpression(_previous),
            Kind.Punct => !(IsPunct(_previous, ")") || IsPunct(_previous, "]") || IsPunct(_previous, "}")
                || IsPunct(_previous, "++") || IsPunct(_previous, "--")),
            _ => false,
        };

        private bool TrySkipRegex()
        {
            var inClass = false;
            _pos++;
            while (_pos < _s.Length)
            {
                var c = _s[_pos];
                if (c is '\n' or '\r' or '\u2028' or '\u2029')
                {
                    return false;
                }

                _pos++;
                if (c == '\\')
                {
                    _pos++;
                }
                else if (c == '[')
                {
                    inClass = true;
                }
                else if (c == ']')
                {
                    inClass = false;
                }
                else if (c == '/' && !inClass)
                {
                    while (_pos < _s.Length && IsIdentPart(_s[_pos]))
                    {
                        _pos++;
                    }

                    return true;
                }
            }

            return false;
        }

        private void SkipString(char quote)
        {
            _pos++;
            while (_pos < _s.Length)
            {
                var c = _s[_pos++];
                if (c == '\\')
                {
                    _pos++;
                }
                else if (c == quote || c == '\n')
                {
                    return;
                }
            }
        }

        /// <summary>Skips template characters up to the closing backtick or a <c>${</c>.</summary>
        private void SkipTemplate()
        {
            while (_pos < _s.Length)
            {
                var c = _s[_pos++];
                if (c == '\\')
                {
                    _pos++;
                }
                else if (c == '`')
                {
                    return;
                }
                else if (c == '$' && _pos < _s.Length && _s[_pos] == '{')
                {
                    _pos++;
                    _braces.Push(true);
                    _depth++;
                    return;
                }
            }
        }

        /// <summary>Skips whitespace and comments; returns whether a line break was among them.</summary>
        private bool SkipTrivia()
        {
            var newline = false;
            while (_pos < _s.Length)
            {
                var c = _s[_pos];
                if (c is '\n' or '\r' or '\u2028' or '\u2029')
                {
                    newline = true;
                    _pos++;
                }
                else if (char.IsWhiteSpace(c) || c == '\uFEFF')
                {
                    _pos++;
                }
                else if (c == '/' && _pos + 1 < _s.Length && _s[_pos + 1] == '/')
                {
                    while (_pos < _s.Length && _s[_pos] is not ('\n' or '\r' or '\u2028' or '\u2029'))
                    {
                        _pos++;
                    }
                }
                else if (c == '/' && _pos + 1 < _s.Length && _s[_pos + 1] == '*')
                {
                    var end = _s.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                    var stop = end < 0 ? _s.Length : end + 2;
                    if (_s.AsSpan(_pos, stop - _pos).IndexOfAny('\n', '\r') >= 0)
                    {
                        newline = true;
                    }

                    _pos = stop;
                }
                else if (c == '#' && _pos == 0 && _s.Length > 1 && _s[1] == '!')
                {
                    while (_pos < _s.Length && _s[_pos] is not ('\n' or '\r'))
                    {
                        _pos++;
                    }
                }
                else
                {
                    break;
                }
            }

            return newline;
        }

        private static bool IsIdentStart(char c) =>
            char.IsAsciiLetter(c) || c is '_' or '$' or '\\' || (c > 0x7F && char.IsLetter(c));

        private static bool IsIdentPart(char c) =>
            IsIdentStart(c) || char.IsAsciiDigit(c) || (c > 0x7F && (char.IsLetterOrDigit(c) || c is '\u200C' or '\u200D'));
    }
}
