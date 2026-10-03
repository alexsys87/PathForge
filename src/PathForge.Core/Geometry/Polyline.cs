namespace PathForge.Core.Geometry;

/// <summary>Helpers for polylines stored as point lists.</summary>
public static class Polyline
{
    /// <summary>Signed area (shoelace). Positive = counter-clockwise.</summary>
    public static double SignedArea(IReadOnlyList<Vec2> ring)
    {
        double area = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            area += ring[i].Cross(ring[(i + 1) % ring.Count]);
        }

        return area / 2;
    }

    public static bool IsCounterClockwise(IReadOnlyList<Vec2> ring) => SignedArea(ring) > 0;

    /// <summary>Even-odd point in polygon test.</summary>
    public static bool Contains(IReadOnlyList<Vec2> ring, Vec2 p)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) &&
                p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    public static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared;
        if (lengthSquared < 1e-24)
        {
            return p.DistanceTo(a);
        }

        var t = Math.Clamp((p - a).Dot(ab) / lengthSquared, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    public static double DistanceTo(IReadOnlyList<Vec2> points, Vec2 p, bool closed)
    {
        if (points.Count == 0)
        {
            return double.PositiveInfinity;
        }

        if (points.Count == 1)
        {
            return p.DistanceTo(points[0]);
        }

        var best = double.PositiveInfinity;
        var count = closed ? points.Count : points.Count - 1;
        for (var i = 0; i < count; i++)
        {
            best = Math.Min(best, DistanceToSegment(p, points[i], points[(i + 1) % points.Count]));
        }

        return best;
    }

    public static double Length(IReadOnlyList<Vec2> points, bool closed)
    {
        double length = 0;
        for (var i = 1; i < points.Count; i++)
        {
            length += points[i - 1].DistanceTo(points[i]);
        }

        if (closed && points.Count > 1)
        {
            length += points[^1].DistanceTo(points[0]);
        }

        return length;
    }

    /// <summary>Rotates a closed ring so that it starts at the vertex nearest to <paramref name="p"/>.</summary>
    public static List<Vec2> RotateToNearest(IReadOnlyList<Vec2> ring, Vec2 p)
    {
        var bestIndex = 0;
        var bestDistance = double.PositiveInfinity;
        for (var i = 0; i < ring.Count; i++)
        {
            var d = (ring[i] - p).LengthSquared;
            if (d < bestDistance)
            {
                bestDistance = d;
                bestIndex = i;
            }
        }

        var result = new List<Vec2>(ring.Count);
        for (var i = 0; i < ring.Count; i++)
        {
            result.Add(ring[(bestIndex + i) % ring.Count]);
        }

        return result;
    }
}
