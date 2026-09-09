using System.Diagnostics;
using System.Text;

namespace Obscura.Parity.Tests.Harness;

/// <summary>
/// Runs either engine with something on stdin, which
/// <see cref="ReferenceEngine"/> does not cover.
/// </summary>
/// <remarks>
/// A separate type rather than an addition to <see cref="ReferenceEngine"/>, so
/// the shared harness file stays untouched. Needed by the <c>mcp</c> stdio
/// transport and by <c>fetch --file -</c>.
/// </remarks>
public static class PipedEngine
{
    /// <summary>Run the Rust reference binary with <paramref name="stdin"/> piped in.</summary>
    public static EngineRun Rust(string stdin, params string[] args) =>
        Run(ReferenceEngine.RustBinary ?? throw new InvalidOperationException(ReferenceEngine.SkipReason), stdin, args);

    /// <summary>Run the C# port with <paramref name="stdin"/> piped in.</summary>
    public static EngineRun Port(string stdin, params string[] args) =>
        Run(ReferenceEngine.PortBinary ?? throw new InvalidOperationException(ReferenceEngine.SkipReason), stdin, args);

    private static EngineRun Run(string exe, string stdin, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["OBSCURA_ALLOW_PRIVATE_NETWORK"] = "1";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {exe}");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outTask = ReadAllAsync(process.StandardOutput, stdout);
        var errTask = ReadAllAsync(process.StandardError, stderr);

        process.StandardInput.Write(stdin);
        process.StandardInput.Flush();
        // Closing stdin is what ends a stdio server's read loop.
        process.StandardInput.Close();

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
            throw new TimeoutException($"{Path.GetFileName(exe)} did not exit within 120s: {string.Join(' ', args)}");
        }
        Task.WaitAll(outTask, errTask);
        return new EngineRun(process.ExitCode, stdout.ToString(), stderr.ToString());
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
