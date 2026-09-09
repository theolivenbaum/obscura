using System.Globalization;
using System.Text.Json.Nodes;

using Obscura.Browser;
using Obscura.Js.Runtime;
using Obscura.Render;

using BrowserPage = Obscura.Browser.Page;

namespace Obscura.Cdp.Domains;

/// <summary>
/// The CDP <c>Page</c> domain: navigation, the frame tree, lifecycle events,
/// navigation history, layout metrics, screenshots and screencast.
/// </summary>
public static partial class Page
{
    /// <summary>Seconds since the Unix epoch, matching Rust's <c>as_secs_f64</c>.</summary>
    private static double Timestamp() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>Rust's <c>Url::parse</c>: absolute only, no base.</summary>
    private static Uri? TryParseUrl(string? value) =>
        value is not null && Uri.TryCreate(value, UriKind.Absolute, out Uri? url) ? url : null;

    /// <summary>
    /// <c>Origin::ascii_serialization</c> as the Rust <c>url</c> crate computes it: a
    /// tuple origin for the special schemes and the literal <c>"null"</c> for every
    /// opaque one (<c>data:</c>, <c>file:</c>, <c>about:</c>).
    /// </summary>
    private static string AsciiOrigin(Uri url)
    {
        string scheme = url.Scheme;
        int? defaultPort = scheme switch
        {
            "http" or "ws" => 80,
            "https" or "wss" => 443,
            "ftp" => 21,
            _ => null,
        };
        if (defaultPort is null || url.Host.Length == 0)
        {
            return "null";
        }

        return url.Port == defaultPort
            ? $"{scheme}://{url.Host}"
            : $"{scheme}://{url.Host}:{url.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// CDP frame ids are strings, while a realm is identified by number, so a child's
    /// protocol id is derived from its page's. It stays stable for the life of the frame,
    /// which is what lets a client match an attach event to the frame it later sees in
    /// the tree.
    /// </summary>
    internal static string ChildFrameId(string pageFrameId, uint frameId) =>
        $"{pageFrameId}-frame-{frameId.ToString(CultureInfo.InvariantCulture)}";

    private static bool IsLocalhost(Uri url)
    {
        string host = url.Host;
        if (host.Length == 0)
        {
            return false;
        }

        return url.HostNameType switch
        {
            UriHostNameType.Dns =>
                host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase),
            UriHostNameType.IPv4 or UriHostNameType.IPv6 =>
                System.Net.IPAddress.TryParse(host.Trim('[', ']'), out System.Net.IPAddress? address)
                && System.Net.IPAddress.IsLoopback(address),
            _ => false,
        };
    }

    private static string SecureContextType(Uri url) => url.Scheme switch
    {
        "https" or "wss" or "file" or "about" => "Secure",
        "http" or "ws" when IsLocalhost(url) => "SecureLocalhost",
        _ => "InsecureScheme",
    };

    /// <summary>
    /// Build the required <c>Page.Frame</c> fields in one place.
    /// </summary>
    /// <remarks>
    /// Generated CDP clients deserialize this object before their frame managers see it,
    /// so every path that returns or emits a frame must use the same protocol-complete
    /// shape.
    /// </remarks>
    internal static JsonObject FrameValue(
        string id,
        string? parentId,
        string loaderId,
        string url,
        string mimeType)
    {
        Uri? parsed = TryParseUrl(url);
        string securityOrigin = parsed is null ? "null" : AsciiOrigin(parsed);
        string secureContext = parsed is null ? "InsecureScheme" : SecureContextType(parsed);
        var frame = new JsonObject
        {
            ["id"] = id,
            ["loaderId"] = loaderId,
            ["url"] = url,
            ["domainAndRegistry"] = "",
            ["securityOrigin"] = securityOrigin,
            ["mimeType"] = mimeType,
            ["adFrameStatus"] = new JsonObject { ["adFrameType"] = "none" },
            ["secureContextType"] = secureContext,
            ["crossOriginIsolatedContextType"] = "NotIsolated",
            ["gatedAPIFeatures"] = new JsonArray(),
        };
        if (parentId is not null)
        {
            frame["parentId"] = parentId;
        }

        return frame;
    }

    private static JsonObject ChildFrameValue(string pageFrameId, FrameRealm frame)
    {
        string id = ChildFrameId(pageFrameId, frame.FrameId);
        string parentId = frame.ParentFrameId == 0
            ? pageFrameId
            : ChildFrameId(pageFrameId, frame.ParentFrameId);
        return FrameValue(id, parentId, $"{id}-loader", frame.Url, "text/html");
    }

    /// <summary>
    /// The page's child frames, flattened with each parent ahead of its children so an
    /// attach event never names a parent the client has not seen yet.
    /// </summary>
    public static List<JsonObject> ChildFrameValues(BrowserPage page)
    {
        var output = new List<JsonObject>();
        Walk(page, 0, output);
        return output;

        static void Walk(BrowserPage page, uint parentFrameId, List<JsonObject> output)
        {
            foreach (FrameRealm frame in page.Frames)
            {
                if (frame.ParentFrameId != parentFrameId)
                {
                    continue;
                }

                output.Add(ChildFrameValue(page.FrameId, frame));
                Walk(page, frame.FrameId, output);
            }
        }
    }

    /// <summary>
    /// The page's live frame hierarchy. <c>childFrames</c> used to be hardcoded empty, so
    /// a client was told the page had no frames however many it had built.
    /// </summary>
    private static JsonObject FrameTree(BrowserPage page, string loaderId) => new()
    {
        ["frame"] = FrameValue(page.FrameId, null, loaderId, page.UrlString(), "text/html"),
        ["childFrames"] = Children(page, 0),
    };

