using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
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
        HardDeadline.ArmHangExit();
        CliOptions.PrintBanner(serve.Port);
        if (serve.StorageDir is { } dir)
        {
            Log.Info($"Storage dir: {dir}");
        }
        if (proxy is { } configured)
        {
            Log.Info($"Using proxy: {CliOptions.RedactProxy(configured)}");
        }
        if (serve.UserAgent is { } ua)
        {
            Log.Info($"User-Agent: {ua}");
        }
        foreach (var directory in serve.FontDirs)
        {
            Log.Info($"Font dir: {directory}");
        }
        // SECURITY.md I1: optional TLS. Loaded here so a bad path fails before
        // anything is bound or spawned.
        X509Certificate2? certificate;
        try
        {
            certificate = PocketCalculator.Net.ServerTls.Resolve(serve.TlsCert, serve.TlsKey);
        }
        catch (InvalidOperationException error)
        {
            throw new CliException(error.Message);
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
                serve.Port, serve.Host, serve.Workers, new WorkerSettings
                {
                    Proxy = proxy,
                    Stealth = args.Stealth,
                    UserAgent = serve.UserAgent,
                    FontDirs = serve.FontDirs,
                    AllowFileAccess = serve.AllowFileAccess,
                    StorageDir = serve.StorageDir,
                    MaxConnections = serve.MaxConnections,
                    AllowPrivateNetwork = args.AllowPrivateNetwork,
                    V8Flags = args.V8Flags,
                    TlsCert = serve.TlsCert,
                    TlsKey = serve.TlsKey,
                    Certificate = certificate,
                })
                .ConfigureAwait(false);
            return;
        }

        await CdpServer.StartWithTlsAsync(
            serve.Port,
            serve.Host,
            proxy,
            args.Stealth,
            serve.UserAgent,
            serve.AllowFileAccess,
            serve.StorageDir,
            args.AllowPrivateNetwork,
            serve.MaxConnections,
            certificate).ConfigureAwait(false);
    }

    /// <summary>The bare <c>obscura</c> invocation with no subcommand.</summary>
    public static async Task RunDefaultAsync(CliArgs args)
    {
        CliOptions.PrintBanner(args.Port);
        if (args.Proxy is { } proxy)
        {
            Log.Info($"Using proxy: {CliOptions.RedactProxy(proxy)}");
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
        WorkerSettings settings)
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

        var launcher = ResolveWorkerLauncher(
            Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.Location);

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
                var psi = WorkerStartInfo(launcher, settings, workerPort, forwardedHost, forwardedPort);
                var child = Process.Start(psi)
                    ?? throw new CliException($"failed to spawn worker on port {workerPort}");
                // Discard the child's output, as the reference redirects both to null.
                _ = child.StandardOutput.ReadToEndAsync();
                _ = child.StandardError.ReadToEndAsync();
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Worker {i + 1} on port {workerPort}"));
                children.Add(child);
            }

            await WaitForWorkersAsync(children, workerPorts).ConfigureAwait(false);
            await VerifyWorkersAsync(workerPorts, token, settings.Certificate).ConfigureAwait(false);
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
        // Deviation (SECURITY.md L3): upstream proxies every accepted connection.
        // The balancer caps live proxied connections at --max-connections, the
        // same server-wide limit a single-worker server applies, and refuses the
        // rest with the worker's own 503 rather than holding two sockets per
        // client without bound.
        var maxConnections = Math.Max(1, settings.MaxConnections);
        var live = 0;
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            if (Interlocked.Increment(ref live) > maxConnections)
            {
                Interlocked.Decrement(ref live);
                Log.Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"refusing connection: at --max-connections ({maxConnections})"));
                _ = Task.Run(() => RefuseAsync(client));
                continue;
            }
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
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProxyAsync(client, workerPort).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref live);
                }
            });
        }
    }

    /// <summary>What a worker process is started with, besides its port.</summary>
    public sealed record WorkerSettings
    {
        /// <summary>The proxy, passed through the environment rather than argv.</summary>
        public string? Proxy { get; init; }

        /// <summary><c>--stealth</c>.</summary>
        public bool Stealth { get; init; }

        /// <summary><c>--user-agent</c>.</summary>
        public string? UserAgent { get; init; }

        /// <summary><c>--font-dir</c>, in order.</summary>
        public IReadOnlyList<string> FontDirs { get; init; } = [];

        /// <summary><c>--allow-file-access</c>.</summary>
        public bool AllowFileAccess { get; init; }

        /// <summary><c>--storage-dir</c>.</summary>
        public string? StorageDir { get; init; }

        /// <summary><c>--max-connections</c>.</summary>
        public int MaxConnections { get; init; } = CliDefinition.DefaultMaxConnections;

        /// <summary><c>--allow-private-network</c>.</summary>
        public bool AllowPrivateNetwork { get; init; }

        /// <summary><c>--v8-flags</c>.</summary>
        public string? V8Flags { get; init; }

        /// <summary><c>--tls-cert</c>, passed on so each worker terminates TLS itself.</summary>
        public string? TlsCert { get; init; }

        /// <summary><c>--tls-key</c>.</summary>
        public string? TlsKey { get; init; }

        /// <summary>The loaded certificate, which the balancer pins when it probes a worker.</summary>
        public X509Certificate2? Certificate { get; init; }
    }

    /// <summary>The program a worker is started as, and the arguments that precede its own.</summary>
    public sealed record WorkerLauncher(string FileName, IReadOnlyList<string> LeadingArguments);

    /// <summary>
    /// Work out how to start another copy of this CLI.
    /// </summary>
    /// <remarks>
    /// Deviation (SECURITY.md M1): upstream starts <c>current_exe() serve ...</c>,
    /// and so did the port with <see cref="Environment.ProcessPath"/>. Run as
    /// <c>dotnet pocket-calculator.dll</c>, that path is the <c>dotnet</c> host, so
    /// workers became <c>dotnet serve ...</c>: an error normally, and with the
    /// <c>dotnet-serve</c> global tool installed an unauthenticated file server on
    /// the balancer's port. Under the host the entry assembly is passed first; a
    /// host with no entry assembly to name is refused rather than guessed at.
    /// </remarks>
    public static WorkerLauncher ResolveWorkerLauncher(string? processPath, string? entryAssemblyPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            throw new CliException("cannot locate the running executable to spawn workers");
        }
        // Either separator, so the check does not depend on the host OS.
        var name = processPath[(processPath.LastIndexOfAny(['/', '\\']) + 1)..];
        if (!name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkerLauncher(processPath, []);
        }
        if (string.IsNullOrEmpty(entryAssemblyPath)
            || !entryAssemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException(
                "cannot spawn workers: running under the dotnet host with no entry assembly to pass it");
        }
        return new WorkerLauncher(processPath, [entryAssemblyPath]);
    }

    /// <summary>The start info for one worker on <paramref name="workerPort"/>.</summary>
    /// <remarks>
    /// Deviation (SECURITY.md I5): upstream passes a worker only the proxy, the
    /// user agent, the font directories and <c>--stealth</c>, so
    /// <c>--allow-file-access</c>, <c>--storage-dir</c>, <c>--max-connections</c>,
    /// <c>--allow-private-network</c> and <c>--v8-flags</c> were silently dropped
    /// under <c>--workers</c>. The port passes them all. Workers share the storage
    /// directory: the cookie file is written by an atomic rename, so the last
    /// worker to save wins but the file is never torn.
    /// </remarks>
    public static ProcessStartInfo WorkerStartInfo(
        WorkerLauncher launcher,
        WorkerSettings settings,
        int workerPort,
        string forwardedHost,
        string forwardedPort)
    {
        var psi = new ProcessStartInfo(launcher.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in launcher.LeadingArguments)
        {
            psi.ArgumentList.Add(argument);
        }
        // Root-level options precede the subcommand.
        if (settings.V8Flags is { } v8Flags)
        {
            psi.ArgumentList.Add("--v8-flags");
            psi.ArgumentList.Add(v8Flags);
        }
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(workerPort.ToString(CultureInfo.InvariantCulture));
        // Workers receive the client-facing Host header through the TCP
        // load balancer. Let their CDP security gate accept that public
        // authority while it continues to reject foreign hosts and browser
        // origins.
        psi.Environment["POCKETCALCULATOR_CDP_FORWARDED_HOST"] = forwardedHost;
        psi.Environment["POCKETCALCULATOR_CDP_FORWARDED_PORT"] = forwardedPort;
        if (settings.Proxy is { } workerProxy)
        {
            // Pass the proxy (which may embed credentials) through the
            // environment, not argv: a --proxy flag is visible in `ps` to
            // any local user, while POCKETCALCULATOR_PROXY is only readable by the
            // owner. The worker's serve path reads this as a fallback.
            psi.Environment["POCKETCALCULATOR_PROXY"] = workerProxy;
        }
        if (settings.UserAgent is { } ua)
        {
            psi.ArgumentList.Add("--user-agent");
            psi.ArgumentList.Add(ua);
        }
        // Each worker loads the fonts once for itself, as upstream passes the flags on.
        foreach (var directory in settings.FontDirs)
        {
            psi.ArgumentList.Add("--font-dir");
            psi.ArgumentList.Add(directory);
        }
        if (settings.Stealth)
        {
            psi.ArgumentList.Add("--stealth");
        }
        if (settings.AllowFileAccess)
        {
            psi.ArgumentList.Add("--allow-file-access");
        }
        if (settings.StorageDir is { } storageDir)
        {
            psi.ArgumentList.Add("--storage-dir");
            psi.ArgumentList.Add(storageDir);
        }
        psi.ArgumentList.Add("--max-connections");
        psi.ArgumentList.Add(settings.MaxConnections.ToString(CultureInfo.InvariantCulture));
        if (settings.AllowPrivateNetwork)
        {
            psi.ArgumentList.Add("--allow-private-network");
        }
        // TLS (SECURITY.md I1): the balancer proxies bytes, so each worker terminates
        // TLS. Paths only; POCKETCALCULATOR_TLS_CERT/_KEY reach workers through the
        // inherited environment.
        if (settings.TlsCert is { } tlsCert && settings.TlsKey is { } tlsKey)
        {
            psi.ArgumentList.Add("--tls-cert");
            psi.ArgumentList.Add(tlsCert);
            psi.ArgumentList.Add("--tls-key");
            psi.ArgumentList.Add(tlsKey);
        }
        return psi;
    }

    /// <summary>
    /// Check that every worker is a CDP server that enforces the token, before the
    /// balancer sends it a single client.
    /// </summary>
    /// <remarks>
    /// The balancer proxies bytes and authenticates nothing itself, so it relies on
    /// its workers for the token. A worker that is some other program listening on
    /// the port (SECURITY.md M1) would serve the public port unauthenticated; this
    /// refuses to start instead. The worker must answer a discovery request with
    /// the DevTools version document, and, with a token configured, refuse the
    /// same request without it with 401.
    /// </remarks>
    public static Task VerifyWorkersAsync(IReadOnlyList<int> workerPorts, string? token) =>
        VerifyWorkersAsync(workerPorts, token, null);

    /// <summary>
    /// As <see cref="VerifyWorkersAsync(IReadOnlyList{int}, string?)"/>, over TLS pinned to
    /// <paramref name="certificate"/> when the workers serve TLS.
    /// </summary>
    public static async Task VerifyWorkersAsync(
        IReadOnlyList<int> workerPorts, string? token, X509Certificate2? certificate)
    {
        for (var i = 0; i < workerPorts.Count; i++)
        {
            var authority = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{workerPorts[i]}");
            var authorized = await ProbeAsync(workerPorts[i], authority, token, certificate).ConfigureAwait(false);
            var ok = authorized.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal)
                && authorized.Contains("\"webSocketDebuggerUrl\"", StringComparison.Ordinal);
            if (ok && !string.IsNullOrEmpty(token))
            {
                var anonymous = await ProbeAsync(workerPorts[i], authority, null, certificate).ConfigureAwait(false);
                ok = anonymous.StartsWith("HTTP/1.1 401 ", StringComparison.Ordinal);
            }
            if (!ok)
            {
                throw new CliException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"worker {i + 1} on port {workerPorts[i]} is not an authenticated CDP server"));
            }
        }
    }

    private static async Task<string> ProbeAsync(
        int port, string authority, string? token, X509Certificate2? certificate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var probe = new TcpClient(AddressFamily.InterNetwork);
            await probe.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
            Stream stream = probe.GetStream();
            if (certificate is not null)
            {
                // The worker must present the very certificate this process loaded.
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = "127.0.0.1",
                            RemoteCertificateValidationCallback = (_, presented, _, _) =>
                                presented is not null
                                && presented.GetCertHashString() == certificate.GetCertHashString(),
                        },
                        cts.Token)
                    .ConfigureAwait(false);
                stream = ssl;
            }

            var request = $"GET /json/version HTTP/1.1\r\nHost: {authority}\r\n"
                + (string.IsNullOrEmpty(token) ? string.Empty : $"Authorization: Bearer {token}\r\n")
                + "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token).ConfigureAwait(false);
            // The version document is small; a bounded read keeps a hostile
            // listener from feeding the balancer without end.
            var buffer = new byte[16 * 1024];
            var received = 0;
            while (received < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(received), cts.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                received += n;
            }
            return Encoding.UTF8.GetString(buffer, 0, received);
        }
        catch (Exception error) when (error is SocketException or IOException or OperationCanceledException
            or System.Security.Authentication.AuthenticationException)
        {
            return string.Empty;
        }
    }

    /// <summary>Turn away a client that arrived while the balancer was at its limit.</summary>
    private static async Task RefuseAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n"
                    + "X-Obscura-Reason: max-connections\r\n\r\n")).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                client.Client.Shutdown(SocketShutdown.Send);
                // Drain briefly so the close is not a reset that discards the 503.
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                var buffer = new byte[4096];
                while (await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false) > 0)
                {
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException
                or InvalidOperationException or SocketException or OperationCanceledException)
            {
                // Best effort: the client sees a reset instead.
            }
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
