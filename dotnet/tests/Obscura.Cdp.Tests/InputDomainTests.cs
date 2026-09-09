using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Cdp.Domains;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>#[cfg(test)] mod tests</c> in
/// <c>crates/obscura-cdp/src/domains/input.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class InputDomainTests
{
    // SEC-501 / #819 - key/code are embedded via JsStr; it must escape control characters
    // (newline/CR/tab/NUL/U+2028-29), not just backslash and quote, so a control char cannot
    // terminate the literal and drop the event.
    [Fact]
    public void JsStrEscapesControlCharacters()
    {
        string literal = Input.JsStr("a\nb\r\t'c\\d\"e");
        Assert.DoesNotContain('\n', literal);
        Assert.DoesNotContain('\r', literal);
        Assert.DoesNotContain('\t', literal);

        // The result must be a valid JS/JSON string literal that round-trips.
        string decoded = JsonSerializer.Deserialize<string>(literal)
            ?? throw new InvalidOperationException("the literal must be valid JSON");
        Assert.Equal("a\nb\r\t'c\\d\"e", decoded);
    }

    /// <summary>
    /// Not in the Rust file: the literal writer must be <c>serde_json::to_string</c>, not
    /// <c>System.Text.Json</c>'s default encoder, which escapes <c>+ &lt; &gt; &amp;</c> and every
    /// non-ASCII code point and would change the bytes the two engines put on the wire.
    /// </summary>
    [Fact]
    public void JsStrKeepsTheBytesSerdeJsonWouldWrite()
    {
        Assert.Equal("\"a+b<c>d&e\"", Input.JsStr("a+b<c>d&e"));
        Assert.Equal("\"é中\"", Input.JsStr("é中"));
        Assert.Equal("\"a\\u0000b\"", Input.JsStr("a\0b"));
    }

    /// <summary>
    /// Not in the Rust file: the button and modifier tables that every generated mouse snippet
    /// interpolates. A wrong entry silently changes <c>event.button</c> / <c>event.buttons</c>.
    /// </summary>
    [Fact]
    public void MouseButtonAndModifierTablesMatchTheProtocol()
    {
        Assert.Equal(0, Input.MouseButtonCode("left"));
        Assert.Equal(1, Input.MouseButtonCode("middle"));
        Assert.Equal(2, Input.MouseButtonCode("right"));
        Assert.Equal(3, Input.MouseButtonCode("back"));
        Assert.Equal(4, Input.MouseButtonCode("forward"));
        Assert.Equal(0, Input.MouseButtonCode("none"));

        Assert.Equal(1ul, Input.MouseButtonMask("left"));
        Assert.Equal(2ul, Input.MouseButtonMask("right"));
        Assert.Equal(4ul, Input.MouseButtonMask("middle"));
        Assert.Equal(8ul, Input.MouseButtonMask("back"));
        Assert.Equal(16ul, Input.MouseButtonMask("forward"));
        Assert.Equal(0ul, Input.MouseButtonMask("none"));

        // CDP Input.Modifier: Alt=1, Ctrl=2, Meta=4, Shift=8.
        Assert.Equal((false, false, false, false), Input.ModifierFlags(0));
        Assert.Equal((true, false, false, false), Input.ModifierFlags(1));
        Assert.Equal((false, true, false, false), Input.ModifierFlags(2));
        Assert.Equal((false, false, true, false), Input.ModifierFlags(4));
        Assert.Equal((false, false, false, true), Input.ModifierFlags(8));
        Assert.Equal((true, false, false, true), Input.ModifierFlags(9));
        Assert.Equal((false, true, false, true), Input.ModifierFlags(10));
    }

    [Fact]
    public async Task UnknownInputMethodIsAnErrorAndTouchIsAcknowledged()
    {
        var ctx = CdpContext.New();
        Assert.Contains(
            "Unknown Input method: nope",
            CdpDomainFixtures.ErrorOf(await Input.HandleAsync("nope", new JsonObject(), ctx, null)),
            StringComparison.Ordinal);
        Assert.True((await Input.HandleAsync("dispatchTouchEvent", new JsonObject(), ctx, null)).IsOk);
        Assert.True((await Input.HandleAsync("setIgnoreInputEvents", new JsonObject(), ctx, null)).IsOk);
    }
}
