using System.Globalization;
using PathForge.Core.Geometry;

namespace PathForge.Core.Leveling;

/// <summary>
/// Measuring a leveling map with a probe on GRBL: the commands to send and the collection of the touches.
/// The first point is probed from the current height and becomes Z0; the others are probed from a small
/// clearance down to at most <c>-maxDepth</c>.
/// </summary>
public sealed class LevelingProbe
{
    private readonly Bounds2 _area;
    private readonly List<double> _touches = new();

    public LevelingProbe(Bounds2 area, double step, double clearance = 1, double maxDepth = 1.5, double firstTravel = 10, double feed = 30)
    {
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0)
        {
            throw new ArgumentException("Пустая область карты высот.", nameof(area));
        }

        _area = area;
        Points = LevelingMap.PlanPoints(area, step, out var countX, out var countY);
        CountX = countX;
        CountY = countY;
        Clearance = Math.Max(0.2, clearance);
        MaxDepth = Math.Max(0.1, maxDepth);
        FirstTravel = Math.Max(1, firstTravel);
        Feed = Math.Max(1, feed);
    }

    public List<Vec2> Points { get; }

    public int CountX { get; }

    public int CountY { get; }

    public double Clearance { get; }

    public double MaxDepth { get; }

    public double FirstTravel { get; }

    public double Feed { get; }

    public int Measured => _touches.Count;

    public bool IsComplete => _touches.Count == Points.Count;

    /// <summary>GRBL commands for the whole measurement (sent one after the other).</summary>
    public List<string> Commands()
    {
        var commands = new List<string>();
        var first = Points[0];
        commands.Add($"G90G0X{F(first.X)}Y{F(first.Y)}");
        commands.Add($"G91G38.2Z-{F(FirstTravel)}F{F(Feed)}");
        commands.Add("G90");
        commands.Add("G10L20P1Z0");
        commands.Add($"G0Z{F(Clearance)}");
        foreach (var p in Points.Skip(1))
        {
            commands.Add($"G0X{F(p.X)}Y{F(p.Y)}");
            commands.Add($"G38.2Z-{F(MaxDepth)}F{F(Feed)}");
            commands.Add($"G0Z{F(Clearance)}");
        }

        commands.Add($"G0X{F(first.X)}Y{F(first.Y)}");
        return commands;
    }

    /// <summary>Records a touch (machine Z). Returns true when all points are measured.</summary>
    public bool AddTouch(Vec3 machinePosition)
    {
        if (!IsComplete)
        {
            _touches.Add(machinePosition.Z);
        }

        return IsComplete;
    }

    public LevelingMap ToMap(DateTime measured) => LevelingMap.FromProbes(_area, CountX, CountY, _touches, measured);

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
