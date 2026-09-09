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

- **Two Obscura.Js tests are timing-flaky, and not only under solution load.**
  830 of 849 is the clean result, but three consecutive runs of that project
  alone gave 2 failures, then 1, then 0, so the earlier note that they pass in
  isolation was optimistic. The pair that showed up:
  `OpsTests.Read_body_capped_rejects_oversized_streamed_body` and
  `RuntimeTests.ParserImagesLoadConcurrentlyWithoutBlockingTheEventLoop`. The
  first prepared render on a fresh process costs ~300ms in embedded font
  initialization against ~1ms once warm, so tests that schedule work tens of
  milliseconds apart collapse two events into one when the host is busy. Fix the
  latency rather than the tests.

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

## Known deviations

Recorded as they are decided. Each entry needs a reason and a tracking note.

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
  are flushed, which skips the `atexit` chain the crash lives in: 0 of 150.
  `Environment.Exit` does not help (26 of 150) because it still runs that chain.
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
