using System.Diagnostics;
using System.Reflection;

namespace Obscura.Cli.CommandLine;

/// <summary>
/// The version string reported by <c>--version</c>.
/// </summary>
/// <remarks>
/// Mirrors <c>crates/obscura-cli/build.rs</c>: an explicit
/// <c>OBSCURA_VERSION</c> wins, then a GitHub tag ref, then the package version
/// with the short commit appended. Local builds are not cut from a tag, so the
/// bare workspace version cannot tell two checkouts apart; tarball and minimal
/// image builds without git keep the bare version.
/// </remarks>
public static class BuildVersion
{
    private static readonly Lazy<string> Lazy = new(Resolve);

    /// <summary>The resolved version, without a leading "v".</summary>
    public static string Value => Lazy.Value;

    private static string Resolve() =>
        Explicit() ?? GitHubTag() ?? Local();

    private static string? Explicit() => Normalize(EnvValue("OBSCURA_VERSION"));

    private static string? GitHubTag()
    {
        if (string.Equals(EnvValue("GITHUB_REF_TYPE"), "tag", StringComparison.Ordinal))
        {
            return Normalize(EnvValue("GITHUB_REF_NAME"));
        }
        var reference = EnvValue("GITHUB_REF");
        return reference is not null && reference.StartsWith("refs/tags/", StringComparison.Ordinal)
            ? Normalize(reference["refs/tags/".Length..])
            : null;
    }

    private static string Local()
    {
        var package = typeof(BuildVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(BuildVersion).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        // Strip any SourceLink metadata the SDK appends, so the shape matches
        // the Rust build's "<package>-dev+<sha>".
        var plus = package.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            package = package[..plus];
        }

        var sha = ShortHead();
        return sha is null ? package : $"{package}-dev+{sha}";
    }

    private static string? ShortHead()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("git")
            {
                ArgumentList = { "rev-parse", "--short", "HEAD" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (proc is null)
            {
                return null;
            }
            var sha = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5_000);
            return proc.ExitCode == 0 && sha.Length > 0 ? sha : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No git on PATH: a tarball or minimal-image build keeps the bare version.
            return null;
        }
    }

    private static string? EnvValue(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? Normalize(string? version) =>
        version is null ? null
        : version.StartsWith('v') ? version[1..]
        : version;
}
