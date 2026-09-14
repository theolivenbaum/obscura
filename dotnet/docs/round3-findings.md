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
