using System.Text;
using Obscura.Dom;
using Obscura.Dom.Selectors;

namespace Obscura.Render.Css;

/// <summary>One compiled, indexed author rule.</summary>
internal class Rule
{
    public required CompiledSelector Selector { get; init; }

    public required uint Specificity { get; init; }

    public required string NormalDecls { get; init; }

    public required string ImportantDecls { get; init; }

    public required DeclarationStreamFlags NormalFlags { get; init; }

    public required DeclarationStreamFlags ImportantFlags { get; init; }

    public uint CandidateSlot { get; set; } = Stylesheet.NoCandidateSlot;

    /// <summary>Source order, for breaking specificity ties (later wins).</summary>
    public required int Order { get; init; }

    public required ContainerConditionId ContainerConditionId { get; init; }

    public required LayerOrder? Layer { get; init; }
}

/// <summary>
/// A <c>sel::before</c> / <c>sel::after</c> / <c>sel::placeholder</c> rule,
/// indexed by its ordinary base selector.
/// </summary>
internal sealed class PseudoRule : Rule
{
}

/// <summary>The declarations one shadow encapsulation scope contributes.</summary>
internal sealed class ShadowScopeDeclarations
{
    public StringBuilder Normal { get; } = new();

    public StringBuilder Important { get; } = new();
}

/// <summary>A shadow scope whose <c>::slotted()</c> rules apply to an assigned light child.</summary>
public sealed record ShadowSlottedScope(Stylesheet Sheet, NodeId Host);

/// <summary>
/// A subject-keyed index of pseudo-element rules.
/// </summary>
internal sealed class PseudoRuleMap
{
    public List<PseudoRule> Rules { get; } = [];

    public List<int> ByRoot { get; } = [];

