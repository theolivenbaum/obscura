// Generates the text alignment conformance pages: node gen-pages.mjs [outDir]
// (default render-repros/text-align). Compare them with
//   node ../float-conformance/conformance.mjs ../../render-repros/text-align '' '' --tol 0.5
// Every line worth measuring sits in an inline element with an id, so its line fragments
// (getClientRects) are compared; atomic inlines are compared by their boxes.
import fs from 'node:fs';
import path from 'node:path';

const out = process.argv[2] || new URL('../../render-repros/text-align', import.meta.url).pathname;
fs.mkdirSync(out, { recursive: true });

const T = 'Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua';
const HE = 'שלום עולם זהו משפט ארוך בעברית שנועד לבדוק שבירת שורות ויישור טקסט מימין לשמאל בדפדפן';
const AR = 'مرحبا بالعالم هذه جملة طويلة باللغة العربية لاختبار تقسيم الأسطر ومحاذاة النص من اليمين إلى اليسار';
const MIX = 'שלום עולם hello world זהו משפט עם English words בתוכו וגם מספרים 12345 בסוף';

// Each word of `text` in its own span, ids prefix0.. so per-word positions are compared.
const words = (prefix, text) => text.split(' ').map((w, i) => `<span id=${prefix}${i}>${w}</span>`).join(' ');

const base = `html, body { margin: 0; }
body { font: 16px/20px 'Liberation Sans', sans-serif; }
.c { width: 300px; background: #f1f3f5; margin-bottom: 10px; }
.he { font-family: 'DejaVu Sans', sans-serif; }
.ib { display: inline-block; width: 40px; height: 12px; background: #1971c2; }
.r { float: right; } .l { float: left; }
.f { width: 60px; height: 30px; background: #087f5b; }
p { margin: 0; }`;

const align = (a, extra = '') => ['center', 'right', 'end', 'left', 'start']
  .map((v) => `<div class=c id=${a}-${v} style="text-align:${v}${extra}"><span id=${a}-${v}-s>${T}</span></div>`).join('\n');

