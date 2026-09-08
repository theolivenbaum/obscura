---
name: obscura
description: Operate and validate Obscura for JavaScript page loading, stealth browsing, anti-fingerprinting, tracker blocking, screenshots and visual comparison, CDP automation with Puppeteer or Playwright, screencasting, PDF export, MCP browser interaction, and web extraction. Covers both the Rust engine and the C# / .NET 10 port. Use when running either engine against deterministic fixtures or real sites, diagnosing rendering, geometry, resource, identity, or transport failures, or choosing the correct CLI, CDP, MCP, rendering, or stealth workflow.
---

# Obscura

Use Obscura as a lightweight, stealth-capable headless browser for automation.
It embeds V8, owns the DOM and rendering pipeline, and exposes Chrome DevTools
Protocol workflows without launching Chromium. Treat rendering and stealth as
first-class, complementary capabilities.

## Which engine

The repository currently holds two implementations of the same engine:

- **Rust** (`crates/`) - the reference implementation and the behavioral
  authority. Build it for anything that needs to be definitive.
- **C# / .NET 10** (`dotnet/`) - the in-progress port. See `todo.md` for what is
  ported and `CLAUDE.md` for the porting rules.

Both expose the same CLI, CDP, and MCP surfaces, so every workflow below applies
to either binary. When results differ, the Rust engine is right; treat the
difference as a port bug and record it. For porting work specifically, use the
`obscura-port` skill.

## Build variants

Official release archives and Docker images include rendering. For a source
checkout, build release mode with the render feature:

```bash
CARGO_INCREMENTAL=0 CARGO_BUILD_JOBS=2 cargo build --release -p obscura-cli --bins --features render
```

Build rendering and stealth together for the wreq/BoringSSL transport,
browser-identity protections, and tracker blocking:

```bash
CARGO_INCREMENTAL=0 CARGO_BUILD_JOBS=2 cargo build --release -p obscura-cli --bins --features render,stealth
```

Build without rendering when only DOM, extraction, or CDP automation is needed:

```bash
CARGO_INCREMENTAL=0 CARGO_BUILD_JOBS=2 cargo build --release -p obscura-cli --bins --no-default-features
CARGO_INCREMENTAL=0 CARGO_BUILD_JOBS=2 cargo build --release -p obscura-cli --bins --no-default-features --features stealth
```

Use `./target/release/obscura` in the commands below when working from source.

Build the C# port instead with:

```bash
cd dotnet && dotnet build -c Release
```

Its CLI is `dotnet/src/Obscura.Cli/bin/Release/net10.0/obscura`, and it takes the
same arguments as the Rust binary. The port needs no build features: rendering is
always compiled in, and `--stealth` is a runtime flag as it is in the Rust build.

## Use stealth

The stealth build keeps the complete rendering, screenshot, screencast, PDF,
CDP, and MCP surface. It adds a consistent browser fingerprint across TLS,
HTTP headers, user agent, navigator, and WebGL surfaces; masks
`navigator.webdriver`; masks patched native functions; and blocks the built-in
tracker-domain list.

Enable stealth at runtime with the global `--stealth` flag. It applies to
`fetch`, `serve`, `scrape`, and `mcp`, before or after the subcommand:

```bash
obscura --stealth fetch https://example.com --screenshot page.png
obscura serve --stealth --port 9222
```

The runtime flag needs a `render,stealth` build for the wreq/BoringSSL transport.

**On the C# port, TLS-level impersonation is not available.** `--stealth` there
applies the JavaScript, header, and browser-identity surfaces, but not the
wreq/BoringSSL ClientHello fingerprint, because that needs a native TLS stack the
port does not take. Use the Rust binary when a test depends on TLS fingerprinting.

## Fetch, evaluate, and capture

```bash
obscura fetch https://example.com --dump text
obscura fetch https://example.com --eval "document.title"
obscura fetch https://example.com --screenshot page.png
obscura fetch https://example.com \
  --eval "window.scrollTo(0, document.documentElement.scrollHeight)" \
  --screenshot bottom.png
```

The CLI screenshot path writes PNG and accepts one URL. An omitted `--wait`
uses adaptive settling with a five-second cap. An explicit `--wait N` is a
fixed delay of `N` seconds. `--timeout` is also measured in seconds and bounds
navigation separately.

Use `--dump original` for a binary or raw HTTP response that should bypass DOM
and JavaScript processing. Use `scrape` for many URLs when the requested output
does not require one screenshot per URL.

## Drive CDP

Start the server:

```bash
obscura serve --port 9222
```

Connect Puppeteer with `puppeteer-core` or Playwright with
`chromium.connectOverCDP`. Standard `page.screenshot()` supports viewport and
full-page capture; `page.pdf()` produces raster-backed print output. Scroll
with page JavaScript before a viewport capture when the user wants a lower
section of the page.

Use raw CDP for `Page.captureScreenshot`, `Page.startScreencast`, and
`Page.printToPDF`. A screencast client must acknowledge every
`Page.screencastFrame` using `Page.screencastFrameAck`. Frames are driven by
page activity; this is not fixed-frame-rate desktop capture.

PDF output supports paper dimensions, margins, landscape, scale, backgrounds,
and page ranges. It does not currently provide selectable text, tagged PDF,
outlines, headers/footers, or complete CSS paged-media behavior.

## Drive MCP

Run `obscura mcp` for stdio or `obscura mcp --http --port 3000` for HTTP.
Navigate first, then inspect or interact with the current page. Refresh a
snapshot or interactive-element listing after navigation, clicking, scrolling,
or a framework rerender because element references may have changed.

Render-enabled MCP builds expose `browser_screenshot` as an MCP PNG image and
`browser_pdf` as an embedded PDF resource. MCP does not stream screencast
frames; choose CDP for that workflow.

## Validate visual behavior

Start with a deterministic fixture that isolates the behavior. Then test a
broad real-site set at both initial and scrolled positions. For an engine
comparison, keep viewport, device scale, user agent, network inputs, settle
policy, scroll, animation time, and capture boundary identical.

Confirm both engines navigated successfully and produced nonblank images before
interpreting a diff. Treat a pixel metric as a regression tripwire, not a
verdict. Inspect resource completion, box geometry, line wrapping, structural
edges, clipping, and fixed or sticky behavior, then reduce real failures to a
fixture. Do not introduce hostname-specific rendering logic.

## Set expectations accurately

Obscura supports many common layout and paint paths but is not a bundled Chrome
build. The C# port additionally rasterizes through Skia rather than tiny-skia and
shapes through HarfBuzz rather than cosmic-text, so anti-aliasing and glyph
positioning can differ slightly from the Rust engine even where layout agrees;
judge port output against the Rust engine, not against Chromium. Long-tail CSS, service workers, some Web APIs, native media, GPU or
compositor effects, PDF structure, and platform font rasterization can differ
from Chromium. Preserve the project's existing positioning and published
benchmark claims when editing its documentation. Use the benchmark suite and
matched inputs when adding or updating performance or fidelity measurements.
