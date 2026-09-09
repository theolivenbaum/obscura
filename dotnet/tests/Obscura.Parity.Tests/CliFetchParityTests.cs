using Obscura.Parity.Tests.Harness;
using Xunit;

namespace Obscura.Parity.Tests;

/// <summary>
/// Differential coverage for <c>obscura fetch</c>: both engines are run over the
/// same fixture and their stdout compared.
/// </summary>
/// <remarks>
/// <para>
/// The golden corpus under <c>golden/</c> records the Rust engine's output for
/// the cheap surfaces so the port can be checked on a machine with no Rust
/// toolchain, but a recording can go stale. These tests run the real binary, so
/// they are what actually gates the CLI.
/// </para>
/// <para>
/// Each case is two process launches with a V8 isolate in each, so the corpus is
/// used in full for the three cheap, high-signal surfaces and sampled for the
/// rest. <c>scripts/regen-golden.sh</c> sweeps everything when a full pass is
/// wanted.
/// </para>
/// </remarks>
public sealed class CliFetchParityTests
{
    /// <summary>Every fixture in <c>render-repros/</c>, as a <c>file://</c> URL.</summary>
    public static TheoryData<string> AllFixtures() => Fixtures.All();

    /// <summary>A spread across the corpus, for the modes that are slower to compare.</summary>
    public static TheoryData<string> SampledFixtures() => Fixtures.Sample(12);

    [ParityTheory]
    [MemberData(nameof(AllFixtures))]
    public void Dump_text_matches(string fixture) =>
        ParityAssert.SameStdOut("fetch", fixture, "--dump", "text", "--quiet");

    [ParityTheory]
    [MemberData(nameof(AllFixtures))]
    public void Dump_links_matches(string fixture) =>
        ParityAssert.SameStdOut("fetch", fixture, "--dump", "links", "--quiet");

    [ParityTheory]
    [MemberData(nameof(AllFixtures))]
    public void Dump_html_matches(string fixture) =>
        ParityAssert.SameStdOut("fetch", fixture, "--dump", "html", "--quiet");

    [ParityTheory]
    [MemberData(nameof(SampledFixtures))]
    public void Dump_markdown_matches(string fixture) =>
        ParityAssert.SameStdOut("fetch", fixture, "--dump", "markdown", "--quiet");

    [ParityTheory]
    [MemberData(nameof(SampledFixtures))]
    public void Dump_assets_matches(string fixture) =>
        ParityAssert.SameStdOut("fetch", fixture, "--dump", "assets", "--quiet");

    /// <summary>
    /// <c>--dump cookies</c> over the corpus, where every fixture yields an
    /// empty jar.
    /// </summary>
    /// <remarks>
    /// Only the empty and single-cookie cases are byte-comparable. Rust's
    /// <c>get_all_cookies</c> iterates a nested <c>HashMap</c>, whose order is
    /// randomized per process, so on a page setting two cookies the reference
    /// itself returns them in a different order from run to run (measured: one
    /// flip in six runs). The port is deterministic in insertion order. The
    /// object key order inside each cookie IS byte-identical, which is the part
    /// that has to be pinned.
    /// </remarks>
    [ParityTheory]
    [MemberData(nameof(SampledFixtures))]
    public void Dump_cookies_matches(string fixture) =>
        ParityAssert.SameStdOut("fetch", fixture, "--dump", "cookies", "--quiet");

    [ParityTheory]
    [MemberData(nameof(SampledFixtures))]
    public void Dump_original_matches(string fixture) =>
        ParityAssert.SameStdOut("fetch", fixture, "--dump", "original", "--quiet");

    /// <summary>
    /// <c>--dump original</c> must be binary-safe: no UTF-8 round trip, no
    /// trailing newline. Compared through <c>--output</c> so nothing passes
    /// through a text stream on the way out.
    /// </summary>
    [ParityFact]
    public void Dump_original_streams_binary_bytes_verbatim()
    {
        // A 1x1 transparent PNG: the first byte is 0x89, which no text decoding
        // survives.
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48,
            0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00,
            0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78,
            0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
            0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
        ];
        var source = Path.Combine(Path.GetTempPath(), $"obscura-parity-{Guid.NewGuid():N}.png");
        var rustOut = source + ".rust";
        var portOut = source + ".port";
        File.WriteAllBytes(source, png);
        try
        {
            var url = $"file://{source}";
            var rust = ReferenceEngine.Rust("fetch", url, "--dump", "original", "--quiet", "-o", rustOut);
            var port = ReferenceEngine.Port("fetch", url, "--dump", "original", "--quiet", "-o", portOut);
            Assert.Equal(rust.ExitCode, port.ExitCode);
            Assert.Equal(png, File.ReadAllBytes(rustOut));
            Assert.Equal(File.ReadAllBytes(rustOut), File.ReadAllBytes(portOut));
        }
        finally
        {
            foreach (var path in new[] { source, rustOut, portOut })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    /// <summary>
    /// The recorded corpus and the live engine must agree, or the goldens have
    /// gone stale and every offline check based on them is meaningless.
    /// </summary>
    [ParityFact]
    public void Golden_corpus_still_matches_the_live_rust_engine()
    {
        var stale = new List<string>();
        foreach (var (name, url) in Fixtures.Named().Take(8))
        {
            foreach (var mode in new[] { "text", "links", "html" })
            {
                var golden = Path.Combine(Fixtures.GoldenDirectory, $"{name}.{mode}.txt");
                if (!File.Exists(golden))
                {
                    continue;
                }
                var rust = ReferenceEngine.Rust("fetch", url, "--dump", mode, "--quiet");
                if (!string.Equals(
                        File.ReadAllText(golden).ReplaceLineEndings("\n").TrimEnd('\n'),
                        rust.StdOut.ReplaceLineEndings("\n").TrimEnd('\n'),
                        StringComparison.Ordinal))
                {
                    stale.Add($"{name}.{mode}");
                }
            }
        }
        Assert.True(
            stale.Count == 0,
            $"golden files disagree with the live Rust engine (regenerate with scripts/regen-golden.sh): {string.Join(", ", stale)}");
    }
}
