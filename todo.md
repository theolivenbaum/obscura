# Obscura C# port - status and work queue

Target: `net10.0`. Reference implementation: the Rust workspace in `crates/`.
Rules and conventions: see `CLAUDE.md`.

Legend: `[ ]` not started, `[~]` in progress, `[x]` ported + tests green,
`[P]` parity-validated against the Rust binary.

Line counts below are **code lines, excluding `#[cfg(test)]` blocks**, measured
from the Rust tree. The raw file sizes are much larger and misleading: the Rust
tree is 86k lines of code and 62k lines of tests. Scope of the port:

| Area | Code | Rust tests |
|---|---:|---:|
| obscura-render | 42,555 | 29,497 |
| vendor/taffy (layout, patched) | 20,520 | 2,498 |
| obscura-cdp | 13,788 | 4,497 |
| obscura-js | 8,565 | 19,574 |
| obscura-browser | 5,794 | 3,982 |
| obscura-net | 3,632 | 1,925 |
| obscura-dom | 3,503 | 1,708 |
| obscura-cli | 3,108 | 726 |
| obscura-mcp | 2,754 | 398 |
| obscura (library API) | 2,224 | 0 |
| **Total** | **~106,400** | **~64,800** |

vendor/cosmic-text (10,450) is mostly replaced by Skia + HarfBuzz; only its
vendored variable-font coordinate fix needs to carry over.

## 0. Setup

- [x] Review the Rust source and map the architecture
- [x] Write `CLAUDE.md`
- [x] Write `todo.md`
- [x] Disable CI/CD (`.github/workflows/*.yml.disabled`, `scripts/ci/*.py.disabled`, `Dockerfile.disabled`)
- [x] Adapt `skills/obscura/SKILL.md` for the port; add `skills/obscura-port/`
- [x] Scaffold `dotnet/` (solution, `Directory.Build.props`, `Directory.Packages.props`, projects)
- [x] Pin the dependency set; confirm V8 is the only native dependency
- [x] `dotnet/docs/op-protocol.md` - the frozen `bootstrap.js` <-> host contract

## 1. Obscura.Dom  (<- crates/obscura-dom, ~5.2k lines)  -  81/81 tests green

- [x] `tree.rs` -> `DomTree`, `Node`, `NodeId`, `NodeData`, shadow roots, slots
- [x] `tree_sink.rs` -> HTML parsing via AngleSharp adapted into the arena tree
- [x] `selector.rs` -> selector parsing, matching, specificity (`Obscura.Dom.Selectors`)
- [x] `serialize.rs` -> `innerHTML` / `outerHTML` serialization
- [x] Unit tests ported (81 facts: 29 tree, 14 tree_sink, 31 selector, 7 serialize)
- [~] Parity: parse + serialize a corpus through both engines. Validated ad hoc against
      `target/release/obscura` over `render-repros/**`: 57/64 fixtures byte-identical for
      parse + serialize (the other 7 differ only where the Rust run executed page script or
      injected engine markup), and 3323 (selector, fixture) `querySelectorAll` count
      comparisons over the 57 script-free fixtures with no semantic divergence. A standing
      parity test needs `Obscura.Cli`, which the harness shells out to.

## 2. Obscura.Net  (<- crates/obscura-net, ~5.6k lines)  -  94/94 tests green

- [x] `encoding.rs` -> charset detection and transcoding (429)
- [x] `cookies.rs` -> `CookieJar`, parsing, domain/path matching, persistence (1281)
- [x] `robots.rs` -> robots.txt fetch/cache/match (172)
- [x] `blocklist.rs` + `pgl_domains.txt` -> tracker blocklist (77)
- [x] `client.rs` -> HTTP client, redirects, SSRF gate, decompression (2847)
- [x] `interceptor.rs` -> request interception types (15)
- [ ] `wreq_client.rs` -> stealth transport (710) **deferred, see Known deviations**
      (`IStealthHttpClient` + `UnavailableStealthHttpClient` keep the seam)
- [x] Unit tests ported (94 facts, all green)
- [ ] Parity: cookie jar and SSRF decisions over a shared fixture table

## 3. Obscura.Js  (<- crates/obscura-js, ~44k lines; 15.8k of it is shared JS)

- [x] Share `bootstrap.js` with the Rust tree by linking it as an embedded resource
- [~] `runtime.rs` -> `ObscuraJsRuntime` on ClearScript (3704)
      - [x] 80 of 95 public methods; watchdog, heap cap, event loop, CDP object
            store, module graphs, frame realms
      - [x] the 15 `screenshot_*`/render-seeding methods (the Page-capture
            boundary), in `Runtime/ObscuraJsRuntime.Capture.cs`, plus
            `RuntimeCanvasSurfaceSource` and `WithSyncRenderLoadingDisabled`.
            The 29 tests that named them are written and green.
      - [ ] **404 of 455 tests are unwritten**, not blocked. Bodies are kept as
            Rust comments. This is the largest gap in the port.
- [x] `ops.rs` -> the 52 ops (3101) - 52/52 ops and 67/67 `op_dom` commands,
      both mechanically diffed against the protocol doc
  - [x] `op_dom` command dispatcher (67 commands)
  - [x] crypto ops (`digest`, `hmac`, `aes-gcm/cbc/ctr`, `pbkdf2`, `hkdf`, `random_bytes`) - 30 tests green
  - [x] URL ops (`parse`, `set`, `resolve`, `encode_query`) - WHATWG parser
        written in tree with Punycode, diffed against the real Rust `url` crate
        over 51,038 cases with 0 mismatches on the curated set
  - [x] fetch / network ops (interception, per-hop SSRF re-validation, 20-hop
        limit, 301/302/303 GET downgrade, CORS preflight, capped body read)
  - [x] render-facing ops (layout geometry, computed style, canvas, image
        metadata, WAAPI)
- [x] `frame.rs` -> child-frame realms (1352)
- [x] `module_loader.rs` + `import_map.rs` -> ES module loading (783)
- [x] `write_stream.rs`, `markdown.rs`, `v8_flags.rs`, `cdp_watchdog.rs` (461) -
      64 tests, 6 ported from Rust and 58 new, since three of these four files
      shipped with no in-file tests at all
- [ ] Unit + integration tests ported
- [ ] Parity: run the same scripts through both runtimes, compare results

## 4. Obscura.Render  (<- crates/obscura-render + vendor/taffy)  -  COMPLETE, 621 tests green

The largest component. Split into stages; each stage is independently testable.

- [x] `css.rs` -> CSS tokenizer, parser, values, at-rules, indexed cascade,
      container-query evaluator and animation sampler (4431) - 79 tests, 2
      skipped (one is `#[ignore]` in Rust, one needs `dom.rs`)
- [x] `style.rs` -> cascade, specificity, inheritance, computed style (8615) -
      86 Rust tests ported, 85 passing, 1 skipped pending `dom.rs`
- [x] `vendor/taffy` -> layout algorithms: block, flexbox, grid (20520) - 116
      tests green, including the vendored grid shrink-to-fit correction, which
      2 ported tests pin (stubbing the fix turns them red)
- [x] `dom.rs` -> render tree construction, fragmentation, scrolling, geometry
      (14769) - 135 Rust tests ported, 135 passing, 0 skipped
- [x] `inline.rs` -> line breaking, text shaping, bidi, inline layout (3535) -
      33 Rust tests ported, 30 passing, 3 skipped pending `dom.rs`. Reimplemented
      on HarfBuzz + Skia; see CLAUDE.md for the measured differences.
- [x] `paint.rs` -> rasterization onto Skia (9721) - 131 Rust tests ported,
      131 passing, 0 skipped, 0 tolerances introduced
- [x] `border.rs` -> border and outline painting (452) - ported inside
      `Core/Border.cs`, because `LayoutStyle.BorderModel`/`.Outline` are fields
      of these types and could not be stubbed. Its 3 tests are green.
