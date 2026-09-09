using System.CommandLine.Parsing;
using System.CommandLine;

namespace Obscura.Cli.CommandLine;

/// <summary>What <c>--dump</c> should emit.</summary>
public enum DumpFormat
{
    Html,
    Text,
    Links,
    Markdown,

    /// <summary>
    /// Stream the raw HTTP response body verbatim (binary-safe), bypassing the
    /// browser and JS layer. For images, JSON, JS, CSS, or any non-HTML resource.
    /// </summary>
    Original,

    /// <summary>
    /// One JSON object per line listing every sub-resource URL the rendered page
    /// references (script src, link href, img src, iframe src, media sources,
    /// embed/object data), so callers can replay the asset graph themselves.
    /// </summary>
    Assets,

    /// <summary>
    /// All cookies in the browser jar as a JSON array, including HttpOnly
    /// cookies that <c>document.cookie</c> cannot reach.
    /// </summary>
    Cookies,
}

/// <summary>
/// The <c>obscura</c> command line, mirroring the Rust CLI exactly.
/// </summary>
/// <remarks>
/// Argument names, defaults, short forms, and which options are global are all
/// part of the user-facing contract and are covered by CLI parity tests, so
/// they must match <c>crates/obscura-cli/src/main.rs</c> rather than being
/// rearranged into something more idiomatic for System.CommandLine.
/// </remarks>
public static class CliDefinition
{
    // Global options: valid before or after the subcommand, and applied to
    // fetch, serve, scrape and mcp alike.
    public static readonly Option<bool> Verbose = new("--verbose", "-v") { Description = "Verbose logging", Recursive = true };
    public static readonly Option<string?> Proxy = new("--proxy") { Description = "HTTP or SOCKS5 proxy URL", Recursive = true };
    public static readonly Option<bool> Stealth = new("--stealth") { Description = "Enable stealth mode (consistent browser fingerprint)", Recursive = true };
    public static readonly Option<bool> ObeyRobots = new("--obey-robots") { Description = "Respect robots.txt before navigating to an HTTP(S) URL", Recursive = true };
    public static readonly Option<bool> AllowPrivateNetwork = new("--allow-private-network")
    {
        Description = "Permit fetches to loopback, RFC1918, and link-local addresses. Default is to block them.",
        Recursive = true,
    };

    // Root-level (not global) options, matching the Rust definition.
    public static readonly Option<int> RootPort = new("--port", "-p")
    {
        Description = "CDP port",
        DefaultValueFactory = _ => 9222,
        CustomParser = ClapNumbers.U16("--port", "PORT"),
    };
    public static readonly Option<string?> RootUserAgent = new("--user-agent") { Description = "Override the user agent" };
    public static readonly Option<FileSystemInfo?> RootStorageDir = new("--storage-dir") { Description = "Directory for persisted cookies and storage" };
    public static readonly Option<string?> V8FlagsOption = new("--v8-flags")
    {
        Description = "Pass raw flags to V8, applied once at startup before any isolate is created",
        HelpName = "FLAGS",
        AllowMultipleArgumentsPerToken = false,
    };

    /// <summary>Builds the full command tree.</summary>
    public static RootCommand Build()
    {
        var root = new RootCommand("Obscura - A lightweight headless browser for web scraping and automation")
        {
            Verbose, Proxy, Stealth, ObeyRobots, AllowPrivateNetwork,
            RootPort, RootUserAgent, RootStorageDir, V8FlagsOption,
        };
        root.Add(BuildServe());
        root.Add(BuildFetch());
        root.Add(BuildScrape());
        root.Add(BuildMcp());
        // clap's `command: Option<Command>` makes the subcommand optional, and a
        // bare `obscura` prints the banner and serves. System.CommandLine instead
        // reports "Required command was not provided." for a command that has
        // subcommands and no action of its own, so the root needs one. It is a
        // marker rather than the real work: Program.cs owns dispatch and must be
        // able to tell it apart from --help / --version.
        root.Action = NoSubcommandAction.Instance;
        return root;
    }

    /// <summary>
    /// Marks a parse that named no subcommand, so the bare-server path runs
    /// instead of System.CommandLine rejecting the command line.
    /// </summary>
    public sealed class NoSubcommandAction : System.CommandLine.Invocation.SynchronousCommandLineAction
    {
        /// <summary>The single instance <see cref="Build"/> installs.</summary>
        public static NoSubcommandAction Instance { get; } = new();

        private NoSubcommandAction()
        {
        }

        /// <inheritdoc />
        public override int Invoke(ParseResult parseResult) => 0;
    }

