using System.Globalization;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Grbl;

/// <summary>State of the program being streamed.</summary>
public enum GrblJobState
{
    /// <summary>No program: the machine can be jogged and commands can be sent.</summary>
    None,

    Running,

    /// <summary>Paused by the operator (feed hold).</summary>
    Paused,

    /// <summary>
    /// The program reached a stop (M0, e.g. a tool change). All motion is finished; the operator may jog,
    /// zero and probe, then resume.
    /// </summary>
    ProgramStop,

    /// <summary>GRBL rejected a line; the machine was put on hold. Resume skips the line, Stop aborts.</summary>
    Error,

    /// <summary>Stop requested: waiting for the machine to decelerate before the reset.</summary>
    Stopping,
}

public enum GrblLogKind
{
    Sent,
    Received,
    Info,
    Error,
}

public sealed record GrblLogEntry(GrblLogKind Kind, string Text);

public sealed record GrblJobResult(bool Success, string Message);

/// <summary>
/// Talks to a GRBL 1.1 controller: status polling, jogging and commands, and streaming of programs with
/// the character-counting method (as many lines as fit into GRBL's serial buffer are in flight, each "ok"
/// or "error" frees the space of the oldest line). All methods are thread-safe; events may be raised on
/// any thread and handlers must not block.
/// </summary>
public sealed class GrblController : IDisposable
{
    /// <summary>Serial receive buffer of GRBL on an ATmega328 (Arduino Nano/Uno, CNC 3018 boards).</summary>
    public const int DefaultRxBufferSize = 128;

    private const byte StatusQuery = (byte)'?';
    private const byte FeedHold = (byte)'!';
    private const byte CycleStart = (byte)'~';
    private const byte Reset = 0x18;
    private const byte JogCancelCommand = 0x85;

    private readonly object _lock = new();
    private readonly IGrblTransport _transport;
    private readonly int _rxBufferSize;
    private readonly Queue<Pending> _pending = new();
    private readonly Queue<string> _manual = new();

    private int _bufferUsed;
    private List<GrblLine> _program = new();
    private int _next;
    private bool _drainAndWait;
    private bool _atEnd;
    private int _idleReports;
    private bool _manualDuringStop;
    private string _lastFeedWord = "";
    private bool _stopRequested;
    private long _lastReceived;

    public GrblController(IGrblTransport transport, int rxBufferSize = DefaultRxBufferSize)
    {
        _transport = transport;
        _rxBufferSize = rxBufferSize;
        _transport.LineReceived += OnLine;
        _transport.Failed += OnFailed;
        IsConnected = true;
        _transport.Start();
    }

    /// <summary>Status, job state or progress changed.</summary>
    public event Action? Changed;

    public event Action<GrblLogEntry>? Log;

    /// <summary>The probe touched; position in machine coordinates.</summary>
    public event Action<Vec3>? ProbeTouched;

    /// <summary>The program finished, was stopped or aborted by an alarm.</summary>
    public event Action<GrblJobResult>? JobFinished;

    public bool IsConnected { get; private set; }

    /// <summary>The controller has sent at least one line: the link is known to work.</summary>
    public bool HasAnswered
    {
        get
        {
            lock (_lock)
            {
                return _lastReceived != 0;
            }
        }
    }

    public GrblStatus Status { get; private set; } = GrblStatus.Unknown;

    public GrblJobState Job { get; private set; }

    /// <summary>Explanation for <see cref="GrblJobState.ProgramStop"/> and <see cref="GrblJobState.Error"/>.</summary>
    public string JobMessage { get; private set; } = "";

    /// <summary>Startup message of the controller, e.g. "Grbl 1.1f ['$' for help]".</summary>
    public string Version { get; private set; } = "";

    public int TotalCommands { get; private set; }

    public int SentCommands { get; private set; }

    public int AcknowledgedCommands { get; private set; }

    /// <summary>Bytes of GRBL's serial buffer occupied by lines that were not answered yet.</summary>
    public int BufferUsed
    {
        get
        {
            lock (_lock)
            {
                return _bufferUsed;
            }
        }
    }

