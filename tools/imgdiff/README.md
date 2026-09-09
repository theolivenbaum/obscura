# imgdiff

Compares two PNG screenshots and reports how many pixels differ beyond a
per-channel tolerance, plus the mean absolute difference. Also scales a
screenshot down to a JPEG (`imgdiff thumb in.png out.jpg [width] [quality]`)
for embedding in a report.

The tolerance is the point. Two rasterizers antialias glyph edges differently by
a few counts, so an exact comparison reports a large difference for a page that
is laid out identically. Sweeping the tolerance separates the two cases: an
anti-aliasing difference collapses as the tolerance rises, a layout difference
does not.

```bash
cd tools/imgdiff && dotnet build -c Release
bin/Release/net10.0/imgdiff a.png b.png 8
```

Kept out of `dotnet/Obscura.slnx` on purpose: it is a diagnostic for comparing
engines, not part of the engine, and it takes a SkiaSharp native asset of its
own.
