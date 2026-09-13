# Obscura vs Chromium — Curiosity Workspace rendering survey

> **Status: all findings below have been acted on.** Fixes landed on
> `claude/sweet-allen-jy7y9i` across five parallel workstreams. See "Outcomes" at the end of
> this file for what was fixed, what was deliberately not fixed, and what is left.


Reference: Chromium 141 (headless) via CDP. Subject: Obscura C# `serve` (CDP), commit 097aa93.
Both driven by the *same* Playwright script over `connectOverCDP`, viewport 1440x950,
against the same live Curiosity Workspace server (auto-login, Lorem Ipsum AI provider).

## F1 — `performance.getEntriesByType('navigation')` returns `[]`  (BOOT BLOCKER)
- Where: `crates/obscura-js/js/bootstrap.js:10596` — `getEntriesByType(){return [];}` is a hard stub.
- Effect: `Request.SupportsDuplexStream()` (Mosaik `FrontEnd/.../Request.cs:664`) reads
  `performance.getEntriesByType("navigation")[0].nextHopProtocol` and throws
  `TypeError: Cannot read properties of undefined`. The first server call dies, the SPA
  never leaves its boot spinner: 156 elements rendered vs Chromium's 580.
- Chromium: 1 navigation entry, 27 resource entries, `nextHopProtocol: "http/1.1"`;
  `PerformanceNavigationTiming` / `PerformanceResourceTiming` are constructors.
  Obscura: 0 / 0, both constructors `undefined`.
- Survey workaround: a navigation-entry shim injected into Obscura only, carrying the exact
  values Chromium reports for this origin. Everything below is measured with that shim in place.

## F2 — `innerText` returns `textContent` (ignores rendered-ness)
- Where: `crates/obscura-js/js/bootstrap.js:3346` — `get innerText() { return this.textContent; }`.
- Effect: `document.body.innerText` begins with the contents of the `<style>` and `<script>`
  elements. Obscura *does* compute `display:none` on those elements correctly; `innerText`
  simply does not consult layout.
- Impact: any consumer reading visible text (the MCP tools in `Obscura.Mcp/Tools.cs` use
  `innerText || textContent`), plus scrapers and assertions.

## F3 — `font-family: inherit` is dropped on form controls, so the UA font survives
- Observed: 259 of 546 elements on the home route compute `font-family: arial` in Obscura where
  Chromium computes `Plus Jakarta Sans`. The set is exactly `<button>` elements *and everything
  inside them* - label spans, icon divs, imgs.
- The author rule exists and is unambiguous. `tss.css`:
  ```css
  input, textarea, select, button, optgroup { font-family: inherit; }
  ```
  and `Curiosity.FrontEnd.ExternalBundle.css` carries the same declaration with
  `font-size: inherit` beside it. "Plus Jakarta Sans" itself is only ever declared in
  `@font-face`; the page font reaches controls purely through that `inherit`.
- Obscura's UA sheet sets `style.FontFamily = "arial"` (and `FontSize = 13.333px`) on
  button/select/input/textarea in `Obscura.Render/Style/ComputedStyle.cs:130-175`, an explicit
  documented deviation. Chromium's UA sheet does the same thing, so the UA value is not the bug -
  the author rule failing to override it is.
- **The narrowing detail:** `font-size: inherit` in that *same rule* works. Obscura computes
  `13px` for those buttons (inherited from the page), not the UA's `13.333px` - the font-size
  histograms agree with Chromium's to within one element. Only `font-family: inherit` fails.
  So this is not a general `inherit` or cascade-origin defect; it is specific to how
  `font-family` handles the `inherit` keyword, which looks like the declaration being skipped
  rather than resolved against the parent's computed value.
- **This is a visible rendering bug, not just a computed-style curiosity.** Icon fonts are the
  casualty: `<i class="fi-rr-sidebar-open">` inside a button inherits `arial`, and its glyph
  comes from a `::before` whose rule sets `content` but deliberately not `font-family`. With
  Arial inherited the private-use codepoint has no glyph, so Chromium's sidebar-collapse icon
  renders in Obscura as the tofu "LB" (visible bottom-left in `out-obscura/shots/023_users.png`).

