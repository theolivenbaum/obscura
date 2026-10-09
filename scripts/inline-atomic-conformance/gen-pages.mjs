// Generates the atomic-inline conformance pages: node gen-pages.mjs [outDir]
// (default render-repros/inline-atomic). Inline-blocks, images and form controls inside lines
// of text; every element worth measuring carries an id. Compare with
// scripts/float-conformance/conformance.mjs render-repros/inline-atomic '' '' --tol 0.5.
import fs from 'node:fs';
import path from 'node:path';

const out = process.argv[2] || new URL('../../render-repros/inline-atomic', import.meta.url).pathname;
fs.mkdirSync(out, { recursive: true });

const T = 'Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor';
const base = `html, body { margin: 0; }
body { font: 16px/20px 'Liberation Sans', Arial, sans-serif; }
.c { width: 400px; background: #f1f3f5; }
p { margin: 0; }
.ib { display: inline-block; background: #a5d8ff; }
.l { float: left; } .r { float: right; }
.a { background: #087f5b; } .b { background: #1971c2; }`;

const img = (w, h, extra = '') => `<img ${extra} src="data:image/svg+xml,%3Csvg xmlns=%22http://www.w3.org/2000/svg%22 width=%22${w}%22 height=%22${h}%22%3E%3Crect width=%22${w}%22 height=%22${h}%22 fill=%22%23e8590c%22/%3E%3C/svg%3E" width=${w} height=${h}>`;

