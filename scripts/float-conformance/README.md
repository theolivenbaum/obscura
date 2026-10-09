# Float conformance

Lays the pages in `render-repros/floats/` out in Chromium and in the port and compares
every element's `getBoundingClientRect()`, inline elements' `getClientRects()` (one rect
per line fragment, which is how line placement around floats is measured: the shim's
`Range.getClientRects()` is a stub) and an `elementFromPoint` per element.

```bash
cd dotnet && dotnet build -c Release src/PocketCalculator.Cli && cd ..
cd scripts/float-conformance
ln -sfn "$(npm root -g)" node_modules      # ESM does not read NODE_PATH
node gen-pages.mjs                         # regenerate render-repros/floats/*.html
node conformance.mjs                       # PASS/FAIL per page and a summary
node conformance.mjs '' '' clear --verbose # just the clear-* pages, with the differences
```

The pages use `16px/20px 'Liberation Sans'`, which both engines have, so line breaks are
comparable. See "Float layout (CSS 2.1 9.5)" in `todo.md` for what is known to differ.
