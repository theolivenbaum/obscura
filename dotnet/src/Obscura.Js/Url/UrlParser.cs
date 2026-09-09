using System.Globalization;
using System.Text;

namespace Obscura.Js.Url;

/// <summary>Which algorithm is driving the parser; it changes a few terminator rules.</summary>
internal enum ParseContext
{
    UrlParser,
    Setter,
    PathSegmentSetter,
}

/// <summary>
/// The parser's cursor. ASCII tab and newline are stripped up front because the spec's
/// iteration skips them everywhere, so filtering once keeps every later rule simple.
/// </summary>
internal struct Input(string s, int pos)
{
    public readonly string S = s;
    public int Pos = pos;

    public static Input NoTrim(string value) => new(Filter(value), 0);

    public static Input TrimTabAndNewlines(string value) => new(Filter(value), 0);

    public static Input TrimC0ControlAndSpace(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && value[start] <= ' ')
        {
            start++;
        }

        while (end > start && value[end - 1] <= ' ')
        {
            end--;
        }

        return new Input(Filter(value[start..end]), 0);
    }

    private static string Filter(string value)
    {
        var needs = false;
        foreach (var c in value)
        {
            if (c is '\t' or '\n' or '\r')
            {
                needs = true;
                break;
            }
        }

        if (!needs)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is not ('\t' or '\n' or '\r'))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public readonly bool IsEmpty => Pos >= S.Length;

    public readonly char? First => Pos < S.Length ? S[Pos] : null;

    public char? Next() => Pos < S.Length ? S[Pos++] : null;

    public readonly string Remainder => S[Pos..];

    public readonly bool StartsWith(char c) => Pos < S.Length && S[Pos] == c;

    public readonly bool StartsWith(string prefix) =>
        S.AsSpan(Pos).StartsWith(prefix, StringComparison.Ordinal);

    public readonly Input? SplitPrefix(char c) => StartsWith(c) ? new Input(S, Pos + 1) : null;

    public readonly Input? SplitPrefix(string prefix) =>
        StartsWith(prefix) ? new Input(S, Pos + prefix.Length) : null;

    public readonly (char? Head, Input Tail) SplitFirst() =>
        Pos < S.Length ? (S[Pos], new Input(S, Pos + 1)) : (null, this);

    public readonly (int Count, Input Tail) CountMatching(Func<char, bool> predicate)
    {
        var i = Pos;
        while (i < S.Length && predicate(S[i]))
        {
            i++;
        }

        return (i - Pos, new Input(S, i));
    }
}

/// <summary>
/// The WHATWG basic URL parser, ported from the Rust <c>url</c> crate's parser so the C#
/// engine and the reference engine serialize identically.
/// <see href="https://url.spec.whatwg.org/#concept-basic-url-parser"/>.
/// </summary>
internal sealed class UrlParser
{
    private readonly StringBuilder _ser;
    private readonly UrlRecord? _base;
    private readonly ParseContext _context;

    private UrlParser(StringBuilder serialization, UrlRecord? baseUrl, ParseContext context)
    {
        _ser = serialization;
        _base = baseUrl;
        _context = context;
    }

    /// <summary>Parses <paramref name="input"/>, optionally against <paramref name="baseUrl"/>.</summary>
    public static UrlRecord? Parse(string input, UrlRecord? baseUrl) =>
        new UrlParser(new StringBuilder(input.Length + 16), baseUrl, ParseContext.UrlParser)
            .ParseUrl(input);

    /// <summary>Parses a scheme for the <c>protocol</c> setter (setter context).</summary>
    public static bool TryParseSetterScheme(string scheme, out string parsed, out bool remainingEmpty)
    {
        var parser = new UrlParser(new StringBuilder(scheme.Length), null, ParseContext.Setter);
        var input = Input.NoTrim(scheme);
        if (!parser.ParseScheme(input, out var remaining))
        {
            parsed = string.Empty;
            remainingEmpty = false;
            return false;
        }

        parsed = parser._ser.ToString();
        remainingEmpty = remaining.IsEmpty;
        return true;
    }

    // ------------------------------------------------------------ serialization helpers

    private int Len => _ser.Length;

    private string Slice(int start, int end) => _ser.ToString(start, end - start);

    private void Truncate(int length) => _ser.Length = length;

    private bool EndsWith(char c) => _ser.Length > 0 && _ser[^1] == c;

    // ------------------------------------------------------------ entry points

    private UrlRecord? ParseUrl(string rawInput)
    {
        var input = Input.TrimC0ControlAndSpace(rawInput);
        if (ParseScheme(input, out var remaining))
        {
            return ParseWithScheme(remaining);
        }

        if (_base is null)
        {
            return null;
        }

        if (input.StartsWith('#'))
        {
            return FragmentOnly(_base, input);
        }

        if (_base.CannotBeABase)
        {
            return null;
        }

        var schemeType = SchemeTypes.Of(_base.Scheme);
        return schemeType.IsFile()
            ? ParseFile(input, schemeType, _base)
            : ParseRelative(input, schemeType, _base);
    }

