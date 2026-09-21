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

## F31 - a flex item that is shrinking is shrunk twice when it has a percentage-sized child

**Symptom (user-reported).** The app sidebar is a different width in the two engines on
81 of 143 routes. On `#/chat-ai` the root `.msk-app-sidebar-default` is 211.98 in
Chromium and 184 in Obscura, and every box below it inherits the 28px shortfall.

**Not a text-measurement problem.** The widest labels inside the sidebar measure within
0.5px of each other (Curiosity 108.56 vs 109, Ctrl+Shift+O 94.38 vs 95, ...).

**Not a flex-base problem.** With `flex-shrink: 0` forced on every item of the row, both
engines report exactly the same base sizes: `[0, 250, 8, 1440]` in a 1440px row.
`.tss-sidebar` carries a definite `width: 250px` in tss.css.

**Not the shrink algorithm in isolation.** A synthetic three-item row with those exact
numbers matches Chromium to the rounding (212.016 / 6.781 / 1221.203 vs 212 / 7 / 1221),
and so do variants with unequal shrink factors, padding + border-box, and a content-driven
third item.

### Minimal repro

```html
<div style="display:flex;width:1440px">
  <div style="width:250px;flex:0 1 auto"><div style="width:100%"></div></div>
  <div style="width:8px;flex:0 1 auto"></div>
  <div style="width:100%;min-width:0;flex:0 1 auto"></div>
</div>
```

| | item 1 | item 2 | item 3 |
|---|---|---|---|
| Chromium | 212.016 | 6.781 | 1221.203 |
| Obscura | **184** | 7 | **1249** |

Remove the `width:100%` child and Obscura gives 212. Replace it with `width:100px` or
`height:50%` and Obscura gives 212. Any percentage *width* on the child reproduces it
(`100%`, `50%`, `calc(100% + 24px)`, `calc(100% - 24px)`); a percentage *padding* or
*margin* does not.

### What the numbers say it is doing

Spec (CSS Flexbox 9.7): scaled flex shrink factor = flex-shrink x **inner** flex base
size; the deficit is computed on outer sizes. For the repro: inner bases 250 / 8 / 1440,
outer total 1698, deficit 258, so item 1 loses `250/1698 * 258 = 38.0` and lands on
**212.0** - which is what Chromium produces.

Obscura's output is exactly what you get by running that same computation **twice**, with
the second pass taking the item's flex base size from the first pass's *resolved* width
instead of from its specified `width`:

```
pass 1: base 250 -> 250 - 250/1698*258      = 212.0
pass 2: base 212 -> 212 - 212/1660*220      = 183.9   (observed 184)
        item 3   -> 1440 - 1440/1660*220    = 1249.2  (observed 1249)
```

The model reproduces every measurement taken, in the app and in the synthetic page, at
five different flex-basis values for item 3 (1200 -> 244, 1300 -> 216, 1400 -> 192,
1440 -> 184, all predicted to within the integer rounding), and with padding +
`box-sizing: border-box` it predicts 189 where Obscura gives 189 and Chromium 215.172.

So the percentage child makes the item's layout re-run, and the re-run re-derives the
flex base size from the used size rather than from the style. A flex item's flex base
size must come from `flex-basis`/`width` on every pass; it is not an output of the
previous pass.

## F32 - a form control with a percentage width contributes nothing to intrinsic sizing

**Symptom.** After F31 the remaining width divergence concentrates in the admin pages:
`#/manage/operate/usage` 42% of aligned pairs, announcement 41%, packages 39%, migrations
37%, llm-usage 35%, user-analytics 36%, queries 34%, code 34%, profiling 35%. On every one
of them the admin sidebar is **214.42 in Chromium and 205 in Obscura**, and the whole
subtree inherits the 9.4px.

**Here Obscura matches the naive spec result and Chromium does not.** Flex bases are
identical in both engines (`[72, 250, 8, 1440]` with `flex-shrink: 0` forced), container
1440, deficit 330, sidenav frozen at `flex: 0 0 auto`. Inner bases 226 / 8 / 1440, so the
sidebar loses `226/1674 * 330 = 44.55` and lands on **205.45** - which is Obscura's answer.
Chromium stops at 214.42 because the sidebar's `min-width: auto` resolves to a
**content-based minimum size** of 214.42 and clamps the shrink there. Emptying the sidebar
in Chromium drops it to 205.42, confirming the floor is its content.

