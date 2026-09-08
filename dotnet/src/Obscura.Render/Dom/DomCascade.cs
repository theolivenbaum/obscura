// Port of the style cascade traversal in crates/obscura-render/src/dom.rs.
using System.Globalization;
using Obscura.Dom;
using Obscura.Dom.Selectors;
using Obscura.Render.Css;

namespace Obscura.Render;

internal static class DomCascade
{
    /// <summary>
    /// Apply HTML presentational attributes at their cascade origin: above the UA defaults,
    /// but below every author stylesheet and style attribute.
    /// </summary>
    internal static void ApplyPresentationalHints(Node node, LayoutStyle style)
    {
        if (node.GetAttribute("dir") is { } direction)
        {
            style.Direction = direction.Trim().ToLowerInvariant() switch
            {
                "ltr" => Layout.Direction.Ltr,
                "rtl" => Layout.Direction.Rtl,
                _ => style.Direction,
            };
        }

        if (node.GetAttribute("color") is { } color)
        {
            ComputedStyle.ApplyInline(style, $"color: {color}");
        }

        if (node.GetAttribute("bgcolor") is { } bgcolor)
        {
            ComputedStyle.ApplyInline(style, $"background-color: {bgcolor}");
        }

        if (node.GetAttribute("width") is { } width)
        {
            ComputedStyle.ApplyInline(
                style,
                AllAsciiDigits(width) ? $"width: {width}px" : $"width: {width}");
        }

        if (node.GetAttribute("align") is { } align)
        {
            ComputedStyle.ApplyInline(style, $"text-align: {align}");
        }

        if (node.GetAttribute("valign") is { } valign)
        {
            ComputedStyle.ApplyInline(style, $"vertical-align: {valign}");
        }

        if (node.GetAttribute("cellspacing") is { } cellspacing && AllAsciiDigits(cellspacing))
        {
            ComputedStyle.ApplyInline(style, $"border-spacing: {cellspacing}px");
        }

        if (node.GetAttribute("height") is { } height)
        {
            ComputedStyle.ApplyInline(
                style,
                AllAsciiDigits(height) ? $"height: {height}px" : $"height: {height}");
        }

        if (style.AspectRatio is null)
        {
            float? aw = ParseFloat(node.GetAttribute("width"));
            float? ah = ParseFloat(node.GetAttribute("height"));
            if (aw is { } w && ah is { } h && w > 0f && h > 0f)
            {
                style.AspectRatio = w / h;
                style.AspectRatioIsMapped = true;
                style.AspectRatioIsIntrinsic = true;
            }
        }

        // An inline SVG with one CSS axis and a viewBox derives the other axis from the
        // viewBox's intrinsic aspect ratio.
        if (style.AspectRatio is null
            && node.AsElement() is { } element
            && string.Equals(element.Name.Local, "svg", StringComparison.Ordinal))
        {
            string? viewBox = node.GetAttribute("viewBox") ?? node.GetAttribute("viewbox");
            if (viewBox is not null)
            {
                List<float> values = [];
                foreach (string part in viewBox.Split(
                    [' ', '\t', '\n', '\r', '\f', ','],
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (ParseFloat(part) is { } value)
                    {
                        values.Add(value);
                    }
                    else
                    {
                        values.Clear();
                        break;
                    }
                }

                if (values.Count == 4 && values[2] > 0f && values[3] > 0f)
                {
                    style.AspectRatio = values[2] / values[3];
                    style.AspectRatioIsIntrinsic = true;
                }
            }
        }
    }

    private static bool AllAsciiDigits(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        foreach (char c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    internal static float? ParseFloat(string? value) =>
        value is not null
        && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : null;

    /// <summary>
    /// The selected <c>&lt;picture&gt;&lt;source&gt;</c> contributes presentation hints to its
    /// associated <c>&lt;img&gt;</c>.
    /// </summary>
    internal static void ApplyPictureSourceHints(
        DomTree tree,
        NodeId imgId,
        (float Width, float Height) viewport,
        LayoutStyle style)
    {
        if (tree.GetNode(imgId) is not { } img)
        {
            return;
        }

        if (img.AsElement() is not { } element
            || !string.Equals(element.Name.Local, "img", StringComparison.Ordinal))
        {
            return;
        }

        if (img.Parent is not { } parentId)
        {
            return;
        }

        if (!DomTraversal.IsLocal(tree, parentId, "picture"))
        {
            return;
        }

        Node? selected = null;
        foreach (NodeId childId in tree.Children(parentId))
        {
            if (childId == imgId)
            {
                break;
            }

            if (tree.GetNode(childId) is not { } source)
            {
                continue;
            }

            if (source.AsElement() is not { } sourceElement
                || !string.Equals(sourceElement.Name.Local, "source", StringComparison.Ordinal))
            {
                continue;
            }

            if (source.GetAttribute("srcset") is not { } srcset || srcset.Trim().Length == 0)
            {
                continue;
            }

            if (source.GetAttribute("media") is { } media
                && media.Trim().Length != 0
                && !CssMediaQuery.AppliesForViewport(media, viewport))
            {
                continue;
            }

            if (source.GetAttribute("type") is { } kind && !ImageCapability.SourceTypeSupported(kind))
            {
                continue;
            }

            selected = source;
            break;
        }

        if (selected is null)
        {
            return;
        }

        float? ParseDimension(string name)
        {
            float? value = ParseFloat(selected.GetAttribute(name)?.Trim());
            return value is { } parsed && float.IsFinite(parsed) && parsed > 0f ? parsed : null;
        }

        float? width = ParseDimension("width");
        float? height = ParseDimension("height");
        if (width is null && height is null)
        {
            return;
        }

        // A missing source dimension explicitly maps to auto so it replaces the corresponding
        // fallback <img> presentation hint.
        style.Width = width is { } w ? Dimension.Px(w) : Dimension.Auto;
        style.Height = height is { } h ? Dimension.Px(h) : Dimension.Auto;
        style.WidthSet = true;
        style.HeightSet = true;
        style.AspectRatio = width is { } aw && height is { } ah ? aw / ah : null;
        style.AspectRatioIsMapped = style.AspectRatio is not null;
        style.AspectRatioIsIntrinsic = style.AspectRatio is not null;
    }

    private enum MatcherBase : byte
    {
        // Push/pop on the matcher currently on top, matching the recursive descent.
        Incremental,

        // Assigned (slotted) nodes match from a matcher pre-seeded with light-DOM ancestors.
        FreshFromAncestors,

        // Shadow-root children match with an empty ancestor filter.
        FreshEmpty,
    }

    private sealed record Visit(
        NodeId Id,
        Stylesheet Sheet,
        IReadOnlyDictionary<string, string> Props,
        float? CellPadding,
        bool ColorSchemeDark,
        MatcherBase MatcherBase,
        bool UseContainerEvaluator);

    internal sealed class CascadeContext
    {
        internal required DomTree Tree { get; init; }

        internal required Stylesheet DocumentSheet { get; init; }

        internal required IReadOnlyDictionary<NodeId, Stylesheet> ShadowSheets { get; init; }

        internal required Dictionary<NodeId, LayoutStyle> Styles { get; init; }

        internal required Dictionary<NodeId, IReadOnlyDictionary<string, string>> CustomProperties { get; init; }

        internal required bool QuirksMode { get; init; }

        internal required (float Width, float Height) Viewport { get; init; }

        internal required AnimationSample AnimationSample { get; init; }

        internal required AnimationTimelineState AnimationTimeline { get; init; }

        internal HashSet<NodeId>? FreshStyles { get; init; }
    }

    /// <summary>
    /// Per-node half of the style cascade: compute and record the style for one node and
    /// report the values its subtree inherits.
    /// </summary>
    private static (IReadOnlyDictionary<string, string> Props,
        float? CellPadding,
        bool ColorSchemeDark,
        bool IsElement)? CascadeNodeStyle(
        CascadeContext context,
        NodeId id,
        Stylesheet sheet,
        Matcher matcher,
        IReadOnlyDictionary<string, string> parentProps,
        ContainerQueryEvaluator? containerEvaluator,
        float? inheritedCellPadding,
        bool inheritedColorSchemeDark)
    {
        DomTree tree = context.Tree;
        if (tree.GetNode(id) is not { } node)
        {
            return null;
        }

        bool isElement = node.IsElement;

        // The custom-property map in force for this node's subtree: the parent's, unless this
        // element declares its own `--x`.
        IReadOnlyDictionary<string, string> thisProps = parentProps;
        float? descendantCellPadding = inheritedCellPadding;
        bool descendantColorSchemeDark = inheritedColorSchemeDark;
        bool reuseStyle = isElement
            && context.FreshStyles is { } fresh
            && !fresh.Contains(id)
            && context.Styles.ContainsKey(id)
            && context.CustomProperties.ContainsKey(id);
        if (reuseStyle)
        {
            thisProps = context.CustomProperties.TryGetValue(id, out var retainedProps)
                ? retainedProps
                : parentProps;
            descendantColorSchemeDark = context.Styles.TryGetValue(id, out LayoutStyle? retained)
                && retained.ColorSchemeDark;
            if (node.AsElement() is { } table
                && string.Equals(table.Name.Local, "table", StringComparison.Ordinal))
            {
                descendantCellPadding = FilterCellPadding(node.GetAttribute("cellpadding"));
            }
        }
        else if (node.AsElement() is { } elem)
        {
            string local = elem.Name.Local;
            if (string.Equals(local, "table", StringComparison.Ordinal))
            {
                // `cellpadding` is a table-scoped presentational hint applied to its cells,
                // below author CSS. Entering any nested table resets the outer table's value.
                descendantCellPadding = FilterCellPadding(node.GetAttribute("cellpadding"));
            }

            LayoutStyle style = ComputedStyle.UaStyle(local);
            style.IsReplacedBox = Inline.IsReplaced(local);
            style.HasReplacedSizing = Inline.HasReplacedSizing(local)
                || (string.Equals(local, "input", StringComparison.Ordinal)
                    && node.GetAttribute("type") is { } imageType
                    && string.Equals(imageType, "image", StringComparison.OrdinalIgnoreCase));
            style.ColorSchemeDark = inheritedColorSchemeDark;
            if (local is "dir" or "dl" or "menu" or "ol" or "ul")
            {
                int listAncestorCount = 0;
                bool hasListOrDefinitionAncestor = false;
                NodeId? ancestor = node.Parent;
                while (ancestor is { } ancestorId)
                {
                    if (tree.GetNode(ancestorId) is not { } ancestorNode)
                    {
                        break;
                    }

                    if (ancestorNode.AsElement() is { } ancestorElement)
                    {
                        string ancestorLocal = ancestorElement.Name.Local;
                        if (ancestorLocal is "dir" or "dl" or "menu" or "ol" or "ul")
                        {
                            hasListOrDefinitionAncestor = true;
                        }

                        if (ancestorLocal is "dir" or "menu" or "ol" or "ul")
                        {
                            listAncestorCount++;
                        }
                    }

                    ancestor = ancestorNode.Parent;
                }

                if (hasListOrDefinitionAncestor)
                {
                    style.Margin = style.Margin with { Top = 0f, Bottom = 0f };
                    style.MarginRelative[0] = null;
                    style.MarginRelative[2] = null;
                }

                if (local is "dir" or "menu" or "ul")
                {
                    style.ListStyle = listAncestorCount switch
                    {
                        0 => Render.ListStyle.Disc,
                        1 => Render.ListStyle.Circle,
                        _ => Render.ListStyle.Square,
                    };
                }
            }

            if (local is "td" or "th" && inheritedCellPadding is { } padding)
            {
                style.Padding = new Edges(padding, padding, padding, padding);
            }

            if (context.QuirksMode && string.Equals(local, "form", StringComparison.Ordinal))
            {
                // Legacy HTML/quirks rendering keeps one em after forms.
                style.MarginRelative[2] = Dimension.Em(1f);
            }

            if (string.Equals(local, "input", StringComparison.Ordinal))
            {
                string inputType = (node.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();
                if (context.QuirksMode)
                {
                    style.BoxSizing = BoxSizing.BorderBox;
                }

                switch (inputType)
                {
                    case "checkbox":
                    case "radio":
                        style.Margin = new Edges(3f, 3f, 3f, 4f);
                        style.Padding = Edges.Zero;
                        style.Border = Edges.Zero;
                        break;
                    case "range":
                    case "color":
                        style.Margin = new Edges(2f, 2f, 2f, 2f);
                        style.Padding = Edges.Zero;
                        style.Border = Edges.Zero;
                        break;
                }
            }

            // UA rule `[hidden]:not([hidden=until-found]) { display: none }`, applied before
            // the author cascade so a matching author `display` still wins.
            if (node.GetAttribute("hidden") is { } hidden
                && !string.Equals(hidden, "until-found", StringComparison.OrdinalIgnoreCase))
            {
                style.Display = Display.None;
            }

            ApplyPresentationalHints(node, style);
            ApplyPictureSourceHints(tree, id, context.Viewport, style);
            string? nodeId = node.GetAttribute("id");
            List<string> classes = [];
            if (node.GetAttribute("class") is { } classAttribute)
            {
                classes.AddRange(classAttribute.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries));
            }

            Stylesheet? shadowHostSheet = null;
            if (tree.ShadowRootOf(id) is { } hostRoot
                && context.ShadowSheets.TryGetValue(hostRoot, out Stylesheet? hostSheet))
            {
                shadowHostSheet = hostSheet;
            }

            List<ShadowSlottedScope> slottedScopes = [];
            NodeId? assignedSlot = tree.AssignedSlot(id);
            for (int guard = 0; guard < tree.Count; guard++)
            {
                if (assignedSlot is not { } slot)
                {
                    break;
                }

                if (tree.ContainingShadowRoot(slot) is not { } root)
                {
                    break;
                }

                if (tree.ShadowRootInfo(root) is not { } rootInfo)
                {
                    break;
                }

                if (context.ShadowSheets.TryGetValue(root, out Stylesheet? shadowSheet))
                {
                    slottedScopes.Add(new ShadowSlottedScope(shadowSheet, rootInfo.Host));
                }

                assignedSlot = tree.AssignedSlot(slot);
            }

            Dictionary<string, string>? effectiveProps;
            if (shadowHostSheet is not null || slottedScopes.Count != 0)
            {
                effectiveProps = sheet.ApplyWithShadowScopesAtAnimationTime(
                    shadowHostSheet,
                    slottedScopes,
                    tree,
                    matcher,
                    id,
                    nodeId,
                    classes,
                    local,
                    style,
                    parentProps,
                    node.GetAttribute("style"),
                    containerEvaluator,
                    context.AnimationSample,
                    context.AnimationTimeline);
            }
            else if (containerEvaluator is { } evaluator)
            {
                effectiveProps = sheet.ApplyWithContainerQueriesAtAnimationTime(
                    tree,
                    matcher,
                    id,
                    nodeId,
                    classes,
                    local,
                    style,
                    parentProps,
                    node.GetAttribute("style"),
                    evaluator,
                    context.AnimationSample,
                    context.AnimationTimeline);
            }
            else
            {
                effectiveProps = sheet.ApplyAtAnimationTime(
                    tree,
                    matcher,
                    id,
                    nodeId,
                    classes,
                    local,
                    style,
                    parentProps,
                    node.GetAttribute("style"),
                    context.AnimationSample,
                    context.AnimationTimeline);
            }

            if (effectiveProps is not null)
            {
                thisProps = effectiveProps;
            }

            context.CustomProperties[id] = thisProps;
            style.IsReplacedBox |= style.ContentImage is not null;
            style.HasReplacedSizing |= style.ContentImage is not null;
            (LayoutStyle? beforePseudo, LayoutStyle? afterPseudo, LayoutStyle? placeholderPseudo) =
                sheet.AllPseudoStyles(tree, matcher, id, thisProps, style, containerEvaluator);
            foreach (LayoutStyle? pseudo in new[] { beforePseudo, afterPseudo })
            {
                if (pseudo is null)
                {
                    continue;
                }

                pseudo.IsReplacedBox = pseudo.ContentImage is not null;
                pseudo.HasReplacedSizing = pseudo.ContentImage is not null;
            }

            style.BeforeContent = beforePseudo is { Position: not Layout.Position.Absolute }
                ? beforePseudo.BeforeContent
                : null;
            style.AfterContent = afterPseudo is { Position: not Layout.Position.Absolute }
                ? afterPseudo.BeforeContent
                : null;
            style.BeforePseudo = beforePseudo;
            style.AfterPseudo = afterPseudo;
            style.PlaceholderPseudo = placeholderPseudo;
            descendantColorSchemeDark = style.ColorSchemeDark;
            context.Styles[id] = style;
        }

        return (thisProps, descendantCellPadding, descendantColorSchemeDark, isElement);
    }

    private static float? FilterCellPadding(string? value)
    {
        float? parsed = ParseFloat(value?.Trim());
        return parsed is { } number && float.IsFinite(number) && number >= 0f ? number : null;
    }

    /// <summary>
    /// Compute the UA + author style for every element in preorder, maintaining the matcher's
    /// ancestor filter as we descend so descendant-combinator rules fast-reject correctly.
    /// </summary>
    /// <remarks>
    /// The traversal runs on an explicit work stack. Stack usage is independent of DOM depth.
    /// </remarks>
    internal static void CascadeWalk(
        CascadeContext context,
        NodeId id,
        Stylesheet sheet,
        Matcher matcher,
        IReadOnlyDictionary<string, string> parentProps,
        ContainerQueryEvaluator? containerEvaluator,
        float? inheritedCellPadding,
        bool inheritedColorSchemeDark)
    {
        DomTree tree = context.Tree;
        List<Matcher> subtreeMatchers = [];

        // A work item is either a visit, a pop-ancestor marker, or a pop-subtree-matcher marker.
        List<(Visit? Visit, int Marker)> work =
        [
            (new Visit(
                id,
                sheet,
                parentProps,
                inheritedCellPadding,
                inheritedColorSchemeDark,
                MatcherBase.Incremental,
                true), 0),
        ];

        while (work.Count > 0)
        {
            (Visit? visit, int marker) = work[^1];
            work.RemoveAt(work.Count - 1);
            if (visit is null)
            {
                if (marker == 1)
                {
                    if (subtreeMatchers.Count > 0)
                    {
                        subtreeMatchers[^1].PopAncestor();
                    }
                    else
                    {
                        matcher.PopAncestor();
                    }
                }
                else
                {
                    subtreeMatchers.RemoveAt(subtreeMatchers.Count - 1);
                }

                continue;
            }

            if (visit.MatcherBase != MatcherBase.Incremental)
            {
                Matcher freshMatcher = tree.CreateMatcher();
                if (visit.MatcherBase == MatcherBase.FreshFromAncestors)
                {
                    List<NodeId> ancestors = tree.Ancestors(visit.Id);
                    for (int index = ancestors.Count - 1; index >= 0; index--)
                    {
                        if (tree.GetNode(ancestors[index])?.IsElement == true)
                        {
                            freshMatcher.PushAncestor(tree, ancestors[index]);
                        }
                    }
                }

                subtreeMatchers.Add(freshMatcher);

                // Runs after the subtree's own pop-ancestor markers.
                work.Add((null, 2));
            }

            Matcher currentMatcher = subtreeMatchers.Count > 0 ? subtreeMatchers[^1] : matcher;
            ContainerQueryEvaluator? evaluator = visit.UseContainerEvaluator ? containerEvaluator : null;
            var computed = CascadeNodeStyle(
                context,
                visit.Id,
                visit.Sheet,
                currentMatcher,
                visit.Props,
                evaluator,
                visit.CellPadding,
                visit.ColorSchemeDark);
            if (computed is not { } result)
            {
                continue;
            }

            if (result.IsElement)
            {
                currentMatcher.PushAncestor(tree, visit.Id);
                work.Add((null, 1));
            }

            // LIFO: push the later phases first so regular children run first, then assigned
            // nodes, then the shadow subtree, all in document order.
            if (tree.ShadowRootOf(visit.Id) is { } shadowRoot
                && context.ShadowSheets.TryGetValue(shadowRoot, out Stylesheet? shadowSheet))
            {
                List<NodeId> shadowChildren = tree.Children(shadowRoot);
                for (int index = shadowChildren.Count - 1; index >= 0; index--)
                {
                    work.Add((new Visit(
                        shadowChildren[index],
                        shadowSheet,
                        result.Props,
                        null,
                        result.ColorSchemeDark,
                        MatcherBase.FreshEmpty,
                        false), 0));
                }
            }

            if (tree.AssignedNodes(visit.Id) is { } assignedNodes)
            {
                for (int index = assignedNodes.Count - 1; index >= 0; index--)
                {
                    NodeId cid = assignedNodes[index];
                    Stylesheet assignedSheet = context.DocumentSheet;
                    if (tree.ContainingShadowRoot(cid) is { } root
                        && context.ShadowSheets.TryGetValue(root, out Stylesheet? found))
                    {
                        assignedSheet = found;
                    }

                    work.Add((new Visit(
                        cid,
                        assignedSheet,
                        result.Props,
                        result.CellPadding,
                        result.ColorSchemeDark,
                        MatcherBase.FreshFromAncestors,
                        true), 0));
                }
            }

            bool isShadowHost = tree.ShadowRootOf(visit.Id) is not null;
            List<NodeId> children = tree.Children(visit.Id);
            for (int index = children.Count - 1; index >= 0; index--)
            {
                NodeId cid = children[index];

                // Assigned light children are cascaded from their flattened slot above.
                if (isShadowHost && tree.AssignedSlot(cid) is not null)
                {
                    continue;
                }

                work.Add((new Visit(
                    cid,
                    visit.Sheet,
                    result.Props,
                    result.CellPadding,
                    result.ColorSchemeDark,
                    MatcherBase.Incremental,
                    visit.UseContainerEvaluator), 0));
            }
        }
    }

    private sealed class CssCounterState
    {
        private readonly Dictionary<string, List<int>> _values = new(StringComparer.Ordinal);

        internal List<string> Apply(
            IReadOnlyList<CounterDirective> reset,
            IReadOnlyList<CounterDirective> increment,
            IReadOnlyList<CounterDirective> set)
        {
            List<string> created = [];
            foreach (CounterDirective directive in reset)
            {
                if (!_values.TryGetValue(directive.Name, out List<int>? stack))
                {
                    stack = [];
                    _values[directive.Name] = stack;
                }

                stack.Add(directive.Value);
                created.Add(directive.Name);
            }

            foreach (CounterDirective directive in increment)
            {
                if (!_values.TryGetValue(directive.Name, out List<int>? stack))
                {
                    stack = [];
                    _values[directive.Name] = stack;
                }

                if (stack.Count == 0)
                {
                    stack.Add(0);
                    created.Add(directive.Name);
                }

                stack[^1] = SaturatingAdd(stack[^1], directive.Value);
            }

            foreach (CounterDirective directive in set)
            {
                if (!_values.TryGetValue(directive.Name, out List<int>? stack))
                {
                    stack = [];
                    _values[directive.Name] = stack;
                }

                if (stack.Count == 0)
                {
                    stack.Add(0);
                    created.Add(directive.Name);
                }

                stack[^1] = directive.Value;
            }

            return created;
        }

        private static int SaturatingAdd(int left, int right)
        {
            long sum = (long)left + right;
            return sum > int.MaxValue ? int.MaxValue : sum < int.MinValue ? int.MinValue : (int)sum;
        }

        internal void PopCreated(IReadOnlyList<string> created)
        {
            for (int index = created.Count - 1; index >= 0; index--)
            {
                if (_values.TryGetValue(created[index], out List<int>? stack))
                {
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    if (stack.Count == 0)
                    {
                        _values.Remove(created[index]);
                    }
                }
            }
        }

        internal string Render(IReadOnlyList<GeneratedContentItem> items)
        {
            System.Text.StringBuilder result = new();
            foreach (GeneratedContentItem item in items)
            {
                switch (item)
                {
                    case GeneratedContentItem.Text text:
                        result.Append(text.Value);
                        break;
                    case GeneratedContentItem.Counter counter:
                    {
                        int value = _values.TryGetValue(counter.Name, out List<int>? stack) && stack.Count > 0
                            ? stack[^1]
                            : 0;
                        result.Append(CssValues.FormatCounterValue(value, counter.Style));
                        break;
                    }

                    case GeneratedContentItem.Counters counters:
                    {
                        if (_values.TryGetValue(counters.Name, out List<int>? stack) && stack.Count > 0)
                        {
                            for (int index = 0; index < stack.Count; index++)
                            {
                                if (index != 0)
                                {
                                    result.Append(counters.Separator);
                                }

                                result.Append(CssValues.FormatCounterValue(stack[index], counters.Style));
                            }
                        }
                        else
                        {
                            result.Append(CssValues.FormatCounterValue(0, counters.Style));
                        }

                        break;
                    }
                }
            }

            return result.ToString();
        }
    }

    /// <summary>
    /// Resolve generated CSS counter text in tree order after the complete author cascade is
    /// known.
    /// </summary>
    internal static void ResolveCssCounters(DomTree tree, Dictionary<NodeId, LayoutStyle> styles)
    {
        CssCounterState counters = new();
        List<string> rootScopes = CounterWalk(tree, tree.Document, styles, counters);
        counters.PopCreated(rootScopes);
    }

    private static List<string> CounterWalk(
        DomTree tree,
        NodeId id,
        Dictionary<NodeId, LayoutStyle> styles,
        CssCounterState counters)
    {
        if (tree.GetNode(id) is null)
        {
            return [];
        }

        if (styles.TryGetValue(id, out LayoutStyle? existing) && existing.Display == Display.None)
        {
            return [];
        }

        List<string> created = styles.TryGetValue(id, out LayoutStyle? style)
            ? counters.Apply(style.CounterReset, style.CounterIncrement, style.CounterSet)
            : [];

        if (styles.TryGetValue(id, out LayoutStyle? withBefore))
        {
            if (withBefore.BeforePseudo is { } pseudo && pseudo.GeneratedContent is { } items)
            {
                pseudo.BeforeContent = counters.Render(items);
            }

            withBefore.BeforeContent =
                withBefore.BeforePseudo is { Position: not Layout.Position.Absolute } beforePseudo
                    ? beforePseudo.BeforeContent
                    : null;
        }

        List<string> childScopes = [];
        foreach (NodeId child in DomTraversal.RenderedChildren(tree, id))
        {
            childScopes.AddRange(CounterWalk(tree, child, styles, counters));
        }

        counters.PopCreated(childScopes);

        if (styles.TryGetValue(id, out LayoutStyle? withAfter))
        {
            if (withAfter.AfterPseudo is { } pseudo && pseudo.GeneratedContent is { } items)
            {
                pseudo.BeforeContent = counters.Render(items);
            }

            withAfter.AfterContent =
                withAfter.AfterPseudo is { Position: not Layout.Position.Absolute } afterPseudo
                    ? afterPseudo.BeforeContent
                    : null;
        }

        return created;
    }

    /// <summary>
    /// Compile one author stylesheet per native ShadowRoot.
    /// </summary>
    internal static Dictionary<NodeId, Stylesheet> CollectShadowStylesheets(
        DomTree tree,
        (float Width, float Height) viewport,
        CssMediaType mediaType)
    {
        List<NodeId> roots = [];
        List<NodeId> stack = [tree.Document];
        HashSet<NodeId> visited = [];
        while (stack.Count > 0)
        {
            NodeId node = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (!visited.Add(node))
            {
                continue;
            }

            if (tree.ShadowRootOf(node) is { } root)
            {
                roots.Add(root);
                List<NodeId> rootChildren = tree.Children(root);
                for (int index = rootChildren.Count - 1; index >= 0; index--)
                {
                    stack.Add(rootChildren[index]);
                }
            }

            List<NodeId> children = tree.Children(node);
            for (int index = children.Count - 1; index >= 0; index--)
            {
                stack.Add(children[index]);
            }
        }

        Dictionary<NodeId, Stylesheet> sheets = [];
        foreach (NodeId root in roots)
        {
            List<string> sources = [];
            foreach (NodeId nodeId in tree.Descendants(root))
            {
                if (tree.GetNode(nodeId) is not { } node || node.AsElement() is not { } element)
                {
                    continue;
                }

                if (!string.Equals(element.Name.Local, "style", StringComparison.Ordinal))
                {
                    continue;
                }

                string? media = node.GetAttribute("media");
                if (media is null
                    || media.Trim().Length == 0
                    || CssMediaQuery.AppliesForViewportAndType(media, viewport, mediaType))
                {
                    sources.Add(tree.TextContent(nodeId));
                }
            }

            sheets[root] = Stylesheet.ParseForViewportAndMedia(tree, sources, viewport, mediaType);
        }

        return sheets;
    }
}
