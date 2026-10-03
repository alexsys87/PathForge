using System.Collections.ObjectModel;
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
    }

    public TextItem Model { get; }

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
            OnPropertyChanged();
            OnPropertyChanged(nameof(FontStatus));
            OnModelChanged();
            NotifyOwner();
        }
    }

    public string FontStatus => Font is null && Model.FontName.Length > 0
        ? $"Шрифт «{Model.FontName}» не найден на этом компьютере — буквы оставлены как были."
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

    public string Layer
    {
        get => Model.Layer;
        set => Set(Model.Layer, string.IsNullOrWhiteSpace(value) ? "Текст" : value, v => Model.Layer = v);
    }

    /// <summary>Re-reads values changed outside the editor (mirroring, font list loaded).</summary>
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
    }

    protected override void OnModelChanged() => OnPropertyChanged(nameof(Display));
}
