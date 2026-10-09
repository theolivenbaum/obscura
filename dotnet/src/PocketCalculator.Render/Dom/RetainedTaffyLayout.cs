// PORT NOTE. No counterpart in crates/obscura-render, which builds a fresh taffy tree and lays
// the whole document out on every pass. This carries layout results across passes for the
// subtrees a retained restyle did not touch, and is a deliberate C# deviation recorded under
// "Known deviations" in todo.md ("A relayout reuses the previous pass's layout results").
using PocketCalculator.Dom;
using PocketCalculator.Render.Layout;
using NodeId = PocketCalculator.Dom.NodeId;
using TaffyNodeId = PocketCalculator.Render.Layout.NodeId;
using TaffyStyle = PocketCalculator.Render.Layout.Style;
using TaffyTree = PocketCalculator.Render.Layout.TaffyTree<int?>;

namespace PocketCalculator.Render;

/// <summary>
/// The box tree one pass laid out, kept with its layout caches so the next retained pass can
/// carry those caches over to the boxes it rebuilds unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Chromium keeps its layout tree and a layout result per box, and a relayout after a small
/// mutation re-runs layout only along the dirty path, reusing the cached result of every clean
/// box whose constraint space is unchanged. The port rebuilds its box tree every pass (it is
/// built from the retained styles, so it costs a tree walk rather than a cascade), and then
/// <see cref="Transplant"/> pairs each new box with the box the previous pass built for the
/// same content. A box whose whole subtree is provably identical gets the previous box's taffy
/// cache and stored layouts. taffy's own cache then does the rest: an unchanged box asked for
/// the same constraints answers from the cache without descending, and one asked for new
/// constraints (a resized container, a changed percentage basis) misses and lays out as before.
/// </para>
/// <para>
/// "Provably identical" fails closed. A box is carried over only when every one of these holds
/// for it and for every box below it: the box maps to the same DOM node (or is an anonymous box
/// in the same position under a carried parent), no DOM node it was built from is in the pass's
/// dirty set or above a node that is, its taffy style is equal and holds no <c>calc()</c> handle
/// (whose resolution context lives outside the style), its measure context is equivalent, its
/// children pair one for one, and the previous pass neither left its cache empty nor computed it
/// in a block formatting context that had floats. A carried cache asked for in a context with
/// floats, or in a different block-context mode, is dropped on the spot
/// (<c>TaffyTree.TaffyView.ComputeChildLayoutInner</c>).
/// </para>
/// <para>
/// <c>POCKETCALCULATOR_FULL_RELAYOUT=1</c> turns this off.
/// </para>
/// </remarks>
internal sealed class RetainedTaffyLayout
{
    private static readonly bool EnvironmentForcesFull =
        Environment.GetEnvironmentVariable("POCKETCALCULATOR_FULL_RELAYOUT") == "1";

    // An AsyncLocal rather than a static, so a test forcing full relayout for its reference
    // does not race the other tests running in parallel; it flows to the large-stack layout
    // thread StackGuard starts for deep trees.
    private static readonly AsyncLocal<bool?> ForceFull = new();

    /// <summary>Whether cross-pass layout reuse is on for passes in this execution context.</summary>
    internal static bool Enabled => !(ForceFull.Value ?? EnvironmentForcesFull);

    /// <summary>
    /// Forces full relayout (or lifts the force) in this execution context until disposed, for
    /// tests that need a reference layout without touching the process environment.
    /// </summary>
    internal static IDisposable ForceFullRelayout(bool force = true)
    {
        bool? saved = ForceFull.Value;
        ForceFull.Value = force;
        return new Restore(saved);
    }

    private sealed class Restore(bool? saved) : IDisposable
    {
        public void Dispose() => ForceFull.Value = saved;
    }

    internal required TaffyTree Tree { get; init; }

    internal required TaffyNodeId Root { get; init; }

    internal required Dictionary<TaffyNodeId, NodeId> IdMap { get; init; }

