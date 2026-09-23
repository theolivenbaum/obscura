// Nested and sibling inline boxes in one inline formatting context: layout, paint and the
// animation start-candidate walk were O(depth) or O(events) per line, per break candidate, per
// fragment or per glyph, so 1200 nested bordered spans took minutes and 5000 sibling spans in
// one paragraph more than a minute and a half. These pin the complexity with a generous time
// bound, and pin that the rewrite left geometry and pixels bit-identical to the scan it
// replaced.
using System.Diagnostics;
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class InlineNestingPerformanceTests
{
    private const string Host = "font:16px/20px 'Liberation Serif'";

    /// <summary>
    /// A block holding <paramref name="count"/> inline elements, each with the text "x ", nested
    /// one inside the next or all siblings, built through the DOM the way a script builds them.
    /// </summary>
    private static (DomTree Tree, List<NodeId> Spans) Build(
        int count,
        bool nested,
        Func<int, string> tag,
        Func<int, string> style,
        Func<int, string>? text = null,
        string hostStyle = Host)
    {
        DomTree tree = HtmlParsing.ParseHtml(
            $"<!doctype html><html><body style=\"margin:8px\"><div id=\"host\" style=\"{hostStyle}\"></div></body></html>");
        NodeId host = tree.GetElementById("host")!.Value;
        NodeId parent = host;
        List<NodeId> spans = [];
        for (int i = 0; i < count; i++)
        {
            NodeId element = tree.NewNode(NodeData.Element(QualName.Html(tag(i))));
            string css = style(i);
            if (css.Length > 0)
            {
                tree.GetNode(element)!.SetAttribute("style", css);
            }

            tree.AppendText(element, text?.Invoke(i) ?? "x ");
            tree.AppendChild(nested ? parent : host, element);
            spans.Add(element);
            parent = element;
        }

        return (tree, spans);
    }

    private static TimeSpan Time(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed;
    }

    /// <summary>
    /// The bound is two orders of magnitude above what these take now (well under a second
    /// each) and far below what they took before, so a loaded machine does not make it flaky
    /// and a regression to per-line or per-fragment O(n) work still fails it.
    /// </summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public void DeeplyNestedBorderedSpansLayOutInLinearTime()
    {
        (DomTree tree, List<NodeId> spans) = Build(2000, nested: true, _ => "span", _ => "border:1px solid red");
        DomLayout? laid = null;
        TimeSpan elapsed = Time(() => StackGuard.RunWithStackFor(tree, () =>
        {
            laid = RenderDom.LayoutDom(tree, (1280f, 720f));
            return RenderPaint.PaintDom(tree, (1280f, 720f), null);
        }));
        Assert.True(elapsed < Bound, $"2000 nested bordered spans took {elapsed}");
        Assert.True(laid!.InlineFragments.TryGetValue(spans[^1], out List<Rect>? innermost) && innermost.Count > 0);
    }

    [Fact]
    public void DeeplyNestedPaddedSpansLayOutInLinearTime()
    {
        (DomTree tree, List<NodeId> spans) = Build(2000, nested: true, _ => "span", _ => "padding:0 3px;margin:0 2px");
        TimeSpan elapsed = Time(() => StackGuard.RunWithStackFor(tree, () => RenderDom.LayoutDom(tree, (1280f, 720f))));
        Assert.True(elapsed < Bound, $"2000 nested padded spans took {elapsed}");
    }

    [Fact]
    public void DeeplyNestedPhrasingChainsLayOutInLinearTime()
    {
        string[] tags = ["b", "i", "a"];
        (DomTree tree, _) = Build(3000, nested: true, i => tags[i % 3], _ => "");
        TimeSpan elapsed = Time(() => StackGuard.RunWithStackFor(tree, () => RenderDom.LayoutDom(tree, (1280f, 720f))));
        Assert.True(elapsed < Bound, $"3000 nested b/i/a took {elapsed}");
    }

    [Fact]
    public void AWideParagraphOfSiblingBorderedSpansLaysOutAndPaintsInLinearTime()
    {
        (DomTree tree, List<NodeId> spans) = Build(20000, nested: false, _ => "span", _ => "border:1px solid red");
        DomLayout? laid = null;
        TimeSpan elapsed = Time(() =>
        {
            laid = RenderDom.LayoutDom(tree, (1280f, 720f));
            _ = RenderPaint.PaintDom(tree, (1280f, 720f), null);
        });
        Assert.True(elapsed < Bound, $"20000 sibling bordered spans took {elapsed}");
        Assert.True(laid!.InlineFragments.TryGetValue(spans[^1], out List<Rect>? last) && last.Count == 1);
    }

    [Fact]
    public void ALongParagraphWithLinksLaysOutInLinearTime()
    {
        // One inline owner is enough to send a paragraph through the per-line probe, which used
        // to reshape the rest of the paragraph for every line it broke.
        (DomTree tree, _) = Build(
            12000,
            nested: false,
            i => i % 20 == 0 ? "a" : "span",
            _ => "",
            i => $"word{i % 97} ");
        TimeSpan elapsed = Time(() => RenderDom.LayoutDom(tree, (1280f, 720f)));
        Assert.True(elapsed < Bound, $"a 12000-word paragraph took {elapsed}");
    }

    [Fact]
    public void MaterializingStartCandidatesOfANestedChainIsLinear()
    {
        (DomTree tree, List<NodeId> spans) = Build(20000, nested: true, _ => "span", _ => "");
        var timeline = new AnimationTimelineState();
        for (int i = 0; i < spans.Count; i++)
        {
            timeline.NoteSubtreeStartCandidate(spans[i], i);
        }

        // Linear, this is milliseconds; walking every root's subtree took about half a minute.
        TimeSpan elapsed = Time(() => timeline.MaterializeStartCandidates(tree));
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"20000 nested start candidates took {elapsed}");

        // Each root keeps its own time; a descendant keeps the first time that reached it,
        // which for a chain noted outermost first is the outermost root's.
        Assert.True(timeline.TryGetStartCandidate(spans[0], out float outer));
        Assert.Equal(0f, outer);
        Assert.True(timeline.TryGetStartCandidate(spans[^1], out float inner));
        Assert.Equal(spans.Count - 1, inner);
        NodeId text = tree.GetNode(spans[^1])!.FirstChild!.Value;
        Assert.True(timeline.TryGetStartCandidate(text, out float leaf));
        Assert.Equal(0f, leaf);
    }

    [Fact]
    public void StartCandidatesOfAChainNotedInnermostFirstKeepTheInnermostTimes()
    {
        (DomTree tree, List<NodeId> spans) = Build(50, nested: true, _ => "span", _ => "");
        var timeline = new AnimationTimelineState();
        for (int i = spans.Count - 1; i >= 0; i--)
        {
            timeline.NoteSubtreeStartCandidate(spans[i], 100 - i);
        }

        timeline.MaterializeStartCandidates(tree);
        for (int i = 0; i < spans.Count; i++)
        {
            Assert.True(timeline.TryGetStartCandidate(spans[i], out float value));
            Assert.Equal(100 - i, value);
        }

        NodeId text = tree.GetNode(spans[^1])!.FirstChild!.Value;
        Assert.True(timeline.TryGetStartCandidate(text, out float leaf));
        Assert.Equal(100 - (spans.Count - 1), leaf);
    }

    /// <summary>FNV-1a over the exact bits of every fragment of every element, in order.</summary>
    private static (int Count, uint Hash) Fingerprint(DomLayout laid, List<NodeId> spans)
    {
        uint hash = 2166136261;
        int count = 0;
        void Mix(float value)
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash = (hash ^ (uint)((bits >> shift) & 0xff)) * 16777619;
            }
        }

        foreach (NodeId span in spans)
        {
            if (!laid.InlineFragments.TryGetValue(span, out List<Rect>? fragments))
            {
                Mix(float.NaN);
                continue;
            }

            foreach (Rect rect in fragments)
            {
                Mix(rect.X);
                Mix(rect.Y);
                Mix(rect.Width);
                Mix(rect.Height);
                count++;
            }
        }

        return (count, hash);
    }

    private static uint PixelHash(Pixmap pixmap)
    {
        uint hash = 2166136261;
        foreach (PremultipliedColor pixel in pixmap.Pixels)
        {
            hash = (hash ^ pixel.R) * 16777619;
            hash = (hash ^ pixel.G) * 16777619;
            hash = (hash ^ pixel.B) * 16777619;
            hash = (hash ^ pixel.A) * 16777619;
        }

        return hash;
    }

    /// <summary>
    /// 40 nested spans with em-sized padding (4.8px, not a short binary fraction, so any
    /// reassociation of the f32 edge sums shows) in a narrow centred block: enough events for
    /// the cached sums, and several wrapped lines. The expected values were taken from the scan
    /// the cache replaced, before the change.
    /// </summary>
    [Fact]
    public void NestedEdgeGeometryAndPixelsAreUnchanged()
    {
        (DomTree tree, List<NodeId> spans) = Build(
            40,
            nested: true,
            _ => "span",
            i => i % 5 == 0 ? "border:1px solid red;padding:0 .3em;margin-left:-.1em" : "border-left:.15em solid blue",
            i => $"w{i} ",
            Host + ";width:300px;text-align:center");
        DomLayout laid = RenderDom.LayoutDom(tree, (400f, 400f));
        Assert.Equal((132, 913039963u), Fingerprint(laid, spans));
        Assert.Equal(836482515u, PixelHash(RenderPaint.PaintDom(tree, (400f, 400f), null)!));
    }

    /// <summary>
    /// A 7000-character paragraph of sibling spans with em-sized edges, negative margins and
    /// relative offsets: it takes the windowed first-line probe, the cached edge sums and the
    /// per-run relative ranges. Expected values from the engine before the change.
    /// </summary>
    [Fact]
    public void SiblingParagraphGeometryAndPixelsAreUnchanged()
    {
        (DomTree tree, List<NodeId> spans) = Build(
            400,
            nested: false,
            i => i % 9 == 0 ? "a" : "span",
            i => (i % 7) switch
            {
                0 => "border:1px solid red;padding:0 .25em",
                1 => "margin-left:-2.3px;background:#ddd",
                2 => "position:relative;top:1px;left:.2em;border-bottom:1px solid",
                _ => "padding-left:1.7px",
            },
            i => $"lorem{i} ipsum ",
            Host + ";width:500px;text-align:justify");
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 800f));
        Assert.Equal((424, 4229691653u), Fingerprint(laid, spans));
        Assert.Equal(1543167602u, PixelHash(RenderPaint.PaintDom(tree, (600f, 800f), null)!));
    }
}
