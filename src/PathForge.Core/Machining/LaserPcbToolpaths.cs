using Clipper2Lib;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>PCB by laser: the paint on the copper is burned away wherever the etchant has to remove copper.</summary>
public static partial class ToolpathGenerator
{
    private static void GenerateLaserPcb(LaserPcbOperation operation, Tool tool, Dictionary<int, Contour> contours, PathWriter writer,
        List<string> warnings, string label)
    {
        // The board outline is not copper even if it was selected together with the copper.
        var board = operation.BoardContourIds.ToHashSet();
        var selected = operation.ContourIds.Where(id => contours.ContainsKey(id) && !board.Contains(id)).Select(id => contours[id]).ToList();
        var closed = selected.Where(c => c.IsClosed).ToList();
        if (closed.Count < selected.Count)
        {
            warnings.Add(Loc.T($"{label}: незамкнутые контуры пропущены ({selected.Count - closed.Count} шт.).", $"{label}: open contours skipped ({selected.Count - closed.Count})."));
        }

        if (closed.Count == 0)
        {
            warnings.Add(Loc.T($"{label}: выберите контуры меди (замкнутые).", $"{label}: select copper contours (closed)."));
            return;
        }

        if (tool.Diameter <= 0)
        {
            warnings.Add(Loc.T($"{label}: у лазера «{tool.Name}» задайте диаметр — это размер пятна (0,08–0,2 мм).", $"{label}: set the diameter of the laser “{tool.Name}” — it is the spot size (0.08–0.2 mm)."));
            return;
        }

        var spot = tool.Diameter;
        var spacing = Math.Max(0.01, operation.LineSpacing);
        if (spacing > spot + 1e-9)
        {
            warnings.Add(Loc.T(
                $"{label}: шаг линий {spacing:0.###} мм больше пятна лазера {spot:0.###} мм — между линиями останутся полоски краски.",
                $"{label}: the line spacing {spacing:0.###} mm is larger than the laser spot {spot:0.###} mm — stripes of paint stay between the lines."));
        }

        var copper = ClipperBridge.EvenOddRegion(closed.Select(c => (IReadOnlyList<Vec2>)c.Flatten(FlattenTolerance)));
        if (Math.Abs(operation.CopperOffset) > 1e-9)
        {
            copper = ClipperBridge.Offset(copper, operation.CopperOffset);
        }

        var copperRings = ClipperBridge.FromPaths(copper);
        if (copperRings.Count == 0)
        {
            warnings.Add(Loc.T($"{label}: после уменьшения меди ничего не осталось.", $"{label}: nothing is left of the copper after shrinking it."));
            return;
        }

        // Copper islands closer than the spot merge when grown by half of it: the beam cannot pass between them.
        var islands = copperRings.Count(r => Polyline.IsCounterClockwise(r));
        var merged = ClipperBridge.FromPaths(ClipperBridge.Offset(copper, spot / 2)).Count(r => Polyline.IsCounterClockwise(r));
        if (merged < islands)
        {
            warnings.Add(Loc.T(
                $"{label}: некоторые дорожки ближе друг к другу, чем пятно лазера {spot:0.###} мм — краска между ними останется и они не разделятся. " +
                "Сфокусируйте лазер точнее (меньше пятно) или уменьшите «Расширить медь».",
                $"{label}: some tracks are closer to each other than the laser spot {spot:0.###} mm — the paint between them stays and they are not separated. " +
                "Focus the laser better (smaller spot) or reduce “Grow copper”."));
        }

        var power = Math.Clamp(operation.PowerPercent, 0, 100) / 100;
        var z = operation.StartZ;
        var passes = Math.Max(1, operation.Passes);
        if (operation.Clearing == LaserPcbClearing.Isolation)
        {
            var levels = LaserIsolationRings(copper, spot, operation.IsolationWidth, spacing);
            for (var pass = 0; pass < passes; pass++)
            {
                foreach (var level in levels)
                {
                    BurnRings(level, power, z, writer);
                }
            }

            return;
        }

        var area = LaserClearingArea(operation, contours, copperRings, warnings, label);
        // The beam centre stays half a spot inside the area to clear: the burned edge lands on the copper edge.
        var centres = ClipperBridge.FromPaths(ClipperBridge.Offset(ClipperBridge.Difference(area, copper), -spot / 2));
        if (centres.Count == 0)
        {
            warnings.Add(Loc.T($"{label}: вокруг меди нечего выжигать.", $"{label}: there is nothing to burn around the copper."));
            return;
        }

        var hatches = new Dictionary<double, List<(Vec2 From, Vec2 To)>>();
        for (var pass = 0; pass < passes; pass++)
        {
            var angle = operation.FillAngle + (operation.CrossHatch && pass % 2 == 1 ? 90 : 0);
            if (!hatches.TryGetValue(angle, out var hatch))
            {
                hatches[angle] = hatch = HatchRings(centres, spacing, angle, warnings, label);
            }

            foreach (var (from, to) in hatch)
            {
                MoveLaserTo(writer, from, z);
                writer.BurnTo(to, power);
            }

            // The outlines last: the ends of the hatch lines leave a wavy edge, the outline makes it straight.
            BurnRings(centres, power, z, writer);
        }
    }

