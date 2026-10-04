using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// DOM event dispatch against Chromium: listener options (capture, once, passive, signal,
/// duplicates), the event path with its capture, at-target and bubble phases through the
/// document to the window, shadow trees (retargeting, composedPath, closed roots,
/// relatedTarget), propagation and cancelation flags, snapshot semantics for listeners added
/// or removed during dispatch, exceptions reported to the window, handleEvent, and on*
/// event handlers (IDL and content attributes, their place among listeners, return values).
/// Every expected line was printed by Chromium 141.0.7390.37 (headless, Playwright) for the
/// same markup and script; the probe leaves out the Event shape (own isTrusted) and the
/// exact SyntaxError text, which the port does not match (see todo.md).
/// </summary>
public sealed class EventDispatchConformanceTests
{
    private const string Markup = """
<!doctype html>
<html id="html"><head><title>ev</title></head>
<body id="body">
<div id="outer"><div id="inner"></div></div>
<div id="host"></div>
<div id="chost"></div>
<form id="f" name="fname"><input id="q" name="q" value="qv" onclick="R.push('attr:' + this.id + ':' + value + ':' + typeof elements + ':' + typeof getElementById + ':' + (event && event.type) + ':' + typeof fname); return false;"></form>
<button id="b2" onclick="R.push('b2attr')">x</button>
</body></html>
""";

