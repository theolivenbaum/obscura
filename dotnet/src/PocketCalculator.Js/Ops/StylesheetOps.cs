using PocketCalculator.Dom;
using PocketCalculator.Js.Url;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// <c>op_external_stylesheet_set</c> / <c>_remove</c> / <c>_get</c>: the host-held CSS of a
/// linked sheet, and whether page script may read it (upstream 04418a5, ops.rs).
/// </summary>
/// <remarks>
/// These replace the port's own <c>op_dom</c> commands <c>get_external_stylesheet_css</c> and
/// <c>set_external_stylesheet_css</c>, which handed any sheet's bytes back to whoever asked,
/// cross-origin ones included.
/// </remarks>
public static class StylesheetOps
{
    /// <summary>
    /// Whether a sheet whose response came from <paramref name="responseUrl"/> is same-origin
    /// with <paramref name="documentUrl"/>. Opaque origins are never same-origin, as with
    /// rust-url's <c>Origin</c> equality.
    /// </summary>
    internal static bool StylesheetOriginClean(string documentUrl, string responseUrl)
    {
        if (UrlRecord.Parse(documentUrl)?.AsciiOrigin is not { } document
            || UrlRecord.Parse(responseUrl)?.AsciiOrigin is not { } response)
        {
            return false;
        }

        return !string.Equals(document, "null", StringComparison.Ordinal)
            && string.Equals(document, response, StringComparison.Ordinal);
    }

    /// <summary>
    /// Installs host-fetched CSS without manufacturing a page-visible <c>&lt;style&gt;</c>.
    /// The sheet is origin-clean only when the caller's import graph was and the host agrees
    /// that <paramref name="responseUrl"/> is same-origin with this document.
    /// </summary>
    public static bool OpExternalStylesheetSet(
        PocketCalculatorState state, uint ownerNid, string css, string responseUrl, bool importedOriginClean) =>
        OpGuard.Run("op_external_stylesheet_set", () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            if (state.Dom is not { } dom)
            {
                return false;
            }

            NodeId owner = NodeId.New(ownerNid);
            if (dom.GetNode(owner)?.AsElement() is not { } element
                || element.Name.Local is not ("link" or "style"))
            {
                return false;
            }

            bool originClean = importedOriginClean && StylesheetOriginClean(state.Url, responseUrl);
            bool cssChanged = !string.Equals(dom.ExternalStylesheetCss(owner), css, StringComparison.Ordinal);
            dom.SetExternalStylesheet(owner, css, originClean);
            if (cssChanged)
            {
                Invalidate(state, dom, owner);
            }

            return true;
        }, false);

    /// <summary>
    /// Installs CSS the host fetched itself (<see cref="LinkedStylesheetLoader"/>), with the
    /// origin-clean bit the host computed from the responses. Nothing the shim passes decides it.
    /// </summary>
    internal static bool SetLoadedExternalStylesheet(
        PocketCalculatorState state, uint ownerNid, string css, bool originClean) =>
        OpGuard.Run("op_load_stylesheet", () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            if (state.Dom is not { } dom)
            {
                return false;
            }

            NodeId owner = NodeId.New(ownerNid);
            if (dom.GetNode(owner)?.AsElement() is not { } element
                || element.Name.Local is not ("link" or "style"))
            {
                return false;
            }

            bool cssChanged = !string.Equals(dom.ExternalStylesheetCss(owner), css, StringComparison.Ordinal);
            dom.SetExternalStylesheet(owner, css, originClean);
            if (cssChanged)
            {
                Invalidate(state, dom, owner);
            }

            return true;
        }, false);

    /// <summary>Forgets a sheet's host-held CSS.</summary>
    public static bool OpExternalStylesheetRemove(PocketCalculatorState state, uint ownerNid) =>
        OpGuard.Run("op_external_stylesheet_remove", () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            if (state.Dom is not { } dom)
            {
                return false;
            }

            NodeId owner = NodeId.New(ownerNid);
            bool changed = dom.RemoveExternalStylesheet(owner);
            if (changed)
            {
                Invalidate(state, dom, owner);
            }

            return changed;
        }, false);

    /// <summary>
    /// The CSSOM-readable bytes of a loaded sheet: <c>null</c> when there is none,
    /// <c>{"originClean":false}</c> without the bytes when it is not origin-clean, and
    /// <c>{"originClean":true,"css":...}</c> otherwise.
    /// </summary>
    public static string OpExternalStylesheetGet(PocketCalculatorState state, uint ownerNid) =>
        OpGuard.Run("op_external_stylesheet_get", () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            if (state.Dom is not { } dom
                || !dom.TryGetExternalStylesheet(NodeId.New(ownerNid), out ExternalStylesheet sheet))
            {
                return "null";
            }

            if (!sheet.OriginClean)
            {
                return """{"originClean":false}""";
            }

            return "{\"originClean\":true,\"css\":" + SerdeJson.String(sheet.Css) + "}";
        }, "null");

    // What upstream does on a change to a connected owner: the sheet is cascade input, so the
    // prepared render and any retained style mutations are stale.
    private static void Invalidate(PocketCalculatorState state, DomTree dom, NodeId owner)
    {
        if (!StateHelpers.NodeIsConnected(dom, owner))
        {
            return;
        }

        state.ActivityGeneration = unchecked(state.ActivityGeneration + 1);
        state.PreparedRender = null;
        state.PendingStyleMutations.Clear();
        state.ResolvedScroll = null;
    }
}
