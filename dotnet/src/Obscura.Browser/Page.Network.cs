using System.Globalization;
using Obscura.Js.Ops;
using Obscura.Net;

namespace Obscura.Browser;

public sealed partial class Page
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal void RecordNetworkEvent(
        string url,
        string method,
        string resourceType,
        int status,
        IReadOnlyDictionary<string, string> responseHeaders,
        int bodySize) =>
        RecordNetworkEventInner(url, method, resourceType, status, responseHeaders, bodySize);

    internal void RecordNetworkEventWithBody(
        string url,
        string method,
        string resourceType,
        int status,
        IReadOnlyDictionary<string, string> responseHeaders,
        byte[] body,
        bool base64Encoded)
    {
        string requestId = RecordNetworkEventInner(
            url,
            method,
            resourceType,
            status,
            responseHeaders,
            body.Length);
        StoreResponseBody(requestId, body, base64Encoded);
    }

    private string RecordNetworkEventInner(
        string url,
        string method,
        string resourceType,
        int status,
        IReadOnlyDictionary<string, string> responseHeaders,
        int bodySize)
    {
        _networkEventCounter += 1;
        string requestId =
            $"{Id}.{_networkEventCounter.ToString(CultureInfo.InvariantCulture)}";
        double timestamp = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds;
        NetworkEvents.Add(new NetworkEvent
        {
            RequestId = requestId,
            Url = url,
            Method = method,
            ResourceType = resourceType,
            Status = status,
            Headers = NoHeaders,
            ResponseHeaders = new Dictionary<string, string>(responseHeaders, StringComparer.OrdinalIgnoreCase),
            BodySize = bodySize,
            Timestamp = timestamp,
        });
        return requestId;
    }

    private void StoreResponseBody(string requestId, byte[] body, bool base64Encoded)
    {
        int maxEntries = PageHelpers.ResponseBodyEntryLimit();
        int maxBytes = PageHelpers.ResponseBodyByteLimit();
        if (maxEntries == 0 || maxBytes == 0 || body.Length > maxBytes)
        {
            return;
        }
        string encoded = base64Encoded
            ? Convert.ToBase64String(body)
            : System.Text.Encoding.UTF8.GetString(body);
        _responseBodies[requestId] = new StoredResponseBody(encoded, base64Encoded);
        _responseBodyOrder.AddLast(requestId);
        while (_responseBodyOrder.Count > maxEntries)
        {
            string oldest = _responseBodyOrder.First!.Value;
            _responseBodyOrder.RemoveFirst();
            _responseBodies.Remove(oldest);
        }
    }

    public StoredResponseBody? GetResponseBody(string requestId)
    {
        if (_responseBodies.TryGetValue(requestId, out StoredResponseBody? stored))
        {
            return stored;
        }
        StoredNetworkResponseBody? body = Js?.GetNetworkResponseBody(requestId);
        return body is null ? null : new StoredResponseBody(body.Body, body.Base64Encoded);
    }

    /// <summary>
    /// Take a stored response body as raw bytes for CDP streaming
    /// (<c>Fetch.takeResponseBodyAsStream</c>).
    /// </summary>
    /// <remarks>
    /// Removes it from the in-memory cache and transfers ownership to the caller, so
    /// a large body is held once and freed when the stream closes rather than
    /// lingering in this long-running process. Binary bodies are stored base64
    /// (byte-exact); text bodies return their UTF-8 bytes. Returns null when the body
    /// was never cached (it exceeded <c>OBSCURA_NETWORK_BODY_BUFFER_BYTES</c>) or the
    /// id is unknown.
    /// </remarks>
    public byte[]? TakeResponseBodyRaw(string requestId)
    {
        StoredResponseBody? stored;
        if (_responseBodies.Remove(requestId, out StoredResponseBody? removed))
        {
            LinkedListNode<string>? node = _responseBodyOrder.First;
            while (node is not null)
            {
                LinkedListNode<string>? next = node.Next;
                if (string.Equals(node.Value, requestId, StringComparison.Ordinal))
                {
                    _responseBodyOrder.Remove(node);
                }
                node = next;
            }
            stored = removed;
        }
        else
        {
            StoredNetworkResponseBody? body = Js?.GetNetworkResponseBody(requestId);
            stored = body is null ? null : new StoredResponseBody(body.Body, body.Base64Encoded);
        }

        if (stored is null)
        {
            return null;
        }
        if (!stored.Base64Encoded)
        {
            return System.Text.Encoding.UTF8.GetBytes(stored.Body);
        }
        try
        {
            return Convert.FromBase64String(stored.Body);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Make the body stored under <paramref name="fromId"/> also retrievable under
    /// <paramref name="toId"/>.
    /// </summary>
    /// <remarks>
    /// The main navigation resource is stored under its internal request id, but the
    /// CDP layer reports it with the navigation's loaderId as the requestId
    /// (Chrome's <c>requestId === loaderId</c> convention). Without this alias,
    /// <c>Network.getResponseBody(loaderId)</c> misses and a client navigating
    /// straight to an image cannot read the main-response body.
    /// </remarks>
    public void AliasResponseBody(string fromId, string toId)
    {
        if (string.Equals(fromId, toId, StringComparison.Ordinal)
            || _responseBodies.ContainsKey(toId))
        {
            return;
        }
        if (_responseBodies.TryGetValue(fromId, out StoredResponseBody? body))
        {
            _responseBodies[toId] = body;
            _responseBodyOrder.AddLast(toId);
        }
    }

    public void ClearResponseBodies()
    {
        _responseBodies.Clear();
        _responseBodyOrder.Clear();
        Js?.ClearNetworkResponseBodies();
    }

    /// <summary>
    /// Absolute URLs the page pulled in via <c>fetch()</c>/XHR. Empty when the page
    /// has no live JS runtime.
    /// </summary>
    public IReadOnlyList<string> FetchedUrls() => Js?.FetchedUrls ?? [];

    /// <summary>
    /// Move network events recorded for script-initiated requests from the JS runtime
    /// into this page's <see cref="NetworkEvents"/>.
    /// </summary>
    /// <remarks>
    /// The CDP layer then emits <c>Network.requestWillBeSent</c> /
    /// <c>responseReceived</c> for them. Idempotent: the runtime's queue is drained,
    /// so repeated calls do not duplicate events. The <c>fetch-{N}</c> request id is
    /// preserved so <c>Network.getResponseBody</c> resolves.
    /// </remarks>
    public void SyncJsNetworkEvents()
    {
        if (Js is not { } js)
        {
            return;
        }
        foreach (JsNetworkEvent ev in js.TakeJsNetworkEvents())
        {
            NetworkEvents.Add(new NetworkEvent
            {
                RequestId = ev.RequestId,
                Url = ev.Url,
                Method = ev.Method,
                ResourceType = "Fetch",
                Status = ev.Status,
                Headers = NoHeaders,
                ResponseHeaders = ev.ResponseHeaders,
                BodySize = ev.BodySize,
                Timestamp = ev.Timestamp,
            });
        }
    }

    public void SetBlockedUrls(IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        BlockedUrlPatterns = [.. patterns];
        Js?.SetBlockedUrls(patterns);
    }

    public void SetPreloadScripts(IReadOnlyList<string> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        _preloadScripts = [.. scripts];
    }

    /// <summary>
    /// Append a script that runs in the page before any of the page's own
    /// <c>&lt;script&gt;</c> tags, matching
    /// <c>Page.addScriptToEvaluateOnNewDocument</c>. Takes effect on the next
    /// navigation.
    /// </summary>
    public void AddPreloadScript(string script) => _preloadScripts.Add(script);

    /// <summary>
    /// Enable CDP-Fetch-style interception of JS-initiated <c>fetch()</c>/XHR.
    /// </summary>
    /// <remarks>
    /// Returns a reader yielding every such request; resolve each through its
    /// resolver with <c>InterceptResolution.{Continue,Fulfill,Fail}</c> to pass, mock
    /// or block it.
    /// </remarks>
    public System.Threading.Channels.ChannelReader<InterceptedRequest> EnableInterception()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<InterceptedRequest>();
        SetInterceptSink(new ChannelInterceptSink(channel.Writer));
        EnableIntercept(true);
        return channel.Reader;
    }

    public void SetInterceptSink(IInterceptSink sink)
    {
        _interceptTx = sink;
        Js?.SetInterceptSink(sink);
    }

    public void EnableIntercept(bool enabled)
    {
        InterceptEnabled = enabled;
        Js?.SetInterceptEnabled(enabled);
    }

    /// <summary>
    /// Register a passive callback fired for every JS <c>fetch()</c>/XHR (and
    /// navigation) request this page makes, once the method/headers/body are known
    /// and before it is sent.
    /// </summary>
    /// <remarks>
    /// Non-blocking; use <see cref="EnableInterception"/> to mutate or block. Scoped
    /// to this page: it never sees sibling pages' requests and dies with the page.
    /// </remarks>
    public ulong OnRequest(RequestCallback callback) => _callbacks.AddRequest(callback);

    /// <summary>
    /// Register a passive callback fired with every JS <c>fetch()</c>/XHR (and
    /// navigation) response this page receives, including its body.
    /// </summary>
    public ulong OnResponse(ResponseCallback callback) => _callbacks.AddResponse(callback);

    /// <summary>Detach a request observer registered with <see cref="OnRequest"/>.</summary>
    public bool OffRequest(ulong id) => _callbacks.RemoveRequest(id);

    /// <summary>Detach a response observer registered with <see cref="OnResponse"/>.</summary>
    public bool OffResponse(ulong id) => _callbacks.RemoveResponse(id);

    private sealed class ChannelInterceptSink(
        System.Threading.Channels.ChannelWriter<InterceptedRequest> writer) : IInterceptSink
    {
        public bool TrySend(InterceptedRequest request) => writer.TryWrite(request);
    }
}
