using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>Face milling of a rectangle in zigzag lines.</summary>
public static partial class ToolpathGenerator
{
    private static void GenerateFacing(FacingOperation operation, Dictionary<int, Contour> contours, OperationContext context)
    {
        var tool = context.Tool;
        var area = operation.Area(contours);
        if (area.Width <= 0 || area.Height <= 0)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: задайте размер области (ширина и высота больше нуля).", $"{context.Label}: set the area size (width and height above zero)."));
            return;
        }

        var stepOver = tool.StepOver;
        if (stepOver <= 0 || stepOver > tool.Diameter)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: перекрытие должно быть от 1 до 100 % диаметра.", $"{context.Label}: the step-over must be 1 to 100 % of the diameter."));
            return;
        }

        var lines = FacingLines(area, tool.Radius, Math.Clamp(operation.OverhangPercent, 0, 200) / 100 * tool.Diameter, stepOver, operation.Axis);
        if (operation.OverhangPercent < 21)
        {
            // Below (√2 − 1) / 2 of the diameter the round cutter cannot reach the corners of the area.
            context.Warnings.Add(Loc.T($"{context.Label}: при выходе за край меньше 21 % диаметра углы области останутся необработанными.", $"{context.Label}: with an overhang below 21 % of the diameter the corners of the area stay uncut."));
        }

        // One continuous zigzag: the lines joined by short moves along the edge.
        var path = new List<Vec2>();
        for (var i = 0; i < lines.Count; i++)
        {
            var (a, b) = i % 2 == 0 ? lines[i] : (lines[i].B, lines[i].A);
            path.Add(a);
            path.Add(b);
        }

        var writer = context.Writer;
        foreach (var z in PassDepths(operation.StartZ, operation.Depth, tool.StepDown))
        {
            EnterAt(path, closed: false, z, double.PositiveInfinity, context);
            foreach (var p in path.Skip(1))
            {
                writer.CutTo(new Vec3(p, z));
            }

            writer.Retract();
        }
    }

    /// <summary>
    /// Tool centre lines covering <paramref name="area"/>: the cutter edge goes <paramref name="overhang"/> past the
    /// edges, lines at most <paramref name="stepOver"/> apart.
    /// </summary>
    internal static List<(Vec2 A, Vec2 B)> FacingLines(Bounds2 area, double radius, double overhang, double stepOver, RasterAxis axis)
    {
        var alongX = axis == RasterAxis.X;
        var (lengthMin, lengthMax) = alongX ? (area.MinX, area.MaxX) : (area.MinY, area.MaxY);
        var (acrossMin, acrossMax) = alongX ? (area.MinY, area.MaxY) : (area.MinX, area.MaxX);
        var inset = radius - overhang;
        var from = lengthMin + inset;
        var to = lengthMax - inset;
        if (from > to)
        {
            from = to = (lengthMin + lengthMax) / 2;
        }

        var first = acrossMin + inset;
        var last = acrossMax - inset;
        if (first > last)
        {
            first = last = (acrossMin + acrossMax) / 2;
        }

        var count = Math.Max(1, (int)Math.Ceiling((last - first) / stepOver - 1e-9) + 1);
        var lines = new List<(Vec2, Vec2)>(count);
        for (var i = 0; i < count; i++)
        {
            var c = count == 1 ? first : first + i * (last - first) / (count - 1);
            lines.Add(alongX ? (new Vec2(from, c), new Vec2(to, c)) : (new Vec2(c, from), new Vec2(c, to)));
        }

        return lines;
    }
}
