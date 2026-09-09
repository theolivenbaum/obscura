using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Obscura.Browser;
using Obscura.Js.Ops;
using Obscura.Js.Runtime;
using Obscura.Net;

namespace Obscura.Cdp;

/// <summary>The CDP WebSocket server: connections, sessions, targets, limits.</summary>
public static partial class CdpServer
{
    /// <summary>
    /// Cap on <em>live</em> CDP connections, each of which costs one OS thread
    /// and its own V8 isolates.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxPendingWsHandoffs"/> bounds only the handoff queue;
    /// connections that have already been handed off are unbounded without this.
    /// 128 matches that bound and is well above any real client fan-out
    /// (Playwright and Puppeteer use one connection per browser). Threads are
    /// what this actually bounds: 128 idle connections cost 146 threads, 33.2 GiB
    /// of reserved address space and 51 MiB resident, and nearly all of that
    /// 33.2 GiB is V8's process-wide sandbox, which is there at zero connections.
    /// Override with <c>--max-connections</c>.
    /// </remarks>
    public const int DefaultMaxConnections = 128;

    /// <summary>
    /// The deferral queue in the interception path must be bounded so a stalled
    /// navigation cannot exhaust memory. At the cap we return an explicit error
    /// response rather than silently dropping. (PR #36 comment 4341743194.)
    /// </summary>
    private const int MaxDeferredMessages = 256;

    /// <summary>
    /// The WS-stream forwarding channel must also be bounded: if the connection
    /// scheduler stalls, the accept thread keeps pushing sockets into the queue.
    /// An unbounded channel would let that queue grow without limit. With a
    /// bounded capacity, when the scheduler is saturated the accept thread closes
    /// the new connection on the spot instead of buffering it; the kernel TCP
    /// backlog still absorbs short-term spikes, but a long-term stall now fails
    /// loudly at accept time rather than silently piling up file descriptors.
    /// </summary>
    private const int MaxPendingWsHandoffs = 128;

    /// <summary>
    /// How long shutdown waits for connection threads to finish before persisting
    /// the cookie jar. Well under the 10s <c>docker stop</c> gives us before
    /// SIGKILL.
    /// </summary>
    private const int ShutdownDrainMs = 3_000;

    /// <summary>
    /// Sent to a client that arrives while the server is at
    /// <c>max_connections</c>, in place of dropping the socket unexplained. The
    /// client sees a refusal it can retry rather than a bare connection reset.
    /// </summary>
    private const string ConnectionLimitResponse =
        "HTTP/1.1 503 Service Unavailable\r\n" +
        "Content-Length: 0\r\nConnection: close\r\n" +
        "X-Obscura-Reason: max-connections\r\n\r\n";

    private const int HttpPeekBuf = 4096;

    /// <summary>
    /// How long a freshly accepted connection may sit without sending a request
    /// head before the accept thread drops it. Real clients send their handshake
    /// immediately after connecting; only probes and preconnects linger.
    /// </summary>
    private static readonly TimeSpan SilentConnectionTtl = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often the accept thread re-polls parked connections that have not sent
    /// a request head yet. Also the retry delay on a persistent accept error, so
    /// it cannot become a log flood. Only paid while something is actually parked;
    /// an idle server blocks in accept and polls nothing.
    /// </summary>
    private static readonly TimeSpan AcceptPollInterval = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Cap on connections parked without a request head. Bounds the accept
    /// thread's polling work and the server's file-descriptor use under probe
    /// floods.
    /// </summary>
    private const int MaxSilentPending = 256;

    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static Task StartAsync(int port, CancellationToken cancellationToken = default) =>
        StartWithOptionsAsync(port, null, false, cancellationToken);

    public static Task StartWithOptionsAsync(
        int port,
        string? proxy,
        bool stealth,
        CancellationToken cancellationToken = default) =>
        StartWithFullOptionsAsync(port, proxy, stealth, null, null, cancellationToken);

