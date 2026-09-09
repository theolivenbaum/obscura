using System.Text.Json.Nodes;

using Obscura.Browser;

using BrowserContextRef = Obscura.Browser.BrowserContext;
using BrowserPage = Obscura.Browser.Page;

namespace Obscura.Cdp.Domains;

/// <summary>
/// The CDP <c>Target</c> domain: targets, browser contexts, sessions and
/// attach/detach.
/// </summary>
/// <remarks>
/// Every <c>TargetInfo</c> this domain emits carries <c>canAccessOpener</c>. It
/// is required by the protocol and strict CDP clients (chromiumoxide) panic when
/// it is missing, including on the browser target that has no opener.
/// </remarks>
public static class Target
{
    public static async Task<DomainResult> HandleAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? parentSessionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        try
        {
            return await HandleCoreAsync(method, parameters, ctx, parentSessionId)
                .ConfigureAwait(false);
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
        string? parentSessionId)
    {
        switch (method)
        {
            case "setDiscoverTargets":
            {
                ctx.PendingEvents.Add(CdpEvent.New(
                    "Target.targetCreated",
                    new JsonObject { ["targetInfo"] = BrowserTargetInfo(withContextId: true) }));
                foreach (BrowserPage page in ctx.Pages)
                {
                    ctx.PendingEvents.Add(CdpEvent.New(
                        "Target.targetCreated",
                        new JsonObject { ["targetInfo"] = PageTargetInfo(page, attached: false) }));
                }

                return DomainResult.Empty();
            }

            case "getTargets":
            {
                var targets = new JsonArray();
                foreach (BrowserPage page in ctx.Pages)
                {
                    targets.Add(PageTargetInfo(page, attached: true));
                }

                return DomainResult.Ok(new JsonObject { ["targetInfos"] = targets });
            }

            case "createTarget":
            {
                string url = parameters.Get("url").AsStringOr("about:blank");
                string? contextId = parameters.Get("browserContextId").AsString();
                BrowserContextRef? context = contextId is null
                    ? ctx.DefaultContext
                    : ctx.BrowserContextById(contextId);
                if (context is null)
                {
                    return DomainResult.Err($"Browser context not found: {contextId}");
                }

                // Same gate as Page.navigate (GHSA-q55h-vfv9-qcr5). Without this, a CDP
                // client can call Target.createTarget {url:"file:///etc/passwd"} and then
                // Runtime.evaluate the body off the created target, bypassing the
                // page-domain check entirely.
                if (CdpUtil.UrlIsFileScheme(url) && !context.AllowFileAccess)
                {
                    return DomainResult.Err(
                        "Target.createTarget to file:// is disabled. Restart with `obscura serve --allow-file-access` to enable.");
                }

                if (!ctx.CreatePageInContext(contextId, out string? created, out string? createError)
                    || created is null)
                {
                    return DomainResult.Err(createError ?? "Browser context not found");
                }

                string pageId = created;
                string sessionId = $"{pageId}-session";

                (string FrameId, string Origin)? committedDocument = null;
                if (ctx.GetPageMut(pageId) is { } page)
                {
                    if (url is "about:blank" or "")
                    {
                        page.NavigateBlank();
                    }
                    else
                    {
                        try
                        {
                            await page.NavigateAsync(url).ConfigureAwait(false);
                            committedDocument = (page.FrameId, page.UrlString());
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            // `navigate(...).await.ok()`: a failed navigation leaves the
                            // target open on its previous document.
                            committedDocument = null;
                        }
                    }
                }

                if (committedDocument is { } document)
                {
                    ctx.CommitDefaultContext(pageId, document.FrameId, document.Origin);
                }

                ctx.Sessions[sessionId] = pageId;

                if (ctx.GetPage(pageId) is { } announced)
                {
                    ctx.PendingEvents.Add(CdpEvent.New(
                        "Target.targetCreated",
                        new JsonObject { ["targetInfo"] = PageTargetInfo(announced, attached: false) }));
                }

                if (ctx.GetPage(pageId) is { } attached)
                {
                    ctx.PendingEvents.Add(CdpEvent.New(
                        "Target.attachedToTarget",
                        new JsonObject
                        {
                            ["sessionId"] = sessionId,
                            ["targetInfo"] = PageTargetInfo(attached, attached: true),
                            ["waitingForDebugger"] = false,
                        }));
                }

                return DomainResult.Ok(new JsonObject { ["targetId"] = pageId });
            }

            case "attachToBrowserTarget":
            {
                // Playwright calls this on connect to obtain a session for the implicit
                // "browser" target. Returning Unknown method aborts the connect handshake
                // before any user code runs.
                const string sessionId = "browser-session";
                ctx.Sessions[sessionId] = "browser";

                ctx.PendingEvents.Add(CdpEvent.New(
                    "Target.attachedToTarget",
                    new JsonObject
                    {
                        ["sessionId"] = sessionId,
                        ["targetInfo"] = BrowserTargetInfo(withContextId: true),
                        ["waitingForDebugger"] = false,
                    }));

                return DomainResult.Ok(new JsonObject { ["sessionId"] = sessionId });
            }

            case "attachToTarget":
            {
                if (parameters.Get("targetId").AsString() is not { } targetId)
                {
                    return DomainResult.Err("targetId required");
                }

                if (ctx.GetPage(targetId) is null)
                {
                    return DomainResult.Err("Target not found");
                }

                string sessionId = ctx.NextTargetSession(targetId);
                ctx.Sessions[sessionId] = targetId;

                if (ctx.GetPage(targetId) is { } page)
                {
                    var eventParams = new JsonObject
                    {
                        ["sessionId"] = sessionId,
                        ["targetInfo"] = PageTargetInfo(page, attached: true),
                        ["waitingForDebugger"] = false,
                    };
                    ctx.PendingEvents.Add(parentSessionId is null
                        ? CdpEvent.New("Target.attachedToTarget", eventParams)
                        : CdpEvent.WithSession("Target.attachedToTarget", eventParams, parentSessionId));
                }

                return DomainResult.Ok(new JsonObject { ["sessionId"] = sessionId });
            }

            case "closeTarget":
            {
                if (parameters.Get("targetId").AsString() is not { } targetId)
                {
                    return DomainResult.Err("targetId required");
                }

                List<string> sessions = [];
                foreach ((string session, string pageId) in ctx.Sessions)
                {
                    if (string.Equals(pageId, targetId, StringComparison.Ordinal))
                    {
                        sessions.Add(session);
                    }
                }

                sessions.Sort(StringComparer.Ordinal);
                foreach (string session in sessions)
                {
                    ctx.PendingEvents.Add(CdpEvent.New(
                        "Target.detachedFromTarget",
                        new JsonObject { ["sessionId"] = session, ["targetId"] = targetId }));
                }

                ctx.PendingEvents.Add(CdpEvent.New(
                    "Target.targetDestroyed",
                    new JsonObject { ["targetId"] = targetId }));

                ctx.RemovePage(targetId);
                return DomainResult.Ok(new JsonObject { ["success"] = true });
            }

            case "setAutoAttach":
                return DomainResult.Empty();

            // No multi-target lifecycle to manage: obscura runs one page per session.
            // Ack these so Chrome-shaped clients that call them do not warn (issue #340).
            case "detachFromTarget":
            {
                if (parameters.Get("sessionId").AsString() is { } sessionId)
                {
                    ctx.Sessions.TryGetValue(sessionId, out string? pageId);
                    ctx.Sessions.Remove(sessionId);
                    ctx.RuntimeEnabledSessions.Remove(sessionId);
                    if (pageId is not null)
                    {
                        ctx.RefreshRuntimeEventCollection(pageId);
                    }

                    ctx.Screencasts.Remove(sessionId);
                }

                return DomainResult.Empty();
            }

            case "activateTarget":
                return DomainResult.Empty();

            case "getBrowserContexts":
            {
                List<string> ids = [.. ctx.BrowserContexts.Keys];
                ids.Sort(StringComparer.Ordinal);
                var array = new JsonArray();
                foreach (string id in ids)
                {
                    array.Add(id);
                }

                return DomainResult.Ok(new JsonObject { ["browserContextIds"] = array });
            }

            case "createBrowserContext":
                return DomainResult.Ok(
                    new JsonObject { ["browserContextId"] = ctx.CreateBrowserContext() });

            case "disposeBrowserContext":
            {
                if (parameters.Get("browserContextId").AsString() is not { } contextId)
                {
                    return DomainResult.Err("browserContextId required");
                }

                List<(string SessionId, string PageId)> sessions = [];
                foreach ((string sessionId, string pageId) in ctx.Sessions)
                {
                    if (ctx.GetPage(pageId) is { } page
                        && string.Equals(page.Context.Id, contextId, StringComparison.Ordinal))
                    {
                        sessions.Add((sessionId, pageId));
                    }
                }

                if (!ctx.DisposeBrowserContext(contextId, out List<string> pageIds, out string? error))
                {
                    return DomainResult.Err(error ?? $"Browser context not found: {contextId}");
                }

                foreach ((string sessionId, string pageId) in sessions)
                {
                    ctx.PendingEvents.Add(CdpEvent.New(
                        "Target.detachedFromTarget",
                        new JsonObject { ["sessionId"] = sessionId, ["targetId"] = pageId }));
                }

                foreach (string pageId in pageIds)
                {
                    ctx.PendingEvents.Add(CdpEvent.New(
                        "Target.targetDestroyed",
                        new JsonObject { ["targetId"] = pageId }));
                }

                return DomainResult.Empty();
            }

            case "getTargetInfo":
            {
                if (parameters.Get("targetId").AsString() is { } targetId)
                {
                    if (ctx.GetPage(targetId) is not { } page)
                    {
                        return DomainResult.Err("Target not found");
                    }

                    JsonObject info = PageTargetInfo(page, attached: true);
                    info["targetId"] = targetId;
                    return DomainResult.Ok(new JsonObject { ["targetInfo"] = info });
                }

                // canAccessOpener is required on every TargetInfo per the CDP spec.
                // Strict clients (chromiumoxide) panic if it's missing. The browser
                // target itself has no opener.
                return DomainResult.Ok(
                    new JsonObject { ["targetInfo"] = BrowserTargetInfo(withContextId: false) });
            }

            default:
                return DomainResult.Err($"Unknown Target method: {method}");
        }
    }

    /// <summary>
    /// The protocol-complete <c>TargetInfo</c> for a page. Built in one place so no caller
    /// can drop <c>canAccessOpener</c>.
    /// </summary>
    internal static JsonObject PageTargetInfo(BrowserPage page, bool attached) => new()
    {
        ["targetId"] = page.Id,
        ["type"] = "page",
        ["title"] = page.Title,
        ["url"] = page.UrlString(),
        ["attached"] = attached,
        ["canAccessOpener"] = false,
        ["browserContextId"] = page.Context.Id,
    };

    /// <summary>The implicit browser target, which has no opener and no page state.</summary>
    private static JsonObject BrowserTargetInfo(bool withContextId)
    {
        var info = new JsonObject
        {
            ["targetId"] = "browser",
            ["type"] = "browser",
            ["title"] = "",
            ["url"] = "",
            ["attached"] = true,
            ["canAccessOpener"] = false,
        };
        if (withContextId)
        {
            info["browserContextId"] = "";
        }

        return info;
    }
}
