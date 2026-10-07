using System.Globalization;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>Parameters of a focus test: a row of lines, each burned with the laser at another height.</summary>
public sealed class LaserFocusTestSettings
{
    /// <summary>Focus height of the bottom line (mm, relative to the current Z0).</summary>
    public double ZFrom { get; set; } = -2;

    /// <summary>Focus height of the top line (mm).</summary>
    public double ZTo { get; set; } = 2;

    /// <summary>Number of lines.</summary>
    public int Steps { get; set; } = 9;

    /// <summary>Length of each line (mm).</summary>
    public double LineLength { get; set; } = 20;

    /// <summary>Distance between neighbouring lines (mm).</summary>
    public double Spacing { get; set; } = 4;

    public double PowerPercent { get; set; } = 30;

    /// <summary>Speed of the lines (mm/min): fast enough that the beam only marks the surface.</summary>
    public double Speed { get; set; } = 1000;

    /// <summary>Lower-left corner of the test in drawing coordinates.</summary>
    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>
/// Contours and operations of a focus test: horizontal lines, the bottom one burned at <see cref="LaserFocusTestSettings.ZFrom"/>,
/// the top one at <see cref="LaserFocusTestSettings.ZTo"/>, each labelled with its Z on the left. The thinnest line
/// shows the height where the beam is focused.
/// </summary>
public sealed record LaserFocusTest(List<Contour> Contours, List<LaserVectorOperation> Operations)
{
    /// <summary>Layer of the test's contours.</summary>
    public const string Layer = "Focus test";

    /// <param name="firstContourId">Id for the first new contour; the others follow.</param>
    public static LaserFocusTest Build(LaserFocusTestSettings settings, string toolId, int firstContourId)
    {
        var steps = Math.Clamp(settings.Steps, 2, 30);
        var spacing = Math.Max(1, settings.Spacing);
        var length = Math.Max(2, settings.LineLength);
        var labelHeight = Math.Clamp(spacing * 0.6, 1.5, 4);
        var texts = Enumerable.Range(0, steps).Select(i => Label(Z(settings, i, steps))).ToList();
        var labelWidth = texts.Max(t => SegmentDigits.Width(t, labelHeight));
        var gap = labelHeight;
        var left = settings.X + labelWidth + gap;

        var contours = new List<Contour>();
        var operations = new List<LaserVectorOperation>();
        var labels = new List<Contour>();
        var nextId = firstContourId;
        for (var i = 0; i < steps; i++)
        {
            var z = Z(settings, i, steps);
            var y = settings.Y + labelHeight / 2 + i * spacing;
            var line = new Contour(nextId++, new Segment[] { new LineSegment(new Vec2(left, y), new Vec2(left + length, y)) }, Layer);
            contours.Add(line);
            operations.Add(new LaserVectorOperation
            {
                Name = Loc.T($"Фокус: Z {texts[i]} мм", $"Focus: Z {texts[i]} mm"),
                Mode = LaserVectorMode.Line,
                PowerPercent = Math.Clamp(settings.PowerPercent, 0, 100),
                Speed = Math.Max(1, settings.Speed),
                StartZ = z,
                ToolId = toolId,
                ContourIds = new List<int> { line.Id },
            });
            labels.AddRange(SegmentDigits.Text(texts[i], new Vec2(left - gap - SegmentDigits.Width(texts[i], labelHeight), y - labelHeight / 2), labelHeight));
        }

        foreach (var label in labels)
        {
            label.Id = nextId++;
            label.Layer = Layer;
        }

        contours.AddRange(labels);
        // The labels are burned in focus (Z0), with the power and speed of the lines.
        operations.Add(new LaserVectorOperation
        {
            Name = Loc.T("Фокус: подписи (Z, мм)", "Focus: labels (Z, mm)"),
            Mode = LaserVectorMode.Line,
            PowerPercent = Math.Clamp(settings.PowerPercent, 0, 100),
            Speed = Math.Max(1, settings.Speed),
            ToolId = toolId,
            ContourIds = labels.Select(l => l.Id).ToList(),
        });

        return new LaserFocusTest(contours, operations);
    }

    private static double Z(LaserFocusTestSettings settings, int index, int count) =>
        Math.Round(settings.ZFrom + (settings.ZTo - settings.ZFrom) * index / (count - 1), 2);

    private static string Label(double z) => z.ToString("0.##", CultureInfo.InvariantCulture);
}