    public static Task StartWithFullOptionsAsync(
        int port,
        string? proxy,
        bool stealth,
        string? userAgent,
        string? storageDir,
        CancellationToken cancellationToken = default) =>
        StartWithHostAsync(port, "127.0.0.1", proxy, stealth, userAgent, storageDir, cancellationToken);

    public static Task StartWithHostAsync(
        int port,
        string host,
        string? proxy,
        bool stealth,
        string? userAgent,
        string? storageDir,
        CancellationToken cancellationToken = default) =>
        StartWithHostAndSecurityAsync(
            port, host, proxy, stealth, userAgent, false, storageDir, cancellationToken);

    public static Task StartWithHostAndSecurityAsync(
        int port,
        string host,
        string? proxy,
        bool stealth,
        string? userAgent,
        bool allowFileAccess,
        string? storageDir,
        CancellationToken cancellationToken = default) =>
        StartWithFullServeOptionsAsync(
            port, host, proxy, stealth, userAgent, allowFileAccess, storageDir, false,
            cancellationToken);

    /// <summary>
    /// The same entry point as <see cref="StartWithHostAndSecurityAsync"/>, kept
    /// because <c>server.rs</c> exposes both names.
    /// </summary>
    public static Task StartWithHostSecurityAndStorageAsync(
        int port,
        string host,
        string? proxy,
        bool stealth,
        string? userAgent,
        bool allowFileAccess,
        string? storageDir,
        CancellationToken cancellationToken = default) =>
        StartWithFullServeOptionsAsync(
            port, host, proxy, stealth, userAgent, allowFileAccess, storageDir, false,
            cancellationToken);

    /// <summary>
    /// Full serve entry point that also accepts <paramref name="allowPrivateNetwork"/>
    /// (issue #33). Older entry points default it to false so existing callers and
    /// public API consumers are unaffected.
    /// </summary>
    public static Task StartWithFullServeOptionsAsync(
        int port,
        string host,
        string? proxy,
        bool stealth,
        string? userAgent,
        bool allowFileAccess,
        string? storageDir,
        bool allowPrivateNetwork,
        CancellationToken cancellationToken = default) =>
        StartWithServeOptionsAndLimitAsync(
            port, host, proxy, stealth, userAgent, allowFileAccess, storageDir,
            allowPrivateNetwork, DefaultMaxConnections, cancellationToken);

    /// <summary>
    /// As <see cref="StartWithFullServeOptionsAsync"/>, with an explicit cap on
    /// live CDP connections. Each connection owns an OS thread and its pages' V8
    /// isolates, so this is what bounds the server's thread and memory footprint.
    /// </summary>
    public static async Task StartWithServeOptionsAndLimitAsync(
        int port,
        string host,
        string? proxy,
        bool stealth,
        string? userAgent,
        bool allowFileAccess,
        string? storageDir,
        bool allowPrivateNetwork,
        int maxConnections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!IPAddress.TryParse(host, out var ip))
        {
            throw new ArgumentException($"invalid --host '{host}'", nameof(host));
        }

        // Issue #62: the HTTP control plane (/json/version, /json) must remain
        // reachable even while V8 JS evaluation blocks a connection's thread.
        //
        // We use a dedicated OS thread with a blocking listener so the kernel's
        // accept backlog is always drained promptly. HTTP endpoints are served
        // directly with blocking I/O; WebSocket connections are forwarded to
        // their own connection thread for CDP processing.
        Socket listener = new(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(new IPEndPoint(ip, port));
            listener.Listen(512);
        }
        catch (SocketException e)
        {
            listener.Dispose();
            throw new IOException($"bind {host}:{port}: {e.Message}", e);
        }

        CdpLog.Info($"Obscura CDP server listening on ws://{host}:{port}");
        CdpLog.Info($"DevTools endpoint: ws://{host}:{port}/devtools/browser");
        if (allowFileAccess)
        {
            CdpLog.Info(
                "file:// navigation enabled (--allow-file-access). Do not expose this port to untrusted networks.");
        }

