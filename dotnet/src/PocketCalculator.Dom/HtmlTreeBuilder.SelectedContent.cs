namespace PocketCalculator.Dom;

// <selectedcontent> (customizable select, as Chromium 141 ships it): the first selectedcontent
// inside a select without `multiple` holds a copy of the selected option's children. The parser
// part is here: when an option is popped off the stack of open elements and it is the select's
// selected option, its children are cloned into the selectedcontent ("maybe clone an option into
// selectedcontent"). html5ever (crates/obscura-dom) predates the element and does nothing.
//
// Only a document parse does this. Chromium ties a selectedcontent to its select when it is
// inserted into a connected select, so a fragment parsed for innerHTML (disconnected) copies
// nothing at parse time; the copy it makes once the fragment is inserted is DOM behaviour the
// port does not have (see "Known deviations" in todo.md).
internal sealed partial class HtmlTreeBuilder
{
    private sealed class SelectState
    {
        /// <summary>The select's first selectedcontent descendant, once one is parsed.</summary>
        public NodeId? Content;

        /// <summary>The option last found selected when it was popped.</summary>
        public NodeId? Selected;

        public bool SawSelectedAttribute;
        public bool SawEnabledOption;
    }

    private Dictionary<NodeId, SelectState>? _selects;

    private SelectState? StateOfOpenSelect()
    {
        if (_context is not null || TopOf(HtmlTag.Select) is not { } select)
        {
            return null;
        }

        _selects ??= [];
        if (!_selects.TryGetValue(select.Id, out var state))
        {
            state = new SelectState();
            _selects[select.Id] = state;
        }

        return state;
    }

    /// <summary>
    /// The end of the input pops every element still open, so an option left open there is
    /// cloned too (<c>&lt;select&gt;...&lt;option&gt;X</c> at end of file).
    /// </summary>
    private void PopOpenOptionsAtEnd()
    {
        if (_selects is null || _tree.ParseTruncated)
        {
            return;
        }

        try
        {
            while (_stack.Count > 0)
            {
                Pop();
            }

            FlushText();
        }
        catch (DomQuotaExceededException)
        {
            _tree.ParseTruncated = true;
        }
    }

    /// <summary>A selectedcontent element was inserted and pushed.</summary>
    private void SelectedContentInserted(Rec rec)
    {
        if (StateOfOpenSelect() is not { Content: null } state)
        {
            return;
        }

        state.Content = rec.Id;
        if (state.Selected is { } option && !HasMultiple())
        {
            CloneOptionInto(option, rec.Id);
        }
    }

    /// <summary>An option element was popped off the stack of open elements.</summary>
    private void OptionPopped(Rec option)
    {
        if (StateOfOpenSelect() is not { } state || _tree.GetNode(option.Id) is not { } node)
        {
            return;
        }

        // The select's selectedness setting algorithm, as far as the options parsed so far go:
        // the last option with `selected`, else the first option that is not disabled.
        var selected = false;
        if (node.GetAttribute("selected") is not null)
        {
            selected = true;
            state.SawSelectedAttribute = true;
        }
        else if (!state.SawSelectedAttribute && !state.SawEnabledOption && !IsDisabledOption(option.Id))
        {
            selected = true;
        }

        if (!IsDisabledOption(option.Id))
        {
            state.SawEnabledOption = true;
        }

        if (!selected)
        {
            return;
        }

        state.Selected = option.Id;
        if (state.Content is { } content && !HasMultiple())
        {
            CloneOptionInto(option.Id, content);
        }
    }

    private bool HasMultiple() =>
        TopOf(HtmlTag.Select) is { } select && _tree.GetNode(select.Id)?.GetAttribute("multiple") is not null;

    private bool IsDisabledOption(NodeId option)
    {
        if (_tree.GetNode(option)?.GetAttribute("disabled") is not null)
        {
            return true;
        }

        return _tree.GetNode(option)?.Parent is { } parent
            && _tree.GetNode(parent) is { } group
            && group.ElementName is { Local: "optgroup" }
            && group.GetAttribute("disabled") is not null;
    }

    /// <summary>Replace the selectedcontent's children with deep clones of the option's.</summary>
    private void CloneOptionInto(NodeId option, NodeId content)
    {
        FlushText();
        foreach (var child in _tree.Children(content))
        {
            _tree.RemoveChild(child);
        }

        foreach (var child in _tree.Children(option))
        {
            if (_tree.CloneNode(child, deep: true) is { } clone)
            {
                _tree.AppendChild(content, clone);
            }
        }
    }
}
