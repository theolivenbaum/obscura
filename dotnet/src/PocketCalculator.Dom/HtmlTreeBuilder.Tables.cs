using System.Collections.Frozen;

namespace PocketCalculator.Dom;

/// <summary>
/// The HTML element names the tree construction rules single out. Anything else is
/// <see cref="Unknown"/> and is matched by its name. The value names are the tag names.
/// </summary>
internal enum HtmlTag : byte
{
    Unknown = 0,
    A, Address, Applet, Area, Article, Aside, B, Base, Basefont, Bgsound, Big, Blockquote,
    Body, Br, Button, Caption, Center, Code, Col, Colgroup, Dd, Details, Dialog, Dir, Div, Dl,
    Dt, Em, Embed, Fieldset, Figcaption, Figure, Font, Footer, Form, Frame, Frameset, H1, H2,
    H3, H4, H5, H6, Head, Header, Hgroup, Hr, Html, I, Iframe, Image, Img, Input, Keygen, Li,
    Link, Listing, Main, Marquee, Math, Menu, Meta, Nav, Nobr, Noembed, Noframes, Noscript,
    Object, Ol, Optgroup, Option, P, Param, Plaintext, Pre, Rb, Rp, Rt, Rtc, Ruby, S, Script,
    Search, Section, Select, Small, Source, Span, Strike, Strong, Style, Sub, Summary, Sup,
    Svg, Table, Tbody, Td, Template, Textarea, Tfoot, Th, Thead, Title, Tr, Track, Tt, U, Ul,
    Var, Wbr, Xmp,
    Count,
}

internal sealed partial class HtmlTreeBuilder
{
    /// <summary>Tag names by <see cref="HtmlTag"/>, lowercase.</summary>
    private static readonly string[] TagNames = BuildTagNames();

    private static readonly FrozenDictionary<string, HtmlTag> TagsByName = BuildTagsByName();

    private static readonly FrozenDictionary<string, HtmlTag>.AlternateLookup<ReadOnlySpan<char>> TagsBySpan =
        TagsByName.GetAlternateLookup<ReadOnlySpan<char>>();

    private static string[] BuildTagNames()
    {
        var names = new string[(int)HtmlTag.Count];
        names[0] = "";
        for (var i = 1; i < names.Length; i++)
        {
            names[i] = string.Intern(((HtmlTag)i).ToString().ToLowerInvariant());
        }

        return names;
    }

