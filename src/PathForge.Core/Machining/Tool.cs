using System.Text.Json.Serialization;

namespace PathForge.Core.Machining;

public enum ToolKind
{
    EndMill,
    BallNose,
    Drill,

    /// <summary>V-shaped engraver: the cutting width grows with depth.</summary>
    VBit,

    /// <summary>Laser module: power and speed are set per operation, Diameter is the spot size.</summary>
    Laser,
}

/// <summary>Cutting tool and its cutting data. Lengths in mm, feeds in mm/min.</summary>
public sealed class Tool
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Tool number used for tool changes (T word).</summary>
    public int Number { get; set; } = 1;

    public string Name { get; set; } = "Фреза Ø3,175";

    public ToolKind Kind { get; set; } = ToolKind.EndMill;

    public double Diameter { get; set; } = 3.175;

    public double SpindleRpm { get; set; } = 10000;

    public double FeedRate { get; set; } = 400;

    public double PlungeRate { get; set; } = 120;

    /// <summary>Maximum depth of cut per pass.</summary>
    public double StepDown { get; set; } = 1;

    /// <summary>Lateral step for pockets, in percent of the diameter.</summary>
    public double StepOverPercent { get; set; } = 40;

    /// <summary>V-bit: diameter of the flat tip (mm).</summary>
    public double TipDiameter { get; set; } = 0.1;

    /// <summary>V-bit: included angle of the cutting edges (degrees).</summary>
    public double TipAngle { get; set; } = 20;

    [JsonIgnore]
    public double Radius => Diameter / 2;

    /// <summary>Width of the cut at the given depth: the V-bit widens with depth, other tools cut their full diameter.</summary>
    public double CuttingDiameter(double depth) =>
        Kind == ToolKind.VBit
            ? TipDiameter + 2 * Math.Max(0, depth) * Math.Tan(Math.Clamp(TipAngle, 1, 179) * Math.PI / 360)
            : Diameter;

    [JsonIgnore]
    public double StepOver => Diameter * StepOverPercent / 100;

    public Tool Clone() => (Tool)MemberwiseClone();
}