    private const string Probe = """
var R = [];
window.__results = {};
window.__errors = [];
function id(n) {
  if (n === window) return 'W';
  if (n === document) return 'D';
  if (n === null) return 'null';
  if (n === undefined) return 'undef';
  if (n && n.nodeType === 11) return 'SR(' + (n.host ? n.host.id : '?') + ')';
  return (n && (n.id || n.nodeName)) || String(n);
}
var $ = function (s) { return document.getElementById(s); };
window.addEventListener('error', function (e) { var syn = e.error instanceof SyntaxError; __errors.push('werr:' + (syn ? 'Uncaught SyntaxError' : e.message) + ':' + (syn ? 'syntax' : (e.error && e.error.message)) + ':' + (e.target === window) + ':' + e.cancelable + ':' + typeof e.lineno); });

function section(name, fn) {
  var out = [];
  try { fn(out); } catch (e) { out.push('THREW ' + e.name + ': ' + e.message); }
  if (__errors.length) { out.push('errors=' + __errors.join(' ; ')); __errors.length = 0; }
  window.__results[name] = out;
}

// 1. options and duplicate detection
section('options', function (L) {
  var t = $('inner');
  var calls = 0;
  function f(e) { calls++; L.push('f:' + e.eventPhase); }
  t.addEventListener('o1', f);
  t.addEventListener('o1', f);          // dup
  t.addEventListener('o1', f, false);   // dup
  t.addEventListener('o1', f, {capture: false}); // dup
  t.addEventListener('o1', f, true);    // distinct (capture)
  t.addEventListener('o1', f, {capture: true, once: true}); // dup of capture
  t.dispatchEvent(new Event('o1'));
  L.push('calls=' + calls);
  calls = 0;
  t.removeEventListener('o1', f, {capture: false, once: true, passive: true});
  t.dispatchEvent(new Event('o1'));
  L.push('afterRemoveBubble=' + calls);
  calls = 0;
  t.removeEventListener('o1', f, true);
  t.dispatchEvent(new Event('o1'));
  L.push('afterRemoveCapture=' + calls);
  // once
  var n = 0;
  t.addEventListener('o2', function g() { n++; L.push('once-nested-start'); t.dispatchEvent(new Event('o2')); L.push('once-nested-end'); }, {once: true});
  t.dispatchEvent(new Event('o2'));
  t.dispatchEvent(new Event('o2'));
  L.push('once n=' + n);
  // once on document and window
  var dn = 0, wn = 0;
  document.addEventListener('o3', function () { dn++; }, {once: true});
  window.addEventListener('o3', function () { wn++; }, {once: true});
  t.dispatchEvent(new Event('o3', {bubbles: true}));
  t.dispatchEvent(new Event('o3', {bubbles: true}));
  L.push('doc once=' + dn + ' win once=' + wn);
  // options getter access order
  var seen = [];
  var opts = {};
  ['capture', 'once', 'passive', 'signal'].forEach(function (k) { Object.defineProperty(opts, k, { get: function () { seen.push(k); return undefined; } }); });
  t.addEventListener('o4', function () {}, opts);
  L.push('getters=' + seen.join(','));
  seen = [];
  t.removeEventListener('o4', function () {}, opts);
  L.push('rmgetters=' + seen.join(','));
  // null and non-callable
  t.addEventListener('o5', null);
  t.addEventListener('o5', {});
  t.dispatchEvent(new Event('o5'));
  try { t.addEventListener('o5'); L.push('no-throw-1arg'); } catch (e) { L.push('1arg:' + e.name); }
  try { t.addEventListener(); L.push('no-throw-0arg'); } catch (e) { L.push('0arg:' + e.name); }
  try { t.dispatchEvent({type: 'x'}); L.push('plainobj ok'); } catch (e) { L.push('plainobj:' + e.name); }
  try { t.dispatchEvent(); L.push('noarg ok'); } catch (e) { L.push('noarg:' + e.name); }
  try { t.addEventListener('o6', 5); L.push('num ok'); } catch (e) { L.push('num:' + e.name); }
  try { t.addEventListener('o6', 'str'); L.push('str ok'); } catch (e) { L.push('str:' + e.name); }
  // type coercion
  var k = 0;
  t.addEventListener(123, function () { k++; });
  t.dispatchEvent(new Event('123'));
  L.push('coerced=' + k);
});

// 2. signal
section('signal', function (L) {
  var t = $('inner');
  var c = new AbortController();
  var n = 0;
  t.addEventListener('s1', function () { n++; }, {signal: c.signal});
  document.addEventListener('s1', function () { n += 10; }, {signal: c.signal});
  window.addEventListener('s1', function () { n += 100; }, {signal: c.signal});
  t.dispatchEvent(new Event('s1', {bubbles: true}));
  L.push('before=' + n);
  c.abort();
  t.dispatchEvent(new Event('s1', {bubbles: true}));
  L.push('after=' + n);
  var c2 = new AbortController(); c2.abort();
  var m = 0;
  t.addEventListener('s2', function () { m++; }, {signal: c2.signal});
  t.dispatchEvent(new Event('s2'));
  L.push('preaborted=' + m);
  // abort during dispatch removes later listener at same node
  var c3 = new AbortController();
  var seq = [];
  t.addEventListener('s3', function () { seq.push('a'); c3.abort(); });
  t.addEventListener('s3', function () { seq.push('b'); }, {signal: c3.signal});
  t.dispatchEvent(new Event('s3'));
  L.push('abortDuring=' + seq.join(''));
  try { t.addEventListener('s4', function () {}, {signal: null}); L.push('nullsignal ok'); } catch (e) { L.push('nullsignal:' + e.name); }
  try { t.addEventListener('s4', function () {}, {signal: {}}); L.push('objsignal ok'); } catch (e) { L.push('objsignal:' + e.name); }
});

// 3. passive
section('passive', function (L) {
  var t = $('inner');
  t.addEventListener('p1', function (e) { e.preventDefault(); L.push('p1 dp=' + e.defaultPrevented + ' rv=' + e.returnValue); }, {passive: true});
  var r = t.dispatchEvent(new Event('p1', {cancelable: true}));
  L.push('p1 ret=' + r);
  t.addEventListener('p2', function (e) { e.returnValue = false; L.push('p2 dp=' + e.defaultPrevented); }, {passive: true});
  L.push('p2 ret=' + t.dispatchEvent(new Event('p2', {cancelable: true})));
  // passive then non-passive at same node
  t.addEventListener('p3', function (e) { e.preventDefault(); }, {passive: true});
  t.addEventListener('p3', function (e) { L.push('p3 second sees ' + e.defaultPrevented); e.preventDefault(); });
  L.push('p3 ret=' + t.dispatchEvent(new Event('p3', {cancelable: true})));
  // default passive
  ['touchstart', 'touchmove', 'wheel', 'mousewheel', 'touchend', 'mousedown'].forEach(function (type) {
    var targets = [['W', window], ['D', document], ['H', document.documentElement], ['B', document.body], ['I', t]];
    var res = [];
    targets.forEach(function (pair) {
      function h(e) { e.preventDefault(); res.push(pair[0] + (e.defaultPrevented ? '1' : '0')); }
      pair[1].addEventListener(type, h);
      var ev = new Event(type, {cancelable: true, bubbles: true});
      pair[1].dispatchEvent(ev);
      pair[1].removeEventListener(type, h);
    });
    L.push(type + ':' + res.join(','));
  });
  // explicit passive:false on window wheel
  function h2(e) { e.preventDefault(); L.push('explicit false dp=' + e.defaultPrevented); }
  window.addEventListener('wheel', h2, {passive: false});
  window.dispatchEvent(new Event('wheel', {cancelable: true}));
  window.removeEventListener('wheel', h2);
});

// 4. phases
function phaseTree(L, type, init, regAtTargetBubbleFirst) {
  var nodes = [['W', window], ['D', document], ['H', document.documentElement], ['B', document.body], ['O', $('outer')], ['I', $('inner')]];
  var hs = [];
  nodes.forEach(function (p) {
    var cap = function (e) { L.push(p[0] + 'c' + e.eventPhase + ':' + id(e.currentTarget) + '/' + id(e.target)); };
    var bub = function (e) { L.push(p[0] + 'b' + e.eventPhase + ':' + id(e.currentTarget) + '/' + id(e.target)); };
    if (regAtTargetBubbleFirst) { p[1].addEventListener(type, bub, false); p[1].addEventListener(type, cap, true); }
    else { p[1].addEventListener(type, cap, true); p[1].addEventListener(type, bub, false); }
    hs.push([p[1], cap, bub]);
  });
  var ev = new Event(type, init);
  var r = $('inner').dispatchEvent(ev);
  L.push('ret=' + r + ' phase=' + ev.eventPhase + ' ct=' + id(ev.currentTarget) + ' target=' + id(ev.target) + ' path=' + ev.composedPath().length);
  hs.forEach(function (h) { h[0].removeEventListener(type, h[1], true); h[0].removeEventListener(type, h[2], false); });
}
section('phases-bubbles', function (L) { phaseTree(L, 'ph1', {bubbles: true}, true); });
section('phases-nobubble', function (L) { phaseTree(L, 'ph2', {bubbles: false}, false); });
section('phases-window', function (L) {
  var ev = new Event('ph3', {bubbles: true});
  window.addEventListener('ph3', function (e) { L.push('wb' + e.eventPhase + ':' + id(e.currentTarget) + '/' + id(e.target) + ' cp=' + e.composedPath().map(id).join(',')); });
  window.addEventListener('ph3', function (e) { L.push('wc' + e.eventPhase); }, true);
  L.push('ret=' + window.dispatchEvent(ev));
  var ev2 = new Event('ph4', {bubbles: true});
  document.addEventListener('ph4', function (e) { L.push('db' + e.eventPhase + ':' + id(e.currentTarget) + ' cp=' + e.composedPath().map(id).join(',')); });
  window.addEventListener('ph4', function (e) { L.push('wb' + e.eventPhase + ':' + id(e.currentTarget)); });
  document.dispatchEvent(ev2);
  // composedPath during dispatch from inner
  $('inner').addEventListener('ph5', function (e) { L.push('cp=' + e.composedPath().map(id).join(',')); });
  $('inner').dispatchEvent(new Event('ph5'));
  // detached node
  var d = document.createElement('div'); var c = document.createElement('span'); d.appendChild(c);
  d.addEventListener('ph6', function (e) { L.push('detached ' + e.eventPhase + ' ' + id(e.currentTarget) + ' cp=' + e.composedPath().map(id).join(',')); });
  c.dispatchEvent(new Event('ph6', {bubbles: true}));
  // text node target
  var tx = document.createTextNode('x'); $('outer').appendChild(tx);
  $('outer').addEventListener('ph7', function (e) { L.push('text ' + e.eventPhase + ' t=' + id(e.target) + ' cp=' + e.composedPath().length); });
  tx.dispatchEvent(new Event('ph7', {bubbles: true}));
  tx.remove();
  // load event does not reach window from a node
  var wl = 0;
  window.addEventListener('load', function lw() { wl++; });
  var img = document.createElement('img'); document.body.appendChild(img);
  document.body.addEventListener('load', function (e) { L.push('body saw load ' + e.eventPhase); });
  document.body.addEventListener('load', function (e) { L.push('body saw load capture ' + e.eventPhase); }, true);
  document.addEventListener('load', function (e) { L.push('doc saw load ' + e.eventPhase + ' cp=' + e.composedPath().map(id).join(',')); }, true);
  img.dispatchEvent(new Event('load', {bubbles: true}));
  L.push('window load listener=' + wl);
  img.remove();
});

// 5. shadow DOM
section('shadow', function (L) {
  var host = $('host');
  var sr = host.attachShadow({mode: 'open'});
  sr.innerHTML = '<div id="sdiv"><span id="sspan"></span><slot id="sslot"></slot></div>';
  var light = document.createElement('b'); light.id = 'light'; host.appendChild(light);
  var span = sr.getElementById('sspan');
  var all = [['W', window], ['D', document], ['B', document.body], ['Hst', host], ['SR', sr], ['Sd', sr.getElementById('sdiv')], ['Sp', span]];
  function wire(type) {
    var hs = [];
    all.forEach(function (p) {
      var c = function (e) { L.push(type + ' ' + p[0] + 'c' + e.eventPhase + ' t=' + id(e.target) + ' cp=' + e.composedPath().map(id).join(',')); };
      var b = function (e) { L.push(type + ' ' + p[0] + 'b' + e.eventPhase + ' t=' + id(e.target)); };
      p[1].addEventListener(type, c, true); p[1].addEventListener(type, b);
      hs.push([p[1], c, b]);
    });
    return function () { hs.forEach(function (h) { h[0].removeEventListener(type, h[1], true); h[0].removeEventListener(type, h[2]); }); };
  }
  var un = wire('sh1');
  var ev = new Event('sh1', {bubbles: true, composed: true});
  span.dispatchEvent(ev);
  L.push('after t=' + id(ev.target) + ' ct=' + id(ev.currentTarget) + ' cp=' + ev.composedPath().length + ' phase=' + ev.eventPhase);
  un();
  un = wire('sh2');
  ev = new Event('sh2', {bubbles: true, composed: false});
  span.dispatchEvent(ev);
  L.push('after t=' + id(ev.target));
  un();
  // slotted light child: path goes through slot
  light.addEventListener('sh3', function (e) { L.push('light cp=' + e.composedPath().map(id).join(',')); });
  sr.getElementById('sslot').addEventListener('sh3', function (e) { L.push('slot t=' + id(e.target) + ' phase=' + e.eventPhase); });
  sr.addEventListener('sh3', function (e) { L.push('sr t=' + id(e.target) + ' phase=' + e.eventPhase); });
  host.addEventListener('sh3', function (e) { L.push('host t=' + id(e.target) + ' phase=' + e.eventPhase); });
  light.dispatchEvent(new Event('sh3', {bubbles: true}));
  // event dispatched on the shadow root itself, non-composed
  host.addEventListener('sh4', function (e) { L.push('host saw sh4'); });
  sr.addEventListener('sh4', function (e) { L.push('sr saw sh4 phase=' + e.eventPhase); });
  sr.dispatchEvent(new Event('sh4', {bubbles: true}));
  sr.dispatchEvent(new Event('sh4', {bubbles: true, composed: true}));
  // host target, composed from inside: phase at host is AT_TARGET
  // closed root
  var ch = $('chost');
  var csr = ch.attachShadow({mode: 'closed'});
  csr.innerHTML = '<i id="ci"></i>';
  var ci = csr.getElementById('ci');
  ci.addEventListener('sh5', function (e) { L.push('inside cp=' + e.composedPath().map(id).join(',')); });
  document.addEventListener('sh5', function (e) { L.push('doc t=' + id(e.target) + ' cp=' + e.composedPath().map(id).join(',')); });
  ch.addEventListener('sh5', function (e) { L.push('chost phase=' + e.eventPhase + ' cp=' + e.composedPath().map(id).join(',')); });
  var e5 = new Event('sh5', {bubbles: true, composed: true});
  ci.dispatchEvent(e5);
  L.push('closed after t=' + id(e5.target) + ' cp=' + e5.composedPath().length);
  // relatedTarget retargeting
  var e6 = new MouseEvent('mouseover', {bubbles: true, composed: true, relatedTarget: sr.getElementById('sdiv')});
  span.addEventListener('mouseover', function (e) { L.push('rt at span: ' + id(e.relatedTarget)); });
  host.addEventListener('mouseover', function (e) { L.push('rt at host: ' + id(e.relatedTarget) + ' t=' + id(e.target)); });
  document.body.addEventListener('mouseover', function (e) { L.push('rt at body: ' + id(e.relatedTarget)); });
  span.dispatchEvent(e6);
  L.push('rt after: ' + id(e6.relatedTarget) + ' t=' + id(e6.target));
  var e7 = new MouseEvent('mouseover', {bubbles: true, composed: true, relatedTarget: $('outer')});
  span.dispatchEvent(e7);
  L.push('rt7 after: ' + id(e7.relatedTarget) + ' t=' + id(e7.target));
  var e8 = new FocusEvent('focusin', {bubbles: true, composed: true, relatedTarget: span});
  sr.getElementById('sdiv').addEventListener('focusin', function (e) { L.push('fi at sdiv rt=' + id(e.relatedTarget)); });
  host.addEventListener('focusin', function (e) { L.push('fi at host rt=' + id(e.relatedTarget)); });
  document.addEventListener('focusin', function (e) { L.push('fi at doc rt=' + id(e.relatedTarget)); });
  sr.getElementById('sdiv').dispatchEvent(e8);
  L.push('fi after t=' + id(e8.target) + ' rt=' + id(e8.relatedTarget));
});

// 6. propagation control
section('propagation', function (L) {
  var o = $('outer'), i = $('inner');
  function reg(t, type, name, cap, fn) { t.addEventListener(type, function (e) { L.push(name); if (fn) fn(e); }, cap); }
  reg(o, 'pr1', 'Oc', true, function (e) { e.stopPropagation(); L.push('cb=' + e.cancelBubble); });
  reg(o, 'pr1', 'Oc2', true);
  reg(i, 'pr1', 'Ib', false);
  reg(document, 'pr1', 'Dc', true);
  i.dispatchEvent(new Event('pr1', {bubbles: true}));
  reg(i, 'pr2', 'I1', false, function (e) { e.stopImmediatePropagation(); });
  reg(i, 'pr2', 'I2', false);
  reg(o, 'pr2', 'O', false);
  i.dispatchEvent(new Event('pr2', {bubbles: true}));
  reg(i, 'pr3', 'I1', false, function (e) { e.cancelBubble = true; L.push('cb=' + e.cancelBubble); e.cancelBubble = false; L.push('cb2=' + e.cancelBubble); });
  reg(i, 'pr3', 'I2', false);
  reg(o, 'pr3', 'O', false);
  var e3 = new Event('pr3', {bubbles: true});
  i.dispatchEvent(e3);
  L.push('after cb=' + e3.cancelBubble);
  // stop at target capture listener: bubble listener at target still runs
  reg(i, 'pr4', 'Ic', true, function (e) { e.stopPropagation(); });
  reg(i, 'pr4', 'Ib', false);
  reg(o, 'pr4', 'Ob', false);
  i.dispatchEvent(new Event('pr4', {bubbles: true}));
  // stopPropagation before dispatch
  var e5 = new Event('pr5', {bubbles: true});
  e5.stopPropagation();
  reg(i, 'pr5', 'I', false);
  L.push('pre-stopped ret=' + i.dispatchEvent(e5));
  reg(i, 'pr5', 'I-again', false);
  i.dispatchEvent(e5);
  // cancelBubble before dispatch
  var e6 = new Event('pr6', {bubbles: true});
  e6.cancelBubble = true;
  reg(i, 'pr6', 'I6', false);
  i.dispatchEvent(e6);
  // preventDefault/returnValue/defaultPrevented
  var e7 = new Event('pr7', {cancelable: true});
  L.push('rv0=' + e7.returnValue + ' dp0=' + e7.defaultPrevented);
  e7.returnValue = false;
  L.push('rv1=' + e7.returnValue + ' dp1=' + e7.defaultPrevented);
  e7.returnValue = true;
  L.push('rv2=' + e7.returnValue + ' dp2=' + e7.defaultPrevented);
  var e8 = new Event('pr8', {cancelable: false});
  e8.preventDefault(); L.push('noncancel dp=' + e8.defaultPrevented);
  reg(i, 'pr9', 'I9', false, function (e) { e.preventDefault(); });
  L.push('ret cancel=' + i.dispatchEvent(new Event('pr9', {cancelable: true})) + ' noncancel=' + i.dispatchEvent(new Event('pr9')));
  var e10 = new Event('pr10', {cancelable: true});
  e10.preventDefault();
  L.push('preventBefore dp=' + e10.defaultPrevented + ' ret=' + i.dispatchEvent(e10) + ' dp=' + e10.defaultPrevented);
  // isTrusted, timeStamp
  var e11 = new Event('pr11');
  L.push('isTrusted=' + e11.isTrusted + ' tsType=' + typeof e11.timeStamp + ' tsNear=' + (Math.abs(e11.timeStamp - performance.now()) < 5000));
  L.push('consts=' + Event.NONE + Event.CAPTURING_PHASE + Event.AT_TARGET + Event.BUBBLING_PHASE + e11.AT_TARGET);
  L.push('srcElement=' + id(e11.srcElement));
  i.addEventListener('pr12', function (e) { L.push('srcElement in=' + id(e.srcElement) + ' composed=' + e.composed); });
  i.dispatchEvent(new Event('pr12'));
  // re-dispatch
  var cnt = 0;
  i.addEventListener('pr13', function (e) { cnt++; if (cnt === 1) { e.stopPropagation(); e.preventDefault(); } });
  o.addEventListener('pr13', function () { cnt += 10; });
  var e13 = new Event('pr13', {bubbles: true, cancelable: true});
  L.push('r1=' + i.dispatchEvent(e13) + ' cnt=' + cnt);
  L.push('r2=' + i.dispatchEvent(e13) + ' cnt=' + cnt + ' dp=' + e13.defaultPrevented);
  // dispatch during dispatch throws
  i.addEventListener('pr14', function (e) { try { o.dispatchEvent(e); L.push('nested ok'); } catch (x) { L.push('nested ' + x.name); } });
  i.dispatchEvent(new Event('pr14'));
  // initEvent during dispatch ignored
  i.addEventListener('pr15', function (e) { e.initEvent('zzz', false, false); L.push('type in=' + e.type); });
  i.dispatchEvent(new Event('pr15'));
  var e16 = new Event('pr16'); e16.stopPropagation(); e16.initEvent('pr16', true, true);
  L.push('init clears stop: cb=' + e16.cancelBubble);
  i.addEventListener('pr16', function () { L.push('pr16 ran'); });
  i.dispatchEvent(e16);
});

// 7. listener mutation during dispatch
section('mutation', function (L) {
  var o = $('outer'), i = $('inner');
  function C() { L.push('C'); }
  function B() { L.push('B (added during)'); }
  i.addEventListener('m1', function A() { L.push('A'); i.addEventListener('m1', B); i.removeEventListener('m1', C); o.addEventListener('m1', function () { L.push('O added during'); }); });
  i.addEventListener('m1', C);
  i.dispatchEvent(new Event('m1', {bubbles: true}));
  L.push('--second');
  i.dispatchEvent(new Event('m1'));
  // remove and re-add same during dispatch: re-added one does not run
  function E() { L.push('E'); }
  i.addEventListener('m2', function () { L.push('D'); i.removeEventListener('m2', E); i.addEventListener('m2', E); });
  i.addEventListener('m2', E);
  i.dispatchEvent(new Event('m2'));
  // capture listener added at target during bubble at target
  i.addEventListener('m3', function () { L.push('bub'); i.addEventListener('m3', function () { L.push('late cap'); }, true); });
  i.dispatchEvent(new Event('m3'));
  // window listener added during dispatch at node
  i.addEventListener('m4', function () { L.push('I'); window.addEventListener('m4', function () { L.push('W added'); }); });
  i.dispatchEvent(new Event('m4', {bubbles: true}));
});

// 8. exceptions
section('exceptions', function (L) {
  var i = $('inner');
  var saved = window.onerror;
  window.onerror = function (msg, src, line, col, err) { L.push('onerror:' + msg + ':' + typeof src + ':' + typeof line + ':' + (err && err.message)); };
  i.addEventListener('x1', function () { L.push('a'); throw new Error('boom'); });
  i.addEventListener('x1', function () { L.push('b'); });
  $('outer').addEventListener('x1', function () { L.push('outer'); });
  i.dispatchEvent(new Event('x1', {bubbles: true}));
  i.addEventListener('x2', function () { throw 'str'; });
  i.dispatchEvent(new Event('x2'));
  i.addEventListener('x3', {handleEvent: 5});
  i.dispatchEvent(new Event('x3'));
  window.onerror = function () { L.push('onerror returns true'); return true; };
  var errEv;
  window.addEventListener('error', function eh(e) { errEv = e; window.removeEventListener('error', eh); });
  i.addEventListener('x4', function () { throw new TypeError('tt'); });
  i.dispatchEvent(new Event('x4'));
  L.push('errEv dp=' + (errEv && errEv.defaultPrevented) + ' trusted=' + (errEv && errEv.isTrusted) + ' type=' + (errEv && errEv.constructor.name));
  window.onerror = saved;
});

// 9. handleEvent and this
section('handleEvent', function (L) {
  var i = $('inner');
  var obj = { handleEvent: function (e) { L.push('h1 this=obj:' + (this === obj) + ' ct=' + id(e.currentTarget)); } };
  i.addEventListener('h1', obj);
  i.addEventListener('h1', obj);
  i.dispatchEvent(new Event('h1'));
  obj.handleEvent = function () { L.push('h1 replaced'); };
  i.dispatchEvent(new Event('h1'));
  var gets = 0;
  var obj2 = {}; Object.defineProperty(obj2, 'handleEvent', { get: function () { gets++; return function () { L.push('getter-handle'); }; } });
  i.addEventListener('h2', obj2);
  L.push('gets after add=' + gets);
  i.dispatchEvent(new Event('h2'));
  L.push('gets after dispatch=' + gets);
  i.addEventListener('h3', function () { L.push('fn this=' + id(this)); });
  i.dispatchEvent(new Event('h3'));
  document.addEventListener('h3', function () { L.push('doc fn this=' + id(this)); });
  window.addEventListener('h3', function () { L.push('win fn this=' + id(this)); });
  i.dispatchEvent(new Event('h3', {bubbles: true}));
  var fobj = function () { L.push('fn with handleEvent called as fn'); };
  fobj.handleEvent = function () { L.push('fn.handleEvent called'); };
  i.addEventListener('h4', fobj);
  i.dispatchEvent(new Event('h4'));
  // arrow and bound
  var arrow = (e) => L.push('arrow ct=' + id(e.currentTarget));
  i.addEventListener('h5', arrow);
  i.dispatchEvent(new Event('h5'));
});

// 10. on* handlers
section('onhandlers', function (L) {
  var i = $('inner');
  i.addEventListener('click', function () { L.push('L1'); });
  i.onclick = function () { L.push('onclick1'); };
  i.addEventListener('click', function () { L.push('L2'); });
  i.click();
  L.push('--');
  i.onclick = function () { L.push('onclick2 (same slot)'); };
  i.click();
  L.push('--');
  i.onclick = null;
  i.onclick = function (e) { L.push('onclick3 (end) this=' + id(this) + ' ' + e.type); return false; };
  var ev = new MouseEvent('click', {cancelable: true, bubbles: true});
  L.push('ret=' + i.dispatchEvent(ev) + ' dp=' + ev.defaultPrevented);
  i.onclick = function () { return true; };
  var ev2 = new MouseEvent('click', {cancelable: true});
  i.dispatchEvent(ev2);
  L.push('return true dp=' + ev2.defaultPrevented);
  i.onclick = null;
  L.push('onclick typeof=' + typeof i.onclick + ' v=' + i.onclick);
  i.onclick = 'str';
  L.push('onclick str=' + i.onclick);
  i.onclick = {handleEvent: function () { L.push('obj handler'); }};
  i.dispatchEvent(new Event('click'));
  L.push('obj onclick=' + typeof i.onclick);
  i.onclick = null;
  // content attribute
  var q = $('q');
  var qe = new MouseEvent('click', {cancelable: true, bubbles: true});
  q.dispatchEvent(qe);
  L.push('attr dp=' + qe.defaultPrevented);
  L.push('q.onclick typeof=' + typeof q.onclick + ' name=' + (q.onclick && q.onclick.name) + ' len=' + (q.onclick && q.onclick.length));
  q.setAttribute('onclick', "R.push('attr2 ' + this.id)");
  q.dispatchEvent(new Event('click'));
  // attribute set after listener: order
  var b2 = $('b2');
  R.length = 0;
  b2.addEventListener('click', function () { R.push('b2L'); });
  b2.dispatchEvent(new Event('click'));
  b2.setAttribute('onclick', "R.push('b2attr-new')");
  b2.dispatchEvent(new Event('click'));
  b2.removeAttribute('onclick');
  b2.dispatchEvent(new Event('click'));
  b2.setAttribute('onclick', "R.push('b2attr-readded')");
  b2.addEventListener('click', function () { R.push('b2L2'); });
  b2.dispatchEvent(new Event('click'));
  L.push('b2=' + R.join(','));
  R.length = 0;
  // syntax error in attribute
  var d = document.createElement('div');
  d.setAttribute('onclick', 'R.push(');
  document.body.appendChild(d);
  d.dispatchEvent(new Event('click'));
  L.push('syntax onclick=' + d.onclick);
  d.remove();
  // onfoo on element for custom type: no handler
  i.onfoo = function () { L.push('onfoo should not run'); };
  i.dispatchEvent(new Event('foo'));
  delete i.onfoo;
  // document and window handlers
  document.onclick = function (e) { L.push('document.onclick phase=' + e.eventPhase); };
  window.onclick = function (e) { L.push('window.onclick phase=' + e.eventPhase + ' this=' + id(this)); };
  i.dispatchEvent(new MouseEvent('click', {bubbles: true}));
  document.onclick = null; window.onclick = null;
  // onerror on window special: return true cancels; args
  var e9 = new ErrorEvent('error', {message: 'mm', filename: 'ff', lineno: 3, colno: 4, error: 'EE', cancelable: true});
  var saved = window.onerror;
  window.onerror = function (a, b, c, d, e) { L.push('onerror args=' + [a, b, c, d, e].join('|')); return true; };
  window.dispatchEvent(e9);
  L.push('onerror dp=' + e9.defaultPrevented);
  window.onerror = function () { return false; };
  var e10 = new ErrorEvent('error', {cancelable: true});
  window.dispatchEvent(e10);
  L.push('onerror false dp=' + e10.defaultPrevented);
  // onerror on element gets just event
  i.onerror = function (a) { L.push('el onerror arg=' + (a && a.type) + ' n=' + arguments.length); return true; };
  var e11 = new ErrorEvent('error', {cancelable: true, message: 'x'});
  i.dispatchEvent(e11);
  L.push('el onerror true dp=' + e11.defaultPrevented);
  i.onerror = null;
  window.onerror = saved;
  // descriptor shapes
  var dsc = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'onclick');
  L.push('HTMLElement.onclick desc=' + (dsc ? (!!dsc.get) + ',' + (!!dsc.set) + ',' + dsc.enumerable : 'none'));
  L.push('own onclick on el=' + Object.prototype.hasOwnProperty.call(i, 'onclick'));
  L.push('window onclick own=' + Object.prototype.hasOwnProperty.call(window, 'onclick'));
});

// 11. window
section('window', function (L) {
  var r = [];
  window.addEventListener('resize', function (e) { r.push('resize ' + e.eventPhase + ' ' + id(e.target)); });
  window.onresize = function () { r.push('onresize'); };
  window.dispatchEvent(new Event('resize'));
  window.onresize = null;
  window.addEventListener('hashchange', function (e) { r.push('hashchange ' + e.constructor.name); });
  window.dispatchEvent(new HashChangeEvent('hashchange'));
  window.addEventListener('popstate', function (e) { r.push('popstate ' + e.state); });
  window.dispatchEvent(new PopStateEvent('popstate', {state: 7}));
  window.addEventListener('unhandledrejection', function (e) { r.push('unh-listener'); });
  L.push(r.join(','));
  L.push('window path=' + (function () { var p; window.addEventListener('wp', function (e) { p = e.composedPath().map(id).join(','); }); window.dispatchEvent(new Event('wp')); return p; })());
  var et = new EventTarget();
  et.addEventListener('e', function (e) { L.push('ET ' + e.eventPhase + ' ' + (e.currentTarget === et) + ' cp=' + e.composedPath().length); });
  et.addEventListener('e', function (e) { L.push('ET cap ' + e.eventPhase); }, true);
  et.dispatchEvent(new Event('e', {bubbles: true}));
});
""";

