using System.Globalization;
using System.Text.Json.Nodes;

namespace Obscura.Cdp.Domains;

/// <summary>How a client resolved a paused request.</summary>
public abstract record FetchResolution
{
    private FetchResolution()
    {
    }

    public sealed record Continue(
        string? Url,
        string? Method,
        IReadOnlyDictionary<string, string>? Headers,
        string? PostData) : FetchResolution;

    public sealed record Fulfill(
        int Status,
        IReadOnlyList<KeyValuePair<string, string>> Headers,
        string Body) : FetchResolution;

    public sealed record Fail(string Reason) : FetchResolution;
}

/// <summary>One request paused by <c>Fetch.requestPaused</c>, with its resolver.</summary>
/// <remarks>
/// Carried for parity with the Rust type. The live interception path parks its resolvers in the
/// server's own map and answers <c>Fetch.continueRequest</c> / <c>fulfillRequest</c> /
/// <c>failRequest</c> before dispatch ever reaches this domain, so
/// <see cref="FetchInterceptState.Paused"/> is empty in practice on both engines; the handler
/// branches below still drain it so an embedder that fills it in gets the documented behaviour.
/// </remarks>
public sealed class PausedRequest
{
    public required string RequestId { get; init; }

    public required string Url { get; init; }

    public required string Method { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public required string ResourceType { get; init; }

    /// <summary>Rust's <c>oneshot::Sender&lt;FetchResolution&gt;</c>.</summary>
    public TaskCompletionSource<FetchResolution> Resolver { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Per-connection Fetch interception state, owned by the CDP context.</summary>
public sealed class FetchInterceptState
{
    private ulong _requestCounter;

    public bool Enabled { get; set; }

    public List<string> Patterns { get; } = [];

    public Dictionary<string, PausedRequest> Paused { get; } = new(StringComparer.Ordinal);

    public string NextRequestId()
    {
        _requestCounter++;
        return "interception-" + _requestCounter.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>CDP <c>Fetch</c> domain: request interception and streamed response bodies.</summary>
/// <remarks>
/// A <c>Continue</c> that rewrites the URL is forwarded verbatim to the fetch op, which re-runs the
/// same SSRF / private-network validation it applies to the original request and to redirects. This
/// handler must never resolve a rewrite through a path that skips that gate.
/// </remarks>
public static class Fetch
{
    public static async Task<DomainResult> HandleAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await Task.CompletedTask.ConfigureAwait(false);
        switch (method)
        {
            case "enable":
            {
                List<string> patterns = [];
                if (parameters.Get("patterns").AsJsonArray() is { } entries)
                {
                    foreach (JsonNode? entry in entries)
                    {
                        if (entry.Get("urlPattern").AsString() is { } pattern)
                        {
                            patterns.Add(pattern);
                        }
                    }
                }
                else
                {
                    patterns.Add("*");
                }

                ctx.FetchIntercept.Enabled = true;
                ctx.FetchIntercept.Patterns.Clear();
                ctx.FetchIntercept.Patterns.AddRange(patterns);
                var sink = ctx.InterceptSink;
                if (ctx.GetSessionPageMut(sessionId) is { } page)
                {
                    page.InterceptBlockPatterns.Clear();
                    page.InterceptBlockPatterns.AddRange(patterns);
                    if (sink is not null)
                    {
                        page.SetInterceptSink(sink);
                    }

                    page.EnableIntercept(true);
                }

                return DomainResult.Empty();
            }

            case "disable":
            {
                ctx.FetchIntercept.Enabled = false;
                ctx.FetchIntercept.Patterns.Clear();
                if (ctx.GetSessionPageMut(sessionId) is { } page)
                {
                    page.InterceptBlockPatterns.Clear();
                    page.EnableIntercept(false);
                }

                List<PausedRequest> paused = [.. ctx.FetchIntercept.Paused.Values];
                ctx.FetchIntercept.Paused.Clear();
                foreach (PausedRequest request in paused)
                {
                    request.Resolver.TrySetResult(
                        new FetchResolution.Continue(null, null, null, null));
                }

                return DomainResult.Empty();
            }

            case "continueRequest":
            {
                if (parameters.Get("requestId").AsString() is not { } requestId)
                {
                    return DomainResult.Err("requestId required");
                }

                if (ctx.FetchIntercept.Paused.Remove(requestId, out PausedRequest? paused))
                {
                    // The URL rewrite is handed on untouched: the fetch op re-validates it against
                    // the same SSRF gate as the original request and as redirects.
                    paused.Resolver.TrySetResult(new FetchResolution.Continue(
                        parameters.Get("url").AsString(),
                        parameters.Get("method").AsString(),
                        null,
                        parameters.Get("postData").AsString()));
                }

                return DomainResult.Empty();
            }

            case "fulfillRequest":
            {
                if (parameters.Get("requestId").AsString() is not { } requestId)
                {
                    return DomainResult.Err("requestId required");
                }

                int status = (int)(parameters.Get("responseCode").AsU64()
                    ?? 200);
                var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                if (parameters.Get("responseHeaders").AsJsonArray() is { } entries)
                {
                    foreach (JsonNode? entry in entries)
                    {
                        string? name = entry.Get("name").AsString();
                        string? value = entry.Get("value").AsString();
                        if (name is not null && value is not null)
                        {
                            headers[name] = value;
                        }
                    }
                }

                string body = parameters.Get("body").AsString() ?? string.Empty;

                if (ctx.FetchIntercept.Paused.Remove(requestId, out PausedRequest? paused))
                {
                    paused.Resolver.TrySetResult(new FetchResolution.Fulfill(
                        status,
                        [.. headers],
                        body));
                }

                return DomainResult.Empty();
            }

            case "failRequest":
            {
                if (parameters.Get("requestId").AsString() is not { } requestId)
                {
                    return DomainResult.Err("requestId required");
                }

                string reason = parameters.Get("errorReason").AsString()
                    ?? "Failed";

                if (ctx.FetchIntercept.Paused.Remove(requestId, out PausedRequest? paused))
                {
                    paused.Resolver.TrySetResult(new FetchResolution.Fail(reason));
                }

                return DomainResult.Empty();
            }

            case "getResponseBody":
                return DomainResult.Ok(new JsonObject
                {
                    ["body"] = string.Empty,
                    ["base64Encoded"] = false,
                });

            case "takeResponseBodyAsStream":
            {
                // Hand the client a streaming handle for a large response body so it can pull it in
                // chunks via IO.read and free it with IO.close, instead of receiving one giant
                // base64 blob (issue #360). The body is moved out of the page cache into the
                // stream, so it is held once and released on close. Requires the body to have been
                // cached (raise OBSCURA_NETWORK_BODY_BUFFER_BYTES for large downloads).
                if (parameters.Get("requestId").AsString() is not { } requestId)
                {
                    return DomainResult.Err("Fetch.takeResponseBodyAsStream requires requestId");
                }

                if (ctx.GetSessionPageMut(sessionId) is not { } sessionPage)
                {
                    return DomainResult.Err("No page");
                }

                byte[]? bytes = sessionPage.TakeResponseBodyRaw(requestId);
                if (bytes is null)
                {
                    foreach (var page in ctx.Pages)
                    {
                        bytes = page.TakeResponseBodyRaw(requestId);
                        if (bytes is not null)
                        {
                            break;
                        }
                    }
                }

                if (bytes is null)
                {
                    return DomainResult.Err(
                        $"Fetch.takeResponseBodyAsStream: no cached body for {requestId}");
                }

                if (!ctx.IoStreams.TryInsert(bytes, out string? handle, out string? error))
                {
                    return DomainResult.Err($"Fetch.takeResponseBodyAsStream: {error}");
                }

                return DomainResult.Ok(new JsonObject { ["stream"] = handle });
            }

            default:
                return DomainResult.Err($"Unknown Fetch method: {method}");
        }
    }
}
