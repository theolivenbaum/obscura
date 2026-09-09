using System.CommandLine;
using System.CommandLine.Parsing;

namespace Obscura.Cli.CommandLine;

/// <summary>One parsed subcommand, the port of the Rust <c>Command</c> enum.</summary>
public abstract record CliCommand
{
    private CliCommand()
    {
    }

    /// <summary><c>Command::Serve</c>.</summary>
    public sealed record Serve : CliCommand
    {
        /// <summary><c>--port</c>.</summary>
        public required int Port { get; init; }

        /// <summary><c>--host</c>.</summary>
        public required string Host { get; init; }

        /// <summary><c>--proxy</c>.</summary>
        public string? Proxy { get; init; }

        /// <summary><c>--user-agent</c>.</summary>
        public string? UserAgent { get; init; }

        /// <summary><c>--workers</c>.</summary>
        public required int Workers { get; init; }

        /// <summary><c>--max-connections</c>.</summary>
        public required int MaxConnections { get; init; }

        /// <summary><c>--allow-file-access</c>.</summary>
        public bool AllowFileAccess { get; init; }

        /// <summary><c>--storage-dir</c>.</summary>
        public string? StorageDir { get; init; }

        /// <summary><c>--quiet</c>.</summary>
        public bool Quiet { get; init; }
    }

    /// <summary><c>Command::Fetch</c>.</summary>
    public sealed record Fetch : CliCommand
    {
        /// <summary>The positional URL, absent in batch mode.</summary>
        public string? Url { get; init; }

        /// <summary><c>--dump</c>; null when the flag was not passed at all.</summary>
        public DumpFormat? Dump { get; init; }

        /// <summary><c>--file</c>, the newline-delimited URL list (<c>-</c> is stdin).</summary>
        public string? File { get; init; }

        /// <summary><c>--concurrency</c>.</summary>
        public required int Concurrency { get; init; }

        /// <summary><c>--selector</c>.</summary>
        public string? Selector { get; init; }

        /// <summary><c>--wait</c>; null means the adaptive default rather than a fixed delay.</summary>
        public long? Wait { get; init; }

        /// <summary><c>--timeout</c>, in seconds.</summary>
        public required long Timeout { get; init; }

        /// <summary><c>--wait-until</c>.</summary>
        public required string WaitUntil { get; init; }

        /// <summary><c>--user-agent</c>.</summary>
        public string? UserAgent { get; init; }

        /// <summary><c>--eval</c>.</summary>
        public string? Eval { get; init; }

        /// <summary><c>--output</c>.</summary>
        public string? Output { get; init; }

        /// <summary><c>--quiet</c>.</summary>
        public bool Quiet { get; init; }

        /// <summary><c>--storage-dir</c>.</summary>
        public string? StorageDir { get; init; }

        /// <summary><c>--screenshot</c>.</summary>
        public string? Screenshot { get; init; }
    }

    /// <summary><c>Command::Scrape</c>.</summary>
    public sealed record Scrape : CliCommand
    {
        /// <summary>The positional URL list.</summary>
        public required IReadOnlyList<string> Urls { get; init; }

        /// <summary><c>--eval</c>.</summary>
        public string? Eval { get; init; }

        /// <summary><c>--concurrency</c>.</summary>
        public required int Concurrency { get; init; }

        /// <summary><c>--format</c>.</summary>
        public required string Format { get; init; }

        /// <summary><c>--timeout</c>, in seconds.</summary>
        public required long Timeout { get; init; }

        /// <summary><c>--quiet</c>.</summary>
        public bool Quiet { get; init; }
    }

    /// <summary><c>Command::Mcp</c>.</summary>
    public sealed record Mcp : CliCommand
    {
        /// <summary><c>--http</c>.</summary>
        public bool Http { get; init; }

        /// <summary><c>--host</c>.</summary>
        public required string Host { get; init; }

        /// <summary><c>--port</c>.</summary>
        public required int Port { get; init; }

        /// <summary><c>--proxy</c>.</summary>
        public string? Proxy { get; init; }

        /// <summary><c>--user-agent</c>.</summary>
        public string? UserAgent { get; init; }
    }
}

/// <summary>The whole parsed command line, the port of the Rust <c>Args</c> struct.</summary>
public sealed record CliArgs
{
    /// <summary><c>--verbose</c>.</summary>
    public bool Verbose { get; init; }

    /// <summary>The subcommand, or null when none was given.</summary>
    public CliCommand? Command { get; init; }

    /// <summary>Root <c>--port</c>, used only by the bare-server path.</summary>
    public int Port { get; init; } = 9222;

    /// <summary>Global <c>--proxy</c>.</summary>
    public string? Proxy { get; init; }

    /// <summary>Global <c>--stealth</c>.</summary>
    public bool Stealth { get; init; }

    /// <summary>Global <c>--obey-robots</c>.</summary>
    public bool ObeyRobots { get; init; }

    /// <summary>Root <c>--user-agent</c>.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Root <c>--storage-dir</c>.</summary>
    public string? StorageDir { get; init; }

    /// <summary>Global <c>--allow-private-network</c>.</summary>
    public bool AllowPrivateNetwork { get; init; }

