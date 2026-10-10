using System.Text;

using AngleSharp.Html;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;
using AngleSharp.Text;

namespace PocketCalculator.Dom;

/// <summary>Why <see cref="DocumentParser"/> handed control back.</summary>
public enum ParserStop
{
    /// <summary>A parser-inserted script's end tag was processed; the script is to be prepared.</summary>
    Script,

    /// <summary>The token budget ran out; the event loop may run before parsing goes on.</summary>
    Yield,

    /// <summary>A nested run (document.write) reached the insertion point.</summary>
    InsertionPoint,

    /// <summary>The end of the input was processed.</summary>
    Finished,

    /// <summary>
    /// An element of a defined custom element was inserted: the caller lets the realm
    /// upgrade it before its children are parsed.
    /// </summary>
    CustomElement,
}

/// <summary>
/// The tree builder of a document whose scripts run while it is parsed: it stops at each
/// script end tag, and document.write inserts into its input at the insertion point.
/// </summary>
/// <remarks>
/// DEVIATION from crates/obscura-browser, which parses the whole document before any
/// script runs, so an inline script saw every element after it, document.write output
/// landed after the script by DOM insertion, and async scripts only ran once parsing and
/// every parser script were done. This follows the HTML parser's script handling: the
/// "text" insertion mode's script end tag pauses the parser, and the host prepares the
/// script before parsing resumes.
/// </remarks>
internal sealed partial class HtmlTreeBuilder
{
    /// <summary>The input of a scripted parse, which document.write inserts into.</summary>
    private TextSource? _writable;

    /// <summary>The script whose end tag the last token processed, for the caller to prepare.</summary>
    private NodeId? _pausedScript;

    /// <summary>Whether script end tags pause the parse (a document with a browsing context).</summary>
    private bool _scripting;

    /// <summary>Every node the parser inserted since the caller last took the log.</summary>
    private List<NodeId>? _insertLog;

    private bool _finished;

    /// <summary>Whether a custom element name is defined, for <see cref="ParserStop.CustomElement"/>.</summary>
    internal Func<string, bool>? IsDefinedCustomElement { get; set; }

    private bool _pauseForCustomElement;

    /// <summary>After inserting an HTML element: stop once the token is done if it is a defined custom element.</summary>
    private void CheckCustomElement(string ns, string local, List<Attribute> attrs)
    {
        if (!_scripting || IsDefinedCustomElement is not { } defined || !string.Equals(ns, Namespaces.Html, StringComparison.Ordinal))
        {
            return;
        }

        if ((local.Contains('-', StringComparison.Ordinal) && defined(local))
            || (GetAttr(attrs, "is") is { Length: > 0 } isValue && defined(isValue)))
        {
            _pauseForCustomElement = true;
        }
    }

    internal bool Finished => _finished || _stopped;

    internal bool Quirks => _quirks;

    /// <summary>A builder for a whole document whose script end tags pause it.</summary>
    internal static HtmlTreeBuilder CreateScripted(DomTree tree, string html)
    {
        var builder = new HtmlTreeBuilder(tree, string.Empty, context: null)
        {
            _scripting = true,
            _insertLog = [],
        };
        builder._tokenizer = builder.NewWritableTokenizer(Normalize(html));
        return builder;
    }

