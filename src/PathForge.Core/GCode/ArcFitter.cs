using PathForge.Core.Geometry;

namespace PathForge.Core.GCode;

/// <summary>A piece of a fitted polyline: straight to point <see cref="End"/> or an arc around <see cref="Center"/>.</summary>
public readonly record struct FittedMove(int End, Vec2? Center, bool Clockwise);

/// <summary>
/// Replaces runs of polyline points that lie on a common circle with circular arcs (G2/G3).
/// The fit is conservative: every point must be within <see cref="Tolerance"/> of the circle and
/// every chord's sagitta below <see cref="MaxSagitta"/>, so the arc never departs from the polyline
/// (four corners of a square, which do lie on a circle, are not turned into one).
/// </summary>
public static class ArcFitter
{
    /// <summary>Allowed distance of the polyline points from the fitted circle (mm). GRBL accepts 0.005.</summary>
    public const double Tolerance = 0.002;

    /// <summary>Allowed distance between a chord and the arc it is replaced by (mm).</summary>
    public const double MaxSagitta = 0.02;

    private const int MinSegments = 3;
    private const int MaxSegmentsPerArc = 4000;
    private const double MinRadius = 0.05;
    private const double MaxRadius = 5000;

    /// <summary>
    /// Splits the path <paramref name="points"/> (points[0] is the current position) into lines and arcs.
    /// Each result entry ends at points[End].
    /// </summary>
    public static List<FittedMove> Fit(IReadOnlyList<Vec2> points)
    {
        var moves = new List<FittedMove>();
        var i = 0;
        while (i < points.Count - 1)
        {
            FittedMove? best = null;
            for (var j = i + MinSegments; j < points.Count && j - i <= MaxSegmentsPerArc; j++)
            {
                if (!TryArc(points, i, j, out var center, out var clockwise))
                {
                    break;
                }

                best = new FittedMove(j, center, clockwise);
            }

            if (best is { } arc)
            {
                moves.Add(arc);
                i = arc.End;
            }
            else
            {
                moves.Add(new FittedMove(i + 1, null, false));
                i++;
            }
        }

        return moves;
    }

    private static bool TryArc(IReadOnlyList<Vec2> p, int from, int to, out Vec2 center, out bool clockwise)
    {
        clockwise = false;
        if (!TryCircle(p[from], p[(from + to) / 2], p[to], out center))
        {
            return false;
        }

        var radius = center.DistanceTo(p[from]);
        if (radius is < MinRadius or > MaxRadius)
        {
            return false;
        }

        double sweep = 0;
        var sign = 0;
        for (var k = from; k <= to; k++)
        {
            if (Math.Abs(center.DistanceTo(p[k]) - radius) > Tolerance)
            {
                return false;
            }

            if (k == to)
            {
                break;
            }

            var a = p[k] - center;
            var b = p[k + 1] - center;
            var step = Math.Atan2(a.Cross(b), a.Dot(b));
            var stepSign = Math.Sign(step);
            if (stepSign == 0 || (sign != 0 && stepSign != sign))
            {
                return false;
            }

            sign = stepSign;
            var chord = p[k].DistanceTo(p[k + 1]);
            var sagitta = radius - Math.Sqrt(Math.Max(0, radius * radius - chord * chord / 4));
            if (sagitta > MaxSagitta || Math.Abs(step) > Math.PI / 4)
            {
                return false;
            }

            sweep += Math.Abs(step);
        }

        // Full circles are left to lines: start = end makes the arc ambiguous for some controllers.
        if (sweep > 2 * Math.PI - 0.01)
        {
            return false;
        }

        clockwise = sign < 0;
        return true;
    }

    private static bool TryCircle(Vec2 a, Vec2 b, Vec2 c, out Vec2 center)
    {
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-12)
        {
            center = default;
            return false;
        }

        var a2 = a.LengthSquared;
        var b2 = b.LengthSquared;
        var c2 = c.LengthSquared;
        center = new Vec2(
            (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d,
            (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d);
        return true;
    }
}
