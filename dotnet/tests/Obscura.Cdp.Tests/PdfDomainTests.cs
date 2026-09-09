using System.Text;
using System.Text.Json.Nodes;
using Obscura.Browser;
using Obscura.Cdp.Domains;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>#[cfg(all(test, feature = "render"))] mod tests</c> in
/// <c>crates/obscura-cdp/src/domains/pdf.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class PdfDomainTests
{
    private const int MaxPageRangeParts = 512;
    private const int MaxPageRangesBytes = 16 * 1024;
    private const long MaxBase64PdfBytes = 64L * 1024 * 1024;

    [Fact]
    public void ParserAcceptsStandardClientDefaultsAndRejectsUnrepresentedFeatures()
    {
        PdfDomain.ParsedPdfOptions standard = PdfDomain.ParseOptions(CdpDomainFixtures.Json(
            """
            {
                "transferMode": "ReturnAsStream",
                "displayHeaderFooter": false,
                "headerTemplate": "",
                "footerTemplate": "",
                "printBackground": false,
                "scale": 1,
                "pageRanges": "",
                "preferCSSPageSize": false,
                "generateTaggedPDF": true,
                "generateDocumentOutline": false
            }
            """));
        Assert.Equal(PdfDomain.PdfTransferMode.ReturnAsStream, standard.TransferMode);
        Assert.False(standard.RequestedPrintBackground);
        Assert.False(standard.Raster.PrintBackground);
        Assert.True(standard.RequestedTaggedPdf);
        Assert.Equal(1.0f, standard.Raster.Scale);
        Assert.Empty(standard.Raster.PageRanges);

        string[] rejected =
        [
            """{"displayHeaderFooter": true}""",
            """{"preferCSSPageSize": true}""",
            """{"headerTemplate": "<span>title</span>"}""",
            """{"generateDocumentOutline": true}""",
        ];
        foreach (string parameters in rejected)
        {
            JsonNode node = CdpDomainFixtures.Json(parameters);
            Assert.Throws<DomainError>(() => PdfDomain.ParseOptions(node));
        }

        PdfDomain.ParsedPdfOptions ranged = PdfDomain.ParseOptions(CdpDomainFixtures.Json(
            """{"scale": 0.5, "pageRanges": "1, 3-5, 8-"}"""));
        Assert.Equal(0.5f, ranged.Raster.Scale);
        Assert.Equal(
            [
                new RasterPdfPageRange(1, 1),
                new RasterPdfPageRange(3, 5),
                new RasterPdfPageRange(8, null),
            ],
            ranged.Raster.PageRanges);

        Assert.Equal(
            [
                new RasterPdfPageRange(1, 10),
                new RasterPdfPageRange(12, null),
            ],
            PdfDomain.ParsePageRanges("5-8, 1-3, 3-6, 10, 9, 12-"));

        string[] invalid =
        [
            """{"scale": 0.09}""",
            """{"scale": 2.01}""",
            """{"pageRanges": "0"}""",
            """{"pageRanges": "3-2"}""",
            """{"pageRanges": "1,,2"}""",
            """{"pageRanges": "1-2-3"}""",
        ];
        foreach (string parameters in invalid)
        {
            JsonNode node = CdpDomainFixtures.Json(parameters);
            Assert.Throws<DomainError>(() => PdfDomain.ParseOptions(node));
        }

        string tooManyParts = string.Join(",", Enumerable.Repeat("1", MaxPageRangeParts + 1));
        Assert.Throws<DomainError>(() => PdfDomain.ParsePageRanges(tooManyParts));
        string tooManyBytes = new('1', MaxPageRangesBytes + 1);
        Assert.Throws<DomainError>(() => PdfDomain.ParsePageRanges(tooManyBytes));
    }

    [Fact]
    public void Base64SizePreflightIsExactAndOverflowSafe()
    {
        Assert.Equal(0L, PdfDomain.Base64EncodedLength(0));
        Assert.Equal(4L, PdfDomain.Base64EncodedLength(1));
        Assert.Equal(4L, PdfDomain.Base64EncodedLength(2));
        Assert.Equal(4L, PdfDomain.Base64EncodedLength(3));
        Assert.Equal(8L, PdfDomain.Base64EncodedLength(4));
        Assert.Null(PdfDomain.Base64EncodedLength(long.MaxValue));
        long largestRaw = MaxBase64PdfBytes / 4 * 3;
        Assert.Equal(MaxBase64PdfBytes, PdfDomain.Base64EncodedLength(largestRaw));
        Assert.True(PdfDomain.Base64EncodedLength(largestRaw + 1) > MaxBase64PdfBytes);
    }

    [Fact]
    public async Task PrintToPdfReturnsPaginatedPdfWithRequestedMediaBox()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        ctx.GetSessionPageMut(session)!.SetViewport((100.0f, 80.0f));
        await ctx.GetSessionPageMut(session)!.NavigateAsync(
            "data:text/html,<html style='margin:0'><body style='margin:0;height:400px;"
            + "background:linear-gradient(red,blue)'></body></html>");

        JsonNode response = CdpDomainFixtures.Unwrap(await PdfDomain.PrintToPdfAsync(
            CdpDomainFixtures.Json(
                """
                {
                    "paperWidth": 4.0, "paperHeight": 6.0,
                    "marginTop": 0.5, "marginBottom": 0.5,
                    "marginLeft": 0.5, "marginRight": 0.5,
                    "printBackground": true
                }
                """),
            ctx,
            session));
        Assert.Equal("print-media-raster", response["obscuraPrintMode"]!.GetValue<string>());
        Assert.True(response["obscuraPrintBackground"]!.GetValue<bool>());

        byte[] bytes = Convert.FromBase64String(response["data"]!.GetValue<string>());
        Assert.StartsWith("%PDF-1.4", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        string text = Encoding.Latin1.GetString(bytes);
        Assert.Contains("/MediaBox [0 0 288.000 432.000]", text, StringComparison.Ordinal);
        Assert.True(
            Occurrences(text, "/Subtype /Image") >= 2,
            "400px tall fixture should paginate at this printable aspect ratio");

        JsonNode streamed = CdpDomainFixtures.Unwrap(await PdfDomain.PrintToPdfAsync(
            CdpDomainFixtures.Json(
                """
                {
                    "transferMode": "ReturnAsStream",
                    "landscape": false,
                    "displayHeaderFooter": false,
                    "headerTemplate": "",
                    "footerTemplate": "",
                    "printBackground": false,
                    "scale": 1,
                    "paperWidth": 8.5,
                    "paperHeight": 11,
                    "marginTop": 0,
                    "marginBottom": 0,
                    "marginLeft": 0,
                    "marginRight": 0,
                    "pageRanges": "",
                    "preferCSSPageSize": false,
                    "generateTaggedPDF": true,
                    "generateDocumentOutline": false
                }
                """),
            ctx,
            session));
        Assert.Equal(string.Empty, streamed["data"]!.GetValue<string>());
        Assert.False(streamed["obscuraPrintBackground"]!.GetValue<bool>());
        Assert.False(streamed["obscuraRequestedPrintBackground"]!.GetValue<bool>());
        Assert.True(streamed["obscuraCapabilities"]!["honorsPrintBackground"]!.GetValue<bool>());
        Assert.True(streamed["obscuraCapabilities"]!["honorsScale"]!.GetValue<bool>());
        Assert.True(streamed["obscuraCapabilities"]!["honorsPrintMedia"]!.GetValue<bool>());
        Assert.True(streamed["obscuraCapabilities"]!["pageRanges"]!.GetValue<bool>());
        Assert.False(streamed["obscuraTaggedPDF"]!.GetValue<bool>());
        Assert.True(streamed["obscuraRequestedTaggedPDF"]!.GetValue<bool>());
        CdpDomainFixtures.AssertJson(
            """["generateTaggedPDF"]""",
            streamed["obscuraIgnoredOptions"]);

        string handle = streamed["stream"]!.GetValue<string>();
        var streamedBytes = new List<byte>();
        while (true)
        {
            JsonNode chunk = CdpDomainFixtures.Unwrap(await Io.HandleAsync(
                "read",
                CdpDomainFixtures.Json($$"""{"handle": "{{handle}}", "size": 1024}"""),
                ctx));
            streamedBytes.AddRange(Convert.FromBase64String(chunk["data"]!.GetValue<string>()));
            if (chunk["eof"]!.GetValue<bool>())
            {
                break;
            }
        }

        string streamedText = Encoding.Latin1.GetString([.. streamedBytes]);
        Assert.StartsWith("%PDF-1.4", streamedText, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", streamedText, StringComparison.Ordinal);
        Assert.True((await Io.HandleAsync(
            "close",
            CdpDomainFixtures.Json($$"""{"handle": "{{handle}}"}"""),
            ctx)).IsOk);
        Assert.False(
            (await Io.HandleAsync(
                "read",
                CdpDomainFixtures.Json($$"""{"handle": "{{handle}}"}"""),
                ctx)).IsOk,
            "closed PDF handles must release their buffer");

        JsonNode ranged = CdpDomainFixtures.Unwrap(await PdfDomain.PrintToPdfAsync(
            CdpDomainFixtures.Json(
                """
                {
                    "paperWidth": 4.0, "paperHeight": 6.0,
                    "marginTop": 0.5, "marginBottom": 0.5,
                    "marginLeft": 0.5, "marginRight": 0.5,
                    "scale": 1,
                    "pageRanges": "2"
                }
                """),
            ctx,
            session));
        string rangedText = Encoding.Latin1.GetString(
            Convert.FromBase64String(ranged["data"]!.GetValue<string>()));
        Assert.Contains("/Count 1", rangedText, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(rangedText, "/Subtype /Image"));
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
