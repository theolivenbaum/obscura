using System.Globalization;
using System.Text;
using PocketCalculator.Dom;
using PocketCalculator.Render.Css;
using SkiaSharp;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// Randomized differential test for incremental relayout: random sequences of DOM mutations are
/// applied to fixture pages, each step is prepared incrementally (retained styles, carried-over
/// layout results) and from scratch with full relayout forced, and the two must agree bit for
/// bit in every computed style, every box rect and the painted pixels.
/// </summary>
/// <remarks>
/// The mutations are recorded exactly as the JS ops record them (RenderInvalidation): an
/// attribute the retained planner classifies Full, like any mutation the ops cannot describe,
/// drops the previous render, and the next step starts over from a full prepare.
/// </remarks>
public class IncrementalLayoutDifferentialTests
{
    private static readonly (float Width, float Height) Viewport = (480f, 360f);

    private const string SharedCss = """
        html,body{margin:0}
        body{font:14px/1.3 sans-serif;padding:6px}
        .a{padding:3px 7px;border:2px solid #333}
        .b{margin:4px 0 9px;background:#eef}
        .c{font-size:18px;letter-spacing:1px}
        .hide{display:none}
        .wide{width:70%}
        .pad{padding:5%}
        .flt{float:left;width:90px;height:30px;background:#fc9}
        .fltr{float:right;width:60px;background:#9cf}
        .abs{position:absolute;right:10px;top:20px;width:80px}
        .rel{position:relative;left:5px;top:3px}
        .flex{display:flex;gap:6px;flex-wrap:wrap}
        .grid{display:grid;grid-template-columns:1fr 2fr 80px;gap:4px}
        .big{min-height:40px;line-height:2}
        .inl{display:inline-block;width:50px;vertical-align:middle}
        .clr{clear:both}
        .ovf{overflow:auto;max-height:50px}
        .pre{white-space:pre}
        .nw{white-space:nowrap}
        .grow{flex:1 1 0}
        .cnt::before{content:counter(item) ". ";counter-increment:item}
        .aft::after{content:" [" attr(data-x) "]"}
        .tbl{display:table;border-spacing:3px}
        .cell{display:table-cell;padding:2px;border:1px solid}
        """;