    public static class Serve
    {
        public static readonly Option<int> Port = new("--port", "-p")
        {
            DefaultValueFactory = _ => 9222,
            CustomParser = ClapNumbers.U16("--port", "PORT"),
        };
        // Loopback by default: binding all interfaces exposes the CDP port,
        // which grants full control of the browser to anything that reaches it.
        public static readonly Option<string> Host = new("--host") { DefaultValueFactory = _ => "127.0.0.1" };
        public static readonly Option<string?> Proxy = new("--proxy");
        public static readonly Option<string?> UserAgent = new("--user-agent");
        public static readonly Option<int> Workers = new("--workers")
        {
            DefaultValueFactory = _ => 1,
            CustomParser = ClapNumbers.U16("--workers", "WORKERS"),
        };
        public static readonly Option<int> MaxConnections = new("--max-connections")
        {
            Description = "Maximum live CDP connections. Connections beyond the limit are refused with 503 rather than queued.",
            DefaultValueFactory = _ => DefaultMaxConnections,
            CustomParser = ClapNumbers.USize("--max-connections", "MAX_CONNECTIONS"),
        };
        public static readonly Option<bool> AllowFileAccess = new("--allow-file-access")
        {
            Description = "Allow CDP clients to navigate to file:// URLs. Off by default so a CDP connection cannot read arbitrary local files.",
        };
        public static readonly Option<FileSystemInfo?> StorageDir = new("--storage-dir");
        public static readonly Option<bool> Quiet = new("--quiet");
    }

    /// <summary>
    /// Mirrors <c>obscura_cdp::DEFAULT_MAX_CONNECTIONS</c>. Each connection runs
    /// on its own thread with its own V8 isolates, so this bounds the server's
    /// thread and memory footprint.
    /// </summary>
    public const int DefaultMaxConnections = 128;

    public static class Fetch
    {
        // Optional so a batch run can pass URLs via --file instead.
        public static readonly Argument<string?> Url = new("url") { Arity = ArgumentArity.ZeroOrOne };
        // Kept nullable so an explicit --dump is distinguishable from none: a
        // bare --eval returns its own value, while --eval with --dump runs the
        // eval, lets its async work settle, then reads the page.
        public static readonly Option<DumpFormat?> Dump = new("--dump")
        {
            CustomParser = ParseDumpFormat,
        };
        public static readonly Option<FileSystemInfo?> File = new("--file")
        {
            Description = "Read newline-delimited URLs from a file (`-` for stdin). Enables batch mode.",
        };
        public static readonly Option<int> Concurrency = new("--concurrency")
        {
            DefaultValueFactory = _ => 1,
            CustomParser = ClapNumbers.NonZeroUSize("--concurrency", "CONCURRENCY"),
        };
        public static readonly Option<string?> Selector = new("--selector");
        public static readonly Option<long?> Wait = new("--wait")
        {
            Description = "Maximum adaptive post-load settle in seconds. Explicit values are a fixed delay; the default is a 5-second cap.",
            CustomParser = ClapNumbers.U64("--wait", "WAIT"),
        };
        public static readonly Option<long> Timeout = new("--timeout")
        {
            DefaultValueFactory = _ => 30,
            CustomParser = ClapNumbers.U64Range("--timeout", "TIMEOUT", 1),
        };
        public static readonly Option<string> WaitUntil = new("--wait-until") { DefaultValueFactory = _ => "load" };
        public static readonly Option<string?> UserAgent = new("--user-agent");
        public static readonly Option<string?> Eval = new("--eval", "-e");
        public static readonly Option<FileSystemInfo?> Output = new("--output", "-o");
        public static readonly Option<bool> Quiet = new("--quiet", "-q");
        public static readonly Option<FileSystemInfo?> StorageDir = new("--storage-dir");
        public static readonly Option<FileSystemInfo?> Screenshot = new("--screenshot", "-s") { HelpName = "FILE" };
    }

    public static class Scrape
    {
        public static readonly Argument<string[]> Urls = new("urls") { Arity = ArgumentArity.ZeroOrMore };
        public static readonly Option<string?> Eval = new("--eval", "-e");
        public static readonly Option<int> Concurrency = new("--concurrency")
        {
            DefaultValueFactory = _ => 10,
            CustomParser = ClapNumbers.NonZeroUSize("--concurrency", "CONCURRENCY"),
        };
        public static readonly Option<string> Format = new("--format") { DefaultValueFactory = _ => "json" };
        public static readonly Option<long> Timeout = new("--timeout")
        {
            DefaultValueFactory = _ => 60,
            CustomParser = ClapNumbers.U64Range("--timeout", "TIMEOUT", 1),
        };
        public static readonly Option<bool> Quiet = new("--quiet", "-q");
    }

    public static class Mcp
    {
        public static readonly Option<bool> Http = new("--http");
        public static readonly Option<string> Host = new("--host") { DefaultValueFactory = _ => "127.0.0.1" };
        public static readonly Option<int> Port = new("--port")
        {
            DefaultValueFactory = _ => 3000,
            CustomParser = ClapNumbers.U16("--port", "PORT"),
        };
        public static readonly Option<string?> Proxy = new("--proxy");
        public static readonly Option<string?> UserAgent = new("--user-agent");
    }

