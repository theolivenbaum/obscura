// Port of the taffy tree construction in crates/obscura-render/src/dom.rs.
using Obscura.Dom;
using TaffyAlignItems = Obscura.Render.Layout.AlignItems;
using TaffyDimension = Obscura.Render.Layout.Dimension;
using TaffyDirection = Obscura.Render.Layout.Direction;
using TaffyDisplay = Obscura.Render.Layout.Display;
using TaffyFlexDirection = Obscura.Render.Layout.FlexDirection;
using TaffyFlexWrap = Obscura.Render.Layout.FlexWrap;
using TaffyJustifyContent = Obscura.Render.Layout.AlignContent;
using TaffyLengthPercentage = Obscura.Render.Layout.LengthPercentage;
using TaffyLengthPercentageAuto = Obscura.Render.Layout.LengthPercentageAuto;
using TaffyNodeId = Obscura.Render.Layout.NodeId;
using TaffyPosition = Obscura.Render.Layout.Position;
using TaffySize = Obscura.Render.Layout.Size<Obscura.Render.Layout.Dimension>;
using TaffyStyle = Obscura.Render.Layout.Style;

namespace Obscura.Render;

internal sealed class MulticolBuild
{
    internal required List<TaffyNodeId> Columns { get; init; }

    internal required List<TaffyNodeId> Children { get; init; }
}

internal readonly record struct FloatContinuation(
    NodeId Owner,
    TaffyNodeId Float,
    TaffyNodeId Flow,
    Float Side);

/// <summary>
/// Registry of shaped inline formatting contexts created during the taffy-tree build.
/// </summary>
internal sealed class IfcRegistry
{
    /// <summary>Single-leaf containers keyed by the container's own DOM id.</summary>
    internal Dictionary<NodeId, int> Whole { get; } = [];

    /// <summary>Anonymous inline-run leaves keyed by the parent block's DOM id.</summary>
    internal Dictionary<NodeId, List<int>> Runs { get; } = [];

    internal Dictionary<NodeId, List<int>> WordItems { get; } = [];

    internal List<GeneratedBoxBuild> Generated { get; } = [];

    /// <summary>Specified column widths per table grid node: (px, percent) per column index.</summary>
    internal Dictionary<TaffyNodeId, (List<float?> Px, List<float?> Percent)> TableCols { get; } = [];

    /// <summary>Column constraints for the fixed table-layout algorithm.</summary>
    internal Dictionary<TaffyNodeId, List<FixedTableColumn>> FixedTableCols { get; } = [];

    /// <summary>Minimum row heights per table grid node, one entry per source row.</summary>
    internal Dictionary<TaffyNodeId, List<float?>> TableRows { get; } = [];

    /// <summary>Floats whose exclusion can continue through later descendant blocks.</summary>
    internal List<FloatContinuation> FloatContinuations { get; } = [];

    /// <summary>Ordinary blocks participating in a native float band.</summary>
    internal HashSet<NodeId> FloatAwareBlocks { get; } = [];

    /// <summary>CSS multi-column containers built as a row of anonymous fragmentainers.</summary>
    internal List<MulticolBuild> Multicol { get; } = [];
}

internal sealed class BuildContext
{
    internal required DomTree Tree { get; init; }

    internal required Layout.TaffyTree<int?> TaffyTree { get; init; }

    internal required Dictionary<TaffyNodeId, NodeId> IdMap { get; init; }

    internal required Dictionary<TaffyNodeId, (NodeId Source, string Word)> Words { get; init; }

    internal required TextEngine Engine { get; init; }

    internal required IfcRegistry Ifc { get; init; }

    internal required IReadOnlyDictionary<NodeId, LayoutStyle> Styles { get; init; }
}

