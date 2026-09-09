using System.Text;

namespace Obscura.Dom;

public sealed partial class DomTree
{
    // A unit of pending serialization work. Held on an explicit heap stack instead of the call
    // stack so a deeply nested tree cannot overflow the thread stack and abort the process (a stack
    // overflow is a hard abort that op_dom's exception guard cannot recover). Descendants() is
    // iterative + capped for the same reason; the serializer must be too.
    private readonly record struct SerializeWork(NodeId Node, bool IncludeSelf, string? CloseTag);

    public string OuterHtml(NodeId nodeId)
    {
        var buf = new StringBuilder();
        var stack = new Stack<SerializeWork>();
        stack.Push(new SerializeWork(nodeId, true, null));
        SerializeWorklist(stack, buf);
        return buf.ToString();
    }

    public string InnerHtml(NodeId nodeId)
    {
        var buf = new StringBuilder();
        var stack = new Stack<SerializeWork>();
        PushChildWork(ContentSource(nodeId), stack);
        SerializeWorklist(stack, buf);
        return buf.ToString();
    }

    // The node whose children represent `nodeId`'s markup. For a <template> that is its contents
    // document, since the parser puts template children there rather than under the element
    // (issue #463); for everything else it is the node itself.
    private NodeId ContentSource(NodeId nodeId) =>
        (GetNode(nodeId)?.Data as ElementData)?.TemplateContents ?? nodeId;

    // Children as work items in document order (top of the LIFO stack first).
    private void PushChildWork(NodeId nodeId, Stack<SerializeWork> stack)
    {
        var children = Children(nodeId);
        for (var i = children.Count - 1; i >= 0; i--)
        {
            stack.Push(new SerializeWork(children[i], true, null));
        }
    }

    private void SerializeWorklist(Stack<SerializeWork> stack, StringBuilder buf)
    {
        // Defense in depth, mirroring Descendants(): a well-formed subtree emits at most one Node
        // plus one CloseTag per node, so 2*nodes.Count work items bound a valid walk. Exceeding it
        // means the graph is cyclic (the AppendChild / InsertBefore guards prevent that); stop
        // rather than spin forever. On a valid tree this bound is never reached.
        var maxSteps = (long)NodeSlotCount * 2 + 16;
        var steps = 0L;

        while (stack.Count > 0)
        {
            var work = stack.Pop();
            steps++;
            if (steps > maxSteps)
            {
                Console.Error.WriteLine("obscura: serialize worklist cap hit - tree has a cycle");
                break;
            }

            if (work.CloseTag is { } closeTag)
            {
                buf.Append("</").Append(closeTag).Append('>');
                continue;
            }

            var nodeId = work.Node;
            var includeSelf = work.IncludeSelf;

            var node = GetNode(nodeId);
            if (node is null)
            {
                continue;
            }

            switch (node.Data)
            {
                case DocumentData:
                    PushChildWork(nodeId, stack);
                    break;

                case DoctypeData doctype:
                    buf.Append("<!DOCTYPE ").Append(doctype.Name).Append('>');
                    break;

                case ElementData element:
                {
                    var tag = element.Name.Local;
                    if (includeSelf)
                    {
                        buf.Append('<').Append(tag);
                        foreach (var attr in element.Attrs)
                        {
                            buf.Append(' ');
                            if (attr.Name.Prefix is { } prefix)
                            {
                                buf.Append(prefix).Append(':');
                            }

                            buf.Append(attr.Name.Local).Append("=\"");
                            EscapeAttr(attr.Value, buf);
                            buf.Append('"');
                        }

                        buf.Append('>');
                    }

                    if (!IsVoidElement(tag))
                    {
                        // Push the closing tag first so it pops after all the children we push next.
                        if (includeSelf)
                        {
                            stack.Push(new SerializeWork(nodeId, true, tag));
                        }

                        // A <template> serializes its contents document, not its own (always empty)
                        // children (issue #463).
                        var source = element.TemplateContents ?? nodeId;
                        PushChildWork(source, stack);
                    }

                    break;
                }

                case TextData text:
                {
                    var parentIsRaw = node.Parent is { } parentId
                        && GetNode(parentId)?.ElementName is { } parentName
                        && IsRawTextElement(parentName.Local);

                    if (parentIsRaw)
                    {
                        buf.Append(text.Contents);
                    }
                    else
                    {
                        EscapeText(text.Contents, buf);
                    }

                    break;
                }

                case CommentData comment:
                    buf.Append("<!--");
                    // The HTML parser can never produce a comment that closes early, but script can
                    // via document.createComment(...). The tokenizer ends a comment on ANY of four
                    // sequences: "-->", "--!>", a leading ">", or a leading "->" (comment-start /
                    // -start-dash abrupt-close). Every one requires a ">". Emitting it verbatim
                    // would close the comment early and let the trailing text parse as live markup
                    // (mXSS). Entities are not decoded inside comments, so escaping every ">" to
                    // "&gt;" neutralizes all four forms at once and keeps the data as a single
                    // comment.
                    if (comment.Contents.Contains('>', StringComparison.Ordinal))
                    {
                        buf.Append(comment.Contents.Replace(">", "&gt;", StringComparison.Ordinal));
                    }
                    else
                    {
                        buf.Append(comment.Contents);
                    }

                    buf.Append("-->");
                    break;

                case ProcessingInstructionData pi:
                    buf.Append("<?").Append(pi.Target).Append(' ').Append(pi.Data).Append('>');
                    break;
            }
        }
    }

    private static void EscapeText(string s, StringBuilder buf)
    {
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': buf.Append("&amp;"); break;
                case '<': buf.Append("&lt;"); break;
                case '>': buf.Append("&gt;"); break;
                default: buf.Append(c); break;
            }
        }
    }

    private static void EscapeAttr(string s, StringBuilder buf)
    {
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': buf.Append("&amp;"); break;
                case '"': buf.Append("&quot;"); break;
                default: buf.Append(c); break;
            }
        }
    }

    internal static bool IsVoidElement(string tag) => tag switch
    {
        "area" or "base" or "br" or "col" or "embed" or "hr" or "img" or "input" or "link"
            or "meta" or "param" or "source" or "track" or "wbr" => true,
        _ => false,
    };

    private static bool IsRawTextElement(string tag) => tag switch
    {
        "script" or "style" or "textarea" or "title" => true,
        _ => false,
    };
}