    private static Command BuildServe() => new("serve", "Run the Chrome DevTools Protocol server")
    {
        Serve.Port, Serve.Host, Serve.Proxy, Serve.UserAgent, Serve.Workers,
        Serve.MaxConnections, Serve.AllowFileAccess, Serve.StorageDir, Serve.Quiet,
    };

    private static Command BuildFetch()
    {
        var cmd = new Command("fetch", "Fetch and render a page")
        {
            Fetch.Url, Fetch.Dump, Fetch.File, Fetch.Concurrency, Fetch.Selector,
            Fetch.Wait, Fetch.Timeout, Fetch.WaitUntil, Fetch.UserAgent, Fetch.Eval,
            Fetch.Output, Fetch.Quiet, Fetch.StorageDir, Fetch.Screenshot,
        };
        cmd.Validators.Add(result =>
        {
            // One screenshot per URL makes no sense for a batch run, and the
            // Rust CLI declares these mutually exclusive.
            if (result.GetResult(Fetch.Screenshot) is not null && result.GetResult(Fetch.File) is not null)
            {
                result.AddError("--screenshot cannot be used with --file");
            }
            RejectOptionLikePositionals(result, Fetch.Url);
            // clap's `value_parser!(u64).range(1..)` and `NonZeroUsize` validate
            // the token the user typed, never the default, so the range lives in
            // the options' own parsers (ClapNumbers) rather than here: a
            // validator sees an absent option as 0 and would reject every plain
            // `fetch`.
        });
        return cmd;
    }

    private static Command BuildScrape()
    {
        var cmd = new Command("scrape", "Fetch many URLs in parallel")
        {
            Scrape.Urls, Scrape.Eval, Scrape.Concurrency, Scrape.Format, Scrape.Timeout, Scrape.Quiet,
        };
        cmd.Validators.Add(result =>
        {
            RejectOptionLikePositionals(result, Scrape.Urls);
        });
        return cmd;
    }

    private static Command BuildMcp() => new("mcp", "Run the MCP automation server")
    {
        Mcp.Http, Mcp.Host, Mcp.Port, Mcp.Proxy, Mcp.UserAgent,
    };

    /// <summary>
    /// clap's <c>ValueEnum</c> parser for <c>--dump</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// clap accepts only the derived kebab-case names, case sensitively, and on
    /// a bad value prints the value plus an indented possible-values line. The
    /// System.CommandLine default accepted any casing (so <c>--dump HTML</c>
    /// worked where the reference rejects it) and reported a conversion failure
    /// naming the CLR type.
    /// </para>
    /// <para>
    /// Not reproduced: for a value that is a near miss clap adds a third line,
    /// <c>  tip: a similar value exists: 'html'</c>. That needs clap's
    /// <c>did_you_mean</c> scoring (a Jaro-Winkler confidence, a threshold, and
    /// its candidate ordering, which prefers neither the highest nor the lowest
    /// score in the cases probed), so the value, the message and the
    /// possible-values line match and only the tip is missing.
    /// </remarks>
    private static DumpFormat? ParseDumpFormat(ArgumentResult result)
    {
        if (result.Tokens.Count == 0)
        {
            return null;
        }
        var raw = result.Tokens[0].Value;
        switch (raw)
        {
            case "html": return DumpFormat.Html;
            case "text": return DumpFormat.Text;
            case "links": return DumpFormat.Links;
            case "markdown": return DumpFormat.Markdown;
            case "original": return DumpFormat.Original;
            case "assets": return DumpFormat.Assets;
            case "cookies": return DumpFormat.Cookies;
            default:
                result.AddError(
                    $"invalid value '{raw}' for '--dump <DUMP>'\n"
                    + "  [possible values: html, text, links, markdown, original, assets, cookies]");
                return null;
        }
    }

    /// <summary>
    /// Reject a token that looks like an option but was bound to a positional
    /// argument.
    /// </summary>
    /// <remarks>
    /// clap refuses an unknown flag; System.CommandLine happily binds
    /// <c>--nope</c> to the next positional, so <c>obscura fetch --nope</c>
    /// would try to navigate to "--nope" instead of reporting a usage error. No
    /// URL starts with a dash, so any such token is a typo. The message is
    /// clap's, because the exit code and the shape are both user visible.
    /// </remarks>
    private static void RejectOptionLikePositionals(CommandResult result, Argument argument)
    {
        if (result.GetResult(argument) is not ArgumentResult supplied)
        {
            return;
        }
        foreach (var token in supplied.Tokens)
        {
            if (token.Value.Length > 1 && token.Value[0] == '-')
            {
                result.AddError($"unexpected argument '{token.Value}' found");
            }
        }
    }
}
