using System.Globalization;
using PocketCalculator.Cli.CommandLine;

namespace PocketCalculator.Cli;

/// <summary>The free functions <c>main.rs</c> keeps beside its command dispatch.</summary>
public static class CliOptions
{
    /// <summary>
    /// Default V8 flags applied at startup unless the user overrides them with
    /// <c>--v8-flags</c>.
    /// </summary>
    /// <remarks>
    /// The default heap matches headless Chrome (~4 GB) so pages that ship heavy
    /// fingerprinting or analytics bundles do not run out of memory out of the
    /// box. <c>--max-semi-space-size=4</c> caps V8's young generation so a
    /// parse/JS allocation burst does not inflate RSS, and
    /// <c>--optimize-for-size</c> trades memory-heavy codegen for a smaller
    /// footprint. V8 parses flags left to right and later wins, so anything the
    /// user passes overrides these.
    /// </remarks>
    public static string DefaultV8Flags { get; } = IntPtr.Size == 8
        ? "--max-old-space-size=4096 --max-semi-space-size=4 --optimize-for-size"
        : "--max-old-space-size=1024 --max-semi-space-size=4 --optimize-for-size";

    /// <summary>The tracing filter for a run.</summary>
    public static string SelectLogFilter(bool verbose, bool quiet) =>
        verbose ? "debug" : quiet ? "off" : "warn";

    /// <summary>Whether the parsed subcommand asked for silence.</summary>
    public static bool IsQuietCommand(CliCommand? command) => command switch
    {
        CliCommand.Fetch { Quiet: true } => true,
        CliCommand.Scrape { Quiet: true } => true,
        CliCommand.Serve { Quiet: true } => true,
        _ => false,
    };

    /// <summary>
    /// Port of <c>configure_font_directories</c> in <c>main.rs</c> (upstream 343fdc7): check
    /// every <c>--font-dir</c> is a directory, then hand the list to the renderer before the
    /// first render. Nothing is configured when the list is empty.
    /// </summary>
    /// <remarks>
    /// Rust also bails with "--font-dir requires a render-enabled build" when built without the
    /// render feature; the port always carries the renderer, so that arm has no counterpart.
    /// </remarks>
    public static void ConfigureFontDirectories(IReadOnlyList<string> fontDirs)
    {
        if (fontDirs.Count == 0)
        {
            return;
        }
        foreach (var directory in fontDirs)
        {
            if (!Directory.Exists(directory))
            {
                throw new CliException(
                    $"Font directory does not exist or is not a directory: {directory}");
            }
        }
        if (!PocketCalculator.Render.FontDirectories.Configure(fontDirs))
        {
            throw new CliException("Font directories must be configured before the first render");
        }
    }

    /// <summary>A subcommand's <c>--proxy</c> wins over the global one.</summary>
    public static string? MergeProxy(string? globalProxy, string? commandProxy) =>
        commandProxy ?? globalProxy;

    /// <summary>
    /// Normalize a raw <c>--v8-flags</c> value into the string handed to V8.
    /// Null when the user did not pass the flag, passed an empty string, or
    /// passed only whitespace; V8 is then left untouched.
    /// </summary>
    public static string? NormalizeV8Flags(string? raw)
    {
        if (raw is null)
        {
            return null;
        }
        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>The default flags, with any user flags appended so they win.</summary>
    public static string EffectiveV8Flags(string? user) =>
        NormalizeV8Flags(user) is { } flags ? $"{DefaultV8Flags} {flags}" : DefaultV8Flags;

    /// <summary>The startup banner the server paths print.</summary>
    /// <remarks>
    /// The lines carry trailing spaces, exactly as the Rust raw string does.
    /// </remarks>
    public static string Banner(int port)
    {
        var version = BuildVersion.Value;
        var portText = port.ToString(CultureInfo.InvariantCulture);
        return string.Join('\n', new[]
        {
            "",
            "   ____  _                              ",
            "  / __ \\| |                             ",
            " | |  | | |__  ___  ___ _   _ _ __ __ _ ",
            " | |  | | '_ \\/ __|/ __| | | | '__/ _` |",
            " | |__| | |_) \\__ \\ (__| |_| | | | (_| |",
            "  \\____/|_.__/|___/\\___|\\__,_|_|  \\__,_|",
            "                   ",
            "  Headless Browser v" + version + "",
            "  CDP server: ws://127.0.0.1:" + portText + "/devtools/browser",
            "",
        });
    }

    /// <summary>Print the banner to stdout, as <c>print_banner</c> does.</summary>
    public static void PrintBanner(int port) => Console.Out.Write(Banner(port) + "\n");
}
