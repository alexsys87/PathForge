using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>
/// Waterline finishing for reliefs: at each Z level the tool runs along the contour where the tool-tip
/// height map (drop cutter) equals that level. Steep walls get evenly spaced passes that parallel lines miss.
/// </summary>
public static partial class ToolpathGenerator
{
    private const int MaxWaterlineLevels = 2000;

    /// <summary>Value outside the map: always below every level, so all contours close.</summary>
    private const float Outside = -1e9f;

    private static void CutWaterlines(ReliefOperation operation, HeightMap tip, OperationContext context)
    {
        var step = Math.Max(0.01, operation.WaterlineStepZ);
        var lowest = tip.Z.Min();
        var count = (int)Math.Ceiling(-lowest / step - 1e-9);
        if (count > MaxWaterlineLevels)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: слишком мелкий шаг по уровням ({count} уровней) — увеличьте шаг Z.", $"{context.Label}: the level step is too small ({count} levels) — increase the Z step."));
            return;
        }

        var writer = context.Writer;
        var approach = operation.StartZ + context.Machine.ApproachClearance;
        var minLength = tip.CellSize * 3;
        for (var k = 1; k <= count; k++)
        {
            // Just above the deepest point the contour shrinks to nothing: stop a hair above it.
            var level = Math.Max(-k * step, lowest + 1e-4);
            var z = operation.StartZ + level;
            var loops = WaterlineLoops(tip, level)
                .Where(l => Polyline.Length(l, closed: true) >= minLength)
                .ToList();
            foreach (var loop in loops)
            {
                // The higher side (material) is on the left; climb milling with M3 wants it on the right.
                if (operation.Direction == CutDirection.Climb)
                {
                    loop.Reverse();
                }
            }

            while (loops.Count > 0)
            {
                var (index, ring) = Nearest(loops, writer.Position.XY);
                loops.RemoveAt(index);
                var start = Polyline.RotateToNearest(ring, writer.Position.XY);
                var path = start.Append(start[0]).Select(p => new Vec3(p, z)).ToList();
                path = Simplify(path, 0.002);

                writer.TravelTo(path[0].XY);
                writer.RapidDownTo(approach);
                writer.PlungeTo(z);
                foreach (var p in path.Skip(1))
                {
                    writer.CutTo(p);
                }

                writer.Retract();
            }
        }
    }

    /// <summary>
    /// Closed contours of the map at <paramref name="level"/> (marching squares over the cell centres),
    /// oriented with the higher side on the left. Saddles are resolved with the average of the four corners.
    /// </summary>
    internal static List<List<Vec2>> WaterlineLoops(HeightMap map, double level)
    {
        var columns = map.Columns;
        var rows = map.Rows;
        // Grid points run from -1 to Columns (and -1 to Rows) so that the outside ring closes every contour.
        var width = columns + 2;

        float Value(int c, int r) => c < 0 || r < 0 || c >= columns || r >= rows ? Outside : map[c, r];
        bool Inside(int c, int r) => Value(c, r) > level;
        long HorizontalEdge(int c, int r) => ((long)(r + 1) * width + (c + 1)) * 2;
        long VerticalEdge(int c, int r) => ((long)(r + 1) * width + (c + 1)) * 2 + 1;

        Vec2 Point(int c, int r) => new(map.CellX(c), map.CellY(r));

        Vec2 Cross(int c0, int r0, int c1, int r1)
        {
            var v0 = Value(c0, r0);
            var v1 = Value(c1, r1);
            var t = Math.Abs(v1 - v0) < 1e-12 ? 0.5 : Math.Clamp((level - v0) / (v1 - v0), 0, 1);
            return Vec2.Lerp(Point(c0, r0), Point(c1, r1), t);
        }

        // Segment from one edge crossing to the next, keyed by its start edge.
        var next = new Dictionary<long, (long To, Vec2 Start)>();
        for (var r = -1; r < rows; r++)
        {
            for (var c = -1; c < columns; c++)
            {
                var mask = (Inside(c, r) ? 1 : 0) | (Inside(c + 1, r) ? 2 : 0) | (Inside(c + 1, r + 1) ? 4 : 0) | (Inside(c, r + 1) ? 8 : 0);
                if (mask is 0 or 15)
                {
                    continue;
                }

                // Edges: 0 bottom, 1 right, 2 top, 3 left.
                var edges = new[] { HorizontalEdge(c, r), VerticalEdge(c + 1, r), HorizontalEdge(c, r + 1), VerticalEdge(c, r) };
                Vec2 EdgePoint(int e) => e switch
                {
                    0 => Cross(c, r, c + 1, r),
                    1 => Cross(c + 1, r, c + 1, r + 1),
                    2 => Cross(c, r + 1, c + 1, r + 1),
                    _ => Cross(c, r, c, r + 1),
                };

                void Segment(int from, int to) => next[edges[from]] = (edges[to], EdgePoint(from));

                var centerInside = (Value(c, r) + Value(c + 1, r) + Value(c + 1, r + 1) + Value(c, r + 1)) / 4 > level;
                switch (mask)
                {
                    case 1: Segment(0, 3); break;
                    case 2: Segment(1, 0); break;
                    case 3: Segment(1, 3); break;
                    case 4: Segment(2, 1); break;
                    case 5:
                        if (centerInside)
                        {
                            Segment(0, 1);
                            Segment(2, 3);
                        }
                        else
                        {
                            Segment(0, 3);
                            Segment(2, 1);
                        }

                        break;
                    case 6: Segment(2, 0); break;
                    case 7: Segment(2, 3); break;
                    case 8: Segment(3, 2); break;
                    case 9: Segment(0, 2); break;
                    case 10:
                        if (centerInside)
                        {
                            Segment(3, 0);
                            Segment(1, 2);
                        }
                        else
                        {
                            Segment(1, 0);
                            Segment(3, 2);
                        }

                        break;
                    case 11: Segment(1, 2); break;
                    case 12: Segment(3, 1); break;
                    case 13: Segment(0, 1); break;
                    case 14: Segment(3, 0); break;
                }
            }
        }

        var loops = new List<List<Vec2>>();
        foreach (var first in next.Keys.ToList())
        {
            if (!next.ContainsKey(first))
            {
                continue;
            }

            var loop = new List<Vec2>();
            var edge = first;
            while (next.Remove(edge, out var segment))
            {
                if (loop.Count == 0 || !loop[^1].IsNear(segment.Start, 1e-9))
                {
                    loop.Add(segment.Start);
                }

                edge = segment.To;
            }

            if (loop.Count > 2 && loop[^1].IsNear(loop[0], 1e-9))
            {
                loop.RemoveAt(loop.Count - 1);
            }

            if (loop.Count >= 3)
            {
                loops.Add(loop);
            }
        }

        return loops;
    }
}
