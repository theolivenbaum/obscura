# Security

This document describes PocketCalculator's security model, the protections it has
and how to configure them, the issues found by the security review of September
2026 (at commit `6dae774`, after the upstream security ports in `todo.md`
"Upstream sync 727cc46..1a3169d"), and which of them are fixed.

**Short version.** PocketCalculator is a browser engine that runs untrusted
JavaScript from arbitrary web pages. The review found that it did not hold the
same-origin policy against a hostile page, let a page read local files, and let
page-supplied inputs crash the whole process. Every Critical and High finding is
now fixed, with a regression test each (see [Fix status](#fix-status)); several
Medium and Low items are partial or open. Work inside ops now stops at the
watchdog's deadline, but the engine still runs every page of a process in one
address space with no per-page memory budget, so the
[Deployment guidance](#deployment-guidance) still applies: isolate tenants per
process, keep secrets off its filesystem, and cap its resources from outside.

## Fix status

The fixes landed after the review, one finding (or a tight group) per commit,
each with a regression test that reproduces the exploit. The summary table's
**Fix** column gives the state of each finding. Behaviour that now differs from
the Rust reference is recorded under "Known deviations" in `todo.md`
("Security review fixes"). The finding sections below describe the problem as
found at `6dae774` and are kept as the record.

Still open after the fixes:

- **Slow inputs.** C# work inside an op, a capture, or an MCP tool call stops when its
  watchdog fires (see [Cancellation of work inside ops](#cancellation-of-work-inside-ops)).
  The worst known inputs are now fast as well:

  | Input | Before | After |
  |---|---|---|
  | 300 nested floats | 77 s | 1.8 s |
  | 5000 nested inline spans | minutes | 1.5 s |
  | 20000 sibling bordered spans | minutes | 1.8 s |
  | nested grid `repeat()` (now invalid, as in Chromium) | exponential | 1.2 s |
  | `innerText`, 10k deep | JS stack overflow | 41 ms |
  | `Range.toString()`, 10k deep | 29 s | 12 ms |

  Grid track counts follow Chromium's `kGridMaxTracks` rules at 10,000 tracks per
  axis. Still slow, but bounded by the deadline:
  - nested `display:table` (quadratic, about 1.7 s at 300);
  - the first layout of a 10k-deep or 50k-wide tree (5-7 s);
  - `innerHTML` with tens of thousands of top-level nodes (quadratic inside
    AngleSharp);
  - a grid item spanning the whole track limit, which allocates up to 400 MB for one
    layout.

  `POCKETCALCULATOR_HANG_EXIT_MS` stays as the opt-in backstop for `serve` and
  `mcp`, for work outside any of these scopes.
- **Deep markup parses in quadratic time** inside AngleSharp's tree builder
  (M11): 50k nested `<div>`s take about 20 s.
- **Memory** (M7, M4): `ArrayBuffer`s are capped per isolate (1 GiB) and DOM data per
  document (512 MiB), detached DOM nothing holds is garbage-collected, and
  `op_fetch_url` writes its result once. WebAssembly memory is still not counted, and
  the per-process limit (`POCKETCALCULATOR_MAX_PROCESS_BYTES`) is opt-in.
- **CDP isolated worlds** (M6): main-frame worlds are separate realms now, so page
  tampering no longer reaches Playwright's or Puppeteer's utility-world results.
  Child-frame worlds still share the frame's realm, and main-world CDP snippets still
  use page-visible built-ins (L10).
- **Remaining page-writable surfaces:**
  - `__virtualUrl` still moves the URL the page reports and the one CDP and MCP
    show. Origin, cookie and initiator decisions no longer read it.
  - The CDP object store and CDP/MCP host snippets use page globals (L10,
    todo.md).
  - MCP `browser_import_state` applies every origin's storage to the current page
    (L9).
- **Not changed:**
  - the curated public suffix list (L6);
  - no HSTS store and no mixed-content blocking (I7);
  - custom-root EKU and revocation checks (I8);
  - `EngineInternal` visible (I10: ClearScript defines it non-configurable);
  - plaintext control planes (I1: terminate TLS in front).

### Cancellation of work inside ops

The V8 watchdog interrupts script with `V8ScriptEngine.Interrupt()`, which V8 only
acts on in JavaScript. A single op that spent its time in C# (one
`getBoundingClientRect()`, one `querySelectorAll`, one screenshot) held the thread
until it finished, and the interrupt that fired during it was lost. A page could then
catch the op's result and loop forever with the watchdog spent. Now:

- **Tokens at the boundaries.** Each isolate has a cancellation source
  (`ScriptCancellation`) that every watchdog cancels before it interrupts, and that
  `CancelTermination` resets. The public render entry points (`RenderPaint.Prepare*`,
  `Paint*`, `Screenshot*`) take a `CancellationToken`. Every sync op, and every
  capture, runs under the isolate's token.
- **Checks in the hot loops.** A synchronous pass keeps its token in a scope
  (`WorkCancellation`) that it checks cheaply per node:
  - every recursive render walk (through `StackGuard`), and taffy's child-layout
    dispatch;
  - the inline line-edge loops;
  - selector matching, `Descendants()` and serialization.

  The large-stack layout thread carries the scope across.
- **Termination is guaranteed.** An op that observes the deadline returns normally
  and keeps the interrupt coming until the script is gone, and the evaluation reports
  "execution terminated" rather than a placeholder result.
- **Callers' deadlines.** `PocketCalculatorJsRuntime.InterruptOnCancellation(token)`
  treats a caller's token as a watchdog. The navigation's script phase uses it, so
  `fetch --timeout` stops a stuck page instead of waiting for the 30 s script
  watchdog or the hard exit.
- **MCP.** Every tool call runs under `POCKETCALCULATOR_MCP_TOOL_TIMEOUT_MS`
  (default 60 s, 0 disables), as every CDP command runs under
  `POCKETCALCULATOR_CDP_COMMAND_TIMEOUT_MS`. Before, a `browser_evaluate` of an
  infinite loop held the MCP server for good.

The checks cost 2-3% on op-heavy, layout-heavy and query-heavy pages, inside the
benchmark's noise floor.

## Reporting a vulnerability

Please do not open a public issue for a vulnerability. Report it privately to
Curiosity GmbH (security advisories on the GitHub repository, or your usual
contact at curiosity.ai). If the issue is also present in the original Rust
[Obscura](https://github.com/h4ckf0r0day/obscura), say so, so it can be reported
upstream as well.

## Contents

- [Threat model](#threat-model)
- [How it works](#how-it-works)
- [Protections and configuration](#protections-and-configuration)
- [Deployment guidance](#deployment-guidance)
- [Findings](#findings)
- [Fix status](#fix-status)
- [Cancellation of work inside ops](#cancellation-of-work-inside-ops)
- [Checked and holding](#checked-and-holding)
- [Remediation plan](#remediation-plan)

## Threat model

Three kinds of party touch the engine, and each is trusted differently.

| Party | Trust | What it must not be able to do |
|---|---|---|
| **Web content**: pages, their scripts, frames, subresources, and the servers that send them | Untrusted | Reach the engine's ops or host helpers; read another origin's data (responses, frames, cookies, storage); read local files; reach private networks; forge trusted input events; crash or wedge the process; exhaust memory or CPU beyond a page's budget |
| **Control-plane clients**: CDP clients (Puppeteer, Playwright), MCP clients (AI agents), CLI callers | Trusted for what they drive, but only once authenticated | Be reachable by an unauthenticated network peer or by a browser tab (CSRF, DNS rebinding); get host capabilities the operator did not grant (local files, private network) |
| **The operator**: whoever launches the process and sets flags and environment | Trusted | Nothing; but the defaults must be safe, and every widening must be explicit |

A fourth concern is specific to automation: a page that cannot read a secret
itself, but can make an **AI agent or scraper** read it (by steering the page to a
local file, or by planting text in a dump), has still leaked it. Findings that work
this way are rated as real, not hypothetical.

## How it works

The engine is a set of managed .NET assemblies (DOM, network, JS, render,
browser, CDP, MCP, CLI) around three native libraries: V8 (through ClearScript),
Skia and HarfBuzz.

### Trust boundaries

```
 control-plane clients                  web content
 (CDP / MCP / CLI)                      (page script, frames, servers)
        |                                        |
        |  token + Host/Origin gates             |  V8 realm per frame
        v                                        v
 +---------------+   host-only entry   +----------------------+
 |  host code    |-------------------->|  bootstrap.js shim   |
 |  (C#: Page,   |  EvaluateHost /     |  (JS: DOM, fetch,    |
 |   CDP, MCP)   |  ExecuteHostScript  |   events, frames)    |
 +---------------+                     +----------------------+
        |                                        |  ops (closure only)
        v                                        v
 +--------------------------------------------------------------+
 |  ops (C#): DOM arena, fetch transport, cookies, storage,     |
 |  stylesheets, frames, crypto, canvas, layout, paint          |
 +--------------------------------------------------------------+
        |                                        |
        v                                        v
   network (SSRF guard at connect)       Skia / HarfBuzz (native)
```

1. **Page script and the op layer.** Every page (and every frame) is a V8 realm
   running `dotnet/src/PocketCalculator.Js/js/bootstrap.js`, a JS shim that
   implements the DOM and web APIs over about 53 host ops.
   - `BootstrapLoader.Install` hands the op table to the shim and then deletes every
     global that led to it (`Deno`, `__obscura_deno_core`, the two handoff globals).
     The shim keeps the ops in a closure (`__obscuraCore`), so page script can
     call a web API but never an op directly.
   - ClearScript reflection is off. No .NET type is exposed, and ops return only
     strings, numbers, booleans and typed arrays.
2. **Host helpers.** Host code sometimes needs page-side capabilities: marking an
   event trusted, setting input files or field values, delivering a message,
   frame bookkeeping. Those live in a frozen, null-prototype `__obscura_host`
   object that no global references. Host code reaches it only as the argument
   of a strict wrapper (`Runtime/HostScript.cs`, `EvaluateHost`,
   `ExecuteHostScript`). Every host snippet that interpolates data escapes it as
   JSON; the review found no string injection.
3. **Trusted events.** `isTrusted` is read from a closure `WeakSet` through
   captured `WeakSet.prototype` methods, so a page can neither mark its own events
   trusted nor steal the set.
4. **Network.** There are two transports:
   - navigations and most subresources go through `PocketCalculatorHttpClient`;
   - page `fetch()`/XHR, iframe documents and dynamic scripts and stylesheets go
     through `op_fetch_url`.

   Both follow redirects by hand and re-validate every hop. The SSRF guard runs
   at connect time on the resolved addresses (`SocketsHttpHandler.ConnectCallback`
   dials exactly the vetted IPs), so DNS rebinding cannot slip between check and
   connect. `op_fetch_url` implements CORS (preflight, per-hop checks,
   credential stripping on cross-origin redirects, opaque `no-cors` responses)
   and the Fetch spec's request guards.
5. **Cookies and storage** are per `BrowserContext`. Cookie access from script uses
   the host-side document URL, not the page's `location`. HttpOnly cookies are
   invisible to script, Secure cookies are only set from https, and SameSite is
   applied with the navigation initiator.
6. **Control planes.**
   - **CDP** is a WebSocket server with `/json` discovery. It checks, in order:
     - any browser `Origin` is refused;
     - the `Host` must be an IP literal or `localhost`, which defeats DNS rebinding;
     - the bearer token (fixed-time comparison).

     Each connection gets its own `BrowserContext`.
   - **MCP** runs over stdio, or over HTTP with a bearer token and an Origin
     allowlist.
   - A non-loopback bind of either needs a 32+ byte token.
7. **Time.** Synchronous V8 work is bounded by a watchdog thread that calls
   `V8ScriptEngine.Interrupt()`. The CLI `fetch` command also arms a process-level
   hard deadline.

### Where the model was weakest

Two design choices were behind most of the Critical findings. Both are fixed:
origin, frame-access and origin-clean decisions are now made by the host from
the calling realm, and internal-load bodies stay host-side behind a token
(C1 to C3, H4). The description below is kept as the record.

- **The host trusts security decisions the shim computes in page-visible JS.**
  - The page origin that `op_fetch_url` uses for CORS, credentials and SameSite is
    computed by the shim with the page's `URL` global.
  - So is the origin a `postMessage` carries.
  - So is whether a dynamic stylesheet is origin-clean.
  - Whether a frame's document is same-origin is decided in a JS getter.

  A page that replaces `URL` or `JSON.parse`, or overwrites an expando such as
  `_iframeLoadedUrl`, changes the answer. The host already knows each realm's
  real URL (`state.Url`, `FrameRealm.Origin`) and should decide all of these
  itself.
- **Internal loads return their bodies to JS.** Cross-origin iframe documents and
  cross-origin scripts and stylesheets are fetched with `internalLoad=true`, and
  their bodies come back into the page realm as JSON. They pass through
  page-overridable built-ins and land on page-reachable objects.

## Protections and configuration

Defaults are safe unless noted. Every knob that widens access is opt-in.

### Network

| Setting | Default | Effect |
|---|---|---|
| `--allow-private-network`, `POCKETCALCULATOR_ALLOW_PRIVATE_NETWORK=1`, `BrowserContext` `allowPrivateNetwork` | off | Turns the SSRF guard off entirely. Blocked ranges: loopback, RFC1918, link-local, CGNAT, `0/8`, `240/4`, multicast, documentation, ULA, v4-mapped / v4-compatible, NAT64, 6to4. |
| `--proxy`, `POCKETCALCULATOR_PROXY` | none | Upstream proxy. Environment proxies (`HTTP_PROXY`, `HTTPS_PROXY`) are ignored. With a proxy, the target name is resolved and vetted locally first (M2). |
| `--obey-robots`, `POCKETCALCULATOR_OBEY_ROBOTS` | off | Honour robots.txt. |
| `--stealth`, `POCKETCALCULATOR_STEALTH` | off | Tracker blocklist plus consistent fingerprint surfaces. The blocklist applies to every request and redirect hop (L7). |
| `POCKETCALCULATOR_FETCH_TIMEOUT_MS` | 30000 | Time to response headers for `fetch()`/XHR. The body is not covered (M3). |
| `POCKETCALCULATOR_FETCH_MAX_BODY_BYTES` | 100 MB | Decompressed body cap for `fetch()`/XHR. |
| `POCKETCALCULATOR_NETWORK_BODY_BUFFER_BYTES` / `_ENTRIES` | 2 MB | Bodies retained for CDP `Network.getResponseBody`. |
| `ResourceRequest.MaxResponseBytes` | 64 MB | Navigation body cap. |
| `SSL_CERT_FILE`, `SSL_CERT_DIR` | none | Extra trust roots. There is no option to disable certificate validation. |

### Control planes

| Setting | Default | Effect |
|---|---|---|
| `--host` (`serve`, `mcp --http`) | `127.0.0.1` | Bind address. A non-loopback bind refuses to start without a token. |
| `POCKETCALCULATOR_CDP_TOKEN` | none | Bearer token for CDP (32+ bytes; required for a non-loopback bind). |
| `POCKETCALCULATOR_MCP_TOKEN` | none | Bearer token for MCP over HTTP (32+ bytes; required for a non-loopback bind). |
| `POCKETCALCULATOR_MCP_ALLOWED_ORIGINS` | none | Browser Origins MCP accepts; any other Origin is refused. |
| `POCKETCALCULATOR_CDP_FORWARDED_HOST` / `_PORT` | none | Public authority a CDP worker advertises behind `serve --workers` or a reverse proxy. |
| `--allow-file-access` (`serve`) | off | Lets CDP navigate to `file://` and `DOM.setFileInputFiles` read host files. One gate covers every CDP navigation path (H1). A web page can never navigate to `file:` itself, even with this flag (H2, H3). |
| `--max-connections` (`serve`) | 128 | Concurrent CDP connections. |
| `--workers N` (`serve`) | 1 | Runs N worker processes behind a TCP balancer; one crashing page takes out one worker. |
| `POCKETCALCULATOR_IO_STREAM_MAX_ENTRIES` / `_BYTES` | bounded | Per-connection CDP `IO` stream store. |
| `POCKETCALCULATOR_CDP_COMMAND_TIMEOUT_MS` | bounded | Per-command CDP watchdog. |

### Content and resources

| Setting | Default | Effect |
|---|---|---|
| `--timeout`, `POCKETCALCULATOR_SCRIPT_DEADLINE_MS`, `POCKETCALCULATOR_NAV_TIMEOUT_MS` | 30 s | Watchdog on synchronous V8 work, and navigation deadlines. The watchdog also cancels C# work inside ops and captures, and `--timeout` now stops the script phase too. `POCKETCALCULATOR_HANG_EXIT_MS` (opt-in, `serve`/`mcp`) exits the process when an interrupted command still does not return (L12). |
| `POCKETCALCULATOR_MAX_ARRAY_BUFFER_BYTES` | 1 GiB | `ArrayBuffer` backing stores per isolate (0 for none). Over it: `RangeError`. |
| `POCKETCALCULATOR_MAX_DOM_BYTES` | 512 MiB | DOM node and text/attribute data per document (0 disables). Over it, after a collection: `QuotaExceededError`. |
| `POCKETCALCULATOR_MAX_PROCESS_BYTES` | off | Process working-set limit: over it, running page work is terminated and new pages are refused. |
| `POCKETCALCULATOR_MCP_TOOL_TIMEOUT_MS` | 60 s | Watchdog on each MCP tool call (0 disables), like `POCKETCALCULATOR_CDP_COMMAND_TIMEOUT_MS` for CDP commands. |
| V8 flags `--max-old-space-size` | 4096 MB | Enforced heap cap, applied to every runtime, library and CDP included (M7). Memory outside the V8 heap is not counted. |
| `--font-dir`, `BrowserConfig.FontDirectories` | none | Operator-chosen font directories, read once. With none set, no font file is read from disk. |
| `POCKETCALCULATOR_FRAME_MESSAGE_QUEUE_BYTES` / `_ENTRIES`, `POCKETCALCULATOR_MAX_LIVE_FRAMES`, `POCKETCALCULATOR_NAV_CHAIN_LIMIT` | bounded | Cross-frame message queue, live frames, navigation chain length. |
| Built-in caps (not configurable) | | Explicit-stack HTML adapter, iterative DOM walks and serialization with cycle guards; CSS 8 MB / 100k rules per sheet, `calc()` depth 64, `var()` depth 16; SVG depth 24, no DTD / external entities; capture 32768 px per side, 16M px, 128 MB; canvas 32767 px per side, 64M px, 256 MB per runtime; PDF 250 pages, 64 MB; PBKDF2 / HKDF / random sizes. |

## Deployment guidance

The Critical and High findings are fixed, but the engine has no per-page memory
budget (see [Fix status](#fix-status)).
For untrusted content:

- **One trust domain per process.**
  - Do not load pages from different customers, or pages alongside secrets, in one
    process. One page can still stall or exhaust memory for every other session
    in it.
  - Prefer `scrape` (one worker process per URL) or `serve --workers N` with a
    small N.
  - Run each process under a supervisor that restarts it.
- **Assume local file read.**
  - Run in a container or sandbox whose filesystem holds nothing secret: no cloud
    credentials, no SSH keys, no source trees, no cookie jars of other tenants.
  - Mount it read-only where possible, and run as an unprivileged user.
  - `file:` is now served only to operator navigations and `file:` documents
    (C4, H1 to H3), but a filesystem with nothing secret is the defence in depth.
- **Enforce egress outside the process.**
  - The SSRF guard checks at connect time. Behind a configured proxy, the proxy
    does its own DNS lookup, so rebinding between the local check and the
    proxy's lookup remains (M2).
  - Block metadata endpoints (`169.254.169.254`, `fd00:ec2::254`) and internal
    ranges at the network layer as well.
- **Cap resources outside the process.** Set a cgroup/container memory limit and
  a CPU limit. DOM text and `ArrayBuffer`s are not counted against the V8 heap
  cap (M7), and deep script-built trees can still cost minutes of CPU.
- **Treat control planes as root on the process.**
  - Keep CDP and MCP on loopback.
  - On a shared host, remember that loopback has no token by default: any local
    user can connect. Set a token anyway.
  - For remote access, put TLS in front (the servers speak plaintext) and set a
    32+ byte token.
- **Treat dumps as untrusted.** Serialization and the markdown dump now escape
  what they must (M9, M10), but a dump is still the page's content. Sanitize it
  before rendering, and do not feed it to an agent with tool access without the
  usual prompt-injection care.
- **Supervise `serve --workers`.** The balancer does not restart a worker that
  exits, and `POCKETCALCULATOR_HANG_EXIT_MS` makes wedged workers exit.

## Findings

Every finding was reproduced against a Release build unless marked *read*, which
means established from the code alone. Line references are at commit `6dae774`.
**Inherited** means the Rust reference has the same behaviour. Findings that
contradict an entry `todo.md` marks done say so.

### Summary

| ID | Severity | Area | Finding | Review | Fix |
|---|---|---|---|---|---|
| C1 | Critical | JS/Net | Cross-origin reads with cookies: the host trusts a page-computed origin | tested | fixed |
| C2 | Critical | JS | Cross-origin iframe documents readable through `contentWindow`, `_iframeDoc`, `_iframeLoadedUrl` | tested; contradicts `04418a5 D` done | fixed |
| C3 | Critical | JS | Internal-load bodies pass through page-overridable `JSON.parse`; `originClean` computed in JS | tested | fixed |
| C4 | Critical | Net/JS | Any page can `import()` local `file://` modules | tested; inherited | fixed |
| C5 | Critical | Render | DOM ~1500 deep: layout StackOverflow kills the process | tested | fixed |
| C6 | Critical | DOM | Nested selectors (`:not(`/`:is(`/`:has(`): parser StackOverflow kills the process, from a static stylesheet too | tested | fixed |
| H1 | High | CDP | `Page.navigate` on a session skips the `--allow-file-access` gate | tested; contradicts todo.md | fixed |
| H2 | High | CDP | Page-initiated navigation to `file://` is followed in CDP pages | tested | fixed |
| H3 | High | MCP | `browser_tab_new` and clicks on `file://` links open local files | tested; contradicts `04418a5 G` done | fixed |
| H4 | High | JS | `postMessage` `event.origin` can be spoofed | tested | fixed |
| H5 | High | Render | `stackalloc char[text.Length]` on page text: StackOverflow | tested | fixed |
| H6 | High | Render | WOFF / WOFF2 decompression bomb (1.5 MB gives 9 GB) | tested | fixed |
| H7 | High | Render | Image decode and paint allocations unbounded (190 KB PNG gives 3.2 GB) | tested | fixed |
| H8 | High | Render/DOM | Uninterruptible CPU hangs in native C#: SVG `<use>` fan-out, `var()` expansion, nested `:has()` | tested | fixed (budgets, and cancellation of work inside ops) |
| M1 | Medium | CLI | `serve --workers` under the `dotnet` host starts `dotnet serve` | tested; conditional | fixed |
| M2 | Medium | Net | A proxy (including `HTTP_PROXY`) disables the hostname SSRF check | tested; inherited | fixed; proxy-side rebinding remains |
| M3 | Medium | Net | No timeout on response body reads | tested | fixed |
| M4 | Medium | Net | `op_fetch_url` copies each body about 5x, with no concurrency cap | read | fixed: concurrency capped, result written once |
| M5 | Medium | Net | `__Host-` / `__Secure-` cookie prefixes not enforced; cookie jar unbounded | read | fixed |
| M6 | Medium | CDP | Isolated worlds share the main world; binding calls forgeable | read | fixed: binding names checked, main-frame worlds are realms; child-frame worlds open |
| M7 | Medium | JS | Memory outside the V8 heap cap; no default cap for embedders | tested | fixed: heap, ArrayBuffer and DOM budgets, DOM collector, opt-in process limit; WebAssembly memory open |
| M8 | Medium | JS | PBKDF2 bomb runs synchronously and cannot be interrupted | tested (CLI) | fixed |
| M9 | Medium | DOM | Serialization mXSS: `textarea`/`title`, foreign `style`/`script` emitted raw | tested; inherited | fixed |
| M10 | Medium | CLI | Markdown dump passes raw HTML and `javascript:` links through | tested | fixed |
| M11 | Medium | DOM | Quadratic parse and append cost on deep trees | tested | partial: append fixed, parse cost in AngleSharp open |
| L1 | Low | MCP | MCP HTTP has no `Host` check (DNS rebinding for SSE GETs) | tested | fixed |
| L2 | Low | MCP | SSE streams do not count against the connection cap | read | fixed |
| L3 | Low | CDP | CDP resource limits loose: 64 MiB messages x 128, unbounded queues/targets | read | fixed; no idle timeout |
| L4 | Low | CDP | Any message containing `"Browser.close"` closes the connection | tested | fixed |
| L5 | Low | CI | Pipeline has no `pr:` section or branch condition on push; no lock files | read | fixed |
| L6 | Low | Net | Public suffix list is curated (~450 rules) | read; in todo.md | open |
| L7 | Low | Net | Tracker blocklist skips `op_fetch_url` and redirect hops | read; inherited | fixed |
| L8 | Low | Net | `InterceptAction.ModifyHeaders` leaks headers into later requests | read | fixed |
| L9 | Low | JS/MCP | Cross-origin `pushState` spoofs `location.origin`; MCP state export/import trusts it | tested | partial: `pushState` fixed; `__virtualUrl` and MCP state import/export open |
| L10 | Low | JS | Host ops and snippets call page-replaceable globals | read; partly in todo.md | partial: `Uint8Array` fixed; CDP/MCP snippets open |
| L11 | Low | Browser | Latent integer overflow in `RgbImage.Decode` | read; not reachable today | fixed |
| L12 | Low | CLI | Process backstop exists for `fetch` only, not `serve`/`mcp` | read | fixed: work inside ops is cancelled; opt-in exit (`POCKETCALCULATOR_HANG_EXIT_MS`) remains the backstop |
| I1 to I10 | Info | various | See [Informational](#informational) | | I2 to I6, I9 fixed; I1, I7, I8, I10 open |

### Critical

#### C1. Cross-origin reads with cookies: the host trusts a page-computed origin

- **Where:** `bootstrap.js:7632`, where `fetch()` computes `pageOrigin` with
  `new URL(_domParse("document_url")).origin`. `FetchOps.cs:431` uses it as given
  for `isCrossOrigin`, the CORS check, `credentialsMode.Allows` and the SameSite
  context. The same pattern appears at `bootstrap.js:4436` (the iframe
  `contentDocument` check).
- **Exploit:** a page replaces `window.URL` so the document URL reports the
  victim's origin, then calls `fetch("https://victim.example/account")`.
  - The request carries the victim's cookies (HttpOnly and SameSite=Strict
    included).
  - The response passes CORS, and the page reads the body.

  Tested between `localhost:8001` and `127.0.0.1:8002`; the unmodified control
  page got a CORS error.
- **Impact:** a complete same-origin policy bypass for anything the context's
  cookie jar is logged into.
- **Fix:** `op_fetch_url` must derive the origin host-side from the calling
  realm's `state.Url`, and ignore the argument. Apply the same to every other op
  that receives an origin from JS.

#### C2. Cross-origin iframe documents are readable

- **Where:** `bootstrap.js:4379-4463`. The iframe loader fetches the frame with
  `internalLoad=true`, `mode: navigate` and `credentials: include`, then stores
  the parsed document on the element as `_iframeDoc` / `_iframeWin`. Only the
  `contentDocument` getter checks origin.
- **Exploit:** with `<iframe src="https://victim.example/">`, `contentDocument` is
  `null`, but any of these returns the victim's credentialed page:
  - `f.contentWindow.document.body.innerHTML`;
  - `f._iframeDoc.body.innerHTML`;
  - setting `f._iframeLoadedUrl = 'about:blank'`, which makes `contentDocument`
    itself return it.
- **Impact:** a credentialed cross-site read of any framable page.
  `todo.md` lists upstream `04418a5 D` (the frame isolation fix) as done; this
  contradicts it.
- **Fix:**
  - Keep fetched frame state in a closure `WeakMap`, not on the element.
  - Return a `_RemoteWindow` (postMessage only) from `contentWindow` for a
    cross-origin frame.
  - Decide same-origin host-side (`FrameRealm.IsSameOriginAs` exists).

#### C3. Internal-load bodies pass through page-overridable built-ins

- **Where:** `bootstrap.js:356-361` (dynamic scripts), `614-618` (linked CSS),
  `4379-4383` (iframes). Each parses the `op_fetch_url` result with the global
  `JSON.parse`.
- **Exploit:** a page sets `JSON.parse = s => (leak.push(s), orig(s))`, then
  inserts a cross-origin `<script src>` or `<link rel=stylesheet>`. The hook
  receives the full response, including `bodyBase64`.
- **Second problem:** a dynamic sheet's `originClean` is computed in JS
  (`bootstrap.js:648`) and sent to the host. With `URL` overridden, a
  cross-origin sheet is marked clean and its text becomes readable through
  CSSOM.
- **Impact:** this defeats the `04418a5 B/C` confidentiality ports for
  dynamically inserted resources.
- **Fix:**
  - Internal loads should not return bodies to JS at all. The host consumes them
    (stylesheet store, frame queue, script execution) and computes `originClean`
    from the response.
  - Capturing intrinsics at bootstrap (`const _parse = JSON.parse`) is a stopgap
    only: `await` and `.then` are hookable through `Promise.prototype`.

#### C4. Any page can import local files as ES modules

- **Where:**
  - `PocketCalculatorHttpClient.cs:579-585` serves any `file:` URL with
    `FetchFileUrlAsync` before any mode or CORS check;
  - `SsrfGuard.ValidateUrl` (`SsrfGuard.cs:172-183`) whitelists `file:` for every
    caller;
  - `PocketCalculatorModuleLoader.cs:253-340` fetches through that path with no
    scheme gate.
- **Exploit:** from an `http(s)` or `data:` page, `import('file:///srv/app/config.mjs')`
  resolves with the file's exports, which the page can then send anywhere.
  - Non-JS files are executed too, and their SyntaxError messages leak tokens
    (`/etc/passwd` gives "Unexpected token ':'").
  - Rejections reveal whether a path exists ("Could not find a part of the path").
  - Classic `<script src=file:>`, stylesheets, images and `fetch('file:')` are
    correctly refused (`PageHelpers.SubresourceAllowed`, `ValidateFetchUrl`);
    modules are the gap.
- **Inherited:** Rust `client.rs:715,1440`.
- **Fix:** gate `file:` in the transport on the request's initiator. Allow it only
  for a top-level navigation the operator started, or a subresource of a `file:`
  document, as Chromium does. Refuse it in `ValidateUrl` for every other caller.

#### C5. Deep DOM kills the process during layout

- **Where:** render-tree building and layout recurse per element with no depth
  guard:
  - `DomBuild.BuildAny` / `Build` (`Render/Dom/DomBuild.cs:143, 209`);
  - `TaffyStyleMapping.ToTaffyStyle`;
  - `BlockLayout.GenerateItemList`.

  No `RuntimeHelpers.EnsureSufficientExecutionStack` call exists anywhere in
  `src/`.
- **Exploit:** a script builds 1500 nested `<div>`s and calls
  `getBoundingClientRect()`, and the process aborts with "Stack overflow" (exit
  134). Static HTML 3000 deep plus a screenshot does the same.
  - .NET cannot catch a stack overflow.
  - In `serve` or `mcp`, one page takes down every session in the process.
- **Fix:**
  - Mirror Chromium's parser depth cap (512 open elements, beyond which the parser
    flattens) in `HtmlParsing.Adapt`.
  - Clamp render-tree depth for script-built trees.
  - Add `EnsureSufficientExecutionStack()` to the recursive build, layout and
    paint paths, and convert the exception at `OpGuard`.
  - Optionally run layout on a thread with a larger stack.

#### C6. Nested selectors kill the process

- **Where:** `SelectorParser.ParseCompound`, `ParsePseudoClass` and
  `ParseRelativeSelector` recurse without a limit (`Dom/Selectors/SelectorParser.cs:323,
  359, 409, 649`).
- **Exploit:** `querySelector(":not(".repeat(20000) + "a" + ")".repeat(20000))`
  aborts the process, even inside `try`/`catch`. So do `:is(` and `:has(`, and so
  does a plain `<style>` containing such a selector, with no script at all.
- **Fix:** count nesting in the parser and reject a selector beyond about 64
  levels as invalid. Add an execution-stack check in the matcher.

### High

#### H1. CDP `Page.navigate` on a session skips the file-access gate

- **Where:** `Server.Processor.cs:201` routes every sessioned `Page.navigate` to
  `ProcessWithInterceptionAsync` (`Server.Navigation.cs:31-120`), which never
  checks the scheme. The gate is only in `Domains/Page.cs:743-746`
  (`DoNavigateAsync`), which the sessionless path reaches.
- **Exploit:** on a default `serve` (no `--allow-file-access`), use
  `Target.attachToTarget {flatten: true}`, then
  `Page.navigate {url: "file:///etc/passwd"}`, then read the page text.
  Puppeteer and Playwright always use flattened sessions, so in practice the gate
  protects nothing.
- **Impact:**
  - On loopback with no token, any local user can read any file the process can.
  - With a token, anyone who holds it can.
  - `todo.md` lists this gate as done.
- **Fix:** one scheme gate shared by every navigation entry point
  (`Page.navigate` with or without a session, `Target.createTarget`,
  history navigation, and the pending-navigation pump). Add a sessioned
  regression test.

#### H2. Page-initiated navigation to `file://` is followed in CDP pages

- **Where:** the autonomous navigation pump (`Server.Processor.cs:129-141`)
  forwards a page-queued navigation (`TakeLivePendingNavigation`) into
  `ProcessWithInterceptionAsync`. Neither the file gate nor
  `PageHelpers.CrossSchemeToFile` is applied on that path.
- **Exploit:** a web page runs `location.href = 'file:///home/app/.env'`, and the
  CDP page lands on the local file.
  - The attacking realm is gone after the navigation, so the page cannot read the
    file itself.
  - But the automation client now holds the file as "the page". An AI agent that
    summarises or acts on the page leaks it.
- **Fix:** refuse a page-initiated navigation from a non-`file:` document to
  `file:`, whatever `--allow-file-access` says (the `CrossSchemeToFile` rule).

#### H3. MCP opens local files through tabs and clicks

- **Where:**
  - `browser_tab_new` (`Mcp/Tools.Tier2.cs:367-385`) has no scheme check;
  - `browser_click` on a `file:` link settles through
    `Page.ProcessPendingNavigationOutcomeAsync` (`Browser/Page.Navigation.cs:845`),
    which does not apply `CrossSchemeToFile`.

  Only `browser_navigate` refuses `file://` (`Tools.cs:67`).
- **Exploit:** a malicious page shows a link, `<a href="file:///home/user/.ssh/id_rsa">`,
  and prompt-injects the agent into clicking it. `browser_snapshot` then returns
  the key into the model's context. `browser_tab_new {url: "file:///..."}` works
  directly.
- **Impact:** `todo.md` lists upstream `04418a5 G` as done; this contradicts it.
- **Fix:**
  - Apply `CrossSchemeToFile` inside `ProcessPendingNavigationOutcomeAsync`, which
    also covers library embedders.
  - Share the MCP scheme gate across `navigate`, `tab_new`, `back`, `forward` and
    `reload`.

#### H4. `postMessage` origin can be spoofed

- **Where:**
  - `_realmOrigin()` (`bootstrap.js:13862-13864`) computes the sender's origin
    with the page's `URL`;
  - `CoreOps.OpPostFrameMessage` (`CoreOps.cs:282-309`) and
    `FrameRealm.DeliverMessage` trust it.
- **Exploit:** a frame on an attacker's origin replaces `window.URL` and calls
  `parent.postMessage(...)`. The parent receives
  `event.origin === "https://bank.example"`, with `isTrusted` true.
- **Impact:** every embedder's `if (e.origin === ...)` check is defeated.
- **Fix:** the host fills in the origin from the sending realm (`FrameRealm.Origin`
  / `state.Url`). Check the receiver side of `_targetOriginAllows` the same way.

#### H5. Unbounded `stackalloc` on page text

- **Where:** `DomTextMeasure.MeasureAdvances` does `stackalloc char[text.Length]`
  (`Render/Dom/DomTextMeasure.cs:62`). It is reached from `<select>` option
  labels, word leaves and `TextEngine.cs:759`.
- **Exploit:** a `<select>` whose option holds 5 MB of text, plus any layout read,
  aborts the process.
- **Fix:** `text.Length <= 256 ? stackalloc : ArrayPool<char>.Shared.Rent(...)`, as
  `Cdp/Domains/Dom.cs:452` already does.

#### H6. Web font decompression bomb

- **Where:**
  - `Woff.DecodeWoff1` inflates each table into an unbounded `MemoryStream`
    and trusts `origLength` as its capacity (`Render/Inline/Woff.cs:74-80`);
  - `DecodeWoff2` decompresses Brotli with no bound (`Woff.cs:123-128`).
- **Exploit:** a 1.5 MB `@font-face` WOFF reached 9.2 GB resident memory in 21 s.
  WOFF2 can be smaller still.
- **Fix:** bound each table to its declared `origLength` and reject a mismatch.
  Cap the total sfnt size (OTS uses 30 MB per table). Stop reading at the cap.

#### H7. Image decode and paint allocations are unbounded

- **Where:**
  - `RasterToPixmap` decodes at full intrinsic size with `SKBitmap.Decode`
    (`Paint/RenderResourceCache.cs:1082`);
  - destination pixmaps are sized from the CSS box, not the paint surface
    (`PaintImages.cs:819-822`, masks at `925-927`);
  - `Pixmap.New` allows up to 512M pixels (2 GB).
- **Exploit:**
  - A 190 KB 1-bit PNG of 40000x40000 pixels, shown at 100x100, reached 3.2 GB.
  - A 68-byte PNG styled `width:20000px; height:20000px`, plus a normal
    screenshot, was OOM-killed at about 3.1 GB.
- **Fix:**
  - Check `SKCodec.Info` against a decoded-pixel budget before decoding.
  - Decode at a reduced scale (`GetScaledDimensions`) to the size actually drawn.
  - Rasterize only the destination rectangle clipped to the surface.
  - Lower `Pixmap.New`'s cap to the capture budget.

#### H8. Uninterruptible CPU hangs in C# ops

These run inside ops, where `V8ScriptEngine.Interrupt()` has no effect. In
`serve` and `mcp` nothing stops them; the CLI `fetch` survives only because its
hard deadline kills the process.

- **SVG `<use>` fan-out.**
  - Mechanism: 10 `<use>` per level, 11 levels deep. Depth is capped at 24, but
    fan-out is not (`SvgRenderer.cs:713, 915-967`).
  - Reached from both `<img src=*.svg>` and inline SVG.
  - Fix: cap total expanded elements per render.
- **`var()` expansion.**
  - Mechanism: 16 levels with 10 references each. Depth is capped, but the
    substituted length is not (`CssDeclarations.cs:264-346`). One `<style>`
    reached 4 GB and hung.
  - Fix: cap the substituted value's length and treat anything longer as invalid
    at computed-value time, as Chromium does.
- **Nested `:has()`.**
  - Mechanism: `:has(:has(:has(:has(:has(:has(span))))))` on a 500-deep tree ran
    for more than 45 s. The matcher has no cache and no budget
    (`SelectorMatching.cs:579-610`).
  - Fix: reject nested `:has()`, which the spec now makes invalid; memoize per
    anchor; add a step budget.

The general fix is a cooperative step budget or cancellation token checked in
layout, paint and matching loops, plus a process-level backstop (L12).

### Medium

**M1. `serve --workers` can start the wrong program.**
- **Where:** `ServeCommand.cs:141,160` starts workers as
  `Environment.ProcessPath serve --port N`. Run as `dotnet PocketCalculator.Cli.dll`,
  that path is the `dotnet` host, so workers become `dotnet serve ...`.
- **What happens:**
  - Normally startup fails.
  - With the `dotnet-serve` global tool on PATH, it served the working directory
    on the public port without authentication. The balancer does no auth itself,
    and `dotnet-serve` ignores the token.
- **Fix:** when running under the host, pass the entry assembly as the first
  argument (or refuse), and have the balancer check the token too.

**M2. A proxy disables the hostname half of the SSRF guard.**
- **Where:** with a proxy, `ConnectCallback` sees only the proxy's address, and
  the proxy resolves the target name. `SocketsHttpHandler.UseProxy` defaults to
  true, so `HTTP_PROXY` / `HTTPS_PROXY` in the environment apply even without
  `--proxy`.
- **Tested:** `http://metadata.internal.example/` reached the proxy unchecked.
  IP-literal URLs are still refused.
- **Inherited** from reqwest.
- **Fix:**
  - Set `UseProxy = false` unless `--proxy` is given.
  - Resolve and vet the target locally before a proxied request (accepting the
    rebinding gap), and document the rest.

**M3. No timeout on response bodies.**
- **Where:** `FetchOps.SendAsync` (`FetchOps.cs:901`) cancels only until headers
  arrive, and `ReadBodyCappedAsync` takes no token. Navigation reads with
  `ResponseHeadersRead`, which leaves the body likewise unbounded.
- **Tested:** a server sending 1 byte per second held a fetch past 15 s with a
  3 s timeout.
- **Fix:** one `CancellationTokenSource` spanning headers and body.

**M4. `op_fetch_url` memory amplification.** *Read.*
- **Where:** after the 100 MB decompressed cap, the op builds a UTF-8 string,
  a base64 copy and a JSON `StringBuilder` (`FetchOps.cs:761-869`), roughly 5x
  the body. There is no per-page limit on concurrent fetches.
- **Impact:** about 20 parallel gzip-bomb fetches exhaust a shared `serve`
  process.
- **Fix:**
  - Cap concurrent fetches and bytes per page.
  - Lower the default body cap.
  - Skip base64 for text bodies.

**M5. Cookie prefixes and jar limits.** *Read.*
- **Where:** `CookieJar.Store` (`Net/Cookies/CookieJar.cs:94-250`).
- **Prefixes:** `__Host-` and `__Secure-` are not enforced. A sibling subdomain can
  plant `__Host-session` with `Domain=parent`, which enables session fixation.
- **Limits:** there is no per-domain count or per-cookie size limit (Chromium: 180
  per domain, 4 KB per cookie). A page can grow the jar without bound and push
  request headers past server limits.
- **Fix:** enforce the prefix rules and Chromium's limits.

**M6. CDP isolated worlds are the main world.** *Read.*
- **Where:** `Page.createIsolatedWorld` records a context but does not create a
  realm (`Cdp/Domains/Runtime.cs:620-623`).
- **Impact:**
  - Playwright and Puppeteer utility scripts run beside page script, which can
    tamper with their globals and results.
  - `__obscura_binding_called` lets a page fire `Runtime.bindingCalled` for any
    binding name (`bootstrap.js:92-97`, `CoreOps.cs:383-388`).
- **Fix:**
  - Back each isolated world with its own engine on the page's `V8Runtime`, as
    `FrameRealm` does.
  - Until then, only report binding names registered for the context.

**M7. Memory outside the heap cap.** *Tested.*
- **What escapes:** `MaxRuntimeHeapSize` covers the V8 heap only. It does not
  cover:
  - DOM text and attributes in the C# arena;
  - `PendingBindingCalls`, which is unbounded (`PocketCalculatorState.cs:109`);
  - `ArrayBuffer` backing stores.
- **Tested:**
  - Appending 4 MB text nodes passed 3 GB and the process was killed.
  - Six 512 MB `Uint8Array`s succeeded.
  - One 5 MB word laid out took 2.9 GB.
- **No default:** library and CDP embedders get no heap cap at all.
- **Partly tracked** in todo.md ("heap cap only exists once configured").
- **Fix:**
  - A per-page DOM byte budget.
  - A cap on `PendingBindingCalls`.
  - A default heap cap in `PocketCalculatorJsRuntime`.
  - A per-process budget (`GC.GetGCMemoryInfo`, `V8Runtime.GetHeapInfo`) that
    refuses new work.

**M8. PBKDF2 cannot be interrupted.**
- **Where:** iterations (10M) and output length (1 MiB) are capped separately, but
  the cost is their product, about 3e11 HMACs, run synchronously in the op
  (`CryptoOps.cs:169-184`).
- **Impact:** the CLI's hard deadline caught it. In `serve`, the page's thread is
  pinned for hours.
- **Fix:** cap iterations times output blocks, or run it asynchronously with
  cancellation.

**M9. Serialization mXSS.**
- **Where:** `IsRawTextElement` (`Dom/DomTree.Serialize.cs:211-214`) treats
  `textarea` and `title` as raw text and ignores namespaces.
- **Tested:** `<title>&lt;/title&gt;&lt;img src=x onerror=alert(1)&gt;</title>`
  serializes as `<title></title><img src=x onerror=alert(1)></title>`, in
  `innerHTML`, `outerHTML` and `--dump html`. `<svg><style>` behaves the same way.
- **Impact:** in-page sanitizers that round-trip `innerHTML`, and consumers that
  render dumps, are exposed.
- **Inherited** (`serialize.rs:209`); fix it as a documented deviation.
- **Fix:** raw text only for HTML-namespace `style`, `script`, `xmp`, `iframe`,
  `noembed`, `noframes` and `plaintext` (and `noscript` with scripting on).

**M10. Markdown dump passes markup through.**
- **What:** `--dump markdown` emits text `&lt;script&gt;` as a literal `<script>`
  and emits `javascript:` links. It does not escape `]` / `)` in link text or
  `alt`.
- **Impact:** markdown rendered downstream gets XSS or link spoofing.
- **Fix:**
  - Escape `<`, `>`, `[`, `]`, `(` and `)` in text.
  - Drop non-http(s) link and image schemes.
  - Check the MCP markdown path too.

**M11. Quadratic cost on deep trees.**
- **Tested:**
  - Parsing 50k nested `<div>`s takes 20 s; 200k exceeds 45 s.
  - Building a 20k-deep chain with `appendChild` takes 27 s.
- **Impact:** CPU denial of service in `serve`.
- **Fix:** the parser depth cap in C5 fixes the parse half.

### Low

**L1. MCP HTTP does not check `Host`.** `Http.cs:164-280` never reads `Host`.
- **Impact:** a DNS-rebound page cannot call tools, because browsers send `Origin`
  on POST and the Origin gate refuses it. It can open same-origin SSE GETs.
- **Fix:** add CDP's Host rule, as the MCP spec recommends.

**L2. SSE streams are uncounted.** An SSE connection detaches from the connection
cap and pings forever (`Http.cs:545-575`).
- **Impact:** any admitted client (any local process on loopback without a token)
  can exhaust file descriptors.
- **Fix:** count SSE streams against the cap.

**L3. CDP resource limits.** *Read.*
- 64 MiB per message times 128 connections, each buffered and then decoded to a
  string (`Server.WebSocket.cs:11`).
- Unbounded per-connection reply queues (`:48`).
- No per-connection cap on targets, and no idle timeout.
- The `--workers` balancer proxies without limit (`ServeCommand.cs:246`).

**L4. `Browser.close` substring match.**
- **Where:** `Server.WebSocket.cs:126`. Any message whose text contains
  `"Browser.close"`, for example as a string argument, closes the connection.
- **Fix:** parse the message and compare the method.

**L5. Pipeline hygiene.** *Read.*
- **Triggers:** `.devops/build-nuget.yml` has `trigger: main` but no `pr:` section.
  For a GitHub repository, Azure DevOps then builds pull requests by default, and
  the push step has no branch condition. Fork builds do not get the service
  connection by default, but a same-repository branch PR would push a package.
- **Fix:** add `pr: none`, or `condition: eq(variables['Build.SourceBranch'],
  'refs/heads/main')` on the pack and push steps.
- **Hygiene:**
  - Consider NuGet lock files and package source mapping.
  - Consider pinning the SDK band.
  - `dotnet list package --vulnerable --include-transitive` reported nothing.

**L6. Curated public suffix list** (~450 rules; already in todo.md).
- **Gap:** it lacks, for example, `ngrok-free.app`, `onrender.com`, `fly.dev`,
  `myshopify.com`, `r2.dev` and regional S3 suffixes.
- **Impact:** supercookies across tenants of those hosts, and wrong same-site
  decisions.
- **Fix:** embed the full list.

**L7. Tracker blocklist coverage.** `BlockTrackers` is checked in
`FetchWithProfileUncachedAsync` on the first URL only. It skips redirect hops and
everything through `op_fetch_url` (fetch, XHR, iframes, dynamic scripts).
Inherited.

**L8. `ModifyHeaders` leaks.** `InterceptAction.ModifyHeaders` extends the
client-wide `ExtraHeaders` permanently (`PocketCalculatorHttpClient.cs:136, 636`),
so a header added for one request (say `Authorization`) goes to every later host.
It is library API only; nothing in the engine produces it.

**L9. Cross-origin `pushState`.**
- **What:** `history.pushState({}, '', 'https://other.example/')` succeeds, and
  `location.origin` then reports the foreign origin. Chromium throws
  `SecurityError`.
- **Host state:** cookies and fetch origin are not moved.
- **MCP:** `browser_export_state` labels storage with the page-reported origin, and
  `browser_import_state` applies every origin's storage to the current page
  (`Mcp/Tools.Tier2.cs:629-706`).

**L10. Host code calls page globals.**
- **Where:**
  - `PocketCalculatorOps.Uint8Array` evaluates the page's `Uint8Array` during an
    op (`:468-477`);
  - CDP and MCP snippets use `globalThis._wrap`, `document.querySelector` and the
    page's `Event` constructors. The constructor item is already in todo.md.
- **Impact:** a page can redirect `DOM.setFileInputFiles` or an MCP fill to
  another element, and can obtain trusted event objects.
- **Fix:** use intrinsics captured at bootstrap.

**L11. `RgbImage.Decode` overflow.**
- **Where:** `(int)(width * height * 4)` is computed in `uint`
  (`Browser/RgbImage.cs:73-83`).
- **Impact:** above about 1.07G pixels it wraps, and a heap buffer overflows. The
  only caller decodes the engine's own capture, bounded at 16M pixels, so it is
  not reachable today.
- **Fix:** checked `ulong` math.

**L12. No process backstop in `serve` and `mcp`.**
- **Where:** `HardDeadline` is armed for `fetch` only (`FetchCommand.cs:161`).
  `serve` and `mcp` have nothing that stops H8 or M8.
- **Related:** `Environment.Exit(124)` runs the exit chain that `ProcessExit.cs`
  documents as crash-prone; prefer `ProcessExit.Immediately`.

### Informational

- **I1.** CDP and MCP speak plaintext only, so tokens cross the network in clear
  text on a non-loopback bind. Terminate TLS in front.
- **I2.** Client-controlled CDP method names are logged at WARN
  (`Dispatch.cs:253`). CR/LF in them can forge log lines.
- **I3.** `-v` logs `Using proxy: <url>` with any credentials in it
  (`ServeCommand.cs:30`).
- **I4.** The cookie jar file is written with the default umask
  (`CookieJar.cs:655`); on most systems other local users can read it.
- **I5.** `serve --workers` does not pass `--allow-file-access`, `--storage-dir`,
  `--max-connections` or `--allow-private-network` to its workers. This fails
  safe but surprises operators.
- **I6.** CDP clients can set the internal `__initiator`, `__userActivated`,
  `__method` and `__body` parameters of `Page.navigate`.
- **I7.** There is no HSTS store and no mixed-content blocking: http subresources
  load on https pages.
- **I8.** The custom-root certificate validator (`SSL_CERT_FILE`/`_DIR`) does not
  check the serverAuth EKU and uses `RevocationMode.NoCheck`.
- **I9.** The SSRF deny-set lacks `fec0::/10` (deprecated site-local) and Teredo
  `2001::/32`.
- **I10.** ClearScript's `EngineInternal` global and the `__obscura_*` names are
  visible to `in` and `getOwnPropertyNames`, which is a fingerprinting tell. The
  `__obscura_*` item is in todo.md.

### Already tracked in `todo.md`

These were confirmed but are listed there already:
- the CDP object store and frame registries being page-writable
  (`__obscura_objects`, `__obscura_frameObjects`, and related);
- `dispatchEvent` not clearing `isTrusted` on re-dispatch;
- host snippets using the page's `Event` constructors;
- `ImageAgent` having no SSRF check;
- the curated public suffix list;
- the heap cap existing only once configured.

## Checked and holding

These were attacked during the review and held.

- **Op table:** the op table, `Deno`, the handoff globals and `__obscura_host` are
  all unreachable from page script.
- **Engine surface:**
  - No .NET types are exposed.
  - Reflection is off.
  - No host-script builder was found with string injection.
- **Trusted events:** they cannot be forged, and `WeakSet.prototype` tampering
  does not reach the set.
- **SSRF by IP literal:** blocked from `fetch()` in every form tried:
  - decimal, octal and hex forms;
  - `127.1`, `0`, `[::]`, `[::1]`, `[fe80::1]`;
  - `[::ffff:127.0.0.1]`, `[::127.0.0.1]`, NAT64;
  - a trailing dot, and percent-encoding.
- **SSRF by name:** a name resolving to loopback (`localtest.me`) is blocked,
  because the check runs at connect time.
- **Interception and redirects:**
  - CDP `Fetch` interception rewrites are re-validated.
  - Redirects are re-validated on every hop.
  - Credentials are stripped on cross-origin redirects.
- **Non-module `file:` loads:** classic `<script src=file:>`, `<link href=file:>`,
  `<img src=file:>` and `fetch('file:')` from a web page are refused.
- **Cookies:**
  - `document.cookie` follows the host-side URL; a cross-origin `pushState` does
    not move it.
  - HttpOnly cookies are hidden from script and cannot be written by it.
- **Control planes:**
  - CDP refuses any `Origin`, a foreign `Host` and a wrong token, and compares the
    token in fixed time.
  - MCP refuses a foreign `Origin` and compares its token in fixed time.
  - The token is never read from the query string and never logged.
- **Parsers:**
  - SVG parsing ignores DTDs and has no external entity resolver.
  - CSS block nesting 100k deep is fine.
  - Deep `innerHTML`, `cloneNode`, `textContent`, `querySelectorAll` and every
    `--dump` format at 20k to 50k depth are correct, only slow.
- **Native interop:**
  - HarfBuzz font blobs live in unmanaged memory (`MemoryMode.Duplicate`).
  - Every other `stackalloc` has a fixed size.
  - The code has no .NET `Regex`, so there is no ReDoS surface there.
- **Output limits:** the PDF and capture limits are sound.
- **Dependencies:** no known-vulnerable packages.

## Remediation plan

Grouped by root cause, in the order that closes the most with the least work:

1. **Move origin decisions to the host** (C1, C2, C3, H4, L9). Ops take no origin
   from JS. The host derives it from the calling realm, keeps internal-load bodies
   out of the page realm, computes `originClean` from the response, and decides
   frame access with `FrameRealm`. The shim keeps frame state in closure
   `WeakMap`s and uses captured intrinsics.
2. **One `file:` gate in the transport, keyed on the initiator** (C4, H1, H2, H3).
   `ValidateUrl` refuses `file:` unless the request is an operator-started
   top-level navigation, or a subresource of a `file:` document with file access
   granted. Every navigation entry point then inherits it.
3. **Stack safety** (C5, C6, H5, M11). Add a parser depth cap of 512, a selector
   nesting cap, and `EnsureSufficientExecutionStack` on the recursive render
   paths. Replace the unbounded `stackalloc`.
4. **Decode and allocation budgets** (H6, H7, M4, M7). Bound WOFF output, check
   image pixels before decoding, clip rasterization, set a DOM byte budget, cap
   concurrent fetches, and give embedders a default heap cap.
5. **Budgets for work inside ops** (H8, M8, L12). Add a cooperative step budget in
   matching, layout and SVG expansion, bound PBKDF2 total cost, and add a process
   backstop for `serve` and `mcp`, or run a page per worker.
6. **Transport and cookie hygiene** (M2, M3, M5, L6, L7, L8). Turn off ambient
   proxies, time out body reads, enforce cookie prefixes and limits, and embed the
   full public suffix list.
7. **Control-plane polish** (M1, M6, L1 to L5, I1 to I6).
8. **Output encoding** (M9, M10).

Each fix that changes behaviour relative to the Rust reference is a deliberate
deviation: comment it at the site and record it under "Known deviations" in
`todo.md`, as `CLAUDE.md` requires. Several of these (C4, M2, M9, L7) are
inherited, so report them upstream too.
