using Xunit;

// The Rust suite runs process-per-test. Several page tests set process-wide
// environment variables (OBSCURA_NAV_CHAIN_LIMIT, OBSCURA_ALLOW_PRIVATE_NETWORK)
// and every one of them binds a loopback fixture server, so they run serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Obscura.Browser.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Every fixture server in this suite binds 127.0.0.1, which the SSRF gate
    /// blocks by default.
    /// </summary>
    /// <remarks>
    /// Page-driven fetches go through the context's client, which the fixtures build
    /// with <c>allowPrivateNetwork: true</c>. The ES-module loader owns a standalone
    /// client (as it does in Rust) whose only opt-in is the environment variable, so
    /// the Rust suite ends up relying on the variable being set process-wide by the
    /// tests that set it explicitly. The port sets it once, up front, instead of
    /// depending on test order.
    /// </remarks>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void AllowLoopbackFixtures() =>
        Environment.SetEnvironmentVariable("OBSCURA_ALLOW_PRIVATE_NETWORK", "1");
}
