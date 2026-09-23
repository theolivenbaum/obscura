using Microsoft.ClearScript.V8;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// Mirrors bootstrap.js's dirty form state onto the arena DOM so the renderer can see it.
/// </summary>
/// <remarks>
/// DEVIATION from crates/obscura-js, which has no equivalent and where the renderer therefore
/// cannot see a value that came from script.
/// <para>
/// HTML keeps the <c>value</c> and <c>checked</c> IDL attributes off the content attributes once
/// they go dirty, and bootstrap.js models that with two plain globals keyed by node id
/// (<c>_formValues</c> / <c>_formChecked</c>). Both are declared as
/// <c>globalThis.X = globalThis.X || {}</c>, so installing them ahead of bootstrap.js hands it
/// these objects instead. They are ordinary objects behind a proxy whose write traps forward to
/// <c>op_dom</c>; reads, key order and <c>undefined</c> semantics are unchanged, which is what
/// bootstrap.js's <c>!== undefined</c> checks depend on.
/// </para>
/// <para>
/// The alternative was to have bootstrap.js call an op itself. That file is shared verbatim with
/// the Rust engine, so the mirror lives on this side of the boundary instead.
/// </para>
/// </remarks>
internal static class FormStateMirror
{
    /// <summary>
    /// Must run before bootstrap.js, which adopts whatever is already on the global.
    /// </summary>
    /// <remarks>
    /// <paramref name="frameId"/> is the realm's own frame id, fixed here. The mirror used
    /// to read the page-writable <c>globalThis.__obscura_frameId</c> on every write, so a
    /// frame's script could set it to another realm's id and write form state into that
    /// realm's document.
    /// </remarks>
    internal static void Install(V8ScriptEngine engine, uint frameId = 0)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var id = frameId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        engine.Execute("form-state-mirror", """
            (function (frameId) {
              // Captured now: globalThis.Deno is deleted once bootstrap.js has run.
              const ops = globalThis.Deno.core.ops;
              const mirror = (cmd, coerce) => new Proxy({}, {
                set(target, key, value) {
                  target[key] = value;
                  const nid = typeof key === 'string' ? key : null;
                  if (nid !== null && /^[0-9]+$/.test(nid)) {
                    try {
                      ops.op_dom(
                        cmd, nid, coerce(value), frameId);
                    } catch (_) {}
                  }
                  return true;
                },
              });
              globalThis._formValues = mirror('set_form_value', (v) => String(v));
              globalThis._formChecked = mirror('set_form_checked', (v) => (v ? 'true' : 'false'));
            })
            """ + "(" + id + ");");
    }

    /// <summary>
    /// The CDP isolated-world variant (SECURITY.md M6): the world's three form-state maps
    /// read and write the page realm's, so a value the page's script set is the value a
    /// world reads, and the other way round. Chromium keeps this state on the node, which
    /// every world shares.
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="Install"/>: proxies installed ahead of bootstrap.js, which
    /// adopts them. Numeric keys go through <c>op_world_call</c>, which only a world's op
    /// table has; the value encoding is bootstrap.js's <c>_worldValueEncode</c>.
    /// </remarks>
    internal static void InstallIsolatedWorld(V8ScriptEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.Execute("world-form-state", """
            (function () {
              const ops = globalThis.Deno.core.ops;
              const encode = (v) => v === undefined ? 'u' : v === null ? 'n'
                : typeof v === 'boolean' ? (v ? 'b1' : 'b0')
                : typeof v === 'number' ? 'd' + String(v) : 's' + String(v);
              const decode = (s) => {
                s = String(s);
                switch (s.charAt(0)) {
                  case 'n': return null;
                  case 'b': return s === 'b1';
                  case 'd': return Number(s.slice(1));
                  case 's': return s.slice(1);
                  default: return undefined;
                }
              };
              const numeric = (key) => typeof key === 'string' && /^[0-9]+$/.test(key);
              const shared = (name) => new Proxy({}, {
                get(target, key) {
                  if (!numeric(key)) return target[key];
                  try { return decode(ops.op_world_call('map-get:' + name, Number(key), '')); }
                  catch (_) { return undefined; }
                },
                set(target, key, value) {
                  if (!numeric(key)) { target[key] = value; return true; }
                  try { ops.op_world_call('map-set:' + name, Number(key), encode(value)); } catch (_) {}
                  return true;
                },
              });
              globalThis._formValues = shared('value');
              globalThis._formChecked = shared('checked');
              globalThis._formIndeterminate = shared('indeterminate');
            })();
            """);
    }
}
