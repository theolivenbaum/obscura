using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Obscura.Render;

namespace Obscura.Cdp.Domains;

/// <summary>
/// <c>Page.printToPDF</c>, backed by the raster PDF exporter.
/// </summary>
/// <remarks>
/// The output honours paper dimensions, margins, landscape, scale, backgrounds and page ranges.
/// It is raster-backed, so it has no selectable text, no tagged structure, no outline and no CSS
/// paged-media behaviour; the handler rejects the options that would imply otherwise and reports
/// the rest truthfully in <c>obscuraCapabilities</c>.
/// </remarks>
public static class PdfDomain
{
    private const long MaxBase64PdfBytes = 64L * 1024 * 1024;
    private const int MaxPageRangesBytes = 16 * 1024;
    private const int MaxPageRangeParts = 512;

    public static long? Base64EncodedLength(long rawLength)
    {
        if (rawLength > long.MaxValue - 2)
        {
            return null;
        }

        long groups = (rawLength + 2) / 3;
        return groups > long.MaxValue / 4 ? null : groups * 4;
    }

    public enum PdfTransferMode
    {
        ReturnAsBase64,
        ReturnAsStream,
    }

    public sealed record ParsedPdfOptions(
        Obscura.Browser.RasterPdfOptions Raster,
        PdfTransferMode TransferMode,
        bool RequestedPrintBackground,
        bool RequestedTaggedPdf);

    private static float Number(JsonNode? parameters, string name, float fallback)
    {
        if (!DomainParams.Has(parameters, name))
        {
            return fallback;
        }

        double? value = parameters.Get(name).AsF64();
        return value is { } number && double.IsFinite(number)
            ? (float)number
            : throw new DomainError($"Invalid parameters: {name} must be a finite number");
    }

    private static bool Boolean(JsonNode? parameters, string name, bool fallback)
    {
        if (!DomainParams.Has(parameters, name))
        {
            return fallback;
        }

        return parameters.Get(name).AsBool()
            ?? throw new DomainError($"Invalid parameters: {name} must be a boolean");
    }

    public static List<Obscura.Browser.RasterPdfPageRange> ParsePageRanges(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Trim();
        if (value.Length == 0)
        {
            return [];
        }

        if (Encoding.UTF8.GetByteCount(value) > MaxPageRangesBytes)
        {
            throw new DomainError(
                $"Invalid parameters: pageRanges exceeds the {MaxPageRangesBytes}-byte limit");
        }

        var ranges = new List<Obscura.Browser.RasterPdfPageRange>();
        string[] parts = value.Split(',');
        for (int index = 0; index < parts.Length; index++)
        {
            if (index >= MaxPageRangeParts)
            {
                throw new DomainError(
                    $"Invalid parameters: pageRanges exceeds the {MaxPageRangeParts}-range limit");
            }

            string part = parts[index].Trim();
            if (part.Length == 0)
            {
                throw new DomainError("Invalid parameters: pageRanges contains an empty range");
            }

            int? start;
            int? end;
            int separator = part.IndexOf('-', StringComparison.Ordinal);
            if (separator >= 0)
            {
                string head = part[..separator];
                string tail = part[(separator + 1)..];
                if (tail.Contains('-', StringComparison.Ordinal))
                {
                    throw new DomainError(
                        $"Invalid parameters: pageRanges has invalid range {Quote(part)}");
                }

                start = Page(head);
                end = Page(tail);
            }
            else
            {
                start = Page(part)
                    ?? throw new DomainError(
                        "Invalid parameters: pageRanges contains an empty page");
                end = start;
            }

            if (start is null && end is null)
            {
                throw new DomainError("Invalid parameters: pageRanges range '-' is empty");
            }

            if (start is { } from && end is { } to && from > to)
            {
                throw new DomainError(
                    $"Invalid parameters: pageRanges range {Quote(part)} is descending");
            }

            ranges.Add(new Obscura.Browser.RasterPdfPageRange(start, end));
        }

        // Normalize before crossing the CDP/browser boundary. This bounds later selection work and
        // makes repeated or overlapping user ranges occupy one entry while preserving Chromium's
        // document-order, print-once semantics.
        List<Obscura.Browser.RasterPdfPageRange> sorted =
            [.. ranges.OrderBy(range => range.Start ?? 1).ThenBy(range => range.End ?? int.MaxValue)];
        var normalized = new List<Obscura.Browser.RasterPdfPageRange>();
        foreach (Obscura.Browser.RasterPdfPageRange range in sorted)
        {
            int start = range.Start ?? 1;
            if (normalized.Count > 0)
            {
                Obscura.Browser.RasterPdfPageRange previous = normalized[^1];
                int previousEnd = previous.End ?? int.MaxValue;
                if (start <= SaturatingAdd(previousEnd, 1))
                {
                    int? merged = previous.End is { } left && range.End is { } right
                        ? Math.Max(left, right)
                        : null;
                    normalized[^1] = previous with { End = merged };
                    continue;
                }
            }

            normalized.Add(new Obscura.Browser.RasterPdfPageRange(start, range.End));
        }

        return normalized;

        static int SaturatingAdd(int left, int right) =>
            right > int.MaxValue - left ? int.MaxValue : left + right;

        static string Quote(string text) => CdpJson.String(text);

        static int? Page(string text)
        {
            text = text.Trim();
            if (text.Length == 0)
            {
                return null;
            }

            // Rust parses into `usize`; a page number past `int.MaxValue` cannot select anything
            // in a document this exporter can produce, so it saturates rather than overflowing.
            if (!ulong.TryParse(
                    text,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out ulong parsed)
                || parsed == 0)
            {
                throw new DomainError(
                    $"Invalid parameters: pageRanges has invalid page {CdpJson.String(text)}");
            }

            return parsed > int.MaxValue ? int.MaxValue : (int)parsed;
        }
    }

