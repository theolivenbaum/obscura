using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// SECURITY.md M7: DOM text and attributes live outside the V8 heap cap, so a loop appending
/// 4 MB text nodes grew the process past 3 GB. Each tree now has a byte budget, charged in
/// O(1) where data is created or changed.
/// </summary>
public sealed class DomByteBudgetTests
{
    [Fact]
    public void NodesAreChargedTheirTextAndAttributes()
    {
        var tree = new DomTree();
        long start = tree.ContentBytes;
        tree.NewNode(NodeData.Text(new string('x', 1000)));
        Assert.Equal(start + DomTree.NodeOverheadBytes + 2000, tree.ContentBytes);

        var element = tree.NewNode(NodeData.Element(
            QualName.Html("div"), [new Attribute(QualName.Attr("title"), "hello")]));
        Assert.Equal(start + (2 * DomTree.NodeOverheadBytes) + 2000 + (2 * ("title".Length + "hello".Length)), tree.ContentBytes);

        tree.AppendChild(tree.Document, element);
        tree.AppendText(element, "abc");
        tree.AppendText(element, "de");
        Assert.Equal(start + (3 * DomTree.NodeOverheadBytes) + 2000 + 20 + 10, tree.ContentBytes);
    }

    [Fact]
    public void GrowthPastTheBudgetThrowsAndChangesNothing()
    {
        var tree = new DomTree { ContentByteBudget = 64 * 1024 };
        var body = tree.NewNode(NodeData.Element(QualName.Html("body")));
        tree.AppendChild(tree.Document, body);

        int appended = 0;
        var error = Assert.Throws<DomQuotaExceededException>(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                tree.AppendChild(body, tree.NewNode(NodeData.Text(new string('x', 4096))));
                appended++;
            }
        });

        Assert.InRange(appended, 1, 7);
        Assert.Equal(appended, tree.Children(body).Count);
        Assert.True(tree.ContentBytes <= tree.ContentByteBudget);
        Assert.Equal(64 * 1024, error.Budget);

        long before = tree.ContentBytes;
        Assert.Throws<DomQuotaExceededException>(() => tree.AppendText(body, new string('y', 64 * 1024)));
        Assert.Equal(before, tree.ContentBytes);
        Assert.DoesNotContain('y', tree.TextContent(body));
    }

    [Fact]
    public void FreeingNodesGivesTheirBytesBack()
    {
        var tree = new DomTree { ContentByteBudget = 64 * 1024 };
        var body = tree.NewNode(NodeData.Element(QualName.Html("body")));
        tree.AppendChild(tree.Document, body);
        long empty = tree.ContentBytes;
        var text = tree.NewNode(NodeData.Text(new string('x', 20_000)));
        tree.AppendChild(body, text);
        Assert.Throws<DomQuotaExceededException>(() => tree.NewNode(NodeData.Text(new string('x', 20_000))));

        tree.Remove(text);
        Assert.Equal(empty, tree.ContentBytes);
        tree.AppendChild(body, tree.NewNode(NodeData.Text(new string('x', 20_000))));
    }

    [Fact]
    public void ChargedChangesAreCheckedBeforeTheyHappen()
    {
        var tree = new DomTree { ContentByteBudget = 10_000 };
        tree.ChargeGrowth(9_000);
        Assert.Throws<DomQuotaExceededException>(() => tree.ChargeGrowth(2_000));
        Assert.Equal(9_000, tree.ContentBytes);
        tree.ChargeGrowth(-5_000);
        tree.ChargeGrowth(2_000);
        Assert.Equal(6_000, tree.ContentBytes);
    }

    [Fact]
    public void ParsingPastTheBudgetKeepsWhatFits()
    {
        var html = "<body>" + string.Concat(Enumerable.Repeat("<p>" + new string('x', 1000) + "</p>", 200)) + "</body>";
        var tree = HtmlParsing.ParseHtml(html, 64 * 1024);

        Assert.True(tree.ParseTruncated);
        Assert.True(tree.ContentBytes <= 64 * 1024);
        int paragraphs = tree.QuerySelectorAll("p").Count;
        Assert.InRange(paragraphs, 10, 40);

        var whole = HtmlParsing.ParseHtml(html);
        Assert.False(whole.ParseTruncated);
        Assert.Equal(200, whole.QuerySelectorAll("p").Count);
    }

    [Fact]
    public void ADefaultBudgetIsSet()
    {
        Assert.True(DomTree.DefaultContentByteBudget > 0);
        Assert.Equal(DomTree.DefaultContentByteBudget, new DomTree().ContentByteBudget);
    }
}
