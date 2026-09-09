using System.Globalization;

namespace Obscura.Cdp;

/// <summary>
/// The <c>tracing</c> macros the CDP crate calls, reduced to what this port
/// needs.
/// </summary>
/// <remarks>
/// <para>
/// Rust's <c>tracing</c> is a no-op until a subscriber is installed, so the
/// <c>info!("INTERCEPTION: ...")</c> lines on the hot path cost nothing in tests
/// and print only when the CLI turns them on. Reproducing that here matters:
/// writing every one of them to stderr unconditionally would put a line on the
/// wire's console for every intercepted request.
/// </para>
/// <para>
/// <c>warn</c> and <c>error</c> are on by default, because losing them hides
/// exactly the conditions they were added for (a refused connection, a
/// watchdog-terminated isolate, a full deferral queue). <c>info</c> and
/// <c>debug</c> follow <c>OBSCURA_LOG</c> / <c>RUST_LOG</c>.
/// </para>
/// </remarks>
public static class CdpLog
{
    private static readonly Lazy<CdpLogLevel> Configured = new(ReadLevel);

    public static CdpLogLevel Level => Configured.Value;

    public static void Error(string message) => Write(CdpLogLevel.Error, "ERROR", message);

    public static void Warn(string message) => Write(CdpLogLevel.Warn, "WARN", message);

    public static void Info(string message) => Write(CdpLogLevel.Info, "INFO", message);

    public static void Debug(string message) => Write(CdpLogLevel.Debug, "DEBUG", message);

    private static void Write(CdpLogLevel level, string label, string message)
    {
        if (level > Configured.Value)
        {
            return;
        }

        Console.Error.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"obscura-cdp {label}: {message}"));
    }

    private static CdpLogLevel ReadLevel()
    {
        var raw = Environment.GetEnvironmentVariable("OBSCURA_LOG")
                  ?? Environment.GetEnvironmentVariable("RUST_LOG");
        if (raw is null)
        {
            return CdpLogLevel.Warn;
        }

        // `RUST_LOG` is a directive list; the CDP server only ever reads a
        // bare level out of it, and an unrecognised directive means "leave the
        // default alone" rather than "print everything".
        foreach (var directive in raw.Split(','))
        {
            var value = directive.Contains('=', StringComparison.Ordinal)
                ? directive[(directive.LastIndexOf('=') + 1)..]
                : directive;
            switch (value.Trim().ToLowerInvariant())
            {
                case "off":
                    return CdpLogLevel.Off;
                case "error":
                    return CdpLogLevel.Error;
                case "warn":
                    return CdpLogLevel.Warn;
                case "info":
                    return CdpLogLevel.Info;
                case "debug":
                case "trace":
                    return CdpLogLevel.Debug;
                default:
                    continue;
            }
        }

        return CdpLogLevel.Warn;
    }
}

/// <summary>Verbosity, ordered so a smaller value is louder.</summary>
public enum CdpLogLevel
{
    Off = 0,
    Error = 1,
    Warn = 2,
    Info = 3,
    Debug = 4,
}
