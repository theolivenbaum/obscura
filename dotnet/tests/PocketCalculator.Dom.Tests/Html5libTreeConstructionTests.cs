// SECURITY.md M11: the tree builder is written here, so the html5lib corpus is what says it is
// the specification's. No counterpart in crates/obscura-dom, which uses html5ever's.
using System.Runtime.CompilerServices;
using System.Text;

using PocketCalculator.Dom;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// The html5lib-tests tree construction corpus, copied verbatim from
/// https://github.com/html5lib/html5lib-tests at <c>9329e64</c>, the last commit before the
/// corpus moved to web-platform-tests (<c>html/syntax/parsing</c>). MIT licensed; see the
/// LICENSE file beside it. Every case not marked <c>#script-off</c> runs, since the engine
/// parses with scripting enabled.
/// </summary>
public class Html5libTreeConstructionTests
{
    /// <summary>
    /// The cases that fail, by file and position in it, with the reason. A case here that starts
    /// passing fails the test, so the list stays exact.
    /// </summary>
    private static readonly Dictionary<string, string> KnownFailures = new(StringComparer.Ordinal)
    {
        // AngleSharp's tokenizer drops U+0000 in the data state instead of emitting it, so
        // foreign content has nothing to turn into U+FFFD.
        ["plain-text-unsafe.dat:14"] = "tokenizer drops NUL",
        ["plain-text-unsafe.dat:15"] = "tokenizer drops NUL",
        ["plain-text-unsafe.dat:16"] = "tokenizer drops NUL",
        ["plain-text-unsafe.dat:17"] = "tokenizer drops NUL",
        ["plain-text-unsafe.dat:20"] = "tokenizer drops NUL",

        // <selectedcontent> mirrors the selected option's content; that is DOM behaviour
        // (option insertion steps), not tree construction.
        ["webkit02.dat:44"] = "selectedcontent",
        ["webkit02.dat:45"] = "selectedcontent",
        ["webkit02.dat:46"] = "selectedcontent",
        ["webkit02.dat:47"] = "selectedcontent",
    };

