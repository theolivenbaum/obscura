// Script execution order conformance probe: Chromium vs PocketCalculator over CDP.
//
// A local server serves pages whose scripts log to window.L, with each external
// resource delayed by a known amount (/js?n=NAME&d=MS). Each page is loaded in
// Chromium and in the port; the logs are printed side by side.
//
// Usage: node probe.mjs [page...]      (PROBE_RUNS=n repeats each engine, PROBE_ENGINES=chromium,port)
import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import http from 'node:http';
import fs from 'node:fs';

const REPO = new URL('../..', import.meta.url).pathname;
const BIN = process.env.POCKETCALCULATOR_PORT_BIN || `${REPO}dotnet/src/PocketCalculator.Cli/bin/Release/net10.0/pocket-calculator`;

// Every page starts with this: a log, lifecycle listeners, and marker ids.
const HEAD = `<script>
window.L = [];
window.T = [];
window.log = function (s) { L.push(s); T.push(Date.now()); };
window.marks = function () { return [...document.querySelectorAll('.m')].map(e => e.id).join(',') || '-'; };
document.addEventListener('readystatechange', () => log('readystatechange ' + document.readyState + ' marks=' + marks()));
document.addEventListener('DOMContentLoaded', () => log('DOMContentLoaded marks=' + marks()));
window.addEventListener('load', () => log('load'));
log('head rs=' + document.readyState + ' body=' + !!document.body);
</script>`;

const js = (n, d, extra = '') => `/js?n=${n}&d=${d}${extra ? '&c=' + encodeURIComponent(extra) : ''}`;

