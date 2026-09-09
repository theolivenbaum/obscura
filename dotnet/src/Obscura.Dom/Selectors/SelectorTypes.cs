namespace Obscura.Dom.Selectors;

/// <summary>How one compound selector relates to the compound to its right.</summary>
public enum Combinator
{
    Descendant,
    Child,
    NextSibling,
    LaterSibling,
    /// <summary>The implicit combinator of <c>::slotted()</c>: step to the assigned slot.</summary>
    SlotAssignment,
    /// <summary>The implicit combinator before a pseudo-element.</summary>
    PseudoElement,
}

public static class CombinatorExtensions
{
    public static bool IsSibling(this Combinator combinator) =>
        combinator is Combinator.NextSibling or Combinator.LaterSibling;
}

/// <summary>The non-tree-structural pseudo-classes Obscura understands.</summary>
public enum PseudoClass
{
    Hover,
    Active,
    Focus,
    FocusVisible,
    FocusWithin,
    Enabled,
    Disabled,
    Checked,
    /// <summary>
    /// <c>:link</c> / <c>:any-link</c>: an <c>&lt;a&gt;</c>/<c>&lt;area&gt;</c> with an
    /// <c>href</c>. Extremely common, so treating it as unsupported silently drops the whole rule
    /// from the cascade, not just the pseudo-class.
    /// </summary>
    Link,
    /// <summary>
    /// <c>:visited</c>. We have no browsing history, so this never matches, the same fallback real
    /// browsers use when history-based styling is suppressed for privacy.
    /// </summary>
    Visited,
}

public enum PseudoElement
{
    Before,
    After,
}

public enum NthType
{
    Child,
    LastChild,
    OnlyChild,
    OfType,
    LastOfType,
    OnlyOfType,
}

/// <summary>The An+B data of an <c>:nth-*</c> pseudo-class.</summary>
public readonly record struct NthData(NthType Type, int A, int B)
{
    public bool IsOnly => Type is NthType.OnlyChild or NthType.OnlyOfType;

    public bool IsOfType => Type is NthType.OfType or NthType.LastOfType or NthType.OnlyOfType;

    public bool IsFromEnd => Type is NthType.LastChild or NthType.LastOfType;

    /// <summary>
    /// Whether this is an edge selector that is not <c>:*-of-type</c> or <c>:only-*</c>: it can
    /// only ever select the first (or last) element child.
    /// </summary>
    public bool IsSimpleEdge => A == 0 && B == 1 && !IsOfType && !IsOnly;

    public static NthData First(bool ofType) =>
        new(ofType ? NthType.OfType : NthType.Child, 0, 1);

    public static NthData Last(bool ofType) =>
        new(ofType ? NthType.LastOfType : NthType.LastChild, 0, 1);
}

public enum AttrOperator
{
    Equal,
    Includes,
    DashMatch,
    Prefix,
    Substring,
    Suffix,
}

public enum ParsedCaseSensitivity
{
    /// <summary>'s' was specified.</summary>
    ExplicitCaseSensitive,
    /// <summary>'i' was specified.</summary>
    AsciiCaseInsensitive,
    /// <summary>No flags were specified and HTML says this is a case-sensitive attribute.</summary>
    CaseSensitive,
    /// <summary>No flags were specified and HTML says this is a case-insensitive attribute.</summary>
    AsciiCaseInsensitiveIfInHtmlElementInHtmlDocument,
}

public enum CaseSensitivity
{
    CaseSensitive,
    AsciiCaseInsensitive,
}

/// <summary>The namespace part of an attribute selector.</summary>
public enum NamespaceConstraintKind
{
    /// <summary>No prefix was written: the attribute must be in no namespace.</summary>
    NoNamespace,
    /// <summary><c>*|attr</c>.</summary>
    Any,
    /// <summary><c>ns|attr</c>.</summary>
    Specific,
}

[Flags]
internal enum SelectorFlags
{
    None = 0,
    HasPseudo = 1 << 0,
    HasSlotted = 1 << 1,
    HasPart = 1 << 2,
    HasHost = 1 << 3,
    HasScope = 1 << 4,
    HasParent = 1 << 5,
    HasNonFeaturelessComponent = 1 << 6,
}

[Flags]
public enum FeaturelessHostMatches
{
    None = 0,
    ForHost = 1 << 0,
    ForScope = 1 << 1,
}

// ---------------------------------------------------------------------- components

/// <summary>One simple selector inside a compound.</summary>
public abstract class Component
{
    private protected Component() { }
}

public sealed class LocalNameComponent(string name, string lowerName) : Component
{
    public readonly string Name = name;
    public readonly string LowerName = lowerName;
}

public sealed class IdComponent(string id) : Component
{
    public readonly string Id = id;
}

public sealed class ClassComponent(string className) : Component
{
    public readonly string ClassName = className;
}

public sealed class AttributeComponent(
    NamespaceConstraintKind namespaceKind,
    string? namespaceUrl,
    string localName,
    string localNameLower,
    AttrOperator? op,
    string? value,
    ParsedCaseSensitivity caseSensitivity) : Component
{
    public readonly NamespaceConstraintKind NamespaceKind = namespaceKind;
    public readonly string? NamespaceUrl = namespaceUrl;
    public readonly string LocalName = localName;
    public readonly string LocalNameLower = localNameLower;
    public readonly AttrOperator? Operator = op;
    public readonly string? Value = value;
    public readonly ParsedCaseSensitivity CaseSensitivity = caseSensitivity;
}

