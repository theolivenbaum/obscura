using System.Text.Json.Nodes;
using Obscura.Net;

namespace Obscura.Cdp.Domains;

/// <summary>CDP <c>Storage</c> domain: the browser-context-scoped cookie surfaces.</summary>
public static class Storage
{
    private static CookieJar CookieJarFor(CdpContext ctx, JsonNode? parameters, string? sessionId)
    {
        if (parameters.Get("browserContextId").AsString() is { } id)
        {
            return ctx.BrowserContextById(id)?.CookieJar
                ?? throw new DomainError($"Browser context not found: {id}");
        }

        return ctx.GetSessionPage(sessionId)?.Context.CookieJar ?? ctx.DefaultContext.CookieJar;
    }

    public static async Task<DomainResult> HandleAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await Task.CompletedTask.ConfigureAwait(false);
        try
        {
            switch (method)
            {
                case "getCookies":
                {
                    var cookies = new JsonArray();
                    foreach (var cookie in CookieJarFor(ctx, parameters, sessionId).GetAllCookies())
                    {
                        cookies.Add(Network.CookieInfoToCdpJson(cookie));
                    }

                    return DomainResult.Ok(new JsonObject { ["cookies"] = cookies });
                }

                case "setCookies":
                {
                    if (parameters.Get("cookies").AsJsonArray() is { } cookies)
                    {
                        CookieJarFor(ctx, parameters, sessionId)
                            .SetCookiesFromCdp(Network.ParseCookies(cookies));
                    }

                    return DomainResult.Empty();
                }

                case "clearCookies":
                    CookieJarFor(ctx, parameters, sessionId).Clear();
                    return DomainResult.Empty();

                case "deleteCookies":
                {
                    if (CookieParams.ParseDeleteCookiesParams(parameters) is { } filter)
                    {
                        CookieJarFor(ctx, parameters, sessionId)
                            .DeleteCookiesFiltered(filter.Name, filter.Domain, filter.Path);
                    }

                    return DomainResult.Empty();
                }

                // Unlike the other domains, an unknown Storage method is a permissive no-op.
                default:
                    return DomainResult.Empty();
            }
        }
        catch (DomainError error)
        {
            return DomainResult.Err(error.Message);
        }
    }
}
