namespace PathForge.Core.Machining;

/// <summary>Ready-made machine settings. Applying a profile keeps the user's own header/footer lines.</summary>
public sealed record MachineProfile(string Name, string Description, Action<MachineSettings> Configure)
{
    public void ApplyTo(MachineSettings settings)
    {
        Configure(settings);
        settings.ProfileName = Name;
    }
}

public static class MachineProfiles
{
    /// <summary>TwoTrees 3018 Pro with the stock 775 motor (~10 000 rpm at S1000), GRBL 1.1.</summary>
    public static MachineProfile Cnc3018Stock { get; } = new(
        "CNC 3018 Pro (шпиндель 775)",
        "GRBL 1.1, поле 300×180×45 мм, S0–1000 ≈ 0–10 000 об/мин",
        m => Configure3018(m, maxRpm: 10000, spindleDelay: 3));

    /// <summary>3018 Pro with a 500 W ER11 spindle (~12 000 rpm at S1000).</summary>
    public static MachineProfile Cnc3018Spindle500W { get; } = new(
        "CNC 3018 Pro (шпиндель 500 Вт)",
        "GRBL 1.1, поле 300×180×45 мм, S0–1000 ≈ 0–12 000 об/мин",
        m => Configure3018(m, maxRpm: 12000, spindleDelay: 4));

    /// <summary>3018 Pro with a diode laser module on the spindle PWM output, GRBL laser mode.</summary>
    public static MachineProfile Cnc3018Laser { get; } = new(
        "CNC 3018 Pro (лазер 5 Вт)",
        "GRBL 1.1 с $32=1 (лазерный режим), мощность S0–1000, без движений по Z",
        m =>
        {
            Configure3018(m, maxRpm: 10000, spindleDelay: 0);
            m.LaserMode = true;
            m.SafeZ = 0;
            m.ApproachClearance = 0;
            m.MaxFeedRate = 3000;
            m.RapidRate = 3000;
        });

    public static MachineProfile Generic { get; } = new(
        "Общий G-code",
        "LinuxCNC / Mach3: обороты пишутся как есть, смена инструмента Tn M6, без проверок поля",
        m =>
        {
            m.Dialect = GcodeDialect.Generic;
            m.SpindleMaxS = 0;
            m.SpindleMaxRpm = 24000;
            m.MaxFeedRate = 0;
            m.RapidRate = 3000;
            m.WorkAreaX = m.WorkAreaY = m.WorkAreaZ = 0;
            m.SpindleDelaySeconds = 2;
            m.UseArcs = true;
            m.LaserMode = false;
        });

    public static IReadOnlyList<MachineProfile> All { get; } = new[] { Cnc3018Stock, Cnc3018Spindle500W, Cnc3018Laser, Generic };

    private static void Configure3018(MachineSettings m, double maxRpm, double spindleDelay)
    {
        m.Dialect = GcodeDialect.Grbl;
        m.SpindleMaxRpm = maxRpm;
        m.SpindleMaxS = 1000; // GRBL $30
        m.MaxFeedRate = 1000; // GRBL $110/$111 on a stock 3018
        m.RapidRate = 1000;
        m.WorkAreaX = 300;
        m.WorkAreaY = 180;
        m.WorkAreaZ = 45;
        m.SafeZ = 5;
        m.ApproachClearance = 1;
        m.UseToolChange = false;
        m.SpindleDelaySeconds = spindleDelay;
        m.DecimalPlaces = 3;
        m.UseArcs = true;
        m.LaserMode = false;
    }
}
