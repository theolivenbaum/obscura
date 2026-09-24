namespace PocketCalculator.Dom;

internal sealed partial class HtmlTreeBuilder
{
    /// <summary>The rules for parsing tokens in foreign content.</summary>
    private void ProcessForeign(Tok t)
    {
        switch (t.Kind)
        {
            case TokKind.Character:
            {
                var chars = t.Chars.Span;
                if (chars.Contains('\0'))
                {
                    chars = chars.ToString().Replace('\0', '�');
                }

                InsertText(chars);
                foreach (var c in chars)
                {
                    if (!IsSpace(c) && c != '�')
                    {
                        _framesetOk = false;
                        break;
                    }
                }

                return;
            }

            case TokKind.Comment:
                InsertComment(t.Text);
                return;

            case TokKind.StartTag:
                if (ForeignBreakoutTags[(int)t.Tag]
                    || (t.Tag == HtmlTag.Font
                        && (GetAttr(t.Attrs, "color") is not null
                            || GetAttr(t.Attrs, "face") is not null
                            || GetAttr(t.Attrs, "size") is not null)))
                {
                    BreakOutOfForeignContent(t);
                    return;
                }

                ForeignStartTag(t);
                return;

            case TokKind.EndTag:
                if (t.Tag is HtmlTag.Br or HtmlTag.P)
                {
                    BreakOutOfForeignContent(t);
                    return;
                }

                ForeignEndTag(t);
                return;
        }
    }

    private void BreakOutOfForeignContent(Tok t)
    {
        while (CurrentNode is { } current
               && current.Ns != ElemNs.Html
               && !current.Is(RecFlags.MathTextIntegrationPoint)
               && !current.Is(RecFlags.HtmlIntegrationPoint))
        {
            Pop();
        }

        ProcessInMode(_mode, t);
    }

    private void ForeignStartTag(Tok t)
    {
        var acn = AdjustedCurrentNode!;
        if (acn.Ns == ElemNs.MathMl)
        {
            InsertForeignElement(t, Namespaces.MathMl, t.Name);
            return;
        }

        var local = SvgTagNames.TryGetValue(t.Name, out var adjusted) ? adjusted : t.Name;
        InsertForeignElement(t, Namespaces.Svg, local);
    }

    /// <summary>
    /// Insert a foreign element for a start tag, adjusting its attributes for the namespace, and
    /// pop it again if the tag was self-closing.
    /// </summary>
    private void InsertForeignElement(Tok t, string ns, string local)
    {
        var isSvg = string.Equals(ns, Namespaces.Svg, StringComparison.Ordinal);
        foreach (var attr in t.Attrs)
        {
            var name = attr.Name.Local;
            if (isSvg)
            {
                if (SvgAttributeNames.TryGetValue(name, out var svgName))
                {
                    attr.Name = QualName.Attr(svgName);
                    continue;
                }
            }
            else if (string.Equals(name, "definitionurl", StringComparison.Ordinal))
            {
                attr.Name = QualName.Attr("definitionURL");
                continue;
            }

            if (ForeignAttributeNames.TryGetValue(name, out var foreign))
            {
                attr.Name = foreign;
            }
        }

        InsertElement(ns, local, t.Attrs);
        if (t.SelfClosing)
        {
            Pop();
        }
    }

    private void ForeignEndTag(Tok t)
    {
        if (_stack.Count <= 1)
        {
            return;
        }

        // The walk from the current node matches foreign elements by lowercased name and stops
        // at the first HTML element, which hands the token to the insertion mode.
        var match = _topForeign.GetValueOrDefault(t.Name);
        var html = _topHtml?.Index ?? -1;
        if (match is not null && match.Index > html)
        {
            PopUntil(match);
            return;
        }

        ProcessInMode(_mode, t);
    }
}
