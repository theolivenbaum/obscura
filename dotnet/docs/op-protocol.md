# The bootstrap.js host op protocol

`bootstrap.js` is shared verbatim between the Rust engine and the C# port, so
the surface it calls is a **frozen contract**. Both hosts must accept the same
arguments and return byte-identical payloads. This file is the reference for
the C# implementation; the authority is `crates/obscura-js/src/ops.rs`.

Regenerate the tables here from the Rust source rather than editing by hand.

## `Deno.core` (non-op members)

The shim touches exactly four non-op members. `DenoCoreShim` implements them.

| Member | Signature | Purpose |
|---|---|---|
| `queueUserTimer` | `(realmId, repeat, delayMs, callback) -> id` | Backs `setTimeout`/`setInterval`. |
| `cancelTimer` | `(id) -> void` | Cancels a queued timer. |
| `setUnhandledPromiseRejectionHandler` | `(handler) -> void` | Drives `unhandledrejection`. |
| `setHandledPromiseRejectionHandler` | `(handler) -> void` | Drives `rejectionhandled`. |

The op table is handed to the shim as `Deno.core.ops`. The shim captures
`Deno.core` in a closure-private `const __obscuraCore` as its first statement, and
once bootstrap has run `BootstrapLoader.Install` deletes `globalThis.Deno` and
`__obscura_core_handoff` in every realm, so page script can never reach the ops
(upstream 04418a5). Host-evaluated script runs in the page realm too, so it has no
ops either: `Runtime.addBinding` forwards through the frozen
`globalThis.__obscura_binding_called(name, payload)`, and `DOM.resolveNode` goes
through `globalThis._wrap(nid)`. The PocketCalculator.Js test assembly alone republishes the
page realm's table as `__obscura_test_ops` (`BootstrapLoader.ExposeOpsForTests`),
as upstream's `#[cfg(test)]` `expose_ops_for_tests` does.

## Host helpers (port deviation)

Upstream publishes the shim's host-side helpers as page-visible globals, which
host script calls by name (`globalThis.__obscura_markTrusted(ev)` and so on). That
lets any page mark its own events trusted. The port keeps them closure-private:
bootstrap.js hands one frozen object to the host through
`globalThis.__obscura_host_handoff`, which `BootstrapLoader.Install` reads into
`DenoCoreShim.HostHelpers` and deletes together with `__obscura_core_handoff`,
before any page script runs, in every realm. Host script reaches it as the
parameter `__obscura_host` of a strict function the host compiles and calls with
that object (`HostScript`; `PocketCalculatorJsRuntime.EvaluateHost` /
`ExecuteHostScript`, `FrameRealm.EvaluateHost` / `ExecuteHostScript`,
`Page.EvaluateHost` / `TryExecuteHost`). Nothing on `globalThis` names it. Client- or
page-supplied code must never go through these entry points.

| Member | Upstream global | Used by |
|---|---|---|
| `markTrusted(ev) -> ev` | `__obscura_markTrusted` | CDP Input, MCP fill/type/fill_form |
| `setFieldValue(el, field, value)` | `__obscura_setFieldValue` | CDP Input, MCP |
| `setInputFiles(el, specs)` | `__obscura_setInputFiles` | `DOM.setFileInputFiles` |
| `deliverMessage(dataJson, origin, sourceFrameId, targetOrigin)` | `__obscura_deliverMessage` | frame `postMessage` delivery |
| `activateLabel(label, control, trusted) -> bool` | `__obscura_activateLabel` | CDP mouseReleased |
| `isDisabled(el)`, `labeledControl(label)`, `interactiveHost(el)` | `__obscura_isDisabled`, `__obscura_labeledControl`, `__obscura_interactiveHost` | CDP mouseReleased |
| `registerLinkedStylesheet(link, href, responseUrl)` | `__obscura_registerLinkedStylesheet` | static `<link>` sheet registration |
| `tryFragmentNavigate(url, replace) -> bool` | `__obscura_tryFragmentNavigate` | `Page.navigate` to a fragment |
| `setScreenOverride(w, h, emulated)` | `__obscura_set_screen_override` | `Emulation.setDeviceMetricsOverride` |
| `liveFrameIds() -> number[]`, `forgetFrame(id)` | `__obscura_liveFrameIds`, `__obscura_forgetFrame` | detached-frame release |
| `pointer.down` | `globalThis.__obscura_mouse_down` | CDP mousePressed / mouseReleased |

