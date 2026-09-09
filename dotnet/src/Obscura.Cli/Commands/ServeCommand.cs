using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Obscura.Cdp;
using Obscura.Cli.CommandLine;

namespace Obscura.Cli.Commands;

/// <summary>
/// The <c>serve</c> subcommand and the bare no-subcommand server path.
/// </summary>
public static class ServeCommand
{
    /// <summary>Dispatch for the <c>serve</c> subcommand.</summary>
    public static async Task RunAsync(CliArgs args, CliCommand.Serve serve)
    {
        // Fall back to OBSCURA_PROXY so a proxy can be supplied without putting
        // credentials on the command line. The multi-worker load balancer passes
        // the proxy to each worker this way.
        var proxy = CliOptions.MergeProxy(args.Proxy, serve.Proxy)
            ?? EnvOrNull("OBSCURA_PROXY");

        CliOptions.PrintBanner(serve.Port);
        if (serve.StorageDir is { } dir)
        {
            Log.Info($"Storage dir: {dir}");
        }
        if (proxy is { } configured)
        {
            Log.Info($"Using proxy: {configured}");
        }
        if (serve.UserAgent is { } ua)
        {
            Log.Info($"User-Agent: {ua}");
        }
        if (args.Stealth)
        {
            // The stealth TLS transport has no managed equivalent, so only the
            // tracker-blocking and identity halves are active here.
            Log.Info("Stealth mode enabled (tracker blocking)");
        }

        if (serve.Workers > 1)
        {
            Log.Info(string.Create(
                CultureInfo.InvariantCulture, $"{serve.Workers} worker processes"));
            await RunMultiWorkerServeAsync(
                serve.Port, serve.Host, serve.Workers, proxy, args.Stealth, serve.UserAgent)
                .ConfigureAwait(false);
            return;
        }

        await CdpServer.StartWithServeOptionsAndLimitAsync(
            serve.Port,
            serve.Host,
            proxy,
            args.Stealth,
            serve.UserAgent,
            serve.AllowFileAccess,
            serve.StorageDir,
            args.AllowPrivateNetwork,
            serve.MaxConnections).ConfigureAwait(false);
    }

    /// <summary>The bare <c>obscura</c> invocation with no subcommand.</summary>
    public static async Task RunDefaultAsync(CliArgs args)
    {
        CliOptions.PrintBanner(args.Port);
        if (args.Proxy is { } proxy)
        {
            Log.Info($"Using proxy: {proxy}");
        }
        await CdpServer.StartWithOptionsAsync(args.Port, args.Proxy, args.Stealth).ConfigureAwait(false);
    }

    /// <summary>
    /// Spawn <paramref name="workers"/> single-worker servers on consecutive
    /// ports and load balance across them round robin.
    /// </summary>
    /// <remarks>
    /// The balancer binds the requested host, not hardcoded loopback: with
    /// <c>--host 0.0.0.0</c> (a container with a mapped port) the single-worker
    /// path already binds all interfaces and the balancer must too, or the
    /// mapped port is refused from outside. Workers stay on loopback and are
    /// only reached by the balancer.
    /// </remarks>
    public static async Task RunMultiWorkerServeAsync(
        int port,
        string host,
        int workers,
        string? proxy,
        bool stealth,
        string? userAgent)
    {
        var exe = Environment.ProcessPath
            ?? throw new CliException("cannot locate the running executable to spawn workers");
        var children = new List<Process>();

        for (var i = 0; i < workers; i++)
        {
            var workerPort = port + 1 + i;
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("serve");
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(workerPort.ToString(CultureInfo.InvariantCulture));
            if (proxy is { } workerProxy)
            {
                // Pass the proxy (which may embed credentials) through the
                // environment, not argv: a --proxy flag is visible in `ps` to
                // any local user, while OBSCURA_PROXY is only readable by the
                // owner. The worker's serve path reads this as a fallback.
                psi.Environment["OBSCURA_PROXY"] = workerProxy;
            }
            if (userAgent is { } ua)
            {
                psi.ArgumentList.Add("--user-agent");
                psi.ArgumentList.Add(ua);
            }
            if (stealth)
            {
                psi.ArgumentList.Add("--stealth");
            }

            var child = Process.Start(psi)
                ?? throw new CliException($"failed to spawn worker on port {workerPort}");
            // Discard the child's output, as the reference redirects both to null.
            _ = child.StandardOutput.ReadToEndAsync();
            _ = child.StandardError.ReadToEndAsync();
            Log.Info(string.Create(CultureInfo.InvariantCulture, $"Worker {i + 1} on port {workerPort}"));
            children.Add(child);
        }

        await Task.Delay(500).ConfigureAwait(false);

        if (!IPAddress.TryParse(host, out var address))
        {
            throw new CliException($"invalid --host '{host}'");
        }
        var listener = new TcpListener(address, port);
        listener.Start();
        Log.Info(string.Create(
            CultureInfo.InvariantCulture, $"Load balancer on {host}:{port}, {workers} workers"));

        var nextWorker = 0;
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            var workerPort = port + 1 + (nextWorker % workers);
            nextWorker = nextWorker == int.MaxValue ? 0 : nextWorker + 1;
            _ = Task.Run(() => ProxyAsync(client, workerPort));
        }
    }

    private static async Task ProxyAsync(TcpClient client, int workerPort)
    {
        using (client)
        {
            var workerAddress = $"127.0.0.1:{workerPort.ToString(CultureInfo.InvariantCulture)}";
            TcpClient worker;
            try
            {
                worker = new TcpClient();
                await worker.ConnectAsync(IPAddress.Loopback, workerPort).ConfigureAwait(false);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                Log.Warn($"worker {workerAddress} unreachable: {error.Message}");
                try
                {
                    var stream = client.GetStream();
                    await stream.WriteAsync(
                        Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
                        .ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception write) when (write is IOException or ObjectDisposedException
                    or InvalidOperationException)
                {
                    // The client is gone too; nothing to report.
                }
                return;
            }

            using (worker)
            {
                try
                {
                    var clientStream = client.GetStream();
                    var workerStream = worker.GetStream();
                    // Both directions run until either side closes, which is what
                    // tokio's copy_bidirectional does.
                    await Task.WhenAny(
                        clientStream.CopyToAsync(workerStream),
                        workerStream.CopyToAsync(clientStream)).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException
                    or InvalidOperationException or SocketException)
                {
                    // A half-closed proxy connection is ordinary.
                }
            }
        }
    }

    private static string? EnvOrNull(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
