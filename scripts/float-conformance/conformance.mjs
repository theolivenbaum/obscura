// Float conformance: every element's box, inline elements' line fragments and a hit test per
// element, in Chromium and in the port, for the pages in render-repros/floats/.
//
// usage: node conformance.mjs [pagesDir] [portBin] [filter] [--save chromium.json] [--verbose]
//                             [--tol px]
//   pagesDir defaults to render-repros/floats, portBin to the Release CLI build.
// Element boxes must agree within 1px, line fragments within 2px (4px in width); hit tests
// are reported, not scored, because elementFromPoint is the shim's heuristic. --tol sets one
// tolerance for every coordinate of both (render-repros/text-align is scored at 0.5).
import fs from 'node:fs';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { chromium } from 'playwright';

const run = promisify(execFile);
const REPO = new URL('../..', import.meta.url).pathname;
const args = process.argv.slice(2);
const saveIdx = args.indexOf('--save');
const save = saveIdx >= 0 ? args[saveIdx + 1] : null;
const verbose = args.includes('--verbose');
const tolIdx = args.indexOf('--tol');
const tol = tolIdx >= 0 ? Number(args[tolIdx + 1]) : null;
const positional = args.filter((a, i) => !a.startsWith('--')
  && (saveIdx < 0 || i !== saveIdx + 1) && (tolIdx < 0 || i !== tolIdx + 1));
const dir = positional[0] || `${REPO}render-repros/floats`;
const bin = positional[1] || process.env.POCKETCALCULATOR_PORT_BIN
  || `${REPO}dotnet/src/PocketCalculator.Cli/bin/Release/net10.0/pocket-calculator`;
const filter = positional[2];

const COLLECT = `(() => {
  const r2 = (v) => Math.round(v * 100) / 100;
  const box = (r) => [r2(r.x), r2(r.y), r2(r.width), r2(r.height)];
  const out = { el: {}, text: {}, hit: {} };
  for (const el of document.body.querySelectorAll('[id]')) {
    out.el[el.id] = box(el.getBoundingClientRect());
  }
  for (const el of document.body.querySelectorAll('[id]')) {
    if (getComputedStyle(el).display !== 'inline') continue;
    out.text[el.id] = Array.from(el.getClientRects()).filter(r => r.width > 0).map(box);
  }
  for (const el of document.body.querySelectorAll('[id]')) {
    const r = el.getBoundingClientRect();
    if (r.width < 4 || r.height < 4) continue;
    const hit = document.elementFromPoint(r.x + 2, r.y + 2);
    out.hit[el.id] = hit ? (hit.id || hit.tagName.toLowerCase()) : null;
  }
  return JSON.stringify(out);
})()`;

const files = fs.readdirSync(dir).filter((f) => f.endsWith('.html') && (!filter || f.includes(filter))).sort();

async function chromeAll() {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  const res = {};
  for (const f of files) {
    await page.goto('file://' + path.resolve(dir, f));
    res[f] = JSON.parse(await page.evaluate(COLLECT));
  }
  await browser.close();
  return res;
}

async function portOne(f) {
  try {
    const { stdout } = await run(bin, ['fetch', 'file://' + path.resolve(dir, f), '--quiet', '--eval', COLLECT], {
      env: { ...process.env, POCKETCALCULATOR_ALLOW_PRIVATE_NETWORK: '1' }, maxBuffer: 1 << 24, timeout: 60000,
    });
    let s = stdout.trim();
    if (s.startsWith('"')) s = JSON.parse(s);
    return JSON.parse(s);
  } catch (e) {
    return { error: String(e).slice(0, 300) };
  }
}

async function portAll() {
  const res = {};
  const queue = [...files];
  await Promise.all([0, 1, 2, 3].map(async () => {
    while (queue.length) {
      const f = queue.shift();
      res[f] = await portOne(f);
    }
  }));
  return res;
}

const near = (a, b, tol) => Math.abs(a - b) <= tol;
const boxOk = (a, b, tol) => a && b && a.every((v, i) => near(v, b[i], tol[i]));

const [cr, pc] = await Promise.all([chromeAll(), portAll()]);
if (save) fs.writeFileSync(save, JSON.stringify(cr, null, 1));

let totalEl = 0, okEl = 0, totalTx = 0, okTx = 0, totalHit = 0, okHit = 0, pagesOk = 0;
for (const f of files) {
  const c = cr[f], p = pc[f];
  const issues = [];
  if (p.error) { issues.push('ERROR ' + p.error); }
  else {
    for (const [id, b] of Object.entries(c.el)) {
      totalEl++;
      if (boxOk(b, p.el[id], tol !== null ? [tol, tol, tol, tol] : [1, 1, 1, 1])) okEl++;
      else issues.push(`el ${id}: chrome ${JSON.stringify(b)} port ${JSON.stringify(p.el[id])}`);
    }
    for (const [k, lines] of Object.entries(c.text)) {
      totalTx++;
      const pl = p.text[k] || [];
      const ok = lines.length === pl.length && lines.every((b, i) => boxOk(b, pl[i], tol !== null ? [tol, tol, tol, tol] : [2, 2, 4, 3]));
      if (ok) okTx++;
      else issues.push(`text ${k}: chrome ${JSON.stringify(lines)} port ${JSON.stringify(pl)}`);
    }
    for (const [id, h] of Object.entries(c.hit)) {
      totalHit++;
      if (p.hit[id] === h) okHit++;
      else if (verbose) console.log(`   (hit ${f} ${id}: chrome ${h} port ${p.hit[id]})`);
    }
  }
  if (issues.length === 0) pagesOk++;
  console.log(`${issues.length === 0 ? 'PASS' : 'FAIL'} ${f}${issues.length ? ` (${issues.length})` : ''}`);
  if (issues.length && verbose) for (const i of issues) console.log('   ' + i);
}
console.log(`pages ${pagesOk}/${files.length}  elements ${okEl}/${totalEl}  text ${okTx}/${totalTx}  hit ${okHit}/${totalHit}`);
