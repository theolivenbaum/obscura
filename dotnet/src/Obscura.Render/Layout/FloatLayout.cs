// Port of vendor/taffy/src/compute/float.rs
//
// Computes the position of floats in a block formatting context. See the Rust
// file for the nine CSS 2.2 float placement rules this implements.
namespace Obscura.Render.Layout;

/// <summary>
/// An empty "slot" that avoids floats and is suitable for non-floated content to be laid out into.
/// </summary>
public struct ContentSlot
{
    /// <summary>The id of the segment that the slot starts in.</summary>
    public int? SegmentId;

    /// <summary>The x position of the start of the slot.</summary>
    public float X;

    /// <summary>The y position of the start of the slot.</summary>
    public float Y;

    /// <summary>The width of the slot.</summary>
    public float Width;

    /// <summary>The height of the slot.</summary>
    public float Height;
}

/// <summary>A floated box that has been placed.</summary>
public struct PlacedFloatedBox
{
    /// <summary>The width of the box.</summary>
    public float Width;

    /// <summary>The height of the box.</summary>
    public float Height;

    /// <summary>
    /// Horizontal distance from the edge of the container that the box is floated towards.
    /// </summary>
    public float XInset;

    /// <summary>Vertical distance from top edge of the container.</summary>
    public float Y;
}

/// <summary>A context for placing floated boxes.</summary>
public sealed class FloatContext
{
    /// <summary>A non-overlapping horizontal segment of the Block Formatting Context container.</summary>
    private struct Segment
    {
        /// <summary>The vertical start point of the segment.</summary>
        public float YStart;

        /// <summary>The vertical end point of the segment.</summary>
        public float YEnd;

        /// <summary>Left inset in slot 0, right inset in slot 1.</summary>
        public float Inset0;

        /// <summary>Right inset.</summary>
        public float Inset1;

        /// <summary>Whether the segment can fit the passed floated box in the horizontal axis.</summary>
        public readonly bool FitsFloatWidth(Size<float> floatedBox, FloatDirection direction, float bfcWidth)
        {
            float slotInset = direction == FloatDirection.Left ? Inset0 : Inset1;
            return slotInset == 0.0f || bfcWidth - floatedBox.Width - InsetSum() >= 0.0f;
        }

        /// <summary>The total space taken up by both insets.</summary>
        public readonly float InsetSum() => Inset0 + Inset1;

        /// <summary>Whether the segment's y range contains the value (start inclusive, end exclusive).</summary>
        public readonly bool ContainsY(float value) => value >= YStart && value < YEnd;
    }

    /// <summary>
    /// Helper for placing a single floated box: given a pinned starting y position, determines
    /// whether there is any x position with sufficient horizontal space across the box's height.
    /// </summary>
    private struct FloatFitter
    {
        private readonly float _bfcWidth;
        private double _slotHeight;

        /// <summary>The union of the left insets of the segments currently being considered.</summary>
        public float Inset0;

        /// <summary>The union of the right insets of the segments currently being considered.</summary>
        public float Inset1;

        public FloatFitter(float bfcWidth, float slotHeight, float inset0, float inset1)
        {
            _bfcWidth = bfcWidth;
            _slotHeight = slotHeight;
            Inset0 = inset0;
            Inset1 = inset1;
        }

        /// <summary>Union the insets of another segment. This is a "max" of the insets on each side.</summary>
        public void UnionInsets(float inset0, float inset1)
        {
            Inset0 = Sys.F32Max(Inset0, inset0);
            Inset1 = Sys.F32Max(Inset1, inset1);
        }

        /// <summary>Whether there is an x position such that the box fits horizontally.</summary>
        public readonly bool FitsHorizontally(float width) =>
            (Inset0 == 0.0f && Inset1 == 0.0f) || _bfcWidth - Inset0 - Inset1 - width >= 0.0f;

        /// <summary>Add the height of another segment.</summary>
        public void AddHeight(float height) => _slotHeight += height;

        /// <summary>Whether the box fits vertically in the accounted-for height.</summary>
        public readonly bool FitsVertically(float height) => _slotHeight >= height;

        /// <summary>The inset on the given side.</summary>
        public readonly float Inset(FloatDirection direction) =>
            direction == FloatDirection.Left ? Inset0 : Inset1;
    }

