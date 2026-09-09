using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;

using Obscura.Browser;
using Obscura.Render;

using BrowserPage = Obscura.Browser.Page;

using SkiaSharp;

namespace Obscura.Cdp.Domains;

/// <summary>
/// The capture half of the <c>Page</c> domain: <c>Page.captureScreenshot</c> and
/// the screencast frame producer.
/// </summary>
/// <remarks>
/// The Rust reference encodes with the <c>image</c> and <c>png</c> crates; this
/// port encodes with Skia, which already backs the paint layer, plus an in-tree
/// streaming PNG writer for the long full-page path (Skia has no incremental
/// encoder, and the whole point of that path is never to hold the full surface).
/// </remarks>
public static partial class Page
{
    private const long DefaultScreenshotQuality = 80;

    private const byte MaxScreencastFramesInFlight = 2;

    private const ulong MaxLongPngPixels = 32UL * 1024 * 1024;

    private const uint MaxLongPngDimension = (128 * 1024) - 1;

    internal enum ScreenshotFormat
    {
        Png,
        Jpeg,
        Webp,
    }

    internal readonly record struct ScreenshotClip(
        double X,
        double Y,
        double Width,
        double Height,
        double Scale);

    internal readonly record struct ScreenshotOptions(
        ScreenshotFormat Format,
        byte Quality,
        bool QualitySupplied,
        ScreenshotClip? Clip,
        bool FromSurface,
        bool CaptureBeyondViewport,
        bool OptimizeForSpeed);

    /// <summary>A by-value copy of a stream's state, as Rust's <c>state.clone()</c> takes.</summary>
    private static ScreencastState CloneState(ScreencastState state) => new()
    {
        Format = state.Format,
        Quality = state.Quality,
        MaxWidth = state.MaxWidth,
        MaxHeight = state.MaxHeight,
        EveryNthFrame = state.EveryNthFrame,
        CommandFrameCounter = state.CommandFrameCounter,
        SessionId = state.SessionId,
        FramesInFlight = state.FramesInFlight,
        ObservedActivityGeneration = state.ObservedActivityGeneration,
        AutonomousFramePending = state.AutonomousFramePending,
    };

    /// <summary><c>Value::get(name).is_some()</c>: present, even when JSON null.</summary>
    private static bool JsonHas(JsonNode? node, string name) =>
        node is JsonObject obj && obj.ContainsKey(name);

    private static bool ScreenshotBool(JsonNode? parameters, string name, bool fallback)
    {
        if (!JsonHas(parameters, name))
        {
            return fallback;
        }

        return parameters.Get(name).AsBool()
            ?? throw new DomainError($"Invalid parameters: {name} must be a boolean");
    }

    /// <summary>
    /// Rust's <c>f32 as u64</c>: saturating, and NaN becomes zero. A C# cast of an
    /// out-of-range float is undefined, which would silently wrap a huge dimension into
    /// a plausible one and slip past the size guards.
    /// </summary>
    private static ulong SaturateToU64(float value)
    {
        if (float.IsNaN(value) || value <= 0.0f)
        {
            return 0UL;
        }

        return value >= 18446744073709551616.0f ? ulong.MaxValue : (ulong)value;
    }

    private static double ScreenshotNumber(JsonObject clip, string name)
    {
        if (!clip.TryGetPropertyValue(name, out JsonNode? node))
        {
            throw new DomainError(
                $"Invalid parameters: mandatory clip.{name} field missing");
        }

        double value = node.AsF64()
            ?? throw new DomainError($"Invalid parameters: clip.{name} must be a number");
        if (!double.IsFinite(value))
        {
            throw new DomainError($"Invalid parameters: clip.{name} must be finite");
        }

        return value;
    }

