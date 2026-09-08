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

## 1. Obscura.Dom  (<- crates/obscura-dom, ~5.2k lines)

- [~] `tree.rs` -> `DomTree`, `Node`, `NodeId`, `NodeData`, shadow roots, slots (1640)
- [~] `tree_sink.rs` -> HTML parsing via AngleSharp adapted into the arena tree (698)
- [~] `selector.rs` -> selector parsing, matching, specificity (1252)
- [~] `serialize.rs` -> `innerHTML` / `outerHTML` serialization (331)
- [ ] Unit tests ported
- [ ] Parity: parse + serialize a corpus through both engines

## 2. Obscura.Net  (<- crates/obscura-net, ~5.6k lines)

- [~] `encoding.rs` -> charset detection and transcoding (429)
- [~] `cookies.rs` -> `CookieJar`, parsing, domain/path matching, persistence (1281)
- [~] `robots.rs` -> robots.txt fetch/cache/match (172)
- [ ] `blocklist.rs` + `pgl_domains.txt` -> tracker blocklist (77)
- [~] `client.rs` -> HTTP client, redirects, SSRF gate, decompression (1747)
- [~] `interceptor.rs` -> request interception types (15)
- [ ] `wreq_client.rs` -> stealth transport (710) **deferred, see Known deviations**
- [ ] Unit tests ported
- [ ] Parity: cookie jar and SSRF decisions over a shared fixture table

## 3. Obscura.Js  (<- crates/obscura-js, ~44k lines; 15.8k of it is shared JS)

- [x] Share `bootstrap.js` with the Rust tree by linking it as an embedded resource
- [ ] `runtime.rs` -> `ObscuraJsRuntime` on ClearScript: isolate lifetime, realms, watchdog (3704)
- [ ] `ops.rs` -> the 52 ops (3101)
  - [ ] `op_dom` command dispatcher (~90 commands)
  - [x] crypto ops (`digest`, `hmac`, `aes-gcm/cbc/ctr`, `pbkdf2`, `hkdf`, `random_bytes`) - 30 tests green
  - [~] URL ops (`parse`, `set`, `resolve`, `encode_query`) - needs a real WHATWG parser, `System.Uri` is not spec-compliant
  - [ ] fetch / network ops
  - [ ] render-facing ops (layout geometry, computed style, canvas, image metadata, WAAPI)
- [ ] `frame.rs` -> child-frame realms (1352)
- [ ] `module_loader.rs` + `import_map.rs` -> ES module loading (783)
- [ ] `write_stream.rs`, `markdown.rs`, `v8_flags.rs`, `cdp_watchdog.rs` (461)
- [ ] Unit + integration tests ported
- [ ] Parity: run the same scripts through both runtimes, compare results

## 4. Obscura.Render  (<- crates/obscura-render + vendor/taffy, ~100k lines)

The largest component. Split into stages; each stage is independently testable.

- [~] `css.rs` -> CSS tokenizer, parser, values, at-rules (4431)
- [ ] `style.rs` -> cascade, specificity, inheritance, computed style (8615)
- [ ] `vendor/taffy` -> layout algorithms: block, flexbox, grid (20520)
- [ ] `dom.rs` -> render tree construction, fragmentation, scrolling, geometry (14769)
- [ ] `inline.rs` -> line breaking, text shaping, bidi, inline layout (3200)
- [ ] `paint.rs` -> rasterization onto Skia: fills, strokes, images, SVG, canvas, effects (9721)
- [ ] `border.rs` -> border and outline painting (452)
- [ ] `lib.rs` -> the public render API, screenshots, animation sampling (2914)
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
- **`set_v8_flags` maps flags onto typed constraints instead of passing a flag
  string to V8.** ClearScript does not expose `v8::V8::set_flags_from_string`;
  it surfaces the same settings as `V8RuntimeConstraints` properties and a small
  `V8GlobalFlags` enum. The heap-sizing flags an embedder actually uses
  (`--max-old-space-size`, `--max-semi-space-size`, `--max-young-generation-size`)
  and a few global toggles map across; anything else is reported through
  `V8Flags.Warned` and ignored rather than silently dropped. The late-call
  refusal is preserved exactly, because a late flag call aborts the process.
- **`System.Text.Encoding.CodePages` is an approved managed dependency.**
  .NET Core ships only UTF-8/16/32, ASCII and Latin-1 in box, and
  `encoding.rs` needs the whole WHATWG legacy set (GBK, Big5, Shift_JIS,
  EUC-JP/KR, windows-125x, ISO-8859-x). The package is Microsoft-published
  managed IL with no native component, so it does not widen the native set.
- **One process, many isolates.** ClearScript allows multiple V8 isolates per
  process, so the Rust "one isolate per process" constraint (and the
  process-per-test requirement) does not apply. Tests run in-process.