    public static TheoryData<string, string> Sections => new()
    {
        { "options", "f:2\nf:2\ncalls=2\nf:2\nafterRemoveBubble=1\nafterRemoveCapture=0\nonce-nested-start\nonce-nested-end\nonce n=1\ndoc once=1 win once=1\ngetters=capture,once,passive,signal\nrmgetters=capture\n1arg:TypeError\n0arg:TypeError\nplainobj:TypeError\nnoarg:TypeError\nnum:TypeError\nstr:TypeError\ncoerced=1" },
        { "signal", "before=111\nafter=111\npreaborted=0\nabortDuring=a\nnullsignal:TypeError\nobjsignal:TypeError" },
        { "passive", "p1 dp=false rv=true\np1 ret=true\np2 dp=false\np2 ret=true\np3 second sees false\np3 ret=false\ntouchstart:W0,D0,H0,B0,I1\ntouchmove:W0,D0,H0,B0,I1\nwheel:W0,D0,H0,B0,I1\nmousewheel:W0,D0,H0,B0,I1\ntouchend:W1,D1,H1,B1,I1\nmousedown:W1,D1,H1,B1,I1\nexplicit false dp=true" },
        { "phases-bubbles", "Wc1:W/inner\nDc1:D/inner\nHc1:html/inner\nBc1:body/inner\nOc1:outer/inner\nIc2:inner/inner\nIb2:inner/inner\nOb3:outer/inner\nBb3:body/inner\nHb3:html/inner\nDb3:D/inner\nWb3:W/inner\nret=true phase=0 ct=null target=inner path=0" },
        { "phases-nobubble", "Wc1:W/inner\nDc1:D/inner\nHc1:html/inner\nBc1:body/inner\nOc1:outer/inner\nIc2:inner/inner\nIb2:inner/inner\nret=true phase=0 ct=null target=inner path=0" },
        { "phases-window", "wb2:W/W cp=W\nwc2\nret=true\ndb2:D cp=D,W\nwb3:W\ncp=inner,outer,body,html,D,W\ndetached 3 DIV cp=SPAN,DIV\ntext 3 t=#text cp=6\ndoc saw load 1 cp=IMG,body,html,D\nbody saw load capture 1\nbody saw load 3\nwindow load listener=0" },
        { "shadow", "sh1 Wc1 t=host cp=sspan,sdiv,SR(host),host,body,html,D,W\nsh1 Dc1 t=host cp=sspan,sdiv,SR(host),host,body,html,D,W\nsh1 Bc1 t=host cp=sspan,sdiv,SR(host),host,body,html,D,W\nsh1 Hstc2 t=host cp=sspan,sdiv,SR(host),host,body,html,D,W\nsh1 SRc1 t=sspan cp=sspan,sdiv,SR(host),host,body,html,D,W\nsh1 Sdc1 t=sspan cp=sspan,sdiv,SR(host),host,body,html,D,W\nsh1 Spc2 t=sspan cp=sspan,sdiv,SR(host),host,body,html,D,W\nsh1 Spb2 t=sspan\nsh1 Sdb3 t=sspan\nsh1 SRb3 t=sspan\nsh1 Hstb2 t=host\nsh1 Bb3 t=host\nsh1 Db3 t=host\nsh1 Wb3 t=host\nafter t=host ct=null cp=0 phase=0\nsh2 SRc1 t=sspan cp=sspan,sdiv,SR(host)\nsh2 Sdc1 t=sspan cp=sspan,sdiv,SR(host)\nsh2 Spc2 t=sspan cp=sspan,sdiv,SR(host)\nsh2 Spb2 t=sspan\nsh2 Sdb3 t=sspan\nsh2 SRb3 t=sspan\nafter t=null\nlight cp=light,sslot,sdiv,SR(host),host,body,html,D,W\nslot t=light phase=3\nsr t=light phase=3\nhost t=light phase=3\nsr saw sh4 phase=2\nsr saw sh4 phase=2\nhost saw sh4\ninside cp=ci,SR(chost),chost,body,html,D,W\nchost phase=2 cp=chost,body,html,D,W\ndoc t=chost cp=chost,body,html,D,W\nclosed after t=chost cp=0\nrt at span: sdiv\nrt after: null t=null\nrt at span: outer\nrt at host: outer t=host\nrt at body: outer\nrt7 after: outer t=host\nfi at sdiv rt=sspan\nfi after t=null rt=null" },
        { "propagation", "Dc\nOc\ncb=true\nOc2\nI1\nI1\ncb=true\ncb2=true\nI2\nafter cb=false\nIc\npre-stopped ret=true\nI\nI-again\nrv0=true dp0=false\nrv1=false dp1=true\nrv2=false dp2=true\nnoncancel dp=false\nI9\nI9\nret cancel=false noncancel=true\npreventBefore dp=true ret=false dp=true\nisTrusted=false tsType=number tsNear=true\nconsts=01232\nsrcElement=null\nsrcElement in=inner composed=false\nr1=false cnt=1\nr2=false cnt=12 dp=true\nnested InvalidStateError\ntype in=pr15\ninit clears stop: cb=false\npr16 ran" },
        { "mutation", "A\nO added during\n--second\nA\nB (added during)\nD\nbub\nI\nW added" },
        { "exceptions", "a\nonerror:Uncaught Error: boom:string:number:boom\nb\nouter\nonerror:Uncaught str:string:number:undefined\nonerror returns true\nerrEv dp=true trusted=true type=ErrorEvent\nerrors=werr:Uncaught Error: boom:boom:true:true:number ; werr:Uncaught str:undefined:true:true:number ; werr:Uncaught TypeError: tt:tt:true:true:number" },
        { "handleEvent", "h1 this=obj:true ct=inner\nh1 replaced\ngets after add=0\ngetter-handle\ngets after dispatch=1\nfn this=inner\nfn this=inner\ndoc fn this=D\nwin fn this=W\nfn with handleEvent called as fn\narrow ct=inner" },
        { "onhandlers", "L1\nonclick1\nL2\n--\nL1\nonclick2 (same slot)\nL2\n--\nL1\nL2\nonclick3 (end) this=inner click\nret=false dp=true\nL1\nL2\nreturn true dp=false\nonclick typeof=object v=null\nonclick str=null\nL1\nL2\nobj onclick=object\nattr dp=true\nq.onclick typeof=function name=onclick len=1\nb2=b2attr,b2L,b2attr-new,b2L,b2L,b2L,b2attr-readded,b2L2\nsyntax onclick=null\nL1\nL2\ndocument.onclick phase=3\nwindow.onclick phase=3 this=W\nonerror args=mm|ff|3|4|EE\nonerror dp=true\nonerror false dp=false\nel onerror arg=error n=1\nel onerror true dp=false\nHTMLElement.onclick desc=true,true,true\nown onclick on el=false\nwindow onclick own=true\nerrors=werr:Uncaught SyntaxError:syntax:true:true:number ; werr:mm:undefined:true:true:number ; werr::undefined:true:true:number" },
        { "window", "resize 2 W,onresize,hashchange HashChangeEvent,popstate 7\nwindow path=W\nET 2 true cp=0\nET cap 2" },
    };

