// Live-site survey: Chromium (twice, for a noise baseline) vs PocketCalculator over CDP.
// Usage: node survey.mjs sites.txt [concurrency] [only-domain...]   (SURVEY_OUT sets the output dir, default ./out)
import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import fs from 'node:fs';

const UA = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36';
const VW = 1280, VH = 800;
const NAV_TIMEOUT = 45000, SETTLE_MS = 4000, ENGINE_BUDGET = 150000;
const REPO = new URL('../..', import.meta.url).pathname;
const BIN = process.env.POCKETCALCULATOR_PORT_BIN || `${REPO}dotnet/src/PocketCalculator.Cli/bin/Release/net10.0/pocket-calculator`;
const OUT = process.env.SURVEY_OUT || 'out';
fs.mkdirSync(`${OUT}/json`, { recursive: true });
fs.mkdirSync(`${OUT}/shots`, { recursive: true });

const PROBE = (VH) => {
  const o = { url: location.href, title: document.title, readyState: document.readyState };
  const all = document.getElementsByTagName('*');
  o.elements = all.length;
  const tags = {};
  for (const e of all) tags[e.localName] = (tags[e.localName] || 0) + 1;
  o.tags = tags;
  const q = (s) => { try { return document.querySelectorAll(s).length; } catch { return -1; } };
  o.counts = {
    links: q('a[href]'), img: q('img'), script: q('script'), iframe: q('iframe'), form: q('form'),
    input: q('input'), button: q('button'), svg: q('svg'), canvas: q('canvas'), video: q('video'),
    stylesheet: q('link[rel~="stylesheet"]'), style: q('style'), customEl: 0, shadowHosts: 0,
  };
  for (const e of all) { if (e.localName.includes('-')) o.counts.customEl++; if (e.shadowRoot) o.counts.shadowHosts++; }
  const skip = new Set(['script', 'style', 'noscript', 'template']);
  let dom = '';
  if (document.body) {
    const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let n = w.nextNode(); n && dom.length < 300000; n = w.nextNode()) {
      const p = n.parentElement;
      if (p && !skip.has(p.localName)) dom += n.data + ' ';
    }
  }
  o.domText = dom;
  try { o.innerText = document.body ? String(document.body.innerText).slice(0, 300000) : ''; } catch (e) { o.innerText = ''; o.innerTextError = String(e); }
  const de = document.documentElement;
  o.scrollHeight = de ? de.scrollHeight : 0;
  o.scrollWidth = de ? de.scrollWidth : 0;
  try {
    const cs = document.body && getComputedStyle(document.body);
    o.bodyStyle = cs ? { font: cs.fontFamily, size: cs.fontSize, color: cs.color, bg: cs.backgroundColor, margin: cs.margin } : null;
  } catch (e) { o.bodyStyle = { error: String(e) }; }
  const path = (e) => {
    const p = [];
    while (e && e.nodeType === 1 && e !== de) {
      let i = 1;
      for (let s = e.previousElementSibling; s; s = s.previousElementSibling) if (s.localName === e.localName) i++;
      p.push(e.localName + ':' + i);
      e = e.parentElement;
    }
    return p.reverse().join('>');
  };
  let visible = 0, inView = 0;
  const boxes = [];
  const lim = Math.min(all.length, 6000);
  for (let i = 0; i < lim; i++) {
    const e = all[i];
    let r;
    try { r = e.getBoundingClientRect(); } catch { continue; }
    if (!(r.width > 0 && r.height > 0)) continue;
    visible++;
    if (r.bottom > 0 && r.top < VH && r.right > 0 && r.left < 1280) inView++;
    if (r.top < 3 * VH && boxes.length < 2500) boxes.push([path(e), Math.round(r.x), Math.round(r.y), Math.round(r.width), Math.round(r.height)]);
  }
  o.visibleElements = visible; o.inViewport = inView; o.boxes = boxes;
  return o;
};

const withTimeout = (p, ms, what) => Promise.race([p, new Promise((_, rej) => setTimeout(() => rej(new Error(`timeout: ${what} after ${ms}ms`)), ms))]);

