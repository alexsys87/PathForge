using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>Corner relief of profiles, V-bit chamfers and helically milled holes (boxes and front panels).</summary>
public static partial class ToolpathGenerator
{
    /// <summary>A tool centre loop turning sharper than this (degrees) has a corner the cutter cannot reach.</summary>
    private const double SharpTurnDegrees = 30;

    /// <summary>
    /// Adds a relief cut at every sharp vertex of a tool centre loop. Clipper's round joins wrap the cutter around
    /// the corners it can reach, so a sharp vertex is where the material corner lies on the outside of the turn,
    /// a cutter radius beyond the path: the relief goes there until the cutter touches the corner point.
    /// </summary>
    internal static List<Vec2> RelieveCorners(List<Vec2> loop, double radius, CornerRelief mode)
    {
        if (mode == CornerRelief.None || radius <= 0 || loop.Count < 3)
        {
            return loop;
        }

        var result = new List<Vec2>(loop.Count + 8);
        var cosLimit = Math.Cos(SharpTurnDegrees * Math.PI / 180);
        for (var i = 0; i < loop.Count; i++)
        {
            var v = loop[i];
            result.Add(v);
            var previous = loop[(i - 1 + loop.Count) % loop.Count];
            var next = loop[(i + 1) % loop.Count];
            var inLength = v.DistanceTo(previous);
            var outLength = next.DistanceTo(v);
            if (inLength < 1e-6 || outLength < 1e-6)
            {
                continue;
            }

            var a = (v - previous) * (1 / inLength);
            var b = (next - v) * (1 / outLength);
            var dot = a.X * b.X + a.Y * b.Y;
            if (dot > cosLimit || dot < -0.95)
            {
                continue;
            }

            // Normals pointing to the outside of the turn; the corner is where both edges, offset by the radius, meet.
            var left = a.X * b.Y - a.Y * b.X > 0;
            Vec2 Outside(Vec2 d) => left ? new Vec2(d.Y, -d.X) : new Vec2(-d.Y, d.X);
            var corner = v + (Outside(a) + Outside(b)) * (radius / (1 + dot));
            var reach = corner.DistanceTo(v) - radius;
            if (reach < 1e-4)
            {
                continue;
            }

            Vec2 relief;
            if (mode == CornerRelief.Dogbone)
            {
                relief = v + (corner - v) * (reach / corner.DistanceTo(v));
            }
            else
            {
                // Along the longer edge: forward along the incoming one or back along the outgoing one.
                var direction = inLength >= outLength ? a : b * -1;
                relief = v + direction * DistanceAlongToCircle(v, direction, corner, radius);
            }

            result.Add(relief);
            result.Add(v);
        }

        return result;
    }

    /// <summary>Smallest t ≥ 0 with |p + t·d − c| = r (d a unit vector); the nearest approach when the line just misses it.</summary>
    private static double DistanceAlongToCircle(Vec2 p, Vec2 d, Vec2 c, double r)
    {
        var f = p - c;
        var b = f.X * d.X + f.Y * d.Y;
        var q = f.LengthSquared - r * r;
        // A T-bone's line runs along the edge, tangent to the circle: rounding may push it just outside.
        var root = Math.Sqrt(Math.Max(0, b * b - q));
        var t = -b - root;
        return t >= 0 ? t : Math.Max(0, -b + root);
    }

    private static void GenerateChamfer(ChamferOperation operation, List<Contour> contours, OperationContext context)
    {
        var tool = context.Tool;
        if (tool.Kind != ToolKind.VBit)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: для фаски выберите V-фрезу (гравёр).", $"{context.Label}: choose a V-bit (engraver) for a chamfer."));
            return;
        }

