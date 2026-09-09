using SkiaSharp;
using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura-cli/tests/box_shadow_screenshot.rs</c>: an outset
/// shadow must paint outside a transparent border box, and the CLI's screenshot
/// path must deliver those exact pixels.
/// </summary>
public sealed class BoxShadowScreenshotTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"obscura-outset-shadow-{Guid.NewGuid():N}.png");

    public BoxShadowScreenshotTests() =>
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void Screenshot_keeps_outset_shadow_outside_transparent_border_box()
    {
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
            "fetch", url, "--screenshot", _path, "--wait", "0", "--timeout", "5", "--quiet");
        Assert.True(run.Success, $"capture failed: {run.StdErr}");

        using var bitmap = SKBitmap.Decode(_path);
        Assert.NotNull(bitmap);
        Assert.Equal(new SKColor(255, 255, 255, 255), bitmap.GetPixel(35, 35));
        Assert.Equal(new SKColor(255, 255, 255, 255), bitmap.GetPixel(21, 35));
        Assert.Equal(new SKColor(0, 0, 0, 255), bitmap.GetPixel(62, 35));
        Assert.Equal(new SKColor(0, 255, 0, 255), bitmap.GetPixel(115, 35));
    }
}
