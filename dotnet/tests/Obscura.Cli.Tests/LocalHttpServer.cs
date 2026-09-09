using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Api;

namespace Obscura.Cli.Tests;

/// <summary>
/// The minimal loopback HTTP/1.1 server the <c>crates/obscura/tests/*</c>
/// fixtures each spawn by hand.
/// </summary>
/// <remarks>
/// Every one of those tests opens <c>127.0.0.1:0</c>, reads one request, writes
/// one <c>Connection: close</c> response, and shuts the socket down. Rather than
/// repeat that in each ported file, they share this. The handler receives the
/// request target (the second whitespace-delimited token of the request line)
/// and returns the content type and the exact body bytes to send.
/// </remarks>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<string, (string ContentType, byte[] Body)> _handler;
    private readonly bool _cors;

    public LocalHttpServer(Func<string, (string ContentType, byte[] Body)> handler, bool cors = false)
    {
        _handler = handler;
        _cors = cors;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Base = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Serve one fixed HTML body for every request.</summary>
    public static LocalHttpServer Html(string html) =>
        new(_ => ("text/html", Encoding.UTF8.GetBytes(html)));

    /// <summary>The origin, with no trailing slash: <c>http://127.0.0.1:PORT</c>.</summary>
    public string Base { get; }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException
                or ObjectDisposedException or SocketException)
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
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                var request = Encoding.UTF8.GetString(buffer, 0, count);
                var parts = request.Split(
                    [' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                var target = parts.Length > 1 ? parts[1] : "/";
                var (contentType, body) = _handler(target);
                var head = new StringBuilder()
                    .Append("HTTP/1.1 200 OK\r\nContent-Type: ").Append(contentType)
                    .Append("\r\nContent-Length: ").Append(body.Length).Append("\r\n");
                if (_cors)
                {
                    head.Append("Access-Control-Allow-Origin: *\r\n");
                }
                head.Append("Connection: close\r\n\r\n");

                await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString())).ConfigureAwait(false);
                await stream.WriteAsync(body).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                client.Client.Shutdown(SocketShutdown.Both);
            }
            catch (Exception error) when (error is IOException or SocketException
                or ObjectDisposedException or InvalidOperationException)
            {
                // A client that hangs up mid-request is not a test failure.
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Stop();
        _shutdown.Dispose();
    }
}

/// <summary>Shared helpers for the ported <c>crates/obscura/tests</c> files.</summary>
internal static class PageProbe
{
    /// <summary>
    /// Pump the page's event loop in <paramref name="sliceMs"/> slices until
    /// <paramref name="done"/> reports true, at most <paramref name="rounds"/>
    /// times. The Rust fixtures write this loop inline.
    /// </summary>
    public static async Task SettleUntilAsync(
        Page page, Func<bool> done, int rounds, ulong sliceMs)
    {
        for (var i = 0; i < rounds; i++)
        {
            await page.SettleAsync(sliceMs).ConfigureAwait(false);
            if (done())
            {
                return;
            }
        }
    }

    /// <summary>Pump until <c>document.body</c> carries <c>data-done="1"</c>.</summary>
    public static Task SettleUntilDoneAsync(Page page, int rounds = 40, ulong sliceMs = 250) =>
        SettleUntilAsync(page, () => DoneMarker(page) == "1", rounds, sliceMs);

    /// <summary>The <c>data-done</c> attribute the probe scripts set, or null.</summary>
    public static string? DoneMarker(Page page) =>
        Text(page.Evaluate("document.body.getAttribute('data-done')"));

    /// <summary>A string evaluate result, or null when it is not a string.</summary>
    public static string? Text(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    /// <summary>A numeric evaluate result, or null when it is not a number.</summary>
    public static double? Number(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.Number ? node.GetValue<double>() : null;

    /// <summary>Parse the JSON a probe wrote into <c>#probe-results</c>.</summary>
    public static JsonNode? ProbeResults(Page page)
    {
        var raw = Text(page.Evaluate("document.getElementById('probe-results').textContent"));
        if (raw is null)
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