        var handoff = Channel.CreateBounded<Socket>(new BoundedChannelOptions(MaxPendingWsHandoffs)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        // Ctrl-C / graceful shutdown coordination. One source for the whole
        // server: it stops the accept thread and wakes every connection
        // processor.
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var signals = InstallSignalHandlers(shutdown);

        var acceptThread = new Thread(() => AcceptLoop(listener, port, handoff.Writer, shutdown.Token))
        {
            IsBackground = true,
            Name = "obscura-cdp-accept",
        };
        acceptThread.Start();

        // This context is a configuration and persistence template. Each
        // WebSocket gets an isolated copy with its own cookie jar and HTTP client
        // (#449), while the thread-per-connection layout from #430 still confines
        // that connection's V8 isolates to one OS thread.
        var template = BrowserContext.WithStorageAndNetwork(
            "default", proxy, stealth, userAgent, storageDir, allowPrivateNetwork);
        template.AllowFileAccess = allowFileAccess;

        // Persistence is deliberately separate from the connection template.
        // Cookie deltas are merged here, but new connections always clone the
        // immutable startup snapshot and can never inherit another live client's
        // session state.
        var persistence = template.IsolatedCopy("persistence", true);
        var persistenceLock = new object();

        // Force V8 and its process-global tables to initialize once on this
        // thread before any connection thread creates an isolate. Creating the
        // very first isolate off the main thread is what #430 traced its abort
        // to; building and dropping one runtime here does that setup singly.
        using (var warmup = new ObscuraJsRuntime())
        {
            _ = warmup;
        }

        // Live CDP connections, incremented on accept and decremented when a
        // connection thread exits.
        var liveConnections = new ConnectionSlots();
        CdpLog.Info($"Connection limit: {maxConnections}");

        // Accept loop: hand each WebSocket connection to its own OS thread so its
        // pages' isolates live on a dedicated thread.
        try
        {
            while (true)
            {
                Socket socket;
                try
                {
                    socket = await handoff.Reader.ReadAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                // Nagle off before the socket moves to the connection thread. CDP
                // exchanges many small (~100-byte) frames during newPage() and
                // navigate; with Nagle on, each small write waits on an ACK or the
                // 40ms delayed-ACK timer (~90ms on newPage, ~30ms on goto).
                try
                {
                    // Back to blocking mode: the accept thread put every fresh
                    // socket in non-blocking mode so it could poll for a request
                    // head without parking, and .NET's async socket I/O is built
                    // on blocking-mode handles (NetworkStream rejects the other
                    // kind outright).
                    socket.Blocking = true;
                    socket.NoDelay = true;
                }
                catch (SocketException e)
                {
                    CdpLog.Error($"set_nodelay on WS stream: {e.Message}");
                }
                catch (ObjectDisposedException)
                {
                    continue;
                }

                // Reserve a slot before spawning. The compare-and-swap keeps the
                // check atomic against the accept thread handing off the next
                // socket concurrently.
                if (!liveConnections.TryReserve(maxConnections))
                {
                    CdpLog.Warn($"refusing CDP connection: at --max-connections ({maxConnections})");
                    RefuseConnection(socket);
                    continue;
                }

                RunConnection(
                    socket, template, persistence, persistenceLock, shutdown.Token, liveConnections);
            }
        }
        finally
        {
            listener.Dispose();
        }

        // Server is shutting down. Connection threads are detached, so saving the
        // jar right here would race them: a connection still writing a Set-Cookie
        // loses it, and the process then exits and kills the thread mid-flight.
        // Draining here restores the ordering the single processor used to have.
        // Every processor has already been woken, so this is bounded in practice;
        // the deadline only covers a connection wedged in V8, where its own
        // command watchdog is the backstop.
        var drainDeadline = Environment.TickCount64 + ShutdownDrainMs;
        while (true)
        {
            var live = liveConnections.Live;
            if (live == 0)
            {
                break;
            }

            if (Environment.TickCount64 >= drainDeadline)
            {
                CdpLog.Warn(
                    $"shutting down with {live} connection(s) still live after {ShutdownDrainMs}ms; " +
                    "cookies they write from here are lost");
                break;
            }

            await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
        }

        persistence.SaveCookies();
    }

    private static IDisposable InstallSignalHandlers(CancellationTokenSource shutdown)
    {
        // Watches SIGTERM as well as Ctrl-C so `docker stop` / `kill` also flush
        // cookies (issue #333).
        List<IDisposable> registrations = [];
        void Trip()
        {
            try
            {
                shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        try
        {
            registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
            {
                ctx.Cancel = true;
                Trip();
            }));
            registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
            {
                ctx.Cancel = true;
                Trip();
            }));
        }
        catch (PlatformNotSupportedException)
        {
        }

