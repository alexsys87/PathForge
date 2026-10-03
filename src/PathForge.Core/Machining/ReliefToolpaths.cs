using PathForge.Core.Geometry;

namespace PathForge.Core.Machining;

/// <summary>3D relief: height map, drop-cutter tool compensation, roughing levels and parallel finishing.</summary>
public static partial class ToolpathGenerator
{
    private const int MaxReliefCells = 4_000_000;

    private static void GenerateRelief(ReliefOperation operation, OperationContext context)
    {
        var tool = context.Tool;
        var hasSource = operation.Source == ReliefSource.Image
            ? operation.Image.Width > 0 && operation.Image.Pixels.Length >= operation.Image.Width * operation.Image.Height
            : operation.Mesh.TriangleCount > 0;
        if (!hasSource)
        {
            context.Warnings.Add($"{context.Label}: не загружена {(operation.Source == ReliefSource.Image ? "картинка" : "модель STL")}.");
            return;
        }

        var resolution = Math.Max(0.02, operation.Resolution);
        var columns = Math.Max(2, (int)Math.Round(operation.WidthMm / resolution));
        var rows = Math.Max(2, (int)Math.Round(operation.HeightMm / resolution));
        if ((long)columns * rows > MaxReliefCells)
        {
            context.Warnings.Add($"{context.Label}: слишком мелкая сетка ({columns}×{rows}); увеличьте шаг сетки.");
            return;
        }

        var surface = operation.Source == ReliefSource.Image
            ? HeightMap.FromImage(operation, columns, rows)
            : HeightMap.FromMesh(operation, columns, rows);
        var toolTip = surface.DropCutter(ToolRadius(tool), ToolProfile(tool));

        if (tool.Kind is ToolKind.EndMill or ToolKind.Drill)
        {
            context.Warnings.Add($"{context.Label}: для рельефа лучше сферическая фреза или гравёр — плоская оставит ступеньки.");
        }

        if (!operation.Roughing && operation.Depth > tool.StepDown + 1e-9)
        {
            context.Warnings.Add($"{context.Label}: глубина больше шага по глубине инструмента — включите черновую обработку.");
        }

        var startZ = operation.StartZ;
        if (operation.Roughing)
        {
            var roughStep = Math.Max(resolution, tool.StepOver);
            var previous = startZ;
            foreach (var level in PassDepths(startZ, operation.Depth, tool.StepDown))
            {
                var lift = operation.RoughAllowance;
                var pass = RasterPass(operation, toolTip, roughStep, cell => Math.Min(startZ, Math.Max(level, startZ + cell + lift)));
                // Skip levels that would only cut air.
                if (pass.All(p => p.Z >= previous - 1e-3))
                {
                    previous = level;
                    continue;
                }

                CutRaster(pass, operation, context);
                previous = level;
            }
        }

        var finish = RasterPass(operation, toolTip, Math.Max(resolution, operation.StepOverMm), cell => startZ + cell);
        CutRaster(finish, operation, context);
    }

    private static double ToolRadius(Tool tool) => Math.Max(tool.Diameter, tool.TipDiameter) / 2;

    /// <summary>Height of the cutting edge above the tip at distance d from the tool axis.</summary>
    private static Func<double, double> ToolProfile(Tool tool)
    {
        var radius = ToolRadius(tool);
        return tool.Kind switch
        {
            ToolKind.BallNose => d => radius - Math.Sqrt(Math.Max(0, radius * radius - d * d)),
            ToolKind.VBit => d => d <= tool.TipDiameter / 2
                ? 0
                : (d - tool.TipDiameter / 2) / Math.Tan(Math.Clamp(tool.TipAngle, 1, 179) * Math.PI / 360),
            _ => _ => 0,
        };
    }

    /// <summary>
    /// Zigzag raster over the grid: lines along the chosen axis spaced by <paramref name="step"/>,
    /// joined along the edge. Every point takes its Z from <paramref name="height"/> of the tool-tip map.
    /// </summary>
    private static List<Vec3> RasterPass(ReliefOperation operation, HeightMap tip, double step, Func<double, double> height)
    {
        var alongX = operation.Axis == RasterAxis.X;
        var lineCount = alongX ? tip.Rows : tip.Columns;
        var pointCount = alongX ? tip.Columns : tip.Rows;
        var stride = Math.Max(1, (int)Math.Round(step / tip.CellSize));
        var lines = new List<int>();
        for (var k = 0; k < lineCount; k += stride)
        {
            lines.Add(k);
        }

        if (lines[^1] != lineCount - 1)
        {
            lines.Add(lineCount - 1);
        }

        Vec3 Point(int along, int across)
        {
            var (c, r) = alongX ? (along, across) : (across, along);
            return new Vec3(tip.CellX(c), tip.CellY(r), height(tip[c, r]));
        }

        var points = new List<Vec3>();
        for (var i = 0; i < lines.Count; i++)
        {
            var forward = i % 2 == 0;
            if (i > 0)
            {
                // Link along the edge from the previous line to this one, following the surface.
                var edge = forward ? 0 : pointCount - 1;
                for (var k = lines[i - 1] + 1; k < lines[i]; k++)
                {
                    points.Add(Point(edge, k));
                }
            }

            for (var j = 0; j < pointCount; j++)
            {
                points.Add(Point(forward ? j : pointCount - 1 - j, lines[i]));
            }
        }

        return Simplify(points, 0.002);
    }

    /// <summary>Drops points that lie on the straight line between their neighbours (within the tolerance).</summary>
    private static List<Vec3> Simplify(List<Vec3> points, double tolerance)
    {
        if (points.Count < 3)
        {
            return points;
        }

        var result = new List<Vec3> { points[0] };
        for (var i = 1; i < points.Count - 1; i++)
        {
            var a = result[^1];
            var b = points[i + 1];
            var p = points[i];
            var ab = new Vec2(b.X - a.X, b.Y - a.Y);
            var ap = new Vec2(p.X - a.X, p.Y - a.Y);
            var length = ab.LengthSquared;
            var t = length < 1e-18 ? 0 : Math.Clamp(ap.Dot(ab) / length, 0, 1);
            var expected = new Vec3(a.X + ab.X * t, a.Y + ab.Y * t, a.Z + (b.Z - a.Z) * t);
            if (p.DistanceTo(expected) > tolerance)
            {
                result.Add(p);
            }
        }

        result.Add(points[^1]);
        return result;
    }

    private static void CutRaster(List<Vec3> points, ReliefOperation operation, OperationContext context)
    {
        if (points.Count == 0)
        {
            return;
        }

        var writer = context.Writer;
        writer.TravelTo(points[0].XY);
        writer.RapidDownTo(operation.StartZ + context.Machine.ApproachClearance);
        writer.PlungeTo(points[0].Z);
        foreach (var p in points.Skip(1))
        {
            writer.CutTo(p);
        }

        writer.Retract();
    }
}