internal static partial class DomBuild
{
    /// <summary>
    /// Build whichever of an element or a text node <paramref name="id"/> is, returning every
    /// taffy node it produced.
    /// </summary>
    internal static List<TaffyNodeId> BuildAny(BuildContext context, NodeId id)
    {
        DomTree tree = context.Tree;
        if (DomTraversal.IsTextNode(tree, id))
        {
            return BuildTextWords(context, id);
        }

        // `display: contents` removes the element's own box; its children lay out as if they
        // were direct children of its parent (CSS Display 3).
        bool splicesChildren = context.Styles.TryGetValue(id, out LayoutStyle? style)
            && style.DisplayContents
            && style.Display != Display.None;
        if (splicesChildren)
        {
            List<TaffyNodeId> spliced = [];
            foreach (NodeId cid in DomTraversal.RenderedChildren(tree, id))
            {
                spliced.AddRange(BuildAny(context, cid));
            }

            return spliced;
        }

        if (IsFlattenableInline(tree, id, context.Styles))
        {
            // A plain inline wrapper around text with no box appearance of its own: real inline
            // boxes do not wrap independently, so flatten its children into the caller's list.
            List<TaffyNodeId> flattened = [];
            foreach (NodeId cid in DomTraversal.RenderedChildren(tree, id))
            {
                flattened.AddRange(BuildAny(context, cid));
            }

            return flattened;
        }

        if (Build(context, id) is not { } inner)
        {
            return [];
        }

        TaffyAlignItems? inlineAlign = null;
        if (style is not null && LayoutStyleExtensions.IsInlineLevelBox(style))
        {
            inlineAlign = style.VerticalAlign switch
            {
                VerticalAlign.Middle => TaffyAlignItems.Center,
                VerticalAlign.Bottom => TaffyAlignItems.FlexEnd,
                VerticalAlign.Top => TaffyAlignItems.FlexStart,
                _ => null,
            };
        }

        bool needsOuter = style is not null
            && style.IsInlineBlock
            && style.Display is Display.Flex or Display.Grid
            && style.Width.IsAuto
            && style.SizeExpressions[0] is null;
        if (!needsOuter)
        {
            if (inlineAlign is { } align)
            {
                TaffyStyle adjusted = context.TaffyTree.GetStyle(inner).Clone();
                adjusted.AlignSelf = align;
                context.TaffyTree.SetStyle(inner, adjusted);
            }

            return [inner];
        }

        // Taffy stores only the inner display mode. A transparent atomic outer node supplies
        // the shrink-wrapping inline participation while the authored node keeps its real
        // Flex/Grid layout and remains the DOM geometry/paint owner.
        bool transfersInlineConstraint = style is not null
            && (!style.MinWidth.IsAuto
                || !style.MaxWidth.IsAuto
                || style.SizeExpressions[2] is not null
                || style.SizeExpressions[4] is not null);
        TaffyDimension outerMinWidth = TaffyDimension.Auto;
        TaffyDimension outerMaxWidth = TaffyDimension.Auto;
        if (transfersInlineConstraint)
        {
            TaffyStyle innerStyle = context.TaffyTree.GetStyle(inner);
            outerMinWidth = innerStyle.MinSize.Width;
            outerMaxWidth = innerStyle.MaxSize.Width;
            TaffyStyle adjusted = innerStyle.Clone();
            TaffySize size = adjusted.Size;
            size.Width = TaffyDimension.FromPercent(1f);
            adjusted.Size = size;
            TaffySize minSize = adjusted.MinSize;
            minSize.Width = TaffyDimension.Auto;
            adjusted.MinSize = minSize;
            TaffySize maxSize = adjusted.MaxSize;
            maxSize.Width = TaffyDimension.Auto;
            adjusted.MaxSize = maxSize;
            context.TaffyTree.SetStyle(inner, adjusted);
        }

        TaffyStyle outerStyle = TaffyStyle.Default;
        outerStyle.Direction = style?.Direction ?? TaffyDirection.Ltr;
        outerStyle.Display = TaffyDisplay.Flex;
        outerStyle.FlexDirection = TaffyFlexDirection.Row;
        outerStyle.FlexWrap = TaffyFlexWrap.Wrap;
        outerStyle.AlignItems = TaffyAlignItems.FlexStart;
        outerStyle.AlignSelf = inlineAlign;
        outerStyle.MinSize = new Layout.Size<TaffyDimension>(outerMinWidth, TaffyDimension.Auto);
        outerStyle.MaxSize = new Layout.Size<TaffyDimension>(outerMaxWidth, TaffyDimension.Auto);
        TaffyNodeId outer = context.TaffyTree.NewWithChildren(outerStyle, [inner]);
        return [outer];
    }

