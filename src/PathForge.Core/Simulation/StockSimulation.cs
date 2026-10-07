using System.Diagnostics;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.Core.Simulation;

/// <summary>One straight tool move with its place on the machining time line (minutes).</summary>
public sealed record SimulationMove(Toolpath Toolpath, Vec3 From, Vec3 To, MoveKind Kind, double Power, double StartTime, double Duration)
{
    public double EndTime => StartTime + Duration;
}

/// <summary>
/// Material removal simulation on a height field. The tool shape (flat, ball, V, drill point) is stamped
/// along every move; the simulation can be advanced step by step for playback, with a time budget per
/// call so that the user interface stays responsive. Also reports rapid moves that hit material and
/// cuts below the stock.
/// </summary>
public sealed class StockSimulation
{
    /// <summary>Default upper limit of grid cells (memory and speed).</summary>
    public const int DefaultMaxCells = 1_500_000;

    private const int MaxIssues = 20;

    private readonly Dictionary<Tool, ToolStamp> _stamps = new();
    private readonly HashSet<Operation> _reportedBelowBottom = new();
    private readonly Dictionary<Operation, double> _deepestBelowBottom = new();
    private int _moveIndex;
    private int _samplesDone;
    private int _lastRapidHit = -1;

    public StockSimulation(CamProject project, GenerationResult generation, double? cellSize = null, int maxCells = DefaultMaxCells)
    {
        var top = project.Stock.ZShift;
        var bottom = top - project.Stock.Thickness;
        var start = new Vec3(0, 0, generation.SafeZ);
        var (moves, time) = TimeLine(generation.Toolpaths, start, project.Machine.RapidRate);
        Moves = moves;
        TotalTime = time;
        ToolPosition = start;

        // Stock area: the drawing plus everything the tools touch, with some margin.
        var area = Bounds2.Empty;
        var drawing = project.DrawingBounds();
        if (!drawing.IsEmpty)
        {
            area = area.Union(new Bounds2(drawing.MinX - generation.Origin.X, drawing.MinY - generation.Origin.Y,
                drawing.MaxX - generation.Origin.X, drawing.MaxY - generation.Origin.Y));
        }

        var maxRadius = 0.0;
        foreach (var move in moves.Where(m => m.Kind != MoveKind.Rapid && Math.Min(m.From.Z, m.To.Z) < top + 1e-6))
        {
            area = area.Union(Bounds2.Of(new[] { move.From.XY, move.To.XY }));
            maxRadius = Math.Max(maxRadius, move.Toolpath.Tool.Diameter / 2);
        }

        if (area.IsEmpty)
        {
            area = new Bounds2(0, 0, 10, 10);
        }

        var margin = maxRadius + 2;
        Field = CreateField(new Bounds2(area.MinX - margin, area.MinY - margin, area.MaxX + margin, area.MaxY + margin), cellSize, maxCells, top, bottom);
    }

    private StockSimulation(List<SimulationMove> moves, double totalTime, Vec3 start, HeightField field)
    {
        Moves = moves;
        TotalTime = totalTime;
        ToolPosition = start;
        Field = field;
    }

    /// <summary>
    /// Simulation of some toolpaths (in any coordinates) over a given area only, e.g. the stock left for a later
    /// operation by the ones before it.
    /// </summary>
    internal static StockSimulation ForArea(IReadOnlyList<Toolpath> toolpaths, Vec3 start, Bounds2 area, double cellSize, double top, double bottom,
        double rapidRate, int maxCells = DefaultMaxCells)
    {
        var (moves, time) = TimeLine(toolpaths, start, rapidRate);
        return new StockSimulation(moves, time, start, CreateField(area, cellSize, maxCells, top, bottom));
    }

    /// <summary>Every move with its place on the time line (minutes).</summary>
    private static (List<SimulationMove> Moves, double Time) TimeLine(IReadOnlyList<Toolpath> toolpaths, Vec3 start, double rapidRate)
    {
        var moves = new List<SimulationMove>();
        var position = start;
        var time = 0.0;
        foreach (var toolpath in toolpaths)
        {
            foreach (var move in toolpath.Moves)
            {
                var length = position.DistanceTo(move.Target);
                var rate = move.Kind switch
                {
                    MoveKind.Rapid => rapidRate,
                    MoveKind.Plunge => move.FeedOr(toolpath.Tool.PlungeRate),
                    _ => move.FeedOr(toolpath.Tool.FeedRate),
                };
                var duration = length / Math.Max(1, rate);
                moves.Add(new SimulationMove(toolpath, position, move.Target, move.Kind, move.Power, time, duration));
                time += duration;
                position = move.Target;
            }
        }

        return (moves, time);
    }