## F4 — `getComputedStyle().fontFamily` is lower-cased
- Chromium returns `Plus Jakarta Sans`; Obscura returns `plus jakarta sans`.
- Computed font-family should preserve the author's casing. Breaks any JS string comparison
  against a font name.

## F5 — suggestion cards render ~2.6x too tall (home route)
- Chromium: the four home-screen suggestion cards sit on rows 31px apart.
  Obscura: the same rows are 81px apart; each card box is roughly 2.6x its reference height.
- Visible in `sbs-early/000_root.sbs.png`. Related in spirit to the documented button
  line-height deviation, which suggests the same root cause is not fully covered.

## F6 — `getComputedStyle().overflow` is never populated (REPORTING, not layout)
- Measured: home route, 546 aligned elements. Chromium computes `hidden` x94, `hidden auto` x7,
  `clip` x6, `auto` x4, `auto hidden` x1 (112 non-visible). Obscura reports `hidden` x8 and
  `visible` for everything else.
- Root cause: `Obscura.Render/Paint/PreparedRender.cs:674-675` emits `overflow-x` and
  `overflow-y` into the computed-style snapshot but **never `overflow`**. The JS
  `getComputedStyle` shim (`bootstrap.js:8382`) falls back to the element's own declaration
  when the snapshot has no entry, so only the 8 elements carrying an *inline*
  `style="overflow:…"` report anything.
- So this is a computed-style *reporting* gap. Overflow parsing itself looks complete -
  `ComputedStyle.cs:395-430` handles the one- and two-value shorthand and both axes - and the
  clipping behaviour has to be judged from the screenshots, not from this table.
- Fix shape: emit the `overflow` shorthand, serialized the way Chromium does (one value when
  both axes agree, `"<x> <y>"` when they differ).

## F7 — background-color divergences
Home route, aligned elements:
- `rgb(71, 82, 95)` -> `rgba(0, 0, 0, 0)` (n=6): a background that is simply not applied.
- `rgb(29, 37, 49)` -> `rgb(71, 82, 95)` (n=4) and `rgba(0,0,0,0)` -> `rgb(71,82,95)` (n=2):
  the wrong rule is winning, not merely a serialization difference.
- `color(srgb 0.0156863 0.262745 0.82 / ...)` -> `rgba(4, 67, 211, 0.141)` (n=4): Chromium
  keeps the wide-gamut `color(srgb ...)` serialization, Obscura flattens to legacy rgba.
  Numerically equivalent here, but it is an observable `getComputedStyle` difference.

## F8 — `location` has no component setters; `location.hash = …` is a silent no-op (SPA ROUTING BLOCKER)
- Root cause, `crates/obscura-js/js/bootstrap.js:6526-6541`: the `location` object is an object
  literal that defines **getters only** for `hash`, `pathname`, `search`, `host`, `hostname`,
  `port` and `protocol`. Only `href` has a setter. Assigning to a getter-only property in
  sloppy mode fails silently, which is exactly what is observed.

| action | Chromium | Obscura |
|---|---|---|
| `location.hash = '/alpha'` | href gains `#/alpha`, `hash` reads back, `hashchange` fires | href unchanged, `hash` stays `""`, `hashchange` never fires |

- Effect on the product: every client-side navigation in Curiosity Workspace goes through
  `Router.Navigate` -> the hash. Under Obscura the app renders whatever route the *initial URL*
  named and can never move. The first survey attempt drove 50+ routes and got byte-identical
  home-page captures for every one of them.
- A hash in the initial URL *is* honoured (`goto('…/#/manage/home')` sets `location.hash`
  correctly and the app routes to it), which is what makes this survey possible at all: the
  final runs navigate by full URL instead.
- Fix shape: add the missing setters. A change that only touches the fragment must be a
  *same-document* navigation - update the URL and fire `hashchange`, do not reload.

## F9a — CDP never reports a same-document navigation (`Page.navigatedWithinDocument`)
- **`Page.navigatedWithinDocument` does not exist anywhere in Obscura** - not in
  `dotnet/src/Obscura.Cdp/**` and not in `crates/obscura-cdp/**`. The only navigation event ever
  emitted is `Page.frameNavigated`.
