# Upstream review, September 2026

Upstream Obscura (`https://github.com/h4ckf0r0day/obscura`) reviewed from the fork
point `727cc46` (2026-09-06, the commit `.reference/obscura/UPSTREAM.md` names) to
`1a3169d` (2026-09-20): 103 commits, 57 of them not merges. Each commit was read
against the C# port on the branch that moved the Rust tree to `.reference/`, with
the known deviations in `todo.md` and `dotnet/docs/round3-findings.md` in view, and
most gaps were confirmed by running the upstream test's input through the C# CLI.

Nothing here has been ported yet. `.reference/obscura/` is still at `727cc46`; it
advances only once these are ported or explicitly declined (CLAUDE.md, "Merging
upstream changes"). The open items are queued in `todo.md` under "Upstream sync
727cc46..1a3169d".

## Outcome

| Area | Commits | Port | Partial | Already | Conflict | Skip |
|---|---:|---:|---:|---:|---:|---:|
| CDP | 15 | 14 | 0 | 0 | 1 | 0 |
| JS / DOM | 17 | 15 | 1 | 0 | 0 | 1 |
| Net / MCP / security / release | 10 | 6 | 2 | 0 | 0 | 2 |
| Render / layout / browser | 9 | 6 | 3 | 0 | 0 | 0 |

`a156914` spans every area and is counted in each. Upstream-only README, AGENTS.md
and sponsor-logo commits are left out. Very little of this was already in the port:
the C# fixes made independently (the form-state mirror, the retained stylesheet
design, several flex/inline layout fixes inside `a156914`) cover parts of `88998d6`,
`deef294` and `a156914`, and in a few places do better than upstream.

## Security first: the C# port is exposed today

In the order the reviews suggest fixing them:

1. **MCP HTTP transport** (`04418a5` G): `Access-Control-Allow-Origin: *` with no
   allowlist, any Content-Type, no token, no header/batch/id limits, and
   `browser_navigate` accepts `file://`. Any website a user visits can drive a
   loopback MCP server and read local files.
2. **Page script reaches the op table** (`04418a5` A, probed): `globalThis.Deno` is
   left on the global, so `Deno.core.ops` is callable from page code.
3. **Render resource loads bypass the SSRF guard** (`97ff86d`, probed): synchronous
   layout loads images and SVG through `ImageAgent` with no private-network check,
   no blocklist, no cookies and no proxy. A `file://` page fetched
   `http://127.0.0.1` from `getBoundingClientRect` without `--allow-private-network`.
4. **fetch()/XHR confidentiality** (`04418a5` C): `Set-Cookie` and unexposed
   cross-origin headers reach script; `no-cors` responses expose status and body.
5. **CORS** (`04f0475`, `05846de`): the preflight checks only Allow-Origin and then
   sends the unsafe request anyway; redirect hops are not CORS-checked.
6. **Credentials on redirects** (`ebe5973`): `Authorization`/`Cookie` are re-sent on
   cross-origin hops.
7. **iframe origin from `src`, not the final URL** (`04418a5` D).
8. **Early URL commit on navigation** (`4778192`): `op_navigate` sets the page URL
   before the navigation commits, which bypasses the cookie same-origin check.
9. **HttpOnly cookies writable from `document.cookie`** (`b369f78`).
10. **Cross-origin stylesheet bytes readable** (`04418a5` B, probed):
    `__obscura_css`, `__obscura_linkedStylesheetCss`, and a page-settable
    `sheet._originClean`.
11. **CDP server** (`04418a5` F, `0671d94`): no token, no Origin refusal, no Host
    check against DNS rebinding; current Chromium refuses both.
12. **Cookie jar rules** (`04418a5` E): no SameSite enforcement, no Secure-origin
    or `SameSite=None`-without-Secure checks, multi-label public suffixes not
    rejected, invalid Domain stored as host-only, host-only lost on CDP import,
    `IsolatedCopy` and `cookies.json`.
13. **Denial of service**: nested CSS `calc()` is quadratic with no depth limit
    (`04418a5` H, probed: 10,000 levels took 15 s); `crypto.getRandomValues` /
    random bytes have no cap (`c2e6fb2`); oversized transform layers fail the
    whole screenshot (`8395f29`).

## Decisions needed