    private static readonly string[] Fixtures =
    [
        // Block flow and inline formatting. Floats come and go through the class pool; a page
        // with a float carries nothing over (RetainedTaffyLayout.HadFloats), and the float
        // conformance pages are run below.
        """
        <!doctype html><html><head><style>{CSS}</style></head><body>
        <h1 id=h class=c>Heading <span>with span</span></h1>
        <p id=p1>Lorem ipsum <b>dolor</b> sit amet, <em class=rel>consectetur</em> adipiscing elit, sed do eiusmod tempor.</p>
        <div id=f1 class=b>block</div>
        <p id=p2 class=a>Text that wraps around the float and keeps wrapping for a while, more words here.</p>
        <div id=d1 class=b><span class=inl>ib</span> inline <a href=#x>link</a> tail text.</div>
        <div id=d2 class="clr wide">cleared <img id=img1 src="{IMG0}" alt="x"> after image</div>
        <ul id=ul><li id=li1>one</li><li id=li2>two two</li><li id=li3>three</li></ul>
        <p id=p3 class=pre>  pre   formatted
          line two</p>
        </body></html>
        """,

        // Flex rows, wrapping, growing items.
        """
        <!doctype html><html><head><style>{CSS}</style></head><body>
        <div id=row class=flex>
          <div id=x1 class="a grow">first item text</div>
          <div id=x2 class=a>second</div>
          <div id=x3 class="a grow">third item with more text inside</div>
          <img id=img1 src="{IMG1}" alt="">
        </div>
        <div id=col style="display:flex;flex-direction:column;align-items:flex-start">
          <div id=y1 class=b>column a</div><div id=y2 class=c>column b</div><div id=y3>column c</div>
        </div>
        <div id=nav class=flex><a id=n1 class=a>Home</a><a id=n2 class=a>About</a><a id=n3 class=a>Products</a><a id=n4 class=a>Contact us</a></div>
        <p id=tail>After the flex containers.</p>
        </body></html>
        """,

        // Grid with auto rows and spanning.
        """
        <!doctype html><html><head><style>{CSS}</style></head><body>
        <div id=g class=grid>
          <div id=g1 class=a>one</div><div id=g2>two with several words</div><div id=g3>3</div>
          <div id=g4 style="grid-column:span 2" class=b>four spans two columns</div><div id=g5 class=c>five</div>
          <div id=g6>six</div><div id=g7><img id=img1 src="{IMG2}" alt=""></div><div id=g8 class=pad>eight</div>
        </div>
        <section id=s><h2 id=h2>Section</h2><p id=sp>Paragraph in a section with text.</p></section>
        </body></html>
        """,

        // Positioned boxes, overflow, inline-blocks, counters, generated content.
        """
        <!doctype html><html><head><style>{CSS} ol{counter-reset:item;list-style:none}</style></head><body>
        <div id=wrap class=rel style="height:120px;border:1px solid">
          <div id=ab class=abs>absolute box</div>
          <div id=ov class=ovf><p>scroll 1</p><p>scroll 2</p><p>scroll 3</p></div>
        </div>
        <ol id=ol><li id=o1 class=cnt>alpha</li><li id=o2 class=cnt>beta</li><li id=o3 class=cnt>gamma</li></ol>
        <p id=ib><span id=s1 class=inl>a</span><span id=s2 class=inl>b</span><span id=s3 class="inl aft" data-x=1>c</span> text</p>
        <div id=fx style="position:fixed;bottom:4px;left:4px" class=a>fixed</div>
        </body></html>
        """,

        // Tables, nowrap, display:contents, right floats.
        """
        <!doctype html><html><head><style>{CSS}</style></head><body>
        <table id=t border=1><tr id=tr1><td id=c1>cell one</td><td id=c2 colspan=2>wide cell</td></tr>
        <tr id=tr2><td id=c3>a</td><td id=c4 class=nw>nowrap cell content here</td><td id=c5>z</td></tr></table>
        <div id=tb class=tbl><div class=cell id=k1>css cell</div><div class=cell id=k2>another css cell</div></div>
        <div id=dc style="display:contents"><p id=dcp>inside contents</p></div>
        <div id=fr class=a>right box</div>
        <p id=after>Paragraph after the right float, wrapping beside it for some time.</p>
        </body></html>
        """,

        // Form controls and nested flex in grid.
        """
        <!doctype html><html><head><style>{CSS}</style></head><body>
        <form id=fm class=grid>
          <label id=l1>Name <input id=i1 size=10></label>
          <button id=bt class=a>Press me</button>
          <textarea id=ta rows=2 cols=12>text</textarea>
          <div id=nf class=flex><span id=nf1>x</span><span id=nf2 class=c>y</span></div>
          <select id=sel><option>one</option><option>two longer</option></select>
        </form>
        <p id=q>Quote <q>inline quote</q> end.</p>
        </body></html>
        """,
    ];

    private static readonly string[] Classes =
        ["a", "b", "c", "hide", "wide", "pad", "flt", "fltr", "abs", "rel", "flex", "grid", "big", "inl", "clr", "ovf", "pre", "nw", "grow", "cnt", "aft"];

    private static readonly string[] Declarations =
    [
        "", "width:120px", "width:50%", "height:33px", "padding:7px", "margin-left:12px",
        "display:none", "display:inline", "display:block", "display:flex", "font-size:20px",
        "border:3px solid red", "max-width:100px", "min-height:25px", "position:relative;top:4px",
        "float:left;width:40px", "line-height:30px", "white-space:nowrap", "flex:2", "text-indent:10px",
        "box-sizing:border-box;width:100px;padding:10px", "transform:translateX(10px)", "visibility:hidden",
    ];

    private static readonly string[] Words =
        ["", "x", "short", "a few more words", "averyveryverylongwordwithoutbreaks", "two\nlines", "  spaced  ", "mixed Text 123"];

    private static readonly string[] Images = [PngDataUrl(10, 10), PngDataUrl(40, 20), PngDataUrl(16, 64), PngDataUrl(1, 1)];

    private static string PngDataUrl(int width, int height)
    {
        using SKBitmap bitmap = new(width, height);
        bitmap.Erase(new SKColor(200, 30, 30));
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
    }

