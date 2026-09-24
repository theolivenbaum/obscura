using AngleSharp.Html.Parser;

namespace PocketCalculator.Dom;

internal sealed partial class HtmlTreeBuilder
{
    private void ProcessInMode(Mode mode, Tok t)
    {
        switch (mode)
        {
            case Mode.Initial: Initial(t); break;
            case Mode.BeforeHtml: BeforeHtml(t); break;
            case Mode.BeforeHead: BeforeHead(t); break;
            case Mode.InHead: InHead(t); break;
            case Mode.AfterHead: AfterHead(t); break;
            case Mode.InBody: InBody(t); break;
            case Mode.Text: InText(t); break;
            case Mode.InTable: InTable(t); break;
            case Mode.InTableText: InTableText(t); break;
            case Mode.InCaption: InCaption(t); break;
            case Mode.InColumnGroup: InColumnGroup(t); break;
            case Mode.InTableBody: InTableBody(t); break;
            case Mode.InRow: InRow(t); break;
            case Mode.InCell: InCell(t); break;
            case Mode.InTemplate: InTemplate(t); break;
            case Mode.AfterBody: AfterBody(t); break;
            case Mode.InFrameset: InFrameset(t); break;
            case Mode.AfterFrameset: AfterFrameset(t); break;
            case Mode.AfterAfterBody: AfterAfterBody(t); break;
            case Mode.AfterAfterFrameset: AfterAfterFrameset(t); break;
        }
    }

    private void Reprocess(Mode mode, Tok t)
    {
        _mode = mode;
        Process(t);
    }

    /// <summary>
    /// Handle the leading whitespace of a character token with <paramref name="whitespace"/>
    /// and return what is left, or true when nothing is.
    /// </summary>
    private static bool SplitLeadingSpace(Tok t, out ReadOnlySpan<char> space)
    {
        var chars = t.Chars.Span;
        var n = LeadingSpace(chars);
        space = chars[..n];
        t.Chars = t.Chars[n..];
        return t.Chars.IsEmpty;
    }

    // ------------------------------------------------------------------ initial .. after head

    private void Initial(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Character:
                if (SplitLeadingSpace(t, out _))
                {
                    return;
                }

                break;
            case TokKind.Comment:
                InsertComment(t.Text, new Location(_tree.Document, null));
                return;
        }

