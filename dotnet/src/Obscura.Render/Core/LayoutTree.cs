namespace Obscura.Render;

/// <summary>
/// A node in the input layout tree. <c>Text</c> is carried for the paint phase; it does not
/// affect layout in phase 1 (inline/text layout comes later).
/// </summary>
public sealed class LayoutNode
{
    public LayoutNode()
    {
    }

    public LayoutNode(LayoutStyle style, string? text, List<LayoutNode> children)
    {
        Style = style;
        Text = text;
        Children = children;
    }

    public LayoutStyle Style = new();

    public string? Text;

    public List<LayoutNode> Children = [];

    public static LayoutNode Leaf(LayoutStyle style) => new(style, null, []);

    public LayoutNode Clone() =>
        new(Style.Clone(), Text, [.. Children.Select(static child => child.Clone())]);
}

/// <summary>Computed geometry for one node and its subtree.</summary>
public sealed class NodeRect
{
    /// <summary>Border box, in viewport coordinates.</summary>
    public Rect BorderBox;

    public List<NodeRect> Children = [];
}

/// <summary>
/// Whether an image MIME type names a format supported by the renderer build.
/// </summary>
public static class ImageCapability
{
    private static readonly string[] SupportedSourceTypes =
    [
        "image/apng",
        "image/bmp",
        "image/gif",
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/svg+xml",
        "image/vnd.microsoft.icon",
        "image/webp",
        "image/x-icon",
    ];

    /// <summary>
    /// Compare the MIME essence case-insensitively and ignore parameters.
    /// </summary>
    /// <remarks>
    /// HTML allows a <c>&lt;source type&gt;</c> value to include parameters, and HTTP MIME
    /// types are ASCII case-insensitive. Keep this list aligned with the image codecs the
    /// renderer build enables plus the SVG paint path.
    /// </remarks>
    internal static bool SourceTypeSupported(string value)
    {
        ReadOnlySpan<char> span = value;
        int semicolon = span.IndexOf(';');
        ReadOnlySpan<char> essence = (semicolon >= 0 ? span[..semicolon] : span).Trim();
        foreach (string supported in SupportedSourceTypes)
        {
            if (essence.Equals(supported, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The standalone layout entry point declared in <c>obscura-render/src/lib.rs</c>.
/// </summary>
/// <remarks>
/// PORT STATUS: NOT YET IMPLEMENTED. The Rust body builds a taffy tree, sets the grid
/// <c>calc()</c> resolver from <c>style.rs</c>, computes layout, and reads the border boxes
/// back. It is blocked on <c>Obscura.Render.Layout.TaffyTree</c> (the vendored taffy port) and
/// on <c>Obscura.Render.Style</c>'s grid calc context. The signature is declared here so the
/// taffy agent has the exact contract to fill in; the method throws until then rather than
/// returning geometry that would silently be wrong.
/// </remarks>
public static class RenderLayout
{
    /// <summary>
    /// Lay out <paramref name="root"/> within a <paramref name="viewport"/> (width, height) in
    /// CSS pixels and return the border-box geometry per node, mirroring the input tree.
    /// </summary>
    public static NodeRect Layout(LayoutNode root, (float Width, float Height) viewport)
    {
        ArgumentNullException.ThrowIfNull(root);
        _ = viewport;
        throw new NotImplementedException(
            "obscura-render lib.rs `layout()` is blocked on the taffy port "
            + "(Obscura.Render.Layout.TaffyTree) and the style.rs grid calc resolver.");
    }
}
