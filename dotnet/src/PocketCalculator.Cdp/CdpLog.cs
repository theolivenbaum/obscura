using System.Buffers;
using System.Globalization;
using System.Text;

namespace PocketCalculator.Cdp;

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
/// <c>debug</c> follow <c>POCKETCALCULATOR_LOG</c> / <c>RUST_LOG</c>.
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
            string.Create(CultureInfo.InvariantCulture, $"obscura-cdp {label}: {EscapeControl(message)}"));
    }

    private static readonly SearchValues<char> ControlChars = SearchValues.Create(
        "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F"
        + "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F"
        + "\u007F\u0085\u2028\u2029");

    /// <summary>
    /// A log message with every control character (C0, DEL, NEL and the Unicode
    /// line and paragraph separators) written as a <c>\uXXXX</c> escape.
    /// </summary>
    /// <remarks>
    /// SECURITY.md I2: log lines carry client-controlled text (CDP method names,
    /// URLs, selectors). A CR or LF in one forged whole log lines, and an ESC
    /// could drive the operator's terminal. One record is one line now. A message
    /// with nothing to escape is returned as is.
    /// </remarks>
    public static string EscapeControl(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var first = message.AsSpan().IndexOfAny(ControlChars);
        if (first < 0)
        {
            return message;
        }

        var builder = new StringBuilder(message.Length + 16);
        builder.Append(message.AsSpan(0, first));
        foreach (var c in message.AsSpan(first))
        {
            if (ControlChars.Contains(c))
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static CdpLogLevel ReadLevel()
    {
        var raw = Environment.GetEnvironmentVariable("POCKETCALCULATOR_LOG")
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
