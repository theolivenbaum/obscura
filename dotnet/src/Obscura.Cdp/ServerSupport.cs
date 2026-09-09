using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Obscura.Js.Ops;
using Obscura.Net;

namespace Obscura.Cdp;

/// <summary>What the WebSocket reader hands the connection's processor.</summary>
internal abstract record ServerMessage
{
    private ServerMessage()
    {
    }

    internal sealed record Cdp(string Text, ChannelWriter<string> ReplyTx) : ServerMessage;

    internal sealed record NewConnection(ChannelWriter<string> ReplyTx) : ServerMessage;
}

/// <summary>Pure helpers shared by the server's accept, processor and WS paths.</summary>
internal static class ServerSupport
{
    /// <summary>
    /// Whether a raw CDP frame is exactly a <c>Page.navigate</c> call, and so
    /// should take the spawn-and-defer navigation path.
    /// </summary>
    /// <remarks>
    /// Matching on the parsed method rather than a <c>Contains("Page.navigate")</c>
    /// substring avoids catching <c>Page.navigateToHistoryEntry</c> (goBack /
    /// goForward), which has no <c>url</c> param and belongs to its own handler,
    /// or any other frame that merely embeds the literal text (for example a
    /// <c>Runtime.evaluate</c> expression). See issue #363.
    /// </remarks>
    internal static bool IsNavigateMethod(string text) =>
        CdpRequest.TryParse(text) is { } req &&
        string.Equals(req.Method, "Page.navigate", StringComparison.Ordinal);

    /// <summary>
    /// Parse a CDP header list (<c>[{"name":..,"value":..}, ..]</c>, as used by
    /// <c>Fetch.continueRequest</c> / <c>fulfillRequest</c>) into a map. Null when
    /// the <c>headers</c> field is absent, so the caller can leave the request's
    /// headers untouched rather than clearing them.
    /// </summary>
    internal static Dictionary<string, string>? ParseCdpHeaders(JsonNode? parameters)
    {
        if (parameters.Get("headers").AsJsonArray() is not { } array)
        {
            return null;
        }

        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        foreach (var entry in array)
        {
            if (entry.Get("name").AsString() is { } name &&
                entry.Get("value").AsString() is { } value)
            {
                headers[name] = value;
            }
        }

        return headers;
    }

    /// <summary>
    /// The lenient base64 decoder <c>Fetch.fulfillRequest</c> bodies go through:
    /// every character outside the alphabet, padding included, is dropped before
    /// the remaining sextets are regrouped, and invalid UTF-8 in the result is
    /// replaced rather than rejected.
    /// </summary>
    internal static string DecodeBase64(string input)
    {
        static int Value(char c) => c switch
        {
            >= 'A' and <= 'Z' => c - 'A',
            >= 'a' and <= 'z' => c - 'a' + 26,
            >= '0' and <= '9' => c - '0' + 52,
            '+' => 62,
            '/' => 63,
            _ => -1,
        };

        List<byte> sextets = [];
        foreach (var c in input)
        {
            var value = Value(c);
            if (value >= 0)
            {
                sextets.Add((byte)value);
            }
        }

        var output = new List<byte>(sextets.Count * 3 / 4);
        for (var i = 0; i < sextets.Count; i += 4)
        {
            var length = Math.Min(4, sextets.Count - i);
            var b0 = sextets[i];
            var b1 = length > 1 ? sextets[i + 1] : (byte)0;
            var b2 = length > 2 ? sextets[i + 2] : (byte)0;
            var b3 = length > 3 ? sextets[i + 3] : (byte)0;
            output.Add((byte)((b0 << 2) | (b1 >> 4)));
            if (length > 2)
            {
                output.Add((byte)((b1 << 4) | (b2 >> 2)));
            }

            if (length > 3)
            {
                output.Add((byte)((b2 << 6) | b3));
            }
        }

        return Encoding.UTF8.GetString([.. output]);
    }