**What makes up that floor.** 214.42 = 190.422 + 24 (the sidebar's padding).

**CORRECTION (verified by mutation, not arithmetic).** The 190.422 is *not* the search
input. Hiding each `.tss-sidebar-middle` child in Chromium one at a time and re-reading the
sidebar shows exactly one that moves it: the `File Processing Queue` nav button, whose
`white-space: nowrap` label makes its min-content 190.422. Hiding the search box changes
nothing, because its `width: 100%` gives it a min-content contribution of 10 in Chromium
too (its max-content contribution is 182 - the two passes differ). Setting the search box
to `width: auto` raises the sidebar to 216, which is what made the arithmetic look right.

The nav buttons carry `width: 100%` from `.tss-sidebar-btn-open .tss-sidebar-btn`, and a
percentage-width descendant is invisible to this engine's flex automatic minimum size - see
"a flex item's automatic minimum size ignores a percentage-width descendant" in todo.md.
That is a third defect, in `DeferCyclicFlexInlineSizes`, and it is what the sidebar waits
on. The two input defects below are real and are fixed; they do not move the sidebar.

### The defect

`.tss-searchbox` and `.tss-textbox` both carry **`width: 100%`** - every Tesserae text
input does. A percentage that cannot be resolved behaves as `auto` for intrinsic
contribution (CSS Sizing 3 §5.2.2), and `width: auto` on a text control gives the
size-based intrinsic width. Measured in the live app, same font (Plus Jakarta Sans 13px),
by cloning the element into a `width: max-content` wrapper:

| | Chromium | Obscura |
|---|---|---|
| the app's own `.tss-searchbox` (no `size` attribute) | **182** | **10** |
| `<input style="width:100%">` | 180 | **8** |
| `<input size="20">` | 180 | 173 |

Obscura falls back to **zero** and reports only the 5+5 padding. With `size` present and no
percentage width it gets the intrinsic width roughly right, so the fallback path is the bug,
not the metric.

This is the same shape as the already-recorded deviation *"a cyclic functional inline size
neutralized to `0px` instead of `auto`"*, one layer over: there the neutralized value was a
`calc()`, here it is a plain percentage, and the box is a form control whose `auto` width is
not zero.

### Second, smaller: the size-based intrinsic width is short

Sweeping `size` in the app's font, `width: max-content`, no percentage:

| `size` | 1 | 2 | 5 | 10 | 20 | 30 | 50 |
|---|---|---|---|---|---|---|---|
| Chromium | 28 | 36 | 60 | 100 | 180 | 260 | 420 |
| Obscura | 25 | 32 | 56 | 95 | 173 | 251 | 407 |

Chromium is exactly `8.0 * size + 20`; Obscura is `7.80 * size + 17.2`. The 8.0 is the
font's OS/2 `xAvgCharWidth` (the `0` advance in this face is 9.52 and `x` is 6.33, so it is
neither), and the +20 is the control chrome. Both terms are slightly low in Obscura.

**Resolved.** Chromium's formula, recovered exactly on four faces at font sizes 10-20, is
`ceil(charWidth * size + max(0, round(maxCharWidth) - charWidth))` with
`charWidth = max(avg, round(avg))`, `avg` the OS/2 `xAvgCharWidth` scaled to the used size
and `maxCharWidth` the `head` bounding box's width. A textarea is
`ceil(charWidth * cols) + 15`, the 15 being the scrollbar gutter. Both are implemented; see
todo.md. The residual on a control that names no `font-family` is a font difference, not a
formula one - Chromium substitutes a system Arial whose bounding box is ~8px wider at 13px
than the Liberation Sans this engine embeds.

**Ruled out.** The automatic minimum size itself is implemented correctly - a seven-case
probe (unbreakable label, `overflow: hidden`, explicit `min-width: 0`, fixed-width child,
replaced image, padded border-box, empty) matches Chromium on every one. A span measured
inside an offscreen absolutely-positioned host reported 0 in one probe run; re-tested
standalone it is 72.3 in both engines, so that was a probe artifact, not a defect.

## F33 - an atomic inline is squeezed into its line instead of overflowing it (FIXED)

**Symptom (user-reported).** The Tesserae Dropdown is ~6px narrower than in Chromium. It is
the widest-spread remaining width defect in the parity survey: 309 divergent element pairs
across 32 routes, deltas clustering at -3, -6 and -7. On `#/`, `div.tss-dropdown` is 123.7 in
Chromium and 118 here; its button is 113.7 against 108; the leaves agree (24 / 83.7).

**The dropdown is not `width: auto`.** `.tss-dropdown` in tss.css carries
`width: calc(100% - 16px)`, which nothing in the subtree dump shows because
`getComputedStyle` reports the used value. Its parent `.tss-dropdown-container` is a
content-sized flex item of `div.tss-stack.msk-home-view-selectors`, so the percentage is
cyclic:

- intrinsic pass: the percentage behaves as `auto` (CSS Sizing 3 5.2.2), the container
  shrink-wraps the dropdown's max-content 117.7 plus its 22px margin plus 2px border = 141.7;
- layout pass: 100% is now the container's definite 139.7 content box, so the dropdown is
  139.7 - 16 = **123.7** and overflows the container by the margin it was measured with.

Both numbers are Chromium's, and 141.7/123.7 is not a contradiction - it is what a cyclic
percentage does.

**Root cause, and it is much wider than the dropdown.** The inline formatting context is
modelled as a wrapping row flex container, so an atomic inline is a flex item at taffy's
default `flex-shrink: 1` and gets squeezed into the line. Reduced to a 98px content box:

