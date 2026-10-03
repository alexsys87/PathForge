using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathForge.Core.Localization;
using PathForge.Core.Machining;
using PathForge.Core.Projects;
using PathForge.Core.Text;

namespace PathForge.App.ViewModels;

/// <summary>Texts written with TrueType fonts and V-carving.</summary>
public sealed partial class MainViewModel
{
    private Task? _fontScan;

    /// <summary>Recomputes <see cref="FontScanStatus"/> in the current language.</summary>
    private Func<string> _fontScanText = () => "";

    private static string TextMessagePrefix => Loc.T("Текст: ", "Text: ");

    /// <summary>TrueType fonts installed on this computer (filled in the background).</summary>
    public ObservableCollection<FontEntry> Fonts { get; } = new();

    public ObservableCollection<TextItemViewModel> Texts { get; } = new();

    [ObservableProperty]
    private TextItemViewModel? selectedText;

    [ObservableProperty]
    private string fontScanStatus = "";

    /// <summary>Folders with installed fonts: the system folder and the per-user folder of Windows 10/11.</summary>
    private static IEnumerable<string> FontFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
    }

    /// <summary>Starts reading the font list once; reading a few hundred font files takes a moment.</summary>
    private Task EnsureFontsLoaded()
    {
        return _fontScan ??= LoadFontsAsync();

        async Task LoadFontsAsync()
        {
            SetFontScanStatus(() => Loc.T("Чтение списка шрифтов…", "Reading the font list…"));
            try
            {
                var fonts = await Task.Run(() => FontCatalog.Scan(FontFolders().Where(f => f.Length > 0)));
                foreach (var font in fonts)
                {
                    Fonts.Add(font);
                }

                SetFontScanStatus(() => fonts.Count == 0
                    ? Loc.T("Шрифты (.ttf, .otf) не найдены.", "No fonts (.ttf, .otf) found.")
                    : Loc.T($"Шрифтов: {fonts.Count}", $"Fonts: {fonts.Count}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetFontScanStatus(() => Loc.T($"Не удалось прочитать шрифты: {ex.Message}", $"Could not read the fonts: {ex.Message}"));
            }

            foreach (var text in Texts)
            {
                text.Refresh();
            }
        }
    }

    private void SetFontScanStatus(Func<string> text)
    {
        _fontScanText = text;
        FontScanStatus = text();
    }

    [RelayCommand]
    private async Task AddText()
    {
        await EnsureFontsLoaded();
        var font = Fonts.FirstOrDefault(f => f.Name == "Arial") ?? Fonts.FirstOrDefault();
        if (font is null)
        {
            _dialogs.ShowError(Loc.T("Не найдено ни одного шрифта (.ttf, .otf).", "No fonts (.ttf, .otf) found."));
            return;
        }

        // Below the drawing if there is one, otherwise at the drawing origin.
        var bounds = _project.DrawingBounds();
        var item = new TextItem
        {
            Text = Loc.T("Текст", "Text"),
            FontPath = font.Path,
            FontIndex = font.Index,
            FontName = font.Name,
            X = bounds.IsEmpty ? 0 : bounds.MinX,
            Y = bounds.IsEmpty ? 0 : bounds.MinY - 15,
        };
        _project.Texts.Add(item);
        var vm = new TextItemViewModel(item, Fonts, OnTextChanged);
        Texts.Add(vm);
        RebuildText(item);
        SelectedText = vm;
        SelectTextContours();
        RefreshLayers();
        OnProjectChanged();
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RemoveText()
    {
        if (SelectedText is null)
        {
            return;
        }

        var index = Texts.IndexOf(SelectedText);
        var ids = _project.Contours.Where(c => c.TextId == SelectedText.Model.Id).Select(c => c.Id).ToList();
        TextBuilder.Remove(_project, SelectedText.Model);
        SelectedContourIds.ExceptWith(ids);
        Texts.RemoveAt(index);
        SelectedText = Texts.Count == 0 ? null : Texts[Math.Min(index, Texts.Count - 1)];
        RefreshLayers();
        OnSelectionChanged();
        OnProjectChanged();
    }

    [RelayCommand]
    private void SelectTextContours()
    {
        if (SelectedText is null)
        {
            return;
        }

        SelectedContourIds.Clear();
        SelectedContourIds.UnionWith(_project.Contours.Where(c => c.TextId == SelectedText.Model.Id).Select(c => c.Id));
        OnSelectionChanged();
    }

    [RelayCommand]
    private void AddVCarve()
    {
        // Prefer a wide V-bit (60°/90°) over a narrow PCB engraver.
        var vbit = _project.Tools.Where(t => t.Kind == ToolKind.VBit).MaxBy(t => t.TipAngle);
        if (vbit is null)
        {
            Messages.Add(Loc.T(
                "Для V-карвинга нужна V-фреза: «Инструменты» → пресет «V-карвинг: V-фреза 60°» или «90°».",
                "V-carving needs a V-bit: “Tools” → preset “V-carving: V-bit 60°” or “90°”."));
        }

        AddOperation(new VCarveOperation { Name = Loc.T("V-карвинг", "V-carving") + $" {Operations.Count + 1}", Depth = 2 }, vbit);
    }

    partial void OnSelectedTextChanged(TextItemViewModel? value)
    {
        if (value is not null)
        {
            _ = EnsureFontsLoaded();
        }
    }

    private void OnTextChanged(TextItem item)
    {
        RebuildText(item);
        RefreshLayers();
        OnSelectionChanged();
        OnProjectChanged();
    }

    /// <summary>Rebuilds the letters of a text; the selection follows the new contours.</summary>
    private bool RebuildText(TextItem item)
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            // Messages written before a language switch have the prefix of the other language.
            if (Messages[i].StartsWith("Текст: ", StringComparison.Ordinal) || Messages[i].StartsWith("Text: ", StringComparison.Ordinal))
            {
                Messages.RemoveAt(i);
            }
        }

        if (string.IsNullOrEmpty(item.FontPath) || !File.Exists(item.FontPath))
        {
            Messages.Add(TextMessagePrefix + Loc.T(
                $"шрифт «{item.FontName}» не найден, буквы не обновлены.",
                $"the font “{item.FontName}” was not found, the letters were not updated."));
            return false;
        }

        try
        {
            var font = FontCatalog.Load(item.FontPath, item.FontIndex);
            var oldIds = _project.Contours.Where(c => c.TextId == item.Id).Select(c => c.Id).ToList();
            var wasSelected = oldIds.Any(SelectedContourIds.Contains);
            foreach (var warning in TextBuilder.Apply(_project, item, font))
            {
                Messages.Add(TextMessagePrefix + warning);
            }

            SelectedContourIds.ExceptWith(oldIds);
            if (wasSelected)
            {
                SelectedContourIds.UnionWith(_project.Contours.Where(c => c.TextId == item.Id).Select(c => c.Id));
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or NotSupportedException
                                       or IndexOutOfRangeException or ArgumentException)
        {
            Messages.Add(TextMessagePrefix + Loc.T(
                $"не удалось прочитать шрифт «{item.FontName}»: {ex.Message}",
                $"could not read the font “{item.FontName}”: {ex.Message}"));
            return false;
        }
    }

    /// <summary>Text items of a freshly loaded project.</summary>
    private void LoadTexts()
    {
        Texts.Clear();
        foreach (var item in _project.Texts)
        {
            Texts.Add(new TextItemViewModel(item, Fonts, OnTextChanged));
        }

        SelectedText = null;
    }

    /// <summary>After the drawing was replaced, the texts are written again.</summary>
    private void RebuildAllTexts()
    {
        foreach (var item in _project.Texts)
        {
            RebuildText(item);
        }
    }

    /// <summary>The drawing was mirrored: texts follow so that rebuilding them keeps the mirrored letters.</summary>
    private void MirrorTexts()
    {
        foreach (var text in Texts)
        {
            TextBuilder.MirrorX(text.Model);
            text.Refresh();
        }
    }
}