        return new Registrations(registrations);
    }

    private sealed class Registrations(List<IDisposable> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }

    /// <summary>The live-connection counter and its compare-and-swap reservation.</summary>
    internal sealed class ConnectionSlots
    {
        private int _live;

        internal int Live => Volatile.Read(ref _live);

        internal bool TryReserve(int max)
        {
            while (true)
            {
                var current = Volatile.Read(ref _live);
                if (current >= max)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _live, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        internal void Release() => Interlocked.Decrement(ref _live);
    }

    /// <summary>
    /// The dedicated accept thread: drains the kernel backlog immediately and
    /// handles the HTTP endpoints with blocking I/O so they never contend with a
    /// connection's V8 work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A connection that is accepted but never sends its request head
    /// (speculative browser preconnects, port probes, slow-loris clients) must not
    /// be able to park this single thread in a blocking read: every later
    /// connection, including CDP clients like Playwright's <c>connectOverCDP</c>,
    /// would then sit in the kernel backlog unanswered until its own connect
    /// timeout (issue #715). The thread therefore never blocks on a
    /// <em>stream</em>: undecided connections are parked and re-polled every
    /// <see cref="AcceptPollInterval"/>, and dropped once they outlive
    /// <see cref="SilentConnectionTtl"/> without sending a request head.
    /// </para>
    /// <para>
    /// While nothing is parked the thread blocks in accept itself, the pre-#715
    /// fast path: zero added latency for the next connection and no CPU while
    /// idle. Blocking on the listener is safe, since it waits for the kernel
    /// rather than for client bytes. While something is parked, the listener is
    /// drained without blocking at least once per 1 ms poll round, far above any
    /// real connect rate, so the kernel backlog cannot overflow under a burst.
    /// </para>
    /// </remarks>
    private static void AcceptLoop(
        Socket listener,
        int port,
        ChannelWriter<Socket> handoff,
        CancellationToken shutdown)
    {
        List<(Socket Socket, long Since)> pending = [];
        while (!shutdown.IsCancellationRequested)
        {
            if (pending.Count == 0)
            {
                // Fast path: nothing parked, block until a connection arrives,
                // then classify it in the sweep below.
                try
                {
                    listener.Blocking = true;
                    var accepted = listener.Accept();
                    accepted.Blocking = false;
                    pending.Add((accepted, Environment.TickCount64));
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException e)
                {
                    if (shutdown.IsCancellationRequested)
                    {
                        return;
                    }

                    CdpLog.Error($"Accept error: {e.SocketErrorCode}");
                    // A persistent error (for example EMFILE) must not turn into
                    // a log flood while the thread idles.
                    Thread.Sleep(AcceptPollInterval);
                }
            }
            else
            {
                // Drain everything the kernel has already queued for us.
                while (true)
                {
                    try
                    {
                        listener.Blocking = false;
                        var accepted = listener.Accept();
                        accepted.Blocking = false;
                        if (pending.Count < MaxSilentPending)
                        {
                            pending.Add((accepted, Environment.TickCount64));
                        }
                        else
                        {
                            CdpLog.Warn(
                                $"dropping connection: {MaxSilentPending} connections parked without a request head");
                            accepted.Dispose();
                        }
                    }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException e)
                    {
                        CdpLog.Error($"Accept error: {e.SocketErrorCode}");
                        break;
                    }
                }
            }

            // Give every parked connection a chance to speak; keep the ones still
            // silent and inside the TTL, dispatch the ones with a request head.
            // Closing a socket here is what drops a connection.
            var round = pending;
            pending = [];
            foreach (var (socket, since) in round)
            {
                if (Environment.TickCount64 - since >= (long)SilentConnectionTtl.TotalMilliseconds)
                {
                    socket.Dispose();
                    continue;
                }

                switch (PeekRequestHead(socket))
                {
                    case { Kind: PeekKind.NotReady }:
                        pending.Add((socket, since));
                        break;
                    case { Kind: PeekKind.Closed }:
                        socket.Dispose();
                        break;
                    case { Kind: PeekKind.Head, Head: var head }:
                        if (!AcceptDispatch(socket, port, handoff, head!))
                        {
                            socket.Dispose();
                        }

                        break;
                }
            }

            if (pending.Count != 0)
            {
                Thread.Sleep(AcceptPollInterval);
            }
        }

        foreach (var (socket, _) in pending)
        {
            socket.Dispose();
        }
    }

    private enum PeekKind
    {
        /// <summary>No classifiable request head yet; poll again next accept round.</summary>
        NotReady,

        /// <summary>Peer went away without sending a full head.</summary>
        Closed,

        /// <summary>A classifiable request head.</summary>
        Head,
    }

    private readonly record struct PeekStatus(PeekKind Kind, string? Head);

    /// <summary>
    /// Peek, without consuming, at a freshly accepted connection's request head.
    /// </summary>
    /// <remarks>
    /// <c>GET</c> requests are only classified once the terminating blank line has
    /// arrived, so <c>/json</c> route matching never sees a truncated head;
    /// anything that cannot be a <c>GET</c> is handed over immediately so
    /// non-HTTP garbage still gets a prompt rejection instead of waiting out the
    /// silent-connection TTL.
    /// </remarks>
    private static PeekStatus PeekRequestHead(Socket socket)
    {
        var buffer = new byte[HttpPeekBuf];
        int n;
        try
        {
            n = socket.Receive(buffer, 0, buffer.Length, SocketFlags.Peek);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
        {
            return new PeekStatus(PeekKind.NotReady, null);
        }
        catch (SocketException)
        {
            return new PeekStatus(PeekKind.Closed, null);
        }
        catch (ObjectDisposedException)
        {
            return new PeekStatus(PeekKind.Closed, null);
        }

        if (n == 0)
        {
            return new PeekStatus(PeekKind.Closed, null);
        }

        var head = buffer.AsSpan(0, n);
        if (n >= 4 && !head[..4].SequenceEqual("GET "u8))
        {
            return new PeekStatus(PeekKind.Head, Lossy(head));
        }

        // A head that overflows the peek buffer is classified with what arrived,
        // matching the pre-polling behavior for oversized headers.
        var complete = n == HttpPeekBuf || head.IndexOf("\r\n\r\n"u8) >= 0;
        return complete
            ? new PeekStatus(PeekKind.Head, Lossy(head))
            : new PeekStatus(PeekKind.NotReady, null);
    }

    /// <summary>
    /// <c>String::from_utf8_lossy</c>: invalid sequences become U+FFFD instead of
    /// throwing, because the bytes are attacker-controlled.
    /// </summary>
    private static string Lossy(ReadOnlySpan<byte> bytes) =>
        new UTF8Encoding(false, false).GetString(bytes);

    /// <summary>
    /// Dispatch a freshly accepted connection on the dedicated accept thread.
    /// </summary>
    /// <remarks>
    /// The connection's request head has already been peeked by the accept loop
    /// and is passed in as <paramref name="head"/>:
    /// HTTP (<c>GET /json/*</c>) is served synchronously with blocking I/O so the
    /// response is never stalled by a busy connection; a WebSocket is forwarded
    /// to its own connection thread. False means the socket must be closed.
    /// </remarks>
    private static bool AcceptDispatch(
        Socket socket,
        int port,
        ChannelWriter<Socket> handoff,
        string head)
    {
        string? endpoint = null;
        if (head.Contains("/json/version", StringComparison.Ordinal))
        {
            endpoint = "version";
        }
        else if (head.Contains("/json/list", StringComparison.Ordinal) ||
                 head.Contains("/json\r\n", StringComparison.Ordinal) ||
                 head.Contains("/json HTTP", StringComparison.Ordinal))
        {
            endpoint = "list";
        }
        else if (head.Contains("/json/protocol", StringComparison.Ordinal))
        {
            endpoint = "protocol";
        }

        if (endpoint is not null)
        {
            // The request head is already sitting in the kernel receive buffer;
            // switch back to blocking mode for the synchronous /json serve.
            try
            {
                socket.Blocking = true;
                HandleHttpJsonBlocking(socket, port, endpoint);
            }
            catch (SocketException e)
            {
                CdpLog.Error($"Accept dispatch error: {e.SocketErrorCode}");
            }
            catch (IOException e)
            {
                CdpLog.Error($"Accept dispatch error: {e.Message}");
            }
            catch (ObjectDisposedException)
            {
            }

            return false;
        }

        // Fall through: a GET that is not a /json endpoint is treated as a
        // WebSocket upgrade (Chromium DevTools clients issue GET with
        // Upgrade: websocket).
        //
        // If the bounded channel is full the connection scheduler is saturated:
        // drop the connection cleanly rather than blocking the accept thread,
        // which would freeze the HTTP control plane this whole layout exists to
        // keep alive. The dropped socket closes itself; the client sees a reset
        // and can retry.
        if (handoff.TryWrite(socket))
        {
            return true;
        }

        CdpLog.Warn(
            $"WS handoff channel full ({MaxPendingWsHandoffs}); dropping new WebSocket connection");
        return false;
    }

    /// <summary>Serve an HTTP <c>/json/*</c> endpoint with blocking I/O on the accept thread.</summary>
    private static void HandleHttpJsonBlocking(Socket socket, int port, string endpoint)
    {
        var scratch = new byte[4096];
        _ = socket.Receive(scratch, 0, scratch.Length, SocketFlags.None);

        var body = endpoint switch
        {
            "version" => CdpJson.SerializePretty(new JsonObject
            {
                ["Browser"] = "Chrome/145.0.0.0",
                ["Protocol-Version"] = "1.3",
                ["User-Agent"] =
                    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36",
                ["V8-Version"] = "14.5.0.0",
                ["WebKit-Version"] = "537.36",
                ["webSocketDebuggerUrl"] = $"ws://127.0.0.1:{port}/devtools/browser",
            }),
            "list" => CdpJson.SerializePretty(new JsonArray
            {
                new JsonObject
                {
                    ["description"] = "",
                    ["devtoolsFrontendUrl"] = "",
                    ["id"] = "page-1",
                    ["title"] = "",
                    ["type"] = "page",
                    ["url"] = "about:blank",
                    ["webSocketDebuggerUrl"] = $"ws://127.0.0.1:{port}/devtools/page/page-1",
                },
            }),
            "protocol" => CdpJson.SerializePretty(new JsonObject
            {
                ["version"] = new JsonObject { ["major"] = "1", ["minor"] = "3" },
            }),
            _ => "{}",
        };

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var response = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Connection: close\r\n\r\n");
        socket.Send(response);
        socket.Send(bodyBytes);
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
    }

    /// <summary>Turn away a connection that arrived while the server was at its limit.</summary>
    /// <remarks>
    /// Best-effort: the socket is going away either way, so a failed write just
    /// means the client sees a reset instead of the 503.
    /// </remarks>
    private static void RefuseConnection(Socket socket)
    {
        try
        {
            socket.Blocking = true;

            // The accept thread only peeked at the WebSocket handshake. Consume
            // its bounded HTTP header before closing: Windows resets a socket
            // closed with unread receive data, which can discard the queued 503.
            socket.ReceiveTimeout = 100;
            var request = new byte[HttpPeekBuf];
            var received = 0;
            while (received < request.Length)
            {
                int n;
                try
                {
                    n = socket.Receive(request, received, request.Length - received, SocketFlags.None);
                }
                catch (SocketException)
                {
                    break;
                }

                if (n == 0)
                {
                    break;
                }

                received += n;
                if (request.AsSpan(0, received).IndexOf("\r\n\r\n"u8) >= 0)
                {
                    break;
                }
            }

            socket.Send(Encoding.UTF8.GetBytes(ConnectionLimitResponse));
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            socket.Dispose();
        }
    }

    /// <summary>
    /// Run one WebSocket connection on its own OS thread: a single-threaded
    /// scheduler hosting this connection's processor (with its own
    /// <see cref="CdpContext"/> and pages) and its frame reader.
    /// </summary>
    /// <remarks>
    /// Confining a connection's pages to one thread is what removes the #430
    /// abort; the interception handshake and the navigation task all stay on this
    /// one thread, so no cross-thread V8 plumbing is needed.
    /// </remarks>
    private static void RunConnection(
        Socket socket,
        BrowserContext template,
        BrowserContext persistence,
        object persistenceLock,
        CancellationToken shutdown,
        ConnectionSlots slots)
    {
        var thread = new Thread(() =>
        {
            // Releases the slot reserved by the accept loop however this thread
            // exits: clean close, error, or fault. A plain decrement at the end
            // would leak slots on the early returns below until the cap wedged
            // the server shut.
            try
            {
                ConnectionBody(socket, template, persistence, persistenceLock, shutdown);
            }
            catch (Exception e)
            {
                CdpLog.Error($"connection thread failed: {e.Message}");
            }
            finally
            {
                slots.Release();
            }
        })
        {
            IsBackground = true,
            Name = "obscura-cdp-conn",
        };

        try
        {
            thread.Start();
        }
        catch (Exception e)
        {
            // The closure never ran, so its slot release never happened: release
            // here or the cap drifts down on every failed spawn.
            CdpLog.Error($"connection thread spawn failed: {e.Message}");
            slots.Release();
            socket.Dispose();
        }
    }

    private static void ConnectionBody(
        Socket socket,
        BrowserContext template,
        BrowserContext persistence,
        object persistenceLock,
        CancellationToken shutdown)
    {
        var defaultContext = template.IsolatedCopy("default", true);
        var initialCookies = defaultContext.CookieJar.GetAllCookies();

        try
        {
            SingleThreadedSynchronizationContext.Run(async () =>
            {
                using var stream = new NetworkStream(socket, ownsSocket: true);
                var messages = Channel.CreateUnbounded<ServerMessage>(
                    new UnboundedChannelOptions { SingleReader = true });
                using var processorStop = new CancellationTokenSource();

                var processor = CdpProcessorAsync(
                    messages.Reader, defaultContext, shutdown, processorStop.Token);
                try
                {
                    await HandleConnectionWsAsync(stream, messages.Writer).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or SocketException or WebSocketExceptionShim)
                {
                    CdpLog.Error($"WebSocket connection error: {e.Message}");
                }
                finally
                {
                    // Connection closed (or shutting down): stop this connection's
                    // processor so the thread can exit.
                    messages.Writer.TryComplete();
                    await processorStop.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        await processor.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            });
        }
        finally
        {
            // Apply only this connection's cookie changes to the persistence
            // template. Unchanged cookies cannot overwrite another connection's
            // updates, while explicit deletes and replacements still persist.
            if (persistence.StorageDir is not null)
            {
                lock (persistenceLock)
                {
                    ServerSupport.MergeCookieDelta(
                        persistence.CookieJar,
                        initialCookies,
                        defaultContext.CookieJar.GetAllCookies());
                    persistence.SaveCookies();
                }
            }
        }
    }

    /// <summary>A WebSocket protocol fault, kept separate from transport faults.</summary>
    internal sealed class WebSocketExceptionShim(string message) : Exception(message);

    internal static string ComputeWebSocketAccept(string key) =>
        Convert.ToBase64String(
            System.Security.Cryptography.SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
}
