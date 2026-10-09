// Generates the float conformance pages: node gen-pages.mjs [outDir] (default render-repros/floats).
// Long text runs are wrapped in <span id=sN> so their line fragments can be compared.
import fs from 'node:fs';
import path from 'node:path';

const out = process.argv[2] || new URL('../../render-repros/floats', import.meta.url).pathname;
fs.mkdirSync(out, { recursive: true });

const T = 'Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua';
const T2 = T + ' ut enim ad minim veniam quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat';

const base = `html, body { margin: 0; }
body { font: 16px/20px 'Liberation Sans', Arial, sans-serif; }
.c { width: 400px; background: #f1f3f5; }
.l { float: left; } .r { float: right; }
.a { background: #087f5b; } .b { background: #1971c2; } .d { background: #e8590c; } .e { background: #862e9c; }
p { margin: 0; }`;

const cases = {
  'left-basic': ['', `<div class=c id=c><div class="l a" id=f style="width:100px;height:50px"></div><p id=p>${T}</p></div>`],
  'left-basic-plain': ['', `<div class=c id=c><div class="l a" id=f style="width:100px;height:50px"></div><p id=p>${T}</p></div>`],
  'control-no-float': ['', `<div class=c id=c><p id=p>${T2}</p><p id=p2 style="text-align:center">${T}</p></div>`],
  'right-basic': ['', `<div class=c id=c><div class="r a" id=f style="width:120px;height:70px"></div><p id=p>${T}</p></div>`],
  'both-sides-text': ['', `<div class=c id=c><div class="l a" id=f1 style="width:80px;height:50px"></div><div class="r b" id=f2 style="width:90px;height:90px"></div><p id=p>${T2}</p></div>`],
  'left-row-wraps': ['', `<div class=c id=c><div class="l a" id=f1 style="width:150px;height:40px"></div><div class="l b" id=f2 style="width:150px;height:40px"></div><div class="l d" id=f3 style="width:150px;height:40px"></div><p id=p>after</p></div>`],
  'staircase': ['', `<div class=c id=c style="width:300px"><div class="l a" id=f1 style="width:100px;height:100px"></div><div class="l b" id=f2 style="width:100px;height:50px"></div><div class="l d" id=f3 style="width:150px;height:30px"></div></div>`],
  'right-staircase': ['', `<div class=c id=c style="width:300px"><div class="r a" id=f1 style="width:100px;height:100px"></div><div class="r b" id=f2 style="width:100px;height:50px"></div><div class="r d" id=f3 style="width:150px;height:30px"></div><div class="l e" id=f4 style="width:40px;height:20px"></div></div>`],
  'left-right-left': ['', `<div class=c id=c><div class="l a" id=f1 style="width:100px;height:60px"></div><div class="r b" id=f2 style="width:100px;height:60px"></div><div class="l d" id=f3 style="width:100px;height:60px"></div><div class="l e" id=f4 style="width:150px;height:20px"></div></div>`],
  'top-not-above-earlier': ['', `<div class=c id=c><div class="l a" id=f1 style="width:300px;height:30px"></div><div class="r b" id=f2 style="width:200px;height:20px"></div><div class="l d" id=f3 style="width:50px;height:20px"></div></div>`],
  'word-moves-below': ['', `<div class=c id=c style="width:200px"><div class="l a" id=f style="width:150px;height:40px"></div><p id=p>Supercalifragilistic word then more words</p></div>`],
  'clear-left': ['', `<div class=c id=c><div class="l a" id=f1 style="width:100px;height:60px"></div><div class="r b" id=f2 style="width:100px;height:90px"></div><div id=k style="clear:left;height:20px;background:#ccc">cleared</div><p id=p>${T}</p></div>`],
  'clear-right': ['', `<div class=c id=c><div class="l a" id=f1 style="width:100px;height:60px"></div><div class="r b" id=f2 style="width:100px;height:90px"></div><div id=k style="clear:right;height:20px;background:#ccc">cleared</div></div>`],
  'clear-both-margin': ['', `<div class=c id=c><div id=x style="height:10px;margin-bottom:10px;background:#ddd"></div><div class="l a" id=f1 style="width:100px;height:60px"></div><div id=k style="clear:both;margin-top:30px;height:20px;background:#ccc">cleared</div></div>`],
  'clear-no-clearance': ['', `<div class=c id=c><div class="l a" id=f1 style="width:100px;height:10px"></div><div id=x style="height:30px;background:#ddd"></div><div id=k style="clear:both;margin-top:15px;height:20px;background:#ccc">cleared</div></div>`],
  'clearfix-after': ['.cf::after { content: ""; display: table; clear: both; }', `<div class="c cf" id=c><div class="l a" id=f1 style="width:100px;height:60px"></div><div class="r b" id=f2 style="width:100px;height:80px"></div></div><p id=p>next</p>`],
  'overflow-hidden-contains': ['', `<div class=c id=c style="overflow:hidden"><div class="l a" id=f1 style="width:100px;height:60px"></div><span id=s>short</span></div><p id=p>next</p>`],
  'flow-root-contains': ['', `<div class=c id=c style="display:flow-root;padding:5px"><div class="r a" id=f1 style="width:100px;height:60px;margin:10px"></div></div><p id=p>next</p>`],
  'non-bfc-escapes': ['', `<div class=c id=c><div id=inner><div class="l a" id=f1 style="width:100px;height:60px"></div></div><p id=p>${T}</p></div>`],
  'bfc-narrowed': ['', `<div class=c id=c><div class="l a" id=f1 style="width:100px;height:60px"></div><div id=o style="overflow:hidden;background:#ccc">${T}</div></div>`],
  'bfc-fixed-moves-down': ['', `<div class=c id=c><div class="l a" id=f1 style="width:150px;height:60px"></div><div id=o style="overflow:hidden;width:300px;height:20px;background:#ccc"></div><div id=o2 style="overflow:hidden;width:200px;height:20px;background:#999"></div></div>`],
  'bfc-staircase-down': ['', `<div class=c id=c><div class="l a" id=f1 style="width:300px;height:30px"></div><div class="l b" id=f2 style="width:200px;height:60px;clear:left"></div><div id=o style="display:flow-root;width:250px;height:20px;background:#ccc"></div></div>`],
  'inline-block-beside': ['', `<div class=c id=c><div class="l a" id=f1 style="width:100px;height:60px"></div><span id=ib style="display:inline-block;width:200px;height:30px;background:#ccc"></span><span id=ib2 style="display:inline-block;width:200px;height:30px;background:#999"></span></div>`],
  'shrink-to-fit': ['', `<div class=c id=c><div class="l a" id=f1>short text</div><div class="r b" id=f2>${T}</div></div>`],
  'percent-width': ['', `<div class=c id=c><div class="l a" id=f1 style="width:25%;height:30px"></div><div class="l b" id=f2 style="width:50%;height:30px;padding:0 5px;box-sizing:border-box"></div><div class="r d" id=f3 style="width:20%;height:40px"></div></div>`],
  'float-margins': ['', `<div class=c id=c><div class="l a" id=f1 style="width:100px;height:40px;margin:10px 20px 5px 15px"></div><div class="l b" id=f2 style="width:100px;height:40px;margin:20px"></div><p id=p>${T}</p></div>`],
  'negative-margin': ['', `<div class=c id=c><div class="l a" id=f1 style="width:200px;height:40px;margin-right:-100px"></div><div class="l b" id=f2 style="width:100px;height:60px"></div><div class="r d" id=f3 style="width:100px;height:30px;margin-top:-10px"></div></div>`],
  'nested-share': ['', `<div class=c id=c><div id=o1><div id=o2><div class="l a" id=f1 style="width:100px;height:80px"></div></div><p id=p1>One two three</p></div><p id=p2>${T}</p></div>`],
  'paragraphs-around': ['', `<div class=c id=c><div class="r a" id=f1 style="width:150px;height:110px"></div><p id=p1 style="margin-bottom:10px">${T}</p><p id=p2>${T}</p></div>`],
  'inline-float-fits': ['', `<div class=c id=c><p id=p>Some words <span class="r a" id=f1 style="width:60px;height:30px"></span>and then more words follow here to fill the remaining lines of text</p></div>`],
  'inline-float-end': ['', `<div class=c id=c><p id=p>Short text line<span class="r a" id=f1 style="width:60px;height:30px"></span></p><p id=p2>next paragraph text</p></div>`],
  'inline-float-no-fit': ['', `<div class=c id=c style="width:300px"><p id=p>Many words here that fill the line <span class="l a" id=f1 style="width:250px;height:30px"></span>and continue onto further lines here</p></div>`],
  'empty-block-collapse': ['', `<div class=c id=c><div id=x style="height:20px;background:#ddd;margin-bottom:20px"></div><div id=e style="margin-top:10px;margin-bottom:30px"><div class="l a" id=f1 style="width:100px;height:30px"></div></div><div id=y style="height:20px;background:#ccc;margin-top:10px">after</div></div>`],
  'margin-collapse-float-first': ['', `<div class=c id=c style="padding-top:1px"><div id=o style="margin-top:20px"><div class="l a" id=f1 style="width:100px;height:30px"></div><p id=p style="margin-top:30px">text beside</p></div></div>`],
  'flex-item-float-ignored': ['', `<div class=c id=c style="display:flex"><div class="r a" id=f1 style="width:100px;height:30px"></div><div class=b id=f2 style="width:100px;height:40px"></div></div>`],
  'grid-item-float-ignored': ['', `<div class=c id=c style="display:grid;grid-template-columns:100px 100px"><div class="r a" id=f1 style="height:30px"></div><div class="l b" id=f2 style="height:40px"></div></div>`],
  'blockify-span': ['', `<div class=c id=c><span class="l a" id=f1 style="width:80px;height:30px;margin:5px"></span><a class="r b" id=f2 style="padding:5px">link</a>text after</div>`],
  'rtl': ['', `<div class=c id=c dir=rtl><div class="l a" id=f1 style="width:100px;height:50px"></div><div class="r b" id=f2 style="width:80px;height:30px"></div><p id=p>${T}</p></div>`],
  'text-align-center': ['', `<div class=c id=c style="text-align:center"><div class="l a" id=f1 style="width:100px;height:50px"></div><p id=p>${T}</p></div>`],
  'text-align-right': ['', `<div class=c id=c style="text-align:right"><div class="r a" id=f1 style="width:100px;height:50px"></div><p id=p>${T}</p></div>`],
  'list-beside-float': ['', `<div class=c id=c><div class="l a" id=f1 style="width:120px;height:100px"></div><ul id=u style="margin:0"><li id=li1>First item</li><li id=li2>Second item text</li><li id=li3>Third</li></ul></div>`],
  'abs-contains': ['', `<div class=c id=c style="position:relative;height:200px"><div id=abs style="position:absolute;top:10px;left:10px;background:#ccc"><div class="l a" id=f1 style="width:100px;height:60px"></div><div class="l b" id=f2 style="width:50px;height:30px"></div></div></div>`],
  'inline-block-contains': ['', `<div class=c id=c><span id=ib style="display:inline-block;background:#ccc"><span class="l a" id=f1 style="width:100px;height:60px"></span>txt</span> after</div>`],
  'table-cell-contains': ['', `<table id=t style="border-spacing:0;width:400px"><tr><td id=td style="padding:0;vertical-align:top"><div class="l a" id=f1 style="width:100px;height:60px"></div>cell text</td><td id=td2 style="padding:0;width:100px;vertical-align:top">x</td></tr></table>`],
  'float-clear-float': ['', `<div class=c id=c><div class="l a" id=f1 style="width:100px;height:60px"></div><div class="l b" id=f2 style="width:100px;height:30px"></div><div class="l d" id=f3 style="width:100px;height:20px;clear:left"></div><div class="r e" id=f4 style="width:50px;height:20px;clear:both"></div></div>`],
  'logical-float': ['', `<div class=c id=c><div class=a id=f1 style="float:inline-start;width:100px;height:40px"></div><div class=b id=f2 style="float:inline-end;width:100px;height:40px"></div><div id=k style="clear:inline-start;height:10px;background:#ccc"></div></div>`],
  'tall-inline-line': ['', `<div class=c id=c style="width:300px"><div class="l a" id=f1 style="width:100px;height:30px"></div><p id=p>small text <span id=big style="font-size:40px;line-height:48px">BIG</span> more small text continues here and on</p></div>`],
  'br-lines': ['', `<div class=c id=c><div class="r a" id=f1 style="width:100px;height:50px"></div><p id=p>one<br>two<br>three<br>four</p></div>`],
  'wikipedia-thumb': ['', `<div class=c id=c style="width:500px"><figure id=fig style="float:right;margin:0 0 10px 10px;width:220px;border:1px solid #ccc;padding:3px"><div id=img style="width:220px;height:120px;background:#999"></div><figcaption id=cap style="font-size:14px">A caption that wraps onto two lines</figcaption></figure><p id=p1>${T2}</p><h2 id=h2 style="margin:10px 0;overflow:hidden;border-bottom:1px solid #aaa">Heading</h2><p id=p2>${T}</p></div>`],
  'nav-bar': ['ul.nav { list-style:none; margin:0; padding:0; background:#333; overflow:hidden; } ul.nav li { float:left; } ul.nav a { display:block; padding:10px 15px; color:#fff; }', `<div class=c id=c><ul class=nav id=nav><li id=li1><a id=a1>Home</a></li><li id=li2><a id=a2>About us</a></li><li id=li3 style="float:right"><a id=a3>Contact</a></li></ul><p id=p>${T}</p></div>`],
  'overflow-past-container': ['', `<div class=c id=c><div id=box><div class="l a" id=f1 style="width:100px;height:100px"></div><p id=p1>short</p></div><p id=p2>${T}</p></div>`],
  'columns-layout': ['.col { float:left; width:33.3333%; box-sizing:border-box; padding:5px; }', `<div class=c id=c style="width:600px"><div class=col id=c1><p id=p1>${T}</p></div><div class=col id=c2><p id=p2>Short</p></div><div class=col id=c3><p id=p3>${T}</p></div><div id=foot style="clear:both;background:#ccc">footer</div></div>`],
  'wiki-footer': ['.other { float:left; width:33%; } .other a { display:block; }', `<div class=c id=c style="width:600px"><div id=footer><div class=other id=o1><a id=a1>Commons</a><a>Freely usable photos</a></div><div class=other id=o2><a id=a2>Wikivoyage</a><a>Free travel guide</a></div><div class=other id=o3><a id=a3>Wiktionary</a><a>Free dictionary</a></div><div class=other id=o4><a id=a4>Wikibooks</a><a>Free textbooks</a></div></div><p id=p style="clear:both">License text</p></div>`],
  'hit-test': ['', `<div class=c id=c><div class="l a" id=f1 style="width:100px;height:60px"></div><p id=p>${T}</p></div>`],
  'float-after-margin': ['', `<div class=c id=c><div id=x style="height:20px;margin-bottom:25px;background:#ddd"></div><div class="l a" id=f1 style="width:100px;height:30px"></div><div id=y style="margin-top:10px;height:20px;background:#ccc">text</div></div>`],
  'float-in-inline': ['', `<div class=c id=c><p id=p>Before <em id=em>emph <span class="r a" id=f1 style="width:50px;height:50px"></span>after</em> tail text that goes on</p></div>`],
  'multiple-ifc-floats': ['', `<div class=c id=c style="width:300px"><p id=p><span class="l a" id=f1 style="width:60px;height:30px"></span>${T}<span class="l b" id=f2 style="width:60px;height:30px"></span> and more words after the second float here</p></div>`],
  'float-padding-border': ['', `<div class=c id=c style="padding:10px;border:5px solid #333"><div class="l a" id=f1 style="width:100px;height:40px;padding:5px;border:3px solid #000"></div><p id=p style="padding-left:10px">${T}</p></div>`],
  'float-then-hr-block': ['', `<div class=c id=c><div class="l a" id=f1 style="width:100px;height:50px"></div><div id=bg style="background:#ccc;height:30px;border:1px solid red">block bg under float</div></div>`],
  'min-height-float': ['', `<div class=c id=c><div class="l a" id=f1 style="width:100px;min-height:40px">x</div><div class="l b" id=f2 style="max-width:80px">long text inside narrow float</div></div>`],
  'float-image': ['', `<div class=c id=c><img id=im class=l src="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='90' height='60'%3E%3Crect width='90' height='60' fill='green'/%3E%3C/svg%3E" style="margin-right:8px"><p id=p>${T}</p></div>`],
};

const plain = new Set(['left-basic-plain']);
for (let [name, [css, body]] of Object.entries(cases)) {
  if (!plain.has(name)) {
    let n = 0;
    body = body.replace(/>([^<>]{30,})</g, (m, text) => `><span id=s${++n}>${text}</span><`);
  }
  const html = `<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>float ${name}</title>
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
