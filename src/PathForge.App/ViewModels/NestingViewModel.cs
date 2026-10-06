using PathForge.Core.Machining;

namespace PathForge.App.ViewModels;

/// <summary>Sheet and gaps for laying out parts (kept for the session, not in the project).</summary>
public sealed class NestingViewModel : ModelWrapper
{
    public NestingViewModel()
        : base(() => { })
    {
    }

    public NestingSettings Model { get; } = new();

    public double SheetWidth
    {
        get => Model.SheetWidth;
        set => Set(Model.SheetWidth, Math.Clamp(value, 1, 10000), v => Model.SheetWidth = v);
    }

    public double SheetHeight
    {
        get => Model.SheetHeight;
        set => Set(Model.SheetHeight, Math.Clamp(value, 1, 10000), v => Model.SheetHeight = v);
    }

    public double Margin
    {
        get => Model.Margin;
        set => Set(Model.Margin, Math.Clamp(value, 0, 1000), v => Model.Margin = v);
    }

    public double Spacing
    {
        get => Model.Spacing;
        set => Set(Model.Spacing, Math.Clamp(value, 0, 1000), v => Model.Spacing = v);
    }

    public bool AllowRotation
    {
        get => Model.AllowRotation;
        set => Set(Model.AllowRotation, value, v => Model.AllowRotation = v);
    }

    public int Copies
    {
        get => Model.Copies;
        set => Set(Model.Copies, Math.Clamp(value, 1, 500), v => Model.Copies = v);
    }

    /// <summary>Takes the sheet size from the machine's work area once it is known.</summary>
    public void SuggestSheet(double workX, double workY, double spacing)
    {
        if (workX > 0 && workY > 0)
        {
            SheetWidth = workX;
            SheetHeight = workY;
        }

        if (spacing > 0)
        {
            Spacing = Math.Round(spacing, 1);
        }
    }
}
