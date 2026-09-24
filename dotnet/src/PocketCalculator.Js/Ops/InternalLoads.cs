using PocketCalculator.Net;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// One engine-initiated load (<c>op_fetch_url</c> with <c>internalLoad</c>) whose body the
/// host holds instead of handing it to page script.
/// </summary>
/// <param name="Mode">The request mode: <c>navigate</c> for a frame document, <c>no-cors</c>
/// for a script or stylesheet. A body is only ever consumed as what it was loaded as.</param>
/// <param name="Status">The final HTTP status.</param>
/// <param name="RequestUrl">The URL the load asked for.</param>
/// <param name="FinalUrl">The URL the response came from, after redirects.</param>
/// <param name="Body">The decoded body text.</param>
/// <param name="Tainted">Whether any hop left the requesting document's origin, which is what
/// makes the response cross-origin (Fetch's response tainting).</param>
public sealed record InternalLoad(
    string Mode,
    int Status,
    string RequestUrl,
    string FinalUrl,
    string Body,
    bool Tainted,
    ReferrerPolicy? ReferrerPolicyHeader = null);

/// <summary>
/// The host-side store of <see cref="InternalLoad"/> bodies, per document.
/// </summary>
/// <remarks>
/// <para>
/// Port addition (SECURITY.md C3). In the Rust engine an internal load returns its body to
/// the shim as JSON, and the shim parses, scans and evaluates it with page-reachable
/// built-ins (<c>JSON.parse</c>, <c>String.prototype.replace</c>, <c>Promise.prototype.then</c>),
/// so a page that replaced one of them read every cross-origin script, stylesheet and frame
/// document the engine loaded for it. Here the op returns a token instead of a cross-origin
/// body, and the host consumes the body itself: it runs a script
/// (<c>op_run_fetched_script</c>), builds a frame (<c>op_frame_document_from_load</c>) or
/// installs a stylesheet (<c>op_load_stylesheet</c>).
/// </para>
/// <para>
/// Each token is taken once, and only by the realm whose load produced it and for the mode
/// it was loaded with. The store is bounded: a load the shim abandons (a superseded iframe
/// src, a removed script) is never taken, so the oldest entries are evicted past the cap.
/// </para>
/// </remarks>
public static class InternalLoads
{
    internal const int MaxEntries = 1024;

    internal const long MaxChars = 128L * 1024 * 1024;

    /// <summary>Stores <paramref name="load"/> for <paramref name="document"/> and returns its token.</summary>
    public static long Put(PocketCalculatorState document, InternalLoad load)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(load);
        var store = document.InternalLoadStore;
        lock (store.Gate)
        {
            var id = ++store.Counter;
            store.Entries[id] = load;
            store.Order.Enqueue(id);
            store.Chars += load.Body.Length;
            while (store.Order.Count > 0 && (store.Entries.Count > MaxEntries || store.Chars > MaxChars))
            {
                var oldest = store.Order.Dequeue();
                if (store.Entries.Remove(oldest, out var evicted))
                {
                    store.Chars -= evicted.Body.Length;
                }
            }

            return id;
        }
    }

    /// <summary>
    /// Takes the load behind <paramref name="token"/>, provided it belongs to
    /// <paramref name="document"/> and was loaded in <paramref name="mode"/>.
    /// </summary>
    public static InternalLoad? Take(PocketCalculatorState document, double token, string mode)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!double.IsFinite(token) || token < 1 || token > long.MaxValue || token != Math.Floor(token))
        {
            return null;
        }

        var store = document.InternalLoadStore;
        lock (store.Gate)
        {
            var id = (long)token;
            if (!store.Entries.TryGetValue(id, out var load)
                || !string.Equals(load.Mode, mode, StringComparison.Ordinal))
            {
                return null;
            }

            store.Entries.Remove(id);
            store.Chars -= load.Body.Length;
            return load;
        }
    }
}

/// <summary>The mutable half of <see cref="InternalLoads"/>, one per document state.</summary>
public sealed class InternalLoadStore
{
    internal Lock Gate { get; } = new();

    internal Dictionary<long, InternalLoad> Entries { get; } = [];

    internal Queue<long> Order { get; } = new();

    internal long Counter { get; set; }

    internal long Chars { get; set; }

    /// <summary>How many bodies are waiting to be consumed.</summary>
    public int Count
    {
        get
        {
            lock (Gate)
            {
                return Entries.Count;
            }
        }
    }
}