    private static HeightField CreateField(Bounds2 area, double? cellSize, int maxCells, double top, double bottom)
    {
        var sizeX = Math.Max(area.Width, 1e-3);
        var sizeY = Math.Max(area.Height, 1e-3);
        var cell = cellSize ?? Math.Clamp(Math.Sqrt(sizeX * sizeY / Math.Max(1000, maxCells)), 0.05, 2);
        var width = Math.Max(1, (int)Math.Ceiling(sizeX / cell));
        var height = Math.Max(1, (int)Math.Ceiling(sizeY / cell));
        if ((long)width * height > maxCells * 2L)
        {
            // An explicit cell size that is too fine for the area: fall back to the limit.
            cell = Math.Sqrt(sizeX * sizeY / maxCells);
            width = Math.Max(1, (int)Math.Ceiling(sizeX / cell));
            height = Math.Max(1, (int)Math.Ceiling(sizeY / cell));
        }

        return new HeightField(new Vec2(area.MinX, area.MinY), width, height, cell, top, bottom);
    }

    public HeightField Field { get; }

    public IReadOnlyList<SimulationMove> Moves { get; }

    /// <summary>Machining time of the whole program (minutes, without acceleration).</summary>
    public double TotalTime { get; }

    /// <summary>Time simulated so far (minutes).</summary>
    public double CurrentTime { get; private set; }

    public Vec3 ToolPosition { get; private set; }

    /// <summary>Toolpath of the move being simulated (null before the first move).</summary>
    public Toolpath? CurrentToolpath { get; private set; }

    /// <summary>Problems found so far: rapid moves into material, cuts below the stock.</summary>
    public List<string> Issues { get; } = new();

    public bool IsFinished => _moveIndex >= Moves.Count;

    /// <summary>Number of rapid moves that went through material.</summary>
    public int RapidHits { get; private set; }

    /// <summary>Back to the untouched stock.</summary>
    public void Reset()
    {
        Field.Reset();
        _moveIndex = 0;
        _samplesDone = 0;
        _lastRapidHit = -1;
        RapidHits = 0;
        CurrentTime = 0;
        CurrentToolpath = null;
        ToolPosition = Moves.Count > 0 ? Moves[0].From : ToolPosition;
        Issues.Clear();
        _reportedBelowBottom.Clear();
        _deepestBelowBottom.Clear();
    }

    /// <summary>Simulates everything (for tests and quick checks).</summary>
    public void RunToEnd() => AdvanceTo(double.PositiveInfinity, null);

    /// <summary>
    /// Continues the simulation up to <paramref name="time"/> (minutes); going back restarts from the
    /// beginning. Returns true when the time was reached, false when the budget ran out first.
    /// </summary>
    public bool AdvanceTo(double time, TimeSpan? budget)
    {
        if (time < CurrentTime - 1e-12)
        {
            Reset();
        }

        var watch = budget is null ? null : Stopwatch.StartNew();
        var stampsSinceCheck = 0;
        while (_moveIndex < Moves.Count)
        {
            var move = Moves[_moveIndex];
            CurrentToolpath = move.Toolpath;
            var samples = SampleCount(move);
            var target = move.EndTime <= time
                ? samples
                : (int)Math.Floor(samples * Math.Clamp((time - move.StartTime) / Math.Max(move.Duration, 1e-12), 0, 1));

            for (var k = _samplesDone + 1; k <= target; k++)
            {
                Stamp(move, k / (double)samples);
                if (++stampsSinceCheck >= 256 && watch is not null)
                {
                    stampsSinceCheck = 0;
                    if (watch.Elapsed > budget)
                    {
                        _samplesDone = k;
                        ToolPosition = Lerp(move.From, move.To, k / (double)samples);
                        CurrentTime = move.StartTime + move.Duration * k / samples;
                        return false;
                    }
                }
            }

            _samplesDone = Math.Max(_samplesDone, target);
            if (target < samples)
            {
                ToolPosition = Lerp(move.From, move.To, Math.Clamp((time - move.StartTime) / Math.Max(move.Duration, 1e-12), 0, 1));
                CurrentTime = time;
                return true;
            }

            OnMoveFinished(move);
            _moveIndex++;
            _samplesDone = 0;
            ToolPosition = move.To;
            CurrentTime = move.EndTime;
        }

        CurrentTime = Math.Min(time, TotalTime);
        return true;
    }

