using PocketCalculator.Dom;

namespace PocketCalculator.Render;

/// <summary>
/// Hit testing against the laid-out box tree, in the order Chromium 141 hit-tests it.
/// </summary>
/// <remarks>
/// <para>
/// Not in crates/obscura-render: the Rust engine has no hit testing, and its shim's
/// <c>document.elementFromPoint</c> is a heuristic over every element's bounding rect (the
/// highest node id containing the point wins), so a click inside a float went to the paragraph
/// after it, a later block's background beat earlier text over it, and z-index, transforms,
/// <c>pointer-events</c> and <c>visibility</c> were ignored. 98 of 272 hit checks on the float
/// conformance pages differed from Chromium.
/// </para>
/// <para>
/// This walks the flat tree (shadow trees included, so the caller retargets) in reverse paint
/// order, CSS 2.1 Appendix E as Blink hit-tests it: per stacking context, positive and zero
/// z-order layers (positioned boxes and stacking contexts, flattened into their stacking
/// context, highest z and latest in tree order first), then the context's own content, then its
/// negative layers. Own content is hit in three phases, as <c>NGBoxFragmentPainter::NodeAtPoint</c>
/// does: the foreground (inline boxes, the content of line boxes, which hits their block, and
/// atomic inlines and flex and grid items, each as a unit), then floats (each as a unit), then
/// block backgrounds, latest first. The root element is hit for any point in the viewport.
/// </para>
/// <para>
/// Known simplifications, recorded as deviations in todo.md: hit areas are border boxes (no
/// border-radius or clip-path), SVG content is hit by object bounding box, flex and grid items
/// are hit in tree order rather than order-modified document order, and generated content and
/// list markers are not hit (they hit their element's box).
/// </para>
/// </remarks>
internal sealed class HitTester
{
    private readonly DomTree _tree;
    private readonly DomLayout _laid;
    private readonly ResolvedScrollState _scroll;
    private readonly float _x;
    private readonly float _y;
    private readonly bool _all;
    private readonly List<NodeId> _hits = [];
    private readonly HashSet<NodeId> _seen = [];
    private Dictionary<NodeId, List<Rect>>? _lineBoxes;
    private bool _done;

    private HitTester(DomTree tree, DomLayout laid, ResolvedScrollState scroll, float x, float y, bool all)
    {
        _tree = tree;
        _laid = laid;
        _scroll = scroll;
        _x = x;
        _y = y;
        _all = all;
    }

