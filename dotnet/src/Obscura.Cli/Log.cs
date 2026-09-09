using System.Globalization;

namespace Obscura.Cli;

/// <summary>Severity levels, matching the tracing filter strings the CLI selects.</summary>
public enum LogLevel
{
    /// <summary><c>off</c>: nothing is emitted.</summary>
    Off,

    /// <summary><c>warn</c>: the default.</summary>
    Warn,

    /// <summary><c>debug</c>: <c>--verbose</c>.</summary>
    Debug,
}

/// <summary>
/// The <c>tracing</c> surface <c>main.rs</c> uses, reduced to what the CLI emits.
/// </summary>
/// <remarks>
/// The filter is the load-bearing part and is ported exactly: at the default
/// <c>warn</c> level every <c>tracing::info!</c> in the CLI is suppressed, which
/// is why <c>serve</c> normally prints only its banner. Logs go to stderr, as
/// the reference configures. The line layout is close to but not identical with
/// <c>tracing_subscriber</c>'s (no ANSI styling, no per-module target), because
/// nothing observable depends on it.
/// </remarks>
public static class Log
{
    /// <summary>The active level. Set once from the parsed filter string.</summary>
    public static LogLevel Level { get; private set; } = LogLevel.Warn;

    /// <summary>Apply a filter string produced by <see cref="CliOptions.SelectLogFilter"/>.</summary>
    public static void SetFilter(string filter) => Level = filter switch
    {
        "debug" => LogLevel.Debug,
        "off" => LogLevel.Off,
        _ => LogLevel.Warn,
    };

    /// <summary>An <c>INFO</c> record; suppressed at the default <c>warn</c> level.</summary>
    public static void Info(string message) => Write(LogLevel.Debug, "INFO", message);

    /// <summary>A <c>WARN</c> record.</summary>
    public static void Warn(string message) => Write(LogLevel.Warn, "WARN", message);

    /// <summary>A <c>DEBUG</c> record.</summary>
    public static void Debug(string message) => Write(LogLevel.Debug, "DEBUG", message);

    private static void Write(LogLevel minimum, string label, string message)
    {
        if (Level == LogLevel.Off || Level < minimum)
        {
            return;
        }
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
        Console.Error.WriteLine($"{stamp} {label,5} obscura_cli: {message}");
    }
}
