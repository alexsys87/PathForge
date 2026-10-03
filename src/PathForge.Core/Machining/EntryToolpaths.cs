using PathForge.Core.Geometry;

namespace PathForge.Core.Machining;

public static partial class ToolpathGenerator
{
    private const int MaxHelixLaps = 200;

    /// <summary>Chord tolerance of the lead arcs (the G-code writer turns them back into G2/G3).</summary>
    private const double LeadTolerance = 0.005;

    private static double RampTangent(OperationContext context) =>
        Math.Tan(Math.Clamp(context.Operation.RampAngle, 0.5, 45) * Math.PI / 180);

    /// <summary>
    /// Spiral down along a closed ring: whole laps, each going down evenly, ending at the start point
    /// at depth <paramref name="z"/>. As many laps as the ramp angle requires.
    /// </summary>
    private static bool TryHelix(List<Vec2> ring, double z, OperationContext context)
    {
        var writer = context.Writer;
        var drop = writer.Position.Z - z;
        if (drop <= 1e-6)
        {
            return true;
        }

        var perimeter = Polyline.Length(ring, closed: true);
        if (perimeter < 1e-3)
        {
            return false;
        }

        var laps = Math.Max(1, (int)Math.Ceiling(drop / RampTangent(context) / perimeter - 1e-9));
        if (laps > MaxHelixLaps)
        {
            return false;
        }

        var zTop = writer.Position.Z;
        var total = perimeter * laps;
        var walked = 0.0;
        for (var lap = 0; lap < laps; lap++)
        {
            for (var i = 1; i <= ring.Count; i++)
            {
                var p = ring[i % ring.Count];
                walked += ring[i - 1].DistanceTo(p);
                writer.CutTo(new Vec3(p, zTop - drop * Math.Min(1, walked / total)));
            }
        }

        writer.CutTo(new Vec3(ring[0], z));
        return true;
    }

    /// <summary>
    /// First entry into a closed ring that is cut flat afterwards (pockets, isolation): the spiral starts
    /// as far before the ring start as the ramp needs, so it costs only the ramp length and not a whole lap.
    /// </summary>
    private static bool TryHelixEntry(List<Vec2> ring, double z, OperationContext context)
    {
        var startZ = context.Operation.StartZ;
        if (z >= startZ - 1e-9)
        {
            return false;
        }

        var perimeter = Polyline.Length(ring, closed: true);
        var rampLength = (startZ - z) / RampTangent(context);
        if (rampLength >= perimeter || perimeter < 1e-3)
        {
            // Needs whole laps: the normal entry spirals from the ring start.
            return false;
        }

        // Walk backwards from the ring start to find where the ramp begins.
        var reversed = new List<Vec2>(ring.Count) { ring[0] };
        for (var i = ring.Count - 1; i > 0; i--)
        {
            reversed.Add(ring[i]);
        }

        var path = PathPrefix(reversed, closed: true, rampLength);
        path.Reverse();

        var writer = context.Writer;
        writer.TravelTo(path[0]);
        writer.RapidDownTo(startZ + context.Machine.ApproachClearance);
        writer.PlungeTo(startZ);
        var walked = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            walked += path[i - 1].DistanceTo(path[i]);
            writer.CutTo(new Vec3(path[i], startZ - (startZ - z) * Math.Min(1, walked / rampLength)));
        }

