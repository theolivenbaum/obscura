# Obscura C# port - status and work queue

Target: `net10.0`. Reference implementation: the Rust workspace in
`.reference/obscura/crates/`, synced to the upstream commit named in
`.reference/obscura/UPSTREAM.md`. Paths below written `crates/...` or `vendor/...`
are relative to `.reference/obscura/`. Rules and conventions: see `CLAUDE.md`.

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
- [x] Move the Rust tree and everything upstream-only into `.reference/obscura/`; the
      root README, `LICENSE` (MIT, Curiosity GmbH) and `NOTICE` are the port's
- [x] NuGet packaging metadata in `dotnet/Directory.Build.props`; publish pipeline
      in `.devops/build-nuget.yml`
- [x] Upstream sync procedure in `CLAUDE.md` ("Merging upstream changes")
- [x] Rename the port to PocketCalculator: projects, namespaces, types, packages, the
      `pocket-calculator` executable and `POCKETCALCULATOR_*` env vars; wire names keep
      Obscura's (CLAUDE.md, "Naming")

## 1. PocketCalculator.Dom  (<- crates/obscura-dom, ~5.2k lines)  -  81/81 tests green

- [x] `tree.rs` -> `DomTree`, `Node`, `NodeId`, `NodeData`, shadow roots, slots
- [x] `tree_sink.rs` -> HTML parsing via AngleSharp adapted into the arena tree
- [x] `selector.rs` -> selector parsing, matching, specificity (`PocketCalculator.Dom.Selectors`)
- [x] `serialize.rs` -> `innerHTML` / `outerHTML` serialization
- [x] Unit tests ported (81 facts: 29 tree, 14 tree_sink, 31 selector, 7 serialize)
- [~] Parity: parse + serialize a corpus through both engines. Validated ad hoc against
      `target/release/obscura` over `render-repros/**`: 57/64 fixtures byte-identical for
      parse + serialize (the other 7 differ only where the Rust run executed page script or
      injected engine markup), and 3323 (selector, fixture) `querySelectorAll` count
      comparisons over the 57 script-free fixtures with no semantic divergence. A standing
      parity test needs `PocketCalculator.Cli`, which the harness shells out to.

## 2. PocketCalculator.Net  (<- crates/obscura-net, ~5.6k lines)  -  94/94 tests green

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

## 3. PocketCalculator.Js  (<- crates/obscura-js, ~44k lines; 15.8k of it is shared JS)

- [x] Share `bootstrap.js` with the Rust tree by linking it as an embedded resource
- [~] `runtime.rs` -> `PocketCalculatorJsRuntime` on ClearScript (3704)
      - [x] 80 of 95 public methods; watchdog, heap cap, event loop, CDP object
            store, module graphs, frame realms
      - [x] the 15 `screenshot_*`/render-seeding methods (the Page-capture
            boundary), in `Runtime/PocketCalculatorJsRuntime.Capture.cs`, plus
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

## 4. PocketCalculator.Render  (<- crates/obscura-render + vendor/taffy)  -  COMPLETE, 621 tests green

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
- [x] Unit tests ported. Only skip left in PocketCalculator.Render.Tests is the cascade
      microbenchmark Rust itself marks `#[ignore]`.
- [ ] Parity: render `render-repros/**` fixtures in both engines and compare

## 5. PocketCalculator.Browser  (<- crates/obscura-browser, ~9.8k lines)  -  91/96 tests green, 5 skipped on PocketCalculator.Js gaps

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
      4 context.rs, 4 across `tests/`), 87 green, 5 skipped on named PocketCalculator.Js
      gaps (see Known deviations), plus 4 written for the `UrlRecord` migration
- [x] URLs go through `PocketCalculator.Js.Url.UrlRecord`, not `System.Uri`. `Page.Url` is
      a `UrlRecord`, `PageUrl` sits on the ported WHATWG parser, and
      `Page.UrlString()` returns its serialization, so a `data:` URL on the CDP
      wire and in `location.href` matches the reference. `PocketCalculator.Browser.NetUrl`
      is the remaining conversion at the `PocketCalculator.Net` boundary (see Open issues)
- [ ] Parity: navigate a fixture corpus, compare DOM + text + links

## 6. PocketCalculator.Cdp  (<- crates/obscura-cdp, ~12.7k lines)  -  279/279 tests green

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

## 7. PocketCalculator.Mcp  (<- crates/obscura-mcp, ~3.2k lines)

- [x] `lib.rs` -> stdio MCP server and tools (2077) - 37 tools, tool list
      byte-identical to the Rust `json!` source (pinned by a differential test)
- [x] `http.rs` -> HTTP/SSE transport (462)
- [x] Integration tests ported (16 found, 16 ported, 16 passing)
- [ ] Parity: identical tool listings and tool-call results

## 8. PocketCalculator.Cli + Obscura  (<- crates/obscura-cli, crates/obscura, ~6k lines)  -  177/178 tests green

- [x] `main.rs` -> `fetch`, `serve`, `scrape`, `mcp`, global flags (1946).
      `serve` had five real defects, `scrape` one protocol bug, and every
      numeric option went through a parser that could kill the process where
      clap prints a usage error; see the commit.
- [x] `worker.rs` -> the `pocket-calculator-worker` binary for parallel scrape (165)
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

## Upstream sync 727cc46..1a3169d

Reviewed 2026-09-23; detail per commit in `dotnet/docs/upstream-review-2026-09.md`.
`.reference/obscura/` stays at `727cc46` until these are ported or declined. Ordered
by severity; one upstream fix per commit, citing the upstream sha.