    internal static ScreenshotOptions ParseScreenshotOptions(JsonNode? parameters)
    {
        if (parameters is not JsonObject)
        {
            throw new DomainError("Invalid parameters: expected an object");
        }

        ScreenshotFormat format;
        JsonNode? formatNode = parameters.Get("format");
        if (!JsonHas(parameters, "format"))
        {
            format = ScreenshotFormat.Png;
        }
        else if (formatNode.AsString() is { } formatText)
        {
            format = formatText switch
            {
                "png" => ScreenshotFormat.Png,
                "jpeg" => ScreenshotFormat.Jpeg,
                "webp" => ScreenshotFormat.Webp,
                _ => throw new DomainError("Invalid image format"),
            };
        }
        else
        {
            throw new DomainError("Invalid parameters: format must be a string");
        }

        bool qualitySupplied = JsonHas(parameters, "quality");
        long quality = DefaultScreenshotQuality;
        if (qualitySupplied)
        {
            long? parsed = parameters.Get("quality").AsI64();
            if (parsed is not { } value || value < int.MinValue || value > int.MaxValue)
            {
                throw new DomainError("Invalid parameters: quality must be an integer");
            }

            quality = value;
        }

        // Chromium accepts an int32 outside [0, 100], but deliberately falls back to its
        // default quality instead of rejecting the request.
        byte effectiveQuality = (byte)(quality is >= 0 and <= 100 ? quality : DefaultScreenshotQuality);

        ScreenshotClip? clip = null;
        if (JsonHas(parameters, "clip"))
        {
            if (parameters.Get("clip") is not JsonObject clipObject)
            {
                throw new DomainError("Invalid parameters: clip must be an object");
            }

            var parsedClip = new ScreenshotClip(
                ScreenshotNumber(clipObject, "x"),
                ScreenshotNumber(clipObject, "y"),
                ScreenshotNumber(clipObject, "width"),
                ScreenshotNumber(clipObject, "height"),
                ScreenshotNumber(clipObject, "scale"));
            if (parsedClip.Width == 0.0)
            {
                throw new DomainError("Cannot take screenshot with 0 width.");
            }

            if (parsedClip.Height == 0.0)
            {
                throw new DomainError("Cannot take screenshot with 0 height.");
            }

            // Chromium's current handler only checks zero, but negative sizes and scales
            // enter invalid gfx sizes and may stall the request. Fail deterministically
            // instead of risking an allocation or hang.
            if (parsedClip.Width < 0.0)
            {
                throw new DomainError("Cannot take screenshot with negative width.");
            }

            if (parsedClip.Height < 0.0)
            {
                throw new DomainError("Cannot take screenshot with negative height.");
            }

            if (parsedClip.Scale <= 0.0)
            {
                throw new DomainError("Cannot take screenshot with non-positive scale.");
            }

            clip = parsedClip;
        }

        return new ScreenshotOptions(
            format,
            effectiveQuality,
            qualitySupplied,
            clip,
            ScreenshotBool(parameters, "fromSurface", true),
            ScreenshotBool(parameters, "captureBeyondViewport", false),
            ScreenshotBool(parameters, "optimizeForSpeed", false));
    }

    internal static string CaptureErrorMessage(CaptureError error) => error switch
    {
        CaptureError.InvalidRegion => "Page.captureScreenshot received an invalid capture region",
        CaptureError.AllocationLimitExceeded => "Page.captureScreenshot bitmap is too large",
        CaptureError.PaintFailed =>
            "Page.captureScreenshot failed: the page has no retained DOM surface to render",
        CaptureError.EncodeFailed => "Page.captureScreenshot renderer PNG encoding failed",
        _ => "Page.captureScreenshot received an invalid capture region",
    };

    internal static CaptureRegion ChromiumClipRegion(ScreenshotClip clip, double deviceScaleFactor)
    {
        // Chromium first converts the CSS clip size to an integer gfx::Size, then applies
        // the effective clip/device scale and rounds to output pixels. Keeping this
        // calculation here avoids asking the raster layer to infer a protocol-specific
        // size from the original fractional rectangle.
        double width = Math.Truncate(clip.Width);
        double height = Math.Truncate(clip.Height);
        if (width <= 0.0 || height <= 0.0)
        {
            throw new DomainError("Screenshot clip is too small at the requested scale.");
        }

        double effectiveScale = clip.Scale * deviceScaleFactor;
        double outputWidth = Math.Round(width * effectiveScale, MidpointRounding.AwayFromZero);
        double outputHeight = Math.Round(height * effectiveScale, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(effectiveScale)
            || effectiveScale <= 0.0
            || !double.IsFinite(outputWidth)
            || !double.IsFinite(outputHeight)
            || outputWidth <= 0.0
            || outputHeight <= 0.0
            || outputWidth > uint.MaxValue
            || outputHeight > uint.MaxValue)
        {
            throw new DomainError("Page.captureScreenshot bitmap is too large");
        }

        return CaptureRegion.WithOutputSize(
            (float)clip.X,
            (float)clip.Y,
            (float)width,
            (float)height,
            (float)effectiveScale,
            (uint)outputWidth,
            (uint)outputHeight);
    }

