using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// SECURITY.md I2: client-controlled text in a log record (a CDP method name, a
/// URL) cannot start a new line or send terminal control sequences.
/// </summary>
public sealed class CdpLogEscapingTests
{
    [Fact]
    public void ControlCharactersAreEscaped()
    {
        var forged = "Page.navigate\r\nobscura-cdp WARN: forged\u001b[2J\u2028\u0085\u007f\t";
        Assert.Equal(
            "Page.navigate\\u000D\\u000Aobscura-cdp WARN: forged\\u001B[2J\\u2028\\u0085\\u007F\\u0009",
            CdpLog.EscapeControl(forged));
    }

    [Fact]
    public void PlainTextIsReturnedUnchanged()
    {
        var plain = "CDP error for Page.navigate: https://example.com/ü";
        Assert.Same(plain, CdpLog.EscapeControl(plain));
    }
}
