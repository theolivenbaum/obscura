using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The helpers the ported <c>crates/obscura-cdp/tests/*.rs</c> integration tests share.
/// </summary>
/// <remarks>
/// Every one of those files opens with the same three shapes: a one-shot loopback HTTP
/// server, a <c>cdp()</c> wrapper that dispatches and asserts the response carries no
/// error, and an <c>eval()</c> wrapper over <c>Runtime.evaluate</c> with
/// <c>returnByValue</c>. They live here once rather than in each ported file.
/// </remarks>
internal static class CoreCdp
{
    /// <summary>
    /// The fixture servers bind 127.0.0.1, which the SSRF gate blocks by default; the Rust
    /// tests set the same variable for the same reason.
    /// </summary>
    internal static void AllowLoopback() =>
        Environment.SetEnvironmentVariable("OBSCURA_ALLOW_PRIVATE_NETWORK", "1");

    /// <summary>
    /// Tear a context's pages down at the end of a test.
    /// </summary>
    /// <remarks>
    /// Each Rust integration test is its own process, and dropping the <c>CdpContext</c>
    /// there drops every page and with it its V8 isolate. In this host all the ported tests
    /// share one process, and an abandoned <c>CdpContext</c> keeps its pages (and their
    /// entered isolates) alive until a GC that may never come during the run. That is not
    /// theoretical: two ported tests that each pass alone both fail when they run in the
    /// same process, because the later context's nested iframe realms never finish
    /// building. <c>CdpContext.RemovePage</c> disposes the page, so returning the pages is
    /// enough.
    /// </remarks>
    internal static IDisposable Owned(CdpContext ctx) => new ContextScope(ctx);

    private sealed class ContextScope(CdpContext ctx) : IDisposable
    {
        public void Dispose()
        {
            foreach (string id in ctx.Pages.Select(page => page.Id).ToList())
            {
                ctx.RemovePage(id);
            }
        }
    }

    internal static async Task<CdpResponse> DispatchAsync(
        CdpContext ctx,
        ulong id,
        string method,
        JsonNode? parameters,
        string? sessionId) =>
        await Dispatcher.DispatchAsync(
            new CdpRequest
            {
                Id = id,
                Method = method,
                Params = parameters,
                SessionId = sessionId,
            },
            ctx);

    /// <summary>Dispatch and assert success, answering the result (<c>{}</c> when absent).</summary>
    internal static async Task<JsonNode> CdpAsync(
        CdpContext ctx,
        ulong id,
        string method,
        JsonNode? parameters,
        string? sessionId)
    {
        CdpResponse response = await DispatchAsync(ctx, id, method, parameters, sessionId);
        Assert.True(response.Error is null, $"CDP {method} failed: {response.Error?.Message}");
        return response.Result ?? new JsonObject();
    }

    internal static Task<JsonNode> EvalAsync(
        CdpContext ctx,
        ulong id,
        string expression,
        string sessionId,
        bool awaitPromise = false) =>
        CdpAsync(
            ctx,
            id,
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                ["awaitPromise"] = awaitPromise,
            },
            sessionId);

    /// <summary>The JSON a probe returned as a <c>JSON.stringify</c> string.</summary>
    internal static JsonNode ParseStringified(JsonNode evaluated)
    {
        string text = evaluated["result"]!["value"].AsString()
            ?? throw new InvalidOperationException(
                $"expected a stringified probe result, got {CdpJson.Serialize(evaluated)}");
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("invalid probe JSON");
    }

    /// <summary>A context with one page and one attached session, navigated to <paramref name="url"/>.</summary>
    internal static async Task<(CdpContext Ctx, string Session)> NavigateAsync(
        string url,
        string sessionId = "session-1",
        string waitUntil = "load")
    {
        AllowLoopback();
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        ctx.Sessions[sessionId] = pageId;
        await CdpAsync(
            ctx,
            1,
            "Page.navigate",
            new JsonObject { ["url"] = url, ["waitUntil"] = waitUntil },
            sessionId);
        return (ctx, sessionId);
    }
}

/// <summary>
/// A loopback HTTP fixture that answers each path from a routing table, standing in for the
/// hand-rolled <c>TcpListener</c> each Rust integration test spawns.
/// </summary>
internal sealed class CoreCdpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, (string Body, string ContentType, int Status)> _route;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentQueue<string> _requests = new();
    private bool _disposed;

    private CoreCdpServer(Func<string, (string Body, string ContentType, int Status)> route)
    {
        _route = route;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = "http://127.0.0.1:"
            + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)
            + "/";
        _ = Task.Run(AcceptLoopAsync);
    }

    internal string Url { get; }

    /// <summary>Every request line path this fixture served, in arrival order.</summary>
    internal IReadOnlyList<string> Requests => [.. _requests];

    internal static CoreCdpServer Html(string body) =>
        new(_ => (body, "text/html", 200));

    internal static CoreCdpServer Routed(
        Func<string, (string Body, string ContentType, int Status)> route) => new(route);

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
                byte[] chunk = new byte[8192];
                int read = await stream.ReadAsync(chunk, _stopping.Token).ConfigureAwait(false);
                string headerText = Encoding.UTF8.GetString(chunk.AsSpan(0, Math.Max(read, 0)));
                string firstLine = headerText.Split("\r\n")[0];
                string[] parts = firstLine.Split(' ');
                string path = parts.Length > 1 ? parts[1] : "/";
                _requests.Enqueue(firstLine);

                (string body, string contentType, int status) = _route(path);
                byte[] payload = Encoding.UTF8.GetBytes(body);
                var head = new StringBuilder();
                head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} OK\r\n");
                head.Append(CultureInfo.InvariantCulture, $"Content-Type: {contentType}\r\n");
                head.Append(
                    CultureInfo.InvariantCulture,
                    $"Content-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n");
                head.Append("Connection: close\r\n\r\n");
                await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), _stopping.Token)
                    .ConfigureAwait(false);
                await stream.WriteAsync(payload, _stopping.Token).ConfigureAwait(false);
                await stream.FlushAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A test that stops mid-request is normal; the fixture must not throw.
            }
        }
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
    }
}
