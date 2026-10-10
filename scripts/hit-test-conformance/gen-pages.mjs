// Generates the hit-test conformance pages: node gen-pages.mjs [outDir] (default render-repros/hit-test).
// Every element worth hitting carries an id; conformance.mjs --grid samples elementFromPoint on a
// grid over each page and compares the ids.
import fs from 'node:fs';
import path from 'node:path';

const out = process.argv[2] || new URL('../../render-repros/hit-test', import.meta.url).pathname;
fs.mkdirSync(out, { recursive: true });

const base = `html, body { margin: 0; }
body { font: 16px/20px 'Liberation Sans', Arial, sans-serif; }
p { margin: 0; }`;

const svgImg = 'data:image/svg+xml,%3Csvg xmlns=%22http://www.w3.org/2000/svg%22 width=%2240%22 height=%2240%22%3E%3Crect width=%2240%22 height=%2240%22 fill=%22red%22/%3E%3C/svg%3E';

const cases = {
  stacking: ['.box { position: absolute; width: 120px; height: 80px; }', `
<div id=a class=box style="left:20px;top:20px;background:#c92a2a;z-index:2"></div>
<div id=b class=box style="left:80px;top:60px;background:#2b8a3e;z-index:1"></div>
<div id=c class=box style="left:140px;top:40px;background:#1864ab"></div>
<div id=d class=box style="left:200px;top:20px;background:#e67700;z-index:-1"></div>
<div id=n style="position:relative;left:0;top:150px;width:300px;height:60px;background:#ced4da"><span id=ns>normal flow text here</span></div>
<div id=e style="position:absolute;left:30px;top:170px;width:60px;height:60px;background:#862e9c;z-index:3"><div id=e1 style="position:absolute;left:30px;top:30px;width:60px;height:60px;background:#f783ac;z-index:-5"></div></div>
<div id=o style="opacity:.5;width:200px;height:50px;margin-top:180px;background:#5f3dc4"><span id=os>opacity</span></div>
<div id=o2 style="width:200px;height:50px;margin-top:-30px;margin-left:50px;background:#0b7285"><span id=o2s>after opacity</span></div>`],
  'inline-content': ['.c { width: 400px; background: #f1f3f5; }', `
<div class=c id=c><p id=p1>First <span id=s1>span one</span> and <b id=b1>bold <i id=i1>italic</i> text</b> then a fairly long tail that wraps onto a second line here.</p>
<div id=over style="margin-top:-30px;margin-left:40px;height:50px;width:200px;background:#a5d8ff"></div>
<p id=p2 style="padding:10px"><a id=a1 href="#x" style="padding:4px;background:#ffd8a8">link with padding</a> plain <span id=s2 style="border:3px solid #333">bordered</span></p>
<p id=p3 style="text-align:center">centered <em id=em1>emphasis</em></p>
<p id=p4 style="text-align:right;line-height:40px">right <span id=s4 style="font-size:30px">big</span></p></div>`],
  'float-text': ['.c { width: 400px; background: #f1f3f5; } .l { float: left; } .r { float: right; }', `
<div class=c id=c><div class=l id=f1 style="width:100px;height:60px;background:#087f5b"><span id=fs>in float</span></div><p id=p1><span id=s1>Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua</span></p>
<div class=r id=f2 style="width:80px;height:80px;background:#1971c2;margin-top:-20px"></div><p id=p2>Ut enim ad minim <b id=b2>veniam quis</b> nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat</p>
<div id=blk style="height:30px;background:#e9ecef">block after</div></div>`],
  'clip-scroll': ['.box { width: 200px; height: 100px; background: #e9ecef; margin: 10px; }', `
<div id=h class=box style="overflow:hidden"><div id=h1 style="width:400px;height:40px;background:#ffa8a8">wide child</div><div id=h2 style="height:120px;width:50px;background:#74c0fc">tall</div></div>
<div id=s class=box style="overflow:auto;scrollbar-width:none"><div id=s1 style="height:60px;background:#b2f2bb">one</div><div id=s2 style="height:60px;background:#ffec99">two</div><div id=s3 style="height:60px;background:#d0bfff">three</div></div>
<div id=v class=box style="overflow:visible;height:30px"><div id=v1 style="height:80px;width:100px;background:#ffc9c9">overflowing visible</div></div>
<div id=x class=box style="overflow-x:hidden;height:40px;margin-top:60px"><div id=x1 style="width:300px;height:70px;background:#99e9f2">x only</div></div>
<script>document.getElementById('s').scrollTop = 50;</script>`],
  'pointer-visibility': ['.box { width: 200px; height: 60px; margin: 10px; }', `
<div id=pe class=box style="pointer-events:none;background:#ffc9c9"><span id=pes>none text</span><div id=pea style="pointer-events:auto;width:80px;height:30px;background:#c92a2a">auto child</div></div>
<div id=under class=box style="background:#d3f9d8;margin-top:-40px;position:relative;z-index:-1">under</div>
<div id=vh class=box style="visibility:hidden;background:#a5d8ff"><span id=vhs>hidden</span><div id=vhv style="visibility:visible;width:80px;height:30px;background:#1864ab">visible child</div></div>
<div id=behind class=box style="background:#fff3bf">behind</div>
<div id=ab style="position:absolute;left:100px;top:250px;width:100px;height:50px;background:#e599f7;pointer-events:none"></div>
<div id=dn style="display:none;width:100px;height:100px"></div>`],
  transforms: ['.box { width: 120px; height: 60px; position: absolute; }', `
<div id=t1 class=box style="left:20px;top:20px;background:#ffa94d;transform:rotate(30deg)"><span id=t1s>rotated</span></div>
<div id=t2 class=box style="left:200px;top:20px;background:#69db7c;transform:scale(1.5);transform-origin:0 0"><div id=t2c style="width:40px;height:20px;background:#2b8a3e"></div></div>
<div id=t3 class=box style="left:20px;top:150px;background:#74c0fc;transform:translate(50px,10px)"></div>
<div id=t4 style="margin-top:250px;width:300px;height:50px;background:#eebefa;transform:skewX(20deg)"><span id=t4s>skewed text</span></div>`],
  positioned: ['.c { position: relative; width: 400px; height: 300px; background: #f8f9fa; }', `
<div class=c id=c><div id=r1 style="position:relative;top:10px;left:10px;width:150px;height:60px;background:#ffc078">rel</div>
<div id=b1 style="width:200px;height:60px;background:#a9e34b;margin-top:-30px">block after rel</div>
<div id=a1 style="position:absolute;left:100px;top:100px;width:100px;height:100px;background:#4dabf7"><div id=a1c style="position:absolute;left:50px;top:50px;width:100px;height:60px;background:#f06595"></div></div>
<div id=a2 style="position:absolute;left:180px;top:150px;width:100px;height:100px;background:#845ef7"></div>
<p id=p style="margin-top:100px">text that flows under the absolutes <span id=sp>span</span></p>
<div id=fx style="position:fixed;right:10px;bottom:10px;width:80px;height:40px;background:#ff8787">fixed</div></div>
<div style="height:600px" id=tall></div>`],
  table: ['table { border-collapse: separate; border-spacing: 4px; background: #e9ecef; } td { padding: 6px; background: #fff; }', `
<table id=t><tr id=r1><td id=c11>one</td><td id=c12>two <span id=sp>span</span></td></tr><tr id=r2><td id=c21 colspan=2 style="height:40px">wide cell</td></tr></table>
<ul id=ul><li id=li1>item one</li><li id=li2>item <a id=a2 href="#">two</a></li></ul>`],
  shadow: ['', `
<div id=host style="width:300px;height:100px;background:#e9ecef"></div>
<div id=host2 style="width:300px;height:60px;background:#dee2e6"><span id=light>light child</span></div>
<script>
const r = document.getElementById('host').attachShadow({ mode: 'open' });
r.innerHTML = '<div id=in1 style="width:150px;height:40px;background:#ffc9c9">inner one</div><div id=in2 style="width:200px;height:40px;background:#a5d8ff"><span id=in2s>inner span</span></div>';
const r2 = document.getElementById('host2').attachShadow({ mode: 'open' });
r2.innerHTML = '<div id=wrap style="padding:10px;background:#b2f2bb"><slot id=slot></slot></div>';
</script>`],
  'inline-blocks': ['.c { width: 400px; background: #f1f3f5; } .ib { display: inline-block; background: #ced4da; }', `
<div class=c id=c><p id=p1>text <span id=ib1 class=ib style="width:60px;height:30px"></span> more <span id=ib2 class=ib style="padding:4px">inside <b id=ibb>bold</b></span> end</p>
<p id=p2>image <img id=im src="${svgImg}" width=40 height=40> after <button id=btn><span id=btns>btn</span></button> <input id=inp value="input"></p></div>`],
};

for (const [name, [css, body]] of Object.entries(cases)) {
  const html = `<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>hit-test ${name}</title>
<style>
${base}
${css}
</style>
</head>
<body id=body>${body}
</body>
</html>
`;
  fs.writeFileSync(path.join(out, `${name}.html`), html);
}
console.log(`wrote ${Object.keys(cases).length} pages to ${out}`);
