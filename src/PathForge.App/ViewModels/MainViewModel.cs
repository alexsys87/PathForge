using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathForge.App.Services;
using PathForge.Core.GCode;
using PathForge.Core.Geometry;
using PathForge.Core.Import;
using PathForge.Core.Import.Pcb;
using PathForge.Core.Localization;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IAppearanceService _appearance;
    private readonly UndoHistory _history = new();
    private readonly DispatcherTimer _regenerateTimer;
    private CamProject _project = CamProject.CreateDefault();
    private GenerationResult _generation = new();
    private GcodeOutput? _gcodeOutput;
    private string? _shownGcode;
    private bool _suppressChanges;

    public MainViewModel(IDialogService dialogs, IAppearanceService appearance, UiPreferences preferences)
    {
        _dialogs = dialogs;
        _appearance = appearance;
        Control = new MachineControlViewModel(dialogs, preferences, CurrentMachineProgram, () => _project.Machine.SafeZ,
            () => _project.LevelingMap,
            map =>
            {
                _project.LevelingMap = map;
                OnProjectChanged();
            },
            ProgramCutBounds,
            () => _project.Machine.SpindleDelaySeconds);
        Control.JobFinished += message => Messages.Add(Loc.T("Станок: ", "Machine: ") + message);
        Simulation = new SimulationViewModel(() => (_project, _generation));
        Simulation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SimulationViewModel.CurrentMoveIndex) or nameof(SimulationViewModel.IsActive))
            {
                UpdateGcodeView();
            }
        };
        Control.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MachineControlViewModel.IsJobActive) or nameof(MachineControlViewModel.JobGcode) or nameof(MachineControlViewModel.ExecutingLine))
            {
                UpdateGcodeView();
            }
        };
        _regenerateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _regenerateTimer.Tick += (_, _) =>
        {
            _regenerateTimer.Stop();
            Regenerate();
        };

        LoadProject(_project, null);
        // The main view model lives as long as the application.
        Loc.LanguageChanged += OnLanguageChanged;
    }

    private static string AllFiles => "|" + Loc.T("Все файлы", "All files") + " (*.*)|*.*";

    private static string DrawingFilter =>
        Loc.T("Чертежи", "Drawings") + " (*.dxf;*.svg)|*.dxf;*.svg|DXF (*.dxf)|*.dxf|SVG (*.svg)|*.svg" + AllFiles;

    private static string StlFilter => Loc.T("3D-модели STL", "STL 3D models") + " (*.stl)|*.stl" + AllFiles;

    private static string ProjectFilter => Loc.T("Проекты PathForge", "PathForge projects") + " (*.pfproj)|*.pfproj";

    /// <summary>
    /// KiCad (*.gbr), Altium Designer / Protel (*.GTL top, *.GBL bottom, *.G1… inner, *.GP1… planes,
    /// *.GKO keep-out, *.GM1… mechanical) and other generators (*.ger, *.pho, *.art).
    /// </summary>
    private static string PcbFilesFilter =>
        Loc.T("Файлы платы", "PCB files") + " (*.gbr;*.gtl;*.gbl;*.gko;*.gm*;*.g?;*.drl;*.xln;*.txt)|*.gbr;*.ger;*.gtl;*.gbl;*.gko;*.gm*;*.gml;*.g1;*.g2;*.g3;*.g4;*.cmp;*.sol;*.drl;*.xln;*.exc;*.drd;*.txt;*.nc" + AllFiles;

    private static string GerberFilter =>
        "Gerber (*.gbr;*.gtl;*.gbl;*.gko;*.gm*;*.ger)|*.gbr;*.gtl;*.gbl;*.g1;*.g2;*.g3;*.g4;*.g5;*.g6;*.gp1;*.gp2;*.gp3;*.gp4;" +
        "*.gko;*.gm*;*.gml;*.ger;*.pho;*.art" + AllFiles;

    /// <summary>KiCad/Eagle (*.drl, *.xln) and Altium Designer NC drill (*.txt, e.g. Board-RoundHoles.TXT and -SlotHoles.TXT).</summary>
    private static string DrillFilter =>
        Loc.T("Сверловка Excellon", "Excellon drill files") + " (*.drl;*.xln;*.txt)|*.drl;*.xln;*.txt;*.exc;*.drd;*.nc" + AllFiles;

    private static string GcodeFilter =>
        "G-code (*.nc)|*.nc|G-code (*.gcode)|*.gcode|" + Loc.T("Текст", "Text") + " (*.txt)|*.txt";

    private static string UndoneText => Loc.T("Отменено.", "Undone.");

    private static string RedoneText => Loc.T("Повторено.", "Redone.");

    /// <summary>Raised when the view should zoom to show the whole drawing.</summary>
    public event EventHandler? ZoomToFitRequested;

    /// <summary>3D material removal simulation.</summary>
    public SimulationViewModel Simulation { get; }

    /// <summary>Machine control panel (GRBL over a serial port).</summary>
    public MachineControlViewModel Control { get; }

    public ObservableCollection<ToolViewModel> Tools { get; } = new();

    public ObservableCollection<OperationViewModel> Operations { get; } = new();

    public ObservableCollection<string> Messages { get; } = new();

    public ObservableCollection<LayerViewModel> Layers { get; } = new();

    public HashSet<int> SelectedContourIds { get; } = new();

    [ObservableProperty]
    private ToolViewModel? selectedTool;

    [ObservableProperty]
    private OperationViewModel? selectedOperation;

    // Assigned in LoadProject, which the constructor always calls.
    [ObservableProperty]
    private MachineSettingsViewModel machine = null!;

    [ObservableProperty]
    private StockSettingsViewModel stock = null!;

    [ObservableProperty]
    private MachineProfile? selectedProfile;

    [ObservableProperty]
    private ToolPreset? selectedToolPreset;

    /// <summary>Tool presets with names in the current language.</summary>
    public IReadOnlyList<Choice<ToolPreset>> ToolPresetOptions { get; } =
        ToolPresets.Cnc3018.Select(p => new Choice<ToolPreset>(p, () => p.Display)).ToList();

    /// <summary>Machine profiles with names in the current language.</summary>
    public IReadOnlyList<Choice<MachineProfile>> ProfileOptions { get; } =
        MachineProfiles.All.Select(p => new Choice<MachineProfile>(p, () => p.Name)).ToList();

    // ---- Language and theme ------------------------------------------------------------------

    public bool IsRussian => _appearance.Language == AppLanguage.Russian;

    public bool IsEnglish => _appearance.Language == AppLanguage.English;

    public bool IsLightTheme => _appearance.Theme == AppTheme.Light;

    public bool IsDarkTheme => _appearance.Theme == AppTheme.Dark;

    [RelayCommand]
    private void SetLanguage(string? code) =>
        _appearance.Language = code == "en" ? AppLanguage.English : AppLanguage.Russian;

    [RelayCommand]
    private void SetTheme(string? theme)
    {
        _appearance.Theme = theme == nameof(AppTheme.Dark) ? AppTheme.Dark : AppTheme.Light;
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    /// <summary>Texts computed by the view models follow the new language (XAML texts update by themselves).</summary>
    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(IsRussian));
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(SelectionText));
        // The description under the profile list.
        OnPropertyChanged(nameof(SelectedProfile));
        Machine.Refresh();
        foreach (var tool in Tools)
        {
            tool.Refresh();
        }

        foreach (var operation in Operations)
        {
            operation.Refresh();
        }

        foreach (var text in Texts)
        {
            text.Refresh();
        }

        FontScanStatus = _fontScanText();
        RefreshLayers();
        Control.RefreshLanguage();
        Simulation.RefreshLanguage();
        // Warnings, statistics and operation summaries are produced again in the new language.
        Regenerate();
    }

    [ObservableProperty]
    private ViewScene scene = ViewScene.Empty;

    [ObservableProperty]
    private string gcode = "";

    /// <summary>
    /// Lines shown in the G-code panel: the program running on the machine while a job is active, otherwise the
    /// project's G-code.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<GcodeLineViewModel> gcodeLines = Array.Empty<GcodeLineViewModel>();

    /// <summary>The line being machined (job) or simulated (3D simulation tab), highlighted in the panel.</summary>
    [ObservableProperty]
    private GcodeLineViewModel? currentGcodeLine;

    [ObservableProperty]
    private string statistics = "";

    [ObservableProperty]
    private string cursorText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string? projectPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private bool isDirty;

    public string Title =>
        $"{(ProjectPath is null ? _project.Name : Path.GetFileNameWithoutExtension(ProjectPath))}{(IsDirty ? " *" : "")} — PathForge";

    public string SelectionText => SelectedContourIds.Count == 0
        ? Loc.T($"Контуров: {_project.Contours.Count}", $"Contours: {_project.Contours.Count}")
        : Loc.T($"Выделено контуров: {SelectedContourIds.Count} из {_project.Contours.Count}",
            $"Selected contours: {SelectedContourIds.Count} of {_project.Contours.Count}");

    /// <summary>Asks to save unsaved changes. Returns false when the user cancels.</summary>
    public bool ConfirmDiscardChanges()
    {
        if (!IsDirty)
        {
            return true;
        }

        var answer = _dialogs.AskYesNoCancel(Loc.T("Сохранить изменения в проекте?", "Save changes to the project?"));
        return answer switch
        {
            true => SaveProjectCore(ProjectPath),
            false => true,
            null => false,
        };
    }

    /// <summary>XY area of all cutting moves in program coordinates (for the height map).</summary>
    private Bounds2 ProgramCutBounds() =>
        Bounds2.Of(_generation.Toolpaths.SelectMany(t => t.Moves).Where(m => m.Kind != MoveKind.Rapid).Select(m => m.Target.XY));

    /// <summary>G-code of the current project for the machine panel; null when there is nothing to cut.</summary>
    private MachineProgram? CurrentMachineProgram()
    {
        if (_regenerateTimer.IsEnabled)
        {
            // An edit is still waiting for the delayed regeneration.
            Regenerate();
        }

        return _generation.Toolpaths.Count == 0
            ? null
            : new MachineProgram(_project.Name, Gcode, _project.Machine.Dialect == GcodeDialect.Grbl);
    }

    // ---- Files ------------------------------------------------------------------------------

    [RelayCommand]
    private void NewProject()
    {
        if (ConfirmDiscardChanges())
        {
            LoadProject(CamProject.CreateDefault(), null);
        }
    }

    [RelayCommand]
    private void OpenProject()
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        var path = _dialogs.OpenFile(Loc.T("Открыть проект", "Open project"), ProjectFilter);
        if (path is null)
        {
            return;
        }

        try
        {
            LoadProject(ProjectSerializer.Load(path), path);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(Loc.T($"Не удалось открыть проект:\n{ex.Message}", $"Could not open the project:\n{ex.Message}"));
        }
    }

    [RelayCommand]
    private void SaveProject() => SaveProjectCore(ProjectPath);

    [RelayCommand]
    private void SaveProjectAs() => SaveProjectCore(null);

    private bool SaveProjectCore(string? path)
    {
        path ??= _dialogs.SaveFile(Loc.T("Сохранить проект", "Save project"), ProjectFilter, _project.Name + ProjectSerializer.FileExtension);
        if (path is null)
        {
            return false;
        }

        try
        {
            ProjectSerializer.Save(_project, path);
            ProjectPath = path;
            IsDirty = false;
            return true;
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(Loc.T($"Не удалось сохранить проект:\n{ex.Message}", $"Could not save the project:\n{ex.Message}"));
            return false;
        }
    }

    [RelayCommand]
    private void ImportDrawing()
    {
        var path = _dialogs.OpenFile(Loc.T("Импорт чертежа", "Import drawing"), DrawingFilter);
        if (path is not null)
        {
            ImportDrawingFile(path);
        }
    }

    public void ImportDrawingFile(string path)
    {
        try
        {
            var import = ReadDrawing(path);
            var contours = ContourBuilder.Build(import.Paths);
            _project.Contours = contours;
            _project.HiddenLayers.Clear();
            RebuildAllTexts();
            _project.SourceFile = path;
            if (ProjectPath is null)
            {
                _project.Name = Path.GetFileNameWithoutExtension(path);
            }

            foreach (var operation in Operations)
            {
                operation.SetContours(Array.Empty<int>());
            }

            SelectedContourIds.Clear();
            Messages.Clear();
            Messages.Add(Loc.T($"Импортировано контуров: {contours.Count} (замкнутых: {contours.Count(c => c.IsClosed)}).", $"Contours imported: {contours.Count} (closed: {contours.Count(c => c.IsClosed)})."));
            if (Operations.Count > 0)
            {
                Messages.Add(Loc.T("Контуры операций сброшены: выберите их заново на новом чертеже.", "The contours of the operations were reset: select them again on the new drawing."));
            }

            foreach (var warning in import.Warnings)
            {
                Messages.Add(warning);
            }

            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(SelectionText));
            RefreshLayers();
            OnProjectChanged();
            Regenerate();
            ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(Loc.T($"Не удалось прочитать чертёж:\n{ex.Message}", $"Could not read the drawing:\n{ex.Message}"));
        }
    }

    private static DxfImportResult ReadDrawing(string path) =>
        string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase)
            ? SvgReader.ReadFile(path)
            : DxfReader.ReadFile(path);

    /// <summary>Adds a DXF/SVG drawing to the current one (e.g. several parts or an SVG logo on a DXF plate).</summary>
    [RelayCommand]
    private void AddDrawing() => ImportPcbFile(DrawingFilter, Loc.T("Добавить чертёж", "Add drawing"), path =>
    {
        var import = ReadDrawing(path);
        return (ContourBuilder.Build(import.Paths), import.Warnings);
    });

    // ---- 3D relief ---------------------------------------------------------------------------

    [RelayCommand]
    private void ImportStl()
    {
        var mesh = LoadMesh();
        if (mesh is null)
        {
            return;
        }

        var (minX, _, minZ, maxX, _, maxZ) = mesh.Bounds();
        var relief = new ReliefOperation
        {
            Name = NewName("Рельеф", "Relief"),
            Source = ReliefSource.Mesh,
            Mesh = mesh,
            // STL files are usually in millimetres: keep the model size, but not deeper than the stock.
            WidthMm = Math.Round(Math.Max(5, maxX - minX), 2),
            Depth = Math.Round(Math.Clamp(maxZ - minZ, 0.5, Math.Max(0.5, _project.Stock.Thickness)), 2),
        };
        AddOperation(relief, BallNoseTool());
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Rest machining after a relief: a copy of the selected (or last) relief with the largest ball nose or V-bit
    /// smaller than its tool; it machines only what the simulation leaves above the model.
    /// </summary>
    [RelayCommand]
    private void AddReliefRest()
    {
        var source = SelectedOperation?.Model as ReliefOperation ?? _project.Operations.OfType<ReliefOperation>().LastOrDefault(o => !o.RestMachining)
            ?? _project.Operations.OfType<ReliefOperation>().LastOrDefault();
        if (source is null)
        {
            Messages.Add(Loc.T("В проекте нет рельефа 3D.", "The project has no 3D relief."));
            return;
        }

        var used = _project.Tools.FirstOrDefault(t => t.Id == source.ToolId);
        var smaller = _project.Tools
            .Where(t => t.Kind is ToolKind.BallNose or ToolKind.VBit && (used is null || t.Diameter < used.Diameter - 1e-9))
            .MaxBy(t => t.Diameter);
        if (smaller is null)
        {
            Messages.Add(Loc.T(
                "Для дообработки добавьте фрезу меньше: «Инструменты» → пресет «Рельеф 3D: Сферическая Ø2 (R1)» или гравёр. Операция создана с прежней фрезой — смените её.",
                "For rest machining add a smaller tool: “Tools” → preset “3D relief: Ball nose Ø2 (R1)” or a V-bit. The operation was created with the same tool — change it."));
        }

        var rest = (ReliefOperation)ProjectSerializer.CloneOperation(source);
        // Roughing stays as in the source: a deep valley the large tool never reached must not be cut in one pass.
        rest.RestMachining = true;
        rest.Name = Loc.T($"Дообработка: {source.Name}", $"Rest: {source.Name}");
        AddOperation(rest, smaller ?? used, source.ContourIds);
    }

    [RelayCommand]
    private void AddImageRelief()
    {
        var image = LoadImage();
        if (image is null)
        {
            return;
        }

        AddOperation(new ReliefOperation { Name = NewName("Рельеф", "Relief"), Image = image, WidthMm = 50, Depth = 2 }, BallNoseTool());
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ReplaceReliefSource()
    {
        if (SelectedOperation is not ReliefOperationViewModel relief)
        {
            return;
        }

        if (relief.IsImage)
        {
            if (LoadImage() is { } image)
            {
                relief.ReplaceImage(image);
            }
        }
        else if (LoadMesh() is { } mesh)
        {
            relief.ReplaceMesh(mesh);
        }
    }

    private StlMesh? LoadMesh()
    {
        var path = _dialogs.OpenFile(Loc.T("3D-модель STL", "STL 3D model"), StlFilter);
        if (path is null)
        {
            return null;
        }

        try
        {
            return StlReader.ReadFile(path);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(Loc.T($"Не удалось прочитать STL:\n{ex.Message}", $"Could not read the STL:\n{ex.Message}"));
            return null;
        }
    }

    private Tool? BallNoseTool()
    {
        var tool = _project.Tools.FirstOrDefault(t => t.Kind == ToolKind.BallNose);
        if (tool is null)
        {
            Messages.Add(Loc.T("Для рельефа добавьте сферическую фрезу: «Инструменты» → пресет «Рельеф 3D».", "For a relief add a ball nose: “Tools” → preset “3D relief”."));
        }

        return tool;
    }

    // ---- Layers ------------------------------------------------------------------------------

    private void RefreshLayers()
    {
        Layers.Clear();
        foreach (var group in _project.Contours.GroupBy(c => c.Layer).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            Layers.Add(new LayerViewModel(group.Key, group.Count(), !_project.HiddenLayers.Contains(group.Key), OnLayerVisibilityChanged));
        }
    }

    private void OnLayerVisibilityChanged(LayerViewModel layer)
    {
        _project.HiddenLayers.Remove(layer.Name);
        if (!layer.IsVisible)
        {
            _project.HiddenLayers.Add(layer.Name);
            // Hidden contours cannot stay selected.
            SelectedContourIds.ExceptWith(_project.Contours.Where(c => c.Layer == layer.Name).Select(c => c.Id));
            OnPropertyChanged(nameof(SelectionText));
        }

        IsDirty = true;
        UpdateScene();
    }

    private bool IsVisible(Contour contour) => !_project.HiddenLayers.Contains(contour.Layer);

    /// <summary>Selects all contours of a layer (replacing the current selection).</summary>
    [RelayCommand]
    private void SelectLayer(LayerViewModel? layer)
    {
        if (layer is null)
        {
            return;
        }

        layer.IsVisible = true;
        SelectedContourIds.Clear();
        SelectedContourIds.UnionWith(_project.Contours.Where(c => c.Layer == layer.Name).Select(c => c.Id));
        OnSelectionChanged();
    }

    [RelayCommand]
    private void AddLayerToSelection(LayerViewModel? layer)
    {
        if (layer is null)
        {
            return;
        }

        layer.IsVisible = true;
        SelectedContourIds.UnionWith(_project.Contours.Where(c => c.Layer == layer.Name).Select(c => c.Id));
        OnSelectionChanged();
    }

    [RelayCommand]
    private void ShowAllLayers()
    {
        foreach (var layer in Layers)
        {
            layer.IsVisible = true;
        }
    }

    // ---- PCB --------------------------------------------------------------------------------

    [RelayCommand]
    private void ImportGerberCopper() => ImportPcbFile(GerberFilter, Loc.T("Импорт Gerber: медь", "Import Gerber: copper"), path =>
    {
        var result = GerberReader.ReadFile(path, GerberMode.Copper);
        return (result.Contours, result.Warnings);
    });

    [RelayCommand]
    private void ImportGerberOutline() => ImportPcbFile(GerberFilter, Loc.T("Импорт Gerber: контур платы", "Import Gerber: board outline"), path =>
    {
        var result = GerberReader.ReadFile(path, GerberMode.Outline);
        return (result.Contours, result.Warnings);
    });

    [RelayCommand]
    private void ImportExcellon() => ImportPcbFile(DrillFilter, Loc.T("Импорт сверловки Excellon", "Import Excellon drill file"), path =>
    {
        var result = ExcellonReader.ReadFile(path);
        return (result.ToContours(), result.Warnings);
    });

    /// <summary>
    /// A whole fabrication output at once: copper layers, board outline and drill files are told apart (X2 attributes
    /// or file names) and each is added as its own layer; mask, silk screen and paste are skipped.
    /// </summary>
    [RelayCommand]
    private void ImportPcbFiles()
    {
        var paths = _dialogs.OpenFiles(Loc.T("Добавить файлы платы (Gerber и сверловка)", "Add PCB files (Gerber and drills)"), PcbFilesFilter);
        if (paths is null || paths.Length == 0)
        {
            return;
        }

        ImportPcbFiles(paths, path =>
        {
            var text = File.ReadAllText(path);
            var (kind, reason) = PcbFileDetector.Detect(path, text);
            switch (kind)
            {
                case PcbFileKind.Copper:
                case PcbFileKind.Outline:
                    var gerber = GerberReader.Read(text, kind == PcbFileKind.Copper ? GerberMode.Copper : GerberMode.Outline, Path.GetFileName(path));
                    return (gerber.Contours, gerber.Warnings, KindName(kind));
                case PcbFileKind.Drill:
                    var drill = ExcellonReader.Read(text);
                    return (drill.ToContours(), drill.Warnings, KindName(kind));
                default:
                    return (new List<Contour>(), new List<string>(), reason);
            }
        });

        static string KindName(PcbFileKind kind) => kind switch
        {
            PcbFileKind.Copper => Loc.T("медь", "copper"),
            PcbFileKind.Outline => Loc.T("контур платы", "board outline"),
            _ => Loc.T("сверловка", "drills"),
        };
    }

    /// <summary>PCB layers are added to the drawing (copper, drills and outline come from separate files).</summary>
    private void ImportPcbFile(string filter, string title, Func<string, (List<Contour> Contours, List<string> Warnings)> read)
    {
        var paths = _dialogs.OpenFiles(title, filter);
        if (paths is null || paths.Length == 0)
        {
            return;
        }

        ImportPcbFiles(paths, path =>
        {
            var (contours, warnings) = read(path);
            return (contours, warnings, "");
        });
    }

    /// <param name="read">Contours and warnings of a file, and its kind for the message (or why it was skipped when there are no contours).</param>
    private void ImportPcbFiles(IReadOnlyList<string> paths, Func<string, (List<Contour> Contours, List<string> Warnings, string Note)> read)
    {
        var added = new List<int>();
        foreach (var path in paths)
        {
            var layer = Path.GetFileName(path);
            try
            {
                var (contours, warnings, note) = read(path);
                if (contours.Count == 0 && note.Length > 0)
                {
                    Messages.Add(Loc.T($"{layer}: пропущен — {note}.", $"{layer}: skipped — {note}."));
                    continue;
                }

                var nextId = _project.NextContourId();
                foreach (var contour in contours)
                {
                    contour.Id = nextId++;
                    if (string.IsNullOrEmpty(contour.Layer))
                    {
                        contour.Layer = layer;
                    }
                }

                _project.Contours.AddRange(contours);
                added.AddRange(contours.Select(c => c.Id));
                _project.SourceFile ??= path;
                if (ProjectPath is null && _project.Contours.Count == contours.Count)
                {
                    _project.Name = Path.GetFileNameWithoutExtension(path);
                }

                var kind = note.Length > 0 ? $" ({note})" : "";
                Messages.Add(Loc.T($"{layer}{kind}: добавлено контуров {contours.Count}.", $"{layer}{kind}: contours added: {contours.Count}."));
                foreach (var warning in warnings)
                {
                    Messages.Add($"{layer}: {warning}");
                }
            }
            catch (Exception ex)
            {
                _dialogs.ShowError(Loc.T($"Не удалось прочитать {layer}:\n{ex.Message}", $"Could not read {layer}:\n{ex.Message}"));
            }
        }

        if (added.Count == 0)
        {
            return;
        }

        RefreshLayers();
        // The new contours are selected so that an operation can be added right away.
        SelectedContourIds.Clear();
        SelectedContourIds.UnionWith(added);
        OnPropertyChanged(nameof(Title));
        OnSelectionChanged();
        OnProjectChanged();
        Regenerate();
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Mirrors the whole drawing left-right, e.g. to mill the bottom copper layer.</summary>
    [RelayCommand]
    private void MirrorX()
    {
        if (_project.Contours.Count == 0)
        {
            return;
        }

        var mirror = Affine2.Scaling(-1, 1);
        _project.Contours = _project.Contours.Select(c => c.Transformed(mirror)).ToList();
        MirrorTexts();
        Messages.Add(Loc.T("Чертёж зеркально отражён по X.", "The drawing was mirrored in X."));
        OnProjectChanged();
        Regenerate();
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AddIsolation()
    {
        var engraver = _project.Tools.FirstOrDefault(t => t.Kind == ToolKind.VBit);
        if (engraver is null)
        {
            Messages.Add(Loc.T("Для изоляции добавьте гравёр: «Инструменты» → пресет «Текстолит (платы): Гравёр 20°».", "For isolation add an engraver: “Tools” → preset “PCB (FR4): Engraver 20°”."));
        }

        AddOperation(new IsolationOperation { Name = NewName("Изоляция", "Isolation") }, engraver);
    }

    // ---- Laser ------------------------------------------------------------------------------

    [RelayCommand]
    private void AddLaserVector() =>
        AddOperation(new LaserVectorOperation { Name = NewName("Лазер", "Laser") }, LaserTool());

    /// <summary>
    /// PCB by laser on a painted blank. Selected contours on a board outline layer (by the file name) become the
    /// board, the rest is copper (drill holes inside pads leave etched centre marks).
    /// </summary>
    [RelayCommand]
    private void AddLaserPcb()
    {
        var selected = _project.Contours.Where(c => SelectedContourIds.Contains(c.Id)).ToList();
        var board = selected.Where(c => PcbFileDetector.IsOutlineFileName(c.Layer)).Select(c => c.Id).ToList();
        if (board.Count == selected.Count)
        {
            board.Clear();
        }

        var operation = new LaserPcbOperation { Name = NewName("Плата лазером", "Laser PCB"), BoardContourIds = board };
        AddOperation(operation, LaserTool(), SelectedContourIds.Where(id => !board.Contains(id)));
        if (board.Count > 0)
        {
            Messages.Add(Loc.T(
                $"{operation.Name}: контур платы — {board.Count} шт. (слой контура), медь — {operation.ContourIds.Count} шт.",
                $"{operation.Name}: board outline — {board.Count} (outline layer), copper — {operation.ContourIds.Count}."));
        }
    }

    /// <summary>Makes the selected contours the board outline of the selected laser PCB operation.</summary>
    [RelayCommand]
    private void AssignBoardOutline()
    {
        if (SelectedOperation is LaserPcbOperationViewModel pcb)
        {
            pcb.SetBoardContours(SelectedContourIds);
        }
    }

    /// <summary>Settings of the power × speed test card.</summary>
    public LaserTestGridViewModel LaserTest { get; } = new();

    /// <summary>Sheet and gaps for laying out parts.</summary>
    public NestingViewModel Nesting { get; } = new();

    /// <summary>Size and distance of the alignment holes for double-sided boards.</summary>
    public AlignmentHolesViewModel AlignmentHoles { get; } = new();

    /// <summary>
    /// Adds (or moves) the two alignment holes of a double-sided board: outside the board outline on its horizontal
    /// centre line, symmetric, so that the mirrored second side keeps the same work zero.
    /// </summary>
    [RelayCommand]
    private void AddAlignmentHoles()
    {
        var board = PcbAlignment.BoardBounds(_project.Contours);
        if (board.IsEmpty)
        {
            Messages.Add(Loc.T("Сначала добавьте файлы платы (медь и контур платы).", "Add the board files first (copper and board outline)."));
            return;
        }

        var existing = _project.Contours.Where(c => c.Layer == PcbAlignment.Layer).ToList();
        var holes = PcbAlignment.Holes(board, AlignmentHoles.Diameter, AlignmentHoles.Margin, _project.NextContourId());
        if (existing.Count == holes.Count)
        {
            // Keep the ids: operations that drill the holes follow the new position.
            for (var i = 0; i < holes.Count; i++)
            {
                existing[i].Segments = holes[i].Segments;
            }

            holes = existing;
        }
        else
        {
            _project.Contours.RemoveAll(c => c.Layer == PcbAlignment.Layer);
            _project.Contours.AddRange(holes);
        }

        RefreshLayers();
        SelectedContourIds.Clear();
        SelectedContourIds.UnionWith(holes.Select(h => h.Id));
        Messages.Add(Loc.T(
            $"Базовые отверстия Ø{AlignmentHoles.Diameter:0.##} добавлены слева и справа от платы. Дальше: «+ Сверление» сверлом того же диаметра, " +
            $"глубина = толщина платы + 2–3 мм (в жертвенный стол), первой операцией. Вставьте в отверстия штифты, для второй стороны переверните плату " +
            "слева направо на штифты и нажмите «Зеркалить по X» — ноль X/Y не трогайте.",
            $"Alignment holes Ø{AlignmentHoles.Diameter:0.##} added left and right of the board. Next: “+ Drill” with a drill of the same size, " +
            "depth = board thickness + 2–3 mm (into the spoil board), as the first operation. Put pins into the holes; for the second side turn the board " +
            "over left to right onto the pins and press “Mirror X” — do not touch the X/Y zero."));
        OnSelectionChanged();
        OnProjectChanged();
        Regenerate();
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Face milling: the selected contours' bounding box (plus a margin) or, without a selection, the drawing or
    /// the whole machine travel (spoil board).
    /// </summary>
    [RelayCommand]
    private void AddFacing()
    {
        var mill = _project.Tools.Where(t => t.Kind == ToolKind.EndMill).MaxBy(t => t.Diameter);
        var facing = new FacingOperation { Name = NewName("Торцовка", "Facing") };
        var drawing = _project.Contours.Count > 0 ? _project.DrawingBounds() : Bounds2.Empty;
        if (!drawing.IsEmpty)
        {
            (facing.X, facing.Y, facing.Width, facing.Height) = (drawing.MinX, drawing.MinY, drawing.Width, drawing.Height);
        }
        else if (_project.Machine.WorkAreaX > 0 && _project.Machine.WorkAreaY > 0)
        {
            (facing.Width, facing.Height) = (_project.Machine.WorkAreaX, _project.Machine.WorkAreaY);
        }

        AddOperation(facing, mill);
        Messages.Add(Loc.T(
            $"{facing.Name}: {(facing.ContourIds.Count > 0 ? "область — выделенные контуры с полями" : "задайте область (X, Y, ширина, высота) или выделите контуры и нажмите «Назначить выделенные»")}. " +
            "Глубина за проход берётся из инструмента (для выравнивания стола 0,1–0,3 мм).",
            $"{facing.Name}: {(facing.ContourIds.Count > 0 ? "the area is the selected contours with a margin" : "set the area (X, Y, width, height) or select contours and press “Assign selected”")}. " +
            "The depth per pass comes from the tool (0.1–0.3 mm for spoil board surfacing)."));
    }

    /// <summary>Sets the facing area to the machine's whole travel (spoil board surfacing).</summary>
    [RelayCommand]
    private void FacingFromMachine()
    {
        if (SelectedOperation is not FacingOperationViewModel facing)
        {
            return;
        }

        if (_project.Machine.WorkAreaX <= 0 || _project.Machine.WorkAreaY <= 0)
        {
            Messages.Add(Loc.T("У профиля станка не задано рабочее поле.", "The machine profile has no work area."));
            return;
        }

        facing.SetContours(Array.Empty<int>());
        facing.SetArea(0, 0, _project.Machine.WorkAreaX, _project.Machine.WorkAreaY);
        facing.RefreshSummary();
    }

    /// <summary>Fills the sheet size and gap from the machine and the largest cutter (before the first layout).</summary>
    [RelayCommand]
    private void SuggestNestingSheet()
    {
        var cutter = _project.Tools.Where(t => t.Kind != ToolKind.Laser).Select(t => t.Diameter).DefaultIfEmpty(0).Max();
        Nesting.SuggestSheet(_project.Machine.WorkAreaX, _project.Machine.WorkAreaY, cutter > 0 ? cutter + 2 : 0);
    }

    /// <summary>
    /// Lays out the selected parts (or all) on the sheet: each closed outer contour with everything inside it,
    /// turned when allowed, with copies; operations get the copies too.
    /// </summary>
    [RelayCommand]
    private void NestParts()
    {
        var result = PathForge.Core.Machining.Nesting.Arrange(_project, SelectedContourIds.ToList(), Nesting.Model);
        foreach (var text in Texts)
        {
            text.Refresh();
        }

        foreach (var operation in Operations)
        {
            operation.RefreshSummary();
        }

        Messages.Add(Loc.T(
            $"Раскладка: на листе {Nesting.SheetWidth:0.#}×{Nesting.SheetHeight:0.#} мм деталей {result.Placed}, заполнено {result.UsedPercent:0} % листа.",
            $"Layout: {result.Placed} parts on the {Nesting.SheetWidth:0.#}×{Nesting.SheetHeight:0.#} mm sheet, {result.UsedPercent:0} % of the sheet used."));
        foreach (var warning in result.Warnings)
        {
            Messages.Add(warning);
        }

        RefreshLayers();
        OnSelectionChanged();
        OnProjectChanged();
        Regenerate();
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds a test card: squares burned with every combination of power (rows) and speed (columns), with the
    /// values burned next to them. Placed to the right of the drawing.
    /// </summary>
    [RelayCommand]
    private void AddLaserTestGrid()
    {
        var laser = LaserTool();
        var drawing = _project.DrawingBounds();
        var settings = LaserTest.Model;
        settings.X = drawing.IsEmpty ? 0 : drawing.MaxX + 10;
        settings.Y = drawing.IsEmpty ? 0 : drawing.MinY;
        var grid = LaserTestGrid.Build(settings, laser?.Id ?? "", _project.NextContourId());
        _project.Contours.AddRange(grid.Contours);
        foreach (var operation in grid.Operations)
        {
            _project.Operations.Add(operation);
            Operations.Add(OperationViewModel.Create(operation, OnProjectChanged));
        }

        SelectedOperation = Operations[^1];
        RefreshLayers();
        Messages.Add(Loc.T(
            $"Тест-сетка: {settings.PowerSteps}×{settings.SpeedSteps} квадратов, мощность растёт снизу вверх, скорость — слева направо. " +
            "Выберите лучший квадрат и перенесите его мощность и скорость в свою операцию.",
            $"Test card: {settings.PowerSteps}×{settings.SpeedSteps} squares, power grows from bottom to top, speed from left to right. " +
            "Pick the best square and copy its power and speed into your operation."));
        OnProjectChanged();
        Regenerate();
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AddLaserRaster()
    {
        var image = LoadImage();
        if (image is null)
        {
            return;
        }

        // Start with 0.1 mm per pixel line, but at most 100 mm wide.
        var width = Math.Round(Math.Min(100, Math.Max(10, image.Width * 0.1)));
        AddOperation(new LaserRasterOperation { Name = NewName("Картинка", "Picture"), Image = image, WidthMm = width }, LaserTool());
    }

    [RelayCommand]
    private void ReplaceRasterImage()
    {
        if (SelectedOperation is LaserRasterOperationViewModel raster && LoadImage() is { } image)
        {
            raster.ReplaceImage(image);
        }
    }

    private GrayImage? LoadImage()
    {
        var path = _dialogs.OpenFile(Loc.T("Картинка для гравировки", "Picture to engrave"), ImageLoader.Filter);
        if (path is null)
        {
            return null;
        }

        try
        {
            return ImageLoader.Load(path);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(Loc.T($"Не удалось открыть картинку:\n{ex.Message}", $"Could not open the picture:\n{ex.Message}"));
            return null;
        }
    }

    private Tool? LaserTool()
    {
        var laser = _project.Tools.FirstOrDefault(t => t.Kind == ToolKind.Laser);
        if (laser is null)
        {
            Messages.Add(Loc.T(
                "Добавьте лазер: «Инструменты» → пресет «Лазер: Лазерный модуль 5 Вт» или «10 Вт», и выберите профиль станка с лазером.",
                "Add a laser: “Tools” → preset “Laser: Laser module 5 W” or “10 W”, and choose a machine profile with a laser."));
        }
        else if (!_project.Machine.LaserMode)
        {
            Messages.Add(Loc.T(
                "Для лазера выберите профиль «CNC 3018 Pro (лазер 5 Вт)» или «(лазер 10 Вт)» на вкладке «Станок» и включите в GRBL $32=1.",
                "For the laser choose the profile “CNC 3018 Pro (5 W laser)” or “(10 W laser)” on the “Machine” tab and enable $32=1 in GRBL."));
        }

        return laser;
    }

    [RelayCommand]
    private void ExportGcode()
    {
        Regenerate();
        if (_generation.Toolpaths.Count == 0)
        {
            _dialogs.ShowError(Loc.T("Нет траекторий для экспорта: добавьте операции и выберите контуры.", "No toolpaths to export: add operations and select contours."));
            return;
        }

        var path = _dialogs.SaveFile(Loc.T("Сохранить G-code", "Save G-code"), GcodeFilter, _project.Name + ".nc");
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, Gcode);
            Messages.Add(Loc.T($"G-code сохранён: {path}", $"G-code saved: {path}"));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(Loc.T($"Не удалось сохранить G-code:\n{ex.Message}", $"Could not save the G-code:\n{ex.Message}"));
        }
    }

    // ---- Operations ------------------------------------------------------------------------

    [RelayCommand]
    private void AddProfile() => AddOperation(new ProfileOperation { Name = NewName("Контур", "Profile") });

    [RelayCommand]
    private void AddPocket() => AddOperation(new PocketOperation { Name = NewName("Карман", "Pocket") });

    [RelayCommand]
    private void AddDrill()
    {
        var drill = _project.Tools.FirstOrDefault(t => t.Kind == ToolKind.Drill);
        AddOperation(new DrillOperation { Name = NewName("Сверление", "Drilling") }, drill);
    }

    /// <summary>Default name of a new operation: kind and number.</summary>
    private string NewName(string russian, string english) => $"{Loc.T(russian, english)} {Operations.Count + 1}";

    /// <param name="contours">Contours of the operation; null = the selected ones.</param>
    private void AddOperation(Operation operation, Tool? preferredTool = null, IEnumerable<int>? contours = null)
    {
        // Small spindles (3018) suffer from straight plunges: ramp by default (not for drills and shallow engraving).
        operation.Entry = operation is ProfileOperation or PocketOperation ? EntryMode.Ramp : EntryMode.Plunge;
        var tool = preferredTool ?? SelectedTool?.Model ?? _project.Tools.FirstOrDefault(t => t.Kind != ToolKind.Drill) ?? _project.Tools.FirstOrDefault();
        operation.ToolId = tool?.Id ?? "";
        operation.ContourIds = (contours ?? SelectedContourIds).OrderBy(i => i).ToList();
        _project.Operations.Add(operation);
        var vm = OperationViewModel.Create(operation, OnProjectChanged);
        Operations.Add(vm);
        SelectedOperation = vm;
        if (operation.ContourIds.Count == 0 && operation is not (LaserRasterOperation or FacingOperation))
        {
            Messages.Add(Loc.T($"{operation.Name}: выделите контуры на чертеже и нажмите «Назначить выделенные».", $"{operation.Name}: select contours on the drawing and press “Assign selected”."));
        }

        OnProjectChanged();
    }

    [RelayCommand]
    private void RemoveOperation()
    {
        if (SelectedOperation is null)
        {
            return;
        }

        var index = Operations.IndexOf(SelectedOperation);
        _project.Operations.Remove(SelectedOperation.Model);
        Operations.RemoveAt(index);
        SelectedOperation = Operations.Count == 0 ? null : Operations[Math.Min(index, Operations.Count - 1)];
        OnProjectChanged();
    }

    [RelayCommand]
    private void MoveOperationUp() => MoveOperation(-1);

    [RelayCommand]
    private void MoveOperationDown() => MoveOperation(1);

    private void MoveOperation(int delta)
    {
        if (SelectedOperation is null)
        {
            return;
        }

        var index = Operations.IndexOf(SelectedOperation);
        var target = index + delta;
        if (target < 0 || target >= Operations.Count)
        {
            return;
        }

        var selected = SelectedOperation;
        Operations.Move(index, target);
        _project.Operations.RemoveAt(index);
        _project.Operations.Insert(target, selected.Model);
        SelectedOperation = selected;
        OnProjectChanged();
    }

    [RelayCommand]
    private void AssignSelection()
    {
        if (SelectedOperation is null)
        {
            return;
        }

        SelectedOperation.SetContours(SelectedContourIds);
    }

    // ---- Tools -------------------------------------------------------------------------------

    [RelayCommand]
    private void AddTool()
    {
        var number = _project.Tools.Count == 0 ? 1 : _project.Tools.Max(t => t.Number) + 1;
        var tool = new Tool { Number = number, Name = Loc.T($"Фреза {number}", $"Tool {number}") };
        _project.Tools.Add(tool);
        var vm = new ToolViewModel(tool, OnProjectChanged);
        Tools.Add(vm);
        SelectedTool = vm;
        OnProjectChanged();
    }

    [RelayCommand]
    private void AddToolFromPreset()
    {
        if (SelectedToolPreset is null)
        {
            return;
        }

        var number = _project.Tools.Count == 0 ? 1 : _project.Tools.Max(t => t.Number) + 1;
        var tool = SelectedToolPreset.Create(number);
        _project.Tools.Add(tool);
        var vm = new ToolViewModel(tool, OnProjectChanged);
        Tools.Add(vm);
        SelectedTool = vm;
        OnProjectChanged();
    }

    [RelayCommand]
    private void ApplyProfile()
    {
        if (SelectedProfile is not null)
        {
            Machine.ApplyProfile(SelectedProfile);
            Messages.Add(Loc.T($"Применён профиль станка «{SelectedProfile.Name}».", $"Machine profile applied: “{SelectedProfile.Name}”."));
        }
    }

    [RelayCommand]
    private void RemoveTool()
    {
        if (SelectedTool is null)
        {
            return;
        }

        if (_project.Operations.Any(o => o.ToolId == SelectedTool.Id))
        {
            _dialogs.ShowError(Loc.T("Инструмент используется в операциях. Сначала выберите в них другой инструмент.", "The tool is used by operations. Choose another tool in them first."));
            return;
        }

        _project.Tools.Remove(SelectedTool.Model);
        Tools.Remove(SelectedTool);
        SelectedTool = Tools.FirstOrDefault();
        OnProjectChanged();
    }

    // ---- Selection ---------------------------------------------------------------------------

    [RelayCommand]
    private void ContourClicked(ContourClick? click)
    {
        if (click is null)
        {
            return;
        }

        if (!click.Additive)
        {
            SelectedContourIds.Clear();
        }

        if (click.ContourId is int id && !SelectedContourIds.Remove(id))
        {
            SelectedContourIds.Add(id);
        }

        OnSelectionChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        SelectedContourIds.Clear();
        SelectedContourIds.UnionWith(_project.Contours.Where(IsVisible).Select(c => c.Id));
        OnSelectionChanged();
    }

    /// <summary>Selects all circles with the diameter of an already selected circle (holes of one drill size).</summary>
    [RelayCommand]
    private void SelectSameDiameter()
    {
        var diameters = _project.Contours
            .Where(c => SelectedContourIds.Contains(c.Id))
            .Select(c => c.TryGetCircle(out _, out var r) ? Math.Round(2 * r, 3) : -1)
            .Where(d => d > 0)
            .ToHashSet();
        if (diameters.Count == 0)
        {
            Messages.Add(Loc.T("Сначала выделите хотя бы одно отверстие (окружность).", "Select at least one hole (circle) first."));
            return;
        }

        SelectedContourIds.Clear();
        SelectedContourIds.UnionWith(_project.Contours
            .Where(c => c.TryGetCircle(out _, out var r) && diameters.Contains(Math.Round(2 * r, 3)))
            .Select(c => c.Id));
        OnSelectionChanged();
    }

    [RelayCommand]
    private void SelectCircles()
    {
        SelectedContourIds.Clear();
        SelectedContourIds.UnionWith(_project.Contours.Where(IsVisible).Where(c => c.TryGetCircle(out _, out _)).Select(c => c.Id));
        OnSelectionChanged();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        SelectedContourIds.Clear();
        OnSelectionChanged();
    }

    [RelayCommand]
    private void ZoomToFit() => ZoomToFitRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedOperationChanged(OperationViewModel? value)
    {
        if (value is not null)
        {
            // Show the contours of the operation as the current selection so they can be edited.
            SelectedContourIds.Clear();
            SelectedContourIds.UnionWith(value.Model.ContourIds);
            OnSelectionChanged();
        }
        else
        {
            UpdateScene();
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionText));
        UpdateScene();
    }

    // ---- Generation --------------------------------------------------------------------------

    private void LoadProject(CamProject project, string? path)
    {
        ApplyProject(project);
        Messages.Clear();
        ProjectPath = path;
        IsDirty = false;
        _history.Reset(ProjectSerializer.Serialize(_project));
        NotifyHistoryChanged();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SelectionText));
        Regenerate();
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows another project (or another state of this one) in all panels.</summary>
    private void ApplyProject(CamProject project)
    {
        _suppressChanges = true;
        try
        {
            _project = project;
            Tools.Clear();
            foreach (var tool in project.Tools)
            {
                Tools.Add(new ToolViewModel(tool, OnProjectChanged));
            }

            Operations.Clear();
            foreach (var operation in project.Operations)
            {
                Operations.Add(OperationViewModel.Create(operation, OnProjectChanged));
            }

            Machine = new MachineSettingsViewModel(project.Machine, OnProjectChanged);
            Stock = new StockSettingsViewModel(project.Stock, OnProjectChanged);
            SelectedProfile = MachineProfiles.Find(project.Machine.ProfileName) ?? MachineProfiles.All[0];
            SelectedTool = Tools.FirstOrDefault();
            SelectedOperation = null;
            LoadTexts();
            SelectedContourIds.Clear();
            RefreshLayers();
        }
        finally
        {
            _suppressChanges = false;
        }
    }

    // ---- Undo / redo -------------------------------------------------------------------------

    private bool CanUndo() => _history.CanUndo || _regenerateTimer.IsEnabled;

    private bool CanRedo() => _history.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_regenerateTimer.IsEnabled)
        {
            // An edit is still waiting to be recorded: record it first so that it is the step undone.
            Regenerate();
        }

        if (_history.Undo() is { } state)
        {
            RestoreState(state, UndoneText);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_history.Redo() is { } state)
        {
            RestoreState(state, RedoneText);
        }
    }

    /// <summary>Puts the project back into a recorded state, keeping the selections where possible.</summary>
    private void RestoreState(string state, string message)
    {
        var operationId = SelectedOperation?.Model.Id;
        var toolId = SelectedTool?.Model.Id;
        var textId = SelectedText?.Model.Id;
        var contours = SelectedContourIds.ToList();
        ApplyProject(ProjectSerializer.Deserialize(state));

        SelectedTool = Tools.FirstOrDefault(t => t.Model.Id == toolId) ?? Tools.FirstOrDefault();
        SelectedText = Texts.FirstOrDefault(t => t.Model.Id == textId);
        SelectedOperation = Operations.FirstOrDefault(o => o.Model.Id == operationId);
        if (SelectedOperation is null)
        {
            var existing = _project.Contours.Select(c => c.Id).ToHashSet();
            SelectedContourIds.UnionWith(contours.Where(existing.Contains));
        }

        IsDirty = true;
        StatusMessage(message);
        OnPropertyChanged(nameof(Title));
        OnSelectionChanged();
        // The restored project is the current state, whatever small formatting differences a new save may have.
        _history.ReplaceCurrent(ProjectSerializer.Serialize(_project));
        Regenerate();
        NotifyHistoryChanged();
    }

    /// <summary>Records the project state for undo when it differs from the last recorded one.</summary>
    private void CommitHistory()
    {
        if (_history.Commit(ProjectSerializer.Serialize(_project)))
        {
            NotifyHistoryChanged();
        }
    }

    private void NotifyHistoryChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Short message that replaces the previous one of the same kind.</summary>
    private void StatusMessage(string text)
    {
        // Messages written before a language switch are in the other language.
        if (Messages.Count > 0 && Messages[^1] is "Отменено." or "Повторено." or "Undone." or "Redone.")
        {
            Messages.RemoveAt(Messages.Count - 1);
        }

        Messages.Add(text);
    }

    private void OnProjectChanged()
    {
        if (_suppressChanges)
        {
            return;
        }

        IsDirty = true;
        // Coalesce bursts of edits (typing) into one regeneration.
        _regenerateTimer.Stop();
        _regenerateTimer.Start();
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void Regenerate()
    {
        _regenerateTimer.Stop();
        _generation = ToolpathGenerator.Generate(_project);
        _gcodeOutput = _generation.Toolpaths.Count == 0 ? null : GcodeWriter.WriteWithLines(_project.Name, _generation, _project.Machine);
        Gcode = _gcodeOutput?.Text ?? Loc.T("(Нет траекторий: добавьте операцию и выберите контуры)", "(No toolpaths: add an operation and select contours)");

        var start = new Vec3(0, 0, _generation.SafeZ);
        var stats = ToolpathStatistics.Compute(_generation.Toolpaths, _project.Machine, start);
        Statistics = _generation.Toolpaths.Count == 0
            ? ""
            : Loc.T(
                $"Резание: {stats.CutLength / 1000:0.00} м · Холостые: {stats.RapidLength / 1000:0.00} м · Время ≈ {stats.EstimatedTime:hh\\:mm\\:ss}",
                $"Cutting: {stats.CutLength / 1000:0.00} m · Rapids: {stats.RapidLength / 1000:0.00} m · Time ≈ {stats.EstimatedTime:hh\\:mm\\:ss}");

        // Keep import messages, replace generation warnings.
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].StartsWith("⚠ ", StringComparison.Ordinal))
            {
                Messages.RemoveAt(i);
            }
        }

        foreach (var warning in _generation.Warnings)
        {
            Messages.Add("⚠ " + warning);
        }

        foreach (var operation in Operations)
        {
            operation.RefreshSummary();
        }

        UpdateScene();
        Simulation.MarkStale();
        UpdateGcodeView();
        Control.RefreshLeveling();
        CommitHistory();
    }

    partial void OnGcodeChanged(string value) => UpdateGcodeView();

    /// <summary>
    /// Shows the running program or the project's G-code and highlights the line being machined or simulated.
    /// </summary>
    private void UpdateGcodeView()
    {
        var job = Control.IsJobActive ? Control.JobGcode : null;
        var text = job ?? Gcode;
        if (!ReferenceEquals(text, _shownGcode))
        {
            _shownGcode = text;
            CurrentGcodeLine = null;
            GcodeLines = SplitLines(text);
        }

        var number = 0;
        if (job is not null)
        {
            number = Control.ExecutingLine;
        }
        else if (Simulation.IsActive && _gcodeOutput is { } output && ReferenceEquals(Simulation.Generation, _generation))
        {
            number = output.LineOfMove(Simulation.CurrentMoveIndex);
        }

        var current = number >= 1 && number <= GcodeLines.Count ? GcodeLines[number - 1] : null;
        if (!ReferenceEquals(current, CurrentGcodeLine))
        {
            if (CurrentGcodeLine is not null)
            {
                CurrentGcodeLine.IsCurrent = false;
            }

            if (current is not null)
            {
                current.IsCurrent = true;
            }

            CurrentGcodeLine = current;
        }
    }

    /// <summary>Lines numbered as the GRBL sender counts them.</summary>
    private static IReadOnlyList<GcodeLineViewModel> SplitLines(string text)
    {
        var source = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', '\r');
        var count = source.Length > 0 && source[^1].Length == 0 ? source.Length - 1 : source.Length;
        var lines = new GcodeLineViewModel[count];
        for (var i = 0; i < count; i++)
        {
            lines[i] = new GcodeLineViewModel(i + 1, source[i]);
        }

        return lines;
    }

    private void UpdateScene()
    {
        var operationContours = SelectedOperation?.Model.ContourIds.ToHashSet() ?? new HashSet<int>();
        if (SelectedOperation?.Model is LaserPcbOperation pcb)
        {
            operationContours.UnionWith(pcb.BoardContourIds);
        }

        var contours = new List<SceneContour>(_project.Contours.Count);
        var bounds = Bounds2.Empty;
        // Contours are shown in program coordinates so that the axis cross marks the work zero.
        var origin = _generation.Origin;
        foreach (var contour in _project.Contours.Where(IsVisible))
        {
            var points = contour.Flatten(0.02).Select(p => p - origin).ToList();
            bounds = bounds.Union(Bounds2.Of(points));
            var state = SelectedContourIds.Contains(contour.Id) ? ContourState.Selected
                : operationContours.Contains(contour.Id) ? ContourState.InOperation
                : ContourState.Normal;
            contours.Add(new SceneContour(contour.Id, points, contour.IsClosed, state));
        }

        var toolpaths = new List<SceneToolpath>();
        var position = new Vec3(0, 0, _generation.SafeZ);
        foreach (var toolpath in _generation.Toolpaths)
        {
            toolpaths.Add(new SceneToolpath(toolpath.Moves, position, toolpath.Operation == SelectedOperation?.Model));
            if (toolpath.Moves.Count > 0)
            {
                position = toolpath.Moves[^1].Target;
            }

            // Reliefs, pictures and facing areas may have no contours: their toolpaths define what "show all" covers.
            if (toolpath.Operation is ReliefOperation or LaserRasterOperation or FacingOperation)
            {
                bounds = bounds.Union(Bounds2.Of(toolpath.Moves.Where(m => m.Kind != MoveKind.Rapid).Select(m => m.Target.XY)));
            }
        }

        Scene = new ViewScene(contours, toolpaths, bounds);
    }
}
