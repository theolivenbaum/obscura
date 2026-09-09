// Bounded raster-backed PDF export over the retained document-space painter.
//
// This deliberately does not claim CSS paged-media support. It preserves the
// print-media layout, fits the full document width into the printable area, and
// slices that immutable layout vertically across PDF pages.
using System.Globalization;
using System.Text;
using Obscura.Render;
using Obscura.Render.Css;

namespace Obscura.Browser;

/// <summary>One inclusive, one-based page range.</summary>
/// <param name="Start">One-based inclusive first page. Null means the first page.</param>
/// <param name="End">One-based inclusive last page. Null means the final page.</param>
public readonly record struct RasterPdfPageRange(int? Start, int? End);

public sealed record RasterPdfOptions
{
    public bool Landscape { get; set; }

    public bool PrintBackground { get; set; }

    public float Scale { get; set; } = 1.0f;

    public IReadOnlyList<RasterPdfPageRange> PageRanges { get; set; } = [];

    public float PaperWidthIn { get; set; } = 8.5f;

    public float PaperHeightIn { get; set; } = 11.0f;

    // CDP's defaults are one centimetre.
    public float MarginTopIn { get; set; } = 0.3937f;

    public float MarginBottomIn { get; set; } = 0.3937f;

    public float MarginLeftIn { get; set; } = 0.3937f;

    public float MarginRightIn { get; set; } = 0.3937f;

    internal (float PageWidth, float PageHeight, float PrintableWidth, float PrintableHeight, float Left, float Bottom)
        PageGeometry()
    {
        float[] values = [PaperWidthIn, PaperHeightIn];
        foreach (float value in values)
        {
            if (!float.IsFinite(value) || value <= 0.0f || value > RasterPdfEncoder.MaxPaperInches)
            {
                throw new RasterPdfException(RasterPdfErrorKind.InvalidPaperSize);
            }
        }
        float[] margins = [MarginTopIn, MarginBottomIn, MarginLeftIn, MarginRightIn];
        foreach (float value in margins)
        {
            if (!float.IsFinite(value) || value < 0.0f)
            {
                throw new RasterPdfException(RasterPdfErrorKind.InvalidMargins);
            }
        }
        (float paperWidthIn, float paperHeightIn) = Landscape
            ? (PaperHeightIn, PaperWidthIn)
            : (PaperWidthIn, PaperHeightIn);
        float pageWidth = paperWidthIn * RasterPdfEncoder.PointsPerInch;
        float pageHeight = paperHeightIn * RasterPdfEncoder.PointsPerInch;
        float left = MarginLeftIn * RasterPdfEncoder.PointsPerInch;
        float bottom = MarginBottomIn * RasterPdfEncoder.PointsPerInch;
        float printableWidth = pageWidth - ((MarginLeftIn + MarginRightIn) * RasterPdfEncoder.PointsPerInch);
        float printableHeight = pageHeight - ((MarginTopIn + MarginBottomIn) * RasterPdfEncoder.PointsPerInch);
        if (printableWidth <= 0.0f || printableHeight <= 0.0f)
        {
            throw new RasterPdfException(RasterPdfErrorKind.InvalidMargins);
        }
        return (pageWidth, pageHeight, printableWidth, printableHeight, left, bottom);
    }
}

public enum RasterPdfErrorKind
{
    InvalidPaperSize,
    InvalidMargins,
    InvalidScale,
    EmptyPageRange,
    NoRenderableDocument,
    TooManyPages,
    RasterWorkLimitExceeded,
    CaptureFailed,
    ImageDecode,
    ImageEncode,
    OutputLimitExceeded,
}

public sealed class RasterPdfException : Exception
{
    public RasterPdfException(RasterPdfErrorKind kind)
        : base(MessageFor(kind, null, 0)) => Kind = kind;

    public RasterPdfException(RasterPdfErrorKind kind, int limit)
        : base(MessageFor(kind, null, limit))
    {
        Kind = kind;
        Limit = limit;
    }

    public RasterPdfException(RasterPdfErrorKind kind, string detail)
        : base(MessageFor(kind, detail, 0)) => Kind = kind;

    public RasterPdfErrorKind Kind { get; }

    /// <summary>Meaningful only for <see cref="RasterPdfErrorKind.TooManyPages"/>.</summary>
    public int Limit { get; }

