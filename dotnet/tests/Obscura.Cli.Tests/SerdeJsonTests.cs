using System.Text.Json.Nodes;
using Obscura.Cli.Json;
using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>
/// The CLI's JSON writer must produce what <c>serde_json</c> produces, because
/// <c>--eval</c>, <c>--dump assets</c>, <c>--dump cookies</c>, batch fetch and
/// <c>scrape</c> all print through it.
/// </summary>
/// <remarks>
/// Not a port of a Rust test: in Rust this is serde_json itself and needs no
/// coverage. Here it is a reimplementation, so it needs pinning.
/// </remarks>
public sealed class SerdeJsonTests
{
    [Fact]
    public void A_bare_js_number_prints_as_an_f64()
    {
        // page.evaluate converts a JS number straight to f64, and serde_json's
        // ryu formatter always emits a decimal point. `--eval "1+1"` is 2.0.
        Assert.Equal("2.0", SerdeJson.ToJson(JsonValue.Create(2.0)));
        Assert.Equal("-0.0", SerdeJson.ToJson(JsonValue.Create(-0.0)));
        Assert.Equal("0.5", SerdeJson.ToJson(JsonValue.Create(0.5)));
        // A positive exponent carries an explicit '+', which is what the
        // reference prints for `--eval "1e30"`.
        Assert.Equal("1e+30", SerdeJson.ToJson(JsonValue.Create(1e30)));
        Assert.Equal("1.234e+33", SerdeJson.ToJson(JsonValue.Create(1.234e33)));
        Assert.Equal("1e-30", SerdeJson.ToJson(JsonValue.Create(1e-30)));
        Assert.Equal("5e-324", SerdeJson.ToJson(JsonValue.Create(double.Epsilon)));
        Assert.Equal("0.001234", SerdeJson.ToJson(JsonValue.Create(0.001234)));
    }

    [Fact]
    public void A_number_inside_a_stringified_object_keeps_its_integer_spelling()
    {
        // Nested values arrive through JSON.stringify and are re-parsed, and
        // serde_json's parser keeps an integer literal an integer.
        Assert.Equal("""{"a":1,"b":[1,2]}""", SerdeJson.ToJson(JsonNode.Parse("""{"a":1,"b":[1,2]}""")));
        Assert.Equal("""{"a":1.5}""", SerdeJson.ToJson(JsonNode.Parse("""{"a":1.5}""")));
    }

    [Fact]
    public void An_integer_built_in_process_stays_an_integer()
    {
        // total_time_ms and friends are u128/usize in Rust, not floats.
        Assert.Equal("181", SerdeJson.ToJson(JsonValue.Create(181L)));
        Assert.Equal("0", SerdeJson.ToJson(JsonValue.Create(0)));
        Assert.Equal("200", SerdeJson.ToJson(JsonValue.Create((ulong)200)));
    }

    [Fact]
    public void Strings_are_escaped_the_way_serde_json_escapes_them()
    {
        // serde_json escapes only the quote, the backslash and the C0 controls.
        // It does not escape <, >, &, +, or any non-ASCII code point, which is
        // exactly where System.Text.Json's default encoder would diverge.
        Assert.Equal("\"<a href=\\\"x\\\">&amp; + \u00e9 \u4e2d\"",
            SerdeJson.ToJson(JsonValue.Create("<a href=\"x\">&amp; + \u00e9 \u4e2d")));
        Assert.Equal("\"\\n\\t\\r\\b\\f\\\\\"", SerdeJson.ToJson(JsonValue.Create("\n\t\r\b\f\\")));
        Assert.Equal("\"\\u0001\"", SerdeJson.ToJson(JsonValue.Create("\u0001")));
    }

    [Fact]
    public void Pretty_printing_matches_to_string_pretty()
    {
        Assert.Equal("[]", SerdeJson.ToJsonPretty(new JsonArray()));
        Assert.Equal("{}", SerdeJson.ToJsonPretty(new JsonObject()));
        Assert.Equal(
            """
            {
              "total_urls": 1,
              "avg_time_ms": 181.0,
              "results": [
                {
                  "eval": null
                }
              ]
            }
            """.ReplaceLineEndings("\n").TrimEnd('\n'),
            SerdeJson.ToJsonPretty(new JsonObject
            {
                ["total_urls"] = JsonValue.Create(1),
                ["avg_time_ms"] = JsonValue.Create(181.0),
                ["results"] = new JsonArray(new JsonObject { ["eval"] = null }),
            }));
    }

    /// <summary>
    /// Object keys go out in insertion order, never sorted.
    /// </summary>
    /// <remarks>
    /// <c>deno_core</c> turns on <c>serde_json/preserve_order</c> and Cargo
    /// unifies features across the graph, so <c>serde_json::Map</c> is an
    /// <c>IndexMap</c> in this workspace, not a <c>BTreeMap</c>. Confirmed
    /// against the reference binary: batch <c>fetch</c> prints
    /// <c>url, ok, status, content_type, bytes, elapsed_ms</c>, which is
    /// declaration order and not alphabetical, and <c>--dump cookies</c> prints
    /// <c>name, value, domain, path, secure, httpOnly, sameSite, expires</c>.
    /// </remarks>
    [Fact]
    public void Object_keys_are_written_in_insertion_order()
    {
        Assert.Equal(
            """{"url":"u","ok":true,"status":200,"content_type":"text/html","bytes":3,"elapsed_ms":7}""",
            SerdeJson.ToJson(new JsonObject
            {
                ["url"] = JsonValue.Create("u"),
                ["ok"] = JsonValue.Create(true),
                ["status"] = JsonValue.Create(200),
                ["content_type"] = JsonValue.Create("text/html"),
                ["bytes"] = JsonValue.Create(3),
                ["elapsed_ms"] = JsonValue.Create(7L),
            }));

        Assert.Equal(
            """{"name":"sid","value":"a","domain":"h","path":"/","secure":false,"httpOnly":true,"sameSite":"Lax","expires":null}""",
            SerdeJson.ToJson(new JsonObject
            {
                ["name"] = JsonValue.Create("sid"),
                ["value"] = JsonValue.Create("a"),
                ["domain"] = JsonValue.Create("h"),
                ["path"] = JsonValue.Create("/"),
                ["secure"] = JsonValue.Create(false),
                ["httpOnly"] = JsonValue.Create(true),
                ["sameSite"] = JsonValue.Create("Lax"),
                ["expires"] = null,
            }));

        // And a parsed object keeps the order its text had.
        Assert.Equal(
            """{"z":1,"a":2}""",
            SerdeJson.ToJson(JsonNode.Parse("""{"z":1,"a":2}""")));
    }

    [Fact]
    public void Null_and_booleans_round_trip()
    {
        Assert.Equal("null", SerdeJson.ToJson(null));
        Assert.Equal("true", SerdeJson.ToJson(JsonValue.Create(true)));
        Assert.Equal("false", SerdeJson.ToJson(JsonValue.Create(false)));
    }

    [Fact]
    public void Non_finite_numbers_become_null()
    {
        // serde_json has no representation for these; Value::from maps them to null.
        Assert.Equal("null", SerdeJson.ToJson(JsonValue.Create(double.NaN)));
        Assert.Equal("null", SerdeJson.ToJson(JsonValue.Create(double.PositiveInfinity)));
    }
}
