namespace Obscura.Net;

/// <summary>Passive observer for an outgoing request.</summary>
/// <param name="request">The request that is about to go out.</param>
public delegate void RequestCallback(RequestInfo request);

/// <summary>Passive observer for a completed response.</summary>
/// <param name="request">The request the response answers.</param>
/// <param name="response">The response.</param>
public delegate void ResponseCallback(RequestInfo request, Response response);

/// <summary>
/// Page-scoped store for the passive on_request/on_response callbacks (issue
/// #408). Each Page owns one, so a callback never fires for another page's
/// requests and dies with its page. The HTTP client itself stays callback-free;
/// page-driven fetches pass the page's registry in. Ids keep the 64-bit shape
/// #416 established on <c>Page::on_request</c>/<c>on_response</c>.
/// </summary>
public sealed class CallbackRegistry
{
    private readonly List<(ulong Id, RequestCallback Callback)> _onRequest = [];
    private readonly List<(ulong Id, ResponseCallback Callback)> _onResponse = [];
    private readonly System.Threading.Lock _lock = new();
    private ulong _idCounter = 1;

    private ulong NextId()
    {
        lock (_lock)
        {
            return _idCounter++;
        }
    }

    /// <summary>
    /// Register a request callback; the returned id detaches it via
    /// <see cref="RemoveRequest"/>.
    /// </summary>
    public ulong AddRequest(RequestCallback callback)
    {
        var id = NextId();
        lock (_lock)
        {
            _onRequest.Add((id, callback));
        }

        return id;
    }

    /// <summary>Register a response callback; see <see cref="AddRequest"/>.</summary>
    public ulong AddResponse(ResponseCallback callback)
    {
        var id = NextId();
        lock (_lock)
        {
            _onResponse.Add((id, callback));
        }

        return id;
    }

    /// <summary>
    /// Detach a request callback. Returns true when the id was found and removed, so
    /// a double detach is a visible no-op.
    /// </summary>
    public bool RemoveRequest(ulong id)
    {
        lock (_lock)
        {
            return _onRequest.RemoveAll(entry => entry.Id == id) != 0;
        }
    }

    /// <summary>Detach a response callback; see <see cref="RemoveRequest"/>.</summary>
    public bool RemoveResponse(ulong id)
    {
        lock (_lock)
        {
            return _onResponse.RemoveAll(entry => entry.Id == id) != 0;
        }
    }

    /// <summary>
    /// True when at least one request callback is registered. Lets fire sites skip
    /// building a <see cref="RequestInfo"/> when nobody listens.
    /// </summary>
    public bool HasRequestCallbacks()
    {
        lock (_lock)
        {
            return _onRequest.Count != 0;
        }
    }

    /// <summary>True when at least one response callback is registered.</summary>
    public bool HasResponseCallbacks()
    {
        lock (_lock)
        {
            return _onResponse.Count != 0;
        }
    }

    /// <summary>Fire every registered request callback.</summary>
    public void FireRequest(RequestInfo info)
    {
        (ulong Id, RequestCallback Callback)[] snapshot;
        lock (_lock)
        {
            snapshot = [.. _onRequest];
        }

        foreach (var (_, callback) in snapshot)
        {
            callback(info);
        }
    }

    /// <summary>Fire every registered response callback.</summary>
    public void FireResponse(RequestInfo info, Response response)
    {
        (ulong Id, ResponseCallback Callback)[] snapshot;
        lock (_lock)
        {
            snapshot = [.. _onResponse];
        }

        foreach (var (_, callback) in snapshot)
        {
            callback(info, response);
        }
    }
}
