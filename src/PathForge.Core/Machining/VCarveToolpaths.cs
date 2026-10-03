using Clipper2Lib;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

public static partial class ToolpathGenerator
{
    private const int MaxVCarveRings = 20000;

    /// <summary>One V-carve pass: a ring (or the needed part of it) at a fixed distance from the walls.</summary>
    private sealed record VCarvePass(List<Vec2> Points, bool Closed, double Offset, double Z);

    /// <summary>
    /// V-carving by offsets: a V-bit whose centre runs at distance d from the wall and at the depth where its
    /// cutting radius equals d touches the wall exactly at the top edge, and its cone also forms the correct
    /// slope everywhere between that path and the wall. Rings are generated every <see cref="VCarveOperation.StepMm"/>;
    /// a ring is only kept where the next ring does not already form the slope (along the centre line of the
    /// shape and into sharp corners). Beyond the depth limit the rings are kept in full to clear a flat bottom.
    /// </summary>
    private static void GenerateVCarve(VCarveOperation operation, List<Contour> contours, OperationContext context)
    {
        var tool = context.Tool;
        if (tool.Kind != ToolKind.VBit)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: V-карвинг выполняется V-фрезой (гравёром) — выберите инструмент типа «V-фреза».", $"{context.Label}: V-carving needs a V-bit (engraver) — choose a tool of the “V-bit” type."));
            return;
        }