| | Chromium | Obscura (before) |
|---|---|---|
| `inline-block` / `inline-flex` / `inline-grid` / `inline-table` `width: 200px` | 200 | **98** |
| the same plus `margin-right: 22px` | 200 | **76** |
| `width: calc(100% + 40px)` | 138 | **98** |
| `width: 150%` | 147 | **98** |
| `width: 50px` (fits) | 50 | 50 |
| `min-width: 200px` (no width) | 200 | 200 |

`min-width` surviving is the tell: the item was shrinking and stopping at its minimum.

**Fix.** `DomBuild.BuildAny` zeroes `flex-shrink` on an in-flow inline-level box whose width
is definite. See "Known deviations" in todo.md and `AtomicInlineSizingTests`.

**Residual, since fixed.** A bare cyclic `width: 100%` on an atomic inline was still clamped,
because `DeferCyclicFlexInlineSizes` had already rewritten it to `Auto` by the time the box was
built, so `BuildAny` read the declaration as `auto` and left the box shrink-wrapping at taffy's
default `flex-shrink: 1`. The `calc()` spelling was unaffected only because its
`SizeExpressions[0]` survives the same neutralization - the two spellings of the same CSS behaved
differently. `LayoutStyle.DeferredCyclicInlineSlots` now records that the declaration is still
definite, and both of `BuildAny`'s readings of it - `pinsInlineSize` and the `needsOuter`
shrink-wrapping participant an `inline-flex`/`inline-grid` gets - consult it. On the reduction
(`width: 100%; margin-right: 22px` on an `inline-flex` in a shrink-wrapping flex item) the
dropdown goes from 117 to **139**, which is Chromium, and the container stays at 141. See
`AtomicInlineSizingTests.ABareCyclicPercentageInlineSizeIsStillDefinite`.

## F34 - a classic scrollbar takes no space out of its scroll container (FIXED)

**Symptom.** A settings row was 9px too wide and every control laid out from its right edge
moved with it. On `#/manage/search/settings`, **403 of 870 strictly-aligned pairs had exactly
+9px of x offset and nothing else wrong** - correct widths, correct y, correct heights.

**Cause.** Headless Chromium driven over CDP draws **classic (non-overlay) scrollbars**, which
occupy layout space, and `tss.css` sets `::-webkit-scrollbar { width: 9px; height: 9px }`
page-wide. The pane that holds the row is `overflow: auto` with 1100px of content in a 567px
box, so Chromium's border box is 1037.22 and its **client box 1028**. Obscura reserved nothing
and gave the children the full 1037.

### The methodology trap, which is the more important half

**Playwright launches every browser with `--hide-scrollbars`.** A reference measured through
`chromium.launch()` therefore reports *zero* reservation, while the survey reference
(`out-chrR3`) was captured against a hand-launched `--headless=new` Chromium, which reserves.
The two disagree, and the probe is the one that lies. Verified directly on `sbar-probe.html`,
same page, same binary, same moment:

| case | via `chromium.launch()` | hand-launched `--headless=new` |
|---|---|---|
| `overflow: auto`, vertical overflow | client 400 | client **393** (7px UA default) |
| `+ ::-webkit-scrollbar { width: 9px }` | client 400 | client **391** |
| `overflow: scroll`, not overflowing | client 400 | client **391** |
| `overflow: hidden` / no overflow / `sb0` | client 400 | client 400 |

So **any probe comparison taken through `runprobe.js chr` is only valid where scrollbars are
not involved.** When a box is a scroll container, launch the reference by hand
(`/opt/pw-browsers/chromium --headless=new --remote-debugging-port=N`) and connect over CDP,
the way the survey does.

This also invalidated an existing test:
`AStableScrollbarGutterIsReservedOnANestedScrollContainer` asserted that `overflow-y: scroll`
with no `scrollbar-gutter` reserves nothing (300px). That is true only under
`--hide-scrollbars`; the reference browser gives 290 (`scrollbar-width: thin`) and 285
(classic). Corrected at the assertion.

### Two judgement calls worth keeping

- **An `auto` axis counts as overflowing at more than 1px, not at any positive amount.** This
  engine's text metrics differ from Chromium's by a fraction of a pixel per line, and an exact
  test invents scrollbars - and 9-15px of width error - out of that noise.
- **The pass runs after every intrinsic/table/fragmentation repair, not after the first
  layout.** Running it earlier was tried: the app's `.tss-segmentedpivot-content` panes
  overflow by a few px provisionally and then do not, so they grew scrollbars Chromium never
  shows and the whole chain came out 15px narrow.

**Still open:** the *document's* own scrollbar does not come out of the initial containing
block unless `scrollbar-gutter` asks for it, and `--hide-scrollbars` is not a flag this engine
reads - worth considering, since Playwright always sends it. taffy also carries one
`ScrollbarWidth` for both axes, so a `::-webkit-scrollbar` setting different `width` and
`height` reserves the larger on both.

## F35 - the scrollbar-gutter relayout does not re-resolve calc() widths below it (FIXED)

**Found on the Tesserae sample app**, `#/view/Searchable List` and
`#/view/Searchable Grouped List`: **4,332 of 6,001 strictly-aligned pairs differ in width,
4,217 of them by exactly +9px.** It is the same +9 as F34 and it survived that fix.