    /// <summary>Build the direct children of a genuine flex/grid container.</summary>
    /// <remarks>
    /// CSS wraps every contiguous run of in-flow text in one anonymous flex/grid item whose
    /// contents form an inline formatting context. Splitting a text node into one taffy item
    /// per word is only valid inside our flex-wrap IFC stand-in.
    /// </remarks>
    internal static List<TaffyNodeId> BuildFlexGridChildren(BuildContext context, NodeId parent)
    {
        DomTree tree = context.Tree;
        List<EffectiveGridChild> effectiveChildren = [];
        DomStyleFixups.CollectEffectiveGridChildren(
            tree, DomTraversal.RenderedChildren(tree, parent), context.Styles, effectiveChildren);
        effectiveChildren.RemoveAll(child =>
            child.Kind == EffectiveGridChildKind.Dom
            && tree.GetNode(child.Node)?.IsElement != true
            && tree.TextContent(child.Node).Trim().Length == 0);

        // CSS `order` uses a stable sort so source order is the tie-break.
        List<EffectiveGridChild> ordered = [.. effectiveChildren
            .Select((child, index) => (child, index))
            .OrderBy(pair => DomStyleFixups.EffectiveGridChildStyle(pair.child, context.Styles)?.Order ?? 0)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.child)];

        List<TaffyNodeId> children = [];
        int cursor = 0;
        while (cursor < ordered.Count)
        {
            EffectiveGridChild current = ordered[cursor];
            bool isText = current.Kind == EffectiveGridChildKind.Dom
                && DomTraversal.IsTextNode(tree, current.Node);
            if (!isText)
            {
                if (current.Kind == EffectiveGridChildKind.Dom)
                {
                    children.AddRange(BuildAny(context, current.Node));
                }
                else
                {
                    LayoutStyle? pseudo = DomStyleFixups.EffectiveGridChildStyle(current, context.Styles);
                    if (BuildInFlowPseudo(context, current.Node, current.Generated, pseudo)
                        is { } generated)
                    {
                        children.AddRange(generated.Nodes);
                    }
                }

                cursor++;
                continue;
            }

            List<NodeId> run = [];
            while (cursor < ordered.Count
                && ordered[cursor].Kind == EffectiveGridChildKind.Dom
                && DomTraversal.IsTextNode(tree, ordered[cursor].Node))
            {
                run.Add(ordered[cursor].Node);
                cursor++;
            }

            if (context.Engine.TryBuildRun(tree, parent, run, context.Styles) is { } item)
            {
                TaffyStyle style = TaffyStyle.Default;
                style.Display = TaffyDisplay.Block;
                TaffyNodeId leaf = context.TaffyTree.NewLeafWithContext(style, item);
                if (!context.Ifc.Runs.TryGetValue(parent, out List<int>? items))
                {
                    items = [];
                    context.Ifc.Runs[parent] = items;
                }

                items.Add(item);
                children.Add(leaf);
                continue;
            }

            foreach (NodeId text in run)
            {
                children.AddRange(BuildAny(context, text));
            }
        }

