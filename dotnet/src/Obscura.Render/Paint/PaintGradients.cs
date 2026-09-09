// Port of the gradient and background-geometry helpers of crates/obscura-render/src/paint.rs.
using SkiaSharp;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

internal readonly record struct BackgroundGeometry(
    Rect OriginRect,
    Rect ClipRect,
    ResolvedBorderRadii ClipRadii);

internal static class PaintGradients
{
    internal static Rect InsetRect(in Rect rect, Sides<float> insets) => new(
        rect.X + insets.Left,
        rect.Y + insets.Top,
        F32.Max(rect.Width - insets.Left - insets.Right, 0f),
        F32.Max(rect.Height - insets.Top - insets.Bottom, 0f));

    internal static Sides<float> AddSides(Sides<float> a, Sides<float> b) => new(
        a.Top + b.Top,
        a.Right + b.Right,
        a.Bottom + b.Bottom,
        a.Left + b.Left);

    internal static BackgroundGeometry BackgroundGeometryFor(in Rect rect, LayoutStyle style)
    {
        Sides<float> border = new(
            style.Border.Top,
            style.Border.Right,
            style.Border.Bottom,
            style.Border.Left);
        Sides<float> padding = new(
            style.Padding.Top,
            style.Padding.Right,
            style.Padding.Bottom,
            style.Padding.Left);
        Sides<float> content = AddSides(border, padding);
        Sides<float> originInsets = style.BackgroundOrigin switch
        {
            BackgroundOrigin.BorderBox => Sides<float>.All(0f),
            BackgroundOrigin.PaddingBox => border,
            _ => content,
        };
        Sides<float> clipInsets = style.BackgroundClip switch
        {
            BackgroundClip.BorderBox or BackgroundClip.Text => Sides<float>.All(0f),
            BackgroundClip.PaddingBox => border,
            _ => content,
        };
        ResolvedBorderRadii outerRadii = style.BorderModel.Radii.Resolve(rect.Width, rect.Height);
        return new BackgroundGeometry(
            InsetRect(rect, originInsets),
            InsetRect(rect, clipInsets),
            outerRadii.Inset(clipInsets));
    }

    internal static SKPath? BackgroundClipPath(BackgroundGeometry geometry)
    {
        if (geometry.ClipRect.Width <= 0f || geometry.ClipRect.Height <= 0f)
        {
            return null;
        }

        if (!geometry.ClipRadii.IsZero())
        {
            return PaintClips.RoundedRectPathRadii(
                geometry.ClipRect.X,
                geometry.ClipRect.Y,
                geometry.ClipRect.Width,
                geometry.ClipRect.Height,
                geometry.ClipRadii);
        }

        using SKPathBuilder builder = new();
        builder.AddRect(new SKRect(
            geometry.ClipRect.X,
            geometry.ClipRect.Y,
            geometry.ClipRect.X + geometry.ClipRect.Width,
            geometry.ClipRect.Y + geometry.ClipRect.Height));
        return builder.Detach();
    }

    internal static Mask? BackgroundExtraClip(Mask? ancestorClip, Mask? polygonClip) =>
        PaintClips.IntersectClipMasks(ancestorClip?.Clone(), polygonClip);

