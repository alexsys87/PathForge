using PathForge.Core.Geometry;
using PathForge.Core.Projects;

namespace PathForge.Core.Machining;

/// <summary>Turns the operations of a project into tool moves.</summary>
public static partial class ToolpathGenerator
{
    private const double FlattenTolerance = 0.01;

    public static GenerationResult Generate(CamProject project)
    {
        var result = new GenerationResult();
        var contours = project.Contours.ToDictionary(c => c.Id);

        // Toolpaths are computed in drawing coordinates with Z0 at the stock top, then moved to the work zero.
        var origin = project.Stock.OriginPoint(project.DrawingBounds());
        var zShift = project.Stock.ZShift;
        result.Origin = origin;
        result.SafeZ = project.Machine.SafeZ + zShift;
        var position = new Vec3(origin, project.Machine.SafeZ);

        foreach (var operation in project.Operations)
        {
            if (!operation.Enabled)
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(operation.Name) ? "Операция" : operation.Name;
            var tool = project.Tools.FirstOrDefault(t => t.Id == operation.ToolId);
            if (tool is null)
            {
                result.Warnings.Add($"{label}: инструмент не выбран.");
                continue;
            }

            if (operation is LaserVectorOperation or LaserRasterOperation || tool.Kind == ToolKind.Laser)
            {
                if (GenerateLaser(operation, tool, contours, position, label, result) is { } laserEnd)
                {
                    position = laserEnd;
                }

                continue;
            }

            if (tool.Diameter <= 0 || tool.StepDown <= 0)
            {
                result.Warnings.Add($"{label}: у инструмента «{tool.Name}» должны быть положительные диаметр и шаг по глубине.");
                continue;
            }

            if (operation.Depth <= 0)
            {
                result.Warnings.Add($"{label}: глубина должна быть больше нуля.");
                continue;
            }

            if (operation is ReliefOperation relief)
            {
                var reliefWriter = new PathWriter(position, project.Machine.SafeZ);
                GenerateRelief(relief, new OperationContext(project.Machine, operation, tool, reliefWriter, result.Warnings, label));
                reliefWriter.Retract();
                if (reliefWriter.Moves.Count > 0)
                {
                    result.Toolpaths.Add(new Toolpath(operation, tool, reliefWriter.Moves));
                    position = reliefWriter.Position;
                }

                continue;
            }

            var selected = new List<Contour>();
            foreach (var id in operation.ContourIds)
            {
                if (contours.TryGetValue(id, out var contour))
                {
                    selected.Add(contour);
                }
            }

            if (selected.Count == 0)
            {
                result.Warnings.Add($"{label}: не выбраны контуры.");
                continue;
            }

            // A V-bit cuts as wide as it is at the operation depth (V-carving works with the cone itself).
            var cuttingTool = tool;
            if (tool.Kind == ToolKind.VBit && operation is not VCarveOperation)
            {
                cuttingTool = tool.Clone();
                cuttingTool.Diameter = tool.CuttingDiameter(operation.Depth);
            }

            var writer = new PathWriter(position, project.Machine.SafeZ);
            var context = new OperationContext(project.Machine, operation, cuttingTool, writer, result.Warnings, label);
            switch (operation)
            {
                case ProfileOperation profile:
                    GenerateProfile(profile, selected, context);
                    break;
                case PocketOperation pocket:
                    GeneratePocket(pocket, selected, context);
                    break;
                case DrillOperation drill:
                    GenerateDrill(drill, selected, context);
                    break;
                case IsolationOperation isolation:
                    GenerateIsolation(isolation, selected, context);
                    break;
                case VCarveOperation vcarve:
                    GenerateVCarve(vcarve, selected, context);
                    break;
            }

            writer.Retract();
            if (writer.Moves.Count > 0)
            {
                result.Toolpaths.Add(new Toolpath(operation, tool, writer.Moves));
                position = writer.Position;
            }
        }

        foreach (var toolpath in result.Toolpaths)
        {
            for (var i = 0; i < toolpath.Moves.Count; i++)
            {
                var t = toolpath.Moves[i].Target;
                toolpath.Moves[i] = toolpath.Moves[i] with { Target = new Vec3(t.X - origin.X, t.Y - origin.Y, t.Z + zShift) };
            }
        }

        MachineChecks.Apply(project, result);
        return result;
    }