The shim also sends its own closure-held realm id, not the page-writable
`globalThis.__obscura_frameId`, as `op_post_frame_message`'s `source_frame_id`,
and the form-state mirror passes the id `BootstrapLoader.Install` was given as
`op_dom`'s frame id. The op signatures are unchanged.

## Ops (55)

`fast` marks ops deno_core binds on the fast path; in C# the distinction is
informational, but a `fast` op must stay allocation-light. Argument types are
shown with the deno_core attribute markers stripped: `String` is a JS string,
`&[u8]`/`JsBuffer` is a typed array, `u32`/`f64` are JS numbers.

| Op | Kind | Arguments | Returns |
|---|---|---|---|
| `op_add_import_map` | sync | `source: String, base_url: String` | `String` |
| `op_async_runtime_available` | fast | `(none)` | `bool` |
| `op_begin_render_task` | fast | `(none)` | `(void)` |
| `op_binding_called` | fast | `name: &str, payload: &str` | `(void)` |
| `op_canvas_paint_damage` | fast | `nid: u32` | `bool` |
| `op_canvas_register_surface` | sync | `nid: u32, width: u32, height: u32, pixels: JsBuffer` | `bool` |
| `op_computed_style` | sync | `nid_str: String` | `String` |
| `op_console_msg` | fast | `level: &str, msg: &str, args_json: &str` | `(void)` |
| `op_css_supports` | fast | `name: &str, value: &str` | `bool` |
| `op_document_domain_candidate` | sync | `current: &str, input: &str` | `String` |
| `op_dom` | sync | `cmd: String, arg1: String, arg2: String, frame_id: u32` | `String` |
| `op_element_scroll_metrics` | sync | `nid_str: String` | `String` |
| `op_element_scroll_to` | sync | `nid_str: String, x: f64, y: f64` | `String` |
| `op_encoding_for_label` | sync | `label: &str` | `String` |
| `op_external_stylesheet_get` | sync | `owner_nid: u32, frame_id: u32` | `String` |
| `op_external_stylesheet_remove` | fast | `owner_nid: u32, frame_id: u32` | `bool` |
| `op_external_stylesheet_set` | fast | `owner_nid: u32, css: String, response_url: String, imported_origin_clean: bool, frame_id: u32` | `bool` |
| `op_fetch_url` | async | `url: String, method: String, headers_json: String, body: JsBuffer, origin: String, mode: String, credentials: String, internal_load: bool` | `String` |
| `op_frame_document_ready` | fast | `url: &str, html: &str, viewport_width: u64, viewport_height: u64` | `u32` |
| `op_get_cookies` | sync | `(none)` | `String` |
| `op_image_metadata` | sync | `nid: u32, _cached_only: bool` | `String` |
| `op_intersection_observer_measurements` | sync | `nids_json: String` | `String` |
| `op_layout_geometry` | sync | `nid_str: String` | `String` |
| `op_layout_metrics` | sync | `(none)` | `String` |
| `op_load_image_metadata` | async | `nid: u32` | `String` |
| `op_navigate` | fast | `url: &str, method: &str, body: &str` | `(void)` |
| `op_post_frame_message` | fast | `target_frame_id: u32, source_frame_id: u32, origin: &str, target_origin: &str, data_json: &str` | `(void)` |
| `op_posted_task` | sync | `frame_id: u32, callback: v8::Global<v8::Function>` | `f64` |
| `op_posted_task_generation` | fast | `frame_id: u32` | `f64` |
| `op_random_bytes` | sync | `len: u32` | `Vec<u8` |
| `op_resize_observer_measurements` | sync | `nids_json: String` | `String` |
| `op_runtime_events_enabled` | fast | `(none)` | `bool` |
| `op_script_mark_started` | fast | `nid: u32` | `bool` |
| `op_script_try_start` | fast | `nid: u32` | `bool` |
| `op_scroll_offset` | sync | `(none)` | `String` |
| `op_scroll_to` | sync | `x: f64, y: f64` | `String` |
| `op_set_cookie` | fast | `cookie_str: &str` | `(void)` |
| `op_set_dynamic_fonts` | fast | `registrations: &str` | `bool` |
| `op_shadow_attach` | fast | `host_nid: u32, mode: String` | `i32` |
| `op_shadow_root_info` | sync | `host_nid: u32` | `String` |
| `op_sleep` | async | `millis: u64` | `(void)` |
| `op_subtle_aes_cbc` | sync | `encrypt: bool, key: &[u8], iv: &[u8], data: &[u8]` | `Vec<u8` |
| `op_subtle_aes_ctr` | sync | `key: &[u8], counter: &[u8], counter_length: u32, data: &[u8]` | `Vec<u8` |
| `op_subtle_aes_gcm` | sync | `encrypt: bool, key: &[u8], iv: &[u8], aad: &[u8], data: &[u8]` | `Vec<u8` |
| `op_subtle_digest` | sync | `algorithm: &str, data: &[u8]` | `Vec<u8>` |
| `op_subtle_hkdf` | sync | `hash: &str, ikm: &[u8], salt: &[u8], info: &[u8], length: u32` | `Vec<u8` |
| `op_subtle_hmac` | sync | `hash: &str, key: &[u8], data: &[u8]` | `Vec<u8` |
| `op_subtle_pbkdf2` | sync | `hash: &str, password: &[u8], salt: &[u8], iterations: u32, length: u32` | `Vec<u8` |
| `op_text_decode` | sync | `label: &str, bytes: &[u8], fatal: bool, ignore_bom: bool` | `String` |
| `op_url_encode_query` | sync | `query: &str, label: &str, special: bool` | `String` |
| `op_url_parse` | sync | `href: &str, base: &str` | `String` |
| `op_url_resolve` | sync | `href: &str, base: &str` | `String` |
| `op_url_set` | sync | `href: &str, part: &str, value: &str` | `String` |
| `op_waapi_control` | fast | `id: f64, action: &str, value: f64` | `bool` |
| `op_waapi_create` | fast | `input: &str` | `bool` |