const cases = {
  // Trailing white space at a soft wrap does not take part in alignment (CSS Text 3 4.1.3).
  'trailing-soft-wrap': ['', align('w')],
  'trailing-per-word': ['', `<div class=c id=c1 style="text-align:center">${words('a', T)}</div>
<div class=c id=c2 style="text-align:right">${words('b', T)}</div>`],
  // Spaces before a forced break, and the last line of the block.
  'trailing-br': ['', `<div class=c id=c1 style="text-align:center"><span id=a1>alpha beta </span><br><span id=a2>gamma</span> <br> <span id=a3>delta</span> </div>
<div class=c id=c2 style="text-align:right"><span id=b1>alpha beta </span><br><span id=b2>gamma</span> <br> <span id=b3>delta</span> </div>
<div class=c id=c3 style="text-align:right"><span id=d1>alpha</span>   <br>   <span id=d2>beta</span>   </div>`],
  // A line that ends inside an inline element: <span>word </span> then a wrap.
  'trailing-span-boundary': ['', `<div class=c id=c1 style="text-align:right"><span id=a1>Lorem ipsum dolor sit amet </span><span id=a2>consectetur adipiscing elit</span> <span id=a3>sed do</span></div>
<div class=c id=c2 style="text-align:center"><span id=b1>Lorem ipsum dolor sit amet </span><b id=b2>consectetur adipiscing elit </b><span id=b3>sed do eiusmod tempor</span></div>
<div class=c id=c3 style="text-align:right"><span id=e1>Lorem ipsum dolor sit amet consectetur</span><span id=e2> adipiscing elit sed do</span></div>
<div class=c id=c4 style="text-align:right"><span id=g1 style="padding:0 6px;border:2px solid #e8590c">Lorem ipsum dolor sit amet consectetur adipiscing elit</span> <span id=g2>sed</span></div>`],
  // A space at the start of a line after a soft wrap is removed.
  'leading-after-wrap': ['', `<div class=c id=c1 style="text-align:right"><span id=a1>Lorem ipsum dolor sit amet consectetur</span> <span id=a2> adipiscing elit</span></div>
<div class=c id=c2><span id=b1>Lorem ipsum dolor sit amet consectetur</span><span id=b2> adipiscing elit</span></div>
<div class=c id=c3 style="text-align:center"><span id=d1>Lorem ipsum dolor sit amet consectetur</span><span id=d2> adipiscing elit</span></div>`],
  'white-space-nowrap': ['', `<div class=c id=c1 style="text-align:center;white-space:nowrap"><span id=a1>alpha beta gamma </span><span id=a2>delta</span> </div>
<div class=c id=c2 style="text-align:right;white-space:nowrap"><span id=b1>alpha beta </span><br><span id=b2>gamma </span></div>`],
  'white-space-pre-line': ['', `<div class=c id=c1 style="text-align:center;white-space:pre-line"><span id=a1>alpha beta   \ngamma   </span><span id=a2>delta</span></div>
<div class=c id=c2 style="text-align:right;white-space:pre-line"><span id=b1>${T}   </span></div>`],
  'white-space-pre-wrap': ['', `<div class=c id=c1 style="text-align:center;white-space:pre-wrap"><span id=a1>alpha beta   \ngamma</span></div>
<div class=c id=c2 style="text-align:right;white-space:pre-wrap"><span id=b1>alpha beta   \ngamma</span></div>
<div class=c id=c3 style="text-align:right;white-space:pre-wrap"><span id=d1>${T}</span></div>
<div class=c id=c4 style="text-align:center;white-space:pre-wrap"><span id=e1>${T}</span></div>`],
  'white-space-break-spaces': ['', `<div class=c id=c1 style="text-align:right;white-space:break-spaces"><span id=a1>alpha beta   \ngamma</span></div>
<div class=c id=c2 style="text-align:center;white-space:break-spaces"><span id=b1>alpha beta   \ngamma</span></div>`],
  'white-space-pre': ['', `<div class=c id=c1 style="text-align:right;white-space:pre"><span id=a1>alpha beta   \ngamma</span></div>
<div class=c id=c2 style="text-align:center;white-space:pre"><span id=b1>alpha beta   \ngamma</span></div>`],
  // Justified lines expand their inner spaces; the last line and a line before <br> are
  // start-aligned unless text-align-last says otherwise.
  'justify': ['', `<div class=c id=c1 style="text-align:justify">${words('a', T)}</div>
<div class=c id=c2 style="text-align:justify"><span id=b1>${T}</span></div>
<div class=c id=c3 style="text-align:justify"><span id=d1>Lorem ipsum dolor sit amet</span><br><span id=d2>${T}</span></div>`],
  'justify-span-boundary': ['', `<div class=c id=c1 style="text-align:justify"><span id=a1>Lorem ipsum dolor sit amet </span><span id=a2>consectetur adipiscing elit</span> <span id=a3>sed do</span></div>
<div class=c id=c2 style="text-align:justify"><span id=b1 style="padding:0 5px;background:#ffd8a8">Lorem ipsum</span> dolor sit amet consectetur <span id=b2>adipiscing elit sed do eiusmod</span></div>`],
  'text-align-last': ['', ['center', 'right', 'end', 'justify', 'left', 'start']
    .map((v) => `<div class=c id=c-${v} style="text-align:justify;text-align-last:${v}"><span id=s-${v}>${T}</span></div>`).join('\n')
    + `\n<div class=c id=c-last-only style="text-align-last:right"><span id=s-last-only>${T}</span></div>`],
  'text-indent': ['', ['center', 'right', 'justify', 'left'].map((v) =>
    `<div class=c id=c-${v} style="text-align:${v};text-indent:40px"><span id=s-${v}>${T}</span></div>`).join('\n')
    + `\n<div class=c id=c-neg style="text-align:right;text-indent:-30px;padding-left:30px;width:270px"><span id=s-neg>${T}</span></div>`],
  // Right to left: start is the right edge, end the left; left and right are physical.
  'rtl-latin': ['', `<div dir=rtl>${align('r')}</div>`],
  'rtl-css-direction': ['', `<div style="direction:rtl"><div class=c id=c1><span id=a1>${T}</span></div>
<div class=c id=c2 style="text-align:justify"><span id=b1>${T}</span></div></div>`],
  'rtl-hebrew': ['', `<div dir=rtl class=he>
<div class=c id=c1>${words('a', HE)}</div>
<div class=c id=c2 style="text-align:center"><span id=b1>${HE}</span></div>
<div class=c id=c3 style="text-align:left"><span id=d1>${HE}</span></div>
<div class=c id=c4 style="text-align:end"><span id=e1>${HE}</span></div>
<div class=c id=c5 style="text-align:justify"><span id=g1>${HE}</span></div></div>`],
  'rtl-arabic': ['', `<div dir=rtl class=he>
<div class=c id=c1><span id=a1>${AR}</span></div>
<div class=c id=c2 style="text-align:center"><span id=b1>${AR}</span></div>
<div class=c id=c3 style="text-align:left"><span id=d1>${AR}</span></div></div>`],
  'rtl-mixed': ['', `<div class=he>
<div class=c id=c1 dir=rtl><span id=a1>${MIX}</span></div>
<div class=c id=c2 dir=rtl><span id=b1>hello world</span></div>
<div class=c id=c3 dir=ltr><span id=d1>שלום עולם</span> <span id=d2>hello</span></div>
<div class=c id=c4 dir=ltr style="text-align:right"><span id=e1>שלום עולם hello</span></div>
<div class=c id=c5 dir=rtl><span id=g1>abc</span> <span id=g2>def</span> <span id=g3>ghi</span></div></div>`],
  'rtl-text-indent': ['', `<div dir=rtl>
<div class=c id=c1 style="text-indent:40px"><span id=a1>${T}</span></div>
<div class=c id=c2 style="text-indent:40px;text-align:left"><span id=b1>${T}</span></div>
<div class=c id=c3 style="text-indent:40px;text-align:center"><span id=d1>${T}</span></div></div>`],
  'rtl-float': ['', `<div dir=rtl>
<div class=c id=c1><div class="l f" id=f1></div><span id=a1>${T}</span></div>
<div class=c id=c2><div class="r f" id=f2></div><span id=b1>${T}</span></div>
<div class=c id=c3 style="text-align:left"><div class="r f" id=f3></div><span id=d1>${T}</span></div></div>`],
  // Inline boxes with padding and borders on aligned and right-to-left lines.
  'inline-box-edges': ['', `<div class=c id=c1 style="text-align:center"><span id=a1 style="padding:0 6px;border:2px solid #e8590c">alpha beta</span> <span id=a2>gamma</span></div>
<div class=c id=c2 dir=rtl><span id=b1 style="padding:0 6px;border:2px solid #e8590c">alpha beta</span> <span id=b2>gamma</span></div>
<div class=c id=c3 dir=rtl style="text-align:left"><span id=d1 style="padding:0 6px;border:2px solid #e8590c">alpha beta</span> <span id=d2>gamma</span></div>
<div class="c he" id=c4 dir=rtl><span id=e1 style="padding:0 6px;border:2px solid #e8590c">שלום עולם</span> <span id=e2>זהו משפט</span></div>
<div class="c he" id=c5 dir=rtl style="text-align:center"><span id=g1>שלום</span> <span id=g2 style="padding:0 4px;margin:0 3px;border:1px solid #e8590c">עולם זהו</span> <span id=g3>משפט</span></div>
<div class="c he" id=c6 dir=rtl style="text-align:left"><span id=h1>שלום</span> <span id=h2 style="padding:0 4px;border:1px solid #e8590c">עולם זהו</span> <span id=h3>משפט</span></div>`],
  // Atomic inlines: a run holding one is laid out as a row of boxes, whose text items are
  // not line fragments, so only the containers and the atomic boxes carry ids. They sit at
  // the line top so their y does not depend on the row's baseline handling.
  'inline-box-atomic': ['.ib, img { vertical-align: top; }', `<div class=c id=c1 style="text-align:center"><span>alpha</span> <span class=ib id=ib1></span> <span>beta gamma</span></div>
<div class=c id=c2 style="text-align:right"><span class=ib id=ib2></span> <span class=ib id=ib3></span> </div>
<div class=c id=c3 style="text-align:center"><img id=im1 width=30 height=20 style="background:#e8590c"> alpha </div>
<div class="c he" id=c4 dir=rtl>שלום <span class=ib id=ib4></span> עולם זהו</div>
<div class="c he" id=c5 dir=rtl style="text-align:left">שלום עולם <span class=ib id=ib5></span></div>
<div class=c id=c7 dir=rtl><img id=im2 width=30 height=20 style="background:#e8590c"> alpha</div>
<div class=c id=c8 dir=rtl style="text-align:left">alpha <span class=ib id=ib7></span></div>`],
};

for (const [name, [css, body]] of Object.entries(cases)) {
  const html = `<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>text-align ${name}</title>
<style>
${base}
${css}
</style>
</head>
<body>
${body}
</body>
</html>
`;
  fs.writeFileSync(path.join(out, `${name}.html`), html);
}
console.log(Object.keys(cases).length, 'pages');
