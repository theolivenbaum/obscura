using System.Text;

namespace Obscura.Render.Css;

/// <summary>One flattened author rule produced by the stylesheet parser.</summary>
public sealed record ParsedRule(
    string Selector,
    string Declarations,
    ContainerConditionId ContainerConditionId,
    LayerOrder? Layer);

/// <summary>
/// The stylesheet parser: splits source into <c>(selector, declarations)</c>
/// rules, flattens CSS Nesting, and resolves the at-rules that carry ordinary
/// rules inside.
/// </summary>
/// <remarks>
/// Error recovery never throws. Unbalanced braces, unterminated comments,
/// unknown at-rules and malformed preludes are all absorbed the way a browser
/// absorbs them, because real sheets ship all of them.
/// </remarks>
public static class CssParser
{
    /// <summary>
    /// Split a stylesheet into <c>(selector, declarations)</c> rules against the
    /// default desktop viewport.
    /// </summary>
    public static List<(string Selector, string Declarations)> ParseStylesheet(string css) =>
        ParseStylesheetForViewport(css, (1280f, 720f));

    public static List<(string Selector, string Declarations)> ParseStylesheetForViewport(
        string css,
        (float Width, float Height) viewport)
    {
        var conditions = NewConditionArena();
        var parsed = ParseStylesheetForViewportPreservingContainers(
            css,
            viewport,
            CssMediaType.Screen,
            conditions,
            ContainerConditionId.None);

        var rules = new List<(string, string)>();
        foreach (var rule in parsed)
        {
            // This legacy tuple API cannot express conditional context. Keep its
            // established behavior by omitting unresolved container rules.
            if (rule.ContainerConditionId == ContainerConditionId.None
                && !rule.Selector.StartsWith(CssAtRules.PropertyRegistrationSelectorPrefix, StringComparison.Ordinal))
            {
                rules.Add((rule.Selector, rule.Declarations));
            }
        }

        return rules;
    }

    /// <summary>A fresh condition arena whose index zero is the unconditional sentinel.</summary>
    public static List<ContainerConditionNode> NewConditionArena() =>
        [new ContainerConditionNode(ContainerConditionId.None, [])];

    public static List<ParsedRule> ParseStylesheetForViewportPreservingContainers(
        string css,
        (float Width, float Height) viewport,
        CssMediaType mediaType,
        List<ContainerConditionNode> containerConditions,
        ContainerConditionId containerConditionId)
    {
        var layers = new LayerRegistry();
        return ParseStylesheetForViewportPreservingContainersInLayer(
            css,
            viewport,
            mediaType,
            containerConditions,
            containerConditionId,
            layers,
            null);
    }

    public static List<ParsedRule> ParseStylesheetForViewportPreservingContainersInLayer(
        string css,
        (float Width, float Height) viewport,
        CssMediaType mediaType,
        List<ContainerConditionNode> containerConditions,
        ContainerConditionId containerConditionId,
        LayerRegistry layers,
        LayerOrder? currentLayer)
    {
        var rules = new List<ParsedRule>();
        var currentSelector = new StringBuilder();
        var currentDeclarations = new StringBuilder();
        var blockDepth = 0;
        var inComment = false;
        var index = 0;

        while (index < css.Length)
        {
            var character = css[index];
            index++;

            if (inComment)
            {
                if (character == '*' && index < css.Length && css[index] == '/')
                {
                    index++;
                    inComment = false;
                }

                continue;
            }

            if (character == '/' && index < css.Length && css[index] == '*')
            {
                index++;
                inComment = true;
                continue;
            }

            if (character == '{')
            {
                if (blockDepth != 0)
                {
                    currentDeclarations.Append(character);
                }

                blockDepth++;
            }
            else if (character == '}' && blockDepth == 0)
            {
                // Stray top-level close brace (unbalanced author CSS; remoteok.com
                // ships one mid-sheet). Browsers error-recover and keep parsing;
                // without this blockDepth goes negative and the state machine
                // inverts, scrambling and losing every rule in the rest of the sheet.
                currentSelector.Clear();
            }
            else if (character == '}')
            {
                blockDepth--;
                if (blockDepth == 0)
                {
                    var selector = currentSelector.ToString().Trim();
                    var declarations = currentDeclarations.ToString().Trim();
                    if (selector.StartsWith('@'))
                    {
                        FlushAtRule(
                            selector[1..],
                            declarations,
                            rules,
                            viewport,
                            mediaType,
                            containerConditions,
                            containerConditionId,
                            layers,
                            currentLayer);
                    }
                    else
                    {
                        // The body may contain nested rules (CSS Nesting, ubiquitous
                        // in Tailwind v4 / modern frameworks: `.a{ &:hover{} .b{} }`).
                        // Flatten them against this selector; Denest also handles the
                        // no-nesting case (just emits the rule's own declarations).
                        Denest(
                            selector,
                            declarations,
                            rules,
                            viewport,
                            mediaType,
                            containerConditions,
                            containerConditionId,
                            layers,
                            currentLayer);
                    }

                    currentSelector.Clear();
                    currentDeclarations.Clear();
                }
                else
                {
                    currentDeclarations.Append(character);
                }
            }
            else if (character == ';' && blockDepth == 0)
            {
                // Layer ordering statements establish slots even though they emit
                // no selector rules. All other statement at-rules are discarded so
                // their prelude cannot bleed into the next selector.
                var statement = currentSelector.ToString().Trim();
                if (statement.StartsWith('@')
                    && CssAtRules.Prelude(statement[1..], "layer") is { } prelude)
                {
                    layers.RegisterStatement(currentLayer, prelude);
                }

                currentSelector.Clear();
            }
            else if (blockDepth > 0)
            {
                currentDeclarations.Append(character);
            }
            else
            {
                currentSelector.Append(character);
            }
        }

        return rules;
    }

