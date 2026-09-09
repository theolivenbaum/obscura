namespace Obscura.Api;

/// <summary>Which arm of the Rust <c>obscura::Error</c> enum an error is.</summary>
public enum ObscuraErrorKind
{
    /// <summary><c>Error::Navigation</c>.</summary>
    Navigation,

    /// <summary><c>Error::JsEval</c>.</summary>
    JsEval,

    /// <summary><c>Error::Timeout</c>.</summary>
    Timeout,

    /// <summary><c>Error::ElementNotFound</c>.</summary>
    ElementNotFound,

    /// <summary><c>Error::NoPage</c>.</summary>
    NoPage,

    /// <summary><c>Error::Internal</c>, the transparent <c>anyhow</c> passthrough.</summary>
    Internal,
}

/// <summary>
/// Port of the Rust <c>obscura::Error</c> enum.
/// </summary>
/// <remarks>
/// Rust returns <c>Result&lt;T, Error&gt;</c>; every caller of this API in the
/// reference either propagates with <c>?</c> or unwraps, so the port throws and
/// carries the arm in <see cref="Kind"/>. The messages are exactly what
/// <c>thiserror</c> renders.
/// </remarks>
public sealed class ObscuraException : Exception
{
    /// <summary>Create an error of the given kind with an already-rendered message.</summary>
    public ObscuraException(ObscuraErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    /// <summary>Which enum arm this error corresponds to.</summary>
    public ObscuraErrorKind Kind { get; }

    /// <summary><c>Error::Navigation</c>: <c>navigation error: {0}</c>.</summary>
    public static ObscuraException Navigation(string detail) =>
        new(ObscuraErrorKind.Navigation, $"navigation error: {detail}");

    /// <summary><c>Error::JsEval</c>: <c>JS evaluation error: {0}</c>.</summary>
    public static ObscuraException JsEval(string detail) =>
        new(ObscuraErrorKind.JsEval, $"JS evaluation error: {detail}");

    /// <summary><c>Error::Timeout</c>: <c>timeout: {0}</c>.</summary>
    public static ObscuraException Timeout(string detail) =>
        new(ObscuraErrorKind.Timeout, $"timeout: {detail}");

    /// <summary><c>Error::ElementNotFound</c>: <c>element not found: {0}</c>.</summary>
    public static ObscuraException ElementNotFound(string detail) =>
        new(ObscuraErrorKind.ElementNotFound, $"element not found: {detail}");

    /// <summary><c>Error::NoPage</c>: <c>no page session</c>.</summary>
    public static ObscuraException NoPage() =>
        new(ObscuraErrorKind.NoPage, "no page session");

    /// <summary><c>Error::Internal</c>: transparent, so the message is the inner one.</summary>
    public static ObscuraException Internal(Exception inner) =>
        new(ObscuraErrorKind.Internal, inner.Message, inner);
}
