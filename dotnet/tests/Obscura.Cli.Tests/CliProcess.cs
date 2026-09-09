using System.Diagnostics;
using System.Text;

namespace Obscura.Cli.Tests;

/// <summary>One completed run of the built CLI.</summary>
public sealed record CliRun(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>True when the process exited 0, matching Rust's <c>status.success()</c>.</summary>
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Runs the built <c>obscura</c> launcher, the way the Rust integration tests
/// run <c>CARGO_BIN_EXE_obscura</c>.
/// </summary>
public static class CliProcess
{
    /// <summary>Path to the built launcher, or null when it has not been built.</summary>
    public static string? Binary { get; } = Resolve();

    /// <summary>Why these tests are skipping, or null when they can run.</summary>
    public static string? SkipReason =>
        Binary is null ? "CLI launcher not built. Run: dotnet build -c Release." : null;

    private static string? Resolve()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) && Directory.Exists(Path.Combine(dir, "crates")))
            {
                foreach (var configuration in new[] { "Release", "Debug" })
                {
                    var candidate = Path.Combine(
                        dir, "dotnet", "src", "Obscura.Cli", "bin", configuration, "net10.0", "obscura");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                return null;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>Run the CLI with the given arguments and no extra environment.</summary>
    public static CliRun Run(params string[] args) => Run(null, args);

    /// <summary>Run the CLI with extra environment variables.</summary>
    public static CliRun Run(IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var exe = Binary ?? throw new InvalidOperationException(SkipReason);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {exe}");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outTask = ReadAllAsync(process.StandardOutput, stdout);
        var errTask = ReadAllAsync(process.StandardError, stderr);
        // A hung CLI must fail the test rather than hang the run.
        if (!process.WaitForExit(milliseconds: 120_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            throw new TimeoutException($"obscura did not exit within 120s: {string.Join(' ', args)}");
        }
        Task.WaitAll(outTask, errTask);
        return new CliRun(process.ExitCode, stdout.ToString(), stderr.ToString());
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
