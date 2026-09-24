using System.Globalization;
using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using PocketCalculator.Js.Runtime;

namespace PocketCalculator.Browser;

/// <summary>
/// Child frames as CDP execution contexts: which realm an object or a point belongs to,
/// and host calls into that realm (port addition, SECURITY.md M6).
/// </summary>
/// <remarks>
/// The Rust engine announces child frames but gives them no execution context, so every
/// CDP command runs against the page's own document. A frame id here is the realm id
/// (<see cref="FrameRealm.FrameId"/>); 0 is the page.
/// </remarks>
public sealed partial class Page
{
    /// <summary>The live child frame with realm id <paramref name="frameId"/>.</summary>
    public FrameRealm? FrameById(uint frameId)
    {
        if (frameId == 0)
        {
            return null;
        }
        foreach (FrameRealm frame in Frames)
        {
            if (frame.FrameId == frameId)
            {
                return frame;
            }
        }
        return null;
    }

    /// <summary>The document an object id's realm is over: 0 for the page's.</summary>
    public uint FrameIdOfObject(string objectId) => Js?.FrameIdOfObject(objectId) ?? 0;

    /// <summary>
    /// <see cref="EvaluateHost"/> in the realm of document <paramref name="frameId"/>: the
    /// page's for 0, a child frame's otherwise. Null when that frame is gone.
    /// </summary>
    public JsonNode? EvaluateHostIn(uint frameId, string expression)
    {
        if (frameId == 0)
        {
            return EvaluateHost(expression);
        }
        if (FrameById(frameId) is not { } frame)
        {
            return null;
        }
        try
        {
            return frame.EvaluateHost(expression);
        }
        catch (JsRuntimeException)
        {
            return null;
        }
    }

    /// <summary><see cref="WithDom"/> over document <paramref name="frameId"/>.</summary>
    public T? WithFrameDom<T>(uint frameId, Func<DomTree, T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (frameId == 0)
        {
            return WithDom(body);
        }
        return FrameById(frameId)?.State.Dom is { } dom ? body(dom) : default;
    }

    /// <summary>
    /// Where child frame <paramref name="frameId"/>'s viewport starts in the page's: the
    /// sum of the content-box origins of its owner element and of every frame above it.
    /// Null when the frame, or one of its ancestors, has no connected owner.
    /// </summary>
    public (double X, double Y)? FrameViewportOffset(uint frameId)
    {
        double x = 0;
        double y = 0;
        for (int depth = 0; frameId != 0; depth++)
        {
            if (depth > 64 || FrameById(frameId) is not { } frame)
            {
                return null;
            }
            JsonNode? origin = EvaluateHostIn(
                frame.ParentFrameId,
                $"__obscura_host.frameContentOrigin({frameId.ToString(CultureInfo.InvariantCulture)})");
            if (origin is not JsonArray { Count: 2 } pair
                || pair[0]?.GetValue<double>() is not { } left
                || pair[1]?.GetValue<double>() is not { } top)
            {
                return null;
            }
            x += left;
            y += top;
            frameId = frame.ParentFrameId;
        }
        return (x, y);
    }

    /// <summary>
    /// The innermost document at page point (<paramref name="x"/>, <paramref name="y"/>),
    /// and the point in that document's viewport: descends through every iframe whose
    /// realm is live and whose element is the hit. A page with no child frames answers
    /// the page and the point unchanged without evaluating anything.
    /// </summary>
    public (uint FrameId, double X, double Y) FrameAtPoint(double x, double y)
    {
        uint frameId = 0;
        if (Frames.Count == 0)
        {
            return (0, x, y);
        }
        for (int depth = 0; depth <= 64; depth++)
        {
            string sx = x.ToString("R", CultureInfo.InvariantCulture);
            string sy = y.ToString("R", CultureInfo.InvariantCulture);
            JsonNode? hit = EvaluateHostIn(
                frameId,
                "(function() { var h = __obscura_host; var el = h.dom.elementFromPoint(" + sx + ", " + sy + ");"
                + "var f = el ? h.frameIdOf(el) : 0; if (!f) return null;"
                + "var o = h.frameContentOrigin(f); if (!o) return null;"
                + "return [f, o[0], o[1]]; })()");
            if (hit is not JsonArray { Count: 3 } triple
                || triple[0]?.GetValue<double>() is not { } child
                || triple[1]?.GetValue<double>() is not { } left
                || triple[2]?.GetValue<double>() is not { } top
                || FrameById((uint)child) is null)
            {
                break;
            }
            frameId = (uint)child;
            x -= left;
            y -= top;
        }
        return (frameId, x, y);
    }

    /// <summary>
    /// The document keyboard input goes to: the realm whose element was focused most
    /// recently, among the page and its child frames, as long as that realm still has a
    /// focused element. A page with no child frames answers the page.
    /// </summary>
    /// <remarks>
    /// Chromium sends key events to the focused frame's focused element. Focus in a frame
    /// realm is that realm's own state here, so the host goes by the order realms last
    /// took focus in (<see cref="PocketCalculator.Js.Ops.PocketCalculatorState.FocusStamp"/>).
    /// </remarks>
    public uint FocusedFrameId()
    {
        if (Frames.Count == 0 || Js is not { } js)
        {
            return 0;
        }
        uint best = 0;
        long stamp = js.State.FocusStamp;
        foreach (FrameRealm frame in Frames)
        {
            if (frame.State.FocusStamp > stamp)
            {
                stamp = frame.State.FocusStamp;
                best = frame.FrameId;
            }
        }
        if (best == 0)
        {
            return 0;
        }
        return EvaluateHostIn(best, "__obscura_host.dom.activeElement() !== null && __obscura_host.dom.activeElement() !== __obscura_host.dom.body()")?.GetValue<bool>() == true
            ? best
            : 0;
    }
}
