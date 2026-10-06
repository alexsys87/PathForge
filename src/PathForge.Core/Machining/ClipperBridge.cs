using Clipper2Lib;
using PathForge.Core.Geometry;

namespace PathForge.Core.Machining;

/// <summary>Polygon offsetting and boolean operations via Clipper2 on an integer grid of 0.1 µm.</summary>
internal static class ClipperBridge
{
    private const double Scale = 10000;

    /// <summary>Maximum deviation of rounded corners from the true arc (mm).</summary>
    private const double ArcTolerance = 0.005;

    public static Paths64 ToPaths(IEnumerable<IReadOnlyList<Vec2>> rings)
    {
        var paths = new Paths64();
        foreach (var ring in rings)
        {
            var path = new Path64(ring.Count);
            foreach (var p in ring)
            {
                path.Add(new Point64((long)Math.Round(p.X * Scale), (long)Math.Round(p.Y * Scale)));
            }

            paths.Add(path);
        }

        return paths;
    }

    public static List<List<Vec2>> FromPaths(Paths64 paths)
    {
        var result = new List<List<Vec2>>(paths.Count);
        foreach (var path in paths)
        {
            if (path.Count < 3)
            {
                continue;
            }

            result.Add(path.Select(p => new Vec2(p.X / Scale, p.Y / Scale)).ToList());
        }

        return result;
    }

    /// <summary>Region covered by the rings with the even-odd rule (nested rings become holes).</summary>
    public static Paths64 EvenOddRegion(IEnumerable<IReadOnlyList<Vec2>> rings) =>
        Clipper.Union(ToPaths(rings), FillRule.EvenOdd);

    /// <summary>Union of polygons with the non-zero rule.</summary>
    public static Paths64 Union(Paths64 subject, Paths64 clip) => Clipper.Union(subject, clip, FillRule.NonZero);

    public static Paths64 Union(Paths64 subject) => Clipper.Union(subject, FillRule.NonZero);

    public static Paths64 Difference(Paths64 subject, Paths64 clip) => Clipper.Difference(subject, clip, FillRule.NonZero);

    /// <summary>Area swept by a pen of radius <paramref name="radius"/> along an open polyline (round or square ends).</summary>
    public static Paths64 Stroke(IReadOnlyList<Vec2> polyline, double radius, bool squareEnds = false) =>
        Clipper.InflatePaths(ToPaths(new[] { polyline }), radius * Scale, JoinType.Round,
            squareEnds ? EndType.Square : EndType.Round, 2, ArcTolerance * Scale);

    /// <summary>Parts of open polylines that lie inside the region (even-odd).</summary>
    public static List<List<Vec2>> ClipLines(IEnumerable<IReadOnlyList<Vec2>> lines, Paths64 region)
    {
        var clipper = new Clipper64();
        clipper.AddOpenSubject(ToPaths(lines));
        clipper.AddClip(region);
        var closed = new Paths64();
        var open = new Paths64();
        clipper.Execute(ClipType.Intersection, FillRule.EvenOdd, closed, open);
        return open.Where(p => p.Count >= 2).Select(p => p.Select(q => new Vec2(q.X / Scale, q.Y / Scale)).ToList()).ToList();
    }

    /// <summary>Parts of open polylines that lie outside the region (even-odd).</summary>
    public static List<List<Vec2>> ClipLinesOutside(IEnumerable<IReadOnlyList<Vec2>> lines, Paths64 region)
    {
        var clipper = new Clipper64();
        clipper.AddOpenSubject(ToPaths(lines));
        clipper.AddClip(region);
        var closed = new Paths64();
        var open = new Paths64();
        clipper.Execute(ClipType.Difference, FillRule.EvenOdd, closed, open);
        return open.Where(p => p.Count >= 2).Select(p => p.Select(q => new Vec2(q.X / Scale, q.Y / Scale)).ToList()).ToList();
    }

    /// <summary>Moves a region by an offset in millimetres.</summary>
    public static Paths64 Translate(Paths64 region, Vec2 offset) =>
        Clipper.TranslatePaths(region, (long)Math.Round(offset.X * Scale), (long)Math.Round(offset.Y * Scale));

    /// <summary>Grows (positive delta) or shrinks (negative delta) a region with round corners.</summary>
    public static Paths64 Offset(Paths64 region, double delta) =>
        Clipper.InflatePaths(region, delta * Scale, JoinType.Round, EndType.Polygon, 2, ArcTolerance * Scale);

    /// <summary>Offset with a coarser approximation of the rounded corners (fewer points, faster to compare).</summary>
    public static Paths64 Offset(Paths64 region, double delta, double arcTolerance) =>
        Clipper.InflatePaths(region, delta * Scale, JoinType.Round, EndType.Polygon, 2, Math.Max(ArcTolerance, arcTolerance) * Scale);
}