The chain (both engines agree above the card, and disagree at it):

| | Chromium | Obscura |
|---|---|---|
| `div.tss-card-container` (`padding: 2px`) | 1117 | **1117** |
| `div.tss-card` (`width: calc(100% - 4px)`) | **1109** | **1118** |

Chromium: container content box 1117 - 4 = 1113, card = 1113 - 4 = **1109**. Correct.

Obscura's 1118 is what that calc gives against a **1126** container - which is the container's
width *before* the scrollbar gutter was reserved. On a page that does not scroll
(`#/view/Menu`) the container is 1126 in both engines and the card is 1118 in both, so the
card's sizing is right; it is the re-resolution after the gutter pass that is missing.

So F34's pass shrinks the scroll container and relayouts, but a descendant whose width is a
`calc()` keeps the value it resolved against the pre-gutter containing block. The workspace
survey could not surface this: there the gutter landed at a level with no percentage-sized
descendants.

**Cause, narrowed.** It is only the *functional* sizes, not percentages in general. A bare
`width: 100%` reaches taffy as a typed percentage and taffy resolves it against the used
containing block on every layout, so it follows the gutter by itself. A `calc()` under a cyclic
flex item does not: `DomSubgridPasses.ResolveFunctionalInlineSizes` flattens it to a px length
off the parent's content box, and that pass runs before the gutter is reserved. The minimal
reduction is four divs - `display:flex` row > `flex:1 1 auto` item > `overflow-y:auto` box >
`padding:2px` container > `width:calc(100% - 4px)` card - and it separates the two cleanly:

| box | Chromium | Obscura before | Obscura after |
|---|---|---|---|
| `calc(100% - 4px)` card | 383 | **392** | 383 |
| `width: 100%` sibling | 387 | 387 | 387 |
| same card, `overflow:hidden` control | 392 | 392 | 392 |

**Fix.** `DomSubgridPasses.ReresolveFunctionalInlineSizes` re-runs that resolution against the
geometry the tree has after the gutter relayout, from the loop in `LayoutDomOnce`. An entry
whose slot already holds the length it would write is skipped and a rank group that wrote
nothing does not reflow, so a page that reserves nothing pays one list walk. The loop went from
two iterations to three because the re-resolution feeds back into what overflows; it still
terminates because a reservation only grows and is capped at the box's scrollbar thickness.
Instrumented over 15 representative routes (200 layouts) the first round ran every time, the
second in 38 of 200, and the third never - the extra slot is headroom, not a working iteration.

**Cost, measured.** 101 Tesserae routes at 1440x950, Chromium as reference, strictly-aligned
pairs only, the same capture path before and after:

| | before | after |
|---|---|---|
| mean abs width error | 5.6680 | **5.0704** |
| ... excluding the unrelated Masonry outlier | 4.8793 | **4.2760** |
| pairs differing at all | 26,866 | **13,143** |
| pairs off by more than 2px | 16,430 | **2,953** |
| pairs at exactly +9px | 13,629 | **449** |

75 routes improved, 22 unchanged, 4 regressed. The two reported routes carry most of it:
`#/view/Searchable List` 6.5742 -> 0.2398 and `#/view/Searchable Grouped List` 6.5974 -> 0.2877,
their 8,337 +9px pairs down to 10. `#/view/Menu`, the non-scrolling control, is byte-identical
before and after, with `.tss-card` at 1118 in both engines.

**The four regressed routes are an existing defect made more visible, not a new one.** Time
Picker, Stepper, Mark Highlighter and Navbar each have a `.tss-stack` that Chromium leaves at
1126 and Obscura reserves a gutter out of at 1117 - Obscura calling an `auto` axis overflowing
where Chromium does not. That -9 was already there before this change (17, 17, 14 and 12 pairs);
correctly re-resolving the calc() descendants now carries the wrong container width down to them
too (63, 61, 48 and 37 pairs), for +0.13 to +0.25 mean abs each. The fix is faithful; the
container width it resolves against is what is wrong, and that belongs to F34's overflow
tolerance.

**Residual, reduced and not fixed here.** `PinFlexItems` pins a cyclic flex item to its *used*
width as a definite length, and that pin is taken before the gutter as well. When the row flex
container is **inside** the scroll container rather than above it, the item keeps its pre-gutter
width and everything below it follows:

| box (row flex inside an `overflow-y: auto` box) | Chromium | Obscura |
|---|---|---|
| `flex: 1 1 auto` item | 391 | **400** |
| `calc(100% - 4px)` card under it | 383 | **392** |

Un-pinning and re-pinning means re-running the whole deferred cyclic resolution after the
gutter, which is the ordering F34 warns about. **Fixed instead by carrying the pin.**
`PinFlexItems` now records each pin's provenance - the flex container it was taken against and
that container's content width at the time - as a `PinnedFlexItem`, and
`RescalePinnedFlexItems` scales the frozen length with the container from inside the gutter
loop, outermost-first so a nested pin sees its container's new width. Scaling rather than
re-deriving is deliberate: the item's flex factors were frozen on purpose, because re-running
the flex algorithm off an already-flexed size flexes it twice. It is exact for the one-item and
equal-factor rows this reaches and an approximation for a row of unequal bases, and it costs one
list walk on a page that reserves nothing. The reduction above is now 391 / 383, both Chromium's.
See `DomLayoutTests.APinnedCyclicFlexItemFollowsAReservedScrollbarGutter`.

