using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura-cli/tests/obey_robots.rs</c>: the global
/// <c>--obey-robots</c> flag must reach both <c>fetch</c> and the
/// <c>scrape</c> worker, and must block before the target request is made.
/// </summary>
public sealed class ObeyRobotsTests
{
    /// <summary>
    /// A one-file HTTP fixture that records every path it is asked for, so the
    /// test can assert the target was never requested.
    /// </summary>
    private sealed class RobotsServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<string> _paths = [];
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _loop;

        public RobotsServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Address = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string Address { get; }

        public string Url(string path) => $"http://{Address}{path}";

        public List<string> Paths()
        {
            lock (_gate)
            {
                return [.. _paths];
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException
                    or SocketException)
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 2000;
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    var firstLine = Encoding.UTF8.GetString(buffer, 0, count)
                        .Split('\n').FirstOrDefault() ?? string.Empty;
                    var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var path = parts.Length > 1 ? parts[1] : "/";
                    lock (_gate)
                    {
                        _paths.Add(path);
                    }

                    var (contentType, body) = path == "/robots.txt"
                        ? ("text/plain", "User-agent: *\nDisallow: /private\n")
                        : ("text/html",
                            "<!doctype html><title>target reached</title><p>private target</p>");
                    var response =
                        $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\n" +
                        $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(response)).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or SocketException
                    or ObjectDisposedException)
                {
                    // A client that hangs up mid-request is not a test failure.
                }
            }
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The accept loop is already unwinding.
            }
            _shutdown.Dispose();
        }
    }

    // The launcher is produced by the Obscura.Cli build this project depends on,
    // so a missing binary is a broken build, not a reason to skip.
    private static void RequireCli() =>
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);

    [Fact]
    public void Obey_robots_is_global_and_blocks_fetch_before_target_request()
    {
        RequireCli();
        using var server = new RobotsServer();
        var run = CliProcess.Run(
            "fetch", "--obey-robots", "--allow-private-network", "--quiet", "--wait", "0",
            server.Url("/private/page"));

        Assert.False(run.Success, "disallowed fetch must fail");
        Assert.Contains("Blocked by robots.txt", run.StdErr, StringComparison.Ordinal);
        Assert.Equal(new List<string> { "/robots.txt" }, server.Paths());
    }

    [Fact]
    public void Obey_robots_reaches_scrape_worker_and_blocks_target_request()
    {
        RequireCli();
        using var server = new RobotsServer();
        var run = CliProcess.Run(
            "--obey-robots", "--allow-private-network", "scrape", "--quiet", "--timeout", "5",
            server.Url("/private/page"));

        Assert.True(run.Success, $"scrape reports per-URL errors as JSON; stderr: {run.StdErr}");
        Assert.Contains("Blocked by robots.txt", run.StdOut, StringComparison.Ordinal);
        Assert.Equal(new List<string> { "/robots.txt" }, server.Paths());
    }

    [Fact]
    public void Fetch_without_obey_robots_keeps_existing_navigation_behavior()
    {
        RequireCli();
        using var server = new RobotsServer();
        var run = CliProcess.Run(
            "--allow-private-network", "fetch", "--quiet", "--wait", "0",
            server.Url("/private/page"));

        Assert.True(run.Success, $"stderr: {run.StdErr}");
        Assert.Equal(new List<string> { "/private/page" }, server.Paths());
    }

    [Fact]
    public void Obey_robots_fetches_an_allowed_target_after_loading_policy()
    {
        RequireCli();
        using var server = new RobotsServer();
        var run = CliProcess.Run(
            "--obey-robots", "--allow-private-network", "fetch", "--quiet", "--wait", "0",
            server.Url("/public/page"));

        Assert.True(run.Success, $"stderr: {run.StdErr}");
        Assert.Equal(new List<string> { "/robots.txt", "/public/page" }, server.Paths());
    }
}
