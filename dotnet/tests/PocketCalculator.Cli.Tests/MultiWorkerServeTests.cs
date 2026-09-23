using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using PocketCalculator.Cli.Commands;
using Xunit;

namespace PocketCalculator.Cli.Tests;

/// <summary>
/// Upstream a156914's multi-worker balancer: workers on OS-assigned loopback ports,
/// a readiness wait in place of a fixed sleep, and the client-facing authority
/// forwarded to the workers so a DNS Host name reaching the balancer is served.
/// </summary>
public sealed partial class MultiWorkerServeTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [GeneratedRegex(@"Worker (\d+) on port (\d+)")]
    private static partial Regex WorkerLine();

    private sealed class Balancer(Process process, int port, IReadOnlyList<int> workerPorts) : IDisposable
    {
        public int Port { get; } = port;
        public IReadOnlyList<int> WorkerPorts { get; } = workerPorts;

        public void Dispose()
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
            catch (InvalidOperationException)
            {
            }
            process.Dispose();
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>Start `serve --workers` and return once it logs that the balancer is up.</summary>
    private static Balancer Start(string host, int port, string? token, int workers = 2)
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
        var psi = new ProcessStartInfo(CliProcess.Binary!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-v", "serve", "--workers", workers.ToString(System.Globalization.CultureInfo.InvariantCulture), "--host", host, "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["POCKETCALCULATOR_CDP_TOKEN"] = token ?? string.Empty;
        var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start the CLI");
        _ = process.StandardOutput.ReadToEndAsync();

        var workerPorts = new List<int>();
        var log = new StringBuilder();
        var deadline = Stopwatch.GetTimestamp() + (30 * Stopwatch.Frequency);
        while (true)
        {
            var line = process.StandardError.ReadLineAsync();
            if (!line.Wait(TimeSpan.FromSeconds(30)) || Stopwatch.GetTimestamp() > deadline)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"balancer did not start:\n{log}");
            }
            if (line.Result is not { } text)
            {
                throw new InvalidOperationException($"balancer exited during startup:\n{log}");
            }
            log.AppendLine(text);
            if (WorkerLine().Match(text) is { Success: true } match)
            {
                workerPorts.Add(int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            if (text.Contains("Load balancer on", StringComparison.Ordinal))
            {
                _ = process.StandardError.ReadToEndAsync();
                return new Balancer(process, port, workerPorts);
            }
        }
    }

    private static async Task<string> RequestAsync(int port, string request)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cts.Token);
    }

    private static string Get(string path, string host, string? token) =>
        $"GET {path} HTTP/1.1\r\nHost: {host}\r\n"
        + (token is null ? string.Empty : $"Authorization: Bearer {token}\r\n")
        + "Connection: close\r\n\r\n";

    /// <summary>
    /// `serve --workers 2 --host 0.0.0.0` behind a DNS name: the loopback workers
    /// used to refuse the name with 403. They now serve it, advertise it in the
    /// discovery JSON, and still demand the token they inherited.
    /// </summary>
    [Fact]
    public async Task DnsHostThroughTheBalancerIsAcceptedAndAdvertised()
    {
        var port = FreePort();
        using var balancer = Start("0.0.0.0", port, Token);
        var host = $"cdp.example.test:{port}";

        // Round robin: two requests reach both workers.
        for (var i = 0; i < 2; i++)
        {
            var version = await RequestAsync(port, Get("/json/version", host, Token));
            Assert.StartsWith("HTTP/1.1 200 OK\r\n", version, StringComparison.Ordinal);
            Assert.Contains(
                $"\"webSocketDebuggerUrl\": \"ws://{host}/devtools/browser\"", version, StringComparison.Ordinal);
        }

        var list = await RequestAsync(port, Get("/json/list", host, Token));
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", list, StringComparison.Ordinal);
        Assert.Contains($"ws://{host}/devtools/page/", list, StringComparison.Ordinal);

        var unauthorized = await RequestAsync(port, Get("/json/version", host, null));
        Assert.StartsWith("HTTP/1.1 401 Unauthorized\r\n", unauthorized, StringComparison.Ordinal);
    }

    /// <summary>
    /// A loopback balancer forwards a loopback authority, which adds no DNS name:
    /// a rebound Host is still refused, and an IP literal still served.
    /// </summary>
    [Fact]
    public async Task LoopbackBalancerStillRefusesAReboundHost()
    {
        var port = FreePort();
        using var balancer = Start("127.0.0.1", port, null);

        var rebound = await RequestAsync(port, Get("/json/version", $"rebind.example:{port}", null));
        Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", rebound, StringComparison.Ordinal);

        var loopback = await RequestAsync(port, Get("/json/version", $"127.0.0.1:{port}", null));
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", loopback, StringComparison.Ordinal);
        Assert.Contains(
            $"\"webSocketDebuggerUrl\": \"ws://127.0.0.1:{port}/devtools/browser\"", loopback, StringComparison.Ordinal);
    }

    /// <summary>
    /// Workers take OS-assigned ports, so the port after the public one being
    /// taken no longer breaks startup (it used to be `port + 1 + i`), and the
    /// balancer announces itself only once every worker accepts connections: the
    /// first request after the announcement is served without a retry.
    /// </summary>
    [Fact]
    public async Task WorkersGetOsAssignedPortsAndTheBalancerWaitsForReadiness()
    {
        TcpListener? neighbour = null;
        var port = 0;
        for (var attempt = 0; attempt < 20 && neighbour is null; attempt++)
        {
            port = FreePort();
            if (port >= IPEndPoint.MaxPort)
            {
                continue;
            }
            var candidate = new TcpListener(IPAddress.Loopback, port + 1);
            try
            {
                candidate.Start();
                neighbour = candidate;
            }
            catch (SocketException)
            {
                candidate.Dispose();
            }
        }
        Assert.NotNull(neighbour);

        using (neighbour)
        using (var balancer = Start("127.0.0.1", port, null))
        {
            Assert.Equal(2, balancer.WorkerPorts.Count);
            Assert.DoesNotContain(port + 1, balancer.WorkerPorts);
            Assert.DoesNotContain(port, balancer.WorkerPorts);
            Assert.NotEqual(balancer.WorkerPorts[0], balancer.WorkerPorts[1]);

            for (var i = 0; i < 2; i++)
            {
                var version = await RequestAsync(port, Get("/json/version", $"127.0.0.1:{port}", null));
                Assert.StartsWith("HTTP/1.1 200 OK\r\n", version, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>A worker that exits before it listens fails startup, as upstream's `try_wait` does.</summary>
    [Fact]
    public async Task AWorkerThatExitsDuringStartupIsReported()
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
        var psi = new ProcessStartInfo(CliProcess.Binary!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--version");
        using var child = Process.Start(psi)!;
        _ = child.StandardError.ReadToEndAsync();
        await child.StandardOutput.ReadToEndAsync();
        Assert.True(child.WaitForExit(30_000));

        var error = await Assert.ThrowsAsync<CliException>(
            () => ServeCommand.WaitForWorkersAsync([child], [FreePort()]));
        Assert.Equal("worker 1 exited during startup: exit status: 0", error.Message);
    }
}