    private float _availableWidth;
    private bool _hasFloats;
    private readonly List<PlacedFloatedBox> _leftFloats = [];
    private readonly List<PlacedFloatedBox> _rightFloats = [];
    private readonly List<Segment> _segments = [];

    /// <summary>
    /// The lowest block-start reached by the last placed float. CSS source order forbids any later
    /// float from rising above this coordinate.
    /// </summary>
    private float? _lastFloatTop;

    /// <summary>The lowest block-end reached by a left float.</summary>
    private float? _lowestLeftFloatBottom;

    /// <summary>The lowest block-end reached by a right float.</summary>
    private float? _lowestRightFloatBottom;

    /// <summary>Whether the float context contains any floats.</summary>
    public bool HasFloats => _hasFloats;

    /// <summary>Whether the float context contains any floats that extend to or below min_y.</summary>
    public bool HasActiveFloats(float minY) =>
        _hasFloats && (_segments.Count > 0 ? _segments[^1].YEnd : 0.0f) > minY;

    /// <summary>Set the width of the float context.</summary>
    public void SetWidth(float availableWidth) => _availableWidth = availableWidth;

    /// <summary>Returns the placed left floats.</summary>
    public IReadOnlyList<PlacedFloatedBox> LeftFloats => _leftFloats;

    /// <summary>Returns the placed right floats.</summary>
    public IReadOnlyList<PlacedFloatedBox> RightFloats => _rightFloats;

    /// <summary>
    /// Divide a segment into two so that a new float can be placed with its vertical start and end
    /// at exact segment boundaries.
    /// </summary>
    internal void SubdivideSegment(int idx, float divideAtY)
    {
        var oldSegment = _segments[idx];
        var newSegment = new Segment
        {
            YStart = divideAtY,
            YEnd = oldSegment.YEnd,
            Inset0 = oldSegment.Inset0,
            Inset1 = oldSegment.Inset1,
        };

        if (!oldSegment.ContainsY(divideAtY) || oldSegment.YStart == divideAtY)
        {
            throw new InvalidOperationException(
                $"cannot subdivide segment [{oldSegment.YStart}, {oldSegment.YEnd}) at {divideAtY}");
        }

        oldSegment.YEnd = divideAtY;
        _segments[idx] = oldSegment;
        _segments.Insert(idx + 1, newSegment);
    }

    /// <summary>Position a floated box within the context, returning its (x, y) coordinates.</summary>
    public Point<float> PlaceFloatedBox(
        Size<float> floatedBox,
        float minY,
        float containingBlockInsetLeft,
        float containingBlockInsetRight,
        FloatDirection direction,
        Clear clear)
    {
        _hasFloats = true;

        var placedFloatedBox = PlaceFloatedBoxInner(
            floatedBox, minY, containingBlockInsetLeft, containingBlockInsetRight, direction, clear);

        float xInset = placedFloatedBox.XInset;
        float y = placedFloatedBox.Y;
        _lastFloatTop = _lastFloatTop.HasValue ? Sys.F32Max(_lastFloatTop.Value, y) : y;

        float bottom = y + placedFloatedBox.Height;
        if (direction == FloatDirection.Left)
        {
            _lowestLeftFloatBottom =
                _lowestLeftFloatBottom.HasValue ? Sys.F32Max(_lowestLeftFloatBottom.Value, bottom) : bottom;
            _leftFloats.Add(placedFloatedBox);
            return new Point<float>(xInset, y);
        }

        _lowestRightFloatBottom =
            _lowestRightFloatBottom.HasValue ? Sys.F32Max(_lowestRightFloatBottom.Value, bottom) : bottom;
        _rightFloats.Add(placedFloatedBox);
        return new Point<float>(_availableWidth - xInset - floatedBox.Width, y);
    }

