using System.CommandLine;
using Obscura.Cli;
using Obscura.Cli.CommandLine;
using Obscura.Cli.Commands;
using Obscura.Js.Runtime;

// The obscura-worker launcher runs the same assembly in worker mode, so the
// scrape fan-out finds its sibling binary exactly as the Rust build lays it out.
if (WorkerHost.IsWorkerProcess())
{
    Log.SetFilter("warn");
    return await WorkerHost.RunAsync().ConfigureAwait(false);
}

// Pin the process timezone before V8/ICU reads it. V8 sources the zone for both
// Date (getTimezoneOffset, toString) and Intl.DateTimeFormat from TZ; left unset
// it defaults to UTC for Date while the page layer advertises a different zone,
// a cross-surface mismatch fingerprinting scripts flag. Default to Europe/Berlin;
// set OBSCURA_TIMEZONE to match the exit IP's region. An existing TZ from the
// host is respected. This runs before any isolate or worker thread starts, so
// the environment is effectively single threaded here.
var configuredTimezone = Environment.GetEnvironmentVariable("OBSCURA_TIMEZONE");
if (!string.IsNullOrWhiteSpace(configuredTimezone))
{
    ProcessEnvironment.Set("TZ", configuredTimezone);
}
else if (Environment.GetEnvironmentVariable("TZ") is null)
{
    ProcessEnvironment.Set("TZ", "Europe/Berlin");
}

var root = CliDefinition.Build();
InstallVersionAction(root);

var parse = root.Parse(args);
if (parse.Errors.Count > 0)
{
    foreach (var error in parse.Errors)
    {
        Console.Error.WriteLine($"error: {error.Message}");
    }
    Console.Error.WriteLine();
    Console.Error.WriteLine($"For more information, try '--help'.");
    // clap exits 2 on a usage error.
    return 2;
}

// --help / --version are actions on the parse result; when one is present it
// owns the invocation and there is no subcommand to run.
if (parse.Action is not null)
{
    return parse.Invoke();
}

var cli = CliArgs.From(parse);

var quiet = CliOptions.IsQuietCommand(cli.Command);
Log.SetFilter(Environment.GetEnvironmentVariable("RUST_LOG") is { Length: > 0 } configured
    ? configured
    : CliOptions.SelectLogFilter(cli.Verbose, quiet));

var v8Flags = CliOptions.EffectiveV8Flags(cli.V8Flags);
Log.Debug($"V8 flags: {v8Flags}");
// The default flag set is the CLI's own and contains flags ClearScript cannot
// express, so reporting those at WARN would put a line on stderr for every
// single run where Rust prints nothing. Only a flag the user chose is loud.
if (CliOptions.NormalizeV8Flags(cli.V8Flags) is not null)
{
    V8Flags.Warned += Log.Warn;
}
else
{
    V8Flags.Warned += Log.Debug;
}
V8Flags.Set(v8Flags);

// Stealth's JS, header and identity surfaces port; the TLS ClientHello
// impersonation does not, because it needs a native TLS stack. Say so once
// rather than letting a caller believe the fingerprint is complete.
if (cli.Stealth)
{
    Log.Info("Stealth mode enabled (tracker blocking; TLS fingerprint impersonation is inactive)");
}

// The js-side fetch path (op_fetch_url) reads OBSCURA_ALLOW_PRIVATE_NETWORK
// directly for its SSRF gate. Mirror the CLI flag into the env var so iframe
// loads and JS fetch() see the same policy the http client layer already uses.
if (cli.AllowPrivateNetwork)
{
    ProcessEnvironment.Set("OBSCURA_ALLOW_PRIVATE_NETWORK", "1");
}

try
{
    switch (cli.Command)
    {
        case CliCommand.Serve serve:
            await ServeCommand.RunAsync(cli, serve).ConfigureAwait(false);
            break;
        case CliCommand.Fetch fetch:
            await FetchCommand.RunAsync(cli, fetch).ConfigureAwait(false);
            break;
        case CliCommand.Scrape scrape:
            await ScrapeCommand.RunAsync(cli, scrape).ConfigureAwait(false);
            break;
        case CliCommand.Mcp mcp:
            await McpCommand.RunAsync(cli, mcp).ConfigureAwait(false);
            break;
        case null:
            await ServeCommand.RunDefaultAsync(cli).ConfigureAwait(false);
            break;
    }
}
catch (NotPortedException error)
{
    // Loud on purpose: exiting 0 here would let a CLI parity test pass against
    // an engine that never ran.
    Console.Error.WriteLine($"obscura: {error.Message}");
    return 70; // EX_SOFTWARE
}
catch (CliException error)
{
    // What the Rust runtime prints for an anyhow error returned from main.
    Console.Error.WriteLine($"Error: {error.Message}");
    return 1;
}

Console.Out.Flush();
return 0;

static void InstallVersionAction(RootCommand root)
{
    // System.CommandLine prints the assembly version; clap prints
    // "obscura <version>" from the build script's OBSCURA_BUILD_VERSION.
    foreach (var option in root.Options)
    {
        if (option is VersionOption)
        {
            option.Action = new PrintVersionAction();
        }
    }
}

internal sealed class PrintVersionAction : System.CommandLine.Invocation.SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        Console.Out.WriteLine($"obscura {BuildVersion.Value}");
        return 0;
    }
}