    /// <summary>Z levels of the successive passes, from the first (shallowest) to the bottom.</summary>
    public static List<double> PassDepths(double startZ, double depth, double stepDown)
    {
        var count = Math.Max(1, (int)Math.Ceiling(depth / stepDown - 1e-9));
        var levels = new List<double>(count);
        for (var i = 1; i <= count; i++)
        {
            levels.Add(startZ - Math.Min(depth, i * stepDown));
        }

        return levels;
    }

    private static void GenerateProfile(ProfileOperation operation, List<Contour> contours, OperationContext context)
    {
        var tool = context.Tool;
        var passes = PassDepths(operation.StartZ, operation.Depth, tool.StepDown);
        var tabTop = operation.BottomZ + operation.TabHeight;

        foreach (var contour in OrderInsideFirst(contours, context.Writer.Position.XY))
        {
            var points = contour.Flatten(FlattenTolerance);
            if (points.Count < 2)
            {
                continue;
            }

            if (!contour.IsClosed)
            {
                if (operation.Side != ProfileSide.OnLine)
                {
                    context.Warnings.Add($"{context.Label}: контур №{contour.Id} не замкнут и обработан по линии.");
                }

                CutOpenPath(points, passes, context);
                continue;
            }

            var withLeads = operation.Lead == LeadMode.Arc && operation.LeadRadius > 0 && operation.Side != ProfileSide.OnLine;
            foreach (var loop in ProfileLoops(operation, points, tool))
            {
                if (loop.Count < 3)
                {
                    continue;
                }

                if (withLeads)
                {
                    // Lead arcs need a straight-enough stretch: start in the middle of an edge, away from corners.
                    var ring = StartAtEdgeMiddle(loop, context.Writer.Position.XY, 2 * operation.LeadRadius);
                    var tabs = TabIntervals(operation, ring, tool, context, contour.Id);
                    var freeOnLeft = operation.Direction == CutDirection.Climb;
                    if (BuildLeads(ring, operation.LeadRadius, freeOnLeft, operation.Side == ProfileSide.Inside) is { } leads)
                    {
                        CutClosedLoopWithLeads(ring, leads.In, leads.Out, passes, tabs, tabTop, context);
                        continue;
                    }

                    context.Warnings.Add($"{context.Label}: контур №{contour.Id}: подвод по дуге не помещается, врезание без подвода.");
                    CutClosedLoop(ring, passes, tabs, tabTop, context);
                }
                else
                {
                    var ring = Polyline.RotateToNearest(loop, context.Writer.Position.XY);
                    var tabs = TabIntervals(operation, ring, tool, context, contour.Id);
                    CutClosedLoop(ring, passes, tabs, tabTop, context);
                }
            }
        }
    }

    /// <summary>Tool centre loops for a closed contour, oriented for the requested cut direction.</summary>
    private static IEnumerable<List<Vec2>> ProfileLoops(ProfileOperation operation, List<Vec2> points, Tool tool)
    {
        if (operation.Side == ProfileSide.OnLine)
        {
            yield return points;
            yield break;
        }

        var ring = Polyline.IsCounterClockwise(points) ? points : Enumerable.Reverse(points).ToList();
        var offset = tool.Radius + operation.Allowance;
        var delta = operation.Side == ProfileSide.Outside ? offset : -offset;
        var loops = ClipperBridge.FromPaths(ClipperBridge.Offset(ClipperBridge.ToPaths(new[] { ring }), delta));

        // Clipper keeps the region on the left of each loop. For an outside cut the part is that region,
        // so climb milling (material on the right with an M3 spindle) needs the reverse direction.
        var reverse = (operation.Side == ProfileSide.Outside) ^ (operation.Direction == CutDirection.Conventional);
        foreach (var loop in loops)
        {
            if (reverse)
            {
                loop.Reverse();
            }

            yield return loop;
        }
    }

