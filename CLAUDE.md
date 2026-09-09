# CLAUDE.md

Guidance for AI coding agents working on the **C# / .NET 10 port of Obscura**.

Obscura is a headless browser engine: it runs real JavaScript through V8, keeps
a real DOM tree, owns its layout and paint pipeline, speaks the Chrome DevTools
Protocol, and is a drop-in replacement for headless Chrome with Puppeteer and
Playwright. The reference implementation is the Rust workspace in `crates/`.
This repository is being ported to C# under `dotnet/`.

Read `todo.md` for the live port status and the ordered work queue.

## Ground rules for the port

1. **The Rust tree in `crates/` is the specification, and it is read-only.**
   It stays in the repo during the port and is the authority on behavior. When
   C# and Rust disagree, Rust is right unless the Rust code is provably wrong.
   Do not edit `crates/**` - not to make a C# test pass, and not to carry a fix
   across. Fix C# only.
2. **Where C# deviates from Rust deliberately, say so in a comment at the
   deviation.** Once a bug is fixed on the C# side and not in `crates/**`, the
   two trees no longer agree, and the next reader diffing them needs to know
   which side is intentional. Put a short comment at the C# code that differs:
   what Rust does, what C# does instead, and why (a bug fix against Chromium, a
   platform difference, a deliberate simplification). Record the same thing under
   "Known deviations" in `todo.md`. A deviation with no comment is a defect,
   because the next port pass will "correct" it back to the Rust behavior.
3. **Port behavior, not syntax.** Match observable behavior exactly (the same
   JSON payloads, the same op protocol strings, the same CDP wire messages, the
   same DOM semantics). Write idiomatic modern C#, not transliterated Rust.
