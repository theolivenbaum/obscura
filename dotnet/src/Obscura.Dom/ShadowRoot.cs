namespace Obscura.Dom;

/// <summary>The encapsulation mode recorded by a native shadow root.</summary>
public enum ShadowRootMode
{
    Open,
    Closed,
}

/// <summary>
/// Stable metadata for a shadow-root node in this tree.
///
/// A shadow root owns an ordinary child list, but is not an ordinary child of its host. The
/// separate host edge keeps parentNode-style walks scoped to one tree while still allowing
/// composed-tree operations to cross explicitly.
/// </summary>
public readonly record struct ShadowRoot(NodeId Id, NodeId Host, ShadowRootMode Mode);

public enum AttachShadowError
{
    None = 0,
    HostIsNotElement,
    HostAlreadyHasShadowRoot,
    InvalidShadowRoot,
}

public static class AttachShadowErrorExtensions
{
    public static string Message(this AttachShadowError error) => error switch
    {
        AttachShadowError.HostIsNotElement => "shadow host is not an element",
        AttachShadowError.HostAlreadyHasShadowRoot => "shadow host already has a shadow root",
        AttachShadowError.InvalidShadowRoot => "shadow root is not a detached fragment node",
        _ => "ok",
    };
}

/// <summary>Thrown by <see cref="DomTree.AttachShadowRoot"/> only when the caller opts into throwing.</summary>
public sealed class AttachShadowException(AttachShadowError error)
    : InvalidOperationException(error.Message())
{
    public AttachShadowError Error { get; } = error;
}
