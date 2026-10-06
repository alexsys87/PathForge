using PathForge.Core.Geometry;

namespace PathForge.Core.Machining;

/// <summary>Accumulates tool moves and keeps track of the current tool position.</summary>
internal sealed class PathWriter
{
    private const double Epsilon = 1e-6;

    private readonly double _safeZ;

    public PathWriter(Vec3 start, double safeZ)
    {
        Position = start;
        _safeZ = safeZ;
    }

    public Vec3 Position { get; private set; }

    public List<ToolMove> Moves { get; } = new();

    public void Retract()
    {
        if (Position.Z < _safeZ - Epsilon)
        {
            Add(MoveKind.Rapid, Position with { Z = _safeZ });
        }
    }

    /// <summary>Retracts and moves at safe height above <paramref name="xy"/>.</summary>
    public void TravelTo(Vec2 xy)
    {
        if (Position.XY.IsNear(xy, Epsilon))
        {
            return;
        }

        Retract();
        Add(MoveKind.Rapid, new Vec3(xy, _safeZ));
    }

    /// <summary>Rapid down to <paramref name="z"/> if the tool is above it.</summary>
    public void RapidDownTo(double z)
    {
        if (Position.Z > z + Epsilon)
        {
            Add(MoveKind.Rapid, Position with { Z = z });
        }
    }

    /// <summary>Rapid up to <paramref name="z"/> if the tool is below it.</summary>
    public void RapidUpTo(double z)
    {
        if (Position.Z < z - Epsilon)
        {
            Add(MoveKind.Rapid, Position with { Z = z });
        }
    }

    public void PlungeTo(double z)
    {
        if (Math.Abs(Position.Z - z) > Epsilon)
        {
            Add(z < Position.Z ? MoveKind.Plunge : MoveKind.Cut, Position with { Z = z });
        }
    }

    /// <summary>Feed move; pure downward moves are emitted as plunges.</summary>
    public void CutTo(Vec3 target)
    {
        if (target.DistanceTo(Position) < Epsilon)
        {
            return;
        }

        var vertical = target.XY.IsNear(Position.XY, Epsilon);
        Add(vertical && target.Z < Position.Z ? MoveKind.Plunge : MoveKind.Cut, target);
    }

    /// <summary>Laser move with the given power (0…1); power 0 moves at feed with the beam off.</summary>
    /// <param name="feed">Speed of this move (mm/min); NaN = the operation speed.</param>
    public void BurnTo(Vec2 target, double power, double feed = double.NaN)
    {
        if (target.IsNear(Position.XY, Epsilon))
        {
            return;
        }

        Add(MoveKind.Cut, new Vec3(target, Position.Z), Math.Clamp(power, 0, 1), feed);
    }

    private void Add(MoveKind kind, Vec3 target, double power = double.NaN, double feed = double.NaN)
    {
        Moves.Add(new ToolMove(kind, target, power, feed));
        Position = target;
    }
}