    /// <summary>
    /// Answer the trivially-constant commands straight off the WebSocket reader,
    /// without waking the connection's processor.
    /// </summary>
    internal static string? FastPathResponse(string text)
    {
        if (CdpRequest.TryParse(text) is not { } req)
        {
            return null;
        }

        JsonNode? result = req.Method switch
        {
            "Network.enable" or "Network.setCacheDisabled" or "Network.setRequestInterception"
                or "Page.setLifecycleEventsEnabled" or "Page.setInterceptFileChooserDialog"
                or "Runtime.runIfWaitingForDebugger" or "Runtime.discardConsoleEntries"
                or "Performance.enable" or "Log.enable" or "Security.enable"
                or "Emulation.setTouchEmulationEnabled"
                or "CSS.enable" or "Accessibility.enable" or "ServiceWorker.enable"
                or "Inspector.enable" or "Debugger.enable" or "Profiler.enable"
                or "HeapProfiler.enable" or "Overlay.enable" or "Storage.enable"
                or "Target.setAutoAttach" => new JsonObject(),
            "Browser.getVersion" => new JsonObject
            {
                ["protocolVersion"] = "1.3",
                ["product"] = "Chrome/145.0.0.0",
                ["revision"] = "@0000000000000000000000000000000000000000",
                ["userAgent"] =
                    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36",
                ["jsVersion"] = "14.5.0.0",
            },
            "Browser.setDownloadBehavior" or "Browser.getWindowBounds" => new JsonObject(),
            _ => null,
        };

        return result is null
            ? null
            : CdpResponse.Success(req.Id, result, req.SessionId).ToJson();
    }

    internal static (string Domain, string Name, string Path) CookieKey(CookieInfo cookie) =>
        (cookie.Domain, cookie.Name, cookie.Path);

    internal static bool CookieValuesMatch(CookieInfo left, CookieInfo right) =>
        string.Equals(left.Value, right.Value, StringComparison.Ordinal) &&
        left.Secure == right.Secure &&
        left.HttpOnly == right.HttpOnly &&
        string.Equals(left.SameSite, right.SameSite, StringComparison.Ordinal) &&
        left.Expires == right.Expires;

    /// <summary>
    /// Apply only one connection's cookie changes to the persistence template.
    /// </summary>
    /// <remarks>
    /// Unchanged cookies cannot overwrite another connection's updates, while
    /// explicit deletes and replacements still persist.
    /// </remarks>
    internal static void MergeCookieDelta(
        CookieJar destination,
        IReadOnlyList<CookieInfo> initial,
        IReadOnlyList<CookieInfo> current)
    {
        Dictionary<(string, string, string), CookieInfo> before = [];
        foreach (var cookie in initial)
        {
            before[CookieKey(cookie)] = cookie;
        }

        Dictionary<(string, string, string), CookieInfo> after = [];
        foreach (var cookie in current)
        {
            after[CookieKey(cookie)] = cookie;
        }

        foreach (var (key, cookie) in before)
        {
            if (!after.ContainsKey(key))
            {
                destination.DeleteCookiesFiltered(cookie.Name, cookie.Domain, cookie.Path);
            }
        }

        List<CookieInfo> changed = [];
        foreach (var (key, cookie) in after)
        {
            if (before.TryGetValue(key, out var previous) && CookieValuesMatch(previous, cookie))
            {
                continue;
            }

            changed.Add(cookie);
        }

        destination.SetCookiesFromCdp(changed);
    }

