using System.Globalization;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>Parameters of a laser test card: squares burned with every combination of power and speed.</summary>
public sealed class LaserTestGridSettings
{
    public double PowerMinPercent { get; set; } = 20;

    public double PowerMaxPercent { get; set; } = 100;

    /// <summary>Rows: power grows from the bottom row to the top one.</summary>
    public int PowerSteps { get; set; } = 5;

    public double SpeedMin { get; set; } = 300;

    public double SpeedMax { get; set; } = 3000;

    /// <summary>Columns: speed grows from left to right.</summary>
    public int SpeedSteps { get; set; } = 5;

    /// <summary>Side of one square (mm).</summary>
    public double CellSize { get; set; } = 8;

    public double Gap { get; set; } = 3;

    /// <summary>Fill the squares (engraving test) or burn their outline (cutting test).</summary>
    public LaserVectorMode Mode { get; set; } = LaserVectorMode.Fill;

    public double FillSpacing { get; set; } = 0.1;

    /// <summary>Lower-left corner of the card in drawing coordinates.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Burn the power and speed values next to the rows and columns.</summary>
    public bool Labels { get; set; } = true;
}

/// <summary>Contours and laser operations of a test card, ready to be added to a project.</summary>
public sealed record LaserTestGrid(List<Contour> Contours, List<LaserVectorOperation> Operations)
{
    /// <summary>Layer of the card's contours.</summary>
    public const string Layer = "Laser test";

    /// <param name="firstContourId">Id for the first new contour; the others follow.</param>
    public static LaserTestGrid Build(LaserTestGridSettings settings, string toolId, int firstContourId)
    {
        var rows = Math.Clamp(settings.PowerSteps, 1, 20);
        var columns = Math.Clamp(settings.SpeedSteps, 1, 20);
        var cell = Math.Max(1, settings.CellSize);
        var gap = Math.Max(0, settings.Gap);
        var labelHeight = Math.Clamp(cell * 0.3, 1.5, 4);
        // Room for the power labels on the left and the speed labels below.
        var left = settings.X + (settings.Labels ? 4 * labelHeight + gap : 0);
        var bottom = settings.Y + (settings.Labels ? labelHeight + gap : 0);

        var contours = new List<Contour>();
        var operations = new List<LaserVectorOperation>();
        var nextId = firstContourId;

        for (var r = 0; r < rows; r++)
        {
            var power = Step(settings.PowerMinPercent, settings.PowerMaxPercent, r, rows);
            for (var c = 0; c < columns; c++)
            {
                var speed = Math.Round(Step(settings.SpeedMin, settings.SpeedMax, c, columns));
                var x = left + c * (cell + gap);
                var y = bottom + r * (cell + gap);
                var square = Rectangle(nextId++, x, y, x + cell, y + cell);
                contours.Add(square);
                operations.Add(new LaserVectorOperation
                {
                    Name = Loc.T($"Тест: {power:0} % · {speed:0} мм/мин", $"Test: {power:0} % · {speed:0} mm/min"),
                    Mode = settings.Mode,
                    PowerPercent = Math.Round(power, 1),
                    Speed = speed,
                    FillSpacing = settings.FillSpacing,
                    // Air assist for the cutting test (as when cutting), none for the engraving test.
                    AirAssist = settings.Mode == LaserVectorMode.Line,
                    ToolId = toolId,
                    ContourIds = new List<int> { square.Id },
                });
            }
        }

        if (settings.Labels)
        {
            var labels = new List<Contour>();
            for (var r = 0; r < rows; r++)
            {
                var text = Step(settings.PowerMinPercent, settings.PowerMaxPercent, r, rows).ToString("0", CultureInfo.InvariantCulture);
                var y = bottom + r * (cell + gap) + (cell - labelHeight) / 2;
                labels.AddRange(SegmentDigits.Text(text, new Vec2(left - gap - SegmentDigits.Width(text, labelHeight), y), labelHeight));
            }

            for (var c = 0; c < columns; c++)
            {
                var text = Math.Round(Step(settings.SpeedMin, settings.SpeedMax, c, columns)).ToString("0", CultureInfo.InvariantCulture);
                var x = left + c * (cell + gap) + (cell - SegmentDigits.Width(text, labelHeight)) / 2;
                labels.AddRange(SegmentDigits.Text(text, new Vec2(x, bottom - gap - labelHeight), labelHeight));
            }

            foreach (var label in labels)
            {
                label.Id = nextId++;
                label.Layer = Layer;
            }

            contours.AddRange(labels);
            // Labels are lines, burned with the middle power at the middle speed: readable on most materials.
            operations.Add(new LaserVectorOperation
            {
                Name = Loc.T("Тест: подписи (мощность, %; скорость, мм/мин)", "Test: labels (power, %; speed, mm/min)"),
                Mode = LaserVectorMode.Line,
                PowerPercent = Math.Round((settings.PowerMinPercent + settings.PowerMaxPercent) / 2, 1),
                Speed = Math.Round((settings.SpeedMin + settings.SpeedMax) / 2),
                ToolId = toolId,
                ContourIds = labels.Select(l => l.Id).ToList(),
            });
        }

        foreach (var contour in contours)
        {
            contour.Layer = Layer;
        }

        return new LaserTestGrid(contours, operations);
    }

