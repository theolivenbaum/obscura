using System.Globalization;
using System.Text;

namespace Obscura.Js.Url;

/// <summary>Whether a scheme is special, and whether it is <c>file</c>.</summary>
public enum SchemeType
{
    /// <summary>The <c>file</c> scheme.</summary>
    File,

    /// <summary><c>http</c>, <c>https</c>, <c>ws</c>, <c>wss</c>, <c>ftp</c>.</summary>
    SpecialNotFile,

    /// <summary>Everything else.</summary>
    NotSpecial,
}

/// <summary>Scheme classification helpers.</summary>
public static class SchemeTypes
{
    /// <summary>True for the five special schemes plus <c>file</c>.</summary>
    public static bool IsSpecial(this SchemeType type) => type != SchemeType.NotSpecial;

    /// <summary>True only for <c>file</c>.</summary>
    public static bool IsFile(this SchemeType type) => type == SchemeType.File;

    /// <summary>Classifies a (lowercased) scheme.</summary>
    public static SchemeType Of(string scheme) => scheme switch
    {
        "http" or "https" or "ws" or "wss" or "ftp" => SchemeType.SpecialNotFile,
        "file" => SchemeType.File,
        _ => SchemeType.NotSpecial,
    };

    /// <summary><see href="https://url.spec.whatwg.org/#default-port"/>.</summary>
    public static int? DefaultPort(string scheme) => scheme switch
    {
        "http" or "ws" => 80,
        "https" or "wss" => 443,
        "ftp" => 21,
        _ => null,
    };
}

/// <summary>
/// A parsed URL, stored the way the Rust <c>url</c> crate stores one: a single serialized
/// string plus offsets into it. Getters are slices, so the component JSON the ops hand to
/// <c>bootstrap.js</c> is produced without re-parsing.
/// </summary>
public sealed class UrlRecord
{
    internal string Serialization = string.Empty;
    internal int SchemeEnd;
    internal int UsernameEnd;
    internal int HostStart;
    internal int HostEnd;
    internal HostKind HostKind;
    internal uint HostV4;
    internal ushort[]? HostV6;
    internal int? Port;
    internal int PathStart;
    internal int? QueryStart;
    internal int? FragmentStart;

    internal UrlRecord()
    {
    }

    /// <summary>Parses an absolute URL. Returns null when the input is not a valid URL.</summary>
    public static UrlRecord? Parse(string input) => UrlParser.Parse(input, null);

    /// <summary>Resolves <paramref name="input"/> against this URL, per the spec's "join".</summary>
    public UrlRecord? Join(string input) => UrlParser.Parse(input, this);

    /// <summary>The full serialization; this is the <c>href</c> getter.</summary>
    public string Href => Serialization;

    /// <inheritdoc/>
    public override string ToString() => Serialization;

    /// <summary>The scheme, lowercased, without the trailing colon.</summary>
    public string Scheme => Serialization[..SchemeEnd];

    /// <summary>The scheme classification.</summary>
    public SchemeType Type => SchemeTypes.Of(Scheme);

    /// <summary>True when the URL has an opaque path (the crate's "cannot be a base").</summary>
    public bool CannotBeABase =>
        SchemeEnd + 1 >= Serialization.Length || Serialization[SchemeEnd + 1] != '/';

    /// <summary>True when the serialization carries an authority (<c>://</c>).</summary>
    public bool HasAuthority =>
        Serialization.AsSpan(SchemeEnd).StartsWith("://", StringComparison.Ordinal);

    /// <summary>True when the URL has a non-empty host.</summary>
    public bool HasHost => HostKind != HostKind.None;

    /// <summary>The serialized host, or null when there is none.</summary>
    public string? HostStr => HasHost ? Serialization[HostStart..HostEnd] : null;

    /// <summary>The percent-encoded username (empty when absent).</summary>
    public string Username =>
        HasAuthority && UsernameEnd > SchemeEnd + 3 ? Serialization[(SchemeEnd + 3)..UsernameEnd] : string.Empty;