    /// <summary>
    /// Handle the at-rules whose bodies contain ordinary rules.
    /// </summary>
    private static void FlushAtRule(
        string at,
        string inner,
        List<ParsedRule> rules,
        (float Width, float Height) viewport,
        CssMediaType mediaType,
        List<ContainerConditionNode> containerConditions,
        ContainerConditionId containerConditionId,
        LayerRegistry layers,
        LayerOrder? currentLayer)
    {
        if (CssAtRules.Prelude(at, "media") is { } mediaPrelude)
        {
            if (CssMediaQuery.AppliesForViewportAndType(mediaPrelude, viewport, mediaType))
            {
                rules.AddRange(ParseStylesheetForViewportPreservingContainersInLayer(
                    inner,
                    viewport,
                    mediaType,
                    containerConditions,
                    containerConditionId,
                    layers,
                    currentLayer));
            }

            return;
        }

        if (CssAtRules.Prelude(at, "supports") is { } supportsPrelude)
        {
            if (CssSupports.ConditionApplies(supportsPrelude))
            {
                rules.AddRange(ParseStylesheetForViewportPreservingContainersInLayer(
                    inner,
                    viewport,
                    mediaType,
                    containerConditions,
                    containerConditionId,
                    layers,
                    currentLayer));
            }

            return;
        }

        if (CssAtRules.Prelude(at, "container") is { } containerPrelude)
        {
            if (CssContainerQuery.ParseQueryList(containerPrelude) is { } alternatives)
            {
                var id = new ContainerConditionId((uint)containerConditions.Count);
                containerConditions.Add(new ContainerConditionNode(containerConditionId, alternatives));
                rules.AddRange(ParseStylesheetForViewportPreservingContainersInLayer(
                    inner,
                    viewport,
                    mediaType,
                    containerConditions,
                    id,
                    layers,
                    currentLayer));
            }

            return;
        }

        var keyframesName = CssAtRules.Prelude(at, "keyframes");
        var prefixed = false;
        if (keyframesName is null)
        {
            keyframesName = CssAtRules.Prelude(at, "-webkit-keyframes");
            prefixed = keyframesName is not null;
        }

        if (keyframesName is not null)
        {
            var name = keyframesName.Trim();
            if (name.Length != 0)
            {
                rules.Add(new ParsedRule(
                    (prefixed ? CssAtRules.WebkitKeyframesSelectorPrefix : CssAtRules.KeyframesSelectorPrefix) + name,
                    inner,
                    ContainerConditionId.None,
                    currentLayer?.Clone()));
            }

            return;
        }

        if (CssAtRules.Prelude(at, "property") is { } propertyName)
        {
            if (propertyName.StartsWith("--", StringComparison.Ordinal))
            {
                rules.Add(new ParsedRule(
                    CssAtRules.PropertyRegistrationSelectorPrefix + propertyName,
                    inner,
                    // Registrations are global name-defining rules. CSS
                    // Conditional 5 deliberately does not gate them on an
                    // enclosing container query.
                    ContainerConditionId.None,
                    null));
            }

            return;
        }

        if (CssAtRules.Prelude(at, "layer") is { } layerPrelude)
        {
            LayerOrder? layer;
            if (layerPrelude.Trim().Length == 0)
            {
                layer = layers.RegisterAnonymous(currentLayer);
            }
            else
            {
                layer = layers.RegisterNamed(currentLayer, layerPrelude);
                if (layer is null)
                {
                    return;
                }
            }

            rules.AddRange(ParseStylesheetForViewportPreservingContainersInLayer(
                inner,
                viewport,
                mediaType,
                containerConditions,
                containerConditionId,
                layers,
                layer));
        }

        // Other at-rules (@font-face, @import, ...) carry no layout-relevant
        // rules for us, so drop them.
    }

