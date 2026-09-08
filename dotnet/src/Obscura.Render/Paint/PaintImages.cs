// Port of the image selection, image painting, mask painting, and canvas-surface painting of
// crates/obscura-render/src/paint.rs.
using System.Globalization;
using Obscura.Dom;
using SkiaSharp;
using NodeId = Obscura.Dom.NodeId;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

internal static class PaintImages
{
    internal static ImageRequestProfile ImageRequestProfileFor(DomTree tree, NodeId id)
    {
        string? value = tree.GetNode(id)?.GetAttribute("crossorigin")?.Trim().ToLowerInvariant();
        return value switch
        {
            null => ImageRequestProfile.NoCorsInclude,
            "use-credentials" => ImageRequestProfile.CorsInclude,
            _ => ImageRequestProfile.CorsSameOrigin,
        };
    }

    /// <summary>
    /// Fetch every <c>&lt;img&gt;</c> once (seeding the cache for paint) and record its
    /// intrinsic dimensions so layout can size replaced elements.
    /// </summary>
    internal static (Dictionary<NodeId, ReplacedIntrinsic> Intrinsic, Dictionary<NodeId, SelectedImage> Selected)
        CollectImageIntrinsics(
            DomTree tree,
            (float Width, float Height) viewport,
            string? baseUrl,
            RenderResourceCache cache)
    {
        Dictionary<NodeId, ReplacedIntrinsic> output = [];
        Dictionary<NodeId, SelectedImage> selected = [];
        foreach (NodeId nid in DomTraversal.RenderedDescendants(tree, tree.Document))
        {
            Node? node = tree.GetNode(nid);
            if (node?.AsElement() is not { } element)
            {
                continue;
            }

            string url;
            float density;
            switch (element.Name.Local)
            {
                case "img":
                {
                    if (ResolveImgUrl(tree, nid, viewport) is not { } candidate)
                    {
                        continue;
                    }

                    (url, density) = candidate;
                    break;
                }

                case "video":
                {
                    string? poster = node.GetAttribute("poster")?.Trim();
                    if (string.IsNullOrEmpty(poster))
                    {
                        continue;
                    }

                    url = poster;
                    density = 1f;
                    break;
                }

                default:
                    continue;
            }

            string resolvedUrl = PaintResources.ResolveResourceUrl(url, baseUrl) ?? url;
            ImageRequestProfile profile = ImageRequestProfileFor(tree, nid);
            selected[nid] = new SelectedImage(resolvedUrl, density, profile);
            byte[]? bytes = PaintResources.FetchProfiledImageBytes(resolvedUrl, null, cache, profile);
            if (bytes is null)
            {
                continue;
            }

            if (PaintResources.ImageIntrinsicMetadata(bytes) is { } intrinsic)
            {
                // A 2x (or w-descriptor) candidate's raw pixels are density times its CSS size.
                output[nid] = intrinsic with
                {
                    Width = intrinsic.Width is { } w ? w / density : null,
                    Height = intrinsic.Height is { } h ? h / density : null,
                };
            }
        }

        return (output, selected);
    }

