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
/// children pair one for one, and the previous pass neither left its cache empty nor laid it out
/// reading the floats of its formatting context. A carried cache asked for in a different
/// block-context mode is dropped on the spot (<c>TaffyTree.TaffyView.ComputeChildLayoutInner</c>).
/// </para>
/// <para>
/// A page with floats carries its layouts over the same way. A box laid out in its parent's
/// block formatting context is cached only when that layout read nothing of the floats, with
/// what it asked recorded beside the entry, and the entry answers only where the floats placed
/// so far give the same answers (<c>FloatDependencies</c>); a box beside a float is laid out
/// again on every pass, as before. Nothing is carried across a pass that gained or lost its
/// floats, which builds its block containers differently.
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

    /// <summary>
    /// Whether the pass had a float. Neither layouts nor inline items are carried across a pass
    /// that gained or lost its floats; see <see cref="Transplant"/>.
    /// </summary>
    internal required bool HadFloats { get; init; }

    /// <summary>The whole-container inline item each element got, by item index in <see cref="Engine"/>.</summary>
    internal required IReadOnlyDictionary<NodeId, int> Whole { get; init; }

    internal required Dictionary<NodeId, ReplacedIntrinsic> Intrinsic { get; init; }

    /// <summary>Generated text (counters included) per element, as the pass used it.</summary>
    internal required Dictionary<NodeId, GeneratedText> Generated { get; init; }

    /// <summary>Set once a later pass has taken this tree's caches or items; it cannot be reused.</summary>
    internal bool Consumed { get; private set; }

    /// <summary>Marks this tree as taken by a later pass.</summary>
    internal void Consume() => Consumed = true;

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
        if (!previous.Tree.Contains(previous.Root))
        {
            return 0;
        }

        previous.Consumed = true;

        // A tree with a float builds and lays out its block containers through the float
        // paths (DomBuild.BuildMixedBlock, BlockLayout), so nothing one tree computed answers
        // for the other.
        if (tree.HasFloats != previous.HadFloats)
        {
            return 0;
        }

        TransplantScratch scratch = TransplantScratch.Rent(
            tree.NodesCapacity,
            previous.Tree.NodesCapacity,
            DomSlots(idMap, previous.IdMap));
        try
        {
            return TransplantInto(previous, tree, root, idMap, words, nativeControlContent, engine, dirty, scratch);
        }
        finally
        {
            scratch.Return();
        }
    }

    /// <summary>One more than the highest DOM slot index either map names.</summary>
    private static int DomSlots(Dictionary<TaffyNodeId, NodeId> current, Dictionary<TaffyNodeId, NodeId> before)
    {
        int slots = 0;
        foreach (NodeId dom in current.Values)
        {
            slots = Math.Max(slots, dom.Index + 1);
        }

        foreach (NodeId dom in before.Values)
        {
            slots = Math.Max(slots, dom.Index + 1);
        }

        return slots;
    }

    /// <summary>The slot index of a taffy node (the key packs it into its low 32 bits).</summary>
    private static int Slot(TaffyNodeId node) => (int)(node.Value & 0xFFFF_FFFF);

    /// <summary>
    /// The per-pass bookkeeping of <see cref="Transplant"/>, indexed by taffy slot (new and old
    /// tree) and DOM slot, rented from the shared pools: dictionaries keyed by box were most of
    /// the pairing's time and, at the size of a real document, large-object allocations every
    /// pass.
    /// </summary>
    private sealed class TransplantScratch
    {
        internal const ulong Ambiguous = ulong.MaxValue;

        // Per new-tree slot: the paired old box (0 when none; a live key never is 0), the DOM
        // owner of an anonymous box plus one (0 when none), the decision (0 undecided, 1 clean,
        // 2 not), and whether the box is a word leaf or a native control.
        internal ulong[] Partner = [];
        internal uint[] AnonymousOwner = [];
        internal byte[] State = [];
        internal bool[] NewWord = [];
        internal bool[] NewControl = [];

        // Per old-tree slot.
        internal bool[] OldMapped = [];
        internal bool[] OldWord = [];
        internal bool[] OldControl = [];

        // Per DOM slot: the old box generated for it (0 when none, Ambiguous when several), and
        // how many boxes the new tree generated for it (saturating at 2).
        internal ulong[] OldBoxOfDom = [];
        internal byte[] NewBoxesOfDom = [];

        internal int OldSlots;

        internal static TransplantScratch Rent(int newSlots, int oldSlots, int domSlots) => new()
        {
            OldSlots = oldSlots,
            Partner = RentCleared<ulong>(newSlots),
            AnonymousOwner = RentCleared<uint>(newSlots),
            State = RentCleared<byte>(newSlots),
            NewWord = RentCleared<bool>(newSlots),
            NewControl = RentCleared<bool>(newSlots),
            OldMapped = RentCleared<bool>(oldSlots),
            OldWord = RentCleared<bool>(oldSlots),
            OldControl = RentCleared<bool>(oldSlots),
            OldBoxOfDom = RentCleared<ulong>(domSlots),
            NewBoxesOfDom = RentCleared<byte>(domSlots),
        };

        private static T[] RentCleared<T>(int length)
        {
            T[] array = System.Buffers.ArrayPool<T>.Shared.Rent(Math.Max(length, 1));
            Array.Clear(array, 0, Math.Max(length, 1));
            return array;
        }

        internal void Return()
        {
            System.Buffers.ArrayPool<ulong>.Shared.Return(Partner);
            System.Buffers.ArrayPool<uint>.Shared.Return(AnonymousOwner);
            System.Buffers.ArrayPool<byte>.Shared.Return(State);
            System.Buffers.ArrayPool<bool>.Shared.Return(NewWord);
            System.Buffers.ArrayPool<bool>.Shared.Return(NewControl);
            System.Buffers.ArrayPool<bool>.Shared.Return(OldMapped);
            System.Buffers.ArrayPool<bool>.Shared.Return(OldWord);
            System.Buffers.ArrayPool<bool>.Shared.Return(OldControl);
            System.Buffers.ArrayPool<ulong>.Shared.Return(OldBoxOfDom);
            System.Buffers.ArrayPool<byte>.Shared.Return(NewBoxesOfDom);
        }
    }

    private static int TransplantInto(
        RetainedTaffyLayout previous,
        TaffyTree tree,
        TaffyNodeId root,
        Dictionary<TaffyNodeId, NodeId> idMap,
        Dictionary<TaffyNodeId, (NodeId Source, string Word)> words,
        Dictionary<TaffyNodeId, Size<float>> nativeControlContent,
        TextEngine engine,
        HashSet<NodeId> dirty,
        TransplantScratch scratch)
    {
        TaffyTree old = previous.Tree;
        bool floats = tree.HasFloats;
        ulong[] partnerOf = scratch.Partner;
        uint[] anonymousOwner = scratch.AnonymousOwner;
        byte[] state = scratch.State;

        // A DOM node that generated more than one box on either side pairs ambiguously.
        ulong[] oldBoxOfDom = scratch.OldBoxOfDom;
        foreach ((TaffyNodeId box, NodeId dom) in previous.IdMap)
        {
            ref ulong slotBox = ref oldBoxOfDom[dom.Index];
            slotBox = slotBox == 0 ? box.Value : TransplantScratch.Ambiguous;
            if (Slot(box) < scratch.OldSlots)
            {
                scratch.OldMapped[Slot(box)] = true;
            }
        }

        byte[] newBoxesOfDom = scratch.NewBoxesOfDom;
        foreach (NodeId dom in idMap.Values)
        {
            ref byte count = ref newBoxesOfDom[dom.Index];
            if (count < 2)
            {
                count++;
            }
        }

        foreach (TaffyNodeId box in words.Keys)
        {
            scratch.NewWord[Slot(box)] = true;
        }

        foreach (TaffyNodeId box in previous.Words.Keys)
        {
            if (Slot(box) < scratch.OldSlots)
            {
                scratch.OldWord[Slot(box)] = true;
            }
        }

        foreach (TaffyNodeId box in nativeControlContent.Keys)
        {
            scratch.NewControl[Slot(box)] = true;
        }

        foreach (TaffyNodeId box in previous.NativeControlContent.Keys)
        {
            if (Slot(box) < scratch.OldSlots)
            {
                scratch.OldControl[Slot(box)] = true;
            }
        }

        // Pair top-down. A mapped box pairs through its DOM node wherever it sits; an anonymous
        // box pairs with the anonymous box at the same index under its parent's partner.
        List<TaffyNodeId> order = new(idMap.Count + 16);
        Stack<(TaffyNodeId Node, TaffyNodeId? Partner, NodeId? Owner)> walk = new();
        walk.Push((root, PartnerOf(root, null, 0), null));
        while (walk.Count != 0)
        {
            WorkCancellation.ThrowIfCancellationRequested();
            (TaffyNodeId node, TaffyNodeId? paired, NodeId? owner) = walk.Pop();
            bool mapped = idMap.TryGetValue(node, out NodeId mappedDom);
            NodeId? domOwner = mapped ? mappedDom : owner;
            int slot = Slot(node);
            if (paired is { } p)
            {
                partnerOf[slot] = p.Value;
            }

            order.Add(node);
            List<TaffyNodeId> children = tree.ChildrenList(node);
            for (int index = 0; index < children.Count; index++)
            {
                walk.Push((children[index], PartnerOf(children[index], paired, index), domOwner));
            }

            // The owner is only needed for anonymous boxes; remember it per box.
            if (!mapped && owner is { } ownerId)
            {
                anonymousOwner[slot] = ownerId.Value + 1;
            }
        }

        // Decide bottom-up: children are always decided before their parent because `order` is
        // a pre-order walk.
        for (int position = order.Count - 1; position >= 0; position--)
        {
            TaffyNodeId node = order[position];
            state[Slot(node)] = IsClean(node) ? (byte)1 : (byte)2;
        }

        // Transplant top-down so a carried box's descendants are all carried with it.
        int carried = 0;
        foreach (TaffyNodeId node in order)
        {
            int slot = Slot(node);
            if (state[slot] == 1)
            {
                tree.TransplantFrom(node, old, new TaffyNodeId(partnerOf[slot]));
                carried++;
            }
        }

        return carried;

        TaffyNodeId? PartnerOf(TaffyNodeId node, TaffyNodeId? parentPartner, int index)
        {
            if (idMap.TryGetValue(node, out NodeId dom))
            {
                ulong box = oldBoxOfDom[dom.Index];
                return newBoxesOfDom[dom.Index] == 1 && box != 0 && box != TransplantScratch.Ambiguous
                    ? new TaffyNodeId(box)
                    : null;
            }

            if (parentPartner is { } parentOld)
            {
                List<TaffyNodeId> oldChildren = old.ChildrenList(parentOld);
                if (index < oldChildren.Count
                    && !(Slot(oldChildren[index]) < scratch.OldSlots && scratch.OldMapped[Slot(oldChildren[index])]))
                {
                    return oldChildren[index];
                }
            }

            return node == root && !idMap.ContainsKey(node) ? previous.Root : null;
        }

        bool IsClean(TaffyNodeId node)
        {
            int slot = Slot(node);
            if (partnerOf[slot] == 0)
            {
                return false;
            }

            TaffyNodeId oldNode = new(partnerOf[slot]);
            if (!old.Contains(oldNode))
            {
                return false;
            }

            int oldSlot = Slot(oldNode);

            // The DOM content this box was built from.
            if (idMap.TryGetValue(node, out NodeId dom))
            {
                if (dirty.Contains(dom))
                {
                    return false;
                }
            }
            else if (anonymousOwner[slot] == 0 || dirty.Contains(new NodeId(anonymousOwner[slot] - 1)))
            {
                return false;
            }

            bool oldWord = oldSlot < scratch.OldSlots && scratch.OldWord[oldSlot];
            if (scratch.NewWord[slot])
            {
                (NodeId Source, string Word) word = words[node];
                if (!oldWord
                    || dirty.Contains(word.Source)
                    || !previous.Words.TryGetValue(oldNode, out (NodeId Source, string Word) previousWord)
                    || previousWord.Source != word.Source
                    || !string.Equals(previousWord.Word, word.Word, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else if (oldWord)
            {
                return false;
            }

            // A box whose layout read the floats of its formatting context (a line box beside
            // one, a float it placed): what that layout left in its inline item (the exclusions
            // Finalize lays the lines out around again) is not carried with the cache, and a hit
            // on an ancestor would skip the layout that sets it.
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
            // cached and always laid out hidden, and for a box a tree with floats laid out in its
            // parent's formatting context, which it caches only when float-blind. That box
            // is only reached through its formatting context's root, which re-lays it out, or
            // answers from its own cache and leaves the carried layouts in place; and MarkDirty
            // walks to the root once anything is carried over.
            if (!old.HasCachedLayout(oldNode)
                && style.Display != Layout.Display.None
                && !(floats && old.OnlyInSharedBlockContext(oldNode)))
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

            bool hasControl = scratch.NewControl[slot];
            bool hadControl = oldSlot < scratch.OldSlots && scratch.OldControl[oldSlot];
            if (hasControl != hadControl)
            {
                return false;
            }

            if (hasControl)
            {
                Size<float> control = nativeControlContent[node];
                Size<float> oldControl = previous.NativeControlContent[oldNode];
                if (BitConverter.SingleToInt32Bits(control.Width) != BitConverter.SingleToInt32Bits(oldControl.Width)
                    || BitConverter.SingleToInt32Bits(control.Height) != BitConverter.SingleToInt32Bits(oldControl.Height))
                {
                    return false;
                }
            }

            List<TaffyNodeId> children = tree.ChildrenList(node);
            List<TaffyNodeId> oldChildren = old.ChildrenList(oldNode);
            if (children.Count != oldChildren.Count)
            {
                return false;
            }

            for (int index = 0; index < children.Count; index++)
            {
                int childSlot = Slot(children[index]);
                if (state[childSlot] != 1 || partnerOf[childSlot] != oldChildren[index].Value)
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

        return AnyCalcTemplate(style.GridTemplateRowsIfAny)
            || AnyCalcTemplate(style.GridTemplateColumnsIfAny)
            || AnyCalcTrack(style.GridAutoRowsIfAny)
            || AnyCalcTrack(style.GridAutoColumnsIfAny);

        static bool AnyCalcTemplate(List<GridTemplateComponent>? list)
        {
            if (list is null)
            {
                return false;
            }

            foreach (GridTemplateComponent component in list)
            {
                if (component.Kind == GridTemplateComponentKind.Single
                    ? Track(component.Single)
                    : component.Repetition is { } repetition && AnyCalcTrack(repetition.Tracks))
                {
                    return true;
                }
            }

            return false;
        }

        static bool AnyCalcTrack(List<TrackSizingFunction>? tracks)
        {
            if (tracks is null)
            {
                return false;
            }

            foreach (TrackSizingFunction track in tracks)
            {
                if (Track(track))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