    private bool ParseScheme(Input input, out Input remaining)
    {
        remaining = input;
        if (input.First is not char first || !char.IsAsciiLetter(first))
        {
            return false;
        }

        var cursor = input;
        while (cursor.Next() is char c)
        {
            switch (c)
            {
                case >= 'a' and <= 'z':
                case >= '0' and <= '9':
                case '+':
                case '-':
                case '.':
                    _ser.Append(c);
                    break;
                case >= 'A' and <= 'Z':
                    _ser.Append((char)(c + 32));
                    break;
                case ':':
                    remaining = cursor;
                    return true;
                default:
                    _ser.Clear();
                    return false;
            }
        }

        if (_context == ParseContext.Setter)
        {
            remaining = cursor;
            return true;
        }

        _ser.Clear();
        return false;
    }

    private UrlRecord? ParseWithScheme(Input input)
    {
        var schemeEnd = Len;
        var scheme = _ser.ToString();
        var schemeType = SchemeTypes.Of(scheme);
        _ser.Append(':');
        switch (schemeType)
        {
            case SchemeType.File:
            {
                var baseFileUrl = _base is not null && _base.Scheme == "file" ? _base : null;
                _ser.Clear();
                return ParseFile(input, schemeType, baseFileUrl);
            }

            case SchemeType.SpecialNotFile:
            {
                var (slashes, remaining) = input.CountMatching(c => c is '/' or '\\');
                if (_base is not null && slashes < 2 && _base.Scheme == scheme)
                {
                    _ser.Clear();
                    return ParseRelative(input, schemeType, _base);
                }

                return AfterDoubleSlash(remaining, schemeType, schemeEnd);
            }

            default:
                return ParseNonSpecial(input, schemeType, schemeEnd);
        }
    }

    private UrlRecord? ParseNonSpecial(Input input, SchemeType schemeType, int schemeEnd)
    {
        if (input.SplitPrefix("//") is Input authority)
        {
            return AfterDoubleSlash(authority, schemeType, schemeEnd);
        }

        var pathStart = Len;
        Input remaining;
        if (input.SplitPrefix('/') is Input rooted)
        {
            _ser.Append('/');
            var hasHost = false;
            remaining = ParsePath(schemeType, ref hasHost, pathStart, rooted);
        }
        else
        {
            remaining = ParseCannotBeABasePath(input);
        }

        return WithQueryAndFragment(
            schemeType, schemeEnd, pathStart, pathStart, pathStart, default, null, pathStart, remaining);
    }

