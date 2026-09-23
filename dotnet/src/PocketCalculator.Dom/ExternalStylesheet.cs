namespace PocketCalculator.Dom;

/// <summary>
/// Host-fetched author CSS held beside its owner node rather than in the tree, and whether
/// page script may read it (upstream 04418a5, <c>ExternalStylesheet</c> in
/// crates/obscura-dom/src/tree.rs). A sheet is origin-clean when its response URL and every
/// <c>@import</c> in its graph are same-origin with the document.
/// </summary>
/// <remarks>
/// DEVIATION from upstream, which keeps a list of sources joined on read. The port joins on
/// write, since every reader wants the joined text.
/// </remarks>
public readonly record struct ExternalStylesheet(string Css, bool OriginClean);
