# Round 3 findings (user-reported component defects)

Measured against Chromium 141 at 1440x950, workspace server on :8080, Obscura CDP on :9400.

## B1 - paint order: content paints over a fixed z-index:1010 layer

Route `#/spaces/new`. The modal is inside
`body > div.tss-layer.tss-fade.tss-locks-page-scroll` which is `position: fixed; z-index: 1010`
and is the LAST body child. Chromium paints it above the page; Obscura lets page content
through it.

What is IDENTICAL between the engines (so it is not a style or layout bug):
- The modal ancestor chain, property for property: `.tss-modal` (relative, isolate) ->
  `.tss-modal-container` (absolute) -> `.tss-layer` (fixed, z-index 1010) -> BODY.
- The bleeding text's full ancestor chain (12 levels, listed below), and every box rect to
  within the known 15px sidebar delta.
- The body child list and their order.

Full ancestor chain of the text that bleeds through, as Obscura reports it:

     0 DIV  tss-textblock                              (static)
     1 DIV  tss-stack msk-card-perspective-content     (static)
     2 DIV  msk-card-perspective tss-stack-item        pos:relative ovf:hidden
     3 DIV  tss-stack                                  (static)
     4 DIV  tss-sectionstack-card tss-stack-item       pos:relative ovf:hidden auto
     5 DIV  tss-stack tss-sectionstack msk-app-content pos:absolute ovf:hidden auto
     6 DIV  msk-app-content-holder tss-stack-item      pos:relative
     7 DIV  tss-stack msk-app-content-stack            pos:relative ovf:hidden
     8 DIV  tss-stack tss-default-component-no-margin  ovf:hidden auto
     9 DIV  tss-splitview tss-splitview-vertical       pos:relative
    10 DIV  tss-stack                                  ovf:hidden
    11 BODY msk-app                                    ovf:hidden

Note `.msk-card-perspective` carries `will-change: transform` in the CSS, which makes it a
stacking context in Chromium. Obscura's `getComputedStyle` reports `willChange` as `auto`, so
it may not be parsing the property at all - worth checking whether that changes the tree it
builds, though it should if anything paint the text EARLIER, not later.

**The distinctive symptom: box backgrounds obey the order, text does not.** The modal's white
panel correctly covers the page's backgrounds; only the text punches through. An input's value
is likewise not painted while its placeholder is (see A3), which may or may not be related.

### Second instance of the same defect

