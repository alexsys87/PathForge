using PathForge.Core.Geometry;

namespace PathForge.Core.Import;

/// <summary>Joins imported pieces whose end points touch into continuous contours.</summary>
public static class ContourBuilder
{
    /// <summary>Default maximum gap (mm) between end points that are joined.</summary>
    public const double DefaultTolerance = 0.01;

    public static List<Contour> Build(IEnumerable<ImportedPath> paths, double tolerance = DefaultTolerance)
    {
        var contours = new List<Contour>();
        var open = new List<Chain>();

        foreach (var path in paths)
        {
            var segments = path.Segments.Where(s => s.Length > 1e-9).ToList();
            if (segments.Count == 0)
            {
                continue;
            }

            var chain = new Chain(path.Layer, segments);
            if (chain.IsClosed(tolerance))
            {
                contours.Add(chain.ToContour(tolerance));
            }
            else
            {
                open.Add(chain);
            }
        }

        var index = new EndpointIndex(tolerance);
        for (var i = 0; i < open.Count; i++)
        {
            index.Add(open[i].Start, i);
            index.Add(open[i].End, i);
        }

        var used = new bool[open.Count];
        for (var i = 0; i < open.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            used[i] = true;
            var current = open[i];

            // Grow forward from the end, then backward from the start.
            while (TryTake(current.End, current.Layer, open, used, index, out var next, out var reverse))
            {
                current.Append(reverse ? next.Reversed() : next);
            }

            while (TryTake(current.Start, current.Layer, open, used, index, out var previous, out var reverse))
            {
                // A piece that ends at our start is prepended as is; one that starts there is reversed.
                current.Prepend(reverse ? previous : previous.Reversed());
            }

            contours.Add(current.ToContour(tolerance));
        }

        for (var i = 0; i < contours.Count; i++)
        {
            contours[i].Id = i + 1;
        }

        return contours;
    }

    /// <summary>
    /// Finds an unused chain with an end point near <paramref name="point"/>.
    /// <paramref name="reverse"/> is true when the chain's END touches the point.
    /// </summary>
    private static bool TryTake(Vec2 point, string layer, List<Chain> chains, bool[] used, EndpointIndex index, out Chain chain, out bool reverse)
    {
        foreach (var candidate in index.Near(point))
        {
            if (used[candidate] || chains[candidate].Layer != layer)
            {
                continue;
            }

            var c = chains[candidate];
            if (c.Start.IsNear(point, index.Tolerance))
            {
                used[candidate] = true;
                chain = c;
                reverse = false;
                return true;
            }

            if (c.End.IsNear(point, index.Tolerance))
            {
                used[candidate] = true;
                chain = c;
                reverse = true;
                return true;
            }
        }

        chain = null!;
        reverse = false;
        return false;
    }

    private sealed class Chain
    {
        private readonly List<Segment> _segments;

        public Chain(string layer, List<Segment> segments)
        {
            Layer = layer;
            _segments = segments;
        }

        public string Layer { get; }

        public Vec2 Start => _segments[0].Start;

        public Vec2 End => _segments[^1].End;

        public bool IsClosed(double tolerance) => Start.IsNear(End, tolerance);

        public Chain Reversed()
        {
            var reversed = new List<Segment>(_segments.Count);
            for (var i = _segments.Count - 1; i >= 0; i--)
            {
                reversed.Add(_segments[i].Reversed());
            }

            return new Chain(Layer, reversed);
        }

        public void Append(Chain other)
        {
            BridgeGap(_segments, End, other.Start);
            _segments.AddRange(other._segments);
        }

        public void Prepend(Chain other)
        {
            var combined = new List<Segment>(other._segments);
            BridgeGap(combined, other.End, Start);
            combined.AddRange(_segments);
            _segments.Clear();
            _segments.AddRange(combined);
        }

        public Contour ToContour(double tolerance)
        {
            if (IsClosed(tolerance))
            {
                BridgeGap(_segments, End, Start);
            }

            return new Contour(0, _segments, Layer);
        }

        /// <summary>Inserts a tiny line so that consecutive segments share exactly the same point.</summary>
        private static void BridgeGap(List<Segment> target, Vec2 from, Vec2 to)
        {
            if (!from.IsNear(to, 1e-6))
            {
                target.Add(new LineSegment(from, to));
            }
        }
    }

    /// <summary>Spatial hash of chain end points for fast neighbour lookup.</summary>
    private sealed class EndpointIndex
    {
        private readonly Dictionary<(long, long), List<int>> _cells = new();

        public EndpointIndex(double tolerance)
        {
            Tolerance = Math.Max(tolerance, 1e-9);
        }

        public double Tolerance { get; }

        public void Add(Vec2 point, int chain)
        {
            var key = Cell(point);
            if (!_cells.TryGetValue(key, out var list))
            {
                list = new List<int>();
                _cells[key] = list;
            }

            list.Add(chain);
        }

        public IEnumerable<int> Near(Vec2 point)
        {
            var (cx, cy) = Cell(point);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (_cells.TryGetValue((cx + dx, cy + dy), out var list))
                    {
                        foreach (var chain in list)
                        {
                            yield return chain;
                        }
                    }
                }
            }
        }

        private (long, long) Cell(Vec2 p) => ((long)Math.Floor(p.X / Tolerance), (long)Math.Floor(p.Y / Tolerance));
    }
}
