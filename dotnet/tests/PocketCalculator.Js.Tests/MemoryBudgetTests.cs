using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// M7: memory the V8 heap cap cannot see is bounded, and a runtime nobody configured still
/// gets a heap cap.
/// </summary>
public sealed class MemoryBudgetTests
{
    [Fact]
    public void BindingCallsFromASynchronousLoopAreCapped()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        fixture.Runtime.ExecutePreloadScript(BindingPreload.Source("b"));
        fixture.Runtime.Evaluate(
            "for (let i = 0; i < 100000; i++) b('payload ' + i);");

        IReadOnlyList<(string Name, string Payload)> calls = fixture.Runtime.TakePendingBindingCalls();
        Assert.Equal(CoreOps.BindingQueueEntryLimit(), calls.Count);

        // Earlier calls are kept, newest dropped; draining frees the queue.
        Assert.Equal("payload 0", calls[0].Payload);
        fixture.Runtime.Evaluate("b('after');");
        Assert.Equal("after", Assert.Single(fixture.Runtime.TakePendingBindingCalls()).Payload);
    }

    [Fact]
    public void BindingCallPayloadBytesAreCapped()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        fixture.Runtime.ExecutePreloadScript(BindingPreload.Source("b"));
        fixture.Runtime.Evaluate(
            "const big = 'x'.repeat(1024 * 1024); for (let i = 0; i < 64; i++) b(big); 0");
        IReadOnlyList<(string Name, string Payload)> calls = fixture.Runtime.TakePendingBindingCalls();
        long bytes = 0;
        foreach ((string name, string payload) in calls)
        {
            bytes += name.Length + payload.Length;
        }

        Assert.InRange(bytes, 1, CoreOps.BindingQueueByteLimit());
        Assert.True(calls.Count < 64);
    }

    [Fact]
    public void UnconfiguredRuntimeStillHasAHeapCap()
    {
        using var fixture = RuntimeFixture.Blank();
        Assert.True(fixture.Runtime.HeapLimitBytes > 0);
    }
}
