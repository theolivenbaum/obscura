using Obscura.Dom;
using Obscura.Render;

namespace Obscura.Js.Ops;

/// <summary>
/// Whether a DOM command can make the retained document layout stale.
/// </summary>
/// <remarks>
/// DOM construction is commonly performed in detached subtrees, and frameworks also
/// assign an attribute its current value. Neither operation changes the rendered
/// document. Chromium dirties layout when the mutation reaches a connected
/// style/layout owner, not merely because a mutating API was entered.
/// </remarks>
internal readonly record struct RenderMutationImpact(bool Connected, bool ActualChange)
{
    internal static readonly RenderMutationImpact None = default;
}

/// <summary>Retained-style damage classification for the <c>op_dom</c> mutation commands.</summary>
internal static class RenderInvalidation
{
    /// <summary>
    /// Modern hydration can touch thousands of distinct connected nodes before the
    /// first rendering opportunity. Keep a bounded safety valve for adversarial
    /// churn, but do not force a whole-document cascade at the scale of an ordinary
    /// React/Framer commit.
    /// </summary>
    internal const int MaxPendingStyleMutations = 4096;

    internal static bool IsRenderMutationCommand(string cmd) => cmd switch
    {
        "set_attribute" or "remove_attribute" or "set_attribute_ns" or "remove_attribute_ns"
            or "append_child" or "remove_child" or "insert_before" or "set_inner_html"
            or "set_inner_html_context" or "set_text_content" => true,
        _ => false,
    };

    private static NodeId? ParseNode(string value) =>
        uint.TryParse(value, out var raw) ? NodeId.New(raw) : null;

    internal static RenderMutationImpact MutationImpact(DomTree dom, string cmd, string arg1, string arg2)
    {
        switch (cmd)
        {
            case "set_attribute":
            {
                if (ParseNode(arg1) is not { } target)
                {
                    return RenderMutationImpact.None;
                }

                var split = arg2.IndexOf('\0', StringComparison.Ordinal);
                if (split < 0)
                {
                    return RenderMutationImpact.None;
                }

                var name = arg2[..split];
                var value = arg2[(split + 1)..];
                var old = dom.GetNode(target)?.GetAttribute(name);
                return new RenderMutationImpact(
                    StateHelpers.NodeIsConnected(dom, target),
                    !string.Equals(old, value, StringComparison.Ordinal));
            }

            case "set_attribute_ns":
            {
                if (ParseNode(arg1) is not { } target)
                {
                    return RenderMutationImpact.None;
                }

                var parts = arg2.Split('\0', 3);
                var ns = parts.Length > 0 ? parts[0] : string.Empty;
                var qualified = parts.Length > 1 ? parts[1] : string.Empty;
                var value = parts.Length > 2 ? parts[2] : string.Empty;
                var colon = qualified.IndexOf(':', StringComparison.Ordinal);
                var local = colon >= 0 ? qualified[(colon + 1)..] : qualified;
                var old = dom.GetNode(target)?.GetAttributeNs(ns, local);
                return new RenderMutationImpact(
                    StateHelpers.NodeIsConnected(dom, target),
                    !string.Equals(old, value, StringComparison.Ordinal));
            }

            case "remove_attribute":
            {
                if (ParseNode(arg1) is not { } target)
                {
                    return RenderMutationImpact.None;
                }

                var existed = dom.GetNode(target)?.GetAttribute(arg2) is not null;
                return new RenderMutationImpact(StateHelpers.NodeIsConnected(dom, target), existed);
            }

            case "remove_attribute_ns":
            {
                if (ParseNode(arg1) is not { } target)
                {
                    return RenderMutationImpact.None;
                }

                var (ns, local) = SplitOnceNul(arg2);
                var existed = dom.GetNode(target)?.GetAttributeNs(ns, local) is not null;
                return new RenderMutationImpact(StateHelpers.NodeIsConnected(dom, target), existed);
            }

            case "append_child":
            {
                if (ParseNode(arg1) is not { } parent || ParseNode(arg2) is not { } child)
                {
                    return RenderMutationImpact.None;
                }

                if (dom.GetNode(parent) is null || dom.GetNode(child) is null)
                {
                    return RenderMutationImpact.None;
                }

                var oldParent = dom.GetNode(child)?.Parent;
                var children = dom.Children(parent);
                var alreadyLast = oldParent == parent
                    && children.Count > 0
                    && children[^1] == child;
                return new RenderMutationImpact(
                    // Moving a connected node into a detached subtree removes its old
                    // box, while attaching a detached node creates a new one.
                    StateHelpers.NodeIsConnected(dom, parent) || StateHelpers.NodeIsConnected(dom, child),
                    !alreadyLast);
            }

            case "remove_child":
            {
                if (ParseNode(arg1) is not { } child)
                {
                    return RenderMutationImpact.None;
                }

                return new RenderMutationImpact(
                    StateHelpers.NodeIsConnected(dom, child),
                    dom.GetNode(child)?.Parent is not null);
            }

            case "insert_before":
            {
                if (ParseNode(arg1) is not { } newNode || ParseNode(arg2) is not { } reference)
                {
                    return RenderMutationImpact.None;
                }

                if (dom.GetNode(newNode) is null)
                {
                    return RenderMutationImpact.None;
                }

                if (dom.GetNode(reference)?.Parent is not { } referenceParent)
                {
                    return RenderMutationImpact.None;
                }

                var newWasConnected = StateHelpers.NodeIsConnected(dom, newNode);
                var alreadyImmediatelyBefore = dom.GetNode(reference)?.PrevSibling == newNode;
                return new RenderMutationImpact(
                    StateHelpers.NodeIsConnected(dom, referenceParent) || newWasConnected,
                    newNode != reference && !alreadyImmediatelyBefore);
            }

            case "set_inner_html":
            case "set_inner_html_context":
            {
                if (ParseNode(arg1) is not { } target)
                {
                    return RenderMutationImpact.None;
                }

                return new RenderMutationImpact(
                    StateHelpers.NodeIsConnected(dom, target),
                    // Parsing normalizes source text, so a cheap string comparison
                    // cannot prove equality. Connected replacement remains dirty.
                    dom.GetNode(target) is not null);
            }

            case "set_text_content":
            {
                if (ParseNode(arg1) is not { } target)
                {
                    return RenderMutationImpact.None;
                }

                var changed = false;
                if (dom.GetNode(target) is { } node)
                {
                    changed = node.Data switch
                    {
                        TextData text => !string.Equals(text.Contents, arg2, StringComparison.Ordinal),
                        CommentData comment => !string.Equals(comment.Contents, arg2, StringComparison.Ordinal),
                        ProcessingInstructionData pi => !string.Equals(pi.Data, arg2, StringComparison.Ordinal),
                        // Element/DocumentFragment textContent replaces their child
                        // structure, which can change style even when the flattened
                        // text is equal (for example `<b>x</b>` -> `x`).
                        _ => ElementTextContentChanges(dom, target, arg2),
                    };
                }

                return new RenderMutationImpact(StateHelpers.NodeIsConnected(dom, target), changed);
            }

            default:
                return RenderMutationImpact.None;
        }
    }

