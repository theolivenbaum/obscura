using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Url;
using PocketCalculator.Net;
using PocketCalculator.Js.Runtime;

namespace PocketCalculator.Browser;

public sealed partial class Page
{
    /// <summary>
    /// Move fetched frame documents into Page ownership without doing work that can
    /// be cancelled. Realms are still created one at a time, in sibling order.
    /// </summary>
    internal bool QueuePendingFrames()
    {
        // Keep exactly one bounded batch in Page ownership. Additional frame
        // documents stay in the runtime's existing queue until this batch finishes,
        // so repeated cancellation cannot multiply the retention ceiling.
        if (_pendingFrameWork.Count != 0)
        {
            return false;
        }
        if (Js is not { } js)
        {
            return false;
        }
        IReadOnlyList<PendingFrame> pending = js.TakePendingFrames();
        if (pending.Count == 0)
        {
            return false;
        }
        foreach (PendingFrame frame in pending)
        {
            _pendingFrameWork.AddLast(new PendingFrameWork.Unattached(frame));
        }
        return true;
    }

    /// <summary>
    /// Create, fetch and run the front frame. The record stays Page-owned at each
    /// await, so cancellation resumes the current URL and leaves every untouched
    /// sibling in order.
    /// </summary>
    internal async Task<bool> RunNextPendingFrameAsync(CancellationToken cancellationToken)
    {
        if (_pendingFrameWork.First?.Value is PendingFrameWork.Unattached unattached)
        {
            _pendingFrameWork.RemoveFirst();
            PendingFrame frame = unattached.Frame;
            // A realm is a live V8 context plus a DOM tree, and the page realm holds
            // its window and document, so nothing here can be collected while the
            // document lives. Refuse past the cap rather than let a page decide how
            // much memory to take.
            int cap = PageHelpers.MaxLiveFrames();
            if (Frames.Count >= cap)
            {
                ForgetFrameReferences(frame.FrameId, frame.ParentFrameId);
                return true;
            }

            FrameRealm? realm = Js is { } parent
                ? FrameRealm.Create(
                    parent, frame.FrameId, frame.ParentFrameId, frame.Url, frame.Html, frame.OpaqueOrigin)
                : null;
            if (realm is null)
            {
                ForgetFrameReferences(frame.FrameId, frame.ParentFrameId);
                return true;
            }

            // Discover author scripts before preload code can mutate the frame DOM,
            // preserving the existing preparation order.
            IReadOnlyList<string> urls = realm.ExternalScriptUrls();

            try
            {
                realm.SetViewport(frame.ViewportWidth, frame.ViewportHeight);
            }
            catch (JsRuntimeException)
            {
                // A frame that refuses a viewport still runs.
            }
            // New-document scripts must be installed before the published child
            // context can be observed, including while its external scripts load.
            foreach (string source in _preloadScripts)
            {
                try
                {
                    realm.ExecuteScript(source);
                }
                catch (JsRuntimeException)
                {
                    // A failed preload must not stop the frame.
                }
            }

            Frames.Add(realm);
            _pendingFrameWork.AddFirst(new PendingFrameWork.Attached
            {
                Id = frame.FrameId,
                ParentId = frame.ParentFrameId,
                FrameUrl = frame.Url,
                Urls = urls,
            });
        }

        while (_pendingFrameWork.First?.Value is PendingFrameWork.Attached front
            && front.NextUrl < front.Urls.Count)
        {
            uint frameId = front.Id;
            string url = front.Urls[front.NextUrl];

            string? source = null;
            if (!ShouldBlockUrl(url) && PageUrl.TryParse(url) is { } parsed)
            {
                double startedAt = PerformanceOps.UnixMilliseconds();
                try
                {
                    Response response = await DoFetchAsync(parsed, cancellationToken).ConfigureAwait(false);
                    // A frame document is a resource of the page that embeds it, which
                    // is the timeline this records onto; the frame's own realm reports
                    // it as its navigation entry.
                    RecordResourceTiming(
                        response.Url.AbsoluteUri,
                        "iframe",
                        response,
                        startedAt,
                        PerformanceOps.UnixMilliseconds());
                    source = System.Text.Encoding.UTF8.GetString(response.Body);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    source = null;
                }
            }

            if (_pendingFrameWork.First?.Value is not PendingFrameWork.Attached current)
            {
                return false;
            }
            if (current.Id != frameId
                || current.NextUrl >= current.Urls.Count
                || !string.Equals(current.Urls[current.NextUrl], url, StringComparison.Ordinal))
            {
                continue;
            }
            if (source is not null)
            {
                current.Sources[url] = source;
            }
            current.NextUrl += 1;
        }

        if (_pendingFrameWork.First?.Value is not PendingFrameWork.Attached finished)
        {
            return false;
        }
        Dictionary<string, string> sources = new(finished.Sources, StringComparer.Ordinal);
        finished.Sources.Clear();
        int index = Frames.FindIndex(realm => realm.FrameId == finished.Id);
        if (index < 0)
        {
            _pendingFrameWork.RemoveFirst();
            return true;
        }
        if (Js is not null)
        {
            Frames[index].RunDocumentScripts(url => sources.TryGetValue(url, out string? code) ? code : null);
            try
            {
                Frames[index].DispatchLoadEvents();
            }
            catch (JsRuntimeException)
            {
                // A frame whose load handlers throw still counts as attached.
            }
        }
        _pendingFrameWork.RemoveFirst();
        return true;
    }