    private static void GeneratePocket(PocketOperation operation, List<Contour> contours, OperationContext context)
    {
        var tool = context.Tool;
        var closed = contours.Where(c => c.IsClosed).ToList();
        if (closed.Count < contours.Count)
        {
            context.Warnings.Add($"{context.Label}: незамкнутые контуры пропущены ({contours.Count - closed.Count} шт.).");
        }

        if (closed.Count == 0)
        {
            return;
        }

        var region = ClipperBridge.EvenOddRegion(closed.Select(c => (IReadOnlyList<Vec2>)c.Flatten(FlattenTolerance)));
        var stepOver = tool.StepOver;
        if (stepOver <= 0 || stepOver > tool.Diameter)
        {
            context.Warnings.Add($"{context.Label}: перекрытие должно быть от 1 до 100 % диаметра.");
            return;
        }

        // Rings from the wall inwards: level 0 is the finishing ring along the wall.
        var levels = new List<List<List<Vec2>>>();
        for (var k = 0; k < 10000; k++)
        {
            var delta = -(tool.Radius + operation.Allowance + k * stepOver);
            var rings = ClipperBridge.FromPaths(ClipperBridge.Offset(region, delta));
            if (rings.Count == 0)
            {
                break;
            }

            if (operation.Direction == CutDirection.Conventional)
            {
                rings.ForEach(r => r.Reverse());
            }

            levels.Add(rings);
        }

        if (levels.Count == 0)
        {
            context.Warnings.Add($"{context.Label}: фреза не помещается в карман.");
            return;
        }

        var writer = context.Writer;
        foreach (var z in PassDepths(operation.StartZ, operation.Depth, tool.StepDown))
        {
            // Cut from the centre outwards so that the wall ring is cut last.
            for (var level = levels.Count - 1; level >= 0; level--)
            {
                var remaining = new List<List<Vec2>>(levels[level]);
                while (remaining.Count > 0)
                {
                    var (index, ring) = Nearest(remaining, writer.Position.XY);
                    remaining.RemoveAt(index);
                    var start = Polyline.RotateToNearest(ring, writer.Position.XY);

                    // Neighbouring rings are at most one step apart: stay down instead of retracting.
                    var stayDown = Math.Abs(writer.Position.Z - z) < 1e-6 &&
                                   writer.Position.XY.DistanceTo(start[0]) <= stepOver * 1.5;
                    if (stayDown)
                    {
                        writer.CutTo(new Vec3(start[0], z));
                    }
                    else
                    {
                        EnterAt(start, closed: true, z, double.PositiveInfinity, context);
                    }

                    CutRing(start, z, writer);
                }
            }

            writer.Retract();
        }
    }

    private static void GenerateIsolation(IsolationOperation operation, List<Contour> contours, OperationContext context)
    {
        var closed = contours.Where(c => c.IsClosed).ToList();
        if (closed.Count == 0)
        {
            context.Warnings.Add($"{context.Label}: выберите контуры меди (замкнутые).");
            return;
        }

        var tool = context.Tool;
        var width = tool.Diameter;
        var step = Math.Max(0.01, width * (1 - Math.Clamp(operation.OverlapPercent, 0, 90) / 100));
        var copper = ClipperBridge.EvenOddRegion(closed.Select(c => (IReadOnlyList<Vec2>)c.Flatten(FlattenTolerance)));
        var islands = ClipperBridge.FromPaths(copper).Count(r => Polyline.IsCounterClockwise(r));

        // Passes from the copper outwards; the first one defines the copper edge.
        var levels = new List<List<List<Vec2>>>();
        for (var k = 0; k < Math.Max(1, operation.Passes); k++)
        {
            var rings = ClipperBridge.FromPaths(ClipperBridge.Offset(copper, width / 2 + k * step));
            // As for an outside profile: the copper is on the left of Clipper's loops; climb needs it on the right.
            if (operation.Direction == CutDirection.Climb)
            {
                rings.ForEach(r => r.Reverse());
            }

            levels.Add(rings);
        }

        var firstOuter = levels[0].Count(r => Polyline.IsCounterClockwise(r) == (operation.Direction != CutDirection.Climb));
        if (firstOuter < islands)
        {
            context.Warnings.Add($"{context.Label}: некоторые дорожки ближе друг к другу, чем ширина реза {width:0.###} мм — " +
                                 "между ними гравёр не пройдёт и они останутся соединены. Уменьшите глубину или возьмите гравёр острее.");
        }

        var writer = context.Writer;
        foreach (var z in PassDepths(operation.StartZ, operation.Depth, tool.StepDown))
        {
            foreach (var level in levels)
            {
                var remaining = new List<List<Vec2>>(level);
                while (remaining.Count > 0)
                {
                    var (index, ring) = Nearest(remaining, writer.Position.XY);
                    remaining.RemoveAt(index);
                    var start = Polyline.RotateToNearest(ring, writer.Position.XY);
                    EnterAt(start, closed: true, z, double.PositiveInfinity, context);
                    CutRing(start, z, writer);
                }
            }
        }
    }

