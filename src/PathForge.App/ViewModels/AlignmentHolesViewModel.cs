namespace PathForge.App.ViewModels;

/// <summary>Size and distance of the alignment holes for double-sided boards (kept for the session).</summary>
public sealed class AlignmentHolesViewModel : ModelWrapper
{
    private double _diameter = 3;
    private double _margin = 5;

    public AlignmentHolesViewModel()
        : base(() => { })
    {
    }

    /// <summary>Hole (and pin) diameter, mm.</summary>
    public double Diameter
    {
        get => _diameter;
        set => Set(_diameter, Math.Clamp(value, 0.5, 10), v => _diameter = v);
    }

    /// <summary>Gap between the board outline and the hole edge, mm.</summary>
    public double Margin
    {
        get => _margin;
        set => Set(_margin, Math.Clamp(value, 0, 100), v => _margin = v);
    }
}
