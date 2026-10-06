using CommunityToolkit.Mvvm.ComponentModel;
using PathForge.Core.Localization;
using PathForge.Core.Machining;
using PathForge.Core.Projects;

namespace PathForge.App.ViewModels;

/// <summary>A value with a display name in the current interface language (combo box item).</summary>
public sealed class Choice<T> : ObservableObject
{
    private readonly Func<string> _label;

    public Choice(T value, string russian, string english)
        : this(value, () => Loc.T(russian, english))
    {
    }

    public Choice(T value, Func<string> label)
    {
        Value = value;
        _label = label;
        // Choices live as long as the application, so the static event keeps nothing alive needlessly.
        Loc.LanguageChanged += () => OnPropertyChanged(nameof(Label));
    }

    public T Value { get; }

    public string Label => _label();

    public override string ToString() => Label;
}

/// <summary>Display names for enum values used in combo boxes.</summary>
public static class Choices
{
    public static IReadOnlyList<Choice<ProfileSide>> ProfileSides { get; } = new[]
    {
        new Choice<ProfileSide>(ProfileSide.Outside, "Снаружи", "Outside"),
        new Choice<ProfileSide>(ProfileSide.Inside, "Внутри", "Inside"),
        new Choice<ProfileSide>(ProfileSide.OnLine, "По линии", "On the line"),
    };

    public static IReadOnlyList<Choice<CutDirection>> CutDirections { get; } = new[]
    {
        new Choice<CutDirection>(CutDirection.Climb, "Попутное", "Climb"),
        new Choice<CutDirection>(CutDirection.Conventional, "Встречное", "Conventional"),
    };

    public static IReadOnlyList<Choice<ToolKind>> ToolKinds { get; } = new[]
    {
        new Choice<ToolKind>(ToolKind.EndMill, "Концевая фреза", "End mill"),
        new Choice<ToolKind>(ToolKind.BallNose, "Сферическая фреза", "Ball nose"),
        new Choice<ToolKind>(ToolKind.Drill, "Сверло", "Drill"),
        new Choice<ToolKind>(ToolKind.VBit, "Гравёр (V-образный)", "V-bit (engraver)"),
        new Choice<ToolKind>(ToolKind.Laser, "Лазер", "Laser"),
    };

    public static IReadOnlyList<Choice<GcodeDialect>> Dialects { get; } = new[]
    {
        new Choice<GcodeDialect>(GcodeDialect.Grbl, "GRBL (3018 и похожие)", "GRBL (3018 and similar)"),
        new Choice<GcodeDialect>(GcodeDialect.Generic, "Общий (LinuxCNC, Mach3)", "Generic (LinuxCNC, Mach3)"),
    };

    public static IReadOnlyList<Choice<OriginAnchor>> Origins { get; } = new[]
    {
        new Choice<OriginAnchor>(OriginAnchor.LowerLeft, "Левый нижний угол", "Lower left corner"),
        new Choice<OriginAnchor>(OriginAnchor.LowerRight, "Правый нижний угол", "Lower right corner"),
        new Choice<OriginAnchor>(OriginAnchor.UpperLeft, "Левый верхний угол", "Upper left corner"),
        new Choice<OriginAnchor>(OriginAnchor.UpperRight, "Правый верхний угол", "Upper right corner"),
        new Choice<OriginAnchor>(OriginAnchor.Center, "Центр", "Centre"),
        new Choice<OriginAnchor>(OriginAnchor.Drawing, "Как в чертеже", "As in the drawing"),
    };

    public static IReadOnlyList<Choice<bool>> ZeroLevels { get; } = new[]
    {
        new Choice<bool>(false, "Верх заготовки", "Stock top"),
        new Choice<bool>(true, "Стол (низ заготовки)", "Table (stock bottom)"),
    };

    public static IReadOnlyList<Choice<EntryMode>> EntryModes { get; } = new[]
    {
        new Choice<EntryMode>(EntryMode.Ramp, "Рампой (зигзаг)", "Ramp (zigzag)"),
        new Choice<EntryMode>(EntryMode.Helix, "По спирали", "Helix"),
        new Choice<EntryMode>(EntryMode.Plunge, "Вертикально", "Straight plunge"),
    };

    public static IReadOnlyList<Choice<LeadMode>> LeadModes { get; } = new[]
    {
        new Choice<LeadMode>(LeadMode.None, "Нет", "None"),
        new Choice<LeadMode>(LeadMode.Arc, "По дуге", "Arc"),
    };

    public static IReadOnlyList<Choice<TextAlignment>> TextAlignments { get; } = new[]
    {
        new Choice<TextAlignment>(TextAlignment.Left, "Влево", "Left"),
        new Choice<TextAlignment>(TextAlignment.Center, "По центру", "Centre"),
        new Choice<TextAlignment>(TextAlignment.Right, "Вправо", "Right"),
    };

    public static IReadOnlyList<Choice<LaserVectorMode>> LaserVectorModes { get; } = new[]
    {
        new Choice<LaserVectorMode>(LaserVectorMode.Line, "По линии (резка, обводка)", "On the line (cut, outline)"),
        new Choice<LaserVectorMode>(LaserVectorMode.Fill, "Заливка штриховкой", "Hatch fill"),
        new Choice<LaserVectorMode>(LaserVectorMode.FillAndLine, "Заливка и обводка", "Fill and outline"),
    };

    public static IReadOnlyList<Choice<KerfCompensation>> KerfCompensations { get; } = new[]
    {
        new Choice<KerfCompensation>(KerfCompensation.None, "Нет — точно по линии", "None — exactly on the line"),
        new Choice<KerfCompensation>(KerfCompensation.Parts, "Детали в размер", "Parts to size"),
        new Choice<KerfCompensation>(KerfCompensation.Openings, "Отверстия в размер", "Openings to size"),
    };

    public static IReadOnlyList<Choice<RasterModulation>> RasterModulations { get; } = new[]
    {
        new Choice<RasterModulation>(RasterModulation.Power, "Мощностью", "By power"),
        new Choice<RasterModulation>(RasterModulation.Speed, "Скоростью", "By speed"),
    };

    public static IReadOnlyList<Choice<RasterMode>> RasterModes { get; } = new[]
    {
        new Choice<RasterMode>(RasterMode.Grayscale, "Оттенки серого (мощность по яркости)", "Grayscale (power by brightness)"),
        new Choice<RasterMode>(RasterMode.Dither, "Дизеринг (точки)", "Dithering (dots)"),
        new Choice<RasterMode>(RasterMode.Threshold, "Чёрно-белое (порог)", "Black and white (threshold)"),
    };

    public static IReadOnlyList<Choice<ReliefFinishing>> ReliefFinishings { get; } = new[]
    {
        new Choice<ReliefFinishing>(ReliefFinishing.Parallel, "Строками", "Lines"),
        new Choice<ReliefFinishing>(ReliefFinishing.Waterline, "По уровням (waterline)", "Levels (waterline)"),
        new Choice<ReliefFinishing>(ReliefFinishing.ParallelAndWaterline, "Строками + по уровням", "Lines + levels"),
    };

    public static IReadOnlyList<Choice<RasterAxis>> RasterAxes { get; } = new[]
    {
        new Choice<RasterAxis>(RasterAxis.X, "Вдоль X", "Along X"),
        new Choice<RasterAxis>(RasterAxis.Y, "Вдоль Y", "Along Y"),
    };
}
