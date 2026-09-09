using System.Text.Json.Nodes;
using Obscura.Render.Css;

namespace Obscura.Cdp.Domains;

/// <summary>
/// CDP <c>Emulation</c> domain: device metrics, viewport, screen size and the default background
/// colour override.
/// </summary>
/// <remarks>
/// A metrics override feeds layout, so the viewport and scale changes routed through
/// <see cref="Obscura.Browser.Page.ApplyDeviceMetricsOverride"/> invalidate the page's prepared
/// render state; that invalidation lives in the browser layer and must not be short-circuited here.
/// </remarks>
public static class Emulation
{
    private const long MaxDeviceMetricDimension = 10_000_000;

    private static uint MetricDimension(JsonNode? parameters, string name)
    {
        long value = parameters.Get(name).AsI64()
            ?? throw new DomainError(
                $"Emulation.setDeviceMetricsOverride requires integer {name}");
        if (value is < 0 or > MaxDeviceMetricDimension)
        {
            throw new DomainError(
                "Emulation.setDeviceMetricsOverride "
                + $"{name} must be between 0 and {MaxDeviceMetricDimension}");
        }

        return (uint)value;
    }

    private static uint? OptionalMetricDimension(JsonNode? parameters, string name) =>
        DomainParams.Has(parameters, name) ? MetricDimension(parameters, name) : null;

    public static RgbaColor? DefaultBackgroundColor(JsonNode? parameters)
    {
        if (!DomainParams.Has(parameters, "color"))
        {
            return null;
        }

        JsonObject color = parameters.Get("color").AsJsonObject()
            ?? throw new DomainError(
                "Emulation.setDefaultBackgroundColorOverride color must be an RGBA object");

        byte Channel(string name)
        {
            long value = color.Get(name).AsI64()
                ?? throw new DomainError(
                    $"Emulation.setDefaultBackgroundColorOverride requires integer color.{name}");
            return (byte)Math.Clamp(value, 0, 255);
        }

        double alpha = 1.0;
        if (DomainParams.Has(color, "a"))
        {
            alpha = color.Get("a").AsF64()
                ?? throw new DomainError(
                    "Emulation.setDefaultBackgroundColorOverride color.a must be a number");
        }

        if (!double.IsFinite(alpha))
        {
            throw new DomainError(
                "Emulation.setDefaultBackgroundColorOverride color.a must be finite");
        }

        byte red = Channel("r");
        byte green = Channel("g");
        byte blue = Channel("b");
        // f32 rounding, half away from zero, matching Rust's `f32::round`.
        byte encodedAlpha = (byte)MathF.Round(
            Math.Clamp((float)alpha, 0.0f, 1.0f) * 255.0f,
            MidpointRounding.AwayFromZero);
        return new RgbaColor(red, green, blue, encodedAlpha);
    }

    public static async Task<DomainResult> HandleAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await Task.CompletedTask.ConfigureAwait(false);
        try
        {
            return HandleInner(method, parameters, ctx, sessionId);
        }
        catch (DomainError error)
        {
            return DomainResult.Err(error.Message);
        }
    }

    private static DomainResult HandleInner(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        switch (method)
        {
            case "setDeviceMetricsOverride":
            {
                uint width = MetricDimension(parameters, "width");
                uint height = MetricDimension(parameters, "height");
                double deviceScaleFactor =
                    parameters.Get("deviceScaleFactor").AsF64()
                    ?? throw new DomainError(
                        "Emulation.setDeviceMetricsOverride requires deviceScaleFactor");
                if (!double.IsFinite(deviceScaleFactor) || deviceScaleFactor < 0.0)
                {
                    throw new DomainError(
                        "Emulation.setDeviceMetricsOverride requires a non-negative finite "
                        + "deviceScaleFactor");
                }

                bool mobile = parameters.Get("mobile").AsBool()
                    ?? throw new DomainError(
                        "Emulation.setDeviceMetricsOverride requires boolean mobile");

                // Parse optional dimensions independently. Even an incomplete screen-size pair must
                // reject a malformed or out-of-range member.
                uint? screenWidth = OptionalMetricDimension(parameters, "screenWidth");
                uint? screenHeight = OptionalMetricDimension(parameters, "screenHeight");
                (float Width, float Height)? screenSize =
                    screenWidth is > 0 && screenHeight is > 0
                        ? ((float)screenWidth.Value, (float)screenHeight.Value)
                        : null;

                var page = ctx.GetSessionPageMut(sessionId)
                    ?? throw new DomainError("No page for session");
                page.ApplyDeviceMetricsOverride(
                    width > 0 ? (float?)width : null,
                    height > 0 ? (float?)height : null,
                    deviceScaleFactor > 0.0 ? (float?)deviceScaleFactor : null,
                    screenSize,
                    mobile);
                return DomainResult.Empty();
            }

            case "clearDeviceMetricsOverride":
            {
                var page = ctx.GetSessionPageMut(sessionId)
                    ?? throw new DomainError("No page for session");
                page.ClearDeviceMetricsOverride();
                return DomainResult.Empty();
            }

            case "setDefaultBackgroundColorOverride":
            {
                RgbaColor? color = DefaultBackgroundColor(parameters);
                var page = ctx.GetSessionPageMut(sessionId)
                    ?? throw new DomainError("No page for session");
                page.SetDefaultBackgroundColorOverride(color);
                return DomainResult.Empty();
            }

            // Touch emulation does not affect layout yet, but acknowledging it is compatible with
            // clients that pair it with a metrics override.
            case "setTouchEmulationEnabled":
                return DomainResult.Empty();

            default:
                return DomainResult.Empty();
        }
    }
}