    internal required Dictionary<TaffyNodeId, (NodeId Source, string Word)> Words { get; init; }

    internal required Dictionary<TaffyNodeId, Size<float>> NativeControlContent { get; init; }

    internal required TextEngine Engine { get; init; }

    internal required Dictionary<NodeId, ReplacedIntrinsic> Intrinsic { get; init; }

    /// <summary>Generated text (counters included) per element, as the pass used it.</summary>
    internal required Dictionary<NodeId, GeneratedText> Generated { get; init; }

    /// <summary>Set once a later pass has taken this tree's caches; it cannot be reused.</summary>
    internal bool Consumed { get; private set; }

    internal readonly record struct GeneratedText(string? Before, string? After, string? BeforePseudo, string? AfterPseudo)
    {
        internal static GeneratedText? Of(LayoutStyle style)
        {
            GeneratedText text = new(
                style.BeforeContent,
                style.AfterContent,
                style.BeforePseudo?.BeforeContent,
                style.AfterPseudo?.BeforeContent);
            return text == default ? null : text;
        }
    }

    internal static Dictionary<NodeId, GeneratedText> SnapshotGenerated(Dictionary<NodeId, LayoutStyle> styles)
    {
        Dictionary<NodeId, GeneratedText> snapshot = [];
        foreach ((NodeId id, LayoutStyle style) in styles)
        {
            if (GeneratedText.Of(style) is { } text)
            {
                snapshot[id] = text;
            }
        }

        return snapshot;
    }

    /// <summary>
    /// The DOM nodes whose boxes cannot be carried over, closed upward: a node is in the result
    /// when it, or any node below it in either the DOM or the flat tree, changed.
    /// </summary>
    internal static HashSet<NodeId> DirtyClosure(
        DomTree tree,
        RetainedTaffyLayout previous,
        HashSet<NodeId>? freshStyles,
        IReadOnlyList<RetainedStyleMutation> mutations,
        IReadOnlyDictionary<NodeId, ReplacedIntrinsic> intrinsic,
        Dictionary<NodeId, LayoutStyle> styles)
    {
        List<NodeId> seeds = [];
        if (freshStyles is not null)
        {
            seeds.AddRange(freshStyles);
        }

        foreach (RetainedStyleMutation mutation in mutations)
        {
            switch (mutation)
            {
                case RetainedStyleMutation.Attribute attribute:
                    // Box generation reads some attributes directly (rowspan, size, rows, cols,
                    // value, open, ...) whatever the style planner made of them.
                    seeds.Add(attribute.Mutation.Node);
                    break;
                case RetainedStyleMutation.Tree { Mutation: TreeStyleMutation.Insert insert }:
                    seeds.Add(insert.Node);
                    seeds.Add(insert.NewParent);
                    if (insert.OldParent is { } oldParent)
                    {
                        seeds.Add(oldParent);
                    }

                    break;
                case RetainedStyleMutation.Tree { Mutation: TreeStyleMutation.Remove remove }:
                    seeds.Add(remove.Node);
                    seeds.Add(remove.OldParent);
                    break;
                case RetainedStyleMutation.Tree { Mutation: TreeStyleMutation.Text text }:
                    seeds.Add(text.Node);
                    if (text.Parent is { } parent)
                    {
                        seeds.Add(parent);
                    }

                    break;
                case RetainedStyleMutation.Animation animation:
                    seeds.Add(animation.Node);
                    break;
                case RetainedStyleMutation.WaapiAnimation waapi:
                    seeds.Add(waapi.Node);
                    break;
            }
        }

        foreach ((NodeId id, ReplacedIntrinsic value) in intrinsic)
        {
            if (!previous.Intrinsic.TryGetValue(id, out ReplacedIntrinsic old) || old != value)
            {
                seeds.Add(id);
            }
        }

        foreach (NodeId id in previous.Intrinsic.Keys)
        {
            if (!intrinsic.ContainsKey(id))
            {
                seeds.Add(id);
            }
        }

        // Counters re-render generated text on elements nothing restyled.
        foreach ((NodeId id, LayoutStyle style) in styles)
        {
            GeneratedText? now = GeneratedText.Of(style);
            GeneratedText? before = previous.Generated.TryGetValue(id, out GeneratedText found) ? found : null;
            if (now != before)
            {
                seeds.Add(id);
            }
        }

        foreach (NodeId id in previous.Generated.Keys)
        {
            if (!styles.ContainsKey(id))
            {
                seeds.Add(id);
            }
        }

        HashSet<NodeId> closure = [];
        Stack<NodeId> pending = new(seeds);
        while (pending.Count != 0)
        {
            NodeId id = pending.Pop();
            if (!closure.Add(id) || tree.GetNode(id) is not { } node)
            {
                continue;
            }

            if (node.Parent is { } parent)
            {
                pending.Push(parent);
            }

            if (DomTraversal.RenderedParent(tree, id) is { } rendered)
            {
                pending.Push(rendered);
            }

            if (tree.ShadowRootInfo(id) is { } shadow)
            {
                pending.Push(shadow.Host);
            }
        }

        return closure;
    }

