namespace PocketCalculator.Dom;

/// <summary>
/// The HTML parser of a document whose scripts run while it is parsed.
/// </summary>
/// <remarks>
/// <para>
/// The caller drives it: <see cref="Run"/> returns at every parser-inserted script end tag,
/// so the script is prepared, and possibly executed, with exactly what precedes it in the
/// tree. While a parser-inserted script runs (between <see cref="EnterScript"/> and
/// <see cref="ExitScript"/>) the insertion point is defined, and <see cref="Write"/> puts
/// document.write's text into the input there; <see cref="RunToInsertionPoint"/> then parses
/// it, stopping at a script end tag in it or where the inserted text ends.
/// </para>
/// <para>
/// DEVIATION from crates/obscura-browser, which parses the whole document first and runs its
/// scripts afterwards. See <see cref="HtmlTreeBuilder.RunScripted"/>.
/// </para>
/// </remarks>
public sealed class DocumentParser : IDomGcParticipant
{
    private readonly HtmlTreeBuilder _builder;
    private readonly List<int> _savedInsertionPoints = [];
    private int _insertionPoint = -1;
    private bool _detached;

    private DocumentParser(DomTree tree, HtmlTreeBuilder builder)
    {
        Tree = tree;
        _builder = builder;
        tree.AddGcParticipant(this);
    }

    /// <summary>Start parsing <paramref name="html"/> into the empty <paramref name="tree"/>.</summary>
    public static DocumentParser Begin(DomTree tree, string html)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(html);
        tree.SetAllowDeclarativeShadowRoots(true);
        return new DocumentParser(tree, HtmlTreeBuilder.CreateScripted(tree, html));
    }

    public DomTree Tree { get; }

    /// <summary>
    /// Whether a name is a defined custom element (an autonomous one's name, or a customized
    /// built-in's is value). When set, the parser stops with <see cref="ParserStop.CustomElement"/>
    /// after inserting such an element, so it is upgraded before its children are parsed, as the
    /// parser would have constructed it.
    /// </summary>
    public Func<string, bool>? IsDefinedCustomElement
    {
        get => _builder.IsDefinedCustomElement;
        set => _builder.IsDefinedCustomElement = value;
    }

    /// <summary>The end of the input has been processed, or the parse was aborted.</summary>
    public bool IsFinished => _builder.Finished;

    /// <summary>How many parser-inserted scripts are running, one inside the other.</summary>
    public int ScriptNestingLevel => _savedInsertionPoints.Count;

    /// <summary>Whether document.write has somewhere to put its text.</summary>
    public bool HasInsertionPoint => _insertionPoint >= 0 && !IsFinished;

    /// <summary>
    /// Parse until a script end tag (<paramref name="script"/> is the script), the end of the
    /// input, or <paramref name="tokenBudget"/> tokens (0: no budget).
    /// </summary>
    public ParserStop Run(int tokenBudget, out NodeId script)
    {
        var stop = _builder.RunScripted(-1, tokenBudget, out script);
        if (stop == ParserStop.Finished)
        {
            Detach();
        }

        return stop;
    }

    /// <summary>A parser-inserted script starts running: the insertion point is just before the next input character.</summary>
    public void EnterScript()
    {
        _savedInsertionPoints.Add(_insertionPoint);
        _insertionPoint = _builder.InputPosition;
    }

    /// <summary>The script <see cref="EnterScript"/> started has finished.</summary>
    public void ExitScript()
    {
        if (_savedInsertionPoints.Count == 0)
        {
            return;
        }

        _insertionPoint = _savedInsertionPoints[^1];
        _savedInsertionPoints.RemoveAt(_savedInsertionPoints.Count - 1);
    }

    /// <summary>Insert <paramref name="text"/> into the input just before the insertion point.</summary>
    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!HasInsertionPoint || text.Length == 0)
        {
            return;
        }

        var length = _builder.InsertInput(_insertionPoint, text);
        // An insertion point of an enclosing script lies at or after this one, and so after
        // the text just inserted.
        for (var i = 0; i < _savedInsertionPoints.Count; i++)
        {
            if (_savedInsertionPoints[i] >= _insertionPoint)
            {
                _savedInsertionPoints[i] += length;
            }
        }

        _insertionPoint += length;
    }

    /// <summary>
    /// Parse what was written, up to the insertion point, stopping early at a script end tag.
    /// </summary>
    public ParserStop RunToInsertionPoint(out NodeId script)
    {
        script = default;
        if (!HasInsertionPoint)
        {
            return ParserStop.InsertionPoint;
        }

        var stop = _builder.RunScripted(_insertionPoint, 0, out script);
        if (stop == ParserStop.Finished)
        {
            Detach();
        }

        return stop;
    }

    /// <summary>The nodes the parser inserted since the last call, in insertion order.</summary>
    public List<NodeId> TakeInsertedNodes() => _builder.TakeInsertLog();

    /// <summary>Stop parsing: nothing more is added to the tree.</summary>
    public void Abort()
    {
        _builder.Abort();
        Detach();
    }

    private void Detach()
    {
        if (!_detached)
        {
            _detached = true;
            Tree.RemoveGcParticipant(this);
        }
    }

    void IDomGcParticipant.MarkRoots(DomCollection collection)
    {
        if (!_detached)
        {
            _builder.MarkParserRoots(collection);
        }
    }

    void IDomGcParticipant.OnFreed(DomTree tree, IReadOnlyList<NodeId> freed)
    {
    }
}