    /// <summary>
    /// Stamps per move: close enough that the scallops between neighbouring stamps of the cutting circle
    /// stay below a quarter cell, and at most 0.05 mm apart in Z.
    /// </summary>
    private int SampleCount(SimulationMove move)
    {
        var length = move.From.XY.DistanceTo(move.To.XY);
        var dz = Math.Abs(move.To.Z - move.From.Z);
        var cell = Field.CellSize;
        var spacing = cell * 0.7;
        var tool = move.Toolpath.Tool;
        // Pointed tools (V-bit, drill) would leave ridges in Z between wider stamps: keep them dense.
        if (tool.Kind is ToolKind.EndMill or ToolKind.BallNose)
        {
            var radius = ToolStamp.CuttingRadius(tool, Field.Top - Math.Min(move.From.Z, move.To.Z));
            spacing = Math.Clamp(Math.Sqrt(0.5 * radius * cell), cell * 0.7, Math.Max(cell * 0.7, radius));
        }

        return Math.Max(1, (int)Math.Ceiling(Math.Max(length / spacing, dz / 0.05)));
    }

    private void Stamp(SimulationMove move, double t)
    {
        var tool = move.Toolpath.Tool;
        var p = Lerp(move.From, move.To, t);
        if (tool.Kind != ToolKind.Laser && p.Z >= Field.Top)
        {
            // The tip is the lowest point of every tool: above the stock nothing can be cut.
            return;
        }

        var stamp = StampFor(tool);
        var ci = (int)Math.Floor((p.X - Field.Origin.X) / Field.CellSize);
        var cj = (int)Math.Floor((p.Y - Field.Origin.Y) / Field.CellSize);
        var heights = Field.Heights;
        var width = Field.Width;

        if (tool.Kind == ToolKind.Laser)
        {
            if (move.Kind == MoveKind.Rapid || double.IsNaN(move.Power) || move.Power <= 0)
            {
                return;
            }

            var burn = (byte)Math.Clamp(move.Power * 255, 1, 255);
            foreach (var (di, dj, _) in stamp.Cells)
            {
                var i = ci + di;
                var j = cj + dj;
                if (i >= 0 && j >= 0 && i < width && j < Field.Height && Field.Burn[j * width + i] < burn)
                {
                    Field.Burn[j * width + i] = burn;
                }
            }

            return;
        }

        var tip = (float)p.Z;
        var reach = (float)(Field.Top - p.Z);
        var hitMaterial = false;
        foreach (var (di, dj, dz) in stamp.Cells)
        {
            if (dz >= reach)
            {
                // Cells are sorted by height: the rest of the tool is above the stock.
                break;
            }

            var i = ci + di;
            var j = cj + dj;
            if (i < 0 || j < 0 || i >= width || j >= Field.Height)
            {
                continue;
            }

            var index = j * width + i;
            var z = tip + dz;
            if (z < heights[index])
            {
                if (heights[index] - z > 0.01f)
                {
                    hitMaterial = true;
                }

                heights[index] = z;
            }
        }

        if (hitMaterial && move.Kind == MoveKind.Rapid && _lastRapidHit != _moveIndex)
        {
            // Reported once per move.
            _lastRapidHit = _moveIndex;
            RapidHits++;
            AddIssue(Loc.T($"{Name(move)}: холостой ход (G0) врезается в материал около X{p.X:0.#} Y{p.Y:0.#} Z{p.Z:0.##}.", $"{Name(move)}: a rapid move (G0) cuts into the material near X{p.X:0.#} Y{p.Y:0.#} Z{p.Z:0.##}."));
        }

        if (p.Z < Field.Bottom - 0.01)
        {
            var operation = move.Toolpath.Operation;
            var depth = Field.Bottom - p.Z;
            if (!_deepestBelowBottom.TryGetValue(operation, out var deepest) || depth > deepest)
            {
                _deepestBelowBottom[operation] = depth;
            }
        }
    }

