# Obscura C# port - status and work queue

Target: `net10.0`. Reference implementation: the Rust workspace in `crates/`.
Rules and conventions: see `CLAUDE.md`.

Legend: `[ ]` not started, `[~]` in progress, `[x]` ported + tests green,
`[P]` parity-validated against the Rust binary.

## 0. Setup

- [x] Review the Rust source and map the architecture
- [x] Write `CLAUDE.md`
- [x] Write `todo.md`
- [x] Disable CI/CD (`.github/workflows/*.yml.disabled`, `scripts/ci/*.py.disabled`, `Dockerfile.disabled`)
- [x] Adapt `skills/obscura/SKILL.md` for the port; add `skills/obscura-port/`
- [x] Scaffold `dotnet/` (solution, `Directory.Build.props`, `Directory.Packages.props`, projects)
- [x] Pin the dependency set; confirm V8 is the only native dependency
- [x] `dotnet/docs/op-protocol.md` - the frozen `bootstrap.js` <-> host contract

## 1. Obscura.Dom  (<- crates/obscura-dom, ~5.2k lines)

- [ ] `tree.rs` -> `DomTree`, `Node`, `NodeId`, `NodeData`, shadow roots, slots (2345)
- [ ] `tree_sink.rs` -> HTML parsing via AngleSharp adapted into the arena tree (698)
- [ ] `selector.rs` -> selector parsing, matching, specificity (1824)
- [ ] `serialize.rs` -> `innerHTML` / `outerHTML` serialization (331)
- [ ] Unit tests ported
- [ ] Parity: parse + serialize a corpus through both engines

## 2. Obscura.Net  (<- crates/obscura-net, ~5.6k lines)

- [ ] `encoding.rs` -> charset detection and transcoding (429)
- [ ] `cookies.rs` -> `CookieJar`, parsing, domain/path matching, persistence (1281)
- [ ] `robots.rs` -> robots.txt fetch/cache/match (172)
- [ ] `blocklist.rs` + `pgl_domains.txt` -> tracker blocklist (77)
- [ ] `client.rs` -> HTTP client, redirects, SSRF gate, decompression (2847)
- [ ] `interceptor.rs` -> request interception types (15)
- [ ] `wreq_client.rs` -> stealth transport (710) **deferred, see Known deviations**
- [ ] Unit tests ported
- [ ] Parity: cookie jar and SSRF decisions over a shared fixture table

## 3. Obscura.Js  (<- crates/obscura-js, ~44k lines; 15.8k of it is shared JS)

- [ ] Vendor `bootstrap.js` unchanged + staleness check in the build
- [ ] `runtime.rs` -> `ObscuraJsRuntime` on ClearScript: isolate lifetime, realms, watchdog (19055)
- [ ] `ops.rs` -> the ~53 ops (6169)
  - [ ] `op_dom` command dispatcher (~90 commands)
  - [ ] crypto ops (`digest`, `hmac`, `aes-gcm/cbc/ctr`, `pbkdf2`, `hkdf`, `random_bytes`)
  - [ ] URL ops (`parse`, `set`, `resolve`, `encode_query`)
  - [ ] fetch / network ops
  - [ ] render-facing ops (layout geometry, computed style, canvas, image metadata, WAAPI)
- [ ] `frame.rs` -> child-frame realms (1352)
- [ ] `module_loader.rs` + `import_map.rs` -> ES module loading (783)
- [ ] `write_stream.rs`, `markdown.rs`, `v8_flags.rs`, `cdp_watchdog.rs` (461)
- [ ] Unit + integration tests ported
- [ ] Parity: run the same scripts through both runtimes, compare results

## 4. Obscura.Render  (<- crates/obscura-render + vendor/taffy, ~100k lines)

The largest component. Split into stages; each stage is independently testable.

- [ ] `css.rs` -> CSS tokenizer, parser, values, at-rules (10350)
- [ ] `style.rs` -> cascade, specificity, inheritance, computed style (10956)
- [ ] `vendor/taffy` -> layout algorithms: block, flexbox, grid (33543)
- [ ] `dom.rs` -> render tree construction, fragmentation, scrolling, geometry (20660)
- [ ] `inline.rs` -> line breaking, text shaping, bidi, inline layout (4983)
- [ ] `paint.rs` -> rasterization onto Skia: fills, strokes, images, SVG, canvas, effects (17219)
- [ ] `border.rs` -> border and outline painting (452)
- [ ] `lib.rs` -> the public render API, screenshots, animation sampling (2914)
- [ ] Fonts: embedded font assets + Skia/HarfBuzz typeface and shaping integration
- [ ] Unit tests ported (`layout_test.rs`)
- [ ] Parity: render `render-repros/**` fixtures in both engines and compare

## 5. Obscura.Browser  (<- crates/obscura-browser, ~9.8k lines)

- [ ] `page.rs` -> `Page`: navigation, evaluation, waiting, interception (8026)
- [ ] `context.rs` -> `BrowserContext` (269)
- [ ] `lifecycle.rs`, `profiles.rs`, `fork_virtual_url.rs` (188)
- [ ] `pdf.rs` -> raster PDF export (1027)
- [ ] Integration tests ported
- [ ] Parity: navigate a fixture corpus, compare DOM + text + links

## 6. Obscura.Cdp  (<- crates/obscura-cdp, ~12.7k lines)

- [ ] `server.rs` -> WebSocket server, sessions, targets (2125)
- [ ] `dispatch.rs` -> method routing (1273)
- [ ] `types.rs`, `util.rs`, `cookie_params.rs` (440)
- [ ] domains: `page` (3179), `runtime` (835), `dom` (788), `target` (540),
      `pdf` (535), `accessibility` (524), `emulation` (456), `input` (438),
      `network` (430), `domsnapshot` (416), `io` (283), `fetch` (224),
      `storage` (124), `browser` (42), `lp` (21)
- [ ] Integration tests ported (27 files)
- [ ] Parity: drive both servers with the same CDP script, diff the messages

## 7. Obscura.Mcp  (<- crates/obscura-mcp, ~3.2k lines)

- [ ] `lib.rs` -> stdio MCP server and tools (2401)
- [ ] `http.rs` -> HTTP/SSE transport (462)
- [ ] Integration tests ported
- [ ] Parity: identical tool listings and tool-call results

## 8. Obscura.Cli + Obscura  (<- crates/obscura-cli, crates/obscura, ~6k lines)

- [ ] `main.rs` -> `fetch`, `serve`, `scrape`, `mcp`, global flags (2672)
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
  Obscura's arena tree at parse time and never escapes `Obscura.Dom`.
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
- **One process, many isolates.** ClearScript allows multiple V8 isolates per
  process, so the Rust "one isolate per process" constraint (and the
  process-per-test requirement) does not apply. Tests run in-process.