    /// <summary>
    /// Flatten a rule body that may contain nested rules (CSS Nesting) into flat
    /// rules.
    /// </summary>
    /// <remarks>
    /// The parser hands the whole body of a rule; here its own declarations are
    /// separated from nested <c>sel { ... }</c> blocks (and nested
    /// <c>@media</c>/<c>@supports</c>/<c>@layer</c> at-rules, which keep the
    /// parent's selector), <c>(sel, own-declarations)</c> is emitted, and each
    /// nested rule is recursed into with the combined selector. Without this,
    /// Tailwind v4 / modern-framework CSS (which nests almost everything) loses
    /// the nested utility rules entirely.
    /// </remarks>
    private static void Denest(
        string selector,
        string body,
        List<ParsedRule> rules,
        (float Width, float Height) viewport,
        CssMediaType mediaType,
        List<ContainerConditionNode> containerConditions,
        ContainerConditionId containerConditionId,
        LayerRegistry layers,
        LayerOrder? currentLayer)
    {
        var length = body.Length;
        var index = 0;
        var segment = 0;
        var own = new StringBuilder();
        char? quote = null;
        var comment = false;
        var paren = 0;

        while (index < length)
        {
            var character = body[index];
            if (comment)
            {
                if (character == '*' && index + 1 < length && body[index + 1] == '/')
                {
                    comment = false;
                    index += 2;
                    continue;
                }

                index++;
                continue;
            }

            if (quote is { } activeQuote)
            {
                if (character == activeQuote)
                {
                    quote = null;
                }

                index++;
                continue;
            }

            if (character == '/' && index + 1 < length && body[index + 1] == '*')
            {
                comment = true;
                index += 2;
                continue;
            }

            switch (character)
            {
                case '\'':
                case '"':
                    quote = character;
                    break;
                case '(':
                    paren++;
                    break;
                case ')':
                    paren = Math.Max(paren - 1, 0);
                    break;
                case '{' when paren == 0:
                {
                    var prelude = body[segment..index];

                    // Find the matching close brace (quote/comment aware).
                    var depth = 1;
                    var scan = index + 1;
                    char? innerQuote = null;
                    var innerComment = false;
                    while (scan < length && depth > 0)
                    {
                        var scanned = body[scan];
                        if (innerComment)
                        {
                            if (scanned == '*' && scan + 1 < length && body[scan + 1] == '/')
                            {
                                innerComment = false;
                                scan += 2;
                                continue;
                            }
                        }
                        else if (innerQuote is { } activeInnerQuote)
                        {
                            if (scanned == activeInnerQuote)
                            {
                                innerQuote = null;
                            }
                        }
                        else if (scanned == '/' && scan + 1 < length && body[scan + 1] == '*')
                        {
                            innerComment = true;
                            scan += 2;
                            continue;
                        }
                        else if (scanned is '\'' or '"')
                        {
                            innerQuote = scanned;
                        }
                        else if (scanned == '{')
                        {
                            depth++;
                        }
                        else if (scanned == '}')
                        {
                            depth--;
                        }

                        scan++;
                    }

                    var innerEnd = Math.Max(scan - 1, index + 1);
                    var inner = body[(index + 1)..innerEnd];
                    var pre = prelude.Trim();
                    if (pre.StartsWith('@'))
                    {
                        DenestAtRule(
                            pre[1..],
                            selector,
                            inner,
                            rules,
                            viewport,
                            mediaType,
                            containerConditions,
                            containerConditionId,
                            layers,
                            currentLayer);
                    }
                    else if (pre.Length != 0)
                    {
                        var full = CssSelectorText.CombineSelectors(selector, pre);
                        Denest(
                            full,
                            inner,
                            rules,
                            viewport,
                            mediaType,
                            containerConditions,
                            containerConditionId,
                            layers,
                            currentLayer);
                    }

                    index = scan;
                    segment = index;
                    continue;
                }

                case ';' when paren == 0:
                {
                    var declaration = body[segment..index].Trim();
                    if (declaration.StartsWith('@'))
                    {
                        if (CssAtRules.Prelude(declaration[1..], "layer") is { } prelude)
                        {
                            layers.RegisterStatement(currentLayer, prelude);
                        }
                    }
                    else if (declaration.Length != 0)
                    {
                        own.Append(declaration);
                        own.Append(';');
                    }

                    index++;
                    segment = index;
                    continue;
                }
            }

            index++;
        }

        var tail = body[segment..].Trim();
        if (tail.Length != 0 && !tail.Contains('{', StringComparison.Ordinal))
        {
            own.Append(tail);
            own.Append(';');
        }

        if (own.ToString().Trim().Length == 0)
        {
            return;
        }

        var declarations = own.ToString();
        foreach (var part in CssSelectorText.SplitSelectorList(selector))
        {
            var trimmed = part.Trim();
            if (trimmed.Length != 0)
            {
                rules.Add(new ParsedRule(trimmed, declarations, containerConditionId, currentLayer?.Clone()));
            }
        }
    }