    private static void GenerateDrill(DrillOperation operation, List<Contour> contours, OperationContext context)
    {
        var centers = new List<Vec2>();
        foreach (var contour in contours)
        {
            if (!contour.TryGetCircle(out var center, out var radius))
            {
                context.Warnings.Add($"{context.Label}: контур №{contour.Id} не окружность и пропущен.");
                continue;
            }

            if (radius * 2 > context.Tool.Diameter + 0.1)
            {
                context.Warnings.Add($"{context.Label}: окружность №{contour.Id} (Ø{radius * 2:0.##}) больше инструмента, сверлится только центр.");
            }

            centers.Add(center);
        }

        var writer = context.Writer;
        var approach = operation.StartZ + context.Machine.ApproachClearance;
        while (centers.Count > 0)
        {
            var index = 0;
            for (var i = 1; i < centers.Count; i++)
            {
                if (centers[i].DistanceTo(writer.Position.XY) < centers[index].DistanceTo(writer.Position.XY))
                {
                    index = i;
                }
            }

            var center = centers[index];
            centers.RemoveAt(index);
            writer.TravelTo(center);
            writer.RapidDownTo(approach);

            if (operation.PeckDepth <= 0)
            {
                writer.PlungeTo(operation.BottomZ);
            }
            else
            {
                var levels = PassDepths(operation.StartZ, operation.Depth, operation.PeckDepth);
                for (var i = 0; i < levels.Count; i++)
                {
                    if (i > 0)
                    {
                        // Back into the hole quickly, stopping just above the previous depth.
                        writer.RapidDownTo(levels[i - 1] + 0.3);
                    }

                    writer.PlungeTo(levels[i]);
                    if (i < levels.Count - 1)
                    {
                        // Clear chips.
                        writer.RapidUpTo(approach);
                    }
                }
            }

            writer.RapidUpTo(approach);
        }
    }

    private static void CutOpenPath(List<Vec2> points, List<double> passes, OperationContext context)
    {
        var writer = context.Writer;
        var forward = !(writer.Position.XY.DistanceTo(points[^1]) < writer.Position.XY.DistanceTo(points[0]));
        var path = forward ? points : Enumerable.Reverse(points).ToList();
        EnterAt(path, closed: false, passes[0], double.PositiveInfinity, context);
        for (var i = 0; i < passes.Count; i++)
        {
            Descend(path, closed: false, passes[i], double.PositiveInfinity, context);
            foreach (var p in path.Skip(1))
            {
                writer.CutTo(new Vec3(p, passes[i]));
            }

            // Go back the other way on the next pass instead of travelling to the start.
            path = Enumerable.Reverse(path).ToList();
        }
    }

    private static void CutClosedLoop(List<Vec2> ring, List<double> passes, List<(double From, double To)> tabs, double tabTop, OperationContext context)
    {
        var writer = context.Writer;
        if (context.Operation.Entry == EntryMode.Helix && tabs.Count == 0)
        {
            CutClosedLoopSpiral(ring, passes, context);
            return;
        }

        // The ramp must stay in front of the first tab: tabs are never cut below their top.
        var rampLimit = tabs.Count > 0 ? Math.Max(0, tabs.Min(t => t.From) - 0.1) : double.PositiveInfinity;
        EnterAt(ring, closed: true, passes[0], rampLimit, context);
        foreach (var z in passes)
        {
            Descend(ring, closed: true, z, rampLimit, context);
            if (tabs.Count == 0 || z >= tabTop)
            {
                CutRing(ring, z, writer);
            }
            else
            {
                CutRingWithTabs(ring, z, tabs, tabTop, writer);
            }
        }
    }

    private static void CutRing(List<Vec2> ring, double z, PathWriter writer)
    {
        for (var i = 1; i <= ring.Count; i++)
        {
            writer.CutTo(new Vec3(ring[i % ring.Count], z));
        }
    }

