using SkiaSharp;

// Compares two PNGs on the overlap region and reports how many pixels differ
// beyond a small per-channel tolerance, plus the mean absolute difference.
// Anti-aliasing differs between rasterizers by a few counts, so a tolerance is
// the only honest way to read "same layout" off a screenshot.
if (args.Length > 0 && args[0] == "thumb") { return Thumb.Run(args); }
if (args.Length < 2) { Console.Error.WriteLine("usage: imgdiff a.png b.png [tolerance] | imgdiff thumb in out [w] [q]"); return 2; }
int tol = args.Length > 2 ? int.Parse(args[2]) : 8;
using var a = SKBitmap.Decode(args[0]);
using var b = SKBitmap.Decode(args[1]);
if (a is null || b is null) { Console.WriteLine("{\"error\":\"decode failed\"}"); return 1; }
int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
long differing = 0, total = (long)w * h, sum = 0;
for (int y = 0; y < h; y++)
for (int x = 0; x < w; x++)
{
    var pa = a.GetPixel(x, y); var pb = b.GetPixel(x, y);
    int dr = Math.Abs(pa.Red - pb.Red), dg = Math.Abs(pa.Green - pb.Green), db2 = Math.Abs(pa.Blue - pb.Blue);
    int d = Math.Max(dr, Math.Max(dg, db2));
    sum += d;
    if (d > tol) differing++;
}
double pct = total == 0 ? 0 : 100.0 * differing / total;
double mad = total == 0 ? 0 : (double)sum / total;
Console.WriteLine($"{{\"aw\":{a.Width},\"ah\":{a.Height},\"bw\":{b.Width},\"bh\":{b.Height}," +
                  $"\"overlapW\":{w},\"overlapH\":{h},\"differingPct\":{pct:F3},\"meanAbsDiff\":{mad:F2},\"tolerance\":{tol}}}");
return 0;