    private static string CorpusDirectory([CallerFilePath] string path = "") =>
        Path.Combine(Path.GetDirectoryName(path)!, "Fixtures", "html5lib-tree-construction");

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(CorpusDirectory(), "*.dat").OrderBy(f => f, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void CorpusFileParsesAsExpected(string file)
    {
        // A file may hold only #script-off cases (noscript01.dat); TheWholeCorpusRuns checks
        // that the corpus as a whole is found.
        var failures = new StringBuilder();
        foreach (var testCase in Load(Path.Combine(CorpusDirectory(), file)))
        {
            if (testCase.ScriptOff)
            {
                continue;
            }

            var key = file + ":" + testCase.Index;
            var actual = Serialize(Parse(testCase), testCase.Fragment is not null);
            var passed = string.Equals(actual, testCase.Expected, StringComparison.Ordinal);
            var known = KnownFailures.ContainsKey(key);
            if (passed == known)
            {
                failures.Append("### ").Append(key)
                    .Append(known ? " passes now; remove it from KnownFailures" : "")
                    .Append('\n').Append(testCase.Data)
                    .Append("\n--- expected\n").Append(testCase.Expected)
                    .Append("\n--- actual\n").Append(actual).Append("\n\n");
            }
        }

        Assert.True(failures.Length == 0, failures.ToString());
    }

    [Fact]
    public void TheWholeCorpusRuns()
    {
        var total = 0;
        foreach (var file in Directory.GetFiles(CorpusDirectory(), "*.dat"))
        {
            total += Load(file).Count(c => !c.ScriptOff);
        }

        Assert.True(total >= 1700, $"only {total} cases found");
    }

    // ------------------------------------------------------------------ the .dat format

    private sealed record Case(int Index, string Data, string? Fragment, bool ScriptOff, string Expected);

    private static List<Case> Load(string path)
    {
        var cases = new List<Case>();
        var lines = File.ReadAllText(path).Split('\n');
        var i = 0;
        var index = 0;
        while (i < lines.Length)
        {
            if (lines[i] != "#data")
            {
                i++;
                continue;
            }

            i++;
            var data = new List<string>();
            while (i < lines.Length && !lines[i].StartsWith('#'))
            {
                data.Add(lines[i++]);
            }

            string? fragment = null;
            var scriptOff = false;
            while (i < lines.Length && lines[i] != "#document")
            {
                if (lines[i] == "#document-fragment")
                {
                    fragment = lines[i + 1];
                    i += 2;
                    continue;
                }

                if (lines[i] == "#script-off")
                {
                    scriptOff = true;
                }

                i++;
            }

            i++;
            var expected = new List<string>();
            while (i < lines.Length && lines[i] != "#data")
            {
                expected.Add(lines[i++]);
            }

            while (expected.Count > 0 && expected[^1].Length == 0)
            {
                expected.RemoveAt(expected.Count - 1);
            }

            cases.Add(new Case(index++, string.Join("\n", data), fragment, scriptOff, string.Join("\n", expected)));
        }

        return cases;
    }

    private static DomTree Parse(Case testCase)
    {
        if (testCase.Fragment is not { } fragment)
        {
            return HtmlParsing.ParseHtml(testCase.Data);
        }

        var parts = fragment.Split(' ');
        var context = parts.Length == 2
            ? new QualName(null, parts[0] == "svg" ? Namespaces.Svg : Namespaces.MathMl, parts[1])
            : QualName.Html(parts[0]);
        return HtmlParsing.ParseFragmentWithContext(testCase.Data, context);
    }

    /// <summary>The html5lib tree dump format.</summary>
    private static string Serialize(DomTree tree, bool fragment)
    {
        var sb = new StringBuilder();
        var root = fragment ? tree.FragmentRoot() : tree.Document;
        foreach (var child in tree.Children(root))
        {
            Write(tree, child, 0, sb);
        }

        if (sb.Length > 0 && sb[^1] == '\n')
        {
            sb.Length--;
        }

        return sb.ToString();
    }

    private static void Write(DomTree tree, NodeId id, int depth, StringBuilder sb)
    {
        var node = tree.GetNode(id)!;
        var indent = "| " + new string(' ', depth * 2);
        switch (node.Data)
        {
            case DoctypeData doctype:
                sb.Append(indent).Append("<!DOCTYPE ").Append(doctype.Name);
                if (doctype.PublicId.Length > 0 || doctype.SystemId.Length > 0)
                {
                    sb.Append(" \"").Append(doctype.PublicId).Append("\" \"").Append(doctype.SystemId).Append('"');
                }

                sb.Append(">\n");
                break;
            case CommentData comment:
                sb.Append(indent).Append("<!-- ").Append(comment.Contents).Append(" -->\n");
                break;
            case TextData text:
                sb.Append(indent).Append('"').Append(text.Contents).Append("\"\n");
                break;
            case ElementData element:
            {
                sb.Append(indent).Append('<');
                if (element.Name.Ns == Namespaces.Svg)
                {
                    sb.Append("svg ");
                }
                else if (element.Name.Ns == Namespaces.MathMl)
                {
                    sb.Append("math ");
                }

                sb.Append(element.Name.Local).Append(">\n");
                var attrs = new List<string>();
                foreach (var attr in element.Attrs)
                {
                    var prefix = attr.Name.Ns switch
                    {
                        Namespaces.XLink => "xlink ",
                        Namespaces.Xml => "xml ",
                        Namespaces.XmlNs => "xmlns ",
                        _ => "",
                    };
                    attrs.Add(prefix + attr.Name.Local + "=\"" + attr.Value + "\"");
                }

                attrs.Sort(StringComparer.Ordinal);
                foreach (var attr in attrs)
                {
                    sb.Append(indent).Append("  ").Append(attr).Append('\n');
                }

                if (element.TemplateContents is { } contents)
                {
                    sb.Append(indent).Append("  content\n");
                    foreach (var child in tree.Children(contents))
                    {
                        Write(tree, child, depth + 2, sb);
                    }
                }

                foreach (var child in tree.Children(id))
                {
                    Write(tree, child, depth + 1, sb);
                }

                break;
            }
        }
    }
}