4. **Native dependencies are a closed set: V8, Skia, HarfBuzz.** Nothing else.
   - `Microsoft.ClearScript.V8.Native.*` - the JavaScript engine.
   - `SkiaSharp.NativeAssets.*` - rasterization and image codecs. tiny-skia (the
     Rust engine's rasterizer) is itself a port of Skia, so painting onto Skia
     tracks the reference more closely than a hand-written rasterizer would.
   - `HarfBuzzSharp.NativeAssets.*` - complex-script shaping, standing in for
     rustybuzz/cosmic-text.
   Every other dependency must be 100% managed IL. Adding a fourth native
   dependency needs an explicit decision recorded in `todo.md` first. In
   particular: no native TLS stack, which is why stealth TLS impersonation is
   a tracked gap rather than a port target.
5. **`bootstrap.js` is shared, not ported.** `crates/obscura-js/js/bootstrap.js`
   is JavaScript and runs unchanged on the C# side. `Obscura.Js` embeds it by
   linking that exact file (see the `EmbeddedResource` in `Obscura.Js.csproj`),
   never by copying it, so the two engines cannot drift. Fix the shim in place
   and both engines pick the fix up. It reaches V8 through
   `BootstrapLoader.Install`, which installs the `Deno.core` shim first.
6. **The op protocol is a contract.** `bootstrap.js` calls ~53 ops, and `op_dom`
   multiplexes ~90 string commands over `(cmd, arg1, arg2) -> string`. The C#
   implementation must accept and return byte-identical payloads. See
   `dotnet/docs/op-protocol.md`.

## Target and language level

- `net10.0`, `LangVersion` latest, nullable enabled, implicit usings enabled.
- Prefer: file-scoped namespaces, primary constructors, collection expressions,
  `record`/`readonly record struct` for value types, pattern matching over
  `if`-chains, `required` members, `Span<T>`/`ReadOnlySpan<T>` on hot paths.
- Avoid LINQ in per-node and per-glyph hot paths; the Rust engine's performance
  is a hard constraint (see Conventions).
- `Directory.Packages.props` is the single place package versions are declared
  (central package management is on). Never pin a version in a `.csproj`.
- SkiaSharp 4.x paths are immutable: build with `SKPathBuilder` and `Detach()`,
  not the obsolete mutable `SKPath` methods.
- **`MathF` is not `f32`.** Rust's `f32::min`/`f32::max` ignore NaN, while
  `MathF.Min`/`MathF.Max` propagate it, and `f32::round` is half-away-from-zero
  while `MathF.Round` is banker's rounding. Every one of these in the render
  layer must go through `Obscura.Render.F32` instead. This is silent when wrong:
  it produces slightly different geometry rather than an error.
- **Layout rounding is a third thing again.** taffy defines its own
  `round` as `floor(v + 0.5)`, half towards positive infinity. That is neither
  `MathF.Round` (half to even) nor `F32.Round` (half away from zero), and the
  three disagree at negative midpoints: `round(-2.5)` is -2 for taffy and -3 for
  `F32.Round`. Layout rounding must use `Obscura.Render.Layout.Sys.Round`.
- **The render layer is `float`, never `double`.** The Rust engine is f32
  throughout, and f64 accumulation diverges visibly in layout and paint. The one
  exception is capture-dimension checking, which is f64 in Rust too.

## Layout

```
dotnet/
  Obscura.slnx                  solution
  Directory.Build.props         shared TFM/analyzer/lang settings
  Directory.Packages.props      central package versions
  src/
    Obscura.Dom/       <- crates/obscura-dom      arena DOM, parsing, selectors, serialization
    Obscura.Net/       <- crates/obscura-net      HTTP, cookies, robots, blocklist, encoding
    Obscura.Js/        <- crates/obscura-js       V8 runtime, ops, bootstrap.js
    Obscura.Render/    <- crates/obscura-render   CSS, computed style, layout, paint
    Obscura.Browser/   <- crates/obscura-browser  Page, navigation, lifecycle, PDF
    Obscura.Cdp/       <- crates/obscura-cdp      CDP server and domains
    Obscura.Mcp/       <- crates/obscura-mcp      MCP tools
    Obscura.Cli/       <- crates/obscura-cli      the `obscura` executable
    Obscura/           <- crates/obscura          embeddable library API
  tests/
    Obscura.<Area>.Tests          xUnit ports of the Rust tests
    Obscura.Parity.Tests          differential tests: C# output vs the Rust binary
  docs/                           port-specific notes (op protocol, dependency map)
```

## Dependency map (Rust crate -> managed .NET)

| Rust | .NET | Notes |
|---|---|---|
| `deno_core` + `v8` | `Microsoft.ClearScript.V8` | Ops are bound onto a plain object exposed as `Deno.core.ops`; `DenoCoreShim` supplies the four non-op `Deno.core` members the shim uses. |
| `html5ever` / `markup5ever` | `AngleSharp` | Used as a spec HTML5 tokenizer/tree builder; its output is adapted into Obscura's arena tree. We do not expose AngleSharp's DOM. |
| `selectors` / `cssparser` | in-tree port | `Obscura.Dom.Selectors`, `Obscura.Render.Css`. Ported, not delegated to AngleSharp, because the cascade needs specificity and matching internals. |
| `taffy` (vendored) | in-tree port | `Obscura.Render.Layout`, including the vendored grid shrink-to-fit fix. |
| `tiny-skia` | `SkiaSharp` | tiny-skia is a port of Skia, so this is the closest available match for path filling, anti-aliasing, and blending. Also supplies the PNG/JPEG/WebP/GIF codecs, so no separate image package is needed. |
| `ab_glyph` / `cosmic-text` (vendored) | `SkiaSharp` + `SkiaSharp.HarfBuzz` | Glyph outlines and metrics from Skia, shaping from HarfBuzz. Carry the vendored variable-font coordinate fix forward. |
| `reqwest` | `System.Net.Http` / `SocketsHttpHandler` | gzip/deflate/brotli are in-box. |
| `wreq` / BoringSSL (stealth transport) | deferred | TLS fingerprint impersonation has no managed equivalent. Stealth's JS/header/identity surfaces port; the TLS-level ClientHello impersonation is tracked as a known gap in `todo.md`. |
| `tokio` | `async`/`await`, `System.Threading.Channels` | |
| `tokio-tungstenite` | `System.Net.WebSockets` + Kestrel | ASP.NET Core ships in the shared framework. |
| `serde_json` | `System.Text.Json` | Source-generated contexts on hot paths. |
| `clap` | `System.CommandLine` | |
| `tracing` | `Microsoft.Extensions.Logging` | |

## Fonts

The engine never uses system fonts. It embeds its own faces (Liberation, DejaVu,
Noto Color Emoji) so rasterization is identical on every host and works on
distroless images with no fontconfig. The C# build links those font files
directly out of `crates/obscura-render/assets/` rather than copying them, so the
two engines can never rasterize against different binaries. Resolve typefaces
with `SKTypeface.FromData` over the embedded resources; never
`SKTypeface.FromFamilyName`.

## Build

```bash
cd dotnet
dotnet build -c Release
dotnet run -c Release --project src/Obscura.Cli -- fetch https://example.com --dump text
```

The Rust reference build (for differential testing) is unchanged:

```bash
CARGO_INCREMENTAL=0 CARGO_BUILD_JOBS=2 cargo build --release -p obscura-cli --bins --features render
```

**Any `cargo test --release -p obscura-cli` without `--features render` rewrites
`target/release/obscura` with a default-features binary.** That binary refuses
`--screenshot` and drops the two render-gated MCP tools, so a parity or
render-compare run after it reports differences that are entirely the build's.
Rebuild the reference after running the Rust tests, or pass `--features render`
to them as well. `scripts/parity-sweep.sh` and `scripts/render-compare.sh` now
probe for this and refuse to run, rather than producing the misleading numbers.

## Test

```bash
cd dotnet
dotnet test -c Release                                  # everything
dotnet test -c Release tests/Obscura.Dom.Tests          # one area
```

- Tests are xUnit v3. Each Rust integration test under `crates/*/tests/` has a
  named counterpart; keep the file name so the mapping stays obvious.
- **V8 isolates:** unlike the Rust engine, ClearScript supports many isolates
  per process, so tests do not need process-per-test. They do need to dispose
  their runtime; a leaked `V8ScriptEngine` will wedge the test host. Use the
  `RuntimeFixture` helper rather than constructing engines ad hoc.
- **Parity tests** (`Obscura.Parity.Tests`) shell out to the Rust binary and
  compare output. They are skipped automatically unless `OBSCURA_RUST_BIN`
  points at a release build. CI-equivalent runs must set it.
- **A deliberate deviation makes parity the wrong assertion for that input.**
  Since `crates/**` is read-only, a bug fixed on the C# side leaves the two
  engines legitimately disagreeing. Do not weaken the fix to keep parity green:
  assert the correct (Chromium) value in an `Obscura.<Area>.Tests` fact instead,
  and if a parity test covers the same input, narrow it and name the deviation
  in the skip/why comment.

## Conventions

- **Performance is a hard constraint.** The Rust engine is ~12x faster and uses
  ~6x less memory than headless Chrome. The C# port is allowed to be slower than
  Rust, but a regression against the Rust numbers is a finding, not a shrug.
  Benchmark old and new revisions interleaved with the same build, page,
  network, viewport, settle policy, and capture path. The noise floor is ~10%.
- **Keep ops exception-safe.** The Rust ops wrap bodies in `catch_unwind` so a
  DOM-op panic returns null instead of aborting inside V8's FFI frame. The C#
  equivalent is the `OpGuard` helper: an exception crossing back into V8 must be
  converted to a JS error or a null return, never allowed to escape.
- **Commits/PRs/comments:** short and factual, no em dashes, no AI filler.

## Text layout: where the port cannot match the reference exactly

`inline.rs` shapes with `cosmic-text` and rasterizes with `swash`. The port uses
HarfBuzz and Skia instead, so it reproduces observable results rather than
internals. These are the known differences, and they are the first place to look
when C# layout drifts from Rust:

- **Line breaking is a subset of UAX#14.** Rust uses `unicode-linebreak`'s
  complete pair table. The port hand-implements LB2-LB8a, LB9-LB12a, LB13-LB19,
  LB21-LB28, LB30a/b. Missing: the LB25 numeric-regex expansion, LB20a, and
  Southeast-Asian dictionary breaking for Thai/Khmer/Lao. Symptom: a wrap one
  word early or late in non-Latin or numeric-heavy text.
- **Bidi is reduced.** No explicit embedding controls (RLE/LRE/PDF), no isolates
  (LRI/RLI/FSI/PDI), no N1/N2 neutral resolution. Pure-LTR text takes an exact
  fast path; mixed-direction paragraphs can reorder differently.
- **Glyph positions match; per-pixel coverage does not.** swash and Skia
  anti-alias differently by a few counts. Treat ink sums as tripwires, never as
  equality assertions.
- **Variable faces carrying `MVAR`** can differ slightly in ascent/descent, and
  therefore baseline position, because the port reads base-face metrics where
  `ttf-parser` applies MVAR deltas.
- **`text-transform: uppercase` omits the Greek iota-subscript block**
  (U+1F80-U+1FFC); polytonic Greek measures narrower than in Rust.
- **Text offsets are UTF-16 code units, not UTF-8 bytes.** This is consistent
  end to end inside the inline layer, but anything crossing into `dom.rs` or
  `paint.rs` that assumes byte offsets must be adapted.
- **Never hand HarfBuzz a blob over managed memory.** Doing so makes shaping
  depend on when the GC runs: the same input intermittently produced different
  glyph selection and advances. Font tables are copied to unmanaged memory with
  `MemoryMode.Duplicate`. Do not undo this.

## Load-bearing invariants (carried over from the Rust engine)

- **DOM mutation arg order:** `insertBefore` / `replaceChild` in `bootstrap.js`
  pass reference-node vs parent nid in a way that is easy to break. If you touch
  mutation ops, verify `before()`, `after()`, `replaceWith()`, and
  `replaceChild()` on connected elements.
- **The reparenting guards in the DOM tree are load-bearing.** `AppendChild` /
  `InsertBefore` reject cycles (inserting an ancestor of the target is a no-op).
  A cyclic reparent made `Descendants()` loop forever and hang the engine.
  Keep the guards and the `Descendants()` length cap.
- **`canAccessOpener` must be in every `TargetInfo` payload**, or strict CDP
  clients (chromiumoxide) panic.
- **Multi-statement `--eval` starting with `const` returns `null`** (V8 gives
  `const` an empty completion value). This is V8 behavior and must be preserved.
- **SSRF:** loopback / RFC1918 / link-local fetches are blocked by default. Use
  `--allow-private-network` (or `OBSCURA_ALLOW_PRIVATE_NETWORK=1`).
- **Watchdog:** synchronous V8 work runs unbounded, so a timeout that only
  cancels at await points cannot interrupt it. The Rust engine terminates the
  isolate from a separate thread. The C# port uses `V8Runtime.Interrupt()` /
  `V8ScriptEngine.Interrupt()` from a watchdog thread for the same reason. The
  CLI keeps a process-level hard deadline as an absolute backstop.

## Porting workflow (per component)

1. Read the Rust source completely before writing C#. Note every public item.
2. Port the types and the public surface first, then the bodies.
3. Port the Rust unit tests (`#[cfg(test)] mod tests`) as xUnit facts in the
   same order, then the integration tests under `crates/<crate>/tests/`.
4. Run `dotnet test` for the area. Green means the port is *plausible*.
5. Add or extend a parity test that runs the same input through both engines.
   Green parity means the port is *done*.
6. Update `todo.md`: move the component's line from `[ ]` to `[x]` and record
   any deliberate deviation under "Known deviations".

## Stealth

The stealth features are privacy-first anti-fingerprinting: they present a
normal, consistent browser fingerprint (user agent, timezone, navigator
properties, and similar surfaces) so ordinary automation traffic is not singled
out. They contain no bot or automation-abuse payload. The port carries the
JS-visible surfaces and header/identity behavior. TLS ClientHello impersonation
(`wreq`/BoringSSL) has no managed equivalent and is a tracked gap.
