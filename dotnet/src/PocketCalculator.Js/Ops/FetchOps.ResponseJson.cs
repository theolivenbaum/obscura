using System.Buffers;
using System.Globalization;
using System.Text;

namespace PocketCalculator.Js.Ops;

public static partial class FetchOps
{
    /// <summary>Chunk size for the two passes over a body; small enough to stay off the LOH.</summary>
    private const int BodyChunkChars = 16 * 1024;

    /// <summary>
    /// The script-facing <c>op_fetch_url</c> result, written straight from the body bytes.
    /// </summary>
    /// <remarks>
    /// The JSON is byte-identical to what the Rust op (and the port before it) builds: the
    /// body decoded as UTF-8 with replacement characters and escaped as serde_json does, and
    /// the same bytes in standard base64. DEVIATION in how, not what (SECURITY.md M4): the port
    /// held the decoded string, the base64 string, a StringBuilder sized for both and the
    /// builder's final copy at once, about five times the body. Here a first pass measures
    /// the escaped body, and the result is written into its final string once, decoding in
    /// small chunks, so a 100 MB body costs the bytes plus the result.
    /// </remarks>
    internal static string ScriptResponseJson(
        string status,
        byte[] body,
        string requestId,
        string url,
        bool redirected,
        bool opaque,
        IReadOnlyDictionary<string, string> headers)
    {
        var head = "{\"status\":" + status + ",\"body\":";
        const string Middle = ",\"bodyBase64\":\"";
        var tailBuilder = new StringBuilder(256);
        tailBuilder.Append("\",\"requestId\":");
        SerdeJson.AppendString(tailBuilder, requestId);
        tailBuilder.Append(",\"url\":");
        SerdeJson.AppendString(tailBuilder, url);
        tailBuilder.Append(",\"redirected\":").Append(redirected ? "true" : "false");
        tailBuilder.Append(",\"opaque\":").Append(opaque ? "true" : "false");
        tailBuilder.Append(",\"headers\":");
        AppendHeaders(tailBuilder, headers);
        tailBuilder.Append('}');
        var tail = tailBuilder.ToString();

        var bytes = opaque ? [] : body;
        long escaped = EscapedUtf8Length(bytes);
        long base64 = ((bytes.Length + 2L) / 3) * 4;
        long total = head.Length + escaped + Middle.Length + base64 + tail.Length;
        if (total > Array.MaxLength)
        {
            throw new OpException($"response body of {bytes.Length} bytes is too large to return to script");
        }

        return string.Create((int)total, (head, bytes, tail), static (span, state) =>
        {
            var (head, bytes, tail) = state;
            head.AsSpan().CopyTo(span);
            var at = head.Length;
            at += WriteEscapedUtf8(bytes, span[at..]);
            Middle.AsSpan().CopyTo(span[at..]);
            at += Middle.Length;
            if (!Convert.TryToBase64Chars(bytes, span[at..], out var written))
            {
                throw new InvalidOperationException("base64 length mismatch");
            }

            at += written;
            tail.AsSpan().CopyTo(span[at..]);
        });
    }

    /// <summary>
    /// The length of <paramref name="bytes"/> decoded as UTF-8 (replacement characters for
    /// malformed input, as <see cref="Encoding.GetString(byte[])"/>) and written as a JSON
    /// string literal, quotes included.
    /// </summary>
    private static long EscapedUtf8Length(ReadOnlySpan<byte> bytes)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = ArrayPool<char>.Shared.Rent(BodyChunkChars);
        try
        {
            long length = 2;
            var completed = false;
            while (!completed)
            {
                decoder.Convert(bytes, chars, flush: true, out var used, out var produced, out completed);
                bytes = bytes[used..];
                for (var i = 0; i < produced; i++)
                {
                    length += EscapedLength(chars[i]);
                }
            }

            return length;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    /// <summary>Writes what <see cref="EscapedUtf8Length"/> measured; returns the length.</summary>
    private static int WriteEscapedUtf8(ReadOnlySpan<byte> bytes, Span<char> output)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = ArrayPool<char>.Shared.Rent(BodyChunkChars);
        try
        {
            var at = 0;
            output[at++] = '"';
            var completed = false;
            while (!completed)
            {
                decoder.Convert(bytes, chars, flush: true, out var used, out var produced, out completed);
                bytes = bytes[used..];
                for (var i = 0; i < produced; i++)
                {
                    at += WriteEscaped(chars[i], output[at..]);
                }
            }

            output[at++] = '"';
            return at;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    /// <summary>The length of one UTF-16 unit as <c>UrlOps.AppendJsonString</c> writes it.</summary>
    private static int EscapedLength(char c) => c switch
    {
        '"' or '\\' or '\b' or '\f' or '\n' or '\r' or '\t' => 2,
        < (char)0x20 => 6,
        _ => 1,
    };

    /// <summary>One UTF-16 unit exactly as <c>UrlOps.AppendJsonString</c> escapes it.</summary>
    private static int WriteEscaped(char c, Span<char> output)
    {
        char short_ = c switch
        {
            '"' => '"',
            '\\' => '\\',
            '\b' => 'b',
            '\f' => 'f',
            '\n' => 'n',
            '\r' => 'r',
            '\t' => 't',
            _ => '\0',
        };
        if (short_ != '\0')
        {
            output[0] = '\\';
            output[1] = short_;
            return 2;
        }

        if (c < 0x20)
        {
            output[0] = '\\';
            output[1] = 'u';
            output[2] = '0';
            output[3] = '0';
            output[4] = "0123456789abcdef"[c >> 4];
            output[5] = "0123456789abcdef"[c & 0xF];
            return 6;
        }

        output[0] = c;
        return 1;
    }

    /// <summary>The status field as the op writes it.</summary>
    private static string StatusText(bool opaque, int status) =>
        opaque ? "0" : status.ToString(CultureInfo.InvariantCulture);
}
