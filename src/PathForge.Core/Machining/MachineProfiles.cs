using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>Ready-made machine settings. Applying a profile keeps the user's own header/footer lines.</summary>
public sealed record MachineProfile(
    string NameRu, string NameEn, string DescriptionRu, string DescriptionEn, Action<MachineSettings> Configure)
{
    public string Name => Loc.T(NameRu, NameEn);

    public string Description => Loc.T(DescriptionRu, DescriptionEn);

    /// <summary>True when a stored profile name (in any language) refers to this profile.</summary>
    public bool Matches(string profileName) => profileName == NameRu || profileName == NameEn;

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
        "CNC 3018 Pro (775 spindle)",
        "GRBL 1.1, поле 300×180×45 мм, S0–1000 ≈ 0–10 000 об/мин",
        "GRBL 1.1, work area 300×180×45 mm, S0–1000 ≈ 0–10,000 rpm",
        m => Configure3018(m, maxRpm: 10000, spindleDelay: 3));

    /// <summary>3018 Pro with a 500 W ER11 spindle (~12 000 rpm at S1000).</summary>
    public static MachineProfile Cnc3018Spindle500W { get; } = new(
        "CNC 3018 Pro (шпиндель 500 Вт)",
        "CNC 3018 Pro (500 W spindle)",
        "GRBL 1.1, поле 300×180×45 мм, S0–1000 ≈ 0–12 000 об/мин",
        "GRBL 1.1, work area 300×180×45 mm, S0–1000 ≈ 0–12,000 rpm",
        m => Configure3018(m, maxRpm: 12000, spindleDelay: 4));

    /// <summary>3018 Pro with a diode laser module on the spindle PWM output, GRBL laser mode.</summary>
    public static MachineProfile Cnc3018Laser { get; } = new(
        "CNC 3018 Pro (лазер 5 Вт)",
        "CNC 3018 Pro (5 W laser)",
        "GRBL 1.1 с $32=1 (лазерный режим), мощность S0–1000, без движений по Z",
        "GRBL 1.1 with $32=1 (laser mode), power S0–1000, no Z moves",
        ConfigureLaser);

    /// <summary>
    /// 3018 Pro with a 10 W (optical power) diode laser module. Wiring and GRBL settings are the same as for
    /// the 5 W module; the stronger beam cuts in fewer passes and needs air assist and a good exhaust.
    /// </summary>
    public static MachineProfile Cnc3018Laser10W { get; } = new(
        "CNC 3018 Pro (лазер 10 Вт)",
        "CNC 3018 Pro (10 W laser)",
        "GRBL 1.1 с $32=1 (лазерный режим), мощность S0–1000, без движений по Z; для резки нужен обдув (air assist)",
        "GRBL 1.1 with $32=1 (laser mode), power S0–1000, no Z moves; cutting needs air assist",
        ConfigureLaser);

    public static MachineProfile Generic { get; } = new(
        "Общий G-code",
        "Generic G-code",
        "LinuxCNC / Mach3: обороты пишутся как есть, смена инструмента Tn M6, без проверок поля",
        "LinuxCNC / Mach3: spindle speed written as is, tool change Tn M6, no work area checks",
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

    public static IReadOnlyList<MachineProfile> All { get; } = new[] { Cnc3018Stock, Cnc3018Spindle500W, Cnc3018Laser, Cnc3018Laser10W, Generic };

    /// <summary>The profile a stored profile name refers to (names are stored in the language of the moment).</summary>
    public static MachineProfile? Find(string profileName) => All.FirstOrDefault(p => p.Matches(profileName));

    private static void ConfigureLaser(MachineSettings m)
    {
        Configure3018(m, maxRpm: 10000, spindleDelay: 0);
        m.LaserMode = true;
        m.SafeZ = 0;
        m.ApproachClearance = 0;
        m.MaxFeedRate = 3000;
        m.RapidRate = 3000;
    }

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