- Mechanism: `bootstrap.js` implements `history.pushState`/`replaceState` correctly in JS - it
  sets `globalThis.__virtualUrl` and never navigates. The host then notices that URL in
  `Page.SyncVirtualUrl` (`dotnet/src/Obscura.Browser/Page.cs:563`), called from
  `ProcessPendingNavigationAsync` (`Page.Navigation.cs:615`), adopts it and returns `true`
  meaning "navigated". That is surfaced to CDP as `Page.frameNavigated`.
- Why it matters: in CDP, `frameNavigated` means a *cross-document* navigation, and every client
  invalidates its execution context on it. Playwright therefore fails the very next call with
  "Execution context was destroyed", even though the document was never actually replaced.
- **Impact, measured over the full run: 20 of 157 routes (13%) could not be captured at all** -
  the first evaluate after load throws and there is no screenshot or DOM to compare. Chromium
  captured every one of them cleanly. The failures are overwhelmingly the admin surface:
  `#/manage/ai`, `#/manage/ai/agents`, `/policies`, `/inspect-chat`, `/nlp`,
  `#/manage/search/settings`, `/facets`, `/synonyms`, `#/manage/data`, `/file-indexing`,
  `/indexes`, `#/manage/operate/migrations`, `/usage`, `/profiling`, `#/manage/access/audit`,
  `#/manage/configure/network`, `/interface`, plus `#/search?query=test`, `#/notes/new` and
  `#/sign-in`.
- The product path is `RouteState.Set` -> `Router.ReplaceQueryParameters` ->
  `history.replaceState`, which every admin hub uses to remember its selected tab. That is why
  the damage concentrates there.
- Because the document is never actually replaced, retrying the evaluate after the spurious
  event recovers the route - which is how the retry pass gets these 20 back. That is a useful
  confirmation that the document survives and only the CDP event is wrong.
- Fix shape: emit `Page.navigatedWithinDocument` (and *not* `frameNavigated`) when the URL change
  came from the History API or is fragment-only. This one is in the C# tree, freely editable.

## F9b — a fragment-only `location.replace()` / `assign()` / `href=` really does reload
- Distinct from F9a and a genuine reload, not just a mis-reported event.
  `bootstrap.js:6528, 6538-6540`: `href=`, `assign()`, `reload()` and `replace()` all call
  `Deno.core.ops.op_navigate(...)` unconditionally.
- Per the HTML spec, navigating to a URL differing from the current one only in its fragment is
  a same-document navigation: no fetch, no teardown, `hashchange` fires.
- Fix shape: compare the target against the current URL first; when only the fragment differs,
  update the URL and fire `hashchange` instead of calling `op_navigate`.

## F10 — the computed-style snapshot omits many commonly-read properties
`PreparedRender.ComputedStyle` (`Obscura.Render/Paint/PreparedRender.cs`) emits ~50 properties.
Absent, and therefore falling back to the element's inline declaration (usually the empty
string) in `getComputedStyle`:

- `overflow` (see F6)
- every box metric: `margin`/`margin-*`, `padding`/`padding-*`, `border`/`border-width`/
  `border-style`/`border-color`/`border-radius`
- `top`, `right`, `bottom`, `left`
- `flex`, `flex-grow`, `flex-shrink`, `flex-basis`, `gap`
- `background` (shorthand), `background-image`, `background-size`, `background-position`,
  `background-repeat`
- `box-shadow`, `text-decoration`, `font-style`, `cursor`, `pointer-events`

These are among the most frequently read properties in UI code (measuring padding, reading a
border radius, checking a computed margin). They are not necessarily mis-*rendered* - this is
the JS-visible snapshot - but any script that measures them gets the wrong answer.
`probe-css.js` measures every one of these against Chromium.

