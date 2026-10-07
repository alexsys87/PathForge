using System.Globalization;
using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>One drill and the holes (and slots) it drills.</summary>
/// <param name="Diameters">Hole diameters of the group, smallest first.</param>
public sealed record DrillGroup(Tool Drill, List<double> Diameters, List<int> ContourIds);

/// <summary>Drill operations planned for a set of holes, one per drill, and what the user should know.</summary>
public sealed record DrillPlan(List<DrillGroup> Groups, List<DrillOperation> Operations, List<string> Warnings);

/// <summary>
/// Drilling by diameter: holes (circles) and drilled slots are grouped by size, every size gets the drill with the
/// nearest diameter among the tools, and one drilling operation is made per drill, from the thinnest drill to the
/// thickest. The G-code pauses for the drill change between them.
/// </summary>
public static class DrillPlanner
{
    /// <summary>A drill this much off the hole size (mm) is reported.</summary>
    public const double Tolerance = 0.1;

    /// <summary>Holes this much larger than the thickest drill (mm) are left out: they are milled, not drilled.</summary>
    public const double MillAbove = 0.5;

    public static DrillPlan Plan(IEnumerable<Contour> contours, IEnumerable<Tool> tools, double depth, double peckDepth = 0)
    {
        var warnings = new List<string>();
        var drills = tools.Where(t => t.Kind == ToolKind.Drill && t.Diameter > 0).OrderBy(t => t.Diameter).ToList();
        var holes = new List<(int Id, double Diameter)>();
        var other = 0;
        foreach (var contour in contours)
        {
            if (contour.TryGetCircle(out _, out var radius))
            {
                holes.Add((contour.Id, Math.Round(radius * 2, 3)));
            }
            else if (contour.TryGetSlot(out _, out _, out var width))
            {
                holes.Add((contour.Id, Math.Round(width, 3)));
            }
            else
            {
                other++;
            }
        }

        if (other > 0)
        {
            warnings.Add(Loc.T($"Сверловка: {other} контуров — не окружности и не пазы, пропущены.", $"Drilling: {other} contours are neither circles nor slots and were skipped."));
        }

        if (holes.Count == 0)
        {
            warnings.Add(Loc.T("Сверловка: среди выбранных контуров нет отверстий.", "Drilling: there are no holes among the selected contours."));
            return new DrillPlan(new List<DrillGroup>(), new List<DrillOperation>(), warnings);
        }

        if (drills.Count == 0)
        {
            warnings.Add(Loc.T(
                "Сверловка: в инструментах нет свёрл — добавьте их («Инструменты» → пресеты «Текстолит (платы): Сверло…»).",
                "Drilling: there are no drills among the tools — add them (“Tools” → presets “PCB (FR4): Drill…”)."));
            return new DrillPlan(new List<DrillGroup>(), new List<DrillOperation>(), warnings);
        }

        var groups = new Dictionary<Tool, (SortedSet<double> Diameters, List<int> Ids)>();
        var tooLarge = new SortedSet<double>();
        foreach (var size in holes.GroupBy(h => h.Diameter).OrderBy(g => g.Key))
        {
            var diameter = size.Key;
            if (diameter > drills[^1].Diameter + MillAbove)
            {
                tooLarge.Add(diameter);
                continue;
            }

            // The nearest drill; between two equally near ones the larger (a component lead still goes in).
            var drill = drills.OrderBy(d => Math.Abs(d.Diameter - diameter)).ThenByDescending(d => d.Diameter).First();
            if (!groups.TryGetValue(drill, out var group))
            {
                groups[drill] = group = (new SortedSet<double>(), new List<int>());
            }

            group.Diameters.Add(diameter);
            group.Ids.AddRange(size.Select(h => h.Id));
            if (Math.Abs(drill.Diameter - diameter) > Tolerance + 1e-9)
            {
                warnings.Add(Loc.T(
                    $"Сверловка: отверстия Ø{F(diameter)} ({size.Count()} шт.) сверлятся сверлом Ø{F(drill.Diameter)} — нет сверла ближе. Добавьте сверло Ø{F(diameter)} в инструменты.",
                    $"Drilling: the Ø{F(diameter)} holes ({size.Count()}) are drilled with the Ø{F(drill.Diameter)} drill — there is no closer one. Add a Ø{F(diameter)} drill to the tools."));
            }
        }

        if (tooLarge.Count > 0)
        {
            var count = holes.Count(h => tooLarge.Contains(h.Diameter));
            warnings.Add(Loc.T(
                $"Сверловка: {count} отверстий больше самого толстого сверла ({string.Join(", ", tooLarge.Select(d => "Ø" + F(d)))}) пропущены — " +
                "фрезеруйте их «Контуром» внутри (кукурузой).",
                $"Drilling: {count} holes larger than the thickest drill ({string.Join(", ", tooLarge.Select(d => "Ø" + F(d)))}) were left out — " +
                "mill them with an inside Profile (corn mill)."));
        }

        var result = groups.OrderBy(g => g.Key.Diameter)
            .Select(g => new DrillGroup(g.Key, g.Value.Diameters.ToList(), g.Value.Ids.OrderBy(i => i).ToList()))
            .ToList();
        var operations = result.Select(g => new DrillOperation
        {
            Name = Loc.T($"Сверловка Ø{F(g.Drill.Diameter)}", $"Drilling Ø{F(g.Drill.Diameter)}"),
            ToolId = g.Drill.Id,
            Depth = depth,
            PeckDepth = peckDepth,
            ContourIds = g.ContourIds,
        }).ToList();
        return new DrillPlan(result, operations, warnings);
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
