using System.Collections.ObjectModel;
using System.IO;
using PathForge.Core.Localization;
using PathForge.Core.Projects;
using PathForge.Core.Text;

namespace PathForge.App.ViewModels;

/// <summary>Editor of one text item; every change rebuilds the letters.</summary>
public sealed class TextItemViewModel : ModelWrapper
{
    private readonly ObservableCollection<FontEntry> _fonts;

    public TextItemViewModel(TextItem model, ObservableCollection<FontEntry> fonts, Action<TextItem> changed)
        : base(() => changed(model))
    {
        Model = model;
        _fonts = fonts;
        LoadAxes();
    }

    public TextItem Model { get; }

    /// <summary>Axes of a variable font (weight, width…); empty for ordinary fonts.</summary>
    public ObservableCollection<FontAxisViewModel> Axes { get; } = new();

    /// <summary>Named styles of a variable font ("Bold", "Condensed"…).</summary>
    public IReadOnlyList<FontInstance> Instances { get; private set; } = Array.Empty<FontInstance>();

    public bool IsVariable => Axes.Count > 0;

    /// <summary>Picking a named style sets every axis to its value.</summary>
    public FontInstance? Instance
    {
        get => Instances.FirstOrDefault(i => i.Coordinates.All(c => Math.Abs((Model.Variation.TryGetValue(c.Key, out var v) ? v : Default(c.Key)) - c.Value) < 0.01));
        set
        {
            if (value is null)
            {
                return;
            }

            foreach (var (tag, coordinate) in value.Coordinates)
            {
                Model.Variation[tag] = coordinate;
            }

            foreach (var axis in Axes)
            {
                axis.Refresh();
            }

            OnPropertyChanged();
            OnModelChanged();
            NotifyOwner();
        }
    }

    private double Default(string tag) => Axes.FirstOrDefault(a => a.Axis.Tag == tag)?.Axis.Default ?? 0;

    /// <summary>Reads the axes of the chosen font (when it is installed here).</summary>
    private void LoadAxes()
    {
        Axes.Clear();
        Instances = Array.Empty<FontInstance>();
        try
        {
            if (Model.FontPath.Length > 0 && File.Exists(Model.FontPath))
            {
                var font = FontCatalog.Load(Model.FontPath, Model.FontIndex);
                foreach (var axis in font.Axes)
                {
                    Axes.Add(new FontAxisViewModel(axis, Model.Variation, () =>
                    {
                        OnPropertyChanged(nameof(Instance));
                        NotifyOwner();
                    }));
                }

                Instances = font.Instances;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or NotSupportedException
                                       or IndexOutOfRangeException or ArgumentException)
        {
            // The font is reported when the letters are built.
        }

        OnPropertyChanged(nameof(Instances));
        OnPropertyChanged(nameof(Instance));
        OnPropertyChanged(nameof(IsVariable));
    }

    public string Display => TextBuilder.Describe(Model);

    public string Text
    {
        get => Model.Text;
        set => Set(Model.Text, value ?? "", v => Model.Text = v);
    }

    /// <summary>Selected font from the list of installed fonts (null when the file is not installed here).</summary>
    public FontEntry? Font
    {
        get => _fonts.FirstOrDefault(f => string.Equals(f.Path, Model.FontPath, StringComparison.OrdinalIgnoreCase) && f.Index == Model.FontIndex);
        set
        {
            if (value is null || (string.Equals(value.Path, Model.FontPath, StringComparison.OrdinalIgnoreCase) && value.Index == Model.FontIndex))
            {
                return;
            }

            Model.FontPath = value.Path;
            Model.FontIndex = value.Index;
            Model.FontName = value.Name;
            // Another font has other axes: start from its default style.
            Model.Variation.Clear();
            LoadAxes();
            OnPropertyChanged();
            OnPropertyChanged(nameof(FontStatus));
            OnModelChanged();
            NotifyOwner();
        }
    }

    public string FontStatus => Font is null && Model.FontName.Length > 0
        ? Loc.T($"Шрифт «{Model.FontName}» не найден на этом компьютере — буквы оставлены как были.", $"The font “{Model.FontName}” is not installed on this computer — the letters are left as they were.")
        : "";

    public double HeightMm
    {
        get => Model.HeightMm;
        set => Set(Model.HeightMm, Math.Clamp(value, 0.1, 2000), v => Model.HeightMm = v);
    }

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

    public TextAlignment Alignment
    {
        get => Model.Alignment;
        set => Set(Model.Alignment, value, v => Model.Alignment = v);
    }

    public double LetterSpacing
    {
        get => Model.LetterSpacing;
        set => Set(Model.LetterSpacing, value, v => Model.LetterSpacing = v);
    }

    public double LineSpacing
    {
        get => Model.LineSpacing;
        set => Set(Model.LineSpacing, Math.Clamp(value, 0.3, 10), v => Model.LineSpacing = v);
    }

    public bool Kerning
    {
        get => Model.Kerning;
        set => Set(Model.Kerning, value, v => Model.Kerning = v);
    }

    public bool Mirrored
    {
        get => Model.Mirrored;
        set => Set(Model.Mirrored, value, v => Model.Mirrored = v);
    }

    public double ArcRadius
    {
        get => Model.ArcRadius;
        set => Set(Model.ArcRadius, Math.Clamp(value, -10000, 10000), v => Model.ArcRadius = v);
    }

    public string Layer
    {
        get => Model.Layer;
        set => Set(Model.Layer, string.IsNullOrWhiteSpace(value) ? Loc.T("Текст", "Text") : value, v => Model.Layer = v);
    }

    protected override void OnModelChanged() => OnPropertyChanged(nameof(Display));
}