See "Known deviations" in todo.md and
`DomLayoutTests.AReservedScrollbarReResolvesFunctionalWidthsBelowIt`.

## F36 - the Masonry component renders nothing

`#/view/Masonry` on the Tesserae sample app. `div.tss-masonry` is **1084 x 7340** in Chromium
and **1084 x 0** in Obscura, and every one of its 50 `position: absolute`
`.tss-masonry-item` children is `[0, 0, 0, 0]` - not merely mis-sized, entirely unplaced.
Chromium lays them out at x 301, width 1084, heights 80-200, stacked down to y 7632.

Everything above the masonry container matches to the pixel, so the page is otherwise fine.
Masonry positions its items absolutely from JavaScript after measuring them, so the first
thing to establish is whether that code ran and what it measured.

## F37 - a chart's SVG children collapse to zero (FIXED)

`#/view/Charts`. **366 SVG elements are `[0, 0, 0, 0]` in Obscura** where Chromium gives them
real geometry - 143 `circle`, 116 `text`, 44 `rect`, 43 `line`, 13 `path`, 7 `g`, including a
`g` that Chromium lays out at `[342, 467, 1009, 141]` and a `line` at `[345, 649, 1003, 0]`.

The telling statistic: on that route there are **zero** divergences that are not a collapse to
zero. Every chart element is either exactly right or completely absent, which points at a
whole subtree never being laid out rather than at a sizing rule.

**Nothing separated the collapsed elements from the working ones, because there were no
working ones.** Across the whole 101-route capture, 0 of 451 SVG descendants (`circle`,
`rect`, `line`, `path`, `text`, `g`, `polyline`, `polygon`) had any geometry, while every
`<svg>` root itself was sized correctly. An inline `<svg>` is an atomic replaced box, so its
children never become taffy nodes and `DomLayout.Rects` has no entry for them;
`op_layout_geometry` then returns the empty string and `getBoundingClientRect()` answers all
zeros. The Rust engine is in the same position and does not care - it hands the subtree to
resvg as one raster - so there was no reference behaviour to port, only Chromium's to
reproduce.

Fixed by `SvgBoxes`, a post-layout pass that resolves each SVG element's object bounding box
through the viewport and `transform` chain into `DomLayout.SvgRects`, which
`PreparedRender.DocumentRect` / `FragmentSource` fall back to. Reduced to `svgbox-probe.html`
(26 elements: shapes, anchored text, a transformed group, a clipped group, an empty group, a
nested viewport, `defs` / `clipPath` / `display:none`), where all 26 now match Chromium within
0.5px. See "An SVG shape answers `getBoundingClientRect()`" under Known deviations in
`todo.md` for what the walk does and does not model.

On the route: **410 collapsed elements -> 0**. What is left is 36 divergences of up to 8px,
and they are all one pre-existing, unrelated defect: the seventh chart's `<svg>` is
`[317, 3315, 1043, 200]` here against Chromium's `...192`. Its parent `div.tss-chart` is 200
tall with an 8px top offset, so `height="100%"` has to resolve against the 192px content box;
resolving it against 200 makes the `preserveAspectRatio="none"` y-scale 1.0 instead of 0.96
and drags all 36 of that chart's children with it. That is a percentage-height defect, not an
SVG one.

## F38 - the Code Diff view, two separate defects

`#/view/Code Diff`, 990 of 2,972 aligned pairs. Two distinct things:

- **237 elements collapse to zero** - 225 `span` (`d2h-code-line-prefix`,
  `d2h-code-line-ctn hljs markdown`) and 12 `path`. One line container is
  `[423, 3955, 2117, 18]` in Chromium and `[502, 29798, 0, 18]` here: zero width and a y
  seven times further down, so the diff table's line boxes are laid out on a completely
  different geometry.
- **753 sizing divergences**, led by a `textarea.tss-textarea` 238 -> 257 (+19), its
  containers +19, and `div.tss-codediff` 821 -> 889 (+68).

### Decomposed (from the d9-d16 chain, both engines index-aligned)

It is three defects, and the third is the one that matters:

1. **`textarea.tss-textarea` is 19px too wide** - 238 -> 257. **[Corrected below: this is not
   an intrinsic-width defect and not independent. The textarea is `width: 100%`, and its column
   and the codediff column are the two items of one 1075px flex row with the textarea column at
   `flex: 1 1 auto`, so both this +19 and the +68 on `div.tss-codediff` are two readings of the
   single quantity the diff table's width sets. Probed on its own the control measures 181 /
   201 / 201 in both engines.]**
2. **`div.tss-codediff` is 68px too wide** - 821 -> 889, x 555 -> 574, and the whole
   `d2h-*` tree under it inherits both.