## Policy note — most of these fixes land in `crates/obscura-js/js/bootstrap.js`
`CLAUDE.md` rule 1 says `crates/**` is read-only; rule 5 carves this one file out explicitly
("`bootstrap.js` is shared, not ported… Fix the shim in place and both engines pick the fix
up"), because `Obscura.Js.csproj` links that exact file rather than copying it. F1, F2, F8 and
F9 are all defects *in that shim*, so rule 5 is the one that governs and the fix belongs there -
it also fixes the Rust engine. Flagging it because the two rules read as contradictory at first
glance and this is not a decision to make silently.

## Open items to confirm before acting
1. **History API — RESOLVED on real pages.** The full survey reproduced the teardown on three
   real HTML routes, so the `text/plain` confound is not the explanation. Every Obscura capture
   failure is a route that rewrites its own URL shortly after load, and each one dies with
   "Execution context was destroyed":
     - `#/search?query=test` (the search view normalises its query into the URL)
     - `#/notes/new` (`_NoteRenderer.NewNote()` navigates)
     - `#/sign-in` (the login view redirects under auto-login)
   Chromium captures all three without incident. Still worth pinning down *which* call each
   route makes (`location.replace` / `href=` vs `history.replaceState`) before fixing, since
   `pushState`/`replaceState` look correct in the shim and `location.*` demonstrably is not.
2. **Does `overflow` actually clip?** F6 is a computed-style reporting gap. Whether Obscura also
   fails to clip, ellipsize or form scroll containers has to come from the screenshots and from
   comparing `overflow-x`/`overflow-y` (which *are* emitted) between the engines.
3. **`#/notes/new`** failed to capture in Obscura with "Execution context was destroyed" - that
   route calls `_NoteRenderer.NewNote()`, which navigates. Check whether it is the same
   fragment-navigation defect as F9.

## Method caveats (so the numbers are read correctly)
- **The two engines run sequentially against one live workspace**, Obscura first, Chromium
  second, because running both at once OOM-killed the server. A route that *writes* to the
  workspace therefore differs legitimately: `#/notes/new` creates a note and `#/spaces/new` can
  create a space, so Chromium may see state Obscura's pass created (or vice versa). Treat DOM
  differences on those two routes as suspect rather than as engine defects.
- **Obscura runs with the F1 performance-navigation shim installed**, Chromium runs untouched.
  Without the shim the Obscura app never boots at all, so there would be nothing to compare.
- **Navigation is a full document load per route in both engines**, forced by F8. That is
  identical treatment, but it means each route is measured on a cold app boot rather than on a
  client-side transition, so this survey says nothing about transition behaviour.
- **Element alignment for the geometry and style tables is structural** (a difflib match over
  `depth|tag|class` signatures). Where the two trees genuinely diverge, some aligned pairs will
  be false matches, so individual large geometry deltas need checking against the screenshot
  before being believed. Property-level counts (font-family, overflow) are robust; single
  bounding-box deltas are not.

## F11 — the `HTMLTable*Element` constructors are missing (breaks two admin views outright)
- `ReferenceError: HTMLTableRowElement is not defined` kills the render of
  `#/manage/operate/llm-usage` and `#/manage/operate/user-analytics`.
- `bootstrap.js` defines 43 `HTML*Element` constructors but not the table family:
  `HTMLTableRowElement`, `HTMLTableCellElement`, `HTMLTableSectionElement`,
  `HTMLTableCaptionElement`, `HTMLTableColElement` are all absent (only `HTMLTableElement` exists).
- Also absent and worth adding while there: `HTMLPictureElement`, `HTMLSourceElement`,
  `HTMLOptGroupElement`, `HTMLOutputElement`, `HTMLMeterElement`, `HTMLTimeElement`,
  `HTMLQuoteElement`, `HTMLDListElement`, `HTMLBaseElement`, `HTMLTitleElement`,
  `HTMLMapElement`, `HTMLAreaElement`, `HTMLObjectElement`, `HTMLEmbedElement`.

## F12 — `document.queryCommandSupported` is missing (breaks the Monaco editor)
- `TypeError: document.queryCommandSupported is not a function`, thrown from Monaco's loader,
  on `#/manage/shell`, `#/manage/operate/code` and `#/manage/operate/runtime-stats`.
- `bootstrap.js:5839` defines `execCommand() { return false; }` and nothing else of the family.
  `queryCommandSupported` and `queryCommandEnabled` are absent.
- Monaco is the code editor behind the admin shell, the code endpoints editor and the AI-tool
  editors, so this is a large feature surface, not one page.
- Fix shape: add `queryCommandSupported()` / `queryCommandEnabled()` returning `false`, matching
  the existing `execCommand` stub.

## F13 — a `"use strict"` classic script never publishes its globals  [CONFIRMED, HIGH IMPACT]
A controlled matrix, both engines, same page:

| script | Chromium | Obscura |
|---|---|---|
| inline, no directive: `var x = …` | object | **object** |
| inline, 20 KB, no directive | object | **object** |
| **inline, `"use strict"`: `var` / `function`** | object / function | **undefined / undefined** |
| external 75 B, `"use strict"` | object / function | **undefined / undefined** |
| external 20 KB, `"use strict"` | object / function | **undefined / undefined** |
| **external 30 B, no directive** | object | **object** |

So it is **not** inline vs external (my earlier conclusion) and **not** script size (the other
candidate, since `ExecuteScriptGuarded` branches at 10,000 chars). The single discriminator is
the **`"use strict"` directive**: a strict classic script's top-level `var` and `function`
declarations never reach `globalThis`, while a sloppy-mode one's do. Chromium publishes all six.

That is exactly the semantics of a **strict-mode `eval`**, where `var`/`function` stay confined
to the eval's own scope instead of creating global bindings. Obscura is evaluating classic
scripts with eval-like semantics rather than as top-level scripts. The path is
`Page.Scripts.cs:604/621` -> `ObscuraJsRuntime.ExecuteScriptGuarded` ->
`ExecuteScript` -> `_engine.Execute(DocumentInfoFor(name), source)`
(`dotnet/src/Obscura.Js/Runtime/ObscuraJsRuntime.cs:384`). The fix is in the C# runtime, **not**
in `bootstrap.js`.

**Blast radius is the reason this ranks high.** Essentially every modern bundler emits
`"use strict"` at the top of its output, and the IIFE/`--global-name` shape
(`"use strict"; var lib = (() => { … })();`) is how a large fraction of classic-script libraries
publish themselves. Under Obscura every one of them "loads successfully" - `onload` fires,
`loaded: true` - and defines nothing. graph-kit and the Graph Explorer are just the instance this
survey happened to catch.

## F14 — "Dynamic script fetch error: HTTP 404" on three admin routes (Obscura-only)
- Seen on `#/manage/shell`, `#/manage/operate/code`, `#/manage/operate/runtime-stats`, always
  alongside the Monaco failure of F12.
- **Confirmed Obscura-specific.** The Chromium run captured all 157 routes with zero failures and
  console errors on only 4 routes (`#/clipboard` - `ERR_CONNECTION_RESET`;
  `#/spaces/configure-apps` - a null `.Show`, an app bug that reproduces in both;
  `#/desktop/welcome` and `#/send-email` - app `ctor` errors). None of them is a script 404, and
  none of the Obscura-only signatures (F11/F12/F13) appears in Chromium at all.
- So Obscura is requesting a URL that Chromium is not, or resolving the same relative URL
  differently. The product loads these through `Transpose.Require.RequireAsync`, which
  deliberately tries both the `.js` and `.min.js` spellings - a 404 on the first would be benign
  *if* the fallback then succeeds, and in Obscura it evidently does not.
- **Still needs the failing URL**, which the survey did not record (the message is the app's, not
  the engine's). A targeted re-run of one of those routes with `requestfailed` / `response`
  listeners will name it; do that before assigning this one.

## Cross-engine error summary
| signature | Chromium | Obscura |
|---|---|---|
| capture failures over 157 routes | 0 | 20 (all F9a) |
| routes with console errors | 4 (all app-side) | 6 distinct engine signatures |
| `HTMLTableRowElement is not defined` | absent | F11 |
| `document.queryCommandSupported is not a function` | absent | F12 |
| `graph-kit … did not define globalThis.graphkit` | absent | F13 |
| `Dynamic script fetch error: HTTP 404` | absent | F14 |

---

# Verified computed-style results (`probe-css.js`, strict alignment)

~50 properties captured per element on five routes in both engines. To avoid the alignment
artefacts that inflate a naive diff, the table below uses only the four routes whose element
counts matched *exactly* (`#/` 546, `#/users` 446, `#/manage/ai/tools` 414,
`#/preferences?id=general` 313) and, within those, only element pairs with an identical
tag **and** class list. **1715 strictly-aligned pairs.**

**This corrected several signals I had reported earlier as real.** Under loose alignment,
`position: static -> absolute` (63), `display: flex -> block` (65), `font-weight` (66),
`font-size` (40), `align-items`, `row-gap`, `opacity`, `justify-content`, `white-space`,
`text-overflow` and `line-height: 0px` (79) all appeared as differences. Under strict alignment
every one of them vanishes or collapses to a handful. They were artefacts of comparing
misaligned trees, not engine defects. What survives:

| property | differing pairs | dominant difference |
|---|---:|---|
| `font-family` | 1706 / 1715 | `arial` x889 (F3) and casing/quoting x816 (F4) |
| `width` | 499 / 1715 | e.g. `199.141px` -> `176px`, `189.5px` -> `183px` |
| `overflow-x` | 335 / 1715 | `hidden` -> `auto` x322; `clip` -> `visible` x11 |
| `overflow-y` | 311 / 1715 | `hidden` -> `auto` x298; `clip` -> `visible` x11 |
| `height` | 254 / 1715 | e.g. `19.5px` -> `20px`, `14px` -> `12px` |
| `background-color` | 93 / 1715 | transparent -> `rgb(71,82,95)` x22; `rgb(71,82,95)` -> `rgb(29,37,49)` x14 |
| `transform` | 15 / 1715 | `none` -> `matrix(1,0,0,1,0,-8)` / `matrix(-1,0,0,1,0,0)` |
| `color` | 10 / 1715 | e.g. `rgb(4,67,211)` -> `rgb(50,49,48)` |
| `line-height` | 4 / 1715 | `14.3px` -> `14.299999px` (float serialization) |
| `visibility` | 2 / 1715 | `hidden` -> `visible` |

## F6 — UPGRADED: `overflow` is genuinely mis-computed, not only mis-reported
The earlier read (a missing shorthand in the snapshot) was only half the story. `overflow-x` and
`overflow-y` **are** emitted, and they carry the wrong values on ~20% of elements:
`hidden` computes as `auto`, and `clip` computes as `visible`. So clipping, ellipsis and scroll
containers really are affected, not just what `getComputedStyle` reports. The missing `overflow`
shorthand (F6 as originally written) remains true and is a second, smaller defect.

## F15 — NEW: real layout divergence in `width` (29%) and `height` (15%)
On strictly-aligned pairs, 499 differ in computed `width` and 254 in `height`. These are emitted,
layout-derived values, so they are genuine. The height differences cluster around line boxes
(`19.5px` -> `20px`, `14px` -> `12px`), which points at font metrics / line-height rounding; the
width differences are larger and less uniform (`199.141px` -> `176px`).

## F16 — NEW: `transform` computes a matrix where Chromium computes `none`
15 pairs, e.g. `none` -> `matrix(1,0,0,1,0,-8)` (an 8px vertical shift) and
`none` -> `matrix(-1,0,0,1,0,0)` (a horizontal flip). Obscura is applying a transform Chromium
does not - visible misplacement, and the flip matches the `.fi-rr-sidebar-open::before`
`transform: scaleX(0.8)` / flip rules in the icon CSS.

## F17 — NEW: float serialization in computed values
`line-height` serializes as `14.299999px` where Chromium emits `14.3px`. Cosmetic, but it breaks
string comparison and any test asserting on computed values.

## F7 — REVISED: `background-color` differs on 93 of 1715 pairs
Both directions occur: 22 pairs where Chromium is transparent and Obscura paints
`rgb(71,82,95)`, and 14 where both paint but disagree (`rgb(71,82,95)` vs `rgb(29,37,49)`).
Painting a background that should not be there is the more visible half.

---

# F18 — RETRACTED, and replaced: these routes DO render; a redirect is done as a full reload

I previously reported that 20 routes "render nothing but a loading spinner". **That was wrong**,
and the error was in my harness, not the engine. `probe-reloadloop.js` settles it:

| route | framenavigated | load | document requests | final state |
|---|---:|---:|---:|---|
| `#/users` (control) | 1 | 1 | 1 | 375 elements, no spinner |
| `#/manage/ai` | **2** | **2** | **2** | **321 elements, no spinner**, url `#/manage/ai/assistants` |

Two conclusions, both against my earlier claim:
1. **Not a reload loop.** Exactly two document loads, then it stops.
2. **Not a blank page.** The route renders 321 elements with no spinner - it had simply not
   finished its *second* boot when my capture ran. The identical "209 elements + spinner" I saw
   across 20 routes was one mid-boot moment, photographed 20 times.

**The real defect is F9b.** `#/manage/ai` redirects to `#/manage/ai/assistants`. That differs
only in the fragment, so it must be a same-document navigation. Obscura issues a **second
document request** and reloads the whole app: `documentRequests=2`. The consequences are
(a) every such route pays a full second boot, and (b) the reload destroys the CDP execution
context, which is what made 20 routes uncapturable in the survey.

So F9b is confirmed on real pages with a hard measurement, and F18 as originally written should
be struck. The 20 routes are being re-captured with a redirect-aware settle (wait for the URL
*and* the element count to hold steady) to get honest comparison data.

**Lesson for the numbers above:** a capture that lands mid-boot is indistinguishable from a
broken render unless you check whether the page is still changing. Any route in this survey whose
element count looks anomalously low should be re-checked the same way before being believed.

---

# F5 — ROOT-CAUSED: `height: fit-content` is not implemented

Re-measured against the clean Chromium reference, on `#/` (home):

| | Chromium | Obscura |
|---|---|---|
| `.msk-home-view-suggestion` card | `[425, 584, 400, **56**]` | `[437, 584, 400, **163**]` |
| its icon | `[438, 594, 36, 36]` | `[450, 647, 36, 36]` |
| its text block | `[484, 604, 328, 17]` | `[496, 657, 328, 17]` |

**Every child is identically sized in both engines** - icon 36x36, text 328x17, label-text 191 vs
192 - and the container is 820x366 in both. Only the card box differs: 56px against 163px, and
the children are correctly centred inside whichever height it has, so `align-items: center`
works. The card is simply stretching to fill its flex line instead of hugging its content.

**Cause.** Tesserae sizes these components with
```css
:where(.tss-avatar, .tss-btn, .tss-contextcard, .tss-cron-editor, .tss-daterange-p…)
  { width: fit-content; height: fit-content; }
```
`:where()` is supported (`Obscura.Dom/Selectors/SelectorParser.cs:692`), so that is not the
problem. The problem is the declaration itself:
`Obscura.Render/Style/ComputedStyle.cs:1054-1069` handles `width: fit-content` by setting
`style.WidthFitContent`, consumed in `DomPasses`, `DomTableSupport`, `CssAnimationSampler` and
`PreparedRender`. The `height` / `block-size` arm sets no equivalent - **`HeightFitContent` does
not exist anywhere in the tree.** `height: fit-content` therefore resolves through
`DimensionValue("fit-content")` and leaves the box free to stretch.

**Blast radius.** That `:where()` list covers avatars, buttons, context cards, cron editors and
date-range pickers - a large slice of the Tesserae component set - so every one of them stretches
vertically inside a flex or grid line. This is also a plausible contributor to the `height`
differences F15 measured on 15% of elements.

# F19 — flex wrap differs: 5 items per row vs 4

`#/preferences?id=themes` renders its theme grid 5 cards wide in Chromium and 4 wide in Obscura
(`sbs/129_preferences_id_themes.sbs.png`). The container widths agree; Obscura's per-card pitch
is ~1.26x Chromium's, so one fewer card fits per line and the grid reflows. This is the visible
form of the `width` divergence F15 measured on 29% of elements, and it is the kind of defect that
changes what a user actually sees rather than just what a script measures.


---

# Outcomes

Five workstreams, partitioned by file so they could run in parallel. Every fix carries tests.

| finding | outcome |
|---|---|
| F1 performance timeline | Fixed. Real `PerformanceNavigationTiming` entry, real lifecycle marks, `mark()`/`measure()` no longer no-ops. Resource entries deliberately still `[]` (no transport metrics reach JS); network phases in the navigation entry are synthesized and documented as such. |
| F2 `innerText` | Fixed. Rendered-text projection: skips non-rendered subtrees, collapses whitespace per `white-space`, breaks at block boundaries and `<br>`, tab-separates table cells. |
| F3 `font-family: inherit` | Fixed. The cascade arm skipped the keyword (`if (family != "inherit")`) so the UA `arial` survived; it now resolves it. `revert`/`revert-layer` keep the UA value. |
| F4 font-family casing | Fixed. `LayoutStyle.FontFamilySpecified` carries the reporting spelling beside the lower-cased matching key; re-serialized Blink-style, verified per shape against Chromium 141. |
| F5 `height: fit-content` | Fixed, and it had a **second cause**: `align-content: baseline` was dropped by `ContentAlignmentValue`, so the container stayed `normal` = stretch. Fixing only `fit-content` left a correct 56px card inside a 175px row. Both fixed. |
| F6 overflow | Fixed, both halves. The cascade collapsed `hidden`/`scroll`/`auto`/`overlay` onto one code so the value could not be reported; now five codes with the computed-value coupling preserved. The `clip -> visible` half was a missing UA rule (`img { overflow: clip }`) — all 11 cases were images. Shorthand also emitted. |
| F7 background-color | **Not fixed.** 93 pairs. Did not reproduce on the routes the cascade agent drove. |
| F8 `location` setters | Fixed. All components, fragment-only changes take the same-document path. |
| F9a CDP same-document | Fixed. `Page.navigatedWithinDocument` with `fragment`/`historyApi` navigationType; real cross-document navigations still get `frameNavigated`. |
| F9b fragment-only reload | Fixed. `href=`/`assign()`/`replace()` delegate to the History API for a fragment-only change. Two adjacent bugs found and fixed: `HashChangeEvent` dropped its init dictionary, and `window.onhashchange` never fired. |
| F10 snapshot omissions | Fixed. Box metrics, insets (correctly `auto`, not `0px`), flex longhands + shorthand, `gap`, `background*`, `box-shadow`, `text-decoration`, `font-style`, plus `cursor` and `pointer-events` newly modelled in the cascade. |
| F11 HTMLTable*Element | Fixed. 19 interface objects added as real `Element` subclasses rather than the `= Element` alias the existing 43 use — an alias makes every element an instance of every one of them. Table members (`cells`, `rowIndex`, `insertCell`, …) added. |
| F12 `queryCommandSupported` | Fixed, with the rest of the family. |
| F13 strict-script globals | Fixed — **and my diagnosis was wrong.** See below. |
| F14 script 404s | **Not investigated.** Needs the failing URL captured first. |
| F15 / F19 width divergence | **Not fixed, and not caused by F5** as I had guessed. Every box size is now correct; the residue is intrinsic inline sizing. Themes cards are 160x100 in *both* engines but the wrapper pitch is 265px vs Chromium's 214px. Points at max-content contribution / text measurement. |
| F16 transform | **Not fixed.** 15 pairs, did not reproduce on the routes driven. |
| F17 float serialization | Fixed. `CssNumber` now matches Blink's `String::Number`; `line-height` reads `14.3px`. |
| F18 | Retracted (see above) — a harness artefact, not a defect. |

## F13: the diagnosis in this document is wrong, and the correction matters

This file argues the discriminator is the `"use strict"` directive. It is not. The real
discriminator is **parser-inserted vs dynamically inserted** scripts.

`bootstrap.js` executes every *dynamically inserted* classic script with `(0, eval)(source)`
(`__runDynScriptTask` for a fetched `script.src` body, `__prepareInsertedScript` for an element's
own text). Indirect eval runs in global scope, but ES confines a **strict** eval's top-level
`var`/`function` to the eval's own variable environment, while a sloppy one publishes them. That
is why strictness correlated perfectly in my matrix: every case I tested was dynamically inserted
(`document.createElement('script')`), so I never tested a parser-inserted strict script — which
works fine. Raw ClearScript handles a strict top-level script correctly; the parser path was never
at fault.

The lesson is the same one F18 taught: a variable that correlates perfectly across six cases can
still be the wrong variable if every case shares an untested confound.

The fix bridges those two call sites onto a port-added `op_run_classic_script`. Its proper home is
`bootstrap.js` itself (which would fix the Rust engine too); the bridge no-ops if that happens.

## Verified end to end

With F1, F8, F9a and F9b merged, the app **boots in Obscura with no shim** (it previously stopped
at a 156-element spinner), and routes that were uncapturable now capture:
`#/manage/ai` 325 elements, `#/manage/search/settings` 899, `#/search?query=test` 485 — 0 failures.

Cascade fixes, strictly aligned against Chromium 141: `font-family`, `overflow`, `cursor` and
`pointer-events` all differ on **0** pairs across 205 + 205 + 329 element pairs on three routes.
The F5 card is `[433,584,400,56]` against Chromium's `[425,584,400,56]`.