    private static bool ElementTextContentChanges(DomTree dom, NodeId target, string text)
    {
        var children = dom.Children(target);
        return children.Count switch
        {
            0 => text.Length != 0,
            1 => dom.GetNode(children[0])?.Data is TextData child
                ? !string.Equals(child.Contents, text, StringComparison.Ordinal)
                : true,
            _ => true,
        };
    }

    private static (string Namespace, string Local) SplitOnceNul(string value)
    {
        var split = value.IndexOf('\0', StringComparison.Ordinal);
        return split < 0 ? (string.Empty, value) : (value[..split], value[(split + 1)..]);
    }

    /// <summary>
    /// The retained-style damage this mutation represents, or null when it can only
    /// be described by a full document cascade.
    /// </summary>
    internal static RetainedStyleMutation? RetainedMutation(DomTree dom, string cmd, string arg1, string arg2)
    {
        if (ParseNode(arg1) is not { } node)
        {
            return null;
        }

        // The retained planner and document stylesheet cache are intentionally
        // light-tree scoped. A mutation inside a connected shadow tree must still
        // invalidate rendering, but cannot be represented by that document-local
        // dirty set until scoped stylesheet invalidation is retained separately.
        if (dom.ContainingShadowRoot(node) is not null)
        {
            return null;
        }

        switch (cmd)
        {
            case "set_attribute":
            {
                var split = arg2.IndexOf('\0', StringComparison.Ordinal);
                if (split < 0)
                {
                    return null;
                }

                var name = arg2[..split];
                var value = arg2[(split + 1)..];
                if (RetainedStylePlanner.RetainedAttributeMutationKindOf(dom, node, name)
                    == RetainedAttributeMutationKind.Full)
                {
                    return null;
                }

                var keepsSelectorValue = !name.Equals("style", StringComparison.OrdinalIgnoreCase);
                return RetainedStyleMutation.From(new AttributeStyleMutation(
                    node,
                    name,
                    keepsSelectorValue ? dom.GetNode(node)?.GetAttribute(name) : null,
                    keepsSelectorValue ? value : null));
            }

            case "remove_attribute":
            {
                if (RetainedStylePlanner.RetainedAttributeMutationKindOf(dom, node, arg2)
                    == RetainedAttributeMutationKind.Full)
                {
                    return null;
                }

                var keepsSelectorValue = !arg2.Equals("style", StringComparison.OrdinalIgnoreCase);
                return RetainedStyleMutation.From(new AttributeStyleMutation(
                    node,
                    arg2,
                    keepsSelectorValue ? dom.GetNode(node)?.GetAttribute(arg2) : null,
                    null));
            }

            case "append_child":
            {
                if (ParseNode(arg2) is not { } child)
                {
                    return null;
                }

                if (dom.GetNode(node) is null || dom.GetNode(child) is not { } childNode)
                {
                    return null;
                }

                return RetainedStyleMutation.From(
                    new TreeStyleMutation.Insert(child, childNode.Parent, node));
            }

            case "remove_child":
            {
                if (dom.GetNode(node)?.Parent is not { } oldParent)
                {
                    return null;
                }

                return RetainedStyleMutation.From(new TreeStyleMutation.Remove(node, oldParent));
            }

            case "insert_before":
            {
                if (ParseNode(arg2) is not { } reference)
                {
                    return null;
                }

                if (dom.GetNode(reference)?.Parent is not { } newParent)
                {
                    return null;
                }

                if (dom.ContainingShadowRoot(newParent) is not null)
                {
                    return null;
                }

                if (dom.GetNode(node) is not { } moving)
                {
                    return null;
                }

                return RetainedStyleMutation.From(
                    new TreeStyleMutation.Insert(node, moving.Parent, newParent));
            }

            case "set_text_content":
            {
                if (dom.GetNode(node) is not { } target)
                {
                    return null;
                }

                // Element/fragment textContent replaces a child list. That can flip
                // :empty and structural/relational selectors, so the local text fast
                // path cannot describe the mutation safely.
                return target.Data is TextData
                    ? RetainedStyleMutation.From(new TreeStyleMutation.Text(node, target.Parent))
                    : null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Queues one retained-style invalidation without letting animation frameworks
    /// evict the whole prepared render merely because they rewrite the same inline
    /// style more than once before the next rendering opportunity.
    /// </summary>
    /// <remarks>
    /// Rendering observes the attribute state at flush boundaries. Repeated writes to
    /// the same node/name therefore retain the first old value and final new value;
    /// intermediate values were never rendered and cannot affect selector matching.
    /// Inline style uses the same rule without storing serialized values.
    /// </remarks>
    internal static bool QueueRetainedStyleMutation(
        List<RetainedStyleMutation> pending,
        RetainedStyleMutation mutation)
    {
        var isResource = mutation is RetainedStyleMutation.Resource;
        var hasResource = false;
        foreach (var queued in pending)
        {
            if (queued is RetainedStyleMutation.Resource)
            {
                hasResource = true;
                break;
            }
        }

        if (isResource && hasResource)
        {
            return true;
        }

        if (mutation is RetainedStyleMutation.Animation animation)
        {
            foreach (var queued in pending)
            {
                if (queued is RetainedStyleMutation.Animation current && current.Node == animation.Node)
                {
                    return true;
                }
            }
        }

        if (mutation is RetainedStyleMutation.WaapiAnimation waapi)
        {
            foreach (var queued in pending)
            {
                if (queued is RetainedStyleMutation.WaapiAnimation current && current.Node == waapi.Node)
                {
                    return true;
                }
            }
        }

        if (mutation is RetainedStyleMutation.Attribute next)
        {
            for (var i = 0; i < pending.Count; i++)
            {
                if (pending[i] is RetainedStyleMutation.Attribute current
                    && current.Mutation.Node == next.Mutation.Node
                    && current.Mutation.Name.Equals(next.Mutation.Name, StringComparison.OrdinalIgnoreCase))
                {
                    pending[i] = new RetainedStyleMutation.Attribute(
                        current.Mutation with { NewValue = next.Mutation.NewValue });
                    return true;
                }
            }
        }

        // Resource refresh is a singleton trigger, not style damage. Keep the bounded
        // safety limit on actual selector/tree/animation invalidations without making
        // a late image discard an exactly-full retained batch.
        var styleDamageLength = pending.Count - (hasResource ? 1 : 0);
        if (!isResource && styleDamageLength >= MaxPendingStyleMutations)
        {
            return false;
        }

        pending.Add(mutation);
        return true;
    }

    /// <summary>
    /// Rebuilds resource-dependent geometry while retaining the previous computed
    /// style graph. Image intrinsic sizes and font metrics can reflow the whole
    /// document, but neither changes selector matching or computed declarations.
    /// </summary>
    internal static void InvalidateRenderResourceGeometry(ObscuraState state)
    {
        if (state.PreparedRender is not null
            && !QueueRetainedStyleMutation(state.PendingStyleMutations, RetainedStyleMutation.Resource.Instance))
        {
            state.PreparedRender = null;
            state.PendingStyleMutations.Clear();
        }

        state.ResolvedScroll = null;
    }
}