3. **The diff table does not get its max-content width, so every code line wraps.**
   `table.d2h-diff-table` is **2535 wide x 267 tall** in Chromium and **1073 x 1878** here; a
   second one is 1212 x 164 against 1073 x 778. Its `div.d2h-file-side-diff` parent is
   `overflow: scroll hidden`, i.e. horizontally scrollable, so the table should size to its
   content and overflow. **[Corrected below: the scrollable ancestor is irrelevant. The table
   is `width: 100%`, and Chromium's 2535 is CSS 2.1 17.5.2's min-content floor - the used width
   is the greater of the specified width and what the columns need. See "Resolved".]**
   Obscura clamps it to the container's 1073 and the lines wrap, which
   is what produces the 225 collapsed `d2h-code-line-*` spans, the `d2h-code-wrapper` heights
   of 13,920 and 14,794 against Chromium's 836, and the line container at y 29,798 against
   y 3,955.

(3) subsumes most of the route's 990 divergent pairs and is the one to fix first.

### Resolved

(3) was three things stacked, and (1) and (2) were downstream of it, not separate defects.

- **A percentage-width table had no min-content floor.** The reference skips such a table in
  the table used-width pass and lets taffy resolve the percentage; CSS 2.1 17.5.2 makes the
  used width the greater of the specified width and what the columns need. `ApplyTableUsedWidths`
  now floors the box with the table's min-content width.
- **The table's intrinsic measurements ran through neutralized percentages.**
  `DeferCyclicFlexInlineSizes` flattens a cyclic percentage inline size to `0px` before the box
  tree is built, so the `width: 100%` spans holding the code text read as zero-wide while the
  table's min-content was measured - the table reported the width of the line-number column
  alone, and its own `width: 100%` reached the pass as `width: 0`. The restore-measure-undo
  `ApplyDeferredFlexAutomaticMinimums` already had is now
  `DomSubgridPasses.EnterTypedPercentageScope` / `ExitTypedPercentageScope`, and the table pass
  measures inside one.
- **A definite-width inline-block shrank its block children.** Taffy's inline-box stand-in is a
  wrapping flex row, and flexbox's automatic minimum size is the content size suggestion - zero
  for a box carrying its own width - so a `width: 461px` child of a `width: 100%` inline-block
  came out at the parent's width. A definite-width inline-block whose in-flow children are all
  block-level now gets real block layout.

On the route: `table.d2h-diff-table` 1073 x 1878 -> **2540 x 267** (Chromium 2534.53 x 267.31),
the second 1073 x 778 -> 1221 x 198 (1211.83 x 164.34), the side-by-side pair 541 x 13,920 /
541 x 14,794 -> 541 x 819 / 541 x 782 (537 x 836), collapsed `d2h-code-line-*` spans 225 -> 135.

(1) is not an intrinsic-width defect at all: `textarea.tss-textarea` is `width: 100%` inside
`.tss-textarea-container`, and probing the control on its own gives 181 / 201 / 201 against
Chromium's 181 / 201 / 201. It and (2) are both the same flex row - the textarea column is
`flex: 1 1 auto` and fills what the `.tss-codediff` column leaves, so both numbers are set by
the first diff table's width. That table is now 792 against Chromium's 818.7 (was 887), and the
textarea follows at 274 against 246.31 (was 257). What is left there is one residual: the first
table's max-content is ~27px short, which is not the clamp this finding was about.

Swept over the 101 Tesserae routes against `out-tssChrL`: 98 comparable, **4 improved, 0 worse**;
geometry divergences over 2px 18,933 -> 14,634, mean absolute box error 46.25 -> 17.04.

## F39 - Masonry never lays out: the mount callback's layout pass is killed by the task watchdog

The title this finding was filed under - "MutationObserver callbacks are delivered an order of
magnitude late" - is wrong, and the measurement that produced it was timing the wrong thing.
`MutationObserver` delivery was measured directly and it is on the microtask checkpoint, in both
engines. What is actually broken is the `Masonry.layout()` pass that the delivered callback goes
on to run: it exceeds the 5.5s autonomous task budget and V8 is terminated inside it.

### Where the notify set is drained

`bootstrap.js`'s `MutationObserver._notify` queues a promise job; the host drains it in
`ObscuraJsRuntime.PumpTick`, which calls `PerformMicrotaskCheckpoint()` at the top of the turn,
after every posted task and after every timer callback (`ObscuraJsRuntime.EventLoop.cs`). That is
the placement DOM 4.3.4 and the HTML event loop ask for. An isolated probe (a `MutationObserver`
on `document.body`, one `appendChild`, timestamps against a `queueMicrotask` scheduled just before
it) shows the callback in the **same checkpoint** as the microtask and ahead of `setTimeout(0)` and
`requestAnimationFrame`, in Obscura exactly as in Chromium.

### What actually happens on `#/view/Masonry`

Instrumenting `Masonry.prototype.layout` on both engines (wrap, log entry and exit):

| | | |
|---|---|---|
| 1st `layout()` - Outlayer's init layout, element still detached | in at 2.1s, out at 2.7s | 0 items, **620ms** |
| 2nd `layout()` - from `DomObserver.WhenMounted` -> rAF -> `setTimeout(16)` | in at 3.8s, **never returns** | 50 items, container measures 1084 |

