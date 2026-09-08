using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Obscura.Dom.Selectors;

/// <summary>
/// The selector parser. Ported in-tree rather than delegated to AngleSharp because the render
/// cascade needs our own specificity, ancestor hashes and subject-key internals.
/// </summary>
public static class SelectorParser
{
    // Thread-local cache of parsed selectors. Without this every querySelector /
    // querySelectorAll re-parses the selector string; for batch-heavy DOM access (agent scraping a
    // table, framework repeatedly polling for elements) the parse cost adds up to tens of ms per
    // page. Cap of 256 entries fits a typical page's distinct selectors without unbounded memory
    // growth.
    private const int SelectorCacheCap = 256;

    [ThreadStatic]
    private static Dictionary<string, SelectorList>? _cache;

    /// <summary>Parse a selector list, throwing <see cref="SelectorParseException"/> on failure.</summary>
    public static SelectorList Parse(string selector)
    {
        // Hot path: cached. Cold path: parse + insert.
        var cache = _cache ??= new Dictionary<string, SelectorList>(64, StringComparer.Ordinal);
        if (cache.TryGetValue(selector, out var cached))
        {
            return cached;
        }

        var parsed = ParseUncached(selector);
        // Crude eviction: if at cap, dump the whole table. A real LRU would be more
        // memory-friendly but selectors are small and 256 is comfortably above a single page's
        // distinct-selector count.
        if (cache.Count >= SelectorCacheCap)
        {
            cache.Clear();
        }

        cache[selector] = parsed;
        return parsed;
    }