    /// <summary>The percent-encoded password, or null when absent.</summary>
    public string? Password =>
        HasAuthority && UsernameEnd != Serialization.Length && Serialization[UsernameEnd] == ':'
            ? Serialization[(UsernameEnd + 1)..(HostStart - 1)]
            : null;

    /// <summary>The explicit port, or null when absent or equal to the scheme default.</summary>
    public int? PortNumber => Port;

    /// <summary>The port, falling back to the scheme's default.</summary>
    public int? PortOrKnownDefault => Port ?? SchemeTypes.DefaultPort(Scheme);

    /// <summary>The path, including its leading slash (or the opaque path).</summary>
    public string Path
    {
        get
        {
            var end = QueryStart ?? FragmentStart ?? Serialization.Length;
            return Serialization[PathStart..end];
        }
    }

    /// <summary>The query without its leading <c>?</c>, or null when absent.</summary>
    public string? Query
    {
        get
        {
            if (QueryStart is not int start)
            {
                return null;
            }

            var end = FragmentStart ?? Serialization.Length;
            return Serialization[(start + 1)..end];
        }
    }

    /// <summary>The fragment without its leading <c>#</c>, or null when absent.</summary>
    public string? Fragment => FragmentStart is int start ? Serialization[(start + 1)..] : null;

    /// <summary>
    /// <see href="https://html.spec.whatwg.org/multipage/#ascii-serialisation-of-an-origin"/>.
    /// Opaque origins - every non-special scheme, and <c>file</c> - serialize as "null".
    /// </summary>
    public string AsciiOrigin => AsciiOriginCore(0);

