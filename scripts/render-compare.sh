#!/usr/bin/env bash
# Render one self-contained HTML fixture in Chromium (via Playwright), the Rust
# reference and the C# port, capture the FULL scroll height in all three, and
# report DOM probes plus pixel differences.
#
# Two things this script exists to get right, both of which produced misleading
# numbers when done by hand:
#
#   1. The fixture is served over loopback and must be self-contained. An engine
#      with egress fetches real subresources that a blocked engine cannot, and
#      the diff then measures network access instead of rendering.
#   2. Full page, not viewport. Playwright takes fullPage; the Obscura CLI has no
#      such flag, so the document height is measured first and fed back as
#      OBSCURA_SHOT_H. See the skill for the caveat that carries.
#
# Usage: scripts/render-compare.sh <fixture.html> [width...]
#        scripts/render-compare.sh test-html-files/renderlab-complex.html 1280 640
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FIXTURE="${1:-}"
if [[ -z "$FIXTURE" || ! -f "$FIXTURE" ]]; then
  echo "usage: $0 <fixture.html> [width...]" >&2
  exit 2
fi
shift || true
WIDTHS=("$@")
[[ ${#WIDTHS[@]} -eq 0 ]] && WIDTHS=(1280 900 640)

RUST="${OBSCURA_RUST_BIN:-$REPO/target/release/obscura}"
CS="${OBSCURA_PORT_BIN:-$REPO/dotnet/src/Obscura.Cli/bin/Release/net10.0/obscura}"
OUT="${RENDER_COMPARE_OUT:-$REPO/target/render-compare}"
PORT="${RENDER_COMPARE_PORT:-8099}"
SETTLE="${RENDER_COMPARE_SETTLE:-3}"

for bin in "$RUST" "$CS"; do
  [[ -x "$bin" ]] || { echo "not executable: $bin" >&2
    echo "  reference: cargo build --release -p obscura-cli --bins --features render" >&2
    echo "  port:      cd dotnet && dotnet build -c Release" >&2; exit 1; }
done
command -v node >/dev/null || { echo "node is required for the Chromium lane" >&2; exit 1; }

# A `cargo test --release -p obscura-cli` run (no --features render) rewrites
# target/release/obscura with a default-features binary, whose --screenshot only
# prints an error. Left unchecked that shows up here as a blank reference lane
# and a 100% pixel difference that has nothing to do with rendering.
if "$RUST" fetch "data:text/html,<b>x</b>" --screenshot /dev/null --quiet --timeout 20 2>&1 \
    | grep -q "requires a build with the render feature"; then
  echo "the reference was built without the render feature: $RUST" >&2
  echo "rebuild it with: cargo build --release -p obscura-cli --bins --features render" >&2
  echo "(a plain 'cargo test --release -p obscura-cli' overwrites it)" >&2
  exit 1
fi

mkdir -p "$OUT"
FIXDIR="$(cd "$(dirname "$FIXTURE")" && pwd)"
FIXNAME="$(basename "$FIXTURE")"
STEM="${FIXNAME%.*}"

# Serve the fixture's directory. Loopback keeps every engine on equal footing,
# and the Obscura SSRF gate needs the explicit opt-in to reach it.
node -e '
const http=require("http"),fs=require("fs"),path=require("path");
const root=process.argv[1],port=+process.argv[2];
const types={".html":"text/html",".css":"text/css",".js":"text/javascript",".svg":"image/svg+xml",
             ".png":"image/png",".jpg":"image/jpeg",".woff2":"font/woff2",".json":"application/json"};
http.createServer((req,res)=>{
  const p=path.join(root,decodeURIComponent(req.url.split("?")[0]));
  fs.readFile(p,(e,d)=>{
    if(e){res.writeHead(404);res.end("not found");return;}
    res.writeHead(200,{"content-type":types[path.extname(p).toLowerCase()]||"application/octet-stream"});
    res.end(d);
  });
}).listen(port,"127.0.0.1");
' "$FIXDIR" "$PORT" &
SERVER=$!
trap 'kill $SERVER 2>/dev/null' EXIT
sleep 1

URL="http://127.0.0.1:$PORT/$FIXNAME"
PROBE='JSON.stringify({nodes:document.querySelectorAll("*").length,text:(document.body&&document.body.innerText||"").length,links:document.querySelectorAll("a[href]").length,imgs:document.querySelectorAll("img").length,canvases:document.querySelectorAll("canvas").length,svgs:document.querySelectorAll("svg").length,iw:innerWidth,w:document.documentElement.scrollWidth,h:document.documentElement.scrollHeight})'

obscura_run() {  # engine bin width -> sets HEIGHT, writes png
  local eng="$1" bin="$2" w="$3"
  # Measure the document at this width first, then paint the whole of it.
  local probe height
  probe="$(env -u HTTPS_PROXY -u https_proxy -u HTTP_PROXY -u http_proxy -u NO_PROXY -u no_proxy \
    OBSCURA_ALLOW_PRIVATE_NETWORK=1 OBSCURA_SHOT_W="$w" OBSCURA_SHOT_H=900 \
    "$bin" fetch "$URL" --screenshot /dev/null --eval "$PROBE" --quiet --wait "$SETTLE" --timeout 90 2>/dev/null | head -1)"
  height="$(printf '%s' "$probe" | grep -oE '\\"h\\":[0-9]+' | head -1 | cut -d: -f2)"
  [[ -z "$height" ]] && height="$(printf '%s' "$probe" | grep -oE '"h":[0-9]+' | head -1 | cut -d: -f2)"
  [[ -z "$height" ]] && height=900
  env -u HTTPS_PROXY -u https_proxy -u HTTP_PROXY -u http_proxy -u NO_PROXY -u no_proxy \
    OBSCURA_ALLOW_PRIVATE_NETWORK=1 OBSCURA_SHOT_W="$w" OBSCURA_SHOT_H="$height" \
    "$bin" fetch "$URL" --screenshot "$OUT/$STEM.w$w.$eng.png" --quiet --wait "$SETTLE" --timeout 90 >/dev/null 2>&1
  printf '%s\t%s\t%s\t%s\n' "$w" "$eng" "$height" "$probe" >> "$OUT/$STEM.probes.tsv"
  echo "  w$w $eng: full height $height px"
}

: > "$OUT/$STEM.probes.tsv"
for w in "${WIDTHS[@]}"; do
  obscura_run rust "$RUST" "$w"
  obscura_run csharp "$CS" "$w"
done

NODE_PATH="$(npm root -g 2>/dev/null)" node -e '
const {chromium}=require("playwright"); const fs=require("fs");
const [url,out,stem,settle,...widths]=process.argv.slice(1);
(async()=>{
  const b=await chromium.launch({args:["--no-sandbox","--hide-scrollbars"]});
  for (const w of widths.map(Number)) {
    const ctx=await b.newContext({viewport:{width:w,height:900},deviceScaleFactor:1});
    const p=await ctx.newPage();
    await p.goto(url,{waitUntil:"load",timeout:90000});
    await p.waitForTimeout(Number(settle)*1000);
    await p.screenshot({path:`${out}/${stem}.w${w}.playwright.png`,fullPage:true});
    const m=await p.evaluate(()=>({nodes:document.querySelectorAll("*").length,
      text:(document.body&&document.body.innerText||"").length,
      links:document.querySelectorAll("a[href]").length,
      imgs:document.querySelectorAll("img").length,
      canvases:document.querySelectorAll("canvas").length,
      svgs:document.querySelectorAll("svg").length,
      iw:innerWidth,w:document.documentElement.scrollWidth,h:document.documentElement.scrollHeight}));
    fs.appendFileSync(`${out}/${stem}.probes.tsv`,[w,"playwright",m.h,JSON.stringify(m)].join("\t")+"\n");
    console.log(`  w${w} playwright: full height ${m.h} px`);
    await ctx.close();
  }
  await b.close();
})().catch(e=>{console.error("chromium lane failed:",e.message);process.exit(1)});
' "$URL" "$OUT" "$STEM" "$SETTLE" "${WIDTHS[@]}" || echo "  (chromium lane unavailable; obscura shots still written)"

echo
echo "probes  -> $OUT/$STEM.probes.tsv"
echo "renders -> $OUT/$STEM.w<width>.<engine>.png"
echo
printf '%-8s %-22s %s\n' WIDTH PAIR "DIFFERING PIXELS"
DIFF="$REPO/tools/imgdiff/bin/Release/net10.0/imgdiff"
if [[ -x "$DIFF" ]]; then
  for w in "${WIDTHS[@]}"; do
    for pair in rust:csharp playwright:rust playwright:csharp; do
      a="${pair%%:*}"; b="${pair##*:}"
      fa="$OUT/$STEM.w$w.$a.png"; fb="$OUT/$STEM.w$w.$b.png"
      [[ -f "$fa" && -f "$fb" ]] || continue
      printf '%-8s %-22s %s\n' "$w" "$a-vs-$b" "$("$DIFF" "$fa" "$fb" 8 | tail -1)"
    done
  done
else
  echo "(build tools/imgdiff to get pixel numbers: cd tools/imgdiff && dotnet build -c Release)"
fi