    /// <summary>
    /// The elements under viewport point (<paramref name="x"/>, <paramref name="y"/>), topmost
    /// first, not retargeted out of shadow trees; just the topmost unless <paramref name="all"/>.
    /// A text hit is reported as its flat-tree parent element. Empty outside the viewport.
    /// </summary>
    internal static List<NodeId> Run(
        DomTree tree,
        DomLayout laid,
        ResolvedScrollState scroll,
        (float Width, float Height) viewport,
        float x,
        float y,
        bool all)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0f || y < 0f || x >= viewport.Width || y >= viewport.Height)
        {
            return [];
        }

        var tester = new HitTester(tree, laid, scroll, x, y, all);
        NodeId? root = DomTraversal.HtmlElement(tree);
        if (root is not { } html)
        {
            return [];
        }

        StackGuard.RunWithStackFor(tree, () =>
        {
            tester.HitStackingContext(html);
            return 0;
        });

        // The root element's box is the canvas: every point in the viewport hits it last.
        if (!tester._done || tester._hits.Count == 0)
        {
            tester.Add(html, force: true);
        }

        return tester._hits;
    }

    private LayoutStyle? StyleOf(NodeId id) => _laid.Styles.TryGetValue(id, out LayoutStyle? style) ? style : null;

    private static bool CreatesStackingContext(DomTree tree, DomLayout laid, NodeId id, LayoutStyle style) =>
        PaintDomPainter.StackingZIndex(tree, laid, id) is not null
        || (style.Opacity is { } opacity && Math.Clamp(opacity, 0f, 1f) < 1f)
        || PaintDomPainter.HasAuthoredTransform(style);

    private static bool IsPositioned(LayoutStyle style) =>
        style.Position is not null || style.PositionFixed || style.PositionSticky;

    private bool IsLayer(NodeId id, LayoutStyle style) =>
        IsPositioned(style) || CreatesStackingContext(_tree, _laid, id, style);

    private int ZOf(NodeId id) => PaintDomPainter.StackingZIndex(_tree, _laid, id) ?? 0;

    /// <summary>Leaves whose descendants never generate hit-testable boxes of their own.</summary>
    private static bool IsOpaqueLeaf(string local) => local switch
    {
        "img" or "canvas" or "video" or "audio" or "iframe" or "embed" or "object"
            or "input" or "textarea" or "select" or "progress" or "meter" => true,
        _ => false,
    };

    private bool IsFlexOrGridItem(NodeId id)
    {
        NodeId? parent = DomTraversal.RenderedParent(_tree, id);
        int guard = 0;
        while (parent is { } parentId && ++guard < 4096)
        {
            if (StyleOf(parentId) is not { } parentStyle || parentStyle.DisplayContents)
            {
                parent = DomTraversal.RenderedParent(_tree, parentId);
                continue;
            }

            return parentStyle.Display == Display.Grid
                || (parentStyle.Display == Display.Flex && !parentStyle.InternalFlexContainer);
        }

        return false;
    }

    /// <summary>A box hit-tested as one unit in its parent's foreground or float phase.</summary>
    private enum UnitKind : byte
    {
        None,
        Atomic,
        Float,
    }

    private UnitKind UnitOf(NodeId id, LayoutStyle style, string? local)
    {
        if (PaintDomPainter.IsEffectiveFloat(_tree, _laid, id))
        {
            return UnitKind.Float;
        }

        if (style.IsInlineBlock
            || (style.Display == Display.Inline && local is not null && Inline.IsReplaced(local))
            || IsFlexOrGridItem(id))
        {
            return UnitKind.Atomic;
        }

        return UnitKind.None;
    }

    private void HitStackingContext(NodeId root)
    {
        if (!StackGuard.CanDescend())
        {
            return;
        }

        List<(int Z, NodeId Id)> layers = [];
        CollectLayers(root, layers);

        // Stable by z, tree order within one z; hit in reverse.
        List<(int Z, int Order, NodeId Id)> ordered = new(layers.Count);
        for (int i = 0; i < layers.Count; i++)
        {
            ordered.Add((layers[i].Z, i, layers[i].Id));
        }

        ordered.Sort(static (a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.Order.CompareTo(b.Order));
        int firstNonNegative = ordered.FindIndex(static entry => entry.Z >= 0);
        if (firstNonNegative < 0)
        {
            firstNonNegative = ordered.Count;
        }

        for (int i = ordered.Count - 1; i >= firstNonNegative && !_done; i--)
        {
            HitLayer(ordered[i].Id);
        }

        if (!_done)
        {
            HitContent(root, ownBackground: false);
        }

        for (int i = firstNonNegative - 1; i >= 0 && !_done; i--)
        {
            HitLayer(ordered[i].Id);
        }

        // A stacking context's own background is below its negative layers (CSS 2.1 E.2 step 1).
        if (!_done
            && _tree.GetNode(root)?.Parent != _tree.Document
            && _laid.PreciseRect(root) is { } rootRect
            && Contains(root, rootRect))
        {
            Add(root);
        }
    }

    private void HitLayer(NodeId id)
    {
        if (StyleOf(id) is { } style && CreatesStackingContext(_tree, _laid, id, style))
        {
            HitStackingContext(id);
        }
        else
        {
            HitContent(id);
        }
    }

    /// <summary>
    /// Every positioned box and stacking context below <paramref name="root"/> that is not inside
    /// a nested stacking context, in tree order. A positioned box that does not stack is a layer
    /// whose descendant layers still belong to the enclosing stacking context.
    /// </summary>
    private void CollectLayers(NodeId root, List<(int Z, NodeId Id)> output)
    {
        Stack<NodeId> pending = new();
        PushChildrenReversed(root, pending);
        int guard = 0;
        while (pending.Count > 0 && ++guard <= _tree.SlotCount + 1)
        {
            NodeId id = pending.Pop();
            if (StyleOf(id) is not { } style || style.Display == Display.None)
            {
                continue;
            }

            if (style.DisplayContents)
            {
                PushChildrenReversed(id, pending);
                continue;
            }

            if (IsLayer(id, style))
            {
                output.Add((ZOf(id), id));
                if (CreatesStackingContext(_tree, _laid, id, style))
                {
                    continue;
                }
            }

            if (DomTraversal.ElementLocalName(_tree, id) is { } local && IsOpaqueLeaf(local))
            {
                continue;
            }

            PushChildrenReversed(id, pending);
        }
    }

    private void PushChildrenReversed(NodeId id, Stack<NodeId> pending)
    {
        List<NodeId> children = DomTraversal.RenderedChildren(_tree, id);
        for (int i = children.Count - 1; i >= 0; i--)
        {
            pending.Push(children[i]);
        }
    }

    /// <summary>One entry of a unit's foreground phase, in tree order.</summary>
    private readonly record struct Foreground(NodeId Id, byte Kind);

    private const byte InlineBox = 0;
    private const byte LineBoxes = 1;
    private const byte AtomicUnit = 2;
    private const byte TextRuns = 3;
    private const byte SvgContent = 4;

    /// <summary>
    /// Hit one unit's own content: everything below <paramref name="unit"/> that is not a layer
    /// (those are hit through their stacking context), with nested floats and atomic inlines hit
    /// as units of their own.
    /// </summary>
    private void HitContent(NodeId unit, bool ownBackground = true)
    {
        if (!StackGuard.CanDescend())
        {
            return;
        }

        List<Foreground> foreground = [];
        List<NodeId> floats = [];
        List<NodeId> backgrounds = [];
        if (StyleOf(unit) is { } unitStyle && !unitStyle.DisplayContents)
        {
            Classify(unit, unitStyle, foreground, ownBackground ? backgrounds : [], isUnitRoot: true);
        }

        string? rootLocal = DomTraversal.ElementLocalName(_tree, unit);
        if (string.Equals(rootLocal, "svg", StringComparison.Ordinal))
        {
            // An inline <svg> is an atomic unit of its line; its shapes are hit inside it.
            foreground.Add(new Foreground(unit, SvgContent));
        }
        else if (rootLocal is null || !IsOpaqueLeaf(rootLocal))
        {
            Stack<NodeId> pending = new();
            PushChildrenReversed(unit, pending);
            int guard = 0;
            while (pending.Count > 0 && ++guard <= _tree.SlotCount + 1)
            {
                NodeId id = pending.Pop();
                if (_tree.GetNode(id) is not { } node)
                {
                    continue;
                }

                if (node.IsText)
                {
                    if (_laid.WordIfcItems.ContainsKey(id) || _laid.IfcItems.ContainsKey(id) || _laid.RunIfcItems.ContainsKey(id))
                    {
                        foreground.Add(new Foreground(id, LineBoxes));
                    }

                    if (_laid.TextRuns.ContainsKey(id))
                    {
                        foreground.Add(new Foreground(id, TextRuns));
                    }

                    continue;
                }

                if (StyleOf(id) is not { } style || style.Display == Display.None)
                {
                    continue;
                }

                if (style.DisplayContents)
                {
                    PushChildrenReversed(id, pending);
                    continue;
                }

                if (IsLayer(id, style))
                {
                    continue;
                }

                string? local = DomTraversal.ElementLocalName(_tree, id);
                switch (UnitOf(id, style, local))
                {
                    case UnitKind.Float:
                        floats.Add(id);
                        continue;
                    case UnitKind.Atomic:
                        foreground.Add(new Foreground(id, AtomicUnit));
                        continue;
                }

                Classify(id, style, foreground, backgrounds, isUnitRoot: false);
                if (local is not null && IsOpaqueLeaf(local))
                {
                    continue;
                }

                if (string.Equals(local, "svg", StringComparison.Ordinal))
                {
                    foreground.Add(new Foreground(id, SvgContent));
                    continue;
                }

                PushChildrenReversed(id, pending);
            }
        }

        for (int i = foreground.Count - 1; i >= 0 && !_done; i--)
        {
            Foreground entry = foreground[i];
            switch (entry.Kind)
            {
                case AtomicUnit:
                    HitContent(entry.Id);
                    break;
                case InlineBox:
                    HitInlineBox(entry.Id);
                    break;
                case LineBoxes:
                    HitLineBoxes(entry.Id);
                    break;
                case TextRuns:
                    HitTextRuns(entry.Id);
                    break;
                case SvgContent:
                    HitSvg(entry.Id);
                    break;
            }
        }

        for (int i = floats.Count - 1; i >= 0 && !_done; i--)
        {
            HitContent(floats[i]);
        }

        for (int i = backgrounds.Count - 1; i >= 0 && !_done; i--)
        {
            NodeId id = backgrounds[i];
            if (_laid.PreciseRect(id) is { } rect && Contains(id, rect))
            {
                Add(id);
            }
        }
    }

    private void Classify(NodeId id, LayoutStyle style, List<Foreground> foreground, List<NodeId> backgrounds, bool isUnitRoot)
    {
        // The root element is the canvas, hit after every negative layer (Run adds it), and a
        // table's rows and row groups are not hit: Chromium reports the cell, or the table
        // between cells.
        bool noBackground = _tree.GetNode(id)?.Parent == _tree.Document
            || DomTraversal.ElementLocalName(_tree, id) is "tr" or "tbody" or "thead" or "tfoot" or "colgroup" or "col";
        bool inlineBox = !isUnitRoot
            && style.Display == Display.Inline
            && !style.IsInlineBlock
            && _laid.InlineFragments.ContainsKey(id);
        if (inlineBox)
        {
            foreground.Add(new Foreground(id, InlineBox));
        }
        else if (!noBackground)
        {
            backgrounds.Add(id);
        }

        if (_laid.IfcItems.ContainsKey(id) || _laid.RunIfcItems.ContainsKey(id))
        {
            foreground.Add(new Foreground(id, LineBoxes));
        }
    }

    private void HitInlineBox(NodeId id)
    {
        if (!_laid.InlineFragments.TryGetValue(id, out List<Rect>? fragments))
        {
            return;
        }

        bool text = HasTextChild(id);
        foreach (Rect fragment in fragments)
        {
            // The box's text is hit on its pixel-snapped rect too (see PixelSnapped); the
            // fragment's font box stands for the text's.
            if (Contains(id, fragment) || (text && Contains(id, PixelSnapped(fragment))))
            {
                Add(id);
                return;
            }
        }
    }

    private bool HasTextChild(NodeId id)
    {
        foreach (NodeId child in DomTraversal.RenderedChildren(_tree, id))
        {
            if (_tree.GetNode(child) is { IsText: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The content of a block's line boxes (from its first glyph to its last, the full line
    /// height) hits the block, as Blink's <c>HitTestLineBoxFragment</c> does; text directly in
    /// a block has no box of its own.
    /// </summary>
    private void HitLineBoxes(NodeId id)
    {
        _lineBoxes ??= [];
        if (!_lineBoxes.TryGetValue(id, out List<Rect>? boxes))
        {
            boxes = [];
            if (_laid.IfcItems.TryGetValue(id, out int whole))
            {
                _laid.TextEngine.AppendLineContentRects(whole, boxes);
            }

            if (_laid.RunIfcItems.TryGetValue(id, out List<int>? runs))
            {
                foreach (int item in runs)
                {
                    _laid.TextEngine.AppendLineContentRects(item, boxes);
                }
            }

            if (_laid.WordIfcItems.TryGetValue(id, out List<int>? words))
            {
                foreach (int item in words)
                {
                    _laid.TextEngine.AppendLineContentRects(item, boxes);
                }
            }

            _lineBoxes[id] = boxes;
        }

        foreach (Rect box in boxes)
        {
            if (Contains(id, box))
            {
                Add(OwnerElement(id));
                return;
            }
        }
    }

    private void HitTextRuns(NodeId id)
    {
        if (!_laid.TextRuns.TryGetValue(id, out List<(Rect Rect, string Text)>? runs))
        {
            return;
        }

        foreach ((Rect rect, _) in runs)
        {
            if (Contains(id, PixelSnapped(rect)))
            {
                Add(OwnerElement(id));
                return;
            }
        }
    }

    /// <summary>
    /// Blink hit-tests a text item against its pixel-snapped rect (<c>HitTestTextItem</c>,
    /// <c>ToPixelSnappedRect</c>): edges rounded half up, so a run whose font box starts at
    /// y 2.23 is hit at y 2.
    /// </summary>
    private static Rect PixelSnapped(in Rect rect)
    {
        float left = MathF.Floor(rect.X + 0.5f);
        float top = MathF.Floor(rect.Y + 0.5f);
        float right = MathF.Floor(rect.X + rect.Width + 0.5f);
        float bottom = MathF.Floor(rect.Y + rect.Height + 0.5f);
        return new Rect(left, top, right - left, bottom - top);
    }

    private void HitSvg(NodeId svg)
    {
        List<NodeId> descendants = DomTraversal.RenderedDescendants(_tree, svg);
        for (int i = descendants.Count - 1; i >= 0 && !_done; i--)
        {
            NodeId id = descendants[i];
            if (_laid.SvgRects.TryGetValue(id, out Rect rect)
                && DomTraversal.ElementLocalName(_tree, id) is "path" or "rect" or "circle" or "ellipse"
                    or "line" or "polyline" or "polygon" or "text" or "use" or "image" or "tspan"
                && Contains(id, rect))
            {
                Add(id);
            }
        }
    }

    /// <summary>A text node's hit is its flat-tree parent element.</summary>
    private NodeId OwnerElement(NodeId id)
    {
        if (_tree.GetNode(id) is { IsText: true } && DomTraversal.RenderedParent(_tree, id) is { } parent)
        {
            return parent;
        }

        return id;
    }

    private bool Contains(NodeId id, in Rect rect)
    {
        (float mx, float my) = _scroll.MovementFor(id);
        float x = _x - mx;
        float y = _y - my;
        if (_scroll.InheritedClipFor(id) is { } clip && !clip.ContainsPoint(_x, _y))
        {
            return false;
        }

        if (_laid.Transforms.TryGetValue(id, out Affine2 transform))
        {
            float det = (transform.A * transform.D) - (transform.B * transform.C);
            if (det == 0f || !float.IsFinite(det))
            {
                return false;
            }

            float dx = x - transform.E;
            float dy = y - transform.F;
            x = ((transform.D * dx) - (transform.C * dy)) / det;
            y = ((transform.A * dy) - (transform.B * dx)) / det;
        }

        return x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height;
    }

    /// <summary>
    /// Record a hit unless the element is invisible to hit testing: <c>visibility: hidden</c> or
    /// <c>pointer-events: none</c> (its descendants can still be hit). <c>opacity: 0</c> is
    /// still hit, as in Chromium.
    /// </summary>
    private void Add(NodeId id, bool force = false)
    {
        if (!force && StyleOf(id) is { } style
            && (style.ComputedVisibilityHidden
                || string.Equals(style.PointerEvents, "none", StringComparison.Ordinal)))
        {
            return;
        }

        if (_seen.Add(id))
        {
            _hits.Add(id);
        }

        if (!_all)
        {
            _done = true;
        }
    }
}
