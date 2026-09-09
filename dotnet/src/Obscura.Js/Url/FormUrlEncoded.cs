using System.Text;

namespace Obscura.Js.Url;

/// <summary>
/// The <c>application/x-www-form-urlencoded</c> parser and serializer.
/// <see href="https://url.spec.whatwg.org/#urlencoded-parsing"/>.
/// </summary>
public static class FormUrlEncoded
{
    /// <summary>
    /// Parses a urlencoded string into its name/value pairs. Bytes are interpreted as UTF-8
    /// after percent-decoding, and <c>+</c> decodes to a space.
    /// </summary>
    public static List<KeyValuePair<string, string>> Parse(string input)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var start = 0;
        while (start <= input.Length)
        {
            var amp = input.IndexOf('&', start);
            var end = amp < 0 ? input.Length : amp;
            var sequence = input[start..end];
            if (sequence.Length != 0)
            {
                var eq = sequence.IndexOf('=');
                var name = eq < 0 ? sequence : sequence[..eq];
                var value = eq < 0 ? string.Empty : sequence[(eq + 1)..];
                pairs.Add(new KeyValuePair<string, string>(DecodeComponent(name), DecodeComponent(value)));
            }

            if (amp < 0)
            {
                break;
            }

            start = amp + 1;
        }

        return pairs;
    }

    /// <summary>Serializes name/value pairs into a urlencoded string.</summary>
    public static string Serialize(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var sb = new StringBuilder();
        foreach (var pair in pairs)
        {
            if (sb.Length != 0)
            {
                sb.Append('&');
            }

            AppendEncodedComponent(sb, pair.Key);
            sb.Append('=');
            AppendEncodedComponent(sb, pair.Value);
        }

        return sb.ToString();
    }

    /// <summary>Percent-decodes one component, mapping <c>+</c> to a space.</summary>
    public static string DecodeComponent(string component)
    {
        var replaced = component.Contains('+', StringComparison.Ordinal)
            ? component.Replace('+', ' ')
            : component;
        var bytes = PercentEncoding.Decode(replaced);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// The urlencoded byte serializer: space becomes <c>+</c>, ASCII alphanumerics and
    /// <c>* - . _</c> stay literal, everything else is percent-encoded.
    /// </summary>
    public static void AppendEncodedComponent(StringBuilder output, string component)
    {
        foreach (var b in Encoding.UTF8.GetBytes(component))
        {
            switch (b)
            {
                case (byte)' ':
                    output.Append('+');
                    break;
                case (byte)'*':
                case (byte)'-':
                case (byte)'.':
                case (byte)'_':
                    output.Append((char)b);
                    break;
                default:
                    if (char.IsAsciiLetterOrDigit((char)b))
                    {
                        output.Append((char)b);
                    }
                    else
                    {
                        PercentEncoding.AppendByte(output, b);
                    }

                    break;
            }
        }
    }

    /// <summary>Serializes one component on its own.</summary>
    public static string EncodeComponent(string component)
    {
        var sb = new StringBuilder(component.Length + 8);
        AppendEncodedComponent(sb, component);
        return sb.ToString();
    }
}
