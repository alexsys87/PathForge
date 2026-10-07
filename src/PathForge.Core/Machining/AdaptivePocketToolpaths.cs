using Clipper2Lib;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>
/// Adaptive (constant load) pocket clearing and rest machining.
/// <para>
/// Adaptive: the tool centre area of the pocket is shrunk until it nearly vanishes — that core lies along the middle
/// of the pocket. A trochoidal path (small circles moving along the core) opens a channel without ever cutting
/// with the full width, then rings grow outwards from the core by a small step. Growing outward offsets are round
/// on the outside and only get narrower into inside corners, so the width of material taken never exceeds the step:
/// no overload in corners. Where a ring reaches the wall it is cut short; a final pass runs along the wall.
/// </para>
/// </summary>
public static partial class ToolpathGenerator
{
    /// <summary>A piece of an adaptive path: cut at depth, entered with a helix when <see cref="Helix"/> is set.</summary>
    private sealed record AdaptivePiece(List<Vec2> Points, bool Closed, List<Vec2>? Helix, Paths64 SafeBefore);

    private static void GenerateAdaptivePocket(PocketOperation operation, Paths64 region, OperationContext context)
    {
        var tool = context.Tool;
        var radius = tool.Radius;
        var step = Math.Clamp(tool.Diameter * operation.AdaptiveStepOverPercent / 100, 0.01, tool.Diameter * 0.5);
        var area = ClipperBridge.Offset(region, -(radius + operation.Allowance));
        var components = Components(area);
        if (components.Count == 0)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: фреза не помещается в карман.", $"{context.Label}: the tool does not fit into the pocket."));
            return;
        }

        var climb = operation.Direction == CutDirection.Climb;
        var plans = components.Select(c => AdaptivePlan(c, radius, step, climb, context)).ToList();
        var levels = PassDepths(operation.StartZ, operation.Depth, tool.StepDown);
        var writer = context.Writer;
        for (var i = 0; i < levels.Count; i++)
        {
            var z = levels[i];
            var above = i == 0 ? operation.StartZ : levels[i - 1];
            var remaining = Enumerable.Range(0, plans.Count).ToList();
            while (remaining.Count > 0)
            {
                // Next pocket: the one whose start is nearest.
                var next = remaining.MinBy(k => plans[k].Pieces[0].Points[0].DistanceTo(writer.Position.XY));
                remaining.Remove(next);
                var plan = plans[next];
                foreach (var piece in plan.Pieces)
                {
                    CutAdaptivePiece(piece, plan.Area, z, above, context);
                }
            }
        }

