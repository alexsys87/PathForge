using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathForge.App.Services;
using PathForge.Core.Geometry;
using PathForge.Core.Grbl;

namespace PathForge.App.ViewModels;

/// <summary>Program text to run on the machine.</summary>
public sealed record MachineProgram(string Name, string Gcode, bool IsGrblDialect);

/// <summary>Machine control panel: connection, position, jogging, zeroing, probing and running programs (GRBL).</summary>
public sealed partial class MachineControlViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 500;
    private const string GcodeFileFilter = "G-code (*.nc;*.gcode;*.ngc;*.tap;*.txt)|*.nc;*.gcode;*.ngc;*.tap;*.txt|Все файлы (*.*)|*.*";

    private readonly IDialogService _dialogs;
    private readonly Func<MachineProgram?> _projectProgram;
    private readonly Func<double> _safeZ;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private GrblController? _controller;
    private int _refreshQueued;
    private DateTime? _jobStarted;
    private MachineProgram? _fileProgram;

    public MachineControlViewModel(IDialogService dialogs, Func<MachineProgram?> projectProgram, Func<double> safeZ)
    {
        _dialogs = dialogs;
        _projectProgram = projectProgram;
        _safeZ = safeZ;
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
    private string stateText = "Нет связи";

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
    private string programSource = "Текущий проект";

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
            IsConnected = true;
            AddLog(new GrblLogEntry(GrblLogKind.Info, $"Подключено: {SelectedPort}, {BaudRate} бод. Ожидание ответа GRBL…"));
            _pollTimer.Start();
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _dialogs.ShowError($"Не удалось открыть {SelectedPort}:\n{ex.Message}\n\nПорт может быть занят другой программой (Candle, Arduino IDE).");
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        if (IsJobActive && !_dialogs.Confirm("Программа ещё выполняется. Отключиться? Станок остановится только после конца буфера команд."))
        {
            return;
        }

        CloseConnection();
        AddLog(new GrblLogEntry(GrblLogKind.Info, "Отключено."));
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
        if (!_dialogs.Confirm($"Пластина щупа лежит на заготовке под фрезой, зажим щупа на фрезе?\n" +
                              $"Фреза опустится максимум на {ProbeTravel:0.#} мм со скоростью {ProbeFeed:0} мм/мин, " +
                              $"затем Z0 будет установлен на поверхности (толщина пластины {ProbePlate:0.###} мм)."))
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
        ProgramSource = "Текущий проект";
    }

    [RelayCommand]
    private void LoadFile()
    {
        var path = _dialogs.OpenFile("Программа для станка", GcodeFileFilter);
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
            _dialogs.ShowError($"Не удалось прочитать файл:\n{ex.Message}");
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
            _dialogs.ShowError("В проекте нет траекторий: добавьте операции и выберите контуры.");
            return;
        }

        if (!program.IsGrblDialect &&
            !_dialogs.Confirm("Программа записана в общем формате G-code (смена инструмента T M6), а не для GRBL.\n" +
                              "Выберите формат GRBL на вкладке «Станок». Всё равно запустить?"))
        {
            return;
        }

        var prepared = GrblProgram.Prepare(program.Gcode);
        if (prepared.CommandCount == 0)
        {
            _dialogs.ShowError("В программе нет команд.");
            return;
        }

        if (prepared.Problems.Count > 0)
        {
            _dialogs.ShowError("Программу нельзя отправить в GRBL:\n" + string.Join("\n", prepared.Problems.Take(10)));
            return;
        }

        var stops = prepared.Lines.Count(l => l.StopAfter);
        var message = $"Запустить «{program.Name}» ({prepared.CommandCount} строк)?\n\n" +
                      "Проверьте: заготовка закреплена, ноль X/Y/Z выставлен, в шпинделе нужная фреза, руки и инструмент убраны." +
                      (stops > 0 ? $"\n\nОстановок для смены инструмента: {stops}. На них программа ждёт, пока вы не нажмёте «Продолжить»." : "");
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
        if (IsJobActive && !_dialogs.Confirm("На станке выполняется программа. Остановить её и закрыть PathForge?"))
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
            _dialogs.ShowError("Сначала подключитесь к станку.");
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
            StateText = "Нет связи";
            VersionText = "";
            WorkX = WorkY = WorkZ = "—";
            MachineText = FeedText = OverridesText = "";
            IsAlarm = CanControl = IsJobActive = CanStartJob = CanPause = CanResume = false;
            JobMessage = "";
            ToolPosition = null;
            return;
        }

        var status = controller.Status;
        var job = controller.Job;
        StateText = job switch
        {
            GrblJobState.ProgramStop => "Остановка программы — ждёт оператора",
            GrblJobState.Error => "Ошибка в программе — пауза",
            GrblJobState.Stopping => "Остановка…",
            _ => GrblMessages.StateName(status.State),
        };
        VersionText = controller.Version;
        var known = status.State != GrblState.Unknown;
        WorkX = known ? Format(status.WorkPosition.X) : "—";
        WorkY = known ? Format(status.WorkPosition.Y) : "—";
        WorkZ = known ? Format(status.WorkPosition.Z) : "—";
        MachineText = known
            ? $"Машинные: X {Format(status.MachinePosition.X)}  Y {Format(status.MachinePosition.Y)}  Z {Format(status.MachinePosition.Z)}"
            : "";
        FeedText = known ? $"Подача {status.Feed:0} мм/мин · шпиндель {status.Spindle:0}" : "";
        OverridesText = $"Подача {status.FeedOverride} % · шпиндель {status.SpindleOverride} %";
        IsAlarm = status.State == GrblState.Alarm;
        CanControl = controller.CanSendCommands && status.State is GrblState.Idle or GrblState.Jog or GrblState.Alarm;
        IsJobActive = job != GrblJobState.None;
        CanStartJob = job == GrblJobState.None && status.State == GrblState.Idle;
        CanPause = job == GrblJobState.Running;
        CanResume = job is GrblJobState.Paused or GrblJobState.Error or GrblJobState.ProgramStop;
        JobMessage = job is GrblJobState.ProgramStop
            ? controller.JobMessage + ". Смените инструмент, выставьте Z0 (кнопки или щуп) и нажмите «Продолжить»."
            : controller.JobMessage;
        ToolPosition = known ? status.WorkPosition.XY : null;

        if (IsJobActive && controller.TotalCommands > 0)
        {
            Progress = 100.0 * controller.AcknowledgedCommands / controller.TotalCommands;
            var elapsed = _jobStarted is { } start ? DateTime.Now - start : TimeSpan.Zero;
            ProgressText = string.Create(CultureInfo.CurrentCulture,
                $"{controller.AcknowledgedCommands} из {controller.TotalCommands} строк · {Progress:0} % · {elapsed:hh\\:mm\\:ss}");
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

        ProgressText = string.Create(CultureInfo.CurrentCulture, $"{result.Message} Время: {elapsed:hh\\:mm\\:ss}");
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