- **`d792bae`, one live JS isolate per CDP connection.** `todo.md` ("Page suspension
  stays") keeps only the driven page's isolate alive, to bound memory. Upstream now
  keeps every page live. Suspension drops page A's globals, listeners and timers
  while a client drives page B, which breaks Playwright `connectOverCDP` with several
  pages and is not what Chromium does. Recommended: measure the memory cost the todo
  entry asks for, then port.
- **`343fdc7` `--font-dir`.** Loading operator font files gives up identical output
  across hosts, against the embedded-fonts-only rule. The cross-document font cache
  in the same commit is worth porting on its own for speed.
- **Where upstream's fix is not Chromium's behaviour**, port Chromium's and record a
  deviation: `fetch()` uppercases only DELETE/GET/HEAD/OPTIONS/POST/PUT (`2e752b9`);
  `window` joins the event path only when the path reaches the document, focus events
  fire only on focusable connected elements, `offsetX` is from the padding edge, and a
  default `<body>` offsetParent gives `offsetTop` 8 (`a156914`); text-decoration
  positions come from font metrics, not upstream's 0.8/0.3 constants (`c68fa2b`);
  the hit test must follow the port's paint band order, because upstream's
  "deeper node wins" tie-break lets an in-flow node beat a later positioned overlay
  (`a156914`); `form.reset()` should not wipe `<textarea>` defaults or leave the dirty
  flag set (`deef294`).

## Found along the way

A left float, then a right float, then another left float, puts the third float
below the first in C# (`[0, 421]`) where it belongs beside it (`[100, 0]`).
`FloatContext` alone places it correctly, so the fault is in the `BlockLayout.cs`
caller (around line 796). Not from any upstream commit.

```html
<!doctype html><body style="margin:0;width:1000px"><div style="float:left;width:100px;height:400px"></div><div style="float:right;width:100px;height:400px"></div><div style="float:left;width:100px;height:400px"></div></body>
```

The per-commit detail follows, one section per area: what changed upstream, the C#
counterpart with file and line, the evidence, and the suggested change and test.
Line numbers are as of this review.

## Upstream review: net / MCP / security / release commits

Upstream range reviewed against fork point 727cc46. C# paths are relative to
`dotnet/src` unless absolute. "Vulnerable" findings were verified by
reading the C# code; the ones marked *(probed)* were also run against the existing
Release CLI build (`PocketCalculator.Cli/bin/Release/net10.0/PocketCalculator.Cli`). No file outside this
report was modified.

| sha | title | class |
|---|---|---|
| 04418a5 | fix: harden security boundaries | **PORT** (many parts; C# vulnerable today) |
| 9ced496 | fix(net): allow disabling stealth tracker blocking | PORT (small) |
| 7b0d538 | Update rustls (RUSTSEC-2026-0285) | SKIP |
| b369f78 | document.cookie must not touch HttpOnly cookies (#915) | **PORT** (vulnerable) |
| d1e3e77 | preserve multi-valued response headers (#913) | PORT |
| 61f8e68 | mcp: include script requests in network history | PORT |
| bf11721 | mcp: collect page console messages | PORT |
| d6c9ef5 | release: shrink binaries, expose plugin interfaces | PARTIAL: port the JS globals, skip the Cargo profile |
| 343fdc7 | render: cache font databases across documents | PARTIAL (perf port recommended; `--font-dir` is a decision) |
| e4c28d9 | test(browser): inspect attached frame during cancellation | SKIP (C# test already immune) |

---

### 04418a5 - fix: harden security boundaries (no PR number)

This commit covers several separate things. They are split below by area. Each part says
whether C# is vulnerable today.

#### A. Page script can reach the privileged op table - PORT, vulnerable *(probed)*

Rust: bootstrap now captures `globalThis.Deno.core` in a closure-private
`const __obscuraCore`, and every call site uses it (about 90 lines). Once the ops are
bound, `take_ops_handoff` / `share_ops_with_realm` delete `globalThis.Deno` as well as
`__obscura_core_handoff`, in the main realm and in child realms. `Runtime.addBinding` now
goes through a narrow, frozen `globalThis.__obscura_binding_called(name, payload)`
instead of `Deno.core.ops.op_binding_called`. Tests reach ops through a
`#[cfg(test)]`-only `__obscura_test_ops`.

C#: `PocketCalculator.Js/Runtime/BootstrapLoader.cs:83-101` deliberately **keeps**
`globalThis.Deno`. It only makes it non-enumerable, because the shim resolves
`Deno.core.ops.X` at call time (93 sites in `PocketCalculator.Js/js/bootstrap.js`). Probe:
`typeof Deno` is `"object"` and `typeof Deno.core.ops.op_fetch_url` is `"function"` from
page script. Page script can therefore call any op directly. For example it can call
`op_post_frame_message` with a forged source origin (the origin is a JS argument), call
`op_fetch_url` with the new `internal_load` flag (see C), call `op_navigate`,
`op_frame_document_ready` and `op_set_cookie`, and read cross-origin CSS through
`op_dom("get_external_stylesheet_css", …)` (see B). `PocketCalculator.Cdp/Domains/Runtime.cs:305`
still emits `Deno.core.ops.op_binding_called`.

Suggested C#: in our bootstrap.js add `const __obscuraCore = globalThis.Deno.core;` at the
top of the IIFE and rewrite every `Deno.core.` to `__obscuraCore.` (a mechanical change;
the IIFE closes over it). Then change the postamble in `BootstrapLoader.Install` to
`delete globalThis.Deno` and remove the comment explaining why it stays. Apply the same
deletion to child realms, wherever the port re-creates `Deno` for a frame realm. Add the
frozen `__obscura_binding_called` and switch `Runtime.cs:305` to it. Tests that poke
`Deno.core.ops` need a test-only exposure, for example a `RuntimeFixture` option that
re-publishes the ops object. Test: port `page_script_cannot_reach_deno_core_or_bootstrap_handoff`
(`[typeof Deno, typeof __obscuraCore, typeof __obscura_core_handoff]` must all be
`"undefined"`, and DOM ops must still work). Record under Known deviations that the
"Deno stays" decision is reversed.

#### B. Cross-origin stylesheet bytes are readable by page script - PORT, vulnerable *(probed)*

Rust:
- It drops `globalThis.__obscura_css`, which was every fetched author sheet concatenated
  into a page global, and the synthetic `<style data-obscura-external-stylesheets>` /
  `data-obscura-inline-import` elements.
- Fetched CSS goes into native `DomTree::external_stylesheets` as `{sources, origin_clean}`.
  Three new ops manage it: `op_external_stylesheet_set/remove/get`. `get` returns
  `{"originClean":false}` without the bytes when the sheet is not clean.
- Origin-clean is computed on the **response URL** (after redirects) and is
  **tainted by any cross-origin `@import`** (`stylesheet_graph_is_origin_clean`, plus the
  shim's `_fetchLinkedCss` now returns `{css, responseUrl, originClean}`).
- CSSOM private state moves into a `WeakMap`, so page script cannot flip
  `sheet._originClean`.
- `__obscura_registerLinkedStylesheet` is deleted after static registration.
- `_loadLinkedStylesheet` computes `pageOrigin` from the document URL. It used to use the
  sheet URL, which made every sheet "same-origin".

C#: we already have the no-synthetic-element half as a deliberate deviation ("A linked
stylesheet leaves no element in the DOM", `DomTree.SetExternalStylesheetCss`,
`Page.Navigation.cs:340-383`), so the *rendering* side matches the new Rust design. The
confidentiality side is missing:
- `PocketCalculator.Browser/Page.Navigation.cs:337` still sets `globalThis.__obscura_css` to all
  fetched CSS, cross-origin included.
- `bootstrap.js:454-455` publishes `__obscura_linkedStylesheetCss(node)` and
  `__obscura_setLinkedStylesheetCss`. The probe shows both are `"function"` to page
  script, so `__obscura_linkedStylesheetCss(link)` returns a cross-origin sheet's text.
  `__obscura_registerLinkedStylesheet` is also still exposed.
- `bootstrap.js:462-473,486`: `_linkedStylesheetIsOriginClean(href)` checks the request
  href, so a same-origin URL that redirects cross-origin counts as clean. `@import`s are
  never considered.
- `bootstrap.js:9062-9073`: a non-clean sheet still stores its text in `sheet._sourceText`,
  and `_originClean` is a plain field the page can set to `true` before reading
  `cssRules`.
- `bootstrap.js:627`: `pageOrigin` is the sheet's own origin, so a dynamic cross-origin
  link is always treated as same-origin when its `@import`s are fetched.

Chromium: `cssRules` on a cross-origin sheet throws `SecurityError`, and no page-reachable
path exposes the bytes.

Suggested C#: remove `__obscura_css`, and check first that nothing in `dotnet/` reads it.
Keep the external-CSS store in `DomTree`, but add an `originClean` bit set by the host.
The origin comes from the final `Response.Url` and the import graph, in
`Page.FetchStylesheetsAsync` / `PageHelpers`. The shim reads the store only through a
closure-private helper that returns nothing when the sheet is not clean. Move CSSOM
private state to a WeakMap. Delete the helper globals after registration. Once A is
fixed, `op_dom` is private too; until then an op_dom command returning the bytes is also
a leak. Because we own the op protocol here (rule 5), the Rust op names need not be
adopted. Tests: port `cross_origin_import_taints_the_materialized_stylesheet_cssom` and
`host_fetched_stylesheet_cascades_without_a_visible_style_node`, and add one fact for a
same-origin href that 302s cross-origin.

#### C. fetch()/XHR response confidentiality - PORT, vulnerable

Rust (`ops.rs`):
- A new `visible_response_headers` always strips `set-cookie` / `set-cookie2`. For a
  cross-origin response it exposes only the 7 CORS-safelisted response headers plus
  those named in `Access-Control-Expose-Headers` (`*` counts only when credentials are
  not `include`).
- `no-cors` cross-origin responses become truly opaque: `status:0`, `body:""`,
  `bodyBase64:""`, `headers:{}`.
- A new 8th op argument `internal_load: bool` lets the shim's own subresource loads
  (dynamic classic scripts, linked CSS, iframes) still get the body. The public `fetch()`
  passes `false`.
- The shim turns an opaque response into `new Response(null, …)`, and the fallback
  `Response` constructor keeps `status: 0` (`init.status === undefined ? 200 : …`).
- The same rules apply to `Fetch.fulfillRequest` interception results
  (`intercept_fulfill_response`).

C#: `PocketCalculator.Js/Ops/FetchOps.cs:728-746` returns the real status, body and **all**
headers, Set-Cookie included, for every mode. `opaque` is only a flag, and the shim still
builds a body from it (`bootstrap.js:7535`). So `fetch(crossOrigin, {mode:'no-cors'})`
returns the cross-origin body, and any response exposes HttpOnly `Set-Cookie` values to
page JS. `bootstrap.js:7985` has the `init.status || 200` bug, so status 0 reads as 200.
The binding at `PocketCalculator.Js/Ops/PocketCalculatorOps.cs:192` takes 7 args.

Wire note: `op_fetch_url`'s JSON result is inside our op contract, and the key set
(`status, body, bodyBase64, requestId, url, redirected, opaque, headers`) is unchanged.
Only the values change. Header ordering was already non-byte-matchable (Known deviation
"Two op payloads cannot be byte-matched").

Suggested C#: add an 8th `internalLoad` parameter to `OpFetchUrl` and its binding. Add a
`VisibleResponseHeaders` helper that filters in both the network path and the intercept
`Fulfill` path (`FetchOps.cs:359`). Blank status, body and headers when
`!internalLoad && mode=="no-cors" && crossedOrigin`. In the shim, pass `true` from
`__fetchDynClassicScript`, `_fetchLinkedCss` and the iframe loader, pass `false` from
`fetch`, and use a null body for opaque responses. Fix the `Response` status default.
Tests: port `response_headers_hide_cookies_and_unexposed_cross_origin_fields`,
`intercepted_internal_load_keeps_cross_origin_body_private_but_usable`,
`cross_origin_dynamic_script_loads_without_exposing_fetch_body` and the extended no-cors
opaque assertion (`status 0, body "", bodyIsNull, headerCount 0`).

#### D. iframe same-origin check uses `src`, not the final URL - PORT, vulnerable

Rust: iframes load through `op_fetch_url(..., internal_load=true)`, record
`_iframeLoadedUrl = response.url`, and hand the final URL to `op_frame_document_ready`.
`contentDocument` compares the page origin against `_iframeLoadedUrl`. The old
"no `://` in src means allow" escape hatch is gone.

C#: `bootstrap.js:4315` uses `fetch(fullUrl,{mode:'no-cors'})`, which after C would
break iframe loading. `bootstrap.js:4360` compares against `this.src`. A same-origin
`src` that redirects cross-origin therefore yields a readable `contentDocument` of
cross-origin content.

Suggested C#: port the shim hunk as written. Test: port
`iframe_origin_follows_the_final_redirect_url`.

#### E. Cookie jar: RFC 6265bis write/send rules - PORT, partly vulnerable

Rust (`cookies.rs`, `client.rs`, `wreq_client.rs`, `ops.rs`):
1. **Invalid Domain rejects the cookie.** It used to be stored host-only on the origin.
   The public suffix is checked with the `psl` crate (multi-label and private suffixes
   such as `co.uk` and `github.io`). A Domain equal to a public-suffix host is kept
   host-only.
2. **`Secure` from an `http:` origin is rejected. `SameSite=None` without `Secure` is
   rejected.** An insecure origin cannot overwrite or shadow a Secure cookie
   (`secure_cookie_conflicts`). This applies to both `set_cookie` and
   `set_cookie_from_js`.
3. **SameSite is enforced on send.** `SameSiteContext {SameSite, CrossSiteTopLevelSafe,
   CrossSite}` is derived from `request.initiator` via registrable-domain `same_site()`,
   with top-level `Navigate` + GET/HEAD counting as lax-allowed. `op_fetch_url` computes
   the context from the page origin. `get_cookie_header` stays as a same-site alias.
4. **Host-only survives import and copy.** `parse_cdp_cookie` sets `host_only` when the
   CDP cookie has `url` and no `domain`. `set_cookies_from_cdp_with_scope` is used by
   `Network.setCookie(s)` and `Storage.setCookies`. `cookies.json` gains an optional
   `"hostOnly"` key, where absent means domain-scoped. `BrowserContext::isolated_copy`
   uses `copy_from`, a raw clone that keeps host-only. Import also skips a
   public-suffix domain and a `SameSite=None` cookie without Secure.

C# (`PocketCalculator.Net/Cookies/CookieJar.cs`): none of this is present.
- `TryResolveCookieDomain` (574-608) keeps the old "ignore the attribute, store
  host-only" behaviour. Its comment still says no PSL is bundled.
- `Store` (96-217) has no Secure-origin, SameSite=None or secure-overlay checks.
- `Collect` (225-275) ignores `SameSite` entirely. **This is the concrete vulnerability**:
  a cross-site subresource or `fetch()` with `credentials:'include'` carries
  `SameSite=Strict/Lax` cookies. Both callers are affected: `PocketCalculatorHttpClient.cs:796-797`
  and `FetchOps.cs:506`.
- `SetCookiesFromCdp` hard-codes `HostOnly = false` (340-342).
- `BrowserContext.cs:180` copies through `GetAllCookies` / `SetCookiesFromCdp`, which
  widens every host-only cookie to its subdomains in each CDP connection's isolated copy.
  That is another leak.
- `SaveToFile` / `LoadFromFile` drop host-only.

Chromium agrees with Rust on 1-3. It rejects a non-matching Domain, rejects
`SameSite=None` without Secure, applies "Leave Secure Cookies Alone", and enforces
SameSite with Lax-by-default. So these are ports, not conflicts.

PSL: `PocketCalculator.Net` cannot use `PocketCalculator.Js/Url/PublicSuffixList.cs`, because Js references
Net and not the other way round. Move `PublicSuffixList` down into `PocketCalculator.Net` and have
Js use it from there. It is curated (~450 multi-label suffixes; see the Known deviation
"The public suffix list is curated, not complete"), so the coverage gap carries over to
cookies. Check that `co.uk` and `github.io` are in it, since both are in the Rust test.

Wire: `cookies.json` must accept files without `hostOnly` and write
`"hostOnly": true|false`, placed after the flattened CookieInfo fields to match serde's
flatten output. `CookieInfo` / CDP `Network.getCookies` payloads are unchanged.

Suggested C#: port `SameSiteContext`, `SameSite(Uri, Uri)`, `GetCookieHeaderInContext`,
`SecureCookieConflicts`, the new `TryResolveCookieDomain`, `SetCookiesFromCdpWithScope`,
`CopyFrom` and a `PersistedCookie` DTO. Pass the context from
`PocketCalculatorHttpClient.SendAsync` (the initiator is already on `ResourceRequest.Initiator`,
`Requests.cs:132`) and from `FetchOps` (the page origin). Make `CookieParams.Parse` return
host-only. Tests: port `multi_label_and_private_public_suffixes_are_rejected`,
`insecure_origin_cannot_set_or_overwrite_secure_cookie`,
`host_only_scope_survives_save_and_load`, `same_site_is_enforced_for_subresources`,
`same_site_none_requires_secure`, the changed attacker-domain test (the cookie is now
*absent* on the attacker origin), the `cookie_params` host-only facts and the
`context.rs` isolated-copy host-only assertion.

#### F. CDP control-plane authentication - PORT, vulnerable

Rust `server.rs`:
- `POCKETCALCULATOR_CDP_TOKEN` must be at least 32 bytes. A non-loopback `--host` without a token
  is refused at startup.
- Every discovery and WS request is refused if it carries **any `Origin` header** (403).
- The `Host` header must match the bind IP and port (loopback accepts any loopback or
  `localhost`). This is DNS-rebinding protection (403).
- `Authorization: Bearer <token>` is compared in constant time (401).
- A request head that fills the peek buffer without `\r\n\r\n` gets 431, where it used to
  be classified with what had arrived.
- The response is a JSON body `{"error":"…"}` with `Connection: close`.

C#: `PocketCalculator.Cdp/Server.cs:180-254` has no token, no Origin check and no Host check.
`Server.cs:652` still treats `n == HttpPeekBuf` as a complete head. A web page can
therefore reach a loopback CDP port through DNS rebinding (`/json/version` → WS URL), and
any non-loopback bind is unauthenticated.

Chromium: since M111 it refuses WS upgrades that carry a non-allowlisted `Origin`
(`--remote-allow-origins`) and checks that `Host` is an IP or `localhost`, so Rust is
aligned. Puppeteer and Playwright over Node `ws` send no Origin.

Suggested C#: port `ControlRefusal`, `HostMatchesBind`, `BearerAuthorized` (use
`CryptographicOperations.FixedTimeEquals`) and the startup check into `Server.cs` /
`ServerSupport.cs`, and call them first in `AcceptDispatch`. The refusal response bytes
should match Rust's exactly. Tests: port `native_loopback_cdp_request_is_allowed`,
`browser_origin_and_rebound_host_are_refused` and `configured_cdp_token_is_mandatory`.
Also check that `CdpContext` / the Cli `serve` path surfaces the startup error.

#### G. MCP HTTP transport - PORT, vulnerable (the most serious item)

Rust `http.rs`:
- `POCKETCALCULATOR_MCP_TOKEN`, with a non-loopback bind refused without one.
- Browser `Origin` is **denied by default**; unset allowlist no longer means permissive.
- No `Access-Control-Allow-Origin: *` is ever sent.
- POST requires `Content-Type: application/json` (415).
- `X-API-Key` is removed from the preflight's allowed headers.
- Limits: body cap 16 MiB → 1 MiB, request line 8 KiB, header line 16 KiB, headers
  64 KiB, batch 1..=64 items, JSON-RPC `id` ≤1 KiB and not an array or object.
- 128 concurrent connections through a semaphore; requests are funnelled to one
  dispatcher over an mpsc(32) channel instead of connections being served sequentially.
- 401 and 415 status texts.
- `lib.rs`: `browser_navigate` rejects `file://`.

C# `PocketCalculator.Mcp/Http.cs`:
- `MaxBodyBytes = 16 MiB` (line 28).
- `OriginAllowed` is permissive when unset (234).
- `CorsHeader` emits `Access-Control-Allow-Origin: *` when unset (269).
- No token and no Content-Type check.
- `LineReader` is unbounded (≈604).
- No batch or id limits (491).
- Sequential connection handling (`RunAsync`, 330).
- `Tools.NavigateAsync` (`Tools.cs:60`) has no `file:` check, and the
  `BrowserContext.AllowFileAccess` gate is only enforced by the CDP domains.

Concretely vulnerable today: any website the user visits can send a CORS "simple"
`POST text/plain` to `http://127.0.0.1:<port>/mcp` and read the reply, because of the
`*`. It can then drive the browser, including `browser_navigate` to
`file:///home/...` followed by a text snapshot, which reads local files.

Suggested C#: port all of the above into `Http.cs` and add the `file:` refusal in
`Tools.NavigateAsync`, with the error text `file:// navigation is disabled for MCP`.
Tests: port `no_allowlist_refuses_browser_callers`, `bearer_token_is_required_when_configured`,
`mcp_navigation_rejects_local_files` and the rewritten `cors_preflight.rs` (expect 403 and
no `*`). Update `sse_stream_does_not_wedge` (no Origin on the preflight), and make the
stalled-body test send `Content-Type: application/json`.

#### H. CSS math nesting guard - PORT, DoS *(probed)*

Rust: `css_math_nesting_is_safe` (depth ≤64 parentheses) at the top of
`resolve_contextual` and `resolve_length`.

C#: `PocketCalculator.Render/Style/StylePrimitives.cs:710 ResolveLength` and
`PocketCalculator.Render/Css/CssLength.cs:20 ResolveContextual` recurse with no bound and slice a
substring at each level, which is quadratic. Probe with `<div style="width:calc(calc(...1px...))">`:

| depth | wall time |
|---|---|
| 5,000 | 3.2 s |
| 10,000 | 15 s |
| 20,000 | killed by the 45 s CLI hard deadline |

It did not stack-overflow before the timeout, but a ~200 KB inline style pins a page, and
a larger input risks an uncatchable `StackOverflowException`.

Suggested C#: add the same scan to both entry points, plus the one in the grid `calc()`
handle path if it recurses separately. Test: port
`deeply_nested_css_math_is_rejected_without_recursing`.

#### I. CLI main thread with a 512 MiB stack - SKIP (not reproduced)

Rust runs `main` on a 512 MiB-stack thread, because V8's stack guard derives from the
native thread. C#: V8 runs on whatever thread ClearScript is entered from. A 50,000-deep
`<div>` document loads and evaluates fine *(probed; 20 s, no crash)*. If a crash shows
up later, the ClearScript equivalent is `V8ScriptEngine.MaxRuntimeStackUsage` plus a
dedicated thread with `maxStackSize`. No action now.

#### J. Docs / Dockerfile / README - SKIP

These are Rust-repo docs. There is no Dockerfile under `dotnet/`. Mirror the new env vars
(`POCKETCALCULATOR_CDP_TOKEN`, `POCKETCALCULATOR_MCP_TOKEN`, the changed meaning of
`POCKETCALCULATOR_MCP_ALLOWED_ORIGINS`) in any C# docs once F and G land.

#### K. Small items

- `obscura/src/cookie.rs get_for_url` → the same-site alias: no C# change needed
  (`Obscura/Api/Cookie.cs`).
- `PocketCalculatorHttpClient` cache-key cookie check → same-site alias: no change.
- `bootstrap.js _loadLinkedStylesheet` reflects the `rel` / `media` / `disabled` IDL
  properties into attributes. Useful with B; C# currently gates on `c.disabled` inside the
  shim (`bootstrap.js:634`).

---

### 9ced496 - fix(net): allow disabling stealth tracker blocking - PORT (small)

Rust: `POCKETCALCULATOR_BLOCK_TRACKERS` (`0` / `false` / `no` / `off`, case-insensitive, trimmed)
turns off the blocklist in the **stealth** (`wreq`) client. It stays on for unset, empty
and unrecognised values. The reqwest client's `block_trackers` is still set
unconditionally by `context.rs` under `--stealth`.

C#: there is no stealth transport. `BrowserContext.cs:41-44` sets
`PocketCalculatorHttpClient.BlockTrackers = true` under stealth, and that client carries all
stealth traffic. The env var does not exist (grep: no hits).

Suggested C#: add `TrackerBlockingEnabled(string?)` and read it where `BlockTrackers` is
set in `BrowserContext` (both the constructor and `IsolatedCopy`). Add a comment noting
that C# has one transport, so the switch covers what the Rust wreq client covers. Tests:
port `tracker_blocking_environment_defaults_to_enabled` and
`tracker_blocking_respects_host_and_setting`.

### 7b0d538 - Update rustls to address RUSTSEC-2026-0285 - SKIP

This is only a `Cargo.lock` bump (rustls 0.23.40→0.23.45, rustls-webpki 0.103.13→0.103.15).
C# uses `System.Net.Http` / `SslStream` over the OS TLS stack and has no rustls, so it is
not affected.

### b369f78 - document.cookie must not overwrite or delete HttpOnly cookies (#915) - PORT, vulnerable

Rust: in `set_cookie_from_js`, both the delete path (Max-Age/Expires in the past) and the
insert path return without change if the existing `(name, path)` entry on that domain is
HttpOnly (RFC 6265 §5.3).

C#: `CookieJar.Store(..., fromJavaScript: true)` (`CookieJar.cs:181-216`) only ignores an
`HttpOnly` *attribute* sent from JS (163). It still deletes (187) and replaces (215) a
server-set HttpOnly entry. So `document.cookie = "session=x"` clobbers the HttpOnly
session cookie, and `document.cookie = "session=; Max-Age=0"` deletes it. That enables
session fixation and forced logout.

Suggested C#: in both paths, when `fromJavaScript` and the existing entry has `HttpOnly`,
return. Tests: port `js_cannot_overwrite_httponly_cookie`,
`js_cannot_delete_httponly_cookie` and `js_can_still_overwrite_non_httponly_cookie`.
Chromium does the same.

### d1e3e77 - preserve multi-valued response headers (#913) - PORT

Rust: `merge_response_header` folds repeated field lines into one `", "`-joined value
(RFC 9110 §5.3). `set-cookie` stays last-wins, because the jar reads every line
separately. Both clients use it. `op_fetch_url` in `ops.rs` still collects last-wins.

C#: `PocketCalculator.Net/Http/PocketCalculatorHttpClient.cs:860-885` `CollectHeaders`/`LastValue` is
last-wins. `PocketCalculator.Js/Ops/FetchOps.cs:820-839` is also last-wins, matching Rust's
unchanged op.

Suggested C#: fold in `PocketCalculatorHttpClient.CollectHeaders`, with a Set-Cookie exception.
Consider folding in `FetchOps.CollectHeaders` too: Chromium's `Headers.get()` returns
combined values, so that would be a Chromium-backed deviation from Rust and would need a
comment and a todo.md entry. Tests: port `response_headers_preserve_duplicate_values` and
`response_headers_do_not_fold_set_cookie`.

### 61f8e68 - fix(mcp): include script requests in network history - PORT

Rust: `tool_network_requests` calls `page.sync_js_network_events()` before reading
`network_events`, so completed `fetch()`/XHR requests appear.

C#: `PocketCalculator.Mcp/Tools.cs:305-323` reads `page.NetworkEvents` without syncing.
`Page.SyncJsNetworkEvents()` exists (`PocketCalculator.Browser/Page.Network.cs:233`).

Suggested C#: add one line, `page.SyncJsNetworkEvents();`. Test: port
`network_tool_includes_completed_script_fetches`.

### bf11721 - fix(mcp): collect page console messages - PORT

Rust:
- `PocketCalculatorState` gets `pending_console_messages` (a VecDeque capped at 1024, dropping the
  oldest) and `console_messages_enabled`.
- `op_console_msg` pushes `"[{level}] {msg}"` when enabled, before the Runtime-events gate.
- `Page` keeps the flag across runtime replacement (`set_console_messages_enabled`,
  re-applied in `init_js`) and exposes `take_pending_console_messages`.
- MCP enables it on every tab it creates.
- `browser_console_messages` drains into `state.console_messages`, capped at 1024.

C#: `BrowserState._consoleMessages` exists but nothing ever fills it
(`PocketCalculator.Mcp/BrowserState.cs:28`, `Tools.cs:325`). `CoreOps.OpConsoleMsg`
(`PocketCalculator.Js/Ops/CoreOps.cs:172`) only records CDP runtime events. So the tool always
answers "No console messages."

Suggested C#: add the fields to `PocketCalculatorState`, the push in `OpConsoleMsg`, the page-owned
flag re-applied in `Page.InitJs`, `TakePendingConsoleMessages`, the enable calls in
`BrowserState` tab creation, and the drain in `Tools.ConsoleMessages`. The message format
`"[level] text"` is user-visible MCP output and should match. Test: port
`console_tool_returns_page_messages` (inline, click and setTimeout messages).

### d6c9ef5 - fix(release): shrink binaries and expose plugin interfaces - PARTIAL

"Plugin interfaces" here means the `navigator.plugins` DOM interfaces, not a library or
extension API. Nothing in it touches the C# library surface in `Obscura/`.

- `Cargo.toml` `[profile.release-dist]` and `release.yml`: SKIP (Rust build only). The C#
  counterpart is the ReadyToRun publish already documented in CLAUDE.md.
- bootstrap.js: adds `Navigator`, `PluginArray`, `Plugin`, `MimeType` and `MimeTypeArray`
  to the pre-declared non-enumerable list, and assigns them to `globalThis`. PORT. C# has
  the constructors inside the IIFE (`bootstrap.js:6977-7048`), but none reaches the
  global. Probe: all five `typeof` are `"undefined"` and
  `navigator.plugins instanceof PluginArray` is false. Chromium exposes all five as
  non-enumerable `window` properties, and a missing `Navigator` is a common automation
  tell (a stealth surface). Test: port the assertions added to the stealth test in
  `runtime.rs` (`typeof ... === "function"`, `instanceof`, and `enumerable === false`).

### 343fdc7 - fix(render): cache font databases across documents - PARTIAL

Rust:
- (1) One process-wide base font database (14 bundled faces), plus a lazily built emoji
  variant, instead of rebuilding per `TextEngine`.
- (2) An LRU of web-font databases keyed by a content signature: 8 entries or 64 MiB.
- (3) `WebFont.data` becomes `Arc<Vec<u8>>`.
- (4) A new `serve --font-dir DIR` (repeatable, render builds only) recursively loads
  `ttf/ttc/otf/otc` into the base database once per process (and is forwarded to
  workers). It must be configured before the first render. `obscura-js` only re-exports
  `configure_font_directories`.

C#:
- `PocketCalculator.Render/Inline/TextEngine.cs:44-110` builds a fresh `FontDatabase` per
  `TextEngine`, one per render pass.
- `FontDatabase.LoadFontSource` (`Inline/FontDatabase.cs:252`) does `SKData.CreateCopy`
  and `SKTypeface.FromData` for every bundled face each time, including Noto Color Emoji
  when `loadEmoji`.
- Only the raw bytes are cached (`FontAssets.Load`, `FontAssets.cs:30,55`), plus decoded
  WOFF (Known deviation "Decoded web faces are cached").
- The shape cache is already carried across passes (`AdoptShapeCache`), but faces are not.
- `SvgFontDatabase.Shared` (`Paint/SvgRenderer.cs:36`) is already process-wide for SVG.

So (1)-(2) are missing. The perf port is worth doing, but it is not a transliteration:
`FaceRecord` holds mutable per-variation dictionaries and lazily built HarfBuzz faces, and
the CDP server renders on several threads, so a shared base set needs thread-safe
instance caches and must never be disposed by a `TextEngine`. Measure it interleaved, per
CLAUDE.md. This is performance only, and the output is identical.

(4) `--font-dir`: needs a decision rather than a straight port. CLAUDE.md "Fonts" says
the engine never uses host fonts. An opt-in, explicit directory loaded with
`SKTypeface.FromData` does not break the "never FromFamilyName / no fontconfig" rule, but
it does give up byte-identical rasterization across hosts when used. If adopted, add it
to `serve` in `PocketCalculator.Cli/CommandLine/CliDefinition.cs`. Rust skips symlinks and sorts
paths. Since C# has no separate render feature, it is always available. Record it as a
deliberate choice in todo.md.

### e4c28d9 - test(browser): inspect attached frame during cancellation - SKIP

Rust test-only fix: it selects the iframe whose `_frameId` matches the attached frame
instead of `querySelector('iframe')`. The C# counterpart
`CancellingFrameScriptFetchKeepsAllSiblingsResumable`
(`dotnet/tests/PocketCalculator.Browser.Tests/PageTests.cs:815-870`) already
asserts `Assert.Single(page.Frames)` and evaluates in that attached realm through
`EvaluateInFrame(0, ...)` (a recorded port deviation). It never depended on DOM iframe
order, so no change is needed.

---

### Suggested order

1. **G** (MCP: drive-by local-file read from any website).
2. **A** (hide `Deno`), which is a prerequisite for B, C and D being meaningful.
3. **C**, **D** and **b369f78** (cross-origin reads, HttpOnly clobbering).
4. **B**.
5. **F** (CDP auth and rebinding).
6. **E** (SameSite, host-only, PSL).
7. **H**.
8. The non-security ports: 61f8e68, bf11721, d1e3e77, 9ced496 and the d6c9ef5 globals.
9. 343fdc7 caching.

## Upstream review: render / layout / browser commits

Upstream: `727cc46..HEAD`. C# checked at `dotnet/src`. Probes ran with
the existing Release CLI (`dotnet/src/PocketCalculator.Cli/bin/Release/net10.0/pocket-calculator`). Scratch
fixtures are in a scratch directory, and a FloatContext harness is in a scratch directory.
Nothing in the repo was edited.

Commits are listed oldest first, because 99647b4 builds on 97ff86d.

| sha | title | class |
|---|---|---|
| 97ff86d | route resource loads through page transport | **PORT** (security: SSRF and blocklist bypass confirmed) |
| 88998d6 | live input values and checkbox state | **PARTIAL** |
| deef294 | fast form state access (+ form.reset) | **PARTIAL** (perf ALREADY; reset PORT) |
| aaf189f | rounded float boundary abort | **PORT** (C# throws, confirmed) |
| c68fa2b | overline / line-through | **PORT** (confirmed missing) |
| d579bdd | avoid redundant layout and text shaping | **PORT** (both parts missing; perf) |
| 99647b4 | DOM-only screenshots network-free | **PORT** (with 97ff86d) |
| 8395f29 | cap transform-layer dimensions (#1019) | **PORT** (C# fails the whole screenshot, confirmed) |
| a156914 | CDP compat (render/browser parts) | **PARTIAL**: hit test PORT (not upstream's ordering); the rest ALREADY |

---

### 97ff86d - fix(render): route resource loads through page transport

No PR number.

**Upstream behaviour.** A page-owned `RenderResourceCache` is now cache-only
(`fresh_render_resources` calls `set_sync_loading_enabled(false)` whenever the runtime has a
page transport). Layout and paint never open synchronous HTTP. A cache miss is recorded
(`record_sync_miss`, deduplicated and bounded by `max_entries`, carrying url, image profile
and is_font). The page drains the misses with `take_sync_misses` and loads them on its async
transport. That transport applies proxy, cookies, `Network.setBlockedURLs`, `Fetch.enable`
interception patterns, and a page-wide concurrency limit of 16. The results are applied at
runtime event-loop turns and promise waits, and are fenced by `document_generation`. The CDP
server calls `queue_pending_render_resources` before and after every command, and after each
pump turn. Network events are emitted for these loads.

**C# state.** C# still has the fork-point model:
- `RenderResourceCache` defaults to `HttpResourceLoader` with sync loading on
  (`PocketCalculator.Render/Paint/RenderResourceCache.cs:133,144`).
- `GetOrLoad` / `GetOrLoadImage` fall through to `ImageAgent.Get` (`:499-560`, `:765`). That
  is a bare static `HttpClient` with a fixed UA. It has no SSRF check, no blocklist, no
  cookies and no proxy, and it retries with `Thread.Sleep`.
- Sync loading is disabled only around capture (`PocketCalculator.Js/Runtime/PocketCalculatorJsRuntime.Capture.cs:39-52`).
- There is no miss queue (no `TakeSyncMisses` or `RecordSyncMiss`).
- `Page.PrepareScreenshotResourcesAsync` (`PocketCalculator.Browser/Page.Capture.cs:22`) is the older
  DOM-scan warmup only.

**Probe (SSRF).** Setup: a `file://` page, **no** `--allow-private-network`, and script that
appends `<img src=http://127.0.0.1:18777/late.svg>` and then reads `getBoundingClientRect()`.
The local server logged `GET /late.svg` with ImageAgent's UA and Accept header, and the rect
width came back as 20. So synchronous layout fetches loopback, bypassing the SSRF default
(CLAUDE.md invariant) and the blocklist. This is worse than a missing port: it breaks a
load-bearing invariant.

**Suggested C# change.**
- `RenderResourceCache`: add `SyncLoadingEnabled`, `RecordSyncMiss(key, url, profile, isFont)`,
  `TakeSyncMisses()` and `HasSyncMisses`, bounded by `_maxEntries`. Split `FetchBytes` /
  `FetchFontBytes` so `is_font` is carried, and add `SeedShared`.
- `PocketCalculatorState` / `PocketCalculatorJsRuntime.State.cs:173,482`: build the page cache with sync loading
  off whenever `HttpClient` or `StealthClient` is present.
- `PocketCalculator.Browser/Page.Capture.cs`: add `QueuePendingRenderResources`,
  `DrainRenderResourceResults`, `HasPendingRenderResources` and `RetireRenderResources`. Loads
  go through the page transport with `ShouldBlockUrl`, `SubresourceAllowed` and the interception
  patterns, under a shared `SemaphoreSlim(16)`, with results fenced by `DocumentGeneration`.
  Rebuild `PrepareScreenshotResourcesAsync` on top of the miss queue.
- `PocketCalculator.Cdp`: service the render resources before and after each dispatch and after pump
  turns.
- Independently, and cheap: put the SSRF/private-network check in `ImageAgent`, or retire it
  for page runtimes, so a standalone path cannot reach loopback either.

**Tests to port.** `cache_only_misses_are_reported_with_their_request_identity`,
`cache_only_miss_queue_respects_the_cache_entry_limit`, and the page.rs tests
`cache_only_layout_never_blocks_on_a_slow_asset_and_late_bytes_update_geometry`,
`blocked_render_resources_are_never_fetched_by_layout_or_transport`,
`renderer_misses_honour_fetch_interception_patterns`,
`render_resource_loads_share_one_page_wide_concurrency_limit` and
`retired_document_loads_never_seed_the_next_document`. Also add a C# fact for the loopback probe
above.

Note: after this change `getBoundingClientRect` on a freshly inserted `<img>` returns 0 until the
bytes land, which is what Chromium does too (async image load).

### 88998d6 - Render live input values and checkbox state from the DOM

No PR number.

**Upstream behaviour.**
- The DOM holds a `FormControlState {value, checked, indeterminate}` per node, and bootstrap
  reads and writes it through the new `op_dom` commands `get_/set_form_value|checked|indeterminate`.
- `:checked` consults the live checked state.
- `cloneNode` copies value and checked, but not indeterminate. This matches the HTML cloning steps.
- The state is dropped when the node is freed.
- Paint draws checkbox and radio from live state, including an indeterminate dash, and draws
  the input value from live state.

**C# state.**
- ALREADY for value and checked paint: this is the documented C# deviation "Dirty form state
  is mirrored onto the arena" (todo.md:2112). It uses `FormStateMirror`
  (`PocketCalculator.Js/Runtime/FormStateMirror.cs`), `DomTree._dirtyFormValues/_dirtyFormChecked`
  (`PocketCalculator.Dom/DomTree.cs:32-139`, freed at `:138`) and
  `PaintNativeControls.cs:615,655,659`. The op names `set_form_value` / `set_form_checked`
  happen to match upstream.
- Probe with `render-repros/live-form-state.html`: the typed, cleared, password, checked and
  radio controls all paint correctly.
- Missing:
  1. **indeterminate.** It is not mirrored (`_formIndeterminate` is a plain object,
     bootstrap.js:726), and `PaintNativeControls` has no indeterminate glyph. In the probe the
     "Mixed" checkbox paints empty, where Chromium paints the dash.
  2. **`:checked` uses attributes only** (`PocketCalculator.Dom/Selectors/SelectorMatching.cs:484`).
     Probe: `querySelectorAll(':checked').length` is 1, and Chromium gives 2.
  3. **cloneNode does not propagate dirty state.** Probe result is
     `["default","default",false,false]`, where Chromium gives `["current","default",true,false]`.

**Suggested C# change.**
- `SelectorMatching.cs:484`: use `TryGetDirtyFormChecked` before falling back to the attribute.
- `DomTree` clone path: copy the dirty value and checked state onto the clone.
- Add a dirty-indeterminate table, a `set_form_indeterminate` command, a proxy in
  `FormStateMirror` (or call the op directly), and an indeterminate dash in
  `PaintNativeControls` (measure the glyph against Chromium, not against upstream's hand-drawn
  shape).
- `bootstrap.js` is now ours (rule 5), so the `FormStateMirror` rationale ("shared verbatim")
  is stale. Consider calling the ops from bootstrap directly and updating the todo.md entry.
  This is optional.

**Tests.** Port `live_form_values_paint_without_mutating_content_attributes` and
`cloned_controls_keep_current_value_and_checked_state`. Add a `:checked` fact.

### deef294 - perf(render): preserve fast form state access

No PR number.

**Upstream behaviour.** Upstream reverts to JS-side maps as the read cache, lazily seeded
through one `get_form_state` op, and still writes through to the DOM. It also rewrites
`HTMLFormElement.reset()`. The new version dispatches a cancelable `reset` event, restores
checkbox and radio `checked` from the attribute, clears indeterminate, and restores the value
of other inputs from the `value` attribute.

**C# state.**
- Perf: ALREADY. The C# mirror keeps the JS maps authoritative for reads, with no op on read
  (`FormStateMirror.cs`).
- reset: **PORT.** C# still has `reset() { for (f of this.elements) if ('value' in f) f.value = ''; }`
  (`PocketCalculator.Js/js/bootstrap.js:12462`). It dispatches no reset event, sets checkbox and radio
  *value* to '' instead of restoring checkedness, and wipes inputs that had a default `value`.

**Suggested change.** In `bootstrap.js` HTMLFormElement.reset:
- Dispatch the cancelable `reset` event first.
- Per control, clear the dirty state rather than assigning. Delete `_formValues[nid]` and
  `_formChecked[nid]` and mirror the delete, which needs a `deleteProperty` trap and a
  `clear_form_state` op. In Chromium the dirty flag is reset, so a later
  `setAttribute('value')` shows through. Upstream's `f.value = attr` gets that wrong.
- For `<textarea>`, do not `value = ''`: that destroys its default text (child text content).
  Upstream still has this bug.
- For `<select>`, reset selectedness.

**Test.** Port `form_reset_restores_default_input_state_and_pixels`, plus the textarea and
setAttribute-after-reset cases measured in Chromium.

### aaf189f - fix(render): avoid rounded float boundary abort

No PR number.

**Upstream behaviour.** `FloatFitter` accumulated segment heights in f64 and compared the sum
against the float height. With rounded segment ends, the sum said "fits" while
`start_y + height > segment.end` in f32, and the later subdivide panicked. The fix drops
`slot_height`, `add_height` and `fits_vertically` and compares endpoints:
`if float_end > end_segment.y.end { end_idx += 1; continue; }`.

**C# state.** Same bug (`PocketCalculator.Render/Layout/FloatLayout.cs:85,113,116,277-312`). Running
the upstream test case through the harness in a scratch FloatContext harness gives:
`THROW cannot subdivide segment [2259.3333, 2280.3333) at 2259.3333` (`SubdivideSegment`,
`:172-176`).

**Suggested change.** In `FloatLayout.cs`, remove `_slotHeight`, `AddHeight` and
`FitsVertically` from `FloatFitter`. Compute `float floatEnd = startY + floatedBox.Height` after
`startY` is fixed, and replace the `AddHeight`/`FitsVertically` block with
`if (floatEnd > endSegment.YEnd) { endIdx++; continue; }`.

**Test.** Port `float_ending_at_rounded_segment_boundary_does_not_panic` into
`PocketCalculator.Render.Tests` layout tests. It expects the third float at y 1859. `FloatContext` is
public.

**Side finding (not from this commit).** In HTML, a left float, then a right float, then a
left float puts the third float *below* the first: `f6.html` gives `[0,421]`, where Chromium
gives `[100,0]`. The standalone `FloatContext` places it correctly (`x=100, y=0` in the fprobe
harness), so the defect is in the caller (`BlockLayout.cs` ~796, `yOffsetForFloat` / clear
handling) and not in `FloatLayout`. The fork-point Rust may share it. It is worth a separate
ticket.

### c68fa2b - fix(render): paint overline and line-through text decorations

Fixes upstream issue #935.

**Upstream behaviour.**
- `LayoutStyle` gains `overline` / `line_through`, parsed from `text-decoration` and
  `text-decoration-line`, with `none` clearing both.
- They propagate into descendants the same way underline does, by OR.
- Glyph metadata carries 3 decoration bits (`META_FILL_SHIFT` moves 1 -> 3).
- Paint groups contiguous runs per decoration. Offsets are overline `-0.8*size`,
  line-through `-0.3*size` and underline `max(0.12*size, 1)`; thickness is `max(size/14, 1)`.
- `@supports` accepts overline and line-through.

**C# state.** Missing. There is no Overline or LineThrough anywhere in `PocketCalculator.Render`. The
probe with `render-repros/text-decoration-lines.html` paints only the underlines.
`PreparedRender.cs:926` notes that the cascade models underline only.

**Suggested change.**
- Add `Overline` / `LineThrough` to `Core/LayoutStyle.cs`.
- Parse them in `Style/ComputedStyle.cs:2623`.
- Accept them in `Css/CssHooks.cs:154` and `Style/StyleSupports.cs:1314`.
- Propagate them in `Inline/InlineCollect.cs`.
- Add metadata bits in `Inline/TextAttrs.cs`.
- Paint in `Inline/TextEnginePaint.cs`.
- Report them in `getComputedStyle` (`PreparedRender.cs:926-930`), which is a C# extra.
- Chromium wins on geometry. Use the font's OS/2 `yStrikeoutPosition` / `yStrikeoutSize` for
  line-through and the ascent for overline, measured on Chromium, rather than upstream's
  `0.8` / `0.3` constants.
- Keep ancestor propagation: `text-decoration:none` on a descendant does not remove the
  ancestor's line. Upstream does this correctly.

**Tests.** Port `crates/obscura-render/tests/text_decoration.rs` (5 tests) as
`TextDecoration.cs`, and the updated metadata round-trip test.

### d579bdd - fix(render): avoid redundant layout and text shaping

No PR number.

**Upstream behaviour.**
1. `InlineItem.measured` is a per-item 16-entry cache keyed by
   `(width.to_bits(), wrap) -> (w, h)` in `measure_text_with_wrap`, so repeated Taffy probes
   do not reshape.
2. `can_retain_layout_for_tabindex`: a mutation made only of selector-kind `tabindex` changes
   (no shadow roots, stylesheet cache hit, retained plan dirty set empty) returns `previous`
   from `prepare_dom_with_retained_styles_with_animation_state`.

**C# state.**
1. Missing. `TextEngine.MeasureTextWithWrap` (`Inline/TextEngine.cs:714-720`) reshapes and
   re-breaks on every call. C# does have a paragraph `ShapeCache`, a documented deviation
   (`Inline/ShapeCache.cs`), so the HarfBuzz call is memoized. Line breaking, `SourceBuffer`
   cloning and the indent loop in `ShapeWithTextIndent` (`:1146`) still repeat per probe, so
   the redundancy exists in C#.
2. Missing. `PaintApi.PrepareDomWithRetainedStylesWithAnimationState`
   (`Paint/PaintApi.cs:301-360`) has only the WAAPI shortcut.

**Suggested change.**
1. Add the `(float? width bits, Wrap) -> size` cache (16 entries, FIFO) to `InlineItem`. Check
   `MeasureTextWithWrap` against it before shaping. Caveats:
   - `ShapeWithTextIndent` also sets `FirstLineOffset` and leaves `Buffer` in the probed state.
     `Finalize` reshapes unconditionally (`:867`), so paint is safe.
   - Audit any reader of `item.Buffer` between measure and finalize (for example baseline
     queries), because a cache hit leaves the buffer from a different width.
2. Add `CanRetainLayoutForMetadata` in `Dom/RetainedStyle.cs`, in the a156914 form, which also
   covers `data-*`. Call it from `PaintApi`. It must build its stylesheet sources the same way
   the C# `StylesheetCache` key is built, *including* `ExternalStylesheetCss` (a C# deviation).
   Otherwise it never hits.
- Benchmark both interleaved against the current build (CLAUDE.md perf rule).

**Tests.** Port `repeated_measurements_reuse_exact_results_and_preserve_final_paint` and
`unreferenced_metadata_can_retain_layout_but_css_dependencies_cannot`.

### 99647b4 - fix(render): keep DOM-only page screenshots network-free

No PR number.

**Upstream behaviour.** The `Page::screenshot` fallback, which is the path with no runtime
(for example after `suspend_js`) or with a non-matching key, paints with a fresh
`RenderResourceCache` with sync loading disabled. It performs no network requests. The tests
also check that warmup and renderer misses honour blocked URLs on a fresh page.

**C# state.** Missing. `Page.ScreenshotWithAnimationSample`
(`PocketCalculator.Browser/Page.Capture.cs:248-289`) has two fallbacks:
- `ScreenshotUnpreparedWithRetainedResources` uses `state.RenderResources` with sync loading
  **on** (`PocketCalculatorJsRuntime.Capture.cs:153-171`).
- The final `RenderPaint.ScreenshotPngScrolledAtAnimationTimeWithSurfaceColor` uses a default
  HTTP-loading cache.

Both can hit the network through `ImageAgent`, bypassing the blocklist and SSRF.

**Suggested change.**
- Route the last fallback through `ScreenshotPngScrolledAtAnimationTimeWithSurfaceColorAndResources`
  (it already exists, `PaintApi.cs:596`) with a `RenderResourceCache` that has
  `SetSyncLoadingEnabled(false)`.
- Wrap the retained-resources fallback in `WithSyncRenderLoadingDisabled`.

**Tests.** Port `suspended_page_screenshot_never_opens_resource_requests`,
`screenshot_warmup_honours_blocked_urls_on_a_fresh_page` and
`renderer_misses_honour_blocked_urls_on_a_fresh_page` (the last needs 97ff86d).

### 8395f29 - fix(obscura-render): cap transform-layer dimensions (#1019)

**Upstream behaviour.** Before allocating a transform layer, skip that element if the layer is
over `MAX_CAPTURE_DIMENSION` (32768) on a side or over `MAX_CAPTURE_PIXELS` (16M). An allocation
failure also skips the element (`continue`) instead of failing the whole paint.

**C# state.** `PaintDom.cs:729-740` computes `layerWidth` / `layerHeight` uncapped. On a null
`Pixmap.New` it does `return null`, which fails the whole page. `Pixmap.New` itself caps at
512M pixels (`Paint/Pixmap.cs:61-65`), which is up to a 2 GB allocation, and catches allocation
exceptions. So there is no abort, but:
- Probe with `tr.html` (the upstream test page plus a `<p>`): `Error: screenshot failed: page
  has no DOM to render`. One element kills the capture.
- Layers between 16M and 512M pixels still allocate up to 2 GB.

**Suggested change.** In `PaintDom.cs` before `Pixmap.New`, add:
`if (layerWidth > Capture.MaxCaptureDimension || layerHeight > Capture.MaxCaptureDimension || (ulong)layerWidth * layerHeight > Capture.MaxCapturePixels) continue;`
and change the null case to `continue`. Also compute the `(uint)` casts from a clamped float,
so a huge or NaN extent cannot wrap.

**Test.** Port `oversized_transform_layer_does_not_abort_the_whole_paint` (paint returns
non-null).

### a156914 - fix(browser): improve CDP compatibility and concurrency (render/browser parts)

No PR number. The CDP, input and bootstrap parts belong to another reviewer.

| Part | Class | Evidence |
|---|---|---|
| `computed_overflow_axes` (keep hidden/scroll/auto distinct in computed style and for `overflow: inherit`) | ALREADY | Probe: `hidden/hidden, scroll/scroll, scroll/hidden, hidden/auto, inherit -> scroll/scroll`, all Chromium values |
| `pointer-events` in `LayoutStyle`, inherited, reported by getComputedStyle | ALREADY | `Core/LayoutStyle.cs:1321`, `PreparedRender.cs:696` (documented deviation, todo.md:2744) |
| `PreparedRender::hit_test` + `op_layout_hit_test` + `pointerEventsNone` in `op_layout_geometry`; `elementFromPoint` uses it | **PORT (Chromium ordering, not upstream's)** | C# `elementFromPoint` is still the JS O(n) highest-nid loop (`bootstrap.js:16911`) and ignores pointer-events. Probe (`hit.html`): overlay with `pointer-events:none` returns `over`, where Chromium returns `under`. |
| `definite_row_flex_intrinsic_width` (cyclic % flex row keeps content width) | ALREADY | Probe: week 336, month 384, the test's values |
| `inset_absolute_fills_positioned_inline_flex_ancestor` | ALREADY | Probe: button 115x40, overlay 91x48 |
| `block_child_fills_a_definite_width_inline_block` | ALREADY | Probe: host 372, child 372 |
| Shape template cache for repeated plain text (`shape_templates`, 64 entries) | ALREADY (superset) | C# `ShapeCache` memoizes every paragraph across passes (`Inline/ShapeCache.cs`, `TextEngine.AdoptShapeCache`) |
| `can_retain_layout_for_metadata` (tabindex + `data-*`) | PORT | See d579bdd |
| `Page::has_pending_navigation` | PORT with the CDP input change | Only consumer is `obscura-cdp/src/domains/input.rs:272`. C# has no `HasPendingNavigation` |
| rustfmt-only churn in dom.rs / paint.rs | SKIP | formatting |

**Hit test caution.** Upstream breaks ties with "deeper DOM node wins, then later DOM order",
and `stacking_z_index` returns `None` for a positioned `z-index:auto` box. So a later-sibling
absolutely positioned overlay loses to a deeper in-flow descendant. Chromium does the reverse,
because positioned boxes paint above in-flow content. Probe case 2 (`#pos` over `#deep`): C#
currently returns `pos`, which is correct by luck (highest nid). Porting upstream as written
would regress it to `deep`.

Implement `HitTest` in `Paint/PreparedRender.cs` as reverse C# paint order, reusing the band
order in `PaintDom` (negative z, block backgrounds, floats, inline content, positioned z-auto
and z 0 in tree order, positive z). Honour `pointer-events:none`, `visibility:hidden` and
overflow clips (`ScrollPaintState` inherited clip). Then expose `op_layout_hit_test` in
`PocketCalculator.Js/Ops/RenderOps.cs` and use it from `bootstrap.js` `elementFromPoint`. Update the
todo.md:2744 note ("pointer-events is reporting only").

**Tests.** Port the three layout tests (C# already passes them, so they are cheap regression
guards). Add hit-test facts measured in Chromium: pointer-events:none passthrough, a positioned
z-auto overlay over deep flow content, a negative-z layer under flow, and a clip.

## Upstream JS/DOM review (fork point 727cc46)

Method: read each upstream diff, found the C# counterpart, and where it was cheap, ran the upstream test's JS through the C# CLI (`obscura fetch data:... --eval`, Release build of the current tree). Paths below are relative to `dotnet/`. Bootstrap line numbers refer to `src/PocketCalculator.Js/js/bootstrap.js` (the C# copy).

| sha | title | class |
|---|---|---|
| 2b07b76 | document.write script order | PORT |
| 6aef52d | removeAttribute('id') updates id index (#1013) | PORT |
| 729c264 | URL search/hash/port setters (#1008) | PORT |
| f645df2 | Web IDL operations enumerable (#999) | PORT |
| 00dd6c7 | btoa Latin-1 (#996) | PORT |
| 61ec5b3 | node identity / getElementById("") | PORT |
| dc5e60e | CharacterData range semantics | PORT |
| 05846de | CORS on redirect hops + preflight status order (#973) | PORT (after 04f0475) |
| 2e752b9 | fetch method uppercase (#969) | PORT, adjusted (see Chromium note) |
| ebe5973 | strip credentials on cross-origin redirect (#967) | PORT |
| 04f0475 | CORS preflight permissions | PORT |
| 4778192 | op_navigate must not move document URL (#940) | PORT |
| 88d2174 | HTMLSlotElement class + assignedNodes (#930) | PORT |
| c2e6fb2 | HKDF / CSPRNG length caps (#910) | PARTIAL (HKDF already safe; RandomBytes not) |
| 25af144 | parsererror keeps the reason | PORT |
| df8b058 | deno_core / V8 upgrade | SKIP (no JS-visible change the port lacks) |
| a156914 (JS/DOM) | event path, focus events, offset*, MouseEvent, named props, :scope | PORT, parts adjusted (see Chromium notes) |

---

### 2b07b76 - fix(js): preserve document.write script order (no PR number)

**Upstream:** a `<script>` written by `document.write` from a parser script is treated as parser-inserted. A written external classic script without async/defer, or a written inline script, goes into a parser-blocking queue (`__parserBlockingScriptQueue`) that runs in order. `op_dom document_write` raises a native flag `document_write_inserted_script` when a placement is a script, and `Page` then drives `has_pending_parser_blocking_scripts` to empty (bounded by the script deadline) before it runs the next parser script. A written async script is queued behind the blocker, so it does not start before the blocker finishes.

**C#:** none of this exists. `grep ParserBlocking|__documentWriteScripts|inlineCode` finds nothing. `DomOps.cs:584` (`document_write`) sets no flag. The placement loop in `Document.write` (bootstrap ~5827-5850) does not tag scripts. `__prepareInsertedScript` (bootstrap:1905) treats every written script as force-async. `Page.Scripts.cs:365` and `:482` call `ExecuteClassic` with no drain afterwards. So `document.write('<script src=x>')` followed by a later parser script lets the later script run before x.

**Classification:** PORT. This is Chromium behaviour: a document.write'd script blocks the parser.

**Suggested change:**
- `PocketCalculatorState`: add a `DocumentWriteInsertedScript` bool. `DomOps.cs` `document_write`: set it when any placement node is a `<script>`. `document_write_reset`: clear it.
- `PocketCalculatorJsRuntime.EventLoop.cs`, next to `HasPendingLoadDelayingScripts` (:664): add `HasPendingParserBlockingScripts()` and `TakeDocumentWriteInsertedScript()`.
- `Page.Scripts.cs`: generalise `DriveLoadDelayingScriptsAsync` (:66) into a pump that takes a predicate, and call it after both `ExecuteClassic` sites when the flag was taken.
- bootstrap: add `__documentWriteScripts` (WeakSet), the queue and counters, `__obscura_hasPendingParserBlockingScripts` (non-enumerable, and added to the hidden-globals list at the top), the `inlineCode` path in `__runDynScriptTask`, and the branches in `__prepareInsertedScript`.

**Tests:** port `document_write_external_script_blocks_later_parser_scripts` and `document_write_does_not_start_later_async_script_before_blocker` to `tests/PocketCalculator.Browser.Tests/PageTests.cs` using `TestHttpServer`. Also extend the hidden-global assertion in `RuntimeTests`.

### 6aef52d - fix(obscura-js): remove_attribute must update the id_index (#1013)

**Upstream:** `remove_attribute` with name `id` calls `update_id_index(node, old, None)`.

**C#:** `DomOps.cs:432-437` removes the attribute with no index maintenance. `get_element_by_id` (`DomOps.cs:128-147`) and `DomTree.GetElementById` (`DomTree.cs:1201`) only check that the indexed node is connected; they do not re-check its `id`. Probe: after `e.removeAttribute('id')`, `getElementById('test') === e` is `true`. Chromium returns `null`.

**Classification:** PORT.

**Suggested change:** in `remove_attribute`, when `arg2 == "id"`, read the old id first, then call the same id-index update that `set_attribute` / `remove_attribute_ns` use. Also consider making `get_element_by_id` verify the attribute on the indexed hit, as a cheap backstop.

**Test:** `RuntimeTests.GetElementByIdReflectsRemoveAttribute`.

### 729c264 - fix(obscura-js): WHATWG-correct URL search/hash/port setters (#1008)

**Upstream:** `search = "?"` gives an empty, non-null query (href ends in `?`). `hash = "#"` gives an empty fragment. Only `""` nulls either one. The `port` setter parses the leading digits (`"8080abc"` gives 8080).

**C#:** `Url/UrlOps.cs:216-228` nulls the component when the stripped value is empty. `TryParsePortValue` (:300) rejects any non-digit. Probe: `["http://example.com/path","http://example.com/path","http://example.com/"]`. Chromium and the upstream fix give `.../path?`, `.../path#`, `http://example.com:8080/`.

**Classification:** PORT.

**Suggested change:**
- search/hash: `value.Length == 0 ? null : (value strip one leading '?'/'#')`.
- port: take leading ASCII digits; if there are none, leave the port unchanged.
- Chromium note: WHATWG does not skip a leading `+`. `TryParsePortValue` currently accepts `+8080`; with state override the spec returns without change. Verify against Chromium, and use a separate leading-digits helper for the `port` setter so the `host` setter path (:260-290) is not disturbed.

**Test:** `UrlTests.UrlSettersFollowWhatwgSearchHashAndPort`.

### f645df2 - fix(js): make Web IDL operations enumerable on interface prototypes (#999)

**Upstream:** a bootstrap IIFE `_markWebIdlOperationsEnumerable` flips the non-`_` function-valued own props of `MutationObserver`, `IntersectionObserver`, `ResizeObserver`, `PerformanceObserver` and `FileReader` prototypes to enumerable. zone.js `patchClass` (Angular) needs this to find `observe`.

**C#:** absent. Probe: `Object.keys(MutationObserver.prototype)` is `[]`, and the same for IO/RO/PO. `FileReader` gives `["EMPTY","LOADING","DONE"]`, which suggests constants are defined as enumerable prototype data properties. Chromium puts constants on the prototype as enumerable too, so that part is fine.

**Classification:** PORT. It must run before `_markBuiltinsNative`, as upstream places it.

**Note:** C#'s `PerformanceObserver` (bootstrap:10125) is a stub class with `observe/disconnect` only. Chromium's key set is `['observe','disconnect','takeRecords']`. Worth adding `takeRecords` while there.

**Tests:** `RuntimeTests.WebIdlOperationsAreEnumerableOnInterfacePrototypes` and `ZoneJsStyleClassPatchFindsObserverOperations`.

### 00dd6c7 - fix(obscura-js): btoa must encode Latin-1, not UTF-8 (#996)

**C#:** bootstrap:11665 still uses `TextEncoder`. Probe: `btoa('é')` gives `"w6k="`, and `btoa('\u{1F600}')` does not throw. ClearScript's V8 provides no btoa, so the polyfill is live.

**Classification:** PORT. Take the upstream line verbatim: `String(s)`, a charCode loop, and `DOMException(..., "InvalidCharacterError")` above 0xFF.

**Test:** `RuntimeTests.BtoaEncodesLatin1NotUtf8`.

### 61ec5b3 - fix(dom): align node identity and id lookup semantics (no PR)

**Upstream:**
- `contains(this)` is true.
- `isSameNode(null)` is `false` (was `null`).
- `Document.getElementById` coerces with `String(id)` and returns null for `""`.

**C#:** probe of the upstream test gives `[false,null,false,"",""]`. Expected `[true,false,true,"null","undefined"]`. The relevant lines are bootstrap:2292 (`contains`), :2401 (`isSameNode`), :5366 (`getElementById`).

**Classification:** PORT.

**Note:** C# also has `getElementById` on other classes (bootstrap ~5920, 10911, 13321; DocumentFragment/ShadowRoot variants). Chromium applies the same empty-string rule there too, so apply it to each of them.

**Test:** `RuntimeTests.NodeIdentityAndElementIdFollowDomConversionRules`.

### dc5e60e - fix(dom): implement CharacterData range semantics (no PR)

**Upstream:**
- `data = null` gives `""`, and `undefined` gives `"undefined"`.
- `substringData/insertData/deleteData/replaceData/splitText` use ToUint32 offsets and counts, and throw `IndexSizeError` when the offset is greater than the length.
- A missing argument throws `TypeError`.
- `appendChild` on CharacterData throws `HierarchyRequestError`.

**C#:** bootstrap:2412-2470 is the pre-fix code. Probe of the upstream test gives `["","","anull","Xtest","teest","teyoest","",null,null,null,null]`. Expected `["undefined","","anull","teXst","te","teyo","s","IndexSizeError","TypeError","IndexSizeError","HierarchyRequestError"]`.

**Classification:** PORT.

**Note:** the `appendChild` guard at bootstrap:2151 is a narrow slice of the pre-insert validity check. Chromium also rejects `insertBefore`/`replaceChild` on CharacterData parents, and a Document child. Consider putting the guard in a shared pre-insert helper rather than only in `appendChild`.

**Test:** `RuntimeTests.CharacterDataUsesWebIdlRangesAndRejectsChildren`.

### 04f0475 - fix(js): enforce CORS preflight permissions (no PR)

**Upstream:**
- Adds a real CORS-safelisted request-header check. It checks values, not just names:
  - `accept`: no unsafe bytes.
  - `accept-language` / `content-language`: a restricted charset.
  - `content-type`: the MIME essence must be one of the three simple types, with token validation.
  - `range`: `bytes=N-[M]`.
  - Each value must be at most 128 bytes, and the aggregate at most 1024 bytes.
- `Access-Control-Request-Headers` becomes the lowercase, sorted, comma-joined unsafe names only, and is omitted when empty.
- The preflight must return 2xx.
- `Access-Control-Allow-Methods` / `-Headers` are parsed as token lists; malformed means failure. The method and every unsafe header must be allowed. `*` counts only when uncredentialed, and never covers `authorization`.

**C#:** `Ops/FetchOps.cs:438-476` has the old logic: `HasNonSimpleHeader` (:842) is name-only, the request-headers value is all custom header names joined with `", "`, and only `Access-Control-Allow-Origin` is checked. The unsafe request is then sent regardless.

**Classification:** PORT. This matches Chromium.

**Suggested change:** in `FetchOps.cs`, add `IsCorsSafelistedMethod`, `IsCorsSafelistedRequestHeader`, `CorsUnsafeRequestHeaderNames`, `ParseCorsHeaderList` (over all header values) and `PreflightAllowsMethod/Header`, and wire them into the preflight block.

**Tests:** port the five unit tests into `OpsTests` (make the helpers `internal`). Port `denied_preflight_never_sends_the_unsafe_request` with a loopback listener (needs the allow-private-network client).

### 05846de - CORS-check intermediate redirect hops + preflight status order (#973)

**Upstream:**
- The preflight's ok-status is checked before its CORS headers.
- In cors mode, a cross-origin redirect response must itself pass `cors_response_allows` before it is followed. Otherwise the result is `{"status":0,"body":"","url":..,"headers":{},"corsBlocked":true,"corsError":"CORS error: cross-origin redirect from '<url>' not allowed by Access-Control-Allow-Origin '<v>'"}`.

**C#:** the redirect loop (`FetchOps.cs` ~480-590) checks CORS only on the final response (~600). There is no preflight status check at all.

**Classification:** PORT, together with 04f0475. Match the upstream JSON payload byte for byte (wire rule 3). Chromium performs the CORS check on redirect responses in cors mode.

**Test:** `cors_mode_blocks_unauthorized_cross_origin_redirect_hop`, using the same loopback-listener shape.

### 2e752b9 - fix(obscura-js): normalize the fetch() request method to uppercase (#969)

**Upstream:** `fetch()` does `String(method).toUpperCase()`.

**C#:** bootstrap:7504 passes `init.method` raw. `Request` (bootstrap:7927) uppercases everything. Probe: `new Request(u,{method:'patch'}).method` is `"PATCH"`.

**Classification:** PORT, with a correction. Chromium follows Fetch "normalize a method": only case-insensitive matches of DELETE, GET, HEAD, OPTIONS, POST and PUT are uppercased; `patch` stays `patch`. Both upstream and the C# `Request` over-normalize.

**Suggested change:** one `_normalizeMethod` helper with that set, used by `fetch()`, `Request` and XHR `open`. Record it under Known deviations, because upstream uppercases every method.

**Test:** capture the method passed to `op_fetch_url`: `delete` should give `DELETE`, and `patch` should stay `patch`.

### ebe5973 - strip credentials on cross-origin redirects in op_fetch_url (#967)

**Upstream:** a per-hop header copy.
- On an origin change it drops `Authorization`, `Proxy-Authorization` and `Cookie`.
- On a 301/302/303 downgrade to GET it drops `Content-Type/-Length/-Encoding/-Language/-Location`.

**C#:** the loop re-applies `customHeaders2` unchanged on every hop (`FetchOps.cs` ~527-534), so `Authorization` leaks to a cross-origin redirect target.

**Classification:** PORT. Chromium strips `Authorization` on a cross-origin redirect.

**Suggested change:** add `SanitizeRedirectHeaders(Dictionary<string,string>, bool crossesOrigin, bool downgradedToGet)`, applied after the downgrade (`FetchOps.cs` ~575).

**Related, not part of the upstream fix:** C# and Rust both downgrade 301/302 to GET for every method. Fetch and Chromium downgrade 301/302 only for POST, and 303 for anything but GET/HEAD. Worth fixing in the same place and recording as a deviation.

**Test:** unit test for the helper in `OpsTests`.

### 4778192 - don't move the document URL before navigation commits (#940)

**Upstream:** `op_navigate` only queues the navigation; it no longer sets `gs.url`.

**C#:** `Ops/CoreOps.cs:250` still does `state.Url = url;` before queueing. `document.cookie` resolves its jar scope from `state.Url`, so the same SOP bypass exists: a queued cross-origin navigation followed by a synchronous `document.cookie` read or write.

**Classification:** PORT.

**Suggested change:** delete that assignment and keep the comment explaining why. `location.href` already reads the `__virtualUrl` preview (bootstrap ~6790, 6840), so the JS side does not depend on `state.Url` moving early. Check the `Page` drain path and run the `FragmentNavigationTests`/`PageTests` suites after.

**Test:** `RuntimeTests.QueuedNavigationDoesNotExposeAnotherOriginsCookies`, using the cookie-jar runtime setup.

### 88d2174 - HTMLSlotElement class and assignedNodes/assignedElements (#930)

**C#:** bootstrap:12502 is still `globalThis.HTMLSlotElement = Element`, and there is no `assigned_nodes` op. The native algorithm already exists as `DomTree.AssignedNodes` (`src/PocketCalculator.Dom/DomTree.cs:950`), used by render.

**Classification:** PORT.

**Suggested change:**
- `DomOps.cs`: add `assigned_nodes`, returning `null` for a non-slot and a JSON id array otherwise. Add it to `docs/op-protocol.md`.
- bootstrap: the `HTMLSlotElement` class plus `_slotDirectAssigned`, `_slotFallbackChildren` and `_slotAssignedNodes`, and the `SLOT` cases in `_elementClassFor` (:6620, XHTML namespace only) and `_elementClassForKnownName` (:6642).

**Test:** `RuntimeTests.SlotElementHasOwnBrandAndNamedAssignment`, including the 20k-deep case.

### c2e6fb2 - cap HKDF and CSPRNG output lengths against DoS (#910)

**C#:**
- `Ops/CryptoOps.cs:191` `Hkdf` calls `HKDF.DeriveKey`. That validates `outputLength <= 255*HashLen` before allocating and throws `ArgumentException`, which the code maps to `CryptoOperationException`. A uint above int.MaxValue casts negative and is also rejected. HKDF is therefore already safe.
- `CryptoOps.cs:209` `RandomBytes` is `RandomNumberGenerator.GetBytes((int)length)` with no cap. Up to 2^31-1 it allocates, so an HMAC `generateKey({length: huge})` can force a GB-scale allocation.

**Classification:** PARTIAL.

**Suggested change:** add `RandomBytesMax = 1024*1024` and throw `CryptoOperationException($"random byte request of {length} bytes exceeds the supported maximum of {RandomBytesMax}")` above it. An explicit `HkdfMaxOutputBytes` check is optional but keeps the message aligned with Rust.

**Tests:** `CryptoOpsTests.RandomBytesRejectsExcessiveLength` and `HkdfRejectsExcessiveOutputLength`.

### 25af144 - stop a generic parsererror from erasing the reason (no PR)

**C#:** bootstrap:10818-10837 has the pre-fix two-block form. The second block overwrites the descriptive `<parsererror>` with the fixed "error while parsing XML", and the first block writes `xmlError.error` unescaped through innerHTML: `'<img src=x>'` puts an `<img>` inside the parsererror.

**Classification:** PORT. Take upstream's single decision and `_escapeXmlErrorText`.

**Tests:** `RuntimeTests.AParsererrorSaysWhatWasWrong` and `AParsererrorDetailIsTextNotMarkup`.

### df8b058 - upgrade: update deno_core and V8

**JS-visible changes upstream:**
1. The timer shim moved from `Deno.core.queueUserTimer` to `createTimer`, and `cancelTimer` got a null guard. This is deno_core API only. C# supplies its own `DenoCoreShim` and timer queue, so it does not apply.
2. `_installWasmStreamingFallback()` is re-run in the `<obscura:init>` script. C# already calls it on every document reset (bootstrap:16994) and at load (:7420), so there is nothing to do.
3. `execute_classic_script` now runs `perform_microtask_checkpoint()` after the script, because deno_core 0.412 uses an explicit microtask policy. C# ClearScript keeps V8's automatic policy, which checkpoints when the outermost call returns (`Runtime/PocketCalculatorJsRuntime.EventLoop.cs:13-16, 42-57`), so C# already has it.

Everything else is `HandleScope`→`PinScope` churn, tokio test attributes, and a tokio-context guard for isolate creation.

**Classification:** SKIP. No JS-visible change is missing.

### a156914 (JS/DOM parts) - fix(browser): improve CDP compatibility and concurrency

The Rust `ops.rs`/`runtime.rs` diff is mostly rustfmt. Substantive JS/DOM items:

1. **Unified event path**
   - **Upstream:**
     - `window`, `Element` and `Document` add/removeEventListener go through `_eventTargetAdd/_eventTargetRemove`, so they get capture, once and signal support.
     - A new `_domEventDispatch` builds a capture → target → bubble path, with `eventPhase` 1/2/3 and inline handlers in the non-capture group.
     - It throws `InvalidStateError` on re-dispatch during dispatch, and resets the stop flags and `target` on each dispatch, so a reused event re-dispatches.
     - `_eventTargetDispatch` gets the same reentrancy guard and target reset.
   - **C#:** pre-fix code at bootstrap:90-104 (window), :3690-3730 (Element, recursive `parentNode.dispatchEvent`), :5520-5537 (Document) and :1870-1895.
   - **Probe:** capture listeners fire in bubble order, `eventPhase` is always 0, window listeners never see a bubbling element event, and a `{once:true}` window listener fired twice.
   - **Classification:** PORT.
   - **Chromium notes (Chromium wins):**
     - Upstream pushes `window` onto every path, even for a detached element tree. Chromium includes Window only when the path reaches the Document. Push `globalThis` only when the last ancestor is the document.
     - Upstream's path follows `parentNode`, so it stops at a ShadowRoot. Chromium continues to the host with retargeting (for composed events). The current C# code has the same limitation, so this is not a regression, but record it.
   - **Test:** port `dom_events_follow_capture_target_and_bubble_order` (the CDP test in `tests/PocketCalculator.Cdp.Tests`, or directly in `RuntimeTests`).
   - `_eventRegistry` / `__windowListeners` have no other readers in C# (`grep`), so removing them is safe.
2. **focus()/blur() fire FocusEvents**
   - **Upstream:** `blur`/`focusout` on the previous element, then `focus`/`focusin`, with `relatedTarget`.
   - **C#:** bootstrap:3849-3850 fires nothing.
   - **Classification:** PORT, adjusted. Chromium fires these only when the element is focusable and connected; upstream focuses any element. Gate on focusability (tabindex, form controls, `a[href]`, contenteditable).
3. **offsetParent / offsetTop / offsetLeft / clientTop / clientLeft**
   - **Upstream:** a real `offsetParent`. Offsets are relative to the offsetParent's padding edge and rounded; clientTop/clientLeft are the border widths.
   - **C#:** bootstrap:4594-4595 returns the viewport rect; `offsetParent` is `undefined` (probe).
   - **Classification:** PORT, adjusted. Chromium does not subtract the `<body>` box when `body` is the offsetParent and is not positioned: a first child of a default body has `offsetTop` 8, not 0. Upstream's formula gives 0 there. Measure in Chromium and special-case `body`.
4. **MouseEvent / KeyboardEvent / PointerEvent**
   - **Upstream:**
     - MouseEvent gets `pageX/pageY/x/y/which/offsetX/offsetY/movementX/Y/getModifierState`.
     - KeyboardEvent gets `getModifierState`.
     - `PointerEvent extends MouseEvent` with spec defaults (`pointerType ''`, width/height 1).
     - `Event.timeStamp` becomes `performance.now()`.
   - **C#:** probe gives `pageX` undefined, no `getModifierState`, `PointerEvent instanceof MouseEvent` false (bootstrap:10297; the later fallback at :15260 never installs), and `timeStamp` from `Date.now()` (:10233).
   - **Classification:** PORT.
   - **Chromium note:** `offsetX/Y` are relative to the target's padding edge. Upstream uses the border box; subtract `clientLeft/Top`.
5. **Window named-property setter**
   - **Upstream:** assigning to `window.<id>` replaces the accessor with an own writable data property.
   - **C#:** `_ensureWindowNamedProperty` (bootstrap:12898). Probe: in strict mode, `window.foo = 5` throws "has only a getter". Chromium allows the assignment.
   - **Classification:** PORT.
   - **Test:** `assignment_shadows_window_named_element_property`.
6. **`:scope` bound to the query root** (`obscura-dom/src/selector.rs`)
   - **C#:** `src/PocketCalculator.Dom/Selectors/SelectorMatching.cs:393` maps `:scope` to `IsRoot()`, and :194 carries the same assumption. `DomTree.Query.cs:42/85/112` never binds a scope element.
   - **Probe:**
     - `root.querySelectorAll(':scope > .item')` gives `[]` (Chromium: `[direct]`).
     - `':scope .menu .item'` gives `[direct,nested]` (Chromium: `[nested]`).
     - `root.matches(':scope')` gives `false` (Chromium: `true`).
   - **Classification:** PORT.
   - **Suggested change:** add a scope element to the matching context, set in `QuerySelectorFrom`, `QuerySelectorAllFrom` and `MatchesSelector` when the root is an element. `:scope` matches it; it falls back to `IsRoot()` when unset. Revisit the shadow `ForScope` branch at :194.
   - **Test:** `SelectorTests.ScopedQueriesBindScopeToTheElementRoot`.
7. **Renderer-backed `elementFromPoint` and pointer-events**
   - **Upstream:** new `op_layout_hit_test(x, y)` and `pointerEventsNone` in `op_layout_geometry`. JS uses the op when present, and skips `pointer-events:none` in the fallback.
   - **C#:** neither exists (`grep`); bootstrap:16903 is the synthetic-rect scan only.
   - **Classification:** PORT. The JS half is trivial; the op depends on the render-side `PreparedRender.hit_test` / `pointer_events_none` from the paint.rs/dom.rs parts of this commit, so coordinate with the render review.
8. **`invalidate_geometry`**
   - **Upstream:** `set_form_value` / `set_form_indeterminate` no longer invalidate prepared geometry. This is a render-cache optimisation.
   - **C#:** equivalent logic lives in `Ops/RenderInvalidation.cs:36,68`.
   - **Classification:** belongs to the render review; the JS semantics do not change.
9. `has_pending_navigation` (runtime) is used by the CDP/browser concurrency changes, so it belongs to that review.

## Upstream review: CDP area (fork point 727cc46)

Paths: C# = `dotnet/src/PocketCalculator.Cdp/...` unless stated. Rust = the upstream repository.

### Summary

| sha | title | class |
|---|---|---|
| a156914 | fix(browser): improve CDP compatibility and concurrency (CDP parts) | PORT (several parts; some depend on JS/render parts of the same commit and on 04418a5) |
| ec62004 | Backspace deletes a whole surrogate pair (#1005) | PORT |
| f81c296 | fix(cdp): correct accessible names and visibility | PORT |
| a161a8d | honor textarea selection when pressing Enter (#939) | PORT |
| 403356f | unique browser attachment sessions (#975) | PORT |
| b0ccbbe | avoid duplicate node lookup in resolveNode | PORT (with 94e857b) |
| 94e857b | canonical node wrappers when resolving CDP handles | PORT |
| 0671d94 | advertise client-facing endpoint | PORT |
| af955b3 | release memory after client disconnect | PORT (managed adaptation) |
| 20a3e02 | preserve undefined in by-value results (#779) | PORT |
| 4383793 | binary Fetch.fulfillRequest bodies via bodyBase64 (#912) | PORT |
| fa0362b | navigateToHistoryEntry index corruption + dropped network events (#920) | PORT |
| d792bae | keep concurrent pages' isolates live (#872) | CONFLICT with C# decision "Page suspension stays"; Chromium wins, so port it, with a memory measurement |
| dc88742 | Fetch domain path parity with server.rs (#919) | PORT |
| dfc546d | error on unresolvable objectId in describeNode/resolveNode (#917) | PORT |

Nothing in this set is ALREADY present in C#. None of it is Rust-only (no SKIP).

---

### a156914 - fix(browser): improve CDP compatibility and concurrency (no PR number)

Covers input.rs, server.rs, page.rs and the three CDP integration tests. The bootstrap/runtime/render/dom parts of this commit are outside this review, but several CDP tests below depend on them.

#### 1. Input.dispatchMouseEvent mousePressed: pointer events, hover transitions, focus
Rust: before `mousedown`, if the hit target changed since last press (`__obscura_mouse_over_target`), it dispatches `pointerout`/`pointerleave`/`pointerover`/`pointerenter`, then `mouseout`/`mouseleave`/`mouseover`/`mouseenter`, computing exited/entered ancestor chains with the right relatedTarget. It then dispatches `pointerdown` (pointerId 1, pointerType 'mouse', isPrimary, pressure 0.5 when buttons!=0, detail 0), then `mousedown`, then focuses `target.closest('input,select,textarea,button,a[href],[tabindex],[contenteditable]')` unless disabled. All mouse events gain `composed:true`. mouseReleased dispatches `pointerup` (pressure 0) before `mouseup`, and `click` gains `composed:true`.
C#: `Domains/Input.cs` `MousePressedJs` (~line 380) and `MouseReleasedJs` dispatch only mousedown/mouseup/click, no `composed`, no pointer events, no focus on press. **PORT.** The Chromium order is pointerover, pointerenter, mouseover, mouseenter, pointerdown, mousedown, focus, focusin, pointerup, mouseup, click, and upstream's test asserts exactly that. The generated script is not a wire payload, so it does not need to match byte for byte. It does depend on the same commit's bootstrap changes: `PointerEvent extends MouseEvent`, which the C# bootstrap already has (bootstrap.js:15260); `MouseEvent.which`/`offsetX`/`offsetY`/`getModifierState`/`movementX`, which C# lacks; and the focus()/focusin event sequence.

#### 2. mouseReleased no longer runs the navigation inline
Rust replaced `process_pending_navigation().await` with `if !page.has_pending_navigation() && page.sync_virtual_url() { emit frameNavigated }`. A real navigation is left for the server loop's `take_live_pending_navigation`, so the `Input.dispatchMouseEvent` response goes out before `Page.frameNavigated`. That is Chromium's order: the input ack comes first and the navigation commits later. It adds `Page::has_pending_navigation()`, and a new server test pins the order: `click_navigation_acknowledges_input_before_navigation_events`.
C#: `Input.cs:204-259` still awaits `page.ProcessPendingNavigationOutcomeAsync()` inside the handler. **PORT**, but keep the C# same-document deviation: when `SyncVirtualUrl()` reports `IsSameDocument`, keep emitting `Page.EmitSameDocumentNavigation` rather than `frameNavigated`. Add `Page.HasPendingNavigation` (PocketCalculator.Browser, over `Js`) and rely on `Server.Processor.TakeLivePendingNavigation`. Port the test into `ServerTests.cs`.

#### 3. Forwarded CDP authority (POCKETCALCULATOR_CDP_FORWARDED_HOST/PORT) and CLI multi-worker
Rust: `host_matches_bind` now also accepts the Host header when it matches a forwarded (ip, port) pair. The server reads `POCKETCALCULATOR_CDP_FORWARDED_HOST`/`_PORT` (both or neither), refuses them on a non-loopback bind, and still requires `POCKETCALCULATOR_CDP_TOKEN` when the forwarded host is public. The CLI multi-worker balancer binds the public port first, reserves OS-assigned worker ports, passes the forwarded env to workers, waits for workers by polling connect instead of a fixed 500 ms sleep, sets TCP_NODELAY and tolerates early-closed peeks.
C#: there is no Origin/Host/bearer control gate at all. `control_refusal` came from upstream 04418a5 ("harden security boundaries"), which is not in this set. `PocketCalculator.Cli/Commands/ServeCommand.cs:87` still uses `port+1+i` and `Task.Delay(500)`. **PORT, after 04418a5 is ported.** The forwarded-authority part is meaningless without the gate. The CLI balancer changes (free-port reservation, readiness polling, NODELAY, tolerant peek) can be ported on their own.

#### 4. CDP integration tests added by this commit
- `iframe_event_dispatch.rs::dom_events_follow_capture_target_and_bubble_order`: capture/target/bubble phases window→document→…→target and back, plus re-dispatching the same Event object. The C# `Node.dispatchEvent` (bootstrap.js:3702) has no capture phase. The fix lives in the bootstrap part of a156914, and the test goes into `tests/PocketCalculator.Cdp.Tests/IframeEventDispatch.cs`.
- `input_mouse_event_parity.rs`: wheel with `overflow: auto hidden` must not scroll the hidden axis. `MouseWheelJs` already reads `overflowX`/`overflowY` separately, so this needs the computed longhands to be right. It also adds `pointer-events:none` overlay hit testing, negative z-index stacking in hit testing, offsetLeft/Top relative to the padding edge of the offsetParent (clientLeft/Top 3), and the extended press/release event order from part 1. These are render/bootstrap fixes; port the tests into `InputMouseEventParity.cs`.
- `window_conformance_parity.rs::assignment_shadows_window_named_element_property`: `window.a = 42` over a named element makes a writable data property. This is a bootstrap fix; the test goes into `WindowConformanceParity.cs`.

---

### ec62004 - Backspace deletes a whole surrogate pair (#1005)
Rust: `BACKSPACE_JS` gains `backCount(str, p)`, which returns 2 when the units at p-2 and p-1 are a high+low surrogate pair. It is used in both the collapsed-caret path and the legacy `s == null` path. Range deletion is unchanged.
C#: `Input.cs` `BackspaceJs` (~line 63) still has `v.slice(0, -1)` and `v.slice(0, s - 1)`. **PORT.** Note that Chromium deletes a whole grapheme cluster on Backspace, not just a surrogate pair: it removes ZWJ sequences and a base plus variation selector as one unit. The surrogate-pair fix is a strict improvement. Test: port `backspace_surrogate.rs`, where "a😀b" with the caret at 3 becomes "ab" with the caret at 1, into `InputDomainTests.cs`.

### f81c296 - fix(cdp): correct accessible names and visibility (no PR)
Rust `accessibility.rs` rewrite:
- `NameContext` pre-computes a visibility map. `hidden`, `aria-hidden="true"` (case-insensitive), head/script/style/template/noscript, `input[type=hidden]` and computed `display:none` hide the subtree. `visibility:hidden` is inherited but can be overridden by a descendant. Hidden nodes are dropped from the AX tree.
- It builds an id→element map (first occurrence wins) and label→control association: `for=` must point at a labelable element, otherwise the first labelable descendant is used.
- The name computation follows accname order: aria-labelledby (deduped, skips missing ids, uses the referenced node's text alternative or content, including hidden content when the reference itself is hidden, never recursing into another labelledby), then aria-label (whitespace-flattened, blank ignored), then native labels, then a text alternative (img/input image alt, submit→"Submit", reset→"Reset", button value), then content for button/link/heading/checkbox/radio/menuitem/tab/cell/row/LabelText/StaticText, then title, then placeholder (input/textarea only). Content names skip nested controls and treat br/p/div/li/h* as word boundaries.
- `childIds` are now computed from AX `parentId`, not DOM children, so they reach through non-AX intermediate nodes.
- New `PocketCalculatorJsRuntime::accessibility_styles()` exposes display/visibility from the prepared render.

C#: `Domains/Accessibility.cs` is the old version. `BuildAxNodes` has no hidden filtering, `ComputeName` (line 367) uses the old aria-label→labelledby→alt→title→placeholder order, and `childIds` come from DOM children (line ~128). **PORT.** Add an accessor in PocketCalculator.Js over `RenderState.EnsurePreparedRender(state)` that returns `NodeId → (DisplayNone, VisibilityHidden)`, and check how C# `ComputedStyle.VisibilityHidden` models "inherit vs explicit" (Rust uses `Option<bool>`). The wire shape of AXNode is unchanged apart from which nodes appear and their name/childIds. Chromium note: real `getFullAXTree` returns hidden nodes as `ignored: true` with `ignoredReasons` rather than omitting them. Upstream omits them. Puppeteer/Playwright filter out ignored nodes either way, so omission is acceptable, but it is not byte-for-byte Chromium. Tests: port the 10 unit tests plus `tests/accessibility_names.rs` into `AccessibilityDomainTests.cs`.

### a161a8d - honor textarea selection when pressing Enter (#939)
Rust `EnterJs` textarea branch: clamp selectionStart/End (null start → end of value), replace `[lower, upper)` with "\n", then `setSelectionRange(lower+1, lower+1)`, then the trusted `input` event.
C#: `Input.cs` `EnterJs` (~line 86) still appends `'\n'`. **PORT.** This matches Chromium. Test: port `textarea_enter_selection.rs`, which covers caret splice, range replace, caret 0 and both keyDown/rawKeyDown, into `InputDomainTests.cs`.

### 403356f - unique browser attachment sessions (#975)
Rust: `Target.attachToBrowserTarget` returns `format!("browser-{uuid}")` instead of the constant "browser-session", so detaching one attachment does not remove another.
C#: `Domains/Target.cs:161` still has `const string sessionId = "browser-session"`. `detachFromTarget` (Target.cs:244) already removes by id, so only the id needs to change. **PORT**, using `"browser-" + Guid.NewGuid()` (hyphenated lowercase, like the Rust uuid Display). Update `tests/PocketCalculator.Cdp.Tests/TargetDomainTests.cs:69-76`, which asserts the literal, and the tests at lines 88/118/134 that use it as a parent session. Add `BrowserAttachmentsDetachIndependently`. Chromium also issues a unique session id per attach.

### 94e857b - canonical node wrappers when resolving CDP handles (no PR)
Rust `DOM.resolveNode`: the hand-rolled `_cache`/`new Element(nid)` wrapper, which made a plain `Element` instead of `HTMLTextAreaElement` and so on, is replaced by `t === 9 ? document : _wrap(nid)`.
C#: `Domains/Dom.cs:179-193` still has the old snippet. **PORT**, merged with b0ccbbe below. Test: `resolve_node_preserves_identity_and_specialized_wrappers`.

### b0ccbbe - avoid duplicate node lookup in resolveNode (no PR)
Rust: the snippet becomes just `globalThis._wrap({node_id})`. This relies on the document wrapper being canonical in `_cache`. The C# bootstrap already does `_cache.set(documentNid, globalThis.document)` (bootstrap.js:17001), and `_wrap` is identical (bootstrap.js:6665), so it is safe here. **PORT.** In `Dom.cs`, use `jsCode = $"globalThis._wrap({nodeId})"`. The test extends to `instanceof HTMLTextAreaElement`/`HTMLAnchorElement` and `resolveNode(document root) === document`. Add them to `DomDomainTests.cs`.

### dfc546d - error on unresolvable objectId in describeNode/resolveNode (#917)
Rust: when the JS lookup returns -1 or a non-number, return `Err("objectId {oid} could not be resolved to a node")` instead of falling back to node 0 (the document).
C#: `Dom.cs:141` and `Dom.cs:172` both have `resolved is { } value and >= 0 ? (ulong)value : 0UL`, which is the same bug. **PORT**: `throw new DomainError($"objectId {objectId} could not be resolved to a node")`. The error string is a wire message, so match the Rust text. Chromium also errors here, with "Could not find object with given id". Tests: `DescribeNodeErrorsOnUnresolvableObjectId`, `ResolveNodeErrorsOnUnresolvableObjectId`.

### 0671d94 - advertise client-facing endpoint (no PR)
Rust: `/json/version` and `/json/list` build `webSocketDebuggerUrl` from the request's `Host` header (case-insensitive name) when it parses as a bare authority (host present, no userinfo, path "/", no query or fragment). Otherwise they fall back to `127.0.0.1:{port}`.
C#: `Server.cs:759,771` hard-code `ws://127.0.0.1:{port}`, and `HandleHttpJsonBlocking(socket, port, endpoint)` has no access to the head. **PORT.** Pass the peeked request head from `AcceptDispatch` and add `WebSocketAuthority(head, port)`. Validate with `Uri.TryCreate("http://" + value + "/")` plus the same userinfo/path/query/fragment checks. This is JSON on the wire, and the format `ws://{authority}/devtools/browser` must match. Chromium also echoes the Host header here. Test: `DiscoveryUsesTheClientFacingHttpAuthority`, where `hOsT: cdp.example.test:9222` is echoed and `attacker.test/path` falls back to 127.0.0.1:9223, in `ServerTests.cs`.

### af955b3 - release memory after client disconnect (no PR)
Rust: when the last live connection ends, it drops the runtime and LocalSet, then calls `malloc_trim(0)` (glibc only). `SlotGuard::release` returns the remaining count.
C#: `Server.cs:886-905`. `RunConnection`'s finally calls `slots.Release()` (`ConnectionSlots.Release` at Server.cs:448 returns void), with no idle trim. **PORT, adapted.** Make `Release()` return `Interlocked.Decrement`. When it reaches 0, run a full blocking compacting GC (`GCSettings.LargeObjectHeapCompactionMode = CompactOnce; GC.Collect(2, Aggressive, true, true)`, as `PageHelpers.ReleaseReplacedDocumentMemory` already does). V8 native heaps are freed by isolate disposal, so for the RSS part optionally P/Invoke `malloc_trim(0)` on Linux/glibc. libc P/Invoke already exists in PocketCalculator.Cli (ProcessExit/ProcessEnvironment), so it does not count as a new native dependency under rule 4, but guard it for musl/non-Linux. Measure RSS before and after, per the performance rule.

### 20a3e02 - preserve undefined in by-value results (#779)
Rust: `v8_to_cdp_value` checks `is_undefined()` before JSON conversion in evaluate-by-value and both callFunctionOn by-value paths (awaited and not). It returns `{type:"undefined"}` with no value and no subtype.
C#: `PocketCalculator.Js/Runtime/PocketCalculatorJsRuntime.Cdp.cs:195`, `:289` and `:308` all use `InfoFromJson(ToJson(read))`, and `ToJson` maps `Undefined`/`VoidResult` to null (line 651). So `returnByValue` of `undefined` reports `{type:"object",subtype:"null",value:null}`. **PORT**: add `ToCdpValue(object? v)`, which returns `new RemoteObjectInfo(false, "undefined", null, "", "", null, null)` (HasValue false) for `Undefined`/`VoidResult` and otherwise `InfoFromJson(ToJson(v))`. This is a wire change toward Chromium (Chromium returns `{"type":"undefined"}`). Test: port `runtime_by_value_undefined.rs`, a 7-value table x {evaluate, callFunctionOn} x {sync, awaited}, into `RuntimeRemoteObjectNullValue.cs` or a new `RuntimeByValueUndefined.cs`.

### 4383793 - binary Fetch.fulfillRequest bodies intact via bodyBase64 (#912)
Rust: `InterceptResolution::Fulfill` gains `body_base64`. server.rs passes the raw CDP base64 through, and `op_fetch_url`'s intercept path emits `{"status","body","bodyBase64","url","headers"}` (in that key order), which the bootstrap already prefers (`_base64ToUint8Array`).
C#: `PocketCalculator.Js/Ops/PocketCalculatorState.cs:395` `Fulfill(Status, Headers, Body)` has no base64. `FetchOps.cs:359-372` emits status/body/url/headers only. `ServerSupport.cs:351` builds it from the lossy `DecodeBase64` string. The C# bootstrap's consumer already reads `parsed.bodyBase64` (bootstrap.js:7535). **PORT**: add `BodyBase64` to `InterceptResolution.Fulfill` and emit it after `body` in `FetchOps` so the key order matches Rust. One improvement over Rust: the lenient decoder drops non-alphabet characters, so passing the raw param through can hand `atob` a string it rejects. Decode to bytes once, then send `body = lossy UTF-8(bytes)` and `bodyBase64 = Convert.ToBase64String(bytes)`. If you do that, comment it as a deviation. Test: non-UTF-8 bytes (PNG header, FF FE 00) round-trip through `Response.arrayBuffer()` under Fetch interception.

### fa0362b - navigateToHistoryEntry index corruption and dropped network events (#920)
Rust: snapshots history and index before `set_history_index`. When `navigate_with_wait` fails, it restores both and returns the error. On success it restores history with `history_index = entry_id` and calls `sync_js_network_events()` before draining `network_events`.
C#: `Domains/Page.cs:1139-1194` calls `SetHistoryIndex` before navigating. The `catch (PageException)` rethrows without rolling back, so the index stays moved (same bug), and there is no `SyncJsNetworkEvents()` before the drain at line 1175. **PORT** both. Consider also catching non-PageException failures, for example a URI parse error, in the rollback. Tests: `FailedHistoryNavigationLeavesCurrentIndexUnchanged` (history `["data:text/html,<p>ok</p>", "not-a-url"]`, navigate to 1, expect error and currentIndex 0) and `HistoryNavigationMovesCurrentIndexOnSuccess` in `PageDomainTests.cs`.

### d792bae - keep concurrent pages' JS isolates live on a shared connection (#872)
Rust: `get_session_page_mut` only resumes the target and no longer suspends others. The interception path's suspend-all loop is removed. The server helpers now service every live page: the event-loop pump runs a turn on each, network-event sync runs per page with its own session and frame, and pending-navigation pickup scans all live pages. New test `concurrent_page_isolation.rs` checks that a page's globalThis state and a live objectId survive a command routed to another page.
C#: `CdpContext.cs:718-757` still suspends another page. `Server.Navigation.cs:83-86` suspends all pages. `Server.Processor.cs` `LiveJsPage`/`PumpLivePageEventLoopAsync`/`SyncLivePageNetworkEvents`/`TakeLivePendingNavigation` (lines ~294-370) serve only the first live page. `Page.SuspendJs` (PocketCalculator.Browser/Page.Evaluate.cs:163) keeps the DOM and `CdpObjectState` recipes but drops the JS heap: globals, listeners, timers, closures.
**CONFLICT**: todo.md section "What the CDP pool move did and did not change", under "Page suspension stays", deliberately keeps suspension to bound one live isolate per connection for memory reasons, and says relaxing it "is a memory trade needing its own measurement". Chromium keeps every tab's heap live. Suspension silently destroys page A's state when page B is driven, which breaks Playwright `connectOverCDP` with several pages. Chromium wins on behaviour, so port it. Remove the two suspend sites, make the four processor helpers loop over all live pages, and keep `CdpContext.V8Lock`, which is unchanged. First run the memory measurement the todo entry asks for (N pages on one connection, RSS) and then rewrite that todo section. ClearScript's multiple isolates per process make this simpler than in Rust. The emitted events are unchanged per page. Test: port `concurrent_page_isolation.rs` into a new `ConcurrentPageIsolation.cs`, and keep `ConcurrentConnectionsHeavyPage` green.

### dc88742 - Fetch domain path parity with server.rs (#919)
Rust `domains/fetch.rs`: `continueRequest` passes `parse_cdp_headers(params)` instead of `None`. `fulfillRequest` base64-decodes `body` via `decode_base64`.
C#: `Domains/Fetch.cs:157-161` passes `null` headers, and `:190` passes the raw base64 `body`. The helpers already exist in `ServerSupport.ParseCdpHeaders` (ServerSupport.cs:47) and `ServerSupport.DecodeBase64` (:73). **PORT**: call them from `Fetch.cs`. The `Fetch.cs` remarks note this path is mostly unused in practice, but embedders can reach it. Combine it with 4383793 so `FetchResolution.Fulfill` carries bytes and base64 too. Tests: `ContinueRequestForwardsHeaderOverrides` and `FulfillRequestBase64DecodesBody` ("SGVsbG8=" becomes "Hello") in `FetchDomainTests.cs`.
