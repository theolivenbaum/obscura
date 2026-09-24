using System.Runtime.InteropServices;
using System.Text;

using AngleSharp.Html;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.Text;

namespace PocketCalculator.Dom;

/// <summary>
/// The HTML tree construction stage, building straight into a <see cref="DomTree"/>.
/// </summary>
/// <remarks>
/// <para>
/// DEVIATION: the Rust engine builds through html5ever's tree builder, and the port first used
/// AngleSharp's. AngleSharp's tree builder answers "has an element in scope" by scanning the
/// whole stack of open elements, so every block start tag cost as much as the stack was deep:
/// 50,000 nested <c>&lt;div&gt;</c>s took 20 s. Its fragment parser then moved every
/// top-level node into the context element one at a time, each move scanning the node list,
/// so <c>innerHTML</c> with 50,000 siblings took 100 s (SECURITY.md M11). Neither can be
/// changed from outside, so this is the WHATWG tree construction algorithm written over
/// AngleSharp's public tokenizer.
/// </para>
/// <para>
/// The algorithm is the specification's, including the 2025 <c>select</c> parser changes
/// Chromium ships, which remove the "in select" insertion modes. What differs is only how the
/// stack of open elements answers questions: each entry is linked to the one below it with the
/// same name, the one below it that bounds a scope, and the one below it that is special, so
/// "has a p element in button scope" compares two indices instead of walking the stack.
/// Scripts never run during a parse here, so there is no script nesting, no document.write
/// re-entry and no parser pause.
/// </para>
/// <para>
/// Parse errors are not reported, so the checks the specification makes only to report one
/// are left out.
/// </para>
/// </remarks>
internal sealed partial class HtmlTreeBuilder
{
    /// <summary>Chromium's <c>kMaximumHTMLParserDOMTreeDepth</c>; see <see cref="HtmlParsing.MaxParserTreeDepth"/>.</summary>
    private const int MaxTreeDepth = HtmlParsing.MaxParserTreeDepth;

    private enum Mode : byte
    {
        Initial,
        BeforeHtml,
        BeforeHead,
        InHead,
        AfterHead,
        InBody,
        Text,
        InTable,
        InTableText,
        InCaption,
        InColumnGroup,
        InTableBody,
        InRow,
        InCell,
        InTemplate,
        AfterBody,
        InFrameset,
        AfterFrameset,
        AfterAfterBody,
        AfterAfterFrameset,
    }

    private enum ElemNs : byte
    {
        Html,
        MathMl,
        Svg,
    }

    [Flags]
    private enum RecFlags : byte
    {
        None = 0,
        Special = 1,
        ScopeBoundary = 2,
        HtmlIntegrationPoint = 4,
        MathTextIntegrationPoint = 8,
        AnnotationXml = 16,

        /// <summary>Special, but not <c>address</c>, <c>div</c> or <c>p</c> (the <c>li</c>/<c>dd</c> walks).</summary>
        SpecialNotAdp = 32,
    }

    private enum Scope : byte
    {
        Default,
        ListItem,
        Button,
        Table,
    }

    private enum TokKind : byte
    {
        Doctype,
        StartTag,
        EndTag,
        Comment,
        Character,
        Eof,
    }

    /// <summary>One token, as the tree builder sees it.</summary>
    private sealed class Tok
    {
        public TokKind Kind;
        public string Name = "";
        public HtmlTag Tag;
        public List<Attribute> Attrs = [];
        public bool SelfClosing;

        /// <summary>Character data; valid only while this token is being processed.</summary>
        public ReadOnlyMemory<char> Chars;

        /// <summary>Comment data.</summary>
        public string Text = "";
    }

    /// <summary>An entry of the stack of open elements, and the element it stands for.</summary>
    private sealed class Rec
    {
        public NodeId Id;
        public string Local = "";
        public ElemNs Ns;

        /// <summary>The element's tag if it is an HTML element the rules name, else Unknown.</summary>
        public HtmlTag Tag;
        public RecFlags Flags;

        /// <summary>Template contents, for a template element.</summary>
        public NodeId? Contents;

        /// <summary>The attributes the start tag had, for an element on the list of active formatting elements.</summary>
        public List<Attribute>? TokenAttrs;

        /// <summary>Position in the stack of open elements, or -1.</summary>
        public int Index = -1;

        /// <summary>This element's entry in the list of active formatting elements, if any.</summary>
        public FmtEntry? Fmt;

        /// <summary>The key of the by-name chain for a name without an <see cref="HtmlTag"/>.</summary>
        public string? NameKey;

