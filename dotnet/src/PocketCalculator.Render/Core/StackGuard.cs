using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using PocketCalculator.Dom;

namespace PocketCalculator.Render;

/// <summary>
/// Stack-depth checks for the recursive walks of the render pipeline.
/// </summary>
/// <remarks>
/// A stack overflow cannot be caught in .NET: it ends the process, and in <c>serve</c> or
/// <c>mcp</c> every other session with it (SECURITY.md C5). Three things keep a deep tree from
/// getting there:
/// <list type="bullet">
/// <item>The box tree is bounded by <see cref="BuildContext.MaxBoxDepth"/>, so layout and paint
/// recurse a bounded number of levels.</item>
/// <item>A tree deeper than <see cref="DeepTreeThreshold"/> is laid out on a dedicated thread
/// with a large stack (<see cref="RunWithStackFor{T}"/>): a table box costs several taffy levels
/// per element, and the 1.5 MB of a thread-pool thread does not hold 768 of them.</item>
/// <item>Walks over the DOM itself (not the box tree) stop descending when the stack runs low
/// (<see cref="CanDescend"/>), which only ever skips elements too deep to have a box; the
/// box-tree recursions throw instead (<see cref="Ensure"/>), so anything that still runs out
/// fails as an ordinary exception.</item>
/// </list>
/// </remarks>
internal static class StackGuard
{
    /// <summary>DOM depth above which layout moves to a thread with a large stack.</summary>
    internal const int DeepTreeThreshold = 256;

    /// <summary>The stack reserved for that thread. Reserved, not committed, on every platform.</summary>
    internal const int DeepTreeStackBytes = 256 * 1024 * 1024;

    [ThreadStatic]
    private static bool _onDeepStack;

    /// <summary>
    /// True while there is stack left for one more level of a recursive walk. Also where
    /// those walks observe the pass's deadline: it throws
    /// <see cref="OperationCanceledException"/> once <see cref="WorkCancellation.Current"/>
    /// is cancelled, so every recursive walk that guards its depth also stops on time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool CanDescend()
    {
        WorkCancellation.ThrowIfCancellationRequested();
        return RuntimeHelpers.TryEnsureSufficientExecutionStack();
    }

    /// <summary>
    /// Throw <see cref="InsufficientExecutionStackException"/> when the stack is low, or
    /// <see cref="OperationCanceledException"/> when the pass has been cancelled.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Ensure()
    {
        WorkCancellation.ThrowIfCancellationRequested();
        RuntimeHelpers.EnsureSufficientExecutionStack();
    }

    /// <summary>
    /// Run <paramref name="body"/> here, or, when <paramref name="tree"/> is deeper than
    /// <see cref="DeepTreeThreshold"/>, on a dedicated large-stack thread while this one waits.
    /// </summary>
    internal static T RunWithStackFor<T>(DomTree tree, Func<T> body)
    {
        if (_onDeepStack || !IsDeeperThan(tree, DeepTreeThreshold))
        {
            return body();
        }

        T result = default!;
        ExceptionDispatchInfo? failure = null;

        // The pass's cancellation scope is thread-static; the layout thread has to
        // observe the same deadline as the thread that is waiting for it.
        CancellationToken cancellation = WorkCancellation.Current;
        var thread = new Thread(
            () =>
            {
                _onDeepStack = true;
                using var scope = WorkCancellation.Enter(cancellation);
                try
                {
                    result = body();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            },
            DeepTreeStackBytes)
        {
            IsBackground = true,
            Name = "pocket-calculator deep layout",
        };
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    /// <summary>Whether any node of the tree, shadow trees included, is deeper than the limit.</summary>
    internal static bool IsDeeperThan(DomTree tree, int limit)
    {
        // Iterative: this is the check that decides whether recursion is safe.
        var stack = new Stack<(NodeId Node, int Depth)>();
        stack.Push((tree.Document, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > limit)
            {
                return true;
            }

            if (tree.GetNode(node) is not { } current)
            {
                continue;
            }

            for (var child = current.FirstChild; child is { } id; child = tree.GetNode(id)?.NextSibling)
            {
                stack.Push((id, depth + 1));
            }

            if (tree.ShadowRootOf(node) is { } root)
            {
                stack.Push((root, depth + 1));
            }
        }

        return false;
    }
}