    private static double Step(double from, double to, int index, int count) =>
        count == 1 ? to : from + (to - from) * index / (count - 1);

    private static Contour Rectangle(int id, double x0, double y0, double x1, double y1) => new(id, new Segment[]
    {
        new LineSegment(new Vec2(x0, y0), new Vec2(x1, y0)),
        new LineSegment(new Vec2(x1, y0), new Vec2(x1, y1)),
        new LineSegment(new Vec2(x1, y1), new Vec2(x0, y1)),
        new LineSegment(new Vec2(x0, y1), new Vec2(x0, y0)),
    }, Layer);
}

/// <summary>
/// Digits drawn like a seven-segment display: enough for test card labels without a font. A minus is the middle
/// segment, a decimal point a short tick at the bottom.
/// </summary>
internal static class SegmentDigits
{
    // Segments a…g as in a display: a top, b top right, c bottom right, d bottom, e bottom left, f top left, g middle.
    private static readonly string[] Patterns =
    {
        "abcdef", "bc", "abged", "abgcd", "fgbc", "afgcd", "afgedc", "abc", "abcdefg", "abcdfg",
    };

    public static double Width(string text, double height) => text.Length * height * 0.8 - height * 0.3;

    public static IEnumerable<Contour> Text(string text, Vec2 at, double height)
    {
        var w = height * 0.5;
        var x = at.X;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
            {
                foreach (var contour in Digit(ch - '0', new Vec2(x, at.Y), w, height))
                {
                    yield return contour;
                }
            }
            else if (ch == '-')
            {
                yield return new Contour(0, new Segment[] { new LineSegment(new Vec2(x, at.Y + height / 2), new Vec2(x + w, at.Y + height / 2)) });
            }
            else if (ch == '.')
            {
                yield return new Contour(0, new Segment[] { new LineSegment(new Vec2(x + w / 2, at.Y), new Vec2(x + w / 2, at.Y + height * 0.12)) });
            }

            x += height * 0.8;
        }
    }

    private static IEnumerable<Contour> Digit(int digit, Vec2 o, double w, double h)
    {
        Vec2 P(double fx, double fy) => new(o.X + fx * w, o.Y + fy * h);
        foreach (var s in Patterns[digit])
        {
            var (a, b) = s switch
            {
                'a' => (P(0, 1), P(1, 1)),
                'b' => (P(1, 1), P(1, 0.5)),
                'c' => (P(1, 0.5), P(1, 0)),
                'd' => (P(1, 0), P(0, 0)),
                'e' => (P(0, 0), P(0, 0.5)),
                'f' => (P(0, 0.5), P(0, 1)),
                _ => (P(0, 0.5), P(1, 0.5)),
            };
            yield return new Contour(0, new Segment[] { new LineSegment(a, b) });
        }
    }
}
