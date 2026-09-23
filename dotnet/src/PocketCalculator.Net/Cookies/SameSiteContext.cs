namespace PocketCalculator.Net;

/// <summary>
/// The site relationship used when deciding whether a cookie may be sent (port of
/// <c>SameSiteContext</c> in <c>crates/obscura-net/src/cookies.rs</c>).
/// </summary>
public enum SameSiteContext
{
    /// <summary>The request is same-site with its initiator, or has none: every cookie may go.</summary>
    SameSite,

    /// <summary>A cross-site top-level navigation with a safe method: Lax and None cookies go.</summary>
    CrossSiteTopLevelSafe,

    /// <summary>Any other cross-site request: only SameSite=None cookies go.</summary>
    CrossSite,
}
