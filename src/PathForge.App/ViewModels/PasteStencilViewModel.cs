using PathForge.Core.Machining;

namespace PathForge.App.ViewModels;

/// <summary>Settings of the solder paste stencil (kept for the session, not in the project).</summary>
public sealed class PasteStencilViewModel : ModelWrapper
{
    public PasteStencilViewModel()
        : base(() => { })
    {
    }

    public PasteStencilSettings Model { get; } = new();

    public double Reduction
    {
        get => Model.Reduction;
        set => Set(Model.Reduction, Math.Clamp(value, 0, 1), v => Model.Reduction = v);
    }

    public double FrameMargin
    {
        get => Model.FrameMargin;
        set => Set(Model.FrameMargin, Math.Clamp(value, 0, 100), v => Model.FrameMargin = v);
    }
}
