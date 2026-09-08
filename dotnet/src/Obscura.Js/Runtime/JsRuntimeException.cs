namespace Obscura.Js.Runtime;

/// <summary>
/// A JavaScript-level failure reported out of the runtime.
/// </summary>
/// <remarks>
/// The Rust engine returns <c>Result&lt;T, String&gt;</c> from every one of
/// these entry points and its callers branch on the message text, so the
/// message is part of the contract: it is reproduced verbatim rather than
/// rewritten into a .NET phrasing.
/// </remarks>
public sealed class JsRuntimeException(string message) : Exception(message);
