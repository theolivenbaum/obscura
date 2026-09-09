using System.Runtime.CompilerServices;
using Xunit;

namespace Obscura.Parity.Tests.Harness;

/// <summary>
/// A fact that runs only when both engines are available.
/// </summary>
/// <remarks>
/// Skipping is deliberate rather than failing, so a developer without a Rust
/// toolchain can still run the rest of the suite. The trap is that skipped
/// parity coverage looks identical to passing coverage in a summary line, so
/// treat "parity validated" as a claim that needs the run count checked, not
/// just a green suite. <see cref="ParityAvailability"/> asserts the suite is
/// actually wired up.
/// </remarks>
public sealed class ParityFactAttribute : FactAttribute
{
    public ParityFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (ReferenceEngine.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>Same gating, for theories.</summary>
public sealed class ParityTheoryAttribute : TheoryAttribute
{
    public ParityTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (ReferenceEngine.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