    public static ParsedPdfOptions ParseOptions(JsonNode? parameters)
    {
        if (parameters is not JsonObject)
        {
            throw new DomainError("Invalid parameters: expected an object");
        }

        (string Name, string Capability)[] unsupportedTrue =
        [
            ("displayHeaderFooter", "headers and footers"),
            ("preferCSSPageSize", "CSS @page sizing"),
            ("generateDocumentOutline", "document outlines"),
        ];
        foreach ((string name, string capability) in unsupportedTrue)
        {
            if (Boolean(parameters, name, false))
            {
                throw new DomainError(
                    $"Page.printToPDF does not yet support {capability} ({name}=true)");
            }
        }

        bool requestedPrintBackground = Boolean(parameters, "printBackground", false);
        // Puppeteer currently sends true by default. Raster PDFs have no semantic structure to tag,
        // but rejecting the request makes the standard client unusable. Accept and truthfully
        // report `taggedPdf:false` in the response.
        bool requestedTaggedPdf = Boolean(parameters, "generateTaggedPDF", false);
        float scale = Number(parameters, "scale", 1.0f);
        if (scale is < 0.1f or > 2.0f)
        {
            throw new DomainError("Invalid parameters: scale must be between 0.1 and 2");
        }

        List<Obscura.Browser.RasterPdfPageRange> pageRanges = [];
        if (DomainParams.Has(parameters, "pageRanges"))
        {
            string ranges = parameters.Get("pageRanges").AsString()
                ?? throw new DomainError("Invalid parameters: pageRanges must be a string");
            pageRanges = ParsePageRanges(ranges);
        }

        string[] templateNames = ["headerTemplate", "footerTemplate"];
        foreach (string name in templateNames)
        {
            if (!DomainParams.Has(parameters, name))
            {
                continue;
            }

            string template = parameters.Get(name).AsString()
                ?? throw new DomainError($"Invalid parameters: {name} must be a string");
            if (template.Length != 0)
            {
                throw new DomainError($"Page.printToPDF {name} is not yet supported");
            }
        }

        PdfTransferMode transferMode;
        if (!DomainParams.Has(parameters, "transferMode"))
        {
            transferMode = PdfTransferMode.ReturnAsBase64;
        }
        else if (parameters.Get("transferMode").AsString() is { } mode)
        {
            transferMode = mode switch
            {
                "ReturnAsBase64" => PdfTransferMode.ReturnAsBase64,
                "ReturnAsStream" => PdfTransferMode.ReturnAsStream,
                _ => throw new DomainError("Invalid parameters: unknown transferMode"),
            };
        }
        else
        {
            throw new DomainError("Invalid parameters: transferMode must be a string");
        }

        var defaults = new Obscura.Browser.RasterPdfOptions();
        var raster = new Obscura.Browser.RasterPdfOptions
        {
            Landscape = Boolean(parameters, "landscape", defaults.Landscape),
            PrintBackground = requestedPrintBackground,
            Scale = scale,
            PageRanges = pageRanges,
            PaperWidthIn = Number(parameters, "paperWidth", defaults.PaperWidthIn),
            PaperHeightIn = Number(parameters, "paperHeight", defaults.PaperHeightIn),
            MarginTopIn = Number(parameters, "marginTop", defaults.MarginTopIn),
            MarginBottomIn = Number(parameters, "marginBottom", defaults.MarginBottomIn),
            MarginLeftIn = Number(parameters, "marginLeft", defaults.MarginLeftIn),
            MarginRightIn = Number(parameters, "marginRight", defaults.MarginRightIn),
        };

        return new ParsedPdfOptions(
            raster,
            transferMode,
            requestedPrintBackground,
            requestedTaggedPdf);
    }

