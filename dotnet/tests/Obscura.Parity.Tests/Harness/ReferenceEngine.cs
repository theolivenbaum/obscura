using System.Diagnostics;
using System.Text;

namespace Obscura.Parity.Tests.Harness;

/// <summary>The outcome of one engine invocation.</summary>
public sealed record EngineRun(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>Stdout with trailing whitespace normalized, for line-wise comparison.</summary>
    public string[] OutLines =>
        StdOut.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
}

/// <summary>
/// Runs the Rust reference binary and the C# port over identical inputs.
/// </summary>
/// <remarks>
/// Parity is what makes a component "done" rather than merely "compiles and its
/// unit tests pass". The Rust engine is the behavioral authority, so these tests
/// exist to catch the divergences that a ported unit test cannot: a unit test
/// ported alongside the code inherits the porter's misreading of the original.
/// </remarks>
public static class ReferenceEngine
{
    /// <summary>Path to the Rust reference binary, or null when unavailable.</summary>
    public static string? RustBinary { get; } = ResolveRustBinary();

    /// <summary>Path to the C# port's CLI, or null when it has not been built.</summary>
    public static string? PortBinary { get; } = ResolvePortBinary();

    /// <summary>Why parity tests are skipping, or null when they can run.</summary>
    public static string? SkipReason =>
        RustBinary is null
            ? "Rust reference binary not found. Build it with: cargo build --release -p obscura-cli --bins --features render, or set OBSCURA_RUST_BIN."
            : PortBinary is null
                ? "C# CLI not built. Run: dotnet build -c Release."
                : null;

    private static string? ResolveRustBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("OBSCURA_RUST_BIN");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            // An explicitly configured path that does not exist is a setup
            // error worth surfacing, not something to silently fall back from.
            return File.Exists(fromEnv) ? fromEnv : null;
        }

        var repo = FindRepoRoot();
        if (repo is null)
        {
            return null;
        }
        var candidate = Path.Combine(repo, "target", "release", "obscura");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? ResolvePortBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("OBSCURA_PORT_BIN");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return File.Exists(fromEnv) ? fromEnv : null;
        }

        var repo = FindRepoRoot();
        if (repo is null)
        {
            return null;
        }
        foreach (var name in new[] { "obscura", "Obscura.Cli" })
        {
            var candidate = Path.Combine(repo, "dotnet", "src", "Obscura.Cli", "bin", "Release", "net10.0", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) && Directory.Exists(Path.Combine(dir, "crates")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>Runs the Rust reference binary.</summary>
    public static EngineRun Rust(params string[] args) =>
        Run(RustBinary ?? throw new InvalidOperationException(SkipReason), args);

    /// <summary>Runs the C# port.</summary>
    public static EngineRun Port(params string[] args) =>
        Run(PortBinary ?? throw new InvalidOperationException(SkipReason), args);

    private static EngineRun Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        // Both engines must see the same environment, or a difference in
        // behavior could come from configuration rather than from the port.
        psi.Environment["OBSCURA_ALLOW_PRIVATE_NETWORK"] = "1";

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {exe}");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outTask = ReadAllAsync(proc.StandardOutput, stdout);
        var errTask = ReadAllAsync(proc.StandardError, stderr);

        // A hung engine must fail the test rather than hang the whole run.
        if (!proc.WaitForExit(milliseconds: 120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{Path.GetFileName(exe)} did not exit within 120s: {string.Join(' ', args)}");
        }
        Task.WaitAll(outTask, errTask);
        return new EngineRun(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task ReadAllAsync(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            sink.Append(buffer, 0, read);
        }
    }
}