        public Rec? PrevName;
        public Rec? PrevScope;
        public Rec? PrevSpecial;
        public Rec? PrevSpecialNotAdp;
        public Rec? PrevHtml;

        public bool IsHtml(HtmlTag tag) => Tag == tag && Ns == ElemNs.Html;

        public bool Is(RecFlags flag) => (Flags & flag) != 0;
    }

    private readonly record struct Location(NodeId Parent, NodeId? Before);

    // ------------------------------------------------------------------ state

    private readonly DomTree _tree;
    private readonly HtmlTokenizer _tokenizer;
    private readonly Tok _tok = new();

    private readonly List<Rec> _stack = [];
    private readonly List<Mode> _templateModes = [];

    // Heads of the chains through the stack of open elements.
    private readonly Rec?[] _topTag = new Rec?[(int)HtmlTag.Count];
    private readonly Dictionary<string, Rec> _topOtherHtml = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Rec> _topForeign = new(StringComparer.Ordinal);
    private Rec? _topScope;
    private Rec? _topSpecial;
    private Rec? _topSpecialNotAdp;
    private Rec? _topHtml;

    private Mode _mode = Mode.Initial;
    private Mode _originalMode = Mode.Initial;
    private Rec? _head;
    private Rec? _form;
    private bool _framesetOk = true;
    private bool _fosterParenting;
    private bool _skipNextNewline;
    private bool _quirks;
    private bool _stopped;

    /// <summary>The fragment parsing context element; not on the stack.</summary>
    private readonly Rec? _context;

    /// <summary>Receives the open elements at the end of the input, if set.</summary>
    private HashSet<NodeId>? _openAtEnd;

    private readonly StringBuilder _pendingTableText = new();
    private bool _pendingTableTextHasNonSpace;

    // Text is buffered until the next tree mutation, so a text node built from many character
    // tokens is concatenated once rather than once per token.
    private readonly StringBuilder _pendingText = new();
    private NodeId _pendingTextParent;
    private NodeId? _pendingTextBefore;

    // A text node that keeps receiving text while other nodes go elsewhere (past the depth cap,
    // or fostered before a table) grows through a builder, written back when the parse ends,
    // instead of being copied on every append.
    private readonly Dictionary<NodeId, StringBuilder> _growingText = [];

    // The attributes of html and body, for the merges a later <html> or <body> start tag makes.
    private Dictionary<Rec, HashSet<QualName>>? _mergedAttributes;

    // The attribute names of the start tag being tokenized, for dropping duplicates in constant time.
    private readonly HashSet<string> _tagAttributeNames = new(StringComparer.Ordinal);
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _tagAttributeNamesBySpan;

    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _namesBySpan;

    private HtmlTreeBuilder(DomTree tree, string html, Rec? context)
    {
        _tree = tree;
        _context = context;
        _namesBySpan = _names.GetAlternateLookup<ReadOnlySpan<char>>();
        _tagAttributeNamesBySpan = _tagAttributeNames.GetAlternateLookup<ReadOnlySpan<char>>();
        _tokenizer = new HtmlTokenizer(new TextSource(new StringTextSource(html)), HtmlEntityProvider.ResolverExtended)
        {
            DisableElementPositionTracking = true,
            ShouldEmitAttribute = ShouldEmitAttribute,
        };
    }

    /// <summary>
    /// How many attributes one tag keeps.
    /// </summary>
    /// <remarks>
    /// DEVIATION from html5ever (crates/obscura-dom) and Chromium, which keep them all.
    /// AngleSharp's tokenizer drops duplicate attributes by comparing every pair when it emits a
    /// tag, so a tag with 20,000 distinct attributes took 3 s inside one token, where no deadline
    /// can stop it (SECURITY.md M11). Duplicates are dropped here instead, before the tokenizer
    /// stores them, and a tag keeps its first <see cref="MaxAttributesPerTag"/> distinct
    /// attributes, which bounds that comparison.
    /// </remarks>
    internal const int MaxAttributesPerTag = 512;

    /// <summary>Below this many attributes a duplicate is found by comparing names directly.</summary>
    private const int AttributeScanLimit = 16;

    private bool ShouldEmitAttribute(ref StructHtmlToken token, ReadOnlyMemory<char> name)
    {
        var attributes = token.Attributes;
        var count = attributes.Count;
        if (count >= MaxAttributesPerTag)
        {
            return false;
        }

        // The first occurrence wins; a later duplicate is dropped, as the tokenizer would.
        var span = name.Span;
        if (count < AttributeScanLimit)
        {
            for (var i = 0; i < count; i++)
            {
                if (span.SequenceEqual(attributes[i].Name.Memory.Span))
                {
                    return false;
                }
            }

            return true;
        }

        if (count == AttributeScanLimit || _tagAttributeNames.Count != count)
        {
            _tagAttributeNames.Clear();
            for (var i = 0; i < count; i++)
            {
                _tagAttributeNamesBySpan.Add(attributes[i].Name.Memory.Span);
            }
        }

        return _tagAttributeNamesBySpan.Add(span);
    }

