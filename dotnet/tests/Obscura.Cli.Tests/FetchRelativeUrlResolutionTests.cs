using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Api;
using Xunit;
using ApiBrowser = Obscura.Api.Browser;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura/tests/fetch_relative_url_resolution.rs</c>.
/// </summary>
/// <remarks>
/// Issue #663: fetch and XHR skipped URL resolution as soon as the relative URL
/// contained <c>://</c> anywhere, so a query carrying an absolute URL (for
/// example <c>api/proxy?target=https://cdn/x.json</c>) fell through unresolved.
/// The Fetch spec lets the URL parser decide absoluteness; resolution must always
/// run. The local server echoes the request target back in the JSON body, so the
/// test asserts on the exact path the engine actually requested.
/// </remarks>
public sealed class FetchRelativeUrlResolutionTests
{
    [Fact]
    public async Task Fetch_and_xhr_resolve_relative_urls_that_contain_double_slashes()
    {
        using var server = new LocalHttpServer(
            target => target.StartsWith("/api", StringComparison.Ordinal)
                || target.StartsWith("/deep/api", StringComparison.Ordinal)
                    ? ("application/json",
                       Encoding.UTF8.GetBytes($"{{\"path\":\"{target}\"}}"))
                    : ("text/html", Encoding.UTF8.GetBytes(
                        "<!doctype html><html><head><title>fixture</title></head><body></body></html>")),
            cors: true);

        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync($"{server.Base}/deep/page");

        page.Evaluate($$"""
            (function() {
                var out = document.createElement('pre');
                out.id = 'probe-results';
                document.body.appendChild(out);
                var results = {};
                var pending = 5;
                function record(name, val) {
                    results[name] = val;
                    pending -= 1;
                    if (pending === 0) {
                        out.textContent = JSON.stringify(results);
                        document.body.setAttribute('data-done', '1');
                    }
                }
                // The issue repro: relative URL whose query carries an absolute one.
                fetch('api/proxy?target=https://cdn.example.com/x.json')
                    .then(function(r) { return r.text(); })
                    .then(function(t) { record('f_query', t); }, function(e) { record('f_query', 'REJECTED'); });
                var x1 = new XMLHttpRequest();
                x1.open('GET', 'api/proxy?target=https://cdn.example.com/x.json');
                x1.onload = function() { record('x_query', x1.responseText); };
                x1.onerror = function() { record('x_query', 'REJECTED'); };
                x1.send();
                // Regression: plain relative URLs still resolve.
                fetch('api/ok')
                    .then(function(r) { return r.text(); })
                    .then(function(t) { record('f_plain', t); }, function(e) { record('f_plain', 'REJECTED'); });
                var x2 = new XMLHttpRequest();
                x2.open('GET', 'api/ok?b=2');
                x2.onload = function() { record('x_plain', x2.responseText); };
                x2.onerror = function() { record('x_plain', 'REJECTED'); };
                x2.send();
                // Regression: absolute URLs stay intact.
                fetch('{{server.Base}}/api/ok?abs=1')
                    .then(function(r) { return r.text(); })
                    .then(function(t) { record('f_abs', t); }, function(e) { record('f_abs', 'REJECTED'); });
            })()
            """);

        await PageProbe.SettleUntilDoneAsync(page);
        Assert.Equal("1", PageProbe.DoneMarker(page));

        var results = PageProbe.ProbeResults(page);
        Assert.NotNull(results);

        Assert.StartsWith(
            "/deep/api/proxy?target=https",
            RequestedPath(results, "f_query"),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "/deep/api/proxy?target=https",
            RequestedPath(results, "x_query"),
            StringComparison.Ordinal);
        Assert.Equal("/deep/api/ok", RequestedPath(results, "f_plain"));
        Assert.Equal("/deep/api/ok?b=2", RequestedPath(results, "x_plain"));
        Assert.Equal("/api/ok?abs=1", RequestedPath(results, "f_abs"));
    }

    /// <summary>
    /// Parse the echoed server JSON out of one probe slot and return the
    /// requested path, the port of the Rust helper of the same name.
    /// </summary>
    private static string RequestedPath(JsonNode? results, string key)
    {
        var raw = PageProbe.Text(results?[key]) ?? $"MISSING_SLOT_{key}";
        JsonNode? inner;
        try
        {
            inner = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return raw;
        }
        return PageProbe.Text(inner?["path"]) ?? raw;
    }
}