    /// <summary>The input stream's newline normalization, done up front so positions are exact.</summary>
    private static string Normalize(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;

    private HtmlTokenizer NewWritableTokenizer(string html)
    {
        if (html.Contains('\0'))
        {
            _nulEscaped = true;
            html = html.Replace('\0', NulStandIn);
        }

        _writable = new TextSource(html);
        return new(_writable, HtmlEntityProvider.ResolverExtended)
        {
            DisableElementPositionTracking = true,
            ShouldEmitAttribute = ShouldEmitAttribute,
        };
    }

    /// <summary>Where the tokenizer will read next.</summary>
    internal int InputPosition => _tokenizer.Position;

    /// <summary>
    /// Insert <paramref name="text"/> into the input at <paramref name="position"/>, which is
    /// not before the tokenizer's position. Returns the inserted length.
    /// </summary>
    internal int InsertInput(int position, string text)
    {
        var source = _writable!;
        text = Normalize(text);
        if (text.Contains('\0'))
        {
            _nulEscaped = true;
            text = text.Replace('\0', NulStandIn);
        }

        var read = source.Index;
        source.Index = position;
        source.InsertText(text);
        source.Index = read;
        return text.Length;
    }

    internal List<NodeId> TakeInsertLog()
    {
        var log = _insertLog ?? [];
        _insertLog = [];
        return log;
    }

    private void LogInsertion(NodeId node) => _insertLog?.Add(node);

    /// <summary>
    /// The "text" insertion mode's script end tag, for a document whose scripts run: the
    /// script is popped and the parser stops for the caller to prepare it.
    /// </summary>
    private bool PauseAtScriptEnd(Rec script)
    {
        if (!_scripting)
        {
            return false;
        }

        _pausedScript = script.Id;
        return true;
    }

    /// <summary>
    /// Run the tree builder until a script end tag, the end of the input, <paramref name="stopAt"/>
    /// (an input position; -1 for none), or <paramref name="tokenBudget"/> tokens (0 for none).
    /// </summary>
    internal ParserStop RunScripted(int stopAt, int tokenBudget, out NodeId script)
    {
        script = default;
        if (Finished)
        {
            return ParserStop.Finished;
        }

        try
        {
            var count = 0;
            while (!_stopped)
            {
                WorkCancellation.ThrowIfCancellationRequested();
                if (stopAt >= 0 && _tokenizer.Position >= stopAt)
                {
                    return ParserStop.InsertionPoint;
                }

                if (tokenBudget > 0 && ++count > tokenBudget)
                {
                    return ParserStop.Yield;
                }

                var acn = AdjustedCurrentNode;
                _tokenizer.IsAcceptingCharacterData = acn is not null && acn.Ns != ElemNs.Html;
                var state = _tokenizer.State;
                var before = _tokenizer.Position;
                ref var token = ref _tokenizer.GetStructToken();
                var eof = token.Type == HtmlTokenType.EndOfFile;
                if (stopAt >= 0 && (eof || _tokenizer.Position > stopAt))
                {
                    // The token runs past the insertion point, into input that is not there yet
                    // for the parser. Text in the data state is split there; anything else is
                    // read again once the parser gets past the script.
                    _writable!.Index = before;
                    _tokenizer.State = state;
                    if (!eof && token.Type == HtmlTokenType.Character && state == HtmlParseMode.PCData && stopAt > before)
                    {
                        var text = new StringBuilder(stopAt - before);
                        for (var i = before; i < stopAt; i++)
                        {
                            text.Append(_writable[i]);
                        }

                        FeedText(text.ToString());
                        _writable.Index = stopAt;
                    }

                    return ParserStop.InsertionPoint;
                }

                ProcessToken(ref token);
                if (eof)
                {
                    FlushText();
                    PopOpenOptionsAtEnd();
                    _finished = true;
                    return ParserStop.Finished;
                }

                if (_pausedScript is { } paused)
                {
                    _pausedScript = null;
                    _pauseForCustomElement = false;
                    script = paused;
                    return ParserStop.Script;
                }

                if (_pauseForCustomElement)
                {
                    _pauseForCustomElement = false;
                    return ParserStop.CustomElement;
                }
            }

            return ParserStop.Finished;
        }
        catch (DomQuotaExceededException)
        {
            // As Run: past the byte budget the parse keeps what it built (SECURITY.md M7).
            _tree.ParseTruncated = true;
            _stopped = true;
            return ParserStop.Finished;
        }
        finally
        {
            // Script may run now: everything parsed so far is in the tree.
            FlushText();
            WriteBackGrowingText();
            _mergedAttributes = null;
            _tree.SetQuirks(_quirks);
        }
    }

    /// <summary>Stop parsing for good (document.open, a navigation away).</summary>
    internal void Abort() => _stopped = true;

    /// <summary>Every node the parser still refers to and may insert into or move.</summary>
    internal void MarkParserRoots(DomCollection collection)
    {
        foreach (var rec in _stack)
        {
            collection.Keep(rec.Id);
            if (rec.Contents is { } contents)
            {
                collection.Keep(contents);
            }
        }

        for (var entry = _afeHead; entry is not null; entry = entry.Next)
        {
            if (entry.Element is { } element)
            {
                collection.Keep(element.Id);
            }
        }

        if (_head is { } head)
        {
            collection.Keep(head.Id);
        }

        if (_form is { } form)
        {
            collection.Keep(form.Id);
        }
    }
}