    /// <summary>Straight (non-premultiplied) RGBA8, standing in for <c>image::RgbaImage</c>.</summary>
    internal sealed class RgbaImage(uint width, uint height, byte[] pixels)
    {
        internal uint Width { get; } = width;

        internal uint Height { get; } = height;

        internal byte[] Pixels { get; } = pixels;

        internal static RgbaImage? Decode(ReadOnlySpan<byte> encoded)
        {
            using SKCodec? codec = SKCodec.Create(new SKMemoryStream(encoded.ToArray()));
            if (codec is null)
            {
                return null;
            }

            var info = new SKImageInfo(
                codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var pixels = new byte[info.BytesSize];
            if (codec.GetPixels(info, pixels) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                return null;
            }

            return new RgbaImage((uint)info.Width, (uint)info.Height, pixels);
        }

        internal SKImage ToImage()
        {
            var info = new SKImageInfo(
                (int)Width, (int)Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            return SKImage.FromPixelCopy(info, Pixels);
        }

        internal RgbaImage Resize(uint width, uint height)
        {
            var target = new SKImageInfo(
                (int)width, (int)height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using SKImage source = ToImage();
            using var destination = new SKBitmap(target);
            // image::imageops::FilterType::Triangle is a bilinear filter.
            source.ScalePixels(
                destination.PeekPixels(),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            return new RgbaImage(width, height, destination.Bytes);
        }
    }

    internal static byte[] EncodeScreenshot(RgbaImage image, ScreenshotOptions options)
    {
        switch (options.Format)
        {
            case ScreenshotFormat.Png:
                return PngWriter.Encode(
                    image.Pixels, image.Width, image.Height, options.OptimizeForSpeed);

            case ScreenshotFormat.Jpeg:
            {
                using SKImage source = image.ToImage();
                // Skia's JPEG encoder, like the pure-Rust one the reference uses, treats
                // quality as 1..=100; Chromium permits zero, whose effective result is the
                // lowest-quality encoding.
                using SKData? data = source.Encode(
                    SKEncodedImageFormat.Jpeg, Math.Max((int)options.Quality, 1));
                return data?.ToArray()
                    ?? throw new DomainError("JPEG screenshot encoding failed");
            }

            case ScreenshotFormat.Webp:
            {
                if (options.QualitySupplied)
                {
                    throw new DomainError(
                        "WebP screenshot quality is not supported by the current lossless encoder");
                }

                using SKImage source = image.ToImage();
                using SKData? data = source.Encode(SKEncodedImageFormat.Webp, 100);
                return data?.ToArray()
                    ?? throw new DomainError("WebP screenshot encoding failed");
            }

            default:
                throw new DomainError("Invalid image format");
        }
    }

    /// <summary>
    /// Encode a full-page PNG a bounded horizontal strip at a time.
    /// </summary>
    /// <remarks>
    /// The ordinary single-surface path stays faster for captures within the renderer's
    /// limits; this fallback exists for tall pages whose complete RGBA surface would
    /// exceed that bound. Each strip is independently validated by the renderer and only
    /// one decoded strip is live while the PNG encoder consumes its scanlines.
    /// </remarks>
    internal static byte[] EncodeLongFullPagePng(
        BrowserPage page,
        (float Width, float Height) contentSize,
        float scale,
        AnimationSample animationSample,
        bool optimizeForSpeed)
    {
        (float width, float height) = contentSize;
        if (!float.IsFinite(width)
            || !float.IsFinite(height)
            || !float.IsFinite(scale)
            || width <= 0.0f
            || height <= 0.0f
            || scale <= 0.0f)
        {
            throw new DomainError(
                "Page.captureScreenshot received an invalid full-page region");
        }

        // Rust's `f32 as u64` saturates; a C# cast of an out-of-range float is undefined,
        // so the guards below would be reading a wrapped value rather than a huge one.
        ulong nativeWidth = SaturateToU64(MathF.Ceiling(width));
        ulong nativeHeight = SaturateToU64(MathF.Ceiling(height));
        double outputWidthValue = Math.Round((double)width * scale, MidpointRounding.AwayFromZero);
        double outputHeightValue = Math.Round((double)height * scale, MidpointRounding.AwayFromZero);
        if (nativeWidth == 0
            || nativeHeight == 0
            || nativeWidth > CaptureLimits.MaxCaptureDimension
            || !double.IsFinite(outputWidthValue)
            || !double.IsFinite(outputHeightValue)
            || outputWidthValue <= 0.0
            || outputHeightValue <= 0.0
            || outputWidthValue > CaptureLimits.MaxCaptureDimension
            || outputHeightValue > MaxLongPngDimension)
        {
            throw new DomainError(
                "Page.captureScreenshot long PNG dimensions are too large");
        }

        var outputWidth = (uint)outputWidthValue;
        var outputHeight = (uint)outputHeightValue;
        // `checked_mul(..).ok_or(..)`: a size that does not fit is answered as a protocol
        // error, not raised. Both operands are non-zero here (rejected above), and the
        // output product is two u32s so it always fits.
        if (nativeWidth > ulong.MaxValue / nativeHeight)
        {
            throw new DomainError("Page.captureScreenshot long PNG size overflow");
        }

        ulong nativePixels = nativeWidth * nativeHeight;
        ulong outputPixels = (ulong)outputWidth * outputHeight;
        if (nativePixels > MaxLongPngPixels || outputPixels > MaxLongPngPixels)
        {
            throw new DomainError(
                $"Page.captureScreenshot long PNG exceeds the {MaxLongPngPixels.ToString(CultureInfo.InvariantCulture)}-pixel safety limit");
        }

        ulong maxNativeRows = Math.Min(
            CaptureLimits.MaxCapturePixels / nativeWidth, CaptureLimits.MaxCaptureDimension);
        ulong maxOutputRows = Math.Min(
            CaptureLimits.MaxCapturePixels / outputWidth, CaptureLimits.MaxCaptureDimension);
        var nativeBoundedOutputRows = (ulong)Math.Floor(
            (maxNativeRows == 0 ? 0.0 : maxNativeRows - 1.0) * scale);
        if (maxNativeRows == 0 || maxOutputRows == 0 || nativeBoundedOutputRows == 0)
        {
            throw new DomainError("Page.captureScreenshot bitmap is too large");
        }

        // Keep each decoded strip materially below the renderer's absolute limit. This
        // leaves headroom for the encoder's scanlines and compressed output.
        var targetOutputRows = (uint)Math.Max(
            Math.Min(Math.Min(maxOutputRows, nativeBoundedOutputRows), 4096UL), 1UL);

        using var writer = new PngWriter(outputWidth, outputHeight, optimizeForSpeed);
        uint outputY = 0;
        while (outputY < outputHeight)
        {
            uint nextOutputY = Math.Min(outputY + targetOutputRows, outputHeight);
            // Derive document-space boundaries from global output rows. The neighboring
            // strips therefore share the exact same rounded edge, rather than
            // accumulating independent per-strip rounding error.
            double cssY = outputY / (double)scale;
            double cssEnd = nextOutputY == outputHeight ? height : nextOutputY / (double)scale;
            double cssHeight = cssEnd - cssY;
            if (cssHeight <= 0.0 || (ulong)Math.Ceiling(cssHeight) > maxNativeRows)
            {
                throw new DomainError(
                    "Page.captureScreenshot could not form a bounded PNG strip");
            }

            uint stripHeight = nextOutputY - outputY;
            var region = CaptureRegion.WithOutputSize(
                0.0f,
                (float)cssY,
                width,
                (float)cssHeight,
                scale,
                outputWidth,
                stripHeight);
            (byte[]? stripPng, CaptureError? error) =
                page.ScreenshotRegionWithAnimationSample(region, animationSample);
            if (error is { } captureError || stripPng is null)
            {
                throw new DomainError(
                    CaptureErrorMessage(error ?? CaptureError.PaintFailed));
            }

            RgbaImage strip = RgbaImage.Decode(stripPng)
                ?? throw new DomainError(
                    "Page.captureScreenshot could not decode a PNG strip");
            if (strip.Width != outputWidth || strip.Height != stripHeight)
            {
                throw new DomainError(
                    $"Page.captureScreenshot PNG strip has dimensions ({strip.Width}, {strip.Height}), expected ({outputWidth}, {stripHeight})");
            }

            writer.WriteRows(strip.Pixels);
            outputY = nextOutputY;
        }

        return writer.Finish();
    }

    /// <summary>
    /// Keep capture itself an observation of the retained page state.
    /// </summary>
    /// <remarks>
    /// Chromium's capture methods do not start a hidden resource-loading phase, and doing
    /// so here added up to three seconds to every screenshot/PDF/screencast start. The
    /// browser navigation and settle paths seed render resources proactively. The
    /// environment variable is retained only as an explicit diagnostic escape hatch for
    /// callers investigating a slow or missing asset.
    /// </remarks>
    internal static async Task PrepareCaptureResourcesIfRequestedAsync(BrowserPage page)
    {
        string? raw = Environment.GetEnvironmentVariable("OBSCURA_RENDER_RESOURCE_DEADLINE_MS");
        if (raw is null
            || !ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong deadlineMs)
            || deadlineMs == 0)
        {
            return;
        }

        try
        {
            await page.PrepareScreenshotResourcesAsync(deadlineMs).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best effort, exactly as the Rust `let _ = ...` discards the result.
        }
    }

    private static long? ScreencastInt32(JsonNode? parameters, string name)
    {
        if (!JsonHas(parameters, name))
        {
            return null;
        }

        long? value = parameters.Get(name).AsI64();
        if (value is not { } number || number < int.MinValue || number > int.MaxValue)
        {
            throw new DomainError($"Invalid parameters: {name} must be an integer");
        }

        return number;
    }

    internal static ScreencastState ParseScreencastState(JsonNode? parameters, long sessionId)
    {
        if (parameters is not JsonObject)
        {
            throw new DomainError("Invalid parameters: expected an object");
        }

        ScreencastFormat format;
        if (!JsonHas(parameters, "format"))
        {
            format = ScreencastFormat.Png;
        }
        else if (parameters.Get("format").AsString() is { } formatText)
        {
            format = formatText switch
            {
                "png" => ScreencastFormat.Png,
                "jpeg" => ScreencastFormat.Jpeg,
                _ => throw new DomainError(
                    "Invalid parameters: screencast format must be png or jpeg"),
            };
        }
        else
        {
            throw new DomainError("Invalid parameters: format must be a string");
        }

        long quality = ScreencastInt32(parameters, "quality") ?? DefaultScreenshotQuality;
        var effectiveQuality = (byte)(quality is >= 0 and <= 100 ? quality : DefaultScreenshotQuality);

        uint? Dimension(string name)
        {
            long? value = ScreencastInt32(parameters, name);
            return value is { } number && number > 0 ? (uint)number : null;
        }

        long everyNthFrame = ScreencastInt32(parameters, "everyNthFrame") ?? 1;
        if (everyNthFrame <= 0)
        {
            throw new DomainError(
                "Invalid parameters: everyNthFrame must be greater than zero");
        }

        return new ScreencastState
        {
            Format = format,
            Quality = effectiveQuality,
            MaxWidth = Dimension("maxWidth"),
            MaxHeight = Dimension("maxHeight"),
            EveryNthFrame = (uint)everyNthFrame,
            CommandFrameCounter = 0,
            SessionId = sessionId,
            FramesInFlight = 0,
            ObservedActivityGeneration = 0,
            AutonomousFramePending = false,
        };
    }

    internal static byte[] EncodeScreencastFrame(byte[] rendererPng, ScreencastState state)
    {
        if (state.Format == ScreencastFormat.Png
            && state.MaxWidth is null
            && state.MaxHeight is null)
        {
            return rendererPng;
        }

        RgbaImage source = RgbaImage.Decode(rendererPng)
            ?? throw new DomainError(
                "Page.startScreencast could not decode renderer PNG");
        double scale = 1.0;
        if (state.MaxWidth is { } maxWidth)
        {
            scale = Math.Min(scale, maxWidth / (double)source.Width);
        }

        if (state.MaxHeight is { } maxHeight)
        {
            scale = Math.Min(scale, maxHeight / (double)source.Height);
        }

        var targetWidth = (uint)Math.Max(
            Math.Round(source.Width * scale, MidpointRounding.AwayFromZero), 1.0);
        var targetHeight = (uint)Math.Max(
            Math.Round(source.Height * scale, MidpointRounding.AwayFromZero), 1.0);
        RgbaImage raster = targetWidth == source.Width && targetHeight == source.Height
            ? source
            : source.Resize(targetWidth, targetHeight);

        ScreenshotFormat format = state.Format == ScreencastFormat.Png
            ? ScreenshotFormat.Png
            : ScreenshotFormat.Jpeg;
        return EncodeScreenshot(
            raster,
            new ScreenshotOptions(
                format,
                state.Quality,
                QualitySupplied: true,
                Clip: null,
                FromSurface: true,
                CaptureBeyondViewport: false,
                OptimizeForSpeed: false));
    }

    /// <summary>
    /// Queue a visible-viewport frame through normal CDP event transport.
    /// </summary>
    /// <remarks>
    /// This is intentionally command-driven until Obscura has a compositor frame pump.
    /// </remarks>
    /// <summary>
    /// Queue one frame, answering the failure reason instead of throwing: frame delivery
    /// is an asynchronous side effect and must never rewrite a command response.
    /// </summary>
    /// <returns><c>null</c> on success (or a deliberate skip), otherwise the error.</returns>
    public static string? QueueScreencastFrame(CdpContext ctx, string? cdpSessionId, bool force)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        try
        {
            ProduceScreencastFrame(ctx, cdpSessionId, force);
            return null;
        }
        catch (DomainError error)
        {
            return error.Message;
        }
    }

    /// <summary>
    /// The frame producer itself. Answers whether a frame was actually queued: a stream
    /// under acknowledgement backpressure, or one skipped by <c>everyNthFrame</c>
    /// sampling, is a deliberate no-op rather than a failure.
    /// </summary>
    internal static bool ProduceScreencastFrame(CdpContext ctx, string? cdpSessionId, bool force)
    {
        if (cdpSessionId is null)
        {
            throw new DomainError(
                "Page.startScreencast requires an attached target session");
        }

        ScreencastState state;
        {
            if (!ctx.Screencasts.TryGetValue(cdpSessionId, out ScreencastState? live))
            {
                return false;
            }

            if (live.FramesInFlight >= MaxScreencastFramesInFlight)
            {
                return false;
            }

            if (!force)
            {
                live.CommandFrameCounter = live.CommandFrameCounter == ulong.MaxValue
                    ? ulong.MaxValue
                    : live.CommandFrameCounter + 1;
                if (live.CommandFrameCounter % live.EveryNthFrame != 0)
                {
                    return false;
                }
            }

            state = CloneState(live);
        }

        BrowserPage page = ctx.GetSessionPageMut(cdpSessionId)
            ?? throw new DomainError("No page for session");
        AnimationSample animationSample = page.LiveAnimationSample();
        (float Width, float Height) viewport = page.Viewport;
        JsonArray? scrollValues = JsonExt.AsJsonArray(page.Evaluate("[window.scrollX, window.scrollY]"));
        double scrollX = scrollValues is { Count: > 0 } ? scrollValues[0].AsF64() ?? 0.0 : 0.0;
        double scrollY = scrollValues is { Count: > 1 } ? scrollValues[1].AsF64() ?? 0.0 : 0.0;
        if (CaptureLimits.ValidateCaptureRegion(CaptureRegion.New(
                (float)scrollX, (float)scrollY, viewport.Width, viewport.Height, 1.0f)) is { } regionError)
        {
            throw new DomainError(CaptureErrorMessage(regionError));
        }

        byte[] png = page.ScreenshotWithAnimationSample(viewport, animationSample)
            ?? throw new DomainError(
                "Page.startScreencast failed: the page has no visible DOM surface to render");
        ulong activityGeneration = page.Js?.ActivityGeneration ?? 0;

        byte[] encoded = EncodeScreencastFrame(png, state);
        string data = Convert.ToBase64String(encoded);
        if (!ctx.Screencasts.TryGetValue(cdpSessionId, out ScreencastState? current))
        {
            return false;
        }

        if (current.SessionId != state.SessionId)
        {
            return false;
        }

        current.FramesInFlight = current.FramesInFlight == byte.MaxValue
            ? byte.MaxValue
            : (byte)(current.FramesInFlight + 1);
        current.ObservedActivityGeneration = activityGeneration;
        current.AutonomousFramePending = false;
        ctx.PendingEvents.Add(new CdpEvent
        {
            Method = "Page.screencastFrame",
            Params = new JsonObject
            {
                ["data"] = data,
                ["metadata"] = new JsonObject
                {
                    ["offsetTop"] = JsonValue.Create(0.0),
                    ["pageScaleFactor"] = JsonValue.Create(1.0),
                    ["deviceWidth"] = JsonValue.Create((double)viewport.Width),
                    ["deviceHeight"] = JsonValue.Create((double)viewport.Height),
                    ["scrollOffsetX"] = JsonValue.Create(scrollX),
                    ["scrollOffsetY"] = JsonValue.Create(scrollY),
                    ["timestamp"] = JsonValue.Create(Timestamp()),
                },
                ["sessionId"] = state.SessionId,
            },
            SessionId = cdpSessionId,
        });
        return true;
    }

    /// <summary>
    /// Advance active page task queues for one bounded compositor slice and emit a frame
    /// when connected-document activity has changed.
    /// </summary>
    /// <remarks>
    /// Chromium feeds <c>Page.screencastFrame</c> from its video consumer on compositor
    /// frames; this is Obscura's single-threaded equivalent until it owns a real
    /// compositor. Generation tracking avoids full-page raster work while the page is
    /// idle. A dirty generation is retained across <c>everyNthFrame</c> sampling and the
    /// acknowledgement window, ensuring neither mechanism loses the latest frame.
    /// </remarks>
    public static async Task PumpScreencastFramesAsync(CdpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await Task.CompletedTask.ConfigureAwait(false);
        List<string> sessions = [.. ctx.Screencasts.Keys];
        foreach (string cdpSessionId in sessions)
        {
            if (!ctx.Screencasts.ContainsKey(cdpSessionId))
            {
                continue;
            }

            BrowserPage? page = ctx.GetSessionPageMut(cdpSessionId);
            if (page?.Js is not { } js)
            {
                continue;
            }

            ulong generation = js.ActivityGeneration;
            bool cssAnimationActive = page.PreparedHasActiveCssAnimations;

            if (!ctx.Screencasts.TryGetValue(cdpSessionId, out ScreencastState? state))
            {
                continue;
            }

            if (state.ObservedActivityGeneration != generation || cssAnimationActive)
            {
                state.ObservedActivityGeneration = generation;
                state.AutonomousFramePending = true;
            }

            if (!state.AutonomousFramePending)
            {
                continue;
            }

            if (QueueScreencastFrame(ctx, cdpSessionId, force: false) is { } error)
            {
                CdpLog.Warn(
                    $"could not produce autonomous screencast frame for {cdpSessionId}: {error}");
            }
        }
    }

    public static bool CommandCanChangeScreencastFrame(string method) => method switch
    {
        "Page.navigate"
            or "Page.reload"
            or "Page.navigateToHistoryEntry"
            or "Runtime.evaluate"
            or "Runtime.callFunctionOn"
            or "Input.dispatchMouseEvent"
            or "Input.dispatchKeyEvent"
            or "Input.dispatchTouchEvent"
            or "Emulation.setDeviceMetricsOverride"
            or "Emulation.clearDeviceMetricsOverride"
            or "Emulation.setDefaultBackgroundColorOverride"
            or "DOM.setAttributeValue"
            or "DOM.removeNode"
            or "DOM.focus"
            or "DOM.setFileInputFiles" => true,
        _ => false,
    };
}

/// <summary>
/// A minimal streaming PNG writer: RGBA8, one IDAT stream fed a strip at a time.
/// </summary>
/// <remarks>
/// Skia has no incremental encoder, and the long full-page path exists precisely so the
/// complete surface is never resident. This writer is the port of the reference's
/// <c>png::Encoder::stream_writer</c> usage, including its Fast/NoFilter mode.
/// </remarks>
internal sealed class PngWriter : IDisposable
{
    private const int MaxIdatChunk = 64 * 1024;