    // ------------------------------------------------------------------ entry points

    /// <summary>Parse a whole document into <paramref name="tree"/>, which is empty.</summary>
    internal static void ParseDocument(DomTree tree, string html)
    {
        var builder = new HtmlTreeBuilder(tree, html, context: null);
        builder.Run();
        tree.SetQuirks(builder._quirks);
    }

    /// <summary>
    /// The HTML fragment parsing algorithm: parse <paramref name="html"/> as the children of an
    /// element named <paramref name="contextName"/>, below <paramref name="root"/>, which stands
    /// for the algorithm's <c>html</c> root element.
    /// </summary>
    /// <remarks>
    /// With <paramref name="openAtEnd"/>, the elements still open when the input ran out, before
    /// the end of file closed them, are added to it: the ones more input could still add to.
    /// </remarks>
    internal static void ParseFragment(
        DomTree tree,
        NodeId root,
        string html,
        QualName contextName,
        HashSet<NodeId>? openAtEnd = null)
    {
        var context = new Rec();
        SetName(context, contextName.Ns, contextName.Local);
        context.Flags = Classify(context.Ns, context.Tag, context.Local, annotationXmlIntegrationPoint: false);

        var builder = new HtmlTreeBuilder(tree, html, context) { _openAtEnd = openAtEnd };
        builder.StartFragment(root);
        builder.Run();
    }

    private void StartFragment(NodeId root)
    {
        var context = _context!;
        var rootRec = new Rec { Id = root, Local = "html", Ns = ElemNs.Html, Tag = HtmlTag.Html };
        rootRec.Flags = Classify(ElemNs.Html, HtmlTag.Html, "html", false);
        Push(rootRec);

        if (context.IsHtml(HtmlTag.Template))
        {
            _templateModes.Add(Mode.InTemplate);
        }

        if (context.Ns == ElemNs.Html)
        {
            _tokenizer.State = context.Tag switch
            {
                HtmlTag.Title or HtmlTag.Textarea => HtmlParseMode.RCData,
                HtmlTag.Style or HtmlTag.Xmp or HtmlTag.Iframe or HtmlTag.Noembed or HtmlTag.Noframes
                    or HtmlTag.Noscript => HtmlParseMode.Rawtext,
                HtmlTag.Script => HtmlParseMode.Script,
                HtmlTag.Plaintext => HtmlParseMode.Plaintext,
                _ => HtmlParseMode.PCData,
            };
        }

        ResetInsertionMode();

        // The form element pointer is the nearest form at or above the context element; the
        // context has no ancestors here.
        if (context.IsHtml(HtmlTag.Form))
        {
            _form = context;
        }
    }

    private void Run()
    {
        try
        {
            while (!_stopped)
            {
                WorkCancellation.ThrowIfCancellationRequested();
                var acn = AdjustedCurrentNode;
                _tokenizer.IsAcceptingCharacterData = acn is not null && acn.Ns != ElemNs.Html;
                ref var token = ref _tokenizer.GetStructToken();
                if (!ReadToken(ref token))
                {
                    continue;
                }

                if (_skipNextNewline)
                {
                    _skipNextNewline = false;
                    if (_tok.Kind == TokKind.Character && _tok.Chars.Span is ['\n', ..])
                    {
                        _tok.Chars = _tok.Chars[1..];
                        if (_tok.Chars.IsEmpty)
                        {
                            continue;
                        }
                    }
                }

                if (_tok.Kind == TokKind.Eof && _openAtEnd is not null)
                {
                    foreach (var open in _stack)
                    {
                        _openAtEnd.Add(open.Id);
                    }
                }

                Process(_tok);
                if (_tok.Kind == TokKind.Eof)
                {
                    break;
                }
            }

            FlushText();
        }
        catch (DomQuotaExceededException)
        {
            // DEVIATION (SECURITY.md M7): past the tree's byte budget the parse keeps what it
            // built and drops the rest, as a truncated response would; Rust has no budget, and
            // Chromium would run out of memory instead.
            _tree.ParseTruncated = true;
        }
        finally
        {
            foreach (var (id, builder) in _growingText)
            {
                if (_tree.GetNode(id)?.Data is TextData text)
                {
                    text.Contents = builder.ToString();
                }
            }

            _growingText.Clear();
        }
    }