/// <summary><c>*</c>, or <c>*|*</c>.</summary>
public sealed class ExplicitUniversalTypeComponent : Component
{
    public static readonly ExplicitUniversalTypeComponent Instance = new();
}

/// <summary><c>*|</c> in a type selector.</summary>
public sealed class ExplicitAnyNamespaceComponent : Component
{
    public static readonly ExplicitAnyNamespaceComponent Instance = new();
}

/// <summary><c>|</c> in a type selector: the element must be in no namespace.</summary>
public sealed class ExplicitNoNamespaceComponent : Component
{
    public static readonly ExplicitNoNamespaceComponent Instance = new();
}

public sealed class NegationComponent(SelectorList list) : Component
{
    public readonly SelectorList List = list;
}

public sealed class RootComponent : Component
{
    public static readonly RootComponent Instance = new();
}

public sealed class EmptyComponent : Component
{
    public static readonly EmptyComponent Instance = new();
}

public sealed class ScopeComponent : Component
{
    public static readonly ScopeComponent Instance = new();
}

public sealed class NthComponent(NthData data, SelectorList? of) : Component
{
    public readonly NthData Data = data;

    /// <summary>The <c>of S</c> selector list of <c>:nth-child(An+B of S)</c>, if any.</summary>
    public readonly SelectorList? Of = of;
}

public sealed class NonTsPseudoClassComponent(PseudoClass pseudoClass) : Component
{
    public readonly PseudoClass PseudoClass = pseudoClass;
}

public sealed class SlottedComponent(Selector selector) : Component
{
    public readonly Selector Selector = selector;
}

public sealed class HostComponent(Selector? selector) : Component
{
    public readonly Selector? Selector = selector;
}

public sealed class IsComponent(SelectorList list) : Component
{
    public readonly SelectorList List = list;
}

public sealed class WhereComponent(SelectorList list) : Component
{
    public readonly SelectorList List = list;
}

public sealed class HasComponent(RelativeSelector[] relatives) : Component
{
    public readonly RelativeSelector[] Relatives = relatives;
}

public sealed class PseudoElementComponent(PseudoElement pseudoElement) : Component
{
    public readonly PseudoElement PseudoElement = pseudoElement;
}

/// <summary>An invalid selector inside a forgiving <c>:is()</c> / <c>:where()</c> list.</summary>
public sealed class InvalidComponent : Component
{
    public static readonly InvalidComponent Instance = new();
}

/// <summary>
/// The synthetic leftmost component of a relative selector: it matches only the <c>:has()</c>
/// anchor element.
/// </summary>
public sealed class RelativeSelectorAnchorComponent : Component
{
    public static readonly RelativeSelectorAnchorComponent Instance = new();
}

// ---------------------------------------------------------------------- selectors

/// <summary>One compound selector: simple selectors with no combinator between them.</summary>
public sealed class CompoundSelector(Component[] components)
{
    public readonly Component[] Components = components;
}

/// <summary>
/// A complex selector, stored in match order: <c>Compounds[0]</c> is the subject (rightmost)
/// compound and <c>Combinators[i]</c> joins <c>Compounds[i]</c> to <c>Compounds[i + 1]</c>, which
/// lies to its left.
/// </summary>
public sealed class Selector
{
    internal Selector(CompoundSelector[] compounds, Combinator[] combinators, uint specificity, SelectorFlags flags)
    {
        Compounds = compounds;
        Combinators = combinators;
        Specificity = specificity;
        Flags = flags;
    }

    public CompoundSelector[] Compounds { get; }

    public Combinator[] Combinators { get; }

    /// <summary>The packed CSS specificity: ids in bits 20+, classes in bits 10+, elements low.</summary>
    public uint Specificity { get; }

    internal SelectorFlags Flags { get; }

    /// <summary>The subject (rightmost) compound.</summary>
    public CompoundSelector Subject => Compounds[0];

    public bool IsSlotted => (Flags & SelectorFlags.HasSlotted) != 0;

    public bool IsPart => (Flags & SelectorFlags.HasPart) != 0;

    /// <summary>
    /// Whether this selector matches a featureless shadow host, with no combinators to the left,
    /// and optionally has a pseudo-element to the right.
    /// </summary>
    public FeaturelessHostMatches MatchesFeaturelessHostSelectorOrPseudoElement()
    {
        var result = FeaturelessHostMatches.None;
        if ((Flags & SelectorFlags.HasNonFeaturelessComponent) != 0)
        {
            return result;
        }

        if ((Flags & SelectorFlags.HasHost) != 0)
        {
            result |= FeaturelessHostMatches.ForHost;
        }

        if ((Flags & SelectorFlags.HasScope) != 0)
        {
            result |= FeaturelessHostMatches.ForScope;
        }

        return result;
    }
}

/// <summary>A comma-separated list of complex selectors.</summary>
public sealed class SelectorList(Selector[] selectors)
{
    public readonly Selector[] Selectors = selectors;

    public int Count => Selectors.Length;
}

/// <summary>One arm of <c>:has()</c>: a combinator plus the selector it introduces.</summary>
public sealed class RelativeSelector(Combinator combinator, Selector selector)
{
    public readonly Combinator Combinator = combinator;
    public readonly Selector Selector = selector;
}

/// <summary>Raised when a selector string cannot be parsed.</summary>
public sealed class SelectorParseException(string selector, string reason)
    : FormatException($"Failed to parse selector '{selector}': {reason}")
{
    public string Selector { get; } = selector;

    public string Reason { get; } = reason;
}
