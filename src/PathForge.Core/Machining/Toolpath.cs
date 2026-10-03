using PathForge.Core.Geometry;

namespace PathForge.Core.Machining;

public enum MoveKind
{
    /// <summary>Positioning at maximum speed (G0).</summary>
    Rapid,

    /// <summary>Feed move straight down into the material (G1 with plunge rate).</summary>
    Plunge,

    /// <summary>Cutting feed move (G1 with feed rate).</summary>
    Cut,
}

/// <param name="Power">Laser power 0…1 for this move; NaN for spindle tools.</param>
public readonly record struct ToolMove(MoveKind Kind, Vec3 Target, double Power = double.NaN);

/// <summary>Tool moves produced by one operation.</summary>
public sealed class Toolpath
{
    public Toolpath(Operation operation, Tool tool, List<ToolMove> moves)
    {
        Operation = operation;
        Tool = tool;
        Moves = moves;
    }

    public Operation Operation { get; }

    public Tool Tool { get; }

    public List<ToolMove> Moves { get; }
}

public sealed class GenerationResult
{
    public List<Toolpath> Toolpaths { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>Drawing point that became X0 Y0 (subtract it from drawing coordinates to get program coordinates).</summary>
    public Vec2 Origin { get; set; }

    /// <summary>Safe height in program coordinates (includes the stock thickness when Z0 is at the bottom).</summary>
    public double SafeZ { get; set; } = 5;
}