        writer.Retract();
    }

    private sealed record AdaptivePocketPlan(Paths64 Area, List<AdaptivePiece> Pieces);

    /// <summary>Paths of one connected tool centre area (outer ring with its islands).</summary>
    private static AdaptivePocketPlan AdaptivePlan(Paths64 area, double radius, double step, bool climb, OperationContext context)
    {
        var pieces = new List<AdaptivePiece>();
        var inner = ClipperBridge.Offset(area, -0.005);

        // Core: the last non-empty inward offset (along the middle of the pocket).
        var depth = 0.0;
        var core = area;
        for (var k = 1; k < 100000; k++)
        {
            var smaller = ClipperBridge.Offset(area, -k * step);
            if (ClipperBridge.FromPaths(smaller).Count == 0)
            {
                break;
            }

            core = smaller;
            depth = k * step;
        }

        var trochoid = Math.Min(radius * 0.5, depth);
        var safe = new Paths64();
        if (trochoid < step * 0.25)
        {
            // Narrower than about two steps: nothing to grow from, the pocket is one slot along its middle.
            context.Warnings.Add(Loc.T(
                $"{context.Label}: карман лишь немного шире фрезы — он прорезается на полную ширину фрезы. Возьмите фрезу меньше.",
                $"{context.Label}: the pocket is only slightly wider than the tool — it is cut with the full tool width. Use a smaller tool."));
            foreach (var ring in ClipperBridge.FromPaths(area))
            {
                pieces.Add(new AdaptivePiece(Orient(ring, climb), true, null, safe));
            }

            return new AdaptivePocketPlan(area, pieces);
        }

        var advance = TrochoidAdvance(radius, trochoid, step);
        foreach (var ring in ClipperBridge.FromPaths(core).Where(r => Polyline.IsCounterClockwise(r)))
        {
            var path = Trochoid(ring, trochoid, advance, climb);
            var helix = Circle(TrochoidCentre(ring), trochoid, climb, path[0]);
            pieces.Add(new AdaptivePiece(path, false, helix, safe));
            safe = ClipperBridge.Union(safe, ClipperBridge.Offset(ClipperBridge.ToPaths(new[] { ring }), trochoid));
        }

        // Rings growing outwards from the core; tight rings take less (see NextRingDistance).
        var distance = trochoid;
        for (var j = 1; j < 100000; j++)
        {
            distance = NextRingDistance(distance, radius, step);
            var grown = ClipperBridge.Offset(core, distance);
            if (ClipperBridge.Area(ClipperBridge.Difference(inner, grown)) < 1e-6)
            {
                break;
            }

            var cut = new List<List<Vec2>>();
            foreach (var ring in ClipperBridge.FromPaths(grown))
            {
                var loop = new List<Vec2>(ring) { ring[0] };
                cut.AddRange(JoinPieces(ClipperBridge.ClipLines(new[] { loop }, inner)));
            }

            foreach (var piece in cut)
            {
                var closed = piece.Count > 3 && piece[0].IsNear(piece[^1], 1e-3);
                if (closed)
                {
                    piece.RemoveAt(piece.Count - 1);
                }

                // Growing rings come out counter-clockwise: material outside, on the right — climb milling.
                if (!climb)
                {
                    piece.Reverse();
                }

                pieces.Add(new AdaptivePiece(piece, closed, null, safe));
            }

            safe = ClipperBridge.Intersect(grown, area);
        }

        AddLeftoverPasses(pieces, area, core, trochoid, radius, step, climb, safe);

        // Finishing pass along the walls (and around islands).
        foreach (var ring in ClipperBridge.FromPaths(area))
        {
            pieces.Add(new AdaptivePiece(climb ? ring : Enumerable.Reverse(ring).ToList(), true, null, safe));
        }

        return new AdaptivePocketPlan(area, pieces);
    }

    /// <summary>
    /// Where growing rings meet a wall or an island at a grazing angle (narrow passages), their ends lie far apart
    /// along it and leave material thicker than the step. Such places get passes parallel to the walls, from the
    /// inside out, before the final wall pass — otherwise that pass would cut them with a large engagement.
    /// </summary>
    private static void AddLeftoverPasses(List<AdaptivePiece> pieces, Paths64 area, Paths64 core, double trochoid, double radius,
        double step, bool climb, Paths64 safe)
    {
        var swept = new Paths64(ClipperBridge.Offset(core, trochoid + radius));
        foreach (var piece in pieces.Where(p => p.Helix is null))
        {
            var line = piece.Closed ? new List<Vec2>(piece.Points) { piece.Points[0] } : piece.Points;
            swept.AddRange(ClipperBridge.Stroke(line, radius));
        }

        var cleared = ClipperBridge.Union(swept);
        var leftover = ClipperBridge.Difference(ClipperBridge.Offset(area, radius - 0.01), cleared);
        // Only what is thicker than a step: thinner scallops along the walls are the final pass's normal work.
        var thick = ClipperBridge.Offset(ClipperBridge.Offset(leftover, -step / 2), step / 2);
        if (ClipperBridge.Area(thick) < 1e-4)
        {
            return;
        }

        var layers = 1;
        while (layers < 1000 && ClipperBridge.Area(ClipperBridge.Offset(leftover, -layers * step / 2)) > 1e-4)
        {
            layers++;
        }

        var zone = ClipperBridge.Offset(thick, radius + step);
        for (var k = layers; k >= 1; k--)
        {
            foreach (var ring in ClipperBridge.FromPaths(ClipperBridge.Offset(area, -k * step)))
            {
                var oriented = Orient(ring, climb);
                var loop = new List<Vec2>(oriented) { oriented[0] };
                foreach (var piece in JoinPieces(ClipperBridge.ClipLines(new[] { loop }, zone)).Where(p => Polyline.Length(p, closed: false) > 0.01))
                {
                    var closed = piece.Count > 3 && piece[0].IsNear(piece[^1], 1e-3);
                    if (closed)
                    {
                        piece.RemoveAt(piece.Count - 1);
                    }

                    pieces.Add(new AdaptivePiece(piece, closed, null, safe));
                }
            }
        }
    }

    private static List<Vec2> Orient(List<Vec2> ring, bool climb) => climb ? ring : Enumerable.Reverse(ring).ToList();

    /// <summary>Cuts one piece at depth <paramref name="z"/>; <paramref name="above"/> is the bottom of the previous level.</summary>
    private static void CutAdaptivePiece(AdaptivePiece piece, Paths64 area, double z, double above, OperationContext context)
    {
        var writer = context.Writer;
        var points = piece.Closed ? Polyline.RotateToNearest(piece.Points, writer.Position.XY) : piece.Points;
        var liftZ = above + context.Machine.ApproachClearance;
        if (piece.Helix is { } helix)
        {
            MoveOver(helix[0], liftZ, area, context);
            writer.PlungeTo(above);
            if (!TryHelix(helix, z, context))
            {
                writer.PlungeTo(z);
            }
        }
        else
        {
            var start = new Vec3(points[0], z);
            var atDepth = Math.Abs(writer.Position.Z - z) < 1e-6;
            if (atDepth && IsInside(writer.Position.XY, points[0], piece.SafeBefore))
            {
                writer.CutTo(start);
            }
            else
            {
                MoveOver(points[0], liftZ, area, context);
                if (piece.SafeBefore.Count == 0 && context.Operation.Entry != EntryMode.Plunge)
                {
                    // The very first path of a level in a narrow pocket: ramp along it.
                    writer.PlungeTo(above);
                    if (!TryRamp(points, piece.Closed, z, double.PositiveInfinity, context))
                    {
                        writer.PlungeTo(z);
                    }
                }
                else
                {
                    // The tool centre is over cleared material: only the edge of the cutter touches the uncut part.
                    writer.PlungeTo(z);
                }
            }
        }

        for (var i = 1; i < points.Count; i++)
        {
            writer.CutTo(new Vec3(points[i], z));
        }

        if (piece.Closed)
        {
            writer.CutTo(new Vec3(points[0], z));
        }
    }

    /// <summary>
    /// Goes over <paramref name="xy"/> at <paramref name="liftZ"/>: straight through the pocket when the way stays
    /// inside it (the level above is cleared), otherwise up to the safe height.
    /// </summary>
    private static void MoveOver(Vec2 xy, double liftZ, Paths64 area, OperationContext context)
    {
        var writer = context.Writer;
        var from = writer.Position.XY;
        if (writer.Position.Z <= liftZ + 1e-6 && IsInside(from, xy, area))
        {
            writer.RapidUpTo(liftZ);
            writer.RapidTo(xy);
        }
        else
        {
            writer.TravelTo(xy);
            writer.RapidDownTo(liftZ);
        }
    }

    /// <summary>The straight line from <paramref name="a"/> to <paramref name="b"/> lies inside the region.</summary>
    private static bool IsInside(Vec2 a, Vec2 b, Paths64 region)
    {
        if (region.Count == 0)
        {
            return false;
        }

        if (a.IsNear(b, 1e-6))
        {
            return ClipperBridge.FromPaths(region).Count(r => Polyline.Contains(r, a)) % 2 == 1;
        }

        var outside = ClipperBridge.ClipLinesOutside(new[] { new[] { a, b } }, region);
        return outside.Sum(p => Polyline.Length(p, closed: false)) < 1e-3;
    }

    /// <summary>Outer rings with the holes that lie in them, each as its own region.</summary>
    private static List<Paths64> Components(Paths64 region)
    {
        var rings = ClipperBridge.FromPaths(region);
        var outers = rings.Where(r => Polyline.IsCounterClockwise(r)).ToList();
        var holes = rings.Where(r => !Polyline.IsCounterClockwise(r)).ToList();
        var result = new List<Paths64>();
        foreach (var outer in outers)
        {
            // A hole belongs to the smallest outer ring around it.
            var mine = holes.Where(h => outers.Where(o => Polyline.Contains(o, h[0])).MinBy(o => Math.Abs(Polyline.SignedArea(o))) == outer);
            result.Add(ClipperBridge.ToPaths(new[] { (IReadOnlyList<Vec2>)outer }.Concat(mine)));
        }

        return result;
    }

    /// <summary>
    /// Advance per turn of a trochoid of radius <paramref name="loop"/> that loads a cutter of radius
    /// <paramref name="radius"/> no more than a straight cut taking <paramref name="step"/>: the front of each loop is
    /// curved around the cutter, so the advance must be smaller than the step.
    /// </summary>
    internal static double TrochoidAdvance(double radius, double loop, double step) =>
        NextRingDistance(loop, radius, step) - loop;

    /// <summary>
    /// Distance from the core of the next growing ring. Where a ring bends around the core (radius d), the material
    /// is on the outside of the bend and wraps around the cutter, so a ring at d + step would load it more than a
    /// straight cut. The next ring is placed where the cutter edge meets the previous boundary (radius d + R) at
    /// the same angle as in a straight cut taking <paramref name="step"/>: cos θ = 1 − step / R.
    /// </summary>
    internal static double NextRingDistance(double distance, double radius, double step)
    {
        var c = 1 - step / radius;
        var reach = distance + radius;
        var next = -radius * c + Math.Sqrt(radius * radius * c * c + reach * reach - radius * radius);
        return distance + Math.Clamp(next - distance, step * 0.1, step);
    }

    /// <summary>Point where a trochoid along <paramref name="ring"/> starts (its first point).</summary>
    private static Vec2 TrochoidCentre(List<Vec2> ring) => ring[0];

    /// <summary>
    /// Circles of radius <paramref name="radius"/> whose centre moves along the closed <paramref name="path"/> by
    /// <paramref name="step"/> per turn. Counter-clockwise for climb milling.
    /// </summary>
    internal static List<Vec2> Trochoid(List<Vec2> path, double radius, double step, bool climb)
    {
        var length = Polyline.Length(path, closed: true);
        var turns = Math.Max(1, (int)Math.Ceiling(length / step - 1e-9));
        var perTurn = Math.Clamp((int)Math.Ceiling(2 * Math.PI * radius / 0.25), 12, 72);
        var sign = climb ? 1.0 : -1.0;
        var points = new List<Vec2>(turns * perTurn + 1) { path[0] + new Vec2(radius, 0) };
        for (var t = 0; t < turns; t++)
        {
            for (var k = 1; k <= perTurn; k++)
            {
                var centre = PointAlong(path, length * (t + (double)k / perTurn) / turns);
                var (sin, cos) = Math.SinCos(sign * 2 * Math.PI * k / perTurn);
                points.Add(centre + new Vec2(radius * cos, radius * sin));
            }
        }

        return points;
    }

    /// <summary>Full circle (closed ring) around <paramref name="centre"/> starting at <paramref name="start"/>.</summary>
    private static List<Vec2> Circle(Vec2 centre, double radius, bool climb, Vec2 start)
    {
        var count = Math.Clamp((int)Math.Ceiling(2 * Math.PI * radius / 0.25), 12, 72);
        var sign = climb ? 1.0 : -1.0;
        var a0 = Math.Atan2(start.Y - centre.Y, start.X - centre.X);
        var ring = new List<Vec2>(count);
        for (var k = 0; k < count; k++)
        {
            var (sin, cos) = Math.SinCos(a0 + sign * 2 * Math.PI * k / count);
            ring.Add(centre + new Vec2(radius * cos, radius * sin));
        }

        return ring;
    }

    /// <summary>Point at arc length <paramref name="distance"/> along a closed polyline.</summary>
    private static Vec2 PointAlong(List<Vec2> path, double distance)
    {
        if (path.Count == 1)
        {
            return path[0];
        }

        var walked = 0.0;
        for (var i = 0; i < path.Count; i++)
        {
            var a = path[i];
            var b = path[(i + 1) % path.Count];
            var length = a.DistanceTo(b);
            if (walked + length >= distance - 1e-12)
            {
                return length < 1e-12 ? a : Vec2.Lerp(a, b, Math.Clamp((distance - walked) / length, 0, 1));
            }

            walked += length;
        }

        return path[0];
    }

    /// <summary>
    /// Rest machining: only where a larger tool (<see cref="PocketOperation.RestFromDiameter"/>) left material —
    /// corners and places narrower than it. The usual pocket rings of this tool are cut only where they touch it.
    /// </summary>
    private static void GenerateRestPocket(PocketOperation operation, Paths64 region, OperationContext context)
    {
        var tool = context.Tool;
        var bigRadius = operation.RestFromDiameter / 2;
        if (bigRadius <= tool.Radius + 1e-6)
        {
            context.Warnings.Add(Loc.T(
                $"{context.Label}: для дообработки фреза должна быть меньше предыдущей (Ø{operation.RestFromDiameter:0.###}).",
                $"{context.Label}: for rest machining the tool must be smaller than the previous one (Ø{operation.RestFromDiameter:0.###})."));
            return;
        }

        // What the larger tool cleared: its centre area grown back by its radius.
        var cleared = ClipperBridge.Offset(ClipperBridge.Offset(region, -(bigRadius + operation.RestFromAllowance)), bigRadius);
        var target = ClipperBridge.Offset(region, -operation.Allowance);
        var rest = ClipperBridge.Difference(target, cleared);
        // Slivers thinner than a hundredth of a millimetre are rounding, not material.
        rest = ClipperBridge.Offset(ClipperBridge.Offset(rest, -0.005), 0.005);
        if (ClipperBridge.Area(rest) < 1e-4)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: предыдущая фреза уже всё убрала — дообрабатывать нечего.", $"{context.Label}: the previous tool already cleared everything — nothing left for rest machining."));
            return;
        }

        // Tool centre positions where the cutter touches the rest material.
        var zone = ClipperBridge.Offset(rest, tool.Radius - 0.01);
        var stepOver = tool.StepOver;
        if (stepOver <= 0 || stepOver > tool.Diameter)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: перекрытие должно быть от 1 до 100 % диаметра.", $"{context.Label}: the step-over must be 1 to 100 % of the diameter."));
            return;
        }

        var pieces = new List<List<Vec2>>();
        for (var k = 9999; k >= 0; k--)
        {
            // Collected from the centre outwards; the wall ring comes last.
            var rings = ClipperBridge.FromPaths(ClipperBridge.Offset(region, -(tool.Radius + operation.Allowance + k * stepOver)));
            if (rings.Count == 0)
            {
                continue;
            }

            foreach (var ring in rings)
            {
                var oriented = operation.Direction == CutDirection.Conventional ? Enumerable.Reverse(ring).ToList() : ring;
                var loop = new List<Vec2>(oriented) { oriented[0] };
                pieces.AddRange(JoinPieces(ClipperBridge.ClipLines(new[] { loop }, zone)).Where(p => Polyline.Length(p, closed: false) > 0.01));
            }
        }

        if (pieces.Count == 0)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: фреза не достаёт до оставшегося материала.", $"{context.Label}: the tool cannot reach the remaining material."));
            return;
        }

        var area = ClipperBridge.Offset(region, -(tool.Radius + operation.Allowance));
        var writer = context.Writer;
        var levels = PassDepths(operation.StartZ, operation.Depth, tool.StepDown);
        for (var i = 0; i < levels.Count; i++)
        {
            var z = levels[i];
            var liftZ = (i == 0 ? operation.StartZ : levels[i - 1]) + context.Machine.ApproachClearance;
            foreach (var piece in pieces)
            {
                // The larger tool has cleared the pocket: plunges land in the open, next to the rest material.
                MoveOver(piece[0], liftZ, area, context);
                writer.PlungeTo(z);
                foreach (var p in piece.Skip(1))
                {
                    writer.CutTo(new Vec3(p, z));
                }

                writer.RapidUpTo(liftZ);
            }
        }

        writer.Retract();
    }
}