Security - all ported (2026-09-23; deviations under "Known deviations", "Upstream security
ports 727cc46..1a3169d"):

- [x] `04418a5` G - MCP HTTP: origin allowlist, Content-Type, token, limits, no `file://`
- [x] `04418a5` A - hide `Deno.core.ops` from page script
- [x] `97ff86d` + `99647b4` - route render resource loads through the page transport
      (SSRF guard, blocklist, cookies, proxy); DOM-only screenshots stay network-free
- [x] `04418a5` C - fetch/XHR response confidentiality (`Set-Cookie`, unexposed
      headers, `no-cors`); `op_fetch_url` gains the `internalLoad` argument
- [x] `04f0475` + `05846de` - CORS preflight enforcement, CORS check per redirect hop
- [x] `ebe5973` - drop credentials on cross-origin redirects; only POST downgrades on 301/302
- [x] `04418a5` D - iframe same-origin check against the final URL
- [x] `4778192` - `op_navigate` must not move the page URL before commit
- [x] `b369f78` - `document.cookie` cannot write or delete HttpOnly cookies
- [x] `04418a5` B - cross-origin stylesheet confidentiality
- [x] `04418a5` F + `0671d94` - CDP token, Origin refusal, Host check; endpoint from Host
- [x] `04418a5` E - cookie jar RFC 6265bis rules, host-only persistence (`hostOnly` key)
- [x] `04418a5` H, `c2e6fb2`, `8395f29` - calc() nesting guard, random-bytes cap,
      oversized transform layer skipped rather than failing the capture

Security follow-ups found while porting (not upstream fixes):

- [x] `__obscura_markTrusted` and the other capability helpers are no longer page-visible:
      they reach host script only as the `__obscura_host` parameter (`HostScript`)
- [x] Page-initiated navigations carry their initiator (document URL, user activation):
      cross-site ones withhold SameSite=Strict, a cross-site POST or iframe also Lax;
      Sec-Fetch-Site/User/Dest, Referer and Origin match Chromium 140
- [x] `no-cors` requests may carry `Authorization`: fixed. The shim's Headers/Request/fetch
      apply the Fetch "request" / "request-no-cors" guards and op_fetch_url filters again
      host-side. Also closed: `mode: "same-origin"` or an unknown mode read a cross-origin
      body with no CORS check
- [x] `ImageAgent` has no SSRF check; it is reachable only from standalone `RenderPaint`
      callers with no page (as upstream), since `Obscura.Render` cannot see `SsrfGuard`
- [x] `serve --workers` behind a DNS Host name: a156914's forwarded authority is ported as
      `POCKETCALCULATOR_CDP_FORWARDED_HOST/PORT`
- [x] The CDP remote-object store (`__obscura_objects`, `__obscura_oid`, `__obscura_await_meta`,
      `__obscura_await_rejected`, `__obscura_done_N`) and the frame registries
      (`__obscura_frameObjects/Elements/Windows`) are page-readable and page-writable, so a
      page can read or forge a client's handles and awaitPromise results. Moving them behind
      the host wrapper changes how client Runtime.evaluate code runs (sloppy mode, `var`)
- [x] `el.dispatchEvent(ev)` does not clear `isTrusted` as DOM requires, so page script can
      re-dispatch a trusted event it received (check Chromium first)
- [x] Host snippets build events with the page's current `Event` / `MouseEvent`
      constructors, which a page can replace; capture them at bootstrap
- [x] Every `__obscura_*` name is still detectable with `'name' in window`; the hide list
      only filters reflection (moved into closure state, I10)
- [x] Referrer policy: `<meta name=referrer>`, `referrerpolicy` and `rel=noreferrer` are not
      supported (Chromium honours all three). Still not implemented: meta refresh and
      followed `window.open`. Chromium 141's default does not block third-party cookies,
      so the port matches it; `Partitioned` cookies are keyed by top-level site
- [x] fetch: `User-Agent` stays settable from script (check whether Chromium ignores it);
      an `Origin` header is sent on no-cors GET/HEAD (Fetch and Chromium omit it);
      CONNECT/TRACE/TRACK are not refused (with open item `2e752b9`); response Headers are
      not immutable; XHR `setRequestHeader` with an invalid name fails at send, not with a
      synchronous SyntaxError
- [ ] A `serve` process with stdin closed may still hit the fd-0 close at shutdown in
      code paths other than the accept thread fixed here; audit `Socket.Dispose` under a
      blocked call

Correctness:

- [ ] `a156914` (CDP part 3, forwarded authority and multi-worker balancer, is ported) - CDP mouse/pointer/focus order, event path phases, event classes,
      window named properties, `:scope`, renderer hit test (on the port's paint order)
- [ ] `2b07b76` - `document.write` script ordering
- [ ] `6aef52d`, `61ec5b3`, `dc5e60e` - id index on removeAttribute, node identity,
      CharacterData ranges
- [ ] `729c264`, `00dd6c7`, `2e752b9` (Chromium casing), `25af144`, `f645df2`,
      `88d2174` - URL setters, btoa, fetch method, parsererror text, enumerable IDL
      operations, HTMLSlotElement
- [ ] `ec62004`, `a161a8d`, `f81c296`, `403356f`, `94e857b`/`b0ccbbe`, `dfc546d`,
      `20a3e02`, `4383793`, `dc88742`, `fa0362b` - CDP input, accessibility, sessions,
      handles, by-value undefined, Fetch domain, history navigation
- [ ] `af955b3` - release memory after the last CDP client disconnects
- [ ] `d1e3e77`, `9ced496`, `61f8e68`, `bf11721`, `d6c9ef5` - multi-valued headers,
      `OBSCURA_BLOCK_TRACKERS`, MCP network and console history, navigator plugin interfaces
- [ ] `aaf189f` - float boundary subdivide throw; `c68fa2b` - overline and line-through
- [ ] `88998d6`, `deef294` (partial) - `indeterminate`, live `:checked`, cloneNode form
      state, `form.reset()`
- [ ] `d579bdd`, `343fdc7` (cache only) - redundant layout/shaping, shared font set;
      benchmark before and after

Decisions:

- [ ] `d792bae` - keep every CDP page's isolate live; conflicts with "Page suspension
      stays". Measure memory, then decide (recommended: port, Chromium never suspends)
- [x] `343fdc7` `--font-dir` - ported as an opt-in: `serve --font-dir DIR` (repeatable,
      forwarded to workers) and `BrowserConfig.FontDirectories` / `FontDirectory(...)`.
      Unset, no file is read and layout is unchanged; set, it gives up identical output
      across hosts, which is the operator's choice. See "Operator font directories" under
      Known deviations

Found during the review, not from upstream:

- [ ] Left, right, left floats: the third float lands below the first (`BlockLayout.cs`
      caller, around line 796); repro in the review document
- [ ] An inline `<span>` reports width 0 from `getBoundingClientRect()`; an `inline-block`
      reports its real width (seen writing the font-directory CDP test)
- [ ] Render loads on CDP pages other than the first produce no Network events:
      `SyncLivePageNetworkEvents` forwards only the first live page
- [ ] Timing-sensitive tests under heavy host load (the two worst IntersectionObserver ones now wait for the event instead of a fixed window): the IntersectionObserver/ResizeObserver
      group in `RuntimeTests` (25/60 ms timer windows) and
      `ConcurrentConnectionsHeavyPageDoNotAbortV8`; both pass alone and fail on the
      unmodified base under the same load. `ForwardWaapiSampleUpdatesRetainedStyleAndPaint`
      was reported failing on the base at low load once; it passes in full runs here

## Open issues

- **Security review, September 2026: see `SECURITY.md`.** 6 Critical, 8 High, 11
  Medium and 12 Low findings. All Critical and High are fixed with regression tests,
  including the three that contradicted items this file had marked done (the CDP
  `--allow-file-access` gate, H1; upstream `04418a5 D` frame isolation, C2;
  `04418a5 G` file navigation from a web page, H3). The deviations are under
  "Security review fixes" in Known deviations. Still open (`SECURITY.md` "Fix status"):
  - [x] cooperative cancellation of C# work inside ops: watchdogs cancel the isolate's
    `ScriptCancellation`, ops and captures run under it, render entry points take a
    `CancellationToken`, MCP tool calls have a budget (`SECURITY.md` "Cancellation of
    work inside ops")
  - [x] pathological-but-legal inputs made fast (see "Security review fixes" in Known
    deviations): nested floats (the layout cache kept evicting a shrink-to-fit
    parent's two measurements; 77 s to 1.8 s at 300), nested and sibling inline
    boxes (edge sums, owner ranges, per-line reshaping; minutes to ~1.5 s at 5000),
    grid track counts (Chromium's `kGridMaxTracks` rules at 10,000 tracks; nested
    `repeat()` is invalid), `innerText`/`Range.toString()`/`contains()` (native,
    linear; 30 s to 12 ms)
  - [x] nested tables: the fixed-width floor and the intrinsic measurements are
    linear in the subtree walks (1,500 nested tables 4.0 s to 2.5 s)
  - [ ] still slow: `ApplyTableUsedWidths` still relays out from the root per table
    depth level (quadratic, ~110 ms warm at the box depth cap); the first layout of a
    50k-wide tree (~4 s, GC-bound: ~45 objects per element); RTL, CJK and short lines
    still reshape the rest of the line per line
  - [x] grid: sparse occupancy (a byte per cell up to 1,024 cells, a segment tree of
    interval sets beyond), 32-bit lines, 100,000 tracks per axis, Chromium's step-2
    auto-placement cursor, `getComputedStyle` serializes the grid properties
  - [ ] grid, found on the way: RTL auto-placement mirrors and searches in reverse
    (Chromium places logically and mirrors after; 92 of 102 remaining random-grid
    mismatches); `em` in grid tracks resolves against 16px; undefined named lines do
    not create implicit lines; `calc()` is serialized as written; `grid-template` and
    `grid` shorthands are not serialized; step 4 with a definite primary position
    steps one track at a time
  - [ ] found on the way, not fixed: computed `visibility` does not inherit (innerText
    shows text inside a hidden subtree's children); closed `<details>`/`<dialog>`
    content is not `display:none`; a float after inline text starts a new line where
    Chromium keeps it on the line (y/height differ); nested `padding:0 3px;margin:0
    2px` spans land far from Chromium's y
  - [x] HTML tree construction is the port's own (`HtmlTreeBuilder`) over AngleSharp's
    tokenizer (M11): 50k nested divs 20 s to 0.14 s, a 50k-sibling fragment 91 s to
    0.13 s; html5lib tree-construction 1756/1765
  - [ ] parser leftovers: the tokenizer drops U+0000 in the data state (no U+FFFD in
    foreign content); `<selectedcontent>` is not implemented; `outerHTML` has no setter
  - [x] memory budgets (M7): `ArrayBuffer` cap per isolate, DOM byte budget per
    document, glyph lists sized up front, opt-in per-process backstop; op_fetch_url
    builds its result from the body bytes in one pass (M4)
  - [x] DOM garbage collector: detached components nothing holds are freed at task
    boundaries (and in-op when the budget refuses a mutation), with generation-tagged
    node ids so a stale id never aliases a reused slot
  - [x] CDP isolated worlds in the main frame are their own realms (M6)
  - [x] WebAssembly memory is capped per isolate and per memory (M7)
  - [x] child-frame execution contexts and isolated worlds are realms; page-dispatched
    events reach world listeners; file input selection is held host-side (M6)
  - [x] CDP main-world snippets, MCP tools and the shim's own DOM paths use built-ins
    captured at bootstrap (L10): Playwright and Puppeteer pass on pages that override
    `querySelector`, `Array.prototype.map/filter`, `Promise.prototype.then`, `JSON`,
    `Object.keys`, `dispatchEvent`, `elementFromPoint` and the event constructors
  - [x] host reads of page-writable values (`window.scrollX/Y`, `location.assign`,
    `__obscura_click_target`, `__obscura_focused`), by-value `toJSON`, input helpers (L10)
  - [ ] L10 leftovers: string/regex handling in the markdown script and shim,
    `Array.prototype.push/forEach`, `Function.prototype.call` and `Set`/`Map` methods in
    shim internals
  - [ ] an inline `<a>` reports an empty rect, so CDP clicks by coordinates miss inline
    links; Puppeteer `type` puts the caret at the end where Chromium puts it at the
    start; Puppeteer `::-p-text` selectors do not work
  - [x] CDP and MCP SSE idle timeouts (L3); optional TLS for `serve` and `mcp --http`,
    including the balancer's own 503 (I1)
  - [x] the History API URL is host-side; MCP storage state follows the committed
    document origin (L9)
  - [x] full public suffix list (L6), HSTS and mixed-content blocking (I7), serverAuth
    EKU on custom roots (I8)
  - [x] `ImageAgent` goes through the guarded transport; fetch methods, User-Agent,
    Origin and immutable response headers; referrer policy (header, meta,
    `referrerpolicy`, `rel=noreferrer`, fetch options, stylesheets, frames,
    `Page.navigate`); CHIPS partitioned cookies and the site for cookies in frames
  - [x] the shim's engine globals are closure state reached as `__obscura_host.vars`;
    bindings install without a bridge global; module completion uses a random
    top-level `const` (I10)
  - [ ] ClearScript's non-configurable `EngineInternal` stays visible to `in`/`typeof`;
    the global's shape differs from Chromium's (Object-based prototype chain, own index
    getters `0`..`49`, own `addEventListener`/`dispatchEvent`/`constructor`,
    `ContentIndex`, `FontFaceSet`, `SharedArrayBuffer`, `webkitAudioContext`)
  - [x] child frames: init scripts and bindings run in frames, a frame's binding call
    reports the frame's context, init scripts belong to the target that added them, a
    frame's own GET navigation is followed, a click that loads a new document emits
    the full navigation sequence
  - [ ] found on the way: an awaited `Runtime.evaluate`/`callFunctionOn` that calls an
    exposed binding deadlocks the connection (main frame too); a frame's POST
    navigation is dropped
  - [ ] network, found on the way: CDP `Network.getCookies` returns every cookie
    (Chromium filters by URL and partition); `document.cookie` in about:srcdoc and
    about:blank frames uses the about: URL; a partitioned `setCookie` for an http URL
    is accepted with Secure forced where Chromium refuses it; images in an inline
    `<style>` use the document policy where Chromium uses the default; the shim has no
    `srcdoc` attribute

- **A forced geometry read after a style write that *does* change layout still
  re-lays out the whole document.** The half that does not is fixed: a retained
  restyle whose recomputed styles no layout pass can observe now keeps the
  layout it already has (`RetainedLayoutReuse`, see Known deviations). On the
  2059-node `thrash.html`, interleaved A/B on one binary, three runs each,
  50 (write, read) pairs, medians: `opacity` 5261ms -> 1211ms, `class` 4947ms
  -> 524ms (Chromium 0.2ms and 0.1ms). A write that really changes a box -
  `left`/`top`, `transform`, `width` - is unchanged at ~5.2s against Chromium's
  3-4ms, because the gate answers *whether* to lay out and nothing yet answers
  *how much*.

  What is left needs retained layout, not a better gate. One full prepare of a
  ~2000-node page costs 77ms and of the Tesserae SPA 130-460ms, and it was
  ~40% HarfBuzz shaping, ~12% building the taffy tree, ~13% taffy itself and
  ~14% `DerivedLayoutState`. **The shaping share is now paid once** - see the
  cross-pass shape cache below - which roughly halves a layout-affecting
  write+read pair. The rest still wants the proposal on file: retain the box
  tree so a restyled out-of-flow box re-runs layout for its formatting context
  rather than the document. Raising the watchdog budget only moves the
  threshold.

- **`#/view/Masonry` on the Tesserae sample app still never lays out**, and F39
  attributes it to the wrong cost. Instrumented per prepare (`PREP #n`), the
  route runs ~33 prepares totalling 13.4s, of which #7-#31 are twenty-five
  *single* `style`-attribute writes at 120-460ms each: Outlayer's
  `Item._transitionTo` reads `getComputedStyle` for the item's current position
  and then writes the new one, fifty times. Those writes change `left`/`top`
  and `transform`, so they are layout, not paint, and the gate correctly
  declines them - measured on and off, the route fails identically. F39's
  root-cause table was taken on `thrash.html`, which is a much cheaper page
  (77ms a prepare) and a different write mix; the conclusion that Masonry is
  "~50 forced relayouts at thrash speed" does not survive measuring Masonry
  itself.

- **The ClearScript op boundary is ~3x the deno_core cost, down from ~20x.**
  Ops used to be registered with `ScriptObject.SetProperty(name, delegate)`,
  which routes every call from `bootstrap.js` through ClearScript's
  reflection-based host-object dispatcher. `PocketCalculator.Js.Ops.FastOpBinding` now
  wraps each op in a `V8FastHostFunction` (ClearScript 7.5), which hands the
  invoker V8's raw argument list instead. Measured in-page after warmup, same
  host, same V8, us/call:

                                            rust    port before   port after
      op_runtime_events_enabled (0 args)    0.07       1.42          0.58
      op_shadow_root_info (1 arg)           0.30       1.40          0.50
      op_dom("document_node_id")            0.37       3.18          1.20
      op_dom("node_type")                   0.47       3.20          1.40

  And on DOM work driven from page script:

                                     rust    port before   port after
      20k setAttribute               28ms      412ms         134ms
      20k createElement              94ms      395ms         174ms
      5k create + style + append    114ms     1329ms         910ms

  Async ops keep the general marshaller: they return `Task` and depend on
  `EnableTaskPromiseConversion`, which the fast path does not perform.

  Beware when measuring this: timing several ops in one process in a fixed
  order makes whichever runs first look slowest, because JIT tier-up dominates
  the first few thousand iterations. The same op measured 8.8us on the first
  lap and 3.2us on the second. An earlier version of this entry reported
  5-27us/call from exactly that mistake. Always run a discarded warm-up lap
  over every op before the measured one, and sanity-check by reversing the
  order.
- **The remaining page-load gap is ~2.3x, and CSS parsing is the largest single
  piece of it.** A Tesserae SPA route at a fixed `--wait 1` takes ~6.9s to
  `--dump html` against the reference's ~2.9s. The whole shape of that is now
  measured, and it is one synchronous JavaScript task, not a spread:

      phase preNavigate        5 ms
      phase navigate        3109 ms     (first prepare, 1990 ms, happens in here)
      phase settle          3552 ms     (ONE event-loop tick; budget was 1000 ms)
      phase dump              18 ms

  The settle loop checks its wall-clock budget between ticks, so a single
  multi-second page task overruns it several times over. That task is the app's
  render, and its cost is one `op_layout_geometry` call: `OBSCURA_OP_PROFILE=1`
  (see `FastOpBinding.OpProfile`) puts 4.3s on one such call against 233ms for
  the ~19,000 `op_dom` calls around it.

  Inside that call, `EnsurePreparedGeometry` is essentially all of it, and
  inside the prepare:

      css parse (Stylesheet.ParseForViewportAndMedia)   ~45%
      cascade   (DomCascade.CascadeWalk)                ~25%
      web fonts (WOFF2 decode, first prepare only)      ~15%
      dom build (DomBuild.Build)                        ~10%
      taffy compute                                      ~5%

  So the next round is CSS, not taffy. Isolated on a page that is 3 MB of
  stylesheet over a one-element body, parse plus cascade is ~600ms against the
  reference's ~125ms, and that ~5x is the widest single ratio anywhere in the
  port. It is allocation-bound: the sheet has 44,912 rules, and parsing it
  allocated 187 MB, with GC pauses accounting for 45% of the wall time (one run
  with a 256 MB gen0 budget and zero collections finished in 187 ms). Tuning
  the GC is not the answer - server GC, background GC and larger gen0 budgets
  all measured within noise of each other on the real page - cutting the
  allocation is.

  A first pass took it to 133 MB and the warm parse from 476ms to 379ms best,
  521ms to 438ms median: the top-level scanner now records offsets into the
  sheet and takes one substring per rule instead of appending every character
  to a `StringBuilder`; `StripPseudoElement` compares spans instead of building
  a suffix string on each of its three calls per rule; `SplitSelectorList`
  returns the input directly when there is no comma; `Denest` trims declaration
  spans and no longer materializes its builder twice; `SelectorParser` reads an
  escape-free identifier as one substring and short-circuits a selector that is
  nothing but one class (95% of this sheet's 45,000 selectors, once the cascade
  has taken the pseudo-element off).

  What is left, measured per rule over the 41,994 pseudo rules on that sheet:
  `CompileRuleSelector` 40 MB, `NoteSelectorForInvalidation` 25 MB, building
  the `PseudoRule` 23 MB, `CssDeclarations.Partition` 21 MB. Each is a few
  hundred bytes of small objects per rule rather than one bad allocation, so
  the next step is a pass over those four with pooling and spans, not a single
  fix. Note also that none of this is visible in a cold single-parse CLI run,
  where JIT dominates the one parse; it shows in the warm bench, in `serve`,
  and in memory.

  Two prepare-path defects were fixed on the way to that conclusion, both of
  which had been hiding behind the op-boundary cost:

  - `EnsurePreparedRender` and `EnsurePreparedGeometry` read the document base
    URL through `StateHelpers.DocumentBaseUrl`, which runs the selector engine
    over the whole tree looking for `base[href]`. A memoized variant already
    existed for `document.baseURI` and these two call sites simply were not
    using it, so the geometry fast path was O(nodes). 200 repeated
    `getBoundingClientRect()` calls on a 5000-node document went from 457ms to
    4ms (the reference is 66ms), and `getComputedStyle` x200 from 507ms to
    312ms (reference 374ms).
  - Web faces were WOFF2-decoded on every prepare, ~600ms of every prepare on
    a page with three faces. `RenderResourceCache` now memoizes the decoded
    sfnt against the fetched byte array. Deliberate deviation, recorded below.
- **Cold start is ~790ms from `dotnet build` output, ~40ms for the reference.**
  Roughly 300ms of it is jitting the DOM, style, layout and paint stack on the
  way to the first frame, and `dotnet publish` now precompiles that away:
  `PublishReadyToRun` is on whenever a RuntimeIdentifier is set, and composite
  turns itself on when the publish is also self-contained. Best of five on a
  trivial file page:

      dotnet build output (JIT)              790ms
      publish, ReadyToRun                    480ms
      publish, composite + self-contained    360ms

  What is left is structural. `crates/obscura-js` bakes `bootstrap.js` into a
  V8 startup snapshot at build time (see the comment at `frame.rs:11`), so the
  reference restores a heap that already has the whole browser surface in it.
  ClearScript exposes no snapshot API, so the port compiles and runs the 731 KB
  script on every start: ~120ms of the remainder, plus V8 platform init that a
  snapshot would also shorten. A V8 code cache does not help - it was measured
  at 38ms to deserialize 349 KB against 40ms to compile from source, and the
  execution time behind it does not move, so it is a small net loss.

  The three load-flaky cases named here are fixed; see the entries below and in
  Open issues. None of them needed a longer deadline:
  `ModuleGraphAndEvaluationShareOneActiveBudget` now spends the graph's share of
  the budget by busy-waiting inside the imported module instead of waiting on a
  delayed response, `PruningAnOldBatchDoesNotStrandANewRuntimeBatch` waits for the
  frame documents it needs instead of for a fixed number of event-loop passes, and
  `Read_body_capped_rejects_oversized_streamed_body` had a fixture that reset the
  connection. What remains of this section is the startup cost itself.

  When benchmarking publish variants, delete `obj/` and `bin/` for the RID
  between runs. Publishing the same project self-contained and then
  framework-dependent into different folders leaves stale intermediates that
  produce a binary which aborts on startup with no output.
- **`PocketCalculator.Net` still speaks `System.Uri`, so URLs are reserialized at the
  transport boundary.** `PocketCalculator.Browser` now keeps `UrlRecord` throughout, but
  `Response.Url`, `Request.Url` and every `PocketCalculatorHttpClient` entry point take a
  `System.Uri`, and `PocketCalculator.Browser.NetUrl` converts in both directions. In Rust
  there is no such boundary: `obscura-net` takes and returns the `url` crate's
  `Url`. The visible effect left is the error text for a host `UrlRecord` accepts
  and `System.Uri` rejects (`http://a..b/`: the reference reports a DNS-shaped
  transport failure, the port reports one too but with the BCL's wording).
  Removing it means moving `PocketCalculator.Js/Url/**` into a project both `PocketCalculator.Net`
  and `PocketCalculator.Js` can reference - `PocketCalculator.Js` depends on `PocketCalculator.Net`, so it
  cannot go the other way. That mirrors the Rust tree, where `url` is a crate
  both depend on.

  Surveyed, not started. The cut is cleaner than the folder suggests, because
  only part of `Url/` is the `url` crate:

  - **Moves** to a new `src/PocketCalculator.Url/PocketCalculator.Url.csproj` (namespace
    `PocketCalculator.Url`), which references nothing: `UrlRecord.cs`, `UrlParser.cs`,
    `UrlHost.cs`, `UrlParseError.cs`, `PercentEncoding.cs`, `Idna.cs`,
    `Punycode.cs`, `UnicodeNormalization.cs` - ~3.6k lines, no dependency on
    anything outside themselves. `PocketCalculator.Net`, `PocketCalculator.Js`, `PocketCalculator.Browser`,
    `PocketCalculator.Cdp`, `PocketCalculator.Cli` and `Obscura` reference it; the solution gains
    one `<Project Path=.../>` line.
  - **Stays** in `PocketCalculator.Js`, because each depends on something `PocketCalculator.Url`
    must not: `UrlOps.cs` (the `ops.rs` layer - needs `OpGuard`),
    `QueryEncoding.cs` (needs `PocketCalculator.Net.WhatwgEncoding`), and
    `FormUrlEncoded.cs` / `PublicSuffixList.cs`, which only `UrlOps` calls.
    Alternatively move `PocketCalculator.Net/Encoding/WhatwgEncoding.cs` +
    `SingleByteTables.cs` into `PocketCalculator.Url` as well, mirroring `encoding_rs`
    being a crate both depend on, and then all four move too.
  - **`using PocketCalculator.Js.Url;`** appears in 20 source files and 7 test files; the
    moved types need it changed to `using PocketCalculator.Url;`.

  The transport conversion is the larger half: 59 `System.Uri` references across
  7 files in `PocketCalculator.Net` (`PocketCalculatorHttpClient.cs` 28, `Requests.cs` 8,
  `CookieJar.cs` 8, `UrlOrigin.cs` 6, `StealthHttpClient.cs` 6, `SsrfGuard.cs` 3,
  `PocketCalculatorNetException.cs` 1). Four of them are behaviour, not signature, and
  need their own test:

  - `CookieJar.HostOf` and `UrlOrigin.Host` strip brackets by testing
    `UriHostNameType.IPv6`; on a `UrlRecord` that is `HostKind.Ipv6`.
  - `SsrfGuard.ValidateUrl` switches on `UriHostNameType` to decide whether the
    host is a literal address - same replacement.
  - `PocketCalculatorHttpClient` resolves a redirect `Location` with
    `Uri.TryCreate(currentUrl, locationString, out _)`; on a `UrlRecord` that is
    `Join`, which is WHATWG and therefore resolves a few `Location` values
    differently. That is the direction of the fix (it is what Rust does), but it
    is an observable change and wants a redirect test of its own.
  - One `System.Uri` legitimately survives, at `HttpRequestMessage.RequestUri`.
    Building it from `UrlRecord.Href` there is what makes `http://a..b/` fail as
    a transport error naming the URL, the way the reference reports it, instead
    of as a BCL `UriFormatException`.

  Then `PocketCalculator.Browser/NetUrl.cs` is deleted (15 call sites in `Page.cs`,
  `Page.Navigation.cs`, `Page.Capture.cs`, `Page.Scripts.cs`,
  `Page.Stylesheets.cs`), `Obscura/Api/Cookie.cs` loses its port-only
  "host not representable as a System.Uri" arm, and
  `PocketCalculator.Cli/Commands/FetchCommand.cs` + `PocketCalculator.Js/Runtime/
  PocketCalculatorJsRuntime.Modules.cs` can stop gating on `Uri.TryCreate` and report the
  `UrlParseError` reason like every other rejection does now.
- **`ConcurrentConnectionsHeavyPageDoNotAbortV8` has room now, and still asserts
  on a 30s guard.** It drives four concurrent CDP connections against a
  subresource-heavy page, and each client waits up to 30s for a response. The
  guard is not the property - a V8 abort kills the process, which is what the test
  is really watching for - but the run has to fit inside it. Its fixture served the
  page from the thread pool (`Task.Run` + `Task.Delay(120)`), sharing a scheduler
  with the CDP server and four clients; on a loaded box the simulated 120ms
  subresource delay became the host's scheduling delay. The fixture now serves on
  dedicated threads with blocking I/O and `Thread.Sleep`. Under a 14-burner stress
  on 4 cores the run went from 24.5/29.2/24.7s to 17.7/20.7/20.3/19.6s - about
  10s of headroom under the guard instead of about 1s - and it passed either way;
  idle it is 2.1-2.8s. The deadline itself was left alone.

  What is left is that the rest of the assembly runs alongside it: four
  connections, an in-process CDP server and the fixture already saturate four
  cores, and one full-suite round in three still lost a client to the 30s guard
  (`client 1: The operation was canceled.`). Giving the class the machine to
  itself with `[CollectionDefinition(DisableParallelization = true)]` was tried
  and reverted - it hung the assembly: the suite went from 24s to over 300s with
  no test reported. Whatever the runner does with a non-parallel collection here,
  it is not worth that, and the guard itself is still the wrong thing to raise.
- **FIXED - `RuntimeTests.ParserImagesLoadConcurrentlyWithoutBlockingTheEventLoop`
  was a real port defect: concurrent image completions raced on page state.**
  Measured on this 4-core box: 6 of 12 runs before; 13 of 14, then 12 of 12,
  after.

  None of the four `Stale` predicates in `RenderOps.FinishAsyncImageMetadata` was
  firing - instrumenting them showed the failing runs never reached one. What the
  trace showed instead was a leader logging `SEEDED` for its URL and the very next
  line, on the same thread, reading that URL back as `known=False`; in other runs a
  leader vanished between `FETCHED` and `SEEDED` with no seed and no result at all,
  and the retry that followed registered as a *follower* of an in-flight entry
  nobody would ever complete, so that element never finished.

  The cause is that `LoadImageMetadataAsync` awaits the transport with
  `ConfigureAwait(false)` and then writes page state from whatever thread-pool
  thread the fetch landed on. Rust cannot do this: `op_load_image_metadata` is a
  deno_core async op whose reaction resumes on the one thread that owns
  `Rc<RefCell<State>>`, so the five elements' completions are serialized by
  construction. In the port four of them ran at once against a plain
  `Dictionary` (`RenderResourceCache._entries` plus its `_order` list and byte
  counter), a plain `Dictionary` (`PocketCalculatorState.RenderImageInFlight`) and a plain
  `List` (`PendingStyleMutations`, through `InvalidateRenderResourceGeometry`).
  Concurrent inserts lost entries outright - which is the seed that reads back
  unknown - and a lost in-flight removal is the stranded follower. The rest
  follows: an unknown answer is `{"state":"pending"}`, which `_applyImageMetadata`
  turns into an `error` event with `naturalWidth` 0, and the shim re-queues,
  which is where the 5th and 7th requests came from.

  Three changes, all host-side (bootstrap.js needed none):

  - `PocketCalculatorState.AsyncResourceGate`, taken around the whole post-fetch tail in
    `RenderOps.LoadImageMetadataAsync` - seed, invalidate, remove the in-flight
    entry, compute the result - and around the in-flight registration and the
    follower's `FinishAsyncImageMetadata`. Waiters are released outside it.
  - A `finally` that removes the request key and releases any waiters when the
    leader fails, so a thrown leader can no longer strand its followers.
  - A lock inside `RenderResourceCache` over the retained byte cache, because the
    renderer reads that cache from the pump thread while a page-transport seed
    writes it from a pool thread, and the gate above cannot cover the reader.

  What is left is the test's `elapsed < 500ms` assertion, which failed once in 14.
  It is not the image lifecycle: the run issued exactly 4 requests and reported
  five `load` events, in 717ms. A fresh process JITs the socket and JSON stack
  inside the measured window - the first request's synchronous prologue alone cost
  ~70ms in the traces - and `maxActive >= 3` already asserts the overlap that the
  wall-clock bound is a proxy for. It is the startup-latency gap in this section,
  not a lifecycle bug.

  Ruled out along the way: the `PocketCalculatorHttpClient.ConnectCallback` change for IP
  literals, and thread-pool starvation from the harness's blocking handler
  (`RawHttpServer` already gives each connection a dedicated thread).
- **FIXED - the two load-flaky `PocketCalculator.Browser.Tests` cases.** Reproduced with
  CPU burners on this 4-core box rather than by re-running the suite.
  `ModuleGraphAndEvaluationShareOneActiveBudget` failed 3 of 3 under six burners,
  always `[false, false]`: the module's whole 350ms budget went to fetching the
  delayed import, so evaluation never started and the case had nothing to assert
  on. Instrumenting `graphElapsedMs` showed why the shape was fragile - for an
  inline module it is ~4ms, because the import is fetched during evaluation, so
  the budget the case is really splitting is fetch + top-level work. The imported
  module now spends its share by busy-waiting (a wall-clock spend that contention
  cannot stretch) instead of being served slowly, against a 1200ms budget and a
  1000ms top-level loop: 8 of 8 under the same stress.
  `PruningAnOldBatchDoesNotStrandANewRuntimeBatch` failed 2 of 6 under ten
  burners, in two different places - a `WaitAsync(2s)` around one event-loop pass
  in the fixture, and `Assert.Single(page.Frames)` after a single pass that
  returned idle before the replacement frame's document had arrived. Both now
  wait for `PendingFrames` to hold what the next step needs: 8 of 8 under the same
  stress, and unchanged at 1.9s idle. The `TestHttpServer` fixture also serves on
  dedicated threads now, for the same reason `RawHttpServer` does.
- **Reading `image.complete` can complete an element from cache with no `load`
  event.** Found while stabilizing the tests: `_refreshImageFromCache` in
  `bootstrap.js` applies a cache hit with `dispatchEvent: false`, so a getter read
  that lands between a request's bytes reaching the renderer cache and that
  request's promise reaction sets `complete`/`naturalWidth` and swallows the
  event. `ImageLifecycleCacheIsSeparatedByCorsCredentialsProfile` hit it in 5 of
  20 runs (2 of 20 once the completions were serialized), reading
  `[true, 2, []]` where it expects `[true, 2, ["load"]]`. The test now waits for
  the event count it is about rather than pumping a fixed 1000ms and reading
  `image.complete`, which is 0 of 20 - but the shim behaviour is still wrong
  against Chromium, where reading `complete` never cancels a pending event. The
  window is narrow in Rust (the reaction resumes on the same thread as the
  getter) and wide here, which is why the port sees it.
- **FIXED - `OpsTests.Read_body_capped_rejects_oversized_streamed_body` was the
  fixture resetting the connection.** The earlier note here blamed first-render
  font initialization; that test never renders. Under load it failed with
  `IOException: Connection reset by peer` where it expects `OpException`:
  `ServeBodyOnce` wrote 4 MB, called `Shutdown(SocketShutdown.Both)` and dropped
  the socket while megabytes were still queued, and the reader - slowed down by
  the same contention - saw the RST before it reached the 1 MB cap. The fixture
  now runs on a dedicated thread with blocking I/O (the Rust harness's
  `std::thread::spawn`), half-closes with `Shutdown(Send)` and drains until the
  reader goes away. 10 of 10 under a 10-burner stress that previously failed about
  one run in six. The cap assertion itself is unchanged.

## 9. Validation

- [x] `PocketCalculator.Parity.Tests` harness: runs a case through both binaries and diffs
- [x] `scripts/parity-sweep.sh` drives both engines over every fixture:
      **320 of 320 outputs byte-identical** (64 fixtures x text/links/html/
      markdown/assets). It now checks exit status as well, and reports a signal
      death separately rather than scoring it as a parity result - which is how
      the CLI's teardown segfault stayed hidden behind a green sweep.
- [x] `scripts/parity-sweep-scrape.sh` covers `scrape`, `serve` and the worker
      protocol: 169 cases, **165 identical**, the 4 remaining both traced to
      recorded deviations outside the CLI
- [x] `PocketCalculator.Parity.Tests`: **306 of 307**, one skipped. Includes the 17
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
- **DEVIATION - pinning a flex item's used width let the flex algorithm shrink it
  a second time.** `resolve_deferred_flex_inline_sizes` writes only `size.width`
  when it pins an item, but that width is the item's *used* main size - an output
  of the flex algorithm - and the relayout right below re-runs that algorithm,
  which reads the declaration back as the item's flex base size. A still-flexible
  item therefore flexes again from its already-flexed size. CSS Flexbox 9.2
  derives the flex base size from `flex-basis`/`width` on every pass, never from
  a previous pass's used size. In a 1440px row of inner bases 250/8/1440,
  `width: 250px; flex: 0 1 auto` with a `width: 100%` child resolved to 212 on
  the first pass and then 184 on the second (Chromium: 212.016); the workspace
  app's `.msk-app-sidebar-default` was 184 against Chromium's 211.98. `PinFlexItems`
  now freezes `FlexGrow`, `FlexShrink` and `FlexBasis` alongside the width, so the
  pin actually pins. Verified against Chromium on 20 probe cases (D1-D12, E1-E8)
  and on the app sidebar tree, which now matches Chromium node for node.
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
- **DEVIATION - `scrollbar-gutter: stable` was honoured only on the root.** The
  reference reserves a gutter out of the initial containing block alone
  (`dom.rs` reads `scrollbar_gutters` off the root element), so a nested scroll
  container reserved none, and it parses no `scrollbar-width` at all. Tesserae's
  annotated text editor overlays a highlight layer on a
  `scrollbar-gutter: stable; scrollbar-width: thin` textarea, and the overlay came
  out 902px against Chromium's 892. The port reserves the gutter through taffy's
  own `ScrollbarWidth`, which takes it out of the content area and leaves the
  computed padding untouched, exactly as Chromium does. Measured against Chromium
  on this platform: 15px classic, 10px for `thin`, 0 for `none`, and nothing at
  all without `scrollbar-gutter` (this build uses overlay scrollbars). Applies on
  the inline axis only, for a box that is a scroll container in either axis.
  Not closed: `both-edges` reserves the right total (270px of 300) but taffy
  insets from the end only, so the content does not shift by the leading gutter
  the way Chromium's does.
- **DEVIATION - an ATOMIC inline whose baseline is its bottom margin edge did not
  extend the line box by the strut's descent.** (The non-atomic half of the same defect
  is the next bullet; this one is only about the taffy flex-row stand-in.) CSS 2.1 10.8 makes a line box
  `max(ascent) + max(descent)` over everything on it *plus the strut*, and 10.8.1
  puts an atomic inline's baseline at its bottom margin edge when it has no in-flow
  line boxes or its `overflow` is not `visible`. Such a box is wholly above the
  baseline, so the strut's descent still has to fit under it: a 12px empty
  `inline-block` in a `line-height: 12px` block is 14px tall in Chromium 141 and was
  12 in both engines. The reference models a line box as a wrapping flex row aligned
  to `flex-start`, which gives every participant the same top edge and cannot express
  it.

  `DomBuild.BottomBaselineAtomicStrutDescent` gives such a box a taffy bottom margin
  of the strut's descent - the half-leading plus the grid-fitted font descent of the
  block container that owns the line, clamped at zero. A margin is what a flex row can
  carry, and it is exact for the line's *height* in every case measured. Two narrower
  things came with it: `VerticalAlign` gained a `Baseline` member, because
  `vertical-align: baseline` used to parse to `Top` and the whole rule turns on telling
  those two apart, and native form controls are excluded by tag - they are atomic and
  childless, so every structural test would let them through, but Chromium gives an
  `<input>` the baseline of the text it renders itself.

  Measured against Chromium 141 on the fixtures: empty inline-block 12 -> 14, an
  explicit `vertical-align: baseline` 14, a 30px one 32, an empty `inline-flex` 14, an
  `<img>` 14, two atomics on one line 22, an atomic beside text 14, `line-height: 0`
  still 12 (a negative descent adds nothing). Unchanged and still right:
  `vertical-align: top` 12, an inline-block containing text 12, a flex or grid item 12,
  an `<input>` 12. Covered by `LineBoxStrutTests`.

  Swept over all 101 Tesserae routes against the Chromium capture, before and after, same
  page, viewport and settle policy. Restricted to the 54 routes whose two captures settled
  to the same node count - the app renders its sidebar commands and its deferred sections
  asynchronously, and a capture that lands a few nodes short narrows the sidebar and shows
  up as a ~1.9px width error that has nothing to do with the build. Mean abs error against
  Chromium over strictly aligned pairs: **height 0.546 -> 0.264**, width 0.188 -> 0.191,
  and the accumulated vertical offset 81.2 -> 1.8 (per-route medians 0.576 -> 0.196,
  0.053 -> 0.053, 106.3 -> 0.16). The `y` collapse is the whole point: a page stacking
  fifty short lines was carrying a hundred pixels of drift by its bottom. **No route's
  height error got worse.** One route's width error moved, `view/Pivot` 0.416 -> 0.571,
  and it is not this change: its `tss-pivot-line` tab underline lays out 0px wide in both
  builds, and the after capture happened to have four of them on screen instead of two.

  **Still open, and it is only about boxes the flex-row stand-in lays out.** A block whose
  inline content is all text folds to one shaped buffer and never reaches taffy
  (`TextEngine.TryBuild` / `TryBuildRun`); the CSS 2.1 10.8.1 model for those landed in the
  next bullet. What is left here is the blocks that do NOT fold - the ones carrying an atomic
  inline, a float or a block-level child - and inside them:

  - An atomic inline that *does* have a line box is still aligned to the line's top rather
    than to its own baseline, so it neither extends the line nor moves within it. The
    `<i class="fi-rr-*">` icon is one of those: its `::before` is an `inline-block` carrying
    an icon glyph, so it has a line box, and its baseline is that glyph's - which lands on the
    box's bottom edge only because the icon face's ascent is the whole em and its descent is
    zero. Chromium 20.19 for a 13px/18.2px icon row, port 18.00; 14 against 12 at 12px/12px.
    The same gap covers an atomic inline shorter than the strut's ascent (Chromium pushes it
    down by the difference, the margin cannot) and an inline-block whose only child generates
    no line box (Chromium 14, port 12).
  - The strut is still absent from a line box that carries no text at all
    (`RunWrapperStyle`'s `hasTextStrut`, and the block-as-flex-row stand-in has none at any
    time), so a 12px icon on a `line-height: 20px` line is 12 here against Chromium's 20. That
    half is independent of baselines and could land first.

  The plan below was read off the vendored taffy and re-checked while the non-atomic half
  landed. It is accurate, and it is still what this half needs:

  - `FlexboxLayout.CalculateCrossSize` sizes a baseline-aligned line as
    `max(baseline) - baseline_i + outerHeight_i`, which *is* the CSS formula, and
    `CalculateChildrenBaseLines` skips any line with fewer than two baseline-aligned children -
    which is why turning `AlignItems.Baseline` on by itself changed nothing.
  - The strut itself needs no new plumbing. A zero-width leaf of height `A` with a bottom
    margin of `max(D, 0)` and `AlignSelf = Baseline` models it exactly, because a leaf reports
    no baseline and taffy falls back to its height.
  - Every *other* participant does. A text leaf's baseline is `A`, not its height `A + D`, and
    an inline-block's is its first line's - and `Leaf.ComputeLeafLayout` and `BlockLayout` both
    report `FirstBaselines = None`, so taffy takes the height for both. So: let a measure
    function report a baseline beside its size, and have `BlockLayout` propagate its first
    in-flow child's. That is the restructure, it moves the vertical position of everything
    inside every line box, and it wants its own before/after survey.

  `FontAssets.LineBoxHalves` (next bullet) is now the one place that says what a box's two
  halves are, so the taffy work should call it rather than re-deriving the leading split.
- **DEVIATION - a line box did not grow to contain an inline box whose font, line-height or
  vertical-align differed from the block's.** CSS 2.1 10.8.1 makes a line box
  `max(above the baseline) + max(below it)` over every inline box on it plus the strut, where a
  box contributes `A + L/2` above and `D + L/2` below with `L = line-height - (A + D)`.
  `crates/obscura-render/src/inline.rs` has no such notion: a folded inline formatting context
  gets one `line_height` (the largest on the line) and every span hangs from the line's top, so
  a 32px span in a `16px/18px` block was 18px tall in both engines and 22px in Chromium 141. A
  54-case matrix measured against Chromium with the faces named explicitly had 26 wrong; the
  font-size row was wrong at every size but one, and the same-size/different-family row - the
  common case on a real page - was wrong by 1px every time.

  **The quantization was measured, not derived, because the obvious reading of 10.8.1 does not
  reproduce Chromium.** Blink floors the ascent to whole pixels once the half-leading is in and
  then takes the descent as `line-height - ascent`, so the two halves are asymmetric and the
  descent carries the remainder and can go negative. Splitting the leading symmetrically
  computes 19.5 for both the 20px and the 8px span in a `16px/18px` block, where Chromium
  reports 19 for one and 20 for the other; the floor-then-subtract rule reproduces all 54.
  `FontAssets.LineBoxHalves` is that rule, and `FontAssets.QuantizedLineHeight` is the other
  half of it - the used line-height is a 1/64px `LayoutUnit` before the leading is split, which
  is why `16px/25.7px` with a 32px span is 30.703125 in Chromium and not the 30.6875 a
  truncation gives.

  `vertical-align` came with it, because a box that is not on the line's baseline cannot be
  sized against it. Measured over 120 more cases:

  - `super` raises the baseline by the PARENT's font size / 3 + 1 and `sub` lowers it by the
    parent's / 5 + 1, both truncated to a `LayoutUnit` - the KHTML rule Blink still carries.
    Neither depends on the aligned box's own size.
  - a `<percentage>` is of the box's OWN used line-height, not the parent's.
  - `middle` puts the box's leaded midpoint at half the parent's x-height, which is why
    `FaceMetrics` now carries `XHeight`, read from `OS/2.sxHeight`.
  - `text-top` and `text-bottom` align the box's LEADED height against the parent's RAW font
    box - a `16px/40px` block with a `text-top` span is 51px tall, not 40. Verified here
    directly against Chromium 141, along with the span's own position inside it.
  - `top` and `bottom` leave the baseline set entirely and can only make the line taller: a
    48px span in a `16px/18px` block is 27px baseline-aligned and 18px `top`-aligned.

  Shifts accumulate down the inline tree, and each is relative to the immediate inline parent,
  not to the block.

  It lands in the folded text path, not in taffy: `TextBuffer.LayoutRuns` composes the line
  from the strut (`TextBuffer.Metrics`) and the per-box halves each glyph carries
  (`TextMetrics.Above`/`Below`/`Align`/`Shift`, filled in `Inline.LineBoxExtent` while spans
  are collected). `TextLayout.PlaceGlyphsOnBaselines` moves each glyph off the line's baseline
  by its own box's shift, and `TextEngine.OwnerBaselineY` does the same for an inline box's
  client rect. This is also why the plan in the bullet above, though accurate about taffy, was
  the wrong place to look for these cases.

  Known short: for a face with no declared `OS/2.sxHeight` (DejaVu Sans is the only bundled
  one), Chromium re-measures the 'x' outline grid-fitted at the used size - 6px at 10px, 9px at
  16px, 14px at 24px, which is not one em fraction at all. The port reads one fraction at the
  em, which costs `vertical-align: middle` up to half a pixel on that face and nothing on the
  three Liberation faces.

  Also still short, and separate: the HTML UA stylesheet's `sub { vertical-align: sub }` /
  `sup { vertical-align: super }` (plus `font-size: smaller`) are not in the port's UA sheet,
  so a bare `<sup>` still sits on the baseline. The machinery it needs now exists, but
  `font-size: smaller` has to be measured first.

  A third, unchased: Blink accumulates fallback-font metrics into a `line-height: normal` line
  where the port uses the span's primary font only. Nothing in the corpus moved on it, so it is
  recorded rather than fixed.

  Covered by `LineBoxFontMetricsTests` (11 facts, 10 of which fail at the parent commit -
  verified here by reverting the sources; the eleventh is the control that must not move).

  Swept over 314 local HTML fixtures, every element's rect, before and after, same binary
  configuration, viewport (1280x720) and settle policy, scored as distance to Chromium 141 per
  component rather than as "did it change": of 46,752 components, **1,220 moved closer to
  Chromium, 17 moved farther and 45,515 did not move**. Every one of the 17 is a sub-pixel
  artefact of the engine's whole-pixel layout rounding: the exact height is now right
  (`vertical-align: middle` at 18.234, a 1.3 line-height at 35.188) and rounds to the far side
  of the value the old model happened to land on. Two files were left out because Chromium and
  the port build a different node count on them, before and after alike. Checked separately
  here: the 64 fixtures under `test-html-files/` and `render-repros/` are byte-identical before
  and after, because none of them carries mixed-font inline content.

  On the 54-case matrix that found this, the mean height error against Chromium falls from
  **2.528px to 0.044px** (26 cases wrong by more than half a pixel, down to 2, both of them the
  rounding artefact above) and the inline box's own client-rect top from 0.694px to 0.000. Over
  a further 114 vertical-align and fractional-line-height cases, 4.720 -> 0.120 and 3.799 ->
  0.000. On the settings-list page the note was written against - a 20-row list of an 18px icon
  glyph, a 14px label, a 10px superscript badge and an 11px hint in a 14px/20px block - every
  row was 3.66px short and the page carried 73.13px of accumulated drift by its last element;
  it is now 0.13px.
- **PARTLY FIXED - a cyclic percentage under a content-sized flex item is now
  neutralized to `auto`.** A content-sized item is measured from exactly the
  content the neutralization touches, so zeroing it is self-defeating; an item
  sized from a declared width or basis is not measured from its content, and
  there the reference's zero is the safer neutral. Carrying `auto` into *both*
  cases was tried and swept over all 136 samples: it made `width: 100%` sidebar
  buttons shrink-wrap to their 80px min-width, costing 0.59, 0.48, 0.46 and 0.30
  mean abs on four samples. Gating it on `Width.IsAuto && FlexBasis.IsAuto` keeps
  Sidebar exact (152/127/174/376 against Chromium's 151/127/174/376) and moves
  Code Diff's two `flex: 1 1 auto` panels from an even 462/462 split to 363/802
  against Chromium's 133/791.
  Still open: the first panel measures 363 where Chromium measures 133, and the
  pair sums to 1165 in a 924px row - they overflow rather than shrinking, so the
  pinned base sizes are not being shrunk by the flex algorithm afterwards.
- **Re-measured, and the even split is gone: Code Diff's two `flex: 1 1 auto`
  panels.** The `contentSizedItem` narrowing above already closed this; the note
  predates it. On `#/view/Code Diff` the pair is 282/802 against Chromium's
  246/829, not 462/462, and a reduction of the shape - a 400px row of two
  `flex: 1 1 auto` items whose only content is a `width: 100%` block around a
  40px and a 160px leaf - is exact in both engines (140/260). What is left on
  that route is two unrelated defects:
  - the left panel is sized by a `<textarea>` whose intrinsic width is 274 here
    against Chromium's 238, which is the whole of the panel's 36px error. That
    is the `cols * charWidth + scrollbar gutter` calculation in
    `LayoutDomControls`, the same family as the `<input>` curve-fit deviation
    below, not a cyclic percentage.
  - the diff tables distribute the width their columns do not claim *equally*
    where Chromium distributes it in proportion to max-content. A 600px
    `width: 100%` table of `alpha` / `beta gamma delta` is 142.9/457.1 in
    Chromium and 258/342 here, and it is wrong in exactly the same way inside a
    plain 600px block with no flex container anywhere - so it is taffy's grid
    track sizing under the table build, not this machinery.
- **DEVIATION - an auto-sized `<button>`'s intrinsic width ignored element
  children.** `native_button_intrinsic_content` recurses past every non-replaced
  element and counts only text plus replaced boxes, so a flex button's child
  boxes, their margins and any icon-font `::before` contributed nothing. Tesserae's
  toolbar buttons (`<i class="fi-rr-*"></i><span>Label</span>`) came out 22px short
  on every one, which shrank the label span and wrapped it, and then failed to wrap
  the button row Chromium wraps. The port counts a definite-width child as its own
  outer box, carries every child's horizontal edges, and shapes `::before`/`::after`
  content with the pseudo's own style. Follow-up: that accumulation was a *sum* over
  the whole subtree, which is only right on one line - see "An auto-sized `<button>`
  accumulates by line" under Known deviations.
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
- **DEVIATION - a flex item sized by the flex algorithm was an indefinite
  containing block.** The general form of the entry above, and the reason the
  connect-apps panel rendered as an empty box. The reference calls a box's block
  size definite only when `height` itself is a length or percentage, so a flex
  item that gets its height from flexing rather than from its own style is an
  indefinite containing block and every descendant `height: %` under it computes
  to `auto`. CSS Flexbox 9.8 says otherwise in two halves that Chromium both
  implements: a flex item's post-flexing MAIN size is definite whenever the
  container's main size is definite, and a stretched item's CROSS size is definite
  whenever the container's cross size is. The block axis is the main axis of a
  column container and the cross axis of a row one, so each half covers one
  `flex-direction`. Tesserae's modal grows its content pane with `flex-grow: 1`
  inside a `height: 80vh` column and then stacks four `height: 100%` boxes under
  it, so the reference collapsed the whole chain (758/682/666/1677 in Chromium
  against 92/16/0/8) and the grid's `overflow: auto` clipped 1676px of cards into
  an 8px box. The port marks such a box definite but NOT known - the post-flex
  size is decided after this top-down pass - so a bare percentage survives and
  taffy resolves it against the used size, the same way the grid-item case does.
  A functional `calc()` percentage under such a box still flattens to `auto`,
  which is the same residual gap the grid case accepts. The taffy layer was
  already correct on its own; only the DOM style pass destroyed the percentage.
  `RenderDom.IsFlexSizedDefiniteBlock` / `IsStretchedFlexItem`, whose three
  stretch conditions are the ones `FlexboxLayout.DetermineUsedCrossSize` applies.
  The `min-height: min-content` half of that same chain (`.tss-grid` carries it)
  is fixed below - the grid now expands to 1677px, so the scroll parent owns the
  scrollbar as it does in Chromium.
- **DEVIATION - `min-height: 0` was used as the percentage basis for children.**
  Vendored taffy (`compute/block.rs`, and so the reference, which shares it) falls
  back to `min_size.height` as the children's percentage resolution height whenever
  a block has no resolved height of its own:
  `known_dimensions.height.or(size.height.maybe_max(min_size.height)).or(min_size.height)`.
  `min-height: 0` is the initial value and says nothing about the used height, so
  that fallback handed every `height: %` child a basis of 0 and collapsed it, where
  Chromium sizes the box from its content and resolves the percentage against that.
  `BlockLayout.ComputeInner` now only takes a POSITIVE minimum as the basis; a zero
  one leaves it unresolved, which makes the child content-sized - the same answer
  Chromium computes. A positive `min-height` still provides the basis, which is what
  Chromium does when the minimum is what the box ends up at.
  The reference never reaches this, because `dom.rs` rewrites every percentage height
  under an indefinite box to `auto` so no percentage survives to be resolved here;
  the entry above, which marks a flex-sized box a definite containing block, is what
  lets them through, and that exposed it. Curiosity Workspace's
  `#/manage/configure/subscription` nests `height: 100%; min-height: 0` twice under
  an auto-height flex item and its inner `overflow: hidden auto` stack came out 24px
  tall instead of 473px, so the route rendered blank while every leaf inside it sat
  at the right position. Chain heights, Obscura before -> after, against Chromium:
  `.msk-setting-group` 82 -> 546 (538), `.msk-setting-content` 40 -> 497 (489),
  the scrolling `.tss-stack` 24 -> 481 (473).
  Residual gap, unchanged by this: a positive `min-height` SMALLER than the content
  height is still used as the basis, where Chromium uses the content height
  (`min-height: 200px` over 437px of content gives 200px here and 437px there).
  Fixing that needs the box's used height before its children are laid out, which is
  a second block-layout pass.
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
- **Closed by the `contentSizedItem` narrowing: a shrink-to-fit block inside a
  flex row does get the intrinsic contribution of a cyclic-percentage child.**
  `flexrow > block > width:100%` is 7.828 in Chromium and 8 here. Re-probed over
  ten shapes - an `inline-block`, a float, a nested flex row and a plain block
  holding the percentage, each under a content-sized item and under a
  declared-width one - and all ten agree with Chromium. The note predates that
  narrowing.
- **DEVIATION - a flex item's automatic minimum size ignored a percentage-width
  descendant.** The definite `0px` that `DeferCyclicFlexInlineSizes` writes was
  also what the min-content measurement behind Flexbox 4.5 read, so the item got
  no content-based floor at all. Repro: a `flex: 0 1 200px` item in a 600px row
  against a `flex: 0 1 4000px; min-width: 0` sibling, holding one
  `white-space: nowrap` child. With the child `width: auto` both engines floored
  the item at its text (165 / 164.047); with the child `width: 100%` (or `50%`,
  or `box-sizing: border-box`, or nested a level deeper, or with the item itself
  a flex container) Chromium still floored at 164.047 and this engine dropped to
  the unfloored 29 - nine of ten probe shapes wrong.

  Flipping the `Percent` arm to `Auto` is what CSS Sizing 3 5.2.2 prescribes, but
  it was already swept and measured worse on four samples, because that arm also
  decides what the *max-content* and flex-base measurements see - and those are
  what `PinFlexItems` pins the item from. The two measurements want different
  neutralizations and one style rewrite cannot serve both, so the narrowing is to
  leave the arm alone and re-derive only the automatic minimum size:
  `DomSubgridPasses.ApplyDeferredFlexAutomaticMinimums` (new, in
  `DomPassesSubgrid`, called from `LayoutDomOnce` just before the first layout)
  puts the deferred percentages back typed, measures each affected item's
  min-content with the item's own `width`/`max-width` dropped, restores the
  neutralization, and installs the clamped result as the item's definite
  `min-width`. Taffy reads that in place of the measurement it would have taken
  off the collapsed subtree. Only an item whose automatic minimum size is really
  content-based is touched (`min-width: auto`, not a scroll container).

  All ten probe shapes now agree (165 against Chromium's 164.047, the same ~1px
  text-metric bias the `width: auto` control already had). Curiosity Workspace's
  admin sidebar on `#/manage/operate/usage` goes from 205 to 215 against
  Chromium's 214.422; over that sidebar's 127 boxes the mean absolute width error
  falls from 5.811 to 0.445 and the boxes off by more than 0.75px from 83 to 5.
  The `width: 100%` nav buttons that the `Auto` flip shrink-wrapped keep filling
  their row (199 against Chromium's 198.422), which was the specific regression
  to avoid.

## Known deviations

Recorded as they are decided. Each entry needs a reason and a tracking note.

### Security review fixes (September 2026)

- **I10 (engine globals):** Rust keeps the shim's state and host-set values as `globalThis.__obscura_*` / `__*` / `_*` globals and calls `__obscura_init`, `__markParserScripts`, `__documentReadyState__` and `__currentScriptNid` by name. The port keeps them in a closure object the host reaches as `__obscura_host.vars` (HostScript), and page init is `__obscura_host.init`. Only ClearScript's non-configurable `EngineInternal` remains, hidden from the global's reflection APIs; `in` and `typeof` still see it.
- **I10 (bindings):** Rust's `Runtime.addBinding` shim calls the page-visible `__obscura_binding_called`. The port files the binding name (`BindingPreload`) and installs a native-looking function that closes over `op_binding_called`; no bridge global exists.
- **I10 (modules):** module completion is recorded through a randomly named top-level `const`, not a `__obscura_moduleSettled_N` global.
- **Child frames:** Rust processes only the page's own navigation and reports every binding call with the main context. The port follows a frame's own GET navigation by reloading its iframe (a POST frame navigation is dropped), reports a frame's binding call with the frame's default context, scopes init scripts and bindings to the target that registered them (removing them when it closes), and emits the full navigation sequence (new loaderId, lifecycle, loadEventFired) for a click that loads a new document.
- **L10 (host invocation):** the by-value serializer is invoked as a method, because ClearScript routes a property-obtained function through its JS `invokeMethod`, which uses the page's `Array.from`/`apply`. Markdown escaping, the world bridge, the iframe loader, link referrer policy, the linked-stylesheet script, the form-state mirror, `op_dom` command checks and lifecycle events use built-ins captured at bootstrap.
- **Public suffix list (L6):** the full Mozilla PSL (ICANN and PRIVATE) is embedded, as the Rust `psl` crate does; the earlier curated C# table is gone. As in `psl`, a TLD not on the list gets the implicit `*` rule. Chromium instead gives a host under an unknown TLD no registrable domain; the implicit rule is kept so `.test` and `.internal` hosts keep their current site and cookie Domain behaviour.
- **SSL_CERT_FILE/SSL_CERT_DIR roots (I8):** the leaf must allow serverAuth (or anyExtendedKeyUsage, or have no EKU), as on the platform path and in Chromium. Revocation is not checked on either path (NoCheck, as Chromium does no online revocation checks).
- **HSTS and mixed content (I7):** Rust has neither. Each BrowserContext's HTTP client keeps an in-memory HSTS store filled from Strict-Transport-Security with Chromium's rules (no IP hosts, one-year cap, first header only); http requests to known hosts are upgraded and reported as a redirect hop; there is no preload list. Requests from https documents to insecure http hosts (loopback and localhost are trustworthy) are blocked, except images, which are upgraded to https without an http fallback; top-level navigations are exempt. Blocked and upgraded requests are reported on the Runtime console with Chromium's text. `POCKETCALCULATOR_ALLOW_INSECURE_CONTENT=1` / `AllowInsecureContent` turns the check off. Differences from Chromium: XHR and worker loads are reported as 'resource', audio and video are not modelled, only the requesting document's scheme is checked (not its ancestors'), and the fetch rejection carries the reason after 'Failed to fetch'.
- **History URL (L9):** Rust keeps it in the page-writable global `__virtualUrl`, which the host read back. C# keeps it in the bootstrap closure; moves reach the host through `op_history_url`, checked against the committed document URL, and `Page.SyncVirtualUrl` reads `PocketCalculatorState.HistoryUrl`.
- **MCP storage (L9):** `browser_storage_state` labels storage with the host-side document origin instead of `location.origin`. `browser_set_storage_state` applies only entries whose origin equals the current document's (an opaque origin matches nothing) and appends a note naming the origins it skipped. The restore snippets run in a function body; upstream's expression wrapping made them a silent no-op.
- **By-value serialization (L10):** Rust's `v8_to_json` is `JSON.stringify`, which calls `toJSON`. C# uses a bootstrap walker that never calls a page's `toJSON`, as Chromium does: Date/Map/nodes are `{}`, NaN/Infinity are null, undefined is dropped. Functions are dropped (Chromium gives `{}`), and URL/DOMRect/PerformanceEntry keep the shim's own `toJSON` while it is unchanged.
- **Focus and click fallback (L10):** Rust uses the page-writable globals `__obscura_focused`/`__obscura_click_target`; C# keeps them in closure state and the host uses `__obscura_host.clickTarget`.
- **CDP link click (L10):** Rust calls `location.assign(href)`; C# calls `__obscura_host.navigate` (the shim's own location path).
- **Scroll reads (L10):** Rust's `getLayoutMetrics`, capture and MCP scroll read `window.scrollX/scrollY/innerHeight`; C# uses the host scroll offset and viewport, and MCP scrolls through the shim's own path.
- **Input helpers (L10):** `setFieldValue`, `setInputFiles`, label activation, the disabled check, fragment navigation and listener calls use captured built-ins, `op_url_parse`/`op_url_set` and the History object as bootstrap left it; a replaced `Element.prototype.click` no longer receives the trusted-activation token. `Page.navigate` checks host-side that a target differs only in its fragment.
- **WebAssembly memory (M7):** Rust has no limit. C# caps it per isolate (`POCKETCALCULATOR_MAX_WASM_MEMORY_BYTES`, default 1 GiB) through a shim wrapper and `op_wasm_memory_admit`, and per memory through `--wasm-max-mem-pages`, set via ClearScript's exported `v8::V8::SetFlagsFromString` before V8 initializes (not on Windows). Refusals are Chromium's RangeError messages. Chromium itself allows up to 4 GiB per memory.
- **`ApplyTableUsedWidths` (`LayoutDomControls.cs`):** `crates/obscura-render/src/dom.rs` lays out with rounding for the table-width snapshot and every intrinsic measurement. C# uses `ComputeUnroundedLayoutWithMeasure`, since the pass reads only unrounded sizes and the tree is laid out and rounded again right after; `MaxDefiniteTableContentWidth` reads a one-pass `DefiniteContentWidthIndex` instead of walking every table's subtree. Output is identical. The per-depth relayout remains quadratic in table nesting depth, bounded by `MaxBoxDepth` (about 110 ms warm at the cap).
- **`Layout/Cache.cs`:** unlike vendor/taffy, a node's nine measurement slots are allocated on the first measurement rather than when the node is created. Behaviour is identical.
- **HTML tree construction is ours (M11):** `HtmlTreeBuilder` implements the WHATWG insertion-mode algorithm over AngleSharp's public `HtmlTokenizer` and builds straight into `DomTree`. Rust uses html5ever's tree builder; the port used AngleSharp's, whose scope checks and fragment moves were quadratic (50k nested divs took 20 s, a 50k-sibling fragment 91 s; now 0.14 s and 0.13 s). The stack of open elements links entries by name, scope and category so scope checks compare positions. It follows the current spec with the customizable-select changes Chromium 141 ships (no "in select" modes, `select` bounds "has an element in scope"; nested `<select>`/`<input>` close an open select; `hr`/`option`/`optgroup` imply end tags in a select), treats `menuitem`/`isindex` as ordinary and `search` as a block, parses SVG/MathML fragment contexts natively, applies Chromium's 512-deep `AttachLater` cap by stack depth, and checks `WorkCancellation` per token. html5lib tree-construction: 1756/1765 (the old adapter: 1659).
- **Attributes per tag:** a tag keeps its first 512 distinct attributes (html5ever and Chromium keep all); duplicates are dropped in constant time before AngleSharp's pairwise dedupe, which took 3 s for 20k attributes and could not be cancelled.
- **document.write is incremental, like Rust:** one tree builder stays alive across calls; a construct cut off at the end of a call waits for the next. Because `document.close()` is a no-op, a trailing `&`/`<`/CR in the last write never appears.
- **The tokenizer still drops U+0000 in the data state** (AngleSharp), so foreign content gets no U+FFFD.
- **ImageAgent:** paint.rs uses a bare HTTP agent. The port routes standalone image fetches through `PocketCalculatorHttpClient` (`PocketCalculator.Render` now references `PocketCalculator.Net`): SSRF on every hop, no ambient proxy, no `file:`, no cookies.
- **Idle timeouts (L3):** Rust keeps silent CDP connections and SSE streams forever. The port closes a CDP connection after `POCKETCALCULATOR_CDP_IDLE_TIMEOUT_MS` (30 min; any inbound byte including pings counts; a command in flight keeps it open) and an SSE stream after `POCKETCALCULATOR_MCP_IDLE_TIMEOUT_MS` (30 min with no MCP request).
- **TLS for control planes (I1):** Rust is plaintext only. The port offers `--tls-cert/--tls-key` (or `POCKETCALCULATOR_TLS_CERT/_KEY`) on `serve` and `mcp --http` via SslStream; discovery says `wss://`, workers terminate TLS, and the balancer probes them pinned to the certificate.
- **fetch methods and headers:** Rust sends any method and uppercases all of them; the port refuses CONNECT/TRACE/TRACK (TypeError; SecurityError in XHR `open`) and normalizes only DELETE/GET/HEAD/OPTIONS/POST/PUT (SocketsHttpHandler still writes PATCH uppercase on the wire). Rust lets script set User-Agent; the port drops it, as Chromium does. Rust sends Origin on every cross-origin hop; the port sends it on CORS cross-origin requests ("null" once tainted by a redirect) and on non-GET/HEAD requests, same-origin POST included.
- **Referrer policy:** Rust applies strict-origin-when-cross-origin to navigations and subresources only and sends no Referer on fetch/XHR/internal loads. The port implements all eight policies from the `Referrer-Policy` header, `<meta name=referrer>`, element `referrerpolicy`, `rel=noreferrer`, fetch options and redirect responses, with Chromium's parsing, the 4096-byte cap, and localhost as trustworthy for the downgrade check. The shim passes the referrer settings in `op_fetch_url`'s ignored `origin` slot; a link's policy reaches `op_navigate` through `op_dom` `set_navigation_referrer_policy`. A late meta applies to the whole document; an image's `referrerpolicy` on the layout path is matched by resolved `src` only; module fetches take the last computed document policy.
- **Mixed content leftovers (I7):** non-secure frames are checked against their nearest secure ancestor; `new WebSocket('ws:...')` from a secure context throws SecurityError (`op_dom` `websocket_mixed_content`); HSTS upgrades `ws:`; media is classified as upgradeable.
- **Child-frame execution contexts (M6):** each child frame gets a default context (its own realm) and a realm per isolated-world name over its document, announced with `executionContextCreated` and routed by object id; also `DOM.getFrameOwner`, `Node.frameId` on iframes, frame node boxes in page coordinates, input routed into the frame under the point, and key input to the last-focused realm. Rust announces frames with no contexts and runs everything in the page realm.
- **Frame render ops:** they read the frame's own document and viewport (the iframe's content box; Rust passes the border box). Rust reads the page's state, so a frame's geometry described the page node that shared its node id.
- **`Promise.prototype.then` captured at boot:** frame timers and async-op accounting use it; Rust calls the page's `then`.
- **Cross-world event dispatch:** a document realm's dispatch runs isolated-world listeners interleaved by registration stamp, with world-side wrappers and `isTrusted`/`preventDefault`/stop flags carried both ways. World window listeners run around the node dispatch (capturing before, others after), because the engine's event model has no node capture phase and does not reach the window.
- **File input selection:** held by the host per document (`op_dom` `set_input_files`/`input_files_version`/`get_input_files`, plus `note_focus`). Rust keeps it on the calling realm's wrapper.
- **Grid occupancy:** taffy stores one state per cell in a dense `rows x columns` matrix. The port keeps the placed areas: a byte per cell up to 1,024 cells, a segment tree of interval sets beyond that. Only occupied/free is kept; answers are cell for cell the same (`CellOccupancyMatrixTests`).
- **Grid coordinates are 32-bit:** taffy's lines are i16 and counts u16. `DetailedGridTracksInfo`/`DetailedGridItemsInfo` fields are `int`.
- **Grid axis limit:** `GridLimits.MaxAxisTracks` = 100,000 explicit plus implicit tracks per axis (Chromium's `kGridMaxTracks` is 10,000,000; track sizing cost bounds it lower here). Template tracks, line numbers and spans still clamp at 10,000. A position found past the limit is pulled into the last track after the search (Chromium), not when the search first crosses it.
- **Step 2 auto-placement cursor:** taffy starts after the last auto-placed cell in the item's first row, converting it with the other axis's track counts. The port keeps Chromium's cursor per start line and jumps over occupied tracks.
- **Implicit-grid estimate:** taffy's `(Auto, Line(n))` gives min line n; the port uses n - 1, so `grid-row: auto / 1` counts its negative implicit track (Chromium).
- **Minimum contribution:** taffy checks every track in the axis for an auto minimum and for fr per item; the port computes the same answer once per axis.
- **`getComputedStyle` grid properties:** the snapshot carries `grid-template-columns`/`-rows` (used px on a grid container, the declared list elsewhere), `grid-template-areas`, `grid-auto-columns`/`-rows`, the placement longhands and `grid-column`/`grid-row`/`grid-area`. Rust reported none of them. New keys in `op_computed_style`; no existing key changed.
- **`grid-row-start`/`grid-column-start` then a named `-end`:** the numeric start is kept; Rust resets it to `auto`.
- **Balancer 503 over TLS (I1):** the multi-worker balancer terminates TLS for its own max-connections 503 (handshake, reply, close_notify), and past `maxConnections` refusals in flight closes refused clients without a reply. Upstream has no TLS and no cap.
- **Referrer downgrade:** scheme-only (https to non-https), including http loopback, as Chromium's network layer does.
- **CDP `Page.navigate` referrer:** `referrer` and `referrerPolicy` are honoured (Chromium 141): the policy is applied against the target, default strict-origin-when-cross-origin, and the navigation stays browser-initiated otherwise; an unknown policy is `Invalid referrerPolicy`. Upstream ignores both.
- **Stylesheet subresource referrers:** a nested `@import` is referred by the importing sheet under its `Referrer-Policy` header, else the document's policy; images and fonts named by an external sheet are referred by the sheet under its header policy, else the default (the document policy does not apply), as in Chromium 141. Rust refers every stylesheet load from the document.
- **Frame referrers:** about:srcdoc and about:blank frames inherit the parent's referrer policy; a srcdoc frame refers as its parent (requests, images, navigations) and its `document.referrer` is the parent under that policy; about:blank sends no Referer.
- **Response and XHR headers:** fetched, cloned, `Response.error()` and `Response.redirect()` headers are immutable (TypeError as in Chromium). XHR `setRequestHeader` throws TypeError, InvalidStateError and SyntaxError synchronously with Chromium's messages, and combines repeated names. Rust accepts any header and overwrites.
- **CHIPS:** Rust's jar has no partitions. The port parses `Partitioned`, rejects it without `Secure`, and keys the cookie by (top-level site, cross-site ancestor bit) of the setting context; it is sent and visible only in that partition, and a partitioned and an unpartitioned cookie of the same name coexist (Chromium 141). CDP cookies carry `partitionKey {topLevelSite, hasCrossSiteAncestor}` only when partitioned; `Network.setCookie` takes that object with both fields, reduces the site and forces Secure, and a malformed key fails the call; `deleteCookies` without `partitionKey` deletes only unpartitioned cookies. `cookies.json` and the MCP storage state write `partitionKey` / `partition_key` only for partitioned cookies, so older files load unchanged.
- **Site for cookies:** Rust judges SameSite by the initiator alone. The port requires the initiating frame, all its ancestors and the target to be same-site with the top-level document, so a cross-site frame's `document.cookie`, fetches, scripts and subframe loads see and send only SameSite=None cookies, and a cross-site context can set only SameSite=None cookies. A top-level navigation's response may still set any cookie.
- **Frame scripts:** Rust fetches a frame's `src=` scripts with the navigation profile. The port fetches them as no-cors script loads by the frame document: Referer from the frame URL (a srcdoc frame's parent) under the frame's referrer policy, Sec-Fetch-Dest script, and the frame's cookie scope.
- **Frame Referrer-Policy header:** a frame document's own `Referrer-Policy` header applies to that frame's requests, and a cross-origin frame's or stylesheet's header is kept host-side even though page script is not shown it.

- **CDP remote objects (L10):** handles and awaited outcomes live in the realm's bootstrap
  closure (`_cdpHost`), handed to strict host wrappers as `__obscura_cdp`, not in the
  page-visible `__obscura_objects`/`__obscura_await_*`/`__obscura_done_N`. Client source and
  `callFunctionOn` declarations run through the eval captured at bootstrap, as an indirect
  eval at global scope (sloppy mode, `var` on the global, as before). Metadata and by-value
  serialization use built-ins captured at bootstrap. `unserializableValue` accepts only
  `Infinity`, `-Infinity`, `NaN`, `-0` and BigInt literals instead of being pasted in as
  source.
- **Host snippets (L10):** CDP Input/DOM, the MCP tools, LP.getMarkdown and the library
  `Element` API reach the DOM through `__obscura_host.dom`: prototype members as bootstrap
  defined them, the shim's own event classes, the realm's own document. Rust calls the
  page's current globals and prototypes.
- **Shim internals (L10):** internal dispatches, internal `querySelector(All)`, node-list
  building, JSON and event construction use the shim's own methods and classes, not the
  page-writable globals. `_wrap` is no longer a global (`__obscura_host.dom.wrap` /
  `__obscura_cdp.wrap`).
- **Frame registries (L10):** private to the bootstrap, reached through
  `__obscura_host.publishFrameObjects`, `forgetFrame`, `forgetFrameObjects` and
  `frameRegistrySize`.
- **dispatchEvent (L10):** a trusted mark covers only the dispatch it was made for, so a
  later re-dispatch has `isTrusted` false, and re-dispatching an event in flight throws
  `InvalidStateError`. Matches Chromium, measured.
- **Hit testing (L10):** `elementFromPoint` and CDP clicks ignore page overrides of
  `document.elementFromPoint`, `querySelectorAll` and `getBoundingClientRect`, as in
  Chromium.

- **ArrayBuffer cap (M7):** Rust sets no `ArrayBuffer` limit. Each runtime caps
  backing stores at 1 GiB (256 MiB on 32-bit) through ClearScript's
  `MaxArrayBufferAllocation` (`POCKETCALCULATOR_MAX_ARRAY_BUFFER_BYTES`, 0 for none).
  Over it V8 throws `RangeError: Array buffer allocation failed`; host op results
  (crypto) reject with `OperationError`; a canvas whose backing store cannot be
  allocated has a `null` 2d context. WebAssembly memory is not counted.
- **DOM byte budget (M7):** Rust has none, and Chromium runs out of memory instead.
  Each `DomTree` charges 64 bytes per node plus 2 per UTF-16 unit of text, comment,
  processing-instruction, doctype and attribute data, incrementally (512 MiB default,
  `POCKETCALCULATOR_MAX_DOM_BYTES`, 0 disables). A growth past it throws
  `QuotaExceededError` from script (op_dom returns `quota-exceeded`) with the document
  unchanged, after one collection and retry; `innerHTML` parses and checks before
  detaching the old children; a parse keeps what fits (`ParseTruncated`).
- **Long-word layout (M7):** the port's word cache skips words over 4096 glyphs, and
  glyph lists are sized up front: about 350 bytes per character instead of 710, with
  no observable change.
- **Per-process backstop (M7):** opt-in `POCKETCALCULATOR_MAX_PROCESS_BYTES`, no Rust
  equivalent. A 100 ms working-set sample over the limit after one forced GC terminates
  the running work of every live isolate and refuses new runtimes
  (`ProcessMemoryExceededException`) while the process stays over.
- **op_fetch_url result (M4):** the JSON is byte-identical to Rust's, but it is written
  once from the body bytes in two passes instead of holding the decoded string, the
  base64 string, a builder and a final copy. A declared Content-Length is read into an
  exact array. bootstrap.js `_utf8DecodeBytes` joins 8K chunks instead of appending per
  character; the output is identical.
- **DOM garbage collector (`DomTree.Gc.cs`, `PocketCalculatorJsRuntime.DomGc.cs`,
  bootstrap.js `_gcWeaken`/`_gcSurvivors`/`_gcForget`):** Rust never frees a detached
  node and keeps every wrapper in a strong `_cache`, so a long-running page leaks and,
  under the DOM budget, would eventually fail. The port frees detached components
  (parent/child, shadow root to host, template to contents) that nothing holds: a
  connected node, a CDP or library pin, an isolated world's pending record, the
  `document.write` stream, or a live JS wrapper in any realm. A full pass runs at task
  boundaries with one exhaustive V8 collection over temporarily weakened wrappers; a
  conservative in-op pass runs when due or when the budget refuses a mutation. Chromium
  frees unreachable detached nodes the same way.
  - **Node ids:** a 7-bit generation sits above a 24-bit slot index, so a stale id never
    aliases a reused slot; slots retire at generation 127, at most 16M slots per tree.
    Ids equal Rust's until a slot is reused. op_dom and DOMSnapshot `backendNodeId`
    serialize the full id.
  - **Pins:** the CDP DOM domain pins the node ids it hands out until the next
    `DOM.getDocument`, like Chromium's DOM agent; library `Element` handles pin while
    alive.
  - **Mutation records:** `__notifyMutation` builds a record only when an observer
    matches, and the `innerHTML` setter reads the old children only when some observer
    can see that element's child list. Rust does both whenever any observer exists.
- **CDP isolated worlds are realms (M6):** Rust records a context for
  `Page.createIsolatedWorld` and runs everything addressed to it in the page realm. C#
  gives each main-frame isolated world its own engine on the page isolate: bootstrap.js
  over the page's document, created on first use, at most 32 per document, disposed with
  the runtime. World objectIds carry the context id as `injectedScriptId`;
  `DOM.resolveNode` honours `executionContextId` and `DOM.requestNode` is added;
  `addScriptToEvaluateOnNewDocument` with `worldName` and `addBinding` with
  `executionContextName` target the world. A world reaches the page realm's form state,
  focus, selection and `click()` through implementations captured at bootstrap end
  (`worldCall`); host-built mutation records are forwarded between realms. Console calls
  from a world are not reported.
- **`document.head`/`body`** use `Document.prototype.querySelector` captured at bootstrap
  end, so an own-property `document.querySelector` override does not remove them
  (Chromium behaviour).

- **Cancellation of work inside ops (H8, L12):** Rust's `terminate_execution` has the
  same gap the port had: C# (or Rust) work inside an op runs to completion, and the
  interrupt raised during it is lost. The port gives each isolate a
  `ScriptCancellation` that every watchdog (`WatchdogScheduler`, `CdpWatchdog`)
  cancels before interrupting, and `CancelTermination` or a settled watchdog resets.
  - **Where it is observed:** sync ops (`FastOpBinding.WithCancellation`) and captures
    (`WithSyncRenderLoadingDisabled`) run under it, through the thread-static
    `WorkCancellation` scope. The render walks, taffy's dispatch, inline line-edge sums,
    selector matching, `Descendants()` and serialization check it. The public
    `RenderPaint` leaf overloads take a `CancellationToken`.
  - **How a cancelled op ends:** it returns `undefined` rather than throwing (ClearScript
    would clear the termination to raise the exception, and the shim's `try`/`catch`
    would swallow it). It keeps re-interrupting until reset
    (`ScriptCancellation.EnsureInterrupted`). An evaluation whose deadline passed
    reports "execution terminated" instead of its result.
  - **Callers and MCP:** `InterruptOnCancellation` lets a caller's token act as a
    watchdog; the navigation script phase uses it with the navigation token. MCP tool
    calls run under `POCKETCALCULATOR_MCP_TOOL_TIMEOUT_MS` (default 60 s); Rust's MCP
    has no bound at all.
  - **Other paths:** CDP `Dispatch` answers a watchdog-cancelled capture with an
    ordinary error. The print-economy capture restores its styles in a `finally`.
- **Layout cache keeps evicted measurements (`Layout/Cache.cs`):** taffy's cache (`vendor/taffy/src/tree/cache.rs`) keeps one measurement per slot and drops the old entry. The port moves an evicted entry into a per-node ring of up to 16, allocated on the first eviction, and checks it after the 9 slots. A shrink-to-fit parent (a float laid out as a flex row) measures its child at its min-content and max-content widths, which share a slot and evicted each other, so every level re-measured its subtree: 300 nested floats took over a minute, now about 1.8 s. Lookups still match the full key, so a hit returns exactly what a recompute would; only the hit rate changes.
- **Grid track limit (`GridLimits.MaxTracks = 10000`):** Rust/taffy has no bound (it caps each `repeat()` at 1000 and uses 16-bit lines that wrap). The port follows Chromium's `kGridMaxTracks` rules with a limit of 10,000 instead of 10,000,000, because taffy's lines are `i16`.
  - `repeat()` nested in `repeat()`, and `repeat(0, ...)`, make the whole `grid-template-*`/`grid-template`/`grid` declaration invalid, so the previous winner is kept. Rust expanded them.
  - Track lists are truncated at 10,000 tracks; Rust's per-`repeat()` 1000 cap is gone (`repeat(1001, 1px)` gives 1001 tracks, as in Chromium).
  - Line numbers and spans clamp to ±10,000 at parse time; Rust turned out-of-range values into `auto`.
  - auto-fill/auto-fit floors each repeated track at 1px and stops at the limit; Rust divided by zero for 0px tracks and cast the float to `u16`, which hung.
  - Placement pulls every item into a window of at most 10,000 tracks per axis (past the end an item lands in the last track, as in Chromium), and the occupancy estimate is clamped the same way. Auto-placement jumps over occupied runs; the positions found are the same.
  - `grid-template-areas` line indexes and named-placement integers are clamped instead of wrapping in `short` casts.
- **`column-count`:** any positive integer is accepted and saturates; Rust rejected values above `u32`. The used count keeps Rust's 64-column cap (Chromium's computed value caps at 65535).
- **innerText is computed natively (`op_inner_text` -> `PreparedRender.InnerText`):** crates/obscura-js walks the subtree in bootstrap.js with one getComputedStyle() per element and recursion per level, O(n x depth), and overflows the stack on deep trees. The script walk stays as the fallback (unstyled element, unrendered root, XML document); the two must stay in step. The op is an addition; no existing op payload changed.
- **Range.toString() walks natively (`op_dom` `range_text`)** instead of the bootstrap walk over the whole common-ancestor subtree with two boundary comparisons per node. An additive op_dom command.
- **`op_dom` `contains` walks up from the candidate** instead of collecting the subtree, and `node.contains(node)` is true (inclusive, as in the spec and Chromium); crates/obscura-js tests strict descendants only.
- **innerText follows Chromium's rules:** `<br>` is a literal newline, `pre-line` collapses spaces, U+000C is not collapsed, and a hidden element contributes no line breaks or tabs of its own. crates/obscura-js treats `<br>` as a required break, keeps `pre-line` spaces, collapses form feeds and adds breaks for hidden blocks.
- **appendChild's "already last child" check reads `LastChild`** instead of copying the parent's child list, and `InheritedBorderSpacing` is cached per prepared render: both were O(children) / O(depth) per call.
- **Inline edge sums (`InlineEdgeIndex`):** inline.rs `line_edge_advance`/`line_advance_before_event`/`line_advance_before_text` scan every boundary event, and `boundary_event_on_line` scans every owner box per event. C# precomputes emptiness per event and, above 32 events, answers from per-line running f32 sums kept in the scan's order. Same bits; cost goes from O(events x owners) per query to linear per shaping.
- **Owner text ranges:** Rust `record_text` pushes an (owner, range) per open owner per text run. C# records the innermost owner per run plus a parent chain, and `SetInlineOwnerOffsets` emits one range per run for the innermost owner with a relative offset. Painted offsets are unchanged; this removes quadratic memory for nested inlines.
- **Windowed first-line probe:** Rust `shape_with_text_indent` shapes and wraps the whole remaining line for every line it breaks. For lines over 2048 characters with no right-to-left text, C# shapes a window cut between two printable ASCII characters and accepts it only when line 2 ends before the window's last word, which provably gives the same first line; otherwise the window grows to the whole line.
- **Word shaping cache:** each TextShaper reuses glyphs for a word under a single attribute span, keyed on text, attributes and direction. Shaping is per word with no context, so the result is identical.
- **`materialize_start_candidates`:** Rust walks each root's full subtree; C# stops at nodes already visited in the same call. The candidates are the same.

The fixes for `SECURITY.md`'s findings. Each is commented at its site. Where Rust
and Chromium differ, Chromium wins.

- **C1 (host-derived request origin):** Rust `op_fetch_url` uses the origin the shim passes, computed with the page's `URL`. The port ignores the argument and uses the calling realm's committed document origin (`"null"` for opaque or sandboxed documents); each realm's op table is bound to its own document.
- **M3 (fetch deadline):** Rust's timeout covered each hop's headers only. The port has one deadline (`POCKETCALCULATOR_FETCH_TIMEOUT_MS`) for preflight, all hops and the body.
- **H4 (postMessage origins):** Rust queues the source frame and origin the shim sends. The port fills both from the sending realm and checks targetOrigin against the receiver's host-known origin before delivery; the shim's `_realmOrigin()` asks the host (`op_realm_origin`).
- **C3 (internal-load bodies):** Rust returns every internal-load body, cross-origin included, to the shim as JSON. The port keeps it host-side behind a `bodyToken`; only a same-origin frame document is returned. The host runs dynamic scripts (`op_run_fetched_script`), builds frames (`op_frame_document_from_load`) and fetches, rebases and installs dynamic stylesheets with a host-computed origin-clean bit (`op_load_stylesheet`, `LinkedStylesheetLoader`). An `@import` media query naming a width or `prefers-` feature is evaluated with `CssMediaQuery` against the viewport, where the shim used `matchMedia`.
- **C2 (frame isolation):** Rust keeps frame state in element expandos and judges same-origin with the page's `URL`. The port keeps it in closure WeakMaps, asks the host (`op_frame_same_origin`), returns a cross-origin-only window for cross-origin frames, and gives `sandbox` without `allow-same-origin` an opaque origin (Rust has no frame sandboxing).
- **L9 (history):** Rust `pushState`/`replaceState` accept any URL. The port throws Chromium's SecurityError for a URL that cannot rewrite the document URL.
- **L10 (Uint8Array):** op results use the realm's `Uint8Array` captured at bind time, not the global looked up per call.
- **C4:** `SsrfGuard.ValidateUrl` refuses `file:` unless the caller allows it. The transport allows it only for a top-level navigation with no initiator, or a request whose initiator is `file:`; a redirect never reaches `file:`. The module loader lets a graph reach `file:` only when both the document and the importer are `file:`, and reports every `file:` module failure as "Failed to fetch dynamically imported module: <url>". Rust (`client.rs`, `module_loader.rs`) serves `file:` to every caller.
- **H1/H2:** every CDP navigation (`Page.navigate` with or without a session, the autonomous pump, reload, `navigateToHistoryEntry`) takes one gate, `NavigationRefusal`. A page-initiated navigation from a non-`file:` document into `file:` is refused even with `--allow-file-access`. Rust gates only the sessionless `Page.navigate`.
- **H3:** `Page.ProcessPendingNavigationOutcomeAsync` and `NavigateWithWaitPostAsync` (when given an initiator) refuse a navigation from a non-`file:` document into `file:` (Chromium's rule). MCP `browser_tab_new`, back, forward and reload refuse `file:` like `browser_navigate`. Rust gates only `browser_navigate`.
- **M2:** the transport ignores `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` and uses only a configured proxy. Unless private network is allowed, a proxied target is resolved and vetted locally first; a forbidden address or an unresolvable name is refused. Residual gap: the proxy does its own lookup (rebinding). reqwest honours ambient proxies and never checks the target behind one.
- **M3 (transport):** one deadline, `Timeout` (30 s default), spans each hop's headers and body. Rust bounded only the headers.
- **M5:** `CookieJar.Store` enforces `__Secure-`/`__Host-` case-insensitively and refuses a nameless cookie whose value carries a prefix; drops cookies over 4096 bytes of name plus value; caps a domain at 180 (purged to 150) and the jar at 3300 (purged to 3000), expired first then least recently used. Counts per jar domain, not per registrable domain; no priority. `cookies.rs` has no prefixes or limits.
- **L7:** the tracker blocklist applies to every redirect hop (status-0 response) and every request on `RequestClient`, including op_fetch_url (`net::ERR_BLOCKED_BY_CLIENT`). Rust checks only the first URL of the main transport.
- **L8:** `InterceptAction.ModifyHeaders` applies to that request only. Rust merges into the client-wide extra headers permanently.
- **I9:** the SSRF deny-set adds `fec0::/10` and `2001::/32` (Teredo).
- **C5 (parser depth cap):** `HtmlParsing.Adapt` mirrors Chromium's `kMaximumHTMLParserDOMTreeDepth` (512): past that open-element depth an element or comment is attached to its intended parent's parent, so parsed content nests at most 513 deep. Rust nests as deep as the markup says, and 3000 unclosed divs overflowed the stack in layout.
- **C5 (box-tree depth and stack policy):** `BuildContext.MaxBoxDepth = 768`: deeper elements get no box (as display:none); only script-built trees reach it, and Chromium would still lay them out. A DOM deeper than 256 is laid out and painted on a 256 MB-stack thread (`StackGuard.RunWithStackFor`). Recursive Render walks and nested paint layers stop when the stack runs low (`StackGuard.CanDescend`); taffy's child-layout dispatch throws `InsufficientExecutionStackException` as a last resort. Nested CSS style rules and at-rules past what the stack holds are dropped. Rust recurses without bound; a 1500-deep chain aborted the process.
- **C6 (selector nesting):** more than 64 nested functional pseudo-classes make a selector invalid (the query finds nothing, the style rule is dropped), not relaxed by forgiving `:is()`/`:where()`. `:has()` inside `:has()` is invalid, as in Chromium. Matching reports no match instead of overflowing on very long combinator chains. Invalid selectors still do not throw SyntaxError (inherited). Rust's `selectors` crate recursed without limit.
- **H5:** `DomTextMeasure.MeasureAdvances` stackallocs at most 256 chars and rents beyond; Rust has no equivalent buffer.
- **M9 (serialization raw-text set):** text is raw only under HTML style, script, xmp, iframe, noembed, noframes, plaintext and noscript (scripting on), per the HTML fragment serialization algorithm and Chromium. Rust `serialize.rs` emitted script/style/textarea/title text raw in any namespace (mXSS).
- **M10 (markdown escaping):** `MarkdownScript` no longer matches `markdown.rs` byte for byte: text escapes `<`/`>` (code too); link and alt text backslash-escape `\`, `[`, `]`; destinations percent-encode whitespace, controls, `()`, `<>`; links and images with a scheme other than http/https/mailto (relative allowed) emit only their text. Parity `Dump_html_matches`/`Dump_markdown_matches` differ on such fixtures.
- **M11 (mutation bookkeeping, no observable change):** the retained-style classification runs only for mutations that invalidate rendering; `ContainingShadowRoot` and `node_root` short-circuit when the tree has no shadow roots.
- **H6:** Rust calls `wuff` with no size limit. C# holds each WOFF1 table to its declared origLength (rejecting a mismatch, or compLength > origLength), bounds WOFF2 Brotli output to the sum of table lengths, and caps the decoded font at 30 MiB (`Woff.MaxSfntSize`, google/woff2's default). A refused WOFF is a failed load; its raw bytes are not passed to the font backend.
- **H7:** Rust decodes at full intrinsic size and allocates the whole CSS box. C# checks the header against 64M decoded pixels (`MaxDecodedImagePixels`): over it, decode at a reduced scale where the codec can, else broken image. Untransformed raster images rasterize only the on-surface part of the destination. `Pixmap.New`/`Mask.New` refuse more than 4x `CaptureLimits.MaxCapturePixels` (64M px; was 512M).
- **H8 `<use>`:** resvg/Rust caps only depth; C# also caps each SVG render at 250,000 element visits (`SvgDocument.MaxElementVisits`), the rest is not painted.
- **H8 `var()`:** Rust caps only depth; C# treats a substitution over 2 MiB as guaranteed-invalid (Chromium `kMaxVariableBytes`), with a 4x work budget per substitution.
- **H8 `:has()`:** Rust recomputes `:has()` at every anchor; C# caches results per (argument, tree, anchor) for one query or cascade.
- **M4:** Rust starts every `op_fetch_url` at once; C# allows 6 in flight per page and queues the rest (`POCKETCALCULATOR_FETCH_MAX_CONCURRENT`).
- **M7:** `PendingBindingCalls` capped at 4096 entries / 8 MiB, newest dropped (`POCKETCALCULATOR_BINDING_QUEUE_ENTRIES`/`_BYTES`). Every `PocketCalculatorJsRuntime` without `--max-old-space-size` gets a 4 GiB heap cap (1 GiB on 32-bit), replacing "heap cap only exists once configured".
- **M8:** Rust caps PBKDF2 iterations and length separately; C# also caps their product (4 x 10M iteration-blocks), `CryptoOperationException` over it.
- **L11:** `RgbImage` uses checked 64-bit size math and refuses a header over 4x the capture budget.
- **L12:** opt-in `POCKETCALCULATOR_HANG_EXIT_MS` (`HangEscalation`) exits serve/mcp with 124 when a watchdog-interrupted command does not return within that many ms. `HardDeadline` exits through `ProcessExit.Immediately(124)`.
- **M1:** Rust starts workers as `current_exe() serve ...`. Under the `dotnet` host the port passes the entry assembly first (and refuses when there is none), and the balancer verifies each worker is a CDP server that enforces the token (401 without it) before serving.
- **I5:** Rust passes workers only proxy, user agent, font dirs and `--stealth`. The port also passes `--allow-file-access`, `--storage-dir` (shared: atomic rename, last save wins), `--max-connections`, `--allow-private-network` and `--v8-flags`.
- **L3:** Rust accepts 64 MiB per CDP message and has unbounded reply queues, targets and balancer connections. The port: 16 MiB per message by default (`POCKETCALCULATOR_CDP_MAX_MESSAGE_BYTES` raises it), a 128 Mi-char reply queue per connection (overflow closes it), 512 targets per connection ("Too many targets"), balancer capped at `--max-connections` (503, `X-Obscura-Reason: max-connections`).
- **L4:** Rust closes a CDP connection on any message containing `"Browser.close"`; the port only when the parsed method is `Browser.close`.
- **M6:** Rust reports `Runtime.bindingCalled` for any name page script passes to `__obscura_binding_called`; the port only for names with a live `Runtime.addBinding` in the context. Isolated worlds are still the main world.
- **L1:** Rust MCP HTTP never reads `Host`; the port applies CDP's Chromium rule (403 `{"error":"host not allowed"}`).
- **L2:** Rust detaches SSE streams from the connection permit; the port counts them against `MaxConnections` and caps streams at 16 (503 `{"error":"too many event streams"}`).
- **I2:** CDP and CLI log records escape control characters and line separators as `\uXXXX`.
- **I3:** `Using proxy:` logs the URL with user info replaced by `***`.
- **I4:** the cookie jar file is written 0600 and a storage dir the save creates 0700 on Unix; Rust uses the umask.
- **I6:** Rust reads `__method`/`__body` from any client `Page.navigate`. The port strips `__method`, `__body`, `__initiator`, `__userActivated` from every client request; only the server's own forwarded navigations carry them.
- **I10 (open):** ClearScript defines `EngineInternal` non-configurable on the global; it cannot be deleted or hidden without a new tell. Pinned by `EngineInternalHidden` (skipped test with reason).

### Upstream security ports 727cc46..1a3169d

Each is commented at its site. Where upstream and Chromium differ, Chromium wins.

Page script and the shim:

- `globalThis.Deno` is deleted after bootstrap in every realm (upstream 04418a5 A). The shim
  closes over `__obscuraCore`; host script reaches the page through `__obscura_binding_called`
  and `_wrap`. Only `PocketCalculator.Js.Tests` get `__obscura_test_ops`, through
  `BootstrapLoader.ExposeOpsForTests` (upstream's cfg(test) `expose_ops_for_tests`).
- Stylesheet ops use the closure-private `_realmFrameId`, not page-writable
  `globalThis.__obscura_frameId`, so one realm's script cannot aim them at another's document.
- `sheet.href` stays the request URL (CSSOM); upstream binds it to the response URL. The
  response URL is kept privately for origin-clean writes.
- A stylesheet is tainted when any redirect hop left the origin, not only the final URL
  (Fetch response tainting): the dynamic path treats an opaque response as unclean, the static
  path checks `Response.RedirectedFrom`.
- Removing a linked `<link>` keeps its stored bytes, so re-appending restores the sheet as in
  Chromium; upstream deletes them. `DomTree.ExternalStylesheetCss` skips a `<link disabled>`
  instead of the shim writing an empty sheet. The store joins text on write, and
  `op_external_stylesheet_set` invalidates the render only when the CSS changed.
- `contentDocument` never treats the opaque origin `"null"` as same-origin. An iframe loads
  through `op_fetch_url(..., internalLoad=true)`, since a public no-cors fetch() is now opaque.
- The fallback `Response` keeps `status: 0` rather than reading it as 200.
- The shim's host helpers are not globals: `markTrusted`, `setFieldValue`, `setInputFiles`,
  `deliverMessage`, `activateLabel`, `isDisabled`, `labeledControl`, `interactiveHost`,
  `registerLinkedStylesheet`, `tryFragmentNavigate`, `setScreenOverride`, `liveFrameIds`,
  `forgetFrame` and the CDP pointer state. Upstream publishes them as `__obscura_*` globals, so
  any page can mark its own events trusted. They travel in one frozen object through
  `__obscura_host_handoff`, which `BootstrapLoader` deletes in every realm; host script gets it
  as the `__obscura_host` parameter of a strict wrapper (`HostScript`, `EvaluateHost` /
  `ExecuteHostScript`), which must never run client- or page-supplied code. The trusted-event
  set is read through WeakSet methods captured at bootstrap. `postMessage` reports the
  closure-held realm id as its source and the form-state mirror uses the realm's fixed frame id;
  upstream reads the page-writable `__obscura_frameId` for both.
- Headers follow Fetch: names and values are validated (TypeError), appends combine with ", ",
  iteration is sorted and lower-cased, and a Request's headers take the "request" or
  "request-no-cors" guard, which silently ignores forbidden request-headers and (no-cors)
  anything but a no-CORS-safelisted Accept/Accept-Language/Content-Language/Content-Type. A
  no-cors Request/fetch() with a method other than GET/HEAD/POST, an invalid mode or
  "navigate" throws TypeError. XHR `setRequestHeader` ignores forbidden headers. Rust keeps and
  forwards every header and method.

Network:

- A redirect downgrades to GET only where Fetch says so: 301/302 for POST, 303 for anything but
  GET/HEAD (`FetchOps.RedirectDowngradesToGet`). Rust downgrades every 301/302/303.
- op_fetch_url filters page-script requests (internal_load false): forbidden request-headers
  are dropped in every mode, no-cors keeps only no-CORS-safelisted headers and rejects methods
  other than GET/HEAD/POST, an unknown mode is cors, and same-origin mode refuses a
  cross-origin hop with the corsBlocked payload. Rust forwards everything and treats any
  non-"cors" mode as CORS-free. CDP `continueRequest` headers and internal loads are not
  filtered.
- Navigations: upstream sends every navigation with no initiator, `Sec-Fetch-User: ?1` always,
  origin-only `Sec-Fetch-Site`, and iframes as no-cors same-origin-credential fetches. The port
  records the initiating document in op_navigate (`PendingNavigation`), forwards it through CDP
  as `__initiator` / `__userActivated`, and applies Chromium 140's rules: Strict withheld
  cross-site and when the redirect chain crosses sites (even browser-initiated), Lax only on
  top-level safe-method navigations, `same-site` as its own value with the least trusted hop
  winning, `?1` only with transient activation (CDP Input, `userGesture`, MCP click/press; 5 s),
  Origin on document-initiated POST, Referer trimmed per hop and never restored. Iframe
  documents load through op_fetch_url mode `navigate`, credentials `include`. Measured against
  Playwright's Chromium 140. (An earlier note here said Chromium blocks third-party cookies in
  cross-site iframes; Chromium 141's default does not, and the port matches it.)
- The `Fetch.fulfillRequest` result for `op_fetch_url` omits `bodyBase64` (Rust has it since
  #912, not ported); the C# fulfill body is text only.
- Cookies: an empty `Domain=` is ignored (host-only), not rejected; on an IP host only the exact
  address is accepted, as a host cookie; `CookieJar.IsSameSite` treats an IP as its own site
  (Rust's psl groups IPs by their last two octets). The CDP server's cookie delta merge keeps
  host-only scope, which Rust widens. The curated public suffix list (see "The public suffix
  list is curated") now also governs cookies.

Rendering:

- After a capture, the CLI and MCP screenshot paths load what that capture's own layout missed
  through the transport and capture again (`Page.LoadCaptureMissesAsync`); upstream leaves those
  bytes out of that capture. Chromium would have loaded them, and the port used to fetch them
  synchronously, so this keeps what those pages showed.
- The autonomous CDP turn awaits to completion instead of being cancelled by an arriving
  command, so waiting on a pending render load is capped at 50 ms and the pump stays armed.
- A runtime with no transport drops the misses a capture records (its next synchronous layout
  loads them) instead of marking them missing. Each document's loads are one object, retired
  and replaced on a new document (upstream swaps an mpsc channel); the applied-event backlog is
  capped at 1024; `data:` URLs are skipped in the warm-up scan.

Servers:

- CDP Host check (`Server.Control.cs` `HostAllowed`) follows Chromium's
  `RequestIsSafeToServe`: no Host, any IP literal, or `localhost` / `*.localhost` on any port;
  a wildcard bind accepts any Host. Upstream requires the bind IP and port and refuses a
  missing Host, which breaks tunnels and port forwards.
- Multi-worker `serve` refuses a non-loopback balancer bind without `POCKETCALCULATOR_CDP_TOKEN`
  (32+ bytes) before binding or spawning. Upstream a156914 leaves it to the workers, which
  refuse through the forwarded authority, so its balancer fails with "worker 1 exited during
  startup". Workers enforce the token too.
- Forwarded CDP authority (a156914, `POCKETCALCULATOR_CDP_FORWARDED_HOST/PORT`): the forwarded
  address widens the Host check exactly as a bind to that address would, on any port (upstream
  also requires the forwarded port). With no usable Host, discovery advertises the balancer's
  authority rather than the worker's internal port. The balancer forwards the address and port
  it actually bound (upstream forwards the raw `--host`/`--port`, which breaks
  `--host localhost` and `--port 0`), kills its workers if startup fails (upstream orphans
  them), and never peeks the request.
- CDP refusals drain the unread request and half-close before closing, so the client reads the
  401/403/431 rather than a reset; Rust drops the stream.
- CDP shutdown stops the accept thread before disposing the listener: disposing it under a
  blocked `Accept()` made .NET close fd 0, which in a process with stdin closed is some other
  live handle.

### Operator font directories load per render pass, not into a cached base database

Upstream 343fdc7 adds `serve --font-dir DIR`: `ttf/ttc/otf/otc` files found recursively
(symlinks skipped, paths sorted, each file once) join the base font database, configured
once per process before the first render. The port keeps all of that observable behaviour
(`PocketCalculator.Render/Inline/FontDirectories.cs`), with three differences:

- The files are read once into memory and each `TextEngine` loads them after the embedded
  faces, the way it loads the embedded faces, since upstream's cross-document database
  cache is not ported yet (open item `343fdc7` (cache only) above).
- C# has no `render` feature, so the flag is always available rather than render-only.
- The library exposes it too (`BrowserConfig.FontDirectories`), where upstream only has the
  CLI flag. A missing or non-directory path fails with upstream's CLI message,
  `Font directory does not exist or is not a directory: <path>`, and a second, different
  configuration after the first render is refused rather than silently ignored.

This relaxes "the engine never uses host fonts" only on request: with nothing configured
no file is read and output is byte-identical to before. Faces still load with
`SKTypeface.FromData`, never `FromFamilyName`, and no fontconfig is involved.

### A linked stylesheet leaves no element in the DOM

`crates/obscura-browser` materializes a fetched `<link rel=stylesheet>` as a synthetic
`<style data-obscura-linked>` inserted after the link, and the cascade reads it from there. That
element is observable: `document.querySelectorAll("style")` counts it, and it shifts the sibling
index of everything after the link.

DEVIATION from crates/obscura-browser. The fetched bytes are held beside the `<link>` itself and
the cascade reads them from there, so no element is created. Measured in Chromium 141 on a page
with one linked sheet: `styleEls=0 linkEls=1 headKids=2`, and the rule applies
(`color: rgb(1, 2, 3)`). This port now reports the same four values.

The sheet also has to enter the cascade at the **link's own document position**, which the
synthetic element got right by construction and a side table does not get for free. Measured both
ways in Chromium 141: with the link before an inline `<style>` the later inline rule wins
(`rgb(99, 99, 99)`); with the inline style first the link wins (`rgb(11, 11, 11)`); both report
`document.styleSheets.length` 2. `ALinkedSheetCascadesAtItsOwnPositionAndAddsNoElement` pins both
directions.

The Rust-derived test that asserted the old shape (that the CSS is readable from a
`style[data-obscura-linked]` which is the link's `nextSibling`) is re-pointed at the Chromium
behaviour rather than weakened: it still asserts import order, both rebased `url()`s and the
link-owned CSSOM sheet, and now also asserts that no `<style>` element exists at all.

### A `br` occupies a line box and reports a client rect

A `br` produced no inline item, so a block containing only `br`s collapsed to zero content height,
a `br` reported an all-zero `getBoundingClientRect()`, and a trailing `br` added an empty line that
Chromium does not add.

DEVIATION from crates/obscura-render, which has the same defect. Measured against Chromium 141 on
`render-repros/forced-line-breaks.html`, scored as per-element distance to Chromium over the 27
elements both engines report: **4628.74 before, 491.60 after, with no element moving farther**. On
`render-repros/pdf-print-media.html`, 2394.39 to 0.78 - the remainder there is sub-pixel line-height
rounding (38.39 against 38.0). A `br` now reports the line's own height, e.g. `0,1,0,17` against
Chromium's `0,1,0,17`, where it was `0,0,0,0`.

Two residuals on that fixture are **not** this change and are tracked separately: an inline inside
a block that also has block children reports an all-zero rect (487 of the 491 remaining distance),
and a `br` under `line-height: 10px` reports height 20 where Chromium gives 17.

`dotnet/tests/PocketCalculator.Render.Tests/ForcedBreakLineBoxTests.cs` pins the behaviour.

### `ch` and `ex` are measured on the element's own face, not scaled from the font size

CSS Values 4 defines `1ch` as the advance of U+0030 and `1ex` as the x-height, both in the
element's *first available font*. `crates/obscura-render/src/style.rs` has no notion of a face when
it resolves a length: it scales the font size by one fixed fraction per unit, and it has no `ch`
unit at all. This port inherited that and added `ch` as a second constant, `1139/2048` - Liberation
Sans' digit advance - so every page got Liberation Sans' `ch` whatever it asked for.

DEVIATION from crates/obscura-render/src/style.rs. `PocketCalculator.Render.FontUnits` carries the pixel
size of `em`, `ch` and `ex` together, and `FontUnitResolver` measures the latter two on the face
`FontResolution.ResolveLoadedFont` selects - the same decision the text engine shapes with. The `0`
advance is read through HarfBuzz at the face's design em; the x-height is `OS/2.sxHeight` via
`FontAssets.XHeight`. One resolver per layout pass, memoized on the font decision rather than on the
element.

**The variable-weight axis is the part that is easy to get wrong.** `ch` is not a per-family
constant: Archivo's digit is 0.573em at `wght` 400 and 0.625em at 800, so an implementation that
reads the base face is wrong on exactly the pages that use a variable webfont. The tuple is built
the way `TextShaper.ShapingVariations` builds it - automatic `wght` and `opsz`, synthesized italic,
authored `font-variation-settings` last. That duplication is deliberate (measuring a CSS unit must
not have to build the inline layer's `TextAttrs`) and
`FontRelativeUnitTests.TheMeasuringAxisTupleMatchesTheShapingAxisTuple` is what keeps the two from
drifting.

Measured on Chromium 141 over HTTP - a webfont does not load over `file://`, where every face
silently falls back to Liberation Sans and all of them measure identically. `width: 1ch` / `1ex` at
`font-size: 1024px` (chromium / obscura, obscura rounds a used width to whole pixels):

| | `ch` | `ex` |
|---|---|---|
| Liberation Sans | 569.5 / 570 | 541 / 541 |
| Liberation Mono | 614.5 / 615 | 541 / 541 |
| Liberation Serif | 512 / 512 | 470 / 470 |
| Archivo wght 400 | 586.75 / 587 | 538.609 / 539 |
| Archivo wght 800 | 639.984 / 640 | 538.609 / 539 |

End to end, `h2` in the closing section of a 67-page mirrored marketing site carries
`max-width: 18ch` at `font-size: 69.12px` and `font-weight: 800` in Archivo: 777.481px in Chromium,
691.942px before, 777.6px now, and the section it is in comes back from 642px tall to 578px against
Chromium's 578.547px. Scored as per-element distance to Chromium across all 67 pages, 5863 elements
moved closer and 0 moved farther. The 64-fixture repo corpus is byte-identical before and after,
because one fixture uses `ex` and none uses `ch` - that corpus cannot detect this bug at all, which
is why the site sweep is the only corpus evidence.

**`transform: translate()` was worse than the unit bug.** It is resolved at paint time and used a
hard-coded 16px em for *every* font-relative unit, so `translateX(2em)` at `font-size: 100px` was
32px where Chromium gives 200px - a 6.25x error independent of `ch`. `LayoutStyle.FontChPx` /
`FontExPx` carry the measured sizes forward for it (`em` is not stored a second time - it is
`FontSize`, and `FontUnits.ForStyle` reassembles all three, so the three cannot drift). Costs 8
bytes on `LayoutStyle`: 1256.04 -> 1264.00 bytes retained per instance, 0.64%, and 804.8 -> 805.0 MB
live on a 20,004-element page.

**`ch` was missing from the unit whitelists that already list `ex`**, in six independent places,
which is how `ch` behaves worse than `ex` rather than merely differently - a declaration in an
unlisted unit is either dropped or frozen at parse time against the initial 16px. Fixed in
`ComputedStyle.GapUnits` (`gap: 2ch` at `font-size: 40px` in Liberation Mono was 18px against
Chromium's 48px) and `ComputedStyle.RelativeLineHeightUnits` (`line-height: 3ch` reverted to
`normal`). **When you add a unit to this engine, check all of them.** Still open, each with its own
reason:

- `StyleBorder.StrictBorderUnits`. A border width is never re-read once the font is known, so
  listing `ch` turns a dropped declaration (0px) into a confidently wrong one (17.797px against
  Chromium's 48px). Tried, measured, reverted. `ex` has the identical defect there today.
- `StyleGrid.GridTrackUnits` and `StyleGrid.AllowedWords` (the `calc()` word filter). Blocked behind
  a separate bug: a grid track resolves *every* font-relative unit against a hard-coded 16px, so
  `grid-template-columns: 2em` at `font-size: 40px` is 32px against Chromium's 80px. Adding `ch`
  there would move a track from one wrong number to a different wrong number.
- `StyleBackground.GradientPositionUnits`. Unmeasured.

`StylePrimitives.FontSizeUnits` and `LineHeightUnits` are deliberately untouched on a positive
result rather than an absence of evidence: `font-size: 4ch` inside a Liberation Mono 40px parent is
96.0156px in both engines.

Two readers keep Liberation Sans' fractions on purpose, as an unreached net rather than a resolution
path: `DomStyleFixups.DefiniteInlineSize` and `TaffyStyleMapping.ToDimension` both read a
`Dimension` the top-down pass has already turned into `Px`, and a value arriving there unresolved
has no face to be measured on either.

One ordering subtlety for the next reader: the face has to be picked *before* `font-family` is
inherited. `ResolveOneComputedStyle` resolves every length about 200 lines above the block that
inherits `font-family` / `font-weight` / `font-style`, so the resolver reads those three the way
that block will (`style.X ?? inh.X`) rather than the pass being reordered. `font-size: 2ch`
correctly uses the *parent's* face.

`dotnet/tests/PocketCalculator.Render.Tests/FontRelativeUnitTests.cs` pins the behaviour (11 facts, 8 of
which fail at the parent commit; of the other three one does not compile there because
`FontUnitResolver` does not exist, one is vacuous while every unit is a single constant, and one is
the em/rem/viewport regression fence that correctly passes both sides).

### A retained restyle that changes nothing layout can see keeps its layout

`crates/obscura-render` re-lays the whole document on every retained restyle, and at the Rust
engine's speed that is affordable. The port is slower per pass - 77ms for a 2059-node page,
130-460ms for the Tesserae SPA - so a forced geometry read after *any* style write cost a
whole-document prepare, which is finding F39.

DEVIATION from crates/obscura-render/src/dom.rs, which has no equivalent.
`PocketCalculator.Render.RetainedLayoutReuse` is a gate in front of the layout half of a retained
restyle. At the end of the top-down pass - the last point at which this pass's style objects
are comparable to the ones the previous layout was produced from - it compares every element
the cascade recomputed against the style object it replaced. If nothing differs, or the only
differences are in members no pass after that point reads, `LayoutDomOnce` returns the previous
`DomLayout` itself and `PrepareInternal` reuses its derived geometry too.

It fails closed in three independent ways, because serving stale geometry is worse than serving
it slowly: only the eight members named in `PaintOnlyMembers` may differ and every other member
of `LayoutStyle` - including one added later, which is in no list - forces a layout; equality is
proven structurally and anything the comparer cannot compare counts as changed; and the gate is
only offered a layout when every mutation in the batch is an attribute mutation, the sheet has
no container queries and the dirty set is at most 512 elements. A tree, text, resource or
animation mutation never reaches it.

`OBSCURA_DISABLE_RETAINED_LAYOUT_REUSE=1` turns it off, which is how the A/B below was measured
on one binary. `dotnet/tests/PocketCalculator.Render.Tests/RetainedLayoutReuseTests.cs` pins the
behaviour (10 facts), each against a full from-scratch layout of the same mutated tree.

Measured on `thrash.html` (2059 nodes), 50 (write, read) pairs, three interleaved runs each,
medians: `opacity` 5261ms -> 1211ms, `class` 4947ms -> 524ms; `transform` and `left`/`top`
unchanged at ~5.2s, correctly, because those change layout.

All 101 Tesserae routes were captured twice from one pinned binary, gate off and gate on. 98 are
byte-identical in geometry and computed style; the three that are not (`Searchable List`,
`Masonry`, `Dropdown`) were each captured three more times per arm, and every one of them varies
*within* an arm by at least as much as it varies between them - `Searchable List` is a virtual
list whose rendered row count swings between 1789 and 11910 run to run, `Masonry` is the route
whose layout pass the watchdog terminates at a different item each time, `Dropdown` differs by
one node. Against the Chromium reference capture, 99 of 101 routes have a bit-identical geometry
divergence in the two arms and every style column is identical, and the two that move are those
same nondeterministic routes.

### An async op's page-state tail runs under a gate, where the reference has one thread

`op_load_image_metadata` is a deno_core async op in Rust: its reaction resumes on
the thread that owns `Rc<RefCell<State>>`, so however many images a page starts,
their completions touch the DOM, the in-flight table and the renderer cache one at
a time. The port awaits the transport with `ConfigureAwait(false)` and resumes on
the thread pool, so five `<img>` elements meant five threads writing plain
collections, and entries were lost - a seeded image read back as unknown and the
shim reported a load error for bytes it had successfully fetched.

The port therefore has something Rust has no need for: `PocketCalculatorState.AsyncResourceGate`,
taken around the whole post-fetch tail of `RenderOps.LoadImageMetadataAsync`, and a
lock inside `RenderResourceCache` over the retained byte cache (the renderer reads it
from the pump thread while a page-transport seed writes it from a pool thread, so the
gate alone cannot cover it). The leader also releases its waiters from a `finally`,
because a leader that throws after registering used to strand every follower on a
promise nothing could settle.

Both locks are uncontended on the synchronous render path, and they do not change
what any op returns. They are the port's replacement for the reference's
single-threaded reaction, not a behavioural difference - and the reason the C# side
needs the comment is that a reader diffing `ops.rs` will find nothing resembling
them there. Measured: 6 of 12 runs of
`ParserImagesLoadConcurrentlyWithoutBlockingTheEventLoop` before, 13 of 14 and
then 12 of 12 after.

### An idle verdict during an explicit settle is confirmed against the page

`PocketCalculatorJsRuntime.RunEventLoopUntilQuiescentAsync` no longer stops the moment `PumpTick`
reports `LoopTick.Idle`: while `__obscura_hasPendingDynamicScripts()` is still true the tick is
demoted to `Waiting` and the loop parks and re-pumps. `budget` still bounds it, so it cannot
hang.

DEVIATION from `crates/obscura-js`, which cannot reach this state. deno_core resolves an async
op's promise inside `poll_event_loop`, so `has_pending_ops` stays true until the page's
continuation has been delivered. In the port an op is a `Task` whose promise ClearScript
resolves from its continuation, and the only host-side evidence of the request -
`PocketCalculatorState.PageInFlight` - is dropped in `FetchOps.FetchUrlAsync`'s `finally`, which runs
*before* that `Task` completes. A settle landing in that window saw no timers, no posted tasks
and nothing in flight, called itself idle, and returned with most of its budget unspent while a
dynamically inserted external script was still waiting for its body.

It only showed under load, which is why it read as a flaky test rather than a defect:
`PocketCalculator.Cdp.Tests.DynamicScriptOnloadFires.DynamicExternalScriptsExecuteAndFireLoad` failed 1
of 12 full-suite runs, 2 of 7 with `-maxThreads 16`, and 0 of 10 and 0 of 5 after. Widening the
window artificially made it deterministic before the fix and harmless after it, out to a 200ms
gap.

That gap is now closed generally, one layer down. `PocketCalculator.Js.Ops.AsyncOpBinding` binds every
`Task`-returning op through a JavaScript shim that increments a counter where the op is called
and decrements it from a reaction on the op's *own* promise. A promise reaction can only run
inside a microtask checkpoint, and the loop evaluates its idle verdict after the checkpoint it
performs rather than during one, so the count outlives the promise resolution for exactly as
long as deno_core's `has_pending_ops` does. The shim's reaction is registered before the page
gets the promise, so the page's continuation runs later in the same drain and anything it
schedules is registered before the loop asks whether it is idle. `PocketCalculatorJsRuntime` implements
`IAsyncOpTracker` over `_pendingAsyncOps`, which `PumpTick` already read.

`TrackAsyncOp` / `AsyncOpScope` are gone. **An earlier version of this entry called them dead
code with no callers; that was wrong.** `RealmSleepAsync` used them, and answered this same
defect for frame timers by holding the op open for a fixed 5ms past the delay. The frame timer
is bound through `AsyncOpBinding` like everything else now, so that timeout is gone with it.

The `HasPendingDynamicScripts()` confirmation stays, for the work that is not an op at all: a
dynamic script between its body arriving and its evaluation, and a module the loader is still
resolving.

Regression: `PocketCalculator.Js.Tests.RuntimeTests.SettleWaitsForAnOutstandingAsyncOpContinuation`. It
uses `op_sleep`, the plainest async op there is - no host timer, no posted task, nothing in
flight - so the op itself is the only evidence the page is owed a continuation, and it needs no
artificial widening. Verified by reverting rather than by reasoning: 5 of 5 runs fail with the
tracker disabled, 5 of 5 pass with it. A `fetch()`-shaped test is deliberately not written,
because that window is between `PageInFlight.Decrement()` and ClearScript resolving the promise
and reaching it would need a widening hook in production code; `fetch()` and XHR are covered by
the same binding.

### Collapsing table borders are resolved per edge and split between the two boxes

`crates/obscura-render` has no collapsing model: it gives the table its whole border and each
cell its own. `DomTableCollapsedBorders` resolves one border per edge segment as the max over
cell, adjacent cell, row, row group, column, column group and table (CSS 2.1 17.6.2), and hands
each of the two boxes that meet there half of it. Only the **width** half of the conflict
resolution is modelled: `hidden` suppression and the cell > row > row group > column > column
group > table order between equal widths decide which border is *painted*, not how wide the edge
is, so neither moves a box.

Chromium 141, 600px block: `border: 5px; border-collapse: collapse` runs its cells from 2.5 to
597.5, and a 5px table holding 9px cells is 609 wide at `box-sizing: content-box; width: 600px`.
Six independent fixtures, each of which the width-max model predicts exactly, are pinned.

`CollapsedBorder` sits beside `Border` rather than replacing it, because a `LayoutStyle` survives
a pass whose node's cascade did not change and a pass rewriting `Border` in place would halve it
again. `UsedBorder` (`CollapsedBorder ?? Border`) is what layout, inline and paint read;
`getComputedStyle` deliberately still reports the **specified** width, which is what Chromium
does, while `clientWidth`/`clientHeight` use the halves.

Three things had to move with it, each a Chromium-verified fix in its own right:

- **A cell's own border contributed no row height at all**, in both border models and
  independently of collapsing. Taffy reports `content_size` two ways - a leaf adds its padding
  and neither border, a container measures from the border-box origin - and the table pass took
  it as a border-box height outright. `border-collapse: separate; td { border: 9px }` around one
  18px line was 18 tall against Chromium's 36.
- **Intrinsic table widths were read from taffy's rounded layout.** With a half-pixel on a cell
  edge the rounded table total and the rounded per-cell totals disagree by up to a pixel, which
  read as the columns not fitting their own max-content and wrapped every cell.
- **`UsedBorder` had to reach the inline layer and paint**, or a cell's text sat at the table's
  content edge and `vertical-align: middle` centred against a box that still subtracted the
  specified border.

### An auto-width table widens for a percentage column, and stretches to a grid area

`AutoTablePercentageIntrinsicFloor` was fed min-content. CSS 2.1 17.5.2.2 and Chromium use
**max-content**: the table grows until each percentage column's share covers that column's
max-content. Chromium 141 makes an auto table holding a `width: 30%` cell 161.2 wide (48.36 /
112.84); it used to stay at its 147.5 max-content and take 30% of that. The existing min-content
floor is untouched, because that is what makes a `width: 10%` table stop at 81.8. A definite
width is not widened this way, which is Chromium's rule too: a `width: 120px` table with a 30%
cell stays 120.

Separately, `DomStyleFixups.StretchesInlineToItsItemArea`: an `auto` table stretches to the item
area a grid parent, or a column-flex parent, gives it, clamped by its own min/max-width. Chromium
141 gives 600 in a 600px `display: grid` block and 200 in a 200px track, while `justify-self:
start`, an auto inline margin, a float and a flex row all keep the shrink-to-fit.

### `<caption>` is laid out

`crates/obscura-render` builds no box for a caption at all - no rect, no height. It is now a
full-width row of the table grid, above or below the rows per `caption-side` (which had to be
parsed; it was not), with margins of exactly the inset between the table's border box and its
first row so it sits outside the border, padding and leading border-spacing.

A caption spans every column but **sizes none of them**: Chromium wraps a caption three times the
table's width rather than widening the table, and floors the table only by the caption's own
min-content. The captions are therefore positioned absolutely for the two intrinsic measurements.
Without that a long-caption table was 196 wide against Chromium's 67.31; it is 68.

### The CSSOM snapshot reports a table box's computed CSS `display`, and carries border-spacing

`crates/obscura-render/src/paint.rs` serializes the **taffy** display, so a `<table>` reported
`block`, `display:inline-table` reported `inline-block` and `display:table-cell` reported
`block`. Chromium 141 reports `table` / `inline-table` / `table-cell`, and `table-row` /
`table-row-group` / `table-header-group` / `table-footer-group` / `table-caption` /
`table-column` / `table-column-group` for the UA displays of `<tr>`, `<tbody>`, `<thead>`,
`<tfoot>`, `<caption>`, `<col>` and `<colgroup>` - every one of which answered `block`.

`PreparedRender.TableDisplay` reconstructs them from `IsTableBox` / `IsTableCellBox` /
`IsInlineBlock` and the element's tag, and re-derives CSS Display blockification: a flex item,
grid item, float, absolutely positioned box and the root report `block`, while `inline-table`
reports `table`. That `ApplyDisplay` clears the flags for any valid authored display is what
makes a surviving flag mean the UA value, so `<tr style="display:flex">` still reports `flex`.

Both cases are now recorded. `ApplyDisplay` sets `LayoutStyle.DisplayAuthored` for every valid
authored value, which is what distinguishes an authored `display: block` on a `<caption>` /
`<col>` / `<colgroup>` from those elements' UA value (they have no UA arm, only the plain
`display: block` every element starts from). And it sets `LayoutStyle.AuthoredTableDisplay` for
the seven values it does **not** lay out - `table-row`, `table-row-group`, `table-header-group`,
`table-footer-group`, `table-column`, `table-column-group`, `table-caption` - recording the
keyword and returning without touching a single layout-visible field.

**Reported before it was laid out, deliberately.** CSSOM asks for the resolved value, not the
used one, and Chromium 141 reports the keyword whatever layout achieves, so the snapshot's answer
was right on its own terms while layout still ignored it. Layout now honours it - see "An
authored internal table `display` is laid out, except where an anonymous cell is needed" - and
the snapshot is unchanged by that. 140 cells were measured: each of the seven on a
`<div>`, `<table>`, `<tr>` and `<td>`, in a block parent, as a flex item, a grid item, a float
and an absolutely positioned box. The subject's UA display makes no difference to the answer -
`<table style="display:table-row">` reports `table-row`, verified here - which is why the record
is read *before* `IsTableBox`; all seven blockify to `block` in the four blockifying contexts,
which reuses `IsBlockifiedBox`.

**`border-spacing` and `border-collapse` now have keys**, where before page script read the empty
string through bootstrap's inline fallback. Both inherit, which the measurements settle: Chromium
141 reports `2px` / `separate` on a `<table>` and on every descendant of one - a `<div>` in a cell
included, and still when the table carries `display:flex` - and `0px` / `separate` elsewhere.

`DomStyleFixups.PropagateBorderSpacing` turned out **not** to be an inheritance pass: it converts
a table's spacing into taffy row/column gaps and stores no CSS value anywhere, so
`PreparedRender.InheritedBorderSpacing` walks to the nearest ancestor that declared one.
`border-collapse` is genuinely inherited onto every node already and reads straight off the style.

The reported value is truncated to whole pixels, which is what Chromium stores - verified
directly: `1.5px` and `2.5px` both report `1px` and `2px`, so it truncates rather than rounds,
and `3.75px 7.25px` is `3px 7px`. It serializes as one value when the axes match. Layout keeps
the fractional value it already used, so no geometry moves.

Four parser gaps surfaced by adding the key, none fixed here because all four move geometry:
`border-spacing` is parsed through the `PxValue` overload that hard-codes `em`/`rem` at 16px, so
`font-size: 20px; border-spacing: 1em` is 16px here against Chromium's 20px in either
declaration order; the parser accepts `10%`, a three-value form and a negative, which Chromium
all reject; `DomCascade` accepts `cellspacing` only when every character is a digit, so
`cellspacing="3px"` falls back to the UA 2px where Chromium reports 3px; and the
`-webkit-border-horizontal-spacing` / `-webkit-border-vertical-spacing` aliases Chromium reports
have no key.

### `border-spacing` is parsed as CSS defines it, and stored as whole pixels

`crates/obscura-render/src/style.rs` keeps whatever lengths `px_value` can make of the
whitespace-separated tokens and resolves `em` against a flat 16. `ComputedStyle.ApplyBorderSpacing`
instead:

- **drops an invalid declaration** the way Chromium does, rather than salvaging part of it. A
  percentage, a third length, a negative, an `auto` or a unitless non-zero leaves the inherited or
  initial value standing - `0px` in a plain div, the UA `2px` on a table. The port used to lay out
  `10%` as `1px` and `1px 2px 3px` as `1px 2px`.
- **answers the CSS-wide keywords**: `initial` is 0, `inherit`/`unset` resolve in the top-down pass
  because nothing carries an inherited `border-spacing` down the style tree, and `revert` leaves the
  previous winner (the UA 2px on a table, which is what Chromium reports).
- **re-reads a font-relative value** from `ResolveFontRelativeDeclarations`, so
  `font-size: 20px; border-spacing: 1em` is `20px` in either declaration order. It was 16px, because
  the value went through the `PxValue(string)` overload that hard-codes `em`/`rem` at 16. That
  overload has ten other callers, so the fix is at the call site: `border-spacing` now takes the
  context-carrying overload, as `filter` and `box-shadow` already did.
- **stores whole pixels**, which is what Chromium does in layout and not only in the snapshot. The
  rule is `(int)(v + 0.01)`, Blink's `RoundForImpreciseConversion`, verified here: `1.989px` is 1
  and `1.99px` is 2, so it is neither truncation nor ordinary rounding; `4.5px` is 4 and `4.999px`
  is 5. The store is 16 bits and **overflows to zero rather than saturating** - `32766px` is
  `32766px` and `32767px` is `0px`, also verified.

That last one was not on the list and is load-bearing: admitting fractional `cellspacing` (below)
would otherwise have moved `cellspacing="2.5"` from a lucky-correct 46 to a wrong 48.

### `cellspacing` is mapped with HTML's rules for parsing dimension values

`dom.rs` maps the attribute only when every character is a digit.
`DomCascade.HtmlDimensionValue` skips leading ASCII whitespace, reads digits with an optional
fraction and ignores the rest, rejecting a percentage. Measured on Chromium 141: `"3px"` is 3,
`"3.9em"` is 3, `"12abc"` is 12, `"\t5"` is 5, `"0007"` is 7, `"8 9"` is 8, `"1/2"` is 1, while
`"3%"`, `"-3"`, `"+7"`, `".5"`, `"abc"` and `""` are all rejected and leave the UA 2px.

### Three more CSSOM snapshot keys

`-webkit-border-horizontal-spacing` and `-webkit-border-vertical-spacing`, which Chromium reports
on every element with the inherited value, and `display: flow-root`, which was reported as `block`
on every element including a plain `<div>`. Of the five setters of `FlowRoot`, `display: table` /
`inline-table` and the anonymous-table style all set `IsTableBox` so `TableDisplay` answers them
first, and the `-webkit-line-clamp` adjustment has its own earlier arm - so `(Block, false)` plus
`FlowRoot` at that point is unambiguous and needs no `IsTableBox` guard.

Also not parsed at all, and left: the two-value `display: inline flow-root`, which Chromium
reports as `inline-block`.

### An authored internal table `display` is laid out, anonymous cells and all

`crates/obscura-render/src/style.rs` rejects `table-row`, `table-row-group`,
`table-header-group`, `table-footer-group`, `table-column`, `table-column-group` and
`table-caption` outright and builds a table for none of them, so such a box is whatever display
it kept - in practice a full-width block. C# records the keyword
(`LayoutStyle.AuthoredTableDisplay`) and now lays the box out, matching the boxes CSS 2.1 17.2.1
asks for and Chromium 141 generates.

All measured on Chromium 141 against a 600px block in `16px/18px "Liberation Mono"` with
`border-spacing: 0`. **Naming the face is load-bearing**: Chromium's unqualified `monospace` is
DejaVu Sans Mono where the engine picks its embedded Liberation Mono, which is a width difference
of its own and has nothing to do with F32. Read off the two `head`/`hmtx` tables, DejaVu Sans
Mono is 1233/2048 and Liberation Mono 1229/2048, so `aa` at 16px is 19.27 unnamed and 19.20 with
the face named, and `Hello world` is 105.97 against 105.63. An earlier revision of this paragraph
had the two advances the wrong way round; figures of 19.27 / 38.53 / 105.97 anywhere in these
notes are the unnamed DejaVu ones. What is left once the face is named is taffy's whole-pixel box
rounding against Chromium's LayoutUnit sixty-fourths - about a pixel per independently-rounded
box.

- **Rows, row groups and captions come from the computed display**
  (`DomTableSupport.CollectCssTableStructure`), for a `display: table` box that is not a
  `<table>` and for an anonymous table box. Nested groups, header groups hoisted and footer
  groups pushed whatever the source order, anonymous rows around runs of loose cells, a row
  group holding no row, `caption-side: bottom`.
- **A row or group's own box is a band.** The grid's children are the cells, so a row is not a
  taffy box; one empty, zero-contribution grid item per row and group, spanning every column of
  the rows it covers, is what gives the element a rect. Chromium reports exactly that band - a
  row whose only content is one anonymous cell in the first of two columns is the table's full
  192.03, not the cell's 153.63 - so this is more accurate than reconstructing a union, and it
  keeps `SynthesizeRowRects` untouched. A column and a column group get the same across the
  other axis.
- **A row that generated no cell of its own is its own anonymous cell.** An anonymous box takes
  only inherited properties and `margin`/`padding`/`border` do not apply to a row, so the two
  are indistinguishable: a `display: table-row; margin: 10px; padding: 10px; border: 3px` div
  lays out at 105.63 x 18 at the parent's origin, exactly as it does with none of the three.
  `width` sizes nothing (105.63, not the declared 300) and `height` is a row minimum.
- **A column generates no box.** Outside a table, 0 x 0 with none of its content rendered;
  inside one, its `width` sizes its track. `span` is an HTML attribute on `<col>` and Chromium
  ignores it elsewhere, so a CSS column is always one track - verified here: three cells under
  two `display: table-column` divs in a 600px table are 200 / 200 / 200 whatever `span` says.
- **Blockification is honoured** for the five keywords: a floated, absolutely positioned or
  fixed box and a flex or grid item is `block` and generates no anonymous table. `IsTableCellBox`
  is exempt, because it is also how this engine marks a `<td>` and Bootstrap's
  `display: table-cell; float: left` input group depends on a floated cell staying in the table
  (`NowrapTableCellKeepsBootstrapControlsOnOneRow`).
- **Each maximal run of table-internal siblings generates its own anonymous table**
  (`DomBuild.AnonymousTableRun`), which is what makes a standalone row work among ordinary
  siblings - before, one table over all of an element's children was the only shape available
  and a mixed child list produced none at all. Verified: a block between two `display: table-row`
  divs produces two separate anonymous tables, not one.
- **A run of children that are not proper table children is one anonymous cell**
  (`DomBuild.BuildAnonymousCell`). Rust generates none, so `BuildTable` answered `null` for any
  such table and the fallback was ordinary block layout, which is where the shrink-to-fit went
  too: `<div style="display:table">aa</div>` was 600 against Chromium's 19.20. The cell is a box
  with no DOM node, so it gets no `IdMap` entry, contributes no border to the collapsing model,
  sizes no column, takes no span and is no row minimum. Its inline runs fold through
  `TextEngine.TryBuildRun` under the *owner's* `Ifc.Runs` key, because the anonymous box has no
  key of its own, and its inner display is the one an authored cell takes in `Build` for the same
  content - the column-flex stand-in for block-level children only, block layout otherwise. That
  split is not cosmetic and it is the opposite of what the gap was first sized as: a cell
  establishes a block formatting context and taffy's block layout collapses a child's trailing
  margin out through it, which made a table over one `margin: 10px` block 28 tall against
  Chromium's 38. Verified against Chromium: a table over plain text, one block, one inline, two
  consecutive blocks (one cell, 19.20 x 36, not two columns), a cell beside a block in either
  order (38.41 in one row), a row mixing the two, and an anonymous cell taking the column width
  the row above negotiated.
- **Only a genuine table box wraps its loose children that way.** An anonymous table box
  generated inside an element that is not a table covers just the run of table-internal children
  (`AnonymousTableRun`); everything else stays in the element's own formatting context. Chromium
  141 leaves the `xx` of a `display: table-cell` div holding `xx` plus two cells as a line of its
  own above a 38.41 x 18 anonymous table, not as a third cell beside them, so
  `CollectCssTableStructure` takes a `genuineTableBox` flag and still answers `null` at the top
  level without it. Inside a *row* the wrapping always applies, genuine table or not.
- **A `table-cell` parent generates the anonymous table its table-internal children need.**
  `WantsAnonymousTableBox` used to answer false for `IsTableCellBox` before it looked at the
  children, so two `display: table-cell` divs inside a `display: table-cell` div stacked (20 wide,
  36 tall) where Chromium puts them in one 38.41 x 18 anonymous row, and the same two inside a
  `<td>` did too. The guard is gone rather than narrowed: a cell is an ordinary non-table box as
  far as 17.2.1 is concerned, and the cells this engine generates for its own internal flex
  containers are never what the function is asked about - `BuildTable` builds them from the
  table's structure and reaches their elements through `Build`, which asks about the element's
  own children. The `<td>` case is the proof that an authored cell need not be told from a
  generated one here: Chromium puts two `display: table-cell` divs inside a real `<td>` side by
  side at x=1 and x=20.2, verified directly. `NowrapTableCellKeepsBootstrapControlsOnOneRow`, a
  cell parent with row children, and a cell parent with plain text are unmoved.
- **The `<table>` path stays keyed on element names** - `CollectTableRows`, `SynthesizeRowRects`,
  the `<col>` pre-pass, the `<caption>` scan and `PropagateBorderSpacing` - deliberately, so real
  table layout does not move. It did not: byte-identical across every fixture measured, and a
  real `<table>` and a `display: inline-table` div were re-checked against a build of the parent
  commit after the change landed.

Still not modelled, each falling back to ordinary boxes so nothing is lost:

- **A caption with no rows**: `placed.Count == 0` answers `null`, so a table holding only a
  caption is not built - 600 x 18 against Chromium's 48.02 x 36, where the zero-column table
  drops the caption to its min-content width and it wraps. Same arm: a `display: table` box whose
  whole content is collapsible whitespace is a 600-wide block where Chromium gives 0 x 0.
- **A block child's margins collapse out through a cell.** A cell establishes a block formatting
  context, so they must not: Chromium makes a cell holding one `margin: 10px` block 39.20 x 38
  with the block 19.20 at (10, 10), and this engine gives 40 x 28 with the block filling the
  cell. Pre-existing and not about anonymous cells - a `<td>`, a `display: table-cell` div and an
  anonymous cell all do it, because the engine's `width: 100%` fill for a cell's block children
  ignores their margins and taffy's block layout collapses the trailing one. The anonymous cell
  deliberately reproduces the authored cell's behaviour rather than inventing a third.
- **An anonymous cell's content is top-aligned, not baseline-aligned.** In a row made taller by a
  bordered sibling cell, Chromium puts the anonymous cell's text on the row's baseline (y=12 for
  a 3px-bordered neighbour) and the engine puts it at the top (y=9). The engine's cells are
  `align-items: flex-start` stand-ins throughout, so this is existing cell behaviour, not new. A
  float inside an anonymous cell likewise does not grow the row (18 against Chromium's 23).
- **`border-spacing` inherited from a grandparent** does not reach a CSS or anonymous table: the
  engine stores the value only where it was declared and `AnonymousTableStyle` clones the
  generating element's style. Pre-existing, and it affects `display: table` divs too; the fix
  belongs in `DomStyleFixups.TableSpacing`, which needs tree access it does not have.
- **`ResolveCollapsedBorders` gives a CSS row no band**, so a `border-collapse: collapse` CSS
  table collapses cell and table borders but not row or group ones.

`display: inherit` carries the record: the `DisplayInherit` copy in `LayoutDomComputed` takes
`AuthoredTableDisplay` with `Display`, through the pseudo-element path and the retained-style
seed as well. A `display: inherit` child of a `display: table-row` box reports `table-row`, and a
later `display: block` in the same block still replaces it. Now that layout reads the record,
such a child also lays out as a table part, which is Chromium's behaviour and was a geometry
change beyond what the CSSOM fix alone did.
### The UA `table` rule's flex construction does not survive an authored `display`

`crates/obscura-render/src/style.rs` gives `table` / `tbody` / `thead` / `tfoot` / `tr` / `td` /
`th` a `flex-direction: column`, an `align-items` and a `min-width: 0` that approximate table
layout with an internal flex container, and its display handling clears only the display pair -
so a `<table style="display:flex">` both laid out and reported as a *column* flex container.
`ComputedStyle.ApplyDisplay` now drops all three with the display pair unless the author set
them, and `PreparedRender` masks `align-items` on an internal flex container the way it already
masked `flex-direction`.

Chromium 141 reports `row` / `normal` / `auto` on such a table, and identically to a `<div>` of
the same display - it never uses flex to build a table, so those are initial values everywhere.
The genuine UA declarations survive every display: `box-sizing: border-box`, and
`border-spacing: 2px` / `border-collapse: separate`, the last two being **inherited** properties
a descendant reads (a `<div>` inside a `<table>` reports 2px, `body` reports 0px, and the
descendant still reports 2px when the table carries `display: flex`). `min-width` is the
invisible one: `auto` and `0` both serialize as `0px` outside a flex container, but `auto` is
what gives a flex item its automatic minimum size.

### A horizontal-only measurement is not reused to answer a vertical one

taffy's measure cache packs the requested axis into spare sign bits of the key and masks them out
again on lookup (`Cache::get`, `CacheKey::x_axis_parent_size`), so one stored measurement answers
a request for either axis. That is unsound, because `compute_block_layout`,
`compute_flexbox_layout` and `compute_grid_layout` all short-circuit a `ComputeSize` run with
`RequestedAxis::Horizontal` and a known width to `(width, 0.0)` **without laying the box out**.
The zero is a placeholder, not a measurement.

Symptom: `display: grid; grid-template-columns: 200px 1fr` with a single `<table>`, flex
container or nested grid as its only child came out height 0. The unoccupied `1fr` is what makes
the column pass do work at all - with `200px 200px` every track starts with
`base_size == growth_limit` and the pass early-returns - the item's width is known through grid
stretch alignment, so the column pass measures it horizontally and caches `(200, 0)`, and the row
pass then reads that 0 as its block-axis contribution. `align-items: start` splits the two:
the item measured 22 and the row still 0.

**The filed diagnosis for this was wrong**, and worth recording as such: it was reported as auto
row-track sizing for a lone item and as table-specific, because a second grid item makes it
disappear. It is neither. A 198-case matrix over 18 column/row/alignment variants and 11 child
display types had 57 failures, and the affected children are every box that takes the flex, grid
or table algorithm - `div`, `img`, `inline-block`, `inline-table` and `span` were all correct,
which is what made it look like a table bug.

DEVIATION from `vendor/taffy/src/tree/cache.rs`: the C# port transcribes it faithfully, so this
is taffy's bug ported correctly rather than a transcription slip. `Cache.Get` now keeps a
horizontal-only entry for horizontal-only requests (`CacheKey.AxisBits()`). Vertical and
both-axis runs never short-circuit, so their entries stay shareable in both directions and the
sharing taffy wanted is kept everywhere else. The fix is at the cache, so it closes the class
rather than the one case.

Measured: 59 fixtures diffed element by element between a worktree build at the parent commit and
the same build plus this patch, **0 differences**. Perf, 5 interleaved pairs on the three
heaviest fixtures (2693 / 2404 / 1206 elements): medians move ~1%, inside the noise floor. A
narrower guard keyed on `height == 0` was prototyped to preserve more cache sharing and bought
nothing measurable, so the simpler rule stands.

**Fixture-corpus note**: `probes-flex/r4b.html` is non-deterministic like its `r4`, `r5` and
`tmp-css` siblings - two runs of one binary disagree on it. Exclude all four when diffing, and
`test-html-files/renderlab-complex.html` with them: it was found to disagree with itself on ~200
lines of y coordinates across consecutive runs of the same binary, the only other fixture of 109
that does.

### `display: inline` over table-internal children generates an anonymous inline-table

`crates/obscura-render/src/dom.rs` generates no anonymous table box for any display; f50a855
added one for `block`, `inline-block`, `flex` and `grid`, and `display: inline` was the case it
could not reach. CSS 2.1 17.2.1 wants an anonymous **inline-table** there - an atomic inline
inside the element's own inline box.

The branch was already correct and simply never reached: two box-tree passes spliced the element
away before `Build` saw it. `IsFlattenableInline` flattened a `<table display:inline>` to its
`tbody` because it has no background, border or position, and `InlineWrapsOnlyInFlowBlocks`
flattened it because `tbody`/`tr` are internal flex containers and so count as in-flow
block-level. Both now decline when the element owns an anonymous table, and the rest comes free
from the path a non-flattenable inline box already takes: `ToTaffyStyle` maps `Display.Inline` to
a wrapping flex row, `IgnoresUsedBoxSizes` zeroes its width so it shrink-wraps and its own
`width` is correctly ignored, and `SynthesizeOrdinaryInlineFragments` produces the element's
fragment.

Chromium 141, `<table style="display:inline">` after the text "before": one 147.5-wide table on
the same line with 34.66 / 112.84 columns. The port gave it no box at all and laid its row out as
a 600-wide block on a line of its own. Twenty cases were measured, including text on either side,
two on one line, wrapping, the element's own border and padding, two rows, and a `<div>` with
`display: table-cell` children.

**A measurement warning worth keeping**: those 147.5 figures hold only with `border-spacing: 0`.
With the UA default 2px the same fixture is `table 0,6 153.5x17` and `tr 2,2 149.5x18` - verified
here. A fact written from the 147.5 numbers without zeroing the spacing measures something else.

Closed: an authored `display: table-row` or `table-row-group` child now generates its anonymous
table, and each maximal run of table-internal siblings generates its own. See "An authored
internal table `display` is laid out, except where an anonymous cell is needed" above for what
landed and for the cases that still fall back.

### Table fixup generates an anonymous table box

`crates/obscura-render/src/dom.rs` builds a table only for a computed table box, so
table-internal children under a non-table `display` laid out in the element's own formatting
context. `ApplyDisplay` clears `IsTableBox` for any authored display and `DomBuildCore` only
calls `BuildTable` when it is set.

DEVIATION: C# generates the anonymous table CSS 2.1 17.2.1 requires, *inside* the element's box.
The element keeps its declared width and the anonymous table is `width: auto` and shrink-to-fits.
It is registered in `IfcRegistry.AnonymousTables` rather than `BuildContext.IdMap`, because an
anonymous box has no DOM node and mapping it to the element would overwrite the element's rect.
Chromium 141 on a 600px `display: inline-block; width: 100%` table of `alpha` /
`beta gamma delta` gives 600 wide with 34.66 / 112.84 columns; before, 35 / 565. The same for
`block`, `flex`, `grid`, `inline-flex`, and for authored `display: table-cell` children under any
of them. `DomBuild.WantsAnonymousTableBox` / `AnonymousTableStyle`.

### A table cell's declared inline size is spent once, on its column

Two separate losses of the same value. `DeferCyclicFlexInlineSizes` neutralizes a cyclic
percentage to a definite `0px` before the box tree is built, so `DomBuildTable.StyleWidth` saw no
percentage and the column vanished (43 / 458 against Chromium's 180 / 420, and it reproduces with
a px-width table, so it is not the percentage path). `RestoreTypedPercentages` then put the
percentage back on the *cell's own* taffy node, undoing `BuildTable`'s deliberate
`Size.Width = Auto`, so the cell resolved 30% a second time against its own 180px track and came
out 54px - which is what the presentational `<td width="30%">` spelling was hitting. One root
cause, two symptoms, one fix: `BuildContext.DeferredInlineWidths` hands the authored percentage
back to the column pass, and `IfcRegistry.TableGridCells` keeps the restore passes off the cell
box.

### A collapsed border is half inside the table's content box, and a collapsing table has no padding

Chromium 141: `box-sizing: content-box; width: 600px; border: Npx; border-collapse: collapse` is
`600 + N` wide - 601 / 605 / 611 / 620 at N = 1 / 5 / 11 / 20 - because only the outer half of a
collapsed border is outside the declaration, and `padding` is ignored entirely (CSS 2.1 17.6.2).
In the *separate* model `border-spacing` lives inside the content box, so it is already part of a
content-box `width`: `border: 5px` with the UA's 2px spacing is 610, not 614. Rust adds the full
border, the full padding and the spacing in both models.
`DomStyleFixups.TableWidthDeclarationEdges` / `TableUsedPadding`. After the fix every table
border-box width in a 24-case matrix (border model x padding x border x width, plus colspan,
rowspan, `<colgroup>`, nested tables, `thead`/`tbody` and fixed layout) matches Chromium exactly;
before, 10 of them disagreed.

### LayoutStyle allocates its collections lazily and keeps its cold fields in a side object

`crates/obscura-render` stores both inline in its style struct, which is affordable for a struct
laid out by value. Here every `LayoutStyle` is a heap object, and on a 60k-element page it was
2,872 bytes retained per element.

Two changes, both measured before being taken. **23 per-side and per-slot collections**
(`MarginAuto`, the three margin, three padding and two inset companions, `SizeExpressions`,
`IndividualTranslateExpressions`, `ContainerNames`, `BorderCascadeOps`,
`BackgroundGradientLayers` and its radial geometries, the four grid track lists, the three
counter lists, `TransformOps`) are now allocated on first non-default write. Reads of the unset
state come from one shared all-default instance, so the read accessors are `ReadOnlySpan<T>` /
`IReadOnlyList<T>` - the span is what makes writing through the shared instance a compile error.
Measured across both synthetic pages and all 26 fixtures (70,973 styles), **every one of them was
empty on every element**: 58.6 MB of live heap holding nothing.

**23 rarely-set members** (~600 bytes: `BorderCascadeBase`, grid placement, the gradient, mask,
background-size and position tuples, `BoxShadow`, `Outline`, `AnimationTiming`, the individual
transform properties, `TransformOrigin`, `ReplacedIntrinsic`, `IntrinsicSize`,
`NativeControlContent`, `BorderSpacing`, `ObjectPosition`, `FontSizeRaw`, `LetterSpacingRaw`)
moved into a `LayoutStyleRare` allocated on first non-default write; fewer than one element in a
thousand sets any of them. Each is still reached through a property of the same name and type, so
no read site changed. `BorderModel` (116 B) was deliberately left inline: it is read whole on
paint paths and a property getter would copy it per read.

`RetainedLayoutReuse` reflects over `LayoutStyle`'s fields, so it now walks `LayoutStyleRare`'s
too with the same paint-only name table, substituting a shared all-default instance for an absent
store; restyle classification is bit-for-bit unchanged.

`LayoutStyle` 1,848 -> 1,240 bytes; retained per style 2,872 -> 1,240. On a 60k-element page live
heap 219.8 -> 126.4 MB (-42.5%) and allocation 750.2 -> 657.7 MB. Timing over 10 interleaved runs
per binary: medians -1.0% and -0.9%, mins within 0.1%.

**Still 56% of the live set on that page** (71 of 126 MB). About 100 of the remaining 8-byte
reference fields are also almost always null (SVG paint, mask, grid line names, the font,
letter-spacing and gap expressions); a second rare tier would take it under ~900 B. And
`LayoutDomComputed` assigns `style.FontVariationSettings = [.. inh.FontVariationSettings]` for
every element, allocating an empty list per style (1.83 MB per 60k), which leaving null when the
inherited list is empty would remove.

### One watchdog thread services every arm, instead of one thread per arm

`Watchdog.Spawn` used to `new Thread(...)` for every armed watchdog and `Stop()` used to
`Thread.Join()` it. `PocketCalculatorJsRuntime.RunEventLoopBoundedAsync` arms one **per event-loop tick**
and every CDP command arms one, so the cost was unbounded in the number of ticks. On a small box
running the suite at high parallelism it aborted the process outright:
`Fatal error. ResumeThread failed with error 6` out of `Thread.StartCore` <- `Watchdog.Spawn` <-
`ArmWatchdog` <- `RunEventLoopBoundedAsync`. That is the most likely explanation for a
12-minute no-progress hang seen in a full-suite run.

DEVIATION from `crates/obscura-js`, which arms a tokio timer against an `IsolateHandle` and needs
no thread of its own. `WatchdogScheduler` keeps one dedicated background thread that holds every
armed deadline and parks on `Monitor.Wait` between them.

**A `Timer` would also have removed the churn and is the wrong tool**: its callback runs on the
thread pool, and the situation this exists for - a page pinning threads inside V8 - is exactly
when a pool callback is late. A dedicated thread keeps the backstop independent of the pool,
which is what the Rust engine gets for free.

The `Thread.Join()` in `Stop()` was load-bearing: it guaranteed that once `Stop()` returned, the
watchdog could no longer interrupt an engine that had moved on to another task. `Cancel` keeps
that guarantee by waiting out a firing already in progress, and the scan runs in a non-inlined
frame for the reason `CdpWatchdogCore` documents - an `Entry` left in the parked frame's stack
slots would pin its `V8ScriptEngine`, and through it a whole document.

Measured: 2000 arm+stop pairs add **0** threads and take **7ms** in total, where before each one
started and joined an OS thread. A runaway `while (true) {}` is still interrupted at 256ms
against a 250ms budget, and `Stop()` still reports that it fired.
`PocketCalculator.Js.Tests.WatchdogSchedulerTests` pins all three.

### What the CDP pool move did and did not change

Three things were flagged when connections moved off dedicated threads. Revisited with the code
rather than left open:

**Page suspension stays.** `CdpContext.GetSessionPageMut` suspends any other page of the same
connection that holds a live JS runtime before resuming the target. Its comment used to justify
that with "V8 allows one entered isolate per OS thread", which is a rusty_v8 property and never
true here; the comment is corrected. The behaviour has an independent reason and keeps it: it
bounds a connection to one live isolate and the document behind it, and a document is hundreds of
megabytes on a large page. Relaxing it is a memory trade needing its own measurement, not a
comment fix.

**The two `[ThreadStatic]` caches are genuinely caches.** `SelectorParser._cache` is a bounded
256-entry selector cache, and `HtmlParsing._parser` / `_contextDocument` are an AngleSharp parser
and the document that owns fragment context elements. `CreateContextElement` calls
`document.CreateElement`, which does not insert, so the context document does not accumulate
nodes across parses and nothing about correctness depends on which thread runs. Being per-pool-
thread rather than per-connection-thread is therefore a hit-rate question only, and Cdp suite
wall time is unchanged either side of the move.

**The control plane was never exposed; the WebSocket upgrade is, slightly.** `/json/*` is served
synchronously on the accept thread (`HandleHttpJsonBlocking`), so it stays responsive however
busy the pool is - which is what `ControlPlaneUnblockedTests.HttpControlPlaneUnblockedDuringLongJs`
pins. What does go through the pool is a new connection's first slice, so a pool saturated by
synchronous V8 can delay its 101 until the runtime injects another thread. That is latency, not a
hang. `TaskCreationOptions.LongRunning` on that one `StartNew` is the remedy if it is ever
measured to matter; it was not taken, because the suite is too load-sensitive on this box to
measure the difference honestly - at the time of writing, HEAD itself failed 3 of 4 full runs
with three different tests while two other builds were running.

### Flake: the heavy-page fixture spawned an OS thread per connection

`ConcurrentConnectionsHeavyPageTests.ConcurrentConnectionsHeavyPageDoNotAbortV8` is the most
frequent flake in the suite and only misbehaves under load. Traced with a heap dump taken from
inside a live stall: every failure is the client's 30s wait for its `sessionId` expiring, because
`Target.createTarget` awaits `NavigateAsync` before emitting `targetCreated`, and the navigation
was parked in `HttpConnection.InitialFillAsync` waiting for a subresource head. Correlating both
sides showed the fixture had simply not run that connection's handler for over 12 seconds, with
both ends ESTABLISHED and both queues empty.

The fixture spawned `new Thread(...)` per connection and slept on it. Under `-maxThreads 16` on
four cores a fresh thread can wait many seconds to be scheduled, and the client's 30s budget and
the navigation's 30s `Page.NavigationTimeout` are equal, so a late subresource fails the test
rather than merely slowing it. That also explains why a thread-pool sampler read `busy=0 pend=0`
through the stall: the fixture's threads are not pool items, so nothing in the process had work.
It now pre-starts 12 serving threads over a `BlockingCollection`, reads the whole request head
(a single `Read` could answer from a truncated request line, making the later close abortive) and
sets socket timeouts. No assertion, timeout or tolerance was changed.

**This is a partial fix and the remainder has a different cause.** Measured over 20 amplified
full-suite runs each, alternating blocks of five: 6/20 failures before, 3/20 after - which at
that N is not significant (Fisher exact p is about 0.45). The residual is a client-side
`ReadAsync` that does not complete although the data is already in the receive buffer
(`avail=102`, peer in FIN_WAIT2), i.e. .NET's `SocketAsyncEngine` not delivering a readiness
completion under 4-16x CPU oversubscription. It reproduces with a stock `HttpClient` and a stock
handler in the same process, so it is not engine code and there is no fix here short of waiting
longer.

Ruled out by separate measured runs: the fixture alone (3600 requests through it were clean),
`PocketCalculator.Net`'s custom `ConnectCallback`, the aggressive gen2 GC from 81c6ea8, V8 isolate churn,
and the CDP accept loop.

### Shaped paragraphs are carried across render passes

Shaping is a pure function of the text, its attributes and the tab width, but the cache lived on
the per-pass `TextEngine`, so every text node was reshaped from scratch on every prepare - about
40% of a prepare on a text-heavy page. The retained-layout gate above cannot help here by
construction: it answers *whether* to lay out and declines a write that changes a box, and those
are exactly the passes that pay for shaping. A container-query prepare re-lays the document
several times inside one prepare and paid for it each time.

DEVIATION from `crates/obscura-render`, which builds its `TextEngine` per pass too and can
afford to reshape; HarfBuzz through P/Invoke cannot. `PocketCalculator.Render.ShapeCache` holds shaped
paragraphs and `TextEngine.AdoptShapeCache` takes over the previous pass's cache - but only when
both passes were built from the same web-font set, so a face arriving later discards it
wholesale. The key covers every input the shaper reads, `TextAttrs.FontId` included, which pins
the exact `@font-face` resource. Reuse is sound because a `ShapeLine` is never mutated after
`ShapeParagraph` returns it: `TextLayout` and `Bidi` only read, and nothing outside
`TextShaping.cs` assigns to a `ShapeWord`, `ShapeSpan` or `ShapeGlyph`.

Measured on a 1200-paragraph page (215 KB), 10 layout-affecting write+read pairs, medians of
three: `left`/`top` 5474ms -> 3049ms, `transform` 5121ms -> 2278ms.

**It is also a memory win, which was not the point of it.** Peak RSS on the same run falls from
951 MB to 550 MB, because without it every pass allocated a fresh set of `ShapeGlyph` arrays for
all 1200 paragraphs. That is most of the "transient churn while laying out" recorded as open
work below; the live set is untouched.

Correctness: 40 fixture pages dumped every element's `getBoundingClientRect` with the cache on
and off, all 40 byte-identical. `OBSCURA_DISABLE_SHAPE_CACHE=1` is the switch that A/B ran on.

### The CDP watchdog scans its slots in a separate frame

`CdpWatchdogCore.WatchdogLoop` calls `FireExpiredAndFindNextDeadline()`
(`[MethodImpl(MethodImplOptions.NoInlining)]`) rather than scanning inline. The worker parks on
`Monitor.Wait` for as long as nothing is armed, and a `Slot` left in that frame's stack slots is
a GC root for that whole time. A `Slot` holds a `V8IsolateHandle`, and through ClearScript's
`V8ScriptEngine -> DocumentSettings -> RecordingModuleLoader -> PocketCalculatorJsRuntime ->
PocketCalculatorState -> PreparedRender` that is the entire previous document - about 440 MB on a
60k-node page, held from the moment a command disarmed until the next one armed. So every
navigation built its new document with the old one still fully resident, and no collection
could reclaim it.

DEVIATION from `crates/obscura-js/src/cdp_watchdog.rs`, which needs no equivalent: Rust drops
the `IsolateHandle` when the slot leaves the map.

Established with `dotnet-dump` + `gcroot` on a server heap, which named the root as
`Thread 574: CdpWatchdogCore.WatchdogLoop() -> Slot -> V8IsolateHandle -> ... ->
PreparedRender`. `CdpWatchdogTests.ADisarmedHandleIsNotKeptAliveByTheParkedWorker` pins the
property but **does not reproduce the bug** - whether a dead local is still reported live
across the wait is the JIT's choice, and with the scan inline that test passes anyway (verified
5 of 5). `WatchdogLoop` is entered once and never returns, so call counting never promotes it
and it leaves tier-0 only by on-stack replacement of its loop; tier-0 reports untracked locals
live for the whole frame, where optimised code need not. A pass therefore says which tier that
thread was in, not that an inline scan is safe - which is why the fix is structural rather than
a reliance on liveness reporting. Do not read a pass as licence to move the scan back inline.

### Replacing a document asks the GC to give its memory back

`Page.InitJs` and `Page.Dispose` call `PageHelpers.ReleaseReplacedDocumentMemory()`, a
`GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true)` gated on the
managed heap exceeding `OBSCURA_DOCUMENT_GC_THRESHOLD_MB` (default 128, `0` disables).

DEVIATION from `crates/obscura-browser/src/page.rs`, which has nothing equivalent and needs
nothing: dropping the Rust page frees its allocations and the allocator returns the pages. .NET
does not. An ordinary blocking, compacting gen2 collection leaves the regions committed - a
probe with a 549 MB committed heap and 0 MB live stayed at 549 MB RSS after
`GC.Collect(2, Forced, true, true)` and fell to 37 MB after the `Aggressive` form, in 52 ms.

Measured over CDP on a 60k-node page, five navigations plus a final `about:blank`. The live
managed set was flat throughout - 421 to 432 MB with a document loaded, 21 MB on `about:blank` -
so nothing was leaking logically; the runtime simply kept the regions.

| | nav 1 | 2 | 3 | 4 | 5 | peak | after about:blank |
|---|---|---|---|---|---|---|---|
| before | 925 | 1392 | 1484 | 1508 | 1534 | 1713 MB | 1493 MB (live 21 MB) |
| after both fixes | 969 | 964 | 1000 | 1021 | 1007 | 1202 MB | 887 MB (live 8 MB) |

Both fixes are needed and neither suffices: the collect alone reached 1805 MB peak (it compacts
a heap it cannot free), the watchdog fix alone 1725 MB (dead sooner, still never returned).

Cost: 60-110 ms per navigation on that heap, against a 9.5-11.6 s navigation, so unchanged
within noise. `DOTNET_GCConserveMemory=9` flattens the curve too without any code change, but
trades throughput globally and does not address the cause.

### A navigation discards the outgoing document's stored response bodies

`Page.NavigateSingleAsync` calls `ClearResponseBodies()` beside the existing
`NetworkEvents.Clear()`. The request ids go with the events that named them, so those bodies
can never be asked for again; Chromium discards them at commit for the same reason.

DEVIATION from `crates/obscura-browser/src/page.rs`, which clears `response_bodies` only from
`Network.clearBrowserCache`, so the buffer grew by a document's worth of bodies per navigation
up to its 128-entry cap. That cap times `ResponseBodyByteLimit` is up to 256 MB per target,
which is a bound but a generous one for a headless engine.

### A neutralized cyclic inline size, and a flex pin, both record what they replaced

`DeferCyclicFlexInlineSizes` rewrites a cyclic percentage inline size before the box tree is
built, and `PinFlexItems` writes a flex item's used main size back as a definite length with its
flex factors frozen. Both rewrites were **lossy**: afterwards nothing in the tree could tell a
neutralized `auto` from an authored one, or a pin from a declaration. Every later consumer that
needed the replaced value back had to reconstruct it - which is what
`ApplyDeferredFlexAutomaticMinimums` and the typed-percentage scope inside `ApplyTableUsedWidths`
are - and two defects were left over that no re-measurement could reach, because neither was a
measurement.

DEVIATION from crates/obscura-render/src/dom.rs, which carries neither record.

- **`LayoutStyle.DeferredCyclicInlineSlots`** is a three-bit record of which inline-size slots
  hold a neutral value, set where the neutralization is written and cleared at the top of the
  next layout's pass. Its readers today are both of the places `DomBuild` asks whether an
  inline-level box's `width` is definite (F33) - and for a bare `width: 100%` under a
  content-sized flex item the field says `auto`, so the box was built shrink-wrapping and stayed
  at taffy's default `flex-shrink: 1`, which the line box then squeezed it with. The `calc()`
  spelling was unaffected only because its `SizeExpressions[0]` survives the same
  neutralization, which is what made the two spellings behave differently for the same CSS. The
  record is deliberately *not* another "measure this as auto" scope: the two that exist already
  are that, and neither could have helped here, because the question `DomBuild` is asking is
  about the declaration, not about a measurement. The two readings are `pinsInlineSize`, which
  zeroes the `flex-shrink` the line box would otherwise squeeze the box with, and `needsOuter`,
  which gives an `inline-flex`/`inline-grid` a shrink-wrapping outer participant that a definite
  width already suppressed. Both had to change: with only the first, an `inline-flex` carrying
  `width: 100%; margin-right: 22px` in a shrink-wrapping flex item was 117 against Chromium's
  139, while the `calc(100% - 16px)` spelling of the same box was already right at 123.

  It is also a new `LayoutStyle` field that the retained-layout-reuse gate above reflects over.
  It is in no `PaintOnlyMembers` list, so a difference in it forces a layout - which is the
  correct fail-closed answer and is already what `Width` does on the same boxes, since the same
  pass rewrites that too.

- **`PinnedFlexItem` / `RescalePinnedFlexItems`** carry each pin's provenance - the flex
  container it was taken against and that container's content width at the time - so a pass that
  narrows the container afterwards can move the pin with it. The scrollbar-gutter pass is exactly
  such a pass (F35's residual): where the row flex container sits *inside* the scroll container
  rather than above it, a `flex: 1 1 auto` item kept its pre-gutter 400 against Chromium's 391,
  and the `calc(100% - 4px)` card under it 392 against 383. The pin is carried by scaling it with
  the container rather than by re-deriving it, because `PinFlexItems` froze the item's factors on
  purpose - re-running the flex algorithm off an already-flexed size flexes it twice. Scaling is
  exact for the one-item and equal-factor rows this reaches and an approximation for a row of
  unequal bases; re-running the whole deferred resolution after the gutter would be exact and is
  the ordering F34 warns about. It runs inside the gutter loop, three rounds outermost-first so a
  nested pin sees its container's new width, and costs one list walk when nothing moved.

Measured the same way F35 was, but A/B'd from one binary behind a temporary environment switch so
a sibling agent's concurrent edits could not land between the two captures. 20 Tesserae routes at
1440x950, Chromium as reference, strictly-aligned pairs only: mean absolute width error
**1.0497 -> 1.0175** over 48,344 pairs, boxes off by more than 2px **1,747 -> 1,664**. Re-run
against the finished build including the `needsOuter` half: **1.0497 -> 1.0151**, 1,747 -> 1,661.
`#/view/Searchable List` carries most of it (0.2291 -> 0.0347, its 76 boxes over 2px down to 0):
its `tss-card-container` goes 1084 -> 1066 and its search box 1084 -> 1075, both exactly
Chromium. `#/view/Pivot` and `#/view/Searchable Grouped List` read as regressions on the first
pass and improve on a clean re-capture (0.3637 -> 0.1953 and 0.5087 -> 0.0603); both render a
different number of rows run to run - the legacy-vs-legacy comparison of those two routes moves
further than the A/B does - so neither is measurable at this resolution.

One route is genuinely worse, and it is F34's tolerance showing through rather than a new defect:
on `#/view/Details List` the `tss-detailslist` container is corrected 538 -> 534 (Chromium 534),
and the `tss-detailslist-header` inside it, which this engine reserves a 9px gutter out of where
Chromium reserves none, moves with it from 529 to 525 - the same -9 it always had, now measured
against a correct parent instead of an inflated one. +0.0062 mean abs, one more box over 2px.

### The MutationObserver shim follows DOM 4.3.4; the Rust shim's registration rules are wrong

`bootstrap.js` is the port's own copy now (CLAUDE.md rule 5), and the shim in
`dotnet/src/PocketCalculator.Js/js/bootstrap.js` diverges from the one in
`crates/obscura-js/js/bootstrap.js` in four places, all of them bugs against DOM 4.3.4 and
against Chromium. Measured with `mo2probe.html` (expected / Rust shim / this one):

| case | Chromium | Rust shim | here |
|---|---|---|---|
| one observer watching two nodes, mutate one | 1 record | 2 records | 1 record |
| `disconnect()` after the mutation, before the checkpoint | 0 callbacks | 1 callback | 0 callbacks |
| `observe(el, { attributeFilter: ['data-x'] })` | 1 record | 0 records | 1 record |

- `observe()` pushed `this` onto `globalThis.__mutationObservers` on every call, so an observer
  watching N nodes was listed N times and `__notifyMutation` delivered each record N times. It is
  now listed once, and re-observing a node replaces that registration's options.
- `disconnect()` removed one list entry and left the record queue alone. It now removes every
  entry, empties the queue and drops the observer from the notify set.
- `attributeOldValue` / `attributeFilter` now imply `attributes`, `characterDataOldValue` implies
  `characterData` (observe() steps 3-4), and `attributeFilter` filters by name.
- The notify set is drained by one microtask per checkpoint instead of one promise job per
  mutation. Delivery was already batched correctly - the first job spliced every record and the
  rest found an empty queue - so this is allocation, not behaviour, but 20,000 mutations under an
  observer queued 20,000 jobs to deliver one callback.

Delivery *placement* was never the problem: the notify set is drained by
`PocketCalculatorJsRuntime.PumpTick`'s `PerformMicrotaskCheckpoint()`, at the top of the turn and after
every posted task and timer callback, which is where the HTML event loop puts it. Covered by
`PocketCalculator.Js.Tests/MutationObserverTests.cs` (7 facts). Worth carrying back to the Rust shim if
`crates/` ever stops being read-only.

### Shaped inline items flush before a level's positive z-index layers, not after them

`paint.rs` paints one stacking context as bands (negative z, normal, floats, positive z) and
then, after that whole loop, walks the level's nodes again to paint the shaped inline items
"last, in tree order". That trailing flush also puts them above the level's *own* positive
z-index stacking contexts, which CSS 2.1 Appendix E does not: inline-level content is step 7,
positive z-index stacking contexts are step 9.

The visible symptom is that box backgrounds obey stacking order and text does not. On
Curiosity Workspace `#/spaces/new` the modal's white panel covers the page's backgrounds while
the page's headline and bullet lines paint straight through it; Chromium hides them. Reduced
to `render-repros`-style markup: a static block of text plus a later
`position: fixed; z-index: 1010` panel over it.

`PaintDomPainter.PaintLaidDomScrolled` now flushes the inline band where the positive-z band
begins instead of after the loop, excluding the remaining entries' subtrees first (each of
them repaints its own subtree through its recursion). Negative-z layers, in-flow block
backgrounds and floats still paint below the text, unchanged.

Covered by `PaintStackingOrderTests`; all 64 `render-repros` fixtures render byte-identically
before and after.

### Dirty form state is mirrored onto the arena so the renderer can see it

HTML keeps `element.value` and `element.checked` off the content attributes once script
assigns them, and `bootstrap.js` models that with two globals keyed by node id
(`_formValues` / `_formChecked`). Neither engine's renderer can read a JS global, so in
`crates/obscura-render` a field whose value came from script paints its *placeholder*:
`getComputedStyle` and `el.value` are right, only the paint is wrong. On
`#/spaces/new` in Curiosity Workspace, Obscura painted the grey "My Space" where
Chromium 141 paints "My awesome space".

`bootstrap.js` is shared verbatim with the Rust engine, so the fix sits on this side of
the boundary: `PocketCalculator.Js.Runtime.FormStateMirror` installs both globals as proxies
*before* bootstrap.js runs (it adopts them, `globalThis.X = globalThis.X || {}`), and
their write traps forward to two op_dom commands the Rust op table does not have,
`set_form_value` / `set_form_checked`. Those land in `DomTree`'s dirty-form-state tables,
which `PaintNativeControls.ShownValue` / `.IsChecked` read ahead of the attribute. Reads,
key order and `undefined` semantics are untouched, which is what bootstrap.js's
`!== undefined` checks depend on. The entry is dropped when the node's arena slot is
freed, so a recycled `NodeId` cannot inherit it.

Not yet mirrored: selector matching. `:checked` and `:placeholder-shown` still consult the
attributes, so a script-driven state change restyles only what the attribute says.

### Native form controls are painted; the reference paints none of them

`crates/obscura-render/src/paint.rs` paints no widget of its own, so against Chromium 141
an unstyled checkbox and radio are blank, a range input has no thumb, and a date/time
input is an empty box sized as though it were a 20-character text field, which collapses a
shrink-to-fit ancestor around it (Curiosity Workspace's `.tss-daterange-picker` measured
80px against Chromium's 324px). `PocketCalculator.Render.PaintNativeControls` paints all of them,
and three pieces of it are worth knowing:

- **`::-webkit-slider-thumb` is matched and cascaded properly**, not replaced by a UA
  default: a page restyles the thumb's size, radius, background and border and those are
  what get painted. It is a fourth `PseudoRuleMap` in `CssCascade` beside
  before/after/placeholder, gated on `input[type=range]`, landing in
  `LayoutStyle.SliderThumbPseudo`, and it defaults to `box-sizing: border-box` the way
  Chromium's UA sheet does. `::-moz-range-thumb` is indexed nowhere, because Chromium
  honours only the WebKit spelling and this engine presents itself as Chromium. With no
  author rule a plain Chromium-style track and round knob are drawn instead.
- **A zero-height slider still has ink.** The thumb stands outside the control, and a page
  that draws its own track commonly leaves the input with no height at all, so
  `PaintDom`'s ancestor-clip cull asks `PaintNativeControls.NativeInkBounds` before
  dropping a box whose own rect has no area.
- **A date/time control is sized from its field text.** Chromium fills it with read-only
  sub-fields (`mm/dd/yyyy`, `Week --, ----`, …) plus a picker indicator; the port measures
  the same text through the inline engine and adds `FieldChromeWidth`, a fixed per-type
  constant read off Chromium (the sub-field padding plus the ~34.33px indicator). That
  lands within 1px of Chromium for all five types. The indicator itself is drawn as a small
  calendar or clock outline rather than Chromium's icon asset. The UA style for these types
  also switches to monospace with 1px of left padding, as Chromium's does.
  A **percentage** width cannot resolve while intrinsic sizes are computed, so the
  intrinsic width is additionally published as a `min-width`: Chromium has shadow content
  to contribute where this engine has no box for it. It differs from Chromium only where
  such a control is deliberately squeezed below its own content width.

Related fix in the same arm: the general `<input>` path painted the `value` attribute as
text for *every* type, which put "50" beside a slider. Only the text-field types show a
value now, and only the text-field types show a placeholder.

### The promise-rejection callback contains everything, and is detached before dispose

deno_core installs V8's `PromiseRejectCallback` and reports from it directly; the ops
around it are wrapped in `catch_unwind`, so nothing unwinds into V8. ClearScript's
equivalent hook (`V8ScriptEngine.PromiseRejectionCallback`) has no such wrapper: its
host-object and fast-function thunks all convert a managed exception into a scheduled
script exception, but the promise-rejection thunk does not, so an exception thrown in
that callback unwinds into V8's own frame.

That is a live process-kill path, because the callback re-enters script to deliver the
report and the watchdog terminates isolates. A page terminated mid-checkpoint made every
re-entry raise `ScriptInterruptedException`, which the callback deliberately rethrew; and
once ClearScript had torn the engine down, the thunk itself raised
`ObjectDisposedException` before any of our code ran. A 157-route survey died on the
second form: `Unhandled exception. System.ObjectDisposedException ... at
V8SplitProxyManaged.<get_InvokePromiseRejectionCallbackFastMethodPtr>g__Thunk`.

So `DenoCoreShim.Report` contains every exception, the interrupt included, and suspends
further delivery until `PocketCalculatorJsRuntime.CancelTermination` clears the termination (a
terminated page can raise thousands of rejections, each one a re-entry). And
`DenoCoreShim.Detach` unregisters the hook before `PocketCalculatorJsRuntime.Dispose` /
`FrameRealm.Dispose` destroy the engine, which is the actual fix for the disposed-engine
form: the catch cannot reach an exception thrown inside the thunk.

Observable difference from Rust: a rejection raised while the isolate is terminating is
dropped rather than reported. The page is being torn down or reset at that point, and the
watchdog's own `ScriptInterruptedException` still reaches the host through the call it
interrupted.

Covered by `RejectionEventTests.Terminating_a_page_that_is_producing_rejections_stays_contained`
and `Rejection_reporting_resumes_after_the_termination_is_cleared`.

### A grid item's percentage height resolves against its grid area

`dom.rs` drops a block-axis percentage whenever the parent box has no definite height,
which is right for a block container and wrong for a grid item: a grid item's containing
block is its **grid area**, so `grid-template-rows: 24px` gives it a definite basis no
matter what the grid container's own height is. The reference computed `height: 100%` on
such an item to `auto`, and with a non-stretch `align-items` nothing else could supply a
height, so the item laid out 0px tall.

`LayoutDomComputed.ResolveOneComputedStyle` now keeps the percentage when the element's
rendered parent is a grid container, and hands it to taffy, which already resolves a grid
item's size against the area itself (`grid/alignment.rs align_and_position_item`, with
`GridItem.KnownDimensions` passing `None` for a track that is still indefinite, so an
`auto` row is sized from content and does not go circular).

Found on Curiosity Workspace: `.tss-gridpicker` (`grid-template-rows: 24px` x8,
`align-items: center`, buttons with inline `height: 100%; width: 100%`) rendered every
cell 24x2 instead of 24x24 and its absolutely-positioned overlays 22x0 instead of 22x22,
504 elements each across `#/preferences?id=file-indexing-schedule`,
`#/preferences?id=file-indexing-monitoring` and `#/manage/data/file-indexing`. The cells
were visibly collapsed in the screenshot. `align-items: stretch` masked the bug, so the
non-stretch case is the one that has to be tested.

`Inherited.CbHeightKnown` is the second half of the change. A percentage basis can now be
definite (bare percentages survive and taffy resolves them) while its pixel value is
unavailable in the style pass, which is exactly a grid item's situation. A **functional**
block-axis percentage has to be flattened to px before layout, so it is gated on the
numeric flag instead: `calc(100% - 4px)` inside a percentage-height grid item stays
`auto` rather than flattening against a basis we would have had to invent.

**Remaining gap:** that `calc()` case. Chromium resolves it to 20px (24 - 4) and the port
gives it the content height. Closing it means resolving the item's row track during the
style pass, which is grid placement and track sizing done twice; not worth it until
something needs it.

Covered by `GridItemPercentageHeightResolvesAgainstGridAreaWithoutStretch`,
`GridItemPercentageHeightResolvesAgainstGridAreaWhenStretched`,
`GridItemPercentageHeightAgainstAutoRowUsesContentHeight`,
`PercentageHeightUnderAutoHeightBlockParentStillBehavesAsAuto` and
`CalcPercentageHeightInsideGridItemStaysWithinTheGridArea`.

### The UA style table is namespace-aware, and SVG presentation attributes cascade

`style.rs`'s `ua_style` is keyed on the tag name alone and `dom.rs` maps HTML presentational
attributes only. The HTML and SVG UA sheets share a lot of names, so an element in the SVG
namespace picked up HTML defaults: an SVG `<a>` got the link colour `rgb(0, 0, 238)` and an
underline (which its `<rect>` children then inherited), `<title>` / `<desc>` got
`display: none`, and every shape reported `display: block`. Chromium gives every SVG element
`display: inline` except `text` and `foreignObject`, which are `block`, and hides none of them
through `display` - `title` / `desc` / `metadata` / `defs` are non-rendered through the SVG
rendering model, which is why `SvgRenderer` skips them by tag name.

So `ComputedStyle.UaStyle` takes the namespace and answers from an SVG table when it is the
SVG one, and `DomCascade.ApplySvgPresentationAttributes` maps the 18 presentation attributes a
real chart uses (`fill`, `stroke`, `stroke-width`, `opacity`, `text-anchor`, `visibility`,
`display`, `color`, `font-size`, `font-family`, ...) into the cascade at author origin, below
every author rule and below the `style` attribute. A bare number on a length-valued attribute
is in user units, i.e. px.

The four SVG-only properties are reported by `getComputedStyle` through `LayoutStyle.SvgPaint`
(`SvgPaintValues`: `fill` `rgb(0, 0, 0)`, `stroke` `none`, `stroke-width` `1px`, `text-anchor`
`start`, all inherited, all reported on every element the way Chromium reports them). That
record is deliberately separate from the specified `SvgFill` / `SvgStroke` / `SvgStrokeWidth`
that `PaintSvg` pushes back into the serialized SVG document as `!important` declarations:
pushing an inherited value there would override the rasterizer's own inheritance, and a `<use>`
of a `<symbol>` in `<defs>` inherits its fill from the use site, not from the `<svg>` root.

One layout bug fell out of `<svg>` becoming inline and is fixed rather than worked around:
`DomBuild.InlineWrapsOnlyInFlowBlocks` spliced any inline element whose children are all
block-level, which now matched an `<svg>` wrapping `<text>`. A replaced box is atomic, so it
now refuses `IsReplacedBox` - without that the svg laid out 0x0 and its SVG text was laid out
and painted as HTML in the body.

Found on Curiosity Workspace, where a 157-route survey showed every SVG chart on the admin
pages mismatching Chromium. The 13-element probe page now matches Chromium 141 exactly on
`display`, `fontSize`, `fontFamily`, `fill`, `stroke`, `strokeWidth`, `opacity`, `textAnchor`,
`visibility` and `color`.

Covered by `SvgStyleTests` (13 facts), which also pins that an SVG `<title>` computing to
`inline` still puts no ink on the page.

### A `calc()` percentage is resolved by layout, not flattened by the style pass

`dom.rs` flattens every functional length to px during the top-down computed-value pass,
against a basis it picks there: the viewport height for a box offset's block axis, and
`Inherited.CbWidth` - the pass's own block-flow estimate of the containing block - for the
inline axis. Both are wrong for a percentage, and no basis picked in that pass can be
right, because the pass runs before layout:

- An **absolutely positioned** box resolves its offsets against the **padding box of the
  nearest positioned ancestor**, which is not the parent the pass is walking and whose used
  size layout has not produced yet. `top: calc(50% - var(--tiny) / 2)` on a chevron inside
  a 34px-tall `position: relative` combobox came out 355px (half of the 720px viewport)
  where Chromium computes 12px, so every Tesserae dropdown chevron sat hundreds of pixels
  below its box and off the page. Measured on `#/manage/data/file-indexing`: container at
  `y=583`, icon at `y=1053`, a constant +470 (`0.5 * 950 - 5`).
- An **inline-axis** size resolves against the used containing block, which the estimate
  only sometimes matches: a flex item that ends 200px wide inside a 300px row gave
  `width: calc(100% + 20px)` as 320px instead of 220px.

Taffy resolves a bare percentage against the real containing block already, and its
`calc()` support (`CompactLength.Calc`, the tree's `CalcResolver`) resolves an opaque
handle the same way. So a percentage-bearing expression is no longer flattened for layout:
`LayoutDomComputed.ResolveOneComputedStyle` parses it into a `GridCalcExpression` - now
shared with the grid track path, with an `allowNegative` flag because an offset may be
negative where a track size may not - and stores it in `LayoutStyle.InsetCalc` /
`LayoutStyle.SizeCalc`, which `TaffyStyleMapping` hands to taffy as a calc value.

Two parts stay flattened, deliberately:

- **The block axis of a relative offset.** Taffy resolves those against a hard 0
  (`BlockLayout`'s `item.Inset.ZipSize(new Size(containerInnerWidth, 0.0f), ...)`), because
  the container's height is not final where the offset is applied. So it keeps being
  flattened, now against the containing block's content-box height, and becomes `auto` when
  that height is indefinite - which is what Chromium computes for a percentage offset it
  cannot resolve (a relative `top: calc(50% - 5px)` under an auto-height parent moves the
  box 0px, not -5px).
- **The block axis of a size.** Its definite/indefinite rules are applied in the style pass
  (see the `.tss-card` entry above) and taffy is not told which case it is in.

Everything else keeps the flattened value too, because layout is not its only reader: the
sticky-offset pass, an inline box's relative shift, pseudo-element paint and the
computed-style projection all read `style.Inset` / `style.Width`. Those get the better basis
as a side effect wherever the style pass does know the containing block's height -
`getComputedStyle().top` on the probe's chevron reports `12px` where it reported `355px` -
and keep the viewport fallback where it does not, so a chevron in a content-sized combobox
is laid out at 12px and still reports `470px`. That reporting gap is the separate
`getComputedStyle` used-value work, not this.

Verified against headless Chromium at 1280x720 on a six-case probe (bare `top`/`bottom`
percentages and a percentage `margin-top` as tripwires), on padding-box, auto-height,
non-parent-ancestor and border-inset ancestors, and on a shrinking flex item.

Covered by `FunctionalInsetsResolveAgainstTheAbsolutePositioningContainingBlock`,
`FunctionalInsetsSampleThePaddingBoxOfTheNearestPositionedAncestor`,
`FunctionalRelativeOffsetsResolveAgainstTheContainingBlockHeight` and
`FunctionalInlineSizesSampleTheUsedContainingBlockWidth`.

**Remaining gap, and it is not this one:** a `calc()` percentage under a flex item that
**shrinks** still resolves against the item's pre-shrink inner size.
`FlexboxLayout.GenerateAnonymousFlexItems` resolves every child size against
`constants.NodeInnerSize` once and the flex algorithm reuses that resolution, so after the
item is shrunk nothing re-resolves it. Minimal repro: a 250px `flex: 0 1 auto` column with
`padding: 0 12px` in a 600px row whose sibling forces a shrink - the column settles at
226px (inner 202px) and a child `width: calc(100% + 32px)` comes out 219px instead of
Chromium's 234px, while a bare `width: 100%` and `width: 150%` beside it are exact. This is
what leaves Curiosity Workspace's sidebar subtree 15px narrow on `#/users`
(`.msk-sidebar-brand` 208px against Chromium's 223.141px, from a basis of 176 where the
reported containing block is 191.141). Not a style-pass basis problem: instrumenting the
resolver shows both 191.14764 and 176 reaching it, and the surviving geometry is the 176
one. Belongs to the flex algorithm, not to the computed-value pass.

### `filter` carries the whole function list; the reference keeps only a blur

`style.rs` parses `filter` for `blur()` alone and stores one sigma (`FilterBlur`), so a
list carrying any other function is dropped entirely: nothing is reported through
`getComputedStyle`, and nothing but blur is painted.

The C# side models the list. `LayoutStyle.Filter` is a `FilterFunction[]`,
`ComputedStyle.ParseFilterFunctions` reads it, `PaintCssValues.FilterCss` serializes it
into the computed-style snapshot, and `PaintFilters.ApplyFilterChain` paints it onto the
group layer the element already gets for `opacity`. Parsed, reported and painted:
`blur()`, `drop-shadow()`, `brightness()`, `contrast()`, `grayscale()`, `invert()`,
`opacity()`, `saturate()`, `sepia()`, `hue-rotate()`.

Found on Curiosity Workspace, where `.tss-pixelavatar-canvas` outlines the pixel-art
avatar with four 1px `drop-shadow()`s and the avatar rendered with no outline at all.
45 `filter` values were measured against Chromium 141 and 44 now serialize identically
(the 45th is the `em` gap below). Three details of Blink's serialization are load-bearing
and are the reason `box-shadow` cannot share the code: the `drop-shadow()` colour comes
first, all three of its lengths are always emitted, and the multiplier functions report a
number rather than the authored percentage.

`backdrop-filter` deliberately keeps the blur-only parse: nothing paints a backdrop
sepia, so reducing a mixed list to its blurs would paint a wrong result where painting
none matches what `@supports` advertises. `@supports (filter: ...)` now answers true for
everything except `url()`.

Not painted, and reported anyway: `filter: url(#svg-filter)`. SVG filter elements are not
modeled, so the reference round-trips through `getComputedStyle` (Chromium reports
`url("#svg-filter")`, and reporting `none` instead would be a second wrong answer) while
`PaintFilters.HasVisibleEffect` returns false for it, so it takes no layer and paints
nothing. `@supports` reports it unsupported for that reason.

Covered by `FilterParsesTheWholeFunctionList`,
`FilterDropShadowTakesItsColorOnEitherSide`, `FilterIsReportedAndDefaultsToNone`,
`FilterDropShadowSerializesColorFirstAndAlwaysThreeLengths`,
`FilterDropShadowOutlinesTheElementOnAllFourSides` and
`FilterColorMatrixFunctionsRecolorTheSubtree`.

Re-measured end to end against Chromium 141 over the whole surface - the four-way avatar
outline, a coloured and an uncoloured `drop-shadow()`, a six-function multiplier chain,
`url()`, the clamped and the dropped out-of-range forms, `blur(1em)`, an alpha colour -
and all twelve now report byte-identically. The outline also paints: the same page
screenshots to 0.048% of pixels differing over the viewport, which is anti-aliasing on the
blurred cases and nothing on the outline.

One value did not, and its fix is below: `currentcolor` in a `filter` or a `box-shadow`
was resolved against whatever `color` the cascade had reached when the declaration was
applied, so `filter: drop-shadow(currentColor 1px 2px 3px); color: rgb(10,20,30)` reported
black - the `color` declaration comes after it - and so did an element that inherits its
colour rather than declaring one. Chromium resolves both against the element's final
computed colour. Such a declaration is now kept for the top-down pass exactly as a
font-relative one is (`ComputedStyle.NeedsLateResolution`, read in
`ResolveFontRelativeDeclarations`, where `style.Color` is already settled), which is where
`filter: blur(1em)` was already being re-read. Covered by
`CurrentColorInAFilterOrShadowResolvesAgainstTheFinalComputedColor`.

### A font-relative length resolves against the element's font, not a flat 16px

`px_value` in `style.rs` scales every font-relative unit by a flat 16 and has no way to
be told otherwise. The port carried that verbatim, so on a 13px element
`filter: blur(1em)` and `box-shadow: 0 0 1em red` both computed to 16px where Chromium
reports 13px, and `1ch` had its unit stripped and read as the bare number - `1ch` meant
1px. Ordinary length properties were never affected: `padding`, `margin`, `line-height`,
`letter-spacing`, `gap` and `font-size` itself all keep a `Dimension` or an expression and
resolve in the DOM top-down pass, which is why `padding: 1em` was exact throughout.

`PxValue` and `Px` now take the em and rem sizes their units are relative to. Nothing can
hand them those while a declaration is being cascaded - `font-size` is itself cascaded, and
inherited when absent - so the three properties that read a length on the spot keep the
declaration text when it carries an `em`, `rem`, `ex` or `ch`
(`LayoutStyle.FilterFontRelative`, `BackdropFilterFontRelative`, `BoxShadowFontRelative`)
and `ComputedStyle.ResolveFontRelativeDeclarations` re-reads it from the top-down pass,
next to `SetGridCalcContext`, which already did the same job for grid tracks. An element
with no font-relative value in those three keeps nothing and pays one null check.

Measured against Chromium 141 on a 13px element, before -> after: `blur(1em)` 16px -> 13px
(Chromium 13px), `blur(0.5em)` 8px -> 6.5px (6.5px), `blur(1rem)` 16px -> 16px (16px),
`drop-shadow(1em 2em)` 16/32px -> 13/26px (13/26px), `box-shadow: 0 0 1em` 16px -> 13px
(13px). Two things came with it, both previously broken rather than merely imprecise:
`calc()` inside one of these lengths (`blur(calc(1em + 2px))` invalidated the whole
declaration, because a bare-token reader cannot see into a function; now 15px, as Chromium
says), and `currentcolor` in a re-read value, which now resolves against the inherited
colour rather than whatever `color` the element had reached mid-cascade.

Covered by `FontRelativeLengthsResolveAgainstTheElementsOwnFontSize`,
`EmInAFilterFollowsTheInheritedFontSizeToo`, `CalcResolvesInsideAFilterOrShadowLength` and
`ChResolvesAgainstTheFontSizeInsteadOfBeingDroppedOrReadAsPx`.

**Still eagerly resolved, and still wrong for a font-relative value:** a `border-width` in
`em` (`StyleBorder.StrictBorderLength`), `border-spacing`, `background-size` and
`background-position`. Each reads its length through the parameterless `PxValue`/`Px` and
would need the same deferral; none was measured as a live difference, so none was moved.

### DEVIATION - `ex` and `ch` are one constant each, where Chromium measures the face

CSS defines `1ex` as the font's x-height and `1ch` as the advance of its `0` glyph, both
per face. The parser has no font at hand, so both are a fraction of the em:
`Dimension.ExPerEm` = 0.528_320_3 (the reference's own constant, Liberation Sans' OS/2
`sxHeight`, 1082/2048) and `Dimension.ChPerEm` = 0.556_152_3, added here as Liberation
Sans' advance for `0`, 1139/2048, read out of
`crates/obscura-render/assets/liberation-sans.ttf`.

Liberation Sans is the choice for the same reason it was `ExPerEm`'s: it is what
`FontAssets.ResolveFontFamily` returns for an element that names no family, so it is the
face this renderer actually paints unstyled text with. The cost is a page that names a
different generic: Liberation Mono's `0` is 0.600_1 em and Liberation Serif's is 0.5, so a
monospace or serif `ch` is ~8% out either way. Chromium measured on the same markup
reports `1ch` = 0.5 em and `1ex` = 0.458_98 em, both of its default *serif* face - this
port's default family is sans, which is a separate, pre-existing difference.

`ch` was not a unit anywhere in the port before this: `DimensionValue` did not know it, so
`width: 1ch` fell through to `auto`, and `PxValue` stripped the unit and read `1ch` as 1.
`DimensionKind.Ch` is appended last so no ordinal moves, and every switch that enumerates
the relative kinds carries an arm for it.

### DEVIATION - a native control's border computes one colour and paints another

Chromium's UA sheet gives `button` `border: 2px outset ButtonBorder` and `input`
`border: 2px inset ButtonBorder`, and `ButtonBorder` resolves differently on the two:
`rgb(0, 0, 0)` on a button, `rgb(118, 118, 118)` on an input and a select. Chromium paints
neither. A control with the default `appearance` goes to the native form-control painter,
which strokes a flat 1px `rgb(118, 118, 118)` line - confirmed on the glass, by sampling a
Chromium 141 capture of a bare `<button>` and `<input>`: one grey pixel, then the face.

The computed value and the painted one are therefore two different things, and an earlier
pass here conflated them - it put the painted grey in the `button` arm, so every button in
the app reported a `border-color` Chromium does not. They are now separate:
`ComputedStyle` computes what `getComputedStyle` has to say (black on a button, grey and
`inset` on an input), and `LayoutStyle.NativeControlAppearance` gets
`PaintBorders.PaintNativeControlBorder` to draw the flat 1px grey in place of the two-tone
relief those values would otherwise produce.

The port does not model `appearance`, so "is this control still native" is read back off
the border: the flag applies only while the border is exactly `2px outset` black or
`2px inset` rgb(118, 118, 118) on all four sides. An author rule that reaches the border
moves one of those and takes the native paint away, which is what Chromium does too. The
gap is an author who writes exactly the UA border back by hand.

Before -> after, on `<button>Hi</button>` and `<input value=x>`: button `border-color`
rgb(118, 118, 118) -> rgb(0, 0, 0) (Chromium rgb(0, 0, 0)); input `border-style` `solid` ->
`inset` (Chromium `inset`); button paint a 2px 156/85 bevel -> a flat 1px 118 stroke
(Chromium flat 1px 118); input paint a flat 2px 118 band -> a flat 1px 118 stroke
(Chromium flat 1px 118). Width, style and geometry were already exact and did not move.

Still unmodeled, and separate: a control's `rgb(239, 239, 239)` UA background, which this
port leaves transparent on a button.

Covered by `ButtonCarriesTheUserAgentOutsetBorder`,
`ButtonComputesTheBlackUserAgentBorderChromiumReports`,
`InputComputesTheInsetUserAgentBorderChromiumReports`,
`SelectKeepsItsSolidGreyUserAgentBorder` and
`NativeControlsPaintTheFlatOnePixelStrokeChromiumDraws`.

### An `<input>`'s intrinsic width is a curve fit, and it is ~8px narrow in a sans face

`dom.rs` sizes a text input's content box as `size * font-size * 0.6 + font-size * 0.675`,
a fit rather than a measurement, and the port carries it. Chromium computes it from the
resolved face: `ceil(avgCharWidth * size)` plus `maxCharWidth - avgCharWidth`.

On a bare `<input value=x>` at the UA 13.3333px, Chromium 141 reports a 185px border box
and this port 177px. The whole 8px is in the content width, not in the UA box: with
`border: 0; padding: 0` the two are 177 and 169, and the 8px of padding and border the
default adds is identical in both. `font-family: monospace` closes it almost completely -
Chromium 178, port 177 - which is what says it is a font metric: the 0.6 em per character
happens to be Liberation Mono's advance exactly (0.600_1 em) and undershoots Arial's.

Left alone deliberately. Matching it means replicating Blink's
`PreferredContentLogicalWidth` against the same face Chromium resolves, which is work in
the text layer and would move every control's intrinsic size; forcing the number with
another constant would only move the error to a different family. `<select>` is 1px out
(Chromium 30, port 31) for the same kind of reason.

### `height: fit-content` is implemented; the reference ignores it

`style.rs` handles `fit-content` on `width`/`inline-size` only (`width_fit_content`),
and its `height`/`block-size` arm parses the keyword as a plain dimension, which falls
back to `auto`. A box declaring `height: fit-content` therefore keeps an automatic
block size and a flex or grid item carrying it stretches to fill its line or row.

`LayoutStyle.HeightFitContent` is the C# counterpart. In the block axis `fit-content`
sizes to content exactly like `auto`, so `Height` stays `Auto`; the one observable
difference is that the box is no longer automatically sized, and CSS stretch alignment
applies to an auto cross size only. `DomStyleFixups.ApplyFitContentBlockSize` writes
that used alignment into the item's own `align-self` (taffy's box-size dimension
cannot carry an intrinsic keyword), leaving an authored `align-items: center` / `end`
alone.

Measured against Chromium on Curiosity Workspace, where Tesserae sizes avatars,
buttons, context cards, cron editors and date-range pickers with
`:where(...) { width: fit-content; height: fit-content }`: a suggestion card came out
163px tall against Chromium's 56px, with every child sized identically in both engines.

Covered by `HeightFitContentHugsContentInsteadOfStretching` and
`HeightFitContentRespectsExplicitCrossAxisAlignment`.

### `width: max-content` / `min-content` are implemented; the reference ignores them

`style.rs` recognizes `fit-content` on `width`/`inline-size` and nothing else, so
`max-content` and `min-content` parse as an unknown dimension and fall back to `auto`.
An `auto` inline size on a block-level box fills its containing block, which is the
opposite of what both keywords ask for: a `width: max-content` column flex box inside a
1200px parent came out 1200px wide against Chromium's 202px.

`LayoutStyle.WidthFitContent` / `HeightFitContent` are now the "is an intrinsic
keyword" predicates over `WidthIntrinsicKeyword` / `HeightIntrinsicKeyword`
(`IntrinsicSizeKeyword`), which say *which* keyword it is. Taffy's box-size dimension
still cannot carry any of them, so the dimension stays `Auto` and
`DomPasses.ApplyIntrinsicInlineSizes` resolves it once the containing space is known:
`min-content` takes the min-content measurement, `max-content` the max-content one, and
`fit-content` keeps the existing `clamp(min-content, stretch-fit, max-content)`. In the
block axis all three size to content like `auto`, so they share
`ApplyFitContentBlockSize` and only stop the box from being stretched.

Covered by `WidthMaxContentAndMinContentSizeToTheirMeasurement`.

### `min-*` / `max-*` take the intrinsic sizing keywords too; the reference ignores them

Same gap one level further: `style.rs` reads `min-width`/`min-height`/`max-width`/
`max-height` through `dimension_value`, which has no keyword path at all, so all three
keywords computed to the initial value and the declaration was silently dropped. On a
300px container with content whose min-content width is 77px and max-content 511px, 13
of 22 probe cases differed from Chromium - `min-width: max-content` stayed at 300,
`max-height: min-content` left a `height: 400px` box at 400, a shrinking column flex
item ignored `min-height: min-content`, and so on.

The four properties now carry their own `IntrinsicSizeKeyword` on `LayoutStyle`
(`MinWidthIntrinsicKeyword`, …), parsed by the same `IntrinsicSizeKeywordValue` the
preferred sizes use, and follow the same mechanism: the dimension stays at its initial
value and a convergence pass writes the measured length.

- **Inline axis** - `ApplyIntrinsicInlineSizes` (the renamed `ApplyFitContentWidths`)
  now measures for `width`, `min-width` and `max-width` in one pass. The measurement
  drops *every* inline-axis declaration on the box first, a plain length included: a
  keyword names an intrinsic size of the content, so `min-width: max-content` beside
  `max-width: 200px` is 511px of min-width that the 200px maximum then loses to, not
  200px measured through its own clamp.
- **Block axis** - `ApplyIntrinsicBlockSizes` runs after the inline pass and its
  relayout, and re-lays each box out at its used inline size with every block-axis
  constraint removed. For a box whose inline size is definite, min-content, max-content
  and the stretch-fit clamp between them are all the content height, so one measurement
  serves all three keywords. `height` needs no counterpart: sizing to content there is
  what `auto` already does.
- `getComputedStyle` reports the keyword for these four (it is their computed value),
  unlike `width`/`height`, which report a used length.

All 22 probe cases now match Chromium, and 27 further cases were added to the probe -
a keyword competing with a length or a percentage, a min-height losing to a larger
height, a max-height beating one, out-of-flow and replaced boxes, and the containing
block widths that make `min-width: min-content` / `max-width: max-content`
non-vacuous. Covered by `MinAndMaxWidthResolveTheIntrinsicSizingKeywords`,
`MinAndMaxHeightResolveTheIntrinsicSizingKeywords`,
`IntrinsicMinAndMaxSizesApplyToFlexGridAndOutOfFlowBoxes` and
`IntrinsicSizingKeywordsAreTheComputedMinAndMaxSizes`.

Found while checking it, and separate: a fixed-width `<img>` in a *narrower* containing
block is shrunk to the container (`width: 160px` in a 100px block gives 100px against
Chromium's 160px). The probe carries two keyword-free controls that show it, and it is
untouched here - it is replaced-element sizing, not keyword resolution.

### An auto-sized `<button>` accumulates by line, not over the whole subtree

`native_button_intrinsic_content` in `dom.rs` walks a button's subtree and adds up every
descendant's contribution. That is a row accumulation, and it is only correct when the
content really does share one line. A `<button>` wrapping a column flex container - the
shape of every stacked Tesserae button - therefore measured the *sum* of the column's
items instead of the widest of them: 227px against Chromium's 180px for a
[51px, 164px] column. The same subtree under an inline-block `<div>` was already right,
because a div is sized by real CSS intrinsic sizing rather than by this shortcut.

The port's walk asks each container how its children stack before combining them
(`DomStyleFixups.StacksChildrenInBlockAxis`): a column flex container, a single-column
grid, and a block container holding block-level children each give their in-flow
children their own line, so the container contributes the widest line; anything else
keeps summing along the line. Consecutive inline-level children of a block container are
still measured as one run.

Measured against Chromium on Curiosity Workspace `#/preferences?id=themes`, where each
theme tile is a `<button class="tss-btn">` around a `.tss-stack` column contributing 51
and 164: the tile went from 257px to Chromium's 206px, and the wrapping row of tiles
from 4 per row to Chromium's 5.

Covered by `ButtonTakesTheWidestItemOfAColumnFlexChildNotTheirSum` and
`ButtonStillSumsInlineLevelContentOnOneLine`.

The 4px that was still missing on a button with no author border is now carried too: the
`button` UA arm sets `border: 2px outset` alongside its `padding: 1px 6px`. On the
`btn-min.html` repro every element matches Chromium exactly (b1/b2/b4 180x46, b3 67x26,
d1 164x40), where the buttons read 176 and 63 before. Covered by
`ButtonCarriesTheUserAgentOutsetBorder`.

### `align-content: baseline` uses its fallback alignment

`content_alignment_value` in `style.rs` does not accept the baseline keywords, so the
declaration is dropped and the container keeps `normal`, which for content distribution
is `stretch`. Baseline alignment does not apply to content distribution at all: CSS Box
Alignment gives it a fallback alignment, `start` for a first baseline and `end` for a
last one, which is what Chromium does.

Found while checking the `height: fit-content` fix against the real app. Tesserae's
`.tss-grid` asks for `align-content: baseline`, so stretching its auto rows made a
400x56 card sit in a 175px row; Chromium sizes the row to the card. With both fixes the
home route's four suggestion cards land on Chromium's exact rows.

Chromium reports the computed value as `baseline` while behaving as `start`; the port
reports the fallback, because taffy's `AlignContent` has no baseline variant to carry.

Covered by `BaselineContentAlignmentUsesItsFallbackInsteadOfStretching`.

### `font-family: inherit` is honoured on form controls

`style.rs` skips the `inherit` keyword on `font-family` (`if family != "inherit"`), so
the declaration is dropped rather than resolved, and whatever the UA sheet put there
survives. Every form control carries an explicit `arial`, and the reset rule every
page ships - `input, textarea, select, button, optgroup { font-family: inherit }` - is
exactly how the page's own face is supposed to reach them, so all of them rendered in
Arial. On Curiosity Workspace that was 889 of 1715 aligned elements: every `<button>`
and everything inside one. `font-size: inherit` in the same rule already worked, so
the two halves of the same declaration disagreed.

The C# arm treats `inherit`/`unset` as "clear the family", which is what the top-down
pass reads as inherit, and `revert`/`revert-layer` as "keep the UA value" - the same
shape the `font-weight` arm already had.

A visible consequence: an icon `<i>` inside a button takes its glyph from a `::before`
whose rule sets `content` but deliberately not `font-family`. Inheriting Arial left the
private-use codepoint without a glyph and the icon rendered as tofu.

Covered by `FontFamilyInheritClearsTheUserAgentFormControlFont` and
`FormControlsInheritThePageFontFamilyThroughTheAuthorRule`.

### The five overflow keywords are kept apart, and an image clips

`style.rs` collapses `hidden`, `scroll`, `auto` and `overlay` onto one code, so the
computed value cannot say which of the four an element specified and a computed-style
query answers `auto` for all of them. `OverflowSpecifiedX`/`Y` now carry the specified
keyword (0 `visible`, 1 `clip`, 2 `hidden`, 3 `scroll`, 4 `auto`, `overlay` sharing
`auto`'s code), `OverflowComputedX`/`Y` carry it after the CSS Overflow computed-value
coupling, and `LayoutStyle.ComputedOverflowCss` renders it. Every code from 2 up is a
scroll container, which is what keeps the coupling and the layout booleans unchanged:
the coupling now turns `visible` into `auto` and `clip` into `hidden` on the other axis
rather than making both codes equal.

`ua_style`'s `img` arm sets display only. Chromium's UA sheet gives an image
`overflow: clip; overflow-clip-margin: content-box`, so an image computes `clip` on
both axes and its content cannot paint outside its box; that was 11 of 1715 aligned
elements on Curiosity Workspace, all images.

**Still outstanding, in `Paint/PreparedRender.cs`:** its local `OverflowAxis` helper
reports `"auto"` for anything scrollable, so `hidden` and `scroll` are still reported
as `auto` (322 and 298 of the 1715 aligned pairs). The model now carries the right
value - the fix is to read `style.ComputedOverflowCss(true)` / `(false)` for
`overflow-x` / `overflow-y`, and the same helper serves the missing `overflow`
shorthand.

Covered by `OverflowKeywordsKeepTheirComputedIdentity`.
### A `font-family` computed value keeps the author's spelling

`style.rs` stores the family list lower-cased (`CssText.AsciiLower` on the C# side),
because every face lookup matches against it case-insensitively. That spelling is also
what the snapshot reported, so Chromium's
`"Plus Jakarta Sans", Inter, "Segoe UI", sans-serif` came back as
`"plus jakarta sans", "inter", ...` - wrong on 816 of 1715 aligned element pairs on
Curiosity Workspace.

`LayoutStyle.FontFamilySpecified` carries the reporting spelling next to the
lower-cased `FontFamily`, and follows it everywhere including inheritance and the
pseudo-element settle. `ComputedStyle.SerializeFontFamilyList` re-serializes the list
the way Blink does: the author's casing, one `", "` between families, and quotes only
where a family does not round-trip as an identifier - so an unquoted `Plus Jakarta
Sans` gains quotes, a quoted `"Inter"` loses them, and a quoted `"sans-serif"` keeps
them because it is a string rather than the generic keyword. Checked against
Chromium 141 for each of those shapes.

Covered by `FontFamilyKeepsTheAuthorsSpellingForReporting`.

### The cascade models `cursor` and `pointer-events`

Neither property exists in `style.rs`, so neither reached the snapshot and
`getComputedStyle` fell through to bootstrap's inline-declaration fallback: `cursor`
was wrong on ~1200 of 1715 aligned pairs and `pointer-events` on ~400. Both are
inherited with initial `auto`, both are stored as the validated keyword (null while
inheriting), and `PreparedRender.ComputedStyle` emits them.

The UA values come with them: `button` and `select` are `default`, `input` is `text`,
and `a` is `pointer` when it has an `href` - which is why that one is set in
`DomCascade` rather than in the tag-keyed `UaStyle`.

`pointer-events` is reporting only. Hit testing runs in JavaScript through
`document.elementFromPoint` in `PocketCalculator.Js`, which does not consult the cascade, so
making the property behavioural is a separate change there.

Covered by `CursorAndPointerEventsAreModelled` and
`CursorAndPointerEventsInheritDownTheTree`.

### The CSSOM snapshot serializes numbers and shorthands like Chromium, not like Rust

`computed_style` in `crates/obscura-render/src/paint.rs` writes each `f32` with
Rust's `Display`, i.e. the shortest decimal that round-trips, so `font-size:11px`
with `line-height:1.3` serializes as `14.299999px`. Blink formats a CSS number
with WTF's `String::Number` - `%.6g` with trailing zeros truncated - and reports
`14.3px`. `PaintCssValues.CssNumber` now does the Chromium thing, so every length
the snapshot emits (and the one SVG `opacity` attribute that shares the helper)
matches what page script compares against. Verified against Chromium 141 on
14.3 / 20.8002 / 0.333333 / 1261.33 / 3.35544e+07 / 0.123457.

`PreparedRender.ComputedStyle` also emits properties the Rust snapshot never had:
the `margin` / `padding` / `border-width` / `border-style` / `border-color` /
`border-radius` / `border` / `outline` / `overflow` / `gap` / `flex` shorthands,
`top` / `right` / `bottom` / `left`, the `flex-*` longhands, `background*`,
`box-shadow`, `text-decoration*` and `font-style`. A property missing from the
snapshot falls through to bootstrap's inline-declaration fallback, which answers
the empty string or a box-derived number, so ~1700 of 1715 measured element pairs
read a wrong value. Covered by
`dotnet/tests/PocketCalculator.Render.Tests/ComputedStyleSnapshotTests.cs`, whose
expectations are all taken from Chromium.

`cursor`, `pointer-events` and the `font-family` casing were on that list and are
now modeled in the cascade - see "The cascade models `cursor` and `pointer-events`"
and "A `font-family` computed value keeps the author's spelling" above. Still not
matched: `text-decoration-line` other than `underline`; computed insets on a
positioned box, which Chromium reports as used values and the port reports as the
specified value; and `background-image` gradients, which are re-serialized from the
parsed layer rather than from their source text.
### Alpha is serialized the way Blink spells it, not as `A / 255`

`css_color` in `crates/obscura-render/src/paint.rs` writes a translucent color's
alpha as the raw `a as f32 / 255.0` ratio, so an authored `rgba(4, 67, 211, 0.1)`
read back as `rgba(4, 67, 211, 0.10196079)`. `PaintCssValues.CssAlpha` instead
searches decimals with 0..3 fraction digits and emits the first that quantizes
back to the same byte, which is what Blink's `Color::SerializeAsCSSColor` does.

Storing alpha in 8 bits was never the bug and is not changed: Chromium quantizes
too, and reports `rgba(1, 2, 3, 0.9999)` as the opaque `rgb(1, 2, 3)`. Verified
against headless Chromium for all 256 alpha values; that table is pinned in
`PaintColorTests.AlphaSerializationMatchesChromiumForEveryByte`.

### A whole `background` shorthand layer can be handed to the color parser

`parse_color_for_scheme` reads a color out of the front of whatever string it is
given - the hex and keyword paths take only the first whitespace-delimited token -
but the functional notations did not, so
`background: rgb(255, 255, 255) none repeat scroll 0% 0%` lost its color
entirely and `background: rgba(4, 67, 211, 0.12) none ...` came back *opaque*,
because the alpha component arrived as the unparseable `"0.12)"` and was
silently dropped. That declaration shape is what
`background: var(--x) none repeat scroll 0% 0%` becomes whenever the custom
property resolves to a functional color, which is pervasive in Tesserae.

C# makes the functional parsers strict about their closing paren and adds
`CssColor.ParseBackgroundLayerColor`, which retries a multi-component value by
picking the component that is a color. The retry only applies when every other
component is something a `background` layer may actually contain, so
`background-color: rgb(1, 2, 3) garbage` and `light-dark(red, blue) trailing`
still invalidate the declaration.

### `color(srgb ...)` parses, and reads back as legacy `rgb()`

Neither engine parsed the CSS Color 4 `color()` function, so
`background-color: color(srgb 0.0156863 0.262745 0.827451 / 0.14)` painted
nothing at all. C# parses the `srgb` space (`ParseColorFunction`); wider spaces
still parse as nothing rather than being silently clipped into sRGB.

`RgbaColor` is Rust's `[u8; 4]`, so `getComputedStyle` returned the equivalent
`rgba(4, 67, 211, 0.14)` where Chromium preserves the `color(srgb ...)`
spelling. The spelling is now carried (below); the numbers are still 8-bit.

### A non-legacy sRGB colour serializes as `color(srgb ...)`

Chromium keeps the space a colour was specified in. The legacy notations - a
hex, a named colour, `rgb()`, `hsl()`, `hwb()` - serialize as
`rgb()`/`rgba()`, while `color(srgb ...)` and a `color-mix()` interpolated in a
space that resolves to sRGB serialize as `color(srgb 0.0156863 0.262745
0.827451 / 0.14)`. `crates/obscura-render` has no notion of a colour's space and
reports every colour as `rgb()`/`rgba()`; that was 25 of the mismatches in one
survey of Curiosity Workspace, all of them Tesserae's `color-mix()` surfaces.

`ComputedStyle.IsSrgbFunctionColor` decides it from the specified text, two
bools on `LayoutStyle` carry it for `color` and `background-color`, and
`PaintCssValues.SrgbFunctionColor` renders it. Two limits, both from the 8-bit
colour model:

- Only the sRGB family is recognised. `lab()`, `oklch()`, `color(display-p3 ...)`
  and a `color-mix(in oklab, ...)` keep their own notation in Chromium and are
  still reported as `rgb()` here.
- The channels are the stored bytes, so a mix of two opaque colours can differ in
  the sixth digit (`color-mix(in srgb, red, blue)` is `0.501961` here against
  Chromium's `0.5`). A mix with `transparent`, which is the Tesserae pattern,
  keeps the other colour's channels exactly.

The flag is per declaration, so a descendant that *inherits* a `color-mix()`
`color` reports it as `rgb()` where Chromium keeps the space. Carrying it would
mean copying the flag beside `Color` in the top-down inheritance pass.

Covered by `ColorMixInSrgbSerializesAsAColorFunction` and
`LegacyColourNotationsStillSerializeAsRgb`.

### A `flex-basis` can be `calc()`, and it resolves against the flex container

`parse_flex_shorthand` splits on plain whitespace, so `flex: 1 1 calc(50% - 6px)`
arrived as three fragments, none of them a basis, and the declaration lost its
basis entirely: the item fell back to its `width` (820px, one per row, where
Chromium lays out two 404px items per row) or, with no width, collapsed to zero.
The longhand did parse, but `dimension_value` flattens the expression against the
initial 16px, so `flex-basis: calc(50% - 6px)` became a 2px basis and reported
`2px`.

The percentage basis of a flex basis is the container's inner main size and is
not known at computed-value time, exactly as for a grid track. So
`ComputedStyle.SetFlexBasis` keeps a percentage-dependent expression whole in
`LayoutStyle.FlexBasisCalc` (a `GridCalcExpression`, taking its font/viewport
context from the same `SetGridCalcContext` call), `TaffyStyleMapping` hands taffy
the calc handle, and the flex algorithm resolves it through the existing
`CalcResolver`. `FlexBasis` itself stays `auto` while that is set, so nothing that
reads the dimension directly sees a flattened value. The computed value reports
the math function, which is what Chromium reports.

This was Tesserae's chat suggestion cards
(`.msk-chat-view-suggestions > .tss-stack-item { flex: 1 1 calc(50% - 6px) }`):
four full-width cards where Chromium lays out four 404px cards two per row.

Covered by `FlexShorthandKeepsAPercentageCalcBasis`,
`FlexBasisLonghandResolvesACalcAgainstTheContainer` and
`FlexBasisWithoutAPercentageComputesToALength`.

### The UA sheet's overflow and colour defaults for controls and replaced boxes

`ua_style` gives neither the form controls nor `canvas`/`video` an `overflow`, and
gives no control a colour. Chromium's UA sheet gives `input` `overflow: clip`,
`textarea` `overflow: auto`, and `canvas`/`video` the same
`overflow: clip; overflow-clip-margin: content-box` an image already had here, so
an input's value and a canvas's children could paint outside their boxes. It also
gives every form control `color: fieldtext`, which is black and does *not*
inherit, and clears the field background on the two controls it paints itself:

- `input` and `textarea` compute `color: rgb(0, 0, 0)` rather than inheriting the
  page's.
- `input[type=checkbox]` / `[type=radio]` compute `background-color:
  rgba(0, 0, 0, 0)`, not the text field's white. Unlike Chromium the engine has no
  native control painter, so an unstyled checkbox now paints as its border alone
  rather than as a white box.
- `input[type=file]` takes its colour back from the page (`color: inherit`) and
  has no field background either. The two per-type rules are in `DomCascade`
  beside the other rules that need the `type` attribute.

Not carried, and still reported wrong: `select` and `button` compute
`color: rgb(0, 0, 0)` and `background-color: rgb(239, 239, 239)` in Chromium,
`select` reports white here and `button` reports transparent; a checkbox's and a
file input's border colour is `currentColor` in Chromium (black, and the page
colour) against the text field's grey here.

Covered by `UserAgentOverflowDefaultsMatchChromium`,
`UserAgentFormControlColoursMatchChromium` and
`AuthorColoursWinOverTheFormControlDefaults`.

### `overflow-clip-margin`, `align-self` and `aspect-ratio` are reported

The CSSOM snapshot omitted all three, so page script read the empty string for
each. `align-self` is `auto` initially and otherwise the alignment keyword;
`overflow-clip-margin` is `0px` unless the UA or the author set it, and is
reported only - an element with `overflow: clip` still clips at its padding box,
so a `content-box` origin or a non-zero margin does not move the clip edge.

`aspect-ratio` cannot be rebuilt from `LayoutStyle.AspectRatio`, which is one
float: Chromium writes both terms out (`1.5` reports as `1.5 / 1`) and keeps
`auto` in front of a ratio (`auto 16 / 9`). `AspectRatioSpecified` carries the
authored text in CSSOM's form. A ratio mapped from an image's `width`/`height`
attributes, or from decoded media, is not the property's computed value and
reports `auto`, as it does in Chromium.

Covered by `SelfAlignmentAndRatioAreReported` and
`OverflowClipMarginReportsTheAuthoredValue`.

### A positioned box reports used insets, and an `auto` minimum reports `0px`

Two more CSSOM values Chromium derives from layout rather than from the
declaration, which the reference reports as specified:

- **Insets.** A positioned box reports its *used* offsets on all four sides. An
  absolutely positioned box is measured off its laid-out margin box against its
  containing block's padding box (`top: 50%` in a 34px containing block reports
  `top: 17px` / `bottom: 7px`), and a relatively positioned one reports the shift
  it was given and the negation of it on the opposite side (`position: relative`
  with no offsets is `0px` on all four). A static box still reports `auto`, and a
  sticky box still reports its specified offsets.
- **Minimum size.** The initial `auto` minimum computes to `0px` except on a flex
  or grid item, where it stays `auto` and means the automatic minimum size.
  Reporting `auto` everywhere told script the box had a minimum it never set.

Both need the element's parent and containing block, which the snapshot did not
have: `PreparedRender.TreeValue` is the tree the layout was prepared from, set in
`PaintPrepare` beside the layout itself. The DOM must not be mutated while a
`PreparedRender` is reused, so the reference is exactly as current as the layout.

The one case not carried is an absolutely positioned box with `margin: auto`,
whose resolved auto margins are not kept on the style, so its used insets are off
by the margin the centring added.

Covered by `RelativeInsetsReportTheUsedOffsets`,
`AutomaticMinimumSizeIsReportedOnlyForFlexAndGridItems` and the widened
`InsetsAreAutoUntilSpecified`.

### A table box reports the initial `flex-direction`

Tables are laid out as internal flex containers
(`LayoutStyle.InternalFlexContainer`, with `flex-direction: column` on `table`,
`thead`, `tbody`, `th` and `td`). That is an implementation detail: CSS gives a
table no flex formatting context, so Chromium reports the initial `row`. The
snapshot already hides the same detail for `display`; `flex-direction` now follows
it, unless the author set the property (`FlexDirectionAuthored`), in which case
the authored value is reported as Chromium reports it. Nothing about how tables
lay out changes.

Still reported wrong on the same boxes, and not part of this change: `display` is
`block` rather than `table`/`table-row-group`/`table-cell`, and `align-items` is
`stretch`/`flex-start` rather than `normal`.

Covered by `TableBoxesReportTheInitialFlexDirection` and
`AnAuthoredFlexDirectionOnATableBoxIsReported`.

### Decoded web faces are cached; the reference re-decodes them

`fetch_and_decode_font` in `crates/obscura-render/src/paint.rs` caches only the
compressed bytes and runs the WOFF decoder on every prepare.
`PaintFonts.FetchAndDecodeFont` memoizes the decoded sfnt in
`RenderResourceCache` instead. The port's WOFF2 path is much slower than
Rust's `wuff`: on a page with three faces, re-decoding was ~600ms of every
prepare, and a prepare runs on the first layout read after any style mutation,
so a settling SPA paid it several times.

The decode is a pure function of the fetched bytes, so this cannot change what
is rendered. Validity is checked by reference equality against the array
`FetchBytes` returns, so a re-fetch or a cache eviction produces a different
array and misses. Bounded at 32 entries.

Not a parity risk: the two engines produce the same fonts, and no test asserts
on decode count.

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
  Obscura's arena tree at parse time and never escapes `PocketCalculator.Dom`. Two behaviors
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
  `PocketCalculator.Net` registers the provider and takes no package reference at all;
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
- **`PocketCalculatorHttpClient` follows redirects by hand and owns cookies.**
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
- **`PocketCalculator.Net` no longer references `PocketCalculator.Dom`.** `crates/obscura-net` has no
  `obscura-dom` dependency; the scaffold's project reference was removed so the
  two areas can be built and tested independently.
- **PocketCalculator.Dom selector-engine differences**, all verified as behavior-preserving:
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
  runtime.** Rust drops the old `Option<PocketCalculatorJsRuntime>` on assignment and the
  isolate goes with it; ClearScript needs an explicit `Dispose`, and a leaked
  `V8ScriptEngine` wedges the process. The property setter therefore has the
  side effect Rust's move already had, which is what makes `init_js`,
  `suspend_js` and `navigate_blank` port line for line.
- **`PageError` is an exception, not a return value.** Every Rust caller of
  `navigate*` branches on `Err`, so `PageException` carries a `PageErrorKind`
  and, for `TooManyClientNavigations`, the limit; the messages are the ones
  `thiserror` renders. Same for `RasterPdfError` -> `RasterPdfException`.
- **A same-document URL change is reported as `Page.navigatedWithinDocument`.**
  Rust's `sync_virtual_url` answers a bare `bool` and `obscura-cdp` turns any
  URL change into `Page.frameNavigated`, which in CDP means a new document: the
  client retires the frame's execution contexts, so the next `Runtime.evaluate`
  fails with "Execution context was destroyed". Driving a single page app whose
  views persist their tab through `history.replaceState` lost 20 of 157 routes
  that way, where Chromium captured all 157. `SyncVirtualUrl` /
  `ProcessPendingNavigationOutcomeAsync` now answer a `PageNavigationOutcome`,
  and `Runtime.evaluate` / `Input.dispatchMouseEvent` emit
  `Page.navigatedWithinDocument` (`navigationType` `fragment` or `historyApi`)
  plus `Target.targetInfoChanged` for a URL change that fetched no document. A
  real document navigation still emits the full `frameNavigated` sequence.
  `Page.navigatedWithinDocument` appears nowhere in `crates/obscura-cdp`, so
  this is a fix rather than a port correction, and the C# half of
  `page_frame_contract` asserts the new event where the Rust test asserts the
  frame. Covered by `SameDocumentNavigationEvents`.
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
- **`PocketCalculator.Browser` takes a direct `SkiaSharp` reference** for the PDF
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

### Bugs found in the layers below PocketCalculator.Browser (not fixed here)

Each of these is pinned by a written-but-skipped test in
`PocketCalculator.Browser.Tests` or `PocketCalculator.Js.Tests`, with the blocker named in the
skip reason.

- **`op_frame_document_ready` records the wrong parent frame.** It reads the
  calling realm from `PocketCalculatorOps.RealmState()`, which resolves
  `RealmStates.Current` - and the runtime only sets that around *synchronous*
  host entries into a realm. `bootstrap.js` calls the op from inside
  `fetch(...).then(...)` in `_loadIframeSrc`, so a frame created by a frame's
  script is queued with `parentFrameId = 0` (the page) instead of its real
  parent. Rust reads the parent from V8's entered-or-microtask context, which is
  correct for an async continuation. Consequences: `window.parent`/`top` in a
  doubly-nested frame point at the page, and the detach sweep cannot discard a
  grandchild when its parent frame is removed. Pinned by
  `PageTests.DetachingAParentDiscardsItsQueuedDescendantWork`.
- **There are two import maps.** `PocketCalculatorJsRuntime` builds
  `new PocketCalculatorModuleLoader(baseUrl, proxyUrl)`, which allocates its own
  `new ImportMap()`, while `op_add_import_map` writes into
  `PocketCalculatorState.ImportMap`. Any import map registered from page JavaScript (a
  script-inserted `<script type="importmap">`) is therefore silently dropped;
  only maps registered through `PocketCalculatorJsRuntime.AddImportMap` (the path `Page`
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

### A dynamically inserted classic script runs as a script, not as an eval

`bootstrap.js` executes a dynamically inserted classic script with
`(0, eval)(source)` twice: on a fetched `script.src` body in `__runDynScriptTask`
and on an inserted element's own text in `__prepareInsertedScript`. Indirect eval
does run in the global scope, but ES semantics confine a *strict* eval's top-level
`var` and `function` declarations to the eval's own variable environment. So any
inserted script whose source begins with `"use strict"` - every bundler prologue,
and the whole `"use strict"; var lib = (() => { ... })();` library shape - loaded,
fired `load`, and published nothing: `globalThis.lib` stayed `undefined` and even
a later `eval('lib')` threw. Chromium evaluates the element as a top-level classic
script, where those declarations create global bindings whatever the strictness.
Parser-inserted scripts were never affected; they go through
`Page.ExecuteClassic` -> `PocketCalculatorJsRuntime.ExecuteScript`, which already compiles
a script.

The fix belongs in the shim, which is shared with Rust and read-only here (rule 1),
so the port rewrites those two call sites on the way into V8
(`BootstrapSource.EngineText`) onto `op_run_classic_script`, a port-added op that
compiles the source as a top-level script in the calling realm. The directive is
left in the source, so the body still runs in strict mode. Rust keeps the eval and
therefore keeps the bug.

Two smaller consequences. A script error now reaches the shim's `catch` as an
`Error` whose message carries the original error's name (`TypeError: boom` rather
than `boom`), which only changes the console text the shim prints; the error is
still caught at the insertion point and the page keeps running. And the third
indirect eval in the shim, the one compiling an inline event-handler attribute, is
deliberately left alone - that source is a function body, not a script.

Pinned by `ClassicScriptScopeTests` (7 facts, including that the bridge applied and
that strict-mode semantics still hold inside the inserted script).

### Resource timing is real, and the C# host is the only one that reports it

`performance.getEntriesByType('resource')` was hard-coded to `[]` in the shim, on
the reasoning that "the host fetches scripts, styles and images without telling
JS, and there is no op exposing them". The first half is true and the second half
was the whole problem: the host does know every subresource it fetched, it just
had no way to say so. Chromium reports 27 resource entries for a normal load of
the Curiosity app and Obscura reported 0, which is visible to any page that sizes
its own payload, reports its own web vitals, or waits on a resource it did not
request itself.

Three port-added ops close it. `op_resource_timings(since_index)` hands over the
records the host appended for the current document; `op_resource_timing_count`
reports how many exist; the shim keeps its own cursor, so a `getEntries()` in a
loop costs one empty array. The producers are the five places the engine fetches
a subresource - `Page.Scripts`, `Page.Stylesheets`, `Page.Capture` (images and
webfonts), `Page.Frames` (frame documents) and `op_fetch_url` (`fetch()`/XHR) -
each recording at its own fetch lambda, which is the only place that brackets the
transport. The Page's existing `RecordNetworkEvent*` could not be reused: it fires
where a resource is *used* (a classic script is recorded as it executes, long
after it arrived), and a resource-timing entry needs the request's own start.

**What is measured and what is zero.** Real: `name`, `initiatorType`,
`responseStatus`, `contentType`, `decodedBodySize`, `startTime`, `fetchStart`,
`responseEnd` and therefore `duration`. `encodedBodySize` is real when
`content-length` survives (it equals `decodedBodySize` otherwise, because
`SocketsHttpHandler` drops `content-length` along with `content-encoding` when it
transparently decompresses, and guessing a ratio would be worse than saying the
body was its own size). `transferSize` is `encodedBodySize + 300`, which is
Chromium's own flat header-overhead placeholder and the convention the navigation
entry already used. `nextHopProtocol` is derived from the scheme, as the
navigation entry already did, because the negotiated ALPN protocol never reaches
JS. Everything else is **0 and deliberately so**: `redirectStart`/`End`,
`workerStart`, `domainLookupStart`/`End`, `connectStart`/`End`,
`secureConnectionStart`, `requestStart`, `responseStart`,
`firstInterimResponseStart`. The transport does not instrument those phases, and
0 is what Resource Timing prescribes for a phase that did not occur or cannot be
reported - so a consumer computing TTFB gets 0, not an invented number.

The buffer lives in the shim, because its size and its clearing are the page's:
`clearResourceTimings()` drops what is buffered without letting it reappear,
`setResourceTimingBufferSize()` sets the maximum without evicting, and
`onresourcetimingbufferfull` fires once when the cap is reached. The host caps its
own list at 1000 records and drops on arrival rather than trimming from the front,
so a cursor never goes stale.

`bootstrap.js` is shared, so all three call sites are guarded by a
`typeof ... === 'function'` test: the Rust engine, which binds none of these ops,
keeps exactly the empty `resource` list it had.

Pinned by `PerformanceTimelineTests` (6 resource facts). Measured live against the
Curiosity app: 80 entries where Chromium reports 50, the difference being the
eager font warm-up described below.

### `document.fonts.check()` answers from the renderer, not from a status nobody sets

`document.fonts.check('13px "Plus Jakarta Sans"')` returned false on a page whose
body text was visibly rendering in that webfont. The font set itself was right -
`document.fonts.size` was 51 and `status` was `"loaded"`, both matching Chromium -
and text measurement was already accurate, so this was a `check()` predicate bug
and nothing else.

The cause: the shim discovers a `FontFace` for every `@font-face` rule in the
page's own stylesheets and constructs it `unloaded`, because a face built from a
CSS source string has not been fetched by *this code*. But the renderer downloads
those sources itself and never told JS, so a CSS-connected face stayed `unloaded`
for the life of the page, and `check()`, which is `all matched faces are loaded`,
answered false. Libraries gate on `check()` to decide whether to wait before
measuring text, so the false negative makes a page loop or mis-measure.

`op_font_resource_loaded(url)` is the renderer's own answer: whether it is holding
usable bytes for that source, i.e. whether text can be shaped with the face right
now. The shim resolves the face's `url()` against `document.baseURI` - the same
resolution the warm-up path used to fetch it - and a CSS-connected face's `status`
getter consults it, latching once loaded. A data-URL source is loaded by
construction and never asks. A script-created `FontFace` is unaffected: it is the
page's to load, and `check()` on one still returns false until `load()` resolves,
which is what Chromium does and what `FontFaceLoadUpdatesStatusSetReadiness`
already pinned.

**Deviation from Chromium, in the safe direction.** Chromium fetches a webfont
only when something uses it, so on the Curiosity app it marks 2 of the 51 faces
loaded and `check('13px Inter')` is false. Obscura eagerly warms every font URL
the page's CSS names (so the first screenshot does not stall on serial font HTTP),
so it genuinely has all 23 and reports them all loaded, and `check('13px Inter')`
is true. That is a true statement about Obscura's renderer rather than a guess,
and it errs towards "go ahead and measure" rather than towards the loop. Narrowing
it would need the renderer's used-font set, which is not worth a new seam.

Rust keeps the old behaviour: it does not bind the op, the shim's guarded call
returns false, and every CSS-connected face stays `unloaded` there.

Pinned by `PerformanceTimelineTests` (3 font facts).

### Computed-style lengths are integers because layout rounds, not because JS does

Chromium reports `getComputedStyle` lengths at LayoutUnit precision (1/64 px):
a `28.4844px` box reads back as `28.4844px`, and `getBoundingClientRect().height`
as `28.484375`. Obscura reports `28px` and `28`. This was checked on the JS side
first and it is **not** there: `op_computed_style` passes the renderer's snapshot
through verbatim, and `PaintCssValues.CssNumber` is `%.6g`, which prints fractions
happily - an inline-block whose used width is fractional still reads back as
`221.4px` today.

The integers come from taffy's layout rounding: `Compute.RoundLayout`
(`PocketCalculator.Render/Layout/Compute.cs`), driven from `TaffyTree.cs`, snaps every
rect to whole pixels, which is what the reference engine does and what Chromium
does not. Fixing it means giving the CSSOM and rect paths an unrounded layout to
read, which is a renderer change; it is recorded here and left alone.

### A fragment navigation keeps the document, and CDP is told so

Four things about same-document navigation were wrong against Chromium, measured side by
side on a trivial page with a `window` marker and `hashchange` / `popstate` counters. All
four are fixed on the C# and shared-shim sides; `crates/` has the same gaps.

- **`Page.navigate` to a URL differing only in the fragment refetched the document.**
  `Page.TryNavigateSameDocumentAsync` (`PocketCalculator.Browser/Page.Navigation.cs`) now asks
  `bootstrap.js` whether the target is same-document and, if so, performs it there: no
  request, the realm and every `window` property intact, `Page.navigatedWithinDocument`
  instead of a `Page.frameNavigated` + load cycle. Both navigate entry points consult it -
  `CdpServer.ProcessWithInterceptionAsync` (the path live `Page.navigate` traffic takes)
  and `Domains.Page.DoNavigateAsync` - and `Page.reload` deliberately does not, because it
  is handed the current URL and means the document literally. The response carries
  **no `loaderId`**: that field is the id of the document the navigation created, and
  Playwright reads it as `newDocumentId` and then waits for a load lifecycle that never
  comes. Chromium omits it here for the same reason.
- **`popstate` never fired.** HTML's "navigate to a fragment" queues `hashchange` (only
  when the fragment moved) and then `popstate`; Obscura fired `hashchange` alone.
  `_fragmentNavigate` in `bootstrap.js` now owns both.
- **`history.pushState` fired a spurious `hashchange`.** `pushState` and `replaceState`
  fire nothing at all, even when the URL they write differs in its fragment. The dispatch
  moved out of them and into the location-driven path, which is where it belongs; a router
  that both pushes state and listens for `hashchange` was routing twice per navigation.
- **Clicking an `<a href="#/x">` did nothing.** Both click paths - `Element.click()` in
  `bootstrap.js` and the CDP mouse path's `MouseReleasedJs` (`PocketCalculator.Cdp/Domains/Input.cs`,
  which mirrors `crates/obscura-cdp/src/domains/input.rs`) - skipped a fragment href
  outright. That was correct back when `location.assign` tore the document down; it now
  means an in-page link, how most single page apps route, was inert.

One supporting detail: an empty fragment is not the same as no fragment, though
`new URL(u).hash` reports `''` for both. `_rawFragment` reads the raw string so
`<a href="#">` from a fragmentless URL fires `hashchange` and a second click does not,
matching Chromium.

Measured on the Curiosity Workspace SPA: walking seven hash routes now costs zero document
requests and keeps one long-lived app instance, with node counts within two of Chromium on
every route. Before, the app rebooted on each.

Pinned by `FragmentNavigationTests` (12 facts, `PocketCalculator.Js.Tests`) and
`SameDocumentNavigationEvents` (6 facts, `PocketCalculator.Cdp.Tests`).

### The SVG viewport is a real viewport, and an `overflow: visible` raster grows right/down

`crates/obscura-render` hands every SVG to resvg, so the reference has no viewport code of
its own to port. The in-tree rasterizer that stands in for it (`SvgRenderer`, see the PORT
NOTE at the top of the file) had three geometry gaps, all found on Curiosity Workspace and
all measured against Chromium 141 on `svg-probe.html` / `imgsvg2-probe.html`:

- **An author `overflow: visible` on an outermost `<svg>` was ignored.** The UA sheet's
  `overflow: hidden` is spelled here as "the pixmap is exactly the CSS viewport", so there
  was nothing for an author declaration to override. `SvgRenderer` now measures the
  content and grows the raster. **The deviation: only the right and bottom overflow is
  recovered.** `PaintDom` blits the raster at the element's border-box origin, so content
  left of or above the viewport has nowhere to land; carrying it needs an origin offset
  out of `PaintDom`, which resvg does not need because it rasterizes the whole page tree.
  Growth is capped at 8192px per side.
- **A nested `<svg>` established no viewport.** Its `x`/`y`/`width`/`height` were dropped
  and its `viewBox` never applied, so its content painted in its own user units at the
  parent's origin: the probe's inner rect covered 60 pixels at [0,0,10,6] where Chromium
  paints 6000 at [50,20,100,60]. `SvgPaintState` now carries the current viewport, which
  is also what a percentage length on a nested `<svg>` resolves against.
- **A `clipPath` ignored its shapes' `transform`.** Every Figma export wraps its artboard
  clip in `transform="translate(...)"`, and dropping it moved the clip to the origin: the
  workspace's empty-search illustration was cut to the top 203 rows of 444.

`<filter>` is the fourth: `feGaussianBlur` now applies through a Skia blur image filter,
including the transparent `feFlood` + normal `feBlend` preamble every Figma export emits
ahead of it. Anything else in a filter still paints unfiltered rather than approximated,
which is the same "skip what usvg cannot represent" rule the rest of the file follows. A
`userSpaceOnUse` filter region is applied as a `ClipRect` as well as the layer bounds,
because Skia treats a layer's bounds as a rasterization hint that an image filter expands
past.

Pinned by `SvgViewportTests` (13 facts, `PocketCalculator.Render.Tests`).

### An SVG shape answers `getBoundingClientRect()`, and the reference has nothing to port

An inline `<svg>` is an atomic replaced box, so its children never become taffy nodes and
`DomLayout.Rects` holds nothing for them. `crates/obscura-render` is in the same position and
does not care: it hands the whole subtree to resvg as one opaque raster, so the reference has
no per-element SVG geometry either. CSSOM View does care - Chromium answers
`getBoundingClientRect()` on an SVG shape with the element's **object bounding box** mapped
through its CTM - and the gap showed up as 366 elements reading `[0, 0, 0, 0]` on one chart
route of the Tesserae sample app (143 `circle`, 116 `text`, 44 `rect`, 43 `line`, 13 `path`,
7 `g`), with no other divergence on that route at all. Charting libraries measure their own
output, so a whole class of app is unmeasurable without it.

`SvgBoxes` (`PocketCalculator.Render/Dom/SvgBoxes.cs`) is a post-layout pass that walks the SVG
rendering tree of every outermost `<svg>` that got a box, resolves each element's user-space
box through the viewport and `transform` chain, and writes document-space rects into
`DomLayout.SvgRects`. `PreparedRender.DocumentRect` / `FragmentSource` fall back to that map.
It is kept **apart from** `Rects` on purpose: an SVG shape has no CSS layout box, so
`ClientSize` answers `(0, 0)` for one rather than its bounding box.

What the walk reproduces, all measured against Chromium 141 on `svgbox-probe.html`:

- The box is the **fill** bounding box: a `stroke-width="3"` horizontal `<line>` is
  zero-height, and a `<path>`'s curves contribute their tight extrema, not their control
  points.
- A container (`g` / `a` / `switch`) unions its children **in its own user space**, before its
  own `transform` - which is also why a `clip-path` does not shrink it and an empty one keeps a
  zero box at that space's origin.
- Nothing outside the SVG rendering model gets a box, and neither does its subtree: `defs`,
  `clipPath`, `mask`, `marker`, `symbol`, `pattern`, the gradients, `filter`, `title`, `desc`,
  `metadata`, and anything under `display: none`.
- A nested `<svg>` reports its own viewport rectangle and re-bases its children on it,
  `viewBox` fitting included.

Three deliberate deviations inside it:

- **Text is one run.** The box is the anchored advance wide and the face's ascent + descent
  tall, both rounded to whole pixels the way Blink normalizes font metrics (11px Liberation
  Sans is 10 above the baseline and 2 below). Per-`tspan` positioning is not modelled, so a
  `<tspan>` gets no box of its own. Measured widths land within 0.5px of Chromium.
- **`<use>` gets no box.** Resolving one means deciding what the referenced element inside
  `<defs>` reports, and nothing in the survey needed it; answering nothing is better than
  answering wrongly.
- **`clientWidth` / `clientHeight` are 0 for every SVG element.** Blink's `<text>` is a
  block-flow underneath and does report a client width there; reporting 0 keeps every SVG
  element consistent instead of reproducing that one internal detail.

`SvgRenderer.ShapePath` was generalized to `(tag, attribute-accessor)` so the raster's parsed
`XElement` document and this pass's live DOM nodes build shape geometry from the same code.

Pinned by `SvgBoxTests` (10 facts, `PocketCalculator.Render.Tests`).

### A text control's intrinsic width comes from the face's metrics, not a fixed em fraction

`dom.rs` sizes a single-line text control as `size * font_size * 0.6 + font_size * 0.675`,
and a textarea as `cols * font_size * 0.6075`. Both are one calibration applied to every
face, so the box is right only for whatever font it was calibrated against.

Chromium reads the face. Reproduced exactly (Chromium 141, Liberation Sans, DejaVu Sans,
Liberation Mono and Plus Jakarta Sans, all loaded as web fonts so both engines rasterize the
same bytes, font sizes 10-20):

```
charWidth = max(avg, round(avg))            avg = OS/2 xAvgCharWidth scaled to the used size
input     = ceil(charWidth * size + max(0, round(maxCharWidth) - charWidth))
textarea  = ceil(charWidth * cols) + 15     15 is the scrollbar gutter
```

`maxCharWidth` is the `head` bounding box's width, which is what Skia reports as
`SkFontMetrics::fMaxCharWidth` - not the widest advance. The `max(avg, round(avg))` is
Blink rounding the metric but never below the real advance: at 13px Liberation Sans measures
7.668 and Chromium multiplies by 8, while at 16px it measures 9.4375 and Chromium multiplies
by 9.4375. A face with no usable OS/2 entry falls back to the advance of `0`, as Blink does.

`FaceMetrics` carries the two new values (`PocketCalculator.Render.Inline`), `TextEngine
.ControlCharacterMetrics` scales them, and `LayoutDomControls` applies the two formulas.

What is left is a font difference, not a formula difference: an `<input>` with no
`font-family` renders in Chromium's system Arial substitute, whose head bounding box is
about 8px wider at 13px than the Liberation Sans this engine embeds, so the two disagree by
that 8px on a control that names no face. With the same file on both sides they agree
exactly.

Pinned by `DomLayoutTests.TextControlSizeAttributeMeasuresTheFaceAverageCharacterWidth` and
`TextareaIntrinsicBoxComesFromRowsAndCols`.

### A form control with a percentage width contributes its size-based width, not its padding

`assign_native_control_size` in `dom.rs` publishes a control's intrinsic box only into an
`auto` width. Every other case keeps the declared width, and for a percentage that no
containing block can resolve there is then nothing left to size the control from: a control
has no child boxes, so it collapsed to its padding. `<input style="width:100%">` reported an
8px max-content width against Chromium's 184, and Curiosity Workspace's own search box
reported 10 against 182 - `.tss-searchbox` and `.tss-textbox` both carry `width: 100%`, so
this was every text field in the product.

A percentage that cannot be resolved behaves as `auto` for an intrinsic contribution (CSS
Sizing 3 5.2.2). The size-based box is now published as the leaf's measured content
(`LayoutStyle.NativeControlContent`, carried to the taffy leaf through `IfcRegistry
.NativeControlContent` and returned by the measure function in `LayoutDomOnce`), which is
used exactly when the percentage cannot resolve and ignored when it can.

The measure answers 0 for a min-content pass, because Chromium contributes the size-based
width to a max-content pass only: measured in Chromium, a `width: 100%` field's max-content
contribution is its full 182 and its min-content contribution is its 10px of padding, so it
must not floor the flex item that holds it.

Pinned by `DomLayoutTests.TextControlWithPercentageWidthStillContributesItsSizeBasedWidth`
and `TextControlSizeDoesNotRaiseAFlexItemAutomaticMinimumSize`.

### An atomic inline with a definite inline size overflows its line instead of shrinking

Our inline formatting context is a wrapping row flex container (`DomBuildCore`), so every
inline-level box in it is a flex item at taffy's default `flex-shrink: 1`. That is right for
an `auto` width - shrinking to the line is exactly CSS 2.1 10.3.9 shrink-to-fit - and wrong
for a definite one: a `width`/`calc()`/percentage that resolves wider than the line is used as
specified and overflows. Measured in Chromium 141 against a 98px content box, an
`inline-block` / `inline-flex` / `inline-grid` / `inline-table` at `width: 200px` is 200 in
every case, `calc(100% + 40px)` is 138 and `150%` is 147; this engine answered 98 for all of
them, and 76 once the box also carried `margin-right: 22px`.

`dom.rs` has no counterpart, so `DomBuild.BuildAny` now zeroes `flex-shrink` on an in-flow
inline-level box whose `width` is not `auto` (or that carries a size expression), alongside
the `vertical-align` -> `align-self` mapping that was already there. Floats and
absolutely-positioned boxes are excluded; they are not line participants.

The user-visible case was the Tesserae dropdown, the widest-spread remaining width defect in
the parity survey (309 divergent element pairs over 32 routes, deltas clustering at -3, -6 and
-7). `.tss-dropdown` carries `width: calc(100% - 16px)` inside a shrink-wrapping flex item, so
the cyclic percentage behaves as `auto` while the item is measured (the item lands on the
dropdown's max-content) and then resolves against that definite result - which in Chromium
overflows the item by the 22px margin it was measured with. On `#/` the dropdown is 123.7 in
Chromium and was 118 here; it is now 124, with its button at 114 against Chromium's 113.7.

Pinned by `AtomicInlineSizingTests`.

Still short of Chromium: a *bare* cyclic `width: 100%` on such a box. `width: 100%` plus
`margin-right: 22px` inside a shrink-wrapping flex item is 139.7 in Chromium and 118 here,
because `DeferCyclicFlexInlineSizes` neutralizes that one to `Auto` before the build, so the
box no longer looks definite when `BuildAny` runs, and `RestoreTypedPercentages` puts the
percentage back without revisiting `flex-shrink`. The `calc()` form is unaffected because its
`SizeExpressions[0]` survives the neutralization.

### A classic scrollbar takes space out of its scroll container

`crates/obscura-render` reserves a scrollbar gutter only out of the initial containing block, and
this port added `scrollbar-gutter: stable` on top of that (see the entry above). Neither reserves
anything for the scrollbar a box actually shows. Headless Chromium driven over CDP draws **classic**
(non-overlay) scrollbars, so it does: a vertical scrollbar narrows the scrollport by its thickness
and a horizontal one shortens it. The reason this was never noticed is that Playwright launches
every browser with `--hide-scrollbars`, so a probe taken through `chromium.launch()` reports no
reservation at all - measure this against a browser started by hand.

Curiosity Workspace sets `::-webkit-scrollbar { width: 9px; height: 9px }` page-wide, so on
`#/manage/search/settings` the settings rows inside the scrolling pane came out 1037 against
Chromium's 1028, and every right-aligned control in them 9px too far right - 403 of that route's
870 strictly-aligned pairs carried exactly +9px of x offset, 218 origin boxes over 12 routes.

Three pieces:

- **`::-webkit-scrollbar` is indexed like the other pseudo-elements** (`Stylesheet.ScrollbarRules`,
  a fifth `PseudoRuleMap` beside before/after/placeholder/slider-thumb). Only the box's own
  `width`, `height` and `display: none` are read back off it, into
  `LayoutStyle.ScrollbarPseudoWidth`/`Height`; the track and thumb sub-pseudos are decoration and
  are not indexed at all. The cascade builds it only for a box that is already a scroll container,
  which is what keeps a page-wide rule off the hot path. `TryPushPseudo` gained
  `universalWhenBare`, because a pseudo-element written with no originating compound is
  `*::-webkit-scrollbar` and `StripPseudoElement` leaves a stray colon for that shape - the flag is
  set for this map only, since making ::before/::after universal-when-bare would change what rules
  they match.
- **`LayoutStyle.ScrollbarThickness(vertical)`** resolves the custom width, then
  `scrollbar-width` (`none` 0, `thin` 10, otherwise the classic 15). `scrollbar-gutter: stable`
  now sizes its gutter through the same helper, so a custom scrollbar sizes that too.
- **`DomScrollbarPasses.ApplyScrollbarGutters`** decides what is actually reserved.
  `overflow: scroll` always shows a scrollbar; `overflow: auto` shows one only when the content
  overflows, and that is knowable only from a completed layout - so the pass runs in `LayoutDomOnce`
  after every intrinsic, table and fragmentation repair, for the same reason the static-position
  harvest does, and re-lays out. Running it after the *first* layout instead is wrong and was tried:
  the app's `.tss-segmentedpivot-content` panes overflow by a few pixels in the provisional layout
  and then do not, so they grew scrollbars Chromium does not show. What it reserves is parked in
  `ReservedScrollbarX`/`Y`, read by `TaffyStyleMapping` (which marks the axis `Overflow.Scroll`, the
  one taffy reservation shape) and subtracted by `PreparedRender.ClientSize` and the scroll tree, so
  `clientWidth`/`clientHeight` report the scrollport. Reservation only ever grows - narrowing a box
  can make its content overflow harder, never less - so the loop terminates; two rounds cover a
  vertical bar narrowing a box into needing a horizontal one. A retained style carries its
  reservation into the next layout, so `ResetScrollbarGutters` clears it before the tree is built.

taffy carries one thickness for both axes, so a `::-webkit-scrollbar` that sets `width` and
`height` to different values reserves the larger on both; no sheet seen here does that.

An `auto` axis is called overflowing at more than 1px, not at any positive amount. Chromium decides
it in LayoutUnits, but this engine's text metrics differ from Chromium's by a fraction of a pixel
per line, and a whole-pixel tolerance is what stops that inventing a scrollbar - and 9 or 15px of
width error with it.

Not covered: the document's own scrollbar still does not come out of the initial containing block
unless `scrollbar-gutter` asks for it, and `--hide-scrollbars` is not a flag this engine reads.

Pinned by `DomLayoutTests.AClassicScrollbarIsReservedByAnOverflowingScrollContainer` and
`ACustomWebkitScrollbarSizesTheReservedScrollbar`, both asserting values measured in Chromium 141.
`AStableScrollbarGutterIsReservedOnANestedScrollContainer` was corrected with them: it asserted that
`overflow-y: scroll` with no `scrollbar-gutter` reserves nothing, which is only true under
`--hide-scrollbars`.

#### ...and the gutter relayout re-resolves the calc() sizes below it

Reserving the gutter narrows the scroll container, and the tree is laid out again - but by then
`DomSubgridPasses.ResolveFunctionalInlineSizes` has already flattened every cyclic-flex
`calc()`/`min()`/`max()`/`clamp()` inline size to a **px length** taken off its parent's content
box. A bare percentage under the same container re-resolves on its own, because
`RestoreTypedPercentages` hands it to taffy typed and taffy resolves it against the used
containing block; the flattened expression has nothing left to re-resolve. So on the Tesserae
sample app's `#/view/Searchable List` the scroll container came out right at 1117 and
`.tss-card` (`width: calc(100% - 4px)`) kept 1118 - the value it had against the 1126 the
container was before the gutter - where Chromium says 1109. 4,332 of that survey's 6,001
strictly-aligned pairs differed in width, 4,217 of them by exactly +9px.

`DomSubgridPasses.ReresolveFunctionalInlineSizes` is the same rank-ordered resolution run again
against the geometry the tree has now, called from the gutter loop in `LayoutDomOnce` after each
reserving relayout. It is cheap when there is nothing to do: an entry whose slot already holds
exactly the length it would write is skipped, and a rank group that wrote nothing does not
reflow, so a page with no reservation pays one walk of the deferred list and no layout.

The loop is one iteration longer (three, was two) because the re-resolution is a feedback edge:
a narrower container is a narrower basis, which can widen or narrow a descendant, which can make
a *different* box overflow. It still terminates on the same argument as before - a reservation
only ever grows, and is capped per axis at that box's scrollbar thickness, so
`ApplyScrollbarGutters` can answer "changed" at most twice per box however the sizes below it
move. Instrumented over 200 layouts on 15 Tesserae routes: round one every time, round two in 38
of 200, round three never. The extra slot is headroom, not a working iteration.

Over the 101-route Tesserae survey against Chromium, mean absolute width error on strictly
aligned pairs went 5.6680 -> 5.0704, pairs off by more than 2px 16,430 -> 2,953, and pairs at
exactly +9px 13,629 -> 449; 75 routes improved, 22 unchanged, 4 regressed by 0.13 to 0.25. Those
four are an existing defect made more visible: they each have a `.tss-stack` that Obscura already
reserved a gutter out of and Chromium does not, so the descendants now correctly follow a
container width that is itself wrong. That belongs to the overflow tolerance above, not here.

**Not fixed with it: a pinned flex item keeps its pre-gutter width.** `PinFlexItems` writes the
item's *used* width as a definite length, and that happens before the gutter too. When the row
flex container is inside the scroll container rather than above it, the item stays at its
pre-gutter width and its descendants follow (measured on a reduction: item 400 against Chromium's
391). Correcting it means un-pinning and re-running the whole deferred cyclic resolution after
the gutter, which is the ordering F34 records as wrong, so it is left. On the Tesserae routes
that showed this defect the pinned item is above the scroll container, which is why the card is
right there.

Pinned by `DomLayoutTests.AReservedScrollbarReResolvesFunctionalWidthsBelowIt`, which carries the
non-scrolling control in the same fact: the same subtree in an `overflow: hidden` box must keep
the wider pre-gutter numbers.

### `nan` and `infinity` are identifiers in CSS, so a length spelling one is invalid

`css.rs` parses a numeric token with `str::parse::<f32>()`, and the port's
`CssNumber.TryParseFloat` reproduced that faithfully - it even added explicit `inf` /
`infinity` / `nan` branches because Rust accepts those spellings and .NET spells them
differently. CSS has no such token: a `<number-token>` is digits (CSS Syntax 3 4.3.3) and
`nan` or `infinity` tokenizes as an identifier, so Chromium rejects `left: NaN%` /
`top: Infinitypx` as an invalid declaration and the box keeps its static position.

Accepting them let a non-finite number reach layout, where it poisons the box for good.
`masonry-layout` is constructed before its element is in the document, so its first
measuring pass has no container width and no items and computes `columnWidth` from the
option *string*; every item it has already adopted is then written
`left: NaN%; top: Infinitypx; transform: translate3d(0px, -Infinitypx, 0)`. Chromium drops
all three, the items stay measurable, and the real pass 16 ms later lays them out. Here the
declarations stuck, the items laid out nowhere and measured 0x0, and each later pass
recomputed NaN from that - so on `#/view/Masonry` of the Tesserae sample app
`div.tss-masonry` was `1084 x 0` against Chromium's `1084 x 7340` and all 50
`.tss-masonry-item` children were `[0, 0, 0, 0]` (round-3 finding F36).

`CssNumber.TryParseFloat` now requires the first character after an optional sign to be a
digit or `.`, which is the whole CSS number grammar's entry condition. A finite number that
*overflows* to infinity (`1e400`) still parses, matching Rust; only the spelling is refused.
The whole port funnels through this one helper (`StylePrimitives.ParseF32`, `CssLength`,
`CssColor`, keyframe offsets, container queries), so nothing else needed changing.

Not fixed, and deliberately: the CSSOM still *stores* the invalid declaration, so
`getAttribute('style')` keeps `left: NaN%` where Chromium's string never contains it. That
validation lives in `bootstrap.js`, which is shared verbatim with the Rust engine and has no
value grammar at all; a per-property validator there is a much larger piece of work and buys
nothing observable in layout, which now matches.

Pinned by `CssTests.ANonNumericTokenIsNotACssNumber` / `ACssNumberStillParses` /
`ANonFiniteLengthDoesNotResolve` and `DomLayoutTests.ANonFiniteInsetIsAnInvalidDeclaration`.

### A table's used width is floored by its min-content width, and an inline-block does block layout

Three related places where Obscura squeezed content that Chromium lets overflow. All three
show up together on the Tesserae `#/view/Code Diff` route (F38 in
`dotnet/docs/round3-findings.md`), where the diff table sat at its container's 1073px and
every code line wrapped, giving `d2h-code-wrapper` heights of 13,920 and 14,794 against
Chromium's 836.

**1. A percentage-width table.** `crates/obscura-render/src/dom.rs` skips such a table in the
table used-width pass ("A percentage-width table resolves against its container, so leave
taffy's percentage handling in place") and taffy then resolves `width: 100%` and stops there.
CSS 2.1 17.5.2 makes the used width the *greater* of the specified width and what the columns
need, so a percentage that resolves narrower than the content has to overflow instead. C#
keeps taffy's percentage resolution and adds a `min-width` floor of the table's min-content
width (`ApplyTableUsedWidths` in `Dom/LayoutDomControls.cs`).

**2. Those intrinsic measurements ran through neutralized percentages.**
`DeferCyclicFlexInlineSizes` flattens a cyclic percentage inline size to a definite `0px`
before the box tree is built, so every `width: 100%` box *inside* a table reads as zero-wide
when the table's own min-content is measured - the table then reports the width of whatever
is not percentage-sized. The restore-measure-undo that `ApplyDeferredFlexAutomaticMinimums`
already did for flex items is now the shared
`DomSubgridPasses.EnterTypedPercentageScope` / `ExitTypedPercentageScope`, and the table pass
measures inside one (excluding the table nodes themselves, so the used widths it writes
survive the exit). The same scope is what lets the neutralized width be recognised as the
percentage it was authored as, rather than as `width: 0`.

**3. A definite-width inline-block shrank its block children.** Taffy's stand-in for an
inline box is a wrapping flex row, and the reference keeps it for any inline-block that is not
auto-width. A block-level child is then a flex item, and flexbox's automatic minimum size is
the *content* size suggestion - zero for a box that carries its width itself - so a
`width: 461px` child of a `width: 100%` inline-block was shrunk to the inline-block's 400.
Chromium lays an inline-block's contents out in a block formatting context, where the child
overflows. `DomBuildCore` now gives a definite-width inline-block whose in-flow children are
all block-level the same real block layout `display: block` already gets. An auto-width
inline-block keeps the stand-in, because its shrink-to-fit width is measured from it.

Pinned by `DomLayoutTests.PercentageWidthTableIsFlooredByItsMinContentWidth`,
`PercentageWidthTableThatFitsKeepsItsContainingBlockWidth`,
`DefiniteWidthInlineBlockDoesNotShrinkItsBlockChildren` and
`TableInAScrollableBoxTakesItsContentWidth`.
