using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Mcp.Tests;

/// <summary>
/// L9: <c>browser_storage_state</c> labels storage with the document origin the host
/// committed, and <c>browser_set_storage_state</c> writes an origin's storage only into a
/// page of that origin.
/// </summary>
/// <remarks>
/// DEVIATION from crates/obscura-mcp's tool_storage_state / tool_set_storage_state, which
/// label with the page's own <c>location.origin</c> and write every origin's entries into
/// whatever page is loaded, so one site's tokens landed in another site's storage.
/// </remarks>
public sealed class StorageStateOrigins
{
    private const string Html =
        "<html><body><script>"
        + "localStorage.setItem('mine', 'kept');"
        + "Object.defineProperty(location, 'origin', { get: () => 'https://bank.example' });"
        + "</script></body></html>";

    /// <summary>Serves <see cref="Html"/> on loopback until disposed.</summary>
    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public Server()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[8192];
                        var request = new StringBuilder();
                        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            int read = await stream.ReadAsync(buffer);
                            if (read == 0)
                            {
                                return;
                            }

                            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        }

                        byte[] body = Encoding.UTF8.GetBytes(Html);
                        byte[] head = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nConnection: close\r\nContent-Length: "
                            + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n");
                        await stream.WriteAsync(head);
                        await stream.WriteAsync(body);
                    }
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    private static async Task<BrowserState> OpenAsync(string url)
    {
        var state = new BrowserState(null, null, false);
        await state.PageMut().NavigateAsync(url);
        return state;
    }

    [Fact]
    public async Task ExportLabelsStorageWithTheCommittedOrigin()
    {
        using var server = new Server();
        string origin = $"http://127.0.0.1:{server.Port}";
        using var state = await OpenAsync(origin + "/");

        var exported = JsonNode.Parse(Tools.StorageState(state))!;
        var entry = exported["origins"]![0]!;
        Assert.Equal(origin, entry["origin"]!.GetValue<string>());
        Assert.Equal("""[["mine","kept"]]""", entry["localStorage"]!.ToJsonString());
    }

    [Fact]
    public async Task ImportAppliesOnlyTheCurrentOriginsStorage()
    {
        using var server = new Server();
        string origin = $"http://127.0.0.1:{server.Port}";
        string other = $"http://localhost:{server.Port}";
        using var state = await OpenAsync(origin + "/");

        var args = new JsonObject
        {
            ["state"] = new JsonObject
            {
                ["cookies"] = new JsonArray(),
                ["origins"] = new JsonArray(
                    new JsonObject
                    {
                        ["origin"] = other,
                        ["localStorage"] = new JsonArray(new JsonArray("token", "secret")),
                        ["sessionStorage"] = new JsonArray(),
                    },
                    new JsonObject
                    {
                        ["origin"] = origin,
                        ["localStorage"] = new JsonArray(new JsonArray("restored", "yes")),
                        ["sessionStorage"] = new JsonArray(),
                    }),
            },
        };

        string result = Tools.SetStorageState(args, state);
        Assert.Equal(
            $"Restored 1 state entries. Skipped storage for {other}: it does not match the current page's origin ({origin}). Navigate to that origin and restore again.",
            result);
        Assert.Equal("yes", state.PageMut().Evaluate("localStorage.getItem('restored')")?.GetValue<string>());
        Assert.Null(state.PageMut().Evaluate("localStorage.getItem('token')"));
    }

    [Fact]
    public async Task ImportOfTheCurrentOriginKeepsTheUpstreamResult()
    {
        using var server = new Server();
        string origin = $"http://127.0.0.1:{server.Port}";
        using var state = await OpenAsync(origin + "/");

        var args = JsonNode.Parse(
            "{\"state\":{\"origins\":[{\"origin\":\"" + origin
            + "\",\"localStorage\":[[\"a\",\"1\"]],\"sessionStorage\":[[\"b\",\"2\"]]}]}}");
        Assert.Equal("Restored 2 state entries.", Tools.SetStorageState(args, state));
        Assert.Equal("2", state.PageMut().Evaluate("sessionStorage.getItem('b')")?.GetValue<string>());
    }
}
