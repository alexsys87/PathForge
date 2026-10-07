using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathForge.App.Controls;
using PathForge.Core.Localization;
using PathForge.Core.Machining;
using PathForge.Core.Projects;
using PathForge.Core.Simulation;

namespace PathForge.App.ViewModels;

/// <summary>3D material removal simulation with playback.</summary>
public sealed partial class SimulationViewModel : ObservableObject
{
    /// <summary>Display grid size: enough detail, still fast to turn into a mesh.</summary>
    private const int DisplaySide = 320;

    private static readonly TimeSpan TickBudget = TimeSpan.FromMilliseconds(35);
    private static readonly TimeSpan SurfaceRefresh = TimeSpan.FromMilliseconds(150);

    private readonly Func<(CamProject Project, GenerationResult Generation)> _source;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _sinceSurface = new();
    private StockSimulation? _simulation;
    private ModelComparison? _comparison;
    private float[]? _deviation;
    private HeightField? _surface;
    private int _version;
    private double _target;
    private bool _stale = true;
    private bool _isActive;
    private bool _settingPosition;

    public SimulationViewModel(Func<(CamProject Project, GenerationResult Generation)> source)
    {
        _source = source;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick();
        SelectedSpeed = Speeds[2];
        SelectedQuality = Qualities[1];
    }

    public IReadOnlyList<Choice<double>> Speeds { get; } = new[]
    {
        new Choice<double>(1, "×1 (реальное время)", "×1 (real time)"),
        new Choice<double>(5, "×5", "×5"),
        new Choice<double>(20, "×20", "×20"),
        new Choice<double>(100, "×100", "×100"),
        new Choice<double>(1000, "×1000", "×1000"),
    };

    public IReadOnlyList<Choice<int>> Qualities { get; } = new[]
    {
        new Choice<int>(400_000, "Быстро", "Fast"),
        new Choice<int>(StockSimulation.DefaultMaxCells, "Обычно", "Normal"),
        new Choice<int>(4_000_000, "Точно (медленнее)", "Fine (slower)"),
    };

    public ObservableCollection<string> Issues { get; } = new();

