namespace PocketCalculator.Browser;

/// <summary>Where a page is in its document lifecycle.</summary>
public enum LifecycleState
{
    Idle,
    Loading,
    DomContentLoaded,
    Loaded,
    NetworkIdle,
    Failed,
}

/// <summary>
/// How far the current document got: committed (the response arrived and the document
/// was created), its <c>DOMContentLoaded</c> dispatched, its <c>load</c> dispatched.
/// </summary>
/// <remarks>
/// Port addition. <see cref="LifecycleState"/> says what a navigation waited for;
/// this says what the document reached, which differs when the navigation deadline
/// stops a document that has already committed (<see cref="Page.LoadAbandoned"/>).
/// </remarks>
public enum DocumentReadiness
{
    /// <summary>No document yet: the navigation has not had its response.</summary>
    None,

    /// <summary>The response arrived and the document was built from it.</summary>
    Committed,

    /// <summary>The document's <c>DOMContentLoaded</c> was dispatched.</summary>
    DomContentLoaded,

    /// <summary>The document's <c>load</c> was dispatched.</summary>
    Loaded,
}

/// <summary>The predicates Rust declares as inherent methods on the enum.</summary>
public static class LifecycleStateExtensions
{
    public static bool IsLoading(this LifecycleState state) => state == LifecycleState.Loading;

    public static bool IsLoaded(this LifecycleState state) =>
        state is LifecycleState.Loaded or LifecycleState.NetworkIdle;

    public static bool IsNetworkIdle(this LifecycleState state) => state == LifecycleState.NetworkIdle;
}

/// <summary>The readiness level a navigation waits for.</summary>
public enum WaitUntil
{
    Load,
    DomContentLoaded,
    NetworkIdle0,
    NetworkIdle2,
}

public static class WaitUntilExtensions
{
    /// <summary>Port of <c>WaitUntil::from_str</c>; an unknown value is <c>Load</c>.</summary>
    public static WaitUntil ParseWaitUntil(string value) => value switch
    {
        "domcontentloaded" => WaitUntil.DomContentLoaded,
        "networkidle0" or "networkIdle" or "networkidle" => WaitUntil.NetworkIdle0,
        "networkidle2" => WaitUntil.NetworkIdle2,
        _ => WaitUntil.Load,
    };
}
