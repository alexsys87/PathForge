using PathForge.Core.Machining;

namespace PathForge.App.ViewModels;

public sealed class StockSettingsViewModel : ModelWrapper
{
    public StockSettingsViewModel(StockSettings model, Action changed)
        : base(changed)
    {
        Model = model;
    }

    public StockSettings Model { get; }

    public double Thickness
    {
        get => Model.Thickness;
        set => Set(Model.Thickness, Math.Max(0.1, value), v => Model.Thickness = v);
    }

    public OriginAnchor Origin
    {
        get => Model.Origin;
        set => Set(Model.Origin, value, v => Model.Origin = v);
    }

    public bool ZeroAtBottom
    {
        get => Model.ZeroAtBottom;
        set => Set(Model.ZeroAtBottom, value, v => Model.ZeroAtBottom = v);
    }
}
