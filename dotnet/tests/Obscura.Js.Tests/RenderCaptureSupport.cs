using Obscura.Dom;
using Obscura.Js.Runtime;
using Obscura.Render;
using Xunit;

namespace Obscura.Js.Tests;

/// <summary>
/// Disposes a runtime a render-capture test builds directly, the way
/// <c>RuntimeFixture</c> does for the runtimes built through its helpers.
/// </summary>
internal sealed class CaptureRuntime(ObscuraJsRuntime runtime) : IDisposable
{
    public ObscuraJsRuntime Runtime { get; } = runtime;

    public void Dispose() => Runtime.Dispose();
}

/// <summary>
/// The fixtures the render-capture tests in <c>runtime.rs</c> share
/// (<c>two_by_three_png</c>, <c>parser_image_runtime</c>,
/// <c>animation_epoch_runtime</c>, <c>animation_test_width</c>).
/// </summary>
/// <remarks>
/// They live beside <c>RuntimeTests</c> rather than inside it because that file
/// is shared with other in-flight ports; a separate file cannot collide with
/// them.
/// </remarks>
internal static class RenderCaptureSupport
{
    /// <summary>The Rust tests' <c>two_by_three_png()</c>: a 2x3 PNG.</summary>
    public static byte[] TwoByThreePng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAYAAAC56t6B"
        + "AAAAFklEQVR4nGP8z8Dwn4GBgYGJAQrgDAAxOwIE7x6DkQAAAABJRU5ErkJggg==");

    /// <summary>The Rust tests' <c>parser_image_runtime</c>.</summary>
    public static CaptureRuntime ParserImageRuntime(string html, Func<string, byte[]?> loader)
    {
        var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml(html));
        runtime.SetUrl("http://example.com/page/index.html");
        runtime.State.RenderResources = RenderResourceCache.WithLoader(loader);
        runtime.RunPageInit();
        return new CaptureRuntime(runtime);
    }

    /// <summary>The Rust tests' <c>animation_epoch_runtime</c>.</summary>
    public static CaptureRuntime AnimationEpochRuntime()
    {
        var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><head><style>
                @keyframes grow { from { width:0px } to { width:100px } }
                .anim { height:10px; animation:grow 1000ms linear forwards }
            </style></head><body style="margin:0"><i id="anchor"></i></body></html>
            """));
        runtime.SetUrl("http://example.test/page");
        runtime.SetViewport(200.0, 80.0);
        runtime.RunPageInit();
        return new CaptureRuntime(runtime);
    }

    /// <summary>The Rust tests' <c>animation_test_width</c>.</summary>
    public static float AnimationTestWidth(ObscuraJsRuntime runtime, string id)
    {
        DomTree dom = runtime.State.Dom ?? throw new InvalidOperationException("no document");
        NodeId node = dom.QuerySelector($"#{id}") ?? throw new InvalidOperationException($"no #{id}");
        PreparedRender prepared = runtime.State.PreparedRender
            ?? throw new InvalidOperationException("no prepared render");
        Dimension width = prepared.Layout.Styles[node].Width;
        return width.Kind == DimensionKind.Px
            ? width.Value
            : throw new InvalidOperationException($"expected animated pixel width, got {width}");
    }

    /// <summary>Moves the document timeline origin the given span into the past.</summary>
    public static void RewindAnimationTimeline(ObscuraJsRuntime runtime, int milliseconds) =>
        runtime.State.SetAnimationTimelineElapsed(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>The IHDR width and height of a PNG, after checking the signature.</summary>
    public static (uint Width, uint Height) PngSize(byte[] bytes)
    {
        Assert.Equal<byte>([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a], bytes[..8]);
        return (
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
    }
}
