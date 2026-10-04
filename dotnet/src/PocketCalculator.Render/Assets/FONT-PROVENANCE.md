# Bundled font provenance

## Noto Color Emoji

- File: `noto-color-emoji.ttf`
- Upstream: `googlefonts/noto-emoji`
- Version: 2.051 (Unicode 17.0)
- Commit: `8998f5dd683424a73e2314a8c1f1e359c19e8742`
- SHA-256: `72a635cb3d2f3524c51620cdde406b217204e8a6a06c6a096ff8ed4b5fd6e27b`
- License: SIL Open Font License 1.1, included in `LICENSE-NOTO-COLOR-EMOJI.txt`

## Noto Sans CJK SC

- File: `noto-sans-cjk-sc-regular.otf` (upstream name `NotoSansCJKsc-Regular.otf`), unmodified
- Upstream: `notofonts/noto-cjk`, `Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf`
- URL: `https://github.com/notofonts/noto-cjk/raw/f8d157532fbfaeda587e826d4cd5b21a49186f7c/Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf`
  (fetched with git at that commit; blob `dc15562470b4f842321894787a0d066879ccff8b`)
- Version: 2.004 (release `Sans2.004`, 2021-04-28; name table
  `Version 2.004;hotconv 1.0.118;makeotfexe 2.5.65603`)
- Commit: `f8d157532fbfaeda587e826d4cd5b21a49186f7c`
- Size: 16,437,364 bytes; 65,535 glyphs, CFF outlines, one weight (Regular, 400)
- SHA-256: `2c76254f6fc379fddfce0a7e84fb5385bb135d3e399294f6eeb6680d0365b74b`
- Copyright: (c) 2014-2021 Adobe (http://www.adobe.com/). Noto is a trademark of Google Inc.
- License: SIL Open Font License 1.1, included in `LICENSE-NOTO-SANS-CJK.txt` (upstream
  `Sans/LICENSE`, verbatim)
- Coverage: CJK Unified Ideographs 20,976/20,992, Extension A 6,582/6,592, Hiragana 93/95,
  Katakana 96/96, Hangul Syllables 11,172/11,172, CJK Symbols and Punctuation 64/64,
  Halfwidth and Fullwidth Forms 225/240, Bopomofo 43/48, Extension B 2,108/42,720.
- Why this file: each regional face under `OTF/` carries the full pan-CJK glyph set (the region
  only picks the default glyph forms), so one face covers Chinese, Japanese and Korean. The
  smaller candidates do not: `SubsetOTF/SC/NotoSansSC-Regular.otf` (8,331,336 bytes) and
  `Variable/OTF/Subset/NotoSansSC-VF.otf` (15,054,748 bytes) have no Hangul Syllables, and the
  full variable face `Variable/OTF/NotoSansCJKsc-VF.otf` is 30,737,452 bytes. A static Bold
  (`NotoSansCJKsc-Bold.otf`, 17,002,248 bytes) would double the cost, so bold CJK text is
  synthesized from this face, as Chromium does for a family with one weight.
