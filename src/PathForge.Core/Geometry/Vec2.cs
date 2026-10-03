using System.Text.Json.Serialization;

namespace PathForge.Core.Geometry;

/// <summary>2D point / vector in millimetres.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = new(0, 0);

    [JsonIgnore]
    public double Length => Math.Sqrt(X * X + Y * Y);

    [JsonIgnore]
    public double LengthSquared => X * X + Y * Y;

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);

    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);

    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);

    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);

    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);

    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);

    public double Dot(Vec2 other) => X * other.X + Y * other.Y;

    /// <summary>Z component of the 3D cross product.</summary>
    public double Cross(Vec2 other) => X * other.Y - Y * other.X;

    public double DistanceTo(Vec2 other) => (this - other).Length;

    /// <summary>Vector rotated by +90 degrees (left normal).</summary>
    [JsonIgnore]
    public Vec2 PerpendicularLeft => new(-Y, X);

    public Vec2 Normalized()
    {
        var len = Length;
        return len < 1e-12 ? Zero : this / len;
    }

    public static Vec2 FromPolar(double radius, double angle) =>
        new(radius * Math.Cos(angle), radius * Math.Sin(angle));

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => a + (b - a) * t;

    public bool IsNear(Vec2 other, double tolerance) => (this - other).LengthSquared <= tolerance * tolerance;

    public override string ToString() => FormattableString.Invariant($"({X:0.###}; {Y:0.###})");
}

/// <summary>3D point in millimetres (machine coordinates).</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    [JsonIgnore]
    public Vec2 XY => new(X, Y);

    public Vec3(Vec2 xy, double z) : this(xy.X, xy.Y, z)
    {
    }

    public double DistanceTo(Vec3 other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        var dz = Z - other.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public override string ToString() => FormattableString.Invariant($"({X:0.###}; {Y:0.###}; {Z:0.###})");
}