    private static JsonArray Children(BrowserPage page, uint parentFrameId)
    {
        var array = new JsonArray();
        foreach (FrameRealm frame in page.Frames)
        {
            if (frame.ParentFrameId != parentFrameId)
            {
                continue;
            }

            array.Add(new JsonObject
            {
                ["frame"] = ChildFrameValue(page.FrameId, frame),
                ["childFrames"] = Children(page, frame.FrameId),
            });
        }

        return array;
    }

    /// <summary>
    /// Emit the post-navigation event stream into <c>ctx.PendingEvents</c>.
    /// </summary>
    /// <remarks>
    /// Shared by both the in-process navigate path and the spawned path in the server, so
    /// the goto-returns-Response and per-isolated-world fixes do not have to be
    /// duplicated.
    /// </remarks>
    public static void EmitNavigationEvents(
        CdpContext ctx,
        string? sessionId,
        string frameId,
        string loaderId,
        string pageUrl,
        string pageId,
        IReadOnlyList<NetworkEvent> networkEvents,
        WaitUntil waitUntil,
        bool reachedNetworkIdle)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(networkEvents);

        ctx.CurrentLoaderIds[pageId] = loaderId;
        ctx.NavEventsEmitted.Add(pageId);
        string? es = sessionId;
        double ts = Timestamp();

        // Real Chrome uses the navigation's loaderId as the main document's request id,
        // and Puppeteer/Playwright identify the navigation response via
        // `requestId === loaderId && type === "Document"` (issue #189).
        var navRequestIds = new List<string>(networkEvents.Count);
        bool navSeen = false;
        foreach (NetworkEvent networkEvent in networkEvents)
        {
            if (!navSeen
                && string.Equals(networkEvent.ResourceType, "Document", StringComparison.Ordinal)
                && string.Equals(networkEvent.Url, pageUrl, StringComparison.Ordinal))
            {
                navSeen = true;
                navRequestIds.Add(loaderId);
            }
            else
            {
                navRequestIds.Add(networkEvent.RequestId);
            }
        }

        int? navIndex = null;
        for (int i = 0; i < networkEvents.Count; i++)
        {
            if (string.Equals(networkEvents[i].ResourceType, "Document", StringComparison.Ordinal)
                && string.Equals(networkEvents[i].Url, pageUrl, StringComparison.Ordinal))
            {
                navIndex = i;
                break;
            }
        }

        // The main resource's body is stored under its internal request id, but the client
        // sees it as `loaderId` (the requestId we report above). Alias it so
        // Network.getResponseBody(loaderId) resolves, which is the only way a client
        // navigating straight to an image/PDF/other resource can read the main body
        // (issue #340). Also read the real Content-Type so frameNavigated reports the
        // actual mime instead of a hardcoded text/html.
        string navMime = "text/html";
        if (navIndex is { } index)
        {
            string internalId = networkEvents[index].RequestId;
            if (networkEvents[index].ResponseHeaders.TryGetValue("content-type", out string? contentType))
            {
                // Strip any `; charset=...` parameter; frameNavigated wants the essence.
                int semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
                navMime = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
            }

            if (!string.Equals(internalId, loaderId, StringComparison.Ordinal))
            {
                ctx.GetPage(pageId)?.AliasResponseBody(internalId, loaderId);
            }
        }

        // Playwright needs `Network.requestWillBeSent` for the main document to arrive
        // BEFORE `Page.frameNavigated` (issue #190).
        if (navIndex is { } mainIndex)
        {
            NetworkEvent networkEvent = networkEvents[mainIndex];
            ctx.PendingEvents.Add(new CdpEvent
            {
                Method = "Network.requestWillBeSent",
                Params = RequestWillBeSent(
                    navRequestIds[mainIndex], loaderId, pageUrl, networkEvent, frameId, "other"),
                SessionId = es,
            });
        }

        IReadOnlyList<ExecutionContextRecord> contexts =
            ctx.CommitDefaultContext(pageId, frameId, pageUrl);
        IReadOnlyList<string> runtimeSessions = ctx.RuntimeSessionsForPage(pageId);
        var phase1 = new List<CdpEvent>
        {
            new()
            {
                Method = "Page.lifecycleEvent",
                Params = LifecycleEvent(frameId, loaderId, "init", ts),
                SessionId = es,
            },
        };
        foreach (string runtimeSession in runtimeSessions)
        {
            phase1.Add(CdpEvent.WithSession(
                "Runtime.executionContextsCleared", new JsonObject(), runtimeSession));
        }

        phase1.Add(new CdpEvent
        {
            Method = "Page.frameNavigated",
            Params = new JsonObject
            {
                ["frame"] = FrameValue(frameId, null, loaderId, pageUrl, navMime),
                ["type"] = "Navigation",
            },
            SessionId = es,
        });
        foreach (string runtimeSession in runtimeSessions)
        {
            foreach (ExecutionContextRecord context in contexts)
            {
                phase1.Add(Runtime.ExecutionContextCreatedEvent(context, runtimeSession));
            }
        }

        phase1.Add(new CdpEvent
        {
            Method = "Page.lifecycleEvent",
            Params = LifecycleEvent(frameId, loaderId, "commit", ts),
            SessionId = es,
        });
        ctx.PendingEvents.AddRange(phase1);