const PAGES = {
  // Parser-blocking inline and external scripts: what DOM each sees, microtasks, a 0 ms timer.
  basic: `<!doctype html><html><head>${HEAD}
<script>
log('inline1 marks=' + marks() + ' body=' + !!document.body);
Promise.resolve().then(() => log('microtask from inline1'));
setTimeout(() => log('timeout0 from inline1'), 0);
</script>
<script>log('inline2')</script>
</head><body>
<div id=a class=m></div>
<script>log('inline3 marks=' + marks() + ' cs=' + (document.currentScript && document.currentScript.nextElementSibling))</script>
<div id=b class=m></div>
<script src="${js('ext1', 100)}"></script>
<div id=c class=m></div>
<script>log('inline4 marks=' + marks())</script>
<div id=d class=m></div>
</body></html>`,

  // Async scripts and timers run while the parser is blocked on a slow script.
  async: `<!doctype html><html><head>${HEAD}
<script>setTimeout(() => log('timeout50 marks=' + marks()), 50);</script>
<script src="${js('asyncFast', 20)}" async></script>
<script src="${js('asyncSlow', 900)}" async></script>
<script>
var s = document.createElement('script'); s.src = '${js('dynFast', 20)}';
s.onload = () => log('dynFast onload'); document.head.appendChild(s);
</script>
</head><body>
<div id=a class=m></div>
<script src="${js('blocking', 400)}"></script>
<div id=b class=m></div>
<script>log('inline after blocking marks=' + marks())</script>
<div id=c class=m></div>
</body></html>`,

  // defer and module scripts: order among themselves, readyState, DOMContentLoaded.
  defer: `<!doctype html><html><head>${HEAD}
<script src="${js('defer1', 300)}" defer></script>
<script type=module src="${js('module1', 10)}"></script>
<script src="${js('defer2', 10)}" defer></script>
<script type=module>log('inlineModule rs=' + document.readyState + ' marks=' + marks())</script>
<script type=module async>log('inlineModuleAsync rs=' + document.readyState + ' marks=' + marks())</script>
<script type=module async src="${js('moduleAsync', 10)}"></script>
<script src="${js('asyncDuringDefer', 150)}" async></script>
<script defer>log('inline defer is ignored rs=' + document.readyState)</script>
</head><body>
<div id=a class=m></div>
<script src="${js('blocking', 50)}"></script>
<div id=b class=m></div>
</body></html>`,

  // Dynamically inserted scripts: async by default, async=false keeps insertion order,
  // inline ones run at insertion.
  dynamic: `<!doctype html><html><head>${HEAD}
<script>
function add(n, d, ordered) {
  var s = document.createElement('script'); s.src = '/js?n=' + n + '&d=' + d;
  if (ordered) s.async = false;
  s.onload = () => log(n + ' onload');
  document.head.appendChild(s);
}
add('ordered1', 300, true);
add('ordered2', 10, true);
add('asyncDyn', 100, false);
var i = document.createElement('script'); i.textContent = "log('dynamic inline runs at insertion')";
document.head.appendChild(i);
log('after inserting');
document.addEventListener('DOMContentLoaded', () => { add('fromDCL', 10, false); });
</script>
</head><body>
<div id=a class=m></div>
<script>log('inline body marks=' + marks())</script>
<div id=b class=m></div>
</body></html>`,

  // document.write: inline written scripts run inside write(), external ones block the parser.
  write: `<!doctype html><html><head>${HEAD}</head><body>
<div id=a class=m></div>
<script>
document.write('<div id=w1 class=m></div><script>log("written inline marks=" + marks())<\\/script><div id=w2 class=m></div>');
log('after write1 marks=' + marks());
document.write('<script src="${js('writtenExt', 100)}"><\\/script><div id=w3 class=m></div>');
log('after write2 marks=' + marks());
document.write('<span id=w4 cla'); document.write('ss=m>x</span>');
log('after write3 marks=' + marks());
</script>
<script>log('next inline marks=' + marks())</script>
<div id=b class=m></div>
</body></html>`,

  // Custom elements defined by an earlier script are constructed by the parser.
  customElements: `<!doctype html><html><head>${HEAD}
<script>
customElements.define('x-a', class extends HTMLElement {
  constructor() { super(); log('x-a constructor attrs=' + this.attributes.length + ' children=' + this.childNodes.length + ' connected=' + this.isConnected); }
  connectedCallback() { log('x-a connected id=' + this.id + ' children=' + this.childNodes.length); }
});
</script>
</head><body>
<x-a id=first class=m><span>child</span></x-a>
<script>log('after first marks=' + marks())</script>
<x-a id=second class=m></x-a>
</body></html>`,

  // A parser-blocking script waits for a pending style sheet in head.
  stylesheet: `<!doctype html><html><head>${HEAD}
<link rel=stylesheet href="/css?d=300">
<script>log('inline after sheet color=' + getComputedStyle(document.documentElement).color)</script>
</head><body>
<div id=a class=m></div>
<script>log('body inline marks=' + marks())</script>
</body></html>`,

  // A MutationObserver installed during parsing sees parser insertions.
  mutation: `<!doctype html><html><head>${HEAD}
<script>
new MutationObserver(rs => {
  var added = [];
  for (var r of rs) for (var n of r.addedNodes) if (n.nodeType === 1) added.push(n.localName + (n.id ? '#' + n.id : ''));
  log('mutations ' + added.join(','));
}).observe(document, { childList: true, subtree: true });
</script>
</head><body>
<div id=a class=m><p id=p1></p></div>
<script>log('inline marks=' + marks())</script>
<div id=b class=m></div>
</body></html>`,

  // An async classic script and a later parser import map: the map is registered when the
  // parser reaches it, before the async script has loaded.
  importMap: `<!doctype html><html><head>${HEAD}
<script async src="${js('async', 0, "import('too-late').then(() => log('import resolved'), () => log('import rejected'))")}"></script>
<script type=importmap>{"imports":{"too-late":"/js?n=later&d=0"}}</script>
</head><body></body></html>`,

  // Parser-inserted external scripts fire load and error at the element.
  scriptEvents: `<!doctype html><html><head>${HEAD}
<script src="${js('ok', 10)}" onload="log('ok onload')" onerror="log('ok onerror')"></script>
<script src="/missing.js" onload="log('missing onload')" onerror="log('missing onerror')"></script>
<script src="${js('asyncOk', 10)}" async onload="log('asyncOk onload')"></script>
</head><body></body></html>`,

  // Tasks queued at the end of parsing, and from DOMContentLoaded, against DOMContentLoaded and load.
  taskBoundaries: `<!doctype html><html><head>${HEAD}
<script>document.addEventListener('DOMContentLoaded', () => { setTimeout(() => log('timeout0 from DOMContentLoaded'), 0); Promise.resolve().then(() => log('microtask from DOMContentLoaded')); });</script>
</head><body><div id=a class=m></div>
<script>setTimeout(() => log('timeout0 at end of body'), 0); Promise.resolve().then(() => log('microtask at end of body')); log('last script');</script>
</body></html>`,

  // A heavy page for timing: 3000 sections, 60 inline scripts, 20 external scripts.
  heavy: `<!doctype html><html><head>${HEAD}
${Array.from({ length: 10 }, (_, i) => `<script src="${js('head' + i, 30)}"></script>`).join('')}
</head><body>
${Array.from({ length: 3000 }, (_, i) => `<section id=s${i}><h2>Section ${i}</h2><p>Lorem <b>ipsum</b> dolor <a href=#s${i}>sit</a> amet.</p>${i % 50 === 0 ? `<script>window.n=(window.n||0)+document.getElementsByTagName('section').length;</script>` : ''}${i % 300 === 0 ? `<script src="${js('body' + i, 20)}" defer></script>` : ''}</section>`).join('\n')}
<script>log('end n=' + window.n)</script>
</body></html>`,

  // grammarly.com's shape: a parser-blocking script at the top of the app root inserts
  // a dynamic script next to itself; the dynamic script removes it; the app's defer
  // scripts later check the root (hydration).
  hydration: `<!doctype html><html><head>${HEAD}
<script src="${js('framework', 200, "log('framework')")}" defer></script>
<script src="${js('main', 50, "log('hydrate root children=' + document.getElementById('root').children.length + ' ' + [...document.getElementById('root').children].map(e => e.localName + (e.id ? '#' + e.id : '')).join(','))")}" defer></script>
</head><body>
<div id=root>
<script id=gate src="${js('gate', 100, "var u = document.createElement('script'); u.id = 'ui'; u.src = '/js?n=ui&d=80&c=' + encodeURIComponent(\"document.getElementById('ui').remove(); log('ui removed itself')\"); document.currentScript.after(u); log('gate inserted ui')")}"></script>
<div id=content class=m>${'<p>lorem ipsum dolor sit amet</p>'.repeat(2000)}</div>
</div>
</body></html>`,
};

let servedAt = 0;
function startServer() {
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://x');
    const d = +(url.searchParams.get('d') || 0);
    if (process.env.PROBE_REQLOG) console.error(`[req] ${Date.now() - servedAt} ${url.pathname}${url.searchParams.get('n') ? ' ' + url.searchParams.get('n') : ''}`);
    const reply = (type, body) => setTimeout(() => {
      res.writeHead(200, { 'content-type': type, 'cache-control': 'no-store' });
      res.end(body);
    }, d);
    if (url.pathname === '/js') {
      const n = url.searchParams.get('n');
      const c = url.searchParams.get('c') || '';
      return reply('text/javascript', `log(${JSON.stringify(n)} + ' rs=' + document.readyState + ' marks=' + marks());${c}`);
    }
    if (url.pathname === '/css') return reply('text/css', 'html { color: rgb(1, 2, 3) }');
    const page = PAGES[url.pathname.slice(1)];
    if (!page) { res.writeHead(404); return res.end(); }
    // The document itself takes as long as a fast real server, which is also time the port
    // uses to create the page's JavaScript runtime (Chromium's renderer is already up).
    setTimeout(() => {
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
      servedAt = Date.now();
      res.end(page);
    }, +(process.env.PROBE_HTML_DELAY ?? 150));
  });
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(server)));
}

async function startPort() {
  const port = 9300 + Math.floor(Math.random() * 500);
  const child = spawn(BIN, ['serve', '--port', String(port), '--allow-private-network'], {
    env: { ...process.env, POCKETCALCULATOR_ALLOW_PRIVATE_NETWORK: '1' },
    stdio: ['ignore', 'ignore', 'pipe'],
  });
  let stderr = '';
  child.stderr.on('data', (b) => { stderr += b; if (process.env.PROBE_STDERR) process.stderr.write(b); });
  for (let i = 0; i < 100; i++) {
    try {
      const r = await fetch(`http://127.0.0.1:${port}/json/version`);
      if (r.ok) return { child, endpoint: `http://127.0.0.1:${port}` };
    } catch { }
    await new Promise((r) => setTimeout(r, 100));
  }
  throw new Error('port did not start: ' + stderr);
}