## `op_dom` commands (67)

`op_dom(cmd, arg1, arg2, frameId) -> String` multiplexes the whole DOM surface
over one op. Arguments and results are strings; structured results are JSON.
An unknown command, a missing node, or a failed operation returns the empty
string or `"null"` depending on the command - match `op_dom_inner` exactly.

```
append_child                    attribute_names                 child_nodes
clone_node                      compare_order                   contains
create_comment_node             create_doctype                  create_document_fragment
create_element                  create_element_ns               create_processing_instruction
create_text_node                doctype_name                    doctype_public_id
document_base_href              document_base_url               document_doctype
document_element                document_encoding               document_node_id
document_referrer               document_title                  document_url
document_write                  document_write_reset            element_children
first_child                     get_attribute                   get_attribute_ns
get_element_by_id               has_child_nodes                 inner_html
insert_before                   is_connected                    last_child
local_name                      matches_selector                namespace_uri
next_after_subtree              next_in_subtree                 next_sibling
node_index                      node_name                       node_root
node_type                       outer_html                      parent_node
pi_target                       prev_in_subtree                 prev_sibling
query_selector                  query_selector_all              query_selector_all_scoped
query_selector_scoped           remove_attribute                remove_attribute_ns
remove_child                    set_attribute                   set_attribute_ns
set_fragment_html_executable    set_inner_html                  set_inner_html_context
set_text_content                tag_name                        template_contents
text_content
```

The three `op_external_stylesheet_*` ops (upstream 04418a5) hold a linked sheet's
fetched CSS beside its `<link>` (or an `@import`'s beside its `<style>`) in the
`DomTree`, with an origin-clean bit. `set` accepts only a `link` or `style` owner
and stores the sheet as origin-clean only when `imported_origin_clean` is true and
`response_url` is same-origin with the frame's document URL (opaque origins never
are); it returns whether it stored. `get` returns `null` when the owner has no
sheet, `{"originClean":false}` without the bytes when it is not origin-clean, and
`{"originClean":true,"css":...}` otherwise, in that key order. A change to a
connected owner discards the prepared render. They replace the port's own
`op_dom` commands `get_external_stylesheet_css` / `set_external_stylesheet_css`,
which returned any sheet's bytes and are gone.

## Rules for the C# implementation

1. **Never let an exception cross back into V8.** The Rust ops wrap their
   bodies in `catch_unwind` because unwinding into V8's FFI frame aborts the
   process. Route every op body through `OpGuard`, which converts a managed
   exception into the op's documented failure value.
2. **Return the documented failure value, not an error.** `op_dom` returns
   `""`/`"null"` for a missing node; throwing instead changes shim behavior.
3. **String results are exact.** JSON field order, number formatting, and
   empty-vs-null choices are all observable through the shim. Compare against
   the Rust output in a parity test rather than reasoning about it.
4. **`frame_id` selects the realm state.** Ops that take `frame_id` resolve
   per-frame state; the main realm is frame 0.

## `op_fetch_url`: what page script may see

