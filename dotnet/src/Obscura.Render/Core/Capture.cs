// RECONCILIATION NOTE: the capture-limit types below are declared in
// `crates/obscura-render/src/paint.rs` and re-exported from lib.rs. paint.rs is owned by the
// paint component agent; these types were requested with the core port because CDP and the
// Page API validate capture regions without touching the rasterizer. The paint agent should
// use these rather than redefining them.
namespace Obscura.Render;

/// <summary>
/// Fetch credentials/CORS identity for an HTML image request.
/// </summary>
/// <remarks>
/// No-CORS uses the ordinary URL cache key so CSS images can share the same response; CORS
/// variants use private keys and can never contaminate one another.
/// </remarks>
public enum ImageRequestProfile
{
    NoCorsInclude,
    CorsSameOrigin,
    CorsInclude,
}

/// <summary>A rectangle in immutable document CSS-pixel coordinates.</summary>
/// <remarks>
/// <c>Scale</c> controls output pixels per CSS pixel without changing the prepared layout
/// viewport. This is the same separation Chromium makes between the page-space clip and the
/// screenshot surface scale.
/// </remarks>
public readonly record struct CaptureRegion
{
    private readonly (uint Width, uint Height)? _outputSize;

    private CaptureRegion(
        float x,
        float y,
        float width,
        float height,
        float scale,
        (uint Width, uint Height)? outputSize)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Scale = scale;
        _outputSize = outputSize;
    }

    public float X { get; }

    public float Y { get; }

    public float Width { get; }

    public float Height { get; }

    public float Scale { get; }

    internal (uint Width, uint Height)? OutputSize => _outputSize;

    public static CaptureRegion New(float x, float y, float width, float height, float scale) =>
        new(x, y, width, height, scale, null);

    /// <summary>
    /// Construct a region whose transport protocol has already resolved the exact output pixel
    /// size. The CSS-space paint extent remains independent of that integer output surface.
    /// </summary>
    public static CaptureRegion WithOutputSize(
        float x,
        float y,
        float width,
        float height,
        float scale,
        uint outputWidth,
        uint outputHeight) =>
        new(x, y, width, height, scale, (outputWidth, outputHeight));
}

public enum CaptureError
{
    InvalidRegion,
    AllocationLimitExceeded,
    PaintFailed,
    EncodeFailed,
}

/// <summary>Hard surface limits for one capture.</summary>
/// <remarks>
/// The byte cap bounds both the native CSS-pixel raster and the scaled output before either
/// allocation occurs.
/// </remarks>
public static class CaptureLimits
{
    public const uint MaxCaptureDimension = 32_768;

    public const ulong MaxCapturePixels = 16UL * 1024 * 1024;

    public const float MaxCaptureScale = 16.0f;

    public const ulong MaxCapturePeakBytes = 128UL * 1024 * 1024;

    /// <summary>
    /// Validate every surface allocation implied by a capture before painting.
    /// </summary>
    /// <remarks>
    /// Protocol adapters use this on legacy viewport captures which preserve the renderer's
    /// native PNG bytes but must obey the same limits as region capture.
    /// </remarks>
    /// <returns><c>null</c> on success, otherwise the reason the region was rejected.</returns>
    public static CaptureError? ValidateCaptureRegion(CaptureRegion region) =>
        CheckedCaptureDimensions(region, out _);

    /// <summary>
    /// The native and output pixel dimensions implied by <paramref name="region"/>, or the
    /// reason they were rejected.
    /// </summary>
    internal static CaptureError? CheckedCaptureDimensions(
        CaptureRegion region,
        out (uint NativeWidth, uint NativeHeight, uint OutputWidth, uint OutputHeight) dimensions)
    {
        dimensions = default;
        if (!float.IsFinite(region.X)
            || !float.IsFinite(region.Y)
            || !float.IsFinite(region.Width)
            || !float.IsFinite(region.Height)
            || !float.IsFinite(region.Scale)
            || region.Width <= 0f
            || region.Height <= 0f
            || region.Scale <= 0f)
        {
            return CaptureError.InvalidRegion;
        }

        if (region.Scale > MaxCaptureScale)
        {
            return CaptureError.AllocationLimitExceeded;
        }

        float nativeWidth = MathF.Ceiling(region.Width);
        float nativeHeight = MathF.Ceiling(region.Height);
        double outputWidth;
        double outputHeight;
        if (region.OutputSize is { } output)
        {
            outputWidth = output.Width;
            outputHeight = output.Height;
        }
        else
        {
            outputWidth = (double)region.Width * region.Scale;
            outputHeight = (double)region.Height * region.Scale;
        }

        outputWidth = Math.Ceiling(outputWidth);
        outputHeight = Math.Ceiling(outputHeight);
        if (!float.IsFinite(nativeWidth)
            || !float.IsFinite(nativeHeight)
            || !double.IsFinite(outputWidth)
            || !double.IsFinite(outputHeight)
            || outputWidth <= 0.0
            || outputHeight <= 0.0
            || nativeWidth > MaxCaptureDimension
            || nativeHeight > MaxCaptureDimension
            || outputWidth > MaxCaptureDimension
            || outputHeight > MaxCaptureDimension)
        {
            return CaptureError.AllocationLimitExceeded;
        }

        dimensions = ((uint)nativeWidth, (uint)nativeHeight, (uint)outputWidth, (uint)outputHeight);
        ReadOnlySpan<(uint Width, uint Height)> pairs =
        [
            (dimensions.NativeWidth, dimensions.NativeHeight),
            (dimensions.OutputWidth, dimensions.OutputHeight),
        ];
        foreach ((uint width, uint height) in pairs)
        {
            if ((ulong)width * height > MaxCapturePixels)
            {
                dimensions = default;
                return CaptureError.AllocationLimitExceeded;
            }
        }

        ulong nativePixels = (ulong)dimensions.NativeWidth * dimensions.NativeHeight;
        ulong outputPixels = (ulong)dimensions.OutputWidth * dimensions.OutputHeight;
        ulong peakPixels =
            dimensions.NativeWidth == dimensions.OutputWidth
            && dimensions.NativeHeight == dimensions.OutputHeight
                ? nativePixels
                // Scaling owns the native RGBA surface and output RGBA surface at the same
                // time. Bound their combined live bytes before either allocation.
                : nativePixels + outputPixels;
        if (peakPixels * 4 > MaxCapturePeakBytes)
        {
            dimensions = default;
            return CaptureError.AllocationLimitExceeded;
        }

        return null;
    }
}
