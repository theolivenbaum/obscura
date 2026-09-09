using System.Text.Json.Nodes;
using Obscura.Js.Runtime;

namespace Obscura.Cdp.Domains;

/// <summary>The non-standard <c>LP</c> domain: markdown extraction over the live DOM.</summary>
public static class Lp
{
    public static async Task<DomainResult> HandleAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ = parameters;
        await Task.CompletedTask.ConfigureAwait(false);
        if (method != "getMarkdown")
        {
            return DomainResult.Err($"Unknown LP method: {method}");
        }

        if (ctx.GetSessionPageMut(sessionId) is not { } page)
        {
            return DomainResult.Err("No page");
        }

        var result = page.Evaluate(MarkdownScript.HtmlToMarkdown);
        var markdown = result.AsString() ?? string.Empty;
        return DomainResult.Ok(new JsonObject { ["markdown"] = markdown });
    }
}
