using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura-cli/tests/screenshot_after_eval.rs</c> and
/// <c>box_shadow_screenshot.rs</c>: the private capture-environment variables
/// that the paired-renderer harness drives the CLI with.
/// </summary>
public sealed class ScreenshotAfterEvalTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"obscura-screenshot-after-eval-{Guid.NewGuid():N}");

    public ScreenshotAfterEvalTests()
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Path_(string name) => Path.Combine(_directory, name);

    private static double? Number(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.Number ? node.GetValue<double>() : null;

    private static string? Text(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    [Fact]
    public void Screenshot_is_captured_after_eval_scrolls_the_live_page()
    {
        var top = Path_("top.png");
        var scrolled = Path_("scrolled.png");
        const string url =
            "data:text/html,<html style=\"margin:0\"><body style=\"margin:0\">" +
            "<div style=\"height:80px;background:red\"></div>" +
            "<div style=\"height:80px;background:blue\"></div>" +
            "</body></html>";
        var environment = new Dictionary<string, string>
        {
            ["OBSCURA_SHOT_W"] = "100",
            ["OBSCURA_SHOT_H"] = "80",
        };

        var topRun = CliProcess.Run(
            environment, "fetch", url, "--screenshot", top, "--wait", "0", "--timeout", "5", "--quiet");
        Assert.True(topRun.Success, $"top capture failed: {topRun.StdErr}");

        var scrolledRun = CliProcess.Run(
            environment, "fetch", url, "--screenshot", scrolled, "--wait", "0", "--timeout", "5", "--quiet",
            "--eval", "(()=>{window.scrollTo(0,80);return JSON.stringify({y:window.scrollY})})()");
        Assert.True(scrolledRun.Success, $"scrolled capture failed: {scrolledRun.StdErr}");

        var state = JsonNode.Parse(scrolledRun.StdOut) as JsonObject;
        Assert.NotNull(state);
        Assert.Equal(80.0, Number(state!["captureState"]?["scrollY"]));

        Assert.NotEqual(
            File.ReadAllBytes(top),
            File.ReadAllBytes(scrolled));
    }

    [Fact]
    public void Screenshots_use_live_animation_time_unless_harness_pins_a_sample()
    {
        var live = Path_("live.png");
        var initial = Path_("initial.png");
        const string url =
            "data:text/html,<html style=\"margin:0\"><head><style>" +
            "@keyframes hide{from{opacity:1}to{opacity:0}}" +
            "#cover{width:80px;height:60px;background:red;" +
            "animation:hide 50ms linear forwards}" +
            "</style></head><body style=\"margin:0;background:lime\">" +
            "<div id=\"cover\"></div></body></html>";

        Dictionary<string, string> Environment(string? animationTime)
        {
            var environment = new Dictionary<string, string>
            {
                ["OBSCURA_SHOT_W"] = "80",
                ["OBSCURA_SHOT_H"] = "60",
                // The Rust test clears this so an ambient value cannot leak in.
                ["OBSCURA_SHOT_ANIMATION_TIME_MS"] = string.Empty,
            };
            if (animationTime is not null)
            {
                environment["OBSCURA_SHOT_ANIMATION_TIME_MS"] = animationTime;
            }
            else
            {
                environment.Remove("OBSCURA_SHOT_ANIMATION_TIME_MS");
            }
            return environment;
        }

        var liveRun = CliProcess.Run(
            Environment(null), "fetch", url, "--screenshot", live,
            "--wait", "1", "--timeout", "5", "--quiet");
        Assert.True(liveRun.Success, $"live capture failed: {liveRun.StdErr}");

        var initialRun = CliProcess.Run(
            Environment("0"), "fetch", url, "--screenshot", initial,
            "--wait", "1", "--timeout", "5", "--quiet");
        Assert.True(initialRun.Success, $"explicit capture failed: {initialRun.StdErr}");

        Assert.NotEqual(File.ReadAllBytes(live), File.ReadAllBytes(initial));
    }

    [Fact]
    public void Paired_capture_reasserts_scroll_after_the_post_eval_settle()
    {
        var screenshot = Path_("reasserted.png");
        const string url =
            "data:text/html,<html style=\"margin:0;scroll-behavior:smooth\">" +
            "<body style=\"margin:0\"><div style=\"height:400px\"></div>" +
            "</body></html>";

        var run = CliProcess.Run(
            new Dictionary<string, string>
            {
                ["OBSCURA_SHOT_W"] = "100",
                ["OBSCURA_SHOT_H"] = "80",
                ["OBSCURA_SHOT_SCROLL_X"] = "0",
                ["OBSCURA_SHOT_SCROLL_Y"] = "80",
            },
            "fetch", url, "--screenshot", screenshot,
            "--eval", "(()=>{scrollTo(0,40);setTimeout(()=>scrollTo(0,10),10);return 'started'})()",
            "--wait", "1", "--timeout", "5", "--quiet");
        Assert.True(run.Success, $"capture failed: {run.StdErr}");

        var report = JsonNode.Parse(run.StdOut) as JsonObject;
        Assert.NotNull(report);
        var controlled = report!["controlledScroll"];
        Assert.Equal(
            10.0,
            Number(controlled?["preReassertActual"]?["y"]));
        Assert.Equal(80.0, Number(controlled?["finalReassertActual"]?["y"]));
        Assert.Equal(80.0, Number(report["captureState"]?["scrollY"]));
        Assert.Equal(
            "immediately-before-capture-state-and-screenshot",
            Text(controlled?["phase"]));
    }

    [Fact]
    public void Paired_capture_evaluates_state_after_final_scroll_reassert()
    {
        var screenshot = Path_("capture-boundary.png");
        const string url =
            "data:text/html,<html style=\"margin:0;scroll-behavior:smooth\">" +
            "<body style=\"margin:0\"><div style=\"height:400px\"></div>" +
            "</body></html>";

        var run = CliProcess.Run(
            new Dictionary<string, string>
            {
                ["OBSCURA_SHOT_W"] = "100",
                ["OBSCURA_SHOT_H"] = "80",
                ["OBSCURA_SHOT_SCROLL_X"] = "0",
                ["OBSCURA_SHOT_SCROLL_Y"] = "80",
                ["OBSCURA_SHOT_EVAL_AT_CAPTURE"] = "1",
                ["OBSCURA_SHOT_RESOURCE_WARMUP"] = "1",
            },
            "fetch", url, "--screenshot", screenshot,
            "--eval", "JSON.stringify({phase:'capture-boundary-before-screenshot',y:scrollY})",
            "--wait", "0", "--timeout", "5", "--quiet");
        Assert.True(run.Success, $"capture failed: {run.StdErr}");

        var report = JsonNode.Parse(run.StdOut) as JsonObject;
        Assert.NotNull(report);
        var evaluation = JsonNode.Parse(
            Text(report!["evaluation"]) ?? throw new JsonException("stringified boundary state"))
            as JsonObject;
        Assert.NotNull(evaluation);
        Assert.Equal("capture-boundary-before-screenshot", Text(evaluation!["phase"]));
        Assert.Equal(
            Number(report["captureState"]?["scrollY"]),
            Number(evaluation["y"]));
        Assert.Equal(
            "before-controlled-scroll-settle",
            Text(report["controlledScroll"]?["initialPhase"]));
        Assert.Equal(
            "immediately-before-capture-state-and-screenshot",
            Text(report["controlledScroll"]?["phase"]));
        Assert.Equal(JsonValueKind.True, report["resourceWarmup"]?["performed"]?.GetValueKind());
        Assert.Equal(1.0, Number(report["resourceWarmup"]?["discardedShots"]));
        Assert.Equal(
            "before-final-scroll-reassert-and-state-sample",
            Text(report["resourceWarmup"]?["phase"]));
    }

    /// <summary>
    /// Port of <c>box_shadow_screenshot.rs</c>: an outset shadow must paint
    /// outside a transparent border box, and the CLI's screenshot path must
    /// deliver those exact pixels.
    /// </summary>
    [Fact]
    public void Screenshot_keeps_outset_shadow_outside_transparent_border_box()
    {
        var path = Path_("outset-shadow.png");
        const string url =
            "data:text/html,<html style=\"margin:0\"><body style=\"margin:0;background:white\">" +
            "<div style=\"position:absolute;left:20px;top:20px;width:40px;height:30px;" +
            "box-shadow:4px 4px 0 black\"></div>" +
            "<div style=\"position:absolute;left:100px;top:20px;width:40px;height:30px;" +
            "background:lime;box-shadow:4px 4px 0 black\"></div>" +
            "</body></html>";

        var run = CliProcess.Run(
            new Dictionary<string, string>
            {
                ["OBSCURA_SHOT_W"] = "160",
                ["OBSCURA_SHOT_H"] = "80",
            },
            "fetch", url, "--screenshot", path, "--wait", "0", "--timeout", "5", "--quiet");
        Assert.True(run.Success, $"capture failed: {run.StdErr}");

        using var bitmap = SKBitmap.Decode(path);
        Assert.NotNull(bitmap);
        Assert.Equal(new SKColor(255, 255, 255, 255), bitmap.GetPixel(35, 35));
        Assert.Equal(new SKColor(255, 255, 255, 255), bitmap.GetPixel(21, 35));
        Assert.Equal(new SKColor(0, 0, 0, 255), bitmap.GetPixel(62, 35));
        Assert.Equal(new SKColor(0, 255, 0, 255), bitmap.GetPixel(115, 35));
    }
}