- [x] `lib.rs` -> core types: `LayoutStyle` (189 fields), geometry, `Affine2`,
      `Dimension`, style enums, background, generated content, animation,
      capture limits, and the `LayoutStyle`->taffy style mapping (2914)
- [x] Fonts: embedded font assets, WOFF1/WOFF2 decoding, variable-font axes
      carried through both shaping and rasterization
- [x] Unit tests ported. Only skip left in Obscura.Render.Tests is the cascade
      microbenchmark Rust itself marks `#[ignore]`.
- [ ] Parity: render `render-repros/**` fixtures in both engines and compare

## 5. Obscura.Browser  (<- crates/obscura-browser, ~9.8k lines)  -  91/96 tests green, 5 skipped on Obscura.Js gaps

- [x] `page.rs` -> `Page`: navigation, evaluation, waiting, interception (4591),
      split across `Page.cs`, `Page.Navigation.cs`, `Page.Frames.cs`,
      `Page.Scripts.cs`, `Page.Stylesheets.cs`, `Page.Network.cs`,
      `Page.Capture.cs`, `Page.Evaluate.cs`, plus the free functions in
      `PageHelpers.cs` and the URL helpers in `PageUrl.cs`
- [x] `context.rs` -> `BrowserContext` (269)
- [x] `lifecycle.rs`, `profiles.rs`, `fork_virtual_url.rs` (188) - the fork's
      `sync_virtual_url` lives on `Page` as `SyncVirtualUrl`
- [x] `pdf.rs` -> raster PDF export (1027), on SkiaSharp for JPEG encode and
      PNG/JPEG decode instead of the `image` crate
- [x] Unit + integration tests ported: all 92 Rust tests (74 page.rs, 10 pdf.rs,
      4 context.rs, 4 across `tests/`), 87 green, 5 skipped on named Obscura.Js
      gaps (see Known deviations), plus 4 written for the `UrlRecord` migration
- [x] URLs go through `Obscura.Js.Url.UrlRecord`, not `System.Uri`. `Page.Url` is
      a `UrlRecord`, `PageUrl` sits on the ported WHATWG parser, and
      `Page.UrlString()` returns its serialization, so a `data:` URL on the CDP
      wire and in `location.href` matches the reference. `Obscura.Browser.NetUrl`
      is the remaining conversion at the `Obscura.Net` boundary (see Open issues)
- [ ] Parity: navigate a fixture corpus, compare DOM + text + links

## 6. Obscura.Cdp  (<- crates/obscura-cdp, ~12.7k lines)  -  279/279 tests green

- [x] `server.rs` -> WebSocket server, sessions, targets (1730)
- [x] `dispatch.rs` -> method routing (1273)
- [x] `types.rs`, `util.rs`, `cookie_params.rs` (440)
- [x] domains: `page` (3179), `runtime` (835), `dom` (788), `target` (540),
      `pdf` (535), `accessibility` (524), `emulation` (456), `input` (438),
      `network` (430), `domsnapshot` (416), `io` (283), `fetch` (224),
      `storage` (124), `browser` (42), `lp` (21). The dispatch table matches the
      Rust one method for method. The four core domains were re-audited arm by
      arm and field by field; `target` and the 30 in-file unit tests needed
      nothing, and four wire-visible divergences were fixed (see the commit).
- [x] Integration tests ported: all 27 files, 70 Rust tests -> 279 facts
- [P] Parity: both servers driven with the same script over raw WebSocket -
      40/40 command responses byte-identical, 111/111 events in identical order.
      The two exceptions are response-header ordering, which comes off a Rust
      `HashMap` and is nondeterministic per process, and header sets the two
      HTTP clients normalize differently.

## 7. Obscura.Mcp  (<- crates/obscura-mcp, ~3.2k lines)

- [x] `lib.rs` -> stdio MCP server and tools (2077) - 37 tools, tool list
      byte-identical to the Rust `json!` source (pinned by a differential test)
- [x] `http.rs` -> HTTP/SSE transport (462)
- [x] Integration tests ported (16 found, 16 ported, 16 passing)
- [ ] Parity: identical tool listings and tool-call results

## 8. Obscura.Cli + Obscura  (<- crates/obscura-cli, crates/obscura, ~6k lines)  -  177/178 tests green

- [x] `main.rs` -> `fetch`, `serve`, `scrape`, `mcp`, global flags (1946).
      `serve` had five real defects, `scrape` one protocol bug, and every
      numeric option went through a parser that could kill the process where
      clap prints a usage error; see the commit.
- [x] `worker.rs` -> the `obscura-worker` binary for parallel scrape (165)
- [x] `crates/obscura` -> embeddable library API (`Obscura` project) (2224) -
      diffed item by item; every public type, method and property present
- [x] Integration tests ported: every file under `crates/obscura-cli/tests` and
      `crates/obscura/tests` now has a same-named counterpart. One fact skipped,
      the stealth-transport wire assertion that is a recorded deliberate gap.
- [P] Parity: `scripts/parity-sweep.sh` is 320/320 byte-identical across
      text/links/html/markdown/assets, and `scripts/parity-sweep-scrape.sh`
      drives 169 `scrape`/`serve`/worker cases at 165 identical. The 4 that
      differ are both outside the CLI: the ClearScript script-name suffix in a
      thrown error's stack, and the platform io error string for a missing file.

## Open issues

- **The ClearScript op boundary costs ~5us per call against deno_core's ~0.1us,
  and it is the port's dominant runtime gap.** Measured in-page after warmup,
  same host, same V8, both engines:

      zero-arg op (op_runtime_events_enabled)   rust 0.10us   port 5.00us
      op_dom("document_node_id")                rust 0.30us   port 9.95us
      op_dom("node_type")                       rust 0.40us   port 4.70us
      op_layout_metrics                         rust 1.50us   port 27.0us

  It is not the op bodies: a zero-argument op that returns a bool pays the same
  4.5us. It is not argument typing either - a standalone ClearScript benchmark
  puts a warmed 4-argument host delegate at 2.4us/call, and `DisableDynamicBinding`,
  `DisableExtensionMethods`, `DisableTypeRestriction` and `UseReflectionBindFallback`
  all measure identical to the default once JIT tiering is controlled for (the
  apparent wins from those flags were tier-up of the first engine in the process).
  deno_core binds `#[op2(fast)]` through V8's fast API, which ClearScript has no
  equivalent for.

  What it costs in practice: `document.createElement` + className + style +
  textContent + appendChild for 5000 nodes is 123ms in Rust and 1061ms in the
  port; 20k `setAttribute` calls are 28ms against 412ms. On the Tesserae SPA the
  page needs ~2s of adaptive settle in Rust and ~6s in the port, which overruns
  the CLI's 5-second settle cap and intermittently captures the page before its
  deferred content mounts. Closing it means cutting the number of crossings
  (batching mutations, caching more in `bootstrap.js`), not micro-tuning the
  binding - and `bootstrap.js` is shared, so any such change has to help both
  engines.
- **Cold start is ~870ms for the port against ~40ms for the reference.** .NET
  startup plus ClearScript/V8 init on an empty page. ReadyToRun publishing is
  untried and is the obvious first thing to measure.
- **`Url::parse` failure reasons are collapsed into one message.** The `url`
  crate's `ParseError` has a distinct `Display` per variant and `page.rs` reports
  it verbatim; `UrlParser.Parse` returns `UrlRecord?` with no error channel, so
  `Page.Navigation` hardcodes one string and every rejected URL reports
  "relative URL without a base". Diffed against the reference binary:

      http://              rust: empty host                        port: relative URL without a base
      http://a:99999/      rust: invalid port number               port: relative URL without a base
      http://[fe80::1      rust: invalid IPv6 address              port: relative URL without a base
      https://xn--/        rust: invalid international domain name  port: relative URL without a base

  The classification is right in every case (all four are rejected, and the
  error kind is `InvalidUrl`); only the reason is lost. Fixing it means threading
  a reason out of `UrlParser` and `UrlHost`, which have 9 and ~25 failure
  returns respectively, so it is its own piece of work rather than a one-liner.
