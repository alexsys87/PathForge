using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>Shape family of a panel cutout.</summary>
public enum CutoutKind
{
    Circle,
    RoundedRectangle,
    /// <summary>A round hole with a flat (anti-rotation) on top.</summary>
    DHole,
    DSub,
    Xlr,
    Display,
    VentHoles,
    VentSlots,
    VentHoneycomb,
    CornerHoles,
}

/// <summary>One editable value of a cutout (label in both languages and the default).</summary>
public sealed record CutoutParameter(string NameRu, string NameEn, double Default)
{
    public string Name => Loc.T(NameRu, NameEn);
}

/// <summary>A cutout of the library: a shape family with preset values.</summary>
public sealed record PanelCutout(string NameRu, string NameEn, CutoutKind Kind, IReadOnlyList<CutoutParameter> Parameters, double Extra = 0)
{
    public string Name => Loc.T(NameRu, NameEn);

    /// <summary>Default values of the parameters.</summary>
    public double[] Defaults => Parameters.Select(p => p.Default).ToArray();
}

/// <summary>
/// Library of front panel cutouts: holes for buttons, switches and pots (with a flat), D-Sub, USB, XLR and power
/// jacks, display windows with their mounting holes, vent grilles and corner mounting holes. Sizes are typical
/// panel cutouts; check them against your part.
/// </summary>
public static class PanelCutouts
{
    public static string Layer => Loc.T("Вырезы", "Cutouts");

    private static CutoutParameter P(string ru, string en, double value) => new(ru, en, value);

    private static PanelCutout Hole(double size, string ru, string en) =>
        new($"Отверстие Ø{size:0.#} ({ru})", $"Hole Ø{size:0.#} ({en})", CutoutKind.DHole,
            new[] { P("Диаметр, мм", "Diameter, mm", size + 0.3), P("Лыска (по лыске), мм; 0 — нет", "Across the flat, mm; 0 — none", 0) });

    private static PanelCutout DSub(string name, double width, double spacing) =>
        new(name, name, CutoutKind.DSub,
            new[] { P("Ширина выреза, мм", "Cutout width, mm", width), P("Высота, мм", "Height, mm", 11), P("Между крепёжными, мм", "Mounting hole spacing, mm", spacing), P("Ø крепёжных, мм", "Mounting hole Ø, mm", 3.2) });

    private static PanelCutout Display(string ru, string en, double w, double h, double hx, double hy, double holeDiameter) =>
        new(ru, en, CutoutKind.Display,
            new[] { P("Окно, ширина", "Window width", w), P("Окно, высота", "Window height", h), P("Крепёж по X", "Holes along X", hx), P("Крепёж по Y", "Holes along Y", hy) },
            holeDiameter);