    /// <summary>Raw <c>--v8-flags</c>, exactly as passed.</summary>
    public string? V8Flags { get; init; }

    /// <summary>
    /// Parse an argv including the program name, the way
    /// <c>Args::try_parse_from</c> does. Returns null when the command line is
    /// invalid; <paramref name="errors"/> then holds clap's equivalent messages.
    /// </summary>
    public static CliArgs? TryParseFrom(IEnumerable<string> argv, out IReadOnlyList<string> errors)
    {
        var args = argv.Skip(1).ToArray();
        var result = CliDefinition.Build().Parse(args);
        if (result.Errors.Count > 0)
        {
            errors = [.. result.Errors.Select(e => e.Message)];
            return null;
        }
        errors = [];
        return From(result);
    }

    /// <summary>Project a successful parse onto the Rust argument shape.</summary>
    public static CliArgs From(ParseResult result)
    {
        var command = result.CommandResult.Command;
        return new CliArgs
        {
            Verbose = result.GetValue(CliDefinition.Verbose),
            Port = result.GetValue(CliDefinition.RootPort),
            Proxy = result.GetValue(CliDefinition.Proxy),
            Stealth = result.GetValue(CliDefinition.Stealth),
            ObeyRobots = result.GetValue(CliDefinition.ObeyRobots),
            UserAgent = result.GetValue(CliDefinition.RootUserAgent),
            StorageDir = Path(result.GetValue(CliDefinition.RootStorageDir)),
            AllowPrivateNetwork = result.GetValue(CliDefinition.AllowPrivateNetwork),
            V8Flags = result.GetValue(CliDefinition.V8FlagsOption),
            Command = command.Name switch
            {
                "serve" => new CliCommand.Serve
                {
                    Port = result.GetValue(CliDefinition.Serve.Port),
                    Host = result.GetValue(CliDefinition.Serve.Host) ?? "127.0.0.1",
                    Proxy = result.GetValue(CliDefinition.Serve.Proxy),
                    UserAgent = result.GetValue(CliDefinition.Serve.UserAgent),
                    Workers = result.GetValue(CliDefinition.Serve.Workers),
                    MaxConnections = result.GetValue(CliDefinition.Serve.MaxConnections),
                    AllowFileAccess = result.GetValue(CliDefinition.Serve.AllowFileAccess),
                    StorageDir = Path(result.GetValue(CliDefinition.Serve.StorageDir)),
                    Quiet = result.GetValue(CliDefinition.Serve.Quiet),
                },
                "fetch" => new CliCommand.Fetch
                {
                    Url = result.GetValue(CliDefinition.Fetch.Url),
                    Dump = result.GetValue(CliDefinition.Fetch.Dump),
                    File = Path(result.GetValue(CliDefinition.Fetch.File)),
                    Concurrency = result.GetValue(CliDefinition.Fetch.Concurrency),
                    Selector = result.GetValue(CliDefinition.Fetch.Selector),
                    Wait = result.GetValue(CliDefinition.Fetch.Wait),
                    Timeout = result.GetValue(CliDefinition.Fetch.Timeout),
                    WaitUntil = result.GetValue(CliDefinition.Fetch.WaitUntil) ?? "load",
                    UserAgent = result.GetValue(CliDefinition.Fetch.UserAgent),
                    Eval = result.GetValue(CliDefinition.Fetch.Eval),
                    Output = Path(result.GetValue(CliDefinition.Fetch.Output)),
                    Quiet = result.GetValue(CliDefinition.Fetch.Quiet),
                    StorageDir = Path(result.GetValue(CliDefinition.Fetch.StorageDir)),
                    Screenshot = Path(result.GetValue(CliDefinition.Fetch.Screenshot)),
                },
                "scrape" => new CliCommand.Scrape
                {
                    Urls = result.GetValue(CliDefinition.Scrape.Urls) ?? [],
                    Eval = result.GetValue(CliDefinition.Scrape.Eval),
                    Concurrency = result.GetValue(CliDefinition.Scrape.Concurrency),
                    Format = result.GetValue(CliDefinition.Scrape.Format) ?? "json",
                    Timeout = result.GetValue(CliDefinition.Scrape.Timeout),
                    Quiet = result.GetValue(CliDefinition.Scrape.Quiet),
                },
                "mcp" => new CliCommand.Mcp
                {
                    Http = result.GetValue(CliDefinition.Mcp.Http),
                    Host = result.GetValue(CliDefinition.Mcp.Host) ?? "127.0.0.1",
                    Port = result.GetValue(CliDefinition.Mcp.Port),
                    Proxy = result.GetValue(CliDefinition.Mcp.Proxy),
                    UserAgent = result.GetValue(CliDefinition.Mcp.UserAgent),
                },
                _ => null,
            },
        };
    }

    /// <summary>
    /// The path exactly as the user typed it. <see cref="FileSystemInfo.FullName"/>
    /// would resolve it against the working directory, which loses <c>-</c> (the
    /// stdin sentinel for <c>--file</c>) and changes what error messages print.
    /// </summary>
    private static string? Path(FileSystemInfo? info) => info?.ToString();
}
