using System.Text.Json.Nodes;

namespace Obscura.Cdp.Domains;

/// <summary>CDP <c>Browser</c> domain: version, window management and permission acks.</summary>
public static class Browser
{
    public static async Task<DomainResult> HandleAsync(string method, JsonNode? parameters)
    {
        _ = parameters;
        await Task.CompletedTask.ConfigureAwait(false);
        switch (method)
        {
            case "getVersion":
                return DomainResult.Ok(new JsonObject
                {
                    ["protocolVersion"] = "1.3",
                    ["product"] = "Chrome/145.0.0.0",
                    ["revision"] = "@0000000000000000000000000000000000000000",
                    ["userAgent"] = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 "
                        + "(KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36",
                    ["jsVersion"] = "14.5.0.0",
                });

            case "close":
                return DomainResult.Empty();

            case "getWindowForTarget":
                return DomainResult.Ok(new JsonObject
                {
                    ["windowId"] = 1,
                    ["bounds"] = new JsonObject
                    {
                        ["left"] = 0,
                        ["top"] = 0,
                        ["width"] = 1280,
                        ["height"] = 720,
                        ["windowState"] = "normal",
                    },
                });

            case "setDownloadBehavior":
                return DomainResult.Empty();

            case "getWindowBounds":
                return DomainResult.Ok(new JsonObject
                {
                    ["bounds"] = new JsonObject
                    {
                        ["left"] = 0,
                        ["top"] = 0,
                        ["width"] = 1280,
                        ["height"] = 720,
                        ["windowState"] = "normal",
                    },
                });

            // No-op acks for window-management methods Playwright sends during page setup. We
            // don't model real OS windows, but answering with {} lets the client's setup sequence
            // complete instead of tearing down the page on an unknown-method error.
            case "setWindowBounds":
                return DomainResult.Empty();

            // Playwright grants permissions (geolocation, notifications, ...) per browser context
            // during setup. obscura does not gate any API on a permission grant today, so the
            // honest answer is to accept and remember nothing; an unknown-method error would abort
            // the client's whole context initialization.
            case "grantPermissions":
            case "resetPermissions":
                return DomainResult.Empty();

            default:
                return DomainResult.Err($"Unknown Browser method: {method}");
        }
    }
}