The eighth argument, `internal_load`, came with upstream 04418a5. The public
`fetch()` (and XHR, which goes through it) passes `false`; the shim's own
subresource loads pass `true`: `__fetchDynClassicScript`, the iframe loader and
(host-side, see below) the linked-stylesheet loader. For `internal_load` false the
result keys are unchanged (`status, body, bodyBase64, requestId, url, redirected,
opaque, headers`); only the values are filtered:

- `set-cookie` / `set-cookie2` are never in `headers`.
- For a response whose final URL is cross-origin to the page, `headers` holds
  only the seven CORS-safelisted names (`cache-control`, `content-language`,
  `content-length`, `content-type`, `expires`, `last-modified`, `pragma`) and
  those named in `Access-Control-Expose-Headers`; `*` exposes all of them only
  when credentials are not `include`.
- When `!internal_load && mode == "no-cors"` and any hop was cross-origin, the
  response is opaque: `status` 0, `body` and `bodyBase64` `""`, `headers` `{}`,
  `opaque` true. The shim turns it into a null-body `Response`.
- A request fulfilled through `Fetch.fulfillRequest` follows the same rules, and
  its result gains an `opaque` key: `{status, body, url, headers, opaque}`. Rust
  also emits `bodyBase64` there (#912); the C# fulfill carries text only and
  leaves it out, as before.

The CDP-facing records (network events, `Network.getResponseBody`, the response
callbacks) keep the full response.

In cors mode a failed CORS check on a cross-origin redirect hop (upstream
05846de) returns `{"status":0,"body":"","url":<hop url>,"headers":{},
"corsBlocked":true,"corsError":"CORS error: cross-origin redirect from '<url>'
not allowed by Access-Control-Allow-Origin '<value>'"}`. A preflight that is not
2xx, lists an invalid token, or does not allow the method or an unsafe request
header rejects the op (upstream 04f0475), and the request is never sent.

### Request guards on page-script requests (port deviation)

Rust forwards every header in `headers_json` and any method in any mode. The
port applies Fetch's request guards when `internal_load` is false, in the shim
(`Headers`, `Request`, `fetch()`, `XMLHttpRequest.setRequestHeader`) and again in
`op_fetch_url`, since page script decides what reaches the op. The signature and
the result keys are unchanged; the op filters its inputs:

- `mode` other than `no-cors` or `same-origin` is treated as `cors`, so an
  unknown value cannot skip the CORS check and the opaque filter. (The shim
  already throws a `TypeError` for an invalid `RequestMode` or `"navigate"`.)
- A forbidden request-header is dropped silently in every mode: `Accept-Charset`,
  `Accept-Encoding`, `Access-Control-Request-Headers`,
  `Access-Control-Request-Method`, `Access-Control-Request-Private-Network`,
  `Connection`, `Content-Length`, `Cookie`, `Cookie2`, `Date`, `DNT`, `Expect`,
  `Host`, `Keep-Alive`, `Origin`, `Referer`, `Set-Cookie`, `TE`, `Trailer`,
  `Transfer-Encoding`, `Upgrade`, `Via`, any `Proxy-*` or `Sec-*`, and
  `X-HTTP-Method`, `X-HTTP-Method-Override`, `X-Method-Override` when the value
  names `CONNECT`, `TRACE` or `TRACK`. `User-Agent` is not forbidden.
- In `no-cors` mode only no-CORS-safelisted headers are sent: `Accept`,
  `Accept-Language`, `Content-Language` and `Content-Type`, each with a value
  that is CORS-safelisted (128-byte cap, the value rules of
  `IsCorsSafelistedRequestHeader`). `Range` is not included. Everything else is
  dropped silently, including `Authorization`.
- In `no-cors` mode a method other than GET, HEAD or POST (case-insensitive)
  rejects the op before anything is sent or recorded.
- In `same-origin` mode, a hop (the first or a redirect target) whose origin is
  not the page's returns the `corsBlocked` payload above, with a `corsError` of
  `Request mode is 'same-origin' but the URL's origin is not same as the request
  origin '<origin>'`, and is not sent.

### Host-decided origins (port deviation, SECURITY.md C1-C3, H4)

Rust takes every origin it judges by from the shim, which computed them with page
script's own `URL` global and read internal-load bodies with page script's own
`JSON.parse`. The port keeps every op signature and decides these host-side:

- `op_fetch_url`'s `origin` argument is ignored. The request origin is the calling
  realm's committed document origin (`StateHelpers.DocumentOrigin`: `"null"` for
  an opaque document or a sandboxed frame), and each realm's op table is bound to
  its own document.