    /// <summary>
    /// Add intrinsic metadata for CSS <c>content:url(...)</c> images after the first cascade.
    /// </summary>
    internal static bool CollectContentImageIntrinsics(
        DomTree tree,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        string? baseUrl,
        RenderResourceCache cache,
        Dictionary<NodeId, ReplacedIntrinsic> output,
        Dictionary<NodeId, SelectedImage> selected,
        IReadOnlyDictionary<NodeId, ReplacedIntrinsic> sourceIntrinsic,
        IReadOnlyDictionary<NodeId, SelectedImage> sourceSelected,
        IReadOnlySet<NodeId> seeded)
    {
        bool changed = false;
        HashSet<NodeId> active = [];
        foreach ((NodeId nid, LayoutStyle style) in styles)
        {
            if (tree.GetNode(nid)?.AsElement() is not { } element
                || !string.Equals(element.Name.Local, "img", StringComparison.Ordinal))
            {
                continue;
            }

            if (style.ContentImage is not { } url)
            {
                continue;
            }

            active.Add(nid);
            string resolvedUrl = PaintResources.ResolveResourceUrl(url, baseUrl) ?? url;
            bool rememberedUrlChanged =
                cache.ContentImageIntrinsics.TryGetValue(nid, out RememberedContentImageIntrinsic remembered)
                && !string.Equals(remembered.ResolvedUrl, resolvedUrl, StringComparison.Ordinal);
            selected[nid] = new SelectedImage(resolvedUrl, 1f, ImageRequestProfile.NoCorsInclude);

            // CSS content replaces the element's ordinary source.
            ReplacedIntrinsic? previousDimensions =
                output.TryGetValue(nid, out ReplacedIntrinsic existing) ? existing : null;
            output.Remove(nid);
            byte[]? bytes = PaintResources.FetchBytes(resolvedUrl, null, cache);
            if (bytes is null)
            {
                changed |= previousDimensions is not null || rememberedUrlChanged;
                cache.ForgetContentImageIntrinsic(nid);
                continue;
            }

            if (PaintResources.ImageIntrinsicMetadata(bytes) is not { } intrinsic)
            {
                changed |= previousDimensions is not null || rememberedUrlChanged;
                cache.ForgetContentImageIntrinsic(nid);
                continue;
            }

            changed |= previousDimensions != intrinsic || rememberedUrlChanged;
            output[nid] = intrinsic;
            cache.RememberContentImageIntrinsic(nid, resolvedUrl, intrinsic);
        }

        // A remembered selection whose computed content disappeared must stop overriding the
        // element's ordinary source.
        foreach (NodeId nid in seeded.ToList())
        {
            if (active.Contains(nid))
            {
                continue;
            }

            cache.ForgetContentImageIntrinsic(nid);
            if (sourceIntrinsic.TryGetValue(nid, out ReplacedIntrinsic dimensions))
            {
                output[nid] = dimensions;
            }
            else
            {
                output.Remove(nid);
            }

            if (sourceSelected.TryGetValue(nid, out SelectedImage? source))
            {
                selected[nid] = source;
            }
            else
            {
                selected.Remove(nid);
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>Choose the URL to paint for an <c>&lt;img&gt;</c>.</summary>
    internal static (string Url, float Density)? ResolveImgUrl(
        DomTree tree,
        NodeId nid,
        (float Width, float Height) viewport)
    {
        Node? node = tree.GetNode(nid);
        if (node is null)
        {
            return null;
        }

        // A <picture>'s preceding, type/media-matching <source> wins over the <img>'s own
        // attributes (HTML "update the source set").
        if (PictureSourceUrl(tree, nid, viewport) is { } pick)
        {
            return pick;
        }

        string? sizes = node.GetAttribute("sizes");
        if (node.GetAttribute("srcset") is { } srcset
            && BestSrcsetCandidate(srcset, sizes, viewport) is { } candidate)
        {
            return candidate;
        }

        if (node.GetAttribute("src") is { } src)
        {
            string trimmed = src.Trim();
            if (trimmed.Length > 0)
            {
                return (trimmed, 1f);
            }
        }

        return null;
    }

    /// <summary>
    /// When <paramref name="imgNid"/> is inside a <c>&lt;picture&gt;</c>, walk its preceding
    /// <c>&lt;source&gt;</c> siblings and return the first supported selection.
    /// </summary>
    internal static (string Url, float Density)? PictureSourceUrl(
        DomTree tree,
        NodeId imgNid,
        (float Width, float Height) viewport)
    {
        Node? img = tree.GetNode(imgNid);
        if (img?.Parent is not { } parent)
        {
            return null;
        }

        bool isPicture = tree.GetNode(parent)?.AsElement() is { } parentElement
            && string.Equals(parentElement.Name.Local, "picture", StringComparison.Ordinal);
        if (!isPicture)
        {
            return null;
        }

        foreach (NodeId cid in tree.Children(parent))
        {
            // Only sources that precede the <img> contribute.
            if (cid == imgNid)
            {
                break;
            }

            Node? child = tree.GetNode(cid);
            if (child?.AsElement() is not { } element
                || !string.Equals(element.Name.Local, "source", StringComparison.Ordinal))
            {
                continue;
            }

            string? srcset = child.GetAttribute("srcset");
            if (srcset is null || srcset.Trim().Length == 0)
            {
                continue;
            }

            if (child.GetAttribute("type") is { } type && !ImageCapability.SourceTypeSupported(type))
            {
                continue;
            }

            if (child.GetAttribute("media") is { } media
                && media.Trim().Length > 0
                && !Css.CssMediaQuery.AppliesForViewport(media, viewport))
            {
                continue;
            }

            string? sizes = child.GetAttribute("sizes");
            if (BestSrcsetCandidate(srcset, sizes, viewport) is { } picked)
            {
                return picked;
            }
        }

        return null;
    }

    /// <summary>Pick one URL from a <c>srcset</c> list, matching the WebKit/Blink selection.</summary>
    internal static (string Url, float Density)? BestSrcsetCandidate(
        string srcset,
        string? sizes,
        (float Width, float Height) viewport)
    {
        const float Dpr = 1f;
        float sourceSize = SourceSizePx(sizes, viewport);
        List<(float Density, string Url)> candidates = [];

        // Parse candidates WHATWG-style: a URL is a run of non-whitespace, optionally followed
        // by a descriptor up to the next comma.
        ReadOnlySpan<char> rest = srcset.AsSpan().TrimStart([' ', '\t', '\n', '\r', '\f', ',']);
        while (rest.Length > 0)
        {
            int urlEnd = 0;
            while (urlEnd < rest.Length && !char.IsWhiteSpace(rest[urlEnd]))
            {
                urlEnd++;
            }

            ReadOnlySpan<char> rawUrl = rest[..urlEnd];
            rest = rest[urlEnd..];

            // Trailing commas on the URL mean the candidate had no descriptor.
            ReadOnlySpan<char> url = rawUrl.TrimEnd(',');
            bool noDescriptor = url.Length != rawUrl.Length;
            rest = rest.TrimStart();
            ReadOnlySpan<char> descriptor = [];
            if (!noDescriptor)
            {
                int comma = rest.IndexOf(',');
                int end = comma < 0 ? rest.Length : comma;
                descriptor = rest[..end].Trim();
                rest = rest[end..];
            }

            rest = rest.TrimStart([',', ' ', '\t', '\n', '\r', '\f']);
            if (url.Length == 0)
            {
                continue;
            }

            float density;
            if (descriptor.Length == 0)
            {
                density = 1f;
            }
            else if (descriptor[^1] == 'w'
                && float.TryParse(descriptor[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out float w))
            {
                if (sourceSize <= 0f)
                {
                    continue;
                }

                density = w / sourceSize;
            }
            else if (descriptor[^1] == 'x'
                && float.TryParse(descriptor[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x))
            {
                density = x;
            }
            else
            {
                // An `h` (height) descriptor or malformed token: skip the candidate.
                continue;
            }

            candidates.Add((density, url.ToString()));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        candidates.Sort((a, b) => a.Density.CompareTo(b.Density));
        foreach ((float density, string url) in candidates)
        {
            if (density >= Dpr)
            {
                return (url, F32.Max(density, 0.01f));
            }
        }

        (float lastDensity, string lastUrl) = candidates[^1];
        return (lastUrl, F32.Max(lastDensity, 0.01f));
    }

    /// <summary>Approximate the CSS px size an image will be displayed at, from `sizes`.</summary>
    internal static float SourceSizePx(string? sizes, (float Width, float Height) viewport)
    {
        if (sizes is null)
        {
            return viewport.Width;
        }

        foreach (string raw in sizes.Split(','))
        {
            string entry = raw.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            (string? condition, string length) = SplitSizeEntry(entry);
            if (condition is not null && !Css.CssMediaQuery.AppliesForViewport(condition, viewport))
            {
                continue;
            }

            if (LengthToPx(length, viewport.Width) is { } px)
            {
                return px;
            }
        }

        return viewport.Width;
    }

    /// <summary>Split one <c>sizes</c> entry into its media condition and trailing length.</summary>
    internal static (string? Condition, string Length) SplitSizeEntry(string entry)
    {
        List<string> tokens = [];
        System.Text.StringBuilder current = new();
        int depth = 0;
        foreach (char c in entry)
        {
            if (c == '(')
            {
                depth++;
                current.Append(c);
            }
            else if (c == ')')
            {
                depth--;
                current.Append(c);
            }
            else if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        string length = tokens.Count > 0 ? tokens[^1] : string.Empty;
        if (tokens.Count > 0)
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return (tokens.Count == 0 ? null : string.Join(' ', tokens), length);
    }

    /// <summary>Resolve a <c>sizes</c> length to px against the assumed viewport.</summary>
    internal static float? LengthToPx(string length, float viewportWidth)
    {
        string t = length.Trim().ToLowerInvariant();
        static float? Num(string s) =>
            float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? v
                : null;

        if (t.EndsWith("vw", StringComparison.Ordinal) && Num(t[..^2]) is { } vw)
        {
            return vw / 100f * viewportWidth;
        }

        if (t.EndsWith('%') && Num(t[..^1]) is { } percent)
        {
            return percent / 100f * viewportWidth;
        }

        if (t.EndsWith("px", StringComparison.Ordinal) && Num(t[..^2]) is { } px)
        {
            return px;
        }

        if (t.EndsWith("rem", StringComparison.Ordinal) && Num(t[..^3]) is { } rem)
        {
            return rem * 16f;
        }

        if (t.EndsWith("em", StringComparison.Ordinal) && Num(t[..^2]) is { } em)
        {
            return em * 16f;
        }

        return Num(t);
    }

    internal static Rect? BackgroundImageRect(
        string src,
        string? baseUrl,
        in Rect boxRect,
        (float Width, float Height)? explicitSize,
        string? sizeExpression,
        ObjectFit? fit,
        BackgroundPosition position,
        float em,
        float rem,
        (float Width, float Height) viewport,
        RenderResourceCache cache)
    {
        byte[]? bytes = PaintResources.FetchBytes(src, baseUrl, cache);
        if (bytes is null)
        {
            return null;
        }

        (float Width, float Height)? intrinsic = PaintSvg.IsSvg(bytes)
            ? PaintSvg.SvgIntrinsic(bytes)
            : PaintResources.ImageDimensions(bytes) is { } dimensions
                ? (dimensions.Width, (float)dimensions.Height)
                : null;

        (float Width, float Height)? expressionSize = null;
        if (sizeExpression is { } expression)
        {
            List<string> components = SplitBackgroundSizeComponents(expression);
            float? Resolve(string value, float basis) =>
                value.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : ComputedStyle.ResolveContextualLength(
                        value,
                        em,
                        rem,
                        viewport.Width / 100f,
                        viewport.Height / 100f,
                        basis);

            float? width = components.Count > 0 ? Resolve(components[0], boxRect.Width) : null;
            float? height = components.Count > 1 ? Resolve(components[1], boxRect.Height) : null;
            expressionSize = (width, height, intrinsic) switch
            {
                ({ } w, { } h, _) => (w, h),
                ({ } w, null, { } i) => (w, w * i.Height / i.Width),
                (null, { } h, { } i) => (h * i.Width / i.Height, h),
                (null, null, { } i) => i,
                _ => null,
            };
        }

        // `cover` and `contain` are complete sizing algorithms, not contextual lengths.
        float finalWidth;
        float finalHeight;
        if (fit is { } objectFit)
        {
            if (intrinsic is not { } size)
            {
                return null;
            }

            float scale = objectFit switch
            {
                ObjectFit.Cover => F32.Max(boxRect.Width / size.Width, boxRect.Height / size.Height),
                ObjectFit.Contain => F32.Min(boxRect.Width / size.Width, boxRect.Height / size.Height),
                _ => 1f,
            };
            finalWidth = size.Width * scale;
            finalHeight = size.Height * scale;
        }
        else if (expressionSize is { } fromExpression)
        {
            (finalWidth, finalHeight) = fromExpression;
        }
        else if (explicitSize is { } authored)
        {
            (finalWidth, finalHeight) = authored;
        }
        else if (intrinsic is { } natural)
        {
            (finalWidth, finalHeight) = natural;
        }
        else
        {
            finalWidth = boxRect.Width;
            finalHeight = boxRect.Height;
        }

        if (finalWidth <= 0f || finalHeight <= 0f)
        {
            return null;
        }

        return new Rect(
            boxRect.X + position.X.Resolve(boxRect.Width - finalWidth),
            boxRect.Y + position.Y.Resolve(boxRect.Height - finalHeight),
            finalWidth,
            finalHeight);
    }

    internal static List<string> SplitBackgroundSizeComponents(string value)
    {
        List<string> components = [];
        int depth = 0;
        int? start = null;
        for (int index = 0; index < value.Length; index++)
        {
            char ch = value[index];
            if (ch == '(')
            {
                depth++;
                start ??= index;
            }
            else if (ch == ')')
            {
                depth = Math.Max(depth - 1, 0);
            }
            else if (char.IsWhiteSpace(ch) && depth == 0)
            {
                if (start is { } begin)
                {
                    components.Add(value[begin..index].Trim());
                    start = null;
                }
            }
            else
            {
                start ??= index;
            }
        }

        if (start is { } last)
        {
            components.Add(value[last..].Trim());
        }

        return components;
    }

    internal static bool PaintCanvasSurface(
        CanvasSurface surface,
        in Rect rect,
        in Rect visibleRect,
        ObjectFit objectFit,
        ObjectPosition objectPosition,
        Pixmap pixmap,
        ResolvedBorderRadii clipRadius,
        Mask? extraClip)
    {
        if (surface.Width == 0
            || surface.Height == 0
            || rect.Width <= 0f
            || rect.Height <= 0f
            || !PaintDomPainter.RectIntersectsPaintSurface(visibleRect, pixmap, 1f))
        {
            return false;
        }

        // Canvas ImageData is straight-alpha RGBA; the surface consumes premultiplied RGBA.
        Pixmap? content = Pixmap.New(surface.Width, surface.Height);
        if (content is null)
        {
            return false;
        }

        using Pixmap owned = content;
        ReadOnlySpan<byte> rgba = surface.Rgba.Span;
        for (int index = 0; index * 4 + 3 < rgba.Length && index < owned.Pixels.Length; index++)
        {
            int offset = index * 4;
            uint alpha = rgba[offset + 3];
            owned.Pixels[index] = PremultipliedColor.FromRgba(
                (byte)(((rgba[offset] * alpha) + 127) / 255),
                (byte)(((rgba[offset + 1] * alpha) + 127) / 255),
                (byte)(((rgba[offset + 2] * alpha) + 127) / 255),
                rgba[offset + 3]);
        }

        Rect dest = ObjectFitDestPositioned(
            rect,
            surface.Width,
            surface.Height,
            objectFit,
            objectPosition);
        if (dest.Width <= 0f || dest.Height <= 0f)
        {
            return false;
        }

        Mask? clip = BuildBoxClip(pixmap, dest, visibleRect, clipRadius, extraClip);
        Affine2 transform = new(
            dest.Width / surface.Width,
            0f,
            0f,
            dest.Height / surface.Height,
            dest.X,
            dest.Y);
        Surface.DrawPixmap(pixmap, 0, 0, owned, 1f, bilinear: true, transform, clip);
        return true;
    }

    private static Mask? BuildBoxClip(
        Pixmap pixmap,
        in Rect dest,
        in Rect visibleRect,
        ResolvedBorderRadii clipRadius,
        Mask? extraClip)
    {
        bool hasRadius = !clipRadius.IsZero();
        bool needsBoxClip = hasRadius
            || dest.Width > visibleRect.Width + 0.5f
            || dest.Height > visibleRect.Height + 0.5f
            || dest.X < visibleRect.X - 0.5f
            || dest.Y < visibleRect.Y - 0.5f;
        Mask? clip = extraClip?.Clone();
        if (!needsBoxClip)
        {
            return clip;
        }

        SKPath? path;
        if (hasRadius)
        {
            path = PaintClips.RoundedRectPathRadii(
                visibleRect.X,
                visibleRect.Y,
                visibleRect.Width,
                visibleRect.Height,
                clipRadius);
        }
        else if (visibleRect.Width > 0f && visibleRect.Height > 0f)
        {
            using SKPathBuilder builder = new();
            builder.AddRect(new SKRect(
                visibleRect.X,
                visibleRect.Y,
                visibleRect.X + visibleRect.Width,
                visibleRect.Y + visibleRect.Height));
            path = builder.Detach();
        }
        else
        {
            path = null;
        }

        if (clip is not null && path is not null)
        {
            clip.IntersectPath(path, evenOdd: false, antiAlias: true);
        }
        else if (clip is null)
        {
            clip = PaintClips.RoundedBoxClipMaskRadii(
                pixmap.Width,
                pixmap.Height,
                visibleRect,
                clipRadius);
        }

        path?.Dispose();
        return clip;
    }

    internal static bool PaintImage(
        string src,
        string? baseUrl,
        in Rect rect,
        in Rect visibleRect,
        ObjectFit objectFit,
        ObjectPosition objectPosition,
        Pixmap pixmap,
        RenderResourceCache cache,
        ImageRequestProfile? profile,
        Affine2? transform,
        ResolvedBorderRadii clipRadius,
        Mask? extraClip)
    {
        if (rect.Width <= 0f || rect.Height <= 0f)
        {
            return false;
        }

        // Image-bearing display lists use the proven CSS-pixel raster path. Cull before cache
        // lookup, SVG parsing, or bitmap resizing.
        if (!PaintDomPainter.RectIntersectsPaintSurface(visibleRect, pixmap, 1f))
        {
            return false;
        }

        byte[]? bytes = profile is { } requestProfile
            ? PaintResources.FetchProfiledImageBytes(src, baseUrl, cache, requestProfile)
            : PaintResources.FetchBytes(src, baseUrl, cache);
        if (bytes is null)
        {
            return false;
        }

        bool svg = PaintSvg.IsSvg(bytes);

        // Destination sub-rect within the element box.
        Rect dest;
        if (objectFit == ObjectFit.Fill)
        {
            dest = rect;
        }
        else
        {
            (float Width, float Height)? intrinsic = svg
                ? PaintSvg.SvgIntrinsic(bytes)
                : PaintResources.ImageDimensions(bytes) is { } dimensions
                    ? (dimensions.Width, (float)dimensions.Height)
                    : null;
            dest = intrinsic is { } size
                ? ObjectFitDestPositioned(rect, size.Width, size.Height, objectFit, objectPosition)
                : rect;
        }

        uint dw = (uint)F32.Max(F32.Round(dest.Width), 1f);
        uint dh = (uint)F32.Max(F32.Round(dest.Height), 1f);
        Pixmap? content = svg
            ? SvgRenderer.Render(bytes, dw, dh)
            : PaintResources.RasterToPixmap(bytes, dw, dh);
        if (content is null)
        {
            return false;
        }

        using Pixmap owned = content;
        Mask? clip = BuildBoxClip(pixmap, dest, visibleRect, clipRadius, extraClip);
        Surface.DrawPixmap(
            pixmap,
            (int)dest.X,
            (int)dest.Y,
            owned,
            1f,
            false,
            transform ?? Affine2.Identity,
            clip);
        return true;
    }

    /// <summary>The destination sub-rect for replaced image content within its box.</summary>
    internal static Rect ObjectFitDest(in Rect boxRect, float iw, float ih, ObjectFit fit) =>
        ObjectFitDestPositioned(boxRect, iw, ih, fit, ObjectPosition.Default);

    internal static Rect ObjectFitDestPositioned(
        in Rect boxRect,
        float iw,
        float ih,
        ObjectFit fit,
        ObjectPosition position)
    {
        float bw = boxRect.Width;
        float bh = boxRect.Height;
        if (iw <= 0f || ih <= 0f)
        {
            return boxRect;
        }

        (float dw, float dh) = fit switch
        {
            ObjectFit.Fill => (bw, bh),
            ObjectFit.Contain => Scaled(F32.Min(bw / iw, bh / ih)),
            ObjectFit.Cover => Scaled(F32.Max(bw / iw, bh / ih)),
            ObjectFit.None => (iw, ih),

            // min(Contain-size, intrinsic-size): the Contain fit, never scaled up.
            _ => Scaled(F32.Min(F32.Min(bw / iw, bh / ih), 1f)),
        };

        return new Rect(
            boxRect.X + position.X.Resolve(bw - dw),
            boxRect.Y + position.Y.Resolve(bh - dh),
            dw,
            dh);

        (float, float) Scaled(float s) => (iw * s, ih * s);
    }

    /// <summary>
    /// Paint a <c>mask-image</c>: an SVG shape used as a stencil and tinted by
    /// <c>background-color</c>/<c>color</c> rather than carrying its own colors.
    /// </summary>
    internal static bool PaintMask(
        string src,
        string? baseUrl,
        in Rect rect,
        ResolvedBorderRadii borderRadius,
        RgbaColor fill,
        ((float X, float Y) Center, List<GradientStop> Stops)? radialGradient,
        RadialGradientGeometry? radialGeometry,
        float em,
        float rootFontSize,
        (float Width, float Height) viewport,
        (float Angle, List<GradientStop> Stops)? linearGradient,
        (float Angle, (float X, float Y) Center, List<GradientStop> Stops)? conicGradient,
        (float Width, float Height)? maskSize,
        (bool X, bool Y)? maskRepeat,
        Mask? extraClip,
        Pixmap pixmap,
        RenderResourceCache cache)
    {
        if (rect.Width <= 0f || rect.Height <= 0f)
        {
            return false;
        }

        // Masks likewise force the CSS-pixel raster path.
        if (!PaintDomPainter.RectIntersectsPaintSurface(rect, pixmap, 1f))
        {
            return false;
        }

        byte[]? bytes = PaintResources.FetchBytes(src, baseUrl, cache);
        if (bytes is null)
        {
            return false;
        }

        uint boxWidth = (uint)MathF.Ceiling(rect.Width);
        uint boxHeight = (uint)MathF.Ceiling(rect.Height);
        (uint tileWidth, uint tileHeight) = maskSize is { } size
            ? ((uint)MathF.Ceiling(F32.Max(size.Width, 1f)), (uint)MathF.Ceiling(F32.Max(size.Height, 1f)))
            : (boxWidth, boxHeight);
        Pixmap? mask = PaintSvg.IsSvg(bytes)
            ? SvgRenderer.Render(bytes, tileWidth, tileHeight)
            : PaintResources.RasterToPixmap(bytes, tileWidth, tileHeight);
        if (mask is null)
        {
            return false;
        }

        using Pixmap ownedMask = mask;
        (bool X, bool Y) repeat = maskSize is not null
            ? maskRepeat ?? (true, true)
            : maskRepeat ?? (false, false);
        List<(float Position, RgbaColor Color)>? normalizedLinear =
            linearGradient is { } linear ? PaintGradients.NormalizedStops(linear.Stops) : null;
        List<(float Position, RgbaColor Color)>? normalizedConic =
            conicGradient is { } conic ? PaintGradients.NormalizedStops(conic.Stops) : null;
        List<(float Position, RgbaColor Color)>? normalizedRadial =
            radialGradient is { } radial ? PaintGradients.NormalizedStops(radial.Stops) : null;
        Pixmap? recolored = Pixmap.New(boxWidth, boxHeight);
        if (recolored is null)
        {
            return false;
        }

        using Pixmap ownedRecolored = recolored;
        for (uint y = 0; y < boxHeight; y++)
        {
            if (!repeat.Y && y >= tileHeight)
            {
                continue;
            }

            uint tileY = repeat.Y ? y % tileHeight : y;
            for (uint x = 0; x < boxWidth; x++)
            {
                if (!repeat.X && x >= tileWidth)
                {
                    continue;
                }

                uint tileX = repeat.X ? x % tileWidth : x;
                uint coverage = ownedMask.Pixels[(int)((tileY * tileWidth) + tileX)].A;
                if (coverage == 0)
                {
                    continue;
                }

                float sampleX = rect.X + x + 0.5f;
                float sampleY = rect.Y + y + 0.5f;
                RgbaColor color;
                if (conicGradient is { } conicSource && normalizedConic is { } conicStops)
                {
                    color = PaintGradients.ConicColorAt(
                        rect,
                        conicSource.Angle,
                        conicSource.Center,
                        conicStops,
                        sampleX,
                        sampleY);
                }
                else if (linearGradient is { } linearSource && normalizedLinear is { } linearStops)
                {
                    color = PaintGradients.LinearColorAt(
                        rect,
                        linearSource.Angle,
                        linearStops,
                        sampleX,
                        sampleY);
                }
                else if (radialGradient is { } radialSource && normalizedRadial is { } radialStops)
                {
                    color = PaintGradients.RadialColorAt(
                        rect,
                        radialSource.Center,
                        radialStops,
                        radialGeometry,
                        em,
                        rootFontSize,
                        viewport,
                        sampleX,
                        sampleY);
                }
                else
                {
                    color = fill;
                }

                color = color with { A = (byte)(color.A * coverage / 255) };
                ownedRecolored.Pixels[(int)((y * boxWidth) + x)] = PaintColor.Premultiplied(color);
            }
        }

        Mask? clip = extraClip?.Clone();
        if (!borderRadius.IsZero())
        {
            SKPath? path = PaintClips.RoundedRectPathRadii(
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                borderRadius);
            if (clip is not null && path is not null)
            {
                clip.IntersectPath(path, evenOdd: false, antiAlias: true);
            }
            else if (clip is null)
            {
                clip = PaintClips.RoundedBoxClipMaskRadii(
                    pixmap.Width,
                    pixmap.Height,
                    rect,
                    borderRadius);
            }

            path?.Dispose();
        }

        Surface.DrawPixmap(
            pixmap,
            (int)MathF.Floor(rect.X),
            (int)MathF.Floor(rect.Y),
            ownedRecolored,
            1f,
            false,
            Affine2.Identity,
            clip);
        return true;
    }
}
