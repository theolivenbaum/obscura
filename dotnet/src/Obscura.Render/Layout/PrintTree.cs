// Port of vendor/taffy/src/util/print.rs
using System.Globalization;
using System.Text;

namespace Obscura.Render.Layout;

/// <summary>Functions for printing a debug representation of the tree.</summary>
public static class TreePrinter
{
    /// <summary>
    /// Writes a debug representation of the computed layout to the writer, starting with the passed
    /// root node.
    /// </summary>
    public static void WriteTree(TextWriter writer, IPrintTree tree, NodeId root)
    {
        writer.WriteLine("TREE");
        WriteNode(writer, tree, root, false, string.Empty);
    }

    /// <summary>Returns a debug representation of the computed layout for a tree of nodes.</summary>
    public static string FormatTree(IPrintTree tree, NodeId root)
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder, CultureInfo.InvariantCulture);
        WriteTree(writer, tree, root);
        return builder.ToString();
    }

    private static void WriteNode(
        TextWriter writer,
        IPrintTree tree,
        NodeId nodeId,
        bool hasSibling,
        string linesString)
    {
        var layout = tree.GetFinalLayout(nodeId);
        string display = tree.GetDebugLabel(nodeId);
        int numChildren = tree.ChildCount(nodeId);

        string forkString = hasSibling ? "├── " : "└── ";
        writer.WriteLine(
            $"{linesString}{forkString} {display} [x: {layout.Location.X,-4} y: {layout.Location.Y,-4} "
            + $"w: {layout.Size.Width,-4} h: {layout.Size.Height,-4} "
            + $"content_w: {layout.ContentSize.Width,-4} content_h: {layout.ContentSize.Height,-4} "
            + $"border: l:{layout.Border.Left} r:{layout.Border.Right} t:{layout.Border.Top} "
            + $"b:{layout.Border.Bottom}, padding: l:{layout.Padding.Left} r:{layout.Padding.Right} "
            + $"t:{layout.Padding.Top} b:{layout.Padding.Bottom}] ({nodeId})");

        string bar = hasSibling ? "│   " : "    ";
        string newString = linesString + bar;

        int index = 0;
        foreach (var child in tree.ChildIds(nodeId))
        {
            WriteNode(writer, tree, child, index < numChildren - 1, newString);
            index += 1;
        }
    }
}
