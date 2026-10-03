using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathForge.App.Services;
using PathForge.Core.Geometry;
using PathForge.Core.Grbl;
using PathForge.Core.Leveling;
using PathForge.Core.Localization;

namespace PathForge.App.ViewModels;

/// <summary>Program text to run on the machine.</summary>
public sealed record MachineProgram(string Name, string Gcode, bool IsGrblDialect);

/// <summary>Machine control panel: connection, position, jogging, zeroing, probing and running programs (GRBL).</summary>
public sealed partial class MachineControlViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 500;

    private static string GcodeFileFilter =>
        "G-code (*.nc;*.gcode;*.ngc;*.tap;*.txt)|*.nc;*.gcode;*.ngc;*.tap;*.txt|" + Loc.T("Все файлы", "All files") + " (*.*)|*.*";

    private static string NotConnectedText => Loc.T("Нет связи", "Not connected");

    private static string NoMapText => Loc.T("Карта высот не снята.", "No height map measured.");

    private static string CurrentProjectText => Loc.T("Текущий проект", "Current project");

    private readonly IDialogService _dialogs;
    private readonly Func<MachineProgram?> _projectProgram;
    private readonly Func<double> _safeZ;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private GrblController? _controller;
    private int _refreshQueued;
    private DateTime? _jobStarted;
    private readonly Func<LevelingMap?> _getMap;
    private readonly Action<LevelingMap?> _setMap;
    private readonly Func<Bounds2> _programBounds;
    private MachineProgram? _fileProgram;
    private LevelingProbe? _probe;

    public MachineControlViewModel(IDialogService dialogs, Func<MachineProgram?> projectProgram, Func<double> safeZ,
        Func<LevelingMap?> getMap, Action<LevelingMap?> setMap, Func<Bounds2> programBounds)
    {
        _dialogs = dialogs;
        _projectProgram = projectProgram;
        _safeZ = safeZ;
        _getMap = getMap;
        _setMap = setMap;
        _programBounds = programBounds;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _pollTimer.Tick += (_, _) =>
        {
            _controller?.RequestStatus();
            if (_jobStarted is not null)
            {
                Refresh();
            }
        };
        RefreshPorts();
    }

    /// <summary>A program finished or was aborted (shown in the main message list).</summary>
    public event Action<string>? JobFinished;

    public ObservableCollection<string> Ports { get; } = new();

    public IReadOnlyList<int> BaudRates { get; } = new[] { 115200, 250000, 57600, 38400, 19200, 9600 };

    public IReadOnlyList<double> JogSteps { get; } = new[] { 0.01, 0.1, 0.5, 1, 5, 10, 50 };

    /// <summary>Newest entries first.</summary>
    public ObservableCollection<string> Log { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string? selectedPort;

    [ObservableProperty]
    private int baudRate = 115200;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    private bool isConnected;

    [ObservableProperty]
    private string stateText = NotConnectedText;

    [ObservableProperty]
    private string versionText = "";

    [ObservableProperty]
    private string workX = "—";

    [ObservableProperty]
    private string workY = "—";

    [ObservableProperty]
    private string workZ = "—";

    [ObservableProperty]
    private string machineText = "";

    [ObservableProperty]
    private string feedText = "";

    [ObservableProperty]
    private string overridesText = "";

    [ObservableProperty]
    private bool isAlarm;

    /// <summary>Jogging, zeroing and commands are possible.</summary>
    [ObservableProperty]
    private bool canControl;

    [ObservableProperty]
    private bool isJobActive;

    [ObservableProperty]
    private bool canStartJob;

    [ObservableProperty]
    private bool canPause;

    [ObservableProperty]
    private bool canResume;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string progressText = "";

    /// <summary>Tool change or error explanation while the program waits for the operator.</summary>
    [ObservableProperty]
    private string jobMessage = "";

    [ObservableProperty]
    private string programSource = CurrentProjectText;

    [ObservableProperty]
    private double jogStep = 1;

    [ObservableProperty]
    private double jogFeed = 1000;

    [ObservableProperty]
    private double jogFeedZ = 300;

    /// <summary>Thickness of the probe plate (0 when touching the stock directly with a clip).</summary>
    [ObservableProperty]
    private double probePlate;

    [ObservableProperty]
    private double probeTravel = 20;

    [ObservableProperty]
    private double probeFeed = 50;

    [ObservableProperty]
    private string consoleInput = "";

    /// <summary>Work position of the tool for the 2D view (null without connection).</summary>
    [ObservableProperty]
    private Vec2? toolPosition;

    public bool IsDisconnected => !IsConnected;

    // ---- Height map (auto-levelling) ---------------------------------------------------------

    [ObservableProperty]
    private double levelMinX;

    [ObservableProperty]
    private double levelMinY;

    [ObservableProperty]
    private double levelMaxX = 50;

    [ObservableProperty]
    private double levelMaxY = 30;

    [ObservableProperty]
    private double levelStep = 10;

    [ObservableProperty]
    private double levelClearance = 1;

    [ObservableProperty]
    private double levelDepth = 1.5;

    [ObservableProperty]
    private double levelFeed = 30;

    /// <summary>Apply the height map to programs started from this panel.</summary>
    [ObservableProperty]
    private bool useLeveling = true;

    [ObservableProperty]
    private bool hasLevelingMap;

    [ObservableProperty]
    private string levelingText = NoMapText;

    [ObservableProperty]
    private string levelingTable = "";

    [ObservableProperty]
    private bool isProbing;

    /// <summary>Shows the texts of the panel in the current interface language.</summary>
    public void RefreshLanguage()
    {
        if (_fileProgram is null)
        {
            ProgramSource = CurrentProjectText;
        }

        RefreshLeveling();
        Refresh();
    }

    /// <summary>Shows the map stored in the project (after loading, undo or measuring).</summary>
    public void RefreshLeveling()
    {
        var map = _getMap();
        HasLevelingMap = map is { IsValid: true };
        if (map is not { IsValid: true })
        {
            LevelingText = NoMapText;
            LevelingTable = "";
            return;
        }

        LevelingText = Loc.T(
            $"Карта {map.CountX}×{map.CountY} точек, X {map.X0:0.#}…{map.X1:0.#}, Y {map.Y0:0.#}…{map.Y1:0.#} мм, " +
            $"перепад {map.Max - map.Min:0.000} мм, снята {map.Measured:g}. Z0 — в первой точке (X{map.X0:0.#} Y{map.Y0:0.#}).",
            $"Map of {map.CountX}×{map.CountY} points, X {map.X0:0.#}…{map.X1:0.#}, Y {map.Y0:0.#}…{map.Y1:0.#} mm, " +
            $"range {map.Max - map.Min:0.000} mm, measured {map.Measured:g}. Z0 is at the first point (X{map.X0:0.#} Y{map.Y0:0.#}).");
        if (map.CountX <= 12)
        {
            var rows = new List<string>();
            for (var j = map.CountY - 1; j >= 0; j--)
            {
                rows.Add(string.Join(" ", Enumerable.Range(0, map.CountX).Select(i => map[i, j].ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture))));
            }

            LevelingTable = string.Join("\n", rows);
        }
        else
        {
            LevelingTable = "";
        }
    }

    [RelayCommand]
    private void LevelAreaFromProgram()
    {
        var bounds = _programBounds();
        if (bounds.IsEmpty)
        {
            _dialogs.ShowError(Loc.T("В проекте нет траекторий.", "The project has no toolpaths."));
            return;
        }

        // A little beyond the cuts so that no move lies outside the map.
        LevelMinX = Math.Round(bounds.MinX - 1, 1);
        LevelMinY = Math.Round(bounds.MinY - 1, 1);
        LevelMaxX = Math.Round(bounds.MaxX + 1, 1);
        LevelMaxY = Math.Round(bounds.MaxY + 1, 1);
    }

    [RelayCommand]
    private void ProbeMap()
    {
        if (_controller is null)
        {
            _dialogs.ShowError(Loc.T("Сначала подключитесь к станку.", "Connect to the machine first."));
            return;
        }

        LevelingProbe probe;
        try
        {
            probe = new LevelingProbe(new Bounds2(LevelMinX, LevelMinY, LevelMaxX, LevelMaxY), LevelStep, LevelClearance, LevelDepth, feed: LevelFeed);
        }
        catch (ArgumentException ex)
        {
            _dialogs.ShowError(ex.Message);
            return;
        }

        if (!_dialogs.Confirm(Loc.T(
                $"Снять карту высот: {probe.Points.Count} точек ({probe.CountX}×{probe.CountY}), шаг {LevelStep:0.#} мм.\n\n" +
                "• Зажим щупа — на гравёре, второй провод — на медь платы (или пластина под фрезой).\n" +
                $"• Фреза — на 2–5 мм над платой: станок сначала поедет в X{LevelMinX:0.#} Y{LevelMinY:0.#} на текущей высоте.\n" +
                "• В первой точке будет установлен Z0, остальные высоты считаются от неё.\n\nНачать?",
                $"Probe the height map: {probe.Points.Count} points ({probe.CountX}×{probe.CountY}), step {LevelStep:0.#} mm.\n\n" +
                "• Probe clip on the engraver, the second wire on the board copper (or a plate under the tool).\n" +
                $"• Tool 2–5 mm above the board: the machine first moves to X{LevelMinX:0.#} Y{LevelMinY:0.#} at the current height.\n" +
                "• Z0 is set at the first point, the other heights are measured from it.\n\nStart?")))
        {
            return;
        }

        _probe = probe;
        IsProbing = true;
        if (Run(c => probe.Commands().ForEach(c.SendCommand)))
        {
            AddLog(new GrblLogEntry(GrblLogKind.Info, Loc.T($"Съёмка карты высот: {probe.Points.Count} точек.", $"Probing the height map: {probe.Points.Count} points.")));
        }
        else
        {
            _probe = null;
            IsProbing = false;
        }
    }

    [RelayCommand]
    private void ClearMap()
    {
        if (_getMap() is not null && _dialogs.Confirm(Loc.T("Удалить карту высот из проекта?", "Delete the height map from the project?")))
        {
            _setMap(null);
            RefreshLeveling();
        }
    }

    private void OnProbeTouched(Vec3 machine)
    {
        if (_probe is not { } probe)
        {
            return;
        }

        if (probe.AddTouch(machine))
        {
            var map = probe.ToMap(DateTime.Now);
            _probe = null;
            IsProbing = false;
            _setMap(map);
            RefreshLeveling();
            AddLog(new GrblLogEntry(GrblLogKind.Info, Loc.T(
                $"Карта высот снята: перепад {map.Max - map.Min:0.000} мм.",
                $"Height map measured: range {map.Max - map.Min:0.000} mm.")));
            JobFinished?.Invoke(LevelingText);
        }
        else
        {
            ProgressText = Loc.T($"Карта высот: точка {probe.Measured} из {probe.Points.Count}", $"Height map: point {probe.Measured} of {probe.Points.Count}");
        }
    }

    // ---- Connection ---------------------------------------------------------------------------

    [RelayCommand]
    private void RefreshPorts()
    {
        var current = SelectedPort;
        Ports.Clear();
        foreach (var port in SerialPortTransport.PortNames())
        {
            Ports.Add(port);
        }

        SelectedPort = current is not null && Ports.Contains(current) ? current : Ports.LastOrDefault();
    }

    private bool CanConnect() => !string.IsNullOrEmpty(SelectedPort);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        if (IsConnected || SelectedPort is null)
        {
            return;
        }

        try
        {
            var transport = new SerialPortTransport(SelectedPort, BaudRate);
            _controller = new GrblController(transport);
            _controller.Changed += QueueRefresh;
            _controller.Log += entry => _dispatcher.BeginInvoke(() => AddLog(entry));
            _controller.JobFinished += result => _dispatcher.BeginInvoke(() => OnJobFinished(result));
            _controller.ProbeTouched += p => _dispatcher.BeginInvoke(() => OnProbeTouched(p));
            IsConnected = true;
            AddLog(new GrblLogEntry(GrblLogKind.Info, Loc.T($"Подключено: {SelectedPort}, {BaudRate} бод. Ожидание ответа GRBL…", $"Connected: {SelectedPort}, {BaudRate} baud. Waiting for GRBL…")));
            _pollTimer.Start();
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _dialogs.ShowError(Loc.T($"Не удалось открыть {SelectedPort}:\n{ex.Message}\n\nПорт может быть занят другой программой (Candle, Arduino IDE).", $"Could not open {SelectedPort}:\n{ex.Message}\n\nThe port may be used by another program (Candle, Arduino IDE)."));
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        if (IsJobActive && !_dialogs.Confirm(Loc.T("Программа ещё выполняется. Отключиться? Станок остановится только после конца буфера команд.", "The program is still running. Disconnect? The machine stops only after its command buffer is empty.")))
        {
            return;
        }

        CloseConnection();
        AddLog(new GrblLogEntry(GrblLogKind.Info, Loc.T("Отключено.", "Disconnected.")));
    }

    private void CloseConnection()
    {
        _pollTimer.Stop();
        _controller?.Dispose();
        _controller = null;
        _jobStarted = null;
        IsConnected = false;
        Refresh();
    }

    // ---- Machine ------------------------------------------------------------------------------

    [RelayCommand]
    private void Unlock() => Run(c => c.Unlock());

    [RelayCommand]
    private void Home() => Run(c => c.Home());

    [RelayCommand]
    private void SoftReset() => Run(c => c.SoftReset());

    /// <summary>Parameter: axis and direction, e.g. "X+" or "Z-".</summary>
    [RelayCommand]
    private void Jog(string? direction)
    {
        if (direction is not { Length: 2 })
        {
            return;
        }

        var distance = (direction[1] == '-' ? -1 : 1) * JogStep;
        Run(c => c.Jog(
            direction[0] == 'X' ? distance : 0,
            direction[0] == 'Y' ? distance : 0,
            direction[0] == 'Z' ? distance : 0,
            direction[0] == 'Z' ? JogFeedZ : JogFeed));
    }

    [RelayCommand]
    private void JogCancel() => _controller?.JogCancel();

    /// <summary>Parameter: axes to zero, e.g. "XY".</summary>
    [RelayCommand]
    private void Zero(string? axes)
    {
        axes ??= "";
        Run(c => c.SetWorkZero(axes.Contains('X'), axes.Contains('Y'), axes.Contains('Z')));
    }

    [RelayCommand]
    private void GoToZero() => Run(c => c.GoToWorkZero(_safeZ()));

    [RelayCommand]
    private void ProbeZ()
    {
        if (!_dialogs.Confirm(Loc.T(
                "Пластина щупа лежит на заготовке под фрезой, зажим щупа на фрезе?\n" +
                $"Фреза опустится максимум на {ProbeTravel:0.#} мм со скоростью {ProbeFeed:0} мм/мин, " +
                $"затем Z0 будет установлен на поверхности (толщина пластины {ProbePlate:0.###} мм).",
                "Is the probe plate on the stock under the tool and the probe clip on the tool?\n" +
                $"The tool goes down at most {ProbeTravel:0.#} mm at {ProbeFeed:0} mm/min, " +
                $"then Z0 is set on the surface (plate thickness {ProbePlate:0.###} mm).")))
        {
            return;
        }

        Run(c => c.ProbeZ(ProbePlate, ProbeTravel, ProbeFeed, retract: 3));
    }

    /// <summary>Parameter: "+", "-" or "0".</summary>
    [RelayCommand]
    private void FeedOverride(string? step) => _controller?.FeedOverride(Direction(step));

    [RelayCommand]
    private void SpindleOverride(string? step) => _controller?.SpindleOverride(Direction(step));

    private static int Direction(string? step) => step switch { "+" => 1, "-" => -1, _ => 0 };

    [RelayCommand]
    private void SendConsole()
    {
        var command = ConsoleInput.Trim();
        if (command.Length == 0)
        {
            return;
        }

        Run(c => c.SendCommand(command));
        ConsoleInput = "";
    }

    // ---- Program ------------------------------------------------------------------------------

    [RelayCommand]
    private void UseProject()
    {
        _fileProgram = null;
        ProgramSource = CurrentProjectText;
    }

    [RelayCommand]
    private void LoadFile()
    {
        var path = _dialogs.OpenFile(Loc.T("Программа для станка", "Program for the machine"), GcodeFileFilter);
        if (path is null)
        {
            return;
        }

        try
        {
            _fileProgram = new MachineProgram(Path.GetFileName(path), File.ReadAllText(path), IsGrblDialect: true);
            ProgramSource = Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(Loc.T($"Не удалось прочитать файл:\n{ex.Message}", $"Could not read the file:\n{ex.Message}"));
        }
    }

    [RelayCommand]
    private void StartJob()
    {
        if (_controller is null)
        {
            return;
        }

        var program = _fileProgram ?? _projectProgram();
        if (program is null)
        {
            _dialogs.ShowError(Loc.T("В проекте нет траекторий: добавьте операции и выберите контуры.", "The project has no toolpaths: add operations and select contours."));
            return;
        }

        if (!program.IsGrblDialect &&
            !_dialogs.Confirm(Loc.T(
                "Программа записана в общем формате G-code (смена инструмента T M6), а не для GRBL.\n" +
                "Выберите формат GRBL на вкладке «Станок». Всё равно запустить?",
                "The program is written in generic G-code (tool change T M6), not for GRBL.\n" +
                "Choose the GRBL format on the “Machine” tab. Run anyway?")))
        {
            return;
        }

        var gcode = program.Gcode;
        var levelingNote = "";
        if (UseLeveling && _getMap() is { IsValid: true } map)
        {
            var leveled = LevelingCompensator.Apply(gcode, map);
            if (leveled.Warnings.Count > 0 &&
                !_dialogs.Confirm(Loc.T("Поправка по карте высот:\n", "Height map correction:\n") + string.Join("\n", leveled.Warnings) +
                                  Loc.T("\n\nПродолжить?", "\n\nContinue?")))
            {
                return;
            }

            gcode = leveled.Gcode;
            levelingNote = Loc.T(
                $"\n\nС поправкой по карте высот (перепад {map.Max - map.Min:0.000} мм): Z0 должен быть выставлен в X{map.X0:0.#} Y{map.Y0:0.#}.",
                $"\n\nWith height map correction (range {map.Max - map.Min:0.000} mm): Z0 must be set at X{map.X0:0.#} Y{map.Y0:0.#}.");
        }

        var prepared = GrblProgram.Prepare(gcode);
        if (prepared.CommandCount == 0)
        {
            _dialogs.ShowError(Loc.T("В программе нет команд.", "The program has no commands."));
            return;
        }

        if (prepared.Problems.Count > 0)
        {
            _dialogs.ShowError(Loc.T("Программу нельзя отправить в GRBL:\n", "The program cannot be sent to GRBL:\n") +
                               string.Join("\n", prepared.Problems.Take(10)));
            return;
        }

        var stops = prepared.Lines.Count(l => l.StopAfter);
        var message = Loc.T(
                          $"Запустить «{program.Name}» ({prepared.CommandCount} строк)?\n\n" +
                          "Проверьте: заготовка закреплена, ноль X/Y/Z выставлен, в шпинделе нужная фреза, руки и инструмент убраны.",
                          $"Run “{program.Name}” ({prepared.CommandCount} lines)?\n\n" +
                          "Check: the stock is clamped, X/Y/Z zero is set, the right tool is in the spindle, hands and tools are clear.") +
                      (stops > 0
                          ? Loc.T(
                              $"\n\nОстановок для смены инструмента: {stops}. На них программа ждёт, пока вы не нажмёте «Продолжить».",
                              $"\n\nTool change stops: {stops}. At each the program waits until you press “Resume”.")
                          : "") +
                      levelingNote;
        if (!_dialogs.Confirm(message))
        {
            return;
        }

        if (Run(c => c.StartJob(prepared.Lines)))
        {
            _jobStarted = DateTime.Now;
            Refresh();
        }
    }

    [RelayCommand]
    private void Pause() => Run(c => c.Pause());

    [RelayCommand]
    private void Resume() => Run(c => c.Resume());

    [RelayCommand]
    private void Stop() => Run(c => c.Stop());

    /// <summary>Asks before closing the window while the machine is working. Returns false to keep the window open.</summary>
    public bool ConfirmClose()
    {
        if (IsJobActive && !_dialogs.Confirm(Loc.T("На станке выполняется программа. Остановить её и закрыть PathForge?", "A program is running on the machine. Stop it and close PathForge?")))
        {
            return false;
        }

        if (IsJobActive)
        {
            _controller?.Stop();
        }

        return true;
    }

    public void Dispose() => CloseConnection();

    // ---- Updating the view --------------------------------------------------------------------

    private bool Run(Action<GrblController> action)
    {
        if (_controller is null)
        {
            _dialogs.ShowError(Loc.T("Сначала подключитесь к станку.", "Connect to the machine first."));
            return false;
        }

        try
        {
            action(_controller);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _dialogs.ShowError(ex.Message);
            return false;
        }
        finally
        {
            Refresh();
        }
    }

    /// <summary>Controller events come from the serial thread: coalesce them into one update on the UI thread.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                Refresh();
            });
        }
    }

    private void Refresh()
    {
        var controller = _controller;
        if (controller is not null && !controller.IsConnected)
        {
            // The connection was lost (cable): release the port so it can be opened again.
            _dispatcher.BeginInvoke(CloseConnection);
            controller = null;
        }

        if (controller is null)
        {
            StateText = NotConnectedText;
            VersionText = "";
            WorkX = WorkY = WorkZ = "—";
            MachineText = FeedText = OverridesText = "";
            IsAlarm = CanControl = IsJobActive = CanStartJob = CanPause = CanResume = false;
            _probe = null;
            IsProbing = false;
            JobMessage = "";
            ToolPosition = null;
            return;
        }

        if (_probe is not null && !controller.IsBusy && !_probe.IsComplete)
        {
            _probe = null;
            IsProbing = false;
            AddLog(new GrblLogEntry(GrblLogKind.Error, Loc.T("Съёмка карты высот прервана (щуп не коснулся, авария или ошибка). Карта не изменена.", "Height map probing aborted (no probe contact, alarm or error). The map was not changed.")));
        }

        var status = controller.Status;
        var job = controller.Job;
        StateText = job switch
        {
            GrblJobState.ProgramStop => Loc.T("Остановка программы — ждёт оператора", "Program stop — waiting for the operator"),
            GrblJobState.Error => Loc.T("Ошибка в программе — пауза", "Program error — paused"),
            GrblJobState.Stopping => Loc.T("Остановка…", "Stopping…"),
            _ => GrblMessages.StateName(status.State),
        };
        VersionText = controller.Version;
        var known = status.State != GrblState.Unknown;
        WorkX = known ? Format(status.WorkPosition.X) : "—";
        WorkY = known ? Format(status.WorkPosition.Y) : "—";
        WorkZ = known ? Format(status.WorkPosition.Z) : "—";
        MachineText = known
            ? Loc.T("Машинные", "Machine") + $": X {Format(status.MachinePosition.X)}  Y {Format(status.MachinePosition.Y)}  Z {Format(status.MachinePosition.Z)}"
            : "";
        FeedText = known
            ? Loc.T($"Подача {status.Feed:0} мм/мин · шпиндель {status.Spindle:0}", $"Feed {status.Feed:0} mm/min · spindle {status.Spindle:0}")
            : "";
        OverridesText = Loc.T(
            $"Подача {status.FeedOverride} % · шпиндель {status.SpindleOverride} %",
            $"Feed {status.FeedOverride} % · spindle {status.SpindleOverride} %");
        IsAlarm = status.State == GrblState.Alarm;
        CanControl = controller.CanSendCommands && status.State is GrblState.Idle or GrblState.Jog or GrblState.Alarm;
        IsJobActive = job != GrblJobState.None;
        CanStartJob = job == GrblJobState.None && status.State == GrblState.Idle;
        CanPause = job == GrblJobState.Running;
        CanResume = job is GrblJobState.Paused or GrblJobState.Error or GrblJobState.ProgramStop;
        JobMessage = job is GrblJobState.ProgramStop
            ? controller.JobMessage + Loc.T(
                ". Смените инструмент, выставьте Z0 (кнопки или щуп) и нажмите «Продолжить».",
                ". Change the tool, set Z0 (buttons or probe) and press “Resume”.")
            : controller.JobMessage;
        ToolPosition = known ? status.WorkPosition.XY : null;

        if (IsJobActive && controller.TotalCommands > 0)
        {
            Progress = 100.0 * controller.AcknowledgedCommands / controller.TotalCommands;
            var elapsed = _jobStarted is { } start ? DateTime.Now - start : TimeSpan.Zero;
            ProgressText = Loc.T(
                $"{controller.AcknowledgedCommands} из {controller.TotalCommands} строк · {Progress:0} % · {elapsed:hh\\:mm\\:ss}",
                $"{controller.AcknowledgedCommands} of {controller.TotalCommands} lines · {Progress:0} % · {elapsed:hh\\:mm\\:ss}");
        }
    }

    private void OnJobFinished(GrblJobResult result)
    {
        var elapsed = _jobStarted is { } start ? DateTime.Now - start : TimeSpan.Zero;
        _jobStarted = null;
        if (result.Success)
        {
            Progress = 100;
        }

        ProgressText = result.Message + Loc.T(" Время: ", " Time: ") + elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        JobFinished?.Invoke(ProgressText);
        Refresh();
    }

    private void AddLog(GrblLogEntry entry)
    {
        var prefix = entry.Kind switch
        {
            GrblLogKind.Sent => "→ ",
            GrblLogKind.Received => "← ",
            GrblLogKind.Error => "⚠ ",
            _ => "• ",
        };
        Log.Insert(0, prefix + GrblMessages.Describe(entry.Text));
        while (Log.Count > MaxLogLines)
        {
            Log.RemoveAt(Log.Count - 1);
        }
    }

    private static string Format(double value) => value.ToString("0.000", CultureInfo.CurrentCulture);
}
