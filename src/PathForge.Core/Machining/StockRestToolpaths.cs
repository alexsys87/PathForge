using Clipper2Lib;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;
using PathForge.Core.Simulation;

namespace PathForge.Core.Machining;

/// <summary>
/// Rest machining of pockets and profiles by simulation: the earlier toolpaths are run over the stock, and the
/// operation cuts only where material is still left above each pass.
/// </summary>
public static partial class ToolpathGenerator
{
    private const int MaxRestCells = 1_000_000;

    /// <summary>Longest step between the points tested along a profile path (mm).</summary>
    private const double RestSampleStep = 0.25;

    /// <summary>
    /// The stock the earlier milling toolpaths (still in drawing coordinates) leave over <paramref name="area"/>,
    /// or null when there are none.
    /// </summary>
    private static HeightField? MilledStock(Projects.CamProject project, IReadOnlyList<Toolpath> earlier, Bounds2 area, double cellSize, int maxCells = StockSimulation.DefaultMaxCells)
    {
        var milled = earlier.Where(t => t.Tool.Kind != ToolKind.Laser).ToList();
        if (milled.Count == 0)
        {
            return null;
        }

        // Z0 is the stock top in drawing coordinates (the zero shift is applied to the finished toolpaths).
        var simulation = StockSimulation.ForArea(milled, new Vec3(0, 0, project.Machine.SafeZ), area, cellSize, 0,
            -project.Stock.Thickness, project.Machine.RapidRate, maxCells);
        simulation.RunToEnd();
        return simulation.Field;
    }

    /// <summary>
    /// Stock for a pocket or profile with rest machining by simulation, over the selected contours and the tool
    /// around them; null (with a warning) when no milling comes before it — the operation is then cut in full.
    /// </summary>
    private static HeightField? RestStockFor(Projects.CamProject project, IReadOnlyList<Toolpath> earlier, List<Contour> contours, Tool tool, Operation operation, List<string> warnings, string label)
    {
        var allowance = operation switch
        {
            PocketOperation pocket => pocket.Allowance,
            ProfileOperation profile => profile.Allowance,
            _ => 0,
        };
        var bounds = contours.Aggregate(Bounds2.Empty, (b, c) => b.Union(c.GetBounds()));
        var margin = tool.Radius + Math.Abs(allowance) + 1;
        var area = new Bounds2(bounds.MinX - margin, bounds.MinY - margin, bounds.MaxX + margin, bounds.MaxY + margin);
        var stock = bounds.IsEmpty ? null : MilledStock(project, earlier, area, Math.Clamp(tool.Radius / 5, 0.02, 0.2), MaxRestCells);
        if (stock is null)
        {
            warnings.Add(Loc.T(
                $"{label}: дообработка по симуляции — выше в списке нет фрезерных операций, обработано полностью.",
                $"{label}: rest machining by simulation — no milling operations above it in the list, machined in full."));
        }

        return stock;
    }

    /// <summary>
    /// Material the stock still has above <paramref name="z"/> (by more than <paramref name="tolerance"/>) inside
    /// <paramref name="target"/>. The edge of the target is where the operation stops (the wall of a pocket, the
    /// part beside a profile), so the cells along it are not counted as material left; nor are specks and slivers
    /// narrower than a cell — rounding of the simulation.
    /// </summary>
    private static Paths64 RestRegion(HeightField stock, Paths64 target, double z, double tolerance)
    {
        var cell = stock.CellSize;
        var inner = ClipperBridge.Offset(target, -(0.6 * cell + 0.01));
        var box = ClipperBridge.Bounds(inner);
        if (box.IsEmpty)
        {
            return new Paths64();
        }

        var i0 = Math.Max(0, (int)Math.Floor((box.MinX - stock.Origin.X) / cell));
        var i1 = Math.Min(stock.Width - 1, (int)Math.Ceiling((box.MaxX - stock.Origin.X) / cell));
        var j0 = Math.Max(0, (int)Math.Floor((box.MinY - stock.Origin.Y) / cell));
        var j1 = Math.Min(stock.Height - 1, (int)Math.Ceiling((box.MaxY - stock.Origin.Y) / cell));
        var level = z + tolerance;
        var runs = new List<IReadOnlyList<Vec2>>();
        for (var j = j0; j <= j1; j++)
        {
            var y0 = stock.Origin.Y + j * cell;
            var start = -1;
            for (var i = i0; i <= i1 + 1; i++)
            {
                var material = i <= i1 && stock[i, j] > level;
                if (material && start < 0)
                {
                    start = i;
                }
                else if (!material && start >= 0)
                {
                    // One rectangle per run of cells with material in a row.
                    var x0 = stock.Origin.X + start * cell;
                    var x1 = stock.Origin.X + i * cell;
                    runs.Add(new[] { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y0 + cell), new Vec2(x0, y0 + cell) });
                    start = -1;
                }
            }
        }

