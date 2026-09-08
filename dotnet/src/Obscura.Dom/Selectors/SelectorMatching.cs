namespace Obscura.Dom.Selectors;

public enum QuirksMode
{
    NoQuirks,
    Quirks,
}

/// <summary>State carried through one selector match.</summary>
public sealed class MatchingContext
{
    public MatchingContext(QuirksMode quirksMode)
    {
        QuirksMode = quirksMode;
    }

    public QuirksMode QuirksMode { get; }

    /// <summary>The ancestor bloom filter, when the caller drives a treewalk cascade.</summary>
    public BloomFilter? BloomFilter { get; set; }

    /// <summary>The shadow host whose tree scope is being matched, for <c>:host</c>/<c>::slotted()</c>.</summary>
    public NodeId? CurrentHost { get; set; }

    /// <summary>The <c>:has()</c> anchor while a relative selector is being matched.</summary>
    internal NodeId? RelativeSelectorAnchor { get; set; }

    internal int NestingLevel { get; set; }

    internal bool InNegation { get; set; }

    internal CaseSensitivity ClassesAndIdsCaseSensitivity => QuirksMode == QuirksMode.Quirks
        ? CaseSensitivity.AsciiCaseInsensitive
        : CaseSensitivity.CaseSensitive;
}

/// <summary>The selector matching algorithm, ported from the reference engine's matching module.</summary>
public static class SelectorMatching
{
    private enum SelectorMatchingResult
    {
        Matched,
        NotMatchedAndRestartFromClosestLaterSibling,
        NotMatchedAndRestartFromClosestDescendant,
        NotMatchedGlobally,
    }