        // No DOCTYPE: quirks mode.
        _quirks = true;
        Reprocess(Mode.BeforeHtml, t);
    }

    private void BeforeHtml(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Comment:
                InsertComment(t.Text, new Location(_tree.Document, null));
                return;
            case TokKind.Character:
                if (SplitLeadingSpace(t, out _))
                {
                    return;
                }

                break;
            case TokKind.StartTag when t.Tag == HtmlTag.Html:
                InsertElement(Namespaces.Html, "html", t.Attrs, new Location(_tree.Document, null));
                _mode = Mode.BeforeHead;
                return;
            case TokKind.EndTag when t.Tag is not (HtmlTag.Head or HtmlTag.Body or HtmlTag.Html or HtmlTag.Br):
                return;
        }

        InsertElement(Namespaces.Html, "html", [], new Location(_tree.Document, null));
        Reprocess(Mode.BeforeHead, t);
    }

    private void BeforeHead(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Character:
                if (SplitLeadingSpace(t, out _))
                {
                    return;
                }

                break;
            case TokKind.Comment:
                InsertComment(t.Text);
                return;
            case TokKind.StartTag when t.Tag == HtmlTag.Html:
                InBody(t);
                return;
            case TokKind.StartTag when t.Tag == HtmlTag.Head:
                _head = InsertHtmlElement(t);
                _mode = Mode.InHead;
                return;
            case TokKind.EndTag when t.Tag is not (HtmlTag.Head or HtmlTag.Body or HtmlTag.Html or HtmlTag.Br):
                return;
        }

        _head = InsertHtmlElement(HtmlTag.Head);
        Reprocess(Mode.InHead, t);
    }

    private void InHead(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Character:
            {
                var done = SplitLeadingSpace(t, out var space);
                InsertText(space);
                if (done)
                {
                    return;
                }

                break;
            }

            case TokKind.Comment:
                InsertComment(t.Text);
                return;

            case TokKind.StartTag:
                switch (t.Tag)
                {
                    case HtmlTag.Html:
                        InBody(t);
                        return;
                    case HtmlTag.Base or HtmlTag.Basefont or HtmlTag.Bgsound or HtmlTag.Link or HtmlTag.Meta:
                        InsertVoidHtmlElement(t);
                        return;
                    case HtmlTag.Title:
                        GenericText(t, HtmlParseMode.RCData);
                        return;
                    case HtmlTag.Noscript or HtmlTag.Noframes or HtmlTag.Style:
                        // Scripting is enabled, so noscript is raw text.
                        GenericText(t, HtmlParseMode.Rawtext);
                        return;
                    case HtmlTag.Script:
                        GenericText(t, HtmlParseMode.Script);
                        return;
                    case HtmlTag.Template:
                        InsertHtmlElement(t);
                        PushMarker();
                        _framesetOk = false;
                        _mode = Mode.InTemplate;
                        _templateModes.Add(Mode.InTemplate);
                        return;
                    case HtmlTag.Head:
                        return;
                }

                break;

            case TokKind.EndTag:
                switch (t.Tag)
                {
                    case HtmlTag.Head:
                        Pop();
                        _mode = Mode.AfterHead;
                        return;
                    case HtmlTag.Body or HtmlTag.Html or HtmlTag.Br:
                        break;
                    case HtmlTag.Template:
                        if (!OnStack(HtmlTag.Template))
                        {
                            return;
                        }

                        GenerateImpliedEndTagsThoroughly();
                        PopUntilHtml(HtmlTag.Template);
                        ClearAfeToLastMarker();
                        _templateModes.RemoveAt(_templateModes.Count - 1);
                        ResetInsertionMode();
                        return;
                    default:
                        return;
                }

                break;
        }

        Pop();
        Reprocess(Mode.AfterHead, t);
    }

    /// <summary>The generic raw text and RCDATA element parsing algorithms (and script).</summary>
    private void GenericText(Tok t, HtmlParseMode state)
    {
        InsertHtmlElement(t);
        _tokenizer.State = state;
        _originalMode = _mode;
        _mode = Mode.Text;
    }

    private void AfterHead(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Character:
            {
                var done = SplitLeadingSpace(t, out var space);
                InsertText(space);
                if (done)
                {
                    return;
                }

                break;
            }

            case TokKind.Comment:
                InsertComment(t.Text);
                return;

            case TokKind.StartTag:
                switch (t.Tag)
                {
                    case HtmlTag.Html:
                        InBody(t);
                        return;
                    case HtmlTag.Body:
                        InsertHtmlElement(t);
                        _framesetOk = false;
                        _mode = Mode.InBody;
                        return;
                    case HtmlTag.Frameset:
                        InsertHtmlElement(t);
                        _mode = Mode.InFrameset;
                        return;
                    case HtmlTag.Base or HtmlTag.Basefont or HtmlTag.Bgsound or HtmlTag.Link or HtmlTag.Meta
                        or HtmlTag.Noframes or HtmlTag.Script or HtmlTag.Style or HtmlTag.Template or HtmlTag.Title:
                    {
                        var head = _head!;
                        Push(head);
                        InHead(t);
                        RemoveFromStack(head);
                        return;
                    }

                    case HtmlTag.Head:
                        return;
                }

                break;

            case TokKind.EndTag:
                switch (t.Tag)
                {
                    case HtmlTag.Template:
                        InHead(t);
                        return;
                    case HtmlTag.Body or HtmlTag.Html or HtmlTag.Br:
                        break;
                    default:
                        return;
                }

                break;
        }

        InsertHtmlElement(HtmlTag.Body);
        Reprocess(Mode.InBody, t);
    }

    // ------------------------------------------------------------------ in body

    private void InBody(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Character:
                BodyCharacters(t.Chars.Span);
                return;
            case TokKind.Comment:
                InsertComment(t.Text);
                return;
            case TokKind.StartTag:
                BodyStartTag(t);
                return;
            case TokKind.EndTag:
                BodyEndTag(t);
                return;
            case TokKind.Eof:
                if (_templateModes.Count > 0)
                {
                    InTemplate(t);
                    return;
                }

                _stopped = true;
                return;
        }
    }

    private void BodyCharacters(ReadOnlySpan<char> chars)
    {
        if (chars.Contains('\0'))
        {
            chars = chars.ToString().Replace("\0", "", StringComparison.Ordinal);
            if (chars.IsEmpty)
            {
                return;
            }
        }

        ReconstructActiveFormattingElements();
        InsertText(chars);
        if (!AllSpace(chars))
        {
            _framesetOk = false;
        }
    }

    private void BodyStartTag(Tok t)
    {
        var tag = t.Tag;
        if (BlockStartTags[(int)tag])
        {
            ClosePIfInButtonScope();
            InsertHtmlElement(t);
            return;
        }

        switch (tag)
        {
            case HtmlTag.Html:
                if (!OnStack(HtmlTag.Template))
                {
                    MergeAttributes(_stack[0], t.Attrs);
                }

                return;

            case HtmlTag.Base or HtmlTag.Basefont or HtmlTag.Bgsound or HtmlTag.Link or HtmlTag.Meta
                or HtmlTag.Noframes or HtmlTag.Script or HtmlTag.Style or HtmlTag.Template or HtmlTag.Title:
                InHead(t);
                return;

            case HtmlTag.Body:
                if (_stack.Count == 1 || !_stack[1].IsHtml(HtmlTag.Body) || OnStack(HtmlTag.Template))
                {
                    return;
                }

                _framesetOk = false;
                MergeAttributes(_stack[1], t.Attrs);
                return;

            case HtmlTag.Frameset:
            {
                if (_stack.Count == 1 || !_stack[1].IsHtml(HtmlTag.Body) || !_framesetOk)
                {
                    return;
                }

                FlushText();
                var body = _stack[1];
                if (_tree.GetNode(body.Id)?.Parent is not null)
                {
                    _tree.RemoveChild(body.Id);
                }

                while (_stack.Count > 1)
                {
                    Pop();
                }

                InsertHtmlElement(t);
                _mode = Mode.InFrameset;
                return;
            }

            case >= HtmlTag.H1 and <= HtmlTag.H6:
                ClosePIfInButtonScope();
                if (CurrentNode is { Ns: ElemNs.Html } current && IsHeading(current.Tag))
                {
                    Pop();
                }

                InsertHtmlElement(t);
                return;

            case HtmlTag.Pre or HtmlTag.Listing:
                ClosePIfInButtonScope();
                InsertHtmlElement(t);
                _skipNextNewline = true;
                _framesetOk = false;
                return;

            case HtmlTag.Form:
            {
                var hasTemplate = OnStack(HtmlTag.Template);
                if (_form is not null && !hasTemplate)
                {
                    return;
                }

                ClosePIfInButtonScope();
                var form = InsertHtmlElement(t);
                if (!hasTemplate)
                {
                    _form = form;
                }

                return;
            }

            case HtmlTag.Li:
            case HtmlTag.Dd or HtmlTag.Dt:
            {
                _framesetOk = false;

                // The walk stops at the first li (dd, dt) or special element other than
                // address, div and p; li, dd and dt are themselves such elements.
                if (_topSpecialNotAdp is { Ns: ElemNs.Html } node
                    && (tag == HtmlTag.Li ? node.Tag == HtmlTag.Li : node.Tag is HtmlTag.Dd or HtmlTag.Dt))
                {
                    GenerateImpliedEndTags(node.Tag);
                    PopUntilHtml(node.Tag);
                }

                ClosePIfInButtonScope();
                InsertHtmlElement(t);
                return;
            }

            case HtmlTag.Plaintext:
                ClosePIfInButtonScope();
                InsertHtmlElement(t);
                _tokenizer.State = HtmlParseMode.Plaintext;
                return;

            case HtmlTag.Button:
                if (InScope(HtmlTag.Button))
                {
                    GenerateImpliedEndTags();
                    PopUntilHtml(HtmlTag.Button);
                }

                ReconstructActiveFormattingElements();
                InsertHtmlElement(t);
                _framesetOk = false;
                return;

            case HtmlTag.A:
            {
                if (LastFormattingElement(HtmlTag.A) is { } element)
                {
                    AdoptionAgency(HtmlTag.A, "a");
                    RemoveFromAfe(element);
                    RemoveFromStack(element);
                }

                ReconstructActiveFormattingElements();
                var a = InsertHtmlElement(t);
                PushFormatting(a, t.Attrs);
                return;
            }

            case HtmlTag.B or HtmlTag.Big or HtmlTag.Code or HtmlTag.Em or HtmlTag.Font or HtmlTag.I
                or HtmlTag.S or HtmlTag.Small or HtmlTag.Strike or HtmlTag.Strong or HtmlTag.Tt or HtmlTag.U:
            {
                ReconstructActiveFormattingElements();
                var element = InsertHtmlElement(t);
                PushFormatting(element, t.Attrs);
                return;
            }

            case HtmlTag.Nobr:
            {
                ReconstructActiveFormattingElements();
                if (InScope(HtmlTag.Nobr))
                {
                    AdoptionAgency(HtmlTag.Nobr, "nobr");
                    ReconstructActiveFormattingElements();
                }

                var element = InsertHtmlElement(t);
                PushFormatting(element, t.Attrs);
                return;
            }

            case HtmlTag.Applet or HtmlTag.Marquee or HtmlTag.Object:
                ReconstructActiveFormattingElements();
                InsertHtmlElement(t);
                PushMarker();
                _framesetOk = false;
                return;

            case HtmlTag.Table:
                if (!_quirks)
                {
                    ClosePIfInButtonScope();
                }

                InsertHtmlElement(t);
                _framesetOk = false;
                _mode = Mode.InTable;
                return;

            case HtmlTag.Area or HtmlTag.Br or HtmlTag.Embed or HtmlTag.Img or HtmlTag.Keygen or HtmlTag.Wbr:
                ReconstructActiveFormattingElements();
                InsertVoidHtmlElement(t);
                _framesetOk = false;
                return;

            case HtmlTag.Input:
            {
                // Customizable select: an input closes an open select, and is dropped when the
                // fragment's context is a select.
                if (_context is { } context && context.IsHtml(HtmlTag.Select))
                {
                    return;
                }

                if (InScope(HtmlTag.Select))
                {
                    CloseSelect();
                }

                ReconstructActiveFormattingElements();
                InsertVoidHtmlElement(t);
                var type = GetAttr(t.Attrs, "type");
                if (type is null || !type.Equals("hidden", StringComparison.OrdinalIgnoreCase))
                {
                    _framesetOk = false;
                }

                return;
            }

            case HtmlTag.Param or HtmlTag.Source or HtmlTag.Track:
                InsertVoidHtmlElement(t);
                return;

            case HtmlTag.Hr:
                ClosePIfInButtonScope();
                if (InScope(HtmlTag.Select))
                {
                    GenerateImpliedEndTags();
                }

                InsertVoidHtmlElement(t);
                _framesetOk = false;
                return;

            case HtmlTag.Image:
                t.Tag = HtmlTag.Img;
                t.Name = "img";
                Process(t);
                return;

            case HtmlTag.Textarea:
                InsertHtmlElement(t);
                _skipNextNewline = true;
                _tokenizer.State = HtmlParseMode.RCData;
                _originalMode = _mode;
                _framesetOk = false;
                _mode = Mode.Text;
                return;

            case HtmlTag.Xmp:
                ClosePIfInButtonScope();
                ReconstructActiveFormattingElements();
                _framesetOk = false;
                GenericText(t, HtmlParseMode.Rawtext);
                return;

            case HtmlTag.Iframe:
                _framesetOk = false;
                GenericText(t, HtmlParseMode.Rawtext);
                return;

            case HtmlTag.Noembed or HtmlTag.Noscript:
                GenericText(t, HtmlParseMode.Rawtext);
                return;

            case HtmlTag.Select:
                if (_context is { } selectContext && selectContext.IsHtml(HtmlTag.Select))
                {
                    return;
                }

                if (InScope(HtmlTag.Select))
                {
                    CloseSelect();
                    return;
                }

                ReconstructActiveFormattingElements();
                InsertHtmlElement(t);
                _framesetOk = false;
                return;

            case HtmlTag.Option:
                if (InScope(HtmlTag.Select))
                {
                    GenerateImpliedEndTags(HtmlTag.Optgroup);
                }
                else if (CurrentIs(HtmlTag.Option))
                {
                    Pop();
                }

                ReconstructActiveFormattingElements();
                InsertHtmlElement(t);
                return;

            case HtmlTag.Optgroup:
                if (InScope(HtmlTag.Select))
                {
                    GenerateImpliedEndTags();
                }
                else if (CurrentIs(HtmlTag.Option))
                {
                    Pop();
                }

                ReconstructActiveFormattingElements();
                InsertHtmlElement(t);
                return;

            case HtmlTag.Rb or HtmlTag.Rtc:
                if (InScope(HtmlTag.Ruby))
                {
                    GenerateImpliedEndTags();
                }

                InsertHtmlElement(t);
                return;

            case HtmlTag.Rp or HtmlTag.Rt:
                if (InScope(HtmlTag.Ruby))
                {
                    GenerateImpliedEndTags(HtmlTag.Rtc);
                }

                InsertHtmlElement(t);
                return;

            case HtmlTag.Math:
                ReconstructActiveFormattingElements();
                InsertForeignElement(t, Namespaces.MathMl, t.Name);
                return;

            case HtmlTag.Svg:
                ReconstructActiveFormattingElements();
                InsertForeignElement(t, Namespaces.Svg, t.Name);
                return;

            case HtmlTag.Caption or HtmlTag.Col or HtmlTag.Colgroup or HtmlTag.Frame or HtmlTag.Head
                or HtmlTag.Tbody or HtmlTag.Td or HtmlTag.Tfoot or HtmlTag.Th or HtmlTag.Thead or HtmlTag.Tr:
                return;

            default:
                ReconstructActiveFormattingElements();
                InsertHtmlElement(t);
                return;
        }
    }

    private void CloseSelect() => PopUntilHtml(HtmlTag.Select);

    private void BodyEndTag(Tok t)
    {
        var tag = t.Tag;
        if (BlockEndTags[(int)tag])
        {
            if (!InScope(tag))
            {
                return;
            }

            GenerateImpliedEndTags();
            PopUntilHtml(tag);
            return;
        }

        switch (tag)
        {
            case HtmlTag.Template:
                InHead(t);
                return;

            case HtmlTag.Select:
                if (InScope(HtmlTag.Select))
                {
                    CloseSelect();
                }

                return;

            case HtmlTag.Body:
                if (InScope(HtmlTag.Body))
                {
                    _mode = Mode.AfterBody;
                }

                return;

            case HtmlTag.Html:
                if (InScope(HtmlTag.Body))
                {
                    Reprocess(Mode.AfterBody, t);
                }

                return;

            case HtmlTag.Form:
                if (!OnStack(HtmlTag.Template))
                {
                    var node = _form;
                    _form = null;
                    if (node is null || !InScope(node))
                    {
                        return;
                    }

                    GenerateImpliedEndTags();
                    RemoveFromStack(node);
                    return;
                }

                if (!InScope(HtmlTag.Form))
                {
                    return;
                }

                GenerateImpliedEndTags();
                PopUntilHtml(HtmlTag.Form);
                return;

            case HtmlTag.P:
                if (!InScope(HtmlTag.P, Scope.Button))
                {
                    InsertHtmlElement(HtmlTag.P);
                }

                ClosePElement();
                return;

            case HtmlTag.Li:
                if (!InScope(HtmlTag.Li, Scope.ListItem))
                {
                    return;
                }

                GenerateImpliedEndTags(HtmlTag.Li);
                PopUntilHtml(HtmlTag.Li);
                return;

            case HtmlTag.Dd or HtmlTag.Dt:
                if (!InScope(tag))
                {
                    return;
                }

                GenerateImpliedEndTags(tag);
                PopUntilHtml(tag);
                return;

            case >= HtmlTag.H1 and <= HtmlTag.H6:
                if (!HeadingInScope())
                {
                    return;
                }

                GenerateImpliedEndTags();
                PopUntilHeading();
                return;

            case HtmlTag.A or HtmlTag.B or HtmlTag.Big or HtmlTag.Code or HtmlTag.Em or HtmlTag.Font
                or HtmlTag.I or HtmlTag.Nobr or HtmlTag.S or HtmlTag.Small or HtmlTag.Strike
                or HtmlTag.Strong or HtmlTag.Tt or HtmlTag.U:
                AdoptionAgency(tag, t.Name);
                return;

            case HtmlTag.Applet or HtmlTag.Marquee or HtmlTag.Object:
                if (!InScope(tag))
                {
                    return;
                }

                GenerateImpliedEndTags();
                PopUntilHtml(tag);
                ClearAfeToLastMarker();
                return;

            case HtmlTag.Br:
                // "</br>" is treated as "<br>" without attributes.
                t.Kind = TokKind.StartTag;
                t.Attrs = [];
                BodyStartTag(t);
                return;

            default:
                AnyOtherEndTag(t);
                return;
        }
    }

    /// <summary>
    /// "Any other end tag" in body. The walk from the current node stops at the first HTML
    /// element with the token's name, or at the first special element; the topmost of each is
    /// on a chain, so it compares their positions.
    /// </summary>
    private void AnyOtherEndTag(Tok t)
    {
        var match = t.Tag != HtmlTag.Unknown
            ? TopOf(t.Tag)
            : _topOtherHtml.GetValueOrDefault(t.Name);
        if (match is null)
        {
            return;
        }

        var special = _topSpecial?.Index ?? -1;
        if (match.Index < special)
        {
            return;
        }

        GenerateImpliedEndTags(match.Tag == HtmlTag.Unknown ? HtmlTag.Count : match.Tag);
        // Implied end tags stop at the matched element only by name; an unknown name is never
        // in the implied list, so nothing above it survives the pops that follow either way.
        PopUntil(match);
    }

    // ------------------------------------------------------------------ adoption agency

    private void AdoptionAgency(HtmlTag tag, string name)
    {
        FlushText();

        var current = CurrentNode!;
        if (current.IsHtml(tag) && current.Fmt is null)
        {
            Pop();
            return;
        }

        for (var outer = 0; outer < 8; outer++)
        {
            var formatting = LastFormattingElement(tag);
            if (formatting is null)
            {
                var fake = new Tok { Kind = TokKind.EndTag, Tag = tag, Name = name };
                AnyOtherEndTag(fake);
                return;
            }

            if (formatting.Index < 0)
            {
                RemoveFromAfe(formatting);
                return;
            }

            if (!InScope(formatting))
            {
                return;
            }

            // The furthest block: the lowest special element above the formatting element.
            Rec? furthest = null;
            for (var i = formatting.Index + 1; i < _stack.Count; i++)
            {
                if (_stack[i].Is(RecFlags.Special))
                {
                    furthest = _stack[i];
                    break;
                }
            }

            if (furthest is null)
            {
                PopUntil(formatting);
                RemoveFromAfe(formatting);
                return;
            }

            var commonAncestor = _stack[formatting.Index - 1];

            // The inner loop works on a copy of the stack above the formatting element, where a
            // removed entry becomes null; the copy is compacted and written back once, so
            // removing many entries does not shift the stack once per entry.
            var baseIndex = formatting.Index;
            List<Rec?> segment = [.. _stack.GetRange(baseIndex + 1, _stack.Count - baseIndex - 1)];
            var furthestPos = furthest.Index - baseIndex - 1;

            // The bookmark: the formatting element's entry is replaced unless a new position is set.
            FmtEntry? bookmarkAfter = null;
            var lastNode = furthest;
            var inner = 0;
            for (var j = furthestPos - 1; j >= 0; j--)
            {
                inner++;
                var node = segment[j]!;
                if (inner > 3 && node.Fmt is not null)
                {
                    RemoveFromAfe(node);
                }

                if (node.Fmt is not { } entry)
                {
                    segment[j] = null;
                    continue;
                }

                var replacement = CreateElement(Namespaces.Html, node.Local, CloneAttributes(node.TokenAttrs!), out _);
                replacement.TokenAttrs = node.TokenAttrs;
                entry.Element = replacement;
                replacement.Fmt = entry;
                node.Fmt = null;
                segment[j] = replacement;

                if (lastNode == furthest)
                {
                    bookmarkAfter = entry;
                }

                _tree.AppendChild(replacement.Id, lastNode.Id);
                lastNode = replacement;
            }

            // Insert the last node at the appropriate place for the common ancestor.
            var location = AppropriatePlace(commonAncestor);
            InsertAt(location, lastNode.Id);

            // A new formatting element takes the furthest block's children.
            var clone = CreateElement(Namespaces.Html, formatting.Local, CloneAttributes(formatting.TokenAttrs!), out _);
            clone.TokenAttrs = formatting.TokenAttrs;
            var furthestNode = _tree.GetNode(furthest.Id)!;
            while (furthestNode.FirstChild is { } child)
            {
                _tree.AppendChild(clone.Id, child);
            }

            _tree.AppendChild(furthest.Id, clone.Id);

            // The list of active formatting elements: the clone takes the formatting element's
            // place, or the bookmark's.
            var formattingEntry = formatting.Fmt!;
            var cloneEntry = new FmtEntry
            {
                Element = clone,
                Segment = formattingEntry.Segment,
                Sig = formattingEntry.Sig,
            };
            clone.Fmt = cloneEntry;
            formatting.Fmt = null;
            if (bookmarkAfter is null)
            {
                AfeReplace(formattingEntry, cloneEntry);
            }
            else
            {
                AfeUnlink(formattingEntry);
                AfeInsertAfter(bookmarkAfter, cloneEntry);
            }

            var sameSig = cloneEntry.Segment.BySig[cloneEntry.Sig];
            sameSig[sameSig.IndexOf(formattingEntry)] = cloneEntry;

            // The stack: drop the formatting element, put the clone just above the furthest block.
            var rebuilt = new List<Rec>(segment.Count + 1);
            foreach (var entry in segment)
            {
                if (entry is null)
                {
                    continue;
                }

                rebuilt.Add(entry);
                if (entry == furthest)
                {
                    rebuilt.Add(clone);
                }
            }

            ReplaceStackFrom(baseIndex, rebuilt);
        }
    }

    // ------------------------------------------------------------------ text

    private void InText(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Character:
                InsertText(t.Chars.Span);
                return;
            case TokKind.Eof:
                Pop();
                Reprocess(_originalMode, t);
                return;
            case TokKind.EndTag:
                Pop();
                _mode = _originalMode;
                return;
        }
    }

    // ------------------------------------------------------------------ tables

    private bool CurrentIsTableish() =>
        CurrentNode is { Ns: ElemNs.Html } c
        && c.Tag is HtmlTag.Table or HtmlTag.Tbody or HtmlTag.Template or HtmlTag.Tfoot or HtmlTag.Thead or HtmlTag.Tr;

    private void ClearStackBackTo(HtmlTag a, HtmlTag b, HtmlTag c)
    {
        while (CurrentNode is { } node && !(node.Ns == ElemNs.Html && (node.Tag == a || node.Tag == b || node.Tag == c)))
        {
            Pop();
        }
    }

    private void ClearToTableContext() => ClearStackBackTo(HtmlTag.Table, HtmlTag.Template, HtmlTag.Html);

    private void ClearToTableBodyContext()
    {
        while (CurrentNode is { } node
               && !(node.Ns == ElemNs.Html
                    && node.Tag is HtmlTag.Tbody or HtmlTag.Tfoot or HtmlTag.Thead or HtmlTag.Template or HtmlTag.Html))
        {
            Pop();
        }
    }

    private void ClearToTableRowContext() => ClearStackBackTo(HtmlTag.Tr, HtmlTag.Template, HtmlTag.Html);

    private void InTable(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Character when CurrentIsTableish():
                _pendingTableText.Clear();
                _pendingTableTextHasNonSpace = false;
                _originalMode = _mode;
                Reprocess(Mode.InTableText, t);
                return;

            case TokKind.Comment:
                InsertComment(t.Text);
                return;

            case TokKind.StartTag:
                switch (t.Tag)
                {
                    case HtmlTag.Caption:
                        ClearToTableContext();
                        PushMarker();
                        InsertHtmlElement(t);
                        _mode = Mode.InCaption;
                        return;
                    case HtmlTag.Colgroup:
                        ClearToTableContext();
                        InsertHtmlElement(t);
                        _mode = Mode.InColumnGroup;
                        return;
                    case HtmlTag.Col:
                        ClearToTableContext();
                        InsertHtmlElement(HtmlTag.Colgroup);
                        Reprocess(Mode.InColumnGroup, t);
                        return;
                    case HtmlTag.Tbody or HtmlTag.Tfoot or HtmlTag.Thead:
                        ClearToTableContext();
                        InsertHtmlElement(t);
                        _mode = Mode.InTableBody;
                        return;
                    case HtmlTag.Td or HtmlTag.Th or HtmlTag.Tr:
                        ClearToTableContext();
                        InsertHtmlElement(HtmlTag.Tbody);
                        Reprocess(Mode.InTableBody, t);
                        return;
                    case HtmlTag.Table:
                        if (!InScope(HtmlTag.Table, Scope.Table))
                        {
                            return;
                        }

                        PopUntilHtml(HtmlTag.Table);
                        ResetInsertionMode();
                        Process(t);
                        return;
                    case HtmlTag.Style or HtmlTag.Script or HtmlTag.Template:
                        InHead(t);
                        return;
                    case HtmlTag.Input:
                    {
                        var type = GetAttr(t.Attrs, "type");
                        if (type is null || !type.Equals("hidden", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        InsertVoidHtmlElement(t);
                        return;
                    }

                    case HtmlTag.Form:
                        if (OnStack(HtmlTag.Template) || _form is not null)
                        {
                            return;
                        }

                        _form = InsertHtmlElement(t);
                        Pop();
                        return;
                }

                break;

            case TokKind.EndTag:
                switch (t.Tag)
                {
                    case HtmlTag.Table:
                        if (!InScope(HtmlTag.Table, Scope.Table))
                        {
                            return;
                        }

                        PopUntilHtml(HtmlTag.Table);
                        ResetInsertionMode();
                        return;
                    case HtmlTag.Body or HtmlTag.Caption or HtmlTag.Col or HtmlTag.Colgroup or HtmlTag.Html
                        or HtmlTag.Tbody or HtmlTag.Td or HtmlTag.Tfoot or HtmlTag.Th or HtmlTag.Thead or HtmlTag.Tr:
                        return;
                    case HtmlTag.Template:
                        InHead(t);
                        return;
                }

                break;

            case TokKind.Eof:
                InBody(t);
                return;
        }

        // Anything else: foster parenting.
        _fosterParenting = true;
        InBody(t);
        _fosterParenting = false;
    }

    private void InTableText(Tok t)
    {
        if (t.Kind == TokKind.Character)
        {
            var chars = t.Chars.Span;
            foreach (var c in chars)
            {
                if (c == '\0')
                {
                    continue;
                }

                _pendingTableText.Append(c);
                if (!IsSpace(c))
                {
                    _pendingTableTextHasNonSpace = true;
                }
            }

            return;
        }

        if (_pendingTableText.Length > 0)
        {
            var text = _pendingTableText.ToString();
            _pendingTableText.Clear();
            if (_pendingTableTextHasNonSpace)
            {
                _fosterParenting = true;
                BodyCharacters(text);
                _fosterParenting = false;
            }
            else
            {
                InsertText(text);
            }
        }

        _pendingTableTextHasNonSpace = false;
        Reprocess(_originalMode, t);
    }

    private void InCaption(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.EndTag when t.Tag == HtmlTag.Caption:
                CloseCaption();
                return;

            case TokKind.StartTag when t.Tag is HtmlTag.Caption or HtmlTag.Col or HtmlTag.Colgroup
                or HtmlTag.Tbody or HtmlTag.Td or HtmlTag.Tfoot or HtmlTag.Th or HtmlTag.Thead or HtmlTag.Tr:
            case TokKind.EndTag when t.Tag == HtmlTag.Table:
                if (CloseCaption())
                {
                    Process(t);
                }

                return;

            case TokKind.EndTag when t.Tag is HtmlTag.Body or HtmlTag.Col or HtmlTag.Colgroup or HtmlTag.Html
                or HtmlTag.Tbody or HtmlTag.Td or HtmlTag.Tfoot or HtmlTag.Th or HtmlTag.Thead or HtmlTag.Tr:
                return;
        }

        InBody(t);
    }

    private bool CloseCaption()
    {
        if (!InScope(HtmlTag.Caption, Scope.Table))
        {
            return false;
        }

        GenerateImpliedEndTags();
        PopUntilHtml(HtmlTag.Caption);
        ClearAfeToLastMarker();
        _mode = Mode.InTable;
        return true;
    }

    private void InColumnGroup(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Character:
            {
                var done = SplitLeadingSpace(t, out var space);
                InsertText(space);
                if (done)
                {
                    return;
                }

                break;
            }

            case TokKind.Comment:
                InsertComment(t.Text);
                return;

            case TokKind.StartTag:
                switch (t.Tag)
                {
                    case HtmlTag.Html:
                        InBody(t);
                        return;
                    case HtmlTag.Col:
                        InsertVoidHtmlElement(t);
                        return;
                    case HtmlTag.Template:
                        InHead(t);
                        return;
                }

                break;

            case TokKind.EndTag:
                switch (t.Tag)
                {
                    case HtmlTag.Colgroup:
                        if (!CurrentIs(HtmlTag.Colgroup))
                        {
                            return;
                        }

                        Pop();
                        _mode = Mode.InTable;
                        return;
                    case HtmlTag.Col:
                        return;
                    case HtmlTag.Template:
                        InHead(t);
                        return;
                }

                break;

            case TokKind.Eof:
                InBody(t);
                return;
        }

        if (!CurrentIs(HtmlTag.Colgroup))
        {
            return;
        }

        Pop();
        Reprocess(Mode.InTable, t);
    }

    private void InTableBody(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.StartTag:
                switch (t.Tag)
                {
                    case HtmlTag.Tr:
                        ClearToTableBodyContext();
                        InsertHtmlElement(t);
                        _mode = Mode.InRow;
                        return;
                    case HtmlTag.Th or HtmlTag.Td:
                        ClearToTableBodyContext();
                        InsertHtmlElement(HtmlTag.Tr);
                        Reprocess(Mode.InRow, t);
                        return;
                    case HtmlTag.Caption or HtmlTag.Col or HtmlTag.Colgroup or HtmlTag.Tbody or HtmlTag.Tfoot
                        or HtmlTag.Thead:
                        if (!AnyInTableScope(HtmlTag.Tbody, HtmlTag.Thead, HtmlTag.Tfoot))
                        {
                            return;
                        }

                        ClearToTableBodyContext();
                        Pop();
                        Reprocess(Mode.InTable, t);
                        return;
                }

                break;

            case TokKind.EndTag:
                switch (t.Tag)
                {
                    case HtmlTag.Tbody or HtmlTag.Tfoot or HtmlTag.Thead:
                        if (!InScope(t.Tag, Scope.Table))
                        {
                            return;
                        }

                        ClearToTableBodyContext();
                        Pop();
                        _mode = Mode.InTable;
                        return;
                    case HtmlTag.Table:
                        if (!AnyInTableScope(HtmlTag.Tbody, HtmlTag.Thead, HtmlTag.Tfoot))
                        {
                            return;
                        }

                        ClearToTableBodyContext();
                        Pop();
                        Reprocess(Mode.InTable, t);
                        return;
                    case HtmlTag.Body or HtmlTag.Caption or HtmlTag.Col or HtmlTag.Colgroup or HtmlTag.Html
                        or HtmlTag.Td or HtmlTag.Th or HtmlTag.Tr:
                        return;
                }

                break;
        }

        InTable(t);
    }

    private void InRow(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.StartTag:
                switch (t.Tag)
                {
                    case HtmlTag.Th or HtmlTag.Td:
                        ClearToTableRowContext();
                        InsertHtmlElement(t);
                        _mode = Mode.InCell;
                        PushMarker();
                        return;
                    case HtmlTag.Caption or HtmlTag.Col or HtmlTag.Colgroup or HtmlTag.Tbody or HtmlTag.Tfoot
                        or HtmlTag.Thead or HtmlTag.Tr:
                        if (CloseRow())
                        {
                            Process(t);
                        }

                        return;
                }

                break;

            case TokKind.EndTag:
                switch (t.Tag)
                {
                    case HtmlTag.Tr:
                        CloseRow();
                        return;
                    case HtmlTag.Table:
                        if (CloseRow())
                        {
                            Process(t);
                        }

                        return;
                    case HtmlTag.Tbody or HtmlTag.Tfoot or HtmlTag.Thead:
                        if (!InScope(t.Tag, Scope.Table))
                        {
                            return;
                        }

                        if (CloseRow())
                        {
                            Process(t);
                        }

                        return;
                    case HtmlTag.Body or HtmlTag.Caption or HtmlTag.Col or HtmlTag.Colgroup or HtmlTag.Html
                        or HtmlTag.Td or HtmlTag.Th:
                        return;
                }

                break;
        }

        InTable(t);
    }

    private bool CloseRow()
    {
        if (!InScope(HtmlTag.Tr, Scope.Table))
        {
            return false;
        }

        ClearToTableRowContext();
        Pop();
        _mode = Mode.InTableBody;
        return true;
    }

    private void InCell(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.EndTag when t.Tag is HtmlTag.Td or HtmlTag.Th:
                if (!InScope(t.Tag, Scope.Table))
                {
                    return;
                }

                GenerateImpliedEndTags();
                PopUntilHtml(t.Tag);
                ClearAfeToLastMarker();
                _mode = Mode.InRow;
                return;

            case TokKind.StartTag when t.Tag is HtmlTag.Caption or HtmlTag.Col or HtmlTag.Colgroup
                or HtmlTag.Tbody or HtmlTag.Td or HtmlTag.Tfoot or HtmlTag.Th or HtmlTag.Thead or HtmlTag.Tr:
                if (!AnyInTableScope(HtmlTag.Td, HtmlTag.Th))
                {
                    return;
                }

                CloseCell();
                Process(t);
                return;

            case TokKind.EndTag when t.Tag is HtmlTag.Body or HtmlTag.Caption or HtmlTag.Col
                or HtmlTag.Colgroup or HtmlTag.Html:
                return;

            case TokKind.EndTag when t.Tag is HtmlTag.Table or HtmlTag.Tbody or HtmlTag.Tfoot
                or HtmlTag.Thead or HtmlTag.Tr:
                if (!InScope(t.Tag, Scope.Table))
                {
                    return;
                }

                CloseCell();
                Process(t);
                return;
        }

        InBody(t);
    }

    private void CloseCell()
    {
        GenerateImpliedEndTags();
        while (_stack.Count > 0)
        {
            var r = Pop();
            if (r.Ns == ElemNs.Html && r.Tag is HtmlTag.Td or HtmlTag.Th)
            {
                break;
            }
        }

        ClearAfeToLastMarker();
        _mode = Mode.InRow;
    }

    // ------------------------------------------------------------------ template

    private void InTemplate(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Character:
            case TokKind.Comment:
                InBody(t);
                return;

            case TokKind.StartTag:
            {
                Mode next;
                switch (t.Tag)
                {
                    case HtmlTag.Base or HtmlTag.Basefont or HtmlTag.Bgsound or HtmlTag.Link or HtmlTag.Meta
                        or HtmlTag.Noframes or HtmlTag.Script or HtmlTag.Style or HtmlTag.Template or HtmlTag.Title:
                        InHead(t);
                        return;
                    case HtmlTag.Caption or HtmlTag.Colgroup or HtmlTag.Tbody or HtmlTag.Tfoot or HtmlTag.Thead:
                        next = Mode.InTable;
                        break;
                    case HtmlTag.Col:
                        next = Mode.InColumnGroup;
                        break;
                    case HtmlTag.Tr:
                        next = Mode.InTableBody;
                        break;
                    case HtmlTag.Td or HtmlTag.Th:
                        next = Mode.InRow;
                        break;
                    default:
                        next = Mode.InBody;
                        break;
                }

                _templateModes[^1] = next;
                Reprocess(next, t);
                return;
            }

            case TokKind.EndTag:
                if (t.Tag == HtmlTag.Template)
                {
                    InHead(t);
                }

                return;

            case TokKind.Eof:
                if (!OnStack(HtmlTag.Template))
                {
                    _stopped = true;
                    return;
                }

                PopUntilHtml(HtmlTag.Template);
                ClearAfeToLastMarker();
                _templateModes.RemoveAt(_templateModes.Count - 1);
                ResetInsertionMode();
                Process(t);
                return;
        }
    }

    // ------------------------------------------------------------------ after body, frameset

    private void AfterBody(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Character:
                if (AllSpace(t.Chars.Span))
                {
                    InBody(t);
                    return;
                }

                break;
            case TokKind.Comment:
                InsertComment(t.Text, new Location(_stack[0].Id, null));
                return;
            case TokKind.StartTag when t.Tag == HtmlTag.Html:
                InBody(t);
                return;
            case TokKind.EndTag when t.Tag == HtmlTag.Html:
                if (_context is null)
                {
                    _mode = Mode.AfterAfterBody;
                }

                return;
            case TokKind.Eof:
                _stopped = true;
                return;
        }

        Reprocess(Mode.InBody, t);
    }

    /// <summary>The whitespace of a character token, for the frameset modes that drop the rest.</summary>
    private void InsertOnlySpace(Tok t)
    {
        var chars = t.Chars.Span;
        if (AllSpace(chars))
        {
            InsertText(chars);
            return;
        }

        Span<char> buffer = chars.Length <= 256 ? stackalloc char[chars.Length] : new char[chars.Length];
        var n = 0;
        foreach (var c in chars)
        {
            if (IsSpace(c))
            {
                buffer[n++] = c;
            }
        }

        InsertText(buffer[..n]);
    }

    private void InFrameset(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Character:
                InsertOnlySpace(t);
                return;
            case TokKind.Comment:
                InsertComment(t.Text);
                return;
            case TokKind.StartTag:
                switch (t.Tag)
                {
                    case HtmlTag.Html:
                        InBody(t);
                        return;
                    case HtmlTag.Frameset:
                        InsertHtmlElement(t);
                        return;
                    case HtmlTag.Frame:
                        InsertVoidHtmlElement(t);
                        return;
                    case HtmlTag.Noframes:
                        InHead(t);
                        return;
                }

                return;
            case TokKind.EndTag when t.Tag == HtmlTag.Frameset:
                if (_stack.Count == 1)
                {
                    return;
                }

                Pop();
                if (_context is null && !CurrentIs(HtmlTag.Frameset))
                {
                    _mode = Mode.AfterFrameset;
                }

                return;
            case TokKind.Eof:
                _stopped = true;
                return;
        }
    }

    private void AfterFrameset(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Character:
                InsertOnlySpace(t);
                return;
            case TokKind.Comment:
                InsertComment(t.Text);
                return;
            case TokKind.StartTag when t.Tag == HtmlTag.Html:
                InBody(t);
                return;
            case TokKind.StartTag when t.Tag == HtmlTag.Noframes:
                InHead(t);
                return;
            case TokKind.EndTag when t.Tag == HtmlTag.Html:
                _mode = Mode.AfterAfterFrameset;
                return;
            case TokKind.Eof:
                _stopped = true;
                return;
        }
    }

    private void AfterAfterBody(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Comment:
                InsertComment(t.Text, new Location(_tree.Document, null));
                return;
            case TokKind.Character when AllSpace(t.Chars.Span):
            case TokKind.StartTag when t.Tag == HtmlTag.Html:
                InBody(t);
                return;
            case TokKind.Eof:
                _stopped = true;
                return;
        }

        Reprocess(Mode.InBody, t);
    }

    private void AfterAfterFrameset(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Comment:
                InsertComment(t.Text, new Location(_tree.Document, null));
                return;
            case TokKind.Character:
            {
                // Whitespace is processed using the in body rules; anything else is ignored.
                var chars = t.Chars.Span;
                Span<char> buffer = chars.Length <= 256 ? stackalloc char[chars.Length] : new char[chars.Length];
                var n = 0;
                foreach (var c in chars)
                {
                    if (IsSpace(c))
                    {
                        buffer[n++] = c;
                    }
                }

                if (n > 0)
                {
                    BodyCharacters(buffer[..n]);
                }

                return;
            }

            case TokKind.StartTag when t.Tag == HtmlTag.Html:
                InBody(t);
                return;
            case TokKind.StartTag when t.Tag == HtmlTag.Noframes:
                InHead(t);
                return;
            case TokKind.Eof:
                _stopped = true;
                return;
        }
    }

    // ------------------------------------------------------------------ reset the insertion mode

    private void ResetInsertionMode()
    {
        // The walk stops at the topmost of these; everything between is skipped.
        Rec? node = null;
        foreach (var tag in ResetModeTags)
        {
            var top = TopOf(tag);
            if (top is not null && (node is null || top.Index > node.Index))
            {
                node = top;
            }
        }

        var last = node is null || node.Index == 0;
        if (last)
        {
            node = _context ?? _stack[0];
        }

        switch (node!.Ns == ElemNs.Html ? node.Tag : HtmlTag.Unknown)
        {
            case HtmlTag.Td or HtmlTag.Th when !last:
                _mode = Mode.InCell;
                return;
            case HtmlTag.Tr:
                _mode = Mode.InRow;
                return;
            case HtmlTag.Tbody or HtmlTag.Thead or HtmlTag.Tfoot:
                _mode = Mode.InTableBody;
                return;
            case HtmlTag.Caption:
                _mode = Mode.InCaption;
                return;
            case HtmlTag.Colgroup:
                _mode = Mode.InColumnGroup;
                return;
            case HtmlTag.Table:
                _mode = Mode.InTable;
                return;
            case HtmlTag.Template:
                _mode = _templateModes[^1];
                return;
            case HtmlTag.Head when !last:
                _mode = Mode.InHead;
                return;
            case HtmlTag.Body:
                _mode = Mode.InBody;
                return;
            case HtmlTag.Frameset:
                _mode = Mode.InFrameset;
                return;
            case HtmlTag.Html:
                _mode = _head is null ? Mode.BeforeHead : Mode.AfterHead;
                return;
            default:
                _mode = Mode.InBody;
                return;
        }
    }
}