    /// <summary>
    /// Hand each queued <c>postMessage</c> to the realm it was addressed to.
    /// </summary>
    /// <remarks>
    /// Reports whether anything was delivered, because a message usually causes a
    /// reply: a widget posts its result, the page answers, and the exchange only
    /// finishes if the caller settles and drains again.
    /// </remarks>
    internal bool DeliverFrameMessages()
    {
        if (Js is not { } js)
        {
            return false;
        }
        IReadOnlyList<PendingFrameMessage> pending = js.TakePendingFrameMessages();
        if (pending.Count == 0)
        {
            return false;
        }

        foreach (PendingFrameMessage message in pending)
        {
            string escapedData = JsonSerializer.Serialize(message.DataJson);
            string escapedOrigin = JsonSerializer.Serialize(message.Origin);
            string escapedTargetOrigin = JsonSerializer.Serialize(message.TargetOrigin);
            if (message.TargetFrameId == 0)
            {
                if (Js is not { } page)
                {
                    continue;
                }
                // Port addition: targetOrigin is checked against the page's host-known
                // origin before delivery, not only by the receiving shim (SECURITY.md H4).
                if (!CoreOps.TargetOriginAllows(
                        message.TargetOrigin, StateHelpers.DocumentOrigin(page.State), message.Origin))
                {
                    continue;
                }
                // A host helper, not upstream's page-visible __obscura_deliverMessage global.
                string script =
                    $"__obscura_host.deliverMessage({escapedData}, {escapedOrigin}, "
                    + $"{message.SourceFrameId.ToString(CultureInfo.InvariantCulture)}, {escapedTargetOrigin});";
                TryExecuteHost(page, "<frame-message>", script);
                continue;
            }

            int index = Frames.FindIndex(frame => frame.FrameId == message.TargetFrameId);
            if (index < 0 || Js is null)
            {
                // The frame was torn down between the send and the drain.
                continue;
            }
            try
            {
                Frames[index].DeliverMessage(
                    message.DataJson,
                    message.Origin,
                    message.SourceFrameId,
                    message.TargetOrigin);
            }
            catch (JsRuntimeException)
            {
                // A frame that throws on delivery does not stop the others.
            }
        }
        return true;
    }

