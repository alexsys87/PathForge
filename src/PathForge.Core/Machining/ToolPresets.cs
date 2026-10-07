using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>A tool template with conservative cutting data for a given machine class.</summary>
public sealed record ToolPreset(string GroupRu, string GroupEn, string NameRu, string NameEn, Tool Template)
{
    public string Group => Loc.T(GroupRu, GroupEn);

    public string Name => Loc.T(NameRu, NameEn);

    public string Display => $"{Group}: {Name}";

    /// <summary>A new tool (own Id) with the preset's data, the given tool number and a name in the current language.</summary>
    public Tool Create(int number)
    {
        var tool = Template.Clone();
        tool.Id = Guid.NewGuid().ToString("N");
        tool.Number = number;
        tool.Name = Name;
        return tool;
    }
}

/// <summary>
/// Starting values for small hobby machines (CNC 3018 with the stock ~10 000 rpm 775 motor,
/// ER11 collet, 3.175 mm shanks). They are deliberately gentle: increase feeds step by step.
/// </summary>
public static class ToolPresets
{
    private static readonly (string Ru, string En) Wood = ("Дерево, фанера", "Wood, plywood");
    private static readonly (string Ru, string En) Mdf = ("МДФ", "MDF");
    private static readonly (string Ru, string En) Relief = ("Рельеф 3D", "3D relief");
    private static readonly (string Ru, string En) Acrylic = ("Акрил", "Acrylic");
    private static readonly (string Ru, string En) Aluminium = ("Алюминий", "Aluminium");
    private static readonly (string Ru, string En) Pcb = ("Текстолит (платы)", "PCB (FR4)");
    private static readonly (string Ru, string En) Engraving = ("Гравировка", "Engraving");
    private static readonly (string Ru, string En) VCarve = ("V-карвинг", "V-carving");
    private static readonly (string Ru, string En) General = ("Общее", "General");
    private static readonly (string Ru, string En) Laser = ("Лазер", "Laser");

