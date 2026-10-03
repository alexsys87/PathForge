using System.Text.Json.Serialization;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Leveling;

/// <summary>
/// Measured surface of a board (or any stock) on a regular grid, in work coordinates. Heights are relative
/// to the first point (lower-left corner), where Z0 is set during probing.
/// </summary>
public sealed class LevelingMap
{
    public double X0 { get; set; }

    public double Y0 { get; set; }

    public double StepX { get; set; }

    public double StepY { get; set; }

    public int CountX { get; set; }

    public int CountY { get; set; }

    /// <summary>Row-major heights, row 0 at the lowest Y.</summary>
    public double[] Heights { get; set; } = Array.Empty<double>();

    public DateTime Measured { get; set; }

    [JsonIgnore]
    public double X1 => X0 + StepX * (CountX - 1);

    [JsonIgnore]
    public double Y1 => Y0 + StepY * (CountY - 1);

    [JsonIgnore]
    public bool IsValid => CountX >= 2 && CountY >= 2 && Heights.Length == CountX * CountY;

    [JsonIgnore]
    public double Min => Heights.Length == 0 ? 0 : Heights.Min();

    [JsonIgnore]
    public double Max => Heights.Length == 0 ? 0 : Heights.Max();

    public double this[int i, int j] => Heights[j * CountX + i];

    /// <summary>Probe points in measuring order: rows back and forth (shortest travel), starting at the lower-left corner.</summary>
    public static List<Vec2> PlanPoints(Bounds2 area, double step, out int countX, out int countY)
    {
        step = Math.Max(0.5, step);
        countX = Math.Max(2, (int)Math.Ceiling(area.Width / step - 1e-9) + 1);
        countY = Math.Max(2, (int)Math.Ceiling(area.Height / step - 1e-9) + 1);
        var points = new List<Vec2>(countX * countY);
        for (var j = 0; j < countY; j++)
        {
            for (var k = 0; k < countX; k++)
            {
                var i = j % 2 == 0 ? k : countX - 1 - k;
                points.Add(new Vec2(
                    area.MinX + area.Width * i / (countX - 1),
                    area.MinY + area.Height * j / (countY - 1)));
            }
        }

        return points;
    }

    /// <summary>Map from probe results given in measuring order (see <see cref="PlanPoints"/>), heights relative to the first one.</summary>
    public static LevelingMap FromProbes(Bounds2 area, int countX, int countY, IReadOnlyList<double> probeZ, DateTime measured)
    {
        if (probeZ.Count != countX * countY)
        {
            throw new ArgumentException(Loc.T("Число измерений не совпадает с сеткой.", "The number of measurements does not match the grid."), nameof(probeZ));
        }

        var map = new LevelingMap
        {
            X0 = area.MinX,
            Y0 = area.MinY,
            StepX = area.Width / (countX - 1),
            StepY = area.Height / (countY - 1),
            CountX = countX,
            CountY = countY,
            Heights = new double[countX * countY],
            Measured = measured,
        };
        var n = 0;
        for (var j = 0; j < countY; j++)
        {
            for (var k = 0; k < countX; k++)
            {
                var i = j % 2 == 0 ? k : countX - 1 - k;
                map.Heights[j * countX + i] = probeZ[n++] - probeZ[0];
            }
        }

        return map;
    }

    /// <summary>Bilinear height at a point; outside the grid the nearest edge value is used.</summary>
    public double HeightAt(double x, double y)
    {
        var u = StepX <= 0 ? 0 : Math.Clamp((x - X0) / StepX, 0, CountX - 1);
        var v = StepY <= 0 ? 0 : Math.Clamp((y - Y0) / StepY, 0, CountY - 1);
        var i = Math.Min((int)Math.Floor(u), CountX - 2);
        var j = Math.Min((int)Math.Floor(v), CountY - 2);
        var fu = u - i;
        var fv = v - j;
        var bottom = this[i, j] * (1 - fu) + this[i + 1, j] * fu;
        var top = this[i, j + 1] * (1 - fu) + this[i + 1, j + 1] * fu;
        return bottom * (1 - fv) + top * fv;
    }

    /// <summary>Distance of a point outside the measured area (0 inside).</summary>
    public double DistanceOutside(double x, double y)
    {
        var dx = Math.Max(0, Math.Max(X0 - x, x - X1));
        var dy = Math.Max(0, Math.Max(Y0 - y, y - Y1));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