        writer.CutTo(new Vec3(ring[0], z));
        return true;
    }

    /// <summary>
    /// Profile without tabs in spiral mode: every pass is one continuous descending lap (or more if the ring
    /// is short for the ramp angle), and a final flat lap cleans the bottom. No plunges at all.
    /// </summary>
    private static void CutClosedLoopSpiral(List<Vec2> ring, List<double> passes, OperationContext context)
    {
        var writer = context.Writer;
        var startZ = context.Operation.StartZ;
        writer.TravelTo(ring[0]);
        writer.RapidDownTo(startZ + context.Machine.ApproachClearance);
        writer.PlungeTo(Math.Max(passes[0], startZ));

        var needsFlatLap = false;
        foreach (var z in passes)
        {
            if (TryHelix(ring, z, context))
            {
                needsFlatLap = true;
            }
            else
            {
                writer.PlungeTo(z);
                CutRing(ring, z, writer);
                needsFlatLap = false;
            }
        }

        if (needsFlatLap)
        {
            CutRing(ring, passes[^1], writer);
        }
    }

    /// <summary>
    /// Rotates a ring so that it starts in the middle of an edge at least <paramref name="minLength"/> long
    /// (the longest edge when there is none), choosing the one nearest to <paramref name="from"/>.
    /// </summary>
    private static List<Vec2> StartAtEdgeMiddle(List<Vec2> ring, Vec2 from, double minLength)
    {
        var count = ring.Count;
        var best = -1;
        var bestDistance = double.PositiveInfinity;
        var longest = 0;
        for (var i = 0; i < count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % count];
            var length = a.DistanceTo(b);
            if (length > ring[longest].DistanceTo(ring[(longest + 1) % count]))
            {
                longest = i;
            }

            if (length >= minLength)
            {
                var distance = ((a + b) / 2).DistanceTo(from);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
        }

        if (best < 0)
        {
            best = longest;
        }

        var result = new List<Vec2>(count + 1) { (ring[best] + ring[(best + 1) % count]) / 2 };
        for (var i = 1; i <= count; i++)
        {
            result.Add(ring[(best + i) % count]);
        }

        return result;
    }

    /// <summary>
    /// Quarter-circle lead-in ending at the ring start and lead-out starting there, both tangent to the
    /// first edge and lying on the free side. Shrinks the radius if the arcs would reach the part.
    /// </summary>
    private static (List<Vec2> In, List<Vec2> Out)? BuildLeads(List<Vec2> ring, double radius, bool freeOnLeft, bool freeInside)
    {
        var p = ring[0];
        var t = (ring[1] - p).Normalized();
        if (t.LengthSquared < 0.5)
        {
            return null;
        }

        var n = freeOnLeft ? t.PerpendicularLeft : -t.PerpendicularLeft;
        var sign = freeOnLeft ? 1.0 : -1.0;
        for (var attempt = 0; attempt < 4; attempt++, radius /= 2)
        {
            var center = p + n * radius;
            var leadIn = Arc(center, -t * radius, sign, radius);
            var leadOut = Arc(center, -n * radius, sign, radius);
            var probe = leadIn.Take(leadIn.Count - 1).Concat(leadOut.Skip(1)).Append(center);
            if (probe.All(q => Polyline.Contains(ring, q) == freeInside))
            {
                return (leadIn, leadOut);
            }
        }

        return null;
    }

    /// <summary>Quarter circle around <paramref name="center"/> starting at center + <paramref name="from"/>.</summary>
    private static List<Vec2> Arc(Vec2 center, Vec2 from, double sign, double radius)
    {
        var count = ArcSegment.SegmentCount(radius, Math.PI / 2, LeadTolerance);
        var points = new List<Vec2>(count + 1);
        for (var k = 0; k <= count; k++)
        {
            var angle = sign * Math.PI / 2 * k / count;
            var (sin, cos) = Math.SinCos(angle);
            points.Add(center + new Vec2(from.X * cos - from.Y * sin, from.X * sin + from.Y * cos));
        }

        return points;
    }

    /// <summary>
    /// Closed profile with lead arcs: each pass comes in along the lead-in, runs the ring, leaves along the
    /// lead-out and returns to the lead-in start on the free side for the next pass.
    /// </summary>
    private static void CutClosedLoopWithLeads(List<Vec2> ring, List<Vec2> leadIn, List<Vec2> leadOut, List<double> passes,
        List<(double From, double To)> tabs, double tabTop, OperationContext context)
    {
        var writer = context.Writer;
        var entryPath = leadIn.Concat(ring.Skip(1)).ToList();
        var leadLength = Polyline.Length(leadIn, closed: false);
        var rampLimit = tabs.Count > 0 ? leadLength + Math.Max(0, tabs.Min(t => t.From) - 0.1) : double.PositiveInfinity;
        for (var i = 0; i < passes.Count; i++)
        {
            var z = passes[i];
            if (i == 0)
            {
                EnterAt(entryPath, closed: false, z, rampLimit, context);
            }
            else
            {
                writer.CutTo(new Vec3(leadIn[0], writer.Position.Z));
                Descend(entryPath, closed: false, z, rampLimit, context);
            }

            foreach (var p in leadIn.Skip(1))
            {
                writer.CutTo(new Vec3(p, z));
            }

            if (tabs.Count == 0 || z >= tabTop)
            {
                CutRing(ring, z, writer);
            }
            else
            {
                CutRingWithTabs(ring, z, tabs, tabTop, writer);
            }

            foreach (var p in leadOut.Skip(1))
            {
                writer.CutTo(new Vec3(p, z));
            }
        }
    }
}
