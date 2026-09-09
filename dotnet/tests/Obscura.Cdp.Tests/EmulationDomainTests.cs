using System.Text.Json.Nodes;
using Obscura.Cdp.Domains;
using Obscura.Render.Css;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>#[cfg(test)] mod tests</c> in
/// <c>crates/obscura-cdp/src/domains/emulation.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class EmulationDomainTests
{
    private static Task<DomainResult> HandleAsync(
        CdpContext ctx,
        string? sessionId,
        string parameters,
        string method = "setDeviceMetricsOverride") =>
        Emulation.HandleAsync(method, CdpDomainFixtures.Json(parameters), ctx, sessionId);

    [Fact]
    public async Task DeviceMetricsOverrideUpdatesPageAndWindowViewport()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession("viewport-session");

        Assert.True((await HandleAsync(
            ctx,
            session,
            """
            {
                "width": 1024,
                "height": 768,
                "deviceScaleFactor": 2,
                "mobile": false,
                "screenWidth": 1440,
                "screenHeight": 900
            }
            """)).IsOk);

        var page = ctx.GetSessionPageMut(session);
        Assert.NotNull(page);
        Assert.Equal((1024.0f, 768.0f), page.Viewport);
        Assert.Equal(2.0f, page.DeviceScaleFactor);
        CdpDomainFixtures.AssertJson(
            "[1024, 768, 1024, 768, 1440, 900, 1440, 900, 2]",
            page.Evaluate(
                "return [innerWidth, innerHeight, visualViewport.width,"
                + " visualViewport.height, screen.width, screen.height,"
                + " screen.availWidth, screen.availHeight, devicePixelRatio];"));
    }

    [Fact]
    public async Task OmittedScreenMetricsClearOnlyTheScreenOverride()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession("screen-session");

        string[] cases =
        [
            """
            {
                "width": 1024, "height": 768, "deviceScaleFactor": 1, "mobile": false,
                "screenWidth": 1111, "screenHeight": 777
            }
            """,
            """{"width": 800, "height": 600, "deviceScaleFactor": 1, "mobile": false}""",
        ];
        foreach (string parameters in cases)
        {
            Assert.True((await HandleAsync(ctx, session, parameters)).IsOk);
        }

        var page = ctx.GetSessionPageMut(session);
        Assert.NotNull(page);
        Assert.Equal((800.0f, 600.0f), page.Viewport);
        CdpDomainFixtures.AssertJson(
            "[800, 600, true, true]",
            page.Evaluate(
                "return [innerWidth, innerHeight, screen.width !== 1111,"
                + " screen.height !== 777];"));
    }

    [Fact]
    public async Task DeviceMetricsOverrideRejectsFractionalAndOutOfRangeDimensions()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession("viewport-session");
        string[] cases =
        [
            """{"width": 0.5, "height": 768, "deviceScaleFactor": 1, "mobile": false}""",
            """{"width": 10000001, "height": 768, "deviceScaleFactor": 1, "mobile": false}""",
            """{"width": -1, "height": 768, "deviceScaleFactor": 1, "mobile": false}""",
        ];
        foreach (string parameters in cases)
        {
            Assert.False((await HandleAsync(ctx, session, parameters)).IsOk, parameters);
        }
    }

    [Fact]
    public async Task ZeroDimensionsDisableSizeOverride()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession("zero-size-session");
        var page = ctx.GetSessionPageMut(session);
        Assert.NotNull(page);
        page.SetViewport((1111.0f, 777.0f));
        page.SetDeviceScaleFactor(1.5f);

        Assert.True((await HandleAsync(
            ctx,
            session,
            """{"width": 0, "height": 0, "deviceScaleFactor": 0, "mobile": false}""")).IsOk);

        page = ctx.GetSessionPageMut(session);
        Assert.NotNull(page);
        Assert.Equal((1111.0f, 777.0f), page.Viewport);
        Assert.Equal(1.5f, page.DeviceScaleFactor);
    }

    [Fact]
    public async Task RepeatedOverridesKeepBaselineAndClearRestoresIt()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession("scale-session");
        var page = ctx.GetSessionPageMut(session);
        Assert.NotNull(page);
        page.SetViewport((1111.0f, 777.0f));
        page.SetDeviceScaleFactor(1.5f);
        string? baselineScreen = page.Evaluate(
            "return [screen.width, screen.height, screen.availWidth, screen.availHeight];")
            ?.ToJsonString();

        Assert.True((await HandleAsync(
            ctx,
            session,
            """
            {
                "width": 640, "height": 480, "deviceScaleFactor": 3, "mobile": false,
                "screenWidth": 900, "screenHeight": 700
            }
            """)).IsOk);
        Assert.Equal(3.0f, ctx.GetSessionPage(session)!.DeviceScaleFactor);

        Assert.True((await HandleAsync(
            ctx,
            session,
            """{"width": 0, "height": 333, "deviceScaleFactor": 0, "mobile": false}""")).IsOk);
        var afterZero = ctx.GetSessionPage(session);
        Assert.NotNull(afterZero);
        Assert.Equal((1111.0f, 333.0f), afterZero.Viewport);
        Assert.Equal(1.5f, afterZero.DeviceScaleFactor);

        Assert.True((await HandleAsync(
            ctx,
            session,
            "{}",
            "clearDeviceMetricsOverride")).IsOk);
        page = ctx.GetSessionPageMut(session);
        Assert.NotNull(page);
        Assert.Equal((1111.0f, 777.0f), page.Viewport);
        Assert.Equal(1.5f, page.DeviceScaleFactor);
        Assert.Equal(
            baselineScreen,
            page.Evaluate(
                "return [screen.width, screen.height, screen.availWidth, screen.availHeight];")
                ?.ToJsonString());
    }

    [Fact]
    public async Task InactiveClearIsANoOp()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession("inactive-clear-session");
        var page = ctx.GetSessionPageMut(session);
        Assert.NotNull(page);
        page.SetViewport((901.0f, 607.0f));
        page.SetDeviceScaleFactor(1.25f);

        Assert.True((await HandleAsync(ctx, session, "{}", "clearDeviceMetricsOverride")).IsOk);

        var after = ctx.GetSessionPage(session);
        Assert.NotNull(after);
        Assert.Equal((901.0f, 607.0f), after.Viewport);
        Assert.Equal(1.25f, after.DeviceScaleFactor);
    }

    [Fact]
    public async Task MobileWithoutCompleteScreenSizeUsesEffectiveViewport()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession("mobile-screen-session");

        string[] cases =
        [
            """{"width": 800, "height": 600, "deviceScaleFactor": 1, "mobile": true}""",
            """
            {
                "width": 700, "height": 500, "deviceScaleFactor": 1, "mobile": true,
                "screenWidth": 1000
            }
            """,
        ];
        foreach (string parameters in cases)
        {
            Assert.True((await HandleAsync(ctx, session, parameters)).IsOk);
        }

        var page = ctx.GetSessionPageMut(session);
        Assert.NotNull(page);
        CdpDomainFixtures.AssertJson(
            "[700, 500, 700, 500]",
            page.Evaluate(
                "return [screen.width, screen.height, screen.availWidth, screen.availHeight];"));
    }

    [Fact]
    public async Task ValidatesMobileAndEachOptionalScreenDimension()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession("validation-session");

        string[] cases =
        [
            """{"width": 800, "height": 600, "deviceScaleFactor": 1}""",
            """{"width": 800, "height": 600, "deviceScaleFactor": 1, "mobile": "false"}""",
            """
            {"width": 800, "height": 600, "deviceScaleFactor": 1, "mobile": false, "screenWidth": -1}
            """,
            """
            {"width": 800, "height": 600, "deviceScaleFactor": 1, "mobile": false, "screenHeight": 0.5}
            """,
            """
            {"width": 800, "height": 600, "deviceScaleFactor": 1, "mobile": false,
             "screenWidth": 10000001}
            """,
        ];
        foreach (string parameters in cases)
        {
            Assert.False((await HandleAsync(ctx, session, parameters)).IsOk, parameters);
        }
    }

    [Fact]
    public void DefaultBackgroundColorMatchesBlinkDefaultsRoundingAndClamping()
    {
        Assert.Null(Emulation.DefaultBackgroundColor(CdpDomainFixtures.Json("{}")));
        Assert.Equal(
            new RgbaColor(1, 2, 3, 255),
            Emulation.DefaultBackgroundColor(
                CdpDomainFixtures.Json("""{"color": {"r": 1, "g": 2, "b": 3}}""")));
        Assert.Equal(
            new RgbaColor(0, 255, 30, 16),
            Emulation.DefaultBackgroundColor(CdpDomainFixtures.Json(
                """{"color": {"r": -20, "g": 400, "b": 30, "a": 0.06274509803921569}}""")));
        Assert.Equal(
            new RgbaColor(1, 2, 3, 255),
            Emulation.DefaultBackgroundColor(
                CdpDomainFixtures.Json("""{"color": {"r": 1, "g": 2, "b": 3, "a": 2}}""")));
        Assert.Equal(
            new RgbaColor(1, 2, 3, 0),
            Emulation.DefaultBackgroundColor(
                CdpDomainFixtures.Json("""{"color": {"r": 1, "g": 2, "b": 3, "a": -1}}""")));
    }

    [Fact]
    public void DefaultBackgroundColorRejectsMalformedRgba()
    {
        string[] cases =
        [
            """{"color": null}""",
            """{"color": {"g": 2, "b": 3}}""",
            """{"color": {"r": 1.5, "g": 2, "b": 3}}""",
            """{"color": {"r": 1, "g": 2, "b": 3, "a": "opaque"}}""",
        ];
        foreach (string parameters in cases)
        {
            JsonNode node = CdpDomainFixtures.Json(parameters);
            Assert.Throws<DomainError>(() => Emulation.DefaultBackgroundColor(node));
        }
    }
}