    public static bool MatchesSelectorList(SelectorList list, DomElement element, MatchingContext context)
    {
        foreach (var selector in list.Selectors)
        {
            if (MatchesSelector(selector, hashes: null, element, context))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Match one complex selector, fast-rejecting against the ancestor bloom filter.</summary>
    public static bool MatchesSelector(
        Selector selector,
        AncestorHashes? hashes,
        DomElement element,
        MatchingContext context)
    {
        if (hashes is not null && context.BloomFilter is { } filter && !hashes.MayMatch(filter))
        {
            return false;
        }

        return MatchesComplexSelector(selector, element, context);
    }

    private static bool MatchesComplexSelector(Selector selector, DomElement element, MatchingContext context) =>
        MatchesComplexSelectorInternal(selector, 0, element, context) == SelectorMatchingResult.Matched;

    private static bool MatchesComplexSelectorList(
        Selector[] selectors,
        DomElement element,
        MatchingContext context)
    {
        foreach (var selector in selectors)
        {
            if (MatchesComplexSelector(selector, element, context))
            {
                return true;
            }
        }

        return false;
    }

    private static SelectorMatchingResult MatchesComplexSelectorInternal(
        Selector selector,
        int index,
        DomElement element,
        MatchingContext context)
    {
        if (!MatchesCompoundSelector(selector.Compounds[index], element, context))
        {
            return SelectorMatchingResult.NotMatchedAndRestartFromClosestLaterSibling;
        }

        if (index >= selector.Combinators.Length)
        {
            return SelectorMatchingResult.Matched;
        }

        var combinator = selector.Combinators[index];
        var candidateNotFound = combinator switch
        {
            Combinator.NextSibling or Combinator.LaterSibling =>
                SelectorMatchingResult.NotMatchedAndRestartFromClosestDescendant,
            _ => SelectorMatchingResult.NotMatchedGlobally,
        };

        var current = element;
        while (true)
        {
            if (NextElementForCombinator(current, combinator, selector, index + 1, context)
                is not { } next)
            {
                return candidateNotFound;
            }

            current = next;
            var result = MatchesComplexSelectorInternal(selector, index + 1, current, context);
            if (result == SelectorMatchingResult.Matched)
            {
                return result;
            }

            if (result == SelectorMatchingResult.NotMatchedGlobally
                || combinator == Combinator.NextSibling)
            {
                return result;
            }

            if (combinator is Combinator.PseudoElement or Combinator.Child)
            {
                // Upgrade the failure status to NotMatchedAndRestartFromClosestDescendant.
                return SelectorMatchingResult.NotMatchedAndRestartFromClosestDescendant;
            }

            if (result == SelectorMatchingResult.NotMatchedAndRestartFromClosestDescendant
                && combinator == Combinator.LaterSibling)
            {
                // Give up this later-sibling matching and restart from the closest descendant
                // combinator.
                return result;
            }

            // Otherwise continue to the next candidate element.
        }
    }

    private static DomElement? NextElementForCombinator(
        DomElement element,
        Combinator combinator,
        Selector selector,
        int nextIndex,
        MatchingContext context)
    {
        switch (combinator)
        {
            case Combinator.NextSibling:
            case Combinator.LaterSibling:
                return element.PrevSiblingElement();

            case Combinator.Child:
            case Combinator.Descendant:
            {
                if (element.ParentElement() is { } parent)
                {
                    return parent;
                }

                if (!element.ParentNodeIsShadowRoot())
                {
                    return null;
                }

                // https://drafts.csswg.org/css-scoping/#host-element-in-tree: a shadow host also
                // appears in its shadow tree, but is featureless there, so only :host / :host()
                // selectors are allowed to match it.
                var featureless = CompoundFeaturelessHostMatches(selector, nextIndex);
                if ((featureless & FeaturelessHostMatches.ForHost) != 0)
                {
                    return element.ContainingShadowHost();
                }

                // :scope is never bound to a scope element here, so the ForScope branch cannot
                // walk out of the shadow tree.
                return null;
            }

            case Combinator.SlotAssignment:
                return AssignedSlot(element, context);

            case Combinator.PseudoElement:
                return element.PseudoElementOriginatingElement();

            default:
                return null;
        }
    }

    private static DomElement? AssignedSlot(DomElement element, MatchingContext context)
    {
        if (context.CurrentHost is not { } scope)
        {
            return null;
        }

        if (element.AssignedSlot() is not { } slot)
        {
            return null;
        }

        var current = slot;
        while (current.ContainingShadowHost() is { } host && host.Opaque != scope)
        {
            if (current.AssignedSlot() is not { } next)
            {
                return null;
            }

            current = next;
        }

        return current.ContainingShadowHost() is { } finalHost && finalHost.Opaque == scope
            ? current
            : null;
    }

    /// <summary>
    /// Whether the compound at <paramref name="index"/> is a featureless-host-only compound and is
    /// the leftmost compound, which is what allows a match to walk out of a shadow tree onto its
    /// host.
    /// </summary>
    private static FeaturelessHostMatches CompoundFeaturelessHostMatches(Selector selector, int index)
    {
        if (index >= selector.Compounds.Length)
        {
            return FeaturelessHostMatches.None;
        }

        var compound = selector.Compounds[index];
        if (compound.Components.Length == 0)
        {
            return FeaturelessHostMatches.None;
        }

        var result = FeaturelessHostMatches.None;
        foreach (var component in compound.Components)
        {
            var componentMatches = ComponentFeaturelessHostMatches(component);
            if (componentMatches == FeaturelessHostMatches.None)
            {
                return FeaturelessHostMatches.None;
            }

            result |= componentMatches;
        }

        // Only the leftmost compound can match the host.
        return index == selector.Compounds.Length - 1 ? result : FeaturelessHostMatches.None;
    }

    private static FeaturelessHostMatches ComponentFeaturelessHostMatches(Component component)
    {
        switch (component)
        {
            case HostComponent:
                return FeaturelessHostMatches.ForHost;
            case ScopeComponent:
                return FeaturelessHostMatches.ForScope;
            case IsComponent isComponent:
                return ListFeaturelessHostMatches(isComponent.List);
            case WhereComponent where:
                return ListFeaturelessHostMatches(where.List);
            default:
                return FeaturelessHostMatches.None;
        }
    }

    private static FeaturelessHostMatches ListFeaturelessHostMatches(SelectorList list)
    {
        // Everything in a logical combination must be able to match the featureless shadow host.
        var result = FeaturelessHostMatches.None;
        foreach (var selector in list.Selectors)
        {
            var matches = selector.MatchesFeaturelessHostSelectorOrPseudoElement();
            if (matches == FeaturelessHostMatches.None)
            {
                return FeaturelessHostMatches.None;
            }

            result |= matches;
        }

        return result;
    }

    private static bool MatchesCompoundSelector(
        CompoundSelector compound,
        DomElement element,
        MatchingContext context)
    {
        foreach (var component in compound.Components)
        {
            if (!MatchesSimpleSelector(component, element, context))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesSimpleSelector(
        Component component,
        DomElement element,
        MatchingContext context)
    {
        switch (component)
        {
            case IdComponent id:
                return element.HasId(id.Id, context.ClassesAndIdsCaseSensitivity);

            case ClassComponent className:
                return element.HasClass(className.ClassName, context.ClassesAndIdsCaseSensitivity);

            case LocalNameComponent localName:
                return element.HasLocalName(SelectName(element, localName.Name, localName.LowerName));

            case AttributeComponent attr:
                return element.AttrMatches(
                    attr.NamespaceKind,
                    attr.NamespaceUrl,
                    SelectName(element, attr.LocalName, attr.LocalNameLower),
                    attr.Operator,
                    attr.Value,
                    ToUnconditionalCaseSensitivity(attr.CaseSensitivity, element));

            case SlottedComponent slotted:
                // <slot>s are never flattened tree slottables.
                if (element.IsHtmlSlotElement())
                {
                    return false;
                }

                return Nested(context, () => MatchesComplexSelector(slotted.Selector, element, context));

            case PseudoElementComponent:
                // Obscura matches pseudo-elements through the render cascade's base selector, never
                // through ordinary selector matching.
                return false;

            case ExplicitUniversalTypeComponent:
            case ExplicitAnyNamespaceComponent:
                return true;

            case ExplicitNoNamespaceComponent:
                return element.HasNamespace(Namespaces.None);

            case NonTsPseudoClassComponent pseudoClass:
                return MatchesNonTsPseudoClass(pseudoClass.PseudoClass, element);

            case RootComponent:
                return element.IsRoot();

            case EmptyComponent:
                return element.IsEmpty();

            case HostComponent host:
            {
                if (context.CurrentHost is not { } scope || scope != element.Opaque)
                {
                    return false;
                }

                if (host.Selector is not { } inner)
                {
                    return true;
                }

                return Nested(context, () => MatchesComplexSelector(inner, element, context));
            }

            case ScopeComponent:
                // No scope element is ever bound, so :scope falls back to the root, matching the
                // reference implementation.
                return element.IsRoot();

            case NthComponent nth:
                return MatchesGenericNthChild(element, context, nth.Data, nth.Of);

            case IsComponent isComponent:
                return Nested(
                    context,
                    () => MatchesComplexSelectorList(isComponent.List.Selectors, element, context));

            case WhereComponent where:
                return Nested(
                    context,
                    () => MatchesComplexSelectorList(where.List.Selectors, element, context));

            case NegationComponent negation:
                return NestedForNegation(
                    context,
                    () => !MatchesComplexSelectorList(negation.List.Selectors, element, context));

            case HasComponent has:
                return MatchesRelativeSelectors(has.Relatives, element, context);

            case RelativeSelectorAnchorComponent:
                return context.RelativeSelectorAnchor is not { } anchor || anchor == element.Opaque;

            case InvalidComponent:
                return false;

            default:
                return false;
        }
    }

    private static bool Nested(MatchingContext context, Func<bool> body)
    {
        context.NestingLevel++;
        try
        {
            return body();
        }
        finally
        {
            context.NestingLevel--;
        }
    }

    private static bool NestedForNegation(MatchingContext context, Func<bool> body)
    {
        context.NestingLevel++;
        var wasInNegation = context.InNegation;
        context.InNegation = true;
        try
        {
            return body();
        }
        finally
        {
            context.InNegation = wasInNegation;
            context.NestingLevel--;
        }
    }

    private static string SelectName(DomElement element, string name, string lowerName) =>
        element.IsHtmlElementInHtmlDocument() ? lowerName : name;

    private static CaseSensitivity ToUnconditionalCaseSensitivity(
        ParsedCaseSensitivity parsed,
        DomElement element) => parsed switch
    {
        ParsedCaseSensitivity.AsciiCaseInsensitive => CaseSensitivity.AsciiCaseInsensitive,
        ParsedCaseSensitivity.AsciiCaseInsensitiveIfInHtmlElementInHtmlDocument =>
            element.IsHtmlElementInHtmlDocument()
                ? CaseSensitivity.AsciiCaseInsensitive
                : CaseSensitivity.CaseSensitive,
        _ => CaseSensitivity.CaseSensitive,
    };

    private static bool MatchesNonTsPseudoClass(PseudoClass pseudoClass, DomElement element) =>
        pseudoClass switch
        {
            PseudoClass.Link => element.IsLink(),
            PseudoClass.Visited => false,
            // :enabled/:disabled/:checked reflect real, static DOM state (the disabled/checked
            // attributes), not live user interaction, so they resolve the same way against a static
            // snapshot as they would in a browser that never received an input event.
            PseudoClass.Enabled => element.IsFormControl() && !element.HasBooleanAttr("disabled"),
            PseudoClass.Disabled => element.IsFormControl() && element.HasBooleanAttr("disabled"),
            PseudoClass.Checked => element.HasBooleanAttr("checked") || element.HasBooleanAttr("selected"),
            // Dynamic user-interaction pseudo-classes have no meaning against a static DOM snapshot
            // with no live user input.
            _ => false,
        };

    // ------------------------------------------------------------------ :nth-*

    private static bool MatchesGenericNthChild(
        DomElement element,
        MatchingContext context,
        NthData data,
        SelectorList? of)
    {
        var selectors = of?.Selectors ?? [];
        var hasSelectors = selectors.Length > 0;
        var selectorsMatch = !hasSelectors
            || Nested(context, () => MatchesComplexSelectorList(selectors, element, context));

        if (data.IsOnly)
        {
            return MatchesGenericNthChild(element, context, NthData.First(data.IsOfType), of)
                && MatchesGenericNthChild(element, context, NthData.Last(data.IsOfType), of);
        }

        if (!selectorsMatch)
        {
            return false;
        }

        var isFromEnd = data.IsFromEnd;
        var isEdgeChildSelector = data.IsSimpleEdge && !hasSelectors;

        // :first/last-child are rather trivial to match.
        if (isEdgeChildSelector)
        {
            return (isFromEnd ? element.NextSiblingElement() : element.PrevSiblingElement()) is null;
        }

        var index = NthChildIndex(element, context, selectors, data.IsOfType, isFromEnd);

        // Is there a non-negative integer n such that An+B=index?
        var an = index - data.B;
        if (data.A == 0)
        {
            return an == 0;
        }

        var n = an / data.A;
        return n >= 0 && data.A * n == an;
    }

    private static int NthChildIndex(
        DomElement element,
        MatchingContext context,
        Selector[] selectors,
        bool isOfType,
        bool isFromEnd)
    {
        var index = 1;
        var current = element;
        while (true)
        {
            var next = isFromEnd ? current.NextSiblingElement() : current.PrevSiblingElement();
            if (next is not { } sibling)
            {
                break;
            }

            current = sibling;
            if (selectors.Length > 0)
            {
                if (Nested(context, () => MatchesComplexSelectorList(selectors, current, context)))
                {
                    index++;
                }
            }
            else if (isOfType)
            {
                if (element.IsSameType(current))
                {
                    index++;
                }
            }
            else
            {
                index++;
            }
        }

        return index;
    }

    // ------------------------------------------------------------------ :has()

    private static bool MatchesRelativeSelectors(
        RelativeSelector[] relatives,
        DomElement element,
        MatchingContext context)
    {
        var savedAnchor = context.RelativeSelectorAnchor;
        context.RelativeSelectorAnchor = element.Opaque;
        context.NestingLevel++;
        try
        {
            foreach (var relative in relatives)
            {
                if (MatchesRelativeSelector(relative, element, context))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            context.NestingLevel--;
            context.RelativeSelectorAnchor = savedAnchor;
        }
    }

    private static bool MatchesRelativeSelector(
        RelativeSelector relative,
        DomElement anchor,
        MatchingContext context)
    {
        var tree = anchor.Tree;
        switch (relative.Combinator)
        {
            case Combinator.Descendant:
            case Combinator.Child:
            {
                foreach (var candidate in tree.Descendants(anchor.NodeId))
                {
                    var element = new DomElement(tree, candidate);
                    if (element.IsElement && MatchesComplexSelector(relative.Selector, element, context))
                    {
                        return true;
                    }
                }

                return false;
            }

            case Combinator.NextSibling:
            case Combinator.LaterSibling:
            {
                var current = anchor.NextSiblingElement();
                while (current is { } sibling)
                {
                    if (MatchesComplexSelector(relative.Selector, sibling, context))
                    {
                        return true;
                    }

                    current = sibling.NextSiblingElement();
                }

                return false;
            }

            default:
                return false;
        }
    }
}