    /// <summary>
    /// Fill <paramref name="path"/> with a CSS <c>linear-gradient</c>. <paramref name="angle"/>
    /// is degrees clockwise from 12 o'clock.
    /// </summary>
    internal static void PaintLinearGradient(
        Pixmap pixmap,
        SKPath path,
        in Rect rect,
        float angle,
        IReadOnlyList<GradientStop> stops,
        Mask? clip,
        float rasterScale)
    {
        if (stops.Count < 2)
        {
            return;
        }

        float rad = F32.ToRadians(angle);
        float dx = MathF.Sin(rad);
        float dy = -MathF.Cos(rad);
        float cx = rect.X + (rect.Width / 2f);
        float cy = rect.Y + (rect.Height / 2f);
        float half = ((MathF.Abs(dx) * rect.Width) + (MathF.Abs(dy) * rect.Height)) / 2f;
        SKPoint start = new(cx - (dx * half), cy - (dy * half));
        SKPoint end = new(cx + (dx * half), cy + (dy * half));
        int n = stops.Count;
        SKColor[] colors = new SKColor[n];
        float[] positions = new float[n];
        float last = 0f;
        for (int i = 0; i < n; i++)
        {
            RgbaColor color = GradientStopColor(stops, i);
            float p = F32.Max(Math.Clamp(stops[i].Position ?? ((float)i / (n - 1)), 0f, 1f), last);
            last = p;
            colors[i] = PaintColor.ToSk(color);
            positions[i] = p;
        }

        if (start == end)
        {
            return;
        }

        using SKShader shader = SKShader.CreateLinearGradient(
            start,
            end,
            colors,
            positions,
            SKShaderTileMode.Clamp);
        using SKPaint paint = new()
        {
            Shader = shader,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        Surface.FillPath(pixmap, path, paint, false, Surface.RasterTransform(rasterScale), clip);
    }

    /// <summary>
    /// Resolve every color-stop to a fraction of the gradient's own line (or ray, for a
    /// radial gradient), then fill in runs of unpositioned stops by spacing them evenly
    /// between their positioned neighbours.
    /// </summary>
    /// <remarks>
    /// <paramref name="lineLength"/> is what makes an authored length meaningful:
    /// <c>transparent 32rem</c> is 512px along the gradient line, not 100% of it, so the
    /// two cannot be resolved at parse time. The percentage already in each
    /// <see cref="GradientStop"/> is the fallback for layers built programmatically, which
    /// carry no authored strings.
    /// </remarks>
    private static float?[] ResolveGradientStopPositions(
        IReadOnlyList<GradientStop> stops,
        IReadOnlyList<string?> stopPositions,
        float lineLength,
        float em,
        float rem,
        (float Width, float Height) viewport)
    {
        float?[] positions = new float?[stops.Count];
        for (int index = 0; index < stops.Count; index++)
        {
            float? resolved = null;
            if (index < stopPositions.Count && stopPositions[index] is { } expression)
            {
                float? pixels = ComputedStyle.ResolveContextualLength(
                    expression,
                    em,
                    rem,
                    viewport.Width / 100f,
                    viewport.Height / 100f,
                    lineLength);
                if (pixels is { } value)
                {
                    resolved = value / lineLength;
                }
            }

            positions[index] = resolved ?? stops[index].Position;
        }

        if (positions.Length == 0)
        {
            return positions;
        }

        positions[0] ??= 0f;
        positions[^1] ??= 1f;

        int previous = 0;
        for (int index = 1; index < positions.Length; index++)
        {
            if (positions[index] is not { } current)
            {
                continue;
            }

            float previousPosition = positions[previous] ?? 0f;
            float position = F32.Max(current, previousPosition);
            positions[index] = position;
            int gap = index - previous;
            for (int offset = 1; offset < gap; offset++)
            {
                positions[previous + offset] =
                    previousPosition + ((position - previousPosition) * offset / gap);
            }

            previous = index;
        }

        return positions;
    }

    internal static void PaintLinearGradientLayer(
        Pixmap pixmap,
        SKPath path,
        in Rect rect,
        float angle,
        IReadOnlyList<GradientStop> stops,
        IReadOnlyList<string?> stopPositions,
        bool repeating,
        float em,
        float rem,
        (float Width, float Height) viewport,
        Mask? clip,
        float rasterScale)
    {
        if (stops.Count < 2)
        {
            return;
        }

        float rad = F32.ToRadians(angle);
        float dx = MathF.Sin(rad);
        float dy = -MathF.Cos(rad);
        float cx = rect.X + (rect.Width / 2f);
        float cy = rect.Y + (rect.Height / 2f);
        float half = ((MathF.Abs(dx) * rect.Width) + (MathF.Abs(dy) * rect.Height)) / 2f;
        float lineLength = half * 2f;
        if (lineLength <= float.Epsilon)
        {
            return;
        }

        SKPoint baseStart = new(cx - (dx * half), cy - (dy * half));
        SKPoint baseEnd = new(cx + (dx * half), cy + (dy * half));
        float?[] positions = ResolveGradientStopPositions(
            stops,
            stopPositions,
            lineLength,
            em,
            rem,
            viewport);
        int lastIndex = positions.Length - 1;

        float first = positions[0] ?? 0f;
        float last = positions[lastIndex] ?? first;
        if (repeating && last - first <= 1e-6f)
        {
            RgbaColor color = GradientStopColor(stops, lastIndex);
            Surface.FillPath(pixmap, path, color, false, Surface.RasterTransform(rasterScale), clip);
            return;
        }

        SKPoint start;
        SKPoint end;
        SKShaderTileMode spread;
        (float Origin, float Span)? normalize;
        if (repeating)
        {
            start = new SKPoint(
                baseStart.X + ((baseEnd.X - baseStart.X) * first),
                baseStart.Y + ((baseEnd.Y - baseStart.Y) * first));
            end = new SKPoint(
                baseStart.X + ((baseEnd.X - baseStart.X) * last),
                baseStart.Y + ((baseEnd.Y - baseStart.Y) * last));
            spread = SKShaderTileMode.Repeat;
            normalize = (first, last - first);
        }
        else
        {
            start = baseStart;
            end = baseEnd;
            spread = SKShaderTileMode.Clamp;
            normalize = null;
        }

        SKColor[] colors = new SKColor[stops.Count];
        float[] offsets = new float[stops.Count];
        float monotonic = 0f;
        for (int index = 0; index < stops.Count; index++)
        {
            RgbaColor color = GradientStopColor(stops, index);
            float position = normalize is { } norm
                ? Math.Clamp(((positions[index] ?? norm.Origin) - norm.Origin) / norm.Span, 0f, 1f)
                : Math.Clamp(positions[index] ?? 0f, 0f, 1f);
            position = F32.Max(position, monotonic);
            monotonic = position;
            colors[index] = PaintColor.ToSk(color);
            offsets[index] = position;
        }

        if (start == end)
        {
            return;
        }

        using SKShader shader = SKShader.CreateLinearGradient(start, end, colors, offsets, spread);
        using SKPaint paint = new()
        {
            Shader = shader,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        Surface.FillPath(pixmap, path, paint, false, Surface.RasterTransform(rasterScale), clip);
    }

    internal static void PaintRadialGradient(
        Pixmap pixmap,
        SKPath path,
        in Rect rect,
        (float X, float Y) center,
        IReadOnlyList<GradientStop> stops,
        IReadOnlyList<string?> stopPositions,
        RadialGradientGeometry? geometry,
        float em,
        float rootFontSize,
        (float Width, float Height) viewport,
        Mask? clip,
        float rasterScale)
    {
        if (stops.Count < 2)
        {
            return;
        }

        SKPoint origin = new(
            rect.X + (rect.Width * center.X),
            rect.Y + (rect.Height * center.Y));
        if (ResolveRadialGradientRadii(rect, origin, geometry, em, rootFontSize, viewport)
            is not { } radii)
        {
            return;
        }

        // Stop positions run along the gradient ray, which is the horizontal radius in the
        // circle-normalized space the shader below works in.
        float?[] resolved = ResolveGradientStopPositions(
            stops,
            stopPositions,
            radii.X,
            em,
            rootFontSize,
            viewport);
        SKColor[] colors = new SKColor[stops.Count];
        float[] offsets = new float[stops.Count];
        float monotonic = 0f;
        for (int index = 0; index < stops.Count; index++)
        {
            float position = F32.Max(Math.Clamp(resolved[index] ?? 0f, 0f, 1f), monotonic);
            monotonic = position;
            colors[index] = PaintColor.ToSk(GradientStopColor(stops, index));
            offsets[index] = position;
        }

        // Skia's radial shader is circular, exactly as tiny-skia's is. Keep the established CSS
        // coordinate-space shader and transform that circle around its center into the authored
        // ellipse. The paint transform later handles device scale.
        float ellipseScale = radii.Y / radii.X;
        SKMatrix gradientTransform = new(
            1f, 0f, 0f,
            0f, ellipseScale, origin.Y * (1f - ellipseScale),
            0f, 0f, 1f);
        using SKShader shader = SKShader.CreateRadialGradient(
            origin,
            radii.X,
            colors,
            offsets,
            SKShaderTileMode.Clamp,
            gradientTransform);
        using SKPaint paint = new()
        {
            Shader = shader,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        Surface.FillPath(pixmap, path, paint, false, Surface.RasterTransform(rasterScale), clip);
    }

    internal static (float X, float Y)? ResolveRadialGradientRadii(
        in Rect rect,
        SKPoint center,
        RadialGradientGeometry? geometry,
        float em,
        float rootFontSize,
        (float Width, float Height) viewport)
    {
        float left = MathF.Abs(center.X - rect.X);
        float right = MathF.Abs(rect.X + rect.Width - center.X);
        float top = MathF.Abs(center.Y - rect.Y);
        float bottom = MathF.Abs(rect.Y + rect.Height - center.Y);

        // Programmatically constructed legacy `Radial` values have no sidecar geometry.
        // Preserve their former circular farthest-corner behavior exactly.
        if (geometry is not { } shape)
        {
            float radius = 0f;
            foreach (float candidate in (float[])
                [Hypot(left, top), Hypot(right, top), Hypot(left, bottom), Hypot(right, bottom)])
            {
                radius = F32.Max(radius, candidate);
            }

            return radius > float.Epsilon ? (radius, radius) : null;
        }

        float radiusX;
        float radiusY;
        switch (shape.Size.Kind)
        {
            case RadialGradientSizeKind.ClosestSide:
                radiusX = F32.Min(left, right);
                radiusY = F32.Min(top, bottom);
                break;
            case RadialGradientSizeKind.FarthestSide:
                radiusX = F32.Max(left, right);
                radiusY = F32.Max(top, bottom);
                break;
            case RadialGradientSizeKind.ClosestCorner:
            {
                float sqrtTwo = MathF.Sqrt(2f);
                radiusX = F32.Min(left, right) * sqrtTwo;
                radiusY = F32.Min(top, bottom) * sqrtTwo;
                break;
            }

            case RadialGradientSizeKind.FarthestCorner:
            {
                float sqrtTwo = MathF.Sqrt(2f);
                radiusX = F32.Max(left, right) * sqrtTwo;
                radiusY = F32.Max(top, bottom) * sqrtTwo;
                break;
            }

            default:
            {
                float? x = ResolveRadialRadius(shape.Size.X, rect.Width, em, rootFontSize, viewport);
                float? y = ResolveRadialRadius(shape.Size.Y, rect.Height, em, rootFontSize, viewport);
                if (x is not { } rx || y is not { } ry)
                {
                    return null;
                }

                radiusX = rx;
                radiusY = ry;
                break;
            }
        }

        if (shape.Shape == RadialGradientShape.Circle
            && shape.Size.Kind != RadialGradientSizeKind.Explicit)
        {
            float radius = shape.Size.Kind switch
            {
                RadialGradientSizeKind.ClosestSide => F32.Min(radiusX, radiusY),
                RadialGradientSizeKind.FarthestSide => F32.Max(radiusX, radiusY),
                RadialGradientSizeKind.ClosestCorner => Hypot(F32.Min(left, right), F32.Min(top, bottom)),
                _ => Hypot(F32.Max(left, right), F32.Max(top, bottom)),
            };
            radiusX = radius;
            radiusY = radius;
        }

        return radiusX > float.Epsilon && radiusY > float.Epsilon ? (radiusX, radiusY) : null;

        static float Hypot(float a, float b) => MathF.Sqrt((a * a) + (b * b));
    }

    private static float? ResolveRadialRadius(
        Dimension radius,
        float percentageBasis,
        float em,
        float rootFontSize,
        (float Width, float Height) viewport)
    {
        Dimension resolved = radius.Resolve(
            em,
            rootFontSize,
            viewport.Width / 100f,
            viewport.Height / 100f);
        float? value = resolved.Kind switch
        {
            DimensionKind.Px => resolved.Value,
            DimensionKind.Percent => resolved.Value * percentageBasis,
            _ => null,
        };
        return value is { } result && float.IsFinite(result) && result >= 0f ? result : null;
    }

    internal static void PaintBackgroundGradientLayers(
        Pixmap pixmap,
        SKPath path,
        in Rect originRect,
        in Rect clipRect,
        ResolvedBorderRadii clipRadii,
        LayoutStyle style,
        float rootFontSize,
        (float Width, float Height) viewport,
        Mask? clip,
        float rasterScale)
    {
        List<BackgroundGradientLayer> layers = style.BackgroundGradientLayers;
        float em = style.FontSize ?? 16f;
        (float Width, float Height) tileSize =
            BackgroundGradientTileSize(style, originRect, em, rootFontSize, viewport);
        bool clipDiffersFromOrigin = MathF.Abs(clipRect.X - originRect.X) > 0.01f
            || MathF.Abs(clipRect.Y - originRect.Y) > 0.01f
            || MathF.Abs(clipRect.Width - originRect.Width) > 0.01f
            || MathF.Abs(clipRect.Height - originRect.Height) > 0.01f;
        bool needsTile = MathF.Abs(tileSize.Width - originRect.Width) > 0.01f
            || MathF.Abs(tileSize.Height - originRect.Height) > 0.01f

            // Even a default-sized image must be treated as a tile when the painting area
            // extends beyond its positioning area.
            || clipDiffersFromOrigin;
        if (needsTile && tileSize.Width > 0f && tileSize.Height > 0f)
        {
            uint width = (uint)Math.Clamp(MathF.Ceiling(tileSize.Width), 1f, 4096f);
            uint height = (uint)Math.Clamp(MathF.Ceiling(tileSize.Height), 1f, 4096f);
            Pixmap? tile = Pixmap.New(width, height);
            if (tile is not null)
            {
                using Pixmap ownedTile = tile;
                Rect tileRect = new(0f, 0f, width, height);
                using SKPathBuilder tileBuilder = new();
                tileBuilder.AddRect(new SKRect(0f, 0f, tileRect.Width, tileRect.Height));
                using SKPath tilePath = tileBuilder.Detach();
                PaintGradientLayerStack(
                    ownedTile,
                    tilePath,
                    tileRect,
                    tileRect,
                    default,
                    layers,
                    style.BackgroundGradientLayerRadialGeometries,
                    em,
                    rootFontSize,
                    viewport,
                    null,
                    1f);
                float tileX = originRect.X
                    + style.BackgroundPosition.X.Resolve(originRect.Width - tileSize.Width);
                float tileY = originRect.Y
                    + style.BackgroundPosition.Y.Resolve(originRect.Height - tileSize.Height);
                (bool X, bool Y) repeats = style.BackgroundRepeat ?? (true, true);
                if (repeats == (true, true))
                {
                    using SKImage image = ownedTile.Snapshot();
                    using SKShader shader = SKShader.CreateImage(
                        image,
                        SKShaderTileMode.Repeat,
                        SKShaderTileMode.Repeat,
                        new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
                        SKMatrix.CreateTranslation(tileX, tileY));
                    using SKPaint paint = new()
                    {
                        Shader = shader,
                        IsAntialias = false,
                        Style = SKPaintStyle.Fill,
                    };
                    Surface.FillPath(pixmap, path, paint, false, Affine2.Identity, clip);
                    return;
                }

                Mask? ownerClip = clip?.Clone();
                if (ownerClip is null)
                {
                    ownerClip = Mask.New(pixmap.Width, pixmap.Height);
                    ownerClip?.FillPath(path, evenOdd: false, antiAlias: true);
                }
                else
                {
                    ownerClip.IntersectPath(path, evenOdd: false, antiAlias: true);
                }

                float startX = repeats.X
                    ? tileX - (MathF.Ceiling((tileX - clipRect.X) / width) * width)
                    : tileX;
                float startY = repeats.Y
                    ? tileY - (MathF.Ceiling((tileY - clipRect.Y) / height) * height)
                    : tileY;
                float endX = repeats.X ? clipRect.X + clipRect.Width : tileX + 0.5f;
                float endY = repeats.Y ? clipRect.Y + clipRect.Height : tileY + 0.5f;
                float y = startY;
                while (y < endY)
                {
                    float x = startX;
                    while (x < endX)
                    {
                        Surface.DrawPixmap(
                            pixmap,
                            (int)MathF.Floor(x),
                            (int)MathF.Floor(y),
                            ownedTile,
                            1f,
                            false,
                            Affine2.Identity,
                            ownerClip);
                        if (!repeats.X)
                        {
                            break;
                        }

                        x += width;
                    }

                    if (!repeats.Y)
                    {
                        break;
                    }

                    y += height;
                }

                return;
            }
        }

        PaintGradientLayerStack(
            pixmap,
            path,
            originRect,
            clipRect,
            clipRadii,
            layers,
            style.BackgroundGradientLayerRadialGeometries,
            em,
            rootFontSize,
            viewport,
            clip,
            rasterScale);
    }

    private static void PaintGradientLayerStack(
        Pixmap pixmap,
        SKPath path,
        in Rect samplingRect,
        in Rect clipRect,
        ResolvedBorderRadii clipRadii,
        IReadOnlyList<BackgroundGradientLayer> layers,
        IReadOnlyList<RadialGradientGeometry?> radialGeometries,
        float em,
        float rootFontSize,
        (float Width, float Height) viewport,
        Mask? clip,
        float rasterScale)
    {
        // CSS lists the topmost background first. Paint back-to-front so every translucent
        // layer composites over the layers authored after it.
        for (int index = layers.Count - 1; index >= 0; index--)
        {
            switch (layers[index])
            {
                case BackgroundGradientLayer.Linear linear:
                    PaintLinearGradientLayer(
                        pixmap,
                        path,
                        samplingRect,
                        linear.Angle,
                        linear.Stops,
                        linear.StopPositions,
                        linear.Repeating,
                        em,
                        rootFontSize,
                        viewport,
                        clip,
                        rasterScale);
                    break;
                case BackgroundGradientLayer.Radial radial:
                    PaintRadialGradient(
                        pixmap,
                        path,
                        samplingRect,
                        radial.Center,
                        radial.Stops,
                        radial.StopPositions,
                        index < radialGeometries.Count ? radialGeometries[index] : null,
                        em,
                        rootFontSize,
                        viewport,
                        clip,
                        rasterScale);
                    break;
                case BackgroundGradientLayer.Conic conic:
                    PaintConicGradientSampled(
                        pixmap,
                        clipRect,
                        samplingRect,
                        clipRadii,
                        conic.Angle,
                        conic.Center,
                        conic.Stops,
                        clip);
                    break;
                default:
                    break;
            }
        }
    }

    internal static (float Width, float Height) BackgroundGradientTileSize(
        LayoutStyle style,
        in Rect rect,
        float em,
        float rem,
        (float Width, float Height) viewport)
    {
        if (style.BackgroundSizeFit is ObjectFit.Cover or ObjectFit.Contain)
        {
            return (rect.Width, rect.Height);
        }

        if (style.BackgroundSizeExpression is { } expression)
        {
            List<string> components = PaintImages.SplitBackgroundSizeComponents(expression);
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

            float width = components.Count > 0
                ? Resolve(components[0], rect.Width) ?? rect.Width
                : rect.Width;
            float height = components.Count > 1
                ? Resolve(components[1], rect.Height) ?? rect.Height
                : rect.Height;
            return (width, height);
        }

        return style.BackgroundSize ?? (rect.Width, rect.Height);
    }

    internal static void PaintConicGradientSampled(
        Pixmap pixmap,
        in Rect rect,
        in Rect samplingRect,
        ResolvedBorderRadii borderRadius,
        float angle,
        (float X, float Y) center,
        IReadOnlyList<GradientStop> stops,
        Mask? extraClip)
    {
        if (rect.Width <= 0f || rect.Height <= 0f || stops.Count < 2)
        {
            return;
        }

        uint width = (uint)MathF.Ceiling(rect.Width);
        uint height = (uint)MathF.Ceiling(rect.Height);
        Pixmap? layer = Pixmap.New(width, height);
        if (layer is null)
        {
            return;
        }

        using Pixmap owned = layer;
        List<(float Position, RgbaColor Color)> normalized = NormalizedStops(stops);
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                RgbaColor color = ConicColorAt(
                    samplingRect,
                    angle,
                    center,
                    normalized,
                    rect.X + x + 0.5f,
                    rect.Y + y + 0.5f);
                owned.Pixels[(int)((y * width) + x)] = PaintColor.Premultiplied(color);
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
            owned,
            1f,
            false,
            Affine2.Identity,
            clip);
    }

    internal static List<(float Position, RgbaColor Color)> NormalizedStops(
        IReadOnlyList<GradientStop> stops)
    {
        int count = stops.Count;
        List<(float, RgbaColor)> normalized = new(count);
        float last = 0f;
        for (int index = 0; index < count; index++)
        {
            RgbaColor color = GradientStopColor(stops, index);
            float authored = stops[index].Position
                ?? (count <= 1 ? 0f : (float)index / (count - 1));
            float position = F32.Max(Math.Clamp(authored, 0f, 1f), last);
            last = position;
            normalized.Add((position, color));
        }

        return normalized;
    }

    internal static RgbaColor GradientStopColor(IReadOnlyList<GradientStop> stops, int index)
    {
        RgbaColor color = stops[index].Color;
        if (color.A != 0)
        {
            return color;
        }

        for (int i = index + 1; i < stops.Count; i++)
        {
            if (stops[i].Color.A != 0)
            {
                RgbaColor neighbor = stops[i].Color;
                return new RgbaColor(neighbor.R, neighbor.G, neighbor.B, 0);
            }
        }

        for (int i = index - 1; i >= 0; i--)
        {
            if (stops[i].Color.A != 0)
            {
                RgbaColor neighbor = stops[i].Color;
                return new RgbaColor(neighbor.R, neighbor.G, neighbor.B, 0);
            }
        }

        return color;
    }

    internal static RgbaColor SampleNormalizedStops(
        IReadOnlyList<(float Position, RgbaColor Color)> stops,
        float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (stops.Count == 0)
        {
            return new RgbaColor(0, 0, 0, 0);
        }

        (float firstPosition, RgbaColor firstColor) = stops[0];
        if (t <= firstPosition)
        {
            return firstColor;
        }

        for (int index = 0; index + 1 < stops.Count; index++)
        {
            (float startPosition, RgbaColor startColor) = stops[index];
            (float endPosition, RgbaColor endColor) = stops[index + 1];
            if (t <= endPosition)
            {
                float span = endPosition - startPosition;
                float fraction = span <= float.Epsilon
                    ? 1f
                    : Math.Clamp((t - startPosition) / span, 0f, 1f);
                byte Interpolate(byte start, byte end) =>
                    (byte)Math.Clamp(F32.Round(start + ((end - start) * fraction)), 0f, 255f);

                return new RgbaColor(
                    Interpolate(startColor.R, endColor.R),
                    Interpolate(startColor.G, endColor.G),
                    Interpolate(startColor.B, endColor.B),
                    Interpolate(startColor.A, endColor.A));
            }
        }

        return stops[^1].Color;
    }

    internal static RgbaColor ConicColorAt(
        in Rect rect,
        float angle,
        (float X, float Y) center,
        IReadOnlyList<(float Position, RgbaColor Color)> stops,
        float x,
        float y)
    {
        float centerX = rect.X + (rect.Width * center.X);
        float centerY = rect.Y + (rect.Height * center.Y);
        float pointAngle = RemEuclid(
            MathF.Atan2(x - centerX, -(y - centerY)) * 180f / MathF.PI,
            360f);
        float position = RemEuclid(pointAngle - angle, 360f) / 360f;
        return SampleNormalizedStops(stops, position);
    }

    internal static RgbaColor LinearColorAt(
        in Rect rect,
        float angle,
        IReadOnlyList<(float Position, RgbaColor Color)> stops,
        float x,
        float y)
    {
        float radians = F32.ToRadians(angle);
        float dx = MathF.Sin(radians);
        float dy = -MathF.Cos(radians);
        float centerX = rect.X + (rect.Width / 2f);
        float centerY = rect.Y + (rect.Height / 2f);
        float half = ((MathF.Abs(dx) * rect.Width) + (MathF.Abs(dy) * rect.Height)) / 2f;
        if (half <= float.Epsilon)
        {
            return SampleNormalizedStops(stops, 0.5f);
        }

        float startX = centerX - (dx * half);
        float startY = centerY - (dy * half);
        float position = (((x - startX) * dx) + ((y - startY) * dy)) / (2f * half);
        return SampleNormalizedStops(stops, position);
    }

    internal static RgbaColor RadialColorAt(
        in Rect rect,
        (float X, float Y) center,
        IReadOnlyList<(float Position, RgbaColor Color)> stops,
        RadialGradientGeometry? geometry,
        float em,
        float rootFontSize,
        (float Width, float Height) viewport,
        float x,
        float y)
    {
        float centerX = rect.X + (rect.Width * center.X);
        float centerY = rect.Y + (rect.Height * center.Y);
        (float X, float Y)? radii = ResolveRadialGradientRadii(
            rect,
            new SKPoint(centerX, centerY),
            geometry,
            em,
            rootFontSize,
            viewport);
        float position = radii is { } r
            ? MathF.Sqrt(
                (((x - centerX) / r.X) * ((x - centerX) / r.X))
                + (((y - centerY) / r.Y) * ((y - centerY) / r.Y)))
            : 0f;
        return SampleNormalizedStops(stops, position);
    }

    private static float RemEuclid(float value, float modulus)
    {
        float remainder = value % modulus;
        return remainder < 0f ? remainder + MathF.Abs(modulus) : remainder;
    }
}