    public static IReadOnlyList<PanelCutout> All { get; } = new[]
    {
        new PanelCutout("Круглое отверстие", "Round hole", CutoutKind.Circle, new[] { P("Диаметр, мм", "Diameter, mm", 10) }),
        new PanelCutout("Прямоугольник со скруглением", "Rounded rectangle", CutoutKind.RoundedRectangle,
            new[] { P("Ширина, мм", "Width, mm", 30), P("Высота, мм", "Height, mm", 20), P("Радиус, мм", "Radius, mm", 2) }),
        new PanelCutout("Отверстие с лыской", "Hole with a flat", CutoutKind.DHole,
            new[] { P("Диаметр, мм", "Diameter, mm", 7.3), P("Лыска (по лыске), мм; 0 — нет", "Across the flat, mm; 0 — none", 6.4) }),
        Hole(7, "тумблер, потенциометр M7", "toggle, M7 pot"),
        Hole(9, "потенциометр 9 мм", "9 mm pot"),
        Hole(12, "кнопка, тумблер 12 мм", "12 mm button, toggle"),
        Hole(16, "кнопка 16 мм", "16 mm button"),
        Hole(19, "кнопка 19 мм", "19 mm button"),
        Hole(22, "кнопка 22 мм", "22 mm button"),
        DSub("D-Sub DB9 (DE-9)", 20.0, 25.0),
        DSub("D-Sub DB15 (DA-15)", 28.4, 33.3),
        DSub("D-Sub DB25", 42.3, 47.0),
        new PanelCutout("USB-A", "USB-A", CutoutKind.RoundedRectangle, new[] { P("Ширина, мм", "Width, mm", 13.5), P("Высота, мм", "Height, mm", 6), P("Радиус, мм", "Radius, mm", 0.5) }),
        new PanelCutout("USB-B", "USB-B", CutoutKind.RoundedRectangle, new[] { P("Ширина, мм", "Width, mm", 12.5), P("Высота, мм", "Height, mm", 11.5), P("Радиус, мм", "Radius, mm", 1) }),
        new PanelCutout("USB-C", "USB-C", CutoutKind.RoundedRectangle, new[] { P("Ширина, мм", "Width, mm", 9.5), P("Высота, мм", "Height, mm", 3.8), P("Радиус, мм", "Radius, mm", 1.9) }),
        new PanelCutout("XLR (Neutrik D)", "XLR (Neutrik D)", CutoutKind.Xlr, new[] { P("Диаметр, мм", "Diameter, mm", 24), P("Ø крепёжных, мм", "Mounting hole Ø, mm", 3.2) }),
        new PanelCutout("Разъём питания 5,5 мм", "Power jack 5.5 mm", CutoutKind.DHole,
            new[] { P("Диаметр, мм", "Diameter, mm", 11), P("Лыска (по лыске), мм; 0 — нет", "Across the flat, mm; 0 — none", 0) }),
        Display("Дисплей LCD 1602", "LCD 1602 display", 71.2, 24.2, 75, 31, 3.2),
        Display("Дисплей LCD 2004", "LCD 2004 display", 97, 40, 93, 55, 3.2),
        Display("Дисплей OLED 0,96″", "OLED 0.96″ display", 23, 13, 23.5, 23.8, 2.2),
        Display("Дисплей OLED 1,3″", "OLED 1.3″ display", 31, 17, 30.4, 28, 3),
        new PanelCutout("Вентиляция: отверстия", "Vent: holes", CutoutKind.VentHoles,
            new[] { P("Ширина, мм", "Width, mm", 40), P("Высота, мм", "Height, mm", 30), P("Ø отверстия, мм", "Hole Ø, mm", 4), P("Шаг, мм", "Pitch, mm", 6) }),
        new PanelCutout("Вентиляция: щели", "Vent: slots", CutoutKind.VentSlots,
            new[] { P("Ширина, мм", "Width, mm", 40), P("Высота, мм", "Height, mm", 30), P("Ширина щели, мм", "Slot width, mm", 3), P("Шаг, мм", "Pitch, mm", 6) }),
        new PanelCutout("Вентиляция: соты", "Vent: honeycomb", CutoutKind.VentHoneycomb,
            new[] { P("Ширина, мм", "Width, mm", 40), P("Высота, мм", "Height, mm", 30), P("Сота (под ключ), мм", "Cell across flats, mm", 6), P("Перемычка, мм", "Wall, mm", 1.5) }),
        new PanelCutout("Крепёжные отверстия по углам", "Corner mounting holes", CutoutKind.CornerHoles,
            new[] { P("Ширина панели, мм", "Panel width, mm", 100), P("Высота панели, мм", "Panel height, mm", 60), P("Отступ от края, мм", "From the edge, mm", 5), P("Диаметр, мм", "Diameter, mm", 3.2) }),
    };