    private UrlRecord? ParseFile(Input input, SchemeType schemeType, UrlRecord? baseFileUrl)
    {
        var (firstChar, inputAfterFirstChar) = input.SplitFirst();
        if (firstChar is '/' or '\\')
        {
            var (nextChar, inputAfterNextChar) = inputAfterFirstChar.SplitFirst();
            if (nextChar is '/' or '\\')
            {
                // file host state
                _ser.Append("file://");
                const int SchemeEnd = 4;
                const int HostStart = 7;
                if (!ParseFileHost(inputAfterNextChar, out var sawHost, out var host, out var remaining))
                {
                    return null;
                }

                var hostEnd = Len;
                var hasHost = host.Kind != HostKind.None;
                if (sawHost)
                {
                    remaining = ParsePathStart(SchemeType.File, ref hasHost, remaining);
                }
                else
                {
                    var pathStartLocal = Len;
                    _ser.Append('/');
                    remaining = ParsePath(SchemeType.File, ref hasHost, pathStartLocal, remaining);
                }

                // A Windows drive letter in the path wins over a host on file: URLs.
                if (!hasHost)
                {
                    _ser.Remove(HostStart, hostEnd - HostStart);
                    hostEnd = HostStart;
                    host = default;
                }

                var (queryStart, fragmentStart) = ParseQueryAndFragment(schemeType, SchemeEnd, remaining);
                return Build(SchemeEnd, HostStart, HostStart, hostEnd, host, null, hostEnd, queryStart, fragmentStart);
            }
            else
            {
                _ser.Append("file://");
                const int SchemeEnd = 4;
                const int HostStart = 7;
                var hostEnd = HostStart;
                ParsedHost host = default;
                if (!StartsWithWindowsDriveLetterSegment(inputAfterFirstChar) && baseFileUrl is not null)
                {
                    var firstSegment = FirstPathSegment(baseFileUrl);
                    if (firstSegment is not null && IsNormalizedWindowsDriveLetter(firstSegment))
                    {
                        _ser.Append('/').Append(firstSegment);
                    }
                    else if (baseFileUrl.HostStr is string hostStr)
                    {
                        _ser.Append(hostStr);
                        hostEnd = Len;
                        host = baseFileUrl.HostSnapshot();
                    }
                }

                var parsePathInput = firstChar is '/' or '\\' or '?' or '#' ? input : inputAfterFirstChar;
                var noHost = false;
                var remaining = ParsePath(SchemeType.File, ref noHost, hostEnd, parsePathInput);
                var (queryStart, fragmentStart) = ParseQueryAndFragment(schemeType, SchemeEnd, remaining);
                return Build(SchemeEnd, HostStart, HostStart, hostEnd, host, null, hostEnd, queryStart, fragmentStart);
            }
        }

        if (baseFileUrl is not null)
        {
            switch (firstChar)
            {
                case null:
                {
                    var beforeFragment = baseFileUrl.FragmentStart is int f
                        ? baseFileUrl.Serialization[..f]
                        : baseFileUrl.Serialization;
                    _ser.Append(beforeFragment);
                    var copy = baseFileUrl.CloneRecord();
                    copy.Serialization = _ser.ToString();
                    copy.FragmentStart = null;
                    return copy;
                }

                case '?':
                {
                    var beforeQuery = BeforeQuery(baseFileUrl);
                    _ser.Append(beforeQuery);
                    var (queryStart, fragmentStart) =
                        ParseQueryAndFragment(schemeType, baseFileUrl.SchemeEnd, input);
                    var copy = baseFileUrl.CloneRecord();
                    copy.Serialization = _ser.ToString();
                    copy.QueryStart = queryStart;
                    copy.FragmentStart = fragmentStart;
                    return copy;
                }

                case '#':
                    return FragmentOnly(baseFileUrl, input);

                default:
                {
                    if (!StartsWithWindowsDriveLetterSegment(input))
                    {
                        _ser.Append(BeforeQuery(baseFileUrl));
                        ShortenPath(SchemeType.File, baseFileUrl.PathStart);
                        var hasHost = true;
                        var remaining = ParsePath(SchemeType.File, ref hasHost, baseFileUrl.PathStart, input);
                        return WithQueryAndFragment(
                            SchemeType.File,
                            baseFileUrl.SchemeEnd,
                            baseFileUrl.UsernameEnd,
                            baseFileUrl.HostStart,
                            baseFileUrl.HostEnd,
                            baseFileUrl.HostSnapshot(),
                            baseFileUrl.Port,
                            baseFileUrl.PathStart,
                            remaining);
                    }

                    _ser.Append("file:///");
                    const int SchemeEnd = 4;
                    const int PathStart = 7;
                    var noHost = false;
                    var rest = ParsePath(SchemeType.File, ref noHost, PathStart, input);
                    var (queryStart, fragmentStart) = ParseQueryAndFragment(SchemeType.File, SchemeEnd, rest);
                    return Build(SchemeEnd, PathStart, PathStart, PathStart, default, null, PathStart, queryStart, fragmentStart);
                }
            }
        }

        {
            _ser.Append("file:///");
            const int SchemeEnd = 4;
            const int PathStart = 7;
            var noHost = false;
            var rest = ParsePath(SchemeType.File, ref noHost, PathStart, input);
            var (queryStart, fragmentStart) = ParseQueryAndFragment(SchemeType.File, SchemeEnd, rest);
            return Build(SchemeEnd, PathStart, PathStart, PathStart, default, null, PathStart, queryStart, fragmentStart);
        }
    }

