using System.Text;

namespace PocketCalculator.Js.Url;

/// <summary>
/// The Fetch standard's <c>data:</c> URL processor, and the JavaScript MIME type list
/// script loading checks a response against.
/// </summary>
/// <remarks>
/// Port addition: the Rust engine has no <c>data:</c> handling in its module loader, so a
/// module graph that reached a <c>data:</c> URL went to the network client and failed
/// (<c>Failed to fetch module data:text/javascript,...</c>, seen on reddit.com).
/// </remarks>
public static class DataUrl
{
    /// <summary>
    /// Runs <see href="https://fetch.spec.whatwg.org/#data-url-processor"/> over
    /// <paramref name="href"/>. Returns false where the processor returns failure: not a
    /// <c>data:</c> URL, no comma, or a body marked base64 that does not decode.
    /// </summary>
    /// <param name="href">A serialized URL; a fragment is ignored.</param>
    /// <param name="mimeEssence">The lowercased <c>type/subtype</c>, <c>text/plain</c> when
    /// the URL names none or an invalid one.</param>
    /// <param name="body">The decoded bytes.</param>
    public static bool TryProcess(string href, out string mimeEssence, out byte[] body)
    {
        mimeEssence = "text/plain";
        body = [];
        if (href is null || !href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var end = href.IndexOf('#', StringComparison.Ordinal);
        var input = href.AsSpan(5, (end < 0 ? href.Length : end) - 5);
        var comma = input.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        var mimeType = input[..comma].Trim(" \t\n\r\f").ToString();
        var bytes = PercentDecode(input[(comma + 1)..]);

        if (EndsWithBase64Marker(mimeType, out var trimmedMime))
        {
            if (ForgivingBase64(bytes) is not { } decoded)
            {
                return false;
            }

            bytes = decoded;
            mimeType = trimmedMime;
        }

        if (mimeType.StartsWith(';'))
        {
            mimeType = "text/plain" + mimeType;
        }

        mimeEssence = Essence(mimeType) ?? "text/plain";
        body = bytes;
        return true;
    }

    /// <summary>
    /// The lowercased essence (<c>type/subtype</c>) of a <c>Content-Type</c> value, or null
    /// when it does not parse as a MIME type.
    /// </summary>
    public static string? Essence(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return null;
        }

        var semi = contentType.IndexOf(';', StringComparison.Ordinal);
        var essence = (semi < 0 ? contentType : contentType[..semi]).Trim(' ', '\t', '\n', '\r', '\f');
        var slash = essence.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == essence.Length - 1)
        {
            return null;
        }

        foreach (var c in essence)
        {
            if (c != '/' && !IsTokenChar(c))
            {
                return null;
            }
        }

        return essence.IndexOf('/', slash + 1) >= 0 ? null : essence.ToLowerInvariant();
    }

    /// <summary>
    /// Whether <paramref name="essence"/> is one of the HTML standard's JavaScript MIME
    /// type essences (<see href="https://mimesniff.spec.whatwg.org/#javascript-mime-type"/>).
    /// </summary>
    public static bool IsJavaScriptMimeType(string? essence) => essence switch
    {
        "application/ecmascript" or "application/javascript" or "application/x-ecmascript"
            or "application/x-javascript" or "text/ecmascript" or "text/javascript"
            or "text/javascript1.0" or "text/javascript1.1" or "text/javascript1.2"
            or "text/javascript1.3" or "text/javascript1.4" or "text/javascript1.5"
            or "text/jscript" or "text/livescript" or "text/x-ecmascript"
            or "text/x-javascript" => true,
        _ => false,
    };

    /// <summary>UTF-8 decode as the HTML standard decodes a module script: a BOM is dropped.</summary>
    public static string Utf8Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    private static bool IsTokenChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_'
            or '`' or '|' or '~';

    private static bool EndsWithBase64Marker(string mimeType, out string trimmed)
    {
        trimmed = mimeType;
        if (mimeType.Length < 7
            || !mimeType.EndsWith("base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var i = mimeType.Length - 7;
        while (i >= 0 && mimeType[i] == ' ')
        {
            i--;
        }

        if (i < 0 || mimeType[i] != ';')
        {
            return false;
        }

        trimmed = mimeType[..i];
        return true;
    }

    private static byte[] PercentDecode(ReadOnlySpan<char> input)
    {
        var utf8 = Encoding.UTF8.GetBytes(input.ToArray());
        var output = new byte[utf8.Length];
        var n = 0;
        for (var i = 0; i < utf8.Length; i++)
        {
            var b = utf8[i];
            if (b == (byte)'%' && i + 2 < utf8.Length
                && Hex(utf8[i + 1]) is var hi and >= 0
                && Hex(utf8[i + 2]) is var lo and >= 0)
            {
                output[n++] = (byte)((hi << 4) | lo);
                i += 2;
                continue;
            }

            output[n++] = b;
        }

        return output.AsSpan(0, n).ToArray();
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    /// <summary><see href="https://infra.spec.whatwg.org/#forgiving-base64-decode"/>.</summary>
    private static byte[]? ForgivingBase64(byte[] input)
    {
        var data = new List<byte>(input.Length);
        foreach (var b in input)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f'))
            {
                data.Add(b);
            }
        }

        if (data.Count % 4 == 0 && data.Count > 0 && data[^1] == '=')
        {
            data.RemoveAt(data.Count - 1);
            if (data.Count > 0 && data[^1] == '=')
            {
                data.RemoveAt(data.Count - 1);
            }
        }

        if (data.Count % 4 == 1)
        {
            return null;
        }

        var output = new List<byte>(data.Count * 3 / 4);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            int v = b switch
            {
                >= (byte)'A' and <= (byte)'Z' => b - 'A',
                >= (byte)'a' and <= (byte)'z' => b - 'a' + 26,
                >= (byte)'0' and <= (byte)'9' => b - '0' + 52,
                (byte)'+' => 62,
                (byte)'/' => 63,
                _ => -1,
            };
            if (v < 0)
            {
                return null;
            }

            buffer = (buffer << 6) | v;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return [.. output];
    }
}
