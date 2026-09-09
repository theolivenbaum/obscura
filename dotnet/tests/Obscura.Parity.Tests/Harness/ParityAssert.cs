using System.Text;
using Xunit;
using Xunit.Sdk;

namespace Obscura.Parity.Tests.Harness;

/// <summary>Comparisons that report a usable diff when the engines disagree.</summary>
public static class ParityAssert
{
    /// <summary>
    /// Asserts both engines produced identical stdout for the same arguments.
    /// </summary>
    public static void SameStdOut(params string[] args)
    {
        var rust = ReferenceEngine.Rust(args);
        var port = ReferenceEngine.Port(args);
        SameLines(rust, port, args);
    }

    /// <summary>Asserts two completed runs produced identical stdout.</summary>
    public static void SameLines(EngineRun rust, EngineRun port, string[] args)
    {
        if (string.Equals(Normalize(rust.StdOut), Normalize(port.StdOut), StringComparison.Ordinal))
        {
            return;
        }
        throw new XunitException(Describe(rust, port, args));
    }

    /// <summary>
    /// Asserts both engines produced the same set of lines, ignoring order.
    /// Use only where the Rust engine's order is genuinely unspecified; prefer
    /// <see cref="SameStdOut"/>, because order differences are usually real bugs.
    /// </summary>
    public static void SameLinesUnordered(params string[] args)
    {
        var rust = ReferenceEngine.Rust(args);
        var port = ReferenceEngine.Port(args);
        var r = rust.OutLines.Order(StringComparer.Ordinal).ToArray();
        var p = port.OutLines.Order(StringComparer.Ordinal).ToArray();
        if (!r.SequenceEqual(p, StringComparer.Ordinal))
        {
            throw new XunitException(Describe(rust, port, args));
        }
    }

    private static string Normalize(string s) => s.ReplaceLineEndings("\n").TrimEnd('\n');

    private static string Describe(EngineRun rust, EngineRun port, string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Engines disagree.");
        sb.AppendLine($"  args: {string.Join(' ', args)}");
        sb.AppendLine($"  exit: rust={rust.ExitCode} port={port.ExitCode}");
        sb.AppendLine();

        var r = rust.OutLines;
        var p = port.OutLines;
        var max = Math.Max(r.Length, p.Length);
        var shown = 0;
        for (var i = 0; i < max && shown < 20; i++)
        {
            var rl = i < r.Length ? r[i] : "<missing>";
            var pl = i < p.Length ? p[i] : "<missing>";
            if (!string.Equals(rl, pl, StringComparison.Ordinal))
            {
                sb.AppendLine($"  line {i + 1}:");
                sb.AppendLine($"    rust: {Truncate(rl)}");
                sb.AppendLine($"    port: {Truncate(pl)}");
                shown++;
            }
        }
        if (shown == 0)
        {
            sb.AppendLine("  (lines match; difference is in trailing content or line count)");
            sb.AppendLine($"  rust lines={r.Length} port lines={p.Length}");
        }
        if (port.StdErr.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  port stderr: {Truncate(port.StdErr, 500)}");
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max = 200) =>
        s.Length <= max ? s : s[..max] + $"... (+{s.Length - max} chars)";
}