    private string AsciiOriginCore(int depth)
    {
        var scheme = Scheme;
        if (scheme == "blob")
        {
            // A blob URL's origin is the origin of the URL in its path. Bound the recursion:
            // "blob:blob:blob:..." is otherwise unbounded work for an attacker-controlled string.
            if (depth >= 4)
            {
                return "null";
            }

            var inner = Parse(Path);
            return inner is null ? "null" : inner.AsciiOriginCore(depth + 1);
        }

        if (scheme is not ("ftp" or "http" or "https" or "ws" or "wss"))
        {
            return "null";
        }

        var host = HostStr;
        if (host is null)
        {
            return "null";
        }

        var port = PortOrKnownDefault;
        if (port is null || SchemeTypes.DefaultPort(scheme) == port)
        {
            return scheme + "://" + host;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{scheme}://{host}:{port.Value}");
    }

    internal UrlRecord CloneRecord() => (UrlRecord)MemberwiseClone();

    /// <summary>Rebuilds the parsed host from the stored offsets and discriminant.</summary>
    internal ParsedHost HostSnapshot() => HostKind switch
    {
        HostKind.Domain => ParsedHost.FromDomain(Serialization[HostStart..HostEnd]),
        HostKind.Ipv4 => ParsedHost.FromIpv4(HostV4),
        HostKind.Ipv6 => ParsedHost.FromIpv6(HostV6!),
        _ => ParsedHost.FromDomain(string.Empty),
    };

    // ---------------------------------------------------------------- setters

    /// <summary>
    /// The crate's <c>set_scheme</c>: both schemes must agree on being special, a new
    /// <c>file</c> scheme is refused when the URL has an authority, and a new special scheme
    /// is refused when the URL has no host. Returns false (and changes nothing) otherwise.
    /// </summary>
    public bool SetScheme(string scheme)
    {
        if (!UrlParser.TryParseSetterScheme(scheme, out var newScheme, out var remainingEmpty))
        {
            return false;
        }

        var newType = SchemeTypes.Of(newScheme);
        var oldType = Type;
        if (newType.IsSpecial() != oldType.IsSpecial() || (newType.IsFile() && HasAuthority))
        {
            return false;
        }

        if (!remainingEmpty || (!HasHost && newType.IsSpecial()))
        {
            return false;
        }

        var delta = newScheme.Length - SchemeEnd;
        Serialization = newScheme + Serialization[SchemeEnd..];
        SchemeEnd = newScheme.Length;
        UsernameEnd += delta;
        HostStart += delta;
        HostEnd += delta;
        PathStart += delta;
        Shift(ref QueryStart, delta);
        Shift(ref FragmentStart, delta);

        // Drop the port if it is now the new scheme's default. A failure here means the URL
        // has no port to normalize, which the crate also ignores.
        SetPort(Port);
        return true;
    }

    /// <summary>True when credentials and ports cannot be set on this URL.</summary>
    private bool CannotHaveCredentialsOrPort => !HasHost || Scheme == "file";

    /// <summary>The crate's <c>set_username</c>.</summary>
    public bool SetUsername(string username)
    {
        if (CannotHaveCredentialsOrPort)
        {
            return false;
        }

        var usernameStart = SchemeEnd + 3;
        if (Serialization[usernameStart..UsernameEnd] == username)
        {
            return true;
        }

        var afterUsername = Serialization[UsernameEnd..];
        var sb = new StringBuilder(Serialization[..usernameStart]);
        PercentEncoding.AppendEncoded(sb, username, PercentEncoding.Userinfo);

        var removed = UsernameEnd;
        UsernameEnd = sb.Length;
        var added = UsernameEnd;

        var newUsernameIsEmpty = UsernameEnd == usernameStart;
        var first = afterUsername.Length > 0 ? afterUsername[0] : (char?)null;
        if (newUsernameIsEmpty && first == '@')
        {
            removed += 1;
            sb.Append(afterUsername, 1, afterUsername.Length - 1);
        }
        else if ((!newUsernameIsEmpty && first == '@') || first == ':' || newUsernameIsEmpty)
        {
            sb.Append(afterUsername);
        }
        else
        {
            added += 1;
            sb.Append('@').Append(afterUsername);
        }

        Serialization = sb.ToString();
        var delta = added - removed;
        HostStart += delta;
        HostEnd += delta;
        PathStart += delta;
        Shift(ref QueryStart, delta);
        Shift(ref FragmentStart, delta);
        return true;
    }

    /// <summary>The crate's <c>set_password</c>; null or empty removes the password.</summary>
    public bool SetPassword(string? password)
    {
        if (CannotHaveCredentialsOrPort)
        {
            return false;
        }

        password ??= string.Empty;
        if (password.Length != 0)
        {
            var hostAndAfter = Serialization[HostStart..];
            var sb = new StringBuilder(Serialization[..UsernameEnd]);
            sb.Append(':');
            PercentEncoding.AppendEncoded(sb, password, PercentEncoding.Userinfo);
            sb.Append('@');

            var oldHostStart = HostStart;
            var newHostStart = sb.Length;
            var delta = newHostStart - oldHostStart;
            HostStart = newHostStart;
            HostEnd += delta;
            PathStart += delta;
            Shift(ref QueryStart, delta);
            Shift(ref FragmentStart, delta);
            sb.Append(hostAndAfter);
            Serialization = sb.ToString();
        }
        else if (UsernameEnd < Serialization.Length && Serialization[UsernameEnd] == ':')
        {
            var usernameStart = SchemeEnd + 3;
            var emptyUsername = usernameStart == UsernameEnd;
            var start = UsernameEnd;                                // remove the ':'
            var end = emptyUsername ? HostStart : HostStart - 1;    // and the '@' if unneeded
            Serialization = Serialization[..start] + Serialization[end..];
            var offset = end - start;
            HostStart -= offset;
            HostEnd -= offset;
            PathStart -= offset;
            Shift(ref QueryStart, -offset);
            Shift(ref FragmentStart, -offset);
        }

        return true;
    }

    /// <summary>
    /// The crate's <c>set_host</c> for a non-null host: everything after an unbracketed
    /// <c>:</c> is discarded, an empty host is refused for special non-file schemes, and an
    /// opaque path means there is no host to set.
    /// </summary>
    public bool SetHost(string host)
    {
        if (CannotBeABase)
        {
            return false;
        }

        var schemeType = Type;
        if (host.Length == 0 && schemeType.IsSpecial() && !schemeType.IsFile())
        {
            return false;
        }

        var hostSubstr = host;
        if (!host.StartsWith('[') || !host.EndsWith(']'))
        {
            var colon = host.IndexOf(':');
            if (colon == 0)
            {
                return false;
            }

            if (colon > 0)
            {
                hostSubstr = host[..colon];
            }
        }

        var parsed = schemeType.IsSpecial()
            ? HostParser.TryParse(hostSubstr, out var h) ? h : (ParsedHost?)null
            : HostParser.TryParseOpaque(hostSubstr, out var o) ? o : (ParsedHost?)null;
        if (parsed is null)
        {
            return false;
        }

        SetHostInternal(parsed.Value);
        return true;
    }

    private void SetHostInternal(ParsedHost host)
    {
        var oldSuffixPos = HostEnd;
        var suffix = Serialization[oldSuffixPos..];
        var sb = new StringBuilder(Serialization[..HostStart]);
        if (!HasAuthority)
        {
            sb.Append("//");
            UsernameEnd += 2;
            HostStart += 2;
        }

        sb.Append(host.ToString());
        HostEnd = sb.Length;
        HostKind = host.IsEmptyDomain ? HostKind.None : host.Kind;
        HostV4 = host.V4;
        HostV6 = host.V6;

        var newSuffixPos = sb.Length;
        sb.Append(suffix);
        Serialization = sb.ToString();

        var delta = newSuffixPos - oldSuffixPos;
        PathStart += delta;
        Shift(ref QueryStart, delta);
        Shift(ref FragmentStart, delta);
    }

    /// <summary>The crate's <c>set_port</c>; a default port is normalized away.</summary>
    public bool SetPort(int? port)
    {
        if (CannotHaveCredentialsOrPort)
        {
            return false;
        }

        if (port is not null && port == SchemeTypes.DefaultPort(Scheme))
        {
            port = null;
        }

        if (Port is null && port is null)
        {
            return true;
        }

        if (Port is not null && port is null)
        {
            var offset = PathStart - HostEnd;
            Serialization = Serialization[..HostEnd] + Serialization[PathStart..];
            PathStart = HostEnd;
            Shift(ref QueryStart, -offset);
            Shift(ref FragmentStart, -offset);
        }
        else if (Port != port)
        {
            var pathAndAfter = Serialization[PathStart..];
            var text = string.Create(CultureInfo.InvariantCulture, $"{Serialization[..HostEnd]}:{port!.Value}");
            var oldPathStart = PathStart;
            PathStart = text.Length;
            var delta = PathStart - oldPathStart;
            Shift(ref QueryStart, delta);
            Shift(ref FragmentStart, delta);
            Serialization = text + pathAndAfter;
        }

        Port = port;
        return true;
    }

    /// <summary>The crate's <c>set_path</c>.</summary>
    public void SetPath(string path) => UrlParser.SetPath(this, path);

    /// <summary>The crate's <c>set_query</c>; null clears the query.</summary>
    public void SetQuery(string? query) => UrlParser.SetQuery(this, query);

    /// <summary>The crate's <c>set_fragment</c>; null clears the fragment.</summary>
    public void SetFragment(string? fragment) => UrlParser.SetFragment(this, fragment);

    internal void StripTrailingSpacesFromOpaquePath()
    {
        if (!CannotBeABase || FragmentStart is not null || QueryStart is not null)
        {
            return;
        }

        var end = Serialization.Length;
        while (end > PathStart && Serialization[end - 1] == ' ')
        {
            end--;
        }

        Serialization = Serialization[..end];
    }

    private static void Shift(ref int? index, int delta)
    {
        if (index is int value)
        {
            index = value + delta;
        }
    }
}