    private UrlRecord? ParseRelative(Input input, SchemeType schemeType, UrlRecord baseUrl)
    {
        var (firstChar, inputAfterFirstChar) = input.SplitFirst();
        switch (firstChar)
        {
            case null:
            {
                var beforeFragment = baseUrl.FragmentStart is int f
                    ? baseUrl.Serialization[..f]
                    : baseUrl.Serialization;
                _ser.Append(beforeFragment);
                var copy = baseUrl.CloneRecord();
                copy.Serialization = _ser.ToString();
                copy.FragmentStart = null;
                return copy;
            }

            case '?':
            {
                _ser.Append(BeforeQuery(baseUrl));
                var (queryStart, fragmentStart) = ParseQueryAndFragment(schemeType, baseUrl.SchemeEnd, input);
                var copy = baseUrl.CloneRecord();
                copy.Serialization = _ser.ToString();
                copy.QueryStart = queryStart;
                copy.FragmentStart = fragmentStart;
                return copy;
            }

            case '#':
                return FragmentOnly(baseUrl, input);

            case '/':
            case '\\':
            {
                var (slashes, remaining) = input.CountMatching(c => c is '/' or '\\');
                if (slashes >= 2)
                {
                    var schemeEnd = baseUrl.SchemeEnd;
                    _ser.Append(baseUrl.Serialization, 0, schemeEnd + 1);
                    if (input.SplitPrefix("//") is Input afterPrefix)
                    {
                        return AfterDoubleSlash(afterPrefix, schemeType, schemeEnd);
                    }

                    return AfterDoubleSlash(remaining, schemeType, schemeEnd);
                }

                var pathStart = baseUrl.PathStart;
                _ser.Append(baseUrl.Serialization, 0, pathStart);
                _ser.Append('/');
                var hasHost = true;
                var rest = ParsePath(schemeType, ref hasHost, pathStart, inputAfterFirstChar);
                return WithQueryAndFragment(
                    schemeType,
                    baseUrl.SchemeEnd,
                    baseUrl.UsernameEnd,
                    baseUrl.HostStart,
                    baseUrl.HostEnd,
                    baseUrl.HostSnapshot(),
                    baseUrl.Port,
                    baseUrl.PathStart,
                    rest);
            }

            default:
            {
                _ser.Append(BeforeQuery(baseUrl));
                PopPath(schemeType, baseUrl.PathStart);
                if (Len == baseUrl.PathStart
                    && (SchemeTypes.Of(baseUrl.Scheme).IsSpecial() || !input.IsEmpty))
                {
                    _ser.Append('/');
                }

                var hasHost = true;
                var (leading, afterLeading) = input.SplitFirst();
                var rest = leading == '/'
                    ? ParsePath(schemeType, ref hasHost, baseUrl.PathStart, afterLeading)
                    : ParsePath(schemeType, ref hasHost, baseUrl.PathStart, input);
                return WithQueryAndFragment(
                    schemeType,
                    baseUrl.SchemeEnd,
                    baseUrl.UsernameEnd,
                    baseUrl.HostStart,
                    baseUrl.HostEnd,
                    baseUrl.HostSnapshot(),
                    baseUrl.Port,
                    baseUrl.PathStart,
                    rest);
            }
        }
    }

    private UrlRecord? AfterDoubleSlash(Input input, SchemeType schemeType, int schemeEnd)
    {
        _ser.Append("//");
        var beforeAuthority = Len;
        if (!ParseUserinfo(input, schemeType, out var usernameEnd, out var remaining))
        {
            return null;
        }

        var hasAuthority = beforeAuthority != Len;
        var hostStart = Len;
        if (!ParseHostAndPort(remaining, schemeEnd, schemeType, out var hostEnd, out var host, out var port, out remaining))
        {
            return null;
        }

        if ((host.Kind == HostKind.None || host.IsEmptyDomain) && hasAuthority)
        {
            return null;
        }

        var pathStart = Len;
        var hasHost = true;
        remaining = ParsePathStart(schemeType, ref hasHost, remaining);
        return WithQueryAndFragment(
            schemeType, schemeEnd, usernameEnd, hostStart, hostEnd, host, port, pathStart, remaining);
    }

    private bool ParseUserinfo(Input input, SchemeType schemeType, out int usernameEnd, out Input remaining)
    {
        usernameEnd = 0;
        remaining = input;

        int? lastAtCount = null;
        var lastAtRemaining = input;
        var scan = input;
        var charCount = 0;
        while (scan.Next() is char c)
        {
            if (c == '@')
            {
                lastAtCount = charCount;
                lastAtRemaining = scan;
            }
            else if (c is '/' or '?' or '#' || (c == '\\' && schemeType.IsSpecial()))
            {
                break;
            }

            charCount++;
        }

        if (lastAtCount is null)
        {
            usernameEnd = Len;
            remaining = input;
            return true;
        }

        if (lastAtCount == 0)
        {
            if (lastAtRemaining.First is char next
                && (next is '/' or '?' or '#' || (schemeType.IsSpecial() && next == '\\')))
            {
                return false;   // empty host
            }

            usernameEnd = Len;
            remaining = lastAtRemaining;
            return true;
        }

        var count = lastAtCount.Value;
        remaining = lastAtRemaining;

        int? explicitUsernameEnd = null;
        var hasPassword = false;
        var hasUsername = false;
        var cursor = input;
        while (count > 0)
        {
            var c = cursor.Next()!.Value;
            count--;
            if (c == ':' && explicitUsernameEnd is null)
            {
                explicitUsernameEnd = Len;
                if (count > 0)
                {
                    _ser.Append(':');
                    hasPassword = true;
                }
            }
            else
            {
                if (!hasPassword)
                {
                    hasUsername = true;
                }

                if (char.IsHighSurrogate(c) && count > 0 && cursor.First is char low && char.IsLowSurrogate(low))
                {
                    cursor.Next();
                    count--;
                    PercentEncoding.AppendEncodedPair(_ser, c, low);
                }
                else
                {
                    PercentEncoding.AppendEncoded(_ser, c, PercentEncoding.Userinfo);
                }
            }
        }

        usernameEnd = explicitUsernameEnd ?? Len;
        if (hasUsername || hasPassword)
        {
            _ser.Append('@');
        }

        return true;
    }

