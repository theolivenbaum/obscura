using Obscura.Cli;
using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>
/// The log-filter, proxy-merge and V8-flag tests from <c>main.rs</c>'s
/// <c>mod tests</c>.
/// </summary>
public sealed class CliOptionsTests
{
    [Fact]
    public void Default_filter_is_warn() => Assert.Equal("warn", CliOptions.SelectLogFilter(false, false));

    [Fact]
    public void Verbose_filter_is_debug() => Assert.Equal("debug", CliOptions.SelectLogFilter(true, false));

    [Fact]
    public void Quiet_filter_is_off() => Assert.Equal("off", CliOptions.SelectLogFilter(false, true));

    [Fact]
    public void Verbose_wins_over_quiet() => Assert.Equal("debug", CliOptions.SelectLogFilter(true, true));

    [Fact]
    public void Normalize_v8_flags_returns_none_when_unset() =>
        Assert.Null(CliOptions.NormalizeV8Flags(null));

    [Fact]
    public void Normalize_v8_flags_returns_none_for_empty_or_whitespace()
    {
        Assert.Null(CliOptions.NormalizeV8Flags(string.Empty));
        Assert.Null(CliOptions.NormalizeV8Flags("   "));
        Assert.Null(CliOptions.NormalizeV8Flags("\t\n"));
    }

    [Fact]
    public void Normalize_v8_flags_trims_surrounding_whitespace() =>
        Assert.Equal("--max-old-space-size=4096", CliOptions.NormalizeV8Flags("  --max-old-space-size=4096  "));

    [Fact]
    public void Normalize_v8_flags_preserves_multi_flag_string()
    {
        const string input = "--max-old-space-size=4096 --max-semi-space-size=64 --expose-gc";
        Assert.Equal(input, CliOptions.NormalizeV8Flags(input));
    }

    [Fact]
    public void Effective_v8_flags_returns_default_when_unset()
    {
        Assert.Equal(CliOptions.DefaultV8Flags, CliOptions.EffectiveV8Flags(null));
        Assert.Equal(CliOptions.DefaultV8Flags, CliOptions.EffectiveV8Flags(string.Empty));
        Assert.Equal(CliOptions.DefaultV8Flags, CliOptions.EffectiveV8Flags("   "));
    }

    [Fact]
    public void Effective_v8_flags_user_overrides_default()
    {
        // V8 parses left to right and later wins, so the user value must come
        // after the default in the merged string.
        const string user = "--max-old-space-size=8192";
        var merged = CliOptions.EffectiveV8Flags(user);
        Assert.StartsWith(CliOptions.DefaultV8Flags, merged, StringComparison.Ordinal);
        Assert.EndsWith(user, merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Effective_v8_flags_appends_user_extras()
    {
        var merged = CliOptions.EffectiveV8Flags("--expose-gc");
        Assert.Contains(CliOptions.DefaultV8Flags, merged, StringComparison.Ordinal);
        Assert.Contains("--expose-gc", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_proxy_overrides_global_proxy() =>
        Assert.Equal(
            "socks5://127.0.0.1:1080",
            CliOptions.MergeProxy("http://global.example:8080", "socks5://127.0.0.1:1080"));

    [Fact]
    public void Global_proxy_is_used_when_command_proxy_is_absent() =>
        Assert.Equal("http://global.example:8080", CliOptions.MergeProxy("http://global.example:8080", null));

    // Not in the Rust suite: the banner is the one place the CLI writes ASCII
    // art with significant trailing whitespace, and it is easy to lose in a
    // reformat.
    [Fact]
    public void Banner_keeps_its_exact_shape()
    {
        var banner = CliOptions.Banner(9222);
        var lines = banner.Split('\n');
        Assert.Equal(11, lines.Length);
        Assert.Equal(string.Empty, lines[0]);
        Assert.Equal("   ____  _                              ", lines[1]);
        Assert.Equal("                   ", lines[7]);
        Assert.Equal($"  Headless Browser v{BuildVersionValue}", lines[8]);
        Assert.Equal("  CDP server: ws://127.0.0.1:9222/devtools/browser", lines[9]);
        Assert.Equal(string.Empty, lines[10]);
    }

    private static string BuildVersionValue => Obscura.Cli.CommandLine.BuildVersion.Value;
}