    private void OnMoveFinished(SimulationMove move)
    {
        var operation = move.Toolpath.Operation;
        var next = _moveIndex + 1 < Moves.Count ? Moves[_moveIndex + 1].Toolpath.Operation : null;
        if (next != operation && _deepestBelowBottom.TryGetValue(operation, out var depth) && _reportedBelowBottom.Add(operation))
        {
            AddIssue(Loc.T($"{Name(move)}: фреза выходит ниже заготовки на {depth:0.##} мм (в жертвенный стол).", $"{Name(move)}: the tool goes {depth:0.##} mm below the stock (into the spoilboard)."));
        }
    }

    private void AddIssue(string text)
    {
        if (Issues.Count < MaxIssues)
        {
            Issues.Add(text);
        }
    }

    private static string Name(SimulationMove move) =>
        string.IsNullOrWhiteSpace(move.Toolpath.Operation.Name) ? Loc.T("Операция", "Operation") : move.Toolpath.Operation.Name;

    private ToolStamp StampFor(Tool tool)
    {
        if (!_stamps.TryGetValue(tool, out var stamp))
        {
            stamp = ToolStamp.Create(tool, Field.CellSize);
            _stamps[tool] = stamp;
        }

        return stamp;
    }

    private static Vec3 Lerp(Vec3 a, Vec3 b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
}

/// <summary>Cells covered by a tool and the height of the cutting surface above the tip at each of them.</summary>
internal sealed class ToolStamp
{
    private ToolStamp(List<(int Di, int Dj, float Dz)> cells)
    {
        Cells = cells;
    }

    public List<(int Di, int Dj, float Dz)> Cells { get; }

    public static ToolStamp Create(Tool tool, double cellSize)
    {
        var radius = Math.Max(tool.Diameter / 2, cellSize * 0.5);
        var reach = (int)Math.Ceiling(radius / cellSize);
        var cells = new List<(int, int, float)>();
        for (var dj = -reach; dj <= reach; dj++)
        {
            for (var di = -reach; di <= reach; di++)
            {
                var r = Math.Sqrt(di * di + dj * dj) * cellSize;
                if (r > radius + 1e-9)
                {
                    continue;
                }

                cells.Add((di, dj, (float)Profile(tool, r)));
            }
        }

        cells.Sort((a, b) => a.Item3.CompareTo(b.Item3));
        return new ToolStamp(cells);
    }

    /// <summary>Radius of the circle the tool cuts at <paramref name="depth"/> below the stock top.</summary>
    public static double CuttingRadius(Tool tool, double depth)
    {
        var radius = Math.Max(tool.Diameter / 2, 1e-3);
        depth = Math.Max(0, depth);
        switch (tool.Kind)
        {
            case ToolKind.BallNose:
                return depth >= radius ? radius : Math.Sqrt(Math.Max(0, 2 * radius * depth - depth * depth));
            case ToolKind.VBit:
                var tan = Math.Tan(Math.Clamp(tool.TipAngle, 1, 179) * Math.PI / 360);
                return Math.Min(radius, tool.TipDiameter / 2 + depth * tan);
            case ToolKind.Drill:
                return Math.Min(radius, depth * Math.Tan(59 * Math.PI / 180));
            default:
                return radius;
        }
    }

    /// <summary>Height of the cutting edge above the tip at distance <paramref name="r"/> from the axis.</summary>
    public static double Profile(Tool tool, double r)
    {
        var radius = tool.Diameter / 2;
        switch (tool.Kind)
        {
            case ToolKind.BallNose:
                return radius - Math.Sqrt(Math.Max(0, radius * radius - r * r));
            case ToolKind.VBit:
                var tan = Math.Tan(Math.Clamp(tool.TipAngle, 1, 179) * Math.PI / 360);
                return Math.Max(0, r - tool.TipDiameter / 2) / tan;
            case ToolKind.Drill:
                // Standard 118° point.
                return r / Math.Tan(59 * Math.PI / 180);
            default:
                return 0;
        }
    }
}
