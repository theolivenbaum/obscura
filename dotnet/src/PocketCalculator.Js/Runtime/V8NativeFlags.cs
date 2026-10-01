using System.Runtime.InteropServices;
using Microsoft.ClearScript.V8;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// Sets a V8 flag ClearScript has no typed setting for, through
/// <c>v8::V8::SetFlagsFromString</c> in ClearScript's own native library.
/// </summary>
/// <remarks>
/// <para>
/// Port addition. ClearScript does not expose V8's flag setter (see
/// <see cref="V8Flags"/>), but its Linux and macOS libraries export it. It is called
/// only while <c>v8::debug::GetCurrentPlatform()</c> is still null, that is before V8
/// is initialized: once it is, V8 has frozen its flags and a late call aborts the
/// process. Where either symbol is missing (the Windows build exports neither) nothing
/// is set and the caller falls back to what it can enforce in script.
/// </para>
/// <para>
/// This loads no new native code: it resolves the library ClearScript itself loads,
/// by the same name and search path.
/// </para>
/// </remarks>
internal static class V8NativeFlags
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetFlagsFromString([MarshalAs(UnmanagedType.LPUTF8Str)] string flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetCurrentPlatform();

    private const string SetFlagsSymbol = "_ZN2v82V818SetFlagsFromStringEPKc";
    private const string CurrentPlatformSymbol = "_ZN2v85debug18GetCurrentPlatformEv";

    /// <summary>
    /// Applies <paramref name="flags"/> if V8 has not been initialized in this process.
    /// </summary>
    /// <returns>Whether the flags were handed to V8.</returns>
    public static bool TryApplyBeforeInitialization(string flags)
    {
        try
        {
            if (!TryLoadLibrary(out IntPtr library)
                || !NativeLibrary.TryGetExport(library, SetFlagsSymbol, out IntPtr setFlags)
                || !NativeLibrary.TryGetExport(library, CurrentPlatformSymbol, out IntPtr currentPlatform))
            {
                return false;
            }

            if (Marshal.GetDelegateForFunctionPointer<GetCurrentPlatform>(currentPlatform)() != IntPtr.Zero)
            {
                // V8 is up (an engine was created outside PocketCalculatorJsRuntime): its
                // flags are frozen, and setting one now is a V8_Fatal.
                return false;
            }

            Marshal.GetDelegateForFunctionPointer<SetFlagsFromString>(setFlags)(flags);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException
            or EntryPointNotFoundException or MarshalDirectiveException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryLoadLibrary(out IntPtr library)
    {
        string? rid = RuntimeIdentifier();
        if (rid is null)
        {
            library = IntPtr.Zero;
            return false;
        }

        string name = "ClearScriptV8." + rid;
        var assembly = typeof(V8Runtime).Assembly;
        if (NativeLibrary.TryLoad(name, assembly, null, out library))
        {
            return true;
        }

        string extension = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        foreach (string candidate in (string[])[
            Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", name + extension),
            Path.Combine(AppContext.BaseDirectory, name + extension)])
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out library))
            {
                return true;
            }
        }

        return false;
    }

    private static string? RuntimeIdentifier()
    {
        string? os = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsWindows() ? "win" : null;
        string? arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        return os is null || arch is null ? null : os + "-" + arch;
    }
}
