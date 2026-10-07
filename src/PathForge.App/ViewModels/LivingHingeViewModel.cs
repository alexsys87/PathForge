using PathForge.Core.Geometry;
using PathForge.Core.Machining;

namespace PathForge.App.ViewModels;

/// <summary>Area and pattern of a living hinge (kept for the session, not in the project).</summary>
public sealed class LivingHingeViewModel : ModelWrapper
{
    public LivingHingeViewModel()
        : base(() => { })
    {
    }

    public LivingHingeSettings Model { get; } = new();

    public double X
    {
        get => Model.X;
        set => Set(Model.X, value, v => Model.X = v);
    }

    public double Y
    {
        get => Model.Y;
        set => Set(Model.Y, value, v => Model.Y = v);
    }

    public double Width
    {
        get => Model.Width;
        set => Set(Model.Width, Math.Max(1, value), v => Model.Width = v);
    }

    public double Height
    {
        get => Model.Height;
        set => Set(Model.Height, Math.Max(1, value), v => Model.Height = v);
    }

    public RasterAxis Along
    {
        get => Model.Along;
        set => Set(Model.Along, value, v => Model.Along = v);
    }

    public double SlotLength
    {
        get => Model.SlotLength;
        set => Set(Model.SlotLength, Math.Clamp(value, 1, 500), v => Model.SlotLength = v);
    }

    public double Bridge
    {
        get => Model.Bridge;
        set => Set(Model.Bridge, Math.Clamp(value, 0.2, 50), v => Model.Bridge = v);
    }

    public double Spacing
    {
        get => Model.Spacing;
        set => Set(Model.Spacing, Math.Clamp(value, 0.2, 50), v => Model.Spacing = v);
    }

    public bool ToEdge
    {
        get => Model.ToEdge;
        set => Set(Model.ToEdge, value, v => Model.ToEdge = v);
    }

    /// <summary>Takes the area from a bounding box (the selected contours).</summary>
    public void SetArea(Bounds2 area)
    {
        X = Math.Round(area.MinX, 3);
        Y = Math.Round(area.MinY, 3);
        Width = Math.Round(area.Width, 3);
        Height = Math.Round(area.Height, 3);
    }
}
