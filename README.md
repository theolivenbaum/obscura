# Obscura for .NET

A headless browser engine in C# / .NET 10. It runs real JavaScript through V8,
keeps a real DOM tree, owns its layout and paint pipeline, speaks the Chrome
DevTools Protocol, and works as a drop-in replacement for headless Chrome with
Puppeteer and Playwright, without shipping or launching Chromium.

This is a reimplementation of **[Obscura](https://github.com/h4ckf0r0day/obscura)**,
the open-source headless browser written in Rust by the Obscura authors. The
engine here is a port of that code base, component by component, into idiomatic
C#. The wire surfaces (CDP messages, the MCP tools, the CLI output, the JavaScript
op protocol) match the original byte for byte so clients built against Obscura
keep working; DOM, CSS and layout behaviour is measured against Chromium, and
where the original disagrees with Chromium this port follows Chromium. The Rust
source it was ported from is kept, read-only, under
[`.reference/obscura/`](.reference/obscura/) for reference.

## What it does

- **JavaScript** through V8 (ClearScript), with a Web API shim covering the DOM,
  events, timers, `fetch`/XHR, workers, storage, canvas and more.
- **Rendering** with its own CSS cascade, flex/grid/block/inline/table layout
  (a port of [Taffy](https://github.com/DioxusLabs/taffy)), and painting onto
  Skia: screenshots, screencasts and PDF export with no browser installed.
- **Chrome DevTools Protocol** server for Puppeteer, Playwright and other CDP
  clients.
- **MCP server** exposing browser tools to AI agents.
- **Stealth**: a consistent, ordinary browser fingerprint (user agent, navigator
  properties, timezone and similar surfaces) and tracker blocking.
- **Deterministic output**: fonts are embedded (Liberation, DejaVu, Noto Color
  Emoji), so a page rasterizes the same on every host, including distroless
  images with no fontconfig.
- **Safe defaults**: fetches to loopback, RFC1918 and link-local addresses are
  blocked unless `--allow-private-network` is given.

The only native dependencies are V8, Skia and HarfBuzz; everything else is
managed code.

## Packages

| Package | What it is |
|---|---|
| `Obscura` | The engine and its library API: `Browser`, `Page`, `Element`, cookies. Start here. |
| `Obscura.Cdp` | The Chrome DevTools Protocol server, on top of `Obscura` |
| `Obscura.Mcp` | The MCP server, on top of `Obscura` |

`Obscura` carries the whole engine as separate assemblies: `Obscura.Browser`
(pages, navigation, screenshots, PDF), `Obscura.Js` (V8, ops, the Web API shim),
`Obscura.Render` (CSS, layout, paint), `Obscura.Net` (HTTP, cookies, robots.txt,
tracker blocklist) and `Obscura.Dom` (DOM tree, HTML parsing, selectors).

```bash
dotnet add package Obscura
```

## Use it as a library

```csharp
using Obscura.Api;

var browser = Browser.Builder()
    .Stealth(true)
    .Build();

using var page = await browser.NewPageAsync();
await page.GotoAsync("https://example.com");

Console.WriteLine(page.Evaluate("document.title"));
Console.WriteLine(page.QuerySelector("h1")?.Text());

var link = await page.WaitForSelectorAsync("a", TimeSpan.FromSeconds(5));
link.Click();
```

`Page` also exposes `Content()`, frame evaluation, preload scripts, and request
interception (`EnableInterception`, `OnRequest`, `OnResponse`).

## Use the CLI

```bash
cd dotnet
dotnet build -c Release
alias obscura="$PWD/src/Obscura.Cli/bin/Release/net10.0/obscura"

obscura fetch https://example.com --dump text          # also html, markdown, links, assets, cookies
obscura fetch https://example.com --eval "document.title"
obscura fetch https://example.com --screenshot page.png
obscura scrape https://a.example https://b.example --concurrency 4
obscura serve --port 9222                               # CDP server
obscura mcp                                             # MCP server over stdio (--http for HTTP)
```

Global options include `--stealth`, `--proxy <url>`, `--user-agent`,
`--storage-dir`, `--obey-robots` and `--allow-private-network`. Run
`obscura --help` or `obscura <command> --help` for the full list.

## Connect Puppeteer or Playwright

Start the CDP server and point a CDP client at it:

```bash
obscura serve --port 9222
```

```js
// Puppeteer
const browser = await puppeteer.connect({ browserWSEndpoint: "ws://127.0.0.1:9222/devtools/browser" });

// Playwright
const browser = await chromium.connectOverCDP("http://127.0.0.1:9222");
```

## Build, test and publish

```bash
cd dotnet
dotnet build -c Release
dotnet test -c Release
```

For anything sensitive to cold start, measure a publish rather than `bin/`;
ReadyToRun precompiles the engine:

```bash
dotnet publish -c Release src/Obscura.Cli -r linux-x64 --self-contained true
```

Packages are built, tested and pushed to NuGet by the Azure DevOps pipeline in
[`.devops/build-nuget.yml`](.devops/build-nuget.yml) on every push to `main`,
versioned `yy.M.<build>`.

## Repository layout

```
dotnet/              the C# engine: src/, tests/, docs/
render-repros/       small layout and paint fixtures, run through both engines
test-html-files/     large self-contained pages for comparison against Chromium
scripts/             parity sweeps and render comparisons against the reference
tools/               imgdiff, the canvas conformance probe, live view
skills/              agent skills for porting and render comparison
todo.md              port status, open issues, and the list of known deviations
.devops/             the NuGet build and publish pipeline
.reference/obscura/  the original Rust Obscura, read-only, for reference
```

`todo.md` is the ledger: what is ported, what is open, and every place this port
deliberately differs from the original and why.

## License and credits

Obscura for .NET is Copyright (c) 2026 Curiosity GmbH and is licensed under the
[MIT License](LICENSE).

It is derived from [Obscura](https://github.com/h4ckf0r0day/obscura), Copyright
the Obscura authors, licensed under the Apache License 2.0; that license is kept
at [`.reference/obscura/LICENSE`](.reference/obscura/LICENSE) and ships in every
package as `LICENSE-OBSCURA.txt`. The layout engine
is a port of Taffy and the text layer follows cosmic-text, both MIT licensed. See
[`NOTICE`](NOTICE) for the full attributions.
