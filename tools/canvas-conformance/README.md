# canvas-conformance

`probe.js` runs 43 canvas 2D operations on small canvases and reports, for each,
the count of non-transparent pixels plus the mean colour of those pixels. Counts
catch a wrong shape; the mean colour catches a wrong colour or a wrong alpha,
which a count alone misses entirely — the straight-vs-premultiplied alpha bug
showed up only in the mean.

Run it through all three engines and compare:

```bash
# Obscura, either engine
target/release/obscura fetch 'data:text/html,<p>x</p>' --eval "$(cat tools/canvas-conformance/probe.js)" --quiet

# Chromium
node -e '
const {chromium}=require("playwright"); const fs=require("fs");
const probe=fs.readFileSync("tools/canvas-conformance/probe.js","utf8");
(async()=>{const b=await chromium.launch({args:["--no-sandbox"]});
const p=await b.newPage(); await p.setContent("<p>x</p>");
console.log(await p.evaluate("("+probe+")")); await b.close();})();'
```

Compare with a tolerance: pixel counts within ~12% and mean channels within ~24
counts, because the engines antialias edges differently. An exact match is the
wrong bar and will send you chasing edge shading.

Last run: 43/43 agree with Chromium, and the Rust reference and the C# port are
byte-identical on all 43.
