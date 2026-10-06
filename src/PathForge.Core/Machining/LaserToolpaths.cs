using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>Laser operations: vector cutting / hatch fill and raster picture engraving.</summary>
public static partial class ToolpathGenerator
{
    /// <summary>Generates a laser operation; returns the end position or null when nothing was produced.</summary>
    private static Vec3? GenerateLaser(Operation operation, Tool tool, Dictionary<int, Contour> contours, Vec3 position,
        string label, GenerationResult result)
    {
        if (tool.Kind != ToolKind.Laser)
        {
            result.Warnings.Add(Loc.T($"{label}: для лазерной операции выберите инструмент «Лазер».", $"{label}: choose a “Laser” tool for a laser operation."));
            return null;
        }

        if (operation is not (LaserVectorOperation or LaserRasterOperation))
        {
            result.Warnings.Add(Loc.T($"{label}: лазер нельзя использовать во фрезерной операции — добавьте лазерную операцию.", $"{label}: a laser cannot be used in a milling operation — add a laser operation."));
            return null;
        }

        // Z stays at the focus height (StartZ); travel happens at the same height with the beam off.
        var writer = new PathWriter(position, operation.StartZ);
        var speed = 0.0;
        switch (operation)
        {
            case LaserVectorOperation vector:
                var selected = vector.ContourIds.Where(contours.ContainsKey).Select(id => contours[id]).ToList();
                if (selected.Count == 0)
                {
                    result.Warnings.Add(Loc.T($"{label}: не выбраны контуры.", $"{label}: no contours selected."));
                    return null;
                }

                speed = vector.Speed;
                GenerateLaserVector(vector, selected, writer, result.Warnings, label);
                break;
            case LaserRasterOperation raster:
                speed = raster.Speed;
                GenerateLaserRaster(raster, writer, result.Warnings, label);
                break;
        }

        if (writer.Moves.Count == 0)
        {
            return null;
        }

        // The speed belongs to the operation: give the toolpath a copy of the tool with that feed.
        var laser = tool.Clone();
        laser.FeedRate = laser.PlungeRate = Math.Max(1, speed);
        result.Toolpaths.Add(new Toolpath(operation, laser, writer.Moves));
        return writer.Position;
    }