- An `internal_load` result is `{status, body, requestId, url, redirected,
  opaque: false, sameOrigin, bodyToken, headers}`. The body stays host-side behind
  `bodyToken`; `body` carries it only for a frame document (`mode` `navigate`)
  whose response never left the document's origin (`sameOrigin` true, the host's
  verdict). A cross-origin
  result reports the request URL, `redirected` false and empty `headers`.
  `bodyBase64` is not sent.
- `op_post_frame_message` ignores `source_frame_id` and `origin` and fills both
  from the sending realm; `targetOrigin` is checked against the receiver's
  host-known origin before delivery as well as in the shim.

The filtered headers are what `Fetch.requestPaused` shows. Headers a CDP client
supplies through `Fetch.continueRequest` are not filtered, and nor are the
engine's own loads (`internal_load` true).

## Port-added ops (9)

These have no counterpart in `ops.rs`. For `op_run_classic_script` the shim as
shipped does not call it; `BootstrapSource.EngineText` rewrites a call site onto
it on the way into V8, so the shared JavaScript file stays untouched. The other
three ARE called from `bootstrap.js`, which is shared with the Rust engine, so
every one of those call sites is guarded by a `typeof ... === 'function'` test
and the shim falls back to exactly the behaviour it had before when the host does
not bind them.

| Op | Kind | Arguments | Returns |
|---|---|---|---|
| `op_run_classic_script` | sync | `source: String, url: String` | `(void)` |
| `op_resource_timings` | fast | `since_index: f64` | `String` (JSON array) |
| `op_resource_timing_count` | fast | `(none)` | `f64` |
| `op_font_resource_loaded` | fast | `url: &str` | `bool` |

Added with the host-decided origins above. `bootstrap.js` is the port's own now,
so these are called unguarded:

| Op | Kind | Arguments | Returns |
|---|---|---|---|
| `op_realm_origin` | fast | `frame_id: u32` | `String` (the realm's origin, `"null"` when opaque) |
| `op_run_fetched_script` | sync | `body_token: f64, url: String` | `(void)`; throws like `op_run_classic_script` |
| `op_frame_document_from_load` | fast | `body_token: f64, viewport_width: u64, viewport_height: u64, sandboxed: bool` | `u32` frame id, 0 when refused |
| `op_load_stylesheet` | async | `owner_nid: u32, url: String` | `String`: `{"ok":true,"responseUrl":...}` or `{"ok":false}` |
| `op_frame_same_origin` | fast | `frame_id: u32` | `f64`: 1 same-origin, 0 cross-origin, -1 unknown |

`op_run_fetched_script` runs the host-held body of a 2xx `no-cors` internal load
of the calling realm, named by its request URL. `op_frame_document_from_load`
queues a frame from the host-held body and final URL of a `navigate` internal load
(what `op_frame_document_ready` did from shim-supplied values; the shim no longer
calls that op). `op_load_stylesheet` fetches a dynamic `<link rel=stylesheet>` and
its `@import` graph host-side, rebases its `url()`s, computes origin-clean from the
responses and installs the result in the stylesheet store. A token is taken once,
only by the realm that loaded it and only in the mode it was loaded with.

`op_resource_timings` returns the subresources the host fetched for the current
document at index >= `since_index`, as a JSON array of
`{name, initiatorType, responseStatus, startedAt, endedAt, decodedBodySize,
encodedBodySize, contentType}`. `startedAt`/`endedAt` are unix-epoch
milliseconds; the shim rebases them onto `performance.timeOrigin`, which only it
knows. Records are appended, never trimmed from the front, so an index stays
valid; the host stops appending at 1000 records. It backs
`performance.getEntriesByType('resource')`. Only measured values travel: the
transport does not instrument DNS, TCP, TLS or the request/response split, and
those phases are absent from the payload and left at 0 by the shim, which is what
Resource Timing prescribes for a phase that cannot be reported.

`op_font_resource_loaded` answers whether the renderer holds usable bytes for an
already-absolute `@font-face` source URL. It backs the `status` of the
CSS-connected `FontFace` objects in `document.fonts`, and therefore
`document.fonts.check()`.

`op_run_classic_script` compiles `source` as a top-level classic script in the
calling realm. It replaces the two `(0, eval)(source)` calls that execute a
dynamically inserted classic script, which dropped the top-level `var` and
`function` declarations of any script beginning with `"use strict"` instead of
publishing them as globals. See "A dynamically inserted classic script runs as a
script, not as an eval" in `todo.md`.

It is the one op that deliberately does not go through `OpGuard`: both call sites
catch and report what the script threw, exactly as they did when eval threw it, so
the failure has to travel back into JavaScript rather than be contained.