    private static void DenestAtRule(
        string at,
        string selector,
        string inner,
        List<ParsedRule> rules,
        (float Width, float Height) viewport,
        CssMediaType mediaType,
        List<ContainerConditionNode> containerConditions,
        ContainerConditionId containerConditionId,
        LayerRegistry layers,
        LayerOrder? currentLayer)
    {
        // A nested at-rule keeps the enclosing selector for its body.
        if (CssAtRules.Prelude(at, "media") is { } mediaPrelude)
        {
            if (CssMediaQuery.AppliesForViewportAndType(mediaPrelude, viewport, mediaType))
            {
                Denest(selector, inner, rules, viewport, mediaType, containerConditions, containerConditionId, layers, currentLayer);
            }

            return;
        }

        if (CssAtRules.Prelude(at, "supports") is { } supportsPrelude)
        {
            if (CssSupports.ConditionApplies(supportsPrelude))
            {
                Denest(selector, inner, rules, viewport, mediaType, containerConditions, containerConditionId, layers, currentLayer);
            }

            return;
        }

        if (CssAtRules.Prelude(at, "container") is { } containerPrelude)
        {
            if (CssContainerQuery.ParseQueryList(containerPrelude) is { } alternatives)
            {
                var id = new ContainerConditionId((uint)containerConditions.Count);
                containerConditions.Add(new ContainerConditionNode(containerConditionId, alternatives));
                Denest(selector, inner, rules, viewport, mediaType, containerConditions, id, layers, currentLayer);
            }

            return;
        }

        if (CssAtRules.Prelude(at, "layer") is { } layerPrelude)
        {
            var layer = layerPrelude.Trim().Length == 0
                ? layers.RegisterAnonymous(currentLayer)
                : layers.RegisterNamed(currentLayer, layerPrelude);
            if (layer is not null)
            {
                Denest(selector, inner, rules, viewport, mediaType, containerConditions, containerConditionId, layers, layer);
            }
        }
    }

    /// <summary>
    /// Every <c>@keyframes</c> body in a source, keyed by animation name. The
    /// Rust original is a test-only helper.
    /// </summary>
    internal static List<(string Name, Keyframes Frames)> ExtractKeyframes(string css)
    {
        var conditions = NewConditionArena();
        var parsed = ParseStylesheetForViewportPreservingContainers(
            css,
            (1280f, 720f),
            CssMediaType.Screen,
            conditions,
            ContainerConditionId.None);

        var result = new List<(string, Keyframes)>();
        foreach (var rule in parsed)
        {
            string? name = null;
            if (rule.Selector.StartsWith(CssAtRules.KeyframesSelectorPrefix, StringComparison.Ordinal))
            {
                name = rule.Selector[CssAtRules.KeyframesSelectorPrefix.Length..];
            }
            else if (rule.Selector.StartsWith(CssAtRules.WebkitKeyframesSelectorPrefix, StringComparison.Ordinal))
            {
                name = rule.Selector[CssAtRules.WebkitKeyframesSelectorPrefix.Length..];
            }

            if (name is not null)
            {
                result.Add((name, CssKeyframes.CompileBody(rule.Declarations)));
            }
        }

        return result;
    }
}