    private static void GenerateLaserVector(LaserVectorOperation operation, List<Contour> contours, PathWriter writer,
        List<string> warnings, string label)
    {
        var power = Math.Clamp(operation.PowerPercent, 0, 100) / 100;
        var fill = operation.Mode is LaserVectorMode.Fill or LaserVectorMode.FillAndLine;
        var line = operation.Mode is LaserVectorMode.Line or LaserVectorMode.FillAndLine;
        var hatch = fill ? HatchLines(contours, operation.FillSpacing, operation.FillAngle, warnings, label) : new List<(Vec2, Vec2)>();
        var kerf = line && operation.Kerf != KerfCompensation.None && operation.KerfWidth > 0
            ? KerfDeltas(contours, operation)
            : new Dictionary<Contour, double>();
        if (kerf.Count > 0 && contours.Any(c => !c.IsClosed))
        {
            warnings.Add(Loc.T($"{label}: ширина реза учитывается только у замкнутых контуров.", $"{label}: the kerf is only compensated on closed contours."));
        }

        for (var pass = 0; pass < Math.Max(1, operation.Passes); pass++)
        {
            var z = operation.StartZ - pass * Math.Max(0, operation.ZStepPerPass);
            foreach (var (from, to) in hatch)
            {
                MoveLaserTo(writer, from, z);
                writer.BurnTo(to, power);
            }

            if (!line)
            {
                continue;
            }

            foreach (var contour in OrderInsideFirst(contours, writer.Position.XY))
            {
                var points = contour.Flatten(FlattenTolerance);
                if (points.Count < 2)
                {
                    continue;
                }

                var paths = new List<List<Vec2>> { points };
                if (kerf.TryGetValue(contour, out var delta))
                {
                    // Offsetting may split a narrow shape in two or make a small hole vanish.
                    paths = ClipperBridge.FromPaths(ClipperBridge.Offset(ClipperBridge.EvenOddRegion(new[] { points }), delta));
                }

                foreach (var path in paths)
                {
                    var burn = path;
                    if (contour.IsClosed)
                    {
                        burn = Polyline.RotateToNearest(path, writer.Position.XY);
                        burn.Add(burn[0]);
                    }

                    MoveLaserTo(writer, burn[0], z);
                    foreach (var p in burn.Skip(1))
                    {
                        writer.BurnTo(p, power);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Offset of every closed contour for kerf compensation: contours inside an odd number of others are holes.
    /// Positive values grow the shape.
    /// </summary>
    internal static Dictionary<Contour, double> KerfDeltas(List<Contour> contours, LaserVectorOperation operation)
    {
        var closed = contours.Where(c => c.IsClosed).Select(c => (Contour: c, Ring: c.Flatten(FlattenTolerance))).Where(c => c.Ring.Count >= 3).ToList();
        var deltas = new Dictionary<Contour, double>();
        var half = operation.KerfWidth / 2;
        foreach (var (contour, ring) in closed)
        {
            var depth = closed.Count(other => !ReferenceEquals(other.Contour, contour) && Polyline.Contains(other.Ring, ring[0]));
            var outer = depth % 2 == 0;
            var grow = outer == (operation.Kerf == KerfCompensation.Parts);
            deltas[contour] = grow ? half : -half;
        }

        return deltas;
    }

    /// <summary>Travel with the beam off to <paramref name="xy"/> and set the focus height.</summary>
    private static void MoveLaserTo(PathWriter writer, Vec2 xy, double z)
    {
        writer.TravelTo(xy);
        writer.RapidDownTo(z);
    }

    /// <summary>Parallel lines inside the closed contours (holes respected), in zigzag order.</summary>
    internal static List<(Vec2 From, Vec2 To)> HatchLines(List<Contour> contours, double spacing, double angleDeg,
        List<string> warnings, string label)
    {
        var closed = contours.Where(c => c.IsClosed).ToList();
        var result = new List<(Vec2, Vec2)>();
        if (closed.Count == 0)
        {
            warnings.Add(Loc.T($"{label}: заливка возможна только внутри замкнутых контуров.", $"{label}: fill is only possible inside closed contours."));
            return result;
        }

        spacing = Math.Max(0.01, spacing);
        // Work in a frame where the hatch lines are horizontal.
        var toLocal = Affine2.Rotation(-angleDeg * Math.PI / 180);
        var toWorld = Affine2.Rotation(angleDeg * Math.PI / 180);
        var rings = closed.Select(c => (IReadOnlyList<Vec2>)c.Flatten(FlattenTolerance).Select(toLocal.Apply).ToList()).ToList();
        var region = ClipperBridge.EvenOddRegion(rings);
        var bounds = rings.Aggregate(Bounds2.Empty, (b, r) => b.Union(Bounds2.Of(r)));

        var rowCount = (int)Math.Floor(bounds.Height / spacing);
        if (rowCount > 200000)
        {
            warnings.Add(Loc.T($"{label}: слишком мелкий шаг заливки для такой площади.", $"{label}: the fill spacing is too small for such an area."));
            return result;
        }

        var scanLines = new List<IReadOnlyList<Vec2>>();
        for (var y = bounds.MinY + spacing / 2; y < bounds.MaxY; y += spacing)
        {
            scanLines.Add(new[] { new Vec2(bounds.MinX - 1, y), new Vec2(bounds.MaxX + 1, y) });
        }

        var pieces = ClipperBridge.ClipLines(scanLines, region)
            .Select(p => (A: p[0], B: p[^1]))
            .Select(p => p.A.X <= p.B.X ? p : (A: p.B, B: p.A))
            .GroupBy(p => Math.Round(p.A.Y / spacing))
            .OrderBy(g => g.Key)
            .ToList();

        var leftToRight = true;
        foreach (var row in pieces)
        {
            var ordered = leftToRight ? row.OrderBy(p => p.A.X) : row.OrderByDescending(p => p.A.X);
            foreach (var (a, b) in ordered)
            {
                var (from, to) = leftToRight ? (a, b) : (b, a);
                result.Add((toWorld.Apply(from), toWorld.Apply(to)));
            }

            leftToRight = !leftToRight;
        }

        return result;
    }

    private static void GenerateLaserRaster(LaserRasterOperation operation, PathWriter writer, List<string> warnings, string label)
    {
        var image = operation.Image;
        if (image.Width == 0 || image.Height == 0 || image.Pixels.Length < image.Width * image.Height)
        {
            warnings.Add(Loc.T($"{label}: картинка не загружена.", $"{label}: no picture loaded."));
            return;
        }

        var interval = Math.Max(0.02, operation.LineInterval);
        var columns = Math.Max(1, (int)Math.Round(operation.WidthMm / interval));
        var rows = Math.Max(1, (int)Math.Round(operation.HeightMm / interval));
        if ((long)columns * rows > 25_000_000)
        {
            warnings.Add(Loc.T($"{label}: слишком большое разрешение ({columns}×{rows}); увеличьте шаг строк.", $"{label}: resolution too high ({columns}×{rows}); increase the line interval."));
            return;
        }

        var bySpeed = operation.Modulation == RasterModulation.Speed;
        var fullPower = Math.Clamp(operation.PowerMaxPercent, 0, 100) / 100;
        // By speed: the grey level 0…1 of each pixel decides the speed, the power stays at the maximum.
        var powers = bySpeed ? RasterLevels(operation, columns, rows) : RasterPowers(operation, columns, rows);
        var fast = Math.Max(1, operation.Speed);
        var slow = Math.Clamp(operation.SpeedMin, 1, fast);
        if (bySpeed && operation.SpeedMin >= operation.Speed)
        {
            warnings.Add(Loc.T($"{label}: минимальная скорость должна быть меньше скорости — оттенки не получатся.", $"{label}: the minimum speed must be below the speed — no grey shades."));
        }

        // Burnt energy per millimetre grows with 1/speed: interpolate there so that grey looks even.
        void Burn(Vec2 to, double value)
        {
            if (!bySpeed || value <= 0)
            {
                writer.BurnTo(to, bySpeed ? 0 : value);
                return;
            }

            var speed = 1 / (1 / fast + value * (1 / slow - 1 / fast));
            writer.BurnTo(to, fullPower, Math.Round(speed));
        }

        var overscan = Math.Max(0, operation.Overscan);
        double X(int column) => operation.X + column * interval;
        var burnedRows = 0;

        for (var r = 0; r < rows; r++)
        {
            var first = Array.FindIndex(powers[r], p => p > 0);
            if (first < 0)
            {
                continue;
            }

            var last = Array.FindLastIndex(powers[r], p => p > 0);
            var y = operation.Y + operation.HeightMm - (r + 0.5) * interval;
            var leftToRight = !operation.Bidirectional || burnedRows % 2 == 0;
            burnedRows++;

            if (leftToRight)
            {
                MoveLaserTo(writer, new Vec2(X(first) - overscan, y), operation.StartZ);
                writer.BurnTo(new Vec2(X(first), y), 0);
                var c = first;
                while (c <= last)
                {
                    var end = c;
                    while (end + 1 <= last && powers[r][end + 1] == powers[r][c])
                    {
                        end++;
                    }

                    Burn(new Vec2(X(end + 1), y), powers[r][c]);
                    c = end + 1;
                }

                writer.BurnTo(new Vec2(X(last + 1) + overscan, y), 0);
            }
            else
            {
                MoveLaserTo(writer, new Vec2(X(last + 1) + overscan, y), operation.StartZ);
                writer.BurnTo(new Vec2(X(last + 1), y), 0);
                var c = last;
                while (c >= first)
                {
                    var start = c;
                    while (start - 1 >= first && powers[r][start - 1] == powers[r][c])
                    {
                        start--;
                    }

                    Burn(new Vec2(X(start), y), powers[r][c]);
                    c = start - 1;
                }

                writer.BurnTo(new Vec2(X(first) - overscan, y), 0);
            }
        }

        if (burnedRows == 0)
        {
            warnings.Add(Loc.T($"{label}: в картинке нечего выжигать (всё светлое).", $"{label}: nothing to burn in the picture (all light)."));
        }
    }

    /// <summary>Grey level 0…1 of every pixel (the power formula with the range 0…100 %).</summary>
    internal static double[][] RasterLevels(LaserRasterOperation operation, int columns, int rows) =>
        RasterPowers(operation, columns, rows, 0, 1);

    /// <summary>Laser power (0…1, rounded to 1/1000) for every pixel of the output grid; row 0 is the top.</summary>
    internal static double[][] RasterPowers(LaserRasterOperation operation, int columns, int rows) =>
        RasterPowers(operation, columns, rows,
            Math.Clamp(operation.PowerMinPercent, 0, 100) / 100,
            Math.Clamp(operation.PowerMaxPercent, 0, 100) / 100);

    private static double[][] RasterPowers(LaserRasterOperation operation, int columns, int rows, double min, double max)
    {
        var image = operation.Image;
        var darkness = new double[rows][];
        for (var r = 0; r < rows; r++)
        {
            darkness[r] = new double[columns];
            for (var c = 0; c < columns; c++)
            {
                var brightness = image.Sample((c + 0.5) * image.Width / columns, (r + 0.5) * image.Height / rows);
                darkness[r][c] = operation.Invert ? brightness : 1 - brightness;
            }
        }

        var powers = new double[rows][];
        for (var r = 0; r < rows; r++)
        {
            powers[r] = new double[columns];
            for (var c = 0; c < columns; c++)
            {
                var d = darkness[r][c];
                double power;
                switch (operation.Mode)
                {
                    case RasterMode.Threshold:
                        power = d >= 0.5 ? max : 0;
                        break;
                    case RasterMode.Dither:
                        var on = d >= 0.5;
                        var error = d - (on ? 1 : 0);
                        // Floyd–Steinberg error diffusion to the not yet processed neighbours.
                        if (c + 1 < columns)
                        {
                            darkness[r][c + 1] += error * 7 / 16;
                        }

                        if (r + 1 < rows)
                        {
                            if (c > 0)
                            {
                                darkness[r + 1][c - 1] += error * 3 / 16;
                            }

                            darkness[r + 1][c] += error * 5 / 16;
                            if (c + 1 < columns)
                            {
                                darkness[r + 1][c + 1] += error * 1 / 16;
                            }
                        }

                        power = on ? max : 0;
                        break;
                    default:
                        power = d < 0.02 ? 0 : min + Math.Clamp(d, 0, 1) * (max - min);
                        break;
                }

                powers[r][c] = Math.Round(power * 1000) / 1000;
            }
        }

        return powers;
    }
}
