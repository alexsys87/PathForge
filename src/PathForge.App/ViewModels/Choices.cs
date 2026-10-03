using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.App.ViewModels;

public sealed record Choice<T>(T Value, string Label);

/// <summary>Display names for enum values used in combo boxes.</summary>
public static class Choices
{
    public static IReadOnlyList<Choice<ProfileSide>> ProfileSides { get; } = new[]
    {
        new Choice<ProfileSide>(ProfileSide.Outside, "Снаружи"),
        new Choice<ProfileSide>(ProfileSide.Inside, "Внутри"),
        new Choice<ProfileSide>(ProfileSide.OnLine, "По линии"),
    };

    public static IReadOnlyList<Choice<CutDirection>> CutDirections { get; } = new[]
    {
        new Choice<CutDirection>(CutDirection.Climb, "Попутное"),
        new Choice<CutDirection>(CutDirection.Conventional, "Встречное"),
    };

    public static IReadOnlyList<Choice<ToolKind>> ToolKinds { get; } = new[]
    {
        new Choice<ToolKind>(ToolKind.EndMill, "Концевая фреза"),
        new Choice<ToolKind>(ToolKind.BallNose, "Сферическая фреза"),
        new Choice<ToolKind>(ToolKind.Drill, "Сверло"),
        new Choice<ToolKind>(ToolKind.VBit, "Гравёр (V-образный)"),
        new Choice<ToolKind>(ToolKind.Laser, "Лазер"),
    };

    public static IReadOnlyList<Choice<GcodeDialect>> Dialects { get; } = new[]
    {
        new Choice<GcodeDialect>(GcodeDialect.Grbl, "GRBL (3018 и похожие)"),
        new Choice<GcodeDialect>(GcodeDialect.Generic, "Общий (LinuxCNC, Mach3)"),
    };

    public static IReadOnlyList<Choice<OriginAnchor>> Origins { get; } = new[]
    {
        new Choice<OriginAnchor>(OriginAnchor.LowerLeft, "Левый нижний угол"),
        new Choice<OriginAnchor>(OriginAnchor.LowerRight, "Правый нижний угол"),
        new Choice<OriginAnchor>(OriginAnchor.UpperLeft, "Левый верхний угол"),
        new Choice<OriginAnchor>(OriginAnchor.UpperRight, "Правый верхний угол"),
        new Choice<OriginAnchor>(OriginAnchor.Center, "Центр"),
        new Choice<OriginAnchor>(OriginAnchor.Drawing, "Как в чертеже"),
    };

    public static IReadOnlyList<Choice<bool>> ZeroLevels { get; } = new[]
    {
        new Choice<bool>(false, "Верх заготовки"),
        new Choice<bool>(true, "Стол (низ заготовки)"),
    };

    public static IReadOnlyList<Choice<EntryMode>> EntryModes { get; } = new[]
    {
        new Choice<EntryMode>(EntryMode.Ramp, "Рампой (зигзаг)"),
        new Choice<EntryMode>(EntryMode.Helix, "По спирали"),
        new Choice<EntryMode>(EntryMode.Plunge, "Вертикально"),
    };

    public static IReadOnlyList<Choice<LeadMode>> LeadModes { get; } = new[]
    {
        new Choice<LeadMode>(LeadMode.None, "Нет"),
        new Choice<LeadMode>(LeadMode.Arc, "По дуге"),
    };

    public static IReadOnlyList<Choice<TextAlignment>> TextAlignments { get; } = new[]
    {
        new Choice<TextAlignment>(TextAlignment.Left, "Влево"),
        new Choice<TextAlignment>(TextAlignment.Center, "По центру"),
        new Choice<TextAlignment>(TextAlignment.Right, "Вправо"),
    };

    public static IReadOnlyList<Choice<LaserVectorMode>> LaserVectorModes { get; } = new[]
    {
        new Choice<LaserVectorMode>(LaserVectorMode.Line, "По линии (резка, обводка)"),
        new Choice<LaserVectorMode>(LaserVectorMode.Fill, "Заливка штриховкой"),
        new Choice<LaserVectorMode>(LaserVectorMode.FillAndLine, "Заливка и обводка"),
    };

    public static IReadOnlyList<Choice<RasterMode>> RasterModes { get; } = new[]
    {
        new Choice<RasterMode>(RasterMode.Grayscale, "Оттенки серого (мощность по яркости)"),
        new Choice<RasterMode>(RasterMode.Dither, "Дизеринг (точки)"),
        new Choice<RasterMode>(RasterMode.Threshold, "Чёрно-белое (порог)"),
    };

    public static IReadOnlyList<Choice<RasterAxis>> RasterAxes { get; } = new[]
    {
        new Choice<RasterAxis>(RasterAxis.X, "Вдоль X"),
        new Choice<RasterAxis>(RasterAxis.Y, "Вдоль Y"),
    };
}