    /// <summary>
    /// Beam centre loops for an isolation strip: from half a spot off the copper out to the strip width minus half
    /// a spot, at most <paramref name="spacing"/> apart. Grouped from the copper outwards.
    /// </summary>
    internal static List<List<List<Vec2>>> LaserIsolationRings(Paths64 copper, double spot, double width, double spacing)
    {
        var first = spot / 2;
        var last = Math.Max(first, width - spot / 2);
        var count = (int)Math.Ceiling((last - first) / spacing - 1e-9) + 1;
        var levels = new List<List<List<Vec2>>>(count);
        for (var i = 0; i < count; i++)
        {
            var distance = count == 1 ? first : first + i * (last - first) / (count - 1);
            levels.Add(ClipperBridge.FromPaths(ClipperBridge.Offset(copper, distance)));
        }

        return levels;
    }

    /// <summary>Area cleared of paint: the board outline (or the copper bounds) grown by the margin.</summary>
    private static Paths64 LaserClearingArea(LaserPcbOperation operation, Dictionary<int, Contour> contours, List<List<Vec2>> copperRings,
        List<string> warnings, string label)
    {
        var margin = Math.Max(0, operation.Margin);
        var outline = operation.BoardContourIds.Where(contours.ContainsKey).Select(id => contours[id]).Where(c => c.IsClosed).ToList();
        if (outline.Count > 0)
        {
            var region = ClipperBridge.EvenOddRegion(outline.Select(c => (IReadOnlyList<Vec2>)c.Flatten(FlattenTolerance)));
            return margin > 0 ? ClipperBridge.Offset(region, margin) : region;
        }

        if (operation.BoardContourIds.Count > 0)
        {
            warnings.Add(Loc.T(
                $"{label}: контур платы не замкнут — очищается прямоугольник вокруг меди.",
                $"{label}: the board outline is not closed — the rectangle around the copper is cleared."));
        }

        var bounds = copperRings.Aggregate(Bounds2.Empty, (b, r) => b.Union(Bounds2.Of(r)));
        var rectangle = new List<Vec2>
        {
            new(bounds.MinX - margin, bounds.MinY - margin),
            new(bounds.MaxX + margin, bounds.MinY - margin),
            new(bounds.MaxX + margin, bounds.MaxY + margin),
            new(bounds.MinX - margin, bounds.MaxY + margin),
        };
        return ClipperBridge.ToPaths(new[] { rectangle });
    }

    /// <summary>Burns closed loops, each time the one nearest to the beam, starting at its nearest point.</summary>
    private static void BurnRings(List<List<Vec2>> rings, double power, double z, PathWriter writer)
    {
        var remaining = new List<List<Vec2>>(rings);
        while (remaining.Count > 0)
        {
            var (index, ring) = Nearest(remaining, writer.Position.XY);
            remaining.RemoveAt(index);
            var burn = Polyline.RotateToNearest(ring, writer.Position.XY);
            MoveLaserTo(writer, burn[0], z);
            foreach (var p in burn.Skip(1))
            {
                writer.BurnTo(p, power);
            }

            writer.BurnTo(burn[0], power);
        }
    }
}