    private readonly uint _width;
    private readonly uint _height;
    private readonly MemoryStream _output = new();
    private readonly MemoryStream _rawIdat = new();
    private readonly ZLibStream _deflate;
    private uint _rowsWritten;
    private bool _finished;

    internal PngWriter(uint width, uint height, bool optimizeForSpeed)
    {
        _width = width;
        _height = height;
        WriteSignature();
        WriteIhdr();
        _deflate = new ZLibStream(
            _rawIdat,
            optimizeForSpeed ? CompressionLevel.Fastest : CompressionLevel.Optimal,
            leaveOpen: true);
    }

    /// <summary>Encode a complete RGBA8 surface in one call.</summary>
    internal static byte[] Encode(byte[] rgba, uint width, uint height, bool optimizeForSpeed)
    {
        using var writer = new PngWriter(width, height, optimizeForSpeed);
        writer.WriteRows(rgba);
        return writer.Finish();
    }

    /// <summary>Append whole rows of straight RGBA8 pixels.</summary>
    internal void WriteRows(ReadOnlySpan<byte> rgba)
    {
        int stride = (int)_width * 4;
        if (stride == 0 || rgba.Length % stride != 0)
        {
            throw new DomainError("Page.captureScreenshot striped PNG write failed");
        }

        int rows = rgba.Length / stride;
        Span<byte> filterByte = [0];
        for (int row = 0; row < rows; row++)
        {
            // Filter type 0 (None), matching the reference's NoFilter setting; the
            // default path pays only compression, which is what the encoder tunes.
            _deflate.Write(filterByte);
            _deflate.Write(rgba.Slice(row * stride, stride));
        }

        _rowsWritten += (uint)rows;
    }