        var closed = contours.Where(c => c.IsClosed).ToList();
        if (closed.Count < contours.Count)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: незамкнутые контуры пропущены ({contours.Count - closed.Count} шт.).", $"{context.Label}: open contours skipped ({contours.Count - closed.Count})."));
        }

        if (closed.Count == 0)
        {
            return;
        }

        var region = ClipperBridge.EvenOddRegion(closed.Select(c => (IReadOnlyList<Vec2>)c.Flatten(FlattenTolerance)));
        var tanHalf = Math.Tan(Math.Clamp(tool.TipAngle, 1, 179) * Math.PI / 360);
        var tipRadius = Math.Max(0, tool.TipDiameter / 2);
        var step = Math.Max(0.01, operation.StepMm);
        var flatStep = operation.FlatStepMm > 0 ? operation.FlatStepMm : Math.Max(step, tool.TipDiameter * 0.8);
        var limitOffset = tipRadius + operation.Depth * tanHalf;

        double ZAt(double offset) => operation.StartZ - Math.Min(operation.Depth, Math.Max(0, offset - tipRadius) / tanHalf);

        // Offsets: cone rings up to the depth limit, then flat-bottom rings.
        var offsets = new List<double>();
        for (var d = tipRadius + Math.Min(step / 2, 0.02); d < limitOffset - 1e-9 && offsets.Count < MaxVCarveRings; d += step)
        {
            offsets.Add(d);
        }

        offsets.Add(limitOffset);
        var rings = new List<(double Offset, Paths64 Region)>();
        for (var k = 0; rings.Count < MaxVCarveRings; k++)
        {
            var d = k < offsets.Count ? offsets[k] : limitOffset + (k - offsets.Count + 1) * flatStep;
            var shrunk = ClipperBridge.Offset(region, -d);
            if (ClipperBridge.FromPaths(shrunk).Count == 0)
            {
                break;
            }

            rings.Add((d, shrunk));
        }

        if (rings.Count == 0)
        {
            context.Warnings.Add(Loc.T($"{context.Label}: контуры слишком узкие для этой V-фрезы.", $"{context.Label}: the contours are too narrow for this V-bit."));
            return;
        }

        if (rings[^1].Offset > limitOffset + 1e-9)
        {
            var flatCount = rings.Count(r => r.Offset > limitOffset + 1e-9);
            if (flatCount > 20)
            {
                context.Warnings.Add(Loc.T(
                    $"{context.Label}: широкие места выбираются V-фрезой на плоское дно ({flatCount} проходов) — " +
                    "это долго; уменьшите глубину или выберите их сначала концевой фрезой.",
                    $"{context.Label}: wide areas are cleared to a flat bottom with the V-bit ({flatCount} passes) — " +
                    "this is slow; reduce the depth or clear them first with an end mill."));
            }
        }

        var passes = new List<VCarvePass>();
        for (var k = 0; k < rings.Count; k++)
        {
            var (d, shrunk) = rings[k];
            var loops = ClipperBridge.FromPaths(shrunk);
            if (operation.Direction == CutDirection.Conventional)
            {
                loops.ForEach(r => r.Reverse());
            }

            var z = ZAt(d);
            var trim = k + 1 < rings.Count && rings[k + 1].Offset <= limitOffset + 1e-9;
            if (!trim)
            {
                passes.AddRange(loops.Select(loop => new VCarvePass(loop, true, d, z)));
                continue;
            }

            // Where the next ring runs alongside, its cone already forms this part of the slope.
            var next = rings[k + 1];
            var covered = ClipperBridge.Offset(next.Region, next.Offset - d + Math.Max(0.2 * step, 0.02));
            foreach (var loop in loops)
            {
                var open = new List<Vec2>(loop) { loop[0] };
                var pieces = JoinPieces(ClipperBridge.ClipLinesOutside(new[] { open }, covered));
                var loopLength = Polyline.Length(loop, closed: true);
                foreach (var piece in pieces)
                {
                    var length = Polyline.Length(piece, closed: false);
                    if (length >= loopLength - 1e-3 && piece[0].IsNear(piece[^1], 1e-3))
                    {
                        passes.Add(new VCarvePass(loop, true, d, z));
                    }
                    else if (length > 0.01)
                    {
                        passes.Add(new VCarvePass(piece, false, d, z));
                    }
                }
            }
        }

        CutVCarvePasses(operation, passes, step, flatStep, tipRadius, tanHalf, context);
    }

    private static void CutVCarvePasses(VCarveOperation operation, List<VCarvePass> passes, double step, double flatStep,
        double tipRadius, double tanHalf, OperationContext context)
    {
        if (passes.Count == 0)
        {
            return;
        }

        var writer = context.Writer;
        var deepest = passes.Min(p => p.Z);
        var levels = PassDepths(operation.StartZ, operation.StartZ - deepest, context.Tool.StepDown);
        var linkLimit = 3 * Math.Max(step, flatStep);
        var approach = operation.StartZ + context.Machine.ApproachClearance;
        var previousLevel = operation.StartZ;
        var lastOffset = 0.0;
        foreach (var level in levels)
        {
            var remaining = passes.Where(p => p.Z < previousLevel - 1e-9).ToList();
            previousLevel = level;
            while (remaining.Count > 0)
            {
                var from = writer.Position.XY;
                var best = 0;
                var bestDistance = double.PositiveInfinity;
                for (var i = 0; i < remaining.Count; i++)
                {
                    var distance = remaining[i].Closed
                        ? remaining[i].Points.Min(p => (p - from).LengthSquared)
                        : (remaining[i].Points[0] - from).LengthSquared;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = i;
                    }
                }

                var pass = remaining[best];
                remaining.RemoveAt(best);
                var points = pass.Closed ? Polyline.RotateToNearest(pass.Points, from) : pass.Points;
                var z = Math.Max(pass.Z, level);
                var start = points[0];
                var gap = start.DistanceTo(from);

                if (writer.Position.Z < operation.StartZ && gap <= linkLimit)
                {
                    // Short hop inside the carved area: rise just enough to clear both walls, move, go down.
                    var clearance = Math.Min(lastOffset, pass.Offset) - gap;
                    var linkZ = Math.Max(z, operation.StartZ - Math.Min(operation.Depth, Math.Max(0, clearance - tipRadius) / tanHalf));
                    linkZ = Math.Max(linkZ, writer.Position.Z);
                    writer.CutTo(writer.Position with { Z = linkZ });
                    writer.CutTo(new Vec3(start, linkZ));
                    writer.PlungeTo(z);
                }
                else
                {
                    writer.TravelTo(start);
                    writer.RapidDownTo(approach);
                    writer.PlungeTo(z);
                }

                foreach (var p in points.Skip(1))
                {
                    writer.CutTo(new Vec3(p, z));
                }

                if (pass.Closed)
                {
                    writer.CutTo(new Vec3(points[0], z));
                }

                lastOffset = pass.Offset;
            }

            writer.Retract();
        }
    }

    /// <summary>Joins pieces whose end meets the start of another (a ring split at its seam).</summary>
    private static List<List<Vec2>> JoinPieces(List<List<Vec2>> pieces)
    {
        var result = new List<List<Vec2>>(pieces);
        var joined = true;
        while (joined && result.Count > 1)
        {
            joined = false;
            for (var i = 0; i < result.Count && !joined; i++)
            {
                for (var j = 0; j < result.Count; j++)
                {
                    if (i != j && result[i][^1].IsNear(result[j][0], 1e-3))
                    {
                        result[i].AddRange(result[j].Skip(1));
                        result.RemoveAt(j);
                        joined = true;
                        break;
                    }
                }
            }
        }

        return result;
    }
}