async function warmUp(browser, base) {
  // One navigation first, so neither engine's first page pays its own start-up.
  const context = browser.contexts()[0] || await browser.newContext();
  const page = await context.newPage();
  await page.goto(`${base}/basic`, { waitUntil: 'load', timeout: 20000 }).catch(() => { });
  await page.close();
}

async function run(browser, base, name) {
  const context = browser.contexts()[0] || await browser.newContext();
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e.message || e)));
  const t0 = Date.now();
  await page.goto(`${base}/${name}`, { waitUntil: 'load', timeout: 20000 });
  const loadMs = Date.now() - t0;
  await page.waitForTimeout(1200);
  const [log, stamps] = await page.evaluate(() => [window.L, window.T]);
  const times = stamps.map((t) => t - servedAt);
  await page.close();
  return { log: errors.length ? [...log, ...errors.map((e) => 'PAGEERROR ' + e)] : log, times, loadMs };
}

const server = await startServer();
const base = `http://127.0.0.1:${server.address().port}`;
if (process.env.PROBE_SERVE) {
  // Serve the pages only (for the CLI, or a browser by hand).
  console.log(base);
  await new Promise(() => { });
}
const names = process.argv.slice(2).length ? process.argv.slice(2) : Object.keys(PAGES);
const runs = +(process.env.PROBE_RUNS || 1);
const engines = (process.env.PROBE_ENGINES || 'chromium,port').split(',');
const results = {};

