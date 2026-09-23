using System.Text;
using PocketCalculator.Dom;
using PocketCalculator.Render.Layout;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render;

// PORT NOTE. crates/obscura-js computes innerText in bootstrap.js, calling getComputedStyle() for
// every element of the subtree and recursing once per level. Each call crossed into the engine,
// serialized and parsed the whole computed-style snapshot, and walked every ancestor for the
// inherited border-spacing, which made innerText O(n * depth) with a large constant (over 40 s on
// a 10k-deep chain, where the JS recursion also overflowed the stack) and 18 s on 50k siblings.
// This is the same algorithm over the retained cascade in one pass: it reads the three computed
// values the JS read (display, white-space, visibility) straight off the layout style, and its
// output is byte-identical to bootstrap's _innerTextCollect/_innerTextJoin, which stays as the
// fallback for builds without a render.
public sealed partial class PreparedRender
{
    /// <summary>
    /// The <c>innerText</c> of <paramref name="root"/>, computed the way bootstrap's
    /// <c>_innerTextOf</c> does for a rendered element, or null when some element of the subtree
    /// has no computed style (the caller then takes the script path, whose fallbacks for an
    /// unstyled element read the inline declaration).
    /// </summary>
    public string? InnerText(NodeId root)
    {
        if (TreeValue is not { } tree
            || tree.GetNode(root)?.AsElement() is not { } rootElement
            || !Layout.Styles.TryGetValue(root, out LayoutStyle? rootStyle))
        {
            return null;
        }

        if (IsInnerTextSkipTag(InnerTextTagName(rootElement))
            || ComputedDisplay(root, rootStyle, false) == "none")
        {
            // The script answers these with textContent itself.
            return null;
        }

        InnerTextJoiner joiner = new();
        List<Frame> stack = [new(root, tree.GetNode(root)!.FirstChild, IsPre(rootStyle), !IsHidden(rootStyle), 0)];
        int guard = 0;
        while (stack.Count > 0)
        {
            if ((++guard & 1023) == 0)
            {
                WorkCancellation.ThrowIfCancellationRequested();
            }

            Frame frame = stack[^1];
            if (frame.Next is not { } childId)
            {
                stack.RemoveAt(stack.Count - 1);
                switch (frame.Close)
                {
                    case 1:
                        joiner.Tab();
                        break;
                    case 2:
                        joiner.Breaks(frame.Breaks);
                        break;
                }

                continue;
            }

            Node? child = tree.GetNode(childId);
            stack[^1] = frame with { Next = child?.NextSibling };
            if (child is null)
            {
                continue;
            }

            if (child.Data is TextData text)
            {
                if (frame.Visible && text.Contents.Length > 0)
                {
                    joiner.Text(frame.Pre ? text.Contents : CollapseWhiteSpace(text.Contents), frame.Pre);
                }

                continue;
            }

            if (child.Data is not ElementData element)
            {
                continue;
            }

            string tag = InnerTextTagName(element);
            if (IsInnerTextSkipTag(tag))
            {
                continue;
            }

            if (!Layout.Styles.TryGetValue(childId, out LayoutStyle? style))
            {
                return null;
            }

            string display = ComputedDisplay(childId, style, false);
            if (display == "none")
            {
                continue;
            }

            if (tag == "BR")
            {
                joiner.ForcedBreak();
                continue;
            }

            bool cell = display == "table-cell" || ((tag == "TD" || tag == "TH") && display == "block");
            bool block = !cell && IsInnerTextBlock(display);
            int breaks = tag == "P" ? 2 : 1;
            byte close = 0;
            if (cell)
            {
                joiner.Tab();
                close = 1;
            }
            else if (block)
            {
                joiner.Breaks(breaks);
                close = 2;
            }

            if (stack.Count > tree.NodeSlotCount)
            {
                // A cyclic tree; the mutation guards make this unreachable.
                return null;
            }

            stack.Add(new Frame(childId, child.FirstChild, IsPre(style), !IsHidden(style), close, breaks));
        }

        return joiner.Finish();
    }

    private readonly record struct Frame(NodeId Node, NodeId? Next, bool Pre, bool Visible, byte Close, int Breaks = 1);