The mount callback fires. The record reaches it. The layout it runs is entered with the right
geometry and is then terminated part-way through: one
`autonomous browser task exceeded its task budget` lands in the server log per navigation, exactly
at that point. The termination is a V8 `TerminateExecution`, so it unwinds through JavaScript
without running `catch` blocks - the wrapper's `try` around the call never sees it. Masonry is left
carrying the `left: NaN%; top: Infinitypx` it computed in its **first**, zero-width layout, so the
container stays 0 forever and nothing schedules another pass. Calling `Masonry.data(el).layout()`
by hand afterwards produces the correct 7340 immediately.

That is also the answer to the third row of the original table. "No observer attached" does not
hang because delivery needs an observer to drive it; it hangs because the one layout pass the page
gets is killed and nothing else pokes the DOM afterwards. Attaching a page-level observer adds
mutation traffic, which gives Tesserae further mount callbacks and therefore further chances for a
`layout()` to land in a cheaper moment - which is what the "~8.8s" reading was.

### Root cause: a forced geometry read after any style write re-lays out the whole document

`thrash.html` - 2059 nodes, 50 items, alternating one inline-style write with one `offsetWidth`
read:

| | Obscura | Chromium |
|---|---|---|
| 50x read-only `offsetWidth` | 130ms | 29.3ms |
| 50x (write `opacity`, read `offsetWidth`) | 3353ms | 0.3ms |
| 50x (write `left`/`top`, read `offsetWidth`) | 3122ms | 4.0ms |
| 50x (write `class`, read `offsetWidth`) | 3068ms | 0.1ms |
| 50x write `left`/`top`, **no** read | 3ms | 0.1ms |
| one read after that batch | 106ms | 0.3ms |

Every write-then-read pair costs one whole-document prepare (~65ms here), and it scales linearly
with document size - 20 pairs cost 249ms at 230 nodes, 650ms at 1030, 2485ms at 4030, i.e. ~25us
per node per forced read. `RenderState.EnsurePreparedRender` rebuilds whenever
`PendingStyleMutations` is non-empty; the retained path
(`PaintApi.PrepareDomWithRetainedStylesWithAnimationState`) retains the **style** maps and then
runs `PrepareInternal`, which re-lays the whole tree. There is no dirty-subtree layout
invalidation. Masonry interleaves ~50 such pairs, so one `layout()` costs 3.3s when the page is
quiet and more than the 5.5s budget during boot.

This is a port *speed* gap, not a logic deviation: `crates/obscura-render` has the same
retained-style / full-relayout split, and `run_autonomous_event_loop_turn` in
`crates/obscura-js/src/runtime.rs` arms the same
`SYNCHRONOUS_TASK_FLOOR_MS + WATCHDOG_SCHEDULING_MARGIN_MS` around the same batch. At the Rust
engine's speed 50 forced relayouts fit inside the budget; at the port's they do not.

**Scoped proposal (not done here):** incremental layout invalidation - carry a dirty set on
`PreparedRender` so a retained restyle of one node re-runs layout for its formatting context
rather than the document. Until that exists, any component that interleaves style writes with
geometry reads over tens of elements is at risk of the same termination, and raising the watchdog
budget only moves the threshold.

### Fixed here: the MutationObserver shim's notify set and registration rules

Measuring the above turned up three real spec divergences in the shared shim, all of them now
fixed in `dotnet/src/Obscura.Js/js/bootstrap.js` - the port owns its copy now, so this is a deviation from the Rust shim and is recorded in `todo.md` (`mo2probe.html`, expected/Obscura-before/after):

| case | Chromium | before | after |
|---|---|---|---|
| one observer watching two nodes, mutate one | 1 record | **2 records** | 1 record |
| `disconnect()` after the mutation, before the checkpoint | 0 callbacks | **1 callback** | 0 callbacks |
| `observe(el, { attributeFilter: ['data-x'] })` | 1 record | **0 records** | 1 record |

- `observe()` pushed `this` onto `globalThis.__mutationObservers` on **every** call, so an
  observer watching N nodes was listed N times and `__notifyMutation` delivered each record N
  times. It is now listed once, and re-observing a node replaces that registration's options.
- `disconnect()` removed one entry and left the record queue alone. It now removes every entry,
  empties the queue and drops the observer from the notify set.
- `attributeOldValue` / `attributeFilter` now imply `attributes` and `characterDataOldValue`
  implies `characterData` (observe() steps 3-4), and `attributeFilter` actually filters.
- The notify set is drained by **one** microtask per checkpoint rather than one per mutation.
  Delivery was already correctly batched - the first job spliced every record and the rest found
  an empty queue - so this is shape and allocation, not behaviour: 20,000 mutations under an
  observer queued 20,000 promise jobs to deliver one callback.

Pinned by `Obscura.Js.Tests/MutationObserverTests.cs` (7 facts). Cost: interleaved A/B of the two
builds on `#/view/Button`, five runs each, 8418ms vs 8431ms (0.15%, noise floor ~10%); a
20,000-mutation churn page is likewise unchanged. The three `#/view/Masonry` timings are
**unchanged**, as expected - they are governed by the forced-layout cost above.