    public Dictionary<string, List<int>> ById { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<int>> ByClass { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<int>> ByAttribute { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<int>> ByLocal { get; } = new(StringComparer.Ordinal);

    public List<int> Universal { get; } = [];

    public int CandidateSlotCount { get; private set; }

    public void Push(PseudoRule rule)
    {
        var index = Rules.Count;
        if (rule.Selector.CandidateKeys.Count > 1)
        {
            rule.CandidateSlot = (uint)CandidateSlotCount;
            CandidateSlotCount++;
        }

        foreach (var key in rule.Selector.CandidateKeys)
        {
            Stylesheet.IndexKey(key, index, ByRoot, ById, ByClass, ByAttribute, ByLocal, Universal);
        }

        Rules.Add(rule);
    }

    public bool BucketMatchesContainerQueryRule(List<int>? bucket, DomTree tree, Matcher matcher, NodeId nid)
    {
        if (bucket is null)
        {
            return false;
        }

        foreach (var index in bucket)
        {
            var rule = Rules[index];
            if (rule.ContainerConditionId != ContainerConditionId.None
                && (rule.CandidateSlot == Stylesheet.NoCandidateSlot
                    || matcher.MarkCandidate((int)rule.CandidateSlot))
                && matcher.Matches(tree, nid, rule.Selector))
            {
                return true;
            }
        }

        return false;
    }

    public bool NodeMatchesContainerQueryRule(DomTree tree, Matcher matcher, NodeId nid)
    {
        if (CandidateSlotCount != 0)
        {
            matcher.BeginCandidateCollection(CandidateSlotCount);
        }

        var node = tree.GetNode(nid);
        if (node?.AsElement() is not { } element)
        {
            return false;
        }

        if (BucketMatchesContainerQueryRule(Lookup(ByLocal, element.Name.Local), tree, matcher, nid))
        {
            return true;
        }

        if (Stylesheet.IsRootElement(tree, nid) && ByRoot.Count != 0
            && BucketMatchesContainerQueryRule(ByRoot, tree, matcher, nid))
        {
            return true;
        }

        if (node.GetAttribute("id") is { } id
            && BucketMatchesContainerQueryRule(Lookup(ById, id), tree, matcher, nid))
        {
            return true;
        }

        if (node.GetAttribute("class") is { } classes)
        {
            foreach (var className in classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (BucketMatchesContainerQueryRule(Lookup(ByClass, className), tree, matcher, nid))
                {
                    return true;
                }
            }
        }

        if (node.Attrs is { } attributes)
        {
            foreach (var attribute in attributes)
            {
                if (BucketMatchesContainerQueryRule(Lookup(ByAttribute, attribute.Name.Local), tree, matcher, nid))
                {
                    return true;
                }
            }
        }

        return Universal.Count != 0 && BucketMatchesContainerQueryRule(Universal, tree, matcher, nid);
    }

    internal static List<int>? Lookup(Dictionary<string, List<int>> map, string key) =>
        map.TryGetValue(key, out var bucket) ? bucket : null;
}

/// <summary>One document-local compiled author stylesheet cache entry.</summary>
internal sealed class CachedStylesheet
{
    public required string[] Sources { get; init; }

    public required (uint Width, uint Height) ViewportBits { get; init; }

    public required CssMediaType MediaType { get; init; }

    public required int SourceBytes { get; init; }

    public required Stylesheet Sheet { get; init; }
}

/// <summary>
/// One document-local compiled author stylesheet cache.
/// </summary>
/// <remarks>
/// DOM mutations still run the complete cascade and layout against the live
/// tree. This cache retains only source parsing, selector compilation, and
/// candidate indexing, whose inputs are the ordered CSS text, viewport, and
/// selected CSS media type. Keeping a single exact-key entry prevents
/// cross-document growth and avoids hash-collision correctness risks.
/// Pathological source sets above the byte bound are parsed normally but never
/// retained.
/// </remarks>
public sealed class StylesheetCache
{
    private const int MaxSourceBytes = 8 * 1024 * 1024;
    private const int MaxRules = 100_000;

    private CachedStylesheet? _entry;
    private ulong _hits;
    private ulong _misses;

    public ulong HitCount => _hits;

    public ulong MissCount => _misses;

    public int RetainedSourceBytes => _entry?.SourceBytes ?? 0;

    public (Stylesheet Sheet, bool Hit) GetOrParse(
        DomTree tree,
        IReadOnlyList<string> sources,
        (float Width, float Height) viewport,
        CssMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var viewportBits = (
            (uint)BitConverter.SingleToInt32Bits(viewport.Width),
            (uint)BitConverter.SingleToInt32Bits(viewport.Height));
        if (_entry is { } entry
            && entry.ViewportBits == viewportBits
            && entry.MediaType == mediaType
            && SourcesEqual(entry.Sources, sources))
        {
            if (_hits != ulong.MaxValue)
            {
                _hits++;
            }

            return (entry.Sheet, true);
        }

        if (_misses != ulong.MaxValue)
        {
            _misses++;
        }

        var sheet = Stylesheet.ParseForViewportAndMedia(tree, sources, viewport, mediaType);
        long sourceBytes = 0;
        foreach (var source in sources)
        {
            sourceBytes += source.Length;
        }

        var compiledRules = sheet.Rules.Count
            + sheet.BeforeRules.Rules.Count
            + sheet.AfterRules.Rules.Count
            + sheet.PlaceholderRules.Rules.Count;
        if (sourceBytes <= MaxSourceBytes && compiledRules <= MaxRules)
        {
            _entry = new CachedStylesheet
            {
                Sources = [.. sources],
                ViewportBits = viewportBits,
                MediaType = mediaType,
                SourceBytes = (int)sourceBytes,
                Sheet = sheet,
            };
        }
        else
        {
            _entry = null;
        }

        return (sheet, false);
    }

    private static bool SourcesEqual(string[] left, IReadOnlyList<string> right)
    {
        if (left.Length != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>An indexed set of author rules ready for fast per-element matching.</summary>
public sealed class Stylesheet
{
    internal const uint NoCandidateSlot = uint.MaxValue;

    internal List<Rule> Rules { get; } = [];

    /// <summary>
    /// Rules whose subject can match this stylesheet's featureless shadow host.
    /// A host is outside the shadow tree's normal selector index and must only
    /// be matched with that tree's explicit host scope.
    /// </summary>
    internal List<int> HostRules { get; } = [];

    /// <summary>
    /// <c>::slotted()</c> rules, matched against assigned light children with
    /// this stylesheet's host supplied as the selector tree scope.
    /// </summary>
    internal List<int> SlottedRules { get; } = [];

    internal InvalidationMap Invalidation { get; } = new();

    internal Dictionary<string, RegisteredCustomProperty> RegisteredCustomProperties { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Index zero is the unconditional sentinel.</summary>
    internal List<ContainerConditionNode> ContainerConditions { get; } = CssParser.NewConditionArena();

    /// <summary>Every offset from each <c>@keyframes</c> rule.</summary>
    internal Dictionary<string, Keyframes> KeyframesByName { get; } = new(StringComparer.Ordinal);

    internal AnimationSampleTime AnimationSampleTime { get; private set; }

    internal List<int> ByRoot { get; } = [];

    internal Dictionary<string, List<int>> ById { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, List<int>> ByClass { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, List<int>> ByAttribute { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, List<int>> ByLocal { get; } = new(StringComparer.Ordinal);

    internal List<int> Universal { get; } = [];

    internal int CandidateSlotCount { get; private set; }

    internal PseudoRuleMap BeforeRules { get; } = new();

    internal PseudoRuleMap AfterRules { get; } = new();

    internal PseudoRuleMap PlaceholderRules { get; } = new();

    /// <summary>
    /// Dependency metadata for conservative incremental-style invalidation.
    /// Building this map does not itself enable incremental cascade skipping.
    /// </summary>
    public InvalidationMap InvalidationMap => Invalidation;

    public bool IsEmpty => Rules.Count == 0;

    /// <summary>Rust <c>Stylesheet::debug_stats</c>.</summary>
    public (int Rules, int Ids, int Classes, int Attributes, int Locals, int Universal) DebugStats() =>
        (Rules.Count, ById.Count, ByClass.Count, ByAttribute.Count, ByLocal.Count, Universal.Count);

    // ------------------------------------------------------------------ parse

    /// <summary>
    /// Parse and index a set of raw CSS sources (the text of each
    /// <c>&lt;style&gt;</c> block, in document order). Selectors that fail to
    /// parse are dropped.
    /// </summary>
    public static Stylesheet Parse(DomTree tree, IReadOnlyList<string> sources) =>
        ParseForViewport(tree, sources, (1280f, 720f));

    /// <summary>
    /// Parse author CSS for the live CSS viewport. Media queries must use the
    /// same dimensions as layout and page JavaScript.
    /// </summary>
    public static Stylesheet ParseForViewport(
        DomTree tree,
        IReadOnlyList<string> sources,
        (float Width, float Height) viewport) =>
        ParseForViewportAndMedia(tree, sources, viewport, CssMediaType.Screen);

    public static Stylesheet ParseForViewportAndMedia(
        DomTree tree,
        IReadOnlyList<string> sources,
        (float Width, float Height) viewport,
        CssMediaType mediaType) =>
        ParseForViewportAndMediaAtAnimationTime(tree, sources, viewport, mediaType, default);

    public static Stylesheet ParseForViewportAtAnimationTime(
        DomTree tree,
        IReadOnlyList<string> sources,
        (float Width, float Height) viewport,
        AnimationSampleTime animationSampleTime) =>
        ParseForViewportAndMediaAtAnimationTime(
            tree,
            sources,
            viewport,
            CssMediaType.Screen,
            animationSampleTime);

    public static Stylesheet ParseForViewportAndMediaAtAnimationTime(
        DomTree tree,
        IReadOnlyList<string> sources,
        (float Width, float Height) viewport,
        CssMediaType mediaType,
        AnimationSampleTime animationSampleTime)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(sources);
        var sheet = new Stylesheet { AnimationSampleTime = animationSampleTime };
        var order = 0;
        var layers = new LayerRegistry();
        var keyframeWinners = new Dictionary<string, (LayerOrder? Layer, bool Prefixed, int Order)>(
            StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var parsed = CssParser.ParseStylesheetForViewportPreservingContainersInLayer(
                source,
                viewport,
                mediaType,
                sheet.ContainerConditions,
                ContainerConditionId.None,
                layers,
                null);

            foreach (var rule in parsed)
            {
                var selector = rule.Selector;
                var declarations = rule.Declarations;

                string? keyframeName = null;
                var prefixed = false;
                if (selector.StartsWith(CssAtRules.KeyframesSelectorPrefix, StringComparison.Ordinal))
                {
                    keyframeName = selector[CssAtRules.KeyframesSelectorPrefix.Length..];
                }
                else if (selector.StartsWith(CssAtRules.WebkitKeyframesSelectorPrefix, StringComparison.Ordinal))
                {
                    keyframeName = selector[CssAtRules.WebkitKeyframesSelectorPrefix.Length..];
                    prefixed = true;
                }

                if (keyframeName is not null)
                {
                    var replaces = true;
                    if (keyframeWinners.TryGetValue(keyframeName, out var winner))
                    {
                        var comparison = CssAtRules.CompareLayerOrder(rule.Layer, winner.Layer);
                        if (comparison == 0)
                        {
                            comparison = (winner.Prefixed, prefixed) switch
                            {
                                (true, false) => 1,
                                (false, true) => -1,
                                _ => order.CompareTo(winner.Order),
                            };
                        }

                        replaces = comparison > 0;
                    }

                    if (replaces)
                    {
                        sheet.KeyframesByName[keyframeName] = CssKeyframes.CompileBody(declarations);
                        keyframeWinners[keyframeName] = (rule.Layer, prefixed, order);
                    }

                    order++;
                    continue;
                }

                if (selector.StartsWith(CssAtRules.PropertyRegistrationSelectorPrefix, StringComparison.Ordinal))
                {
                    var name = selector[CssAtRules.PropertyRegistrationSelectorPrefix.Length..];
                    if (CssPropertyRegistration.Parse(declarations) is { } registration)
                    {
                        sheet.RegisteredCustomProperties[name] = registration;
                    }

                    continue;
                }

                var trimmed = selector.Trim();
                if (TryPushPseudo(sheet, tree, trimmed, declarations, rule, order, "before", sheet.BeforeRules)
                    || TryPushPseudo(sheet, tree, trimmed, declarations, rule, order, "after", sheet.AfterRules)
                    || TryPushPseudo(
                        sheet,
                        tree,
                        trimmed,
                        declarations,
                        rule,
                        order,
                        "placeholder",
                        sheet.PlaceholderRules))
                {
                    order++;
                    continue;
                }

                var compiled = tree.CompileRuleSelector(selector);
                if (compiled is null)
                {
                    if (CssSelectorText.SelectorRequiresConservativeTracking(selector))
                    {
                        CssInvalidationBuilder.NoteSelectorForInvalidation(sheet.Invalidation, selector, order);
                        order++;
                    }

                    continue;
                }

                CssInvalidationBuilder.NoteSelectorForInvalidation(sheet.Invalidation, selector, order);
                CssInvalidationBuilder.NoteDeclarationAttributeDependencies(
                    sheet.Invalidation,
                    declarations,
                    order);
                var (normalDecls, importantDecls) = CssDeclarations.Partition(declarations);
                var index = sheet.Rules.Count;
                var indexed = new Rule
                {
                    Selector = compiled,
                    Specificity = compiled.Specificity,
                    NormalDecls = normalDecls,
                    ImportantDecls = importantDecls,
                    NormalFlags = DeclarationStreamFlags.Compute(normalDecls),
                    ImportantFlags = DeclarationStreamFlags.Compute(importantDecls),
                    Order = order,
                    ContainerConditionId = rule.ContainerConditionId,
                    Layer = rule.Layer,
                };

                if (compiled.MatchesFeaturelessHost())
                {
                    sheet.HostRules.Add(index);
                }

                if (compiled.IsSlotted())
                {
                    sheet.SlottedRules.Add(index);
                }

                if (compiled.CandidateKeys.Count > 1)
                {
                    indexed.CandidateSlot = (uint)sheet.CandidateSlotCount;
                    sheet.CandidateSlotCount++;
                }

                foreach (var key in compiled.CandidateKeys)
                {
                    IndexKey(
                        key,
                        index,
                        sheet.ByRoot,
                        sheet.ById,
                        sheet.ByClass,
                        sheet.ByAttribute,
                        sheet.ByLocal,
                        sheet.Universal);
                }

                sheet.Rules.Add(indexed);
                order++;
            }
        }

        return sheet;
    }

    private static bool TryPushPseudo(
        Stylesheet sheet,
        DomTree tree,
        string selector,
        string declarations,
        ParsedRule rule,
        int order,
        string which,
        PseudoRuleMap target)
    {
        if (CssSelectorText.StripPseudoElement(selector, which) is not { } baseSelector)
        {
            return false;
        }

        var compiled = tree.CompileRuleSelector(baseSelector);
        if (compiled is null)
        {
            if (CssSelectorText.SelectorRequiresConservativeTracking(baseSelector))
            {
                // Keep correctness metadata for relative/structural syntax that
                // the current selector matcher cannot yet compile.
                CssInvalidationBuilder.NoteSelectorForInvalidation(sheet.Invalidation, baseSelector, order);
            }

            return true;
        }

        CssInvalidationBuilder.NoteSelectorForInvalidation(sheet.Invalidation, baseSelector, order);
        CssInvalidationBuilder.NoteDeclarationAttributeDependencies(sheet.Invalidation, declarations, order);
        var (normalDecls, importantDecls) = CssDeclarations.Partition(declarations);
        target.Push(new PseudoRule
        {
            Selector = compiled,
            Specificity = compiled.Specificity,
            NormalDecls = normalDecls,
            ImportantDecls = importantDecls,
            NormalFlags = DeclarationStreamFlags.Compute(normalDecls),
            ImportantFlags = DeclarationStreamFlags.Compute(importantDecls),
            Order = order,
            ContainerConditionId = rule.ContainerConditionId,
            Layer = rule.Layer,
        });
        return true;
    }

    internal static void IndexKey(
        SelectorKey key,
        int index,
        List<int> byRoot,
        Dictionary<string, List<int>> byId,
        Dictionary<string, List<int>> byClass,
        Dictionary<string, List<int>> byAttribute,
        Dictionary<string, List<int>> byLocal,
        List<int> universal)
    {
        switch (key.Kind)
        {
            case SelectorKeyKind.Root:
                byRoot.Add(index);
                break;
            case SelectorKeyKind.Id:
                Bucket(byId, key.Value).Add(index);
                break;
            case SelectorKeyKind.Class:
                Bucket(byClass, key.Value).Add(index);
                break;
            case SelectorKeyKind.Attribute:
                Bucket(byAttribute, key.Value).Add(index);
                break;
            case SelectorKeyKind.Local:
                Bucket(byLocal, key.Value).Add(index);
                break;
            default:
                universal.Add(index);
                break;
        }

        static List<int> Bucket(Dictionary<string, List<int>> map, string key)
        {
            if (!map.TryGetValue(key, out var bucket))
            {
                bucket = [];
                map[key] = bucket;
            }

            return bucket;
        }
    }

    /// <summary>Rust <c>css::is_root_element</c>.</summary>
    internal static bool IsRootElement(DomTree tree, NodeId nid) =>
        tree.GetNode(nid)?.Parent is { } parent && (tree.GetNode(parent)?.IsDocument ?? false);

    // -------------------------------------------------------- container query

    /// <summary>
    /// Until completed container geometry is supplied, preserved conditional
    /// rules remain inactive rather than using viewport geometry.
    /// </summary>
    private bool ContainerConditionIsActive(
        ContainerConditionId id,
        NodeId subject,
        ContainerQuerySubjectKind kind,
        ContainerQueryEvaluator? evaluator) =>
        id == ContainerConditionId.None
        || (evaluator is not null && evaluator.ConditionMatches(this, subject, id, kind));

    public bool HasContainerQueries()
    {
        foreach (var rule in Rules)
        {
            if (rule.ContainerConditionId != ContainerConditionId.None)
            {
                return true;
            }
        }

        return AnyConditional(BeforeRules) || AnyConditional(AfterRules) || AnyConditional(PlaceholderRules);

        static bool AnyConditional(PseudoRuleMap map)
        {
            foreach (var rule in map.Rules)
            {
                if (rule.ContainerConditionId != ContainerConditionId.None)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private bool BucketMatchesContainerQueryRule(List<int>? bucket, DomTree tree, Matcher matcher, NodeId nid)
    {
        if (bucket is null)
        {
            return false;
        }

        foreach (var index in bucket)
        {
            var rule = Rules[index];
            if (rule.ContainerConditionId != ContainerConditionId.None
                && (rule.CandidateSlot == NoCandidateSlot || matcher.MarkCandidate((int)rule.CandidateSlot))
                && matcher.Matches(tree, nid, rule.Selector))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="nid"/> can receive a declaration from any
    /// container-conditional rule in the current DOM state.
    /// </summary>
    public bool NodeMatchesContainerQueryRule(DomTree tree, Matcher matcher, NodeId nid)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(matcher);
        if (CandidateSlotCount != 0)
        {
            matcher.BeginCandidateCollection(CandidateSlotCount);
        }

        var node = tree.GetNode(nid);
        if (node is null)
        {
            return false;
        }

        var normalMatch = false;
        if (node.AsElement() is { } element)
        {
            normalMatch =
                BucketMatchesContainerQueryRule(PseudoRuleMap.Lookup(ByLocal, element.Name.Local), tree, matcher, nid)
                || (IsRootElement(tree, nid) && ByRoot.Count != 0
                    && BucketMatchesContainerQueryRule(ByRoot, tree, matcher, nid))
                || (node.GetAttribute("id") is { } id
                    && BucketMatchesContainerQueryRule(PseudoRuleMap.Lookup(ById, id), tree, matcher, nid))
                || (node.GetAttribute("class") is { } classes && AnyClassMatches(classes))
                || (node.Attrs is { } attributes && AnyAttributeMatches(attributes))
                || (Universal.Count != 0 && BucketMatchesContainerQueryRule(Universal, tree, matcher, nid));
        }

        var supportsPlaceholder = node.AsElement() is { } control
            && control.Name.Local is "input" or "textarea";
        return normalMatch
            || BeforeRules.NodeMatchesContainerQueryRule(tree, matcher, nid)
            || AfterRules.NodeMatchesContainerQueryRule(tree, matcher, nid)
            || (supportsPlaceholder && PlaceholderRules.NodeMatchesContainerQueryRule(tree, matcher, nid));

        bool AnyClassMatches(string classes)
        {
            foreach (var className in classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (BucketMatchesContainerQueryRule(PseudoRuleMap.Lookup(ByClass, className), tree, matcher, nid))
                {
                    return true;
                }
            }

            return false;
        }

        bool AnyAttributeMatches(List<Obscura.Dom.Attribute> attributes)
        {
            foreach (var attribute in attributes)
            {
                if (BucketMatchesContainerQueryRule(
                        PseudoRuleMap.Lookup(ByAttribute, attribute.Name.Local),
                        tree,
                        matcher,
                        nid))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public int ContainerConditionDepth()
    {
        var deepest = 0;
        for (var index = 1; index < ContainerConditions.Count; index++)
        {
            var depth = 1;
            var parent = ContainerConditions[index].Parent;
            while (parent != ContainerConditionId.None)
            {
                depth++;
                parent = ContainerConditions[(int)parent.Value].Parent;
            }

            deepest = Math.Max(deepest, depth);
        }

        return deepest;
    }

    // ---------------------------------------------------------------- pseudos

    public (LayoutStyle? Before, LayoutStyle? After) PseudoStyles(
        DomTree tree,
        Matcher matcher,
        NodeId nid,
        IReadOnlyDictionary<string, string> props,
        LayoutStyle hostStyle)
    {
        var (before, after, _) = PseudoStylesInternal(tree, matcher, nid, props, hostStyle, null);
        return (before, after);
    }

    public (LayoutStyle? Before, LayoutStyle? After, LayoutStyle? Placeholder) AllPseudoStyles(
        DomTree tree,
        Matcher matcher,
        NodeId nid,
        IReadOnlyDictionary<string, string> props,
        LayoutStyle hostStyle,
        ContainerQueryEvaluator? evaluator) =>
        PseudoStylesInternal(tree, matcher, nid, props, hostStyle, evaluator);

    private (LayoutStyle? Before, LayoutStyle? After, LayoutStyle? Placeholder) PseudoStylesInternal(
        DomTree tree,
        Matcher matcher,
        NodeId nid,
        IReadOnlyDictionary<string, string> props,
        LayoutStyle hostStyle,
        ContainerQueryEvaluator? evaluator)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(hostStyle);
        var supportsPlaceholder = tree.GetNode(nid)?.AsElement() is { } element
            && element.Name.Local is "input" or "textarea";
        return (
            BuildPseudo(BeforeRules, false),
            BuildPseudo(AfterRules, false),
            supportsPlaceholder ? BuildPseudo(PlaceholderRules, true) : null);

        LayoutStyle? BuildPseudo(PseudoRuleMap rules, bool isPlaceholder)
        {
            var normalMatched = new List<(uint Specificity, int Order, int Index)>();
            var importantMatched = new List<(uint Specificity, int Order, int Index)>();
            if (rules.CandidateSlotCount != 0)
            {
                matcher.BeginCandidateCollection(rules.CandidateSlotCount);
            }

            void Consider(List<int>? bucket)
            {
                if (bucket is null)
                {
                    return;
                }

                foreach (var index in bucket)
                {
                    var rule = rules.Rules[index];
                    if (rule.CandidateSlot != NoCandidateSlot && !matcher.MarkCandidate((int)rule.CandidateSlot))
                    {
                        continue;
                    }

                    // Candidate buckets only reject impossible originating
                    // elements; full selector matching remains authoritative.
                    // Container lookup is an ancestor walk, so keep it behind
                    // the selector match as well.
                    if (matcher.Matches(tree, nid, rule.Selector)
                        && ContainerConditionIsActive(
                            rule.ContainerConditionId,
                            nid,
                            ContainerQuerySubjectKind.OriginatingPseudo,
                            evaluator))
                    {
                        var matched = (rule.Specificity, rule.Order, index);
                        if (rule.NormalDecls.Length != 0)
                        {
                            normalMatched.Add(matched);
                        }

                        if (rule.ImportantDecls.Length != 0)
                        {
                            importantMatched.Add(matched);
                        }
                    }
                }
            }

            var node = tree.GetNode(nid);
            if (node is not null)
            {
                if (node.AsElement() is { } subject)
                {
                    Consider(PseudoRuleMap.Lookup(rules.ByLocal, subject.Name.Local));
                }

                if (node.GetAttribute("id") is { } id)
                {
                    Consider(PseudoRuleMap.Lookup(rules.ById, id));
                }

                if (node.GetAttribute("class") is { } classes)
                {
                    foreach (var className in classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        Consider(PseudoRuleMap.Lookup(rules.ByClass, className));
                    }
                }

                if (rules.ByAttribute.Count != 0 && node.Attrs is { } attributes)
                {
                    foreach (var attribute in attributes)
                    {
                        Consider(PseudoRuleMap.Lookup(rules.ByAttribute, attribute.Name.Local));
                    }
                }
            }

            if (IsRootElement(tree, nid) && rules.ByRoot.Count != 0)
            {
                Consider(rules.ByRoot);
            }

            if (rules.Universal.Count != 0)
            {
                Consider(rules.Universal);
            }

            if (normalMatched.Count == 0 && importantMatched.Count == 0)
            {
                return null;
            }

            SortCascade(normalMatched, index => rules.Rules[index].Layer, important: false);
            SortCascade(importantMatched, index => rules.Rules[index].Layer, important: true);

            // Generated ::before/::after boxes have an inline outer display by
            // default. LayoutStyle's general default is block because it
            // primarily represents ordinary DOM boxes, so set the pseudo
            // initial value explicitly before applying author declarations.
            var style = new LayoutStyle { Display = Display.Inline };
            style.ColorSchemeDark = hostStyle.ColorSchemeDark;
            if (isPlaceholder)
            {
                // Chromium's light native-control placeholder color. Author
                // declarations cascade over this UA-origin initial value.
                style.Color = new RgbaColor(117, 117, 117, 255);
            }

            var inheritedColorSchemeDark = hostStyle.ColorSchemeDark;
            List<GeneratedContentItem>? generatedContent = null;
            foreach (var (_, _, index) in normalMatched)
            {
                var rule = rules.Rules[index];
                if (!rule.NormalFlags.HasColorScheme)
                {
                    continue;
                }

                var expanded = CssVariables.SubstituteDeclarations(
                    rule.NormalDecls,
                    props,
                    rule.NormalFlags.HasVar);
                ComputedStyle.ApplyColorSchemeDeclarationsFrom(style, expanded, inheritedColorSchemeDark);
            }

            foreach (var (_, _, index) in importantMatched)
            {
                var rule = rules.Rules[index];
                if (!rule.ImportantFlags.HasColorScheme)
                {
                    continue;
                }

                var expanded = CssVariables.SubstituteDeclarations(
                    rule.ImportantDecls,
                    props,
                    rule.ImportantFlags.HasVar);
                ComputedStyle.ApplyColorSchemeDeclarationsFrom(style, expanded, inheritedColorSchemeDark);
            }

            foreach (var (_, _, index) in normalMatched)
            {
                var rule = rules.Rules[index];
                var expanded = CssVariables.SubstituteDeclarations(
                    rule.NormalDecls,
                    props,
                    rule.NormalFlags.HasVar);
                ComputedStyle.ApplyDeclarationsWithLockedColorScheme(style, expanded);
                var content = CssValues.ExtractContent(expanded, AttributeLookup);
                if (content.Found)
                {
                    generatedContent = content.Items;
                }
            }

            foreach (var (_, _, index) in importantMatched)
            {
                var rule = rules.Rules[index];
                var expanded = CssVariables.SubstituteDeclarations(
                    rule.ImportantDecls,
                    props,
                    rule.ImportantFlags.HasVar);
                ComputedStyle.ApplyDeclarationsWithLockedColorScheme(style, expanded);
                var content = CssValues.ExtractContent(expanded, AttributeLookup);
                if (content.Found)
                {
                    generatedContent = content.Items;
                }
            }

            style.BeforeContent = generatedContent is null
                ? null
                : CssValues.GeneratedContentWithZeroCounters(generatedContent);
            style.GeneratedContent = generatedContent;
            if (isPlaceholder)
            {
                // `color` is inherited on the pseudo. The declaration parser
                // represents `inherit` as null, so resolve it against the
                // originating control after the author cascade.
                style.Color ??= hostStyle.Color;
                return style;
            }

            return style.GeneratedContent is not null || style.ContentImage is not null ? style : null;
        }

        string? AttributeLookup(string name) => tree.GetNode(nid)?.GetAttribute(name);
    }

    // ------------------------------------------------------------ shadow DOM

    private ShadowScopeDeclarations ShadowHostDeclarations(
        DomTree tree,
        Matcher matcher,
        NodeId host,
        ContainerQueryEvaluator? evaluator)
    {
        var normal = new List<(uint Specificity, int Order, int Index)>();
        var important = new List<(uint Specificity, int Order, int Index)>();
        foreach (var index in HostRules)
        {
            var rule = Rules[index];
            if (matcher.MatchesShadowHost(tree, host, rule.Selector, host)
                && ContainerConditionIsActive(
                    rule.ContainerConditionId,
                    host,
                    ContainerQuerySubjectKind.Element,
                    evaluator))
            {
                var matched = (rule.Specificity, rule.Order, index);
                if (rule.NormalDecls.Length != 0)
                {
                    normal.Add(matched);
                }

                if (rule.ImportantDecls.Length != 0)
                {
                    important.Add(matched);
                }
            }
        }

        return CollectScopeDeclarations(normal, important);
    }

    private ShadowScopeDeclarations ShadowSlottedDeclarations(
        DomTree tree,
        Matcher matcher,
        NodeId subject,
        NodeId host,
        ContainerQueryEvaluator? evaluator)
    {
        var normal = new List<(uint Specificity, int Order, int Index)>();
        var important = new List<(uint Specificity, int Order, int Index)>();
        foreach (var index in SlottedRules)
        {
            var rule = Rules[index];
            if (matcher.MatchesInShadowScope(tree, subject, rule.Selector, host)
                && ContainerConditionIsActive(
                    rule.ContainerConditionId,
                    subject,
                    ContainerQuerySubjectKind.Element,
                    evaluator))
            {
                var matched = (rule.Specificity, rule.Order, index);
                if (rule.NormalDecls.Length != 0)
                {
                    normal.Add(matched);
                }

                if (rule.ImportantDecls.Length != 0)
                {
                    important.Add(matched);
                }
            }
        }

        return CollectScopeDeclarations(normal, important);
    }

    private ShadowScopeDeclarations CollectScopeDeclarations(
        List<(uint Specificity, int Order, int Index)> normal,
        List<(uint Specificity, int Order, int Index)> important)
    {
        SortCascade(normal, index => Rules[index].Layer, important: false);
        SortCascade(important, index => Rules[index].Layer, important: true);

        var declarations = new ShadowScopeDeclarations();
        foreach (var (_, _, index) in normal)
        {
            CssDeclarations.AppendDeclarationStream(declarations.Normal, Rules[index].NormalDecls);
        }

        foreach (var (_, _, index) in important)
        {
            CssDeclarations.AppendDeclarationStream(declarations.Important, Rules[index].ImportantDecls);
        }

        return declarations;
    }

    private static void SortCascade(
        List<(uint Specificity, int Order, int Index)> matched,
        Func<int, LayerOrder?> layerOf,
        bool important)
    {
        if (matched.Count <= 1)
        {
            return;
        }

        matched.Sort((left, right) => CssAtRules.CompareRuleCascade(
            layerOf(left.Index),
            left.Specificity,
            left.Order,
            layerOf(right.Index),
            right.Specificity,
            right.Order,
            important));
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Apply every author rule that matches <paramref name="nid"/> to
    /// <paramref name="style"/>, in cascade order (ascending specificity, then
    /// source order, so the winner is applied last).
    /// </summary>
    /// <returns>
    /// The element's own custom-property map (parent plus this element's
    /// <c>--x</c> declarations) when it declares any, so the caller can thread
    /// the richer map to descendants; <c>null</c> means "reuse the parent's map".
    /// </returns>
    public Dictionary<string, string>? Apply(
        DomTree tree,
        Matcher matcher,
        NodeId nid,
        string? id,
        IReadOnlyList<string> classes,
        string local,
        LayoutStyle style,
        IReadOnlyDictionary<string, string> parentProps,
        string? inlineCss)
    {
        var timeline = new AnimationTimelineState();
        return ApplyAtAnimationTime(
            tree,
            matcher,
            nid,
            id,
            classes,
            local,
            style,
            parentProps,
            inlineCss,
            new AnimationSample(AnimationSampleTime, AnimationSampleMode.DocumentTime),
            timeline);
    }

    public Dictionary<string, string>? ApplyAtAnimationTime(
        DomTree tree,
        Matcher matcher,
        NodeId nid,
        string? id,
        IReadOnlyList<string> classes,
        string local,
        LayoutStyle style,
        IReadOnlyDictionary<string, string> parentProps,
        string? inlineCss,
        AnimationSample animationSample,
        AnimationTimelineState animationTimeline) =>
        ApplyInternal(
            tree,
            matcher,
            nid,
            id,
            classes,
            local,
            style,
            parentProps,
            inlineCss,
            null,
            [],
            null,
            animationSample,
            animationTimeline);

    public Dictionary<string, string>? ApplyWithContainerQueries(
        DomTree tree,
        Matcher matcher,
        NodeId nid,
        string? id,
        IReadOnlyList<string> classes,
        string local,
        LayoutStyle style,
        IReadOnlyDictionary<string, string> parentProps,
        string? inlineCss,
        ContainerQueryEvaluator evaluator)
    {
        var timeline = new AnimationTimelineState();
        return ApplyWithContainerQueriesAtAnimationTime(
            tree,
            matcher,
            nid,
            id,
            classes,
            local,
            style,
            parentProps,
            inlineCss,
            evaluator,
            new AnimationSample(AnimationSampleTime, AnimationSampleMode.DocumentTime),
            timeline);
    }

    public Dictionary<string, string>? ApplyWithContainerQueriesAtAnimationTime(
        DomTree tree,
        Matcher matcher,
        NodeId nid,
        string? id,
        IReadOnlyList<string> classes,
        string local,
        LayoutStyle style,
        IReadOnlyDictionary<string, string> parentProps,
        string? inlineCss,
        ContainerQueryEvaluator evaluator,
        AnimationSample animationSample,
        AnimationTimelineState animationTimeline) =>
        ApplyInternal(
            tree,
            matcher,
            nid,
            id,
            classes,
            local,
            style,
            parentProps,
            inlineCss,
            null,
            [],
            evaluator,
            animationSample,
            animationTimeline);

    /// <summary>
    /// Apply document author rules together with <c>:host</c> and
    /// <c>::slotted()</c> rules from the element's applicable shadow scopes.
    /// </summary>
    /// <remarks>
    /// Encapsulation context precedes specificity and layer order: shadow
    /// normal declarations are weaker than document/inline normal
    /// declarations, while shadow <c>!important</c> is stronger than
    /// document/inline author-important.
    /// </remarks>
    public Dictionary<string, string>? ApplyWithShadowScopesAtAnimationTime(
        Stylesheet? shadowHostSheet,
        IReadOnlyList<ShadowSlottedScope> slottedScopes,
        DomTree tree,
        Matcher matcher,
        NodeId nid,
        string? id,
        IReadOnlyList<string> classes,
        string local,
        LayoutStyle style,
        IReadOnlyDictionary<string, string> parentProps,
        string? inlineCss,
        ContainerQueryEvaluator? evaluator,
        AnimationSample animationSample,
        AnimationTimelineState animationTimeline) =>
        ApplyInternal(
            tree,
            matcher,
            nid,
            id,
            classes,
            local,
            style,
            parentProps,
            inlineCss,
            shadowHostSheet,
            slottedScopes,
            evaluator,
            animationSample,
            animationTimeline);

    private Dictionary<string, string>? ApplyInternal(
        DomTree tree,
        Matcher matcher,
        NodeId nid,
        string? id,
        IReadOnlyList<string> classes,
        string local,
        LayoutStyle style,
        IReadOnlyDictionary<string, string> parentProps,
        string? inlineCss,
        Stylesheet? shadowHostSheet,
        IReadOnlyList<ShadowSlottedScope> slottedScopes,
        ContainerQueryEvaluator? evaluator,
        AnimationSample animationSample,
        AnimationTimelineState animationTimeline)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(classes);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(parentProps);
        ArgumentNullException.ThrowIfNull(slottedScopes);
        ArgumentNullException.ThrowIfNull(animationTimeline);

        var shadowHostDeclarations = shadowHostSheet?.ShadowHostDeclarations(tree, matcher, nid, evaluator)
            ?? new ShadowScopeDeclarations();
        var shadowScopeDeclarations = new ShadowScopeDeclarations();
        CssDeclarations.AppendDeclarationStream(
            shadowScopeDeclarations.Normal,
            shadowHostDeclarations.Normal.ToString());
        var slottedDeclarations = new List<ShadowScopeDeclarations>(slottedScopes.Count);
        foreach (var scope in slottedScopes)
        {
            slottedDeclarations.Add(
                scope.Sheet.ShadowSlottedDeclarations(tree, matcher, nid, scope.Host, evaluator));
        }

        // Match Gecko's ShadowCascadeOrder: normal declarations progress from
        // the host's own (innermost) tree through outer slot scopes toward the
        // document. Important declarations reverse that order.
        for (var index = slottedDeclarations.Count - 1; index >= 0; index--)
        {
            CssDeclarations.AppendDeclarationStream(
                shadowScopeDeclarations.Normal,
                slottedDeclarations[index].Normal.ToString());
        }

        foreach (var declarations in slottedDeclarations)
        {
            CssDeclarations.AppendDeclarationStream(
                shadowScopeDeclarations.Important,
                declarations.Important.ToString());
        }

        CssDeclarations.AppendDeclarationStream(
            shadowScopeDeclarations.Important,
            shadowHostDeclarations.Important.ToString());

        // Keep the two cascade priorities separate from the outset. A typical
        // stylesheet has very few important declarations, so cloning and
        // sorting every matching normal-only rule into an empty important pass
        // is substantial wasted work on every element.
        var normalMatched = new List<(uint Specificity, int Order, int Index)>();
        var importantMatched = new List<(uint Specificity, int Order, int Index)>();
        if (CandidateSlotCount != 0)
        {
            matcher.BeginCandidateCollection(CandidateSlotCount);
        }

        void Consider(List<int>? bucket)
        {
            if (bucket is null)
            {
                return;
            }

            foreach (var index in bucket)
            {
                var rule = Rules[index];
                if (rule.CandidateSlot != NoCandidateSlot && !matcher.MarkCandidate((int)rule.CandidateSlot))
                {
                    continue;
                }

                // Container lookup is an ancestor walk. Keep it behind selector
                // matching and cache the result per condition.
                if (matcher.Matches(tree, nid, rule.Selector)
                    && ContainerConditionIsActive(
                        rule.ContainerConditionId,
                        nid,
                        ContainerQuerySubjectKind.Element,
                        evaluator))
                {
                    var matched = (rule.Specificity, rule.Order, index);
                    if (rule.NormalDecls.Length != 0)
                    {
                        normalMatched.Add(matched);
                    }

                    if (rule.ImportantDecls.Length != 0)
                    {
                        importantMatched.Add(matched);
                    }
                }
            }
        }

        Consider(PseudoRuleMap.Lookup(ByLocal, local));
        if (IsRootElement(tree, nid) && ByRoot.Count != 0)
        {
            Consider(ByRoot);
        }

        if (id is not null)
        {
            Consider(PseudoRuleMap.Lookup(ById, id));
        }

        foreach (var className in classes)
        {
            Consider(PseudoRuleMap.Lookup(ByClass, className));
        }

        if (ByAttribute.Count != 0 && tree.GetNode(nid)?.Attrs is { } nodeAttributes)
        {
            foreach (var attribute in nodeAttributes)
            {
                Consider(PseudoRuleMap.Lookup(ByAttribute, attribute.Name.Local));
            }
        }

        if (Universal.Count != 0)
        {
            Consider(Universal);
        }

        SortCascade(normalMatched, index => Rules[index].Layer, important: false);
        SortCascade(importantMatched, index => Rules[index].Layer, important: true);

        var (inlineNormal, inlineImportant) = inlineCss is null
            ? (string.Empty, string.Empty)
            : CssDeclarations.Partition(inlineCss);

        // Pass 1: collect this element's own custom properties (`--x: value`),
        // in cascade order (last wins), layered over the inherited map. Custom
        // properties cascade fully before any `var()` is substituted.
        var inlineNormalFlags = DeclarationStreamFlags.Compute(inlineNormal);
        var inlineImportantFlags = DeclarationStreamFlags.Compute(inlineImportant);
        var shadowNormalText = shadowScopeDeclarations.Normal.ToString();
        var shadowImportantText = shadowScopeDeclarations.Important.ToString();
        var shadowNormalFlags = DeclarationStreamFlags.Compute(shadowNormalText);
        var shadowImportantFlags = DeclarationStreamFlags.Compute(shadowImportantText);
        var hasOwnCustomProperties = shadowNormalFlags.HasCustomProperties
            || shadowImportantFlags.HasCustomProperties
            || inlineNormalFlags.HasCustomProperties
            || inlineImportantFlags.HasCustomProperties
            || AnyFlag(normalMatched, static rule => rule.NormalFlags.HasCustomProperties)
            || AnyFlag(importantMatched, static rule => rule.ImportantFlags.HasCustomProperties);

        // Registered properties are already represented in the parent's
        // computed map. Most descendants need no changes, so detect the
        // uncommon transition before cloning the potentially large map.
        var registrationsChangeParent = false;
        foreach (var (name, registration) in RegisteredCustomProperties)
        {
            if (registration.Inherits && parentProps.ContainsKey(name))
            {
                continue;
            }

            if (registration.InitialValue is { } initial)
            {
                if (!parentProps.TryGetValue(name, out var current)
                    || !string.Equals(current, initial, StringComparison.Ordinal))
                {
                    registrationsChangeParent = true;
                    break;
                }
            }
            else if (parentProps.ContainsKey(name))
            {
                registrationsChangeParent = true;
                break;
            }
        }

        Dictionary<string, string>? effective = null;
        if (hasOwnCustomProperties || registrationsChangeParent)
        {
            effective = ResolveCustomProperties(
                normalMatched,
                importantMatched,
                parentProps,
                shadowNormalText,
                shadowNormalFlags,
                shadowImportantText,
                shadowImportantFlags,
                inlineNormal,
                inlineNormalFlags,
                inlineImportant,
                inlineImportantFlags,
                registrationsChangeParent);
        }

        IReadOnlyDictionary<string, string> props = effective ?? parentProps;

        var inheritedColorSchemeDark = style.ColorSchemeDark;

        // `light-dark()` resolves against the element's final used color
        // scheme, not the declaration order. Determine the scheme winner across
        // the complete author cascade before applying any color-valued property.
        if (shadowNormalFlags.HasColorScheme)
        {
            ComputedStyle.ApplyColorSchemeDeclarationsFrom(
                style,
                CssVariables.SubstituteDeclarations(shadowNormalText, props, shadowNormalFlags.HasVar),
                inheritedColorSchemeDark);
        }

        foreach (var (_, _, index) in normalMatched)
        {
            var rule = Rules[index];
            if (!rule.NormalFlags.HasColorScheme)
            {
                continue;
            }

            ComputedStyle.ApplyColorSchemeDeclarationsFrom(
                style,
                CssVariables.SubstituteDeclarations(rule.NormalDecls, props, rule.NormalFlags.HasVar),
                inheritedColorSchemeDark);
        }

        if (inlineNormalFlags.HasColorScheme)
        {
            ComputedStyle.ApplyColorSchemeDeclarationsFrom(
                style,
                CssVariables.SubstituteDeclarations(inlineNormal, props, inlineNormalFlags.HasVar),
                inheritedColorSchemeDark);
        }

        foreach (var (_, _, index) in importantMatched)
        {
            var rule = Rules[index];
            if (!rule.ImportantFlags.HasColorScheme)
            {
                continue;
            }

            ComputedStyle.ApplyColorSchemeDeclarationsFrom(
                style,
                CssVariables.SubstituteDeclarations(rule.ImportantDecls, props, rule.ImportantFlags.HasVar),
                inheritedColorSchemeDark);
        }

        if (inlineImportantFlags.HasColorScheme)
        {
            ComputedStyle.ApplyColorSchemeDeclarationsFrom(
                style,
                CssVariables.SubstituteDeclarations(inlineImportant, props, inlineImportantFlags.HasVar),
                inheritedColorSchemeDark);
        }

        if (shadowImportantFlags.HasColorScheme)
        {
            ComputedStyle.ApplyColorSchemeDeclarationsFrom(
                style,
                CssVariables.SubstituteDeclarations(shadowImportantText, props, shadowImportantFlags.HasVar),
                inheritedColorSchemeDark);
        }

        // Pass 2: apply normal declarations with `var()` substituted against
        // the resolved custom-property map.
        ComputedStyle.ApplyDeclarationsWithLockedColorScheme(
            style,
            CssVariables.SubstituteDeclarations(shadowNormalText, props, shadowNormalFlags.HasVar));
        foreach (var (_, _, index) in normalMatched)
        {
            var rule = Rules[index];
            ComputedStyle.ApplyDeclarationsWithLockedColorScheme(
                style,
                CssVariables.SubstituteDeclarations(rule.NormalDecls, props, rule.NormalFlags.HasVar));
        }

        ComputedStyle.ApplyDeclarationsWithLockedColorScheme(
            style,
            CssVariables.SubstituteDeclarations(inlineNormal, props, inlineNormalFlags.HasVar));

        // Animation control properties from author !important participate in
        // computed timing, while the animated value itself remains below the
        // important author origin.
        var importantHasAnimation = shadowImportantFlags.HasAnimation
            || inlineImportantFlags.HasAnimation
            || AnyFlag(importantMatched, static rule => rule.ImportantFlags.HasAnimation);
        style.AnimationHasRenderEffect = false;
        style.AnimationEffectImpact = AnimationEffectImpact.None;
        if (KeyframesByName.Count != 0 && (style.AnimationName is not null || importantHasAnimation))
        {
            var animationStyle = style.Clone();
            foreach (var (_, _, index) in importantMatched)
            {
                var rule = Rules[index];
                if (!rule.ImportantFlags.HasAnimation)
                {
                    continue;
                }

                ComputedStyle.ApplyAnimationDeclarations(
                    animationStyle,
                    CssVariables.SubstituteDeclarations(rule.ImportantDecls, props, rule.ImportantFlags.HasVar));
            }

            if (inlineImportantFlags.HasAnimation)
            {
                ComputedStyle.ApplyAnimationDeclarations(
                    animationStyle,
                    CssVariables.SubstituteDeclarations(inlineImportant, props, inlineImportantFlags.HasVar));
            }

            if (shadowImportantFlags.HasAnimation)
            {
                ComputedStyle.ApplyAnimationDeclarations(
                    animationStyle,
                    CssVariables.SubstituteDeclarations(shadowImportantText, props, shadowImportantFlags.HasVar));
            }

            if (animationStyle.AnimationName is { } name && KeyframesByName.TryGetValue(name, out var keyframes))
            {
                style.AnimationHasRenderEffect = keyframes.Tracks.Count != 0;
                var impact = AnimationEffectImpact.None;
                foreach (var property in keyframes.Tracks.Keys)
                {
                    var candidate = CssKeyframes.EffectImpact(property);
                    if (candidate > impact)
                    {
                        impact = candidate;
                    }
                }

                style.AnimationEffectImpact = impact;
                var localSample = animationTimeline.SampleFor(
                    nid,
                    new AnimationInstanceKey(name),
                    animationStyle.AnimationTiming.PlayState,
                    animationSample);
                style.AnimationLocalTimeMs = localSample.Milliseconds;
                CssAnimationSampler.SampleAnimationProperties(
                    keyframes,
                    style,
                    animationStyle.AnimationTiming,
                    localSample,
                    props);
            }
            else
            {
                animationTimeline.ClearAnimation(nid, animationSample);
            }
        }
        else
        {
            animationTimeline.ClearAnimation(nid, animationSample);
        }

        // Web Animations contribute at the animation cascade origin: above every
        // normal author declaration (including inline style), but below author
        // !important. Keeping the renderer-side effect separate from the
        // authored declaration block lets cancel() reveal the exact underlying
        // value, and CSSOM never observes a synthetic inline rewrite.
        var hasWaapi = false;
        foreach (var _ in animationTimeline.WaapiForNode(nid, animationSample.Time))
        {
            hasWaapi = true;
            break;
        }

        var waapiUnderlyingTransformOps = hasWaapi ? new List<TransformOp>(style.TransformOps) : null;
        var waapiUnderlyingOpacity = style.Opacity;
        CssAnimationSampler.SampleWaapiProperties(animationTimeline, nid, style, animationSample);

        var importantHasTransform = shadowImportantFlags.HasTransform
            || inlineImportantFlags.HasTransform
            || AnyFlag(importantMatched, static rule => rule.ImportantFlags.HasTransform);
        var importantHasOpacity = shadowImportantFlags.HasOpacity
            || inlineImportantFlags.HasOpacity
            || AnyFlag(importantMatched, static rule => rule.ImportantFlags.HasOpacity);

        foreach (var (_, _, index) in importantMatched)
        {
            var rule = Rules[index];
            ComputedStyle.ApplyDeclarationsWithLockedColorScheme(
                style,
                CssVariables.SubstituteDeclarations(rule.ImportantDecls, props, rule.ImportantFlags.HasVar));
        }

        ComputedStyle.ApplyDeclarationsWithLockedColorScheme(
            style,
            CssVariables.SubstituteDeclarations(inlineImportant, props, inlineImportantFlags.HasVar));
        ComputedStyle.ApplyDeclarationsWithLockedColorScheme(
            style,
            CssVariables.SubstituteDeclarations(shadowImportantText, props, shadowImportantFlags.HasVar));

        style.WaapiSampleState = waapiUnderlyingTransformOps is null
            ? null
            : new WaapiSampleState
            {
                UnderlyingTransformOps = waapiUnderlyingTransformOps,
                UnderlyingOpacity = waapiUnderlyingOpacity,
                TransformFastPath = !importantHasTransform,
                OpacityFastPath = !importantHasOpacity,
            };
        return effective;

        bool AnyFlag(List<(uint Specificity, int Order, int Index)> matched, Func<Rule, bool> predicate)
        {
            foreach (var (_, _, index) in matched)
            {
                if (predicate(Rules[index]))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private Dictionary<string, string>? ResolveCustomProperties(
        List<(uint Specificity, int Order, int Index)> normalMatched,
        List<(uint Specificity, int Order, int Index)> importantMatched,
        IReadOnlyDictionary<string, string> parentProps,
        string shadowNormal,
        DeclarationStreamFlags shadowNormalFlags,
        string shadowImportant,
        DeclarationStreamFlags shadowImportantFlags,
        string inlineNormal,
        DeclarationStreamFlags inlineNormalFlags,
        string inlineImportant,
        DeclarationStreamFlags inlineImportantFlags,
        bool registrationsChangeParent)
    {
        var own = new List<(string Name, string Value)>();

        void CollectCustom(string css)
        {
            foreach (var declaration in CssDeclarations.Split(css))
            {
                var separator = declaration.IndexOf(':');
                if (separator < 0)
                {
                    continue;
                }

                var name = declaration[..separator].Trim();
                if (name.StartsWith("--", StringComparison.Ordinal) && name.Length > 2)
                {
                    own.Add((name, declaration[(separator + 1)..].Trim()));
                }
            }
        }

        if (shadowNormalFlags.HasCustomProperties)
        {
            CollectCustom(shadowNormal);
        }

        foreach (var (_, _, index) in normalMatched)
        {
            var rule = Rules[index];
            if (rule.NormalFlags.HasCustomProperties)
            {
                CollectCustom(rule.NormalDecls);
            }
        }

        if (inlineNormalFlags.HasCustomProperties)
        {
            CollectCustom(inlineNormal);
        }

        foreach (var (_, _, index) in importantMatched)
        {
            var rule = Rules[index];
            if (rule.ImportantFlags.HasCustomProperties)
            {
                CollectCustom(rule.ImportantDecls);
            }
        }

        if (inlineImportantFlags.HasCustomProperties)
        {
            CollectCustom(inlineImportant);
        }

        if (shadowImportantFlags.HasCustomProperties)
        {
            CollectCustom(shadowImportant);
        }

        var resolved = new Dictionary<string, string>(parentProps, StringComparer.Ordinal);
        foreach (var (name, registration) in RegisteredCustomProperties)
        {
            if (registration.Inherits && resolved.ContainsKey(name))
            {
                continue;
            }

            if (registration.InitialValue is { } initial)
            {
                resolved[name] = initial;
            }
            else
            {
                resolved.Remove(name);
            }
        }

        var hasOwn = own.Count != 0;
        var ownNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, value) in own)
        {
            ownNames.Add(name);
            RegisteredCustomProperties.TryGetValue(name, out var registration);

            void SetInitial()
            {
                if (registration?.InitialValue is { } initial)
                {
                    resolved[name] = initial;
                }
                else
                {
                    resolved.Remove(name);
                }
            }

            void Inherit()
            {
                if (parentProps.TryGetValue(name, out var inherited))
                {
                    resolved[name] = inherited;
                }
                else
                {
                    SetInitial();
                }
            }

            switch (CssText.AsciiLower(value.Trim()))
            {
                case "initial":
                    SetInitial();
                    break;
                case "inherit":
                    Inherit();
                    break;
                case "unset":
                case "revert":
                case "revert-layer":
                    if (registration is not null && !registration.Inherits)
                    {
                        SetInitial();
                    }
                    else
                    {
                        Inherit();
                    }

                    break;
                default:
                {
                    var valid = registration is null
                        || (CssVariables.SubstituteVarValue(value, resolved, 0) is { } substituted
                            && CssPropertyRegistration.ValueMatches(registration, substituted));
                    if (valid)
                    {
                        resolved[name] = value;
                    }
                    else
                    {
                        SetInitial();
                    }

                    break;
                }
            }
        }

        // Custom properties inherit their computed value, not their original
        // token stream. Resolve only declarations won on this element against
        // the complete same-element environment (so forward references work),
        // then pass those substituted values to descendants. Re-resolving an
        // inherited `--b:var(--a)` after a child overrides `--a` is observably
        // wrong: browsers keep the parent's already-computed `--b`.
        var environment = new Dictionary<string, string>(resolved, StringComparer.Ordinal);
        foreach (var name in ownNames)
        {
            if (!environment.TryGetValue(name, out var value))
            {
                continue;
            }

            if (CssVariables.SubstituteVarValue(value, environment, 0) is { } computed)
            {
                resolved[name] = computed;
            }
            else if (RegisteredCustomProperties.TryGetValue(name, out var registration)
                && registration.InitialValue is { } initial)
            {
                resolved[name] = initial;
            }
            else
            {
                resolved.Remove(name);
            }
        }

        return hasOwn || registrationsChangeParent ? resolved : null;
    }
}
