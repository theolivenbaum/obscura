// Generates the replaced-element sizing conformance pages: node gen-pages.mjs [outDir]
// (default render-repros/replaced-sizing). One page per formatting context and element kind;
// each page sizes the element through every combination of width/height (auto, px, %),
// min/max, aspect-ratio, box-sizing and object-fit, each in its own 400px wrapper. Compare with
// scripts/float-conformance/conformance.mjs render-repros/replaced-sizing '' '' --tol 0.5.
import fs from 'node:fs';
import zlib from 'node:zlib';

const out = process.argv[2] || new URL('../../render-repros/replaced-sizing', import.meta.url).pathname;
fs.mkdirSync(out, { recursive: true });

// A 150x36 solid PNG, the shape of the capcut.com logo that found the bug.
function png(w, h) {
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc = (buf) => {
    let c = 0xffffffff;
    for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const chunk = (type, data) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const td = Buffer.concat([Buffer.from(type), data]);
    const c = Buffer.alloc(4); c.writeUInt32BE(crc(td));
    return Buffer.concat([len, td, c]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 2;
  const row = Buffer.concat([Buffer.from([0]), Buffer.alloc(w * 3, 0x55)]);
  const raw = Buffer.concat(Array.from({ length: h }, () => row));
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}
const PNG = 'data:image/png;base64,' + png(150, 36).toString('base64');
// Ratio only: a viewBox and no width/height.
const SVG_RATIO = `data:image/svg+xml,%3Csvg xmlns=%22http://www.w3.org/2000/svg%22 viewBox=%220 0 150 36%22%3E%3Crect width=%22150%22 height=%2236%22 fill=%22%23e8590c%22/%3E%3C/svg%3E`;

const kinds = {
  png: (id, st, attrs = '') => `<img id=${id} ${attrs} style="${st}" src="${PNG}">`,
  svgimg: (id, st, attrs = '') => `<img id=${id} ${attrs} style="${st}" src="${SVG_RATIO}">`,
  svg: (id, st, attrs = '') => `<svg id=${id} ${attrs} style="${st}" viewBox="0 0 150 36"><rect width="150" height="36" fill="#e8590c"/></svg>`,
  canvas: (id, st, attrs = '') => `<canvas id=${id} ${attrs} style="${st}"></canvas>`,
  video: (id, st, attrs = '') => `<video id=${id} ${attrs} style="${st}"></video>`,
  iframe: (id, st, attrs = '') => `<iframe id=${id} ${attrs} style="${st}"></iframe>`,
  inputimg: (id, st, attrs = '') => `<input type=image id=${id} ${attrs} style="${st}" src="${PNG}">`,
};

// [name, style, attributes, wrapper extra style]
const sizes = [
  ['auto', '', ''],
  ['w', 'width:120px', ''],
  ['h', 'height:24px', ''],
  ['wh', 'width:120px;height:24px', ''],
  ['wh-contain', 'width:120px;height:24px;object-fit:contain', ''],
  ['wh-cover', 'width:120px;height:24px;object-fit:cover', ''],
  ['wpct', 'width:50%', ''],
  ['hpct', 'height:50%', '', 'height:100px'],
  ['hpct-auto', 'height:50%', ''],
  ['wpct-h', 'width:50%;height:24px', ''],
  ['wh-border-box', 'width:120px;height:24px;box-sizing:border-box;padding:4px;border:2px solid #333', ''],
  ['w-padding', 'width:120px;padding:4px;border:2px solid #333', ''],
  ['h-border-box', 'height:24px;box-sizing:border-box;padding:3px', ''],
  ['maxw', 'max-width:100px', ''],
  ['maxh', 'max-height:20px', ''],
  ['minw', 'min-width:200px', ''],
  ['minh', 'min-height:50px', ''],
  ['w-maxh', 'width:120px;max-height:20px', ''],
  ['h-maxw', 'height:24px;max-width:80px', ''],
  ['wh-maxh', 'width:120px;height:24px;max-height:20px', ''],
  ['wh-minw', 'width:120px;height:24px;min-width:200px', ''],
  ['w-maxw-pct', 'width:600px;max-width:100%', ''],
  ['maxw-pct', 'max-width:50%', ''],
  ['minw-maxh', 'min-width:200px;max-height:20px', ''],
  ['ar', 'aspect-ratio:2', ''],
  ['ar-w', 'aspect-ratio:2;width:120px', ''],
  ['ar-wh', 'aspect-ratio:2;width:120px;height:24px', ''],
  ['ar-auto', 'aspect-ratio:auto 1;width:120px', ''],
  ['w-hauto', 'width:120px;height:auto', ''],
  ['attrs', '', 'width=120 height=24'],
  ['attr-w', '', 'width=120'],
  ['attrs-css-w', 'width:60px', 'width=120 height=24'],
  ['attrs-css-hauto', 'width:60px;height:auto', 'width=120 height=24'],
];

const contexts = {
  inline: { wrap: 'width:400px', el: '', before: 'text ', after: ' tail' },
  block: { wrap: 'width:400px', el: 'display:block;' },
  flex: { wrap: 'width:400px;display:flex;align-items:flex-start' },
  'flex-stretch': { wrap: 'width:400px;display:flex' },
  'flex-col': { wrap: 'width:400px;display:flex;flex-direction:column;align-items:flex-start' },
  grid: { wrap: 'width:400px;display:grid;justify-items:start;align-items:start' },
  'grid-stretch': { wrap: 'width:400px;display:grid' },
  abs: { wrap: 'width:400px;position:relative;min-height:10px', el: 'position:absolute;left:0;top:0;' },
  float: { wrap: 'width:400px;overflow:hidden', el: 'float:left;' },
  'inline-block-parent': { wrap: 'width:400px', inner: 'display:inline-block' },
};

const base = `html, body { margin: 0; }
body { font: 16px/20px 'Liberation Sans', Arial, sans-serif; }
.w { margin: 0 0 4px; background: #f1f3f5; }
img, svg, canvas, video, iframe, input { background: #a5d8ff; }`;

let pages = 0;
for (const [ctxName, ctx] of Object.entries(contexts)) {
  for (const [kind, make] of Object.entries(kinds)) {
    let body = '';
    for (const [name, st, attrs, wrapExtra] of sizes) {
      const id = `${kind}-${name}`;
      const wrapStyle = [ctx.wrap, wrapExtra].filter(Boolean).join(';');
      // An absolutely positioned box needs a height on the wrapper for a percentage height.
      let el = make(id, (ctx.el || '') + st, attrs);
      if (ctx.inner) el = `<div style="${ctx.inner}" id=${id}-ib>${el}</div>`;
      body += `<div class=w id=${id}-w style="${wrapStyle}">${ctx.before || ''}${el}${ctx.after || ''}</div>\n`;
    }
    const html = `<!doctype html><html><head><meta charset=utf-8><style>${base}</style></head><body>\n${body}</body></html>\n`;
    fs.writeFileSync(`${out}/${ctxName}-${kind}.html`, html);
    pages++;
  }
}
console.log(`${pages} pages in ${out}`);
