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
        // Rust logs "Stealth mode enabled (...)" here, in the Serve arm only.
        // Program.cs already logs the port's equivalent line, with the TLS gap
        // named, for every subcommand; repeating it here would put two stealth
        // lines on stderr for `-v serve --stealth` where Rust prints one.

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

        var listener = Bind(host, port);
        Log.Info(string.Create(
            CultureInfo.InvariantCulture, $"Load balancer on {host}:{port}, {workers} workers"));

        // Rust keeps this counter in a u16 and advances it with wrapping_add, so
        // the round-robin sequence resumes from 0 after 65535 connections rather
        // than from wherever a wider counter would land.
        ushort nextWorker = 0;
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            var workerPort = port + 1 + (nextWorker % workers);
            nextWorker = unchecked((ushort)(nextWorker + 1));
            Log.Debug(string.Create(
                CultureInfo.InvariantCulture,
                $"Routing {client.Client.RemoteEndPoint} to worker port {workerPort}"));
            _ = Task.Run(() => ProxyAsync(client, workerPort));
        }
    }

    /// <summary>
    /// Bind the balancer to <paramref name="host"/>, which may be a name.
    /// </summary>
    /// <remarks>
    /// Rust binds a <c>(&amp;str, u16)</c>, and tokio resolves it and tries each
    /// resolved address in turn, so <c>--host localhost</c> works there. A plain
    /// <see cref="IPAddress.TryParse"/> would reject it.
    /// </remarks>
    private static TcpListener Bind(string host, int port)
    {
        IPAddress[] candidates;
        if (IPAddress.TryParse(host, out var literal))
        {
            candidates = [literal];
        }
        else
        {
            try
            {
                candidates = Dns.GetHostAddresses(host);
            }
            catch (Exception error) when (error is SocketException or ArgumentException)
            {
                throw new CliException($"invalid --host '{host}': {error.Message}");
            }
        }

        Exception? last = null;
        foreach (var address in candidates)
        {
            var listener = new TcpListener(address, port);
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException error)
            {
                last = error;
                listener.Dispose();
            }
        }
        throw new CliException(
            $"cannot bind {host}:{port.ToString(CultureInfo.InvariantCulture)}"
            + (last is null ? ": no addresses resolved" : $": {last.Message}"));
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
                // tokio's copy_bidirectional copies each direction to EOF, shuts
                // the destination's write half down, and returns only once BOTH
                // directions are done. Completing on the first one instead would
                // tear down a connection whose peer is still sending: a client
                // that half-closes after its request would never get the reply.
                await Task.WhenAll(
                    PumpAsync(client, worker),
                    PumpAsync(worker, client)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One direction of the bidirectional copy, with the half-close.</summary>
    private static async Task PumpAsync(TcpClient from, TcpClient to)
    {
        try
        {
            await from.GetStream().CopyToAsync(to.GetStream()).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException
            or InvalidOperationException or SocketException)
        {
            // A half-closed proxy connection is ordinary.
        }
        finally
        {
            try
            {
                to.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                // The peer is already gone.
            }
        }
    }

    private static string? EnvOrNull(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
