using PathForge.Core.Geometry;

namespace PathForge.Core.Text;

public enum GlyphSegmentKind
{
    Line,
    Quadratic,
    Cubic,
}

/// <summary>Piece of a glyph outline ending at <see cref="End"/>; unused control points are ignored.</summary>
public readonly record struct GlyphSegment(GlyphSegmentKind Kind, Vec2 Control1, Vec2 Control2, Vec2 End);

/// <summary>Closed outline of a glyph made of lines, quadratic (TrueType) and cubic (CFF) Bézier pieces.</summary>
public sealed class GlyphPath
{
    private const int MaxCurveSegments = 64;

    public GlyphPath(Vec2 start)
    {
        Start = start;
    }

    public Vec2 Start { get; }

    public List<GlyphSegment> Segments { get; } = new();

    public Vec2 End => Segments.Count == 0 ? Start : Segments[^1].End;

    public void LineTo(Vec2 end) => Segments.Add(new GlyphSegment(GlyphSegmentKind.Line, end, end, end));

    public void QuadTo(Vec2 control, Vec2 end) => Segments.Add(new GlyphSegment(GlyphSegmentKind.Quadratic, control, control, end));

    public void CubicTo(Vec2 control1, Vec2 control2, Vec2 end) =>
        Segments.Add(new GlyphSegment(GlyphSegmentKind.Cubic, control1, control2, end));

    /// <summary>
    /// TrueType contour: consecutive off-curve points have an implied on-curve point half-way between them.
    /// </summary>
    public static GlyphPath FromQuadraticPoints(IReadOnlyList<(Vec2 P, bool OnCurve)> points)
    {
        var sequence = new List<(Vec2 P, bool On)>(points.Count + 2);
        var first = -1;
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].OnCurve)
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            sequence.Add(((points[^1].P + points[0].P) / 2, true));
            sequence.AddRange(points.Select(p => (p.P, false)));
        }
        else
        {
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[(first + i) % points.Count];
                sequence.Add((p.P, p.OnCurve));
            }
        }

        sequence.Add(sequence[0]);
        var path = new GlyphPath(sequence[0].P);
        var index = 1;
        while (index < sequence.Count)
        {
            if (sequence[index].On)
            {
                path.LineTo(sequence[index].P);
                index++;
                continue;
            }

            var control = sequence[index].P;
            var next = sequence[index + 1];
            if (next.On)
            {
                path.QuadTo(control, next.P);
                index += 2;
            }
            else
            {
                path.QuadTo(control, (control + next.P) / 2);
                index++;
            }
        }

        return path;
    }

    /// <summary>
    /// Polyline through the outline after mapping the points with <paramref name="map"/> (which must be affine,
    /// e.g. scale and move). Curves are split so that the chord error stays within the tolerance (Wang's formula).
    /// The closing point is not repeated.
    /// </summary>
    public List<Vec2> Flatten(Func<Vec2, Vec2> map, double tolerance)
    {
        tolerance = Math.Max(tolerance, 1e-4);
        var current = map(Start);
        var result = new List<Vec2> { current };
        foreach (var segment in Segments)
        {
            var end = map(segment.End);
            switch (segment.Kind)
            {
                case GlyphSegmentKind.Quadratic:
                {
                    var c = map(segment.Control1);
                    var count = Count((current - 2 * c + end).Length / (4 * tolerance));
                    for (var k = 1; k <= count; k++)
                    {
                        var t = (double)k / count;
                        var u = 1 - t;
                        result.Add(u * u * current + 2 * u * t * c + t * t * end);
                    }

                    break;
                }

                case GlyphSegmentKind.Cubic:
                {
                    var c1 = map(segment.Control1);
                    var c2 = map(segment.Control2);
                    var bend = Math.Max((current - 2 * c1 + c2).Length, (c1 - 2 * c2 + end).Length);
                    var count = Count(0.75 * bend / tolerance);
                    for (var k = 1; k <= count; k++)
                    {
                        var t = (double)k / count;
                        var u = 1 - t;
                        result.Add(u * u * u * current + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * end);
                    }

                    break;
                }

                default:
                    result.Add(end);
                    break;
            }

            current = end;
        }

        var clean = new List<Vec2>(result.Count);
        foreach (var p in result)
        {
            if (clean.Count == 0 || !clean[^1].IsNear(p, 1e-9))
            {
                clean.Add(p);
            }
        }

        if (clean.Count > 1 && clean[^1].IsNear(clean[0], 1e-9))
        {
            clean.RemoveAt(clean.Count - 1);
        }

        return clean;
    }

    private static int Count(double squared) => Math.Clamp((int)Math.Ceiling(Math.Sqrt(squared)), 1, MaxCurveSegments);
}
