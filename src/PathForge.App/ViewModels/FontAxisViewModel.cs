using PathForge.Core.Text;

namespace PathForge.App.ViewModels;

/// <summary>One design axis of a variable font with the value chosen for a text.</summary>
public sealed class FontAxisViewModel : ModelWrapper
{
    private readonly Dictionary<string, double> _variation;

    public FontAxisViewModel(FontAxis axis, Dictionary<string, double> variation, Action changed)
        : base(changed)
    {
        Axis = axis;
        _variation = variation;
    }

    public FontAxis Axis { get; }

    /// <summary>Axis name with its tag, e.g. "Weight (wght)".</summary>
    public string Label => Axis.Name == Axis.Tag ? Axis.Tag : $"{Axis.Name} ({Axis.Tag})";

    public double Minimum => Axis.Minimum;

    public double Maximum => Axis.Maximum;

    public double Value
    {
        get => _variation.TryGetValue(Axis.Tag, out var value) ? value : Axis.Default;
        set => Set(Value, Math.Round(Math.Clamp(value, Axis.Minimum, Axis.Maximum), 2), v => _variation[Axis.Tag] = v);
    }
}
