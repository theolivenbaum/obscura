// SECURITY.md M7: one 5 MB word laid out took 2.9 GB. The glyph lists grew by doubling
// and the word cache kept a second copy of every glyph, about 710 bytes per character.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;

namespace PocketCalculator.Render.Tests;

public class LongWordLayoutMemoryTests
{
    [Fact]
    public void ALongWordAllocatesAboutOneCopyOfItsGlyphs()
    {
        const int Length = 256 * 1024;
        DomTree tree = HtmlParsing.ParseHtml("<body>" + new string('x', Length) + "</body>");
        long before = GC.GetAllocatedBytesForCurrentThread();
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        long perChar = (GC.GetAllocatedBytesForCurrentThread() - before) / Length;

        Assert.True(laid.Rects.ContainsKey(tree.QuerySelector("body")!.Value));
        // One ShapeGlyph (96 bytes) and one LayoutGlyph (120 bytes) per glyph, plus the
        // transient break and HarfBuzz tables: about 350. It was about 710.
        Assert.True(perChar < 450, $"{perChar} bytes per character");
    }

    [Fact]
    public void ALongWordStillOverflowsOnOneLine()
    {
        DomTree tree = HtmlParsing.ParseHtml("<body><p>" + new string('x', 20_000) + "</p></body>");
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        var p = laid.Rects[tree.QuerySelector("p")!.Value];
        Assert.Equal(18f, p.Height);
    }
}
