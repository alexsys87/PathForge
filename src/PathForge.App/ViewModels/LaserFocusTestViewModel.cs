using PathForge.Core.Machining;

namespace PathForge.App.ViewModels;

/// <summary>Settings of the laser focus test (kept for the session, not in the project).</summary>
public sealed class LaserFocusTestViewModel : ModelWrapper
{
    public LaserFocusTestViewModel()
        : base(() => { })
    {
    }

    public LaserFocusTestSettings Model { get; } = new();

    public double ZFrom
    {
        get => Model.ZFrom;
        set => Set(Model.ZFrom, Math.Clamp(value, -20, 20), v => Model.ZFrom = v);
    }

    public double ZTo
    {
        get => Model.ZTo;
        set => Set(Model.ZTo, Math.Clamp(value, -20, 20), v => Model.ZTo = v);
    }

    public int Steps
    {
        get => Model.Steps;
        set => Set(Model.Steps, Math.Clamp(value, 2, 30), v => Model.Steps = v);
    }

    public double PowerPercent
    {
        get => Model.PowerPercent;
        set => Set(Model.PowerPercent, Math.Clamp(value, 0, 100), v => Model.PowerPercent = v);
    }

    public double Speed
    {
        get => Model.Speed;
        set => Set(Model.Speed, Math.Max(1, value), v => Model.Speed = v);
    }
}
