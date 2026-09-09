using Xunit;

// The Rust suite runs process-per-test (nextest). Several of these tests bind a
// loopback fixture server and one sets a process-wide environment variable, so
// they run serially here.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Obscura.Mcp.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// The fixture servers in this suite bind 127.0.0.1, which the SSRF gate
    /// refuses by default - exactly as the issue reporter in #618 had to pass
    /// <c>--allow-private-network</c> to run their repro.
    /// </summary>
    /// <remarks>
    /// <c>click_on_a_submit_button_issues_the_request_before_replying</c> sets this
    /// inline in Rust, where each test owns its process. Here it is set once, up
    /// front, so the value does not depend on test order.
    /// </remarks>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void AllowLoopbackFixtures() =>
        Environment.SetEnvironmentVariable("OBSCURA_ALLOW_PRIVATE_NETWORK", "1");
}