    private static string Html(int fixture) =>
        Fixtures[fixture]
            .Replace("{CSS}", SharedCss, StringComparison.Ordinal)
            .Replace("{IMG0}", Images[0], StringComparison.Ordinal)
            .Replace("{IMG1}", Images[1], StringComparison.Ordinal)
            .Replace("{IMG2}", Images[2], StringComparison.Ordinal);

    public static TheoryData<int, int> Cases()
    {
        // POCKETCALCULATOR_DIFFERENTIAL_SEEDS raises the seed count for a long soak run.
        int seeds = int.TryParse(
            Environment.GetEnvironmentVariable("POCKETCALCULATOR_DIFFERENTIAL_SEEDS"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int requested) && requested > 0 ? requested : 6;
        TheoryData<int, int> data = [];

        // POCKETCALCULATOR_DIFFERENTIAL_ONLY=fixture:seed reruns one failing case.
        if (Environment.GetEnvironmentVariable("POCKETCALCULATOR_DIFFERENTIAL_ONLY") is { } only
            && only.Split(':') is [string f, string n]
            && int.TryParse(f, CultureInfo.InvariantCulture, out int onlyFixture)
            && int.TryParse(n, CultureInfo.InvariantCulture, out int onlySeed))
        {
            data.Add(onlyFixture, onlySeed);
            return data;
        }

        for (int fixture = 0; fixture < Fixtures.Length; fixture++)
        {
            for (int seed = 1; seed <= seeds; seed++)
            {
                data.Add(fixture, seed);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RandomMutationSequencesMatchAFullRelayout(int fixture, int seed)
    {
        Run(fixture, seed * 7919 + fixture, steps: 24, cancelAt: -1);
    }

    /// <summary>The float conformance pages (render-repros/floats), with the shared classes added.</summary>
    public static TheoryData<string, int> FloatCases()
    {
        TheoryData<string, int> data = [];
        if (FloatPagesDirectory() is not { } directory)
        {
            return data;
        }

        string? only = Environment.GetEnvironmentVariable("POCKETCALCULATOR_DIFFERENTIAL_ONLY_PAGE");
        int seeds = int.TryParse(
            Environment.GetEnvironmentVariable("POCKETCALCULATOR_DIFFERENTIAL_FLOAT_SEEDS"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int requested) && requested > 0 ? requested : 2;
        foreach (string file in Directory.GetFiles(directory, "*.html").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            if (only is not null && !string.Equals(only, name, StringComparison.Ordinal))
            {
                continue;
            }

            for (int seed = 1; seed <= seeds; seed++)
            {
                data.Add(name, seed);
            }
        }

        return data;
    }

    private static string? FloatPagesDirectory()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "render-repros", "floats");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [Theory]
    [MemberData(nameof(FloatCases))]
    public void RandomMutationSequencesOnFloatPagesMatchAFullRelayout(string page, int seed)
    {
        string html = File.ReadAllText(Path.Combine(FloatPagesDirectory()!, page))
            .Replace("</head>", "<style>" + SharedCss.Replace("html,body{margin:0}", "", StringComparison.Ordinal) + "</style></head>", StringComparison.Ordinal);
        Run(html, page, seed * 104729 + page.Length, steps: 16, cancelAt: -2);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 5)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 6)]
    [InlineData(5, 1)]
    public void ACancelledPassLeavesTheNextPassExact(int fixture, int cancelAt)
    {
        Run(fixture, 4242 + fixture, steps: cancelAt + 6, cancelAt);
    }

    [Fact]
    public void AMutationOfOneGridItemCarriesTheOtherItemsOver()
    {
        StringBuilder html = new("<!doctype html><html><head><style>" + SharedCss + "</style></head><body><div class=grid>");
        for (int i = 0; i < 60; i++)
        {
            html.Append(CultureInfo.InvariantCulture, $"<div id=g{i} class=a>item {i} text</div>");
        }

        html.Append("</div></body></html>");
        DomTree tree = HtmlParsing.ParseHtml(html.ToString());
        RenderResourceCache resources = new();
        StylesheetCache cache = new();
        PreparedRender first = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCache(tree, Viewport, null, resources, [], cache)!;
        NodeId target = tree.GetElementById("g7")!.Value;
        tree.GetNode(target)!.SetAttribute("class", "a c");
        PreparedRender second = RenderPaint.PrepareDomWithRetainedStyles(
            tree, Viewport, null, resources, [], cache, first,
            [RetainedStyleMutation.From(new AttributeStyleMutation(target, "class", "a", "a c"))])!;

        Assert.True(second.Layout.TransplantedBoxes > 50, $"carried {second.Layout.TransplantedBoxes}");
        Assert.True(second.Layout.AdoptedInlineItems > 50, $"adopted {second.Layout.AdoptedInlineItems}");
        Assert.Equal(Snapshot(tree, Reference(tree)), Snapshot(tree, second));
    }

    /// <summary>
    /// What a retained pass carries over is bounded: one previous layout's box tree and items,
    /// and nothing of the passes before it. A pass's engine that kept the one it took items from
    /// would chain every engine since the page loaded.
    /// </summary>
    [Fact]
    public void ARetainedPassDoesNotKeepEarlierPassesAlive()
    {
        DomTree tree = HtmlParsing.ParseHtml(Html(0));
        RenderResourceCache resources = new();
        StylesheetCache cache = new();
        (PreparedRender latest, WeakReference first) = Chain(tree, resources, cache);
        for (int attempt = 0; attempt < 3 && first.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(first.IsAlive, "the first pass's text engine is still reachable");
        Assert.True(latest.Layout.AdoptedInlineItems > 0);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (PreparedRender Latest, WeakReference First) Chain(DomTree tree, RenderResourceCache resources, StylesheetCache cache)
    {
        PreparedRender previous = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCache(tree, Viewport, null, resources, [], cache)!;
        WeakReference first = new(previous.Layout.TextEngine);
        NodeId target = tree.GetElementById("d1")!.Value;
        for (int i = 0; i < 4; i++)
        {
            string value = i % 2 == 0 ? "b c" : "b";
            string old = tree.GetNode(target)!.GetAttribute("class")!;
            tree.GetNode(target)!.SetAttribute("class", value);
            previous = RenderPaint.PrepareDomWithRetainedStyles(
                tree, Viewport, null, resources, [], cache, previous,
                [RetainedStyleMutation.From(new AttributeStyleMutation(target, "class", old, value))])!;
        }

        return (previous, first);
    }

    private static PreparedRender Reference(DomTree tree)
    {
        using IDisposable full = RetainedTaffyLayout.ForceFullRelayout();
        return RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCache(
            tree, Viewport, null, new RenderResourceCache(), [], new StylesheetCache())
            ?? throw new InvalidOperationException("reference did not prepare");
    }

    /// <summary>
    /// POCKETCALCULATOR_DIFFERENTIAL_FROM=k prepares every step before k in full, to find the
    /// shortest incremental history that still diverges.
    /// </summary>
    private static readonly int IncrementalFrom = int.TryParse(
        Environment.GetEnvironmentVariable("POCKETCALCULATOR_DIFFERENTIAL_FROM"),
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out int from) ? from : 0;

    private static void Run(int fixture, int seed, int steps, int cancelAt) =>
        Run(Html(fixture), fixture.ToString(CultureInfo.InvariantCulture), seed, steps, cancelAt);

    private static void Run(string html, string fixture, int seed, int steps, int cancelAt)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        Random rng = new(seed);
        RenderResourceCache resources = new();
        StylesheetCache cache = new();
        PreparedRender? previous = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCache(tree, Viewport, null, resources, [], cache);
        Assert.NotNull(previous);
        StringBuilder log = new();
        for (int step = 0; step < steps; step++)
        {
            List<RetainedStyleMutation> mutations = [];
            bool needsFull = false;
            int count = 1 + rng.Next(3);
            for (int i = 0; i < count; i++)
            {
                string? applied = Mutate(tree, rng, mutations, ref needsFull);
                if (applied is not null)
                {
                    log.Append(CultureInfo.InvariantCulture, $"step {step}: {applied}\n");
                }
            }

            PreparedRender? next;
            if (step == cancelAt && previous is not null && !needsFull)
            {
                // Cancel the incremental pass part-way, the way a watchdog does. RenderState
                // drops the previous render when a prepare throws, so the next step starts over
                // from a full prepare; nothing the cancelled pass touched may leak into it.
                bool cancelled = false;
                using (CancellationTokenSource source = new())
                using (Timer timer = new(_ => source.Cancel(), null, 1 + rng.Next(4), Timeout.Infinite))
                {
                    try
                    {
                        using (WorkCancellation.Enter(source.Token))
                        {
                            RenderPaint.PrepareDomWithRetainedStyles(tree, Viewport, null, resources, [], cache, previous, mutations);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                }

                // A pass that finished before the timer fired consumed the previous render as
                // any pass does; the step goes on from a full prepare either way.
                log.Append(CultureInfo.InvariantCulture, $"step {step}: cancelled={cancelled}\n");
                previous = null;
                cache = new StylesheetCache();
                next = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCache(tree, Viewport, null, resources, [], cache);
            }
            else if (needsFull || previous is null || step < IncrementalFrom)
            {
                next = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCache(tree, Viewport, null, resources, [], cache);
            }
            else
            {
                next = RenderPaint.PrepareDomWithRetainedStyles(tree, Viewport, null, resources, [], cache, previous, mutations);
            }

            Assert.NotNull(next);
            string incremental = Snapshot(tree, next);
            string reference = Snapshot(tree, Reference(tree));
            if (!string.Equals(incremental, reference, StringComparison.Ordinal))
            {
                if (Environment.GetEnvironmentVariable("POCKETCALCULATOR_DIFFERENTIAL_DUMP") is { } dumpDir)
                {
                    File.WriteAllText(Path.Combine(dumpDir, "incremental.txt"), next.Layout.RetainedBoxes?.Dump() ?? "");
                    File.WriteAllText(Path.Combine(dumpDir, "full.txt"), Reference(tree).Layout.RetainedBoxes?.Dump() ?? "");
                }

                Assert.Fail($"fixture {fixture} seed {seed} step {step} diverged from a full relayout\n"
                    + FirstDifference(reference, incremental) + "\nmutations:\n" + log);
            }

            previous = next;
        }
    }

    private static string FirstDifference(string expected, string actual)
    {
        string[] a = expected.Split('\n');
        string[] b = actual.Split('\n');
        StringBuilder report = new();
        int shown = 0;
        for (int i = 0; i < Math.Max(a.Length, b.Length) && shown < 12; i++)
        {
            string x = i < a.Length ? a[i] : "<end>";
            string y = i < b.Length ? b[i] : "<end>";
            if (!string.Equals(x, y, StringComparison.Ordinal))
            {
                string node = "";
                for (int j = i; j >= 0 && j < a.Length; j--)
                {
                    if (a[j].StartsWith('#'))
                    {
                        node = a[j];
                        break;
                    }
                }

                report.Append(CultureInfo.InvariantCulture, $"line {i} (node {node})\n  full:        {x}\n  incremental: {y}\n");
                shown++;
            }
        }

        return shown == 0 ? "(no line difference)" : report.ToString();
    }

    private static List<NodeId> Elements(DomTree tree)
    {
        List<NodeId> result = [];
        NodeId? body = tree.QuerySelector("body");
        if (body is not { } root)
        {
            return result;
        }

        foreach (NodeId id in tree.Descendants(root))
        {
            if (tree.GetNode(id)?.AsElement() is { } element
                && element.Name.Local is not ("script" or "style" or "option"))
            {
                result.Add(id);
            }
        }

        return result;
    }

    private static List<NodeId> TextNodes(DomTree tree)
    {
        List<NodeId> result = [];
        if (tree.QuerySelector("body") is not { } root)
        {
            return result;
        }

        foreach (NodeId id in tree.Descendants(root))
        {
            if (tree.GetNode(id)?.Data is TextData
                && tree.GetNode(id)?.Parent is { } parent
                && tree.GetNode(parent)?.AsElement()?.Name.Local is not ("style" or "script" or "textarea" or "option"))
            {
                result.Add(id);
            }
        }

        return result;
    }

    private static string? SetAttribute(DomTree tree, NodeId node, string name, string? value, List<RetainedStyleMutation> mutations, ref bool needsFull)
    {
        Node element = tree.GetNode(node)!;
        string? old = element.GetAttribute(name);
        if (string.Equals(old, value, StringComparison.Ordinal))
        {
            return null;
        }

        if (RetainedStylePlanner.RetainedAttributeMutationKindOf(tree, node, name) == RetainedAttributeMutationKind.Full)
        {
            needsFull = true;
        }

        bool keepsValue = !name.Equals("style", StringComparison.OrdinalIgnoreCase);
        mutations.Add(RetainedStyleMutation.From(new AttributeStyleMutation(
            node, name, keepsValue ? old : null, keepsValue ? value : null)));
        if (value is null)
        {
            element.RemoveAttribute(name);
        }
        else
        {
            element.SetAttribute(name, value);
        }

        return $"attr #{node} {name}={value ?? "<removed>"}";
    }

    private static string? Mutate(DomTree tree, Random rng, List<RetainedStyleMutation> mutations, ref bool needsFull)
    {
        List<NodeId> elements = Elements(tree);
        if (elements.Count == 0)
        {
            return null;
        }

        NodeId target = elements[rng.Next(elements.Count)];
        switch (rng.Next(10))
        {
            case 0:
            case 1:
            {
                string cls = Classes[rng.Next(Classes.Length)];
                string current = tree.GetNode(target)!.GetAttribute("class") ?? "";
                List<string> list = [.. current.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
                if (!list.Remove(cls))
                {
                    list.Add(cls);
                }

                return SetAttribute(tree, target, "class", list.Count == 0 ? null : string.Join(' ', list), mutations, ref needsFull);
            }

            case 2:
            case 3:
            {
                string declaration = Declarations[rng.Next(Declarations.Length)];
                return SetAttribute(tree, target, "style", declaration.Length == 0 ? null : declaration, mutations, ref needsFull);
            }

            case 4:
            {
                List<NodeId> texts = TextNodes(tree);
                if (texts.Count == 0)
                {
                    return null;
                }

                NodeId text = texts[rng.Next(texts.Count)];
                string words = Words[rng.Next(Words.Length)];
                if (tree.GetNode(text)?.Data is not TextData data)
                {
                    return null;
                }

                mutations.Add(RetainedStyleMutation.From(new TreeStyleMutation.Text(text, tree.GetNode(text)!.Parent)));
                data.Contents = words;
                return $"text #{text} = {words.Replace("\n", "\\n", StringComparison.Ordinal)}";
            }

            case 5:
            {
                // Insert a small detached subtree, then attach it once.
                string tag = rng.Next(4) switch { 0 => "div", 1 => "span", 2 => "p", _ => "img" };
                NodeId created = tree.NewNode(NodeData.Element(QualName.Html(tag)));
                if (tag == "img")
                {
                    tree.GetNode(created)!.SetAttribute("src", Images[rng.Next(Images.Length)]);
                }
                else
                {
                    if (rng.Next(2) == 0)
                    {
                        tree.GetNode(created)!.SetAttribute("class", Classes[rng.Next(Classes.Length)]);
                    }

                    NodeId text = tree.NewNode(new TextData(Words[1 + rng.Next(Words.Length - 1)]));
                    tree.AppendChild(created, text);
                }

                NodeId parent = target;
                if (tree.GetNode(parent)?.AsElement()?.Name.Local is "img" or "input" or "textarea" or "select" or "br")
                {
                    parent = tree.GetNode(parent)!.Parent!.Value;
                }

                List<NodeId> children = tree.Children(parent);
                mutations.Add(RetainedStyleMutation.From(new TreeStyleMutation.Insert(created, null, parent)));
                if (children.Count == 0 || rng.Next(2) == 0)
                {
                    tree.AppendChild(parent, created);
                }
                else
                {
                    tree.InsertBefore(children[rng.Next(children.Count)], created);
                }

                return $"insert <{tag}> #{created} into #{parent}";
            }

            case 6:
            {
                if (tree.GetNode(target)?.Parent is not { } parent)
                {
                    return null;
                }

                mutations.Add(RetainedStyleMutation.From(new TreeStyleMutation.Remove(target, parent)));
                tree.RemoveChild(target);
                return $"remove #{target}";
            }

            case 7:
            {
                // Move an element under another one that is not inside it.
                NodeId destination = elements[rng.Next(elements.Count)];
                if (destination == target
                    || tree.Ancestors(destination).Contains(target)
                    || tree.GetNode(destination)?.AsElement()?.Name.Local is "img" or "input" or "textarea" or "select" or "br"
                    || tree.GetNode(target)?.Parent is not { } oldParent)
                {
                    return null;
                }

                mutations.Add(RetainedStyleMutation.From(new TreeStyleMutation.Insert(target, oldParent, destination)));
                tree.AppendChild(destination, target);
                return $"move #{target} to #{destination}";
            }

            case 8:
            {
                List<NodeId> images = elements.FindAll(id => tree.GetNode(id)?.AsElement()?.Name.Local == "img");
                if (images.Count == 0)
                {
                    return null;
                }

                return SetAttribute(tree, images[rng.Next(images.Count)], "src", Images[rng.Next(Images.Length)], mutations, ref needsFull);
            }

            default:
            {
                string name = rng.Next(6) switch
                {
                    0 => "hidden",
                    1 => "data-x",
                    2 => "title",
                    3 => "dir",
                    4 => "colspan",
                    _ => "size",
                };
                string? value = rng.Next(3) == 0 ? null : name switch
                {
                    "dir" => rng.Next(2) == 0 ? "rtl" : "ltr",
                    "colspan" or "size" => (1 + rng.Next(3)).ToString(CultureInfo.InvariantCulture),
                    _ => "v" + rng.Next(3).ToString(CultureInfo.InvariantCulture),
                };
                return SetAttribute(tree, target, name, value, mutations, ref needsFull);
            }
        }
    }

    private static string F(float value) =>
        BitConverter.SingleToInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture);

    private static string R(Rect rect) => $"{F(rect.X)},{F(rect.Y)},{F(rect.Width)},{F(rect.Height)}";

    /// <summary>
    /// Everything a page can observe of a prepared render, in document order: computed style,
    /// border box (snapped and LayoutUnit-precise), inline fragments, text runs, generated
    /// boxes, content size and the painted pixels.
    /// </summary>
    internal static string Snapshot(DomTree tree, PreparedRender prepared)
    {
        DomLayout layout = prepared.Layout;
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"content {F(prepared.ContentSize().Width)} {F(prepared.ContentSize().Height)}\n");
        foreach (NodeId id in tree.Descendants(tree.Document))
        {
            text.Append(CultureInfo.InvariantCulture, $"#{id}");
            if (layout.Rects.TryGetValue(id, out Rect rect))
            {
                text.Append(" rect ").Append(R(rect));
            }

            if (layout.PreciseRect(id) is { } precise)
            {
                text.Append(" precise ").Append(R(precise));
            }

            if (layout.InlineFragments.TryGetValue(id, out List<Rect>? fragments))
            {
                text.Append(" frags");
                foreach (Rect fragment in fragments)
                {
                    text.Append(' ').Append(R(fragment));
                }
            }

            if (layout.TextRuns.TryGetValue(id, out List<(Rect Rect, string Text)>? runs))
            {
                text.Append(" runs");
                foreach ((Rect runRect, string runText) in runs)
                {
                    text.Append(' ').Append(R(runRect)).Append('=').Append(runText);
                }
            }

            if (layout.SvgRects.TryGetValue(id, out Rect svg))
            {
                text.Append(" svg ").Append(R(svg));
            }

            text.Append('\n');
            if (tree.GetNode(id)?.IsElement == true && prepared.ComputedStyle(id) is { } computed)
            {
                foreach ((string name, string value) in computed.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    text.Append("  ").Append(name).Append(": ").Append(value).Append('\n');
                }
            }
        }

        foreach (GeneratedBox box in layout.GeneratedBoxes)
        {
            text.Append(CultureInfo.InvariantCulture, $"generated #{box.Host} {box.Kind} {R(box.Rect)}\n");
        }

        using Pixmap? pixmap = RenderPaint.PaintPrepared(tree, prepared, new RenderResourceCache(), (0f, 0f));
        if (pixmap is not null)
        {
            byte[] pixels = pixmap.Data();
            text.Append("pixels ").Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels))).Append('\n');
        }

        return text.ToString();
    }
}
