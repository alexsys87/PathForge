using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>3D relief: height map, drop-cutter tool compensation, roughing levels, parallel and waterline finishing.</summary>
public static partial class ToolpathGenerator
{
    private const int MaxReliefCells = 4_000_000;

    /// <param name="boundary">Contours that limit the machined area (<see cref="ReliefOperation.LimitToContours"/>); empty = no limit.</param>
    private static void GenerateRelief(ReliefOperation operation, IReadOnlyList<Contour> boundary, OperationContext context)
    {
        var tool = context.Tool;
        var hasSource = operation.Source == ReliefSource.Image
            ? operation.Image.Width > 0 && operation.Image.Pixels.Length >= operation.Image.Width * operation.Image.Height
            : operation.Mesh.TriangleCount > 0;
        if (!hasSource)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: не загружена {(operation.Source == ReliefSource.Image ? "картинка" : "модель STL")}.", $"{context.Label}: no {(operation.Source == ReliefSource.Image ? "picture" : "STL model")} loaded."));
            return;
        }

        var resolution = Math.Max(0.02, operation.Resolution);
        var columns = Math.Max(2, (int)Math.Round(operation.WidthMm / resolution));
        var rows = Math.Max(2, (int)Math.Round(operation.HeightMm / resolution));
        if ((long)columns * rows > MaxReliefCells)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: слишком мелкая сетка ({columns}×{rows}); увеличьте шаг сетки.", $"{context.Label}: the grid is too fine ({columns}×{rows}); increase the grid step."));
            return;
        }

        var surface = operation.Source == ReliefSource.Image
            ? HeightMap.FromImage(operation, columns, rows)
            : HeightMap.FromMesh(operation, columns, rows);
        var toolTip = surface.DropCutter(ToolRadius(tool), ToolProfile(tool));
        var area = AreaMask(operation, boundary, toolTip, context);

        if (tool.Kind is ToolKind.EndMill or ToolKind.Drill)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: для рельефа лучше сферическая фреза или гравёр — плоская оставит ступеньки.", $"{context.Label}: a ball nose or a V-bit is better for a relief — a flat end mill leaves steps."));
        }

        if (!operation.Roughing && operation.Depth > tool.StepDown + 1e-9)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: глубина больше шага по глубине инструмента — включите черновую обработку.", $"{context.Label}: the depth exceeds the tool step-down — enable roughing."));
        }

        var startZ = operation.StartZ;
        if (operation.Roughing)
        {
            var roughStep = Math.Max(resolution, tool.StepOver);
            var previous = startZ;
            foreach (var level in PassDepths(startZ, operation.Depth, tool.StepDown))
            {
                var lift = operation.RoughAllowance;
                var pass = RasterPass(operation, toolTip, roughStep, cell => Math.Min(startZ, Math.Max(level, startZ + cell + lift)), area);
                // Skip levels that would only cut air.
                if (pass.All(run => run.All(p => p.Z >= previous - 1e-3)))
                {
                    previous = level;
                    continue;
                }

                pass.ForEach(run => CutRaster(run, operation, context));
                previous = level;
            }
        }

        if (operation.Finishing != ReliefFinishing.Waterline)
        {
            var finish = RasterPass(operation, toolTip, Math.Max(resolution, operation.StepOverMm), cell => startZ + cell, area);
            finish.ForEach(run => CutRaster(run, operation, context));
        }

        if (operation.Finishing != ReliefFinishing.Parallel)
        {
            var levels = area;
            if (operation.SteepAngle > 0)
            {
                // Overlap the parallel lines by one step so that no strip is left between the two strategies.
                var steep = SteepMask(toolTip, operation.SteepAngle, (int)Math.Ceiling(Math.Max(resolution, operation.StepOverMm) / toolTip.CellSize));
                levels = area is null ? steep : steep.Zip(area, (a, b) => a && b).ToArray();
            }

            CutWaterlines(operation, toolTip, levels, context);
        }
    }

    /// <summary>
    /// Cells whose centre lies inside the boundary contours (even-odd: nested contours are holes), or null when
    /// the whole relief is machined.
    /// </summary>
    private static bool[]? AreaMask(ReliefOperation operation, IReadOnlyList<Contour> boundary, HeightMap grid, OperationContext context)
    {
        if (!operation.LimitToContours)
        {
            return null;
        }

        var mask = BoundaryMask(boundary, grid);
        if (mask is null)
        {
            context.Warnings.Add(Loc.T(
                $"{context.Label}: для ограничения области выберите замкнутые контуры — обработан весь рельеф.",
                $"{context.Label}: select closed contours to limit the area — the whole relief was machined."));
            return null;
        }

        if (!mask.Any(m => m))
        {
            context.Warnings.Add(Loc.T(
                $"{context.Label}: выбранные контуры не пересекают рельеф — обрабатывать нечего.",
                $"{context.Label}: the selected contours do not overlap the relief — nothing to machine."));
        }

        return mask;
    }

    /// <summary>Cells whose centre is inside the closed contours (even-odd), or null when there are no closed contours.</summary>
    internal static bool[]? BoundaryMask(IReadOnlyList<Contour> boundary, HeightMap grid)
    {
        var rings = boundary.Where(c => c.IsClosed).Select(c => c.Flatten(FlattenTolerance)).Where(r => r.Count >= 3).ToList();
        if (rings.Count == 0)
        {
            return null;
        }

        var mask = new bool[grid.Columns * grid.Rows];
        var crossings = new List<double>();
        for (var r = 0; r < grid.Rows; r++)
        {
            // Scanline through the cell centres: inside between pairs of crossings.
            var y = grid.CellY(r);
            crossings.Clear();
            foreach (var ring in rings)
            {
                for (var i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    if ((a.Y <= y) != (b.Y <= y))
                    {
                        crossings.Add(a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                    }
                }
            }

            crossings.Sort();
            for (var k = 0; k + 1 < crossings.Count; k += 2)
            {
                var c0 = Math.Max(0, (int)Math.Ceiling((crossings[k] - grid.OriginX) / grid.CellSize - 0.5));
                var c1 = Math.Min(grid.Columns - 1, (int)Math.Floor((crossings[k + 1] - grid.OriginX) / grid.CellSize - 0.5));
                for (var c = c0; c <= c1; c++)
                {
                    mask[r * grid.Columns + c] = true;
                }
            }
        }

        return mask;
    }

    /// <summary>The surface a relief operation aims at (heights ≤ 0 below its top), or null without a picture or model.</summary>
    internal static HeightMap? ReliefSurface(ReliefOperation operation)
    {
        var hasSource = operation.Source == ReliefSource.Image
            ? operation.Image.Width > 0 && operation.Image.Pixels.Length >= operation.Image.Width * operation.Image.Height
            : operation.Mesh.TriangleCount > 0;
        var resolution = Math.Max(0.02, operation.Resolution);
        var columns = Math.Max(2, (int)Math.Round(operation.WidthMm / resolution));
        var rows = Math.Max(2, (int)Math.Round(operation.HeightMm / resolution));
        if (!hasSource || (long)columns * rows > MaxReliefCells)
        {
            return null;
        }

        return operation.Source == ReliefSource.Image
            ? HeightMap.FromImage(operation, columns, rows)
            : HeightMap.FromMesh(operation, columns, rows);
    }

    /// <summary>Cells where the tool-tip surface is steeper than <paramref name="angle"/> degrees, grown by <paramref name="grow"/> cells.</summary>
    internal static bool[] SteepMask(HeightMap tip, double angle, int grow)
    {
        var limit = Math.Tan(Math.Clamp(angle, 0, 89.9) * Math.PI / 180);
        var columns = tip.Columns;
        var rows = tip.Rows;
        var steep = new bool[columns * rows];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var c0 = Math.Max(0, c - 1);
                var c1 = Math.Min(columns - 1, c + 1);
                var r0 = Math.Max(0, r - 1);
                var r1 = Math.Min(rows - 1, r + 1);
                var gx = c1 == c0 ? 0 : (tip[c1, r] - tip[c0, r]) / ((c1 - c0) * tip.CellSize);
                var gy = r1 == r0 ? 0 : (tip[c, r1] - tip[c, r0]) / ((r1 - r0) * tip.CellSize);
                steep[r * columns + c] = gx * gx + gy * gy >= limit * limit;
            }
        }

        if (grow <= 0)
        {
            return steep;
        }

        // Grow the steep area (square neighbourhood, separable: rows then columns).
        var rowsGrown = new bool[steep.Length];
        for (var r = 0; r < rows; r++)
        {
            var last = int.MinValue / 2;
            for (var c = 0; c < columns; c++)
            {
                if (steep[r * columns + c])
                {
                    last = c;
                }

                rowsGrown[r * columns + c] = c - last <= grow;
            }

            last = int.MaxValue / 2;
            for (var c = columns - 1; c >= 0; c--)
            {
                if (steep[r * columns + c])
                {
                    last = c;
                }

                rowsGrown[r * columns + c] |= last - c <= grow;
            }
        }

        var grown = new bool[steep.Length];
        for (var c = 0; c < columns; c++)
        {
            var last = int.MinValue / 2;
            for (var r = 0; r < rows; r++)
            {
                if (rowsGrown[r * columns + c])
                {
                    last = r;
                }

                grown[r * columns + c] = r - last <= grow;
            }

            last = int.MaxValue / 2;
            for (var r = rows - 1; r >= 0; r--)
            {
                if (rowsGrown[r * columns + c])
                {
                    last = r;
                }

                grown[r * columns + c] |= last - r <= grow;
            }
        }

        return grown;
    }

    /// <summary>The mask cell under a point (points on the grid edge belong to the nearest cell).</summary>
    private static bool InMask(bool[] mask, HeightMap grid, Vec2 p)
    {
        var c = Math.Clamp((int)Math.Floor((p.X - grid.OriginX) / grid.CellSize), 0, grid.Columns - 1);
        var r = Math.Clamp((int)Math.Floor((p.Y - grid.OriginY) / grid.CellSize), 0, grid.Rows - 1);
        return mask[r * grid.Columns + c];
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
    /// With a <paramref name="mask"/> the raster breaks into separate runs over the allowed cells.
    /// </summary>
    private static List<List<Vec3>> RasterPass(ReliefOperation operation, HeightMap tip, double step, Func<double, double> height, bool[]? mask)
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

        var runs = new List<List<Vec3>>();
        var points = new List<Vec3>();

        void Point(int along, int across)
        {
            var (c, r) = alongX ? (along, across) : (across, along);
            if (mask is not null && !mask[r * tip.Columns + c])
            {
                if (points.Count > 0)
                {
                    runs.Add(Simplify(points, 0.002));
                    points = new List<Vec3>();
                }

                return;
            }

            points.Add(new Vec3(tip.CellX(c), tip.CellY(r), height(tip[c, r])));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var forward = i % 2 == 0;
            if (i > 0)
            {
                // Link along the edge from the previous line to this one, following the surface.
                var edge = forward ? 0 : pointCount - 1;
                for (var k = lines[i - 1] + 1; k < lines[i]; k++)
                {
                    Point(edge, k);
                }
            }

            for (var j = 0; j < pointCount; j++)
            {
                Point(forward ? j : pointCount - 1 - j, lines[i]);
            }
        }

        if (points.Count > 0)
        {
            runs.Add(Simplify(points, 0.002));
        }

        return runs;
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