    /// <summary>Cuts a ring at depth <paramref name="z"/>, lifting to <paramref name="tabTop"/> over tab intervals.</summary>
    private static void CutRingWithTabs(List<Vec2> ring, double z, List<(double From, double To)> tabs, double tabTop, PathWriter writer)
    {
        bool InTab(double s) => tabs.Any(t => s > t.From && s < t.To);

        var distance = 0.0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var length = a.DistanceTo(b);
            if (length < 1e-9)
            {
                continue;
            }

            // Split the edge at tab boundaries.
            var cuts = new List<double> { distance, distance + length };
            foreach (var (from, to) in tabs)
            {
                if (from > distance && from < distance + length)
                {
                    cuts.Add(from);
                }

                if (to > distance && to < distance + length)
                {
                    cuts.Add(to);
                }
            }

            cuts.Sort();
            for (var k = 0; k + 1 < cuts.Count; k++)
            {
                var pieceZ = InTab((cuts[k] + cuts[k + 1]) / 2) ? Math.Max(z, tabTop) : z;
                var end = Vec2.Lerp(a, b, (cuts[k + 1] - distance) / length);
                if (Math.Abs(writer.Position.Z - pieceZ) > 1e-9)
                {
                    writer.CutTo(writer.Position with { Z = pieceZ });
                }

                writer.CutTo(new Vec3(end, pieceZ));
            }

            distance += length;
        }

