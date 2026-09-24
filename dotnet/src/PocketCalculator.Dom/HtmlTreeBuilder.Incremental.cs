using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens.Struct;

namespace PocketCalculator.Dom;

/// <summary>
/// A tree builder fed its input a piece at a time, for <c>document.write()</c>.
/// </summary>
/// <remarks>
/// <para>
/// html5ever's tokenizer (crates/obscura-js/src/write_stream.rs) keeps its state between feeds
/// and waits at the end of each for more input. AngleSharp's cannot be resumed: at the end of
/// its input it finishes whatever it was in. So the tree builder stays alive between feeds and
/// a fresh tokenizer starts, each time, at the last point where the tokenizer's state is known:
/// after a token that left it in the data state, or at the start tag of an element whose
/// content is raw text (script, style, title, textarea and the like), whose text is then
/// tokenized again and replaces what the previous feed made of it.
/// </para>
/// <para>
/// What the end of a feed might still change waits for the next one, as html5ever's does: a tag,
/// comment or doctype cut off by the end, a character reference that might go on (<c>&amp;am</c>),
/// a lone <c>&lt;</c>, and an unfinished CDATA section. Newlines are normalized by the caller,
/// which holds back a trailing CR, so the tokenizer's positions are exact.
/// </para>
/// </remarks>
internal sealed partial class HtmlTreeBuilder
{
    /// <summary>Where the next feed restarts inside a raw text element.</summary>
    private sealed record Replay(Rec Element, HtmlParseMode State, bool SkipNewline);

    private Replay? _replay;

    /// <summary>The ids on the stack of open elements, kept for an incremental builder only.</summary>
    private HashSet<NodeId>? _openIds;

    /// <summary>
    /// A builder for the fragment parsing algorithm with <paramref name="contextName"/> as
    /// context, building below <paramref name="root"/>, fed by <see cref="Feed"/>.
    /// </summary>
    internal static HtmlTreeBuilder CreateIncremental(DomTree tree, NodeId root, QualName contextName)
    {
        var context = new Rec();
        SetName(context, contextName.Ns, contextName.Local);
        context.Flags = Classify(context.Ns, context.Tag, context.Local, annotationXmlIntegrationPoint: false);

        var builder = new HtmlTreeBuilder(tree, string.Empty, context) { _openIds = [] };
        builder.StartFragment(root);
        return builder;
    }

    /// <summary>Whether <paramref name="id"/> is on the stack of open elements: more input can still add to it.</summary>
    internal bool IsOpen(NodeId id) => _openIds?.Contains(id) ?? false;

    /// <summary>
    /// Parse <paramref name="input"/>, which starts where the previous feed stopped, as far as
    /// is certain. Returns how much of it is done; the rest must start the next feed.
    /// </summary>
    internal int Feed(string input)
    {
        if (_stopped)
        {
            return input.Length;
        }

        var safe = 0;
        try
        {
            _tokenizer = NewTokenizer(input);
            if (_replay is { } replay)
            {
                // The element's text is tokenized again, now with more of it.
                ClearText(replay.Element);
                _tokenizer.GetStructToken();
                _tokenizer.State = replay.State;
                _skipNextNewline = replay.SkipNewline;
            }

            while (true)
            {
                WorkCancellation.ThrowIfCancellationRequested();
                var acn = AdjustedCurrentNode;
                _tokenizer.IsAcceptingCharacterData = acn is not null && acn.Ns != ElemNs.Html;
                var inData = _tokenizer.State == HtmlParseMode.PCData;
                var before = _tokenizer.Position;
                ref var token = ref _tokenizer.GetStructToken();
                var after = _tokenizer.Position;
                if (token.Type == HtmlTokenType.EndOfFile)
                {
                    break;
                }

                if (after >= input.Length && inData)
                {
                    var raw = input.AsSpan(before, after - before);
                    if (token.Type == HtmlTokenType.Character)
                    {
                        var done = CertainTextLength(raw);
                        if (done < raw.Length)
                        {
                            if (done > 0)
                            {
                                FeedText(input.Substring(before, done));
                            }

                            safe = before + done;
                            break;
                        }
                    }
                    else if ((token.Type == HtmlTokenType.Comment && !CommentIsClosed(raw))
                             || (token.Type == HtmlTokenType.Doctype && raw[^1] != '>'))
                    {
                        break;
                    }
                }

                ProcessToken(ref token);

                if (_tokenizer.State == HtmlParseMode.PCData)
                {
                    safe = after;
                    _replay = null;
                }
                else if (_tok.Kind == TokKind.StartTag)
                {
                    safe = before;
                    _replay = new Replay(CurrentNode!, _tokenizer.State, _skipNextNewline);
                }
            }

            FlushText();
        }
        catch (DomQuotaExceededException)
        {
            _tree.ParseTruncated = true;
            _stopped = true;
            safe = input.Length;
        }
        finally
        {
            WriteBackGrowingText();
        }

        return safe;
    }

