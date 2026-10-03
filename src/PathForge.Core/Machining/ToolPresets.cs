namespace PathForge.Core.Machining;

/// <summary>A tool template with conservative cutting data for a given machine class.</summary>
public sealed record ToolPreset(string Group, string Name, Tool Template)
{
    public string Display => $"{Group}: {Name}";

    /// <summary>A new tool (own Id) with the preset's data and the given tool number.</summary>
    public Tool Create(int number)
    {
        var tool = Template.Clone();
        tool.Id = Guid.NewGuid().ToString("N");
        tool.Number = number;
        return tool;
    }
}

/// <summary>
/// Starting values for small hobby machines (CNC 3018 with the stock ~10 000 rpm 775 motor,
/// ER11 collet, 3.175 mm shanks). They are deliberately gentle: increase feeds step by step.
/// </summary>
public static class ToolPresets
{
    public static IReadOnlyList<ToolPreset> Cnc3018 { get; } = new[]
    {
        Preset("Дерево, фанера", "Фреза 1-заходная Ø3,175", ToolKind.EndMill, 3.175, 10000, feed: 400, plunge: 120, stepDown: 1.0, stepOver: 40),
        Preset("Дерево, фанера", "Фреза 2-заходная Ø2", ToolKind.EndMill, 2, 10000, feed: 300, plunge: 100, stepDown: 0.6, stepOver: 40),
        Preset("Дерево, фанера", "Фреза Ø1", ToolKind.EndMill, 1, 10000, feed: 200, plunge: 60, stepDown: 0.3, stepOver: 40),
        Preset("МДФ", "Фреза 1-заходная Ø3,175", ToolKind.EndMill, 3.175, 10000, feed: 450, plunge: 120, stepDown: 1.2, stepOver: 45),
        Preset("Рельеф 3D", "Сферическая Ø3,175 (R1,6)", ToolKind.BallNose, 3.175, 10000, feed: 600, plunge: 150, stepDown: 1.0, stepOver: 30),
        Preset("Рельеф 3D", "Сферическая Ø2 (R1)", ToolKind.BallNose, 2, 10000, feed: 500, plunge: 120, stepDown: 0.8, stepOver: 30),
        Preset("Акрил", "Фреза 1-заходная Ø3,175 (O-flute)", ToolKind.EndMill, 3.175, 10000, feed: 350, plunge: 80, stepDown: 0.8, stepOver: 40),
        Preset("Алюминий", "Фреза 1-заходная Ø3,175", ToolKind.EndMill, 3.175, 10000, feed: 150, plunge: 40, stepDown: 0.2, stepOver: 30),
        Preset("Текстолит (платы)", "Кукуруза Ø0,8 (вырезка, отверстия)", ToolKind.EndMill, 0.8, 10000, feed: 120, plunge: 40, stepDown: 0.4, stepOver: 40),
        Preset("Текстолит (платы)", "Кукуруза Ø1,0 (вырезка платы)", ToolKind.EndMill, 1.0, 10000, feed: 150, plunge: 50, stepDown: 0.5, stepOver: 40),
        VBit("Текстолит (платы)", "Гравёр 20°, кончик 0,1", tip: 0.1, angle: 20, feed: 150, plunge: 50),
        VBit("Текстолит (платы)", "Гравёр 30°, кончик 0,1", tip: 0.1, angle: 30, feed: 150, plunge: 50),
        VBit("Гравировка", "Гравёр 60°, кончик 0,2", tip: 0.2, angle: 60, feed: 250, plunge: 60),
        VBit("V-карвинг", "V-фреза 60° Ø3,175", tip: 0.05, angle: 60, feed: 400, plunge: 100, stepDown: 1.0),
        VBit("V-карвинг", "V-фреза 90° Ø3,175", tip: 0.05, angle: 90, feed: 400, plunge: 100, stepDown: 1.0),
        Preset("Текстолит (платы)", "Сверло Ø0,8", ToolKind.Drill, 0.8, 10000, feed: 100, plunge: 50, stepDown: 2, stepOver: 50),
        Preset("Текстолит (платы)", "Сверло Ø1,0", ToolKind.Drill, 1.0, 10000, feed: 100, plunge: 50, stepDown: 2, stepOver: 50),
        Preset("Общее", "Сверло Ø3", ToolKind.Drill, 3, 8000, feed: 150, plunge: 60, stepDown: 3, stepOver: 50),
        Preset("Лазер", "Лазерный модуль 5 Вт (пятно ≈0,15)", ToolKind.Laser, 0.15, 0, feed: 1000, plunge: 1000, stepDown: 1, stepOver: 100),
    };

    private static ToolPreset VBit(string group, string name, double tip, double angle, double feed, double plunge, double stepDown = 0.1) =>
        new(group, name, new Tool
        {
            Name = name,
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

    private static ToolPreset Preset(string group, string name, ToolKind kind, double diameter, double rpm,
        double feed, double plunge, double stepDown, double stepOver) =>
        new(group, name, new Tool
        {
            Name = name,
            Kind = kind,
            Diameter = diameter,
            SpindleRpm = rpm,
            FeedRate = feed,
            PlungeRate = plunge,
            StepDown = stepDown,
            StepOverPercent = stepOver,
        });
}
