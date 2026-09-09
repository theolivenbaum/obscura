using System.Runtime.InteropServices;

namespace Obscura.Cli;

/// <summary>
/// Ends the process without running native shutdown handlers.
/// </summary>
/// <remarks>
/// <para>
/// Roughly one run in ten segfaulted after doing its work correctly. The exit
/// code was 139 and the output was complete and byte-identical to the
/// reference, because the crash lands after <c>main</c> returns and after
/// stdout is flushed, in the native teardown that runs on the way out. Measured
/// on this machine: 0 of 150 for <c>--version</c>, which never builds an
/// isolate, against 17 of 150 for <c>fetch about:blank</c> and 20 of 150 for a
/// <c>--dump</c>, so it tracks having created a V8 isolate and not what the page
/// contains. It predates the port work in this branch: the base commit measured
/// 19 and 12 per 320 sweep cases against 15 and 7 for the current build.
/// </para>
/// <para>
/// <see cref="Environment.Exit"/> does not avoid it (26 of 150), because it
/// still runs the C runtime's <c>atexit</c> chain. <c>_exit</c> does (0 of 150):
/// it terminates immediately, skipping <c>atexit</c> handlers and static
/// destructors. That is the standard remedy for an embedder that has already
/// released its V8 state explicitly, which <c>ObscuraJsRuntime.Dispose</c> has
/// done by this point, and it costs nothing the reference engine provides -
/// Rust drops the isolate and returns from <c>main</c> with no equivalent
/// chain to run.
/// </para>
/// <para>
/// Because this skips CLR shutdown, every byte must already be flushed, which
/// is why the flush lives here rather than at the call sites. No new native
/// dependency: libc is the platform, not a package. Windows has no
/// <c>_exit</c> with these semantics and has not shown the crash, so it keeps
/// <see cref="Environment.Exit"/>.
/// </para>
/// </remarks>
internal static class ProcessExit
{
    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void SysExit(int status);

    /// <summary>
    /// Flushes the standard streams and terminates with <paramref name="status"/>.
    /// Does not return; the declared value is there so callers can write
    /// <c>return ProcessExit.Immediately(code);</c> and stay readable.
    /// </summary>
    internal static int Immediately(int status)
    {
        Console.Out.Flush();
        Console.Error.Flush();
        if (OperatingSystem.IsWindows())
        {
            Environment.Exit(status);
        }
        else
        {
            SysExit(status);
        }
        return status;
    }
}