if (engines.includes('chromium')) {
  const browser = await chromium.launch();
  await warmUp(browser, base);
  for (const name of names) {
    results[name] ??= {};
    results[name].chromium = [];
    for (let i = 0; i < runs; i++) results[name].chromium.push(await run(browser, base, name));
  }
  await browser.close();
}
if (engines.includes('port')) {
  const { child, endpoint } = await startPort();
  try {
    const browser = await chromium.connectOverCDP(endpoint);
    await warmUp(browser, base);
    for (const name of names) {
      results[name] ??= {};
      results[name].port = [];
      for (let i = 0; i < runs; i++) {
        try { results[name].port.push(await run(browser, base, name)); }
        catch (e) { results[name].port.push({ log: ['NAVIGATION ' + e.message.split('\n')[0]], loadMs: -1 }); }
      }
    }
    await browser.close().catch(() => { });
  } finally { child.kill('SIGKILL'); }
}
server.close();

let differing = 0;
for (const name of names) {
  const r = results[name];
  const c = r.chromium?.[0]?.log || [];
  const p = r.port?.[0]?.log || [];
  const ct = r.chromium?.[0]?.times || [];
  const pt = r.port?.[0]?.times || [];
  const times = !!process.env.PROBE_TIMES;
  const same = JSON.stringify(c) === JSON.stringify(p);
  if (!same) differing++;
  console.log(`\n=== ${name} ${same ? 'SAME' : 'DIFFERENT'}  (load ms chromium ${r.chromium?.map((x) => x.loadMs).join('/')} port ${r.port?.map((x) => x.loadMs).join('/')})`);
  const rows = Math.max(c.length, p.length);
  for (let i = 0; i < rows; i++) {
    const a = c[i] ?? '', b = p[i] ?? '';
    const ta = times ? String(ct[i] ?? '').padStart(5) + ' ' : '', tb = times ? String(pt[i] ?? '').padStart(5) + ' ' : '';
    console.log(`${a === b ? ' ' : '*'} ${ta}${a.padEnd(66).slice(0, 66)} | ${tb}${b}`);
  }
}
if (process.env.PROBE_JSON) fs.writeFileSync(process.env.PROBE_JSON, JSON.stringify(results, null, 1));
console.log(`\n${differing} of ${names.length} pages differ`);
