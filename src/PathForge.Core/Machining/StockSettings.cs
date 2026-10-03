using System.Text.Json.Serialization;
using PathForge.Core.Geometry;

namespace PathForge.Core.Machining;

/// <summary>Point of the drawing that becomes the work zero X0 Y0 of the program.</summary>
public enum OriginAnchor
{
    /// <summary>Drawing coordinates are used as they are.</summary>
    Drawing,
    LowerLeft,
    LowerRight,
    UpperLeft,
    UpperRight,
    Center,
}

/// <summary>Workpiece and work zero.</summary>
public sealed class StockSettings
{
    /// <summary>Stock thickness (mm).</summary>
    public double Thickness { get; set; } = 10;

    /// <summary>Corner (or centre) of the drawing's bounding box placed at X0 Y0.</summary>
    public OriginAnchor Origin { get; set; } = OriginAnchor.Drawing;

    /// <summary>Z0 is the machine bed / bottom of the stock instead of the stock top.</summary>
    public bool ZeroAtBottom { get; set; }

    /// <summary>Shift of the stock top in program coordinates (0 or the thickness).</summary>
    [JsonIgnore]
    public double ZShift => ZeroAtBottom ? Thickness : 0;

    /// <summary>Drawing point that becomes X0 Y0.</summary>
    public Vec2 OriginPoint(Bounds2 drawingBounds)
    {
        if (drawingBounds.IsEmpty || Origin == OriginAnchor.Drawing)
        {
            return Vec2.Zero;
        }

        return Origin switch
        {
            OriginAnchor.LowerLeft => new Vec2(drawingBounds.MinX, drawingBounds.MinY),
            OriginAnchor.LowerRight => new Vec2(drawingBounds.MaxX, drawingBounds.MinY),
            OriginAnchor.UpperLeft => new Vec2(drawingBounds.MinX, drawingBounds.MaxY),
            OriginAnchor.UpperRight => new Vec2(drawingBounds.MaxX, drawingBounds.MaxY),
            _ => drawingBounds.Center,
        };
    }

    public StockSettings Clone() => (StockSettings)MemberwiseClone();
}