    private static FrozenDictionary<string, HtmlTag> BuildTagsByName()
    {
        var map = new Dictionary<string, HtmlTag>(StringComparer.Ordinal);
        for (var i = 1; i < (int)HtmlTag.Count; i++)
        {
            map[TagNames[i]] = (HtmlTag)i;
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ categories

    private static readonly bool[] SpecialTags = TagSet(
        HtmlTag.Address, HtmlTag.Applet, HtmlTag.Area, HtmlTag.Article, HtmlTag.Aside, HtmlTag.Base,
        HtmlTag.Basefont, HtmlTag.Bgsound, HtmlTag.Blockquote, HtmlTag.Body, HtmlTag.Br,
        HtmlTag.Button, HtmlTag.Caption, HtmlTag.Center, HtmlTag.Col, HtmlTag.Colgroup, HtmlTag.Dd,
        HtmlTag.Details, HtmlTag.Dir, HtmlTag.Div, HtmlTag.Dl, HtmlTag.Dt, HtmlTag.Embed,
        HtmlTag.Fieldset, HtmlTag.Figcaption, HtmlTag.Figure, HtmlTag.Footer, HtmlTag.Form,
        HtmlTag.Frame, HtmlTag.Frameset, HtmlTag.H1, HtmlTag.H2, HtmlTag.H3, HtmlTag.H4, HtmlTag.H5,
        HtmlTag.H6, HtmlTag.Head, HtmlTag.Header, HtmlTag.Hgroup, HtmlTag.Hr, HtmlTag.Html,
        HtmlTag.Iframe, HtmlTag.Img, HtmlTag.Input, HtmlTag.Keygen, HtmlTag.Li, HtmlTag.Link,
        HtmlTag.Listing, HtmlTag.Main, HtmlTag.Marquee, HtmlTag.Menu, HtmlTag.Meta, HtmlTag.Nav,
        HtmlTag.Noembed, HtmlTag.Noframes, HtmlTag.Noscript, HtmlTag.Object, HtmlTag.Ol, HtmlTag.P,
        HtmlTag.Param, HtmlTag.Plaintext, HtmlTag.Pre, HtmlTag.Script, HtmlTag.Search,
        HtmlTag.Section, HtmlTag.Select, HtmlTag.Source, HtmlTag.Style, HtmlTag.Summary,
        HtmlTag.Table, HtmlTag.Tbody, HtmlTag.Td, HtmlTag.Template, HtmlTag.Textarea,
        HtmlTag.Tfoot, HtmlTag.Th, HtmlTag.Thead, HtmlTag.Title, HtmlTag.Tr, HtmlTag.Track,
        HtmlTag.Ul, HtmlTag.Wbr, HtmlTag.Xmp);

    /// <summary>
    /// The HTML elements that bound "has an element in scope". <c>select</c> is one since the
    /// customizable select parser changes, which Chromium ships: <c>&lt;p&gt;&lt;select&gt;&lt;p&gt;</c>
    /// nests the second p, and <c>&lt;/font&gt;</c> inside a select no longer adopts across it.
    /// </summary>
    private static readonly bool[] ScopeTags = TagSet(
        HtmlTag.Applet, HtmlTag.Caption, HtmlTag.Html, HtmlTag.Table, HtmlTag.Td, HtmlTag.Th,
        HtmlTag.Marquee, HtmlTag.Object, HtmlTag.Select, HtmlTag.Template);

    /// <summary>What "generate implied end tags" pops.</summary>
    private static readonly bool[] ImpliedEndTags = TagSet(
        HtmlTag.Dd, HtmlTag.Dt, HtmlTag.Li, HtmlTag.Optgroup, HtmlTag.Option, HtmlTag.P,
        HtmlTag.Rb, HtmlTag.Rp, HtmlTag.Rt, HtmlTag.Rtc);

    /// <summary>What "generate all implied end tags thoroughly" pops.</summary>
    private static readonly bool[] ThoroughImpliedEndTags = TagSet(
        HtmlTag.Caption, HtmlTag.Colgroup, HtmlTag.Dd, HtmlTag.Dt, HtmlTag.Li, HtmlTag.Optgroup,
        HtmlTag.Option, HtmlTag.P, HtmlTag.Rb, HtmlTag.Rp, HtmlTag.Rt, HtmlTag.Rtc, HtmlTag.Tbody,
        HtmlTag.Td, HtmlTag.Tfoot, HtmlTag.Th, HtmlTag.Thead, HtmlTag.Tr);

    /// <summary>The start tags that break out of foreign content.</summary>
    private static readonly bool[] ForeignBreakoutTags = TagSet(
        HtmlTag.B, HtmlTag.Big, HtmlTag.Blockquote, HtmlTag.Body, HtmlTag.Br, HtmlTag.Center,
        HtmlTag.Code, HtmlTag.Dd, HtmlTag.Div, HtmlTag.Dl, HtmlTag.Dt, HtmlTag.Em, HtmlTag.Embed,
        HtmlTag.H1, HtmlTag.H2, HtmlTag.H3, HtmlTag.H4, HtmlTag.H5, HtmlTag.H6, HtmlTag.Head,
        HtmlTag.Hr, HtmlTag.I, HtmlTag.Img, HtmlTag.Li, HtmlTag.Listing, HtmlTag.Menu,
        HtmlTag.Meta, HtmlTag.Nobr, HtmlTag.Ol, HtmlTag.P, HtmlTag.Pre, HtmlTag.Ruby, HtmlTag.S,
        HtmlTag.Small, HtmlTag.Span, HtmlTag.Strong, HtmlTag.Strike, HtmlTag.Sub, HtmlTag.Sup,
        HtmlTag.Table, HtmlTag.Tt, HtmlTag.U, HtmlTag.Ul, HtmlTag.Var);

    /// <summary>The start tags "in body" that close a <c>p</c> in button scope and insert.</summary>
    private static readonly bool[] BlockStartTags = TagSet(
        HtmlTag.Address, HtmlTag.Article, HtmlTag.Aside, HtmlTag.Blockquote, HtmlTag.Center,
        HtmlTag.Details, HtmlTag.Dialog, HtmlTag.Dir, HtmlTag.Div, HtmlTag.Dl, HtmlTag.Fieldset,
        HtmlTag.Figcaption, HtmlTag.Figure, HtmlTag.Footer, HtmlTag.Header, HtmlTag.Hgroup,
        HtmlTag.Main, HtmlTag.Menu, HtmlTag.Nav, HtmlTag.Ol, HtmlTag.P, HtmlTag.Search,
        HtmlTag.Section, HtmlTag.Summary, HtmlTag.Ul);

    /// <summary>The end tags "in body" that close an element of the same name in scope.</summary>
    private static readonly bool[] BlockEndTags = TagSet(
        HtmlTag.Address, HtmlTag.Article, HtmlTag.Aside, HtmlTag.Blockquote, HtmlTag.Button,
        HtmlTag.Center, HtmlTag.Details, HtmlTag.Dialog, HtmlTag.Dir, HtmlTag.Div, HtmlTag.Dl,
        HtmlTag.Fieldset, HtmlTag.Figcaption, HtmlTag.Figure, HtmlTag.Footer, HtmlTag.Header,
        HtmlTag.Hgroup, HtmlTag.Listing, HtmlTag.Main, HtmlTag.Menu, HtmlTag.Nav, HtmlTag.Ol,
        HtmlTag.Pre, HtmlTag.Search, HtmlTag.Section, HtmlTag.Summary, HtmlTag.Ul);

    /// <summary>The elements "reset the insertion mode appropriately" stops at.</summary>
    private static readonly HtmlTag[] ResetModeTags =
    [
        HtmlTag.Td, HtmlTag.Th, HtmlTag.Tr, HtmlTag.Tbody, HtmlTag.Thead, HtmlTag.Tfoot,
        HtmlTag.Caption, HtmlTag.Colgroup, HtmlTag.Table, HtmlTag.Template, HtmlTag.Head,
        HtmlTag.Body, HtmlTag.Frameset, HtmlTag.Html,
    ];

    private static bool[] TagSet(params HtmlTag[] tags)
    {
        var set = new bool[(int)HtmlTag.Count];
        foreach (var tag in tags)
        {
            set[(int)tag] = true;
        }

        return set;
    }

    private static bool IsHeading(HtmlTag tag) => tag is >= HtmlTag.H1 and <= HtmlTag.H6;

    // ------------------------------------------------------------------ foreign content

    private static readonly FrozenDictionary<string, string> SvgTagNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["altglyph"] = "altGlyph", ["altglyphdef"] = "altGlyphDef", ["altglyphitem"] = "altGlyphItem",
        ["animatecolor"] = "animateColor", ["animatemotion"] = "animateMotion",
        ["animatetransform"] = "animateTransform", ["clippath"] = "clipPath", ["feblend"] = "feBlend",
        ["fecolormatrix"] = "feColorMatrix", ["fecomponenttransfer"] = "feComponentTransfer",
        ["fecomposite"] = "feComposite", ["feconvolvematrix"] = "feConvolveMatrix",
        ["fediffuselighting"] = "feDiffuseLighting", ["fedisplacementmap"] = "feDisplacementMap",
        ["fedistantlight"] = "feDistantLight", ["fedropshadow"] = "feDropShadow", ["feflood"] = "feFlood",
        ["fefunca"] = "feFuncA", ["fefuncb"] = "feFuncB", ["fefuncg"] = "feFuncG", ["fefuncr"] = "feFuncR",
        ["fegaussianblur"] = "feGaussianBlur", ["feimage"] = "feImage", ["femerge"] = "feMerge",
        ["femergenode"] = "feMergeNode", ["femorphology"] = "feMorphology", ["feoffset"] = "feOffset",
        ["fepointlight"] = "fePointLight", ["fespecularlighting"] = "feSpecularLighting",
        ["fespotlight"] = "feSpotLight", ["fetile"] = "feTile", ["feturbulence"] = "feTurbulence",
        ["foreignobject"] = "foreignObject", ["glyphref"] = "glyphRef", ["lineargradient"] = "linearGradient",
        ["radialgradient"] = "radialGradient", ["textpath"] = "textPath",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> SvgAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["attributename"] = "attributeName", ["attributetype"] = "attributeType",
        ["basefrequency"] = "baseFrequency", ["baseprofile"] = "baseProfile", ["calcmode"] = "calcMode",
        ["clippathunits"] = "clipPathUnits", ["diffuseconstant"] = "diffuseConstant",
        ["edgemode"] = "edgeMode", ["filterunits"] = "filterUnits", ["glyphref"] = "glyphRef",
        ["gradienttransform"] = "gradientTransform", ["gradientunits"] = "gradientUnits",
        ["kernelmatrix"] = "kernelMatrix", ["kernelunitlength"] = "kernelUnitLength",
        ["keypoints"] = "keyPoints", ["keysplines"] = "keySplines", ["keytimes"] = "keyTimes",
        ["lengthadjust"] = "lengthAdjust", ["limitingconeangle"] = "limitingConeAngle",
        ["markerheight"] = "markerHeight", ["markerunits"] = "markerUnits", ["markerwidth"] = "markerWidth",
        ["maskcontentunits"] = "maskContentUnits", ["maskunits"] = "maskUnits",
        ["numoctaves"] = "numOctaves", ["pathlength"] = "pathLength",
        ["patterncontentunits"] = "patternContentUnits", ["patterntransform"] = "patternTransform",
        ["patternunits"] = "patternUnits", ["pointsatx"] = "pointsAtX", ["pointsaty"] = "pointsAtY",
        ["pointsatz"] = "pointsAtZ", ["preservealpha"] = "preserveAlpha",
        ["preserveaspectratio"] = "preserveAspectRatio", ["primitiveunits"] = "primitiveUnits",
        ["refx"] = "refX", ["refy"] = "refY", ["repeatcount"] = "repeatCount", ["repeatdur"] = "repeatDur",
        ["requiredextensions"] = "requiredExtensions", ["requiredfeatures"] = "requiredFeatures",
        ["specularconstant"] = "specularConstant", ["specularexponent"] = "specularExponent",
        ["spreadmethod"] = "spreadMethod", ["startoffset"] = "startOffset", ["stddeviation"] = "stdDeviation",
        ["stitchtiles"] = "stitchTiles", ["surfacescale"] = "surfaceScale",
        ["systemlanguage"] = "systemLanguage", ["tablevalues"] = "tableValues", ["targetx"] = "targetX",
        ["targety"] = "targetY", ["textlength"] = "textLength", ["viewbox"] = "viewBox",
        ["viewtarget"] = "viewTarget", ["xchannelselector"] = "xChannelSelector",
        ["ychannelselector"] = "yChannelSelector", ["zoomandpan"] = "zoomAndPan",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// "Adjust foreign attributes": the attributes that get a namespace in SVG and MathML, as
    /// (prefix, local name, namespace).
    /// </summary>
    private static readonly FrozenDictionary<string, QualName> ForeignAttributeNames = new Dictionary<string, QualName>(StringComparer.Ordinal)
    {
        ["xlink:actuate"] = new("xlink", Namespaces.XLink, "actuate"),
        ["xlink:arcrole"] = new("xlink", Namespaces.XLink, "arcrole"),
        ["xlink:href"] = new("xlink", Namespaces.XLink, "href"),
        ["xlink:role"] = new("xlink", Namespaces.XLink, "role"),
        ["xlink:show"] = new("xlink", Namespaces.XLink, "show"),
        ["xlink:title"] = new("xlink", Namespaces.XLink, "title"),
        ["xlink:type"] = new("xlink", Namespaces.XLink, "type"),
        ["xml:lang"] = new("xml", Namespaces.Xml, "lang"),
        ["xml:space"] = new("xml", Namespaces.Xml, "space"),
        ["xmlns"] = new(null, Namespaces.XmlNs, "xmlns"),
        ["xmlns:xlink"] = new("xmlns", Namespaces.XmlNs, "xlink"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Attribute names common enough to intern once for every parse.</summary>
    private static readonly FrozenSet<string> CommonAttributeNames = new[]
    {
        "id", "class", "style", "href", "src", "type", "name", "value", "alt", "title", "rel",
        "content", "width", "height", "action", "method", "for", "role", "lang", "charset",
        "target", "placeholder", "disabled", "checked", "selected", "tabindex", "hidden",
        "colspan", "rowspan", "align", "valign", "border", "cellpadding", "cellspacing", "d",
        "viewbox", "fill", "stroke", "xmlns", "srcset", "sizes", "loading", "async", "defer",
        "crossorigin", "integrity", "media", "property", "http-equiv", "data-id", "aria-label",
        "aria-hidden", "shadowrootmode", "encoding",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> CommonAttributeNamesBySpan =
        CommonAttributeNames.GetAlternateLookup<ReadOnlySpan<char>>();
}
