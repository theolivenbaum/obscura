using System.Runtime.InteropServices;

namespace Obscura.Cli;

/// <summary>
/// Environment writes that the native side has to see too.
/// </summary>
/// <remarks>
/// <c>Environment.SetEnvironmentVariable</c> updates only the managed copy on
/// Unix; it does not call <c>setenv</c>, so native code reading <c>getenv</c>
/// (V8 for <c>TZ</c>, for one) never observes it. The reference relies on the
/// process environment actually changing, so the write goes through both.
/// </remarks>
public static class ProcessEnvironment
{
    /// <summary>Set a variable for both managed and native readers.</summary>
    public static void Set(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        if (OperatingSystem.IsWindows())
        {
            // The Windows CRT and the managed environment already share a block.
            return;
        }
        try
        {
            SetEnv(name, value, 1);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // No libc to reach: the managed write above still stands, which is
            // what every managed reader in the engine uses.
        }
    }

    [DllImport("libc", EntryPoint = "setenv", CharSet = CharSet.Ansi)]
    private static extern int SetEnv(string name, string value, int overwrite);
}
