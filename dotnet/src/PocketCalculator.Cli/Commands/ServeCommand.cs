using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PocketCalculator.Cdp;
using PocketCalculator.Cli.CommandLine;

namespace PocketCalculator.Cli.Commands;

/// <summary>
/// The <c>serve</c> subcommand and the bare no-subcommand server path.
/// </summary>
public static class ServeCommand
{
    /// <summary>Dispatch for the <c>serve</c> subcommand.</summary>
    public static async Task RunAsync(CliArgs args, CliCommand.Serve serve)
    {
        // Fall back to POCKETCALCULATOR_PROXY so a proxy can be supplied without putting
        // credentials on the command line. The multi-worker load balancer passes
        // the proxy to each worker this way.
        var proxy = CliOptions.MergeProxy(args.Proxy, serve.Proxy)
            ?? EnvOrNull("POCKETCALCULATOR_PROXY");

        CliOptions.ConfigureFontDirectories(serve.FontDirs);
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
        foreach (var directory in serve.FontDirs)
        {
            Log.Info($"Font dir: {directory}");
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
                serve.Port, serve.Host, serve.Workers, proxy, args.Stealth, serve.UserAgent,
                serve.FontDirs)
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
    /// Spawn <paramref name="workers"/> single-worker servers on OS-assigned
    /// loopback ports and load balance across them round robin (upstream a156914).
    /// </summary>
    /// <remarks>
    /// The balancer binds the requested host, not hardcoded loopback: with
    /// <c>--host 0.0.0.0</c> (a container with a mapped port) the single-worker
    /// path already binds all interfaces and the balancer must too, or the
    /// mapped port is refused from outside. Workers stay on loopback and are
    /// only reached by the balancer, which tells them the client-facing
    /// authority through <c>POCKETCALCULATOR_CDP_FORWARDED_HOST</c>/<c>_PORT</c> so their
    /// Host check accepts the name a client used to reach the balancer.
    /// </remarks>
    public static async Task RunMultiWorkerServeAsync(
        int port,
        string host,
        int workers,
        string? proxy,
        bool stealth,
        string? userAgent,
        IReadOnlyList<string>? fontDirs = null)
    {
        // Deviation: upstream a156914 leaves this check to the workers, which see a
        // public forwarded authority, refuse to start without a token, and make the
        // balancer fail with "worker 1 exited during startup". The port refuses
        // here first, before anything is bound or spawned, with the message the
        // single-worker server gives. Workers inherit POCKETCALCULATOR_CDP_TOKEN
        // from this process's environment and enforce it too.
        var token = Environment.GetEnvironmentVariable("POCKETCALCULATOR_CDP_TOKEN");
        if (!string.IsNullOrEmpty(token) && System.Text.Encoding.UTF8.GetByteCount(token) < 32)
        {
            throw new CliException("POCKETCALCULATOR_CDP_TOKEN must be at least 32 bytes");
        }
        var loopback = IPAddress.TryParse(host, out var literal)
            ? IPAddress.IsLoopback(literal)
            : host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (!loopback && string.IsNullOrEmpty(token))
        {
            throw new CliException(
                "refusing to expose CDP without authentication; set POCKETCALCULATOR_CDP_TOKEN to at least 32 bytes");
        }

        var exe = Environment.ProcessPath
            ?? throw new CliException("cannot locate the running executable to spawn workers");

        // Claim the public port before starting children so another process
        // cannot take it during worker startup.
        var listener = Bind(host, port);
        var bound = (IPEndPoint)listener.LocalEndpoint;

        // Internal worker ports are implementation details. Asking the OS for free
        // ports avoids assuming that every port adjacent to the public one is
        // available, or that `port + workers` cannot overflow.
        var reservations = new List<Socket>(workers);
        var workerPorts = new List<int>(workers);
        var children = new List<Process>(workers);
        try
        {
            for (var i = 0; i < workers; i++)
            {
                var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                reservations.Add(reservation);
                reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                workerPorts.Add(((IPEndPoint)reservation.LocalEndPoint!).Port);
            }

            // Deviation: upstream forwards the --host string and --port as given.
            // A worker parses the host as an IP literal, so `--host localhost` made
            // every upstream worker exit at startup, and `--port 0` forwarded port
            // 0. The port forwards the address and port the balancer actually
            // bound, which are the same values for an IP literal and a fixed port.
            var forwardedHost = bound.Address.ToString();
            var forwardedPort = bound.Port.ToString(CultureInfo.InvariantCulture);

            for (var i = 0; i < workers; i++)
            {
                var workerPort = workerPorts[i];
                reservations[i].Dispose();
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("serve");
                psi.ArgumentList.Add("--port");
                psi.ArgumentList.Add(workerPort.ToString(CultureInfo.InvariantCulture));
                // Workers receive the client-facing Host header through the TCP
                // load balancer. Let their CDP security gate accept that public
                // authority while it continues to reject foreign hosts and browser
                // origins.
                psi.Environment["POCKETCALCULATOR_CDP_FORWARDED_HOST"] = forwardedHost;
                psi.Environment["POCKETCALCULATOR_CDP_FORWARDED_PORT"] = forwardedPort;
                if (proxy is { } workerProxy)
                {
                    // Pass the proxy (which may embed credentials) through the
                    // environment, not argv: a --proxy flag is visible in `ps` to
                    // any local user, while POCKETCALCULATOR_PROXY is only readable by the
                    // owner. The worker's serve path reads this as a fallback.
                    psi.Environment["POCKETCALCULATOR_PROXY"] = workerProxy;
                }
                if (userAgent is { } ua)
                {
                    psi.ArgumentList.Add("--user-agent");
                    psi.ArgumentList.Add(ua);
                }
                // Each worker loads the fonts once for itself, as upstream passes the flags on.
                foreach (var directory in fontDirs ?? [])
                {
                    psi.ArgumentList.Add("--font-dir");
                    psi.ArgumentList.Add(directory);
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

            await WaitForWorkersAsync(children, workerPorts).ConfigureAwait(false);
        }
        catch
        {
            // Deviation: when startup fails, upstream returns the error and leaves
            // the workers it already spawned running, orphaned on their loopback
            // ports. The port stops them.
            foreach (var reservation in reservations)
            {
                reservation.Dispose();
            }
            foreach (var child in children)
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (Exception error) when (error is InvalidOperationException
                    or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }
            }
            listener.Dispose();
            throw;
        }

        Log.Info(string.Create(
            CultureInfo.InvariantCulture, $"Load balancer on {host}:{port}, {workers} workers"));

        // Rust advances a usize counter with wrapping_add; a ulong wraps the same
        // way for any count a process can accept.
        ulong nextWorker = 0;
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            try
            {
                client.NoDelay = true;
            }
            catch (SocketException error)
            {
                Log.Warn($"client {client.Client.RemoteEndPoint} TCP_NODELAY failed: {error.Message}");
            }
            var workerPort = workerPorts[(int)(nextWorker % (ulong)workerPorts.Count)];
            nextWorker = unchecked(nextWorker + 1);
            Log.Debug(string.Create(
                CultureInfo.InvariantCulture,
                $"Routing {client.Client.RemoteEndPoint} to worker port {workerPort}"));
            // Upstream peeks the first bytes of each request on the accept loop to
            // tell `/json` from a WebSocket (both are then proxied the same way)
            // and, since a156914, drops a client that closes before sending
            // anything. The port has never peeked: it proxies from the first byte
            // on its own task, so a silent or early-closed client cannot stall or
            // end the accept loop, and it simply sees an empty exchange.
            _ = Task.Run(() => ProxyAsync(client, workerPort));
        }
    }

    /// <summary>
    /// Wait until every worker has bound its control port, polling each with a
    /// connect, for at most five seconds in all (upstream a156914, which replaced
    /// a fixed 500 ms sleep that dominated multi-worker startup).
    /// </summary>
    /// <remarks>
    /// A worker that exits first fails startup with
    /// <c>worker N exited during startup: exit status: C</c>; one still not
    /// listening at the deadline fails it with the connect error, as upstream
    /// returns the io error.
    /// </remarks>
    public static async Task WaitForWorkersAsync(IReadOnlyList<Process> children, IReadOnlyList<int> workerPorts)
    {
        var deadline = Stopwatch.GetTimestamp() + (5 * Stopwatch.Frequency);
        for (var i = 0; i < children.Count; i++)
        {
            while (true)
            {
                if (children[i].HasExited)
                {
                    throw new CliException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"worker {i + 1} exited during startup: exit status: {children[i].ExitCode}"));
                }

                try
                {
                    using var probe = new TcpClient(AddressFamily.InterNetwork);
                    await probe.ConnectAsync(IPAddress.Loopback, workerPorts[i]).ConfigureAwait(false);
                    break;
                }
                catch (SocketException) when (Stopwatch.GetTimestamp() < deadline)
                {
                    await Task.Delay(5).ConfigureAwait(false);
                }
                catch (SocketException error)
                {
                    throw new CliException(error.Message, error);
                }
            }
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
                try
                {
                    worker.NoDelay = true;
                }
                catch (SocketException error)
                {
                    // Upstream a156914 drops the connection when this fails.
                    Log.Warn($"worker {workerAddress} TCP_NODELAY failed: {error.Message}");
                    return;
                }

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