    /// <summary>Work position of the last successful probe touch.</summary>
    public Vec3? LastProbe { get; private set; }

    /// <summary>Commands are queued or waiting for their answer.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_lock)
            {
                return _manual.Count > 0 || _pending.Count > 0;
            }
        }
    }

    /// <summary>The operator may jog, zero and send commands.</summary>
    public bool CanSendCommands => IsConnected && Job is GrblJobState.None or GrblJobState.ProgramStop;

    public void RequestStatus()
    {
        lock (_lock)
        {
            if (IsConnected)
            {
                Realtime(StatusQuery);
            }
        }
    }

    /// <summary>
    /// Queues a command line typed by the operator (or produced by the buttons). Lines are sent one after
    /// the other, each when the previous one was answered. A few real-time characters (?, !, ~) are sent at once.
    /// </summary>
    public void SendCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            EnsureConnected();
            if (command is "?" or "!" or "~")
            {
                Realtime((byte)command[0]);
                Write(GrblLogKind.Sent, command);
                return;
            }

            if (!CanSendCommands)
            {
                throw new InvalidOperationException(Loc.T("Идёт выполнение программы — команды можно отправлять после её завершения или на остановке.", "A program is running — commands can be sent after it finishes or while it is stopped."));
            }

            if (command.Length > GrblProgram.MaxLineLength)
            {
                throw new InvalidOperationException(Loc.T($"Команда длиннее {GrblProgram.MaxLineLength} символов.", $"The command is longer than {GrblProgram.MaxLineLength} characters."));
            }

            _manual.Enqueue(command);
            if (Job == GrblJobState.ProgramStop)
            {
                _manualDuringStop = true;
            }

            Pump();
        }
    }

    /// <summary>Relative jog in work millimetres; GRBL cancels it with <see cref="JogCancel"/>.</summary>
    public void Jog(double dx, double dy, double dz, double feed)
    {
        var axes = "";
        if (dx != 0)
        {
            axes += "X" + Format(dx);
        }

        if (dy != 0)
        {
            axes += "Y" + Format(dy);
        }

        if (dz != 0)
        {
            axes += "Z" + Format(dz);
        }

        if (axes.Length > 0)
        {
            SendCommand($"$J=G91G21{axes}F{Format(Math.Max(1, feed))}");
        }
    }

    public void JogCancel()
    {
        lock (_lock)
        {
            if (!IsConnected)
            {
                return;
            }

            // Drop jogs that were not sent yet, then stop the running one.
            var keep = _manual.Where(c => !c.StartsWith("$J=", StringComparison.Ordinal)).ToList();
            _manual.Clear();
            keep.ForEach(_manual.Enqueue);
            Realtime(JogCancelCommand);
        }
    }

    /// <summary>Sets the current position as work zero for the given axes (G10 L20 P1: stored in G54).</summary>
    public void SetWorkZero(bool x, bool y, bool z)
    {
        var axes = (x ? "X0" : "") + (y ? "Y0" : "") + (z ? "Z0" : "");
        if (axes.Length > 0)
        {
            SendCommand("G10L20P1" + axes);
        }
    }

    /// <summary>Lifts to <paramref name="safeZ"/> above the work zero, then moves over X0 Y0.</summary>
    public void GoToWorkZero(double safeZ)
    {
        SendCommand($"G90G0Z{Format(safeZ)}");
        SendCommand("G0X0Y0");
    }

    /// <summary>
    /// Touches a probe plate lying on the stock and sets Z0 at the stock surface:
    /// probes down at most <paramref name="maxTravel"/>, sets Z to the plate thickness, lifts by <paramref name="retract"/>.
    /// A missed plate stops the machine with an alarm and the rest is not executed.
    /// </summary>
    public void ProbeZ(double plateThickness, double maxTravel, double feed, double retract)
    {
        SendCommand($"G91G38.2Z-{Format(Math.Abs(maxTravel))}F{Format(Math.Max(1, feed))}");
        SendCommand("G90");
        SendCommand($"G10L20P1Z{Format(plateThickness)}");
        SendCommand($"G0Z{Format(plateThickness + Math.Abs(retract))}");
    }

    public void Unlock() => SendCommand("$X");

    public void Home() => SendCommand("$H");

    /// <summary>Starts streaming a prepared program. The machine must be idle.</summary>
    public void StartJob(IReadOnlyList<GrblLine> program)
    {
        lock (_lock)
        {
            EnsureConnected();
            if (Job != GrblJobState.None)
            {
                throw new InvalidOperationException(Loc.T("Программа уже выполняется.", "A program is already running."));
            }

            if (Status.State != GrblState.Idle)
            {
                throw new InvalidOperationException(Loc.T($"Станок должен быть в состоянии «Готов», сейчас: {GrblMessages.StateName(Status.State)}.", $"The machine must be Idle, now: {GrblMessages.StateName(Status.State)}."));
            }

            if (_pending.Count > 0 || _manual.Count > 0)
            {
                throw new InvalidOperationException(Loc.T("Подождите, пока выполнятся отправленные команды.", "Wait until the sent commands are done."));
            }

            _program = program.ToList();
            _next = 0;
            _drainAndWait = false;
            _atEnd = false;
            _lastFeedWord = "";
            TotalCommands = _program.Count(l => l.Text.Length > 0);
            SentCommands = 0;
            AcknowledgedCommands = 0;
            JobMessage = "";
            Job = GrblJobState.Running;
            Write(GrblLogKind.Info, Loc.T($"Старт программы: {TotalCommands} строк.", $"Program start: {TotalCommands} lines."));
            Pump();
            Changed?.Invoke();
        }
    }

    /// <summary>Feed hold: the machine decelerates and stops on the path.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (Job == GrblJobState.Running)
            {
                Realtime(FeedHold);
                Job = GrblJobState.Paused;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Continues after a pause, an error hold or a program stop.</summary>
    public void Resume()
    {
        lock (_lock)
        {
            switch (Job)
            {
                case GrblJobState.Paused:
                case GrblJobState.Error:
                    Realtime(CycleStart);
                    Job = GrblJobState.Running;
                    JobMessage = "";
                    Pump();
                    break;
                case GrblJobState.ProgramStop:
                    if (_manual.Count > 0 || _pending.Count > 0)
                    {
                        throw new InvalidOperationException(Loc.T("Подождите, пока выполнятся отправленные команды.", "Wait until the sent commands are done."));
                    }

                    if (_manualDuringStop)
                    {
                        // Jogging does not change the modal state, but probing and typed commands may: restore what the program relies on.
                        Send("G90G21G94" + _lastFeedWord, -1);
                    }

                    _manualDuringStop = false;
                    _drainAndWait = false;
                    JobMessage = "";
                    Job = GrblJobState.Running;
                    Write(GrblLogKind.Info, Loc.T("Продолжение программы.", "Program resumed."));
                    Pump();
                    break;
                default:
                    return;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>Stops the program: hold first (keeps the position), then reset (clears GRBL's buffers, stops the spindle).</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!IsConnected)
            {
                return;
            }

            _manual.Clear();
            if (Job == GrblJobState.None)
            {
                if (Status.State == GrblState.Jog)
                {
                    Realtime(JogCancelCommand);
                }

                return;
            }

            _next = _program.Count;
            Job = GrblJobState.Stopping;
            var moving = Status.State is GrblState.Run or GrblState.Jog or GrblState.Home ||
                         (Status.State == GrblState.Hold && Status.SubState != 0);
            if (moving)
            {
                Realtime(FeedHold);
                _stopRequested = true;
                Changed?.Invoke();
            }
            else
            {
                ResetController(Loc.T("Программа остановлена.", "Program stopped."));
            }
        }
    }

    /// <summary>Immediate reset (Ctrl-X). During motion GRBL reports an alarm because steps may be lost.</summary>
    public void SoftReset()
    {
        lock (_lock)
        {
            if (IsConnected)
            {
                ResetController(Loc.T("Сброс контроллера.", "Controller reset."));
            }
        }
    }

    /// <summary>
    /// Declares the connection lost when the controller, which has answered before, has been silent for
    /// <paramref name="timeout"/> although status reports are polled several times a second. Needed for
    /// network links: TCP does not notice a dropped WiFi connection for minutes. Not checked during homing,
    /// when GRBL does not answer status queries until the cycle is done.
    /// </summary>
    public void CheckResponse(TimeSpan timeout) => CheckResponse(timeout, Environment.TickCount64);

    internal void CheckResponse(TimeSpan timeout, long now)
    {
        lock (_lock)
        {
            if (!IsConnected || _lastReceived == 0 || now - _lastReceived <= timeout.TotalMilliseconds ||
                _pending.Any(p => p.Text.StartsWith("$H", StringComparison.Ordinal)))
            {
                return;
            }

            OnFailed(new TimeoutException(Loc.T(
                $"нет ответа {timeout.TotalSeconds:0} с.",
                $"no answer for {timeout.TotalSeconds:0} s.")));
        }
    }

    /// <summary>Feed override: +1 adds 10 %, -1 subtracts 10 %, 0 back to 100 %.</summary>
    public void FeedOverride(int direction) => Override(direction switch { > 0 => 0x91, < 0 => 0x92, _ => 0x90 });

    /// <summary>Spindle (or laser power) override: +1 adds 10 %, -1 subtracts 10 %, 0 back to 100 %.</summary>
    public void SpindleOverride(int direction) => Override(direction switch { > 0 => 0x9A, < 0 => 0x9B, _ => 0x99 });

    public void Dispose()
    {
        lock (_lock)
        {
            _transport.LineReceived -= OnLine;
            _transport.Failed -= OnFailed;
            IsConnected = false;
            _transport.Dispose();
        }
    }

    // ---- Receiving ----------------------------------------------------------------------------

    private void OnLine(string raw) => OnLine(raw, Environment.TickCount64);

    internal void OnLine(string raw, long now)
    {
        lock (_lock)
        {
            // Any data, even an empty line, proves that the link is alive.
            _lastReceived = Math.Max(now, 1);
            var line = raw.Trim();
            if (line.Length == 0)
            {
                return;
            }

            if (line[0] == '<')
            {
                if (GrblStatus.TryParse(line, Status, out var status))
                {
                    OnStatus(status);
                }

                return;
            }

            if (line == "ok")
            {
                Acknowledge(null);
            }
            else if (GrblMessages.TryCode(line, "error:", out var error))
            {
                Acknowledge(error);
            }
            else if (GrblMessages.TryCode(line, "ALARM:", out var alarm))
            {
                OnAlarm(alarm);
            }
            else if (line.StartsWith("Grbl ", StringComparison.Ordinal))
            {
                OnStartup(line);
            }
            else if (line.StartsWith("[PRB:", StringComparison.Ordinal))
            {
                OnProbe(line);
            }
            else
            {
                Write(GrblLogKind.Received, line);
            }

            Changed?.Invoke();
        }
    }

    private void OnStatus(GrblStatus status)
    {
        Status = status;
        if (_stopRequested && (status.IsHoldComplete || status.State == GrblState.Idle))
        {
            ResetController(Loc.T("Программа остановлена.", "Program stopped."));
        }
        else if (Job == GrblJobState.Running && _drainAndWait && _pending.Count == 0)
        {
            // Two idle reports in a row: the first may have been taken before the last block started moving.
            _idleReports = status.State == GrblState.Idle ? _idleReports + 1 : 0;
            if (_idleReports >= 2)
            {
                if (_atEnd)
                {
                    FinishJob(true, Loc.T($"Программа выполнена: {AcknowledgedCommands} строк.", $"Program finished: {AcknowledgedCommands} lines."));
                }
                else
                {
                    Job = GrblJobState.ProgramStop;
                    _manualDuringStop = false;
                    Write(GrblLogKind.Info, Loc.T("Остановка программы: ", "Program stop: ") + (JobMessage.Length > 0 ? JobMessage : "M0"));
                }
            }
        }

        Changed?.Invoke();
    }

    private void Acknowledge(int? error)
    {
        if (_pending.Count == 0)
        {
            Write(error is null ? GrblLogKind.Received : GrblLogKind.Error, error is null ? "ok" : GrblMessages.Error(error.Value));
            return;
        }

        var line = _pending.Dequeue();
        _bufferUsed -= line.Length;
        if (line.SourceLine > 0)
        {
            AcknowledgedCommands++;
            if (error is { } code)
            {
                Write(GrblLogKind.Error, Loc.T($"Строка {line.SourceLine} «{line.Text}»: {GrblMessages.Error(code)}", $"Line {line.SourceLine} “{line.Text}”: {GrblMessages.Error(code)}"));
                if (Job is GrblJobState.Running or GrblJobState.Paused)
                {
                    // Lines already in GRBL's buffer keep running: hold the machine and let the operator decide.
                    Realtime(FeedHold);
                    Job = GrblJobState.Error;
                    JobMessage = Loc.T(
                        $"Строка {line.SourceLine} «{line.Text}» не выполнена. {GrblMessages.Error(code)}. " +
                        "«Продолжить» — пропустить строку, «Стоп» — прервать программу.",
                        $"Line {line.SourceLine} “{line.Text}” failed. {GrblMessages.Error(code)}. " +
                        "“Resume” skips the line, “Stop” aborts the program.");
                }
            }
        }
        else if (error is { } code)
        {
            Write(GrblLogKind.Error, $"{line.Text}: {GrblMessages.Error(code)}");
            // A failed step makes the following commands of a sequence (probing, go to zero) meaningless.
            _manual.Clear();
        }
        else if (line.SourceLine == 0)
        {
            Write(GrblLogKind.Received, $"{line.Text}: ok");
        }

        Pump();
    }

    private void OnAlarm(int code)
    {
        var message = GrblMessages.Alarm(code);
        Write(GrblLogKind.Error, message);
        // GRBL resets its buffers on an alarm: nothing in flight will be answered.
        _pending.Clear();
        _bufferUsed = 0;
        _manual.Clear();
        _stopRequested = false;
        if (Job != GrblJobState.None)
        {
            FinishJob(false, Loc.T("Программа прервана. ", "Program aborted. ") + message);
        }
    }

    private void OnStartup(string banner)
    {
        Version = banner;
        Write(GrblLogKind.Info, banner);
        _pending.Clear();
        _bufferUsed = 0;
        _manual.Clear();
        _stopRequested = false;
        if (Job != GrblJobState.None)
        {
            FinishJob(false, Loc.T("Контроллер перезапустился — программа прервана.", "The controller restarted — program aborted."));
        }
    }

    private void OnProbe(string line)
    {
        // [PRB:x,y,z:1] in machine coordinates; the last field is 1 when the probe touched.
        var body = line.Trim('[', ']')[4..];
        var parts = body.Split(':');
        var values = parts[0].Split(',');
        if (parts.Length >= 2 && parts[1] == "1" && values.Length >= 3 &&
            double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
            double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
            double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            var wco = Status.WorkOffset;
            LastProbe = new Vec3(x - wco.X, y - wco.Y, z - wco.Z);
            ProbeTouched?.Invoke(new Vec3(x, y, z));
            Write(GrblLogKind.Info, Loc.T(
                FormattableString.Invariant($"Касание щупа: Z = {z:0.000} (машинные координаты)."),
                FormattableString.Invariant($"Probe touch: Z = {z:0.000} (machine coordinates).")));
        }
        else
        {
            Write(GrblLogKind.Error, Loc.T("Щуп не сработал: ", "Probe failed: ") + line);
        }
    }

    private void OnFailed(Exception error)
    {
        lock (_lock)
        {
            if (!IsConnected)
            {
                return;
            }

            IsConnected = false;
            Write(GrblLogKind.Error, Loc.T("Связь со станком потеряна: ", "Connection to the machine lost: ") + error.Message);
            _pending.Clear();
            _bufferUsed = 0;
            _manual.Clear();
            if (Job != GrblJobState.None)
            {
                FinishJob(false, Loc.T("Связь со станком потеряна — программа прервана. Станок может продолжать движение до конца буфера!", "Connection to the machine lost — program aborted. The machine may keep moving until its buffer is empty!"));
            }

            Changed?.Invoke();
        }
    }

    // ---- Sending ------------------------------------------------------------------------------

    /// <summary>Sends as many program lines as fit into GRBL's buffer, then queued operator commands.</summary>
    private void Pump()
    {
        if (!IsConnected)
        {
            return;
        }

        if (Job == GrblJobState.Running && !_drainAndWait)
        {
            while (_next < _program.Count)
            {
                var line = _program[_next];
                if (line.Text.Length > 0)
                {
                    if (!Fits(line.Text.Length + 1))
                    {
                        break;
                    }

                    Send(line.Text, line.SourceLine);
                    SentCommands++;
                    RememberFeed(line.Text);
                }

                _next++;
                if (line.StopAfter)
                {
                    JobMessage = line.Comment.Length > 0 ? line.Comment : Loc.T("Остановка программы (M0)", "Program stop (M0)");
                    _drainAndWait = true;
                    _idleReports = 0;
                    break;
                }
            }

            if (_next >= _program.Count && !_drainAndWait)
            {
                _drainAndWait = true;
                _atEnd = true;
                _idleReports = 0;
            }
        }

        // Operator commands go one at a time so that a failed step can cancel the rest.
        if (CanSendCommands && _manual.Count > 0 && _pending.All(p => p.SourceLine > 0))
        {
            var command = _manual.Peek();
            if (Fits(command.Length + 1))
            {
                _manual.Dequeue();
                Send(command, 0);
                Write(GrblLogKind.Sent, command);
            }
        }
    }

    private bool Fits(int length) => _bufferUsed + length <= _rxBufferSize - 1;

    /// <param name="sourceLine">Program line number; 0 for operator commands, -1 for internal ones.</param>
    private void Send(string text, int sourceLine)
    {
        try
        {
            _transport.Write(text + "\n");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
        {
            OnFailed(ex);
            return;
        }

        var length = text.Length + 1;
        _pending.Enqueue(new Pending(text, length, sourceLine));
        _bufferUsed += length;
    }

    private void Realtime(byte command)
    {
        try
        {
            _transport.WriteRealtime(command);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
        {
            OnFailed(ex);
        }
    }

    private void Override(byte command)
    {
        lock (_lock)
        {
            if (IsConnected)
            {
                Realtime(command);
            }
        }
    }

    private void ResetController(string message)
    {
        Realtime(Reset);
        _pending.Clear();
        _bufferUsed = 0;
        _manual.Clear();
        _stopRequested = false;
        if (Job != GrblJobState.None)
        {
            FinishJob(false, message);
        }
        else
        {
            Write(GrblLogKind.Info, message);
        }

        Changed?.Invoke();
    }

    private void FinishJob(bool success, string message)
    {
        Job = GrblJobState.None;
        JobMessage = "";
        _program = new List<GrblLine>();
        _next = 0;
        _drainAndWait = false;
        _atEnd = false;
        Write(success ? GrblLogKind.Info : GrblLogKind.Error, message);
        JobFinished?.Invoke(new GrblJobResult(success, message));
    }

    /// <summary>Feed is modal: remember the last F word so that it can be restored after manual commands.</summary>
    private void RememberFeed(string text)
    {
        var index = text.IndexOf('F', StringComparison.Ordinal);
        if (index < 0)
        {
            return;
        }

        var end = index + 1;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.'))
        {
            end++;
        }

        if (end > index + 1)
        {
            _lastFeedWord = text[index..end];
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException(Loc.T("Нет связи со станком.", "Not connected to the machine."));
        }
    }

    private void Write(GrblLogKind kind, string text) => Log?.Invoke(new GrblLogEntry(kind, text));

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record Pending(string Text, int Length, int SourceLine);
}
