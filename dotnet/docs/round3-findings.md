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

**Residual.** A bare cyclic `width: 100%` on an atomic inline is still clamped, because
`DeferCyclicFlexInlineSizes` has already rewritten it to `Auto` by the time the box is built.

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