        var depth = ChamferOperation.TipDepth(tool, operation.ChamferWidth);
        if (depth <= 0)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: ширина фаски должна быть больше нуля.", $"{context.Label}: the chamfer width must be greater than zero."));
            return;
        }

        var maxWidth = Math.Max(0, tool.Diameter - tool.TipDiameter) / 2;
        if (operation.ChamferWidth > maxWidth + 1e-9)
        {
            context.Warnings.Add(Loc.T(
                $"{context.Label}: фаска {operation.ChamferWidth:0.##} мм шире конуса фрезы ({maxWidth:0.##} мм) — сделайте её в два прохода или возьмите фрезу больше.",
                $"{context.Label}: the {operation.ChamferWidth:0.##} mm chamfer is wider than the bit's cone ({maxWidth:0.##} mm) — make it in two goes or use a larger bit."));
        }

        // The tip runs half its width off the edge: the cone then meets the surface the chamfer width inside it.
        var offset = tool.TipDiameter / 2;
        var passes = PassDepths(operation.StartZ, depth, tool.StepDown);
        foreach (var contour in OrderInsideFirst(contours, context.Writer.Position.XY))
        {
            if (!contour.IsClosed)
            {
                context.Warnings.Add(Loc.T($"{context.Label}: контур №{contour.Id} не замкнут — фаска только по замкнутым.", $"{context.Label}: contour #{contour.Id} is open — chamfers need closed contours."));
                continue;
            }

            var points = contour.Flatten(FlattenTolerance);
            var ring = Polyline.IsCounterClockwise(points) ? points : Enumerable.Reverse(points).ToList();
            var loops = ClipperBridge.FromPaths(ClipperBridge.Offset(ClipperBridge.ToPaths(new[] { ring }), operation.Side == ProfileSide.Inside ? -offset : offset));
            if (loops.Count == 0)
            {
                context.Warnings.Add(Loc.T($"{context.Label}: контур №{contour.Id} меньше кончика фрезы.", $"{context.Label}: contour #{contour.Id} is smaller than the bit's tip."));
                continue;
            }

            var reverse = (operation.Side != ProfileSide.Inside) ^ (operation.Direction == CutDirection.Conventional);
            foreach (var loop in loops.Where(l => l.Count >= 3))
            {
                if (reverse)
                {
                    loop.Reverse();
                }

                CutClosedLoop(Polyline.RotateToNearest(loop, context.Writer.Position.XY), passes, new List<(double, double)>(), double.NegativeInfinity, context);
            }
        }
    }

    private static void GenerateHelixHoles(HelixHoleOperation operation, List<Contour> contours, OperationContext context)
    {
        var tool = context.Tool;
        var holes = new List<(Vec2 Center, double Diameter, int Id)>();
        foreach (var contour in contours)
        {
            if (contour.TryGetCircle(out var center, out var radius))
            {
                holes.Add((center, operation.HoleDiameter > 0 ? operation.HoleDiameter : 2 * radius, contour.Id));
            }
            else
            {
                context.Warnings.Add(Loc.T($"{context.Label}: контур №{contour.Id} не окружность — пропущен.", $"{context.Label}: contour #{contour.Id} is not a circle and was skipped."));
            }
        }

        var writer = context.Writer;
        while (holes.Count > 0)
        {
            var hole = holes.MinBy(h => h.Center.DistanceTo(writer.Position.XY));
            holes.Remove(hole);
            var top = operation.StartZ;
            var depth = operation.Depth;
            if (operation.CounterboreDiameter > hole.Diameter + 1e-6)
            {
                var counterbore = Math.Clamp(operation.CounterboreDepth, 0, depth);
                MillHelixHole(hole.Center, operation.CounterboreDiameter, top, counterbore, hole.Id, operation, context);
                top -= counterbore;
                depth -= counterbore;
            }

            if (depth > 1e-6)
            {
                MillHelixHole(hole.Center, hole.Diameter, top, depth, hole.Id, operation, context);
            }
        }
    }

    /// <summary>
    /// One hole: rings from the centre out (the last one finishes the wall), each a helix from <paramref name="top"/>
    /// down by <paramref name="depth"/> and a full circle at the bottom.
    /// </summary>
    private static void MillHelixHole(Vec2 center, double diameter, double top, double depth, int contourId, HelixHoleOperation operation, OperationContext context)
    {
        var tool = context.Tool;
        var writer = context.Writer;
        var outer = (diameter - tool.Diameter) / 2;
        if (outer < -0.01)
        {
            context.Warnings.Add(Loc.T(
                $"{context.Label}: отверстие №{contourId} (Ø{diameter:0.##}) меньше фрезы Ø{tool.Diameter:0.##} — пропущено.",
                $"{context.Label}: hole #{contourId} (Ø{diameter:0.##}) is smaller than the Ø{tool.Diameter:0.##} tool and was skipped."));
            return;
        }

        var bottom = top - depth;
        var approach = top + context.Machine.ApproachClearance;
        if (outer < 0.05)
        {
            // As wide as the cutter: plunge like a drill.
            writer.TravelTo(center);
            writer.RapidDownTo(approach);
            writer.PlungeTo(bottom);
            writer.Retract();
            return;
        }

        var step = Math.Max(0.05, tool.StepOver);
        var radii = new List<double>();
        for (var r = outer; ; r -= step)
        {
            radii.Add(Math.Max(r, 0.05));
            if (r <= tool.Radius)
            {
                break;
            }
        }

        radii.Reverse();
        var ccw = operation.Direction == CutDirection.Climb;
        var rampTan = Math.Tan(Math.Clamp(operation.RampAngle, 0.5, 45) * Math.PI / 180);
        foreach (var r in radii)
        {
            // Depth per turn: the ramp angle on this circumference, at most the tool's step-down.
            var pitch = Math.Min(tool.StepDown, 2 * Math.PI * r * rampTan);
            var turns = Math.Max(1, (int)Math.Ceiling(depth / pitch - 1e-9));
            var segmentsPerTurn = Math.Clamp((int)Math.Ceiling(2 * Math.PI * r / 0.2), 16, 180);
            var start = center + new Vec2(r, 0);
            writer.TravelTo(start);
            writer.RapidDownTo(approach);
            writer.CutTo(new Vec3(start, top));
            var total = turns * segmentsPerTurn;
            for (var k = 1; k <= total + segmentsPerTurn; k++)
            {
                // Down along the helix, then one more flat turn at the bottom.
                var angle = (ccw ? 1 : -1) * 2 * Math.PI * k / segmentsPerTurn;
                var z = top - depth * Math.Min(1, (double)k / total);
                writer.CutTo(new Vec3(center + new Vec2(Math.Cos(angle) * r, Math.Sin(angle) * r), z));
            }

            // Off the wall before going up.
            writer.CutTo(new Vec3(center, bottom));
            writer.Retract();
        }
    }
}