const cases = {
  'ib-basic': ['', `<div class=c id=c><p id=p><span id=s1>alpha</span> <span class=ib id=ib style="width:60px;height:30px"></span> <span id=s2>beta gamma</span></p></div>`],
  'ib-text-baseline': ['', `<div class=c id=c><p id=p><span id=s1>before</span> <span class=ib id=ib>inside</span> <span id=s2>after</span> <span class=ib id=ib2 style="font-size:30px">big</span> <span id=s3>end</span></p></div>`],
  'ib-multiline-baseline': ['', `<div class=c id=c><p id=p><span id=s1>before</span> <span class=ib id=ib style="width:60px">one two three four</span> <span id=s2>after</span></p></div>`],
  'ib-overflow-baseline': ['', `<div class=c id=c><p id=p><span id=s1>before</span> <span class=ib id=ib style="overflow:hidden">clip</span> <span id=s2>mid</span> <span class=ib id=ib2 style="width:30px;height:30px"></span> <span id=s3>after</span></p></div>`],
  'img-baseline': ['', `<div class=c id=c><p id=p><span id=s1>text</span> ${img(40, 40, 'id=im1')} <span id=s2>more</span> ${img(20, 10, 'id=im2')} <span id=s3>end</span></p></div>`],
  'img-vertical-align': ['img { margin: 0 2px; }', `<div class=c id=c><p id=p><span id=s1>x</span>${img(30, 30, 'id=im1 style="vertical-align:middle"')}<span id=s2>y</span>${img(30, 30, 'id=im2 style="vertical-align:top"')}<span id=s3>z</span>${img(30, 30, 'id=im3 style="vertical-align:bottom"')}<span id=s4>w</span>${img(30, 30, 'id=im4 style="vertical-align:text-top"')}<span id=s5>v</span></p></div>`],
  'ib-vertical-align': ['', `<div class=c id=c><p id=p><span id=s1>x</span><span class=ib id=ib1 style="height:40px;width:20px;vertical-align:middle"></span><span id=s2>y</span><span class=ib id=ib2 style="height:40px;width:20px;vertical-align:top"></span><span id=s3>z</span><span class=ib id=ib3 style="height:10px;width:20px;vertical-align:super"></span><span id=s4>w</span><span class=ib id=ib4 style="height:10px;width:20px;vertical-align:-5px"></span></p></div>`],
  'ib-wrap': ['.ib { width: 90px; height: 24px; margin: 2px; }', `<div class=c id=c><p id=p><span id=s1>start</span> <span class=ib id=ib1></span> <span class=ib id=ib2></span> <span class=ib id=ib3></span> <span class=ib id=ib4></span> <span class=ib id=ib5></span> <span id=s2>finish line of text that wraps</span></p></div>`],
  'ib-nowrap-glue': ['', `<div class=c id=c style="width:200px"><p id=p><span id=s1>wordwordword</span>&nbsp;<span class=ib id=ib1 style="width:60px;height:20px"></span><span id=s2>(</span><span class=ib id=ib2 style="width:40px;height:20px"></span><span id=s3>).</span> <span id=s4>next</span></p></div>`],
  'ib-adjacent-break': ['.ib { width: 70px; height: 20px; }', `<div class=c id=c style="width:200px"><p id=p><span class=ib id=ib1></span><span class=ib id=ib2></span><span class=ib id=ib3></span><span id=s1>tail</span><span class=ib id=ib4></span></p></div>`],
  'ib-center': ['', `<div class=c id=c><p id=p style="text-align:center"><span id=s1>alpha</span> <span class=ib id=ib style="width:60px;height:20px"></span></p><p id=p2 style="text-align:center"><span class=ib id=ib2 style="width:60px;height:20px"></span> <span id=s2>beta</span></p></div>`],
  'ib-right': ['', `<div class=c id=c><p id=p style="text-align:right"><span id=s1>alpha</span> <span class=ib id=ib style="width:60px;height:20px"></span> <span id=s2>omega</span></p></div>`],
  'ib-justify': ['.ib { width: 50px; height: 18px; }', `<div class=c id=c><p id=p style="text-align:justify"><span id=s1>${T}</span> <span class=ib id=ib1></span> <span id=s2>more words here</span> <span class=ib id=ib2></span> <span id=s3>and the last line</span></p></div>`],
  'ib-trailing-space': ['.ib { width: 150px; height: 18px; }', `<div class=c id=c style="width:300px"><p id=p style="text-align:right"><span id=s1>one two</span> <span class=ib id=ib1></span> <span class=ib id=ib2></span> <span id=s2>three</span></p></div>`],
  'ib-pre': ['.ib { width: 30px; height: 18px; }', `<div class=c id=c><p id=p style="white-space:pre"><span id=s1>a  b</span>  <span class=ib id=ib1></span>  <span id=s2>c</span></p></div>`],
  'ib-rtl': ['.ib { width: 30px; height: 18px; }', `<div class=c id=c><p id=p dir=rtl><span id=s1>alpha</span> <span class=ib id=ib1></span> <span id=s2>beta</span> <span class=ib id=ib2></span> <span id=s3>gamma</span></p></div>`],
  'ib-rtl-center': ['.ib { width: 30px; height: 18px; }', `<div class=c id=c><p id=p dir=rtl style="text-align:center"><span id=s1>alpha</span> <span class=ib id=ib1></span> <span id=s2>beta</span></p></div>`],
  'ib-tall-line': ['', `<div class=c id=c><p id=p><span id=s1>low</span> <span class=ib id=ib style="width:30px;height:50px"></span> <span id=s2>text that wraps onto the second line of this paragraph here</span></p></div>`],
  'ib-margins': ['', `<div class=c id=c><p id=p><span id=s1>a</span> <span class=ib id=ib1 style="margin:5px 10px;padding:3px;border:2px solid #333">box</span> <span id=s2>b</span><span class=ib id=ib2 style="margin-left:-5px;width:20px;height:10px"></span><span id=s3>c</span></p></div>`],
  'ib-nested': ['', `<div class=c id=c><p id=p><span id=s1>out</span> <span class=ib id=ib1 style="padding:2px">in <span class=ib id=ib2 style="width:20px;height:20px;background:#e599f7"></span> deep</span> <span id=s2>out</span></p></div>`],
  'span-around-ib': ['', `<div class=c id=c><p id=p><span id=s1>a</span> <span id=sp style="border:1px solid #333;padding:0 3px">in <span class=ib id=ib style="width:40px;height:25px"></span> span</span> <span id=s2>b</span></p></div>`],
  'ib-br': ['.ib { width: 40px; height: 18px; }', `<div class=c id=c><p id=p><span id=s1>one</span> <span class=ib id=ib1></span><br id=br><span class=ib id=ib2></span> <span id=s2>two</span></p></div>`],
  'inline-flex-grid': ['', `<div class=c id=c><p id=p><span id=s1>flex</span> <span id=f style="display:inline-flex;gap:4px;background:#ffd8a8"><span id=f1>a</span><span id=f2>bb</span></span> <span id=s2>grid</span> <span id=g style="display:inline-grid;grid-template-columns:20px 20px;background:#d0bfff"><span id=g1>1</span><span id=g2>2</span><span id=g3>3</span></span> <span id=s3>end</span></p></div>`],
  'form-controls': ['', `<div class=c id=c><p id=p><span id=s1>name</span> <input id=in value="text" size=8> <button id=bt>Go</button> <select id=se><option>one</option></select> <span id=s2>end</span></p></div>`],
  'ib-float-narrow': ['.ib { width: 80px; height: 20px; }', `<div class=c id=c><div class="l a" id=f1 style="width:100px;height:50px"></div><p id=p><span class=ib id=ib1></span> <span class=ib id=ib2></span> <span class=ib id=ib3></span> <span class=ib id=ib4></span> <span class=ib id=ib5></span> <span class=ib id=ib6></span></p></div>`],
  'ib-float-nav': ['.ib { padding: 0 6px; }', `<div class=c id=c><div class="r b" id=f1 style="width:120px;height:30px"></div><span class=ib id=ib1>Home</span> <span class=ib id=ib2>About us</span> <span class=ib id=ib3>Products</span> <span class=ib id=ib4>Contact</span> <span class=ib id=ib5>Blog</span></div>`],
  'ib-percent-beside-float': ['', `<div class=c id=c><div class="l a" id=f1 style="width:150px;height:40px"></div><span class=ib id=ib1 style="width:30%;height:20px"></span> <span id=s1>text</span></div>`],
  'ib-shrink-to-fit': ['', `<div class=c id=c style="display:flow-root"><div class="l a" id=f1><span id=s1>float</span> <span class=ib id=ib1 style="width:50px;height:15px"></span> <span id=s2>text</span></div></div><table id=t><tr><td id=td><span id=s3>cell</span> <span class=ib id=ib2 style="width:70px;height:15px"></span></td></tr></table>`],
  'svg-inline': ['', `<div class=c id=c><p id=p><span id=s1>icon</span> <svg id=sv width=24 height=24 style="vertical-align:middle"><rect width=24 height=24 fill="#2b8a3e"/></svg> <span id=s2>label</span></p></div>`],
};

for (const [name, [css, body]] of Object.entries(cases)) {
  const html = `<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>inline-atomic ${name}</title>
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
console.log(`wrote ${Object.keys(cases).length} pages to ${out}`);
