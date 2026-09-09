---
name: render-compare
description: Compare how Obscura renders a page against a real browser (headless Chromium via Playwright) and against the C# port, using the self-contained fixtures in test-html-files/. Use when investigating a visual or layout difference, when checking whether a rendering gap is in the port or in the reference engine, when adding a fixture, or when a screenshot looks wrong and you need to know which engine is at fault.
---

# Comparing Obscura's rendering against a real browser

Three lanes: **Chromium** through Playwright (the ground truth a user's browser
would show), the **Rust reference** in `target/release/obscura`, and the **C#
port** in `dotnet/src/Obscura.Cli/bin/Release/net10.0/obscura`.

Run it:

```bash
scripts/render-compare.sh test-html-files/renderlab-complex.html          # 1280, 900, 640
scripts/render-compare.sh test-html-files/renderlab-complex.html 1280     # one width
```

Output lands in `target/render-compare/`: one PNG per width per engine, a
`.probes.tsv` of DOM measurements, and a pixel-difference table.

## Read the two comparisons separately

They answer different questions and conflating them wastes time.

**Reference vs port** is a parity question with a right answer: they should
agree. A difference here is a port bug. Judge it on the DOM probes first
(identical node counts, element counts and `scrollHeight` mean the layout
agrees) and only then on pixels.

**Chromium vs Obscura** is not a parity question. Obscura is its own engine and
does not try to be pixel-identical to Chromium. A difference here is a
*capability* question: which CSS or canvas feature is Obscura not implementing?
The useful signal is that **Chromium-vs-reference and Chromium-vs-port should be
within a percentage point of each other**. When they are, the port has not
drifted; when they diverge, the port has changed behaviour the reference has.

## Pixels lie unless you sweep the tolerance

`tools/imgdiff a.png b.png <tolerance>` counts pixels whose per-channel
difference exceeds the tolerance. A single number is not interpretable, because
two rasterizers shade glyph edges differently by a few counts and a page of
dense text then reports a large difference while being laid out identically.

Sweep it. Anti-aliasing collapses, layout does not:

```bash
for t in 8 24 48 96; do tools/imgdiff/bin/Release/net10.0/imgdiff a.png b.png $t; done
```

Measured on `renderlab-complex.html`, reference against port fell from 2.7% to
well under 1% as the tolerance rose, while Chromium against the reference barely
moved from 98% - because a missing canvas and an unblurred element are
large-amplitude differences, not edge shading. Also read `meanAbsDiff`: a high
differing-pixel count with a low mean is anti-aliasing, and a high mean is a
real difference.

## The traps

**A fixture that touches the network invalidates the run.** Every file in
`test-html-files/` must be self-contained. The engines do not have equal network
access - headless Chromium is commonly reset by a sandbox gateway while the
Obscura CLI is not - so an external stylesheet means one engine styles the page
and the other does not. That turned a 15% difference into 79% the first time
this was run, and the number was measuring egress. If you must mirror a real
site, rewrite every absolute URL to a local path so all three engines get the
same 404.

**Capture the full page, not the viewport.** Playwright takes `fullPage: true`.
The Obscura CLI has no equivalent, so `render-compare.sh` measures
`document.documentElement.scrollHeight` first and passes it back as
`OBSCURA_SHOT_H`. Carry the caveat: in Obscura that also changes the *navigation*
viewport, so `vh` units, `position: fixed` and anything driven by viewport height
resolve against the tall viewport rather than the real one. Chromium's
`fullPage` keeps the viewport and stitches. For a page that leans on `vh`,
compare at viewport height as well before concluding anything.

**Pin the animation frame.** `OBSCURA_SHOT_ANIMATION_TIME_MS` makes the engine
paint an exact instant on the document timeline instead of sampling live, so two
runs of an animated page are comparable. Without it a `requestAnimationFrame`
page differs from itself between runs.

**Check the JS-built DOM, not just the picture.** A page whose content is built
on `DOMContentLoaded` can look plausible while half the work never ran. The
probes in `.probes.tsv` count generated elements for exactly this: if
`nodes` matches Chromium but a generated card count does not, the difference is
in script execution, not painting.

**`innerText` legitimately differs from Chromium.** Chromium's honours CSS
visibility and collapses to rendered text; Obscura's is more permissive, so
Obscura reports several times more characters on a page with hidden content.
Compare `innerText` between reference and port, never against Chromium.

## Adding a fixture

Drop a self-contained `.html` into `test-html-files/`, add a row to its README
saying what the file exercises, and run the script. Prefer one page that
exercises many primitives over many pages that each exercise one: the value is
in catching the interaction between features.

Small single-purpose layout repros belong in `render-repros/` instead, where
`scripts/parity-sweep.sh` drives them through both engines over five `--dump`
modes.

## When you find a difference

Decide which tree owns it before writing any code.

- **Port only** (reference matches Chromium, port does not): fix in `dotnet/`.
- **Both engines** (reference and port agree with each other and differ from
  Chromium): the gap is in the engine's rendering, not the port. If it lives in
  `crates/obscura-js/js/bootstrap.js`, fix it there - that file is shared
  verbatim by both engines and linked, not copied, so one fix lands in both and
  parity is preserved. If it lives in the Rust render layer, it is a reference
  change and needs to be decided as such; note it in `todo.md` rather than
  working around it in the port.