- **`Obscura.Net` still speaks `System.Uri`, so URLs are reserialized at the
  transport boundary.** `Obscura.Browser` now keeps `UrlRecord` throughout, but
  `Response.Url`, `Request.Url` and every `ObscuraHttpClient` entry point take a
  `System.Uri`, and `Obscura.Browser.NetUrl` converts in both directions. In Rust
  there is no such boundary: `obscura-net` takes and returns the `url` crate's
  `Url`. The visible effect left is the error text for a host `UrlRecord` accepts
  and `System.Uri` rejects (`http://a..b/`: the reference reports a DNS-shaped
  transport failure, the port reports one too but with the BCL's wording).
  Removing it means moving `Obscura.Js/Url/**` into a project both `Obscura.Net`
  and `Obscura.Js` can reference - `Obscura.Js` depends on `Obscura.Net`, so it
  cannot go the other way. That mirrors the Rust tree, where `url` is a crate
  both depend on.
- **`Runtime.evaluate` drops an explicit `"value": null`.** Rust returns
  `{"type":"object","subtype":"null","description":"null","value":null}`; the
  port omits the key, so a client reading `result.value` gets `undefined` where
  Chrome and Rust give `null`.

- **`ConcurrentConnectionsHeavyPageDoNotAbortV8` is load-flaky.** It drives six
  concurrent CDP connections against a subresource-heavy page under a 30s
  deadline, and it fails intermittently when the rest of the suite is running.
  Measured by interleaving three full-suite rounds against the shim before and
  after the canvas work: the old shim failed 2 of 3 rounds and the new one 2 of
  3, so it is the test's sensitivity and not a regression. Worth noting because
  it looked exactly like a regression on a single run each way, and startup cost
  was ruled out separately (18 interleaved runs: 626ms median before, 622ms
  after).
- **`RuntimeTests.ParserImagesLoadConcurrentlyWithoutBlockingTheEventLoop` is a
  real port defect, not host-load flakiness.** Measured on this 4-core box:
  C# 4-5 passes in 8-10 runs, the Rust counterpart 8 of 8. The earlier note here
  blamed first-render font-initialization latency collapsing two events into one;
  the diagnostics say otherwise. In every failing run `__imageTimerRan` is
  already `true`, so event-loop ordering is fine. What differs is the request
  count: passing runs issue exactly 4 requests, failing runs issue 5 or 7, with
  `/one.png` and `/three.png` (one `<img>` each) duplicated. Each duplicate comes
  back with `Known == false`, which `_applyImageMetadata` turns into an `error`
  event with `naturalWidth` 0 - the assertion that actually fires. So the chain
  is: something makes `FinishAsyncImageMetadata` answer `stale` (or
  `ProfiledCachedImageMetadata` answer null), bootstrap.js re-queues the request
  on that answer, and the retry observes an unseeded cache entry. Which of the
  four `Stale` predicates in `RenderOps.FinishAsyncImageMetadata` fires is not
  yet pinned. Ruled out along the way: the `ObscuraHttpClient.ConnectCallback`
  change for IP literals (2 of 8 with the old client, so if anything worse), and
  thread-pool starvation from the harness's blocking handler - `RawHttpServer`
  now gives each connection a dedicated thread, exactly as the Rust harness's
  `std::thread::spawn` does, which removes the confound but does not change the
  pass rate.
- **`OpsTests.Read_body_capped_rejects_oversized_streamed_body` is timing-flaky.**
  The first prepared render on a fresh process costs ~300ms in embedded font
  initialization against ~1ms once warm, so a test that schedules work tens of
  milliseconds apart can collapse two events into one when the host is busy. Fix
  the latency rather than the test.

## 9. Validation

- [x] `Obscura.Parity.Tests` harness: runs a case through both binaries and diffs
- [x] `scripts/parity-sweep.sh` drives both engines over every fixture:
      **320 of 320 outputs byte-identical** (64 fixtures x text/links/html/
      markdown/assets). It now checks exit status as well, and reports a signal
      death separately rather than scoring it as a parity result - which is how
      the CLI's teardown segfault stayed hidden behind a green sweep.
- [x] `scripts/parity-sweep-scrape.sh` covers `scrape`, `serve` and the worker
      protocol: 169 cases, **165 identical**, the 4 remaining both traced to
      recorded deviations outside the CLI
- [x] `Obscura.Parity.Tests`: **306 of 307**, one skipped. Includes the 17
      `--eval` expressions and `UrlSerializationParityTests`, which pins page-URL
      serialization over 13 opaque-path cases.
- [ ] Obstacle course (companion repo `obscura-benchmark`) at 33/33
- [ ] Performance comparison vs the Rust build on the standard pages
- [ ] Re-enable CI as .NET workflows (rename off `.disabled`, rewrite for dotnet)

## Changes to the shared shim

`crates/obscura-js/js/bootstrap.js` is JavaScript, shared verbatim, and linked
rather than copied by both builds - Rust through `include_str!` in its
`build.rs`, C# through an `EmbeddedResource` link. A fix there lands in both
engines, so it closes a gap without creating a deviation. That is worth stating
because the alternative, implementing a missing feature host-side in C#, would
have created one.

- **Canvas 2D was a stub and is now a rasterizer.** The context rasterized
  exactly one thing: a solid `fillRect`. `stroke()`, `clip()`, `closePath()`,
  every transform method, all four gradient constructors, `ellipse`,
  `roundRect`, `arcTo`, the Bezier methods, `setLineDash`, `isPointInPath` and
  `isPointInStroke` were no-ops or returned inert objects, `fill()` handled only
  arc segments, and `rect()` painted immediately instead of adding to the path.
  A page that drew a chart therefore got a blank canvas, which is most real
  dashboards, since a chart is strokes and gradient fills over a transformed
  context. Now implemented: an affine transform stack, a path model that
  flattens curves and arcs in device space, scanline fill with 4x vertical
  supersampling and fractional horizontal coverage for both winding rules,
  stroking built as geometry (segment quads plus join and cap discs, dash
  splitting, so width, caps, joins and gradient strokes all come from the fill
  path), a per-pixel clip mask that `save`/`restore` carry, linear/radial/conic
  gradients and patterns sampled through the inverse transform, `drawImage`
  through the transform, and `source-over`/`multiply`/`lighter`/
  `destination-out`/`copy`. Colour parsing gained `#rgba`/`#rrggbbaa`, space and
  percentage `rgb()`, `hsl()`, and the full CSS named set.

  Validated by a 43-probe conformance script run through all three engines:
  **43 of 43 agree with headless Chromium, and the reference and the port are
  byte-identical on all 43.** On the `renderlab-complex.html` hero chart the
  alpha histogram now matches Chromium within 0.3% where the canvas was
  previously empty. Pinned by `Canvas2dConformanceTests` (11 facts).

  Glyph rendering is deliberately unchanged: `fillText` still draws the
  deterministic pseudo-glyphs the fingerprint RNG produces rather than real
  outlines, since that is a stealth surface and not a canvas gap. Only its
  placement now honours the transform, `textAlign` and `textBaseline`.
  `drawImage` from a decoded `<img>` is still unsupported, because the host
  exposes only `op_image_metadata` and never hands the shim image pixels.

- **The backing store was compositing premultiplied alpha into a straight-alpha
  buffer.** `op_canvas_register_surface` hands the buffer straight to the render
  layer, which documents it as straight-alpha RGBA and premultiplies once itself
  before the rasterizer sees it. The shim was premultiplying too, so every
  translucent pixel was darkened twice and `getImageData` reported
  `(128,0,0,128)` for 50% red where a browser reports `(255,0,0,128)`. Source-
  over is now resolved back to straight alpha, and `clearRect` scales coverage
  rather than colour. This was a pre-existing bug, not something the rewrite
  introduced.

- **The CSS named-colour table is built on first use.** It is ~150 entries and
  every frame realm re-parses this file, so at top level it was an allocation
  per realm on a path that is already startup-critical.

### Layout fixes found against the Tesserae sample suite

`crates/**` is read-only (ground rule 1), so from here these are fixed in the C#
tree only and the two engines legitimately disagree on them. Each one carries a
DEVIATION comment at the C# code that differs.

- **DEVIATION - a nested flex item was pinned from a layout where its own
  ancestors were still collapsed.** `resolve_deferred_flex_inline_sizes` pins
  every affected flex item in one pass off the intrinsic-neutral layout and only
  then restores percentages. That is right for an outermost item, whose used size
  the flex algorithm has already chosen, but a nested item gets measured inside
  ancestors whose percentage widths are still neutralized to zero, so it is
  pinned to its min-content and stays there. On Tesserae's Stack sample an
  `.tss-stack { width: 100% }` panel sat at 0 while the radio row below it was
  pinned to 448px instead of 544px, wrapping every two-word label. The port pins
  outermost-first, restoring each level's percentages and reflowing before
  measuring the next level down (`PinFlexItems` / `RestoreTypedPercentages` /
  `ResolveFunctionalInlineSizes` in `DomPassesSubgrid`).
- **DEVIATION - a cyclic *functional* inline size neutralized to `0px` instead of
  `auto`.** CSS Sizing 3 says a cyclic percentage behaves as `auto` for intrinsic
  contribution; the reference writes a definite `Px(max(value, 0))`, which is
  `0px` for the common `calc(100% - Npx)` and collapses the box for the whole
  intrinsic pass. The port neutralizes the Expression source to `Auto`.
  **Bare percentages deliberately keep the reference's zero.** Switching them to
  `Auto` as well was tried and measured over all 136 samples: it bought 0.11 mean
  abs on Code Diff and cost 0.59 on Sidebar, 0.48 on Search Box, 0.46 on
  Searchable List and 0.30 on Node View, where `width: 100%` sidebar buttons
  shrink-wrapped to their 80px min-width instead of filling their 384px row. The
  restore path puts the typed percentage back either way, so the difference is
  what the *intrinsic* pass measures; the reference's zero is closer for the bare
  form. Reverted, and the Code Diff case below stays open.
- **DEVIATION - `display: none` was ignored on an absolutely positioned
  pseudo-element.** `paint_positioned_pseudo` guards on `position: absolute`
  alone. An out-of-flow pseudo never reaches the taffy tree, so the
  `display: none` that suppresses an in-flow one is not applied anywhere else
  either and the box paints regardless. Tesserae hides an unselected radio's dot
  with `.tss-option-mark:after { display: none }` on an absolutely positioned
  pseudo, so every radio and checkbox in the samples painted as selected. The
  port also checks `Display == None` and `EffectivelyInvisible` there. Verified
  against the Rust binary, which shows the same over-paint, so this is a shared
  engine limitation.

  The earlier note here blamed the sibling combinator, from a `getComputedStyle`
  probe. That was wrong twice over: a paint-level matrix shows `~`, `+`,
  `:checked`, `[checked]` and class-sibling forms all match correctly with a
  trailing pseudo-element, and the probe itself was reading a separate gap -
  **`getComputedStyle(el, '::before'|'::after')` does not report the pseudo's
  computed style at all**, returning `display: block` and `content: ""` for
  every pseudo regardless of what the cascade resolved. That reporting gap is
  still open and is its own item below.
- **Open, not fixed: `getComputedStyle(el, pseudo)` returns defaults.** It reports
  `display: block` and `content: ""` for every `::before`/`::after`, whatever the
  cascade resolved, so it cannot be used to diagnose pseudo styling; paint is the
  only reliable signal today. Layout and paint use the real resolved pseudo style,
  so this is a DOM/CDP reporting gap rather than a rendering one.
- **Open, not fixed: `scrollbar-gutter: stable` is honoured only on the root.**
  Both engines reserve the gutter from the initial containing block only
  (`dom.rs` reads `scrollbar_gutters` off the root element alone), so a nested
  scroll container does not. Tesserae's annotated text editor overlays a
  highlight layer on a `scrollbar-gutter: stable; scrollbar-width: thin`
  textarea; the overlay comes out 902px wide against Chromium's 892px, so the
  highlight boxes sit 10px off the text they mark. A real fix also needs
  `scrollbar-width: thin` to pick the 10px gutter rather than the classic width.
- **Open, not fixed: Code Diff's two `flex: 1 1 auto` panels split their row
  evenly.** Both panels' content is percentage-sized, so with the bare-percentage
  neutralization both flex base sizes measure 0 and the row splits 462/462
  instead of Chromium's 133/791; the diff table then wraps to three times its
  height (mean abs 7.17 against a 3.08 median). Fixing it properly needs the
  intrinsic pass to measure a bare cyclic percentage as `auto` *without* losing
  the restore that a `width: 100%` button depends on - the two uses want
  different answers from the same neutralization.
- **DEVIATION - an auto-sized `<button>`'s intrinsic width ignored element
  children.** `native_button_intrinsic_content` recurses past every non-replaced
  element and counts only text plus replaced boxes, so a flex button's child
  boxes, their margins and any icon-font `::before` contributed nothing. Tesserae's
  toolbar buttons (`<i class="fi-rr-*"></i><span>Label</span>`) came out 22px short
  on every one, which shrank the label span and wrapped it, and then failed to wrap
  the button row Chromium wraps. The port counts a definite-width child as its own
  outer box, carries every child's horizontal edges, and shapes `::before`/`::after`
  content with the pseudo's own style.
- **DEVIATION - `repeat(auto-fit, minmax(<math function>, 1fr))` collapsed to one
  column.** Vendored taffy (so the reference too) counts only a bare length or
  percentage as a track's fixed component, so `min()`/`calc()` reads as
  intrinsic. An auto-repetition beside a non-fixed track invalidates the whole
  template and the grid falls back to zero explicit tracks - one implicit column
  with every item stacked. Tesserae's grids are
  `repeat(auto-fit, minmax(min(160px, 100%), 1fr))`: Chromium lays out five 177px
  columns in a 924px container, both engines laid out one 924px column. The port
  counts a resolvable calc as fixed. Verified against the Rust binary, which
  shows the same collapse, so this is a shared engine limitation rather than a
  port defect.
- **DEVIATION - a definite flex basis did not make a column item's block size
  definite.** The reference calls a box's block size definite only when `height`
  itself is a length or percentage. CSS Flexbox 9.8 also makes a flex item's main
  size definite when it has a definite flex basis in a container with a definite
  main size, and Chromium resolves descendant percentage heights against it.
  Tesserae's time-histogram bars are `height: 100%` inside a `flex: 1 1 120px`
  column item, so the reference computed them to `auto`, every bar laid out 0px
  tall, and the chart rendered as an empty box (Chromium: 107px bars in a 120px
  row).
- **DEVIATION - the CSS-wide keyword `inherit` was dropped on the box-size
  properties.** `width`/`height`/`min-*`/`max-*` are not inherited properties, so
  `inherit` has to copy the parent's computed value explicitly; the reference
  parses it as an unrecognized length and falls back to the initial value.
  Tesserae's annotated text editor sizes its textarea with `min-height: inherit`
  off a per-instance container, so every editor collapsed to a single row (58px
  against Chromium's 160/120/80). The port records a `LayoutStyle.SizeInherit`
  bitmask and resolves it against the already-computed parent in the top-down
  pass. Still open: `inherit` on `padding-*` and `margin-*` is dropped the same
  way (`padding-left: inherit` gives 0 where Chromium gives the parent's 40px);
  no Tesserae sample uses it.
- **DEVIATION - a boxed percentage image floated its flex item to the image's
  natural width.** The reference floors a content-sized flex item at every
  deferred image's natural width (its #698 fix). That is only sound when the
  image can reach that size. Tesserae's inline labels wrap a `width: 100%` SVG in
  a `width: 14px` span, and the reference lifted the whole 60px label to the SVG's
  natural width - 150px for a viewBox-only SVG (the 300x150 default object size at
  its ratio) and 512px for one with explicit dimensions. The port skips the floor
  when a box between the image and the flex item already has a definite inline
  size, since that box caps the contribution.
- **DEVIATION - `<button>` did not take the user-agent control font.** The
  reference's `button` arm sets no font, so a button inherits the page's
  font-size, family and line-height. Chromium gives every form control
  `font: 400 13.3333px Arial`, and being the shorthand it also resets
  `line-height` to `normal`, which an author rule setting only `font-size` does
  not restore. `select`, `input` and `textarea` already carried this in both
  trees; `button` was the one left out. Under Tesserae's inherited
  `line-height: 1.4` every button was 38px tall against Chromium's 21px. The
  port's `button` arm now sets all three. Still short of Chromium by the 2px
  outset UA border, which no Tesserae button shows because `.tss-btn` declares
  its own; not fixed here.
- **DEVIATION - a control's intrinsic width is rounded up.** The reference
  stores the summed width as measured. Taffy rounds used boxes to whole pixels,
  and the parts (shaped label, icon glyph, child edges) are measured separately
  with their own sub-pixel error, so a box sized at exactly its label's width can
  round down and wrap the label it was sized for: "Section Stack" measured 143px
  against a 79.5px label and broke over two lines. The port ceilings it - at most
  a pixel wide, never a pixel short.
- **DEVIATION - functional block-axis sizes resolved against the viewport
  height.** `crates/obscura-render/src/dom.rs` uses `viewport.1` as the
  percentage basis for `size_expressions[1|3|5]`. Chromium resolves a block-axis
  percentage against the containing block's content-box height, and treats it as
  `auto` when that height is indefinite. Tesserae's `.tss-card` is
  `height: calc(100% - 4px)` inside an auto-height parent, so the reference sizes
  every card to a full viewport instead of to its content
  (`height: calc(100% - 4px)` under an auto-height parent: Chromium 18px,
  reference 716px; under a definite 300px parent: Chromium 296px, reference
  716px). The port adds `Inherited.CbHeight` and applies both rules in
  `LayoutDomComputed.ResolveOneComputedStyle`. Taffy already applies them to a
  bare percentage; only the flattened functional form needed it.

- **`calc()` percentages under a resizable flex item resolved against the
  declaration, not the used width.** A row flex item with a Px width was treated
  as definite by the cyclic-inline deferral, so descendants fell back to the
  pre-layout containing-block estimate. Tesserae's page shell is
  `width: 1px; min-width: 0; flex-grow: 1`, which collapsed every
  `calc(100% - 4px)` card in the page body to 0. Fixed in both engines by
  treating a growable or shrinkable row-flex item as indefinite. Chromium
  parity on both shapes (1022px grown, 596px shrunk).
- **Still open: a shrink-to-fit block inside a flex row does not get the
  intrinsic contribution of a cyclic-percentage child.** `flexrow > block >
  width:100%` gives Chromium 8px (the child's text max-content) and both engines
  0px, because the cyclic neutralization writes a definite `0px` rather than
  behaving as `auto` for intrinsic contribution. Not a regression; predates the
  fix above.

## Known deviations

Recorded as they are decided. Each entry needs a reason and a tracking note.

### Rounded corners were parabolas

Every rounded box in both engines was a squircle. `rounded_rect_path_radii` /
`RoundedRectPathRadii` built each corner as a quadratic Bezier whose single
control point sat on the corner itself, which is a parabola: its midpoint is
6.1% further from the corner centre than a quarter circle, so `border-radius:50%`
did not draw a circle and the bulge grew with the radius. Corners are now cubic
approximations with control points at `4/3*(sqrt(2)-1)` of the radius along the
tangents. A 40px circle's worst departure from a true circle: 2.32px before,
0.51px after, against Chromium's own 0.70px.

One builder per engine feeds both the fills and the clip masks, so it was a
single change on each side. On blur.html the plain-circle cell went 1.80 to 0.20
against Chromium and the blurred-shadow cell 3.48 to 1.40, a shadow inheriting
the shape it is cast from.

Worth recording how it was found: a reader looked at the parity page and asked
why the first frame's shadow blob was a rounded square where Chromium's was
round. The first answer here was that the shape error belonged to the old shadow
algorithm, on the strength of the new halo tracking Chromium closely. That was
wrong - the halo comparison was too coarse to show a 1.2px bulge, and the plain
circle sitting beside it in the same image had carried the same error all along.

### Known deviation: backdrop-filter edge band

Two details decide how a `backdrop-filter` panel's edges look, and both were
settled by measuring against Chromium on a blurred panel over a 45-degree stripe
backdrop, not by reading the spec:

- The filter region is the element's own border box. Reaching 3 sigma further out
  to find "real" backdrop reads 13.07 mean abs against Chromium; cropping to the
  box reads 12.07.
- Out-of-region samples are transparent rather than edge-duplicated, and the
  filtered backdrop composites *over* the sharp original rather than replacing
  it. The blurred copy is therefore partly transparent in a band about 3 sigma
  wide and the unblurred backdrop shows through, which is what produces
  Chromium's gradient from the local colour at the very edge to the blurred
  average further in. That reads 3.97.

The residual 3.97 is the shape of that band: a three-pass box blur with
transparent edges is not bit-exact with Skia's own. Both engines agree with each
other to 0.01.

### Reference gaps fixed in both engines

These were found by comparing against Chromium, were present identically in
Rust and C#, and were fixed on both sides rather than papered over in the port.

- **Blurred `box-shadow` painted as a solid blob.** Both engines ramped alpha
  only outward from a fully opaque shape at a uniform per-layer alpha, so a wide
  blur read as a solid shape with a linear skirt. The spec's blur is a gaussian
  of sigma = blur/2, so coverage is ~50% at the shape edge. Layers now march
  inward from 2.5 sigma. Mean absolute difference against Chromium over the
  affected region: 62.4 -> 3.5.
- **`radial-gradient` dropped absolute-length stop positions.** Only
  percentages survived parsing, so `transparent 32rem` became an unpositioned
  stop and the ramp spread over the whole ending shape. Radial layers now carry
  the authored strings to paint like linear layers already did, and resolve them
  against the gradient ray.
- **The `background` shorthand dropped its color layer** whenever any gradient
  layer parsed, so `background: linear-gradient(...), #00f` composited over
  whatever was behind the element instead of over blue.

Together the last two took `test-html-files/renderlab-complex.html` from 92.8%
of pixels differing from Chromium (47.3 mean abs) to 37.7% (21.9) at 640px.

All four of the gaps that page listed are now implemented in both engines, with
one attribution on it corrected below.

- **`filter: blur()`** now parses and paints. The kernel is the three-pass box
  approximation SVG's `feGaussianBlur` defines normatively, which is what CSS
  `blur()` is specified in terms of, so both engines land at 0.03 mean abs
  against Chromium (was 17.15) with a byte-identical sampled row. Only a
  blur-only filter list is recorded; any other function stays unimplemented
  rather than being reduced to its blurs, and `@supports` reports exactly that.
- **`backdrop-filter: blur()`** now paints: 3.97 mean abs over a blurred panel
  against Chromium, versus 97.46 for not implementing it. Two edge details were
  measured rather than reasoned about, and both matter (see the deviation below).
- **Inset `box-shadow`** now paints, over the background and under the border
  where CSS Backgrounds 3 puts it: 8.53 to 1.43 on its cell.
- **Auto-width `<button>`** now measures its label through the inline engine
  rather than `text_width`, so the box fits the text that will be laid out in it.

**Correction.** The parity page called `backdrop-filter` "the largest remaining
contributor to the renderlab fixture's difference against Chromium". That was
inferred from a screenshot, not measured, and it is wrong. Implementing it moved
the fixture's whole-page mean abs from 21.86 to 22.41 at 640px - i.e. not at all,
within the noise of the other changes in the same commit range. The fixture has
exactly one `backdrop-blur` element in layout, the 640x69 sticky header, which is
0.75% of a 9,168px page and sits over a near-uniform backdrop.

Measured band by band, the fixture's remaining difference is **cumulative
vertical drift**, not any unimplemented paint feature:

| band (640px wide) | mean abs vs Chromium |
|---|---|
| y 0-300 | 15.97 |
| y 600-900 | 6.40 |
| y 900-1200 | 4.92 |
| y 1800-2100 | 18.20 |
| y 2400-2700 | 34.76 |

It is low near the top and saturates further down: the document is 9,168px against
Chromium's 9,274px, so once the two disagree on a block's height everything below
it is offset and the pixel difference stops being about that block's rendering.
That gap follows from the engine embedding its own faces instead of using system
fonts, which is a deliberate policy, so paint work cannot close it.

- **Stealth TLS impersonation is not ported.** `wreq`/BoringSSL fingerprints the
  ClientHello; .NET's `SocketsHttpHandler` does not expose that surface and every
  managed workaround needs a native TLS stack, which the port's "V8 only" rule
  forbids. `--stealth` in the C# build applies the JS, header, and identity
  surfaces and logs that TLS impersonation is inactive.
- **HTML parsing delegates to AngleSharp** instead of porting `html5ever`.
  AngleSharp is fully managed and spec-compliant; its DOM is adapted into
  Obscura's arena tree at parse time and never escapes `Obscura.Dom`. Two behaviors
  html5ever exposed through its `TreeSink` are reimplemented on the adapter because
  AngleSharp has no equivalent: declarative shadow roots
  (`<template shadowrootmode>`, including the valid-shadow-host allowlist) and the
  MathML `annotation-xml` integration-point flag, which is recomputed from the
  element's `encoding` attribute. Quirks mode comes from `IDocument.CompatMode`,
  which is `BackCompat` for full quirks only, matching `QuirksMode::Quirks`.
- **`DomTree.GetNode` returns the live arena node, not a clone.** Rust's
  `get_node` clones and offers `with_node_mut` for mutation; in C# the node is a
  class, so one accessor covers both and op_dom mutates through it directly.
- **Selector queries throw `SelectorParseException` on an invalid selector**, where
  Rust returns `Result<_, String>`. `TryQuerySelector*` / `TryMatchesSelector`
  mirror the Rust shape; `op_dom` must use those (or catch) so a bad selector
  yields an empty result rather than a JS error, as it does today.
- **Rasterization uses SkiaSharp, not a hand-written managed rasterizer.**
  Decided after the Apache-2.0 review below. This adds `libSkiaSharp` and
  `libHarfBuzzSharp` to the native dependency set (V8 plus two). The trade is
  deliberate: tiny-skia is a port of Skia, so pixel behavior tracks the Rust
  engine more closely than a from-scratch rasterizer would, Skia also supplies
  the image codecs, and HarfBuzz covers complex-script shaping that would
  otherwise have to be written by hand.
- **SixLabors packages are not used.** ImageSharp 3.x+, ImageSharp.Drawing 2.x+,
  and Fonts 2.x+ moved off Apache-2.0 to the Six Labors Split License, and
  Drawing 3.x fails the build outright without a license key. Only the frozen
  older versions are Apache-2.0, and they do not form a compatible set.
- **`set_v8_flags` maps flags onto typed constraints instead of passing a flag
  string to V8.** ClearScript does not expose `v8::V8::set_flags_from_string`;
  it surfaces the same settings as `V8RuntimeConstraints` properties and a small
  `V8GlobalFlags` enum. The heap-sizing flags an embedder actually uses
  (`--max-old-space-size`, `--max-semi-space-size`, `--max-young-generation-size`)
  and a few global toggles map across; anything else is reported through
  `V8Flags.Warned` and ignored rather than silently dropped. The late-call
  refusal is preserved exactly, because a late flag call aborts the process.
- **Legacy code pages need no package on net10.0.** `encoding.rs` needs the whole
  WHATWG legacy set (GBK, Big5, Shift_JIS, EUC-JP/KR, windows-125x, ISO-8859-x),
  which older .NET Core releases only had via `System.Text.Encoding.CodePages`.
  On `net10.0` `CodePagesEncodingProvider` is in the shared framework, so
  `Obscura.Net` registers the provider and takes no package reference at all;
  the `PackageVersion` entry in `Directory.Packages.props` is unused and can be
  dropped.
  The three pages the framework still lacks (ISO-8859-10, ISO-8859-14,
  ISO-8859-16) plus `x-user-defined` are served from in-tree 96-entry index
  tables in `Encoding/SingleByteTables.cs`.
- **The WHATWG label table is ported in tree.** `encoding_rs::Encoding::for_label`
  has no .NET equivalent (`Encoding.GetEncoding("gbk")` resolves to code page 936
  whose `WebName` is `gb2312`, not the canonical `GBK`), so
  `Encoding/WhatwgEncoding.cs` carries the standard's label -> canonical-name
  table and maps canonical names onto code pages. `label_name` and
  `document.characterSet` therefore report the WHATWG spelling, as in Rust.
- **`ObscuraHttpClient` follows redirects by hand and owns cookies.**
  `SocketsHttpHandler` is configured with `AllowAutoRedirect = false` and
  `UseCookies = false` so the SSRF gate, the CORS check and the `CookieJar` see
  every hop, exactly as the reqwest client does with `Policy::none()`.
- **The DNS-time SSRF guard is a `ConnectCallback`, not a resolver plug-in.**
  reqwest takes a `dns_resolver`; `SocketsHttpHandler` has no equivalent, so
  `SsrfGuardResolver` resolves the name and checks every returned address inside
  `SocketsHttpHandler.ConnectCallback` before the socket is dialled. Same
  deny-set, same failure message, and it covers redirect hops because each hop
  opens its own connection through the same callback. There is one transport
  rather than reqwest + wreq, so the two-implementation drift risk is gone.

  The two hooks do not fire on the same set, and that difference was a bug. A
  `dns_resolver` runs only for a host that needs resolving, so the reference
  never sees an IP-literal endpoint; a `ConnectCallback` fires for every
  connection. With `HTTPS_PROXY=http://127.0.0.1:PORT` set - this sandbox, and
  most corporate networks - the port refused its own proxy with
  "SSRF blocked: '127.0.0.1' resolves to forbidden address 127.0.0.1" while the
  reference proxied normally, and the only workaround was
  `--allow-private-network`, which disables the guard entirely. So the callback
  now skips an IP-literal endpoint, which is exactly the set the reference's
  resolver never inspects. A literal *target* is still refused, by `ValidateUrl`
  on entry and on every redirect hop, which is where Rust rejects it too, and a
  hostname resolving to a private address is still blocked by the resolver.
  Pinned by `ProxyEndpointNotSsrfBlockedTests`.
- **`SSL_CERT_FILE` / `SSL_CERT_DIR` roots are additive and cached per value.**
  .NET has no `add_root_certificate`, so the roots are applied through a
  `RemoteCertificateValidationCallback` that first honours the platform trust
  store and only then rebuilds the chain against the configured roots with
  `X509ChainTrustMode.CustomRootTrust`. Rust caches the parsed roots once per
  process in a `OnceLock`; the port keys the cache on the current
  `(SSL_CERT_FILE, SSL_CERT_DIR)` values so tests that change the environment in
  one process still see the right store.
- **Request header order is not reproduced.** The Rust client builds a `HeaderMap`
  in Chrome's navigation order; `HttpRequestMessage` serializes in its own order.
  Every header value matches; only the ordering differs, which the stealth
  surfaces would care about and the tracked TLS gap already covers.
- **`Obscura.Net` no longer references `Obscura.Dom`.** `crates/obscura-net` has no
  `obscura-dom` dependency; the scaffold's project reference was removed so the
  two areas can be built and tested independently.
- **Obscura.Dom selector-engine differences**, all verified as behavior-preserving:
  - The bloom filter uses one djb2 hash where Rust uses two different hashers.
    Self-consistent between the selector and element sides, so the only effect
    is a marginally higher false-positive rate, never a missed match.
  - No `NthIndexCache`; `:nth-*` indices are recomputed per match. Performance
    only, identical results.
  - `GetNode` returns the live node rather than Rust's clone plus
    `with_node_mut`. One accessor covers read and write.
  - Declarative shadow roots and the MathML integration-point flag are
    reimplemented on the AngleSharp adapter, because AngleSharp implements
    neither html5ever hook.
  - Carried over faithfully and worth knowing because they look like bugs:
    `::before`/`::after`, `::part()` and namespace-prefixed selectors are parse
    errors, `:scope` falls back to `:root`, and `:visited`/`:hover`/`:active`/
    `:focus*` never match. Confirmed against the Rust binary that a parse error
    yields an empty NodeList rather than an exception, so `op_dom` must use the
    `Try*` query variants.
- **UTS 46 IDNA mapping is partial.** The full per-code-point table is not
  shipped, so unassigned and disallowed code points outside the implemented
  classes are accepted where the reference rejects them, and a few compatibility
  mappings produce different punycode. Measured over 12,000 random wide-Unicode
  hosts the divergence is strictly one-directional: 7,428 accepted-by-port /
  rejected-by-Rust, and 0 rejected-by-port / accepted-by-Rust. No valid hostname
  is broken. `CheckBidi` and `CheckJoiners` are not implemented; NFC is
  composition-only, so input needing full decomposition first still diverges.
- **The public suffix list is curated, not complete** (~450 multi-label suffixes
  against the real list's ~10,000). The algorithm is complete, and the implicit
  wildcard rule makes every single-label TLD correct, but a missing multi-label
  suffix would let `document.domain` relax one label further than the reference.
- **`DomTextMeasure` reproduces ab_glyph's height-based scale, not Skia's em-based
  one.** `PxScale::from(px)` sizes a glyph so hhea's ascender-minus-descender is
  `px` tall; `SKFont.Size` sizes the em square. Liberation Sans is 2288 units
  against a 2048 em, so measuring em-based made every advance 10.5% wider than
  the reference reports, per face: Sans 1.1172, Serif 1.1074, Mono 1.1328, DejaVu
  Sans 1.1641. Since `text_width` sizes auto-width `<button>` and `<select>`
  boxes, RenderLab's "Launch simulated demo" button was 211px against the
  reference's 196px, the label wrapped differently, and the document came out 16px
  shorter at a 640px viewport - a 21.8% whole-page pixel difference, because every
  row below the divergence was offset. Now 2.2%, which is edge shading. Advances
  are summed in font units and scaled once, because Skia quantizes at a fractional
  size and left three of seven calibration cases a pixel out. `PaintText.DrawText`
  takes the same conversion, or the static-font glyphs would be drawn 11% larger
  than the reference paints them and sit on a different baseline. Pinned by
  `DomTextMeasureScaleTests`.
- **Grid `calc()` handles use a weak registry, not a raw pointer.** Rust hands
  taffy the `Arc` address as the opaque handle. Managed objects have no stable
  address, so the port allocates an aligned counter handle and keeps a weak
  registry that the resolver looks up; `LayoutStyle` still owns the strong
  references exactly as in Rust. A handle whose expression has been collected
  resolves to 0.
- **`parse_linear_gradient` does not reproduce a Rust panic.** For a value
  ending exactly in `linear-gradient(`, Rust slices out of range and panics; the
  port clamps and returns "no gradient", because style application must never
  throw.
- **SVG rasterization is an in-tree reimplementation, not a binding.**
  `usvg`/`resvg` have no managed counterpart. The port covers shapes, paths,
  groups, transforms, `viewBox`/`preserveAspectRatio`, `use`/`symbol`/`defs`,
  gradients, patterns, `clipPath`, text over the bundled font database, and
  presentation-attribute inheritance. It does NOT implement SVG filters, masks,
  markers or `textPath`; nothing in paint.rs reaches them today, and an asset
  using them renders without those effects rather than failing. Paint-server
  recursion is capped at depth 4, because a self-referential pattern fill
  overflowed the stack.
- **Image resampling is self-consistent, not bit-identical to Rust.** A Lanczos3
  implementation stands in for `image::imageops::resize`. The capture test
  proves the fallback path is taken and deterministic, not that samples match
  the Rust `image` crate byte for byte.
- **ClearScript cannot tell a static import from a dynamic one.** deno_core's
  loader gets an `is_dyn_import` flag; `DocumentLoader` does not, and
  `engine.Compile` triggers no loads, so a compile/evaluate split cannot
  separate them either. The port brackets static roots with
  `BeginStaticGraph()`; a top-level `import()` inside such a bracket is
  misclassified as static. Leaving the bracket off counts every load, which is
  the conservative direction. This is the one place the module port is a
  heuristic rather than a port.
- **`document.write` re-parses the accumulated stream per call.** AngleSharp
  exposes no resumable tree builder, so the stream identifies nodes by
  child-index path instead of arena id and costs O(whole stream) per write
  rather than O(new text). The algorithm is otherwise ported unchanged. Whether
  an element is still open is recovered with a text probe rather than
  html5ever's `trace_handles`, so inside `<table>` the open set is approximate
  where foster-parenting moves the probe; it only matters for `script` and
  `template`, which are not foster-parented.
- **Two op payloads cannot be byte-matched, and neither can be.**
  `op_computed_style` and the `op_fetch_url` header map are built by Rust from a
  `HashMap`, whose iteration order is randomized per process. No byte parity is
  achievable in either direction; the key sets and values match, and the port
  uses insertion order.
- **`op_fetch_url` has no stealth branch.** Rust routes scripted fetch through
  `StealthHttpClient::send_single`; the deferred stealth transport has no
  equivalent, so scripted fetch falls through to the ordinary client.
- **Cross-realm objects cannot be shared.** ClearScript refuses a `ScriptObject`
  from another engine, so `publish_realm_objects` cannot hand the page a
  frame's live `window`/`document`. Same-origin `contentWindow.someGlobal` and
  `contentDocument` do not resolve to the frame's real objects. The port
  registers an empty entry rather than a copy, because a copy would look live
  and silently not be.
- **No ICU locale control.** Rust calls `v8::icu::set_default_locale("en-US")`;
  ClearScript exposes no ICU entry point. `Intl.*` and `resolvedOptions().locale`
  can therefore leak the host locale while `navigator.language` reports en-US,
  which is a regression of issue #734.
- **The heap cap only exists once configured.** Rust arms at V8's own default
  limit, so an unconfigured page is still protected; here protection begins only
  after `--max-old-space-size` or `SetHeapLimit`, and below that V8's internal
  OOM still aborts the process. The violation policy is also load-bearing:
  `Exception` raises an ordinary script error that page JS can simply catch,
  defeating the cap, so the port uses the uncatchable `Interrupt` plus a
  raise-collect-restore recovery.
- **The CLI exited on a signal after succeeding, and now does not.** About one
  run in ten exited 139 (SIGSEGV) having produced complete, byte-identical
  output: the crash lands in native shutdown after `main` returns and after
  stdout is flushed. Localized by rate: 0 of 150 for `--version`, which builds
  no isolate, against 17 of 150 for `fetch about:blank` and 20 of 150 for a
  `--dump`, so it tracks having created a V8 isolate rather than anything about
  the page. It predates this branch (the base commit measured 19 and 12 crashes
  per 320 sweep cases against 15 and 7 for the current build), and it was
  invisible because the sweep compared stdout without checking exit status.
  `ProcessExit.Immediately` ends the process with libc `_exit` once the streams
  are flushed, which skips the `atexit` chain the crash lives in.
  `Environment.Exit` does not help (26 of 150) because it still runs that chain.

  Stressed, with the pre-fix commit built as a matched control and both binaries
  run back to back on the same machine state:

      serial, fetch about:blank      pre-fix 29/150 and 16/150   fixed 0/150 twice
      serial, 1500 runs              -                           fixed 0/1500
      4-way parallel, mixed, 3000    pre-fix 4/3000              fixed 0/3000

  The parallel harness is the weaker test and it is worth knowing why: process
  contention widens the window the race needs, so the same fault that fires
  about 15% of the time serially fires 0.13% of the time under four-way
  parallelism. Measure this one serially.

  Pinned by `ProcessExitTests`, deliberately probabilistic, and
  `scripts/parity-sweep.sh` now reports a signal death instead of scoring it as
  a parity result. No new native dependency: libc is the platform.
- **ClearScript names V8 script documents its own way, so `Error.stack` text
  differs.** Line and column always match; only the script name does. Two
  symptoms, one cause, both measured against the reference binary:
  - A `file:` script URL loses its scheme, because ClearScript names a
    URI-based document by `Uri.LocalPath` when the URI is a file URI:

        rust:  at inner (file:///tmp/stack.html:2:26)
        port:  at inner (/tmp/stack.html:2:26)

    `http`, `https` and `about` URLs print verbatim and do not diverge.
  - A host-internal script (`<eval>`, `<eval-remote>`, `<done?>`) is named, not
    URI-based, and ClearScript appends a uniqueness counter and a transient
    marker: `<eval> [5] [temp]` where deno_core reuses `<eval>` every time.
    `DocumentFlags.None` drops the ` [temp]` but not the counter, so the name is
    still unstable per call.

  There is no supported fix. `DocumentInfo` exposes `Name` and `Uri` as
  getter-only with two mutually exclusive constructors, so a document cannot
  carry a URI and an overridden name; `ScriptEngine.DocumentNameManager` is
  internal. Naming file scripts by string instead would trade the missing scheme
  for the uniquifier and also drop the URI V8 uses as `import()`'s referrer.
  This is visible to a page that parses `Error.stack`, and on the CDP wire in
  `RemoteObject.description` and `exceptionDetails.exception.description`.
- **Every frame re-parses bootstrap.js.** There is no snapshot equivalent, so a
  frame realm cannot be restored from a prebuilt context. Correctness holds;
  per-frame startup cost does not.
- **One process, many isolates.** ClearScript allows multiple V8 isolates per
  process, so the Rust "one isolate per process" constraint (and the
  process-per-test requirement) does not apply. Tests run in-process.

- **`Page` is `IDisposable` and assigning `Page.Js` disposes the previous
  runtime.** Rust drops the old `Option<ObscuraJsRuntime>` on assignment and the
  isolate goes with it; ClearScript needs an explicit `Dispose`, and a leaked
  `V8ScriptEngine` wedges the process. The property setter therefore has the
  side effect Rust's move already had, which is what makes `init_js`,
  `suspend_js` and `navigate_blank` port line for line.
- **`PageError` is an exception, not a return value.** Every Rust caller of
  `navigate*` branches on `Err`, so `PageException` carries a `PageErrorKind`
  and, for `TooManyClientNavigations`, the limit; the messages are the ones
  `thiserror` renders. Same for `RasterPdfError` -> `RasterPdfException`.
- **The navigation deadline is a `CancellationTokenSource`, not a dropped
  future.** `tokio::time::timeout` cancels the inner future at its next await
  point; the port threads the token into every HTTP call and awaits on the
  navigation path, which cancels at the same points. A cancelled navigation sets
  `LifecycleState.Failed` and reports the same
  `navigation exceeded {ms}ms deadline` message.
- **`RasterPage` releases its raster explicitly instead of relying on the GC.**
  Rust's `page_rasters_are_released_before_capturing_the_next_page` proves the
  previous page's decoded raster is gone with a `Weak` probe. A managed
  equivalent would depend on when the GC runs, so `encode_pdf_pages` calls
  `RasterPage.Release()` in a `finally` before requesting the next page and the
  test asserts on that. Same invariant, deterministic.
- **`Obscura.Browser` takes a direct `SkiaSharp` reference** for the PDF
  exporter's JPEG encode and PNG decode, which is what the `image` crate does
  for `pdf.rs`. No new native dependency: it is the same `libSkiaSharp` the
  render layer already loads.
- **The browser test suite sets `OBSCURA_ALLOW_PRIVATE_NETWORK=1` once, in a
  module initializer.** Every fixture server binds 127.0.0.1. Page-driven
  fetches go through the context's client, which the fixtures build with
  `allowPrivateNetwork: true`, but the ES-module loader owns a standalone client
  whose only opt-in is the environment variable - in Rust too. The Rust suite
  ends up relying on the variable being set process-wide by the handful of tests
  that set it explicitly; the port sets it up front rather than depending on
  test order.

### Bugs found in the layers below Obscura.Browser (not fixed here)

Each of these is pinned by a written-but-skipped test in
`Obscura.Browser.Tests` or `Obscura.Js.Tests`, with the blocker named in the
skip reason.

- **`op_frame_document_ready` records the wrong parent frame.** It reads the
  calling realm from `ObscuraOps.RealmState()`, which resolves
  `RealmStates.Current` - and the runtime only sets that around *synchronous*
  host entries into a realm. `bootstrap.js` calls the op from inside
  `fetch(...).then(...)` in `_loadIframeSrc`, so a frame created by a frame's
  script is queued with `parentFrameId = 0` (the page) instead of its real
  parent. Rust reads the parent from V8's entered-or-microtask context, which is
  correct for an async continuation. Consequences: `window.parent`/`top` in a
  doubly-nested frame point at the page, and the detach sweep cannot discard a
  grandchild when its parent frame is removed. Pinned by
  `PageTests.DetachingAParentDiscardsItsQueuedDescendantWork`.
- **There are two import maps.** `ObscuraJsRuntime` builds
  `new ObscuraModuleLoader(baseUrl, proxyUrl)`, which allocates its own
  `new ImportMap()`, while `op_add_import_map` writes into
  `ObscuraState.ImportMap`. Any import map registered from page JavaScript (a
  script-inserted `<script type="importmap">`) is therefore silently dropped;
  only maps registered through `ObscuraJsRuntime.AddImportMap` (the path `Page`
  uses for parser-discovered maps) take effect. Rust shares one
  `Rc<RefCell<ImportMap>>` between the op state and the loader
  (`runtime.rs`: `let import_map = state.borrow().import_map.clone();`). Pinned
  by `PageTests.DynamicallyInsertedImportMapControlsLaterDynamicImport` and
  `PageTests.DynamicImportMapUsesLiveDocumentBaseAtInsertion`.
- **`unhandledrejection` is never dispatched.** `DenoCoreShim` stores the
  callback `Deno.core.setUnhandledPromiseRejectionHandler` registers and nothing
  ever invokes it, so the `PromiseRejectionEvent` bootstrap.js builds never
  fires. The other half of the invariant already holds: a rejected background
  promise does not stop the page event loop. Pinned by
  `UnhandledRejectionTests.RejectedBackgroundPromiseDoesNotStopThePageEventLoop`.
- **A dynamic `import()` is resolved synchronously.** ClearScript runs
  `DocumentLoader` while the calling script is still executing, so a lazy module
  graph is fetched inside the script-execution phase instead of being left as
  post-load work; deno_core defers it to the event loop. This is the same root
  cause as the existing "ClearScript cannot tell a static import from a dynamic
  one" deviation, but it also changes *when* the work happens. Pinned by
  `PageTests.LazyModuleGraphIsPostLoadWorkUntilCallerSettles`.
