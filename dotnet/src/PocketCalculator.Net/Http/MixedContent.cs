using System.Net;

namespace PocketCalculator.Net;

/// <summary>What the mixed-content check decides for one request.</summary>
public enum MixedContentDecision
{
    /// <summary>Not mixed content: the request goes out as it is.</summary>
    Allow,

    /// <summary>Passive mixed content (an image): the request goes out over https instead.</summary>
    Upgrade,

    /// <summary>Blockable mixed content: the request never leaves.</summary>
    Block,
}

/// <summary>
/// Mixed-content checks for requests a secure document makes (W3C Mixed Content,
/// as Chromium ships it). The Rust engine has none (SECURITY.md I7): an https page
/// loaded http scripts, stylesheets and frames, which an on-path attacker can replace.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A request is mixed when its initiator is an https (or wss) document and the
/// target is http (or ws) to a host that is not potentially trustworthy. Loopback
/// (<c>127.0.0.0/8</c>, <c>::1</c>) and <c>localhost</c> / <c>*.localhost</c> are
/// trustworthy, so they are never mixed.</item>
/// <item>A top-level navigation is never mixed content.</item>
/// <item>Images, audio and video are upgraded to https; the upgraded request is not
/// retried over http if it fails, as in Chromium. Everything else (scripts, stylesheets,
/// frames, fetch/XHR, fonts, workers, WebSockets) is blocked.</item>
/// <item>A frame that is not itself secure (<c>about:srcdoc</c>, <c>about:blank</c>, an
/// http frame) is checked against its nearest secure ancestor, as Chromium checks the
/// frame and the top frame: see <see cref="Context"/>.</item>
/// <item>Each redirect hop is checked again, so an https URL that redirects to http is
/// treated the same way.</item>
/// <item>The decision is reported on the page's console channel with Chromium's text.</item>
/// </list>
/// <para>
/// <c>POCKETCALCULATOR_ALLOW_INSECURE_CONTENT=1</c>, or
/// <see cref="PocketCalculatorHttpClient.AllowInsecureContent"/>, turns the check off,
/// the equivalent of Chromium's <c>--allow-running-insecure-content</c>.
/// </para>
/// </remarks>
public static class MixedContent
{
    private const string EnvAllowInsecure = "POCKETCALCULATOR_ALLOW_INSECURE_CONTENT";

    /// <summary>Whether <c>POCKETCALCULATOR_ALLOW_INSECURE_CONTENT</c> switches the check off.</summary>
    public static bool EnvAllowsInsecureContent() =>
        Environment.GetEnvironmentVariable(EnvAllowInsecure) is "1" or "true";

    /// <summary>
    /// The decision for a request from <paramref name="initiator"/> to
    /// <paramref name="target"/>. <paramref name="topLevelNavigation"/> is true for a
    /// navigation of the top-level browsing context, which is never mixed.
    /// </summary>
    public static MixedContentDecision Check(
        Uri? initiator,
        Uri target,
        ResourceType resourceType,
        bool topLevelNavigation)
    {
        if (topLevelNavigation
            || initiator is null
            || !ProhibitsMixedContent(initiator)
            || !IsInsecure(target))
        {
            return MixedContentDecision.Allow;
        }

        return resourceType is ResourceType.Image or ResourceType.Media
            ? MixedContentDecision.Upgrade
            : MixedContentDecision.Block;
    }

    /// <summary>
    /// The document whose security decides for a request: the initiating document when it
    /// is secure, otherwise its nearest secure ancestor (null when there is none).
    /// </summary>
    public static Uri? Context(Uri? initiator, Uri? secureAncestor) =>
        initiator is not null && ProhibitsMixedContent(initiator) ? initiator : secureAncestor ?? initiator;

    /// <summary>True when a document at <paramref name="document"/> is a secure context whose requests are checked.</summary>
    public static bool ProhibitsMixedContent(Uri document) =>
        string.Equals(document.Scheme, "https", StringComparison.OrdinalIgnoreCase)
        || string.Equals(document.Scheme, "wss", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="target"/> is an http or ws URL whose host is not
    /// potentially trustworthy (Secure Contexts 3.2).
    /// </summary>
    public static bool IsInsecure(Uri target)
    {
        if (!string.Equals(target.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(target.Scheme, "ws", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsTrustworthyHost(UrlOrigin.Host(target));
    }

    /// <summary>Loopback addresses and <c>localhost</c> names are potentially trustworthy.</summary>
    public static bool IsTrustworthyHost(string host)
    {
        var name = host.TrimEnd('.');
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(name, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>The https form of a passive request's URL.</summary>
    public static Uri UpgradeUrl(Uri target)
    {
        var builder = new UriBuilder(target)
        {
            Scheme = string.Equals(target.Scheme, "ws", StringComparison.OrdinalIgnoreCase) ? "wss" : "https",
        };
        builder.Port = target.IsDefaultPort || target.Port == 80 ? -1 : target.Port;
        return builder.Uri;
    }

    /// <summary>Chromium's console text for a blocked request.</summary>
    public static string BlockedMessage(string page, string requestKind, string target) =>
        $"Mixed Content: The page at '{page}' was loaded over HTTPS, but requested an insecure "
        + $"{requestKind} '{target}'. This request has been blocked; the content must be served over HTTPS.";

    /// <summary>Chromium's console text for a blocked WebSocket.</summary>
    public static string BlockedWebSocketMessage(string page, string target) =>
        $"Mixed Content: The page at '{page}' was loaded over HTTPS, but attempted to connect to the insecure "
        + $"WebSocket endpoint '{target}'. This request has been blocked; this endpoint must be available over WSS.";

    /// <summary>Chromium's console text for an automatically upgraded request.</summary>
    public static string UpgradedMessage(string page, string target) =>
        $"Mixed Content: The page at '{page}' was loaded over HTTPS, but requested an insecure element "
        + $"'{target}'. This request was automatically upgraded to HTTPS, For more information see "
        + "https://blog.chromium.org/2019/10/no-more-mixed-messages-about-https.html";

    /// <summary>
    /// Chromium's name for the request context in the blocked message
    /// (<c>MixedContentChecker</c>'s <c>RequestContextName</c>).
    /// </summary>
    public static string RequestKind(ResourceType resourceType, bool nestedDocument = false) => resourceType switch
    {
        ResourceType.Document => nestedDocument ? "frame" : "resource",
        ResourceType.Script => "script",
        ResourceType.Stylesheet => "stylesheet",
        ResourceType.Image => "image",
        ResourceType.Font => "font",
        ResourceType.Xhr => "XMLHttpRequest endpoint",
        _ => "resource",
    };
}
