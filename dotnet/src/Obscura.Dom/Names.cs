namespace Obscura.Dom;

/// <summary>The namespace URIs the tree distinguishes, matching html5ever's <c>ns!()</c> atoms.</summary>
public static class Namespaces
{
    public const string None = "";
    public const string Html = "http://www.w3.org/1999/xhtml";
    public const string MathMl = "http://www.w3.org/1998/Math/MathML";
    public const string Svg = "http://www.w3.org/2000/svg";
    public const string XLink = "http://www.w3.org/1999/xlink";
    public const string Xml = "http://www.w3.org/XML/1998/namespace";
    public const string XmlNs = "http://www.w3.org/2000/xmlns/";
}

/// <summary>
/// The port of html5ever's <c>QualName</c>: an optional prefix, a namespace URI and a local name.
/// The prefix is kept separate from the local name so qualified-name APIs and serialization can
/// reconstruct <c>prefix:local</c> without the attribute store having to guess.
/// </summary>
public readonly record struct QualName(string? Prefix, string Ns, string Local)
{
    public static QualName Html(string local) => new(null, Namespaces.Html, local);

    /// <summary>An attribute name in no namespace, which is what the HTML parser produces.</summary>
    public static QualName Attr(string local) => new(null, Namespaces.None, local);

    public bool Equals(QualName other) =>
        string.Equals(Prefix, other.Prefix, StringComparison.Ordinal)
        && string.Equals(Ns, other.Ns, StringComparison.Ordinal)
        && string.Equals(Local, other.Local, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(
        Prefix is null ? 0 : StringComparer.Ordinal.GetHashCode(Prefix),
        StringComparer.Ordinal.GetHashCode(Ns),
        StringComparer.Ordinal.GetHashCode(Local));

    public override string ToString() => Prefix is null ? Local : $"{Prefix}:{Local}";
}