    private static string MessageFor(RasterPdfErrorKind kind, string? detail, int limit) => kind switch
    {
        RasterPdfErrorKind.InvalidPaperSize =>
            "PDF paper dimensions must be finite and between 0 and 200 inches",
        RasterPdfErrorKind.InvalidMargins =>
            "PDF margins must be finite, non-negative, and leave a printable area",
        RasterPdfErrorKind.InvalidScale => "PDF scale must be finite and between 0.1 and 2",
        RasterPdfErrorKind.EmptyPageRange => "PDF page ranges select no pages from this document",
        RasterPdfErrorKind.NoRenderableDocument => "the page has no retained renderable document",
        RasterPdfErrorKind.TooManyPages =>
            $"PDF pagination would exceed the {limit.ToString(CultureInfo.InvariantCulture)}-page safety limit",
        RasterPdfErrorKind.RasterWorkLimitExceeded =>
            "PDF raster work would exceed the bounded page or document pixel budget",
        RasterPdfErrorKind.CaptureFailed => $"document-space PDF capture failed: {detail}",
        RasterPdfErrorKind.ImageDecode => $"PDF raster image decoding failed: {detail}",
        RasterPdfErrorKind.ImageEncode => $"PDF JPEG encoding failed: {detail}",
        RasterPdfErrorKind.OutputLimitExceeded => "encoded PDF would exceed the 64 MiB safety limit",
        _ => kind.ToString(),
    };
}

/// <summary>One rasterized PDF page, released before the next one is captured.</summary>
internal sealed class RasterPage
{
    internal required RgbImage Rgb { get; init; }

    internal required float DrawWidthPt { get; init; }

    internal required float DrawHeightPt { get; init; }

    /// <summary>
    /// Rust proves the previous page's raster is gone with a <c>Weak</c> probe; the
    /// port makes the release explicit so the invariant is deterministic rather than
    /// dependent on when the GC runs.
    /// </summary>
    internal Action? LifetimeProbe { get; init; }

    internal bool Released { get; private set; }

    internal void Release()
    {
        Released = true;
        LifetimeProbe?.Invoke();
    }
}

internal readonly record struct PaginationPlan(
    float PointsPerCssPixel,
    float CssPageHeight,
    int PageCount);

internal static class RasterPdfEncoder
{
    internal const float PointsPerInch = 72.0f;
    internal const float MaxPaperInches = 200.0f;
    internal const int MaxPdfPages = 250;

    // Page ranges may select a small bounded subset from a much longer document.
    // Keep the arithmetic/index space finite without charging unselected pages
    // against the output-page limit.
    internal const int MaxPdfDocumentPages = 1_000_000;
    internal const ulong MaxPdfPagePixels = 16UL * 1024 * 1024;
    internal const ulong MaxPdfTotalRasterPixels = 64UL * 1024 * 1024;
    internal const int MaxPdfOutputBytes = 64 * 1024 * 1024;

    internal static PaginationPlan PaginationPlanFor(
        float contentWidth,
        float contentHeight,
        float printableWidth,
        float printableHeight,
        float scale)
    {
        if (!float.IsFinite(scale) || scale < 0.1f || scale > 2.0f)
        {
            throw new RasterPdfException(RasterPdfErrorKind.InvalidScale);
        }
        float pointsPerCssPixel = printableWidth / contentWidth * scale;
        float cssPageHeight = printableHeight / pointsPerCssPixel;
        if (!float.IsFinite(pointsPerCssPixel)
            || pointsPerCssPixel <= 0.0f
            || !float.IsFinite(cssPageHeight)
            || cssPageHeight <= 0.0f)
        {
            throw new RasterPdfException(RasterPdfErrorKind.RasterWorkLimitExceeded);
        }

        float pageCountValue = MathF.Max(MathF.Ceiling(contentHeight / cssPageHeight), 1.0f);
        if (!float.IsFinite(pageCountValue) || pageCountValue > MaxPdfDocumentPages)
        {
            throw new RasterPdfException(RasterPdfErrorKind.TooManyPages, MaxPdfDocumentPages);
        }

        return new PaginationPlan(pointsPerCssPixel, cssPageHeight, (int)pageCountValue);
    }