    /// <summary>The cutout's contours, centred at <paramref name="center"/> and turned by <paramref name="angleDeg"/>.</summary>
    /// <param name="values">Parameter values (missing ones take the defaults).</param>
    public static List<Contour> Place(PanelCutout cutout, IReadOnlyList<double> values, Vec2 center, double angleDeg, int firstContourId, List<string>? warnings = null)
    {
        double V(int i) => i < values.Count ? values[i] : cutout.Parameters[i].Default;
        var shapes = cutout.Kind switch
        {
            CutoutKind.Circle => new() { Circle(Vec2.Zero, V(0)) },
            CutoutKind.RoundedRectangle => new() { RoundedRectangle(Vec2.Zero, V(0), V(1), V(2)) },
            CutoutKind.DHole => new() { DHole(V(0), V(1)) },
            CutoutKind.DSub => DSub(V(0), V(1), V(2), V(3)),
            CutoutKind.Xlr => new() { Circle(Vec2.Zero, V(0)), Circle(new Vec2(-9.5, 12), V(1)), Circle(new Vec2(9.5, -12), V(1)) },
            CutoutKind.Display => Display(V(0), V(1), V(2), V(3), cutout.Extra),
            CutoutKind.VentHoles => VentHoles(V(0), V(1), V(2), V(3)),
            CutoutKind.VentSlots => VentSlots(V(0), V(1), V(2), V(3)),
            CutoutKind.VentHoneycomb => Honeycomb(V(0), V(1), V(2), V(3)),
            CutoutKind.CornerHoles => CornerHoles(V(0), V(1), V(2), V(3)),
            _ => new List<List<Segment>>(),
        };

        if (shapes.Count == 0)
        {
            warnings?.Add(Loc.T($"{cutout.Name}: при таких размерах не получается ни одного выреза.", $"{cutout.Name}: these sizes give no cutout at all."));
        }

        var place = Affine2.Then(Affine2.Rotation(angleDeg * Math.PI / 180), Affine2.Translation(center.X, center.Y));
        var id = firstContourId;
        return shapes.Select(s => new Contour(id++, s.Select(segment => segment.Transform(place)), Layer)).ToList();
    }

    private static List<Segment> Circle(Vec2 c, double diameter) => new() { new ArcSegment(c, Math.Max(0.01, diameter) / 2, 0, 2 * Math.PI) };

    /// <summary>Counter-clockwise rectangle with corner arcs (radius limited to half the shorter side).</summary>
    internal static List<Segment> RoundedRectangle(Vec2 c, double width, double height, double radius)
    {
        var w = Math.Max(0.01, width) / 2;
        var h = Math.Max(0.01, height) / 2;
        var r = Math.Clamp(radius, 0, Math.Min(w, h));
        if (r < 1e-6)
        {
            return Polygon(new[] { c + new Vec2(-w, -h), c + new Vec2(w, -h), c + new Vec2(w, h), c + new Vec2(-w, h) });
        }

        // Counter-clockwise from the start of the bottom edge: edge, corner arc, edge, …
        var segments = new List<Segment>();
        var corners = new[] { new Vec2(w - r, -h + r), new Vec2(w - r, h - r), new Vec2(-w + r, h - r), new Vec2(-w + r, -h + r) };
        var edgeStarts = new[] { new Vec2(-w + r, -h), new Vec2(w, -h + r), new Vec2(w - r, h), new Vec2(-w, h - r) };
        for (var k = 0; k < 4; k++)
        {
            var arc = new ArcSegment(c + corners[k], r, -Math.PI / 2 + k * Math.PI / 2, Math.PI / 2);
            var from = c + edgeStarts[k];
            if (!from.IsNear(arc.Start, 1e-9))
            {
                segments.Add(new LineSegment(from, arc.Start));
            }

            segments.Add(arc);
        }

        return segments;
    }

    /// <summary>Round hole with a flat on top: <paramref name="acrossFlat"/> is the distance from the flat to the opposite side.</summary>
    private static List<Segment> DHole(double diameter, double acrossFlat)
    {
        var r = Math.Max(0.01, diameter) / 2;
        if (acrossFlat <= 0 || acrossFlat >= diameter)
        {
            return Circle(Vec2.Zero, diameter);
        }

        var y = acrossFlat - r;
        var x = Math.Sqrt(r * r - y * y);
        var flatAngle = Math.Atan2(y, x);
        // From the left end of the flat around the bottom to its right end, then back along the flat.
        var arc = new ArcSegment(Vec2.Zero, r, Math.PI - flatAngle, Math.PI + 2 * flatAngle);
        return new List<Segment> { arc, new LineSegment(arc.End, arc.Start) };
    }

    private static List<List<Segment>> DSub(double width, double height, double spacing, double holeDiameter)
    {
        // The "D": wide on top, narrower at the bottom with 10° sides, corners rounded by 1 mm.
        var h = height / 2;
        var bottom = width / 2 - height * Math.Tan(10 * Math.PI / 180);
        var shape = new List<Vec2> { new(-bottom, -h), new(bottom, -h), new(width / 2, h), new(-width / 2, h) };
        var rounded = ClipperBridge.FromPaths(ClipperBridge.Offset(ClipperBridge.Offset(ClipperBridge.ToPaths(new[] { shape }), -1), 1));
        var result = rounded.Select(r => Polygon(r)).ToList();
        result.Add(Circle(new Vec2(-spacing / 2, 0), holeDiameter));
        result.Add(Circle(new Vec2(spacing / 2, 0), holeDiameter));
        return result;
    }