    /// <summary>
    /// Remove the JS references owned by the iframe's parent realm and by the page's
    /// same-origin frame table.
    /// </summary>
    /// <remarks>
    /// This also covers a frame rejected before a <c>FrameRealm</c> exists, so the
    /// normal teardown path cannot be skipped.
    /// </remarks>
    private void ForgetFrameReferences(uint frameId, uint parentFrameId)
    {
        string id = frameId.ToString(CultureInfo.InvariantCulture);
        // The element's frame binding is closure state now (SECURITY.md C2), so the unbinding
        // goes through the host helper rather than through `_frameId` expandos.
        string script = $"__obscura_host.forgetFrame({id});";
        ExecuteFrameOwnerScript(parentFrameId, script);
        if (parentFrameId != 0 && Js is { } js)
        {
            TryExecuteHost(js, "<frame-detach>", $"__obscura_host.forgetFrameObjects({id});");
        }
    }

    /// <summary>
    /// Execute cleanup in the realm that owns the iframe element. Nested iframe
    /// registries live in their parent frame, not in the page realm.
    /// </summary>
    private void ExecuteFrameOwnerScript(uint parentFrameId, string script)
    {
        if (parentFrameId == 0)
        {
            if (Js is { } js)
            {
                TryExecuteHost(js, "<frame-detach>", script);
            }
            return;
        }

        int index = Frames.FindIndex(frame => frame.FrameId == parentFrameId);
        if (index < 0 || Js is null)
        {
            return;
        }
        try
        {
            Frames[index].ExecuteHostScript(script);
        }
        catch (JsRuntimeException)
        {
            // Releasing nested frame references is best effort.
        }
    }

    /// <summary>
    /// Discard the realms whose iframe element has left the document.
    /// </summary>
    /// <remarks>
    /// A browser discards a child browsing context when its element is removed.
    /// Nothing here can be collected on its own, because the page realm holds each
    /// frame's window and document so that <c>contentWindow</c> can be the frame's
    /// real object, so a page that replaces an iframe repeatedly would otherwise
    /// accumulate contexts and DOM trees for the life of the document. Descendants
    /// go in the same pass so none can run after their owner has left the document.
    /// </remarks>
    internal void ReleaseDetachedFrames()
    {
        if (Frames.Count == 0 && _pendingFrameWork.Count == 0)
        {
            return;
        }
        // Each realm reports its own frames whose element is still connected.
        // Liveness is asked of the element rather than of a document query, because
        // an iframe inside a shadow root is absent from
        // `document.querySelectorAll('iframe')` - the shape a challenge widget uses -
        // and treating it as detached tears down a live frame.
        // A host helper; upstream calls the page-visible __obscura_liveFrameIds global,
        // which page script could replace to have its frames torn down or kept.
        const string LiveFrameIds = "__obscura_host.liveFrameIds()";

        if (Js is not { } js)
        {
            return;
        }
        List<uint> live = [];
        // A query that did not run says nothing about which frames are gone, and an
        // empty answer here reads as "all of them". Holding them is bounded by the
        // live-frame cap; discarding a live frame is not recoverable, so leave the
        // tree alone.
        if (!TryCollectFrameIds(() => js.EvaluateHost(LiveFrameIds), live))
        {
            return;
        }
        // A frame's own children are in that frame's document, not the page's.
        for (int index = 0; index < Frames.Count; index++)
        {
            FrameRealm frame = Frames[index];
            if (!TryCollectFrameIds(() => frame.EvaluateHost(LiveFrameIds), live))
            {
                return;
            }
        }

        HashSet<uint> discardedIds = [];
        foreach (FrameRealm frame in Frames)
        {
            if (!live.Contains(frame.FrameId))
            {
                discardedIds.Add(frame.FrameId);
            }
        }
        foreach (PendingFrameWork pending in _pendingFrameWork)
        {
            if (!live.Contains(pending.FrameId))
            {
                discardedIds.Add(pending.FrameId);
            }
        }
        while (true)
        {
            int before = discardedIds.Count;
            List<uint> descendants = [];
            foreach (FrameRealm frame in Frames)
            {
                if (discardedIds.Contains(frame.ParentFrameId))
                {
                    descendants.Add(frame.FrameId);
                }
            }
            foreach (PendingFrameWork pending in _pendingFrameWork)
            {
                if (discardedIds.Contains(pending.ParentFrameId))
                {
                    descendants.Add(pending.FrameId);
                }
            }
            foreach (uint id in descendants)
            {
                discardedIds.Add(id);
            }
            if (discardedIds.Count == before)
            {
                break;
            }
        }
        if (discardedIds.Count == 0)
        {
            return;
        }

        List<(uint FrameId, uint ParentFrameId)> discarded = [];
        foreach (FrameRealm frame in Frames)
        {
            if (discardedIds.Contains(frame.FrameId))
            {
                discarded.Add((frame.FrameId, frame.ParentFrameId));
            }
        }
        foreach (PendingFrameWork pending in _pendingFrameWork)
        {
            if (pending is PendingFrameWork.Unattached && discardedIds.Contains(pending.FrameId))
            {
                discarded.Add((pending.FrameId, pending.ParentFrameId));
            }
        }

        // Clean owner realms before dropping any parent. This matters for a nested
        // child whose iframe registry lives in a parent that is also being removed.
        foreach ((uint frameId, uint parentFrameId) in discarded)
        {
            ForgetFrameReferences(frameId, parentFrameId);
        }
        for (int index = Frames.Count - 1; index >= 0; index--)
        {
            if (discardedIds.Contains(Frames[index].FrameId))
            {
                FrameRealm realm = Frames[index];
                Frames.RemoveAt(index);
                realm.Dispose();
            }
        }
        LinkedListNode<PendingFrameWork>? node = _pendingFrameWork.First;
        while (node is not null)
        {
            LinkedListNode<PendingFrameWork>? next = node.Next;
            if (discardedIds.Contains(node.Value.FrameId))
            {
                _pendingFrameWork.Remove(node);
            }
            node = next;
        }
    }

