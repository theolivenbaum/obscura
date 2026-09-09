using System.Text;
using System.Text.Json.Nodes;
using Obscura.Net;

namespace Obscura.Cdp.Domains;

/// <summary>CDP <c>Network</c> domain: request/response bodies, cookies, cache and blocking.</summary>
public static class Network
{
    private const long SessionCookieExpires = -1;
    private const int DefaultSecurePort = 443;
    private const int DefaultInsecurePort = 80;
    private const string SourceSchemeSecure = "Secure";
    private const string SourceSchemeNonsecure = "NonSecure";
    private const string DefaultSameSite = "Lax";

    /// <summary>
    /// Resolve the cookie jar for a Network request: prefer the session's page jar, fall back to
    /// the default browser context.
    /// </summary>
    /// <remarks>
    /// Puppeteer and Playwright both call Network.setCookie/getCookies/deleteCookies BEFORE
    /// attaching to a target; requiring a session would break those flows (Storage.* already
    /// mirrors this).
    /// </remarks>
    private static CookieJar CookieJarFor(CdpContext ctx, string? sessionId) =>
        ctx.GetSessionPage(sessionId)?.Context.CookieJar ?? ctx.DefaultContext.CookieJar;

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
                return DomainResult.Empty();

            case "disable":
            {
                if (ctx.GetSessionPageMut(sessionId) is { } page)
                {
                    page.ClearResponseBodies();
                }
                else
                {
                    foreach (var each in ctx.Pages)
                    {
                        each.ClearResponseBodies();
                    }
                }

                return DomainResult.Empty();
            }

            case "setExtraHTTPHeaders":
            {
                JsonObject? headers = parameters.Get("headers").AsJsonObject();
                if (ctx.GetSessionPage(sessionId) is { } page && headers is not null)
                {
                    var headerMap = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, JsonNode?> entry in headers)
                    {
                        headerMap[entry.Key] = entry.Value.AsString() ?? string.Empty;
                    }

                    page.HttpClient.SetExtraHeaders(headerMap);
                }

                return DomainResult.Empty();
            }

            case "setUserAgentOverride":
            {
                string userAgent = parameters.Get("userAgent").AsString() ?? string.Empty;
                ctx.GetSessionPage(sessionId)?.HttpClient.SetUserAgent(userAgent);
                return DomainResult.Empty();
            }

            case "getCookies":
            case "getAllCookies":
            {
                var cookies = new JsonArray();
                foreach (CookieInfo cookie in CookieJarFor(ctx, sessionId).GetAllCookies())
                {
                    cookies.Add(CookieInfoToCdpJson(cookie));
                }

                return DomainResult.Ok(new JsonObject { ["cookies"] = cookies });
            }

            case "setCookie":
            {
                if (CookieParams.ParseCdpCookie(parameters) is not { } cookie)
                {
                    return DomainResult.Err("setCookie: missing required name/domain (or url)");
                }

                CookieJarFor(ctx, sessionId).SetCookiesFromCdp([cookie]);
                return DomainResult.Ok(new JsonObject { ["success"] = true });
            }

            case "setCookies":
            {
                if (parameters.Get("cookies").AsJsonArray() is { } cookies)
                {
                    CookieJarFor(ctx, sessionId).SetCookiesFromCdp(ParseCookies(cookies));
                }

                return DomainResult.Empty();
            }

            case "deleteCookies":
            {
                if (CookieParams.ParseDeleteCookiesParams(parameters) is { } filter)
                {
                    CookieJarFor(ctx, sessionId)
                        .DeleteCookiesFiltered(filter.Name, filter.Domain, filter.Path);
                }

                return DomainResult.Empty();
            }

            case "clearBrowserCookies":
                CookieJarFor(ctx, sessionId).Clear();
                return DomainResult.Empty();

            case "setCacheDisabled":
            case "setRequestInterception":
                return DomainResult.Empty();

            case "setBlockedURLs":
            {
                var patterns = new List<string>();
                if (parameters.Get("urls").AsJsonArray() is { } urls)
                {
                    foreach (JsonNode? entry in urls)
                    {
                        if (entry.AsString() is { } pattern)
                        {
                            patterns.Add(pattern);
                        }
                    }
                }

                if (ctx.GetSessionPageMut(sessionId) is { } page)
                {
                    page.SetBlockedUrls(patterns);
                }
                else
                {
                    foreach (var each in ctx.Pages)
                    {
                        each.SetBlockedUrls([.. patterns]);
                    }
                }

                return DomainResult.Empty();
            }

            case "getResponseBody":
            {
                if (parameters.Get("requestId").AsString() is not { } requestId)
                {
                    return DomainResult.Err("Network.getResponseBody requires requestId");
                }

                Obscura.Browser.StoredResponseBody? body;
                if (ctx.GetSessionPage(sessionId) is { } page)
                {
                    body = page.GetResponseBody(requestId);
                }
                else
                {
                    body = null;
                    foreach (var each in ctx.Pages)
                    {
                        body = each.GetResponseBody(requestId);
                        if (body is not null)
                        {
                            break;
                        }
                    }
                }

                return body is null
                    ? DomainResult.Err($"No response body found for requestId {requestId}")
                    : DomainResult.Ok(new JsonObject
                    {
                        ["body"] = body.Body,
                        ["base64Encoded"] = body.Base64Encoded,
                    });
            }

            default:
                return DomainResult.Err($"Unknown Network method: {method}");
        }
    }

    internal static List<CookieInfo> ParseCookies(JsonArray cookies)
    {
        var parsed = new List<CookieInfo>();
        foreach (JsonNode? entry in cookies)
        {
            if (CookieParams.ParseCdpCookie(entry) is { } cookie)
            {
                parsed.Add(cookie);
            }
        }

        return parsed;
    }

    internal static JsonObject CookieInfoToCdpJson(CookieInfo cookie)
    {
        ArgumentNullException.ThrowIfNull(cookie);
        long expires = cookie.Expires ?? SessionCookieExpires;
        bool session = cookie.Expires is null;
        string sameSite = cookie.SameSite.Length == 0 ? DefaultSameSite : cookie.SameSite;
        return new JsonObject
        {
            ["name"] = cookie.Name,
            ["value"] = cookie.Value,
            ["domain"] = cookie.Domain,
            ["path"] = cookie.Path,
            ["expires"] = expires,
            // Rust measures the UTF-8 length of both halves; a UTF-16 count would
            // disagree for any non-ASCII cookie.
            ["size"] = Encoding.UTF8.GetByteCount(cookie.Name) + Encoding.UTF8.GetByteCount(cookie.Value),
            ["httpOnly"] = cookie.HttpOnly,
            ["secure"] = cookie.Secure,
            ["session"] = session,
            ["sameSite"] = sameSite,
            ["sameParty"] = false,
            ["sourceScheme"] = cookie.Secure ? SourceSchemeSecure : SourceSchemeNonsecure,
            ["sourcePort"] = cookie.Secure ? DefaultSecurePort : DefaultInsecurePort,
            ["priority"] = "Medium",
        };
    }
}