    private PlacedFloatedBox PlaceFloatedBoxInner(
        Size<float> floatedBox,
        float minY,
        float containingBlockInsetLeft,
        float containingBlockInsetRight,
        FloatDirection direction,
        Clear clear)
    {
        float slotInset = direction == FloatDirection.Left ? containingBlockInsetLeft : containingBlockInsetRight;

        float? clearedThreshold = ClearedThreshold(clear);
        if (clearedThreshold.HasValue)
        {
            minY = Sys.F32Max(minY, clearedThreshold.Value);
        }

        if (_lastFloatTop.HasValue)
        {
            minY = Sys.F32Max(minY, _lastFloatTop.Value);
        }

        // Ensure the float is placed in a segment at or below "min_y".
        int startIdx = _segments.Count;
        for (int i = 0; i < _segments.Count; i++)
        {
            if (_segments[i].YEnd > minY)
            {
                startIdx = i;
                break;
            }
        }

        float startY = minY;
        int endIdx = startIdx;

        int? start = null;
        int? end = null;
        float placedInset = 0.0f;

        while (true)
        {
            // Start segment does not exist: create a new segment below all existing ones.
            if (startIdx >= _segments.Count)
            {
                start = null;
                end = null;
                placedInset = slotInset;
                break;
            }

            var startSegment = _segments[startIdx];

            // Candidate start segment doesn't have horizontal space: retry with the next segment.
            if (!startSegment.FitsFloatWidth(floatedBox, direction, _availableWidth))
            {
                startIdx += 1;
                endIdx = Math.Max(endIdx, startIdx);
                continue;
            }

            startY = Sys.F32Max(startY, startSegment.YStart);
            float availableHeight = startSegment.YEnd - startY;
            var fitter = new FloatFitter(
                _availableWidth, availableHeight, containingBlockInsetLeft, containingBlockInsetRight);
            fitter.UnionInsets(startSegment.Inset0, startSegment.Inset1);

            bool restartOuter = false;
            while (true)
            {
                if (endIdx >= _segments.Count)
                {
                    start = startIdx;
                    end = null;
                    placedInset = fitter.Inset(direction);
                    goto placed;
                }

                var endSegment = _segments[endIdx];

                fitter.UnionInsets(endSegment.Inset0, endSegment.Inset1);
                if (!fitter.FitsHorizontally(floatedBox.Width))
                {
                    startIdx += 1;
                    endIdx = Math.Max(endIdx, startIdx);
                    restartOuter = true;
                    break;
                }

                if (endIdx != startIdx)
                {
                    fitter.AddHeight(endSegment.YEnd - endSegment.YStart);
                }

                if (!fitter.FitsVertically(floatedBox.Height))
                {
                    endIdx += 1;
                    continue;
                }

                start = startIdx;
                end = endIdx;
                placedInset = fitter.Inset(direction);
                goto placed;
            }

            if (restartOuter)
            {
                continue;
            }
        }

    placed:

        // Short-circuit for zero-sized boxes
        if (floatedBox.Width == 0.0f || floatedBox.Height == 0.0f)
        {
            return new PlacedFloatedBox
            {
                Width = floatedBox.Width,
                Height = floatedBox.Height,
                Y = startY,
                XInset = placedInset,
            };
        }

        // Handle case where floated box is placed after all existing segments
        if (!start.HasValue)
        {
            float lastYEnd = _segments.Count > 0 ? _segments[^1].YEnd : 0.0f;
            if (startY > lastYEnd)
            {
                _segments.Add(new Segment { YStart = lastYEnd, YEnd = startY, Inset0 = 0.0f, Inset1 = 0.0f });
            }

            float newStartY = Sys.F32Max(lastYEnd, startY);

            float inset0 = containingBlockInsetLeft;
            float inset1 = containingBlockInsetRight;
            if (direction == FloatDirection.Left)
            {
                inset0 += floatedBox.Width;
            }
            else
            {
                inset1 += floatedBox.Width;
            }

            _segments.Add(new Segment
            {
                YStart = newStartY,
                YEnd = newStartY + floatedBox.Height,
                Inset0 = inset0,
                Inset1 = inset1,
            });

            return new PlacedFloatedBox
            {
                Width = floatedBox.Width,
                Height = floatedBox.Height,
                Y = newStartY,
                XInset = slotInset,
            };
        }

        int resolvedStartIdx = start.Value;

        // If the floated box doesn't start at the exact same y-offset as the segment it starts in,
        // subdivide that segment.
        if (startY != _segments[resolvedStartIdx].YStart)
        {
            SubdivideSegment(resolvedStartIdx, startY);
            resolvedStartIdx += 1;
            if (end.HasValue)
            {
                end = end.Value + 1;
            }
        }

        int resolvedEndIdx;
        if (!end.HasValue)
        {
            float lastYEnd = _segments.Count > 0 ? _segments[^1].YEnd : 0.0f;
            if (minY > lastYEnd)
            {
                _segments.Add(new Segment { YStart = lastYEnd, YEnd = minY, Inset0 = 0.0f, Inset1 = 0.0f });
            }

            resolvedEndIdx = _segments.Count - 1;
        }
        else
        {
            resolvedEndIdx = end.Value;
            float endY = startY + floatedBox.Height;
            if (endY != _segments[resolvedEndIdx].YEnd)
            {
                SubdivideSegment(resolvedEndIdx, endY);
            }
        }

        // Update inset for the range of segments that the float is placed in
        float placedInsetPlusWidth = placedInset + floatedBox.Width;
        for (int i = resolvedStartIdx; i <= resolvedEndIdx; i++)
        {
            var segment = _segments[i];
            if (direction == FloatDirection.Left)
            {
                segment.Inset0 = placedInsetPlusWidth;
            }
            else
            {
                segment.Inset1 = placedInsetPlusWidth;
            }

            _segments[i] = segment;
        }

        return new PlacedFloatedBox
        {
            Width = floatedBox.Width,
            Height = floatedBox.Height,
            Y = startY,
            XInset = placedInset,
        };
    }

