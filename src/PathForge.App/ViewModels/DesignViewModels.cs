using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PathForge.Core.Machining;

namespace PathForge.App.ViewModels;

/// <summary>Values of the drawing edits (move, turn, scale, arrays), kept for the session.</summary>
public sealed partial class DrawingEditViewModel : ObservableObject
{
    [ObservableProperty]
    private double moveX = 10;

    [ObservableProperty]
    private double moveY;

    /// <summary>Degrees, counter-clockwise.</summary>
    [ObservableProperty]
    private double angle = 90;

    [ObservableProperty]
    private double scaleFactor = 1;

    [ObservableProperty]
    private int columns = 3;

    [ObservableProperty]
    private int rows = 1;

    [ObservableProperty]
    private double stepX = 20;

    [ObservableProperty]
    private double stepY = 20;

    [ObservableProperty]
    private int circularCount = 6;

    [ObservableProperty]
    private double circularSweep = 360;

    [ObservableProperty]
    private double centerX;

    [ObservableProperty]
    private double centerY;

    /// <summary>Copies turn with the circle (off: they are only moved).</summary>
    [ObservableProperty]
    private bool rotateCopies = true;
}

/// <summary>Settings of the finger-joint box (kept for the session).</summary>
public sealed class FingerBoxViewModel : ModelWrapper
{
    public FingerBoxViewModel()
        : base(() => { })
    {
    }

    public FingerBoxSettings Model { get; } = new();

    public double Width
    {
        get => Model.Width;
        set => Set(Model.Width, Math.Clamp(value, 5, 2000), v => Model.Width = v);
    }

    public double Depth
    {
        get => Model.Depth;
        set => Set(Model.Depth, Math.Clamp(value, 5, 2000), v => Model.Depth = v);
    }

    public double Height
    {
        get => Model.Height;
        set => Set(Model.Height, Math.Clamp(value, 5, 2000), v => Model.Height = v);
    }

    public bool InnerDimensions
    {
        get => Model.InnerDimensions;
        set => Set(Model.InnerDimensions, value, v => Model.InnerDimensions = v);
    }

    public double Thickness
    {
        get => Model.Thickness;
        set => Set(Model.Thickness, Math.Clamp(value, 0.5, 50), v => Model.Thickness = v);
    }

    public double FingerWidth
    {
        get => Model.FingerWidth;
        set => Set(Model.FingerWidth, Math.Clamp(value, 1, 500), v => Model.FingerWidth = v);
    }

    public BoxLid Lid
    {
        get => Model.Lid;
        set => Set(Model.Lid, value, v => Model.Lid = v);
    }

    public double Fit
    {
        get => Model.Fit;
        set => Set(Model.Fit, Math.Clamp(value, -1, 1), v => Model.Fit = v);
    }

    public double Gap
    {
        get => Model.Gap;
        set => Set(Model.Gap, Math.Clamp(value, 0, 100), v => Model.Gap = v);
    }
}

/// <summary>A cutout chosen from the library, its values and where it goes.</summary>
public sealed partial class PanelCutoutViewModel : ObservableObject
{
    public PanelCutoutViewModel()
    {
        Values = new ObservableCollection<CutoutValue>();
        SelectedCutout = PanelCutouts.All[0];
    }

    public IReadOnlyList<PanelCutout> Cutouts => PanelCutouts.All;

    /// <summary>The values of the chosen cutout, with their labels.</summary>
    public ObservableCollection<CutoutValue> Values { get; }

    [ObservableProperty]
    private PanelCutout selectedCutout;

    [ObservableProperty]
    private double centerX;

    [ObservableProperty]
    private double centerY;

    [ObservableProperty]
    private double angle;

    partial void OnSelectedCutoutChanged(PanelCutout value)
    {
        Values.Clear();
        foreach (var parameter in value.Parameters)
        {
            Values.Add(new CutoutValue(parameter));
        }
    }

    /// <summary>Labels follow the interface language.</summary>
    public void RefreshLanguage()
    {
        foreach (var value in Values)
        {
            value.RefreshLabel();
        }
    }
}

/// <summary>One editable value of a cutout.</summary>
public sealed partial class CutoutValue : ObservableObject
{
    private readonly CutoutParameter _parameter;

    public CutoutValue(CutoutParameter parameter)
    {
        _parameter = parameter;
        value = parameter.Default;
    }

    public string Label => _parameter.Name;

    [ObservableProperty]
    private double value;

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>Settings of the dial scale (kept for the session).</summary>
public sealed class DialScaleViewModel : ModelWrapper
{
    public DialScaleViewModel()
        : base(() => { })
    {
    }

    public DialScaleSettings Model { get; } = new();

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

    public double Radius
    {
        get => Model.Radius;
        set => Set(Model.Radius, Math.Clamp(value, 0.5, 1000), v => Model.Radius = v);
    }

    public double StartAngle
    {
        get => Model.StartAngle;
        set => Set(Model.StartAngle, value, v => Model.StartAngle = v);
    }

    public double Sweep
    {
        get => Model.Sweep;
        set => Set(Model.Sweep, Math.Clamp(value, -360, 360), v => Model.Sweep = v);
    }

    public int Divisions
    {
        get => Model.Divisions;
        set => Set(Model.Divisions, Math.Clamp(value, 1, 360), v => Model.Divisions = v);
    }

    public int MajorEvery
    {
        get => Model.MajorEvery;
        set => Set(Model.MajorEvery, Math.Clamp(value, 1, 360), v => Model.MajorEvery = v);
    }

    public double MinorLength
    {
        get => Model.MinorLength;
        set => Set(Model.MinorLength, Math.Clamp(value, 0.1, 100), v => Model.MinorLength = v);
    }

    public double MajorLength
    {
        get => Model.MajorLength;
        set => Set(Model.MajorLength, Math.Clamp(value, 0.1, 100), v => Model.MajorLength = v);
    }

    public double LabelFrom
    {
        get => Model.LabelFrom;
        set => Set(Model.LabelFrom, value, v => Model.LabelFrom = v);
    }

    public double LabelTo
    {
        get => Model.LabelTo;
        set => Set(Model.LabelTo, value, v => Model.LabelTo = v);
    }

    public double LabelHeight
    {
        get => Model.LabelHeight;
        set => Set(Model.LabelHeight, Math.Clamp(value, 0, 50), v => Model.LabelHeight = v);
    }

    public bool Arc
    {
        get => Model.Arc;
        set => Set(Model.Arc, value, v => Model.Arc = v);
    }

    public double StrokeWidth
    {
        get => Model.StrokeWidth;
        set => Set(Model.StrokeWidth, Math.Clamp(value, 0, 10), v => Model.StrokeWidth = v);
    }
}
