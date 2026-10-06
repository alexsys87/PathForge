using PathForge.Core.Geometry;

namespace PathForge.Core.Machining;

public sealed record ToolpathStatistics(double CutLength, double RapidLength, TimeSpan EstimatedTime)
{
    /// <summary>Rough machining time: path lengths divided by feeds, without acceleration.</summary>
    public static ToolpathStatistics Compute(IEnumerable<Toolpath> toolpaths, MachineSettings machine, Vec3 start)
    {
        double cut = 0, rapid = 0, minutes = 0;
        var position = start;
        foreach (var toolpath in toolpaths)
        {
            foreach (var move in toolpath.Moves)
            {
                var length = position.DistanceTo(move.Target);
                switch (move.Kind)
                {
                    case MoveKind.Rapid:
                        rapid += length;
                        minutes += length / Math.Max(1, machine.RapidRate);
                        break;
                    case MoveKind.Plunge:
                        cut += length;
                        minutes += length / Math.Max(1, move.FeedOr(toolpath.Tool.PlungeRate));
                        break;
                    default:
                        cut += length;
                        minutes += length / Math.Max(1, move.FeedOr(toolpath.Tool.FeedRate));
                        break;
                }

                position = move.Target;
            }
        }

        return new ToolpathStatistics(cut, rapid, TimeSpan.FromMinutes(minutes));
    }
}