    /// <summary>Copy the tokenizer's token into <see cref="_tok"/>; false for one to skip.</summary>
    private bool ReadToken(ref StructHtmlToken token)
    {
        var tok = _tok;
        switch (token.Type)
        {
            case HtmlTokenType.Character:
                tok.Kind = TokKind.Character;
                tok.Chars = token.Data.Memory;
                return !tok.Chars.IsEmpty;

            case HtmlTokenType.StartTag:
            case HtmlTokenType.EndTag:
            {
                tok.Kind = token.Type == HtmlTokenType.StartTag ? TokKind.StartTag : TokKind.EndTag;
                var name = token.Name.Memory.Span;
                if (TagsBySpan.TryGetValue(name, out var tag))
                {
                    tok.Tag = tag;
                    tok.Name = TagNames[(int)tag];
                }
                else
                {
                    tok.Tag = HtmlTag.Unknown;
                    tok.Name = Intern(name);
                }

                tok.SelfClosing = token.IsSelfClosing;
                if (tok.Kind == TokKind.StartTag)
                {
                    var attributes = token.Attributes;
                    var list = new List<Attribute>(attributes.Count);
                    for (var i = 0; i < attributes.Count; i++)
                    {
                        var attribute = attributes[i];
                        var attrName = attribute.Name.Memory.Span;
                        var local = CommonAttributeNamesBySpan.TryGetValue(attrName, out var common)
                            ? common
                            : Intern(attrName);
                        list.Add(new Attribute(QualName.Attr(local), attribute.Value.ToString()));
                    }

                    tok.Attrs = list;
                }
                else
                {
                    tok.Attrs = [];
                }

                return true;
            }

            case HtmlTokenType.Comment:
                tok.Kind = TokKind.Comment;
                tok.Text = token.Data.ToString();
                return true;

            case HtmlTokenType.Doctype:
                tok.Kind = TokKind.Doctype;
                ProcessDoctypeToken(ref token);
                return false;

            case HtmlTokenType.EndOfFile:
                tok.Kind = TokKind.Eof;
                return true;

            default:
                return false;
        }
    }

    private string Intern(ReadOnlySpan<char> name)
    {
        if (_namesBySpan.TryGetValue(name, out var known))
        {
            return known;
        }

        var value = new string(name);
        _names[value] = value;
        return value;
    }

    /// <summary>
    /// A DOCTYPE token matters only in the initial insertion mode (outside foreign content,
    /// where it is ignored everywhere), so it is handled here while the struct token's
    /// identifiers are still readable.
    /// </summary>
    private void ProcessDoctypeToken(ref StructHtmlToken token)
    {
        if (_skipNextNewline)
        {
            _skipNextNewline = false;
        }

        var acn = AdjustedCurrentNode;
        if (_mode != Mode.Initial || (acn is not null && acn.Ns != ElemNs.Html))
        {
            return;
        }

        FlushText();
        var doctype = _tree.NewNode(NodeData.Doctype(
            token.Name.ToString(),
            token.IsPublicIdentifierMissing ? "" : token.PublicIdentifier.ToString(),
            token.IsSystemIdentifierMissing ? "" : token.SystemIdentifier.ToString()));
        _tree.AppendChild(_tree.Document, doctype);

        // Only full quirks mode changes parsing (a table no longer closes a p) or selector
        // matching, so limited quirks is not tracked.
        _quirks = token.IsFullQuirks;
        _mode = Mode.BeforeHtml;
    }

    // ------------------------------------------------------------------ dispatch

    private Rec? CurrentNode => _stack.Count > 0 ? _stack[^1] : null;

    private Rec? AdjustedCurrentNode =>
        _context is not null && _stack.Count == 1 ? _context : CurrentNode;

    /// <summary>The tree construction dispatcher.</summary>
    private void Process(Tok t)
    {
        var acn = AdjustedCurrentNode;
        if (acn is null
            || acn.Ns == ElemNs.Html
            || t.Kind == TokKind.Eof
            || (acn.Is(RecFlags.MathTextIntegrationPoint)
                && ((t.Kind == TokKind.StartTag && t.Name is not ("mglyph" or "malignmark"))
                    || t.Kind == TokKind.Character))
            || (acn.Is(RecFlags.AnnotationXml) && t.Kind == TokKind.StartTag && t.Tag == HtmlTag.Svg)
            || (acn.Is(RecFlags.HtmlIntegrationPoint)
                && (t.Kind == TokKind.StartTag || t.Kind == TokKind.Character)))
        {
            ProcessInMode(_mode, t);
        }
        else
        {
            ProcessForeign(t);
        }
    }