    public static async Task<DomainResult> PrintToPdfAsync(
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        try
        {
            return await PrintToPdfInnerAsync(parameters, ctx, sessionId).ConfigureAwait(false);
        }
        catch (DomainError error)
        {
            return DomainResult.Err(error.Message);
        }
    }

    private static async Task<DomainResult> PrintToPdfInnerAsync(
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        var options = ParseOptions(parameters);
        var page = ctx.GetSessionPageMut(sessionId)
            ?? throw new DomainError("No page for session");
        await Page.PrepareCaptureResourcesIfRequestedAsync(page).ConfigureAwait(false);
        AnimationSample animationSample = page.LiveAnimationSample();
        byte[] pdf;
        try
        {
            pdf = page.RasterPdfWithAnimationSample(options.Raster, animationSample);
        }
        catch (Obscura.Browser.RasterPdfException error)
        {
            throw new DomainError(error.Message, error);
        }

        var capabilities = new JsonObject
        {
            ["cssPagedMedia"] = false,
            ["honorsPrintMedia"] = true,
            ["honorsPrintBackground"] = true,
            ["honorsScale"] = true,
            ["pageRanges"] = true,
            ["printColorAdjustExact"] = false,
            ["taggedPdf"] = false,
        };
        var response = new JsonObject
        {
            ["obscuraPrintMode"] = "print-media-raster",
            ["obscuraPrintBackground"] = options.RequestedPrintBackground,
            ["obscuraRequestedPrintBackground"] = options.RequestedPrintBackground,
            ["obscuraTaggedPDF"] = false,
            ["obscuraRequestedTaggedPDF"] = options.RequestedTaggedPdf,
            ["obscuraCapabilities"] = capabilities,
        };

        var ignoredOptions = new JsonArray();
        if (options.RequestedTaggedPdf)
        {
            ignoredOptions.Add("generateTaggedPDF");
        }

        response["obscuraIgnoredOptions"] = ignoredOptions;

        switch (options.TransferMode)
        {
            case PdfTransferMode.ReturnAsBase64:
            {
                long encodedLength = Base64EncodedLength(pdf.LongLength)
                    ?? throw new DomainError("Page.printToPDF base64 response size overflow");
                if (encodedLength > MaxBase64PdfBytes)
                {
                    throw new DomainError(
                        $"Page.printToPDF base64 response would be {encodedLength} bytes, "
                        + $"exceeding the {MaxBase64PdfBytes}-byte response limit; "
                        + "use transferMode=ReturnAsStream");
                }

                response["data"] = Convert.ToBase64String(pdf);
                break;
            }

            case PdfTransferMode.ReturnAsStream:
            {
                if (!ctx.IoStreams.TryInsert(pdf, out string? handle, out string? error))
                {
                    throw new DomainError($"Page.printToPDF could not open stream: {error}");
                }

                response["data"] = string.Empty;
                response["stream"] = handle;
                break;
            }

            default:
                throw new UnreachableException();
        }

        return DomainResult.Ok(response);
    }
}