    /// <summary>
    /// Popular cutters for the ER11 collet of a 3018 (collets 1–7 mm: 3.175 mm shanks, 4 and 6 mm ones with the
    /// matching collet), grouped by material. The first preset of a kind is the usual choice for it.
    /// </summary>
    public static IReadOnlyList<ToolPreset> Cnc3018 { get; } = new[]
    {
        // Wood and plywood.
        Preset(Wood, ("Фреза 1-заходная Ø3,175", "1-flute end mill Ø3.175"), ToolKind.EndMill, 3.175, 10000, feed: 400, plunge: 120, stepDown: 1.0, stepOver: 40),
        Preset(Wood, ("Фреза 2-заходная Ø2", "2-flute end mill Ø2"), ToolKind.EndMill, 2, 10000, feed: 300, plunge: 100, stepDown: 0.6, stepOver: 40),
        Preset(Wood, ("Фреза 2-заходная Ø3,175 (стружка вверх)", "2-flute end mill Ø3.175 (up-cut)"), ToolKind.EndMill, 3.175, 10000, feed: 500, plunge: 150, stepDown: 0.8, stepOver: 40),
        Preset(Wood, ("Фреза Ø3,175 стружка вниз (чистый верх фанеры)", "End mill Ø3.175 down-cut (clean plywood top)"), ToolKind.EndMill, 3.175, 10000, feed: 450, plunge: 100, stepDown: 0.6, stepOver: 40),
        Preset(Wood, ("Фреза компрессионная Ø3,175 (без сколов с двух сторон)", "Compression end mill Ø3.175 (no chipping on both faces)"), ToolKind.EndMill, 3.175, 10000, feed: 400, plunge: 100, stepDown: 1.0, stepOver: 40),
        Preset(Wood, ("Фреза 1-заходная Ø2", "1-flute end mill Ø2"), ToolKind.EndMill, 2, 10000, feed: 300, plunge: 90, stepDown: 0.5, stepOver: 40),
        Preset(Wood, ("Фреза Ø1,5", "End mill Ø1.5"), ToolKind.EndMill, 1.5, 10000, feed: 250, plunge: 70, stepDown: 0.4, stepOver: 40),
        Preset(Wood, ("Фреза Ø1", "End mill Ø1"), ToolKind.EndMill, 1, 10000, feed: 200, plunge: 60, stepDown: 0.3, stepOver: 40),
        Preset(Wood, ("Фреза 2-заходная Ø4 (цанга 4 мм)", "2-flute end mill Ø4 (4 mm collet)"), ToolKind.EndMill, 4, 10000, feed: 550, plunge: 150, stepDown: 0.8, stepOver: 40),
        Preset(Wood, ("Фреза 2-заходная Ø6 (цанга 6 мм)", "2-flute end mill Ø6 (6 mm collet)"), ToolKind.EndMill, 6, 10000, feed: 600, plunge: 150, stepDown: 0.6, stepOver: 40),

        // MDF.
        Preset(Mdf, ("Фреза 1-заходная Ø3,175", "1-flute end mill Ø3.175"), ToolKind.EndMill, 3.175, 10000, feed: 450, plunge: 120, stepDown: 1.2, stepOver: 45),
        Preset(Mdf, ("Фреза 2-заходная Ø6 (цанга 6 мм)", "2-flute end mill Ø6 (6 mm collet)"), ToolKind.EndMill, 6, 10000, feed: 650, plunge: 150, stepDown: 0.8, stepOver: 45),

        // 3D relief.
        Preset(Relief, ("Сферическая Ø3,175 (R1,6)", "Ball nose Ø3.175 (R1.6)"), ToolKind.BallNose, 3.175, 10000, feed: 600, plunge: 150, stepDown: 1.0, stepOver: 30),
        Preset(Relief, ("Сферическая Ø2 (R1)", "Ball nose Ø2 (R1)"), ToolKind.BallNose, 2, 10000, feed: 500, plunge: 120, stepDown: 0.8, stepOver: 30),
        Preset(Relief, ("Сферическая Ø1 (R0,5)", "Ball nose Ø1 (R0.5)"), ToolKind.BallNose, 1, 10000, feed: 300, plunge: 80, stepDown: 0.3, stepOver: 20),
        Preset(Relief, ("Сферическая Ø6 (R3, цанга 6 мм)", "Ball nose Ø6 (R3, 6 mm collet)"), ToolKind.BallNose, 6, 10000, feed: 700, plunge: 150, stepDown: 1.0, stepOver: 30),
        VBit(Relief, ("Коническая сферическая R0,25, 15° (мелкие детали)", "Tapered ball nose R0.25, 15° (fine detail)"), tip: 0.5, angle: 30, feed: 400, plunge: 100, stepDown: 0.5),
        VBit(Relief, ("Коническая сферическая R0,5, 15°", "Tapered ball nose R0.5, 15°"), tip: 1.0, angle: 30, feed: 450, plunge: 100, stepDown: 0.6),

        // Acrylic.
        Preset(Acrylic, ("Фреза 1-заходная Ø3,175 (O-flute)", "1-flute end mill Ø3.175 (O-flute)"), ToolKind.EndMill, 3.175, 10000, feed: 350, plunge: 80, stepDown: 0.8, stepOver: 40),
        Preset(Acrylic, ("Фреза 1-заходная Ø2 (O-flute)", "1-flute end mill Ø2 (O-flute)"), ToolKind.EndMill, 2, 10000, feed: 300, plunge: 70, stepDown: 0.5, stepOver: 40),

        // Aluminium.
        Preset(Aluminium, ("Фреза 1-заходная Ø3,175", "1-flute end mill Ø3.175"), ToolKind.EndMill, 3.175, 10000, feed: 150, plunge: 40, stepDown: 0.2, stepOver: 30),
        Preset(Aluminium, ("Фреза 1-заходная Ø2", "1-flute end mill Ø2"), ToolKind.EndMill, 2, 10000, feed: 120, plunge: 30, stepDown: 0.15, stepOver: 30),
        Preset(Aluminium, ("Фреза 2-заходная Ø4 (цанга 4 мм)", "2-flute end mill Ø4 (4 mm collet)"), ToolKind.EndMill, 4, 10000, feed: 180, plunge: 40, stepDown: 0.2, stepOver: 30),

        // PCB (FR4).
        Preset(Pcb, ("Кукуруза Ø0,8 (вырезка, отверстия)", "Corn mill Ø0.8 (cut-out, holes)"), ToolKind.EndMill, 0.8, 10000, feed: 120, plunge: 40, stepDown: 0.4, stepOver: 40),
        Preset(Pcb, ("Кукуруза Ø1,0 (вырезка платы)", "Corn mill Ø1.0 (board cut-out)"), ToolKind.EndMill, 1.0, 10000, feed: 150, plunge: 50, stepDown: 0.5, stepOver: 40),
        Preset(Pcb, ("Кукуруза Ø1,5", "Corn mill Ø1.5"), ToolKind.EndMill, 1.5, 10000, feed: 180, plunge: 50, stepDown: 0.6, stepOver: 40),
        Preset(Pcb, ("Кукуруза Ø2,0", "Corn mill Ø2.0"), ToolKind.EndMill, 2.0, 10000, feed: 220, plunge: 60, stepDown: 0.8, stepOver: 40),
        Preset(Pcb, ("Кукуруза Ø3,175", "Corn mill Ø3.175"), ToolKind.EndMill, 3.175, 10000, feed: 250, plunge: 70, stepDown: 1.0, stepOver: 40),
        Preset(Pcb, ("Фреза Ø2 (удаление лишней меди)", "End mill Ø2 (removing excess copper)"), ToolKind.EndMill, 2.0, 10000, feed: 300, plunge: 60, stepDown: 0.2, stepOver: 50),
        VBit(Pcb, ("Гравёр 20°, кончик 0,1", "Engraver 20°, tip 0.1"), tip: 0.1, angle: 20, feed: 150, plunge: 50),
        VBit(Pcb, ("Гравёр 30°, кончик 0,1", "Engraver 30°, tip 0.1"), tip: 0.1, angle: 30, feed: 150, plunge: 50),
        VBit(Pcb, ("Гравёр 10°, кончик 0,1 (тонкие зазоры)", "Engraver 10°, tip 0.1 (fine gaps)"), tip: 0.1, angle: 10, feed: 120, plunge: 40),
        Drill(Pcb, 0.3, plunge: 30),
        Drill(Pcb, 0.4, plunge: 30),
        Drill(Pcb, 0.5, plunge: 30),
        Drill(Pcb, 0.6, plunge: 40),
        Drill(Pcb, 0.7, plunge: 40),
        Drill(Pcb, 0.8, plunge: 50),
        Drill(Pcb, 0.9, plunge: 50),
        Drill(Pcb, 1.0, plunge: 50),
        Drill(Pcb, 1.1, plunge: 50),
        Drill(Pcb, 1.2, plunge: 50),
        Drill(Pcb, 1.5, plunge: 50),

        // Engraving.
        VBit(Engraving, ("Гравёр 60°, кончик 0,2", "Engraver 60°, tip 0.2"), tip: 0.2, angle: 60, feed: 250, plunge: 60),
        VBit(Engraving, ("Гравёр 30°, кончик 0,2", "Engraver 30°, tip 0.2"), tip: 0.2, angle: 30, feed: 200, plunge: 60),
        VBit(Engraving, ("Гравёр 45°, кончик 0,2", "Engraver 45°, tip 0.2"), tip: 0.2, angle: 45, feed: 220, plunge: 60),

        // V-carving and chamfers.
        VBit(VCarve, ("V-фреза 60° Ø3,175", "V-bit 60° Ø3.175"), tip: 0.05, angle: 60, feed: 400, plunge: 100, stepDown: 1.0),
        VBit(VCarve, ("V-фреза 90° Ø3,175", "V-bit 90° Ø3.175"), tip: 0.05, angle: 90, feed: 400, plunge: 100, stepDown: 1.0),
        VBit(VCarve, ("V-фреза 30° Ø3,175", "V-bit 30° Ø3.175"), tip: 0.05, angle: 30, feed: 350, plunge: 80, stepDown: 0.8),
        VBit(VCarve, ("V-фреза 90° Ø6 (фаски, цанга 6 мм)", "V-bit 90° Ø6 (chamfers, 6 mm collet)"), tip: 0.1, angle: 90, feed: 400, plunge: 100, stepDown: 1.0, diameter: 6),

        // General: drills for wood and plastics, surfacing.
        Preset(General, ("Сверло Ø2", "Drill Ø2"), ToolKind.Drill, 2, 8000, feed: 150, plunge: 60, stepDown: 3, stepOver: 50),
        Preset(General, ("Сверло Ø3", "Drill Ø3"), ToolKind.Drill, 3, 8000, feed: 150, plunge: 60, stepDown: 3, stepOver: 50),
        Preset(General, ("Сверло Ø4", "Drill Ø4"), ToolKind.Drill, 4, 8000, feed: 150, plunge: 50, stepDown: 3, stepOver: 50),
        Preset(General, ("Сверло Ø5", "Drill Ø5"), ToolKind.Drill, 5, 7000, feed: 130, plunge: 45, stepDown: 3, stepOver: 50),
        Preset(General, ("Сверло Ø6 (цанга 6 мм)", "Drill Ø6 (6 mm collet)"), ToolKind.Drill, 6, 6000, feed: 120, plunge: 40, stepDown: 3, stepOver: 50),
        Preset(General, ("Фреза Ø6 для выравнивания стола (торцовка)", "Ø6 end mill for spoil board surfacing (facing)"), ToolKind.EndMill, 6, 10000, feed: 600, plunge: 150, stepDown: 0.3, stepOver: 60),
        Preset(General, ("Фреза для выравнивания Ø12, хвостовик 6 мм (торцовка)", "Surfacing bit Ø12, 6 mm shank (facing)"), ToolKind.EndMill, 12, 10000, feed: 700, plunge: 150, stepDown: 0.2, stepOver: 70),

        // Laser modules.
        Preset(Laser, ("Лазерный модуль 5 Вт (пятно ≈0,15)", "Laser module 5 W (spot ≈0.15)"), ToolKind.Laser, 0.15, 0, feed: 1000, plunge: 1000, stepDown: 1, stepOver: 100),
        Preset(Laser, ("Лазерный модуль 10 Вт (пятно ≈0,1)", "Laser module 10 W (spot ≈0.1)"), ToolKind.Laser, 0.1, 0, feed: 1000, plunge: 1000, stepDown: 1, stepOver: 100),
    };