    [ObservableProperty]
    private SimulationFrame? frame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayLabel))]
    private bool isPlaying;

    /// <summary>Slider position 0…1000 along the machining time.</summary>
    [ObservableProperty]
    private double position;

    [ObservableProperty]
    private Choice<double> selectedSpeed = null!;

    [ObservableProperty]
    private Choice<int> selectedQuality = null!;

    [ObservableProperty]
    private string timeText = "";

    [ObservableProperty]
    private string operationText = "";

    [ObservableProperty]
    private string statsText = "";

    /// <summary>Colour the stock by its difference from the relief model (left material, gouges).</summary>
    [ObservableProperty]
    private bool showComparison;

    /// <summary>Differences up to this are shown as "on the model" (mm).</summary>
    [ObservableProperty]
    private double comparisonTolerance = 0.05;

    /// <summary>The project has a relief (picture or STL) to compare with.</summary>
    [ObservableProperty]
    private bool hasModel;

    [ObservableProperty]
    private string comparisonText = "";

    /// <summary>Draw the toolpath of the operation being machined over the stock.</summary>
    [ObservableProperty]
    private bool showPath = true;

    /// <summary>The project holds a probed height map (auto-levelling).</summary>
    [ObservableProperty]
    private bool hasHeightMap;

    /// <summary>Show the probed height map over the stock.</summary>
    [ObservableProperty]
    private bool showHeightMap = true;

    /// <summary>Vertical exaggeration of the height map: board unevenness is a few hundredths of a millimetre.</summary>
    [ObservableProperty]
    private double heightMapScale = 50;

    [ObservableProperty]
    private string heightMapText = "";

    private Core.Leveling.LevelingMap? _heightMap;

    public string PlayLabel => IsPlaying ? Loc.T("❚❚ Пауза", "❚❚ Pause") : Loc.T("▶ Пуск", "▶ Play");

    /// <summary>Texts that are not recomputed by <see cref="MarkStale"/> follow the new language.</summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(PlayLabel));
        if (_simulation is not null)
        {
            UpdateTexts(_simulation);
        }
        else if (StatsText.Length > 0)
        {
            StatsText = NoToolpathsText;
        }
    }

    private static string NoToolpathsText => Loc.T("Нет траекторий: добавьте операции и выберите контуры.", "No toolpaths: add operations and select contours.");

    /// <summary>The toolpaths the current simulation was built from (null without one).</summary>
    public GenerationResult? Generation { get; private set; }

    /// <summary>Index of the move being simulated, counted through all toolpaths (-1 without a simulation).</summary>
    [ObservableProperty]
    private int currentMoveIndex = -1;

    /// <summary>The 3D tab is visible: only then the simulation is computed.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                if (value && _stale)
                {
                    Rebuild();
                }
                else if (!value)
                {
                    IsPlaying = false;
                    _timer.Stop();
                }
            }
        }
    }

    /// <summary>The toolpaths changed.</summary>
    public void MarkStale()
    {
        _stale = true;
        if (_isActive)
        {
            Rebuild();
        }
    }

    [RelayCommand]
    private void Rebuild()
    {
        _stale = false;
        IsPlaying = false;
        Issues.Clear();
        var (project, generation) = _source();
        _heightMap = project.LevelingMap is { IsValid: true } map ? map : null;
        HasHeightMap = _heightMap is not null;
        HeightMapText = _heightMap is null ? "" : Loc.T(
            $"Карта высот {_heightMap.CountX}×{_heightMap.CountY}, перепад {_heightMap.Max - _heightMap.Min:0.000} мм",
            $"Height map {_heightMap.CountX}×{_heightMap.CountY}, range {_heightMap.Max - _heightMap.Min:0.000} mm");
        Generation = generation;
        if (generation.Toolpaths.Count == 0)
        {
            _simulation = null;
            CurrentMoveIndex = -1;
            _comparison = null;
            _deviation = null;
            HasModel = false;
            ComparisonText = "";
            _surface = null;
            Frame = null;
            TimeText = "";
            OperationText = "";
            StatsText = NoToolpathsText;
            return;
        }

        _simulation = new StockSimulation(project, generation, maxCells: SelectedQuality.Value);
        _comparison = new ModelComparison(project, generation, _simulation.Field);
        HasModel = _comparison.HasModel;
        _deviation = null;
        ComparisonText = "";
        // The finished part is shown first; Start plays the machining from the beginning.
        _target = _simulation.TotalTime;
        SetPosition(1000);
        _sinceSurface.Restart();
        _surface = null;
        _timer.Start();
        Tick();
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_simulation is null)
        {
            return;
        }

        if (IsPlaying)
        {
            IsPlaying = false;
            return;
        }

        if (_target >= _simulation.TotalTime - 1e-9)
        {
            // From the end: start again.
            _target = 0;
        }

        IsPlaying = true;
        _clock.Restart();
        _timer.Start();
    }

    [RelayCommand]
    private void ToStart() => JumpTo(0);

    [RelayCommand]
    private void ToEnd() => JumpTo(double.PositiveInfinity);

    partial void OnPositionChanged(double value)
    {
        if (!_settingPosition && _simulation is not null)
        {
            _target = value / 1000 * _simulation.TotalTime;
            _timer.Start();
        }
    }

    partial void OnShowComparisonChanged(bool value) => RefreshSurface();

    partial void OnComparisonToleranceChanged(double value)
    {
        if (value < 0.001)
        {
            ComparisonTolerance = 0.001;
            return;
        }

        RefreshSurface();
    }

    partial void OnShowPathChanged(bool value) => RefreshSurface();

    partial void OnShowHeightMapChanged(bool value) => RefreshSurface();

    partial void OnHeightMapScaleChanged(double value)
    {
        if (value is < 1 or > 10000)
        {
            HeightMapScale = Math.Clamp(value, 1, 10000);
            return;
        }

        RefreshSurface();
    }

    /// <summary>Redraws the stock with the current display options.</summary>
    private void RefreshSurface()
    {
        if (_simulation is null)
        {
            return;
        }

        _surface = null;
        _timer.Start();
        Tick();
    }

    partial void OnSelectedQualityChanged(Choice<int> value)
    {
        if (_simulation is not null)
        {
            Rebuild();
        }
    }

    private void JumpTo(double time)
    {
        if (_simulation is null)
        {
            return;
        }

        IsPlaying = false;
        _target = Math.Clamp(time, 0, _simulation.TotalTime);
        SetPosition(_target / Math.Max(1e-12, _simulation.TotalTime) * 1000);
        _timer.Start();
    }

    private void Tick()
    {
        var simulation = _simulation;
        if (simulation is null)
        {
            _timer.Stop();
            return;
        }

        if (IsPlaying)
        {
            _target += _clock.Elapsed.TotalMinutes * SelectedSpeed.Value;
            _clock.Restart();
            if (_target >= simulation.TotalTime)
            {
                _target = simulation.TotalTime;
                IsPlaying = false;
            }
        }

        var reached = simulation.AdvanceTo(_target, TickBudget);
        var settled = reached && !IsPlaying;
        if (_surface is null || settled || _sinceSurface.Elapsed >= SurfaceRefresh)
        {
            _surface = simulation.Field.Downsample(DisplaySide);
            _version++;
            _sinceSurface.Restart();
            UpdateComparison(_surface);
            UpdateTexts(simulation);
        }

        CurrentMoveIndex = simulation.CurrentMoveIndex;
        var tip = simulation.ToolPosition;
        Frame = new SimulationFrame(_surface, _version, simulation.CurrentToolpath?.Tool, new Point3D(tip.X, tip.Y, tip.Z),
            ShowComparison && HasModel ? _deviation : null, ComparisonTolerance, ShowPath ? simulation.CurrentToolpath : null,
            ShowHeightMap ? _heightMap : null, HeightMapScale);
        if (IsPlaying)
        {
            SetPosition(simulation.CurrentTime / Math.Max(1e-12, simulation.TotalTime) * 1000);
        }

        TimeText = $"{Format(simulation.CurrentTime)} / {Format(simulation.TotalTime)}";
        if (settled)
        {
            UpdateTexts(simulation);
            _timer.Stop();
        }
    }

    /// <summary>Difference from the model for the display surface and its summary.</summary>
    private void UpdateComparison(HeightField display)
    {
        if (!ShowComparison || _comparison is not { HasModel: true } comparison)
        {
            _deviation = null;
            ComparisonText = "";
            return;
        }

        var result = comparison.Compare(ComparisonTolerance);
        _deviation = ModelComparison.Downsample(comparison.Field, result.Deviation, display);
        ComparisonText = Loc.T(
            $"В допуске {result.WithinPercent:0.#} % · недорез {result.LeftoverPercent:0.#} % (до {result.MaxLeftover:0.###} мм) · зарез {result.GougePercent:0.#} % (до {result.MaxGouge:0.###} мм)",
            $"Within tolerance {result.WithinPercent:0.#} % · material left {result.LeftoverPercent:0.#} % (up to {result.MaxLeftover:0.###} mm) · gouged {result.GougePercent:0.#} % (up to {result.MaxGouge:0.###} mm)");
    }

    private void UpdateTexts(StockSimulation simulation)
    {
        var toolpath = simulation.CurrentToolpath;
        OperationText = toolpath is null ? "" : $"{toolpath.Operation.Name} · {toolpath.Tool.Name}";
        var field = simulation.Field;
        StatsText = Loc.T(
            $"Снято {field.RemovedVolume() / 1000:0.0} см³ · сетка {field.Width}×{field.Height}, ячейка {field.CellSize:0.###} мм",
            $"Removed {field.RemovedVolume() / 1000:0.0} cm³ · grid {field.Width}×{field.Height}, cell {field.CellSize:0.###} mm");

        if (Issues.Count != simulation.Issues.Count)
        {
            Issues.Clear();
            foreach (var issue in simulation.Issues)
            {
                Issues.Add("⚠ " + issue);
            }
        }
    }

    private void SetPosition(double value)
    {
        _settingPosition = true;
        Position = value;
        _settingPosition = false;
    }

    private static string Format(double minutes) => TimeSpan.FromMinutes(minutes).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}