    /// <summary>Hand one token to the tree builder, as <see cref="Run"/> does.</summary>
    private void ProcessToken(ref StructHtmlToken token)
    {
        if (!ReadToken(ref token))
        {
            return;
        }

        if (_skipNextNewline)
        {
            _skipNextNewline = false;
            if (_tok.Kind == TokKind.Character && _tok.Chars.Span is ['\n', ..])
            {
                _tok.Chars = _tok.Chars[1..];
                if (_tok.Chars.IsEmpty)
                {
                    return;
                }
            }
        }

        Process(_tok);
    }

    /// <summary>Tokenize and process text that is known to be complete.</summary>
    private void FeedText(string text)
    {
        var outer = _tokenizer;
        _tokenizer = NewTokenizer(text);
        try
        {
            while (true)
            {
                ref var token = ref _tokenizer.GetStructToken();
                if (token.Type == HtmlTokenType.EndOfFile)
                {
                    break;
                }

                ProcessToken(ref token);
            }
        }
        finally
        {
            _tokenizer = outer;
        }
    }

    /// <summary>
    /// How much of a character token that ran to the end of the input cannot change with more
    /// input: everything before a character reference that might go on, a trailing <c>&lt;</c>
    /// or <c>&lt;/</c>, or an unfinished CDATA section.
    /// </summary>
    private static int CertainTextLength(ReadOnlySpan<char> raw)
    {
        if (raw.StartsWith("<![CDATA[", StringComparison.Ordinal) && !raw.EndsWith("]]>", StringComparison.Ordinal))
        {
            return 0;
        }

        if (raw.EndsWith("</", StringComparison.Ordinal))
        {
            return raw.Length - 2;
        }

        if (raw.EndsWith("<", StringComparison.Ordinal))
        {
            return raw.Length - 1;
        }

        var amp = raw.LastIndexOf('&');
        if (amp >= 0 && MayStillBeACharacterReference(raw[(amp + 1)..]))
        {
            return amp;
        }

        return raw.Length;
    }

    /// <summary>Whether what follows an ampersand at the end of the input could still become a reference.</summary>
    private static bool MayStillBeACharacterReference(ReadOnlySpan<char> rest)
    {
        if (rest.IsEmpty)
        {
            return true;
        }

        if (rest[0] == '#')
        {
            var digits = rest[1..];
            if (digits is ['x' or 'X', ..])
            {
                foreach (var c in digits[1..])
                {
                    if (!char.IsAsciiHexDigit(c))
                    {
                        return false;
                    }
                }

                return true;
            }

            foreach (var c in digits)
            {
                if (!char.IsAsciiDigit(c))
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var c in rest)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CommentIsClosed(ReadOnlySpan<char> raw)
    {
        if (!raw.StartsWith("<!--", StringComparison.Ordinal))
        {
            // A bogus comment ends at the first '>'.
            return raw[^1] == '>';
        }

        return raw.EndsWith("-->", StringComparison.Ordinal)
            || raw.EndsWith("--!>", StringComparison.Ordinal)
            || raw.SequenceEqual("<!-->")
            || raw.SequenceEqual("<!--->");
    }

    /// <summary>Empty a raw text element's text, which the next feed tokenizes again.</summary>
    private void ClearText(Rec element)
    {
        for (var child = _tree.GetNode(element.Id)?.FirstChild; child is { } id; child = _tree.GetNode(id)?.NextSibling)
        {
            if (_tree.GetNode(id)?.Data is TextData text)
            {
                _tree.RecordChange(-2L * text.Contents.Length);
                text.Contents = string.Empty;
            }
        }
    }
}
