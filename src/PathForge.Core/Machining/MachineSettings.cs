namespace PathForge.Core.Machining;

/// <summary>Flavour of G-code the controller understands.</summary>
public enum GcodeDialect
{
    /// <summary>LinuxCNC / Mach3 style: tool change with Tn M6, any comment text.</summary>
    Generic,

    /// <summary>
    /// GRBL 1.1 (CNC 3018 and similar): no M6, lines of at most 80 characters,
    /// comments limited to ASCII.
    /// </summary>
    Grbl,
}

/// <summary>Machine and G-code output settings. Z = 0 is the top of the stock unless the stock says otherwise.</summary>
public sealed class MachineSettings
{
    /// <summary>Name of the profile the settings were taken from (informational).</summary>
    public string ProfileName { get; set; } = "";

    public GcodeDialect Dialect { get; set; } = GcodeDialect.Generic;

    /// <summary>Height for rapid moves above the stock top.</summary>
    public double SafeZ { get; set; } = 5;

    /// <summary>Height above the stock where rapid approach ends and plunging starts.</summary>
    public double ApproachClearance { get; set; } = 1;

    /// <summary>Rapid speed used only for time estimation (mm/min).</summary>
    public double RapidRate { get; set; } = 3000;

    /// <summary>Highest feed the controller accepts (mm/min, 0 = no check). GRBL silently limits faster feeds.</summary>
    public double MaxFeedRate { get; set; }

    /// <summary>Spindle speed at full output (rpm). Used to convert tool rpm into the S word.</summary>
    public double SpindleMaxRpm { get; set; } = 24000;

    /// <summary>
    /// S value that means full spindle output (GRBL $30, usually 1000). 0 = write the rpm itself as S.
    /// </summary>
    public double SpindleMaxS { get; set; }

    /// <summary>Travel of the machine (mm, 0 = no check).</summary>
    public double WorkAreaX { get; set; }

    public double WorkAreaY { get; set; }

    public double WorkAreaZ { get; set; }

    public int DecimalPlaces { get; set; } = 3;

    /// <summary>
    /// GRBL laser mode ($32=1): spindle commands become laser power (M4 dynamic power), no Z retracts,
    /// no spindle start delay. The laser must be focused at Z0 (stock top) before the job.
    /// </summary>
    public bool LaserMode { get; set; }

    /// <summary>Write circular arcs as G2/G3 instead of many short lines (needs at least 3 decimals).</summary>
    public bool UseArcs { get; set; }

    /// <summary>Emit "Tn M6" when the tool changes (ignored for GRBL, which has no M6).</summary>
    public bool UseToolChange { get; set; }

    /// <summary>Pause after starting the spindle (seconds, 0 = none).</summary>
    public double SpindleDelaySeconds { get; set; } = 2;

    /// <summary>Move to X0 Y0 at the end of the program.</summary>
    public bool ReturnToOrigin { get; set; } = true;

    /// <summary>Extra lines written after the standard program start.</summary>
    public string Header { get; set; } = "";

    /// <summary>Extra lines written before the program end.</summary>
    public string Footer { get; set; } = "";

    /// <summary>S word for a spindle speed in rpm, scaled to the controller range when configured.</summary>
    public double SpindleWord(double rpm)
    {
        if (SpindleMaxS <= 0 || SpindleMaxRpm <= 0)
        {
            return Math.Max(0, rpm);
        }

        return Math.Round(Math.Clamp(rpm / SpindleMaxRpm, 0, 1) * SpindleMaxS);
    }

    public MachineSettings Clone() => (MachineSettings)MemberwiseClone();
}
