namespace Obscura.Cli.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// The fixture servers in this suite bind 127.0.0.1, which the SSRF gate
    /// refuses by default.
    /// </summary>
    /// <remarks>
    /// The Rust tests each call <c>std::env::set_var("OBSCURA_ALLOW_PRIVATE_NETWORK", "1")</c>
    /// inline, which is safe there because nextest runs one process per test.
    /// Here one process runs the whole assembly, so it is set once up front:
    /// setting and clearing it per test class races with whatever else is
    /// running, and made the value depend on test order.
    /// </remarks>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void AllowLoopbackFixtures() =>
        Environment.SetEnvironmentVariable("OBSCURA_ALLOW_PRIVATE_NETWORK", "1");
}