    /// <summary>Seconds since the Unix epoch, as <c>Duration::as_secs_f64</c> reports them.</summary>
    internal static double UnixSeconds() =>
        (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>
    /// Turn one intercepted request into the <c>Network.requestWillBeSent</c> +
    /// <c>Fetch.requestPaused</c> pair the client resolves, and park its resolver.
    /// </summary>
    internal static void EmitInterceptedRequest(
        InterceptedRequest intercepted,
        string frameId,
        string? sessionId,
        ChannelWriter<string> replyTx,
        Dictionary<string, TaskCompletionSource<InterceptResolution>> interceptedPaused)
    {
        CdpLog.Info(
            $"INTERCEPTION: requestPaused for {intercepted.Method} {intercepted.Url} (sending to client)");
        var now = UnixSeconds();
        JsonObject Request() => new()
        {
            ["url"] = intercepted.Url,
            ["method"] = intercepted.Method,
            ["headers"] = HeadersObject(intercepted.Headers),
            ["initialPriority"] = "High",
            ["referrerPolicy"] = "strict-origin-when-cross-origin",
        };

        var requestWillBeSent = new JsonObject
        {
            ["method"] = "Network.requestWillBeSent",
            ["params"] = new JsonObject
            {
                ["requestId"] = intercepted.RequestId,
                ["loaderId"] = "",
                ["documentURL"] = "",
                ["request"] = Request(),
                ["timestamp"] = now,
                ["wallTime"] = now,
                ["initiator"] = new JsonObject { ["type"] = "script" },
                ["type"] = intercepted.ResourceType,
                ["frameId"] = frameId,
            },
            ["sessionId"] = sessionId,
        };
        replyTx.TryWrite(CdpJson.Serialize(requestWillBeSent));

        var requestPaused = new JsonObject
        {
            ["method"] = "Fetch.requestPaused",
            ["params"] = new JsonObject
            {
                ["requestId"] = intercepted.RequestId,
                ["request"] = Request(),
                ["frameId"] = frameId,
                ["resourceType"] = intercepted.ResourceType,
                ["networkId"] = intercepted.RequestId,
                ["responseErrorReason"] = null,
                ["responseStatusCode"] = null,
                ["responseHeaders"] = null,
            },
            ["sessionId"] = sessionId,
        };
        replyTx.TryWrite(CdpJson.Serialize(requestPaused));
        interceptedPaused[intercepted.RequestId] = intercepted.Resolver;
    }

    internal static JsonObject HeadersObject(IReadOnlyDictionary<string, string> headers)
    {
        var obj = new JsonObject();
        foreach (var (name, value) in headers)
        {
            obj[name] = value;
        }

        return obj;
    }

    /// <summary>
    /// Resolve a paused request from a <c>Fetch.*</c> frame. True when the frame
    /// named a request this connection had parked, in which case the command's
    /// response has already been sent and dispatch must not run for it.
    /// </summary>
    internal static bool HandleFetchResolution(
        string text,
        CdpContext ctx,
        ChannelWriter<string> replyTx,
        Dictionary<string, TaskCompletionSource<InterceptResolution>> interceptedPaused)
    {
        _ = ctx;
        if (CdpRequest.TryParse(text) is not { } req)
        {
            return false;
        }

        var method = req.Method;
        var requestId = req.Params.Get("requestId").AsStringOr(string.Empty);
        CdpLog.Info(
            $"INTERCEPTION resolution: {method} for {requestId}, paused_count={interceptedPaused.Count}");

        if (!interceptedPaused.Remove(requestId, out var resolver))
        {
            return false;
        }

        CdpLog.Info($"INTERCEPTION resolved: {requestId}");
        InterceptResolution resolution;
        switch (method)
        {
            case "Fetch.continueRequest":
                // Honor the client's overrides (Playwright route.continue,
                // Puppeteer request.continue). The fetch op applies each and
                // re-validates a rewritten URL through the SSRF gate. Leaving
                // these null silently sent the request unmodified (issue #365).
                resolution = new InterceptResolution.Continue(
                    req.Params.Get("url").AsString(),
                    req.Params.Get("method").AsString(),
                    ParseCdpHeaders(req.Params),
                    req.Params.Get("postData").AsString());
                break;

            case "Fetch.fulfillRequest":
            {
                var status = (int)(req.Params.Get("responseCode").AsU64() ?? 200);
                var body = DecodeBase64(req.Params.Get("body").AsStringOr(string.Empty));
                Dictionary<string, string> headers = new(StringComparer.Ordinal);
                if (req.Params.Get("responseHeaders").AsJsonArray() is { } array)
                {
                    foreach (var header in array)
                    {
                        if (header.Get("name").AsString() is { } name &&
                            header.Get("value").AsString() is { } value)
                        {
                            headers[name] = value;
                        }
                    }
                }

                resolution = new InterceptResolution.Fulfill(status, headers, body);
                break;
            }

            case "Fetch.failRequest":
                resolution = new InterceptResolution.Fail(
                    req.Params.Get("errorReason").AsStringOr("Failed"));
                break;

            default:
                // Put the resolver back: this frame is not a resolution, so the
                // request is still parked and its real answer is still coming.
                interceptedPaused[requestId] = resolver;
                return false;
        }

        resolver.TrySetResult(resolution);
        replyTx.TryWrite(
            CdpResponse.Success(req.Id, new JsonObject(), req.SessionId).ToJson());
        return true;
    }

    internal static string Utf8Preview(string text, int maxBytes) =>
        CdpUtil.TruncateOnCharBoundary(text, maxBytes);

    internal static string FormatPort(int port) => port.ToString(CultureInfo.InvariantCulture);
}
