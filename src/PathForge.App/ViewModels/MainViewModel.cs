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
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const string DrawingFilter = "Чертежи (*.dxf;*.svg)|*.dxf;*.svg|DXF (*.dxf)|*.dxf|SVG (*.svg)|*.svg|Все файлы (*.*)|*.*";
    private const string StlFilter = "3D-модели STL (*.stl)|*.stl|Все файлы (*.*)|*.*";
    private const string ProjectFilter = "Проекты PathForge (*.pfproj)|*.pfproj";
    private const string GerberFilter = "Gerber (*.gbr;*.gtl;*.gbl;*.gko;*.gm1;*.ger)|*.gbr;*.gtl;*.gbl;*.gko;*.gm1;*.gml;*.ger;*.pho|Все файлы (*.*)|*.*";
    private const string DrillFilter = "Сверловка Excellon (*.drl;*.xln;*.txt)|*.drl;*.xln;*.txt;*.exc;*.drd|Все файлы (*.*)|*.*";
    private const string GcodeFilter = "G-code (*.nc)|*.nc|G-code (*.gcode)|*.gcode|Текст (*.txt)|*.txt";

    private readonly IDialogService _dialogs;
    private readonly UndoHistory _history = new();
    private readonly DispatcherTimer _regenerateTimer;
    private CamProject _project = CamProject.CreateDefault();
    private GenerationResult _generation = new();
    private bool _suppressChanges;

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        Control = new MachineControlViewModel(dialogs, CurrentMachineProgram, () => _project.Machine.SafeZ,
            () => _project.LevelingMap,
            map =>
            {
                _project.LevelingMap = map;
                OnProjectChanged();
            },
            ProgramCutBounds);
        Control.JobFinished += message => Messages.Add("Станок: " + message);
        Simulation = new SimulationViewModel(() => (_project, _generation));
        _regenerateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _regenerateTimer.Tick += (_, _) =>
        {
            _regenerateTimer.Stop();
            Regenerate();
        };

        LoadProject(_project, null);
    }

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

    public IReadOnlyList<ToolPreset> ToolPresetList => ToolPresets.Cnc3018;

    [ObservableProperty]
    private ViewScene scene = ViewScene.Empty;

    [ObservableProperty]
    private string gcode = "";

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
        ? $"Контуров: {_project.Contours.Count}"
        : $"Выделено контуров: {SelectedContourIds.Count} из {_project.Contours.Count}";

    /// <summary>Asks to save unsaved changes. Returns false when the user cancels.</summary>
    public bool ConfirmDiscardChanges()
    {
        if (!IsDirty)
        {
            return true;
        }

        var answer = _dialogs.AskYesNoCancel("Сохранить изменения в проекте?");
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

        var path = _dialogs.OpenFile("Открыть проект", ProjectFilter);
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
            _dialogs.ShowError($"Не удалось открыть проект:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private void SaveProject() => SaveProjectCore(ProjectPath);

    [RelayCommand]
    private void SaveProjectAs() => SaveProjectCore(null);

    private bool SaveProjectCore(string? path)
    {
        path ??= _dialogs.SaveFile("Сохранить проект", ProjectFilter, _project.Name + ProjectSerializer.FileExtension);
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
            _dialogs.ShowError($"Не удалось сохранить проект:\n{ex.Message}");
            return false;
        }
    }

    [RelayCommand]
    private void ImportDrawing()
    {
        var path = _dialogs.OpenFile("Импорт чертежа", DrawingFilter);
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
            Messages.Add($"Импортировано контуров: {contours.Count} (замкнутых: {contours.Count(c => c.IsClosed)}).");
            if (Operations.Count > 0)
            {
                Messages.Add("Контуры операций сброшены: выберите их заново на новом чертеже.");
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
            _dialogs.ShowError($"Не удалось прочитать чертёж:\n{ex.Message}");
        }
    }

    private static DxfImportResult ReadDrawing(string path) =>
        string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase)
            ? SvgReader.ReadFile(path)
            : DxfReader.ReadFile(path);

    /// <summary>Adds a DXF/SVG drawing to the current one (e.g. several parts or an SVG logo on a DXF plate).</summary>
    [RelayCommand]
    private void AddDrawing() => ImportPcbFile(DrawingFilter, "Добавить чертёж", path =>
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
            Name = $"Рельеф {Operations.Count + 1}",
            Source = ReliefSource.Mesh,
            Mesh = mesh,
            // STL files are usually in millimetres: keep the model size, but not deeper than the stock.
            WidthMm = Math.Round(Math.Max(5, maxX - minX), 2),
            Depth = Math.Round(Math.Clamp(maxZ - minZ, 0.5, Math.Max(0.5, _project.Stock.Thickness)), 2),
        };
        AddOperation(relief, BallNoseTool());
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AddImageRelief()
    {
        var image = LoadImage();
        if (image is null)
        {
            return;
        }

        AddOperation(new ReliefOperation { Name = $"Рельеф {Operations.Count + 1}", Image = image, WidthMm = 50, Depth = 2 }, BallNoseTool());
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
        var path = _dialogs.OpenFile("3D-модель STL", StlFilter);
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
            _dialogs.ShowError($"Не удалось прочитать STL:\n{ex.Message}");
            return null;
        }
    }

    private Tool? BallNoseTool()
    {
        var tool = _project.Tools.FirstOrDefault(t => t.Kind == ToolKind.BallNose);
        if (tool is null)
        {
            Messages.Add("Для рельефа добавьте сферическую фрезу: «Инструменты» → пресет «Рельеф 3D».");
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
    private void ImportGerberCopper() => ImportPcbFile(GerberFilter, "Импорт Gerber: медь", path =>
    {
        var result = GerberReader.ReadFile(path, GerberMode.Copper);
        return (result.Contours, result.Warnings);
    });

    [RelayCommand]
    private void ImportGerberOutline() => ImportPcbFile(GerberFilter, "Импорт Gerber: контур платы", path =>
    {
        var result = GerberReader.ReadFile(path, GerberMode.Outline);
        return (result.Contours, result.Warnings);
    });

    [RelayCommand]
    private void ImportExcellon() => ImportPcbFile(DrillFilter, "Импорт сверловки Excellon", path =>
    {
        var result = ExcellonReader.ReadFile(path);
        return (result.ToContours(), result.Warnings);
    });

    /// <summary>PCB layers are added to the drawing (copper, drills and outline come from separate files).</summary>
    private void ImportPcbFile(string filter, string title, Func<string, (List<Contour> Contours, List<string> Warnings)> read)
    {
        var path = _dialogs.OpenFile(title, filter);
        if (path is null)
        {
            return;
        }

        try
        {
            var (contours, warnings) = read(path);
            var nextId = _project.NextContourId();
            var layer = Path.GetFileName(path);
            foreach (var contour in contours)
            {
                contour.Id = nextId++;
                if (string.IsNullOrEmpty(contour.Layer))
                {
                    contour.Layer = layer;
                }
            }

            _project.Contours.AddRange(contours);
            RefreshLayers();
            _project.SourceFile ??= path;
            if (ProjectPath is null && _project.Contours.Count == contours.Count)
            {
                _project.Name = Path.GetFileNameWithoutExtension(path);
            }

            // The new contours are selected so that an operation can be added right away.
            SelectedContourIds.Clear();
            SelectedContourIds.UnionWith(contours.Select(c => c.Id));
            Messages.Add($"{layer}: добавлено контуров {contours.Count}.");
            foreach (var warning in warnings)
            {
                Messages.Add(warning);
            }

            OnPropertyChanged(nameof(Title));
            OnSelectionChanged();
            OnProjectChanged();
            Regenerate();
            ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError($"Не удалось прочитать файл:\n{ex.Message}");
        }
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
        Messages.Add("Чертёж зеркально отражён по X.");
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
            Messages.Add("Для изоляции добавьте гравёр: «Инструменты» → пресет «Текстолит (платы): Гравёр 20°».");
        }

        AddOperation(new IsolationOperation { Name = $"Изоляция {Operations.Count + 1}" }, engraver);
    }

    // ---- Laser ------------------------------------------------------------------------------

    [RelayCommand]
    private void AddLaserVector() =>
        AddOperation(new LaserVectorOperation { Name = $"Лазер {Operations.Count + 1}" }, LaserTool());

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
        AddOperation(new LaserRasterOperation { Name = $"Картинка {Operations.Count + 1}", Image = image, WidthMm = width }, LaserTool());
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
        var path = _dialogs.OpenFile("Картинка для гравировки", ImageLoader.Filter);
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
            _dialogs.ShowError($"Не удалось открыть картинку:\n{ex.Message}");
            return null;
        }
    }

    private Tool? LaserTool()
    {
        var laser = _project.Tools.FirstOrDefault(t => t.Kind == ToolKind.Laser);
        if (laser is null)
        {
            Messages.Add("Добавьте лазер: «Инструменты» → пресет «Лазер: Лазерный модуль 5 Вт», и выберите профиль станка с лазером.");
        }
        else if (!_project.Machine.LaserMode)
        {
            Messages.Add("Для лазера выберите профиль «CNC 3018 Pro (лазер 5 Вт)» на вкладке «Станок» и включите в GRBL $32=1.");
        }

        return laser;
    }

    [RelayCommand]
    private void ExportGcode()
    {
        Regenerate();
        if (_generation.Toolpaths.Count == 0)
        {
            _dialogs.ShowError("Нет траекторий для экспорта: добавьте операции и выберите контуры.");
            return;
        }

        var path = _dialogs.SaveFile("Сохранить G-code", GcodeFilter, _project.Name + ".nc");
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, Gcode);
            Messages.Add($"G-code сохранён: {path}");
        }
        catch (Exception ex)
        {
            _dialogs.ShowError($"Не удалось сохранить G-code:\n{ex.Message}");
        }
    }

    // ---- Operations ------------------------------------------------------------------------

    [RelayCommand]
    private void AddProfile() => AddOperation(new ProfileOperation { Name = $"Контур {Operations.Count + 1}" });

    [RelayCommand]
    private void AddPocket() => AddOperation(new PocketOperation { Name = $"Карман {Operations.Count + 1}" });

    [RelayCommand]
    private void AddDrill()
    {
        var drill = _project.Tools.FirstOrDefault(t => t.Kind == ToolKind.Drill);
        AddOperation(new DrillOperation { Name = $"Сверление {Operations.Count + 1}" }, drill);
    }

    private void AddOperation(Operation operation, Tool? preferredTool = null)
    {
        // Small spindles (3018) suffer from straight plunges: ramp by default (not for drills and shallow engraving).
        operation.Entry = operation is ProfileOperation or PocketOperation ? EntryMode.Ramp : EntryMode.Plunge;
        var tool = preferredTool ?? SelectedTool?.Model ?? _project.Tools.FirstOrDefault(t => t.Kind != ToolKind.Drill) ?? _project.Tools.FirstOrDefault();
        operation.ToolId = tool?.Id ?? "";
        operation.ContourIds = SelectedContourIds.OrderBy(i => i).ToList();
        _project.Operations.Add(operation);
        var vm = OperationViewModel.Create(operation, OnProjectChanged);
        Operations.Add(vm);
        SelectedOperation = vm;
        if (operation.ContourIds.Count == 0 && operation is not LaserRasterOperation)
        {
            Messages.Add($"{operation.Name}: выделите контуры на чертеже и нажмите «Назначить выделенные».");
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
        var tool = new Tool { Number = number, Name = $"Фреза {number}" };
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
            Messages.Add($"Применён профиль станка «{SelectedProfile.Name}».");
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
            _dialogs.ShowError("Инструмент используется в операциях. Сначала выберите в них другой инструмент.");
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
            Messages.Add("Сначала выделите хотя бы одно отверстие (окружность).");
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
            SelectedProfile = MachineProfiles.All.FirstOrDefault(p => p.Name == project.Machine.ProfileName) ?? MachineProfiles.All[0];
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
            RestoreState(state, "Отменено.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_history.Redo() is { } state)
        {
            RestoreState(state, "Повторено.");
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
        if (Messages.Count > 0 && Messages[^1] is "Отменено." or "Повторено.")
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
        Gcode = _generation.Toolpaths.Count == 0
            ? "(Нет траекторий: добавьте операцию и выберите контуры)"
            : GcodeWriter.Write(_project.Name, _generation, _project.Machine);

        var start = new Vec3(0, 0, _generation.SafeZ);
        var stats = ToolpathStatistics.Compute(_generation.Toolpaths, _project.Machine, start);
        Statistics = _generation.Toolpaths.Count == 0
            ? ""
            : $"Резание: {stats.CutLength / 1000:0.00} м · Холостые: {stats.RapidLength / 1000:0.00} м · Время ≈ {stats.EstimatedTime:hh\\:mm\\:ss}";

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
        Control.RefreshLeveling();
        CommitHistory();
    }

    private void UpdateScene()
    {
        var operationContours = SelectedOperation?.Model.ContourIds.ToHashSet() ?? new HashSet<int>();
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

            // Reliefs and pictures have no contours: their toolpaths define what "show all" covers.
            if (toolpath.Operation is ReliefOperation or LaserRasterOperation)
            {
                bounds = bounds.Union(Bounds2.Of(toolpath.Moves.Where(m => m.Kind != MoveKind.Rapid).Select(m => m.Target.XY)));
            }
        }

        Scene = new ViewScene(contours, toolpaths, bounds);
    }
}
