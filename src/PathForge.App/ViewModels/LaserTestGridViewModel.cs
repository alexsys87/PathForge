using PathForge.Core.Machining;

namespace PathForge.App.ViewModels;

/// <summary>Settings of the laser test card (kept for the session, not in the project).</summary>
public sealed class LaserTestGridViewModel : ModelWrapper
{
    public LaserTestGridViewModel()
        : base(() => { })
    {
    }

    public LaserTestGridSettings Model { get; } = new();

    public double PowerMinPercent
    {
        get => Model.PowerMinPercent;
        set => Set(Model.PowerMinPercent, Math.Clamp(value, 0, 100), v => Model.PowerMinPercent = v);
    }

    public double PowerMaxPercent
    {
        get => Model.PowerMaxPercent;
        set => Set(Model.PowerMaxPercent, Math.Clamp(value, 0, 100), v => Model.PowerMaxPercent = v);
    }

    public int PowerSteps
    {
        get => Model.PowerSteps;
        set => Set(Model.PowerSteps, Math.Clamp(value, 1, 20), v => Model.PowerSteps = v);
    }

    public double SpeedMin
    {
        get => Model.SpeedMin;
        set => Set(Model.SpeedMin, Math.Max(1, value), v => Model.SpeedMin = v);
    }

    public double SpeedMax
    {
        get => Model.SpeedMax;
        set => Set(Model.SpeedMax, Math.Max(1, value), v => Model.SpeedMax = v);
    }

    public int SpeedSteps
    {
        get => Model.SpeedSteps;
        set => Set(Model.SpeedSteps, Math.Clamp(value, 1, 20), v => Model.SpeedSteps = v);
    }

    public double CellSize
    {
        get => Model.CellSize;
        set => Set(Model.CellSize, Math.Clamp(value, 2, 50), v => Model.CellSize = v);
    }

    public bool Cutting
    {
        get => Model.Mode == LaserVectorMode.Line;
        set => Set(Model.Mode, value ? LaserVectorMode.Line : LaserVectorMode.Fill, v => Model.Mode = v);
    }
}
