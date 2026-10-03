using System.Globalization;
using System.Text.RegularExpressions;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Import.Pcb;

public sealed record DrillHole(Vec2 Center, double Diameter);

public sealed class ExcellonResult
{
    public List<DrillHole> Holes { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>Holes as circle contours, one layer per diameter (e.g. "Сверловка Ø0.8").</summary>
    public List<Contour> ToContours()
    {
        return Holes.Select(h => new Contour(0, new Segment[] { new ArcSegment(h.Center, h.Diameter / 2, 0, 2 * Math.PI) },
                Loc.T("Сверловка", "Drills") + string.Create(CultureInfo.InvariantCulture, $" Ø{h.Diameter:0.###}")))
            .ToList();
    }
}

/// <summary>
/// Reader for Excellon drill files as written by KiCad, Eagle, EasyEDA and others:
/// metric or inch, decimal or implicit coordinates (LZ/TZ), tools in the header or body.
/// Written from the public format description.
/// </summary>
public static partial class ExcellonReader
{
    public static ExcellonResult ReadFile(string path) => Read(File.ReadAllText(path));

    public static ExcellonResult Read(string text)
    {
        var result = new ExcellonResult();
        var tools = new Dictionary<int, double>();
        var scale = 1.0; // mm per unit
        var integerDigits = 3;
        var decimalDigits = 3;
        var leadingZerosKept = false; // LZ: leading zeros present, trailing omitted
        var incremental = false;
        int? tool = null;
        var position = Vec2.Zero;
        var slotWarned = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith("METRIC", StringComparison.Ordinal) || line.StartsWith("INCH", StringComparison.Ordinal))
            {
                var metric = line.StartsWith("METRIC", StringComparison.Ordinal);
                scale = metric ? 1 : 25.4;
                integerDigits = metric ? 3 : 2;
                decimalDigits = metric ? 3 : 4;
                if (line.Contains("LZ", StringComparison.Ordinal))
                {
                    leadingZerosKept = true;
                }
                else if (line.Contains("TZ", StringComparison.Ordinal))
                {
                    leadingZerosKept = false;
                }

                // Optional explicit format such as "METRIC,LZ,000.000".
                var format = FormatRegex().Match(line);
                if (format.Success)
                {
                    integerDigits = format.Groups[1].Value.Length;
                    decimalDigits = format.Groups[2].Value.Length;
                }

                continue;
            }

            switch (line)
            {
                case "M71":
                    scale = 1;
                    continue;
                case "M72":
                    scale = 25.4;
                    continue;
                case "G90":
                    incremental = false;
                    continue;
                case "G91":
                case "ICI,ON":
                    incremental = true;
                    continue;
                case "M30":
                case "M00":
                    return Finish(result);
            }

            var toolDefinition = ToolRegex().Match(line);
            if (toolDefinition.Success)
            {
                var number = int.Parse(toolDefinition.Groups[1].Value, CultureInfo.InvariantCulture);
                if (toolDefinition.Groups[2].Success)
                {
                    tools[number] = double.Parse(toolDefinition.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture) * scale;
                }

                // "T1" selects; "T1C0.8" defines (header) and some generators also use it to select in the body.
                tool = number == 0 ? null : number;
                continue;
            }

            if (line.Contains("G85", StringComparison.Ordinal))
            {
                if (!slotWarned)
                {
                    result.Warnings.Add(Loc.T("Пазы (G85) пока не поддерживаются: сверлятся только их концы.", "Slots (G85) are not supported yet: only their ends are drilled."));
                    slotWarned = true;
                }
            }

            if (line[0] is not ('X' or 'Y'))
            {
                continue;
            }

            foreach (var part in line.Split("G85"))
            {
                var hit = PointRegex().Match(part);
                if (!hit.Success || hit.Length == 0)
                {
                    continue;
                }

                var x = hit.Groups[1].Success ? Coordinate(hit.Groups[1].Value) : (incremental ? 0 : position.X);
                var y = hit.Groups[2].Success ? Coordinate(hit.Groups[2].Value) : (incremental ? 0 : position.Y);
                position = incremental ? position + new Vec2(x, y) : new Vec2(x, y);
                if (tool is int t && tools.TryGetValue(t, out var diameter))
                {
                    result.Holes.Add(new DrillHole(position, diameter));
                }
                else
                {
                    result.Warnings.Add(Loc.T($"Отверстие {position} без известного сверла пропущено.", $"Hole {position} without a known drill was skipped."));
                }
            }
        }

        return Finish(result);

        double Coordinate(string value)
        {
            if (value.Contains('.'))
            {
                return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) * scale;
            }

            var negative = value.StartsWith('-');
            var digits = value.TrimStart('+', '-');
            if (leadingZerosKept)
            {
                // Trailing zeros were omitted: the number is left-aligned.
                digits = digits.PadRight(integerDigits + decimalDigits, '0');
            }

            var number = long.Parse(digits, CultureInfo.InvariantCulture) / Math.Pow(10, decimalDigits);
            return (negative ? -number : number) * scale;
        }
    }

    private static ExcellonResult Finish(ExcellonResult result)
    {
        if (result.Holes.Count == 0)
        {
            result.Warnings.Add(Loc.T("В файле не найдено отверстий.", "No holes found in the file."));
        }

        return result;
    }

    [GeneratedRegex(@"(0+)\.(0+)")]
    private static partial Regex FormatRegex();

    [GeneratedRegex(@"^T(\d+)(?:[FSB][\d.]*)*(?:C([\d.]+))?")]
    private static partial Regex ToolRegex();

    [GeneratedRegex(@"^(?:X([+-]?[\d.]+))?(?:Y([+-]?[\d.]+))?")]
    private static partial Regex PointRegex();
}
