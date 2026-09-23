using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Obscura.Api;
using Obscura.Cli.CommandLine;
using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>
/// <c>serve --font-dir</c> and its library counterpart, <see cref="BrowserConfig.FontDirectories"/>
/// (upstream 343fdc7). The render-side loading is covered in
/// <c>Obscura.Render.Tests/FontDirectoryTests.cs</c>.
/// </summary>
public sealed class FontDirTests
{
    private static string MissingDirectory() =>
        Path.Combine(Path.GetTempPath(), "obscura-missing-font-dir-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Font_dir_is_a_serve_option_only()
    {
        // Upstream declares it on `Command::Serve` alone.
        Assert.Null(CliArgs.TryParseFrom(["obscura", "fetch", "--font-dir", "/fonts", "about:blank"], out _));
    }

    [Fact]
    public void Serve_rejects_a_missing_font_directory()
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
        string missing = MissingDirectory();
        CliRun run = CliProcess.Run("serve", "--port", "0", "--font-dir", missing);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains($"Error: Font directory does not exist or is not a directory: {missing}", run.StdErr);
    }

    [Fact]
    public void Serve_rejects_a_file_named_as_a_font_directory()
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
        string file = Path.GetTempFileName();
        try
        {
            CliRun run = CliProcess.Run("serve", "--port", "0", "--font-dir", file);
            Assert.Equal(1, run.ExitCode);
            Assert.Contains($"Error: Font directory does not exist or is not a directory: {file}", run.StdErr);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Configure_rejects_a_missing_directory_before_touching_the_renderer()
    {
        string missing = MissingDirectory();
        var error = Assert.Throws<CliException>(() => CliOptions.ConfigureFontDirectories([missing]));
        Assert.Equal($"Font directory does not exist or is not a directory: {missing}", error.Message);
    }

    [Fact]
    public void Configure_with_no_directories_is_a_no_op()
    {
        CliOptions.ConfigureFontDirectories([]);
    }

    [Fact]
    public void Builders_collect_repeated_font_directories()
    {
        BrowserConfig config = BrowserConfig.Builder().FontDirectory("/fonts/cjk").FontDirectory("/fonts/brand").Build();
        Assert.Equal(["/fonts/cjk", "/fonts/brand"], config.FontDirectories);
        Assert.Empty(new BrowserConfig().FontDirectories);
    }

    [Fact]
    public void Library_rejects_a_missing_font_directory()
    {
        string missing = MissingDirectory();
        var error = Assert.Throws<ObscuraException>(
            () => Obscura.Api.Browser.Builder().FontDirectory(missing).Build());
        Assert.Equal($"Font directory does not exist or is not a directory: {missing}", error.Message);
    }

    /// <summary>
    /// End to end: a face from <c>--font-dir</c> lays out text that names its family, and
    /// without the flag the same page lays out as if the family did not exist.
    /// </summary>
    [Fact]
    public async Task Served_page_uses_a_face_from_the_font_directory()
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
        string root = Path.Combine(Path.GetTempPath(), "obscura-font-dir-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            File.Copy(ArabicFixture(), Path.Combine(root, "nested", "NotoSansArabic.TTF"));

            (double Named, double Unknown) loaded = await MeasureAsync(["--font-dir", root]);
            (double Named, double Unknown) absent = await MeasureAsync([]);

            // Without the directory the family is unknown, so both spans fall back alike.
            Assert.Equal(absent.Unknown, absent.Named);
            Assert.Equal(absent.Unknown, loaded.Unknown);
            // With it, the named span lays out in Noto Sans Arabic instead.
            Assert.NotEqual(loaded.Unknown, loaded.Named);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ArabicFixture([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(path)!, "..", "Obscura.Render.Tests", "Fixtures", "fonts", "NotoSansArabic.ttf"));

    private static int PickPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        probe.Listen(1);
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static async Task<(double Named, double Unknown)> MeasureAsync(string[] extra)
    {
        int port = PickPort();
        var psi = new ProcessStartInfo(CliProcess.Binary!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in (string[])["serve", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), .. extra])
        {
            psi.ArgumentList.Add(arg);
        }

        using Process server = Process.Start(psi) ?? throw new InvalidOperationException("serve did not start");
        _ = server.StandardOutput.ReadToEndAsync();
        _ = server.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using ClientWebSocket ws = await ConnectWhenListeningAsync(port, deadline.Token);
            ulong next = 0;
            async Task<JsonNode> CallAsync(string method, JsonObject parameters, string? session = null)
            {
                ulong id = ++next;
                var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters };
                if (session is not null)
                {
                    message["sessionId"] = session;
                }

                await ws.SendAsync(
                    Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, deadline.Token);
                while (true)
                {
                    JsonNode reply = JsonNode.Parse(await ReceiveAsync(ws, deadline.Token))!;
                    if (reply["id"]?.GetValue<ulong>() == id)
                    {
                        Assert.True(reply["error"] is null, reply.ToJsonString());
                        return reply["result"]!;
                    }
                }
            }

            JsonNode target = await CallAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" });
            JsonNode attached = await CallAsync(
                "Target.attachToTarget",
                new JsonObject { ["targetId"] = target["targetId"]!.GetValue<string>(), ["flatten"] = true });
            string session = attached["sessionId"]!.GetValue<string>();
            const string script =
                "document.body.innerHTML = '<span id=a style=\"display: inline-block; font-family: Noto Sans Arabic\">\u0645\u0631\u062d\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645</span><br>"
                + "<span id=b style=\"display: inline-block; font-family: No Such Face\">\u0645\u0631\u062d\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645</span>';"
                + "JSON.stringify([document.getElementById('a').getBoundingClientRect().width,"
                + " document.getElementById('b').getBoundingClientRect().width])";
            JsonNode evaluated = await CallAsync(
                "Runtime.evaluate",
                new JsonObject { ["expression"] = script, ["returnByValue"] = true },
                session);
            JsonArray widths = JsonNode.Parse(evaluated["result"]!["value"]!.GetValue<string>())!.AsArray();
            return (widths[0]!.GetValue<double>(), widths[1]!.GetValue<double>());
        }
        finally
        {
            try
            {
                server.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            await server.WaitForExitAsync();
        }
    }

    private static async Task<ClientWebSocket> ConnectWhenListeningAsync(int port, CancellationToken ct)
    {
        while (true)
        {
            var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/devtools/browser"), ct);
                return ws;
            }
            catch (WebSocketException)
            {
                ws.Dispose();
                await Task.Delay(100, ct);
            }
        }
    }

    private static async Task<string> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new List<byte>();
        while (true)
        {
            ValueWebSocketReceiveResult result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException("server closed the connection");
            }

            message.AddRange(buffer.AsSpan(0, result.Count));
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString([.. message]);
            }
        }
    }
}
