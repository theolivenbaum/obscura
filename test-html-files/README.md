# test-html-files

Self-contained HTML fixtures for cross-engine render comparison.

Every file here must be **fully self-contained**: no external CSS, JS, fonts,
images or XHR. That is the whole point. A fixture that reaches the network makes
the comparison measure network access rather than rendering, and the engines do
not have equal access to it (headless Chromium is commonly blocked in CI
sandboxes while the Obscura CLI is not, which silently turned a 15% pixel
difference into a 79% one the first time this was run). Inline everything, and
use `data:` URIs for images.

These are distinct from `render-repros/`, which holds small single-purpose
layout repros driven by `scripts/parity-sweep.sh`. Files here are large,
realistic pages meant for visual comparison against a real browser.

| File | Exercises |
|---|---|
| `renderlab-complex.html` | canvas 2D, inline SVG with gradients and dash arrays, conic/radial gradients, `clip-path`, `backdrop-filter`, `mix-blend-mode`, layered shadows, a native `<dialog>`, `<details>`, a JS-generated DOM built on `DOMContentLoaded`, `requestAnimationFrame` animations that settle, and three responsive breakpoints |

Compare one against Chromium with:

```bash
scripts/render-compare.sh test-html-files/renderlab-complex.html
```

See `skills/render-compare/SKILL.md` for what the numbers mean.