        if (ctx.FetchIntercept.Enabled)
        {
            for (int i = 0; i < networkEvents.Count; i++)
            {
                NetworkEvent networkEvent = networkEvents[i];
                string rid = navRequestIds[i];
                ctx.PendingEvents.Add(new CdpEvent
                {
                    Method = "Fetch.requestPaused",
                    Params = new JsonObject
                    {
                        ["requestId"] = rid,
                        ["request"] = new JsonObject
                        {
                            ["url"] = networkEvent.Url,
                            ["method"] = networkEvent.Method,
                            ["headers"] = Headers(networkEvent.Headers),
                        },
                        ["frameId"] = frameId,
                        ["resourceType"] = networkEvent.ResourceType,
                        ["networkId"] = rid,
                    },
                    SessionId = es,
                });
            }
        }

        for (int i = 0; i < networkEvents.Count; i++)
        {
            NetworkEvent networkEvent = networkEvents[i];
            string rid = navRequestIds[i];
            if (navIndex != i)
            {
                ctx.PendingEvents.Add(new CdpEvent
                {
                    Method = "Network.requestWillBeSent",
                    Params = RequestWillBeSent(rid, loaderId, pageUrl, networkEvent, frameId, "other"),
                    SessionId = es,
                });
            }

            ctx.PendingEvents.Add(new CdpEvent
            {
                Method = "Network.responseReceived",
                Params = ResponseReceived(rid, loaderId, networkEvent, frameId),
                SessionId = es,
            });
            ctx.PendingEvents.Add(new CdpEvent
            {
                Method = "Network.loadingFinished",
                Params = LoadingFinished(rid, networkEvent),
                SessionId = es,
            });
        }

        var phase3 = new List<CdpEvent>
        {
            new()
            {
                Method = "Page.lifecycleEvent",
                Params = LifecycleEvent(frameId, loaderId, "DOMContentLoaded", ts),
                SessionId = es,
            },
            new()
            {
                Method = "Page.domContentEventFired",
                Params = new JsonObject { ["timestamp"] = JsonValue.Create(ts) },
                SessionId = es,
            },
            new()
            {
                Method = "Page.lifecycleEvent",
                Params = LifecycleEvent(frameId, loaderId, "load", ts),
                SessionId = es,
            },
            new()
            {
                Method = "Page.loadEventFired",
                Params = new JsonObject { ["timestamp"] = JsonValue.Create(ts) },
                SessionId = es,
            },
        };
        if (reachedNetworkIdle || waitUntil is WaitUntil.Load or WaitUntil.DomContentLoaded)
        {
            double idleTs = Timestamp();
            phase3.Add(new CdpEvent
            {
                Method = "Page.lifecycleEvent",
                Params = LifecycleEvent(frameId, loaderId, "networkIdle", idleTs),
                SessionId = es,
            });
        }

        phase3.Add(new CdpEvent
        {
            Method = "Page.frameStoppedLoading",
            Params = new JsonObject { ["frameId"] = frameId },
            SessionId = es,
        });
        ctx.PendingEvents.AddRange(phase3);

