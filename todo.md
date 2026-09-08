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

## 1. Obscura.Dom  (<- crates/obscura-dom, ~5.2k lines)  -  81/81 tests green  -  81/81 tests green

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
- [ ] `runtime.rs` -> `ObscuraJsRuntime` on ClearScript: isolate lifetime, realms, watchdog (3704)
- [ ] `ops.rs` -> the 52 ops (3101)
  - [ ] `op_dom` command dispatcher (~90 commands)
  - [x] crypto ops (`digest`, `hmac`, `aes-gcm/cbc/ctr`, `pbkdf2`, `hkdf`, `random_bytes`) - 30 tests green
  - [x] URL ops (`parse`, `set`, `resolve`, `encode_query`) - WHATWG parser
        written in tree with Punycode, diffed against the real Rust `url` crate
        over 51,038 cases with 0 mismatches on the curated set
  - [ ] fetch / network ops
  - [ ] render-facing ops (layout geometry, computed style, canvas, image metadata, WAAPI)
- [ ] `frame.rs` -> child-frame realms (1352)
- [ ] `module_loader.rs` + `import_map.rs` -> ES module loading (783)
- [ ] `write_stream.rs`, `markdown.rs`, `v8_flags.rs`, `cdp_watchdog.rs` (461)
- [ ] Unit + integration tests ported
- [ ] Parity: run the same scripts through both runtimes, compare results

## 4. Obscura.Render  (<- crates/obscura-render + vendor/taffy, ~100k lines)

The largest component. Split into stages; each stage is independently testable.

- [x] `css.rs` -> CSS tokenizer, parser, values, at-rules, indexed cascade,
      container-query evaluator and animation sampler (4431) - 79 tests, 2
      skipped (one is `#[ignore]` in Rust, one needs `dom.rs`)
- [ ] `style.rs` -> cascade, specificity, inheritance, computed style (8615)
- [x] `vendor/taffy` -> layout algorithms: block, flexbox, grid (20520) - 116
      tests green, including the vendored grid shrink-to-fit correction, which
      2 ported tests pin (stubbing the fix turns them red)
- [ ] `dom.rs` -> render tree construction, fragmentation, scrolling, geometry (14769)
- [ ] `inline.rs` -> line breaking, text shaping, bidi, inline layout (3200)
- [ ] `paint.rs` -> rasterization onto Skia: fills, strokes, images, SVG, canvas, effects (9721)
- [x] `border.rs` -> border and outline painting (452) - ported inside
      `Core/Border.cs`, because `LayoutStyle.BorderModel`/`.Outline` are fields
      of these types and could not be stubbed. Its 3 tests are green.
- [x] `lib.rs` -> core types: `LayoutStyle` (189 fields), geometry, `Affine2`,
      `Dimension`, style enums, background, generated content, animation,
      capture limits, and the `LayoutStyle`->taffy style mapping (2914)
- [ ] Fonts: embedded font assets + Skia/HarfBuzz typeface and shaping integration
- [ ] Unit tests ported (`layout_test.rs`)
- [ ] Parity: render `render-repros/**` fixtures in both engines and compare

## 5. Obscura.Browser  (<- crates/obscura-browser, ~9.8k lines)

- [ ] `page.rs` -> `Page`: navigation, evaluation, waiting, interception (4591)
- [ ] `context.rs` -> `BrowserContext` (269)
- [ ] `lifecycle.rs`, `profiles.rs`, `fork_virtual_url.rs` (188)
- [ ] `pdf.rs` -> raster PDF export (1027)
- [ ] Integration tests ported
- [ ] Parity: navigate a fixture corpus, compare DOM + text + links

## 6. Obscura.Cdp  (<- crates/obscura-cdp, ~12.7k lines)

- [ ] `server.rs` -> WebSocket server, sessions, targets (1730)
- [ ] `dispatch.rs` -> method routing (1273)
- [ ] `types.rs`, `util.rs`, `cookie_params.rs` (440)
- [ ] domains: `page` (3179), `runtime` (835), `dom` (788), `target` (540),
      `pdf` (535), `accessibility` (524), `emulation` (456), `input` (438),
      `network` (430), `domsnapshot` (416), `io` (283), `fetch` (224),
      `storage` (124), `browser` (42), `lp` (21)
- [ ] Integration tests ported (27 files)
- [ ] Parity: drive both servers with the same CDP script, diff the messages

## 7. Obscura.Mcp  (<- crates/obscura-mcp, ~3.2k lines)

- [ ] `lib.rs` -> stdio MCP server and tools (2077)
- [ ] `http.rs` -> HTTP/SSE transport (462)
- [ ] Integration tests ported
- [ ] Parity: identical tool listings and tool-call results

## 8. Obscura.Cli + Obscura  (<- crates/obscura-cli, crates/obscura, ~6k lines)

- [ ] `main.rs` -> `fetch`, `serve`, `scrape`, `mcp`, global flags (1946)
- [ ] `worker.rs` -> the `obscura-worker` binary for parallel scrape (165)
- [ ] `crates/obscura` -> embeddable library API (`Obscura` project) (2224)
- [ ] Integration tests ported
- [ ] Parity: CLI golden-output tests for every `--dump` mode

## 9. Validation

- [ ] `Obscura.Parity.Tests` harness: runs a case through both binaries and diffs
- [ ] Port `render-repros/run.sh` to drive the C# binary
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
- **One process, many isolates.** ClearScript allows multiple V8 isolates per
  process, so the Rust "one isolate per process" constraint (and the
  process-per-test requirement) does not apply. Tests run in-process.
