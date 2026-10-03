using System.Globalization;
using PathForge.Core.Geometry;

namespace PathForge.Core.Grbl;

public enum GrblState
{
    Unknown,
    Idle,
    Run,
    Hold,
    Jog,
    Alarm,
    Door,
    Check,
    Home,
    Sleep,
}

/// <summary>Machine state from a GRBL 1.1 status report such as <c>&lt;Idle|MPos:1.000,2.000,0.000|FS:0,0&gt;</c>.</summary>
public sealed record GrblStatus
{
    public static readonly GrblStatus Unknown = new();

    public GrblState State { get; init; } = GrblState.Unknown;

    /// <summary>Sub-state, e.g. 0 for "Hold:0" (hold complete) and 1 for "Hold:1" (still decelerating); -1 when absent.</summary>
    public int SubState { get; init; } = -1;

    public Vec3 MachinePosition { get; init; }

    public Vec3 WorkPosition { get; init; }

    /// <summary>Work coordinate offset: WPos = MPos - WCO.</summary>
    public Vec3 WorkOffset { get; init; }

    public double Feed { get; init; }

    public double Spindle { get; init; }

    public int FeedOverride { get; init; } = 100;

    public int RapidOverride { get; init; } = 100;

    public int SpindleOverride { get; init; } = 100;

    /// <summary>Free planner blocks and free serial buffer bytes when reported (Bf field).</summary>
    public int? PlannerFree { get; init; }

    public int? BufferFree { get; init; }

    /// <summary>Active input pins (X, Y, Z limits, P probe, D door, H hold, R reset, S start).</summary>
    public string Pins { get; init; } = "";

    public bool IsHoldComplete => State == GrblState.Hold && SubState == 0;

    /// <summary>
    /// Parses a status report. Fields that are not part of every report (WCO, Ov) are taken over from
    /// <paramref name="previous"/>, as GRBL only sends them from time to time.
    /// </summary>
    public static bool TryParse(string line, GrblStatus previous, out GrblStatus status)
    {
        status = previous;
        line = line.Trim();
        if (line.Length < 3 || line[0] != '<' || line[^1] != '>')
        {
            return false;
        }

        var fields = line[1..^1].Split('|');
        var stateText = fields[0];
        var subState = -1;
        var colon = stateText.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            int.TryParse(stateText[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out subState);
            stateText = stateText[..colon];
        }

        if (!Enum.TryParse<GrblState>(stateText, ignoreCase: false, out var state))
        {
            state = GrblState.Unknown;
        }

        Vec3? mpos = null, wpos = null;
        var wco = previous.WorkOffset;
        double feed = 0, spindle = 0;
        int feedOv = previous.FeedOverride, rapidOv = previous.RapidOverride, spindleOv = previous.SpindleOverride;
        int? plannerFree = null, bufferFree = null;
        var pins = "";
        foreach (var field in fields.Skip(1))
        {
            var separator = field.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var name = field[..separator];
            var values = field[(separator + 1)..].Split(',');
            switch (name)
            {
                case "MPos":
                    mpos = Vector(values);
                    break;
                case "WPos":
                    wpos = Vector(values);
                    break;
                case "WCO":
                    wco = Vector(values) ?? wco;
                    break;
                case "FS":
                    feed = Number(values, 0);
                    spindle = Number(values, 1);
                    break;
                case "F":
                    feed = Number(values, 0);
                    break;
                case "Ov":
                    feedOv = (int)Number(values, 0, feedOv);
                    rapidOv = (int)Number(values, 1, rapidOv);
                    spindleOv = (int)Number(values, 2, spindleOv);
                    break;
                case "Bf":
                    plannerFree = (int)Number(values, 0);
                    bufferFree = (int)Number(values, 1);
                    break;
                case "Pn":
                    pins = values[0];
                    break;
            }
        }

        var machine = mpos ?? (wpos is { } w ? Add(w, wco) : previous.MachinePosition);
        var work = wpos ?? (mpos is { } m ? Subtract(m, wco) : previous.WorkPosition);
        status = new GrblStatus
        {
            State = state,
            SubState = subState,
            MachinePosition = machine,
            WorkPosition = work,
            WorkOffset = wco,
            Feed = feed,
            Spindle = spindle,
            FeedOverride = feedOv,
            RapidOverride = rapidOv,
            SpindleOverride = spindleOv,
            PlannerFree = plannerFree,
            BufferFree = bufferFree,
            Pins = pins,
        };
        return true;
    }

    private static Vec3? Vector(string[] values)
    {
        if (values.Length < 3)
        {
            return null;
        }

        return new Vec3(Number(values, 0), Number(values, 1), Number(values, 2));
    }

    private static double Number(string[] values, int index, double fallback = 0) =>
        index < values.Length && double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static Vec3 Add(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static Vec3 Subtract(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}
