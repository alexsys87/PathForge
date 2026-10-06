namespace PathForge.Core.Grbl;

/// <summary>Hold-to-move input: each axis from −1 to 1, a fraction of the jog feed (keys give ±1, a joystick anything between).</summary>
public readonly record struct JogVector(double X, double Y, double Z)
{
    public bool IsZero => X == 0 && Y == 0 && Z == 0;

    public static JogVector operator +(JogVector a, JogVector b) =>
        new(Math.Clamp(a.X + b.X, -1, 1), Math.Clamp(a.Y + b.Y, -1, 1), Math.Clamp(a.Z + b.Z, -1, 1));

    /// <summary>Which way each axis moves (−1, 0, 1): a change of direction needs a stop first, a change of speed does not.</summary>
    public (int X, int Y, int Z) Direction => (Math.Sign(X), Math.Sign(Y), Math.Sign(Z));
}

/// <summary>A relative jog to send: distances in millimetres and the path feed in mm/min.</summary>
public readonly record struct JogSegment(double Dx, double Dy, double Dz, double Feed);

public enum JogActionKind
{
    None,

    /// <summary>Send a jog cancel (0x85): the input was released or changed direction.</summary>
    Cancel,

    /// <summary>Send <see cref="JogAction.Segment"/>.</summary>
    Move,
}

public readonly record struct JogAction(JogActionKind Kind, JogSegment Segment = default)
{
    public static JogAction None => default;
}

/// <summary>
/// Hold-to-move jogging (keys held down, joystick deflected) the way the GRBL documentation recommends: short
/// jog segments keep the planner only a fraction of a second ahead of the machine, and a jog cancel stops it
/// as soon as the input is released. The lead is kept small on purpose: should the cancel never arrive (the
/// connection drops while a key is held), the machine still stops within <see cref="LeadMs"/>.
/// Call <see cref="Update"/> every <see cref="TickMs"/> or so.
/// </summary>
public sealed class ContinuousJog
{
    public const int TickMs = 50;

    /// <summary>Motion of one segment.</summary>
    public const int SegmentMs = 150;

    /// <summary>The planner never holds more motion than this.</summary>
    public const int LeadMs = 300;

    private long _plannedUntil;
    private bool _moving;
    private bool _waitForStop;
    private bool _waitForRelease;
    private (int X, int Y, int Z) _direction;

    /// <summary>The machine is being moved by this jog (a cancel is due when the input is released).</summary>
    public bool IsMoving => _moving;

    /// <param name="input">Current input; zero when nothing is pressed.</param>
    /// <param name="feedXY">Jog feed of X and Y at full deflection, mm/min.</param>
    /// <param name="feedZ">Jog feed of Z at full deflection, mm/min.</param>
    /// <param name="now">Milliseconds of a monotonic clock.</param>
    /// <param name="busy">A command is still waiting for its "ok": the next segment waits.</param>
    /// <param name="machineJogging">GRBL reports the Jog state (still decelerating after a cancel).</param>
    public JogAction Update(JogVector input, double feedXY, double feedZ, long now, bool busy, bool machineJogging)
    {
        if (input.IsZero)
        {
            _waitForRelease = false;
            return Stop();
        }

        if (_waitForRelease)
        {
            return JogAction.None;
        }

        if (_moving && input.Direction != _direction)
        {
            // A new direction: stop first, the planner still holds motion the old way.
            return Stop();
        }

        if (_waitForStop)
        {
            if (busy || machineJogging)
            {
                return JogAction.None;
            }

            _waitForStop = false;
        }

        if (busy || (_moving && _plannedUntil - now > LeadMs - SegmentMs))
        {
            return JogAction.None;
        }

        var vx = input.X * feedXY;
        var vy = input.Y * feedXY;
        var vz = input.Z * feedZ;
        var feed = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        if (feed < 1)
        {
            return JogAction.None;
        }

        // mm/min × ms → mm.
        const double minutes = SegmentMs / 60000.0;
        _moving = true;
        _direction = input.Direction;
        _plannedUntil = Math.Max(now, _plannedUntil) + SegmentMs;
        return new JogAction(JogActionKind.Move, new JogSegment(vx * minutes, vy * minutes, vz * minutes, feed));
    }

    /// <summary>
    /// A segment was rejected (e.g. beyond the soft limits): stop sending until the input is released, so
    /// that the same error is not repeated many times a second.
    /// </summary>
    public void Halt()
    {
        _moving = false;
        _waitForStop = true;
        _waitForRelease = true;
        _plannedUntil = 0;
    }

    /// <summary>Forgets the motion state (after a disconnect or when the input is switched off).</summary>
    public void Reset()
    {
        _moving = false;
        _waitForStop = false;
        _waitForRelease = false;
        _plannedUntil = 0;
    }

    private JogAction Stop()
    {
        if (!_moving)
        {
            return JogAction.None;
        }

        _moving = false;
        _waitForStop = true;
        _plannedUntil = 0;
        return new JogAction(JogActionKind.Cancel);
    }
}