    internal byte[] Finish()
    {
        if (_finished)
        {
            return _output.ToArray();
        }

        _finished = true;
        if (_rowsWritten != _height)
        {
            throw new DomainError(
                "Page.captureScreenshot striped PNG finish failed: incomplete image");
        }

        _deflate.Dispose();
        byte[] compressed = _rawIdat.ToArray();
        for (int offset = 0; offset < compressed.Length; offset += MaxIdatChunk)
        {
            int length = Math.Min(MaxIdatChunk, compressed.Length - offset);
            WriteChunk("IDAT"u8, compressed.AsSpan(offset, length));
        }

        WriteChunk("IEND"u8, ReadOnlySpan<byte>.Empty);
        return _output.ToArray();
    }

    public void Dispose()
    {
        if (!_finished)
        {
            _deflate.Dispose();
        }

        _rawIdat.Dispose();
        _output.Dispose();
    }

    private void WriteSignature() =>
        _output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    private void WriteIhdr()
    {
        Span<byte> ihdr = stackalloc byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], _width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), _height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type: RGBA
        ihdr[10] = 0;  // compression: deflate
        ihdr[11] = 0;  // filter method: adaptive
        ihdr[12] = 0;  // interlace: none
        WriteChunk("IHDR"u8, ihdr);
    }

    private void WriteChunk(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        _output.Write(length);
        _output.Write(type);
        _output.Write(data);
        uint crc = Crc32(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        _output.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in first)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        foreach (byte b in second)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }
}
