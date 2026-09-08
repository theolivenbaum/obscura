using System.Text;

namespace Obscura.Net;

/// <summary>
/// The WHATWG Encoding Standard's label table and decoders, standing in for the
/// Rust <c>encoding_rs</c> crate. Labels resolve to a canonical encoding name
/// exactly as <c>Encoding::for_label</c> does, and decoding goes through the
/// matching .NET code page (legacy pages come from
/// <c>System.Text.Encoding.CodePages</c>) or an in-tree single byte table for
/// the handful of pages .NET does not ship.
/// </summary>
public sealed class WhatwgEncoding
{
    private const int TableCodePage = -1;
    private const int ReplacementCodePage = -2;

    private static readonly Dictionary<string, string> LabelToName = BuildLabelTable();
    private static readonly Dictionary<string, WhatwgEncoding> Instances =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock InstanceLock = new();

    private readonly int _codePage;
    private readonly char[]? _table;
    private System.Text.Encoding? _lenient;
    private System.Text.Encoding? _strict;
    private Dictionary<uint, byte>? _reverseTable;

    static WhatwgEncoding()
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private WhatwgEncoding(string name, int codePage, char[]? table)
    {
        Name = name;
        _codePage = codePage;
        _table = table;
    }

    /// <summary>The WHATWG canonical name, e.g. <c>GBK</c> or <c>Shift_JIS</c>.</summary>
    public string Name { get; }

    /// <summary>True for the "replacement" encoding, which decodes to a single U+FFFD.</summary>
    public bool IsReplacement => _codePage == ReplacementCodePage;

    /// <summary>
    /// The WHATWG canonical (lowercased) name for a label, or null when the label
    /// names no encoding. Mirrors <c>encoding::label_name</c>.
    /// </summary>
    public static string? LabelName(string? label)
    {
        var name = CanonicalName(label);
        return name?.ToLowerInvariant();
    }

    /// <summary>The canonical (correctly cased) name for a label, or null.</summary>
    public static string? CanonicalName(string? label)
    {
        if (label is null)
        {
            return null;
        }

        var trimmed = TrimAsciiWhitespace(label);
        if (trimmed.Length == 0)
        {
            return null;
        }

        return LabelToName.TryGetValue(trimmed.ToLowerInvariant(), out var name) ? name : null;
    }

    /// <summary>Resolve a label to an encoding, mirroring <c>Encoding::for_label</c>.</summary>
    public static WhatwgEncoding? ForLabel(string? label)
    {
        var name = CanonicalName(label);
        return name is null ? null : ForName(name);
    }