    private static bool TryCollectFrameIds(Func<JsonNode?> evaluate, List<uint> live)
    {
        JsonNode? value;
        try
        {
            value = evaluate();
        }
        catch (JsRuntimeException)
        {
            return false;
        }
        if (value is JsonArray array)
        {
            foreach (JsonNode? entry in array)
            {
                if (entry is not null && entry.GetValueKind() == JsonValueKind.Number)
                {
                    live.Add((uint)entry.GetValue<double>());
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Move the frame tree forward by one step: give any fetched frame document a
    /// realm, then hand on any message waiting for a realm.
    /// </summary>
    /// <remarks>
    /// Reports whether anything happened, so a caller can pump again. A new frame
    /// runs scripts that can post, and a message usually causes a reply, so neither
    /// queue is finished until both are quiet.
    /// </remarks>
    internal async Task<bool> AdvanceFramesAsync(CancellationToken cancellationToken = default)
    {
        bool queuedNew = QueuePendingFrames();
        ReleaseDetachedFrames();
        if (_pendingFrameWork.Count == 0 && QueuePendingFrames())
        {
            queuedNew = true;
            ReleaseDetachedFrames();
        }
        int queued = _pendingFrameWork.Count;
        bool scriptsRan = false;
        for (int i = 0; i < queued; i++)
        {
            scriptsRan |= await RunNextPendingFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        bool delivered = DeliverFrameMessages();
        ReleaseDetachedFrames();
        return queuedNew || scriptsRan || delivered;
    }

    /// <summary>URLs of the page's live child frames, in creation order.</summary>
    public IReadOnlyList<string> FrameUrls() => [.. Frames.Select(frame => frame.Url)];

    /// <summary>Evaluate an expression inside one of the page's child frames.</summary>
    public JsonNode? EvaluateInFrame(int index, string expression)
    {
        if (index < 0 || index >= Frames.Count)
        {
            throw new InvalidOperationException("no such frame");
        }
        if (Js is null)
        {
            throw new InvalidOperationException("no runtime");
        }
        return Frames[index].Evaluate(expression);
    }
}
