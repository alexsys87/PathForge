using PathForge.Core.Machining;

namespace PathForge.App.ViewModels;

/// <summary>Editable wrapper around a <see cref="Tool"/>; writes straight into the model.</summary>
public sealed class ToolViewModel : ModelWrapper
{
    public ToolViewModel(Tool model, Action changed)
        : base(changed)
    {
        Model = model;
    }

    public Tool Model { get; }

    public string Id => Model.Id;

    public string Display => $"T{Model.Number} · {Model.Name} (Ø{Model.Diameter:0.##})";

    public int Number
    {
        get => Model.Number;
        set => Set(Model.Number, Math.Max(0, value), v => Model.Number = v);
    }

    public string Name
    {
        get => Model.Name;
        set => Set(Model.Name, value, v => Model.Name = v);
    }

    public ToolKind Kind
    {
        get => Model.Kind;
        set => Set(Model.Kind, value, v => Model.Kind = v);
    }

    public double Diameter
    {
        get => Model.Diameter;
        set => Set(Model.Diameter, Math.Max(0.01, value), v => Model.Diameter = v);
    }

    public double SpindleRpm
    {
        get => Model.SpindleRpm;
        set => Set(Model.SpindleRpm, Math.Max(0, value), v => Model.SpindleRpm = v);
    }

    public double FeedRate
    {
        get => Model.FeedRate;
        set => Set(Model.FeedRate, Math.Max(1, value), v => Model.FeedRate = v);
    }

    public double PlungeRate
    {
        get => Model.PlungeRate;
        set => Set(Model.PlungeRate, Math.Max(1, value), v => Model.PlungeRate = v);
    }

    public double StepDown
    {
        get => Model.StepDown;
        set => Set(Model.StepDown, Math.Max(0.01, value), v => Model.StepDown = v);
    }

    public double StepOverPercent
    {
        get => Model.StepOverPercent;
        set => Set(Model.StepOverPercent, Math.Clamp(value, 1, 100), v => Model.StepOverPercent = v);
    }

    public double TipDiameter
    {
        get => Model.TipDiameter;
        set => Set(Model.TipDiameter, Math.Max(0.01, value), v => Model.TipDiameter = v);
    }

    public double TipAngle
    {
        get => Model.TipAngle;
        set => Set(Model.TipAngle, Math.Clamp(value, 1, 179), v => Model.TipAngle = v);
    }

    public bool IsVBit => Model.Kind == ToolKind.VBit;

    /// <summary>Hint for V-bits: cut width at 0.1 mm depth.</summary>
    public string CutWidthHint => IsVBit
        ? $"Ширина реза: {Model.CuttingDiameter(0.05):0.###} мм на глубине 0,05; {Model.CuttingDiameter(0.1):0.###} мм на 0,1"
        : "";

    protected override void OnModelChanged()
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(IsVBit));
        OnPropertyChanged(nameof(CutWidthHint));
    }
}