    /// <summary>Resolve a canonical encoding name to an encoding.</summary>
    public static WhatwgEncoding? ForName(string name)
    {
        lock (InstanceLock)
        {
            if (Instances.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var created = Create(name);
            if (created is not null)
            {
                Instances[name] = created;
            }

            return created;
        }
    }

    /// <summary>UTF-8, the default for every detection path.</summary>
    public static WhatwgEncoding Utf8 { get; } = ForName("UTF-8")!;

    /// <summary>
    /// Decode with BOM sniffing, matching <c>Encoding::decode</c>: a leading
    /// UTF-8/UTF-16LE/UTF-16BE byte order mark wins over this encoding and is
    /// stripped from the output. Malformed input becomes U+FFFD.
    /// </summary>
    public string Decode(ReadOnlySpan<byte> bytes)
    {
        var (encoding, offset) = SniffBom(bytes, this);
        return encoding.DecodeWithoutBomHandling(bytes[offset..]);
    }

    /// <summary>Decode without BOM handling; malformed input becomes U+FFFD.</summary>
    public string DecodeWithoutBomHandling(ReadOnlySpan<byte> bytes)
    {
        if (IsReplacement)
        {
            return bytes.Length == 0 ? string.Empty : "�";
        }

        if (_table is not null)
        {
            return DecodeTable(bytes, fatal: false) ?? string.Empty;
        }

        return LenientEncoding().GetString(bytes);
    }

    /// <summary>
    /// Strict decode: null when the input is not valid in this encoding.
    /// Backs <c>TextDecoder</c>'s fatal mode.
    /// </summary>
    public string? DecodeFatal(ReadOnlySpan<byte> bytes, bool ignoreBom)
    {
        var target = this;
        var offset = 0;
        if (!ignoreBom)
        {
            (target, offset) = SniffBom(bytes, this);
        }

        var rest = bytes[offset..];
        if (target.IsReplacement)
        {
            return rest.Length == 0 ? string.Empty : null;
        }

        if (target._table is not null)
        {
            return target.DecodeTable(rest, fatal: true);
        }

        try
        {
            return target.StrictEncoding().GetString(rest);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Encode a single scalar value to this charset. Returns false when the code
    /// point is unmappable, which the URL query serializer turns into a numeric
    /// character reference.
    /// </summary>
    public bool TryEncodeRune(System.Text.Rune rune, out byte[] bytes)
    {
        // WHATWG "get an output encoding": replacement and the UTF-16 encodings
        // all encode as UTF-8. encoding_rs's new_encoder does the same.
        if (IsReplacement || _codePage == 1200 || _codePage == 1201)
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(rune.ToString());
            return true;
        }

        if (_table is not null)
        {
            var reverse = ReverseTable();
            if (rune.Value < 0x80)
            {
                bytes = [(byte)rune.Value];
                return true;
            }

            if (reverse.TryGetValue((uint)rune.Value, out var b))
            {
                bytes = [b];
                return true;
            }

            bytes = [];
            return false;
        }

        try
        {
            bytes = StrictEncoding().GetBytes(rune.ToString());
            return true;
        }
        catch (EncoderFallbackException)
        {
            bytes = [];
            return false;
        }
    }

    private static (WhatwgEncoding Encoding, int Offset) SniffBom(
        ReadOnlySpan<byte> bytes,
        WhatwgEncoding fallback)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (ForName("UTF-8")!, 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (ForName("UTF-16LE")!, 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (ForName("UTF-16BE")!, 2);
        }

        return (fallback, 0);
    }

    private string? DecodeTable(ReadOnlySpan<byte> bytes, bool fatal)
    {
        var table = _table!;
        var builder = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (b < 0x80)
            {
                builder.Append((char)b);
                continue;
            }

            var mapped = table[b - 0x80];
            if (mapped == '￿')
            {
                if (fatal)
                {
                    return null;
                }

                builder.Append('�');
                continue;
            }

            builder.Append(mapped);
        }

        return builder.ToString();
    }

    private Dictionary<uint, byte> ReverseTable()
    {
        if (_reverseTable is not null)
        {
            return _reverseTable;
        }

        var reverse = new Dictionary<uint, byte>(128);
        for (var i = 0; i < 128; i++)
        {
            var c = _table![i];
            if (c != '￿')
            {
                reverse.TryAdd(c, (byte)(0x80 + i));
            }
        }

        _reverseTable = reverse;
        return reverse;
    }

    private System.Text.Encoding LenientEncoding() =>
        _lenient ??= System.Text.Encoding.GetEncoding(
            _codePage,
            EncoderFallback.ReplacementFallback,
            new DecoderReplacementFallback("�"));

    private System.Text.Encoding StrictEncoding() =>
        _strict ??= System.Text.Encoding.GetEncoding(
            _codePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);

    private static WhatwgEncoding? Create(string name)
    {
        if (!NameToCodePage.TryGetValue(name, out var codePage))
        {
            return null;
        }

        if (codePage == ReplacementCodePage)
        {
            return new WhatwgEncoding(name, ReplacementCodePage, null);
        }

        if (codePage == TableCodePage)
        {
            return new WhatwgEncoding(name, TableCodePage, SingleByteTables.For(name));
        }

        try
        {
            _ = System.Text.Encoding.GetEncoding(codePage);
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        return new WhatwgEncoding(name, codePage, null);
    }

    private static string TrimAsciiWhitespace(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsAsciiWhitespace(value[start]))
        {
            start++;
        }

        while (end > start && IsAsciiWhitespace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsAsciiWhitespace(char c) =>
        c is '\t' or '\n' or '\f' or '\r' or ' ';

    private static readonly Dictionary<string, int> NameToCodePage = new(StringComparer.Ordinal)
    {
        ["UTF-8"] = 65001,
        ["IBM866"] = 866,
        ["ISO-8859-2"] = 28592,
        ["ISO-8859-3"] = 28593,
        ["ISO-8859-4"] = 28594,
        ["ISO-8859-5"] = 28595,
        ["ISO-8859-6"] = 28596,
        ["ISO-8859-7"] = 28597,
        ["ISO-8859-8"] = 28598,
        ["ISO-8859-8-I"] = 38598,
        ["ISO-8859-10"] = TableCodePage,
        ["ISO-8859-13"] = 28603,
        ["ISO-8859-14"] = TableCodePage,
        ["ISO-8859-15"] = 28605,
        ["ISO-8859-16"] = TableCodePage,
        ["KOI8-R"] = 20866,
        ["KOI8-U"] = 21866,
        ["macintosh"] = 10000,
        ["windows-874"] = 874,
        ["windows-1250"] = 1250,
        ["windows-1251"] = 1251,
        ["windows-1252"] = 1252,
        ["windows-1253"] = 1253,
        ["windows-1254"] = 1254,
        ["windows-1255"] = 1255,
        ["windows-1256"] = 1256,
        ["windows-1257"] = 1257,
        ["windows-1258"] = 1258,
        ["x-mac-cyrillic"] = 10007,
        ["GBK"] = 936,
        ["gb18030"] = 54936,
        ["Big5"] = 950,
        ["EUC-JP"] = 51932,
        ["ISO-2022-JP"] = 50220,
        ["Shift_JIS"] = 932,
        ["EUC-KR"] = 51949,
        ["replacement"] = ReplacementCodePage,
        ["UTF-16BE"] = 1201,
        ["UTF-16LE"] = 1200,
        ["x-user-defined"] = TableCodePage,
    };

    private static Dictionary<string, string> BuildLabelTable()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string name, params string[] labels)
        {
            foreach (var label in labels)
            {
                table[label] = name;
            }
        }

        Add("UTF-8", "unicode-1-1-utf-8", "unicode11utf8", "unicode20utf8", "utf-8", "utf8",
            "x-unicode20utf8");
        Add("IBM866", "866", "cp866", "csibm866", "ibm866");
        Add("ISO-8859-2", "csisolatin2", "iso-8859-2", "iso-ir-101", "iso8859-2", "iso88592",
            "iso_8859-2", "iso_8859-2:1987", "l2", "latin2");
        Add("ISO-8859-3", "csisolatin3", "iso-8859-3", "iso-ir-109", "iso8859-3", "iso88593",
            "iso_8859-3", "iso_8859-3:1988", "l3", "latin3");
        Add("ISO-8859-4", "csisolatin4", "iso-8859-4", "iso-ir-110", "iso8859-4", "iso88594",
            "iso_8859-4", "iso_8859-4:1988", "l4", "latin4");
        Add("ISO-8859-5", "csisolatincyrillic", "cyrillic", "iso-8859-5", "iso-ir-144",
            "iso8859-5", "iso88595", "iso_8859-5", "iso_8859-5:1988");
        Add("ISO-8859-6", "arabic", "asmo-708", "csiso88596e", "csiso88596i", "csisolatinarabic",
            "ecma-114", "iso-8859-6", "iso-8859-6-e", "iso-8859-6-i", "iso-ir-127", "iso8859-6",
            "iso88596", "iso_8859-6", "iso_8859-6:1987");
        Add("ISO-8859-7", "csisolatingreek", "ecma-118", "elot_928", "greek", "greek8",
            "iso-8859-7", "iso-ir-126", "iso8859-7", "iso88597", "iso_8859-7", "iso_8859-7:1987",
            "sun_eu_greek");
        Add("ISO-8859-8", "csiso88598e", "csisolatinhebrew", "hebrew", "iso-8859-8",
            "iso-8859-8-e", "iso-ir-138", "iso8859-8", "iso88598", "iso_8859-8",
            "iso_8859-8:1988", "visual");
        Add("ISO-8859-8-I", "csiso88598i", "iso-8859-8-i", "logical");
        Add("ISO-8859-10", "csisolatin6", "iso-8859-10", "iso-ir-157", "iso8859-10", "iso885910",
            "l6", "latin6");
        Add("ISO-8859-13", "iso-8859-13", "iso8859-13", "iso885913");
        Add("ISO-8859-14", "iso-8859-14", "iso8859-14", "iso885914");
        Add("ISO-8859-15", "csisolatin9", "iso-8859-15", "iso8859-15", "iso885915", "iso_8859-15",
            "l9");
        Add("ISO-8859-16", "iso-8859-16");
        Add("KOI8-R", "cskoi8r", "koi", "koi8", "koi8-r", "koi8_r");
        Add("KOI8-U", "koi8-ru", "koi8-u");
        Add("macintosh", "csmacintosh", "mac", "macintosh", "x-mac-roman");
        Add("windows-874", "dos-874", "iso-8859-11", "iso8859-11", "iso885911", "tis-620",
            "windows-874");
        Add("windows-1250", "cp1250", "windows-1250", "x-cp1250");
        Add("windows-1251", "cp1251", "windows-1251", "x-cp1251");
        Add("windows-1252", "ansi_x3.4-1968", "ascii", "cp1252", "cp819", "csisolatin1", "ibm819",
            "iso-8859-1", "iso-ir-100", "iso8859-1", "iso88591", "iso_8859-1", "iso_8859-1:1987",
            "l1", "latin1", "us-ascii", "windows-1252", "x-cp1252");
        Add("windows-1253", "cp1253", "windows-1253", "x-cp1253");
        Add("windows-1254", "cp1254", "csisolatin5", "iso-8859-9", "iso-ir-148", "iso8859-9",
            "iso88599", "iso_8859-9", "iso_8859-9:1989", "l5", "latin5", "windows-1254",
            "x-cp1254");
        Add("windows-1255", "cp1255", "windows-1255", "x-cp1255");
        Add("windows-1256", "cp1256", "windows-1256", "x-cp1256");
        Add("windows-1257", "cp1257", "windows-1257", "x-cp1257");
        Add("windows-1258", "cp1258", "windows-1258", "x-cp1258");
        Add("x-mac-cyrillic", "x-mac-cyrillic", "x-mac-ukrainian");
        Add("GBK", "chinese", "csgb2312", "csiso58gb231280", "gb2312", "gb_2312", "gb_2312-80",
            "gbk", "iso-ir-58", "x-gbk");
        Add("gb18030", "gb18030");
        Add("Big5", "big5", "big5-hkscs", "cn-big5", "csbig5", "x-x-big5");
        Add("EUC-JP", "cseucpkdfmtjapanese", "euc-jp", "x-euc-jp");
        Add("ISO-2022-JP", "csiso2022jp", "iso-2022-jp");
        Add("Shift_JIS", "csshiftjis", "ms932", "ms_kanji", "shift-jis", "shift_jis", "sjis",
            "windows-31j", "x-sjis");
        Add("EUC-KR", "cseuckr", "csksc56011987", "euc-kr", "iso-ir-149", "korean",
            "ks_c_5601-1987", "ks_c_5601-1989", "ksc5601", "ksc_5601", "windows-949");
        Add("replacement", "csiso2022kr", "hz-gb-2312", "iso-2022-cn", "iso-2022-cn-ext",
            "iso-2022-kr", "replacement");
        Add("UTF-16BE", "unicodefffe", "utf-16be");
        Add("UTF-16LE", "csunicode", "iso-10646-ucs-2", "ucs-2", "unicode", "unicodefeff",
            "utf-16", "utf-16le");
        Add("x-user-defined", "x-user-defined");

        return table;
    }
}
