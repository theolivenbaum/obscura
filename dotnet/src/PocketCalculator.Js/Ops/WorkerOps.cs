using Microsoft.ClearScript;
using PocketCalculator.Js.Url;
using PocketCalculator.Net;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// Sources of <c>blob:</c> URLs minted for JavaScript Blobs, so the module loader can
/// import them. Port addition.
/// </summary>
/// <remarks>
/// bootstrap.js keeps every object URL's text in its own closure, where the module loader
/// (which runs in C#, possibly off the runtime thread) cannot read it. A Blob whose type
/// is a JavaScript MIME type is also registered here; any other Blob is refused as a module
/// by Chromium's strict MIME check, so it is not copied. Bounded like the internal-load
/// store: past the cap the oldest entries go.
/// </remarks>
public sealed class BlobScriptStore
{
    internal const long MaxChars = 64L * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private long _chars;

    /// <summary>Records <paramref name="source"/> as the body of <paramref name="url"/>.</summary>
    public void Register(string url, string source)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("blob:", StringComparison.Ordinal) || source is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_sources.Remove(url, out var old))
            {
                _chars -= old.Length;
            }

            _sources[url] = source;
            _order.Enqueue(url);
            _chars += source.Length;
            while (_chars > MaxChars && _order.Count > 0)
            {
                if (_sources.Remove(_order.Dequeue(), out var evicted))
                {
                    _chars -= evicted.Length;
                }
            }
        }
    }

    /// <summary>Forgets <paramref name="url"/> (<c>URL.revokeObjectURL</c>).</summary>
    public void Revoke(string url)
    {
        lock (_gate)
        {
            if (_sources.Remove(url, out var old))
            {
                _chars -= old.Length;
            }
        }
    }

    /// <summary>The source registered for <paramref name="url"/>, or null.</summary>
    public string? Get(string url)
    {
        lock (_gate)
        {
            return _sources.GetValueOrDefault(url);
        }
    }
}

/// <summary>
/// The worker ops: <c>importScripts</c>' synchronous script load, and the declaration scan
/// the shim uses to give each worker script its global bindings.
/// </summary>
public static class WorkerOps
{
    /// <summary>
    /// <c>op_worker_import_script</c>: fetches <paramref name="url"/> as a classic
    /// worker-imported script and, when that succeeds, hands its source to
    /// <paramref name="runner"/> as <c>runner(scope, source, url, muted)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port addition (Rust workers have no <c>importScripts</c>). <c>importScripts</c> is
    /// synchronous, so the engine thread waits for the fetch here, as it does for a static
    /// module graph (<c>PocketCalculatorModuleLoader.LoadDocument</c>). The fetch goes
    /// through the page's client, so it carries the page's cookies (same-origin
    /// credentials, as the HTML fetch for a worker-imported script), passes its SSRF and
    /// mixed-content gates and fires its callbacks. Like a module graph load, it is not
    /// offered to CDP <c>Fetch</c> interception: a paused request is resolved by the CDP
    /// loop, which is waiting on this thread.
    /// </para>
    /// <para>
    /// The wait stays blocking on purpose. <c>importScripts</c> blocks its worker by
    /// specification, and a worker here is emulated in the page's realm (bootstrap.js
    /// <c>Worker</c>), so its thread is the page's and the page isolate is held for the fetch;
    /// a worker of its own (an isolate and a thread) is what would let the page run meanwhile,
    /// and is not ported. What made the wait dangerous is gone: op settlements no longer park
    /// pool threads on the isolate lock (<see cref="OpCompletionContext"/>), so the fetch's
    /// continuations get a thread. The wait runs with no synchronization context, under the
    /// fetch timeout and the script's deadline (<see cref="PocketCalculator.Dom.WorkCancellation"/>).
    /// </para>
    /// <para>
    /// The source never reaches page script as a value: the host passes it straight to the
    /// shim's runner, which evaluates it (SECURITY.md C3). <c>muted</c> is true for a
    /// script from another origin than the worker's, whose exceptions
    /// <c>importScripts</c> reports as a <c>NetworkError</c>.
    /// </para>
    /// </remarks>
    /// <returns>The empty string when the runner ran, <c>"network"</c> on a network error:
    /// a refused or failed fetch, a non-2xx status, or a type other than JavaScript (the
    /// strict MIME check Chromium applies to every worker-imported script).</returns>
    public static string ImportScript(
        PocketCalculatorState state,
        PocketCalculatorState document,
        string url,
        string workerUrl,
        object? scope,
        object? runner)
    {
        if (scope is not ScriptObject scopeObject || runner is not ScriptObject runnerFunction)
        {
            return "network";
        }