    private bool ParseHostAndPort(
        Input input,
        int schemeEnd,
        SchemeType schemeType,
        out int hostEnd,
        out ParsedHost host,
        out int? port,
        out Input remaining)
    {
        hostEnd = 0;
        port = null;
        if (!ParseHost(input, schemeType, out host, out remaining))
        {
            return false;
        }

        _ser.Append(host.ToString());
        hostEnd = Len;
        if (host.IsEmptyDomain && (remaining.StartsWith(':') || schemeType.IsSpecial()))
        {
            return false;
        }

        if (remaining.SplitPrefix(':') is Input afterColon)
        {
            var defaultPort = SchemeTypes.DefaultPort(Slice(0, schemeEnd));
            if (!ParsePort(afterColon, defaultPort, _context, out port, out remaining))
            {
                return false;
            }

            if (port is int p)
            {
                _ser.Append(':').Append(p.ToString(CultureInfo.InvariantCulture));
            }
        }

        return true;
    }

    private static bool ParseHost(Input input, SchemeType schemeType, out ParsedHost host, out Input remaining)
    {
        if (schemeType.IsFile())
        {
            return GetFileHost(input, out host, out remaining);
        }

        host = default;
        var text = input.S;
        var insideBrackets = false;
        var i = input.Pos;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == ':' && !insideBrackets)
            {
                break;
            }

            if (c == '\\' && schemeType.IsSpecial())
            {
                break;
            }

            if (c is '/' or '?' or '#')
            {
                break;
            }