    private static List<List<Segment>> Display(double w, double h, double holesX, double holesY, double holeDiameter)
    {
        var result = new List<List<Segment>> { RoundedRectangle(Vec2.Zero, w, h, 0.5) };
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
        {
            result.Add(Circle(new Vec2(sx * holesX / 2, sy * holesY / 2), holeDiameter > 0 ? holeDiameter : 3.2));
        }

        return result;
    }

    /// <summary>Round holes in staggered rows, only whole holes inside the area.</summary>
    private static List<List<Segment>> VentHoles(double width, double height, double diameter, double pitch)
    {
        var result = new List<List<Segment>>();
        pitch = Math.Max(diameter + 0.5, pitch);
        var rowStep = pitch * Math.Sqrt(3) / 2;
        var r = diameter / 2;
        var rows = (int)Math.Floor((height - diameter) / rowStep + 1e-9) + 1;
        var y0 = -(rows - 1) * rowStep / 2;
        for (var row = 0; row < rows; row++)
        {
            var shift = row % 2 == 0 ? 0 : pitch / 2;
            var count = (int)Math.Floor((width - diameter - shift) / pitch + 1e-9) + 1;
            var x0 = -((count - 1) * pitch + shift) / 2 + shift;
            for (var i = 0; i < count; i++)
            {
                var c = new Vec2(x0 + i * pitch, y0 + row * rowStep);
                if (Math.Abs(c.X) + r <= width / 2 + 1e-9 && Math.Abs(c.Y) + r <= height / 2 + 1e-9)
                {
                    result.Add(Circle(c, diameter));
                }
            }
        }

        return result;
    }

    /// <summary>Vertical slots with round ends across the area height.</summary>
    private static List<List<Segment>> VentSlots(double width, double height, double slotWidth, double pitch)
    {
        var result = new List<List<Segment>>();
        pitch = Math.Max(slotWidth + 0.5, pitch);
        var count = (int)Math.Floor((width - slotWidth) / pitch + 1e-9) + 1;
        var x0 = -(count - 1) * pitch / 2;
        for (var i = 0; i < count && height >= slotWidth; i++)
        {
            result.Add(RoundedRectangle(new Vec2(x0 + i * pitch, 0), slotWidth, height, slotWidth / 2));
        }

        return result;
    }

    /// <summary>Hexagonal cells (flat sides left and right) with walls between them, only whole cells inside the area.</summary>
    private static List<List<Segment>> Honeycomb(double width, double height, double acrossFlats, double wall)
    {
        var result = new List<List<Segment>>();
        var radius = acrossFlats / Math.Sqrt(3); // centre to vertex
        var pitchX = acrossFlats + wall;
        var pitchY = pitchX * Math.Sqrt(3) / 2;
        for (var row = -100; row <= 100; row++)
        {
            for (var column = -100; column <= 100; column++)
            {
                var c = new Vec2(column * pitchX + (row % 2 == 0 ? 0 : pitchX / 2), row * pitchY);
                if (Math.Abs(c.X) + acrossFlats / 2 > width / 2 + 1e-9 || Math.Abs(c.Y) + radius > height / 2 + 1e-9)
                {
                    continue;
                }

                result.Add(Polygon(Enumerable.Range(0, 6).Select(k => c + new Vec2(Math.Cos(Math.PI / 2 + k * Math.PI / 3), Math.Sin(Math.PI / 2 + k * Math.PI / 3)) * radius)));
            }
        }

        return result;
    }

    private static List<List<Segment>> CornerHoles(double width, double height, double inset, double diameter)
    {
        var x = width / 2 - inset;
        var y = height / 2 - inset;
        return new List<List<Segment>> { Circle(new Vec2(-x, -y), diameter), Circle(new Vec2(x, -y), diameter), Circle(new Vec2(x, y), diameter), Circle(new Vec2(-x, y), diameter) };
    }

    private static List<Segment> Polygon(IEnumerable<Vec2> points)
    {
        var ring = points.ToList();
        return ring.Select((p, k) => (Segment)new LineSegment(p, ring[(k + 1) % ring.Count])).ToList();
    }
}