    // ------------------------------------------------------------------ the stack of open elements

    private static void SetName(Rec rec, string ns, string local)
    {
        rec.Local = local;
        if (string.Equals(ns, Namespaces.Svg, StringComparison.Ordinal))
        {
            rec.Ns = ElemNs.Svg;
            rec.Tag = HtmlTag.Unknown;
        }
        else if (string.Equals(ns, Namespaces.MathMl, StringComparison.Ordinal))
        {
            rec.Ns = ElemNs.MathMl;
            rec.Tag = HtmlTag.Unknown;
        }
        else
        {
            rec.Ns = ElemNs.Html;
            rec.Tag = TagsByName.TryGetValue(local, out var tag) ? tag : HtmlTag.Unknown;
        }
    }

    private static RecFlags Classify(ElemNs ns, HtmlTag tag, string local, bool annotationXmlIntegrationPoint)
    {
        var flags = RecFlags.None;
        switch (ns)
        {
            case ElemNs.Html:
                if (SpecialTags[(int)tag])
                {
                    flags |= RecFlags.Special;
                    if (tag is not (HtmlTag.Address or HtmlTag.Div or HtmlTag.P))
                    {
                        flags |= RecFlags.SpecialNotAdp;
                    }
                }

                if (ScopeTags[(int)tag])
                {
                    flags |= RecFlags.ScopeBoundary;
                }

                break;

            case ElemNs.MathMl:
                switch (local)
                {
                    case "mi" or "mo" or "mn" or "ms" or "mtext":
                        flags |= RecFlags.Special | RecFlags.SpecialNotAdp | RecFlags.ScopeBoundary
                            | RecFlags.MathTextIntegrationPoint;
                        break;
                    case "annotation-xml":
                        flags |= RecFlags.Special | RecFlags.SpecialNotAdp | RecFlags.ScopeBoundary
                            | RecFlags.AnnotationXml;
                        if (annotationXmlIntegrationPoint)
                        {
                            flags |= RecFlags.HtmlIntegrationPoint;
                        }

                        break;
                }

                break;

            case ElemNs.Svg:
                if (local is "foreignObject" or "desc" or "title")
                {
                    flags |= RecFlags.Special | RecFlags.SpecialNotAdp | RecFlags.ScopeBoundary
                        | RecFlags.HtmlIntegrationPoint;
                }

                break;
        }

        return flags;
    }

    private void Push(Rec r)
    {
        r.Index = _stack.Count;
        _stack.Add(r);

        if (r.Ns == ElemNs.Html)
        {
            r.PrevHtml = _topHtml;
            _topHtml = r;
            if (r.Tag != HtmlTag.Unknown)
            {
                r.PrevName = _topTag[(int)r.Tag];
                _topTag[(int)r.Tag] = r;
            }
            else
            {
                r.NameKey = r.Local;
                ref var head = ref CollectionsMarshal.GetValueRefOrAddDefault(_topOtherHtml, r.Local, out _);
                r.PrevName = head;
                head = r;
            }
        }
        else
        {
            r.NameKey ??= r.Local.ToLowerInvariant();
            ref var head = ref CollectionsMarshal.GetValueRefOrAddDefault(_topForeign, r.NameKey, out _);
            r.PrevName = head;
            head = r;
        }

        if (r.Is(RecFlags.ScopeBoundary))
        {
            r.PrevScope = _topScope;
            _topScope = r;
        }

        if (r.Is(RecFlags.Special))
        {
            r.PrevSpecial = _topSpecial;
            _topSpecial = r;
        }

        if (r.Is(RecFlags.SpecialNotAdp))
        {
            r.PrevSpecialNotAdp = _topSpecialNotAdp;
            _topSpecialNotAdp = r;
        }
    }

    private Rec Pop()
    {
        var r = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);

        if (r.Ns == ElemNs.Html)
        {
            _topHtml = r.PrevHtml;
            if (r.Tag != HtmlTag.Unknown)
            {
                _topTag[(int)r.Tag] = r.PrevName;
            }
            else if (r.PrevName is { } prev)
            {
                _topOtherHtml[r.NameKey!] = prev;
            }
            else
            {
                _topOtherHtml.Remove(r.NameKey!);
            }
        }
        else if (r.PrevName is { } prev)
        {
            _topForeign[r.NameKey!] = prev;
        }
        else
        {
            _topForeign.Remove(r.NameKey!);
        }

