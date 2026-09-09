using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Obscura.Cdp;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The domain tests share process-wide state (the SSRF opt-in, loopback fixture ports) and every
/// one of them drives a V8 isolate, so they run serially the way the Rust suite's
/// process-per-test does.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CdpDomainCollection
{
    public const string Name = "cdp-domains";
}

internal static class CdpDomainFixtures
{
    /// <summary>
    /// Every fixture server here binds 127.0.0.1, which the SSRF gate blocks by default; the Rust
    /// tests call <c>std::env::set_var("OBSCURA_ALLOW_PRIVATE_NETWORK", "1")</c> for the same
    /// reason.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void AllowLoopbackFixtures() =>
        Environment.SetEnvironmentVariable("OBSCURA_ALLOW_PRIVATE_NETWORK", "1");

    /// <summary>A context with one page and one session attached to it.</summary>
    internal static (CdpContext Ctx, string SessionId) NewSession(string sessionId = "session-1")
    {
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        ctx.Sessions[sessionId] = pageId;
        return (ctx, sessionId);
    }

    internal static JsonNode Unwrap(DomainResult result)
    {
        Assert.True(result.IsOk, result.Error);
        Assert.NotNull(result.Value);
        return result.Value;
    }

    internal static string ErrorOf(DomainResult result)
    {
        Assert.False(result.IsOk, "expected an error");
        Assert.NotNull(result.Error);
        return result.Error;
    }

    /// <summary>Compare a <see cref="JsonNode"/> against a JSON literal, as <c>assert_eq!(.., json!(..))</c> does.</summary>
    internal static void AssertJson(string expected, JsonNode? actual) =>
        Assert.Equal(
            JsonNode.Parse(expected)?.ToJsonString() ?? "null",
            actual?.ToJsonString() ?? "null");

    /// <summary>Parse a JSON literal the way the Rust tests write <c>json!(...)</c>.</summary>
    internal static JsonNode Json(string text) =>
        JsonNode.Parse(text) ?? throw new InvalidOperationException("invalid JSON literal");
}

/// <summary>
/// The one-shot loopback HTTP fixture the Rust integration tests spawn with
/// <c>TcpListener::bind("127.0.0.1:0")</c>.
/// </summary>
internal sealed class CdpTestServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly byte[] _body;
    private readonly string _contentType;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentQueue<string> _paths = new();
    private bool _disposed;

    private CdpTestServer(string body, string contentType)
    {
        _body = Encoding.UTF8.GetBytes(body);
        _contentType = contentType;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = "http://127.0.0.1:"
            + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)
            + "/";
        _ = Task.Run(AcceptLoopAsync);
    }

    internal string Url { get; }

    internal IReadOnlyCollection<string> Paths => [.. _paths];

    internal static CdpTestServer ServeHtml(string body) => new(body, "text/html");

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
                byte[] chunk = new byte[4096];
                int read = await stream.ReadAsync(chunk, _stopping.Token).ConfigureAwait(false);
                string headerText = Encoding.UTF8.GetString(chunk.AsSpan(0, Math.Max(read, 0)));
                string firstLine = headerText.Split("\r\n")[0];
                string[] parts = firstLine.Split(' ');
                _paths.Enqueue(parts.Length > 1 ? parts[1] : "/");

                var head = new StringBuilder();
                head.Append("HTTP/1.1 200 OK\r\n");
                head.Append(CultureInfo.InvariantCulture, $"Content-Type: {_contentType}\r\n");
                head.Append(
                    CultureInfo.InvariantCulture,
                    $"Content-Length: {_body.Length.ToString(CultureInfo.InvariantCulture)}\r\n");
                head.Append("Connection: close\r\n\r\n");
                await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), _stopping.Token)
                    .ConfigureAwait(false);
                await stream.WriteAsync(_body, _stopping.Token).ConfigureAwait(false);
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