        // Target.targetInfoChanged: strict CDP clients (browser-use, and
        // Puppeteer/Playwright `page.url()` tracking) cache the TargetInfo from
        // attachedToTarget and only refresh it on this event. Without it they keep
        // reporting the pre-navigation url/title (about:blank) and never see the loaded
        // page. Emit it browser-level (no sessionId) with the new url/title.
        BrowserPage? navigated = ctx.GetPage(pageId);
        string ticTitle = navigated?.Title ?? string.Empty;
        string ticContext = navigated?.Context.Id ?? string.Empty;
        ctx.PendingEvents.Add(CdpEvent.New(
            "Target.targetInfoChanged",
            new JsonObject
            {
                ["targetInfo"] = new JsonObject
                {
                    ["targetId"] = pageId,
                    ["type"] = "page",
                    ["title"] = ticTitle,
                    ["url"] = pageUrl,
                    ["attached"] = true,
                    ["canAccessOpener"] = false,
                    ["browserContextId"] = ticContext,
                },
            }));
    }

    /// <summary>
    /// Emit completed script-initiated requests after the document lifecycle has already
    /// finished. These requests belong to the current document loader and must not replay
    /// frame navigation or load lifecycle events.
    /// </summary>
    public static void EmitRuntimeNetworkEvents(
        CdpContext ctx,
        string? sessionId,
        string frameId,
        string pageUrl,
        string pageId,
        IReadOnlyList<NetworkEvent> networkEvents)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(networkEvents);
        if (networkEvents.Count == 0)
        {
            return;
        }

        string loaderId = ctx.CurrentLoaderIds.TryGetValue(pageId, out string? current)
            ? current
            : $"loader-blank-{pageId}";
        foreach (NetworkEvent networkEvent in networkEvents)
        {
            string requestId = networkEvent.RequestId;
            ctx.PendingEvents.Add(new CdpEvent
            {
                Method = "Network.requestWillBeSent",
                Params = RequestWillBeSent(
                    requestId, loaderId, pageUrl, networkEvent, frameId, "script"),
                SessionId = sessionId,
            });
            ctx.PendingEvents.Add(new CdpEvent
            {
                Method = "Network.responseReceived",
                Params = ResponseReceived(requestId, loaderId, networkEvent, frameId),
                SessionId = sessionId,
            });
            ctx.PendingEvents.Add(new CdpEvent
            {
                Method = "Network.loadingFinished",
                Params = LoadingFinished(requestId, networkEvent),
                SessionId = sessionId,
            });
        }
    }

    private static JsonObject LifecycleEvent(string frameId, string loaderId, string name, double ts) =>
        new()
        {
            ["frameId"] = frameId,
            ["loaderId"] = loaderId,
            ["name"] = name,
            ["timestamp"] = JsonValue.Create(ts),
        };

    private static JsonObject Headers(IReadOnlyDictionary<string, string> headers)
    {
        var value = new JsonObject();
        foreach ((string name, string content) in headers)
        {
            value[name] = content;
        }

        return value;
    }

    private static JsonObject RequestWillBeSent(
        string requestId,
        string loaderId,
        string documentUrl,
        NetworkEvent networkEvent,
        string frameId,
        string initiatorType) =>
        new()
        {
            ["requestId"] = requestId,
            ["loaderId"] = loaderId,
            ["documentURL"] = documentUrl,
            ["request"] = new JsonObject
            {
                ["url"] = networkEvent.Url,
                ["method"] = networkEvent.Method,
                ["headers"] = Headers(networkEvent.Headers),
            },
            ["timestamp"] = JsonValue.Create(networkEvent.Timestamp),
            ["wallTime"] = JsonValue.Create(networkEvent.Timestamp),
            ["initiator"] = new JsonObject { ["type"] = initiatorType },
            ["type"] = networkEvent.ResourceType,
            ["frameId"] = frameId,
        };

    private static JsonObject ResponseReceived(
        string requestId,
        string loaderId,
        NetworkEvent networkEvent,
        string frameId) =>
        new()
        {
            ["requestId"] = requestId,
            ["loaderId"] = loaderId,
            ["timestamp"] = JsonValue.Create(networkEvent.Timestamp),
            ["type"] = networkEvent.ResourceType,
            ["response"] = new JsonObject
            {
                ["url"] = networkEvent.Url,
                ["status"] = networkEvent.Status,
                ["statusText"] = "",
                ["headers"] = Headers(networkEvent.ResponseHeaders),
                ["mimeType"] = networkEvent.ResponseHeaders.TryGetValue("content-type", out string? mime)
                    ? mime
                    : string.Empty,
            },
            ["frameId"] = frameId,
        };

    private static JsonObject LoadingFinished(string requestId, NetworkEvent networkEvent) => new()
    {
        ["requestId"] = requestId,
        ["timestamp"] = JsonValue.Create(networkEvent.Timestamp),
        ["encodedDataLength"] = networkEvent.BodySize,
    };

    /// <summary>
    /// Parse the <c>waitUntil</c> argument that Puppeteer/Playwright pass on
    /// <c>Page.navigate</c>.
    /// </summary>
    /// <remarks>
    /// Puppeteer and Playwright drive navigation via <c>Page.navigate</c> without a
    /// server-side waitUntil - they wait for <c>Page.lifecycleEvent</c> on the client
    /// side. Defaulting the server to <c>Load</c> means running every
    /// parser/deferred/async script on JS-heavy pages before emitting <c>load</c>, which
    /// on sites like github.com pushes nav past 25s and clients time out at 15s. Real
    /// Chrome streams <c>DOMContentLoaded</c> as soon as the parser is done; we batch our
    /// event emission at the end of navigation, so the closest we can get is to default
    /// to <c>DomContentLoaded</c> and skip the full-load wait. CLI callers that pass
    /// <c>--wait-until load</c> (or <c>networkidle*</c>) are unaffected.
    /// </remarks>
    public static WaitUntil ParseWaitUntil(JsonNode? parameters)
    {
        JsonNode? node = parameters.Get("waitUntil");
        if (node.AsString() is { } text)
        {
            return WaitUntilExtensions.ParseWaitUntil(text);
        }

        if (JsonExt.AsJsonArray(node) is { } array)
        {
            WaitUntil? best = null;
            int bestRank = int.MinValue;
            foreach (JsonNode? item in array)
            {
                if (item.AsString() is not { } value)
                {
                    continue;
                }

                WaitUntil parsed = WaitUntilExtensions.ParseWaitUntil(value);
                int rank = parsed switch
                {
                    WaitUntil.DomContentLoaded => 0,
                    WaitUntil.Load => 1,
                    WaitUntil.NetworkIdle2 => 2,
                    WaitUntil.NetworkIdle0 => 3,
                    _ => 0,
                };
                // `max_by_key` keeps the LAST maximum, so use >= here.
                if (best is null || rank >= bestRank)
                {
                    best = parsed;
                    bestRank = rank;
                }
            }

            if (best is { } chosen)
            {
                return chosen;
            }
        }

        return WaitUntil.DomContentLoaded;
    }

    private static async Task<JsonNode?> DoNavigateAsync(
        string url,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        WaitUntil waitUntil = ParseWaitUntil(parameters);

        // Block CDP-initiated file:// navigation by default. Anyone who can reach the CDP
        // port (default localhost, but Docker images bind 0.0.0.0) could otherwise read
        // any file the obscura process can read. Opt in via
        // `obscura serve --allow-file-access` when local-HTML testing is the intended
        // workflow.
        bool allowFileAccess = ctx.GetSessionPage(sessionId) is { } gate
            ? gate.Context.AllowFileAccess
            : ctx.DefaultContext.AllowFileAccess;
        if (CdpUtil.UrlIsFileScheme(url) && !allowFileAccess)
        {
            throw new DomainError(
                "Page.navigate to file:// is disabled. Restart with `obscura serve --allow-file-access` to enable.");
        }

        List<string> preloadScripts = [.. ctx.PreloadScripts.Select(entry => entry.Source)];

        BrowserPage page = ctx.GetSessionPageMut(sessionId)
            ?? throw new DomainError("No page for session");
        string frameId = page.FrameId;
        string loaderId = $"loader-{Guid.NewGuid()}";

        // Preloads (addBinding shims, addScriptToEvaluateOnNewDocument sources) must run
        // BEFORE the page's own scripts (CDP contract). Hand them to the page so the
        // navigation can inject them at the right point.
        page.SetPreloadScripts(preloadScripts);

        string navMethod = parameters.Get("__method").AsString() ?? "GET";
        string navBody = parameters.Get("__body").AsString() ?? string.Empty;
        try
        {
            if (string.Equals(navMethod, "POST", StringComparison.Ordinal) && navBody.Length != 0)
            {
                await page.NavigateWithWaitPostAsync(url, waitUntil, navMethod, navBody)
                    .ConfigureAwait(false);
            }
            else
            {
                await page.NavigateWithWaitAsync(url, waitUntil).ConfigureAwait(false);
            }
        }
        catch (PageException exception)
        {
            throw new DomainError(exception.Message);
        }

        bool reachedNetworkIdle = page.Lifecycle.IsNetworkIdle();
        // Fold in script-initiated requests (fetch/XHR/dynamic resource) so they emit as
        // Network events alongside static subresources (#406).
        page.SyncJsNetworkEvents();
        List<NetworkEvent> networkEvents = [.. page.NetworkEvents];
        page.NetworkEvents.Clear();
        string pageUrl = page.UrlString();
        string pageId = page.Id;

        EmitNavigationEvents(
            ctx,
            sessionId,
            frameId,
            loaderId,
            pageUrl,
            pageId,
            networkEvents,
            waitUntil,
            reachedNetworkIdle);

        return new JsonObject
        {
            ["frameId"] = frameId,
            ["loaderId"] = loaderId,
        };
    }

    public static async Task<DomainResult> HandleAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        try
        {
            return await HandleCoreAsync(method, parameters, ctx, sessionId).ConfigureAwait(false);
        }
        catch (DomainError error)
        {
            return DomainResult.Err(error.Message);
        }
    }

    private static async Task<DomainResult> HandleCoreAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        switch (method)
        {
            case "enable":
            {
                // Chrome loads a new target's initial about:blank right after
                // createTarget, so by the time a client attaches and calls Page.enable the
                // page has already produced its load events. Obscura creates pages
                // silently, which starves clients that wait for the initial load:
                // chromiumoxide's new_page blocks until the main frame's "load" lifecycle
                // event arrives (#833). Emit those events once, the first time a session
                // enables the page domain on a page that has not emitted a navigation.
                // This is not a document change, so there is no execution-context churn:
                // the existing context announced by Runtime.enable stays valid.
                (string PageId, string FrameId, string Url)? initial = null;
                if (ctx.GetSessionPage(sessionId) is { } page
                    && !ctx.NavEventsEmitted.Contains(page.Id))
                {
                    initial = (page.Id, page.FrameId, page.UrlString());
                }

                if (initial is { } start)
                {
                    ctx.NavEventsEmitted.Add(start.PageId);
                    double ts = Timestamp();
                    string loaderId = $"loader-blank-{start.PageId}";
                    string? es = sessionId;
                    // Build the frame through the schema-complete helper: generated CDP
                    // clients (chromiumoxide) reject a frameNavigated payload missing
                    // secureContextType/crossOriginIsolatedContextType and drop the whole
                    // connection (#833).
                    JsonObject frame = FrameValue(start.FrameId, null, loaderId, start.Url, "text/html");
                    ctx.PendingEvents.AddRange(
                    [
                        new CdpEvent
                        {
                            Method = "Page.frameNavigated",
                            Params = new JsonObject { ["frame"] = frame, ["type"] = "Navigation" },
                            SessionId = es,
                        },
                        new CdpEvent
                        {
                            Method = "Page.lifecycleEvent",
                            Params = LifecycleEvent(start.FrameId, loaderId, "commit", ts),
                            SessionId = es,
                        },
                        new CdpEvent
                        {
                            Method = "Page.lifecycleEvent",
                            Params = LifecycleEvent(start.FrameId, loaderId, "DOMContentLoaded", ts),
                            SessionId = es,
                        },
                        new CdpEvent
                        {
                            Method = "Page.domContentEventFired",
                            Params = new JsonObject { ["timestamp"] = JsonValue.Create(ts) },
                            SessionId = es,
                        },
                        new CdpEvent
                        {
                            Method = "Page.lifecycleEvent",
                            Params = LifecycleEvent(start.FrameId, loaderId, "load", ts),
                            SessionId = es,
                        },
                        new CdpEvent
                        {
                            Method = "Page.loadEventFired",
                            Params = new JsonObject { ["timestamp"] = JsonValue.Create(ts) },
                            SessionId = es,
                        },
                        new CdpEvent
                        {
                            Method = "Page.lifecycleEvent",
                            Params = LifecycleEvent(start.FrameId, loaderId, "networkIdle", ts),
                            SessionId = es,
                        },
                        new CdpEvent
                        {
                            Method = "Page.frameStoppedLoading",
                            Params = new JsonObject
                            {
                                ["frameId"] = start.FrameId,
                                ["timestamp"] = JsonValue.Create(ts),
                            },
                            SessionId = es,
                        },
                    ]);
                }

                return DomainResult.Empty();
            }

            case "navigate":
            {
                string url = parameters.Get("url").AsString()
                    ?? throw new DomainError("url required");
                return DomainResult.Ok(
                    await DoNavigateAsync(url, parameters, ctx, sessionId).ConfigureAwait(false));
            }

            case "reload":
            {
                string currentUrl = ctx.GetSessionPage(sessionId)?.UrlString() ?? "about:blank";
                var reloadParams = new JsonObject
                {
                    ["waitUntil"] = parameters.Get("waitUntil").Clone()
                        ?? JsonValue.Create("load"),
                };
                return DomainResult.Ok(await DoNavigateAsync(currentUrl, reloadParams, ctx, sessionId)
                    .ConfigureAwait(false));
            }

            case "getFrameTree":
            {
                BrowserPage page = ctx.GetSessionPage(sessionId)
                    ?? throw new DomainError("No page for session");
                string loaderId = ctx.CurrentLoaderIds.TryGetValue(page.Id, out string? current)
                    ? current
                    : $"loader-blank-{page.Id}";
                return DomainResult.Ok(new JsonObject { ["frameTree"] = FrameTree(page, loaderId) });
            }

            case "createIsolatedWorld":
            {
                BrowserPage page = ctx.GetSessionPage(sessionId)
                    ?? throw new DomainError("No page for session");
                string frameIdParam = parameters.Get("frameId").AsString() ?? page.FrameId;
                string origin;
                bool persistAcrossNavigation;
                if (string.Equals(frameIdParam, page.FrameId, StringComparison.Ordinal))
                {
                    origin = page.UrlString();
                    persistAcrossNavigation = true;
                }
                else if (page.Frames.FirstOrDefault(frame =>
                    string.Equals(ChildFrameId(page.FrameId, frame.FrameId), frameIdParam, StringComparison.Ordinal))
                    is { } childFrame)
                {
                    origin = childFrame.Url;
                    persistAcrossNavigation = false;
                }
                else
                {
                    throw new DomainError($"No frame with given id found: {frameIdParam}");
                }

                string worldName = parameters.Get("worldName").AsString() ?? string.Empty;
                string pageId = page.Id;

                if (ctx.EnsureDefaultContext(pageId) is null)
                {
                    throw new DomainError("No page for session");
                }

                (ExecutionContextRecord context, bool created) = ctx.CreateIsolatedContext(
                    pageId, frameIdParam, origin, worldName, persistAcrossNavigation);
                if (created)
                {
                    foreach (string runtimeSession in ctx.RuntimeSessionsForPage(pageId))
                    {
                        ctx.PendingEvents.Add(
                            Runtime.ExecutionContextCreatedEvent(context, runtimeSession));
                    }
                }

                return DomainResult.Ok(new JsonObject { ["executionContextId"] = context.Id });
            }

            case "setLifecycleEventsEnabled":
                return DomainResult.Empty();

            case "addScriptToEvaluateOnNewDocument":
            {
                string source = parameters.Get("source").AsString() ?? string.Empty;
                ctx.PreloadCounter += 1;
                string identifier = ctx.PreloadCounter.ToString(CultureInfo.InvariantCulture);
                if (source.Length != 0)
                {
                    ctx.PreloadScripts.Add((identifier, source));
                }

                return DomainResult.Ok(new JsonObject { ["identifier"] = identifier });
            }

            case "removeScriptToEvaluateOnNewDocument":
            {
                string identifier = parameters.Get("identifier").AsString() ?? string.Empty;
                ctx.PreloadScripts.RemoveAll(entry =>
                    string.Equals(entry.Identifier, identifier, StringComparison.Ordinal));
                return DomainResult.Empty();
            }

            case "setInterceptFileChooserDialog":
                return DomainResult.Empty();

            // Obscura does not download files to disk, so there is no behavior to
            // configure; ack it so clients that set it do not warn (issue #340).
            case "setDownloadBehavior":
                return DomainResult.Empty();

            case "getLayoutMetrics":
            {
                // Playwright calls this before every page.screenshot(). Report the same
                // live CSS viewport that responsive page code and paint use.
                (double width, double height) = ctx.GetSessionPage(sessionId) is { } metricsPage
                    ? (metricsPage.Viewport.Width, (double)metricsPage.Viewport.Height)
                    : (1280.0, 720.0);
                double pageX = 0.0;
                double pageY = 0.0;
                double contentWidth = width;
                double contentHeight = height;
                JsonArray? values = JsonExt.AsJsonArray(ctx.GetSessionPageMut(sessionId)?.Evaluate(
                    "[window.scrollX, window.scrollY, "
                    + "document.documentElement && document.documentElement.scrollWidth, "
                    + "document.documentElement && document.documentElement.scrollHeight]"));
                if (values is not null)
                {
                    pageX = values.Count > 0 ? values[0].AsF64() ?? 0.0 : 0.0;
                    pageY = values.Count > 1 ? values[1].AsF64() ?? 0.0 : 0.0;
                    contentWidth = values.Count > 2 && values[2].AsF64() is { } cw && cw > 0.0
                        ? cw
                        : width;
                    contentHeight = values.Count > 3 && values[3].AsF64() is { } ch && ch > 0.0
                        ? ch
                        : height;
                }

                JsonObject LayoutViewport() => new()
                {
                    ["pageX"] = JsonValue.Create(pageX),
                    ["pageY"] = JsonValue.Create(pageY),
                    ["clientWidth"] = JsonValue.Create(width),
                    ["clientHeight"] = JsonValue.Create(height),
                };
                JsonObject VisualViewport() => new()
                {
                    ["offsetX"] = JsonValue.Create(0.0),
                    ["offsetY"] = JsonValue.Create(0.0),
                    ["pageX"] = JsonValue.Create(pageX),
                    ["pageY"] = JsonValue.Create(pageY),
                    ["clientWidth"] = JsonValue.Create(width),
                    ["clientHeight"] = JsonValue.Create(height),
                    ["scale"] = JsonValue.Create(1.0),
                    ["zoom"] = JsonValue.Create(1.0),
                };
                JsonObject ContentSize() => new()
                {
                    ["x"] = JsonValue.Create(0.0),
                    ["y"] = JsonValue.Create(0.0),
                    ["width"] = JsonValue.Create(contentWidth),
                    ["height"] = JsonValue.Create(contentHeight),
                };

                return DomainResult.Ok(new JsonObject
                {
                    ["layoutViewport"] = LayoutViewport(),
                    ["visualViewport"] = VisualViewport(),
                    ["contentSize"] = ContentSize(),
                    ["cssLayoutViewport"] = LayoutViewport(),
                    ["cssVisualViewport"] = VisualViewport(),
                    ["cssContentSize"] = ContentSize(),
                });
            }

            case "getNavigationHistory":
            {
                BrowserPage page = ctx.GetSessionPage(sessionId)
                    ?? throw new DomainError("No page for session");
                // Synthesize an entry for the current page when history is empty (initial
                // about:blank, never-navigated targets). Puppeteer's goBack reads
                // `currentIndex` and `entries[currentIndex-1]`; an empty entries[] used to
                // make every back/forward fail.
                var entries = new JsonArray();
                if (page.History.Count == 0)
                {
                    entries.Add(new JsonObject
                    {
                        ["id"] = 0,
                        ["url"] = page.UrlString(),
                        ["userTypedURL"] = page.UrlString(),
                        ["title"] = page.Title,
                        ["transitionType"] = "typed",
                    });
                }
                else
                {
                    for (int i = 0; i < page.History.Count; i++)
                    {
                        entries.Add(new JsonObject
                        {
                            ["id"] = (ulong)i,
                            ["url"] = page.History[i],
                            ["userTypedURL"] = page.History[i],
                            ["title"] = i == page.HistoryIndex ? page.Title : string.Empty,
                            ["transitionType"] = "typed",
                        });
                    }
                }

                return DomainResult.Ok(new JsonObject
                {
                    ["currentIndex"] = page.History.Count == 0 ? 0 : page.HistoryIndex,
                    ["entries"] = entries,
                });
            }

            case "navigateToHistoryEntry":
            {
                var entryId = (int)(parameters.Get("entryId").AsU64() ?? 0);
                BrowserPage page = ctx.GetSessionPageMut(sessionId)
                    ?? throw new DomainError("No page for session");
                string? targetUrl = entryId >= 0 && entryId < page.History.Count
                    ? page.History[entryId]
                    : null;
                if (targetUrl is not null)
                {
                    page.SetHistoryIndex(entryId);
                }

                if (targetUrl is { } url)
                {
                    // Stash + restore history so PushHistory doesn't clobber the cursor we
                    // just moved.
                    List<string> stashHistory = [.. page.History];
                    int stashIndex = page.HistoryIndex;
                    BrowserPage navigating = ctx.GetSessionPageMut(sessionId)
                        ?? throw new DomainError("No page for session");
                    try
                    {
                        await navigating.NavigateWithWaitAsync(url, WaitUntil.DomContentLoaded)
                            .ConfigureAwait(false);
                    }
                    catch (PageException exception)
                    {
                        throw new DomainError(exception.Message);
                    }

                    navigating.History.Clear();
                    navigating.History.AddRange(stashHistory);
                    navigating.HistoryIndex = stashIndex;
                    string frameId = navigating.FrameId;
                    string pageId = navigating.Id;
                    List<NetworkEvent> networkEvents = [.. navigating.NetworkEvents];
                    navigating.NetworkEvents.Clear();
                    string pageUrl = navigating.UrlString();
                    bool reachedIdle = navigating.Lifecycle.IsNetworkIdle();

                    string loaderId = $"loader-{Guid.NewGuid()}";
                    EmitNavigationEvents(
                        ctx,
                        sessionId,
                        frameId,
                        loaderId,
                        pageUrl,
                        pageId,
                        networkEvents,
                        WaitUntil.DomContentLoaded,
                        reachedIdle);
                }

                return DomainResult.Empty();
            }

            case "resetNavigationHistory":
            {
                if (ctx.GetSessionPageMut(sessionId) is { } page)
                {
                    page.History.Clear();
                    page.HistoryIndex = 0;
                }

                return DomainResult.Empty();
            }

            case "printToPDF":
                return await PdfDomain.PrintToPdfAsync(parameters, ctx, sessionId).ConfigureAwait(false);

            case "startScreencast":
            {
                string cdpSession = sessionId
                    ?? throw new DomainError(
                        "Page.startScreencast requires an attached target session");
                BrowserPage page = ctx.GetSessionPageMut(sessionId)
                    ?? throw new DomainError("No page for session");
                await PrepareCaptureResourcesIfRequestedAsync(page).ConfigureAwait(false);
                long streamId = ctx.NextScreencastSession();
                ScreencastState state = ParseScreencastState(parameters, streamId);
                ctx.Screencasts[cdpSession] = state;
                int pendingBefore = ctx.PendingEvents.Count;
                ctx.PendingEvents.Add(new CdpEvent
                {
                    Method = "Page.screencastVisibilityChanged",
                    Params = new JsonObject { ["visible"] = true },
                    SessionId = sessionId,
                });
                if (QueueScreencastFrame(ctx, sessionId, force: true) is { } frameError)
                {
                    ctx.PendingEvents.RemoveRange(pendingBefore, ctx.PendingEvents.Count - pendingBefore);
                    ctx.Screencasts.Remove(cdpSession);
                    return DomainResult.Err(frameError);
                }

                return DomainResult.Ok(new JsonObject
                {
                    ["obscuraFrameSource"] = "activity-driven",
                    ["obscuraAutonomousFrames"] = true,
                });
            }

            case "stopScreencast":
            {
                if (sessionId is not null)
                {
                    ctx.Screencasts.Remove(sessionId);
                }

                return DomainResult.Empty();
            }

            case "screencastFrameAck":
            {
                long acknowledged = ScreencastInt32(parameters, "sessionId")
                    ?? throw new DomainError("Invalid parameters: sessionId is required");
                if (sessionId is not null
                    && ctx.Screencasts.TryGetValue(sessionId, out ScreencastState? state))
                {
                    // Ignore delayed acknowledgements from a replaced stream.
                    if (state.SessionId == acknowledged)
                    {
                        state.FramesInFlight = state.FramesInFlight == 0
                            ? (byte)0
                            : (byte)(state.FramesInFlight - 1);
                    }
                }

                return DomainResult.Empty();
            }

            case "captureScreenshot":
                return DomainResult.Ok(
                    await CaptureScreenshotAsync(parameters, ctx, sessionId).ConfigureAwait(false));

            case "captureSnapshot":
                // A DOM/layer-tree snapshot (not a raster image). Distinct from
                // captureScreenshot; keep the clear error so clients fail fast.
                return DomainResult.Err(
                    $"Page.{method} is not supported by Obscura: no layout or paint engine. "
                    + "For visual snapshots, drive a real headless Chromium for the "
                    + "screenshot leg of your pipeline and use Obscura for the scraping leg.");

            default:
                return DomainResult.Err($"Unknown Page method: {method}");
        }
    }

    private static async Task<JsonNode?> CaptureScreenshotAsync(
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        ScreenshotOptions options = ParseScreenshotOptions(parameters);
        if (!options.FromSurface)
        {
            throw new DomainError(
                "Page.captureScreenshot fromSurface=false is not supported: Obscura has no separate browser-window compositor surface");
        }

        if (options.Format == ScreenshotFormat.Webp && options.QualitySupplied)
        {
            throw new DomainError(
                "WebP screenshot quality is not supported by the current lossless encoder");
        }

        BrowserPage page = ctx.GetSessionPageMut(sessionId)
            ?? throw new DomainError("No page for session");
        await PrepareCaptureResourcesIfRequestedAsync(page).ConfigureAwait(false);
        AnimationSample animationSample = page.LiveAnimationSample();
        (float Width, float Height) viewport = page.Viewport;
        double deviceScaleFactor = page.DeviceScaleFactor;
        (float X, float Y) trustedScroll = page.ScreenshotScrollOffset();
        (double scrollX, double scrollY) = ((double)trustedScroll.X, (double)trustedScroll.Y);
        (float Width, float Height)? fullPageSize = null;
        if (options.CaptureBeyondViewport && options.Clip is null)
        {
            (float Width, float Height) contentSize =
                page.PreparedContentSizeWithAnimationSample(animationSample)
                ?? throw new DomainError(
                    "Page.captureScreenshot failed: no retained document size");
            fullPageSize = (
                Math.Max(contentSize.Width, viewport.Width),
                Math.Max(contentSize.Height, viewport.Height));
        }

        CaptureRegion? region;
        if (options.Clip is { } clip)
        {
            region = ChromiumClipRegion(clip, deviceScaleFactor);
        }
        else if (fullPageSize is { } contentSize)
        {
            region = CaptureRegion.New(
                0.0f, 0.0f, contentSize.Width, contentSize.Height, page.DeviceScaleFactor);
        }
        else if (page.DeviceScaleFactor != 1.0f)
        {
            region = CaptureRegion.New(
                (float)scrollX, (float)scrollY, viewport.Width, viewport.Height, page.DeviceScaleFactor);
        }
        else
        {
            region = null;
        }

        byte[] png;
        bool pngIsFinalEncoding;
        if (region is { } captureRegion)
        {
            (byte[]? bytes, CaptureError? error) =
                page.ScreenshotRegionWithAnimationSample(captureRegion, animationSample);
            if (bytes is not null && error is null)
            {
                png = bytes;
                pngIsFinalEncoding = false;
            }
            else if (error == CaptureError.AllocationLimitExceeded
                && options.Format == ScreenshotFormat.Png
                && options.Clip is null
                && options.CaptureBeyondViewport)
            {
                png = EncodeLongFullPagePng(
                    page,
                    fullPageSize ?? throw new DomainError(
                        "full-page route has retained dimensions"),
                    page.DeviceScaleFactor,
                    animationSample,
                    options.OptimizeForSpeed);
                pngIsFinalEncoding = true;
            }
            else
            {
                throw new DomainError(
                    CaptureErrorMessage(error ?? CaptureError.PaintFailed));
            }
        }
        else
        {
            if (CaptureLimits.ValidateCaptureRegion(CaptureRegion.New(
                    (float)scrollX, (float)scrollY, viewport.Width, viewport.Height, 1.0f))
                is { } validationError)
            {
                throw new DomainError(CaptureErrorMessage(validationError));
            }

            png = page.ScreenshotWithAnimationSample(viewport, animationSample)
                ?? throw new DomainError(
                    "Page.captureScreenshot failed: the page has no DOM to render");
            pngIsFinalEncoding = false;
        }

        // Keep the common path allocation-free and byte-for-byte compatible with the
        // renderer's native PNG encoder.
        byte[] encoded;
        if (options.Format == ScreenshotFormat.Png
            && (!options.OptimizeForSpeed || pngIsFinalEncoding))
        {
            encoded = png;
        }
        else
        {
            RgbaImage source = RgbaImage.Decode(png)
                ?? throw new DomainError(
                    "Page.captureScreenshot could not decode renderer PNG");
            encoded = EncodeScreenshot(source, options);
        }

        return new JsonObject { ["data"] = Convert.ToBase64String(encoded) };
    }
}