Route `#/spaces/connect-apps`. The available-apps list looks empty in Obscura. It is not
missing: both engines lay out **430 card nodes, 344 with non-zero boxes**, at the same
positions (`msk-app-context-card` at [495,228,342,54] vs Chromium's [483,228,350,54]). They are
laid out and not painted.

### What was ruled out - five synthetic reductions that all MATCH Chromium

Do not re-derive these; they are dead ends:
1. `stack-probe.html` - 12 stacking cases: DOM order, z-index inversion, negative z-index,
   stacking contexts from opacity / transform / filter / will-change / isolation, a parent
   clamping a child's z-index, position:fixed, flex-child z-index, three-deep ordering.
   **All 12 identical.**
2. `stack2-probe.html` - the app's shape: absolute content pane + later fixed z-1010 layer
   containing overlay and panel. Identical.
3. `stack3-probe.html` - the same with the base text wrapped in static / relative-auto /
   relative-0 / absolute. Identical.
4. `stack4-probe.html` - `will-change: transform` on the text's wrapper, and a
   `visibility: hidden` layer with a `visibility: visible` container (the Tesserae pattern),
   separately and together. Identical.
5. `stack5-probe.html` - the layer appended to `<body>` by JS after load, as the app does.
   Identical.

So the trigger needs the real page. Bisect there.

### Resolved - the trailing inline flush

Root cause: `PaintLaidDomScrolled` painted the boxes in stacking bands and then flushed the
shaped inline items for the whole level *after* that loop, so a level's text landed above its
own positive z-index stacking contexts. `paint.rs` does the same thing, so this is a recorded
deviation (see "Known deviations" in `todo.md`). The flush now happens where the positive-z
band begins. A synthetic reduction does reproduce it - probes 1-5 above all kept the base text
in a *positioned* box, which lands in the same band as the layer and so orders correctly;
`PaintStackingOrderTests` uses a static one.

The `#/spaces/connect-apps` list is **not** this defect and is still open. The cards are
clipped away, not mispainted: `.tss-stack` above them is 92px high in Obscura against 758px in
Chromium, and the collapse propagates down to `.msk-connect-apps-grid`, which ends up 8px high
with `overflow: auto` over 1676px of content. That is an auto-height flex column not filling
its parent, i.e. a layout defect, not a paint one. Fixing the paint order does remove the page
text that was bleeding over that modal too.

## A1 - the slider thumb is never painted

`#/preferences?id=file-indexing-schedule`. The track paints; the thumb does not.
The thumb is a native pseudo-element:

    .tss-slider::-webkit-slider-thumb { -webkit-appearance: none; width: 16px; height: 24px;
        border-radius: 10px; background-color: ...; border: 2px solid ... }
    .tss-slider::-moz-range-thumb { ... }

Geometry is identical in both engines (`input.tss-fake.tss-slider` is 187x0, the fake track
divs match), so this is purely that the pseudo-element is not painted.

## A2 - `<input type="date">` has no UA content and collapses

`#/manage/operate/change-history`. Chromium renders `mm/dd/yyyy` plus a picker indicator in a
~140px box; Obscura paints an empty box. The container collapses with it:
`.tss-daterange-picker` is **324px in Chromium and 80px in Obscura**.

## A3 - a text input paints its placeholder instead of its value

`#/spaces/new`. The DOM value is correct in Obscura - `getComputedStyle` and the element agree:
`value: "My awesome space"`, `placeholder: "My Space"`, rect [487,429,538,64], font-size 24px,
identical to Chromium. Chromium paints the value; Obscura paints the grey placeholder.

Related, found earlier and still open: an unstyled checkbox paints nothing, because there is no
native control painter at all.

## C - already tracked, not new

GraphKit's canvas is 1202 wide in Chromium and 1217 in Obscura, and the available-apps grid
overflows its container, both the known 15px sidebar delta from the flex-shrink `calc()` defect
(a flex item that shrinks never re-resolves a `calc()` size). Tracked separately.

## S1 - `overflow: visible` on an `<svg>` is ignored (FIXED)

`svg-probe.html` case s9: an `<svg width=120 height=60 style="overflow:visible">` whose `<rect>`
extends past the viewport. Chromium paints **4800** red pixels (the overflow shows); Obscura
paints **900** (clipped to the svg box). The outermost `<svg>` gets `overflow: hidden` from the
UA sheet, and an author `overflow: visible` has to override it.

## S2 - a nested `<svg>` is not scaled or placed correctly (FIXED)

`svg-probe.html` case s10: `<svg width=200 height=100 viewBox="0 0 200 100">` containing
`<svg x=50 y=20 width=100 height=60 viewBox="0 0 10 6"><rect width=10 height=6/></svg>`.
Chromium paints **6000** red pixels, Obscura **60** - two orders of magnitude out, so the inner
viewport's scale is being dropped and the rect is drawn in the inner viewBox's user units
without the width/height mapping.

Eight other cases in the same probe are byte-identical between the engines and are NOT defects:
viewBox scaling up, no viewBox, `preserveAspectRatio` default and `none`, clipping of content
outside the viewBox, CSS-sized svg with a viewBox, a flat zero-value polyline, and percentage
width/height attributes.

## S3 - SVG `<filter>` / `feGaussianBlur` renders wrong

`imgsvg2-probe.html`, three `<img>` elements at 400x411 loading a 432x444 SVG. Non-white, red
and green pixel counts inside each case rect:

| case | content | Chromium | Obscura |
|---|---|---|---|
| j1 | `clip-path="url(#c0)"` around plain shapes | (164400, 105775, 14800) | (164400, 105774, 14800) |
| j2 | same plus `<g filter="url(#f1)">`, `feGaussianBlur stdDeviation=20`, `filterUnits="userSpaceOnUse"` | (132924, 105777, 14800) | **(123849, 105771, 14800)** |
| j3 | the real `no-results-light.svg` | (7825, 0, 0) | **(2088, 0, 0)** |

In j2 the red circle and green bar are pixel-identical, so shapes, `clip-path`, viewBox scaling
and SVG-in-`<img>` fitting are all correct. The ~9,000 pixel shortfall is entirely the blurred
rect. j3 is the same defect at scale: Obscura draws about a quarter of the illustration's ink.

This is what the user saw as "empty search message sizing is wrong" - on
`#/search?query=<no hits>` the "Nothing turned up" illustration is visibly cropped. The `<img>`
is 400x411 in both engines and every surrounding box matches, so it is purely what gets
rasterized inside.

Ruled out, do not re-derive: plain SVG-in-`<img>` scaling (`imgsvg-probe.html` fits a 432x444
SVG into 400x411 and 200x206, both exact), and `clip-path` on its own (j1).

## Not defects, checked and dismissed

- **Monaco** renders correctly on `#/manage/operate/code`: syntax colouring, line numbers, gutter
  and text all match. The only differences are the ~4px width delta from the known flex-shrink
  `calc()` defect and a missing gutter separator line.
- **The dashboard sparklines** on `#/` draw in both engines. Chromium's zero-value baselines are
  missing in Obscura and the sparkline is inset rather than full-bleed, which is worth a look but
  is not the SVG viewport defect.

### What S1 and S2 were

`SvgRenderer.RenderElement` treated a nested `<svg>` as a plain group, so it established no
viewport at all, and the raster pixmap was always exactly the CSS viewport, so an author
`overflow: visible` had nothing to override. Both are fixed; all ten cases now match Chromium
exactly, s10 in bounds as well as count. The one limit is recorded under "Known deviations" in
`todo.md`: an `overflow: visible` raster can only grow right and down, because `PaintDom` blits
it at the element's border-box origin.

The dashboard sparklines are a **separate** matter and are not these two defects. Cases s6 and
s7 are exactly that shape - a CSS-sized `<svg>` with `preserveAspectRatio="none"` and a
polyline, flat-zero included - and both already matched Chromium byte for byte before this
change. Whatever insets the sparkline on `#/` is upstream of the rasterizer.

## S3 - SVG `<filter>` is not applied, and a `clipPath` drops its shapes' transforms (FIXED)

`imgsvg2-probe.html` case j2 (`<g filter="url(#f1)">` + `feGaussianBlur stdDeviation="20"`,
`filterUnits="userSpaceOnUse"`) painted the rect sharp: `RenderElement` skipped the `<filter>`
*element* as a definition but never read the `filter` *attribute*. Pixels differing from
Chromium by more than 8: **11,374 before, 67 after**.

Case j3 - the real `no-results-light.svg` - was a different defect that happened to travel with
it. Its artboard clip is `<clipPath id="clip0_398_1060"><rect width="392" height="203"
transform="translate(20 121)"/></clipPath>`, and the clip builder called `ShapePath(shape)`
without the shape's own `transform`, so the clip landed at the origin and cut the illustration
to its top 203 rows of 444. Chromium-diff pixels: **23,264 before, 1,298 after** (the remainder
is blur-kernel and anti-aliasing spread, which is expected - see the pixel-tolerance note in
`PaintTests`).

## I1 - the intrinsic sizing keywords are ignored on `min-*` and `max-*`

`min-content` / `max-content` / `fit-content` are implemented for `width` and `height`, which route
through `StylePrimitives.IntrinsicSizeKeywordValue`. `min-width`, `min-height`, `max-width` and
`max-height` instead go through `DimensionValue`, which has no keyword path, so the declaration
computes to `auto` and is silently dropped (`ComputedStyle.cs:1325-1351`).

Measured with `intrinsic-probe.html` (a 300px container, monospace text whose min-content width is
one 8-character word and whose max-content width is the whole string). **13 of 22 cases differ:**

| case | Chromium | Obscura |
|---|---|---|
| `min-width: max-content` | 510.55 x 20 | 300 x 40 |
| `max-width: min-content` | 77.06 x 120 | 300 x 40 |
| `min-height: min-content` (block, height 40) | 120 x **120** | 120 x **40** |
| `min-height: max-content` | 120 x 120 | 120 x 40 |
| `min-height: fit-content` | 120 x 120 | 120 x 40 |
| `max-height: min-content` (block, height 400) | 120 x **120** | 120 x **400** |
| `max-height: max-content` | 120 x 120 | 120 x 400 |
| flex column, shrinking, `min-height: min-content` | 120 x **120** | 120 x **60** |
| flex column, shrinking, `min-height: max-content` | 120 x 120 | 120 x 60 |
| flex row, `min-width: max-content` | 510.55 x 120 | 300 x 120 |
| flex row, `max-width: min-content` | 77.06 x 120 | 300 x 120 |
| grid item, `min-height: min-content` | 120 x **120** | 120 x **60** |
| grid item, `min-width: max-content` | 510.55 x 120 | 300 x 120 |

The nine that match do so because the keyword happens to agree with the default, or because they
are the already-working `width`/`height` forms - `min-width: min-content` and
`max-width: max-content` are vacuous on a box that already fills its container. A first version of
this probe reported only 6 failures for exactly that reason; the height cases now put content
taller than the container so the keyword has to do work.

`width: max-content` at 509 against Chromium's 510.55 is the known text-measurement delta, not a
keyword failure.

This is what leaves the connect-apps grid at its scroll parent's 666px where Chromium expands it
to 1677px.
