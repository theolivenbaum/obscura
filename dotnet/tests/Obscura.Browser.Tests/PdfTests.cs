using System.Globalization;
using System.Text;
using Obscura.Dom;
using Xunit;

namespace Obscura.Browser.Tests;

/// <summary>
/// The xUnit port of the tests in <c>crates/obscura-browser/src/pdf.rs</c>.
/// </summary>
public sealed class PdfTests
{
    private static int FindBytes(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(needle);

    /// <summary>
    /// Decode the actual JPEG XObjects emitted into the PDF, rather than trusting
    /// pagination options or writer-internal page counters.
    /// </summary>
    private static List<RgbImage> PdfPageRasters(byte[] pdf)
    {
        List<RgbImage> pages = [];
        int cursor = 0;
        while (true)
        {
            int relative = FindBytes(pdf.AsSpan(cursor), "/Subtype /Image"u8);
            if (relative < 0)
            {
                break;
            }
            int imageObject = cursor + relative;
            int lengthKey = imageObject + FindBytes(pdf.AsSpan(imageObject), "/Length "u8);
            int digitsStart = lengthKey + "/Length ".Length;
            while (char.IsWhiteSpace((char)pdf[digitsStart]))
            {
                digitsStart += 1;
            }
            int digitsEnd = digitsStart;
            while (char.IsAsciiDigit((char)pdf[digitsEnd]))
            {
                digitsEnd += 1;
            }
            int length = int.Parse(
                Encoding.ASCII.GetString(pdf, digitsStart, digitsEnd - digitsStart),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture);
            int streamStart = digitsEnd
                + FindBytes(pdf.AsSpan(digitsEnd), "stream\n"u8)
                + "stream\n".Length;
            int streamEnd = streamStart + length;
            RgbImage raster = Assert.IsType<RgbImage>(
                RgbImage.Decode(pdf.AsSpan(streamStart, streamEnd - streamStart)));
            pages.Add(raster);
            cursor = streamEnd;
        }
        return pages;
    }

    private static bool ChannelNear((byte R, byte G, byte B) actual, (int R, int G, int B) expected) =>
        Math.Abs(actual.R - expected.R) <= 20
        && Math.Abs(actual.G - expected.G) <= 20
        && Math.Abs(actual.B - expected.B) <= 20;

    private static RasterPdfOptions ExactPageOptions() => new()
    {
        PrintBackground = true,
        PaperWidthIn = 100.0f / RasterPdfEncoder.PointsPerInch,
        PaperHeightIn = 80.0f / RasterPdfEncoder.PointsPerInch,
        MarginTopIn = 0.0f,
        MarginBottomIn = 0.0f,
        MarginLeftIn = 0.0f,
        MarginRightIn = 0.0f,
    };

    [Fact]
    public void OptionsRejectImpossibleMediaBoxes()
    {
        var options = new RasterPdfOptions { PaperWidthIn = 0.0f };
        Assert.Equal(
            RasterPdfErrorKind.InvalidPaperSize,
            Assert.Throws<RasterPdfException>(() => options.PageGeometry()).Kind);
        var margins = new RasterPdfOptions { MarginLeftIn = 5.0f, MarginRightIn = 5.0f };
        Assert.Equal(
            RasterPdfErrorKind.InvalidMargins,
            Assert.Throws<RasterPdfException>(() => margins.PageGeometry()).Kind);
    }

    [Fact]
    public void PaginationPreflightBoundsPagesAndRasterWork()
    {
        (_, _, float printableWidth, float printableHeight, _, _) =
            new RasterPdfOptions().PageGeometry();
        PaginationPlan ordinary = RasterPdfEncoder.PaginationPlanFor(
            1280.0f, 10_000.0f, printableWidth, printableHeight, 1.0f);
        Assert.True(ordinary.PageCount > 1);
        List<int> ordinaryPages = RasterPdfEncoder.SelectedPageIndices(ordinary.PageCount, []);
        RasterPdfEncoder.ValidateSelectedRasterWork(1280.0f, 10_000.0f, ordinary, ordinaryPages);

        PaginationPlan oversizedPage = RasterPdfEncoder.PaginationPlanFor(
            5_000.0f, 5_000.0f, printableWidth, printableHeight, 1.0f);
        List<int> oversizedSelection = RasterPdfEncoder.SelectedPageIndices(oversizedPage.PageCount, []);
        Assert.Equal(
            RasterPdfErrorKind.RasterWorkLimitExceeded,
            Assert.Throws<RasterPdfException>(() => RasterPdfEncoder.ValidateSelectedRasterWork(
                5_000.0f, 5_000.0f, oversizedPage, oversizedSelection)).Kind);

        PaginationPlan tooMuchTotal = RasterPdfEncoder.PaginationPlanFor(
            1_000.0f, 70_000.0f, printableWidth, printableHeight, 1.0f);
        List<int> tooMuchSelection = RasterPdfEncoder.SelectedPageIndices(tooMuchTotal.PageCount, []);
        Assert.Equal(
            RasterPdfErrorKind.RasterWorkLimitExceeded,
            Assert.Throws<RasterPdfException>(() => RasterPdfEncoder.ValidateSelectedRasterWork(
                1_000.0f, 70_000.0f, tooMuchTotal, tooMuchSelection)).Kind);

        PaginationPlan tooMany = RasterPdfEncoder.PaginationPlanFor(
            1_000.0f, 400_000.0f, printableWidth, printableHeight, 1.0f);
        RasterPdfException error = Assert.Throws<RasterPdfException>(
            () => RasterPdfEncoder.SelectedPageIndices(tooMany.PageCount, []));
        Assert.Equal(RasterPdfErrorKind.TooManyPages, error.Kind);
        Assert.Equal(RasterPdfEncoder.MaxPdfPages, error.Limit);
    }

    [Fact]
    public void SelectedRangesAloneDetermineOutputAndRasterBudgets()
    {
        (_, _, float printableWidth, float printableHeight, _, _) =
            new RasterPdfOptions().PageGeometry();
        PaginationPlan longPlan = RasterPdfEncoder.PaginationPlanFor(
            1_000.0f, 400_000.0f, printableWidth, printableHeight, 1.0f);
        Assert.True(longPlan.PageCount > RasterPdfEncoder.MaxPdfPages);
        List<int> selected = RasterPdfEncoder.SelectedPageIndices(
            longPlan.PageCount,
            [new RasterPdfPageRange(1, 1)]);
        Assert.Equal<int>([0], selected);
        RasterPdfEncoder.ValidateSelectedRasterWork(1_000.0f, 400_000.0f, longPlan, selected);

        RasterPdfException tooMany = Assert.Throws<RasterPdfException>(
            () => RasterPdfEncoder.SelectedPageIndices(
                longPlan.PageCount,
                [new RasterPdfPageRange(1, RasterPdfEncoder.MaxPdfPages + 1)]));
        Assert.Equal(RasterPdfErrorKind.TooManyPages, tooMany.Kind);
        Assert.Equal(RasterPdfEncoder.MaxPdfPages, tooMany.Limit);

        PaginationPlan basePlan = RasterPdfEncoder.PaginationPlanFor(
            800.0f, 2_000.0f, printableWidth, printableHeight, 1.0f);
        float impossibleHeight = basePlan.CssPageHeight * (RasterPdfEncoder.MaxPdfDocumentPages + 16.0f);
        RasterPdfException tooManyDocument = Assert.Throws<RasterPdfException>(
            () => RasterPdfEncoder.PaginationPlanFor(
                800.0f, impossibleHeight, printableWidth, printableHeight, 1.0f));
        Assert.Equal(RasterPdfErrorKind.TooManyPages, tooManyDocument.Kind);
        Assert.Equal(RasterPdfEncoder.MaxPdfDocumentPages, tooManyDocument.Limit);
    }

    [Fact]
    public void ScaleChangesCssPageSpanAndRejectsInvalidValues()
    {
        (_, _, float printableWidth, float printableHeight, _, _) =
            new RasterPdfOptions().PageGeometry();
        PaginationPlan normal = RasterPdfEncoder.PaginationPlanFor(
            800.0f, 2_000.0f, printableWidth, printableHeight, 1.0f);
        PaginationPlan enlarged = RasterPdfEncoder.PaginationPlanFor(
            800.0f, 2_000.0f, printableWidth, printableHeight, 2.0f);
        Assert.Equal(normal.PointsPerCssPixel * 2.0f, enlarged.PointsPerCssPixel);
        Assert.Equal(normal.CssPageHeight / 2.0f, enlarged.CssPageHeight);
        Assert.True(enlarged.PageCount >= normal.PageCount);
        Assert.Equal(
            RasterPdfErrorKind.InvalidScale,
            Assert.Throws<RasterPdfException>(() => RasterPdfEncoder.PaginationPlanFor(
                800.0f, 2_000.0f, printableWidth, printableHeight, 0.09f)).Kind);
    }

    [Fact]
    public void PageRangesClipDeduplicateAndPreserveDocumentOrder()
    {
        Assert.Equal<int>([0, 1, 2, 3], RasterPdfEncoder.SelectedPageIndices(4, []));
        Assert.Equal<int>(
            [0, 1, 2, 3, 4, 5],
            RasterPdfEncoder.SelectedPageIndices(
                6,
                [
                    new RasterPdfPageRange(3, 5),
                    new RasterPdfPageRange(1, 3),
                    new RasterPdfPageRange(5, null),
                ]));
        Assert.Equal<int>(
            [0, 1],
            RasterPdfEncoder.SelectedPageIndices(6, [new RasterPdfPageRange(null, 2)]));
        Assert.Equal(
            RasterPdfErrorKind.EmptyPageRange,
            Assert.Throws<RasterPdfException>(
                () => RasterPdfEncoder.SelectedPageIndices(3, [new RasterPdfPageRange(9, 12)])).Kind);
    }

    [Fact]
    public void RasterPdfRepeatsFixedContentAndAdvancesFlowOnEverySelectedPage()
    {
        using var page = new Page("pdf-fixed-page", BrowserContext.New("pdf-fixed"));
        page.SetViewport((100.0f, 80.0f));
        page.Js = PageFixtures.RuntimeFor(
            "https://example.test/pdf-fixed",
            """
            <html style="margin:0"><body style="margin:0;width:100px;height:200px">
                <div style="position:fixed;z-index:5;left:0;top:0;width:20px;height:10px;background:#111"></div>
                <div style="height:80px;background:#e02020"></div>
                <div style="height:80px;background:#20c040"></div>
                <div style="height:40px;background:#2050e0"></div>
            </body></html>
            """,
            (100.0f, 80.0f));

        RasterPdfOptions options = ExactPageOptions();
        byte[] pdf = page.RasterPdf(options);
        Assert.Contains(
            "/MediaBox [0 0 100.000 80.000]",
            Encoding.Latin1.GetString(pdf),
            StringComparison.Ordinal);
        List<RgbImage> rasters = PdfPageRasters(pdf);
        Assert.Equal(3, rasters.Count);
        Assert.Equal((100u, 80u), (rasters[0].Width, rasters[0].Height));
        Assert.Equal((100u, 80u), (rasters[1].Width, rasters[1].Height));
        Assert.Equal(
            (100u, 40u),
            (rasters[2].Width, rasters[2].Height));
        for (int index = 0; index < rasters.Count; index++)
        {
            Assert.True(
                ChannelNear(rasters[index].GetPixel(5, 5), (17, 17, 17)),
                $"fixed header missing from decoded page {index + 1}");
        }
        (int R, int G, int B)[] expectedFlow = [(224, 32, 32), (32, 192, 64), (32, 80, 224)];
        for (int index = 0; index < expectedFlow.Length; index++)
        {
            RgbImage raster = rasters[index];
            Assert.True(
                ChannelNear(raster.GetPixel(raster.Width / 2, raster.Height / 2), expectedFlow[index]),
                $"ordinary flow did not advance on page {index + 1}");
        }
        Assert.Equal(
            (0.0f, 0.0f),
            page.Js!.ScrollOffset);

        RasterPdfOptions ranged = ExactPageOptions();
        ranged.PageRanges = [new RasterPdfPageRange(2, 3)];
        List<RgbImage> selected = PdfPageRasters(page.RasterPdf(ranged));
        Assert.Equal(2, selected.Count);
        Assert.True(ChannelNear(selected[0].GetPixel(5, 5), (17, 17, 17)));
        Assert.True(ChannelNear(selected[1].GetPixel(5, 5), (17, 17, 17)));
        Assert.True(ChannelNear(selected[0].GetPixel(50, 40), (32, 192, 64)));
        Assert.True(ChannelNear(selected[1].GetPixel(50, 20), (32, 80, 224)));
    }

    [Fact]
    public void RasterPdfSelectsPrintMediaAndRestoresScreenRenderState()
    {
        using var page = new Page("pdf-media-page", BrowserContext.New("pdf-media"));
        page.SetViewport((100.0f, 80.0f));
        page.Js = PageFixtures.RuntimeFor(
            "https://example.test/pdf-media",
            """
            <!doctype html><html><head>
                <style>
                    html,body{margin:0;width:100px;height:80px;background:#101010}
                    #print-marker,#screen-marker{display:none}
                    @media print {
                        body{background:#2050e0}
                    }
                    @media screen {
                        body{background:#e02020}
                    }
                </style>
                <style media="print">
                    #print-marker{display:block;position:absolute;left:60px;top:10px;
                                  width:30px;height:30px;background:#f0d020}
                </style>
                <style media="screen">
                    #screen-marker{display:block;position:absolute;left:5px;top:5px;
                                   width:10px;height:10px;background:#20c040}
                </style>
            </head><body><div id="print-marker"></div><div id="screen-marker"></div></body></html>
            """,
            (100.0f, 80.0f));

        byte[] screenBefore = Assert.IsType<byte[]>(page.Screenshot((100.0f, 80.0f)));
        RgbImage screenBeforePixels = Assert.IsType<RgbImage>(RgbImage.Decode(screenBefore));
        Assert.Equal((224, 32, 32), ToInts(screenBeforePixels.GetPixel(50, 60)));
        Assert.Equal((32, 192, 64), ToInts(screenBeforePixels.GetPixel(8, 8)));
        Assert.Equal((224, 32, 32), ToInts(screenBeforePixels.GetPixel(70, 20)));

        List<RgbImage> pages = PdfPageRasters(page.RasterPdf(ExactPageOptions()));
        Assert.Single(pages);
        RgbImage printed = pages[0];
        Assert.True(ChannelNear(printed.GetPixel(50, 60), (32, 80, 224)), "@media print body color missing");
        Assert.True(
            ChannelNear(printed.GetPixel(70, 20), (240, 208, 32)),
            "media=print stylesheet marker missing");
        Assert.True(
            ChannelNear(printed.GetPixel(8, 8), (32, 80, 224)),
            "media=screen marker leaked into print");

        byte[] screenAfter = Assert.IsType<byte[]>(page.Screenshot((100.0f, 80.0f)));
        Assert.True(
            screenAfter.AsSpan().SequenceEqual(screenBefore),
            "temporary print cascade must not poison retained screen geometry or stylesheet cache");
    }

    private static (int R, int G, int B) ToInts((byte R, byte G, byte B) pixel) =>
        (pixel.R, pixel.G, pixel.B);

    [Fact]
    public void WriterEmitsXrefAndOneImagePerPage()
    {
        byte[] pdf = RasterPdfEncoder.EncodePdfPages(1, 612.0f, 792.0f, 36.0f, 36.0f, 720.0f, _ =>
            new RasterPage
            {
                Rgb = RgbImage.FromPixel(2, 3, (10, 20, 30)),
                DrawWidthPt = 100.0f,
                DrawHeightPt = 150.0f,
            });
        Assert.StartsWith("%PDF-1.4", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
        string text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/Count 1", text, StringComparison.Ordinal);
        Assert.Contains("/Subtype /Image", text, StringComparison.Ordinal);
        Assert.Contains("xref\n0 6", text, StringComparison.Ordinal);
        int startxref = int.Parse(
            text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length)..]
                .Split('\n')[0],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture);
        Assert.StartsWith("xref\n", Encoding.Latin1.GetString(pdf, startxref, 5), StringComparison.Ordinal);
        int objectOneOffset = int.Parse(
            text.Split("xref\n0 6\n")[1].Split('\n')[1][..10],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture);
        Assert.StartsWith(
            "1 0 obj\n",
            Encoding.Latin1.GetString(pdf, objectOneOffset, 8),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// PORT DEVIATION: Rust proves the previous page's raster is gone with a
    /// <c>Weak</c> probe. A managed equivalent would depend on when the GC runs, so
    /// <see cref="RasterPage.Release"/> makes the same invariant - the previous
    /// page's raster is released before the next capture is requested - explicit
    /// and deterministic.
    /// </summary>
    [Fact]
    public void PageRastersAreReleasedBeforeCapturingTheNextPage()
    {
        RasterPage? previous = null;
        byte[] pdf = RasterPdfEncoder.EncodePdfPages(4, 612.0f, 792.0f, 36.0f, 36.0f, 720.0f, index =>
        {
            if (previous is { } earlier)
            {
                Assert.True(
                    earlier.Released,
                    $"page {index} was requested while the prior raster was still retained");
            }
            var page = new RasterPage
            {
                Rgb = RgbImage.FromPixel(8, 8, ((byte)index, 0, 0)),
                DrawWidthPt = 100.0f,
                DrawHeightPt = 100.0f,
            };
            previous = page;
            return page;
        });
        Assert.Equal(
            4,
            Encoding.Latin1.GetString(pdf).Split("/Subtype /Image").Length - 1);
    }

    [Fact]
    public void WriterEnforcesTheOutputLimitWhileEncodingTheImageStream()
    {
        var writer = new PdfWriter(5, 600);
        writer.WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>"u8.ToArray());
        writer.WriteObject(2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>"u8.ToArray());
        RgbImage noisy = RgbImage.FromFunction(128, 128, (x, y) => (
            (byte)(x * 37),
            (byte)(y * 53),
            (byte)((x + y) * 71)));
        Assert.Equal(
            RasterPdfErrorKind.OutputLimitExceeded,
            Assert.Throws<RasterPdfException>(() => writer.WriteRgbImage(5, noisy)).Kind);
        Assert.True(writer.Length <= 600);
    }
}