            if (c == '[')
            {
                insideBrackets = true;
            }
            else if (c == ']')
            {
                insideBrackets = false;
            }
        }

        var hostStr = text[input.Pos..i];
        remaining = new Input(text, i);
        if (schemeType == SchemeType.SpecialNotFile && hostStr.Length == 0)
        {
            return false;
        }

        if (schemeType.IsSpecial())
        {
            return HostParser.TryParse(hostStr, out host);
        }

        return HostParser.TryParseOpaque(hostStr, out host);
    }

    private static bool GetFileHost(Input input, out ParsedHost host, out Input remaining)
    {
        host = default;
        FileHost(input, out var hostStr, out remaining);
        if (!HostParser.TryParse(hostStr, out var parsed))
        {
            return false;
        }

        host = parsed.Kind == HostKind.Domain && parsed.Domain == "localhost"
            ? ParsedHost.FromDomain(string.Empty)
            : parsed;
        return true;
    }

    private bool ParseFileHost(Input input, out bool sawHost, out ParsedHost host, out Input remaining)
    {
        host = default;
        sawHost = false;
        FileHost(input, out var hostStr, out remaining);
        if (hostStr.Length == 0)
        {
            return true;
        }

        if (!HostParser.TryParse(hostStr, out var parsed))
        {
            return false;
        }

        if (parsed.Kind == HostKind.Domain && parsed.Domain == "localhost")
        {
            return true;
        }

        _ser.Append(parsed.ToString());
        sawHost = true;
        host = parsed;
        return true;
    }

    private static void FileHost(Input input, out string hostStr, out Input remaining)
    {
        var text = input.S;
        var i = input.Pos;
        while (i < text.Length && text[i] is not ('/' or '\\' or '?' or '#'))
        {
            i++;
        }

        hostStr = text[input.Pos..i];
        if (IsWindowsDriveLetter(hostStr))
        {
            hostStr = string.Empty;
            remaining = input;
            return;
        }

        remaining = new Input(text, i);
    }

    private static bool ParsePort(
        Input input, int? defaultPort, ParseContext context, out int? port, out Input remaining)
    {
        port = null;
        remaining = input;
        var value = 0;
        var hasAnyDigit = false;
        while (remaining.First is char c)
        {
            if (char.IsAsciiDigit(c))
            {
                value = (value * 10) + (c - '0');
                if (value > ushort.MaxValue)
                {
                    return false;
                }

                hasAnyDigit = true;
            }
            else if (context == ParseContext.UrlParser && c is not ('/' or '\\' or '?' or '#'))
            {
                return false;
            }
            else
            {
                break;
            }

            remaining.Next();
        }

        if (!hasAnyDigit && context == ParseContext.Setter && !remaining.IsEmpty)
        {
            return false;
        }

        port = !hasAnyDigit || value == defaultPort ? null : value;
        return true;
    }

    private Input ParsePathStart(SchemeType schemeType, ref bool hasHost, Input input)
    {
        var pathStart = Len;
        var (maybeC, remaining) = input.SplitFirst();
        if (schemeType.IsSpecial())
        {
            if (!EndsWith('/'))
            {
                _ser.Append('/');
                if (maybeC is '/' or '\\')
                {
                    return ParsePath(schemeType, ref hasHost, pathStart, remaining);
                }
            }

            return ParsePath(schemeType, ref hasHost, pathStart, input);
        }

        if (maybeC is '?' or '#')
        {
            return input;
        }

        if (maybeC is not null && maybeC != '/')
        {
            _ser.Append('/');
        }

        return ParsePath(schemeType, ref hasHost, pathStart, input);
    }

    private Input ParsePath(SchemeType schemeType, ref bool hasHost, int pathStart, Input input)
    {
        while (true)
        {
            var segmentStart = Len;
            var endsWithSlash = false;
            while (true)
            {
                var before = input;
                if (input.Next() is not char c)
                {
                    break;
                }

                if (c == '/' && _context != ParseContext.PathSegmentSetter)
                {
                    _ser.Append('/');
                    endsWithSlash = true;
                    break;
                }

                if (c == '\\' && _context != ParseContext.PathSegmentSetter && schemeType.IsSpecial())
                {
                    _ser.Append('/');
                    endsWithSlash = true;
                    break;
                }

                if ((c is '?' or '#') && _context == ParseContext.UrlParser)
                {
                    input = before;
                    break;
                }

                // "file:///c:foo" gains the separator the drive letter implies.
                if (schemeType.IsFile() && Len > pathStart
                    && IsNormalizedWindowsDriveLetter(Slice(pathStart + 1, Len)))
                {
                    _ser.Append('/');
                    segmentStart += 1;
                }

                var set = _context == ParseContext.PathSegmentSetter
                    ? (schemeType.IsSpecial() ? PercentEncoding.SpecialPathSegment : PercentEncoding.PathSegment)
                    : PercentEncoding.Path;
                if (char.IsHighSurrogate(c) && input.First is char low && char.IsLowSurrogate(low))
                {
                    input.Next();
                    PercentEncoding.AppendEncodedPair(_ser, c, low);
                }
                else
                {
                    PercentEncoding.AppendEncoded(_ser, c, set);
                }
            }

            var segmentBeforeSlash = endsWithSlash ? Slice(segmentStart, Len - 1) : Slice(segmentStart, Len);
            switch (segmentBeforeSlash)
            {
                case ".." or "%2e%2e" or "%2e%2E" or "%2E%2e" or "%2E%2E"
                    or "%2e." or "%2E." or ".%2e" or ".%2E":
                    Truncate(segmentStart);
                    if (EndsWith('/') && LastSlashCanBeRemoved(pathStart))
                    {
                        Truncate(Len - 1);
                    }

                    ShortenPath(schemeType, pathStart);
                    if (endsWithSlash && !EndsWith('/'))
                    {
                        _ser.Append('/');
                    }

                    break;

                case "." or "%2e" or "%2E":
                    Truncate(segmentStart);
                    if (!EndsWith('/'))
                    {
                        _ser.Append('/');
                    }

                    break;

                default:
                    if (schemeType.IsFile() && segmentStart == pathStart + 1
                        && IsWindowsDriveLetter(segmentBeforeSlash))
                    {
                        var drive = segmentBeforeSlash[0];
                        Truncate(segmentStart);
                        _ser.Append(drive).Append(':');
                        if (endsWithSlash)
                        {
                            _ser.Append('/');
                        }

                        hasHost = false;
                    }

                    break;
            }

            if (!endsWithSlash)
            {
                break;
            }
        }

        if (schemeType.IsFile())
        {
            // A file URL's path never keeps more than one leading slash.
            var path = Slice(pathStart, Len);
            Truncate(pathStart);
            _ser.Append('/').Append(path.TrimStart('/'));
        }

        return input;
    }

    private bool LastSlashCanBeRemoved(int pathStart)
    {
        var beforeSegment = Len - 1;
        var slash = -1;
        for (var i = beforeSegment - 1; i >= 0; i--)
        {
            if (_ser[i] == '/')
            {
                slash = i;
                break;
            }
        }

        if (slash < 0)
        {
            return false;
        }

        return slash >= pathStart && !PathStartsWithWindowsDriveLetter(Slice(slash, Len));
    }

    private void ShortenPath(SchemeType schemeType, int pathStart)
    {
        if (Len == pathStart)
        {
            return;
        }

        if (schemeType.IsFile() && IsNormalizedWindowsDriveLetter(Slice(pathStart, Len)))
        {
            return;
        }

        PopPath(schemeType, pathStart);
    }

    private void PopPath(SchemeType schemeType, int pathStart)
    {
        if (Len <= pathStart)
        {
            return;
        }

        var slash = -1;
        for (var i = Len - 1; i >= pathStart; i--)
        {
            if (_ser[i] == '/')
            {
                slash = i;
                break;
            }
        }

        if (slash < 0)
        {
            return;
        }

        var segmentStart = slash + 1;
        if (!(schemeType.IsFile() && IsNormalizedWindowsDriveLetter(Slice(segmentStart, Len))))
        {
            Truncate(segmentStart);
        }
    }

    private Input ParseCannotBeABasePath(Input input)
    {
        while (true)
        {
            var before = input;
            if (input.Next() is not char c)
            {
                return input;
            }

            if ((c is '?' or '#') && _context == ParseContext.UrlParser)
            {
                return before;
            }

            if (char.IsHighSurrogate(c) && input.First is char low && char.IsLowSurrogate(low))
            {
                input.Next();
                PercentEncoding.AppendEncodedPair(_ser, c, low);
            }
            else
            {
                PercentEncoding.AppendEncoded(_ser, c, AsciiSet.Controls);
            }
        }
    }

    private UrlRecord? WithQueryAndFragment(
        SchemeType schemeType,
        int schemeEnd,
        int usernameEnd,
        int hostStart,
        int hostEnd,
        ParsedHost host,
        int? port,
        int pathStart,
        Input remaining)
    {
        // Keep "web+demo:/.//not-a-host/" from re-serializing as an authority.
        if (pathStart == schemeEnd + 1)
        {
            if (pathStart + 1 < Len && _ser[pathStart] == '/' && _ser[pathStart + 1] == '/')
            {
                _ser.Insert(pathStart, "/.");
                pathStart += 2;
            }
        }
        else if (pathStart == schemeEnd + 3 && Slice(schemeEnd, pathStart) == ":/.")
        {
            if (!(pathStart + 1 < Len && _ser[pathStart + 1] == '/'))
            {
                _ser.Remove(schemeEnd, pathStart - schemeEnd);
                _ser.Insert(schemeEnd, ':');
                pathStart -= 2;
            }
        }

        var (queryStart, fragmentStart) = ParseQueryAndFragment(schemeType, schemeEnd, remaining);
        return Build(schemeEnd, usernameEnd, hostStart, hostEnd, host, port, pathStart, queryStart, fragmentStart);
    }

    private (int? QueryStart, int? FragmentStart) ParseQueryAndFragment(
        SchemeType schemeType, int schemeEnd, Input input)
    {
        int? queryStart = null;
        var next = input.Next();
        switch (next)
        {
            case '#':
                break;
            case '?':
            {
                queryStart = Len;
                _ser.Append('?');
                var rest = ParseQuery(schemeType, input);
                if (rest is null)
                {
                    return (queryStart, null);
                }

                input = rest.Value;
                break;
            }

            case null:
                return (null, null);
            default:
                // Unreachable: the callers only stop at '?' or '#'.
                return (null, null);
        }

        var fragmentStart = Len;
        _ser.Append('#');
        ParseFragment(input);
        return (queryStart, fragmentStart);
    }

    private Input? ParseQuery(SchemeType schemeType, Input input)
    {
        var set = schemeType.IsSpecial() ? PercentEncoding.SpecialQuery : PercentEncoding.Query;
        while (true)
        {
            if (input.Next() is not char c)
            {
                return null;
            }

            if (c == '#' && _context == ParseContext.UrlParser)
            {
                return input;
            }

            if (char.IsHighSurrogate(c) && input.First is char low && char.IsLowSurrogate(low))
            {
                input.Next();
                PercentEncoding.AppendEncodedPair(_ser, c, low);
            }
            else
            {
                PercentEncoding.AppendEncoded(_ser, c, set);
            }
        }
    }

    private void ParseFragment(Input input)
    {
        while (input.Next() is char c)
        {
            if (char.IsHighSurrogate(c) && input.First is char low && char.IsLowSurrogate(low))
            {
                input.Next();
                PercentEncoding.AppendEncodedPair(_ser, c, low);
            }
            else
            {
                PercentEncoding.AppendEncoded(_ser, c, PercentEncoding.Fragment);
            }
        }
    }

    private UrlRecord FragmentOnly(UrlRecord baseUrl, Input input)
    {
        var beforeFragment = baseUrl.FragmentStart is int i
            ? baseUrl.Serialization[..i]
            : baseUrl.Serialization;
        _ser.Append(beforeFragment).Append('#');
        input.Next();   // the '#'
        ParseFragment(input);
        var copy = baseUrl.CloneRecord();
        copy.Serialization = _ser.ToString();
        copy.FragmentStart = beforeFragment.Length;
        return copy;
    }

    private UrlRecord Build(
        int schemeEnd,
        int usernameEnd,
        int hostStart,
        int hostEnd,
        ParsedHost host,
        int? port,
        int pathStart,
        int? queryStart,
        int? fragmentStart)
    {
        var record = new UrlRecord
        {
            Serialization = _ser.ToString(),
            SchemeEnd = schemeEnd,
            UsernameEnd = usernameEnd,
            HostStart = hostStart,
            HostEnd = hostEnd,
            HostKind = host.IsEmptyDomain ? HostKind.None : host.Kind,
            HostV4 = host.V4,
            HostV6 = host.V6,
            Port = port,
            PathStart = pathStart,
            QueryStart = queryStart,
            FragmentStart = fragmentStart,
        };
        return record;
    }

    private static string BeforeQuery(UrlRecord url)
    {
        var cut = url.QueryStart ?? url.FragmentStart;
        return cut is int i ? url.Serialization[..i] : url.Serialization;
    }

    private static string? FirstPathSegment(UrlRecord url)
    {
        var path = url.Path;
        if (!path.StartsWith('/'))
        {
            return null;
        }

        var rest = path[1..];
        var slash = rest.IndexOf('/');
        return slash < 0 ? rest : rest[..slash];
    }

    // ------------------------------------------------------------ shared predicates

    internal static bool IsNormalizedWindowsDriveLetter(string segment) =>
        IsWindowsDriveLetter(segment) && segment[1] == ':';

    internal static bool IsWindowsDriveLetter(string segment) =>
        segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] is ':' or '|';

    private static bool PathStartsWithWindowsDriveLetter(string s) =>
        s.Length > 0 && s[0] is '/' or '\\' or '?' or '#' && StartsWithWindowsDriveLetter(s[1..]);

    private static bool StartsWithWindowsDriveLetter(string s) =>
        s.Length >= 2 && char.IsAsciiLetter(s[0]) && s[1] is ':' or '|'
        && (s.Length == 2 || s[2] is '/' or '\\' or '?' or '#');

    private static bool StartsWithWindowsDriveLetterSegment(Input input)
    {
        var cursor = input;
        var a = cursor.Next();
        var b = cursor.Next();
        var c = cursor.Next();
        if (a is not char ca || !char.IsAsciiLetter(ca) || b is not (':' or '|'))
        {
            return false;
        }

        return c is null || c is '/' or '\\' or '?' or '#';
    }

    // ------------------------------------------------------------ setter entry points

    internal static void SetPath(UrlRecord url, string path)
    {
        var afterPath = TakeAfterPath(url);
        var oldAfterPathPos = url.Serialization.Length;
        var cannotBeABase = url.CannotBeABase;
        var schemeType = url.Type;

        var sb = new StringBuilder(url.Serialization, 0, url.PathStart, url.Serialization.Length);
        var parser = new UrlParser(sb, null, ParseContext.Setter);
        if (cannotBeABase)
        {
            if (path.StartsWith('/'))
            {
                sb.Append("%2F");
                path = path[1..];
            }

            parser.ParseCannotBeABasePath(Input.NoTrim(path));
        }
        else
        {
            var hasHost = true;
            parser.ParsePathStart(schemeType, ref hasHost, Input.NoTrim(path));
        }

        url.Serialization = sb.ToString();

        var newAfterPathPos = url.Serialization.Length;
        var delta = newAfterPathPos - oldAfterPathPos;
        if (url.QueryStart is int q)
        {
            url.QueryStart = q + delta;
        }

        if (url.FragmentStart is int f)
        {
            url.FragmentStart = f + delta;
        }

        url.Serialization += afterPath;
    }

    internal static void SetQuery(UrlRecord url, string? query)
    {
        var fragment = TakeFragment(url);
        if (url.QueryStart is int start)
        {
            url.Serialization = url.Serialization[..start];
            url.QueryStart = null;
        }

        if (query is not null)
        {
            url.QueryStart = url.Serialization.Length;
            var sb = new StringBuilder(url.Serialization);
            sb.Append('?');
            var parser = new UrlParser(sb, null, ParseContext.Setter);
            parser.ParseQuery(url.Type, Input.TrimTabAndNewlines(query));
            url.Serialization = sb.ToString();
        }
        else
        {
            url.QueryStart = null;
            if (fragment is null)
            {
                url.StripTrailingSpacesFromOpaquePath();
            }
        }

        if (fragment is not null)
        {
            url.FragmentStart = url.Serialization.Length;
            url.Serialization = url.Serialization + "#" + fragment;
        }
    }

    internal static void SetFragment(UrlRecord url, string? fragment)
    {
        if (url.FragmentStart is int start)
        {
            url.Serialization = url.Serialization[..start];
            url.FragmentStart = null;
        }

        if (fragment is not null)
        {
            url.FragmentStart = url.Serialization.Length;
            var sb = new StringBuilder(url.Serialization);
            sb.Append('#');
            var parser = new UrlParser(sb, null, ParseContext.Setter);
            parser.ParseFragment(Input.NoTrim(fragment));
            url.Serialization = sb.ToString();
        }
        else
        {
            url.FragmentStart = null;
            url.StripTrailingSpacesFromOpaquePath();
        }
    }

    private static string TakeAfterPath(UrlRecord url)
    {
        var cut = url.QueryStart ?? url.FragmentStart;
        if (cut is not int i)
        {
            return string.Empty;
        }

        var afterPath = url.Serialization[i..];
        url.Serialization = url.Serialization[..i];
        return afterPath;
    }

    private static string? TakeFragment(UrlRecord url)
    {
        if (url.FragmentStart is not int start)
        {
            return null;
        }

        var fragment = url.Serialization[(start + 1)..];
        url.Serialization = url.Serialization[..start];
        url.FragmentStart = null;
        return fragment;
    }
}
