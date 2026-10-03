using System.Globalization;
using System.Text.RegularExpressions;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;
using PathForge.Core.Machining;

namespace PathForge.Core.Import.Pcb;

public sealed record DrillHole(Vec2 Center, double Diameter);

/// <summary>Slot (oblong hole) of the drill width along a path: a drilled slot (G85) or a routed one (M15…M16).</summary>
public sealed record DrillSlot(IReadOnlyList<Vec2> Path, double Diameter);

public sealed class ExcellonResult
{
    public List<DrillHole> Holes { get; } = new();

    public List<DrillSlot> Slots { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Holes as circle contours, one layer per diameter (e.g. "Сверловка Ø0.8"), and slots as their outlines
    /// (layer "Пазы Ø…"): slots are milled with a "Profile → Inside" operation and a smaller end mill.
    /// </summary>
    public List<Contour> ToContours()
    {
        var contours = Holes.Select(h => new Contour(0, new Segment[] { new ArcSegment(h.Center, h.Diameter / 2, 0, 2 * Math.PI) },
                Loc.T("Сверловка", "Drills") + string.Create(CultureInfo.InvariantCulture, $" Ø{h.Diameter:0.###}")))
            .ToList();

        foreach (var slot in Slots)
        {
            var layer = Loc.T("Пазы", "Slots") + string.Create(CultureInfo.InvariantCulture, $" Ø{slot.Diameter:0.###}");
            contours.AddRange(SlotOutlines(slot).Select(segments => new Contour(0, segments, layer)));
        }

        return contours;
    }

    /// <summary>Outline of a slot: an exact oblong for a straight slot, a polygon for a routed path with corners.</summary>
    private static IEnumerable<List<Segment>> SlotOutlines(DrillSlot slot)
    {
        var radius = slot.Diameter / 2;
        var path = slot.Path.Where((p, i) => i == 0 || !p.IsNear(slot.Path[i - 1], 1e-9)).ToList();
        if (path.Count == 1)
        {
            yield return new List<Segment> { new ArcSegment(path[0], radius, 0, 2 * Math.PI) };
            yield break;
        }

        if (path.Count == 2)
        {
            // Counter-clockwise: right side forward, around the end, left side back, around the start.
            var (a, b) = (path[0], path[1]);
            var n = (b - a).Normalized().PerpendicularLeft * radius;
            var angle = Math.Atan2(-n.Y, -n.X);
            yield return new List<Segment>
            {
                new LineSegment(a - n, b - n),
                new ArcSegment(b, radius, angle, Math.PI),
                new LineSegment(b + n, a + n),
                new ArcSegment(a, radius, angle + Math.PI, Math.PI),
            };
            yield break;
        }

        foreach (var ring in ClipperBridge.FromPaths(ClipperBridge.Stroke(path, radius)))
        {
            yield return ring.Select((p, k) => (Segment)new LineSegment(p, ring[(k + 1) % ring.Count])).ToList();
        }
    }
}

/// <summary>
/// Reader for Excellon drill files as written by KiCad, Eagle, EasyEDA, Altium Designer and others:
/// metric or inch, decimal or implicit coordinates (LZ/TZ, the ";FILE_FORMAT=2:5" comment of Altium),
/// tools in the header or body, drilled slots (G85) and routed slots (G00 … M15 G01 … M16).
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
        var explicitFormat = false; // digits given by ";FILE_FORMAT=" (Altium) or "METRIC,LZ,000.000"
        var leadingZerosKept = false; // LZ: leading zeros present, trailing omitted
        var incremental = false;
        int? tool = null;
        var position = Vec2.Zero;
        List<Vec2>? route = null; // routed slot while the tool is down (M15 … M16)

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith(';'))
            {
                // Altium writes the number of integer and decimal digits as a comment: ";FILE_FORMAT=2:5".
                var fileFormat = FileFormatRegex().Match(line);
                if (fileFormat.Success)
                {
                    integerDigits = int.Parse(fileFormat.Groups[1].Value, CultureInfo.InvariantCulture);
                    decimalDigits = int.Parse(fileFormat.Groups[2].Value, CultureInfo.InvariantCulture);
                    explicitFormat = true;
                }

                continue;
            }

            if (line.StartsWith("METRIC", StringComparison.Ordinal) || line.StartsWith("INCH", StringComparison.Ordinal))
            {
                var metric = line.StartsWith("METRIC", StringComparison.Ordinal);
                scale = metric ? 1 : 25.4;
                if (!explicitFormat)
                {
                    integerDigits = metric ? 3 : 2;
                    decimalDigits = metric ? 3 : 4;
                }

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
                    explicitFormat = true;
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
                case "M15":
                    // Rout mode: the tool goes down at the current position.
                    route = new List<Vec2> { position };
                    continue;
                case "M16":
                case "M17":
                    FinishRoute();
                    continue;
                case "M30":
                case "M00":
                    FinishRoute();
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

            // Rout mode moves: G00 travels with the tool up, G01 cuts while the tool is down (after M15).
            var rout = RoutRegex().Match(line);
            if (rout.Success)
            {
                position = Point(rout.Groups[2].Value);
                if (rout.Groups[1].Value == "0")
                {
                    FinishRoute();
                }
                else
                {
                    route?.Add(position);
                }

                continue;
            }

            if (line[0] is not ('X' or 'Y'))
            {
                continue;
            }

            var slotAt = line.IndexOf("G85", StringComparison.Ordinal);
            if (slotAt >= 0)
            {
                // Drilled slot: "X..Y..G85X..Y..", the second point inherits missing axes from the first.
                var start = Point(line[..slotAt]);
                var end = Point(line[(slotAt + 3)..]);
                position = end;
                if (Diameter() is double width)
                {
                    result.Slots.Add(new DrillSlot(new[] { start, end }, width));
                }

                continue;
            }

            position = Point(line);
            if (Diameter() is double diameter)
            {
                result.Holes.Add(new DrillHole(position, diameter));
            }
        }

        FinishRoute();
        return Finish(result);

        Vec2 Point(string part)
        {
            var hit = PointRegex().Match(part);
            var x = hit.Groups[1].Success ? Coordinate(hit.Groups[1].Value) : (incremental ? 0 : position.X);
            var y = hit.Groups[2].Success ? Coordinate(hit.Groups[2].Value) : (incremental ? 0 : position.Y);
            return incremental ? position + new Vec2(x, y) : new Vec2(x, y);
        }

        double? Diameter()
        {
            if (tool is int t && tools.TryGetValue(t, out var diameter))
            {
                return diameter;
            }

            result.Warnings.Add(Loc.T($"Отверстие {position} без известного сверла пропущено.", $"Hole {position} without a known drill was skipped."));
            return null;
        }

        void FinishRoute()
        {
            if (route is { Count: >= 2 } && Diameter() is double width)
            {
                result.Slots.Add(new DrillSlot(route, width));
            }

            route = null;
        }

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
        if (result.Holes.Count == 0 && result.Slots.Count == 0)
        {
            result.Warnings.Add(Loc.T("В файле не найдено отверстий.", "No holes found in the file."));
        }

        return result;
    }

    [GeneratedRegex(@"(0+)\.(0+)")]
    private static partial Regex FormatRegex();

    [GeneratedRegex(@"FILE_FORMAT\s*=\s*(\d+)\s*:\s*(\d+)")]
    private static partial Regex FileFormatRegex();

    [GeneratedRegex(@"^T(\d+)(?:[FSB][\d.]*)*(?:C([\d.]+))?")]
    private static partial Regex ToolRegex();

    [GeneratedRegex(@"^G0?([01])((?:[XY][+-]?[\d.]+)+)")]
    private static partial Regex RoutRegex();

    [GeneratedRegex(@"^(?:X([+-]?[\d.]+))?(?:Y([+-]?[\d.]+))?")]
    private static partial Regex PointRegex();
}