    internal static void ValidateSelectedRasterWork(
        float contentWidth,
        float contentHeight,
        PaginationPlan plan,
        IReadOnlyList<int> selectedPages)
    {
        float pixelWidthValue = MathF.Ceiling(contentWidth);
        if (!float.IsFinite(pixelWidthValue)
            || pixelWidthValue <= 0.0f
            || pixelWidthValue > CaptureLimits.MaxCaptureDimension)
        {
            throw new RasterPdfException(RasterPdfErrorKind.RasterWorkLimitExceeded);
        }
        ulong pixelWidth = (ulong)pixelWidthValue;
        ulong totalPixels = 0;
        foreach (int pageIndex in selectedPages)
        {
            if (pageIndex >= plan.PageCount)
            {
                throw new RasterPdfException(RasterPdfErrorKind.EmptyPageRange);
            }
            float y = pageIndex * plan.CssPageHeight;
            float sliceHeight = MathF.Ceiling(MathF.Min(contentHeight - y, plan.CssPageHeight));
            if (!float.IsFinite(sliceHeight)
                || sliceHeight <= 0.0f
                || sliceHeight > CaptureLimits.MaxCaptureDimension)
            {
                throw new RasterPdfException(RasterPdfErrorKind.RasterWorkLimitExceeded);
            }
            ulong pagePixels = pixelWidth * (ulong)sliceHeight;
            if (pagePixels > MaxPdfPagePixels)
            {
                throw new RasterPdfException(RasterPdfErrorKind.RasterWorkLimitExceeded);
            }
            totalPixels += pagePixels;
            if (totalPixels > MaxPdfTotalRasterPixels)
            {
                throw new RasterPdfException(RasterPdfErrorKind.RasterWorkLimitExceeded);
            }
        }
    }

    internal static List<int> SelectedPageIndices(int pageCount, IReadOnlyList<RasterPdfPageRange> ranges)
    {
        if (pageCount == 0)
        {
            throw new RasterPdfException(RasterPdfErrorKind.EmptyPageRange);
        }
        if (ranges.Count == 0)
        {
            if (pageCount > MaxPdfPages)
            {
                throw new RasterPdfException(RasterPdfErrorKind.TooManyPages, MaxPdfPages);
            }
            return [.. Enumerable.Range(0, pageCount)];
        }
        SortedSet<int> selected = [];
        foreach (RasterPdfPageRange range in ranges)
        {
            int start = range.Start ?? 1;
            int end = range.End ?? pageCount;
            if (start <= 0 || end <= 0 || start > end)
            {
                throw new RasterPdfException(RasterPdfErrorKind.EmptyPageRange);
            }
            if (start > pageCount)
            {
                continue;
            }
            end = Math.Min(end, pageCount);
            int span = end - start + 1;
            if (span > MaxPdfPages)
            {
                throw new RasterPdfException(RasterPdfErrorKind.TooManyPages, MaxPdfPages);
            }
            for (int page = start; page <= end; page++)
            {
                selected.Add(page - 1);
                if (selected.Count > MaxPdfPages)
                {
                    throw new RasterPdfException(RasterPdfErrorKind.TooManyPages, MaxPdfPages);
                }
            }
        }
        if (selected.Count == 0)
        {
            throw new RasterPdfException(RasterPdfErrorKind.EmptyPageRange);
        }
        return [.. selected];
    }