### Follow-up: half of the forced-layout cost is fixed, and the Masonry attribution above is wrong

**Fixed: a retained restyle that changes nothing layout can see now keeps its layout.**
`Obscura.Render.RetainedLayoutReuse` is a gate in front of the layout half of a retained restyle.
At the end of the top-down pass - the last point at which this pass's style objects are
comparable to the ones the previous layout was produced from - it compares every element the
cascade recomputed against the style object it replaced. If nothing differs, or the only
differences are in members no pass after that point reads, `LayoutDomOnce` returns the previous
`DomLayout` itself and `PrepareInternal` reuses its derived geometry too. It fails closed three
ways: only eight named members may differ and every other member of `LayoutStyle` forces a
layout, equality is proven structurally and anything the comparer cannot compare counts as
changed, and the gate is only offered a layout for a batch of pure attribute mutations against a
container-query-free sheet with at most 512 recomputed elements.

`thrash.html` again, interleaved A/B on one binary (`OBSCURA_DISABLE_RETAINED_LAYOUT_REUSE=1`),
three runs each, medians. This container measures ~1.7x slower than the table above, so the
"before" column is not the same number as the one at the top of this finding:

| 50 iterations | before | after | Chromium |
|---|---|---|---|
| read-only `offsetWidth` | 130ms | 133ms | 15-28ms |
| write `opacity`, read | 5261ms | **1211ms** | 0.2ms |
| write `class`, read | 4947ms | **524ms** | 0.1ms |
| write `transform`, read | 5114ms | 5320ms | 2.6-3.1ms |
| write `left`/`top`, read | 5107ms | 5272ms | 3.5-3.9ms |
| write `left`/`top`, no read | 5ms | 5ms | 0.1ms |
| one read after that batch | 71ms | 77ms | 0.2ms |

The last four rows are unchanged on purpose: those writes really do change a box, and the gate
decides *whether* to lay out, not *how much*.

**The Masonry attribution in this finding is wrong.** It was measured on `thrash.html` and
carried over. Instrumenting the prepare path per call (`PREP #n <ms> <mutations>`) and driving
`#/view/Masonry` shows the route running ~33 prepares totalling **13.4s**, not fifty cheap ones:

```
PREP #1  2782ms full
PREP #2   972ms retained mut=4   [attr:data-tss-unmount-pending, attr:style, Tree, Animation]
PREP #3  2523ms retained mut=167 [Tree, attr:data-tss-mount-pending, attr:class, Animation]
...
PREP #7   462ms retained mut=1   [attr:style]      <- 25 of these, 120-460ms each
PREP #31  196ms retained mut=1   [attr:style]
```

Prepares #7-#31 are Outlayer's `Item._transitionTo`, which reads `getComputedStyle` for the
item's current position and then writes the new one, fifty times. Those writes change `left`,
`top` and `transform`, so they are layout and the gate correctly declines them - the route was
measured with the gate on and off and fails identically, with the same single
`autonomous browser task exceeded its task budget` and the same `left: NaN%`.

What actually costs the 13.4s is that **one full prepare of the Tesserae SPA costs 130-460ms**,
against 77ms for the 2059-node `thrash.html`. Profiled per phase on a 133ms Masonry prepare:
`DomBuild.Build` 57ms, taffy 40ms (21 `ComputeRootLayout` calls, from the intrinsic-sizing and
scrollbar-gutter repair passes), HarfBuzz shaping 24ms over 509 paragraphs, web-font collection
18ms, cascade + top-down 7ms, `DerivedLayoutState` 4ms. On `thrash.html` the same profile is
shaping 31ms of 77ms - **every text node is reshaped from scratch on every prepare**, because
the shaping cache lives on the per-pass `TextEngine`.

So the remaining work is not a better gate, it is retained layout, in two independent pieces:

1. **A cross-pass shape cache.** Shaping is a pure function of (text, attributes, tab width) and
   is ~40% of a prepare on a text-heavy page. This is the cheapest large win and carries no
   layout-semantics risk; the only care needed is that a cached `ShapeLine` is not mutated by
   its consumer.
2. **A retained box tree**, so a restyled out-of-flow box re-runs layout for its formatting
   context rather than the document. That is what would bring the last four rows of the table
   down, and it is what `#/view/Masonry` needs: fifty flushes at 130ms do not fit in a 5.5s
   budget however cheap the gate is.

Pinned by `Obscura.Render.Tests/RetainedLayoutReuseTests.cs` (10 facts), each comparing the
incremental result against a full from-scratch layout of the same mutated tree. Parity: all 101
Tesserae routes captured twice from one pinned binary, gate off and gate on - 98 byte-identical
in geometry and computed style, and the three that are not (`Searchable List`, `Masonry`,
`Dropdown`) vary at least as much *within* an arm as between the arms when each is captured
three more times per arm. Against the Chromium reference, 99 of 101 routes have a bit-identical
geometry divergence in the two arms and every style column is identical.
