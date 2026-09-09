using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Cli.Commands;
using Obscura.Dom;
using Obscura.Js.Url;
using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>
/// The readable-text and asset-extraction tests from <c>main.rs</c>'s
/// <c>mod tests</c>.
/// </summary>
public sealed class DumpExtractorsTests
{
    private static string BodyText(string html)
    {
        var dom = HtmlParsing.ParseHtml(html);
        Assert.True(dom.TryQuerySelector("body", out var body, out _));
        Assert.NotNull(body);
        return string.Join(
            ' ',
            DumpExtractors.ExtractReadableText(dom, body!.Value)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Skips_nav_header_footer_aside()
    {
        var text = BodyText(
            """
            <html><body>
                <header>SITE HEADER</header>
                <nav>NAV LINKS</nav>
                <aside>SIDEBAR</aside>
                <main><p>Article body.</p></main>
                <footer>FOOTER</footer>
            </body></html>
            """);
        Assert.Contains("Article body.", text, StringComparison.Ordinal);
        foreach (var boilerplate in new[] { "SITE HEADER", "NAV LINKS", "SIDEBAR", "FOOTER" })
        {
            Assert.DoesNotContain(boilerplate, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Still_skips_script_and_style()
    {
        // Regression guard for the original skip list.
        var text = BodyText(
            """
            <html><body>
                <p>Hello.</p>
                <script>console.log("nope")</script>
                <style>.x { color: red }</style>
            </body></html>
            """);
        Assert.Contains("Hello.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("console.log", text, StringComparison.Ordinal);
        Assert.DoesNotContain("color: red", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_asset_url_keeps_absolute_unchanged()
    {
        var baseUrl = UrlRecord.Parse("https://page.test/a/b");
        const string abs = "https://cdn.test/x.js";
        Assert.Equal(abs, DumpExtractors.ResolveAssetUrl(abs, baseUrl));
    }

    [Fact]
    public void Resolve_asset_url_joins_relative_against_base()
    {
        var baseUrl = UrlRecord.Parse("https://page.test/a/b");
        Assert.Equal(
            "https://page.test/static/x.js",
            DumpExtractors.ResolveAssetUrl("/static/x.js", baseUrl));
    }

    [Fact]
    public void Resolve_asset_url_drops_empty()
    {
        var baseUrl = UrlRecord.Parse("https://page.test/");
        Assert.Null(DumpExtractors.ResolveAssetUrl(string.Empty, baseUrl));
        Assert.Null(DumpExtractors.ResolveAssetUrl("   ", baseUrl));
    }

    [Fact]
    public void Link_kind_from_rel_handles_common_values()
    {
        Assert.Equal("stylesheet", DumpExtractors.LinkKindFromRel("stylesheet"));
        Assert.Equal("icon", DumpExtractors.LinkKindFromRel("icon"));
        // First token wins for a multi-token rel such as "shortcut icon".
        Assert.Equal("icon", DumpExtractors.LinkKindFromRel("shortcut icon"));
        Assert.Equal("manifest", DumpExtractors.LinkKindFromRel("manifest"));
        Assert.Equal("preload", DumpExtractors.LinkKindFromRel("preload"));
        Assert.Equal("prefetch", DumpExtractors.LinkKindFromRel("prefetch"));
        Assert.Equal("modulepreload", DumpExtractors.LinkKindFromRel("modulepreload"));
        Assert.Equal("dns-prefetch", DumpExtractors.LinkKindFromRel("dns-prefetch"));
        Assert.Equal("preconnect", DumpExtractors.LinkKindFromRel("preconnect"));
        Assert.Equal("alternate", DumpExtractors.LinkKindFromRel("alternate"));
        // Empty / unknown falls back to generic "link" so the URL is still emitted.
        Assert.Equal("link", DumpExtractors.LinkKindFromRel(string.Empty));
        Assert.Equal("link", DumpExtractors.LinkKindFromRel("noopener"));
    }

    [Fact]
    public void Extract_assets_covers_every_resource_tag()
    {
        const string html = """
            <html><head>
                <link rel="stylesheet" href="/site.css">
                <link rel="icon" href="/favicon.ico">
                <link rel="preload" href="/font.woff2">
                <link href="/no-rel.css">
                <script src="/app.js"></script>
            </head><body>
                <img src="/logo.png">
                <iframe src="/frame.html"></iframe>
                <video src="/clip.mp4"><source src="/clip.webm"></video>
                <audio src="/track.mp3"></audio>
                <embed src="/widget.swf">
                <object data="/doc.pdf"></object>
            </body></html>
            """;
        var dom = HtmlParsing.ParseHtml(html);
        var baseUrl = UrlRecord.Parse("https://example.test/page");
        var ndjson = DumpExtractors.ExtractAssets(dom, baseUrl);
        var records = ndjson.Split('\n')
            .Select(line => JsonNode.Parse(line) as JsonObject
                ?? throw new JsonException($"each line must be valid JSON: {line}"))
            .ToList();

        // Every emitted record must have an absolute URL on example.test and a
        // non-empty type string.
        foreach (var record in records)
        {
            var url = record["url"]!.GetValue<string>();
            Assert.StartsWith("https://example.test/", url, StringComparison.Ordinal);
            Assert.NotEmpty(record["type"]!.GetValue<string>());
        }

        var pairs = records
            .Select(r => (r["url"]!.GetValue<string>(), r["type"]!.GetValue<string>()))
            .ToList();

        Assert.Contains(("https://example.test/app.js", "script"), pairs);
        Assert.Contains(("https://example.test/site.css", "stylesheet"), pairs);
        Assert.Contains(("https://example.test/favicon.ico", "icon"), pairs);
        Assert.Contains(("https://example.test/font.woff2", "preload"), pairs);
        Assert.Contains(("https://example.test/no-rel.css", "link"), pairs);
        Assert.Contains(("https://example.test/logo.png", "image"), pairs);
        Assert.Contains(("https://example.test/frame.html", "iframe"), pairs);
        Assert.Contains(("https://example.test/clip.mp4", "video"), pairs);
        Assert.Contains(("https://example.test/clip.webm", "media"), pairs);
        Assert.Contains(("https://example.test/track.mp3", "audio"), pairs);
        Assert.Contains(("https://example.test/widget.swf", "embed"), pairs);
        Assert.Contains(("https://example.test/doc.pdf", "object"), pairs);
    }

    [Fact]
    public void Extract_assets_skips_empty_attributes()
    {
        const string html = """
            <html><body>
                <script src=""></script>
                <img src="   ">
                <iframe src="/ok.html"></iframe>
            </body></html>
            """;
        var dom = HtmlParsing.ParseHtml(html);
        var baseUrl = UrlRecord.Parse("https://example.test/");
        var ndjson = DumpExtractors.ExtractAssets(dom, baseUrl);
        var lines = ndjson.Split('\n');
        // Only the iframe with a non-empty src survives.
        Assert.Single(lines);
        Assert.Contains("\"https://example.test/ok.html\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"iframe\"", lines[0], StringComparison.Ordinal);
    }

    // Not in the Rust suite, but the whitespace collapsing across sibling text
    // nodes is the subtlest part of the extractor and `text_whitespace.rs`
    // exercises the same rule through the binary.
    [Fact]
    public void Readable_text_collapses_whitespace_across_inline_siblings()
    {
        Assert.Equal("a b", BodyText("<html><body><span>a</span> <span>b</span></body></html>"));
        Assert.Equal("ab", BodyText("<html><body><span>a</span><span>b</span></body></html>"));
    }
}
