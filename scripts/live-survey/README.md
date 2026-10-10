# Live-site survey

Loads real websites in Chromium (twice, for a noise floor) and in the port's CDP
server, and scores where the port differs by more than Chromium differs from
itself. It is the harness behind the October 2026 top-100 survey.

```bash
# once: Playwright + Chromium (preinstalled in the cloud image), Pillow + numpy
cd dotnet && dotnet build -c Release && cd ..
cd scripts/live-survey
ln -sfn "$(npm root -g)" node_modules      # ESM does not read NODE_PATH
node survey.mjs sites.txt 2                # writes out/json/<domain>.json and out/shots/
node survey.mjs sites.txt 2 bing.com       # or just some sites
python3 analyze.py out > analysis.txt      # verdicts, ranked; analysis.json alongside
```

- Each site gets a fresh `pocket-calculator serve`, the same Chrome 141 Linux UA
  and a 1280x800 viewport in both engines. Pages wait for `load` (45 s), then 4 s.
- Behind a proxy, set `HTTPS_PROXY`: both engines are given it explicitly. The
  port ignores an ambient proxy (SECURITY.md M2), so without that it would leave
  through a different egress than Chromium and the comparison measures the network.
- `analyze.py` treats a site as inconclusive when Chromium was blocked or when its
  two runs disagree badly; nothing can be said about the port there.
- Verdicts: `agree` (nothing beyond Chromium's own variation), `minor` (layout or
  pixel drift), `diverge` (missing content, failed navigation, or several drifts),
  `broken` (blank or near-empty page).
- `sites.txt` is the 100 most-visited real websites from Cloudflare Radar's top-500
  domains for 21-28 September 2026, after removing ad, analytics, CDN, DNS and API
  domains. Live pages change; re-run broken sites before drawing conclusions.