        writer.CutTo(writer.Position with { Z = z });
    }

    /// <summary>Equally spaced tab intervals along the loop (arc length), widened by the tool diameter.</summary>
    private static List<(double From, double To)> TabIntervals(ProfileOperation operation, List<Vec2> ring, Tool tool, OperationContext context, int contourId)
    {
        var tabs = new List<(double, double)>();
        if (operation.TabCount <= 0 || operation.TabHeight <= 0 || operation.TabWidth <= 0)
        {
            return tabs;
        }

        var length = Polyline.Length(ring, closed: true);
        var width = operation.TabWidth + tool.Diameter;
        if (width * operation.TabCount >= length * 0.8)
        {
            context.Warnings.Add($"{context.Label}: контур №{contourId} слишком короткий для {operation.TabCount} перемычек.");
            return tabs;
        }

        for (var i = 0; i < operation.TabCount; i++)
        {
            var center = (i + 0.5) * length / operation.TabCount;
            tabs.Add((center - width / 2, center + width / 2));
        }

        return tabs;
    }

    /// <summary>Moves over the start of <paramref name="path"/> and goes down to <paramref name="z"/>.</summary>
    private static void EnterAt(List<Vec2> path, bool closed, double z, double rampLimit, OperationContext context)
    {
        var writer = context.Writer;
        var startZ = context.Operation.StartZ;
        if (context.Operation.Entry == EntryMode.Helix && closed && double.IsPositiveInfinity(rampLimit) &&
            TryHelixEntry(path, z, context))
        {
            return;
        }

        writer.TravelTo(path[0]);
        writer.RapidDownTo(startZ + context.Machine.ApproachClearance);
        if (context.Operation.Entry != EntryMode.Plunge)
        {
            // Through the air straight down to the surface, then ramp into the material.
            writer.PlungeTo(Math.Max(z, startZ));
        }

        Descend(path, closed, z, rampLimit, context);
    }

    /// <summary>Goes down from the current height at the start of <paramref name="path"/> to <paramref name="z"/>.</summary>
    private static void Descend(List<Vec2> path, bool closed, double z, double rampLimit, OperationContext context)
    {
        var writer = context.Writer;
        var done = context.Operation.Entry switch
        {
            EntryMode.Ramp => TryRamp(path, closed, z, rampLimit, context),
            EntryMode.Helix when closed && double.IsPositiveInfinity(rampLimit) => TryHelix(path, z, context),
            EntryMode.Helix => TryRamp(path, closed, z, rampLimit, context),
            _ => false,
        };

        if (!done)
        {
            writer.PlungeTo(z);
        }
    }

    /// <summary>Longest zigzag leg along the path (mm); long legs only add travel.</summary>
    private const double MaxRampLeg = 40;

    private const int MaxRampLegs = 200;

    /// <summary>
    /// Zigzag ramp: forward along the start of the path and back, descending all the time,
    /// so that the tool ends at the start point at depth <paramref name="z"/>.
    /// </summary>
    private static bool TryRamp(List<Vec2> path, bool closed, double z, double rampLimit, OperationContext context)
    {
        var writer = context.Writer;
        var drop = writer.Position.Z - z;
        if (drop <= 1e-6)
        {
            return true;
        }

        var angle = Math.Clamp(context.Operation.RampAngle, 0.5, 45) * Math.PI / 180;
        var rampLength = drop / Math.Tan(angle);
        var available = Math.Min(Math.Min(Polyline.Length(path, closed), rampLimit), MaxRampLeg);
        if (available < Math.Max(0.1, context.Tool.Diameter * 0.25))
        {
            return false;
        }

        var legs = 2 * (int)Math.Ceiling(rampLength / (2 * available));
        if (legs > MaxRampLegs)
        {
            return false;
        }

        var legLength = rampLength / legs;
        var forward = PathPrefix(path, closed, legLength);
        var backward = Enumerable.Reverse(forward).ToList();
        var zTop = writer.Position.Z;
        for (var leg = 0; leg < legs; leg++)
        {
            var points = leg % 2 == 0 ? forward : backward;
            var walked = 0.0;
            for (var i = 1; i < points.Count; i++)
            {
                walked += points[i].DistanceTo(points[i - 1]);
                var progress = (leg + walked / legLength) / legs;
                writer.CutTo(new Vec3(points[i], zTop - drop * Math.Min(1, progress)));
            }
        }

        // Rounding safety: make sure the exact depth is reached at the start point.
        writer.CutTo(new Vec3(path[0], z));
        return true;
    }

    /// <summary>Points from the start of the path up to the given distance (last point interpolated).</summary>
    private static List<Vec2> PathPrefix(List<Vec2> path, bool closed, double distance)
    {
        var result = new List<Vec2> { path[0] };
        var walked = 0.0;
        var count = closed ? path.Count + 1 : path.Count;
        for (var i = 1; i < count; i++)
        {
            var a = path[i - 1];
            var b = path[i % path.Count];
            var length = a.DistanceTo(b);
            if (walked + length >= distance)
            {
                result.Add(length < 1e-12 ? b : Vec2.Lerp(a, b, (distance - walked) / length));
                return result;
            }

            walked += length;
            result.Add(b);
        }

        return result;
    }

    /// <summary>
    /// Orders contours so that contours lying inside others come first (the part stays attached
    /// while its inner features are cut), then by nearest neighbour.
    /// </summary>
    private static List<Contour> OrderInsideFirst(List<Contour> contours, Vec2 from)
    {
        var flattened = contours.Select(c => c.Flatten(0.1)).ToList();
        var nesting = new int[contours.Count];
        for (var i = 0; i < contours.Count; i++)
        {
            if (flattened[i].Count == 0)
            {
                continue;
            }

            for (var j = 0; j < contours.Count; j++)
            {
                if (i != j && contours[j].IsClosed && flattened[j].Count > 2 && Polyline.Contains(flattened[j], flattened[i][0]))
                {
                    nesting[i]++;
                }
            }
        }

        var ordered = new List<Contour>(contours.Count);
        var position = from;
        foreach (var group in Enumerable.Range(0, contours.Count).GroupBy(i => nesting[i]).OrderByDescending(g => g.Key))
        {
            var remaining = group.ToList();
            while (remaining.Count > 0)
            {
                var best = remaining.MinBy(i => flattened[i].Count == 0 ? double.MaxValue : flattened[i].Min(p => p.DistanceTo(position)));
                remaining.Remove(best);
                ordered.Add(contours[best]);
                if (flattened[best].Count > 0)
                {
                    position = flattened[best][0];
                }
            }
        }

        return ordered;
    }

    private static (int Index, List<Vec2> Ring) Nearest(List<List<Vec2>> rings, Vec2 from)
    {
        var bestIndex = 0;
        var bestDistance = double.PositiveInfinity;
        for (var i = 0; i < rings.Count; i++)
        {
            foreach (var p in rings[i])
            {
                var d = (p - from).LengthSquared;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestIndex = i;
                }
            }
        }

        return (bestIndex, rings[bestIndex]);
    }

    private sealed record OperationContext(MachineSettings Machine, Operation Operation, Tool Tool, PathWriter Writer, List<string> Warnings, string Label);
}
