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

    public static IReadOnlyList<ToolPreset> Cnc3018 { get; } = new[]
    {
        Preset(Wood, ("Фреза 1-заходная Ø3,175", "1-flute end mill Ø3.175"), ToolKind.EndMill, 3.175, 10000, feed: 400, plunge: 120, stepDown: 1.0, stepOver: 40),
        Preset(Wood, ("Фреза 2-заходная Ø2", "2-flute end mill Ø2"), ToolKind.EndMill, 2, 10000, feed: 300, plunge: 100, stepDown: 0.6, stepOver: 40),
        Preset(Wood, ("Фреза Ø1", "End mill Ø1"), ToolKind.EndMill, 1, 10000, feed: 200, plunge: 60, stepDown: 0.3, stepOver: 40),
        Preset(Mdf, ("Фреза 1-заходная Ø3,175", "1-flute end mill Ø3.175"), ToolKind.EndMill, 3.175, 10000, feed: 450, plunge: 120, stepDown: 1.2, stepOver: 45),
        Preset(Relief, ("Сферическая Ø3,175 (R1,6)", "Ball nose Ø3.175 (R1.6)"), ToolKind.BallNose, 3.175, 10000, feed: 600, plunge: 150, stepDown: 1.0, stepOver: 30),
        Preset(Relief, ("Сферическая Ø2 (R1)", "Ball nose Ø2 (R1)"), ToolKind.BallNose, 2, 10000, feed: 500, plunge: 120, stepDown: 0.8, stepOver: 30),
        Preset(Acrylic, ("Фреза 1-заходная Ø3,175 (O-flute)", "1-flute end mill Ø3.175 (O-flute)"), ToolKind.EndMill, 3.175, 10000, feed: 350, plunge: 80, stepDown: 0.8, stepOver: 40),
        Preset(Aluminium, ("Фреза 1-заходная Ø3,175", "1-flute end mill Ø3.175"), ToolKind.EndMill, 3.175, 10000, feed: 150, plunge: 40, stepDown: 0.2, stepOver: 30),
        Preset(Pcb, ("Кукуруза Ø0,8 (вырезка, отверстия)", "Corn mill Ø0.8 (cut-out, holes)"), ToolKind.EndMill, 0.8, 10000, feed: 120, plunge: 40, stepDown: 0.4, stepOver: 40),
        Preset(Pcb, ("Кукуруза Ø1,0 (вырезка платы)", "Corn mill Ø1.0 (board cut-out)"), ToolKind.EndMill, 1.0, 10000, feed: 150, plunge: 50, stepDown: 0.5, stepOver: 40),
        Preset(Pcb, ("Фреза Ø2 (удаление лишней меди)", "End mill Ø2 (removing excess copper)"), ToolKind.EndMill, 2.0, 10000, feed: 300, plunge: 60, stepDown: 0.2, stepOver: 50),
        VBit(Pcb, ("Гравёр 20°, кончик 0,1", "Engraver 20°, tip 0.1"), tip: 0.1, angle: 20, feed: 150, plunge: 50),
        VBit(Pcb, ("Гравёр 30°, кончик 0,1", "Engraver 30°, tip 0.1"), tip: 0.1, angle: 30, feed: 150, plunge: 50),
        VBit(Engraving, ("Гравёр 60°, кончик 0,2", "Engraver 60°, tip 0.2"), tip: 0.2, angle: 60, feed: 250, plunge: 60),
        VBit(VCarve, ("V-фреза 60° Ø3,175", "V-bit 60° Ø3.175"), tip: 0.05, angle: 60, feed: 400, plunge: 100, stepDown: 1.0),
        VBit(VCarve, ("V-фреза 90° Ø3,175", "V-bit 90° Ø3.175"), tip: 0.05, angle: 90, feed: 400, plunge: 100, stepDown: 1.0),
        Preset(Pcb, ("Сверло Ø0,6", "Drill Ø0.6"), ToolKind.Drill, 0.6, 10000, feed: 100, plunge: 40, stepDown: 2, stepOver: 50),
        Preset(Pcb, ("Сверло Ø0,8", "Drill Ø0.8"), ToolKind.Drill, 0.8, 10000, feed: 100, plunge: 50, stepDown: 2, stepOver: 50),
        Preset(Pcb, ("Сверло Ø1,0", "Drill Ø1.0"), ToolKind.Drill, 1.0, 10000, feed: 100, plunge: 50, stepDown: 2, stepOver: 50),
        Preset(Pcb, ("Сверло Ø1,2", "Drill Ø1.2"), ToolKind.Drill, 1.2, 10000, feed: 100, plunge: 50, stepDown: 2, stepOver: 50),
        Preset(General, ("Сверло Ø3", "Drill Ø3"), ToolKind.Drill, 3, 8000, feed: 150, plunge: 60, stepDown: 3, stepOver: 50),
        Preset(General, ("Фреза Ø6 для выравнивания стола (торцовка)", "Ø6 end mill for spoil board surfacing (facing)"), ToolKind.EndMill, 6, 10000, feed: 600, plunge: 150, stepDown: 0.3, stepOver: 60),
        Preset(Laser, ("Лазерный модуль 5 Вт (пятно ≈0,15)", "Laser module 5 W (spot ≈0.15)"), ToolKind.Laser, 0.15, 0, feed: 1000, plunge: 1000, stepDown: 1, stepOver: 100),
        Preset(Laser, ("Лазерный модуль 10 Вт (пятно ≈0,1)", "Laser module 10 W (spot ≈0.1)"), ToolKind.Laser, 0.1, 0, feed: 1000, plunge: 1000, stepDown: 1, stepOver: 100),
    };

    private static ToolPreset VBit((string Ru, string En) group, (string Ru, string En) name, double tip, double angle,
        double feed, double plunge, double stepDown = 0.1) =>
        new(group.Ru, group.En, name.Ru, name.En, new Tool
        {
            Name = name.Ru,
            Kind = ToolKind.VBit,
            Diameter = 3.175,
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