async function drive(page, url, label, domain) {
  const r = { engine: label, pageErrors: [], consoleErrors: [], requests: 0, requestsFailed: 0, failedSamples: [] };
  page.on('pageerror', (e) => r.pageErrors.length < 60 && r.pageErrors.push(String(e && e.message || e).slice(0, 400)));
  page.on('console', (m) => { if (m.type() === 'error' && r.consoleErrors.length < 60) r.consoleErrors.push(m.text().slice(0, 300)); });
  page.on('request', () => r.requests++);
  page.on('requestfailed', (q) => { r.requestsFailed++; if (r.failedSamples.length < 15) r.failedSamples.push(`${q.failure()?.errorText} ${q.url().slice(0, 150)}`); });
  const t0 = Date.now();
  try {
    const resp = await page.goto(url, { waitUntil: 'load', timeout: NAV_TIMEOUT });
    r.status = resp ? resp.status() : null;
  } catch (e) {
    r.navError = String(e.message || e).split('\n')[0].slice(0, 300);
  }
  r.loadMs = Date.now() - t0;
  await page.waitForTimeout(SETTLE_MS).catch(() => {});
  for (let attempt = 0; attempt < 3 && !r.probe; attempt++) {
    try { r.probe = await withTimeout(page.evaluate(PROBE, VH), 30000, 'probe'); delete r.probeError; }
    catch (e) {
      r.probeError = String(e.message || e).split('\n')[0].slice(0, 300);
      if (!/destroyed|navigat/i.test(r.probeError)) break;
      r.probeRetries = attempt + 1;
      await page.waitForTimeout(4000).catch(() => {});
    }
  }
  try { await withTimeout(page.screenshot({ path: `${OUT}/shots/${domain}.${label}.png`, timeout: 30000 }), 35000, 'screenshot'); r.shot = true; }
  catch (e) { r.shotError = String(e.message || e).split('\n')[0].slice(0, 300); }
  r.totalMs = Date.now() - t0;
  return r;
}

async function runChromium(browser, url, label, domain) {
  const ctx = await browser.newContext({ userAgent: UA, viewport: { width: VW, height: VH }, locale: 'en-US' });
  try {
    const page = await ctx.newPage();
    return await withTimeout(drive(page, url, label, domain), ENGINE_BUDGET, `${label} budget`);
  } catch (e) { return { engine: label, fatal: String(e.message || e).slice(0, 300) }; }
  finally { await ctx.close().catch(() => {}); }
}

async function runObscura(url, port, domain) {
  const proc = spawn(BIN, ['serve', '--port', String(port), '--user-agent', UA, ...(process.env.HTTPS_PROXY ? ['--proxy', process.env.HTTPS_PROXY] : []), '--quiet'], { env: process.env, stdio: ['ignore', 'pipe', 'pipe'] });
  let stderr = '', exit = null;
  proc.stderr.on('data', (d) => { stderr = (stderr + d).slice(-4000); });
  proc.stdout.on('data', () => {});
  proc.on('exit', (code, sig) => { exit = { code, sig }; });
  let browser;
  try {
    for (let i = 0; i < 60; i++) {
      try { const res = await fetch(`http://127.0.0.1:${port}/json/version`); if (res.ok) break; } catch {}
      await new Promise((r) => setTimeout(r, 250));
    }
    browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`, { timeout: 20000 });
    let ctx, page, ctxMode;
    try { ctx = await browser.newContext({ userAgent: UA, viewport: { width: VW, height: VH }, locale: 'en-US' }); page = await ctx.newPage(); ctxMode = 'new'; }
    catch (e) { ctx = browser.contexts()[0]; page = await ctx.newPage(); await page.setViewportSize({ width: VW, height: VH }).catch(() => {}); ctxMode = 'default:' + String(e.message).slice(0, 120); }
    const r = await withTimeout(drive(page, url, 'obscura', domain), ENGINE_BUDGET, 'obscura budget');
    r.ctxMode = ctxMode;
    return finish(r);
  } catch (e) {
    return finish({ engine: 'obscura', fatal: String(e.message || e).slice(0, 300) });
  } finally {
    await browser?.close().catch(() => {});
    proc.kill('SIGKILL');
  }
  function finish(r) { r.processExit = exit; r.stderrTail = stderr.slice(-1500); return r; }
}

const [, , siteFile, concArg, ...only] = process.argv;
let sites = fs.readFileSync(siteFile, 'utf8').split('\n').map((s) => s.trim()).filter(Boolean);
if (only.length) sites = sites.filter((s) => only.includes(s));
const conc = Number(concArg || 3);
const chrome = await chromium.launch(process.env.HTTPS_PROXY ? { proxy: { server: process.env.HTTPS_PROXY } } : {});
let next = 0, portSeq = 9400;
async function worker() {
  while (next < sites.length) {
    const domain = sites[next++];
    const url = `https://${domain}/`;
    const t = Date.now();
    const [a, b, o] = await Promise.all([
      runChromium(chrome, url, 'chromeA', domain),
      runChromium(chrome, url, 'chromeB', domain),
      runObscura(url, portSeq++, domain),
    ]);
    fs.writeFileSync(`${OUT}/json/${domain}.json`, JSON.stringify({ domain, url, at: new Date().toISOString(), chromeA: a, chromeB: b, obscura: o }));
    const s = (r) => r.fatal ? 'FATAL' : r.navError ? 'NAVERR' : (r.probe ? r.probe.elements : 'noprobe');
    console.log(`${domain}\t${((Date.now() - t) / 1000).toFixed(0)}s\tA=${s(a)} B=${s(b)} O=${s(o)} oErr=${o.pageErrors?.length ?? '-'}`);
  }
}
await Promise.all(Array.from({ length: conc }, worker));
await chrome.close();