    /// <summary>Get the bottom of the lowest relevant float for the specified clear property.</summary>
    public float? ClearedThreshold(Clear clear) => clear switch
    {
        Clear.Left => _lowestLeftFloatBottom,
        Clear.Right => _lowestRightFloatBottom,
        Clear.Both => _lowestLeftFloatBottom.HasValue && _lowestRightFloatBottom.HasValue
            ? Sys.F32Max(_lowestLeftFloatBottom.Value, _lowestRightFloatBottom.Value)
            : _lowestLeftFloatBottom ?? _lowestRightFloatBottom,
        _ => null,
    };

    /// <summary>Search for a space suitable for laying out non-floated content into.</summary>
    public ContentSlot FindContentSlot(
        float minY,
        float containingBlockInsetLeft,
        float containingBlockInsetRight,
        Clear clear,
        int? after)
    {
        float? threshold = ClearedThreshold(clear);
        if (threshold.HasValue)
        {
            minY = Sys.F32Max(minY, threshold.Value);
        }

        if (!HasActiveFloats(minY))
        {
            return new ContentSlot
            {
                SegmentId = null,
                X = containingBlockInsetLeft,
                Y = minY,
                Width = _availableWidth - containingBlockInsetLeft - containingBlockInsetRight,
                Height = float.PositiveInfinity,
            };
        }

        int atLeast = after.HasValue ? after.Value + 1 : 0;

        int startIdx = _segments.Count;
        if (atLeast <= _segments.Count)
        {
            for (int i = atLeast; i < _segments.Count; i++)
            {
                if (_segments[i].YEnd > minY)
                {
                    startIdx = i;
                    break;
                }
            }
        }

        if (startIdx < _segments.Count)
        {
            var segment = _segments[startIdx];
            float insetLeft = Sys.F32Max(segment.Inset0, containingBlockInsetLeft);
            float insetRight = Sys.F32Max(segment.Inset1, containingBlockInsetRight);
            return new ContentSlot
            {
                SegmentId = startIdx,
                X = insetLeft,
                Y = Sys.F32Max(segment.YStart, minY),
                Width = _availableWidth - insetLeft - insetRight,
                Height = float.PositiveInfinity,
            };
        }

        return new ContentSlot
        {
            SegmentId = null,
            X = containingBlockInsetLeft,
            Y = minY,
            Width = _availableWidth - containingBlockInsetLeft - containingBlockInsetRight,
            Height = float.PositiveInfinity,
        };
    }
}

/// <summary>Context for computing the intrinsic width contribution of a set of floats.</summary>
public sealed class FloatIntrinsicWidthCalculator(AvailableSpace availableWidth)
{
    private readonly AvailableSpace _availableWidth = availableWidth;
    private float _contribution;

    /// <summary>Add a float to the computation.</summary>
    public void AddFloat(float width, FloatDirection direction, Clear clear)
    {
        switch (_availableWidth.Kind)
        {
            case AvailableSpaceKind.Definite:
                // We will never hit this code path with definite available space.
                break;
            case AvailableSpaceKind.MinContent:
                _contribution = Sys.F32Max(_contribution, width);
                break;
            default:
                _contribution += width;
                break;
        }
    }

    /// <summary>Get the computed float contribution to intrinsic width.</summary>
    public float Result() => _contribution;
}
