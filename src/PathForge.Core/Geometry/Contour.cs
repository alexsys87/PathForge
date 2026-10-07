using System.Text.Json.Serialization;

namespace PathForge.Core.Geometry;

/// <summary>Chain of connected segments imported from a drawing. Closed when the end meets the start.</summary>
public sealed class Contour
{
    /// <summary>Distance below which the end point is considered equal to the start point.</summary>
    public const double ClosingTolerance = 1e-3;

    /// <summary>Default chord tolerance used to approximate arcs with polylines.</summary>
    public const double DefaultFlattenTolerance = 0.01;

    public Contour()
    {
    }

    public Contour(int id, IEnumerable<Segment> segments, string layer = "")
    {
        Id = id;
        Segments = segments.ToList();
        Layer = layer;
    }

    public int Id { get; set; }

    public string Layer { get; set; } = "";

    /// <summary>Id of the text item the contour was generated from (null for drawing contours).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextId { get; set; }

    public List<Segment> Segments { get; set; } = new();

    [JsonIgnore]
    public Vec2 Start => Segments[0].Start;

    [JsonIgnore]
    public Vec2 End => Segments[^1].End;

    [JsonIgnore]
    public bool IsClosed => Segments.Count > 0 && Start.IsNear(End, ClosingTolerance);

    [JsonIgnore]
    public double Length => Segments.Sum(s => s.Length);

    /// <summary>The contour is exactly one full circle (typical drill hole).</summary>
    [JsonIgnore]
    public bool IsCircle => Segments.Count == 1 && Segments[0] is ArcSegment { IsFullCircle: true };

    /// <summary>Center and radius when the contour is made of arcs sharing one center that close a full circle.</summary>
    public bool TryGetCircle(out Vec2 center, out double radius)
    {
        center = default;
        radius = 0;
        if (!IsClosed || Segments.Count == 0 || Segments.Any(s => s is not ArcSegment))
        {
            return false;
        }

        var arcs = Segments.Cast<ArcSegment>().ToList();
        var first = arcs[0];
        var sameCircle = arcs.All(a => a.Center.IsNear(first.Center, 1e-6) && Math.Abs(a.Radius - first.Radius) < 1e-6);
        var totalSweep = Math.Abs(arcs.Sum(a => a.Sweep));
        if (!sameCircle || Math.Abs(totalSweep - 2 * Math.PI) > 1e-6)
        {
            return false;
        }

        center = first.Center;
        radius = first.Radius;
        return true;
    }

    /// <summary>
    /// Ends of the axis and width when the contour is a straight slot (oblong hole, e.g. an Excellon G85 slot):
    /// two parallel lines joined by two half circles of the same radius.
    /// </summary>
    public bool TryGetSlot(out Vec2 a, out Vec2 b, out double width)
    {
        a = b = default;
        width = 0;
        if (!IsClosed || Segments.Count != 4)
        {
            return false;
        }

        var arcs = Segments.OfType<ArcSegment>().ToList();
        if (arcs.Count != 2 || Segments.OfType<LineSegment>().Count() != 2 ||
            arcs.Any(arc => Math.Abs(Math.Abs(arc.Sweep) - Math.PI) > 1e-6) ||
            Math.Abs(arcs[0].Radius - arcs[1].Radius) > 1e-6 || arcs[0].Center.IsNear(arcs[1].Center, 1e-6) ||
            Enumerable.Range(0, 4).Any(i => Segments[i].GetType() == Segments[(i + 1) % 4].GetType()))
        {
            return false;
        }

        a = arcs[0].Center;
        b = arcs[1].Center;
        width = 2 * arcs[0].Radius;
        return true;
    }

    /// <summary>Polyline approximation. For closed contours the closing point is not repeated.</summary>
    public List<Vec2> Flatten(double tolerance = DefaultFlattenTolerance)
    {
        var points = new List<Vec2>();
        if (Segments.Count == 0)
        {
            return points;
        }

        points.Add(Segments[0].Start);
        foreach (var segment in Segments)
        {
            segment.AppendPoints(points, tolerance);
        }

        if (IsClosed && points.Count > 1)
        {
            points.RemoveAt(points.Count - 1);
        }

        return points;
    }

    public Bounds2 GetBounds() => Bounds2.Of(Flatten());

    /// <summary>Copy transformed by a similarity (move, rotate, uniform scale, mirror).</summary>
    public Contour Transformed(Affine2 transform) =>
        new(Id, Segments.Select(s => s.Transform(transform)), Layer) { TextId = TextId };
}
