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

The op table is handed to the shim as `Deno.core.ops`. After bootstrap runs, the
host deletes `globalThis.Deno` so page script can never reach the ops, exactly as
deno_core does. The shim keeps its own captured reference.

## Ops (52)

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
| `op_fetch_url` | async | `url: String, method: String, headers_json: String, body: JsBuffer, origin: String, mode: String, credentials: String` | `String` |
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
