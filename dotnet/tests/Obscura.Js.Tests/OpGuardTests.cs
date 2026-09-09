using Obscura.Js.Ops;
using Xunit;

namespace Obscura.Js.Tests;

public sealed class OpGuardTests
{
    [Fact]
    public void Run_returns_the_body_result_when_it_succeeds() =>
        Assert.Equal("ok", OpGuard.Run("op_test", () => "ok", onFailure: ""));

    [Fact]
    public void Run_contains_an_exception_and_returns_the_failure_value() =>
        // An exception escaping into V8's frame is a process abort in the Rust
        // engine; the shim expects the documented failure value instead.
        Assert.Equal("", OpGuard.Run<string>("op_test", () => throw new InvalidOperationException("boom"), onFailure: ""));

    [Fact]
    public void Run_reports_contained_failures()
    {
        var seen = new List<string>();
        void Handler(string op, Exception _) => seen.Add(op);
        OpGuard.OpFailed += Handler;
        try
        {
            OpGuard.Run<string>("op_dom", () => throw new InvalidOperationException(), "null");
        }
        finally
        {
            OpGuard.OpFailed -= Handler;
        }
        Assert.Equal(["op_dom"], seen);
    }

    [Fact]
    public void Run_does_not_contain_a_script_interrupt()
    {
        // The watchdog terminates a runaway page by interrupting the isolate.
        // Swallowing that would defeat the termination path entirely.
        Assert.Throws<Microsoft.ClearScript.ScriptInterruptedException>(
            () => OpGuard.Run<string>("op_test", () => throw new Microsoft.ClearScript.ScriptInterruptedException(), ""));
    }

    [Fact]
    public void Void_run_contains_an_exception() =>
        OpGuard.Run("op_test", () => throw new InvalidOperationException("boom"));

    [Fact]
    public async Task RunAsync_contains_an_exception() =>
        Assert.Equal("", await OpGuard.RunAsync<string>(
            "op_fetch_url", () => throw new HttpRequestException("no network"), onFailure: ""));
}