        return children;
    }

    /// <summary>
    /// Is <paramref name="id"/> a <c>display: inline</c> element with no box appearance or
    /// sizing of its own?
    /// </summary>
    internal static bool IsFlattenableInline(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (tree.GetNode(id) is not { } node || node.AsElement() is not { } element)
        {
            return false;
        }

        // BR is boxless-looking but semantically contributes a mandatory break.
        if (string.Equals(element.Name.Local, "br", StringComparison.Ordinal))
        {
            return false;
        }

        if (!styles.TryGetValue(id, out LayoutStyle? style))
        {
            return false;
        }

        return style.Display == Display.Inline
            && !style.IsInlineBlock
            && !style.IsReplacedBox
            && style.BeforePseudo is null
            && style.AfterPseudo is null
            && style.BackgroundColor is null
            && style.BackgroundImage is null
            && style.MaskImage is null
            && style.Border == Edges.Zero
            && style.Position is null
            && !style.OverflowHidden
            && style.Float is null;
    }

    /// <summary>
    /// Split a text node into one taffy leaf per word, so it can wrap across several lines
    /// within its container instead of being one indivisible box.
    /// </summary>
    internal static List<TaffyNodeId> BuildTextWords(BuildContext context, NodeId id)
    {
        DomTree tree = context.Tree;
        if (tree.GetNode(id) is not { } node || node.TextContentOfTextNode is not { } contents)
        {
            return [];
        }

        System.Text.StringBuilder collapsed = new();
        bool inSpace = false;
        foreach (char c in contents)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inSpace)
                {
                    collapsed.Append(' ');
                    inSpace = true;
                }
            }
            else
            {
                collapsed.Append(c);
                inSpace = false;
            }
        }

        string displayText = collapsed.ToString();

        float fsize = 16f;
        bool isBold = false;
        string? family = null;
        float lineHeight = fsize * 1.2f;
        TextTransform transform = TextTransform.None;
        float letterSpacing = 0f;
        NodeId? parentId = DomTraversal.RenderedParent(tree, id);
        if (parentId is { } parent && context.Styles.TryGetValue(parent, out LayoutStyle? parentStyle))
        {
            fsize = parentStyle.FontSize ?? 16f;
            isBold = ComputedStyle.UsedFontWeight(parentStyle) >= 600;
            family = parentStyle.FontFamily;
            lineHeight = FontResolution.UsedLineHeight(parentStyle);
            transform = parentStyle.TextTransform ?? TextTransform.None;
            letterSpacing = parentStyle.LetterSpacing ?? 0f;

            List<TaffyNodeId> shaped = BuildShapedWordLeaves(context, id, displayText, parentStyle);
            if (shaped.Count != 0)
            {
                return shaped;
            }
        }

        displayText = TransformWordLeafText(displayText, transform);
        return BuildWordLeaves(context, id, displayText, fsize, lineHeight, isBold, family, letterSpacing);
    }

    /// <summary>Webfont-aware counterpart to <see cref="BuildWordLeaves"/>.</summary>
    internal static List<TaffyNodeId> BuildShapedWordLeaves(
        BuildContext context,
        NodeId sourceId,
        string text,
        LayoutStyle style)
    {
        List<TaffyNodeId> leaves = [];
        foreach (string token in TokenizeWithSpaces(text))
        {
            if (token.Trim().Length == 0)
            {
                continue;
            }

            // A token normally retains one trailing collapsed space. Shape it as preformatted
            // content so the item keeps that advance.
            LayoutStyle tokenStyle = style.Clone();
            tokenStyle.WhiteSpace = Render.WhiteSpace.Pre;
            if (context.Engine.PushGeneratedText(token, tokenStyle) is not { } item)
            {
                // Returning no leaves makes the caller use the deterministic static-font
                // fallback for the whole text node.
                return [];
            }

            (float width, float height) = context.Engine.MeasureWord(item);
            TaffyStyle taffyStyle = TaffyStyle.Default;
            taffyStyle.Size = new Layout.Size<TaffyDimension>(
                TaffyDimension.FromLength(F32.Max(width, 0f)),
                TaffyDimension.FromLength(F32.Max(height, 0f)));
            TaffyNodeId leaf = context.TaffyTree.NewLeafWithContext(taffyStyle, item);
            context.Words[leaf] = (
                sourceId,
                TransformWordLeafText(token, style.TextTransform ?? TextTransform.None));
            if (!context.Ifc.WordItems.TryGetValue(sourceId, out List<int>? items))
            {
                items = [];
                context.Ifc.WordItems[sourceId] = items;
            }

            items.Add(item);
            leaves.Add(leaf);
        }

        return leaves;
    }

    internal static string TransformWordLeafText(string text, TextTransform transform)
    {
        switch (transform)
        {
            case TextTransform.None:
                return text;
            case TextTransform.Uppercase:
                return text.ToUpperInvariant();
            case TextTransform.Lowercase:
                return text.ToLowerInvariant();
            case TextTransform.Capitalize:
            {
                bool atWordStart = true;
                System.Text.StringBuilder output = new(text.Length);
                // Rust iterates Unicode scalar values; enumerate runes so an astral letter
                // at a word start is title-cased instead of leaving a surrogate half alone.
                foreach (System.Text.Rune rune in text.EnumerateRunes())
                {
                    if (System.Text.Rune.IsWhiteSpace(rune))
                    {
                        atWordStart = true;
                        output.Append(rune.ToString());
                    }
                    else if (atWordStart)
                    {
                        output.Append(System.Text.Rune.ToUpperInvariant(rune).ToString());
                        atWordStart = false;
                    }
                    else
                    {
                        output.Append(rune.ToString());
                    }
                }

                return output.ToString();
            }

            default:
                return text;
        }
    }

    /// <summary>
    /// Split <paramref name="text"/> into one taffy leaf per word and register each against
    /// <paramref name="sourceId"/>.
    /// </summary>
    internal static List<TaffyNodeId> BuildWordLeaves(
        BuildContext context,
        NodeId sourceId,
        string text,
        float fsize,
        float lineHeight,
        bool isBold,
        string? family,
        float letterSpacing)
    {
        List<TaffyNodeId> leaves = [];
        foreach (string token in TokenizeWithSpaces(text))
        {
            float width = DomTextMeasure.TextWidth(token, fsize, isBold, family, letterSpacing);

            // A pure-whitespace token keeps its width so adjacent inline content stays visually
            // separated, but contributes no height.
            float height = token.Trim().Length == 0 ? 0f : F32.Max(lineHeight, 0f);
            TaffyStyle taffyStyle = TaffyStyle.Default;
            taffyStyle.Size = new Layout.Size<TaffyDimension>(
                TaffyDimension.FromLength(width),
                TaffyDimension.FromLength(height));
            TaffyNodeId leaf = context.TaffyTree.NewLeaf(taffyStyle);
            context.Words[leaf] = (sourceId, token);
            leaves.Add(leaf);
        }

        return leaves;
    }

    /// <summary>
    /// Build the word leaves for a <c>::before</c>/<c>::after</c> literal, registered against
    /// the host element's own id.
    /// </summary>
    internal static List<TaffyNodeId> BuildPseudoContent(
        BuildContext context,
        NodeId id,
        string content,
        LayoutStyle style)
    {
        List<TaffyNodeId> shaped = BuildShapedWordLeaves(context, id, content, style);
        if (shaped.Count != 0)
        {
            return shaped;
        }

        float fsize = style.FontSize ?? 16f;
        bool isBold = ComputedStyle.UsedFontWeight(style) >= 600;
        return BuildWordLeaves(
            context,
            id,
            content,
            fsize,
            FontResolution.UsedLineHeight(style),
            isBold,
            style.FontFamily,
            style.LetterSpacing ?? 0f);
    }

    internal static bool PseudoRequiresGeneratedBox(LayoutStyle style, string? content)
    {
        if (content is not null && content.Length == 0)
        {
            return true;
        }

        foreach (bool value in style.MarginAuto)
        {
            if (value)
            {
                return true;
            }
        }

        foreach (float? value in style.PaddingPercent)
        {
            if (value is not null)
            {
                return true;
            }
        }

        return style.ContentImage is not null
            || style.Display != Display.Inline
            || style.IsInlineBlock
            || style.Position is not null
            || style.Margin != Edges.Zero
            || style.Padding != Edges.Zero
            || style.Border != Edges.Zero
            || style.BackgroundColor is not null
            || style.BackgroundGradient is not null
            || style.BackgroundRadialGradient is not null
            || style.BackgroundConicGradient is not null
            || style.BackgroundImage is not null
            || style.MaskImage is not null
            || style.BoxShadow is not null
            || !style.BorderModel.Radii.IsZero()
            || style.OverflowHidden
            || style.TransformOps.Count != 0
            || style.IndividualTranslate is not null
            || style.IndividualRotate is not null
            || style.IndividualScale is not null;
    }

    internal static bool HasInFlowGeneratedPseudo(LayoutStyle? pseudo) =>
        pseudo is not null
        && pseudo.Display != Display.None
        && pseudo.Position != TaffyPosition.Absolute;

    /// <summary>
    /// Build one in-flow pseudo as either zero-overhead generated text leaves or a real
    /// anonymous box.
    /// </summary>
    internal static (List<TaffyNodeId> Nodes, bool BlockLevel)? BuildInFlowPseudo(
        BuildContext context,
        NodeId host,
        GeneratedBoxKind kind,
        LayoutStyle? pseudo)
    {
        if (pseudo is null)
        {
            return null;
        }

        if (pseudo.Position == TaffyPosition.Absolute || pseudo.Display == Display.None)
        {
            return null;
        }

        string? content = pseudo.BeforeContent;
        if (!PseudoRequiresGeneratedBox(pseudo, content))
        {
            if (content is null)
            {
                return null;
            }

            List<TaffyNodeId> leaves = BuildPseudoContent(context, host, content, pseudo);
            return leaves.Count != 0 ? (leaves, false) : null;
        }

        List<TaffyNodeId> children = [];

        // CSS table fixup discards whitespace-only text between table structures.
        if (content is { Length: > 0 } && !(pseudo.IsTableBox && content.Trim().Length == 0))
        {
            children = BuildPseudoContent(context, host, content, pseudo);
        }

        TaffyStyle taffyStyle = TaffyStyleMapping.ToTaffyStyle(pseudo);

        // A block pseudo's outer participation is block-level, but its generated text still
        // forms an inline formatting context inside it.
        if (pseudo.Display == Display.Block && children.Count != 0)
        {
            taffyStyle.Display = TaffyDisplay.Flex;
            taffyStyle.FlexDirection = TaffyFlexDirection.Row;
            taffyStyle.FlexWrap = TaffyFlexWrap.Wrap;
        }

        TaffyNodeId node = children.Count == 0
            ? context.TaffyTree.NewLeaf(taffyStyle)
            : context.TaffyTree.NewWithChildren(taffyStyle, [.. children]);
        context.Ifc.Generated.Add(new GeneratedBoxBuild(host, kind, node));
        bool blockLevel = pseudo.Display != Display.Inline && !pseudo.IsInlineBlock;
        return ([node], blockLevel);
    }

    /// <summary>
    /// Split already whitespace-collapsed text into tokens, each carrying at most one trailing
    /// space (<c>"Hello World "</c> -&gt; <c>["Hello ", "World "]</c>).
    /// </summary>
    internal static List<string> TokenizeWithSpaces(string text)
    {
        List<string> tokens = [];
        System.Text.StringBuilder current = new();
        foreach (char c in text)
        {
            current.Append(c);
            if (c == ' ')
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length != 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>
    /// Expand <c>display: contents</c> wrappers so their children partition into the caller's
    /// segment list as if they were direct children (CSS Display 3).
    /// </summary>
    internal static void FlattenContentsChildren(
        DomTree tree,
        IReadOnlyList<NodeId> children,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        List<NodeId> output)
    {
        foreach (NodeId cid in children)
        {
            bool splices = styles.TryGetValue(cid, out LayoutStyle? style)
                && style.DisplayContents
                && style.Display != Display.None;
            if (splices)
            {
                FlattenContentsChildren(tree, DomTraversal.RenderedChildren(tree, cid), styles, output);
            }
            else
            {
                output.Add(cid);
            }
        }
    }

    /// <summary>Does an inline contribute only in-flow block children?</summary>
    internal static bool InlineWrapsOnlyInFlowBlocks(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (!styles.TryGetValue(id, out LayoutStyle? style))
        {
            return false;
        }

        bool anyInset = false;
        foreach (Dimension? inset in style.Inset)
        {
            if (inset is not null)
            {
                anyInset = true;
                break;
            }
        }

        if (!anyInset)
        {
            foreach (string? expression in style.InsetExpressions)
            {
                if (expression is not null)
                {
                    anyInset = true;
                    break;
                }
            }
        }

        if (style.Display != Display.Inline
            || style.IsInlineBlock
            || style.BeforePseudo is not null
            || style.AfterPseudo is not null
            || style.Float is not null
            || style.Position == TaffyPosition.Absolute
            || (style.Position is not null && anyInset))
        {
            return false;
        }

        bool sawBlock = false;
        foreach (NodeId cid in DomTraversal.RenderedChildren(tree, id))
        {
            if (tree.GetNode(cid) is not { } node)
            {
                continue;
            }

            if (node.TextContentOfTextNode is { } contents)
            {
                if (contents.Trim().Length != 0)
                {
                    return false;
                }

                continue;
            }

            if (!node.IsElement)
            {
                continue;
            }

            if (!styles.TryGetValue(cid, out LayoutStyle? child))
            {
                return false;
            }

            if (child.Display == Display.None)
            {
                continue;
            }

            if (child.Position == TaffyPosition.Absolute || child.Float is not null)
            {
                return false;
            }

            if (child.DisplayContents)
            {
                if (!InlineWrapsOnlyInFlowBlocks(tree, cid, styles))
                {
                    return false;
                }

                sawBlock = true;
            }
            else if (DomStyleFixups.IsInFlowBlockLevel(child))
            {
                sawBlock = true;
            }
            else
            {
                return false;
            }
        }

        return sawBlock;
    }

    /// <summary>
    /// Recursively splice <c>display:contents</c>, decoration-free inline wrappers, and inline
    /// wrappers whose only generated flow content is block-level.
    /// </summary>
    internal static void FlattenBoxlessInlineChildren(
        DomTree tree,
        IReadOnlyList<NodeId> children,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        List<NodeId> output)
    {
        foreach (NodeId cid in children)
        {
            bool displayContents = styles.TryGetValue(cid, out LayoutStyle? style)
                && style.DisplayContents
                && style.Display != Display.None;
            if (displayContents
                || IsFlattenableInline(tree, cid, styles)
                || InlineWrapsOnlyInFlowBlocks(tree, cid, styles))
            {
                FlattenBoxlessInlineChildren(
                    tree, DomTraversal.RenderedChildren(tree, cid), styles, output);
            }
            else
            {
                output.Add(cid);
            }
        }
    }

    /// <summary>
    /// Style for a single-leaf inline run: a block-level box that fills the containing block's
    /// width, with height from the shaped text.
    /// </summary>
    internal static TaffyStyle RunLeafStyle()
    {
        TaffyStyle style = TaffyStyle.Default;
        style.Size = new Layout.Size<TaffyDimension>(
            TaffyDimension.FromPercent(1f),
            TaffyDimension.Auto);
        return style;
    }

    /// <summary>Style for an anonymous inline-run wrapper.</summary>
    internal static TaffyStyle RunWrapperStyle(LayoutStyle parent, bool hasTextStrut)
    {
        TaffyJustifyContent? justify = parent.TextAlign switch
        {
            { } align when align == TaffyAlignItems.FlexEnd => TaffyJustifyContent.FlexEnd,
            { } align when align == TaffyAlignItems.Center => TaffyJustifyContent.Center,
            _ => null,
        };
        float lineHeight = hasTextStrut ? F32.Max(FontResolution.UsedLineHeight(parent), 0f) : 0f;
        TaffyStyle style = TaffyStyle.Default;
        style.Direction = parent.Direction ?? TaffyDirection.Ltr;
        style.Display = TaffyDisplay.Flex;
        style.FlexDirection = TaffyFlexDirection.Row;
        style.FlexWrap = TaffyFlexWrap.Wrap;
        style.AlignItems = TaffyAlignItems.FlexStart;
        style.JustifyContent = justify;
        style.Size = new Layout.Size<TaffyDimension>(
            TaffyDimension.FromPercent(1f),
            TaffyDimension.Auto);

        // Every CSS line box starts with the parent's font/line-height strut.
        style.MinSize = new Layout.Size<TaffyDimension>(
            TaffyDimension.Auto,
            TaffyDimension.FromLength(lineHeight));
        return style;
    }

    internal static void SetNativeFloatClear(
        BuildContext context,
        TaffyNodeId node,
        LayoutStyle style,
        bool generatedPseudo)
    {
        TaffyStyle native = context.TaffyTree.GetStyle(node).Clone();
        native.Float = style.Float switch
        {
            Float.Left => Layout.Float.Left,
            Float.Right => Layout.Float.Right,
            _ => Layout.Float.None,
        };
        native.Clear = style.Clear switch
        {
            Clear.Left => Layout.Clear.Left,
            Clear.Right => Layout.Clear.Right,
            Clear.Both => Layout.Clear.Both,
            _ => Layout.Clear.None,
        };
        if (generatedPseudo && style.IsTableBox)
        {
            // Bootstrap-style clearfix pseudos use display:table only to generate an empty
            // block formatting box. Taffy's independent table-item clear path is incomplete.
            native.Display = TaffyDisplay.Block;
            native.ItemIsTable = false;
        }

        context.TaffyTree.SetStyle(node, native);
    }

    internal static List<TaffyNodeId> BuildChildrenWithNativeFloatBand(
        BuildContext context,
        NodeId parentId,
        LayoutStyle parentStyle,
        IReadOnlyList<NodeId> domChildren)
    {
        List<TaffyNodeId> result = [];
        foreach ((GeneratedBoxKind kind, LayoutStyle? pseudo) in new[]
        {
            (GeneratedBoxKind.Before, parentStyle.BeforePseudo),
            (GeneratedBoxKind.After, parentStyle.AfterPseudo),
        })
        {
            if (kind == GeneratedBoxKind.After)
            {
                foreach (NodeId id in domChildren)
                {
                    if (!context.Styles.TryGetValue(id, out LayoutStyle? style))
                    {
                        continue;
                    }

                    if (style.Float is null
                        && style.Display == Display.Block
                        && !DomStyleFixups.EstablishesBlockFormattingContext(style))
                    {
                        context.Ifc.FloatAwareBlocks.Add(id);
                    }

                    foreach (TaffyNodeId node in BuildAny(context, id))
                    {
                        SetNativeFloatClear(context, node, style, false);
                        result.Add(node);
                    }
                }
            }

            if (BuildInFlowPseudo(context, parentId, kind, pseudo) is { } built && pseudo is not null)
            {
                foreach (TaffyNodeId node in built.Nodes)
                {
                    SetNativeFloatClear(context, node, pseudo, true);
                    result.Add(node);
                }
            }
        }

        return result;
    }

    internal static NodeId? InlineWrapperFloat(
        DomTree tree,
        NodeId wrapper,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (!styles.TryGetValue(wrapper, out LayoutStyle? wrapperStyle))
        {
            return null;
        }

        bool anyPaddingPercent = false;
        foreach (float? value in wrapperStyle.PaddingPercent)
        {
            if (value is not null)
            {
                anyPaddingPercent = true;
                break;
            }
        }

        if (wrapperStyle.Display != Display.Inline
            || wrapperStyle.Margin != Edges.Zero
            || wrapperStyle.Padding != Edges.Zero
            || anyPaddingPercent
            || wrapperStyle.Border != Edges.Zero
            || wrapperStyle.BeforePseudo is not null
            || wrapperStyle.AfterPseudo is not null)
        {
            return null;
        }

        List<NodeId> visible = [];
        foreach (NodeId child in tree.Children(wrapper))
        {
            if (tree.GetNode(child) is not { } node)
            {
                continue;
            }

            if (!node.IsElement)
            {
                if (tree.TextContent(child).Trim().Length != 0)
                {
                    visible.Add(child);
                }

                continue;
            }

            if (styles.TryGetValue(child, out LayoutStyle? childStyle)
                && childStyle.Display != Display.None)
            {
                visible.Add(child);
            }
        }

        if (visible.Count != 1)
        {
            return null;
        }

        return styles.TryGetValue(visible[0], out LayoutStyle? only) && only.Float is not null
            ? visible[0]
            : null;
    }

    internal static bool NeedsColumnFlexTextFitContentCap(
        DomTree tree,
        NodeId id,
        LayoutStyle style,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (style.Display != Display.Flex
            || style.InternalFlexContainer
            || style.IsInlineBlock
            || style.Float is not null
            || style.Position == TaffyPosition.Absolute
            || style.AspectRatio is not null
            || !style.Width.IsAuto
            || !style.MinWidth.IsAuto
            || !style.MaxWidth.IsAuto
            || style.SizeExpressions[0] is not null
            || style.SizeExpressions[2] is not null
            || style.SizeExpressions[4] is not null
            || style.Padding.Left != 0f
            || style.Padding.Right != 0f
            || style.PaddingPercent[1] is not null
            || style.PaddingPercent[3] is not null
            || style.Border.Left != 0f
            || style.Border.Right != 0f
            || style.Margin.Left != 0f
            || style.Margin.Right != 0f
            || style.MarginAuto[1]
            || style.MarginAuto[3]
            || style.MarginPercent[1] is not null
            || style.MarginPercent[3] is not null
            || style.MarginRelative[1] is not null
            || style.MarginRelative[3] is not null
            || style.MarginExpressions[1] is not null
            || style.MarginExpressions[3] is not null)
        {
            return false;
        }

        bool hasDirectText = false;
        foreach (NodeId child in DomTraversal.RenderedChildren(tree, id))
        {
            if (tree.GetNode(child)?.TextContentOfTextNode is { } contents
                && contents.Trim().Length != 0)
            {
                hasDirectText = true;
                break;
            }
        }

        if (!hasDirectText)
        {
            return false;
        }

        NodeId? parent = DomTraversal.RenderedParent(tree, id);
        LayoutStyle parentStyle;
        while (true)
        {
            if (parent is not { } parentId)
            {
                return false;
            }

            if (!styles.TryGetValue(parentId, out LayoutStyle? found))
            {
                return false;
            }

            if (found.DisplayContents)
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
                continue;
            }

            parentStyle = found;
            break;
        }

        if (parentStyle.Display != Display.Flex
            || parentStyle.InternalFlexContainer
            || parentStyle.FlexDirection
                is not (TaffyFlexDirection.Column or TaffyFlexDirection.ColumnReverse))
        {
            return false;
        }

        return DomStyleFixups.UsedFlexAlignment(style.AlignSelf, parentStyle.AlignItems)
            != TaffyAlignItems.Stretch;
    }
}
