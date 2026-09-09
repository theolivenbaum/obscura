namespace Obscura.Cli;

/// <summary>
/// The port of <c>anyhow::bail!</c> in <c>main.rs</c>: a message the CLI reports
/// as <c>Error: {message}</c> on stderr before exiting 1.
/// </summary>
/// <remarks>
/// Every <c>anyhow::bail!</c> in the reference reaches <c>main</c>'s
/// <c>anyhow::Result</c> and is printed by the Rust runtime, which also sets the
/// process exit code to 1. Both are user visible and parity tested.
/// </remarks>
public sealed class CliException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A subcommand whose engine is not available in this build.
/// </summary>
/// <remarks>
/// Exiting 70 (EX_SOFTWARE) rather than 0 is deliberate: a silent success would
/// let CLI parity tests pass against an engine that never ran. Nothing throws
/// this today, now that <c>serve</c> and <c>mcp</c> are wired to
/// <c>Obscura.Cdp</c> and <c>Obscura.Mcp</c>; it is kept as the shape a future
/// unavailable-engine path should use.
/// </remarks>
public sealed class NotPortedException(string message) : Exception(message);
