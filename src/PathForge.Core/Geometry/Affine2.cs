namespace PathForge.Core.Geometry;

/// <summary>2D affine transform: x' = M11*x + M12*y + Tx, y' = M21*x + M22*y + Ty.</summary>
public readonly record struct Affine2(double M11, double M12, double M21, double M22, double Tx, double Ty)
{
    public static readonly Affine2 Identity = new(1, 0, 0, 1, 0, 0);

    public double Determinant => M11 * M22 - M12 * M21;

    public Vec2 Apply(Vec2 p) => new(M11 * p.X + M12 * p.Y + Tx, M21 * p.X + M22 * p.Y + Ty);

    public static Affine2 Translation(double dx, double dy) => new(1, 0, 0, 1, dx, dy);

    public static Affine2 Scaling(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public static Affine2 Rotation(double angle)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        return new Affine2(c, -s, s, c, 0, 0);
    }

    /// <summary>Returns the transform that applies <paramref name="first"/> and then <paramref name="second"/>.</summary>
    public static Affine2 Then(Affine2 first, Affine2 second) => new(
        second.M11 * first.M11 + second.M12 * first.M21,
        second.M11 * first.M12 + second.M12 * first.M22,
        second.M21 * first.M11 + second.M22 * first.M21,
        second.M21 * first.M12 + second.M22 * first.M22,
        second.M11 * first.Tx + second.M12 * first.Ty + second.Tx,
        second.M21 * first.Tx + second.M22 * first.Ty + second.Ty);

    /// <summary>True when the transform preserves circles (rotation, uniform scale, mirror, translation).</summary>
    public bool IsSimilarity(out double scale)
    {
        var sx = Math.Sqrt(M11 * M11 + M21 * M21);
        var sy = Math.Sqrt(M12 * M12 + M22 * M22);
        scale = sx;
        var orthogonal = Math.Abs(M11 * M12 + M21 * M22) < 1e-9 * Math.Max(1, sx * sy);
        return orthogonal && Math.Abs(sx - sy) < 1e-9 * Math.Max(1, sx);
    }
}