    /// <summary>
    /// Carries the previous pass's layout caches onto every box of <paramref name="tree"/> whose
    /// subtree is unchanged. Returns how many boxes were carried over.
    /// </summary>
    internal static int Transplant(
        RetainedTaffyLayout previous,
        TaffyTree tree,
        TaffyNodeId root,
        Dictionary<TaffyNodeId, NodeId> idMap,
        Dictionary<TaffyNodeId, (NodeId Source, string Word)> words,
        Dictionary<TaffyNodeId, Size<float>> nativeControlContent,
        TextEngine engine,
        HashSet<NodeId> dirty)
    {
        if (previous.Consumed || !previous.Tree.Contains(previous.Root))
        {
            return 0;
        }

        previous.Consumed = true;
        TaffyTree old = previous.Tree;

        // A DOM node that generated more than one box on either side pairs ambiguously.
        Dictionary<NodeId, TaffyNodeId> oldByDom = new(previous.IdMap.Count);
        HashSet<NodeId> ambiguous = [];
        foreach ((TaffyNodeId box, NodeId dom) in previous.IdMap)
        {
            if (!oldByDom.TryAdd(dom, box))
            {
                ambiguous.Add(dom);
            }
        }

        HashSet<NodeId> seenNew = new(idMap.Count);
        foreach (NodeId dom in idMap.Values)
        {
            if (!seenNew.Add(dom))
            {
                ambiguous.Add(dom);
            }
        }

        // Pair top-down. A mapped box pairs through its DOM node wherever it sits; an anonymous
        // box pairs with the anonymous box at the same index under its parent's partner.
        Dictionary<TaffyNodeId, TaffyNodeId> partner = new(idMap.Count + 16);
        Dictionary<TaffyNodeId, NodeId> anonymousOwners = [];
        List<TaffyNodeId> order = new(idMap.Count + 16);
        Stack<(TaffyNodeId Node, TaffyNodeId? Partner, NodeId? Owner)> walk = new();
        walk.Push((root, PartnerOf(root, null, 0), null));
        while (walk.Count != 0)
        {
            WorkCancellation.ThrowIfCancellationRequested();
            (TaffyNodeId node, TaffyNodeId? paired, NodeId? owner) = walk.Pop();
            NodeId? domOwner = idMap.TryGetValue(node, out NodeId mapped) ? mapped : owner;
            if (paired is { } p)
            {
                partner[node] = p;
            }

            order.Add(node);
            IReadOnlyList<TaffyNodeId> children = tree.ChildrenView(node);
            for (int index = 0; index < children.Count; index++)
            {
                walk.Push((children[index], PartnerOf(children[index], paired, index), domOwner));
            }

            // The owner is only needed for anonymous boxes; remember it per box.
            if (!idMap.ContainsKey(node) && owner is { } anonymousOwner)
            {
                anonymousOwners[node] = anonymousOwner;
            }
        }

        // Decide bottom-up: children are always decided before their parent because `order` is
        // a pre-order walk.
        Dictionary<TaffyNodeId, bool> clean = new(order.Count);
        int carried = 0;
        for (int position = order.Count - 1; position >= 0; position--)
        {
            TaffyNodeId node = order[position];
            bool isClean = IsClean(node);
            clean[node] = isClean;
        }

        // Transplant top-down so a carried box's descendants are all carried with it.
        foreach (TaffyNodeId node in order)
        {
            if (clean.TryGetValue(node, out bool isClean) && isClean)
            {
                tree.TransplantFrom(node, old, partner[node]);
                carried++;
            }
        }

        return carried;

        TaffyNodeId? PartnerOf(TaffyNodeId node, TaffyNodeId? parentPartner, int index)
        {
            if (idMap.TryGetValue(node, out NodeId dom))
            {
                return !ambiguous.Contains(dom) && oldByDom.TryGetValue(dom, out TaffyNodeId found)
                    ? found
                    : null;
            }

            if (parentPartner is { } parentOld)
            {
                IReadOnlyList<TaffyNodeId> oldChildren = old.ChildrenView(parentOld);
                if (index < oldChildren.Count && !previous.IdMap.ContainsKey(oldChildren[index]))
                {
                    return oldChildren[index];
                }
            }

            return node == root && !idMap.ContainsKey(node) ? previous.Root : null;
        }

        bool IsClean(TaffyNodeId node)
        {
            if (!partner.TryGetValue(node, out TaffyNodeId oldNode) || !old.Contains(oldNode))
            {
                return false;
            }

            // The DOM content this box was built from.
            if (idMap.TryGetValue(node, out NodeId dom))
            {
                if (dirty.Contains(dom))
                {
                    return false;
                }
            }
            else if (!anonymousOwners.TryGetValue(node, out NodeId owner) || dirty.Contains(owner))
            {
                return false;
            }

            if (words.TryGetValue(node, out (NodeId Source, string Word) word))
            {
                if (dirty.Contains(word.Source)
                    || !previous.Words.TryGetValue(oldNode, out (NodeId Source, string Word) oldWord)
                    || oldWord.Source != word.Source
                    || !string.Equals(oldWord.Word, word.Word, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else if (previous.Words.ContainsKey(oldNode))
            {
                return false;
            }

            if (old.IsFloatDependent(oldNode))
            {
                return false;
            }

            TaffyStyle style = tree.GetStyle(node);
            if (!style.Equals(old.GetStyle(oldNode)) || HasCalc(style))
            {
                return false;
            }

            // An empty previous cache would break taffy's dirty-propagation invariant (an empty
            // node under a non-empty ancestor) - except under display:none, which is never
            // cached and always laid out hidden.
            if (!old.HasCachedLayout(oldNode) && style.Display != Layout.Display.None)
            {
                return false;
            }

            bool hasContext = tree.HasNodeContext(node);
            if (hasContext != old.HasNodeContext(oldNode))
            {
                return false;
            }

            if (hasContext)
            {
                if (tree.GetNodeContext(node) is not { } context
                    || old.GetNodeContext(oldNode) is not { } oldContext
                    || !engine.MeasureContextMatches(context, previous.Engine, oldContext))
                {
                    return false;
                }
            }

            bool hasControl = nativeControlContent.TryGetValue(node, out Size<float> control);
            bool hadControl = previous.NativeControlContent.TryGetValue(oldNode, out Size<float> oldControl);
            if (hasControl != hadControl
                || (hasControl
                    && (BitConverter.SingleToInt32Bits(control.Width) != BitConverter.SingleToInt32Bits(oldControl.Width)
                        || BitConverter.SingleToInt32Bits(control.Height) != BitConverter.SingleToInt32Bits(oldControl.Height))))
            {
                return false;
            }

            IReadOnlyList<TaffyNodeId> children = tree.ChildrenView(node);
            IReadOnlyList<TaffyNodeId> oldChildren = old.ChildrenView(oldNode);
            if (children.Count != oldChildren.Count)
            {
                return false;
            }

            for (int index = 0; index < children.Count; index++)
            {
                TaffyNodeId child = children[index];
                if (!clean.TryGetValue(child, out bool childClean)
                    || !childClean
                    || partner[child] != oldChildren[index])
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>A text dump of the box tree with unrounded layouts, for diagnosing a divergence.</summary>
    internal string Dump()
    {
        System.Text.StringBuilder text = new();
        Visit(Root, 0);
        return text.ToString();

        void Visit(TaffyNodeId node, int depth)
        {
            Layout.Layout layout = Tree.GetUnroundedLayout(node);
            string owner = IdMap.TryGetValue(node, out NodeId dom) ? $"#{dom.Value}" : "anon";
            text.Append(' ', depth * 2)
                .Append(owner).Append(' ').Append(Tree.GetStyle(node).Display)
                .Append(System.Globalization.CultureInfo.InvariantCulture,
                    $" loc={layout.Location.X},{layout.Location.Y} size={layout.Size.Width}x{layout.Size.Height} content={layout.ContentSize.Width}x{layout.ContentSize.Height}")
                .Append(Tree.WasCarriedOver(node) ? (Tree.IsTransplanted(node) ? " C" : " c") : "")
                .Append(Tree.IsFloatDependent(node) ? " F" : "")
                .Append('\n');
            foreach (TaffyNodeId child in Tree.ChildrenView(node))
            {
                Visit(child, depth + 1);
            }
        }
    }

    /// <summary>
    /// Whether a style carries an opaque <c>calc()</c> handle. The expression behind it takes its
    /// font and viewport context from the pass, so two equal handles need not resolve alike.
    /// </summary>
    internal static bool HasCalc(TaffyStyle style)
    {
        static bool D(Layout.Dimension value) => value.IntoRaw().IsCalc;
        static bool Lpa(LengthPercentageAuto value) => value.IntoRaw().IsCalc;
        static bool Lp(LengthPercentage value) => value.IntoRaw().IsCalc;
        static bool Track(TrackSizingFunction track) =>
            track.Min.IntoRaw().IsCalc || track.Max.IntoRaw().IsCalc;

        if (D(style.Size.Width) || D(style.Size.Height)
            || D(style.MinSize.Width) || D(style.MinSize.Height)
            || D(style.MaxSize.Width) || D(style.MaxSize.Height)
            || D(style.FlexBasis)
            || Lpa(style.Margin.Left) || Lpa(style.Margin.Right) || Lpa(style.Margin.Top) || Lpa(style.Margin.Bottom)
            || Lpa(style.Inset.Left) || Lpa(style.Inset.Right) || Lpa(style.Inset.Top) || Lpa(style.Inset.Bottom)
            || Lp(style.Padding.Left) || Lp(style.Padding.Right) || Lp(style.Padding.Top) || Lp(style.Padding.Bottom)
            || Lp(style.Border.Left) || Lp(style.Border.Right) || Lp(style.Border.Top) || Lp(style.Border.Bottom)
            || Lp(style.Gap.Width) || Lp(style.Gap.Height))
        {
            return true;
        }

        foreach (List<GridTemplateComponent> list in (List<GridTemplateComponent>[])[style.GridTemplateRows, style.GridTemplateColumns])
        {
            foreach (GridTemplateComponent component in list)
            {
                if (component.Kind == GridTemplateComponentKind.Single
                    ? Track(component.Single)
                    : component.Repetition is { } repetition && repetition.Tracks.Exists(Track))
                {
                    return true;
                }
            }
        }

        return style.GridAutoRows.Exists(Track) || style.GridAutoColumns.Exists(Track);
    }
}