    // Element.tagName, as the tag_name DOM op spells it.
    private static string InnerTextTagName(ElementData element) =>
        string.Equals(element.Name.Ns, Namespaces.Html, StringComparison.Ordinal)
            ? element.Name.Local.ToUpperInvariant()
            : element.Name.Prefix is { } prefix
                ? prefix + ":" + element.Name.Local
                : element.Name.Local;

    // bootstrap's _innerTextSkipTags.
    private static bool IsInnerTextSkipTag(string tag) => tag switch
    {
        "SCRIPT" or "STYLE" or "HEAD" or "TITLE" or "META" or "LINK" or "NOSCRIPT" or "TEMPLATE" or "BASE" => true,
        _ => false,
    };

    // bootstrap's _innerTextIsBlock. `table` itself is deliberately not in it: only the
    // `table-*` internal displays, which are longer than five characters, are.
    private static bool IsInnerTextBlock(string display) =>
        display is "block" or "flow-root" or "list-item" or "flex" or "grid"
        || (display.Length > 5 && display.StartsWith("table", StringComparison.Ordinal));

    // bootstrap's _innerTextIsPre over the computed white-space, which is never empty.
    private static bool IsPre(LayoutStyle style) =>
        (style.WhiteSpace ?? WhiteSpace.Normal) is not (WhiteSpace.Normal or WhiteSpace.NoWrap);

    private static bool IsHidden(LayoutStyle style) => style.VisibilityHidden == true;

    // data.replace(/[\t\n\f\r ]+/g, ' ')
    private static string CollapseWhiteSpace(string data)
    {
        int i = data.AsSpan().IndexOfAny("\t\n\f\r ");
        if (i < 0)
        {
            return data;
        }

        StringBuilder sb = new(data.Length);
        sb.Append(data, 0, i);
        bool inRun = false;
        for (; i < data.Length; i++)
        {
            char c = data[i];
            if (c is '\t' or '\n' or '\f' or '\r' or ' ')
            {
                if (!inRun)
                {
                    sb.Append(' ');
                    inRun = true;
                }
            }
            else
            {
                sb.Append(c);
                inRun = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>bootstrap's <c>_innerTextJoin</c>, fed one segment at a time.</summary>
    private sealed class InnerTextJoiner
    {
        private readonly StringBuilder _result = new();
        private int _breaks;
        private int _forced;
        private bool _tab;
        private bool _seen;
        private bool _lastPre;

        public void Tab()
        {
            if (_seen)
            {
                _tab = true;
            }
        }

        public void Breaks(int breaks)
        {
            // Line breaks before any text at all are dropped; block boundaries that meet
            // collapse to the widest one.
            if (_seen && breaks > _breaks)
            {
                _breaks = breaks;
            }
        }

        public void ForcedBreak()
        {
            if (!_seen)
            {
                return;
            }

            // Consecutive <br> stack.
            _forced++;
            if (_forced > _breaks)
            {
                _breaks = _forced;
            }
        }

        public void Text(string text, bool pre)
        {
            ReadOnlySpan<char> span = text;
            if (!pre)
            {
                if (!_seen || _breaks > 0 || _tab || EndsWithSpace())
                {
                    span = span.TrimStart(' ');
                }

                if (span.IsEmpty)
                {
                    return;
                }
            }

            if (_breaks > 0)
            {
                if (!_lastPre)
                {
                    TrimTrailingSpaces();
                }

                _result.Append('\n', _breaks);
            }
            else if (_tab)
            {
                if (!_lastPre)
                {
                    TrimTrailingSpaces();
                }

                _result.Append('\t');
            }

            _breaks = 0;
            _forced = 0;
            _tab = false;
            _result.Append(span);
            _lastPre = pre;
            _seen = true;
        }

        public string Finish()
        {
            if (!_lastPre)
            {
                TrimTrailingSpaces();
            }

            return _result.ToString();
        }

        private bool EndsWithSpace() => _result.Length > 0 && _result[^1] == ' ';

        private void TrimTrailingSpaces()
        {
            int end = _result.Length;
            while (end > 0 && _result[end - 1] == ' ')
            {
                end--;
            }

            _result.Length = end;
        }
    }
}
