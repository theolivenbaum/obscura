// End-of-cascade adjustments Chromium's StyleAdjuster and LayoutTheme::AdjustStyle make to
// form controls and other "unusual" elements.
using PocketCalculator.Render.Css;

namespace PocketCalculator.Render;

public static partial class ComputedStyle
{
    /// <summary>
    /// Whether the user-agent sheet gives this element native <c>appearance: auto</c>.
    /// </summary>
    /// <remarks>
    /// Measured on Chromium 141: every <c>input</c> but <c>hidden</c>, <c>file</c> and
    /// <c>image</c>, and <c>button</c>, <c>select</c>, <c>textarea</c>, <c>meter</c> and
    /// <c>progress</c>. <paramref name="inputType"/> is the lower-cased <c>type</c> attribute.
    /// </remarks>
    internal static bool HasUserAgentAppearance(string local, string? inputType) => local switch
    {
        "input" => inputType is not ("hidden" or "file" or "image"),
        "button" or "select" or "textarea" or "meter" or "progress" => true,
        _ => false,
    };

    /// <summary>
    /// Elements whose <c>display: contents</c> computes to <c>none</c> (CSS Display 3,
    /// appendix B): replaced elements and form controls, which have no children to hoist.
    /// Measured on Chromium 141; <c>button</c>, <c>fieldset</c>, <c>legend</c>,
    /// <c>details</c> and <c>option</c> keep <c>contents</c>.
    /// </summary>
    private static bool ContentsComputesToNone(string local) => local is "input" or "select"
        or "textarea" or "meter" or "progress" or "img" or "video" or "audio" or "canvas"
        or "iframe" or "embed" or "object" or "svg" or "br" or "wbr";

    /// <summary>
    /// Settle <c>appearance</c> and apply the display adjustments Chromium makes to form
    /// controls once the author cascade has run.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-render, which keeps an author <c>display</c> on a form
    /// control as written. Chromium makes three adjustments, all measured on Chromium 141:
    /// <list type="bullet">
    /// <item><c>display: contents</c> on a replaced element or a form control computes to
    /// <c>none</c>.</item>
    /// <item>A control that keeps its native appearance (LayoutTheme::AdjustStyle) turns
    /// <c>inline</c>, <c>inline-table</c> and every internal table display into
    /// <c>inline-block</c>, and <c>table</c> into <c>block</c>; <c>block</c>, <c>flex</c>,
    /// <c>grid</c>, their inline forms, <c>flow-root</c>, <c>contents</c> and <c>none</c> are
    /// kept. This is what getComputedStyle reports.</item>
    /// <item>A drop-down <c>select</c> with native appearance computes
    /// <c>line-height: normal</c> whatever the author set.</item>
    /// <item>With <c>appearance: none</c> the computed value stays <c>inline</c>, but the
    /// control is still laid out as an atomic inline-level box: a <c>display: inline</c>
    /// button is never split across lines.</item>
    /// </list>
    /// wikipedia.org's <c>.lang-list-button { display: inline }</c> laid out as an inline box
    /// in the port, broke across two lines and floated its label to the right edge. See
    /// "Known deviations" in todo.md.
    /// </remarks>
    internal static void AdjustFormControlStyle(
        LayoutStyle style,
        string local,
        bool htmlNamespace,
        string? inputType,
        string? parentAppearance,
        bool menuList = false)
    {
        bool userAgentAppearance = htmlNamespace && HasUserAgentAppearance(local, inputType);
        style.ComputedAppearance = style.AppearanceSpecified switch
        {
            null => userAgentAppearance ? "auto" : "none",
            "initial" or "unset" => "none",
            "inherit" => parentAppearance ?? "none",
            { } keyword => keyword,
        };

        if (style.DisplayContents && (htmlNamespace || local == "svg") && ContentsComputesToNone(local))
        {
            style.DisplayContents = false;
            style.Display = Display.None;
            return;
        }

        if (!userAgentAppearance || style.DisplayContents || style.Display == Display.None)
        {
            return;
        }

        bool effectiveAppearance = !CssText.EqualsAscii(style.ComputedAppearance, "none");

        // LayoutTheme::AdjustMenuListStyle: a drop-down `select` that keeps its native
        // appearance ignores the author line-height (Chromium 141: `line-height: 30px` or an
        // inherited one through `font: inherit` still computes to `normal`, 19px tall).
        if (menuList && effectiveAppearance)
        {
            style.LineHeight = PocketCalculator.Render.LineHeight.Normal;
            style.LineHeightExpression = null;
        }
        if (style.Display == Display.Inline && !style.IsInlineBlock && !style.IsTableBox)
        {
            style.IsInlineBlock = true;
            style.ReportsInlineDisplay = !effectiveAppearance;
            return;
        }

        if (!effectiveAppearance)
        {
            return;
        }

        if (style.AuthoredTableDisplay != TableInternalDisplay.None || style.IsTableCellBox)
        {
            style.AuthoredTableDisplay = TableInternalDisplay.None;
            if (style.IsTableCellBox)
            {
                style.IsTableCellBox = false;
                style.InternalFlexContainer = false;
                if (!style.FlexDirectionAuthored)
                {
                    style.FlexDirection = null;
                }

                if (!style.AlignItemsAuthored)
                {
                    style.AlignItems = null;
                }
            }

            style.Display = Display.Inline;
            style.IsInlineBlock = true;
            style.FlowRoot = false;
            style.IsTableBox = false;
        }
        else if (style.IsTableBox)
        {
            // `inline-table` becomes `inline-block`, `table` becomes `block`.
            style.IsTableBox = false;
            style.FlowRoot = false;
        }
    }
}