        if (runs.Count == 0)
        {
            return new Paths64();
        }

        var rest = ClipperBridge.Intersect(ClipperBridge.Union(ClipperBridge.ToPaths(runs)), inner);
        return ClipperBridge.Offset(ClipperBridge.Offset(rest, -cell / 2), cell / 2);
    }

    /// <summary>
    /// Pocket rest machining by simulation: the usual rings of this tool, at every pass cut only where they come
    /// within reach of the material still left above the pass.
    /// </summary>
    private static void GenerateStockRestPocket(PocketOperation operation, Paths64 region, HeightField stock, OperationContext context)
    {
        var tool = context.Tool;
        var stepOver = tool.StepOver;
        if (stepOver <= 0 || stepOver > tool.Diameter)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: перекрытие должно быть от 1 до 100 % диаметра.", $"{context.Label}: the step-over must be 1 to 100 % of the diameter."));
            return;
        }

        var area = ClipperBridge.Offset(region, -(tool.Radius + operation.Allowance));
        if (area.Count == 0)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: фреза не помещается в карман.", $"{context.Label}: the tool does not fit into the pocket."));
            return;
        }

        // Rings from the centre outwards; the wall ring comes last.
        var loops = new List<List<Vec2>>();
        for (var k = 0; k < 10000; k++)
        {
            var rings = ClipperBridge.FromPaths(ClipperBridge.Offset(region, -(tool.Radius + operation.Allowance + k * stepOver)));
            if (rings.Count == 0)
            {
                break;
            }

            loops.InsertRange(0, rings.Select(ring =>
            {
                var oriented = operation.Direction == CutDirection.Conventional ? Enumerable.Reverse(ring).ToList() : ring;
                return new List<Vec2>(oriented) { oriented[0] };
            }));
        }

        // What this tool can reach: corners narrower than it stay as they are, whatever the operations above left there.
        var target = ClipperBridge.Offset(area, tool.Radius);
        var tolerance = Math.Max(0.005, operation.RestTolerance);
        var writer = context.Writer;
        var levels = PassDepths(operation.StartZ, operation.Depth, tool.StepDown);
        var restFound = false;
        var cut = false;
        for (var i = 0; i < levels.Count; i++)
        {
            var z = levels[i];
            var rest = RestRegion(stock, target, z, tolerance);
            if (ClipperBridge.Area(rest) < 1e-4)
            {
                continue;
            }

            restFound = true;
            var zone = ClipperBridge.Offset(rest, tool.Radius - 0.01);
            var pieces = loops.SelectMany(loop => JoinPieces(ClipperBridge.ClipLines(new[] { loop }, zone)))
                .Where(p => Polyline.Length(p, closed: false) > 0.01)
                .ToList();
            var liftZ = (i == 0 ? operation.StartZ : levels[i - 1]) + context.Machine.ApproachClearance;
            foreach (var piece in pieces)
            {
                // Above this level everything within reach is already gone; the piece itself may start in material.
                MoveOver(piece[0], liftZ, area, context);
                Descend(piece, closed: false, z, double.PositiveInfinity, context);
                foreach (var p in piece.Skip(1))
                {
                    writer.CutTo(new Vec3(p, z));
                }

                writer.RapidUpTo(liftZ);
                cut = true;
            }
        }

        if (!restFound)
        {
            context.Warnings.Add(Loc.T(
                $"{context.Label}: остатков нет — операции выше уже выбрали карман с допуском {operation.RestTolerance:0.###} мм.",
                $"{context.Label}: nothing left — the operations above already cleared the pocket within {operation.RestTolerance:0.###} mm."));
        }
        else if (!cut)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: фреза не достаёт до оставшегося материала.", $"{context.Label}: the tool cannot reach the remaining material."));
        }

        writer.Retract();
    }

    /// <summary>
    /// Profile rest machining by simulation: the tool centre path (closed loop or open line) is cut, pass by pass,
    /// only where the tool would still meet material left by the earlier operations. Tabs are kept.
    /// </summary>
    /// <returns>Whether anything was left to cut along the path.</returns>
    private static bool CutStockRestPath(List<Vec2> path, bool closed, List<double> passes, List<(double From, double To)> tabs, double tabTop,
        HeightField stock, double tolerance, OperationContext context)
    {
        var tool = context.Tool;
        var writer = context.Writer;
        var (points, distances) = Densify(path, closed, RestSampleStep);
        var band = ClipperBridge.Stroke(closed ? new List<Vec2>(path) { path[0] } : path, tool.Radius);
        var zones = new Dictionary<double, Paths64>();
        Paths64 Zone(double z)
        {
            if (!zones.TryGetValue(z, out var zone))
            {
                zones[z] = zone = ClipperBridge.Offset(RestRegion(stock, band, z, tolerance), tool.Radius - 0.01);
            }

            return zone;
        }

        bool InTab(double s) => tabs.Any(t => s > t.From && s < t.To);
        var count = points.Count;
        var anything = false;
        for (var i = 0; i < passes.Count; i++)
        {
            var z = passes[i];
            var above = i == 0 ? context.Operation.StartZ : passes[i - 1];
            // Over the tabs the previous pass stopped at the tab top.
            var liftZ = (tabs.Count > 0 && i > 0 ? Math.Max(above, tabTop) : above) + context.Machine.ApproachClearance;
            var heights = new double[count];
            var flags = new bool[count];
            for (var k = 0; k < count; k++)
            {
                var pointAbove = tabs.Count > 0 && above < tabTop && InTab(distances[k]) ? tabTop : above;
                heights[k] = tabs.Count > 0 && z < tabTop && InTab(distances[k]) ? tabTop : z;
                flags[k] = heights[k] < pointAbove - 1e-9 && ClipperBridge.Contains(Zone(heights[k]), points[k]);
            }

            foreach (var piece in FlaggedRuns(flags, closed))
            {
                anything = true;
                var piece3 = piece.Select(k => new Vec3(points[k], heights[k])).ToList();
                var piece2 = piece3.Select(p => p.XY).ToList();
                var full = closed && piece.Count == count + 1;
                // The ramp must stay in front of the first change of height (a tab).
                var flat = piece3.FindIndex(p => Math.Abs(p.Z - piece3[0].Z) > 1e-9);
                var rampLimit = flat < 0 ? double.PositiveInfinity : Math.Max(0, Polyline.Length(piece2.Take(flat).ToList(), closed: false) - 0.1);
                writer.TravelTo(piece2[0]);
                writer.RapidDownTo(liftZ);
                Descend(full ? points : piece2, full && double.IsPositiveInfinity(rampLimit), piece3[0].Z, rampLimit, context);
                foreach (var p in piece3.Skip(1))
                {
                    if (Math.Abs(p.Z - writer.Position.Z) > 1e-9)
                    {
                        writer.CutTo(writer.Position with { Z = p.Z });
                    }

                    writer.CutTo(p);
                }

                writer.RapidUpTo(liftZ);
            }
        }

        return anything;
    }

    /// <summary>
    /// Index runs of set flags, each grown by one point on both sides so that the cut overlaps the material edge.
    /// For a closed path the runs wrap around; a path set all the way round is one run back to its start.
    /// </summary>
    internal static List<List<int>> FlaggedRuns(bool[] flags, bool closed)
    {
        var count = flags.Length;
        var runs = new List<List<int>>();
        if (count == 0 || !flags.Any(f => f))
        {
            return runs;
        }

        if (closed && flags.All(f => f))
        {
            runs.Add(Enumerable.Range(0, count + 1).Select(k => k % count).ToList());
            return runs;
        }

        // A closed path is walked from a point without material, so that no run is split at the seam.
        var first = closed ? Array.IndexOf(flags, false) : 0;
        List<int>? run = null;
        for (var n = 0; n < count; n++)
        {
            var k = (first + n) % count;
            if (flags[k])
            {
                if (run is null)
                {
                    run = new List<int>();
                    var before = closed ? (k - 1 + count) % count : k - 1;
                    if (before >= 0)
                    {
                        run.Add(before);
                    }
                }

                run.Add(k);
            }
            else if (run is not null)
            {
                run.Add(k);
                runs.Add(run);
                run = null;
            }
        }

        if (run is not null)
        {
            // Open path: the run reaches the end. Closed path: it ends just before the starting point (no material).
            if (closed)
            {
                run.Add(first);
            }

            runs.Add(run);
        }

        return runs;
    }

    /// <summary>Points along the path no farther apart than <paramref name="step"/>, with the distance of each from the start.</summary>
    private static (List<Vec2> Points, List<double> Distances) Densify(List<Vec2> path, bool closed, double step)
    {
        var points = new List<Vec2> { path[0] };
        var distances = new List<double> { 0 };
        var walked = 0.0;
        var edges = closed ? path.Count : path.Count - 1;
        for (var i = 0; i < edges; i++)
        {
            var a = path[i];
            var b = path[(i + 1) % path.Count];
            var length = a.DistanceTo(b);
            var parts = Math.Max(1, (int)Math.Ceiling(length / step));
            for (var n = 1; n <= parts; n++)
            {
                if (closed && i == edges - 1 && n == parts)
                {
                    // Back at the start of a closed path.
                    break;
                }

                points.Add(Vec2.Lerp(a, b, (double)n / parts));
                distances.Add(walked + length * n / parts);
            }

            walked += length;
        }

        return (points, distances);
    }
}