    /// <summary>Presets of the same group as <paramref name="preset"/>, in the library order.</summary>
    public static IEnumerable<ToolPreset> SameGroup(ToolPreset preset) => Cnc3018.Where(p => p.GroupRu == preset.GroupRu);

    /// <summary>Whether a project tool is (still) the preset: same kind, diameter, angle and name.</summary>
    public static bool Matches(ToolPreset preset, Tool tool) =>
        tool.Kind == preset.Template.Kind && Math.Abs(tool.Diameter - preset.Template.Diameter) < 1e-9 &&
        (tool.Kind != ToolKind.VBit || Math.Abs(tool.TipAngle - preset.Template.TipAngle) < 1e-9) &&
        (tool.Name == preset.NameRu || tool.Name == preset.NameEn);

    private static ToolPreset Drill((string Ru, string En) group, double diameter, double plunge)
    {
        var ru = diameter.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
        var en = diameter.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return Preset(group, ($"Сверло Ø{ru}", $"Drill Ø{en}"), ToolKind.Drill, diameter, 10000, feed: 100, plunge: plunge, stepDown: 2, stepOver: 50);
    }

    private static ToolPreset VBit((string Ru, string En) group, (string Ru, string En) name, double tip, double angle,
        double feed, double plunge, double stepDown = 0.1, double diameter = 3.175) =>
        new(group.Ru, group.En, name.Ru, name.En, new Tool
        {
            Name = name.Ru,
            Kind = ToolKind.VBit,
            Diameter = diameter,
            TipDiameter = tip,
            TipAngle = angle,
            SpindleRpm = 10000,
            FeedRate = feed,
            PlungeRate = plunge,
            StepDown = stepDown,
            StepOverPercent = 50,
        });

    private static ToolPreset Preset((string Ru, string En) group, (string Ru, string En) name, ToolKind kind, double diameter, double rpm,
        double feed, double plunge, double stepDown, double stepOver) =>
        new(group.Ru, group.En, name.Ru, name.En, new Tool
        {
            Name = name.Ru,
            Kind = kind,
            Diameter = diameter,
            SpindleRpm = rpm,
            FeedRate = feed,
            PlungeRate = plunge,
            StepDown = stepDown,
            StepOverPercent = stepOver,
        });
}