        if (r.Is(RecFlags.ScopeBoundary))
        {
            _topScope = r.PrevScope;
        }

        if (r.Is(RecFlags.Special))
        {
            _topSpecial = r.PrevSpecial;
        }

        if (r.Is(RecFlags.SpecialNotAdp))
        {
            _topSpecialNotAdp = r.PrevSpecialNotAdp;
        }

        r.Index = -1;
        r.PrevName = r.PrevScope = r.PrevSpecial = r.PrevSpecialNotAdp = r.PrevHtml = null;
        return r;
    }

    /// <summary>
    /// Replace the entries from <paramref name="index"/> up with <paramref name="entries"/>,
    /// by popping and pushing, which keeps the chains exact. Costs what lies above the index.
    /// </summary>
    private void ReplaceStackFrom(int index, List<Rec> entries)
    {
        while (_stack.Count > index)
        {
            Pop();
        }

        foreach (var entry in entries)
        {
            Push(entry);
        }
    }

    private void RemoveFromStack(Rec r)
    {
        if (r.Index < 0)
        {
            return;
        }

        if (r.Index == _stack.Count - 1)
        {
            Pop();
            return;
        }

        var index = r.Index;
        var above = _stack.GetRange(index + 1, _stack.Count - index - 1);
        ReplaceStackFrom(index, above);
    }

    private void PopUntilHtml(HtmlTag tag)
    {
        while (_stack.Count > 0)
        {
            if (Pop().IsHtml(tag))
            {
                return;
            }
        }
    }

    private void PopUntil(Rec target)
    {
        while (_stack.Count > 0)
        {
            if (Pop() == target)
            {
                return;
            }
        }
    }

    private void PopUntilHeading()
    {
        while (_stack.Count > 0)
        {
            var r = Pop();
            if (r.Ns == ElemNs.Html && IsHeading(r.Tag))
            {
                return;
            }
        }
    }

    private bool CurrentIs(HtmlTag tag) => CurrentNode is { } c && c.IsHtml(tag);

    private Rec? TopOf(HtmlTag tag) => _topTag[(int)tag];

    private int TopIndex(HtmlTag tag) => _topTag[(int)tag]?.Index ?? -1;

    private bool OnStack(HtmlTag tag) => _topTag[(int)tag] is not null;

    private int ScopeLimit(Scope scope)
    {
        var limit = _topScope?.Index ?? -1;
        switch (scope)
        {
            case Scope.ListItem:
                limit = Math.Max(limit, Math.Max(TopIndex(HtmlTag.Ol), TopIndex(HtmlTag.Ul)));
                break;
            case Scope.Button:
                limit = Math.Max(limit, TopIndex(HtmlTag.Button));
                break;
            case Scope.Table:
                limit = Math.Max(TopIndex(HtmlTag.Html), Math.Max(TopIndex(HtmlTag.Table), TopIndex(HtmlTag.Template)));
                break;
        }

        return limit;
    }

    /// <summary>
    /// "Has an element in scope": the topmost element of that name is at or above the topmost
    /// element that bounds the scope. The boundary itself counts, since the specification's
    /// walk checks the target before the boundary list.
    /// </summary>
    private bool InScope(HtmlTag tag, Scope scope = Scope.Default)
    {
        var r = _topTag[(int)tag];
        return r is not null && r.Index >= ScopeLimit(scope);
    }

    private bool InScope(Rec r) => r.Index >= 0 && r.Index >= ScopeLimit(Scope.Default);

    private bool HeadingInScope()
    {
        var top = -1;
        for (var tag = HtmlTag.H1; tag <= HtmlTag.H6; tag++)
        {
            top = Math.Max(top, TopIndex(tag));
        }

        return top >= 0 && top >= ScopeLimit(Scope.Default);
    }

    private bool AnyInTableScope(HtmlTag a, HtmlTag b, HtmlTag c = HtmlTag.Unknown)
    {
        var top = Math.Max(TopIndex(a), TopIndex(b));
        if (c != HtmlTag.Unknown)
        {
            top = Math.Max(top, TopIndex(c));
        }

        return top >= 0 && top >= ScopeLimit(Scope.Table);
    }

    // ------------------------------------------------------------------ implied end tags

    private void GenerateImpliedEndTags(HtmlTag except = HtmlTag.Unknown)
    {
        while (CurrentNode is { Ns: ElemNs.Html } c && ImpliedEndTags[(int)c.Tag] && c.Tag != except)
        {
            Pop();
        }
    }

    private void GenerateImpliedEndTagsThoroughly()
    {
        while (CurrentNode is { Ns: ElemNs.Html } c && ThoroughImpliedEndTags[(int)c.Tag])
        {
            Pop();
        }
    }

    private void ClosePElement()
    {
        GenerateImpliedEndTags(HtmlTag.P);
        PopUntilHtml(HtmlTag.P);
    }

    private void ClosePIfInButtonScope()
    {
        if (InScope(HtmlTag.P, Scope.Button))
        {
            ClosePElement();
        }
    }

    // ------------------------------------------------------------------ insertion

    /// <summary>The appropriate place for inserting a node.</summary>
    private Location AppropriatePlace(Rec? overrideTarget = null)
    {
        var target = overrideTarget ?? CurrentNode!;
        if (_fosterParenting && target.Ns == ElemNs.Html
            && target.Tag is HtmlTag.Table or HtmlTag.Tbody or HtmlTag.Tfoot or HtmlTag.Thead or HtmlTag.Tr)
        {
            var lastTemplate = TopOf(HtmlTag.Template);
            var lastTable = TopOf(HtmlTag.Table);
            if (lastTemplate is not null && (lastTable is null || lastTemplate.Index > lastTable.Index))
            {
                return new Location(Inside(lastTemplate), null);
            }

            if (lastTable is null)
            {
                return new Location(Inside(_stack[0]), null);
            }

            if (_tree.GetNode(lastTable.Id)?.Parent is { } parent)
            {
                return new Location(parent, lastTable.Id);
            }

            return new Location(Inside(_stack[lastTable.Index - 1]), null);
        }

        return new Location(Inside(target), null);
    }

    private static NodeId Inside(Rec r) => r.Contents ?? r.Id;

    /// <summary>
    /// Chromium's <c>HTMLConstructionSite::AttachLater</c>: past a stack depth of
    /// <see cref="MaxTreeDepth"/> an element or comment is attached to the parent of where it
    /// would have gone, so markup never nests deeper than that however it is written. The stack
    /// keeps growing, so end tags still match what the markup opened. A parent with no parent of
    /// its own (template contents) keeps the node, as in Chromium.
    /// </summary>
    private Location ApplyDepthCap(Location location)
    {
        if (_stack.Count > MaxTreeDepth && location.Before is null
            && _tree.GetNode(location.Parent)?.Parent is { } grandparent)
        {
            return new Location(grandparent, null);
        }

        return location;
    }

    private void InsertAt(Location location, NodeId node)
    {
        FlushText();
        if (location.Before is { } before)
        {
            _tree.InsertBefore(before, node);
        }
        else
        {
            _tree.AppendChild(location.Parent, node);
        }
    }

    private Rec CreateElement(string ns, string local, List<Attribute> attrs, out bool isTemplate)
    {
        var rec = new Rec();
        SetName(rec, ns, local);

        var annotationXmlIntegrationPoint = rec.Ns == ElemNs.MathMl
            && local == "annotation-xml"
            && IsHtmlEncoding(attrs);
        rec.Flags = Classify(rec.Ns, rec.Tag, local, annotationXmlIntegrationPoint);

        isTemplate = rec.IsHtml(HtmlTag.Template);
        rec.Id = _tree.NewNode(NodeData.Element(
            new QualName(null, ns, local),
            attrs,
            templateContents: null,
            mathmlAnnotationXmlIntegrationPoint: annotationXmlIntegrationPoint));

        if (isTemplate)
        {
            var contents = _tree.NewNode(NodeData.Document);
            if (_tree.GetNode(rec.Id)?.Data is ElementData data)
            {
                data.TemplateContents = contents;
            }

            rec.Contents = contents;
        }

        return rec;
    }

    private static bool IsHtmlEncoding(List<Attribute> attrs)
    {
        foreach (var attr in attrs)
        {
            if (string.Equals(attr.Name.Local, "encoding", StringComparison.Ordinal)
                && attr.Name.Ns.Length == 0)
            {
                return attr.Value.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                    || attr.Value.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    /// <summary>Insert an element at the appropriate place and push it.</summary>
    private Rec InsertElement(string ns, string local, List<Attribute> attrs, Location? at = null)
    {
        var rec = CreateElement(ns, local, attrs, out var isTemplate);
        var location = ApplyDepthCap(at ?? AppropriatePlace());

        var consumed = isTemplate
            && HtmlParsing.AllowDeclarativeShadowRoots(_tree, location.Parent)
            && ConsumeDeclarativeShadow(location, rec, attrs);

        if (!consumed)
        {
            InsertAt(location, rec.Id);
        }

        Push(rec);
        return rec;
    }

    /// <summary>
    /// A declarative shadow root: the template becomes the intended parent's shadow root and is
    /// never inserted. Its contents are the root, so its children land in the shadow tree.
    /// </summary>
    private bool ConsumeDeclarativeShadow(Location location, Rec template, List<Attribute> attrs)
    {
        FlushText();
        return HtmlParsing.AttachDeclarativeShadow(_tree, location.Parent, template.Id, attrs);
    }

    private Rec InsertHtmlElement(Tok t) => InsertElement(Namespaces.Html, t.Name, t.Attrs);

    private Rec InsertHtmlElement(string local, List<Attribute> attrs) =>
        InsertElement(Namespaces.Html, local, attrs);

    private Rec InsertHtmlElement(HtmlTag tag) => InsertElement(Namespaces.Html, TagNames[(int)tag], []);

    private void InsertVoidHtmlElement(Tok t)
    {
        InsertHtmlElement(t);
        Pop();
    }

    private void InsertComment(string data, Location? at = null)
    {
        var location = at ?? ApplyDepthCap(AppropriatePlace());
        var comment = _tree.NewNode(NodeData.Comment(data));
        InsertAt(location, comment);
    }

    private void InsertText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        // Text is never inserted into the Document itself.
        var location = AppropriatePlace();
        if (location.Parent == _tree.Document)
        {
            return;
        }

        if (_pendingText.Length > 0
            && (location.Parent != _pendingTextParent || location.Before != _pendingTextBefore))
        {
            FlushText();
        }

        _pendingTextParent = location.Parent;
        _pendingTextBefore = location.Before;
        _pendingText.Append(text);
    }

    private void FlushText()
    {
        if (_pendingText.Length == 0)
        {
            return;
        }

        // "Insert a character": into the Text node just before the insertion location, if there
        // is one, else into a new one.
        var before = _pendingTextBefore;
        var adjacent = before is { } b
            ? _tree.GetNode(b)?.PrevSibling
            : _tree.GetNode(_pendingTextParent)?.LastChild;
        if (adjacent is { } adjacentId && _tree.GetNode(adjacentId)?.Data is TextData existing)
        {
            _tree.ChargeGrowth(2L * _pendingText.Length);
            if (!_growingText.TryGetValue(adjacentId, out var builder))
            {
                builder = new StringBuilder(existing.Contents, existing.Contents.Length + _pendingText.Length);
                _growingText[adjacentId] = builder;
            }

            builder.Append(_pendingText);
            _pendingText.Clear();
            return;
        }

        var node = _tree.NewNode(NodeData.Text(_pendingText.ToString()));
        _pendingText.Clear();
        if (before is { } beforeId)
        {
            _tree.InsertBefore(beforeId, node);
        }
        else
        {
            _tree.AppendChild(_pendingTextParent, node);
        }
    }

    /// <summary>Add each attribute the element does not already have (the html and body merges).</summary>
    private void MergeAttributes(Rec rec, List<Attribute> attrs)
    {
        if (_tree.GetNode(rec.Id)?.Data is not ElementData data)
        {
            return;
        }

        _mergedAttributes ??= [];
        if (!_mergedAttributes.TryGetValue(rec, out var present))
        {
            present = [];
            foreach (var existing in data.Attrs)
            {
                present.Add(existing.Name);
            }

            _mergedAttributes[rec] = present;
        }

        foreach (var attr in attrs)
        {
            if (!present.Add(attr.Name))
            {
                continue;
            }

            _tree.ChargeGrowth(DomTree.SizeOf(attr));
            data.Attrs.Add(attr);
            if (string.Equals(attr.Name.Local, "id", StringComparison.Ordinal) && attr.Name.Ns.Length == 0)
            {
                _tree.UpdateIdIndex(rec.Id, null, attr.Value);
            }
        }
    }

    // ------------------------------------------------------------------ small helpers

    private static bool IsSpace(char c) => c is '\t' or '\n' or '\f' or '\r' or ' ';

    private static int LeadingSpace(ReadOnlySpan<char> s)
    {
        var i = 0;
        while (i < s.Length && IsSpace(s[i]))
        {
            i++;
        }

        return i;
    }

    private static bool AllSpace(ReadOnlySpan<char> s) => LeadingSpace(s) == s.Length;

    private static string? GetAttr(List<Attribute> attrs, string name)
    {
        foreach (var attr in attrs)
        {
            if (string.Equals(attr.Name.Local, name, StringComparison.Ordinal) && attr.Name.Ns.Length == 0)
            {
                return attr.Value;
            }
        }

        return null;
    }
}
