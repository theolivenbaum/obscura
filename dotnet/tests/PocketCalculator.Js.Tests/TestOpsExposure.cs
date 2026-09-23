using System.Runtime.CompilerServices;
using PocketCalculator.Js.Runtime;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Publishes each runtime's op table as <c>__obscura_test_ops</c> for this test
/// assembly, as upstream's <c>#[cfg(test)]</c> <c>expose_ops_for_tests</c> does for
/// obscura-js's own tests. Page script in production never sees it.
/// </summary>
internal static class TestOpsExposure
{
    [ModuleInitializer]
    internal static void Enable() => BootstrapLoader.ExposeOpsForTests = true;
}