    internal static byte[] EncodePdfPages(
        int pageCount,
        float pageWidth,
        float pageHeight,
        float left,
        float bottom,
        float printableHeight,
        Func<int, RasterPage> pageSource)
    {
        long objectCountValue = 2L + ((long)pageCount * 3L);
        if (objectCountValue > int.MaxValue)
        {
            throw new RasterPdfException(RasterPdfErrorKind.OutputLimitExceeded);
        }
        int objectCount = (int)objectCountValue;
        var writer = new PdfWriter(objectCount, MaxPdfOutputBytes);
        writer.WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>"u8.ToArray());

        string kids = string.Join(
            ' ',
            Enumerable.Range(0, pageCount).Select(index =>
                $"{(3 + (index * 3)).ToString(CultureInfo.InvariantCulture)} 0 R"));
        string pagesDictionary =
            $"<< /Type /Pages /Count {pageCount.ToString(CultureInfo.InvariantCulture)} /Kids [{kids}] >>";
        writer.WriteObject(2, Encoding.ASCII.GetBytes(pagesDictionary));

        for (int index = 0; index < pageCount; index++)
        {
            RasterPage page = pageSource(index);
            try
            {
                int pageId = 3 + (index * 3);
                int contentId = pageId + 1;
                int imageId = pageId + 2;
                string pageDictionary =
                    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 "
                    + Fixed3(pageWidth) + " " + Fixed3(pageHeight)
                    + "] /Resources << /XObject << /Im0 "
                    + imageId.ToString(CultureInfo.InvariantCulture)
                    + " 0 R >> >> /Contents "
                    + contentId.ToString(CultureInfo.InvariantCulture)
                    + " 0 R >>";
                writer.WriteObject(pageId, Encoding.ASCII.GetBytes(pageDictionary));

                float drawY = bottom + printableHeight - page.DrawHeightPt;
                string commands =
                    "q\n" + Fixed3(page.DrawWidthPt) + " 0 0 " + Fixed3(page.DrawHeightPt)
                    + " " + Fixed3(left) + " " + Fixed3(drawY) + " cm\n/Im0 Do\nQ\n";
                string content =
                    "<< /Length " + commands.Length.ToString(CultureInfo.InvariantCulture)
                    + " >>\nstream\n" + commands + "endstream";
                writer.WriteObject(contentId, Encoding.ASCII.GetBytes(content));
                writer.WriteRgbImage(imageId, page.Rgb);
            }
            finally
            {
                // The page, including its decoded RGB raster, is released here before
                // the next page is captured. Only the bounded final PDF survives.
                page.Release();
            }
        }

        return writer.Finish();
    }

    internal static string Fixed3(float value) =>
        value.ToString("0.000", CultureInfo.InvariantCulture);
}

internal sealed class PdfWriter
{
    private readonly List<byte> _output = [];
    private readonly int[] _offsets;
    private readonly int _limit;

    internal PdfWriter(int objectCount, int limit)
    {
        _offsets = new int[objectCount + 1];
        _limit = limit;
        Append([0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A, 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);
    }

    internal int Length => _output.Count;

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        long newLength = (long)_output.Count + bytes.Length;
        if (newLength > _limit)
        {
            throw new RasterPdfException(RasterPdfErrorKind.OutputLimitExceeded);
        }
        _output.AddRange(bytes);
    }

    internal void WriteObject(int id, byte[] body)
    {
        _offsets[id] = _output.Count;
        Append(Encoding.ASCII.GetBytes($"{id.ToString(CultureInfo.InvariantCulture)} 0 obj\n"));
        Append(body);
        Append("\nendobj\n"u8);
    }

    internal void WriteRgbImage(int id, RgbImage rgb)
    {
        const int LengthDigits = 20;
        // Encode first so the output limit is enforced before a large stream is
        // appended: Rust streams the encoder into the buffer and trips the same cap
        // mid-encode.
        byte[]? jpeg;
        try
        {
            jpeg = rgb.EncodeJpeg(90);
        }
        catch (Exception error)
        {
            throw new RasterPdfException(RasterPdfErrorKind.ImageEncode, error.Message);
        }
        if (jpeg is null)
        {
            throw new RasterPdfException(RasterPdfErrorKind.ImageEncode, "the encoder produced no data");
        }

        _offsets[id] = _output.Count;
        Append(Encoding.ASCII.GetBytes(
            $"{id.ToString(CultureInfo.InvariantCulture)} 0 obj\n"
            + "<< /Type /XObject /Subtype /Image /Width "
            + rgb.Width.ToString(CultureInfo.InvariantCulture)
            + " /Height " + rgb.Height.ToString(CultureInfo.InvariantCulture)
            + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length "));
        string length = jpeg.Length.ToString(CultureInfo.InvariantCulture).PadLeft(LengthDigits, '0');
        if (length.Length != LengthDigits)
        {
            throw new RasterPdfException(RasterPdfErrorKind.OutputLimitExceeded);
        }
        Append(Encoding.ASCII.GetBytes(length + " >>\nstream\n"));
        Append(jpeg);
        Append("\nendstream\nendobj\n"u8);
    }

    internal byte[] Finish()
    {
        int objectCount = _offsets.Length - 1;
        int xrefOffset = _output.Count;
        Append(Encoding.ASCII.GetBytes(
            $"xref\n0 {(objectCount + 1).ToString(CultureInfo.InvariantCulture)}\n0000000000 65535 f \n"));
        for (int index = 1; index < _offsets.Length; index++)
        {
            Append(Encoding.ASCII.GetBytes(
                _offsets[index].ToString(CultureInfo.InvariantCulture).PadLeft(10, '0') + " 00000 n \n"));
        }
        Append(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {(objectCount + 1).ToString(CultureInfo.InvariantCulture)} /Root 1 0 R >>\n"
            + $"startxref\n{xrefOffset.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n"));
        return [.. _output];
    }
}

public sealed partial class Page
{
    /// <summary>
    /// Export the current print-media layout as a paginated raster PDF.
    /// </summary>
    /// <remarks>
    /// The full document width is scaled uniformly into the printable width; vertical
    /// slices become pages. Print media rules participate in normal cascade and
    /// layout, but CSS paged media, headers and footers remain outside this
    /// raster-backed exporter.
    /// </remarks>
    public byte[] RasterPdf(RasterPdfOptions options) =>
        RasterPdfWithAnimationSample(options, LiveAnimationSample());