    /// <summary>Parse a selector list, reporting failure instead of throwing.</summary>
    public static bool TryParse(
        string selector,
        [NotNullWhen(true)] out SelectorList? list,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            list = Parse(selector);
            error = null;
            return true;
        }
        catch (SelectorParseException e)
        {
            list = null;
            error = e.Message;
            return false;
        }
    }

    internal static SelectorList ParseUncached(string selector)
    {
        var parser = new Parser(selector);
        try
        {
            var list = parser.ParseSelectorList(forgiving: false);
            parser.SkipWhitespaceAndComments();
            if (!parser.AtEnd)
            {
                throw new ParseFailure($"unexpected trailing input at {parser.Position}");
            }

            return list;
        }
        catch (ParseFailure failure)
        {
            throw new SelectorParseException(selector, failure.Message);
        }
    }

    private sealed class ParseFailure(string message) : Exception(message);

    // ------------------------------------------------------------------ the recursive descent parser

    private sealed class Parser(string input)
    {
        private readonly string _input = input;
        private int _pos;

        public int Position => _pos;

        public bool AtEnd => _pos >= _input.Length;

        private char Peek => _pos < _input.Length ? _input[_pos] : '\0';

        private char PeekAt(int offset) => _pos + offset < _input.Length ? _input[_pos + offset] : '\0';

        private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

        public bool SkipWhitespaceAndComments()
        {
            var any = false;
            while (!AtEnd)
            {
                if (IsWhitespace(Peek))
                {
                    any = true;
                    _pos++;
                }
                else if (Peek == '/' && PeekAt(1) == '*')
                {
                    var end = _input.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                    _pos = end < 0 ? _input.Length : end + 2;
                }
                else
                {
                    break;
                }
            }

            return any;
        }

        public SelectorList ParseSelectorList(bool forgiving)
        {
            var selectors = new List<Selector>();
            while (true)
            {
                SkipWhitespaceAndComments();
                if (forgiving)
                {
                    var start = _pos;
                    try
                    {
                        selectors.Add(ParseComplexSelector());
                    }
                    catch (ParseFailure)
                    {
                        // A forgiving list (:is / :where) keeps an invalid arm as a selector that
                        // can never match, instead of discarding the whole list.
                        _pos = start;
                        SkipArm();
                        selectors.Add(BuildSelector(
                            [[InvalidComponent.Instance]],
                            []));
                    }
                }
                else
                {
                    selectors.Add(ParseComplexSelector());
                }

                SkipWhitespaceAndComments();
                if (!AtEnd && Peek == ',')
                {
                    _pos++;
                    continue;
                }

                break;
            }

            return new SelectorList([.. selectors]);
        }

        // Skip past an unparsable arm of a forgiving list, honoring nesting and strings.
        private void SkipArm()
        {
            var depth = 0;
            while (!AtEnd)
            {
                var c = Peek;
                if (c is '"' or '\'')
                {
                    ReadString(c);
                    continue;
                }

                if (c is '(' or '[')
                {
                    depth++;
                }
                else if (c is ')' or ']')
                {
                    if (depth == 0)
                    {
                        return;
                    }

                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    return;
                }
                else if (c == '\\')
                {
                    _pos++;
                }

                _pos++;
            }
        }

        public Selector ParseComplexSelector()
        {
            var compounds = new List<List<Component>>();
            var combinators = new List<Combinator>();

            while (true)
            {
                ParseCompound(compounds, combinators);

                var sawWhitespace = SkipWhitespaceAndComments();
                if (AtEnd || Peek is ',' or ')')
                {
                    break;
                }

                Combinator combinator;
                switch (Peek)
                {
                    case '>':
                        combinator = Combinator.Child;
                        _pos++;
                        break;
                    case '+':
                        combinator = Combinator.NextSibling;
                        _pos++;
                        break;
                    case '~':
                        combinator = Combinator.LaterSibling;
                        _pos++;
                        break;
                    default:
                        if (!sawWhitespace)
                        {
                            throw new ParseFailure($"unexpected '{Peek}' at {_pos}");
                        }

                        combinator = Combinator.Descendant;
                        break;
                }

                SkipWhitespaceAndComments();
                if (AtEnd || Peek is ',' or ')')
                {
                    throw new ParseFailure("dangling combinator");
                }

                combinators.Add(combinator);
            }

            return BuildSelector(compounds, combinators);
        }

        /// <summary>
        /// One arm of <c>:has()</c>: an optional leading combinator, then a complex selector, with
        /// a synthetic anchor compound appended on the left.
        /// </summary>
        private RelativeSelector ParseRelativeSelector()
        {
            SkipWhitespaceAndComments();
            var combinator = Combinator.Descendant;
            switch (Peek)
            {
                case '>':
                    combinator = Combinator.Child;
                    _pos++;
                    break;
                case '+':
                    combinator = Combinator.NextSibling;
                    _pos++;
                    break;
                case '~':
                    combinator = Combinator.LaterSibling;
                    _pos++;
                    break;
            }

            SkipWhitespaceAndComments();
            var inner = ParseComplexSelector();

            var compounds = new CompoundSelector[inner.Compounds.Length + 1];
            Array.Copy(inner.Compounds, compounds, inner.Compounds.Length);
            compounds[^1] = new CompoundSelector([RelativeSelectorAnchorComponent.Instance]);

            var combinators = new Combinator[inner.Combinators.Length + 1];
            Array.Copy(inner.Combinators, combinators, inner.Combinators.Length);
            combinators[^1] = combinator;

            return new RelativeSelector(
                combinator,
                new Selector(compounds, combinators, inner.Specificity, inner.Flags));
        }

        private void ParseCompound(List<List<Component>> compounds, List<Combinator> combinators)
        {
            var components = new List<Component>();
            compounds.Add(components);
            var sawAny = false;

            // Optional type selector (with namespace prefix) comes first.
            if (TryParseTypeSelector(components))
            {
                sawAny = true;
            }

            while (!AtEnd)
            {
                var c = Peek;
                if (c == '#')
                {
                    _pos++;
                    components.Add(new IdComponent(ReadIdent()));
                    sawAny = true;
                }
                else if (c == '.')
                {
                    _pos++;
                    components.Add(new ClassComponent(ReadIdent()));
                    sawAny = true;
                }
                else if (c == '[')
                {
                    _pos++;
                    components.Add(ParseAttributeSelector());
                    sawAny = true;
                }
                else if (c == ':')
                {
                    if (PeekAt(1) == ':')
                    {
                        _pos += 2;
                        // A pseudo-element opens a new compound joined by its own combinator, so
                        // matching walks back to the originating element the same way the
                        // reference engine does.
                        var (component, combinator) = ParsePseudoElement();
                        combinators.Add(combinator);
                        components = [component];
                        compounds.Add(components);
                        sawAny = true;
                        continue;
                    }

                    _pos++;
                    components.Add(ParsePseudoClass());
                    sawAny = true;
                }
                else
                {
                    break;
                }
            }

            if (!sawAny)
            {
                throw new ParseFailure(AtEnd ? "empty selector" : $"unexpected '{Peek}' at {_pos}");
            }
        }

        private bool TryParseTypeSelector(List<Component> components)
        {
            // `*`, `ns|name`, `*|name`, `|name`, `name`.
            if (Peek == '*')
            {
                if (PeekAt(1) == '|')
                {
                    _pos += 2;
                    components.Add(ExplicitAnyNamespaceComponent.Instance);
                    AddLocalNameOrUniversal(components);
                    return true;
                }

                _pos++;
                components.Add(ExplicitUniversalTypeComponent.Instance);
                return true;
            }

            if (Peek == '|' && PeekAt(1) != '=')
            {
                _pos++;
                components.Add(ExplicitNoNamespaceComponent.Instance);
                AddLocalNameOrUniversal(components);
                return true;
            }

            if (!IsIdentStart())
            {
                return false;
            }

            var start = _pos;
            var name = ReadIdent();
            if (Peek == '|' && PeekAt(1) != '=')
            {
                // Obscura's selector implementation declares no namespace prefixes, so a prefixed
                // type selector is a parse error, exactly as in the reference engine.
                _pos = start;
                throw new ParseFailure($"unknown namespace prefix '{name}'");
            }

            components.Add(new LocalNameComponent(name, AsciiLowercase(name)));
            return true;
        }

        private void AddLocalNameOrUniversal(List<Component> components)
        {
            if (Peek == '*')
            {
                _pos++;
                components.Add(ExplicitUniversalTypeComponent.Instance);
                return;
            }

            var name = ReadIdent();
            components.Add(new LocalNameComponent(name, AsciiLowercase(name)));
        }

        private Component ParseAttributeSelector()
        {
            SkipWhitespaceAndComments();

            var namespaceKind = NamespaceConstraintKind.NoNamespace;
            string? namespaceUrl = null;

            if (Peek == '*' && PeekAt(1) == '|')
            {
                _pos += 2;
                namespaceKind = NamespaceConstraintKind.Any;
            }
            else if (Peek == '|' && PeekAt(1) != '=')
            {
                _pos++;
            }

            if (!IsIdentStart())
            {
                throw new ParseFailure("no qualified name in attribute selector");
            }

            var localName = ReadIdent();
            if (namespaceKind == NamespaceConstraintKind.NoNamespace && Peek == '|' && PeekAt(1) != '=')
            {
                throw new ParseFailure($"unknown namespace prefix '{localName}'");
            }

            var localNameLower = AsciiLowercase(localName);
            SkipWhitespaceAndComments();

            if (Peek == ']')
            {
                _pos++;
                return new AttributeComponent(
                    namespaceKind,
                    namespaceUrl,
                    localName,
                    localNameLower,
                    op: null,
                    value: null,
                    ParsedCaseSensitivity.CaseSensitive);
            }

            AttrOperator op;
            switch (Peek)
            {
                case '=':
                    op = AttrOperator.Equal;
                    _pos++;
                    break;
                case '~' when PeekAt(1) == '=':
                    op = AttrOperator.Includes;
                    _pos += 2;
                    break;
                case '|' when PeekAt(1) == '=':
                    op = AttrOperator.DashMatch;
                    _pos += 2;
                    break;
                case '^' when PeekAt(1) == '=':
                    op = AttrOperator.Prefix;
                    _pos += 2;
                    break;
                case '*' when PeekAt(1) == '=':
                    op = AttrOperator.Substring;
                    _pos += 2;
                    break;
                case '$' when PeekAt(1) == '=':
                    op = AttrOperator.Suffix;
                    _pos += 2;
                    break;
                default:
                    throw new ParseFailure($"unexpected token in attribute selector at {_pos}");
            }

            SkipWhitespaceAndComments();
            string value;
            if (Peek is '"' or '\'')
            {
                value = ReadString(Peek);
            }
            else if (IsIdentStart())
            {
                value = ReadIdent();
            }
            else
            {
                throw new ParseFailure("bad value in attribute selector");
            }

            SkipWhitespaceAndComments();
            var flags = ParsedCaseSensitivity.CaseSensitive;
            var explicitFlag = false;
            if (IsIdentStart())
            {
                var flag = ReadIdent();
                if (flag.Equals("i", StringComparison.OrdinalIgnoreCase))
                {
                    flags = ParsedCaseSensitivity.AsciiCaseInsensitive;
                }
                else if (flag.Equals("s", StringComparison.OrdinalIgnoreCase))
                {
                    flags = ParsedCaseSensitivity.ExplicitCaseSensitive;
                }
                else
                {
                    throw new ParseFailure($"invalid attribute flag '{flag}'");
                }

                explicitFlag = true;
                SkipWhitespaceAndComments();
            }

            if (Peek != ']')
            {
                throw new ParseFailure("unclosed attribute selector");
            }

            _pos++;

            if (!explicitFlag
                && namespaceKind == NamespaceConstraintKind.NoNamespace
                && AsciiCaseInsensitiveHtmlAttributes.Contains(localNameLower))
            {
                flags = ParsedCaseSensitivity.AsciiCaseInsensitiveIfInHtmlElementInHtmlDocument;
            }

            return new AttributeComponent(
                namespaceKind,
                namespaceUrl,
                localName,
                localNameLower,
                op,
                value,
                flags);
        }

        private (Component Component, Combinator Combinator) ParsePseudoElement()
        {
            var name = ReadIdent();
            var lower = AsciiLowercase(name);
            switch (lower)
            {
                // ::before / ::after are deliberately NOT parsed here. The reference selector
                // implementation supplies no pseudo-element parser, so any `::pseudo` other than
                // ::slotted() fails the whole selector list; the render cascade compiles the base
                // selector without its pseudo-element instead.
                case "slotted":
                {
                    ExpectOpenParen();
                    SkipWhitespaceAndComments();
                    var inner = ParseComplexSelector();
                    if (inner.Compounds.Length != 1)
                    {
                        throw new ParseFailure("::slotted() takes a compound selector");
                    }

                    SkipWhitespaceAndComments();
                    ExpectCloseParen();
                    return (new SlottedComponent(inner), Combinator.SlotAssignment);
                }

                default:
                    throw new ParseFailure($"unsupported pseudo-element '::{name}'");
            }
        }

        private Component ParsePseudoClass()
        {
            var name = ReadIdent();
            var lower = AsciiLowercase(name);
            var functional = Peek == '(';

            if (!functional)
            {
                return lower switch
                {
                    "root" => RootComponent.Instance,
                    "empty" => EmptyComponent.Instance,
                    "scope" => ScopeComponent.Instance,
                    "host" => new HostComponent(null),
                    "first-child" => new NthComponent(new NthData(NthType.Child, 0, 1), null),
                    "last-child" => new NthComponent(new NthData(NthType.LastChild, 0, 1), null),
                    "only-child" => new NthComponent(new NthData(NthType.OnlyChild, 0, 1), null),
                    "first-of-type" => new NthComponent(new NthData(NthType.OfType, 0, 1), null),
                    "last-of-type" => new NthComponent(new NthData(NthType.LastOfType, 0, 1), null),
                    "only-of-type" => new NthComponent(new NthData(NthType.OnlyOfType, 0, 1), null),
                    _ => new NonTsPseudoClassComponent(ParseNonTsPseudoClass(name, lower)),
                };
            }

            ExpectOpenParen();
            Component component;
            switch (lower)
            {
                case "not":
                {
                    // :not() is not forgiving.
                    var list = ParseSelectorList(forgiving: false);
                    component = new NegationComponent(list);
                    break;
                }

                case "is":
                {
                    var list = ParseSelectorList(forgiving: true);
                    component = new IsComponent(list);
                    break;
                }

                case "where":
                {
                    var list = ParseSelectorList(forgiving: true);
                    component = new WhereComponent(list);
                    break;
                }

                case "has":
                {
                    var relatives = new List<RelativeSelector>();
                    while (true)
                    {
                        relatives.Add(ParseRelativeSelector());
                        SkipWhitespaceAndComments();
                        if (!AtEnd && Peek == ',')
                        {
                            _pos++;
                            continue;
                        }

                        break;
                    }

                    component = new HasComponent([.. relatives]);
                    break;
                }

                case "host":
                {
                    SkipWhitespaceAndComments();
                    var inner = ParseComplexSelector();
                    if (inner.Compounds.Length != 1)
                    {
                        throw new ParseFailure(":host() takes a compound selector");
                    }

                    component = new HostComponent(inner);
                    break;
                }

                case "nth-child":
                    component = ParseNthPseudoClass(NthType.Child, allowOf: true);
                    break;
                case "nth-last-child":
                    component = ParseNthPseudoClass(NthType.LastChild, allowOf: true);
                    break;
                case "nth-of-type":
                    component = ParseNthPseudoClass(NthType.OfType, allowOf: false);
                    break;
                case "nth-last-of-type":
                    component = ParseNthPseudoClass(NthType.LastOfType, allowOf: false);
                    break;

                default:
                    throw new ParseFailure($"unsupported pseudo-class ':{name}()'");
            }

            SkipWhitespaceAndComments();
            ExpectCloseParen();
            return component;
        }

        private static PseudoClass ParseNonTsPseudoClass(string name, string lower) => lower switch
        {
            "hover" => PseudoClass.Hover,
            "active" => PseudoClass.Active,
            "focus" => PseudoClass.Focus,
            "focus-visible" => PseudoClass.FocusVisible,
            "focus-within" => PseudoClass.FocusWithin,
            "enabled" => PseudoClass.Enabled,
            "disabled" => PseudoClass.Disabled,
            "checked" => PseudoClass.Checked,
            "link" or "any-link" => PseudoClass.Link,
            "visited" => PseudoClass.Visited,
            _ => throw new ParseFailure($"unsupported pseudo-class ':{name}'"),
        };

        private Component ParseNthPseudoClass(NthType type, bool allowOf)
        {
            var (a, b) = ParseNth();
            SkipWhitespaceAndComments();
            SelectorList? of = null;
            if (allowOf && IsIdentStart())
            {
                var start = _pos;
                var word = ReadIdent();
                if (!word.Equals("of", StringComparison.OrdinalIgnoreCase))
                {
                    _pos = start;
                    throw new ParseFailure("expected 'of' in :nth-child()");
                }

                of = ParseSelectorList(forgiving: false);
            }

            return new NthComponent(new NthData(type, a, b), of);
        }

        private (int A, int B) ParseNth()
        {
            SkipWhitespaceAndComments();
            if (IsIdentStart())
            {
                var start = _pos;
                var word = ReadIdent();
                if (word.Equals("odd", StringComparison.OrdinalIgnoreCase))
                {
                    return (2, 1);
                }

                if (word.Equals("even", StringComparison.OrdinalIgnoreCase))
                {
                    return (2, 0);
                }

                // `n`, `n+2`, `-n+3` and friends fall through to the numeric path.
                _pos = start;
            }

            var a = 0;
            var b = 0;
            var sign = 1;
            if (Peek is '+' or '-')
            {
                sign = Peek == '-' ? -1 : 1;
                _pos++;
            }

            var digitsStart = _pos;
            while (!AtEnd && char.IsAsciiDigit(Peek))
            {
                _pos++;
            }

            var hasDigits = _pos > digitsStart;
            var number = hasDigits ? int.Parse(_input.AsSpan(digitsStart, _pos - digitsStart)) : 1;

            if (!AtEnd && (Peek is 'n' or 'N'))
            {
                _pos++;
                a = sign * number;

                SkipWhitespaceAndComments();
                if (!AtEnd && Peek is '+' or '-')
                {
                    var bSign = Peek == '-' ? -1 : 1;
                    _pos++;
                    SkipWhitespaceAndComments();
                    var bStart = _pos;
                    while (!AtEnd && char.IsAsciiDigit(Peek))
                    {
                        _pos++;
                    }

                    if (_pos == bStart)
                    {
                        throw new ParseFailure("expected an integer in An+B");
                    }

                    b = bSign * int.Parse(_input.AsSpan(bStart, _pos - bStart));
                }

                return (a, b);
            }

            if (!hasDigits)
            {
                throw new ParseFailure("expected An+B");
            }

            return (0, sign * number);
        }

        private void ExpectOpenParen()
        {
            if (Peek != '(')
            {
                throw new ParseFailure("expected '('");
            }

            _pos++;
        }

        private void ExpectCloseParen()
        {
            if (Peek != ')')
            {
                throw new ParseFailure("expected ')'");
            }

            _pos++;
        }

        // -------------------------------------------------------------- lexical helpers

        private bool IsIdentStart()
        {
            var c = Peek;
            if (c == '\\')
            {
                return true;
            }

            if (c == '-')
            {
                var next = PeekAt(1);
                return next == '-' || next == '_' || next == '\\' || char.IsAsciiLetter(next) || next >= 0x80;
            }

            return c == '_' || char.IsAsciiLetter(c) || c >= 0x80;
        }

        private static bool IsIdentChar(char c) =>
            c == '_' || c == '-' || char.IsAsciiLetterOrDigit(c) || c >= 0x80;

        private string ReadIdent()
        {
            if (!IsIdentStart())
            {
                throw new ParseFailure($"expected an identifier at {_pos}");
            }

            var sb = new StringBuilder();
            while (!AtEnd)
            {
                var c = Peek;
                if (c == '\\')
                {
                    _pos++;
                    AppendEscape(sb);
                }
                else if (IsIdentChar(c))
                {
                    sb.Append(c);
                    _pos++;
                }
                else
                {
                    break;
                }
            }

            if (sb.Length == 0)
            {
                throw new ParseFailure($"expected an identifier at {_pos}");
            }

            return sb.ToString();
        }

        private string ReadString(char quote)
        {
            _pos++;
            var sb = new StringBuilder();
            while (!AtEnd)
            {
                var c = Peek;
                if (c == quote)
                {
                    _pos++;
                    return sb.ToString();
                }

                if (c == '\\')
                {
                    _pos++;
                    if (!AtEnd && Peek == '\n')
                    {
                        _pos++;
                        continue;
                    }

                    AppendEscape(sb);
                    continue;
                }

                sb.Append(c);
                _pos++;
            }

            throw new ParseFailure("unterminated string");
        }

        private void AppendEscape(StringBuilder sb)
        {
            if (AtEnd)
            {
                sb.Append('\uFFFD');
                return;
            }

            var c = Peek;
            if (!char.IsAsciiHexDigit(c))
            {
                sb.Append(c);
                _pos++;
                return;
            }

            var value = 0;
            var digits = 0;
            while (digits < 6 && !AtEnd && char.IsAsciiHexDigit(Peek))
            {
                value = (value * 16) + Convert.ToInt32(Peek.ToString(), 16);
                _pos++;
                digits++;
            }

            if (!AtEnd && IsWhitespace(Peek))
            {
                _pos++;
            }

            if (value == 0 || value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF))
            {
                sb.Append('\uFFFD');
            }
            else
            {
                sb.Append(char.ConvertFromUtf32(value));
            }
        }
    }

    // ------------------------------------------------------------------ building

    internal static Selector BuildSelector(
        List<List<Component>> parseOrderCompounds,
        List<Combinator> parseOrderCombinators)
    {
        var count = parseOrderCompounds.Count;
        var compounds = new CompoundSelector[count];
        for (var i = 0; i < count; i++)
        {
            compounds[i] = new CompoundSelector([.. parseOrderCompounds[count - 1 - i]]);
        }

        var combinators = new Combinator[parseOrderCombinators.Count];
        for (var i = 0; i < combinators.Length; i++)
        {
            combinators[i] = parseOrderCombinators[combinators.Length - 1 - i];
        }

        var (specificity, flags) = ComputeSpecificityAndFlags(compounds);
        return new Selector(compounds, combinators, specificity, flags);
    }

    private const uint Max10Bit = (1u << 10) - 1;

    private static uint PackSpecificity(uint id, uint classLike, uint element) =>
        (Math.Min(id, Max10Bit) << 20) | (Math.Min(classLike, Max10Bit) << 10) | Math.Min(element, Max10Bit);

    private static (uint Id, uint ClassLike, uint Element) UnpackSpecificity(uint value) =>
        (value >> 20, (value >> 10) & Max10Bit, value & Max10Bit);

    private static (uint Specificity, SelectorFlags Flags) ComputeSpecificityAndFlags(
        CompoundSelector[] compounds)
    {
        uint id = 0;
        uint classLike = 0;
        uint element = 0;
        var flags = SelectorFlags.None;

        void Add(uint packed)
        {
            var (a, b, c) = UnpackSpecificity(packed);
            id += a;
            classLike += b;
            element += c;
        }

        foreach (var compound in compounds)
        {
            foreach (var component in compound.Components)
            {
                switch (component)
                {
                    case PseudoElementComponent:
                        flags |= SelectorFlags.HasPseudo;
                        element += 1;
                        break;

                    case LocalNameComponent:
                        flags |= SelectorFlags.HasNonFeaturelessComponent;
                        element += 1;
                        break;

                    case SlottedComponent slotted:
                        flags |= SelectorFlags.HasSlotted | SelectorFlags.HasNonFeaturelessComponent;
                        element += 1;
                        Add(slotted.Selector.Specificity);
                        flags |= slotted.Selector.Flags;
                        break;

                    case HostComponent host:
                        flags |= SelectorFlags.HasHost;
                        classLike += 1;
                        if (host.Selector is { } hostSelector)
                        {
                            Add(hostSelector.Specificity);
                            flags |= hostSelector.Flags & ~SelectorFlags.HasNonFeaturelessComponent;
                        }

                        break;

                    case IdComponent:
                        flags |= SelectorFlags.HasNonFeaturelessComponent;
                        id += 1;
                        break;

                    case ClassComponent:
                    case AttributeComponent:
                    case RootComponent:
                    case EmptyComponent:
                    case NonTsPseudoClassComponent:
                        flags |= SelectorFlags.HasNonFeaturelessComponent;
                        classLike += 1;
                        break;

                    case ScopeComponent:
                        flags |= SelectorFlags.HasScope;
                        classLike += 1;
                        break;

                    case NthComponent nth:
                    {
                        // https://drafts.csswg.org/selectors/#specificity-rules: the specificity of
                        // :nth-child() combines a regular pseudo-class with that of its selector
                        // argument S.
                        classLike += 1;
                        flags |= SelectorFlags.HasNonFeaturelessComponent;
                        if (nth.Of is { } of)
                        {
                            var (spec, listFlags) = ListSpecificityAndFlags(of.Selectors);
                            Add(spec);
                            flags |= listFlags;
                        }

                        break;
                    }

                    case WhereComponent where:
                    {
                        // :where() contributes zero specificity but still carries flags.
                        var (_, listFlags) = ListSpecificityAndFlags(where.List.Selectors);
                        flags |= listFlags;
                        break;
                    }

                    case NegationComponent negation:
                    {
                        var (spec, listFlags) = ListSpecificityAndFlags(negation.List.Selectors);
                        Add(spec);
                        flags |= listFlags;
                        break;
                    }

                    case IsComponent isComponent:
                    {
                        var (spec, listFlags) = ListSpecificityAndFlags(isComponent.List.Selectors);
                        Add(spec);
                        flags |= listFlags;
                        break;
                    }

                    case HasComponent has:
                    {
                        var selectors = new Selector[has.Relatives.Length];
                        for (var i = 0; i < has.Relatives.Length; i++)
                        {
                            selectors[i] = has.Relatives[i].Selector;
                        }

                        var (spec, listFlags) = ListSpecificityAndFlags(selectors);
                        Add(spec);
                        flags |= listFlags | SelectorFlags.HasNonFeaturelessComponent;
                        break;
                    }

                    case ExplicitUniversalTypeComponent:
                    case ExplicitAnyNamespaceComponent:
                    case ExplicitNoNamespaceComponent:
                    case RelativeSelectorAnchorComponent:
                    case InvalidComponent:
                        flags |= SelectorFlags.HasNonFeaturelessComponent;
                        break;
                }
            }
        }

        return (PackSpecificity(id, classLike, element), flags);
    }

    /// <summary>Finds the maximum specificity of the selectors in a list and returns it.</summary>
    private static (uint Specificity, SelectorFlags Flags) ListSpecificityAndFlags(Selector[] selectors)
    {
        uint specificity = 0;
        var flags = SelectorFlags.None;
        foreach (var selector in selectors)
        {
            specificity = Math.Max(specificity, selector.Specificity);
            flags |= selector.Flags;
        }

        return (specificity, flags);
    }

    internal static string AsciiLowercase(string value)
    {
        foreach (var c in value)
        {
            if (char.IsAsciiLetterUpper(c))
            {
                return string.Create(value.Length, value, static (span, source) =>
                {
                    for (var i = 0; i < source.Length; i++)
                    {
                        span[i] = char.IsAsciiLetterUpper(source[i])
                            ? (char)(source[i] + 32)
                            : source[i];
                    }
                });
            }
        }

        return value;
    }

    /// <summary>
    /// The HTML attributes whose values match ASCII-case-insensitively in an HTML element in an
    /// HTML document. https://html.spec.whatwg.org/multipage/#selectors
    /// </summary>
    private static readonly HashSet<string> AsciiCaseInsensitiveHtmlAttributes = new(StringComparer.Ordinal)
    {
        "accept", "accept-charset", "align", "alink", "axis", "bgcolor", "charset", "checked",
        "clear", "codetype", "color", "compact", "declare", "defer", "dir", "direction",
        "disabled", "enctype", "face", "frame", "hreflang", "http-equiv", "lang", "language",
        "link", "media", "method", "multiple", "nohref", "noresize", "noshade", "nowrap",
        "readonly", "rel", "rev", "rules", "scope", "scrolling", "selected", "shape", "target",
        "text", "type", "valign", "valuetype", "vlink",
    };
}
