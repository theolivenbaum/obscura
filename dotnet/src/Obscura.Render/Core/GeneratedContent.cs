namespace Obscura.Render;

/// <summary>One computed CSS counter operation.</summary>
public readonly record struct CounterDirective(string Name, int Value);

/// <summary>Counter styles supported in generated text.</summary>
public enum GeneratedCounterStyle
{
    /// <summary>The default.</summary>
    Decimal = 0,

    DecimalLeadingZero,
    LowerAlpha,
    UpperAlpha,
    LowerRoman,
    UpperRoman,
}

/// <summary>One item in a computed <c>content</c> value.</summary>
public abstract record GeneratedContentItem
{
    private GeneratedContentItem()
    {
    }

    public sealed record Text(string Value) : GeneratedContentItem;

    public sealed record Counter(string Name, GeneratedCounterStyle Style) : GeneratedContentItem;

    public sealed record Counters(string Name, string Separator, GeneratedCounterStyle Style)
        : GeneratedContentItem;
}
