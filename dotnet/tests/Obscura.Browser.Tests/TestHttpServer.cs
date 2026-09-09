using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Obscura.Browser.Tests;

/// <summary>One request the fixture server answered.</summary>
internal sealed record TestRequest(string Method, string Path, string Headers, byte[] Body);

/// <summary>What the fixture server writes back.</summary>
internal sealed record TestResponse(
    string ContentType,
    byte[] Body,
    string Status = "200 OK",
    IReadOnlyList<(string Name, string Value)>? ExtraHeaders = null,
    int DelayMs = 0)
{
    internal static TestResponse Html(string body) =>
        new("text/html", Encoding.UTF8.GetBytes(body));

    internal static TestResponse Css(string body) =>
        new("text/css", Encoding.UTF8.GetBytes(body));

    internal static TestResponse JavaScript(string body) =>
        new("application/javascript", Encoding.UTF8.GetBytes(body));

    internal static TestResponse Text(string body) =>
        new("text/plain", Encoding.UTF8.GetBytes(body));

    internal static TestResponse Svg(string body) =>
        new("image/svg+xml", Encoding.UTF8.GetBytes(body));
}

/// <summary>
/// The raw <c>TcpListener</c> fixture the Rust tests spawn per case.
/// </summary>
/// <remarks>
/// Deliberately not Kestrel: the tests assert on connection-level behaviour
/// (<c>Connection: close</c>, per-request delays, exact request paths) and a raw
/// listener is the same shape as the reference.
/// </remarks>
internal sealed class TestHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<TestRequest, TestResponse> _handler;
    private readonly CancellationTokenSource _stopping = new();
    private readonly BlockingCollection<string> _paths = [];
    private readonly ConcurrentQueue<TestRequest> _requests = new();
    private bool _disposed;

    private TestHttpServer(Func<TestRequest, TestResponse> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Origin = "http://127.0.0.1:"
            + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        _ = Task.Run(AcceptLoopAsync);
    }

    internal string Origin { get; }

    internal IReadOnlyCollection<TestRequest> Requests => [.. _requests];

    internal static TestHttpServer Start(Func<TestRequest, TestResponse> handler) => new(handler);

    /// <summary>Rust's <c>request_rx.recv_timeout(...)</c>.</summary>
    internal string NextPath(TimeSpan timeout) =>
        _paths.TryTake(out string? path, timeout) ? path : throw new TimeoutException("no request arrived");

    internal string NextPath() => NextPath(TimeSpan.FromSeconds(2));

    internal bool TryNextPath(TimeSpan timeout, out string path)
    {
        bool taken = _paths.TryTake(out string? value, timeout);
        path = value ?? string.Empty;
        return taken;
    }

    internal List<string> SortedPaths(int count, TimeSpan timeout)
    {
        List<string> paths = [];
        for (int i = 0; i < count; i++)
        {
            paths.Add(NextPath(timeout));
        }
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception)
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
                using NetworkStream stream = client.GetStream();
                List<byte> buffer = [];
                byte[] chunk = new byte[4096];
                int headerEnd = -1;
                while (headerEnd < 0)
                {
                    int read = await stream.ReadAsync(chunk, _stopping.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }
                    buffer.AddRange(chunk.AsSpan(0, read).ToArray());
                    headerEnd = IndexOfHeaderEnd(buffer);
                }

                string headerText = Encoding.UTF8.GetString([.. buffer.Take(headerEnd)]);
                string firstLine = headerText.Split("\r\n")[0];
                string[] parts = firstLine.Split(' ');
                string method = parts.Length > 0 ? parts[0] : "GET";
                string path = parts.Length > 1 ? parts[1] : "/";
                int contentLength = 0;
                foreach (string line in headerText.Split("\r\n"))
                {
                    int colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon > 0
                        && string.Equals(line[..colon], "content-length", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = int.TryParse(
                            line[(colon + 1)..].Trim(),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out contentLength);
                    }
                }
                while (buffer.Count < headerEnd + contentLength)
                {
                    int read = await stream.ReadAsync(chunk, _stopping.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    buffer.AddRange(chunk.AsSpan(0, read).ToArray());
                }
                byte[] body = [.. buffer.Skip(headerEnd).Take(contentLength)];

                var request = new TestRequest(method, path, headerText, body);
                _requests.Enqueue(request);
                _paths.Add(path);

                TestResponse response = _handler(request);
                if (response.DelayMs > 0)
                {
                    await Task.Delay(response.DelayMs, _stopping.Token).ConfigureAwait(false);
                }

                var head = new StringBuilder();
                head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.Status}\r\n");
                head.Append(CultureInfo.InvariantCulture, $"Content-Type: {response.ContentType}\r\n");
                head.Append(
                    CultureInfo.InvariantCulture,
                    $"Content-Length: {response.Body.Length.ToString(CultureInfo.InvariantCulture)}\r\n");
                head.Append("Connection: close\r\n");
                foreach ((string name, string value) in response.ExtraHeaders ?? [])
                {
                    head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
                }
                head.Append("\r\n");
                await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), _stopping.Token)
                    .ConfigureAwait(false);
                await stream.WriteAsync(response.Body, _stopping.Token).ConfigureAwait(false);
                await stream.FlushAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A test that stops mid-request is normal; the fixture must not throw
                // out of its accept loop.
            }
        }
    }

    private static int IndexOfHeaderEnd(List<byte> buffer)
    {
        for (int i = 0; i + 3 < buffer.Count; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i + 4;
            }
        }
        return -1;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stopping.Cancel();
        _listener.Stop();
        _stopping.Dispose();
        _paths.Dispose();
    }
}
