using System.Text.Json.Serialization;

namespace PathForge.Core.Geometry;

/// <summary>Elementary 2D curve: straight line or circular arc.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LineSegment), "line")]
[JsonDerivedType(typeof(ArcSegment), "arc")]
public abstract record Segment
{
    [JsonIgnore]
    public abstract Vec2 Start { get; }

    [JsonIgnore]
    public abstract Vec2 End { get; }

    [JsonIgnore]
    public abstract double Length { get; }

    public abstract Segment Reversed();

    /// <summary>Appends points approximating the segment, excluding <see cref="Start"/>.</summary>
    public abstract void AppendPoints(List<Vec2> points, double tolerance);

    public abstract Segment Transform(Affine2 transform);
}

public sealed record LineSegment(Vec2 From, Vec2 To) : Segment
{
    [JsonIgnore]
    public override Vec2 Start => From;

    [JsonIgnore]
    public override Vec2 End => To;

    [JsonIgnore]
    public override double Length => From.DistanceTo(To);

    public override Segment Reversed() => new LineSegment(To, From);

    public override void AppendPoints(List<Vec2> points, double tolerance) => points.Add(To);

    public override Segment Transform(Affine2 transform) => new LineSegment(transform.Apply(From), transform.Apply(To));
}

/// <summary>
/// Circular arc. Angles in radians, <see cref="Sweep"/> is signed: positive = counter-clockwise.
/// A full circle has |Sweep| = 2π.
/// </summary>
public sealed record ArcSegment(Vec2 Center, double Radius, double StartAngle, double Sweep) : Segment
{
    public const int MaxPointsPerArc = 4096;

    [JsonIgnore]
    public override Vec2 Start => PointAt(StartAngle);

    [JsonIgnore]
    public override Vec2 End => PointAt(StartAngle + Sweep);

    [JsonIgnore]
    public override double Length => Math.Abs(Sweep) * Radius;

    [JsonIgnore]
    public bool IsFullCircle => Math.Abs(Math.Abs(Sweep) - 2 * Math.PI) < 1e-9;

    public Vec2 PointAt(double angle) => Center + Vec2.FromPolar(Radius, angle);

    public override Segment Reversed() => new ArcSegment(Center, Radius, StartAngle + Sweep, -Sweep);

    public override void AppendPoints(List<Vec2> points, double tolerance)
    {
        var count = SegmentCount(Radius, Sweep, tolerance);
        for (var i = 1; i <= count; i++)
        {
            points.Add(PointAt(StartAngle + Sweep * i / count));
        }
    }

    /// <summary>Number of chords needed so that the chord error stays below the tolerance.</summary>
    public static int SegmentCount(double radius, double sweep, double tolerance)
    {
        if (radius <= tolerance)
        {
            return Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2)));
        }

        var maxStep = 2 * Math.Acos(1 - tolerance / radius);
        var count = (int)Math.Ceiling(Math.Abs(sweep) / maxStep);
        return Math.Clamp(count, 1, MaxPointsPerArc);
    }

    public override Segment Transform(Affine2 transform)
    {
        if (!transform.IsSimilarity(out var scale))
        {
            // Non-uniform scale turns the arc into an ellipse: approximate with lines.
            throw new InvalidOperationException("Arc cannot be transformed by a non-uniform scale; flatten it first.");
        }

        var start = transform.Apply(Start);
        var center = transform.Apply(Center);
        var mirrored = transform.Determinant < 0;
        var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        return new ArcSegment(center, Radius * scale, startAngle, mirrored ? -Sweep : Sweep);
    }

    /// <summary>
    /// Creates an arc from a DXF bulge (tan of a quarter of the included angle) between two points.
    /// Returns a line when the bulge is (almost) zero.
    /// </summary>
    public static Segment FromBulge(Vec2 from, Vec2 to, double bulge)
    {
        var chord = to - from;
        var chordLength = chord.Length;
        if (Math.Abs(bulge) < 1e-9 || chordLength < 1e-12)
        {
            return new LineSegment(from, to);
        }

        var sweep = 4 * Math.Atan(bulge);
        // Signed sagitta and radius; the center lies on the chord's left normal for positive bulge.
        var sagitta = bulge * chordLength / 2;
        var signedRadius = (chordLength * chordLength / 4 + sagitta * sagitta) / (2 * sagitta);
        var mid = (from + to) / 2;
        var normal = chord.PerpendicularLeft / chordLength;
        var center = mid + normal * (signedRadius - sagitta);
        var startAngle = Math.Atan2(from.Y - center.Y, from.X - center.X);
        return new ArcSegment(center, Math.Abs(signedRadius), startAngle, sweep);
    }
}
