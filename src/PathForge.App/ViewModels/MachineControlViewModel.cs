using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Windows.Input;
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

    /// <summary>A network link whose board has been silent this long is treated as lost (see <see cref="GrblController.CheckResponse(TimeSpan)"/>).</summary>
    private static readonly TimeSpan NetworkAnswerTimeout = TimeSpan.FromSeconds(10);

    private static string GcodeFileFilter =>
        "G-code (*.nc;*.gcode;*.ngc;*.tap;*.txt)|*.nc;*.gcode;*.ngc;*.tap;*.txt|" + Loc.T("Все файлы", "All files") + " (*.*)|*.*";

    private static string NotConnectedText => Loc.T("Нет связи", "Not connected");

    private static string NoMapText => Loc.T("Карта высот не снята.", "No height map measured.");

    private static string CurrentProjectText => Loc.T("Текущий проект", "Current project");

    private readonly IDialogService _dialogs;
    private readonly UiPreferences _preferences;
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

    // The open connection, and automatic reconnection after it is lost (USB unplugged, board reboot, WiFi dropped).
    private ConnectionTarget? _target;
    private int _connectionGeneration;
    private DispatcherTimer? _reconnectTimer;
    private bool _reconnecting;
    private bool _reconnectOpening;
    private HashSet<string> _reconnectKnownPorts = new(StringComparer.OrdinalIgnoreCase);
    private int _reconnectAttempts;

    // Keyboard and joystick jogging.
    private readonly WinmmJoystick _joystick = new();
    private readonly ContinuousJog _continuousJog = new();
    private readonly HashSet<Key> _heldKeys = new();
    private readonly DispatcherTimer _jogTimer;
    private bool _jogInputAllowed;
    private bool _joystickArmed;
    private int _failedCommandsSeen;

    public MachineControlViewModel(IDialogService dialogs, UiPreferences preferences, Func<MachineProgram?> projectProgram, Func<double> safeZ,
        Func<LevelingMap?> getMap, Action<LevelingMap?> setMap, Func<Bounds2> programBounds)
    {
        _dialogs = dialogs;
        _preferences = preferences;
        ConnectionKind = preferences.MachineConnection;
        Host = preferences.MachineHost ?? "";
        NetworkPort = preferences.MachineNetworkPort;
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
            if (_target is { IsNetwork: true })
            {
                _controller?.CheckResponse(NetworkAnswerTimeout);
            }

            if (_jobStarted is not null)
            {
                Refresh();
            }
        };
        _jogTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ContinuousJog.TickMs) };
        _jogTimer.Tick += (_, _) => JogTick();
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

    /// <summary>USB (COM port), telnet or WebSocket (e.g. MKS DLC32 over WiFi).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerial), nameof(IsNetwork))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private MachineConnectionKind connectionKind;

    /// <summary>Connection kinds for the list, named in the interface language.</summary>
    [ObservableProperty]
    private IReadOnlyList<ConnectionKindOption> connectionKinds = ConnectionKindOption.All();

    /// <summary>IP address or host name of the board for the network connection.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string host = "192.168.4.1";

    [ObservableProperty]
    private int networkPort = TelnetTransport.DefaultPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    private bool isConnected;

    /// <summary>A connection is being opened (a network address may take a few seconds to answer).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    private bool isConnecting;

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

    public bool IsDisconnected => !IsConnected && !IsConnecting;

    public bool IsSerial => ConnectionKind == MachineConnectionKind.Serial;

    public bool IsNetwork => !IsSerial;

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

        ConnectionKinds = ConnectionKindOption.All();
        // The list was replaced: let it select the current kind again.
        OnPropertyChanged(nameof(ConnectionKind));
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

    private bool CanConnect() => IsNetwork ? !string.IsNullOrWhiteSpace(Host) : !string.IsNullOrEmpty(SelectedPort);

    /// <summary>The port field follows the connection kind unless the user typed a port of their own.</summary>
    partial void OnConnectionKindChanged(MachineConnectionKind oldValue, MachineConnectionKind newValue)
    {
        if (NetworkPort == DefaultNetworkPort(oldValue) || NetworkPort == 0)
        {
            NetworkPort = DefaultNetworkPort(newValue);
        }
    }

    private static int DefaultNetworkPort(MachineConnectionKind kind) => kind switch
    {
        MachineConnectionKind.WebSocket or MachineConnectionKind.WebSocketBridge => WebSocketTransport.DefaultPort,
        _ => TelnetTransport.DefaultPort,
    };

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (IsConnected || IsConnecting || !CanConnect())
        {
            return;
        }

        if (IsNetwork && NetworkPort is < 2 or > 65535)
        {
            _dialogs.ShowError(Loc.T(
                "Сетевой порт — число от 2 до 65535 (у MKS DLC32: telnet — 23, WebSocket — 81).",
                "The network port is a number from 2 to 65535 (MKS DLC32: telnet 23, WebSocket 81)."));
            return;
        }

        var target = IsNetwork
            ? new ConnectionTarget(ConnectionKind, Host.Trim(), NetworkPort)
            : new ConnectionTarget(ConnectionKind, SelectedPort!, BaudRate);
        var generation = _connectionGeneration;
        IsConnecting = true;
        StateText = Loc.T($"Подключение к {target.Describe()}…", $"Connecting to {target.Describe()}…");
        IGrblTransport transport;
        try
        {
            // Off the UI thread: a network address that does not answer takes seconds to time out.
            transport = await Task.Run(target.Open);
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            IsConnecting = false;
            Refresh();
            _dialogs.ShowError(target.IsNetwork
                ? Loc.T(
                    $"Не удалось подключиться к {target.Describe()}:\n{ex.Message}\n\n" +
                    "Проверьте IP-адрес платы (показан на её экране или в роутере) и что компьютер в той же сети. " +
                    "Telnet должен быть включён ($ESP130=ON), WebSocket работает при включённом веб-интерфейсе ($ESP120=ON). " +
                    "По telnet плата принимает только одного клиента — закройте другие программы (LightBurn, LaserGRBL).",
                    $"Could not connect to {target.Describe()}:\n{ex.Message}\n\n" +
                    "Check the board IP address (shown on its screen or in the router) and that the computer is on the same network. " +
                    "Telnet must be on ($ESP130=ON), WebSocket works while the web interface is on ($ESP120=ON). " +
                    "Over telnet the board accepts a single client — close other programs (LightBurn, LaserGRBL).")
                : Loc.T(
                    $"Не удалось открыть {target.Address}:\n{ex.Message}\n\nПорт может быть занят другой программой (Candle, Arduino IDE).",
                    $"Could not open {target.Address}:\n{ex.Message}\n\nThe port may be used by another program (Candle, Arduino IDE)."));
            return;
        }

        IsConnecting = false;
        if (generation != _connectionGeneration)
        {
            // The panel was closed while connecting.
            transport.Dispose();
            return;
        }

        _target = target;
        _controller = CreateController(transport);
        IsConnected = true;
        AddLog(new GrblLogEntry(GrblLogKind.Info, Loc.T(
            $"Подключено: {target.Describe()}. Ожидание ответа GRBL…",
            $"Connected: {target.Describe()}. Waiting for GRBL…")));
        _pollTimer.Start();
        SaveConnectionPreferences();
        Refresh();
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

    private static bool IsConnectionError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SocketException or TimeoutException;

    /// <summary>Creates a controller over an opened connection and wires its events to the panel.</summary>
    private GrblController CreateController(IGrblTransport transport)
    {
        var controller = new GrblController(transport);
        controller.Changed += QueueRefresh;
        controller.Log += entry => _dispatcher.BeginInvoke(() => AddLog(entry));
        controller.JobFinished += result => _dispatcher.BeginInvoke(() => OnJobFinished(result));
        controller.ProbeTouched += p => _dispatcher.BeginInvoke(() => OnProbeTouched(p));
        return controller;
    }

    private void SaveConnectionPreferences()
    {
        _preferences.MachineConnection = ConnectionKind;
        _preferences.MachineHost = Host.Trim();
        _preferences.MachineNetworkPort = NetworkPort;
        _preferences.Save();
    }

    /// <summary>
    /// The connection was lost (USB unplugged, board reboot, WiFi dropped): release the dead connection and,
    /// like Candle, keep trying to open it again until it comes back or the user disconnects.
    /// </summary>
    private void BeginReconnect()
    {
        if (_reconnectTimer is not null || _target is not { } target)
        {
            return;
        }

        // The controller has already marked the connection as lost: release it.
        _controller?.Dispose();
        _controller = null;

        if (!_reconnecting)
        {
            _reconnecting = true;
            _reconnectAttempts = 0;
            _reconnectKnownPorts = target.IsNetwork
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(SerialPortTransport.PortNames(), StringComparer.OrdinalIgnoreCase);
            AddLog(new GrblLogEntry(GrblLogKind.Error, Loc.T(
                $"Связь потеряна. Восстанавливаю подключение к {target.Describe()}…",
                $"Connection lost. Reconnecting to {target.Describe()}…")));
        }

        // Otherwise a restored connection broke again before GRBL answered (e.g. the board still holds
        // the old telnet client and dropped the new one): keep counting attempts without new log lines.
        _reconnectTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(target.IsNetwork ? 2 : 1),
        };
        _reconnectTimer.Tick += (_, _) => TryReconnect();
        _reconnectTimer.Start();
    }

    private async void TryReconnect()
    {
        if (_reconnectTimer is null || _reconnectOpening || _target is not { } target)
        {
            return;
        }

        _reconnectAttempts++;
        if (!target.IsNetwork)
        {
            if (FindReturnedPort(target.Address) is not { } name)
            {
                StateText = Loc.T(
                    $"Связь потеряна — порт {target.Address} не найден, попытка {_reconnectAttempts}…",
                    $"Connection lost — port {target.Address} not found, attempt {_reconnectAttempts}…");
                return;
            }

            target = target with { Address = name };
        }

        var generation = _connectionGeneration;
        IGrblTransport transport;
        _reconnectOpening = true;
        try
        {
            transport = await Task.Run(target.Open);
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // Not back yet (port still held by the OS, board rebooting, network down): keep trying.
            StateText = Loc.T(
                $"Связь потеряна — {target.Describe()}, попытка {_reconnectAttempts}: {ex.Message}",
                $"Connection lost — {target.Describe()}, attempt {_reconnectAttempts}: {ex.Message}");
            return;
        }
        finally
        {
            _reconnectOpening = false;
        }

        if (generation != _connectionGeneration || _reconnectTimer is null)
        {
            // The user disconnected while the attempt was running.
            transport.Dispose();
            return;
        }

        _reconnectTimer.Stop();
        _reconnectTimer = null;
        _target = target;
        _controller = CreateController(transport);
        if (!target.IsNetwork && !string.Equals(target.Address, SelectedPort, StringComparison.OrdinalIgnoreCase))
        {
            RefreshPorts();
            SelectedPort = target.Address;
        }

        // "Restored" is logged by Refresh once GRBL answers.
        Refresh();
    }

    /// <summary>The lost COM port if it is back; after a hard USB reset it may return under another number, so a single new port is accepted too.</summary>
    private string? FindReturnedPort(string portName)
    {
        var ports = SerialPortTransport.PortNames();
        if (ports.Contains(portName, StringComparer.OrdinalIgnoreCase))
        {
            return portName;
        }

        var fresh = ports.Where(p => !_reconnectKnownPorts.Contains(p)).ToList();
        return fresh.Count == 1 ? fresh[0] : null;
    }

    private void CloseConnection()
    {
        _connectionGeneration++;
        _reconnectTimer?.Stop();
        _reconnectTimer = null;
        _reconnecting = false;
        _pollTimer.Stop();
        _controller?.Dispose();
        _controller = null;
        _target = null;
        _jobStarted = null;
        IsConnected = false;
        Refresh();
    }

    /// <summary>What the panel connects to: a COM port with its baud rate, or a network address with its port.</summary>
    private sealed record ConnectionTarget(MachineConnectionKind Kind, string Address, int Number)
    {
        public bool IsNetwork => Kind != MachineConnectionKind.Serial;

        public IGrblTransport Open() => Kind switch
        {
            MachineConnectionKind.Telnet => new TelnetTransport(Address, Number),
            MachineConnectionKind.WebSocket => new WebSocketTransport(Address, Number, WebSocketCommandRoute.Http),
            MachineConnectionKind.WebSocketBridge => new WebSocketTransport(Address, Number, WebSocketCommandRoute.WebSocket),
            _ => new SerialPortTransport(Address, Number),
        };

        public string Describe() => Kind switch
        {
            MachineConnectionKind.Telnet => $"{Address}:{Number} (telnet)",
            MachineConnectionKind.WebSocket or MachineConnectionKind.WebSocketBridge => $"ws://{Address}:{Number}",
            _ => Loc.T($"{Address}, {Number} бод", $"{Address}, {Number} baud"),
        };
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

    // ---- Keyboard and joystick ----------------------------------------------------------------

    /// <summary>
    /// The keyboard and a joystick move the machine. Off by default and switched on only by the button, so that
    /// a key pressed by mistake never moves the machine; switched off again on disconnect and when a program starts.
    /// </summary>
    [ObservableProperty]
    private bool isJogInputEnabled;

    /// <summary>Keys move the machine while held down; otherwise each press moves one step.</summary>
    [ObservableProperty]
    private bool jogHoldToMove = true;

    /// <summary>Joystick state for the panel.</summary>
    [ObservableProperty]
    private string joystickText = "";

    /// <summary>
    /// Keys and the joystick work only while the main window is active and the panel is shown (set by the window):
    /// typing in another program or looking at another tab must not move the machine.
    /// </summary>
    public void SetJogInputContext(bool allowed)
    {
        _jogInputAllowed = allowed;
        if (!allowed)
        {
            // Key-up events go to the other window: forget the keys, the next tick stops the motion.
            _heldKeys.Clear();
        }
    }

    /// <summary>A key pressed or released in the main window. Returns true when it was used for jogging.</summary>
    public bool HandleJogKey(Key key, bool isDown, bool isRepeat)
    {
        if (!IsJogInputEnabled || !_jogInputAllowed)
        {
            return false;
        }

        switch (key)
        {
            case Key.Escape or Key.Space:
                if (isDown)
                {
                    StopJogMotion();
                }

                return true;
            case Key.Add or Key.OemPlus or Key.Subtract or Key.OemMinus:
                if (isDown && !isRepeat)
                {
                    ChangeJogStep(key is Key.Add or Key.OemPlus ? 1 : -1);
                }

                return true;
        }

        if (KeyDirection(key) is not { } direction)
        {
            return false;
        }

        if (JogHoldToMove)
        {
            if (isDown)
            {
                _heldKeys.Add(key);
            }
            else
            {
                _heldKeys.Remove(key);
            }

            // React at once instead of waiting for the next tick.
            JogTick();
        }
        else if (isDown && !isRepeat && CanControl && _controller is not null)
        {
            Run(c => c.Jog(direction.X * JogStep, direction.Y * JogStep, direction.Z * JogStep, direction.Z != 0 ? JogFeedZ : JogFeed));
        }

        return true;
    }

    partial void OnIsJogInputEnabledChanged(bool value)
    {
        if (!value)
        {
            StopJogInput();
            AddLog(new GrblLogEntry(GrblLogKind.Info, Loc.T("Управление с клавиатуры и джойстика выключено.", "Keyboard and joystick control is off.")));
            return;
        }

        if (_controller is not { } controller || controller.Job is not (GrblJobState.None or GrblJobState.ProgramStop))
        {
            IsJogInputEnabled = false;
            return;
        }

        _heldKeys.Clear();
        _continuousJog.Reset();
        _joystickArmed = false;
        _failedCommandsSeen = controller.FailedCommands;
        _jogTimer.Start();
        AddLog(new GrblLogEntry(GrblLogKind.Info, Loc.T(
            "Управление с клавиатуры и джойстика включено: стрелки — X/Y, PgUp/PgDn — Z, Esc или пробел — стоп.",
            "Keyboard and joystick control is on: arrows — X/Y, PgUp/PgDn — Z, Esc or Space — stop.")));
    }

    private void StopJogInput()
    {
        _jogTimer.Stop();
        _heldKeys.Clear();
        if (_continuousJog.IsMoving)
        {
            _controller?.JogCancel();
        }

        _continuousJog.Reset();
        JoystickText = "";
    }

    private void StopJogMotion()
    {
        _heldKeys.Clear();
        _controller?.JogCancel();
        _continuousJog.Reset();
        // The joystick must come back to the centre before it moves the machine again.
        _joystickArmed = false;
    }

    private void ChangeJogStep(int direction)
    {
        var index = JogSteps.ToList().FindIndex(s => s >= JogStep);
        index = Math.Clamp((index < 0 ? JogSteps.Count - 1 : index) + direction, 0, JogSteps.Count - 1);
        JogStep = JogSteps[index];
    }

    /// <summary>Arrows and PgUp/PgDn; with Num Lock on the numeric keypad gives the same layout (4/6, 8/2, 9/3) as in Candle.</summary>
    private static JogVector? KeyDirection(Key key) => key switch
    {
        Key.Left or Key.NumPad4 => new JogVector(-1, 0, 0),
        Key.Right or Key.NumPad6 => new JogVector(1, 0, 0),
        Key.Up or Key.NumPad8 => new JogVector(0, 1, 0),
        Key.Down or Key.NumPad2 => new JogVector(0, -1, 0),
        Key.PageUp or Key.NumPad9 => new JogVector(0, 0, 1),
        Key.PageDown or Key.NumPad3 => new JogVector(0, 0, -1),
        _ => null,
    };

    private void JogTick()
    {
        if (_controller is not { } controller || !IsJogInputEnabled)
        {
            return;
        }

        var input = default(JogVector);
        if (_jogInputAllowed)
        {
            // A key released while the focus was elsewhere never sends its key-up event.
            _heldKeys.RemoveWhere(k => !Keyboard.IsKeyDown(k));
            if (JogHoldToMove)
            {
                foreach (var key in _heldKeys)
                {
                    input += KeyDirection(key) ?? default;
                }
            }

            var stick = _joystick.Read();
            if (_joystickArmed)
            {
                input += stick;
            }
            else if (stick.IsZero)
            {
                _joystickArmed = true;
            }
        }

        JoystickText = _joystick.Name is not { } name
            ? Loc.T("Джойстик не найден.", "No joystick found.")
            : _joystickArmed
                ? Loc.T($"Джойстик: {name}.", $"Joystick: {name}.")
                : Loc.T($"Джойстик: {name} — отпустите ручку в центр, чтобы начать.", $"Joystick: {name} — let the stick return to the centre to start.");

        var status = controller.Status;
        if (!controller.CanSendCommands || status.State is not (GrblState.Idle or GrblState.Jog))
        {
            // Alarm, hold, homing: jogs would only be rejected.
            input = default;
        }

        if (controller.FailedCommands != _failedCommandsSeen)
        {
            // A jog was rejected (soft limits, alarm): stop until the input is released.
            _failedCommandsSeen = controller.FailedCommands;
            if (_continuousJog.IsMoving)
            {
                controller.JogCancel();
                _continuousJog.Halt();
            }
        }

        var action = _continuousJog.Update(input, JogFeed, JogFeedZ, Environment.TickCount64, controller.IsBusy, status.State == GrblState.Jog);
        try
        {
            switch (action.Kind)
            {
                case JogActionKind.Cancel:
                    controller.JogCancel();
                    break;
                case JogActionKind.Move:
                    var segment = action.Segment;
                    controller.Jog(segment.Dx, segment.Dy, segment.Dz, segment.Feed, quiet: true);
                    break;
            }
        }
        catch (InvalidOperationException)
        {
            // The controller stopped accepting commands (connection lost, program started).
            _continuousJog.Halt();
        }
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

    public void Dispose()
    {
        _jogTimer.Stop();
        CloseConnection();
    }

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
            // The connection was lost (cable, board reboot, network): try to restore it automatically.
            BeginReconnect();
            controller = null;
        }

        if (controller is not null && _reconnecting && controller.HasAnswered && _target is { } restored)
        {
            _reconnecting = false;
            AddLog(new GrblLogEntry(GrblLogKind.Info, Loc.T(
                $"Связь восстановлена: {restored.Describe()} (попыток: {_reconnectAttempts}).",
                $"Connection restored: {restored.Describe()} (attempts: {_reconnectAttempts}).")));
        }

        if (IsJogInputEnabled && controller?.Job is not (GrblJobState.None or GrblJobState.ProgramStop))
        {
            // Disconnected or a program started: the keys must not move the machine any more.
            IsJogInputEnabled = false;
        }

        if (controller is null)
        {
            if (!IsConnecting)
            {
                StateText = _reconnectTimer is not null && _target is { } lost
                    ? Loc.T($"Связь потеряна — переподключение к {lost.Describe()}…", $"Connection lost — reconnecting to {lost.Describe()}…")
                    : NotConnectedText;
            }

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

/// <summary>A connection kind with its name for the list.</summary>
public sealed record ConnectionKindOption(MachineConnectionKind Kind, string Name)
{
    public static IReadOnlyList<ConnectionKindOption> All() => new ConnectionKindOption[]
    {
        new(MachineConnectionKind.Serial, Loc.T("USB (COM-порт)", "USB (COM port)")),
        new(MachineConnectionKind.Telnet, Loc.T("Сеть: telnet", "Network: telnet")),
        new(MachineConnectionKind.WebSocket, Loc.T("Сеть: WebSocket (MKS DLC32, Grbl_Esp32)", "Network: WebSocket (MKS DLC32, Grbl_Esp32)")),
        new(MachineConnectionKind.WebSocketBridge, Loc.T("Сеть: WebSocket-мост (ESP8266, FluidNC)", "Network: WebSocket bridge (ESP8266, FluidNC)")),
    };
}