        var source = Fetch(state, document, url, workerUrl, out var finalUrl);
        if (source is null)
        {
            return "network";
        }

        var workerOrigin = UrlRecord.Parse(workerUrl)?.AsciiOrigin ?? "null";
        var muted = string.Equals(workerOrigin, "null", StringComparison.Ordinal)
            || !string.Equals(UrlRecord.Parse(url)?.AsciiOrigin, workerOrigin, StringComparison.Ordinal)
            || !string.Equals(UrlRecord.Parse(finalUrl)?.AsciiOrigin, workerOrigin, StringComparison.Ordinal);
        runnerFunction.InvokeAsFunction(scopeObject, source, finalUrl, muted);
        return string.Empty;
    }

    /// <summary>The script's text, or null on a network error.</summary>
    private static string? Fetch(
        PocketCalculatorState state,
        PocketCalculatorState document,
        string url,
        string workerUrl,
        out string finalUrl)
    {
        finalUrl = url;
        var parsed = UrlRecord.Parse(url);
        if (parsed is null)
        {
            return null;
        }

        var scheme = parsed.Scheme;
        var documentIsFile = UrlRecord.Parse(document.Url)?.Scheme == "file";
        if (scheme is not ("http" or "https") && !(scheme == "file" && documentIsFile))
        {
            return null;
        }

        foreach (var pattern in state.BlockedUrls)
        {
            if (string.Equals(pattern, "*", StringComparison.Ordinal)
                || url.Contains(pattern, StringComparison.Ordinal)
                || FetchOps.GlobMatch(pattern, url))
            {
                return null;
            }
        }

        if (state.HttpClient is not { } client)
        {
            return null;
        }

        StateHelpers.PushCapped(state.FetchedUrls, url, StateHelpers.MaxFetchedUrls);

        var source = !document.OpaqueOrigin && document.SiteUrl is { } site ? site : document.Url;
        var initiator = Uri.TryCreate(source, UriKind.Absolute, out var from) ? from : new Uri("about:blank");
        var request = ResourceRequest.Subresource(ResourceType.Script, initiator);
        // A worker's fetches are made by the worker, so the worker's URL is the Referer
        // (a blob: or data: worker's falls back to its document's).
        request.Referrer = UrlRecord.Parse(workerUrl) is { Scheme: "http" or "https" } worker
            && Uri.TryCreate(worker.Href, UriKind.Absolute, out var workerUri)
                ? workerUri
                : Uri.TryCreate(document.Url, UriKind.Absolute, out var own) ? own : null;
        request.Credentials = RequestCredentials.SameOrigin;
        request.ReferrerPolicy = StateHelpers.DocumentReferrerPolicy(document);
        request.SecureAncestor = document.SecureAncestorUrl is { } secure && Uri.TryCreate(secure, UriKind.Absolute, out var ancestor)
            ? ancestor
            : null;
        StateHelpers.WithFrameScope(request, document);

        Response response;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(PocketCalculator.Dom.WorkCancellation.Current);
            deadline.CancelAfter(FetchOps.FetchTimeout());
            var target = new Uri(parsed.Href);
            response = OpCompletionContext.WithoutContext(() =>
                (state.StealthClient is { IsAvailable: true } stealth
                    ? stealth.FetchResourceWithCallbacksAsync(target, request, state.Callbacks, deadline.Token)
                    : client.FetchResourceWithCallbacksAsync(target, request, state.Callbacks, deadline.Token))
                .GetAwaiter()
                .GetResult());
        }
        catch (OperationCanceledException) when (PocketCalculator.Dom.WorkCancellation.Current.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }

        if (response.Status is < 200 or > 299
            || !DataUrl.IsJavaScriptMimeType(DataUrl.Essence(response.ContentType())))
        {
            return null;
        }

        finalUrl = UrlRecord.Parse(response.Url.AbsoluteUri)?.Href ?? response.Url.AbsoluteUri;
        return ContentEncoding.DecodeNonHtml(response.Body, response.ContentType());
    }
}
