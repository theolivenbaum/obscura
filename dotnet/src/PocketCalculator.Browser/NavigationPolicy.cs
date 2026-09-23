using PocketCalculator.Js.Ops;

namespace PocketCalculator.Browser;

/// <summary>
/// The <c>file:</c> rule every navigation entry point shares (SECURITY.md C4, H1-H3).
/// </summary>
/// <remarks>
/// <para>
/// A document never navigates itself, or a new window, from a non-<c>file:</c> URL
/// into <c>file:</c>, whatever the operator allowed: that is Chromium's rule for
/// page-initiated navigations. The page is not able to read the file afterwards (its
/// realm is gone), but the automation client now holds the local file as "the page",
/// and an agent acting on it leaks it.
/// </para>
/// <para>
/// A navigation the operator started (host code, the CLI, CDP, MCP) may reach
/// <c>file:</c> when the entry point's own switch allows it: CDP's
/// <c>--allow-file-access</c>, never for MCP, always for host code. The transport
/// backs this up: it serves <c>file:</c> only to a navigation with no initiator or a
/// request from a <c>file:</c> document.
/// </para>
/// </remarks>
public static class NavigationPolicy
{
    /// <summary>Whether <paramref name="url"/> is a <c>file:</c> URL, spelled any way.</summary>
    public static bool IsFileUrl(string url)
    {
        if (PageUrl.TryParse(url) is { } parsed)
        {
            return string.Equals(parsed.Scheme, "file", StringComparison.OrdinalIgnoreCase);
        }

        return url.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a navigation <paramref name="initiator"/> queued (null: none, the
    /// operator's) would step from a non-<c>file:</c> document into <c>file:</c>.
    /// </summary>
    public static bool RefusesPageInitiated(PendingNavigation? initiator, string url) =>
        initiator is not null && PageHelpers.CrossSchemeToFile(initiator.Initiator, url);

    /// <summary>Chromium's console text for a refused load of a local resource.</summary>
    public static string LocalResourceRefusal(string url) => $"Not allowed to load local resource: {url}";
}