    public byte[] RasterPdfAtAnimationTime(
        RasterPdfOptions options,
        AnimationSampleTime animationSampleTime) =>
        RasterPdfWithAnimationSample(
            options,
            new AnimationSample(animationSampleTime, AnimationSampleMode.LocalOverride));

    public byte[] RasterPdfWithAnimationSample(RasterPdfOptions options, AnimationSample animationSample)
    {
        ArgumentNullException.ThrowIfNull(options);
        (float pageWidth, float pageHeight, float printableWidth, float printableHeight, float left, float bottom) =
            options.PageGeometry();
        if (Js is not { } js)
        {
            throw new RasterPdfException(RasterPdfErrorKind.NoRenderableDocument);
        }
        if (!js.SetAnimationSample(animationSample))
        {
            throw new RasterPdfException(RasterPdfErrorKind.NoRenderableDocument);
        }
        CssMediaType previousMedia = js.SetRenderMedia(CssMediaType.Print);
        try
        {
            if (js.PreparedContentSize() is not { } size)
            {
                throw new RasterPdfException(RasterPdfErrorKind.NoRenderableDocument);
            }
            (float contentWidth, float contentHeight) = size;
            if (!float.IsFinite(contentWidth) || !float.IsFinite(contentHeight)
                || contentWidth <= 0.0f || contentHeight <= 0.0f)
            {
                throw new RasterPdfException(RasterPdfErrorKind.NoRenderableDocument);
            }

            PaginationPlan plan = RasterPdfEncoder.PaginationPlanFor(
                contentWidth,
                contentHeight,
                printableWidth,
                printableHeight,
                options.Scale);
            List<int> selectedPages = RasterPdfEncoder.SelectedPageIndices(plan.PageCount, options.PageRanges);
            RasterPdfEncoder.ValidateSelectedRasterWork(contentWidth, contentHeight, plan, selectedPages);

            return RasterPdfEncoder.EncodePdfPages(
                selectedPages.Count,
                pageWidth,
                pageHeight,
                left,
                bottom,
                printableHeight,
                outputPageIndex =>
                {
                    int pageIndex = selectedPages[outputPageIndex];
                    float y = pageIndex * plan.CssPageHeight;
                    float sliceHeight = MathF.Min(contentHeight - y, plan.CssPageHeight);
                    (byte[]? png, CaptureError? error) = js.ScreenshotPreparedRegionAtScrollWithBackgrounds(
                        CaptureRegion.New(0.0f, y, contentWidth, sliceHeight, 1.0f),
                        (0.0f, y),
                        options.PrintBackground);
                    if (png is null)
                    {
                        throw new RasterPdfException(
                            RasterPdfErrorKind.CaptureFailed,
                            (error ?? CaptureError.PaintFailed).ToString());
                    }
                    RgbImage? decoded = RgbImage.Decode(png);
                    if (decoded is null)
                    {
                        throw new RasterPdfException(
                            RasterPdfErrorKind.ImageDecode,
                            "the page capture could not be decoded");
                    }
                    return new RasterPage
                    {
                        Rgb = decoded,
                        DrawWidthPt = contentWidth * plan.PointsPerCssPixel,
                        DrawHeightPt = sliceHeight * plan.PointsPerCssPixel,
                    };
                });
        }
        finally
        {
            js.SetRenderMedia(previousMedia);
        }
    }
}
