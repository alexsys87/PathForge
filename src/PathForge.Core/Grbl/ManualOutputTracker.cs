namespace PathForge.Core.Grbl;

/// <summary>
/// What the control panel shows for the spindle / laser output. GRBL reports the output only every 2-4 seconds
/// (accessory state A:, sent together with the overrides), so right after the operator switched it by hand the
/// panel would show the old state for seconds. The tracker holds the state that was just commanded until GRBL
/// reports the same, a command was rejected, or two reports that carry the accessory state disagree with it (the
/// first of them may have been taken before the command arrived). A firmware that never reports the accessory
/// state keeps the commanded state, as there is nothing to check it against.
/// </summary>
public sealed class ManualOutputTracker
{
    private bool? _commanded;
    private int _reportsAtCommand;
    private int _failedAtCommand;

    /// <summary>The operator switched the output on or off; <paramref name="status"/> is the last report before that.</summary>
    public void Commanded(bool on, GrblStatus status, int failedCommands)
    {
        ArgumentNullException.ThrowIfNull(status);
        _commanded = on;
        _reportsAtCommand = status.AccessoryReports;
        _failedAtCommand = failedCommands;
    }

    /// <summary>Forgets the command: a reset, a program or a lost connection decides the output now.</summary>
    public void Reset() => _commanded = null;

    /// <summary>Whether the output is on: the commanded state until GRBL has confirmed or contradicted it.</summary>
    public bool Resolve(GrblStatus status, int failedCommands)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (_commanded is not { } commanded)
        {
            return status.SpindleOn;
        }

        var reports = status.AccessoryReports - _reportsAtCommand;
        // reports < 0: a new controller (reconnect) started counting again.
        if (failedCommands != _failedAtCommand || reports < 0 || reports >= 2 || (reports >= 1 && status.SpindleOn == commanded))
        {
            _commanded = null;
            return status.SpindleOn;
        }

        return commanded;
    }
}
