using System.Text;

namespace Obscura.Net.Tests;

/// <summary>Port of the <c>#[cfg(test)] mod tests</c> block in <c>encoding.rs</c>.</summary>
public class EncodingTests
{
    [Fact]
    public void ContentTypeCharsetWins()
    {
        var bytes = "<html><head><meta charset=\"utf-8\"></head><body></body></html>"u8.ToArray();
        var (encoding, source) = ContentEncoding.DetectEncoding(bytes, "text/html; charset=gbk");
        Assert.Equal("GBK", encoding.Name);
        Assert.Equal("content-type", source);
    }

    [Fact]
    public void ContentTypeQuotedCharsetIsParsed()
    {
        var (encoding, _) = ContentEncoding.DetectEncoding([], "text/html; charset=\"Shift_JIS\"");
        Assert.Equal("Shift_JIS", encoding.Name);
    }

    [Fact]
    public void MetaCharsetUsedWhenHeaderMissing()
    {
        var bytes = "<!doctype html><html><head><meta charset=\"big5\"></head></html>"u8.ToArray();
        var (encoding, source) = ContentEncoding.DetectEncoding(bytes, null);
        Assert.Equal("Big5", encoding.Name);
        Assert.Equal("meta-charset", source);
    }

    [Fact]
    public void MetaHttpEquivCharsetIsRecognized()
    {
        var bytes =
            "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=EUC-KR\"></head></html>"u8
                .ToArray();
        var (encoding, _) = ContentEncoding.DetectEncoding(bytes, null);
        Assert.Equal("EUC-KR", encoding.Name);
    }

    [Fact]
    public void UnrelatedMetaAttributesDoNotDeclareACharset()
    {
        byte[][] samples =
        [
            "<meta name=\"description\" content=\"charset=gbk\">"u8.ToArray(),
            "<meta data-charset=\"gbk\">"u8.ToArray(),
            "<metadata charset=\"gbk\">"u8.ToArray(),
        ];
        foreach (var bytes in samples)
        {
            var (encoding, source) = ContentEncoding.DetectEncoding(bytes, null);
            Assert.Equal("UTF-8", encoding.Name);
            Assert.Equal("default-utf8", source);
        }
    }

    [Fact]
    public void NoCharsetAnywhereFallsBackToUtf8()
    {
        var bytes = "<html><body>hello</body></html>"u8.ToArray();
        var (encoding, source) = ContentEncoding.DetectEncoding(bytes, null);
        Assert.Equal("UTF-8", encoding.Name);
        Assert.Equal("default-utf8", source);
    }

    [Fact]
    public void DecodeResponseGbkBytesRoundtrip()
    {
        // "ni hao" encoded as GBK = C4 E3 BA C3
        byte[] bytes = [0xC4, 0xE3, 0xBA, 0xC3];
        var text = ContentEncoding.DecodeResponse(bytes, "text/html; charset=gbk");
        Assert.Equal("你好", text);
    }

    [Fact]
    public void DecodeNonHtmlSkipsMetaSniff()
    {
        // A JS body that happens to contain a string `<meta charset="gbk">` must NOT
        // be decoded as GBK - non-HTML resources only honor the HTTP header.
        var bytes = Encoding.UTF8.GetBytes("var x = '<meta charset=\"gbk\">'; // not the real charset");
        var text = ContentEncoding.DecodeNonHtml(bytes, "application/javascript");
        Assert.Contains("<meta charset=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UrlEncodeQueryEucjpHighBytes()
    {
        // U+8108 is EUC-JP CC AE; both bytes are above 0x7E so both encode.
        Assert.Equal("%CC%AE", ContentEncoding.UrlEncodeQuery("脈", "euc-jp", true));
    }

    [Fact]
    public void UrlEncodeQueryUnmappableBecomesNcr()
    {
        // A code point not in shift_jis becomes the percent-encoded &#NNN;.
        // U+3402 is a CJK ext-A han char not in shift_jis.
        var got = ContentEncoding.UrlEncodeQuery("㐂", "shift_jis", true);
        Assert.Equal("%26%2313314%3B", got);
    }

    [Fact]
    public void UrlEncodeQueryBig5LowTrailByteIsEscaped()
    {
        // U+4E00 is Big5 A4 40; the 0x40 trail byte is ASCII '@' but must still be
        // percent-encoded because it serializes a non-ASCII char.
        Assert.Equal("%A4%40", ContentEncoding.UrlEncodeQuery("一", "big5", true));
    }

    [Fact]
    public void UrlEncodeQueryKeepsAsciiStructure()
    {
        // ASCII delimiters in a real query stay literal (standard query set): only the
        // non-ASCII value is re-encoded to the target charset.
        Assert.Equal("a=%CC%AE&b=c", ContentEncoding.UrlEncodeQuery("a=脈&b=c", "euc-jp", true));
    }

    [Fact]
    public void MetaSniffOnlyScansFirst1Kb()
    {
        var bytes = new List<byte>(Enumerable.Repeat((byte)' ', 2048));
        bytes.AddRange("<meta charset=\"gbk\">"u8.ToArray());
        var (encoding, _) = ContentEncoding.DetectEncoding(bytes.ToArray(), null);
        // Beyond 1KB: ignored, fall back to UTF-8.
        Assert.Equal("UTF-8", encoding.Name);
    }
}