    [Theory]
    [MemberData(nameof(Sections))]
    public void DispatchMatchesChromium(string section, string expected)
    {
        using var fixture = RuntimeFixture.Setup(Markup);
        var rt = fixture.Runtime;
        rt.ExecuteScript("probe", Probe);
        var actual = rt.Evaluate("window.__results['" + section + "'].join('\\n')")!.GetValue<string>();
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// DOMContentLoaded bubbles from the document to the window (capturing window listener
    /// first); the window's load event targets the document, runs window.onload among the
    /// window's listeners in registration order (capturing ones included) and does not reach
    /// the document; a timer's exception reaches the window's error listeners. All trusted.
    /// The test runtime has no page loader, so the lifecycle steps are run by hand; window
    /// listeners fire there as in the CLI.
    /// </summary>
    [Fact]
    public async Task LifecycleEventsAndTimerErrorsReachTheWindow()
    {
        using var fixture = RuntimeFixture.Setup("<!doctype html><html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript("setup", """
            globalThis.A = [];
            const id = (n) => n === window ? 'W' : n === document ? 'D' : String(n && n.nodeName);
            window.addEventListener('load', (e) => A.push('win load L1 t=' + id(e.target) + ' phase=' + e.eventPhase + ' trusted=' + e.isTrusted));
            window.onload = () => A.push('window.onload');
            window.addEventListener('load', () => A.push('win load L2'));
            window.addEventListener('load', () => A.push('win load cap'), true);
            document.addEventListener('load', () => A.push('doc load (should not)'));
            document.addEventListener('DOMContentLoaded', (e) => A.push('DCL doc phase=' + e.eventPhase + ' bubbles=' + e.bubbles + ' trusted=' + e.isTrusted));
            window.addEventListener('DOMContentLoaded', (e) => A.push('DCL win phase=' + e.eventPhase + ' t=' + id(e.target)));
            window.addEventListener('DOMContentLoaded', (e) => A.push('DCL win cap phase=' + e.eventPhase), true);
            window.addEventListener('error', (e) => A.push('werr:' + e.message + ':' + (e.error && e.error.message) + ':' + e.isTrusted));
            setTimeout(() => { throw new Error('late'); }, 0);
            """);
        rt.RunLifecycle("DOMContentLoaded", "load");
        await rt.RunEventLoopBoundedAsync(100);
        Assert.Equal(
            "DCL win cap phase=1|DCL doc phase=2 bubbles=true trusted=true|DCL win phase=3 t=D|"
            + "win load L1 t=D phase=2 trusted=true|window.onload|win load L2|win load cap|werr:Uncaught Error: late:late:true",
            rt.Evaluate("A.join('|')")!.GetValue<string>());
    }

    /// <summary>
    /// A &lt;body onload&gt; attribute is the window's load handler: the same function through
    /// document.body.onload and window.onload, named onload, run once by the window's load,
    /// ahead of listeners added afterwards (the parser set it first).
    /// </summary>
    [Fact]
    public void BodyOnloadAttributeIsTheWindowHandler()
    {
        using var fixture = RuntimeFixture.Setup("<!doctype html><html><body onload=\"A.push('attr ' + (this === window))\"></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript("setup", """
            globalThis.A = [];
            A.push(String(document.body.onload === window.onload) + ' ' + window.onload.name + ' ' + ('onhashchange' in document.body) + ' ' + ('onhashchange' in document.createElement('div')));
            window.addEventListener('load', () => A.push('listener'));
            """);
        rt.RunLifecycle("load");
        Assert.Equal("true onload true false|attr true|listener", rt.Evaluate("A.join('|')")!.GetValue<string>());
    }

    /// <summary>
    /// focus() and blur() fire blur/focusout then focus/focusin, trusted FocusEvents, composed,
    /// each naming the other element as relatedTarget; focus and blur do not bubble but pass the
    /// window and document in the capture phase. click() is composed, so a click in a shadow
    /// tree reaches the document retargeted to the host, and window.event is undefined while a
    /// listener inside the shadow tree runs.
    /// </summary>
    [Fact]
    public void FocusEventsAndComposedClick()
    {
        using var fixture = RuntimeFixture.Setup("<!doctype html><html><body><input id=a><input id=b><div id=host></div></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript("setup", """
            globalThis.L = [];
            const id = (n) => n === window ? 'W' : n === document ? 'D' : n === null ? 'null' : (n.id || n.nodeName);
            const a = document.getElementById('a'), b = document.getElementById('b');
            ['focus', 'blur', 'focusin', 'focusout'].forEach((t) => {
              document.addEventListener(t, (e) => L.push(t + ' t=' + id(e.target) + ' rt=' + id(e.relatedTarget) + ' phase=' + e.eventPhase + ' bubbles=' + e.bubbles + ' ' + e.constructor.name + ' composed=' + e.composed + ' trusted=' + e.isTrusted), true);
            });
            window.addEventListener('focus', (e) => L.push('window focus listener t=' + id(e.target)), true);
            a.focus();
            b.focus();
            b.blur();
            const sr = document.getElementById('host').attachShadow({ mode: 'open' });
            sr.innerHTML = '<button id="sb">x</button>';
            sr.getElementById('sb').addEventListener('click', () => L.push('in shadow window.event=' + typeof window.event));
            document.addEventListener('click', (e) => L.push('doc t=' + id(e.target) + ' window.event=' + (window.event === e) + ' composed=' + e.composed));
            sr.getElementById('sb').click();
            """);
        Assert.Equal(
            "window focus listener t=a|focus t=a rt=null phase=1 bubbles=false FocusEvent composed=true trusted=true|"
            + "focusin t=a rt=null phase=1 bubbles=true FocusEvent composed=true trusted=true|"
            + "blur t=a rt=b phase=1 bubbles=false FocusEvent composed=true trusted=true|"
            + "focusout t=a rt=b phase=1 bubbles=true FocusEvent composed=true trusted=true|"
            + "window focus listener t=b|focus t=b rt=a phase=1 bubbles=false FocusEvent composed=true trusted=true|"
            + "focusin t=b rt=a phase=1 bubbles=true FocusEvent composed=true trusted=true|"
            + "blur t=b rt=null phase=1 bubbles=false FocusEvent composed=true trusted=true|"
            + "focusout t=b rt=null phase=1 bubbles=true FocusEvent composed=true trusted=true|"
            + "in shadow window.event=undefined|doc t=host window.event=true composed=true",
            rt.Evaluate("L.join('|')")!.GetValue<string>());
    }
}
