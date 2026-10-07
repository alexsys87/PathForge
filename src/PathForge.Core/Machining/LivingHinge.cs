using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>Parameters of a living hinge: rows of staggered slots that let a plywood panel bend.</summary>
public sealed class LivingHingeSettings
{
    /// <summary>Lower-left corner of the hinge area in drawing coordinates.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 40;

    public double Height { get; set; } = 60;

    /// <summary>Slots run along Y (the panel bends around a vertical axis) or along X.</summary>
    public RasterAxis Along { get; set; } = RasterAxis.Y;

    /// <summary>Wanted slot length (mm); adjusted so that whole slots fit the area.</summary>
    public double SlotLength { get; set; } = 15;

    /// <summary>Uncut material between two slots of a line (mm).</summary>
    public double Bridge { get; set; } = 3;

    /// <summary>Distance between neighbouring lines of slots (mm): smaller bends tighter but is weaker.</summary>
    public double Spacing { get; set; } = 1.5;

    /// <summary>
    /// Every second line is cut through the area edge: the most flexible pattern when the area spans the panel
    /// from edge to edge. Off: all slots stay inside the area (a hinge in the middle of a panel).
    /// </summary>
    public bool ToEdge { get; set; } = true;
}

/// <summary>Slot contours of a living hinge and the laser operation that cuts them.</summary>
public sealed record LivingHinge(List<Contour> Contours, LaserVectorOperation Operation, List<string> Warnings)
{
    /// <summary>Layer of the hinge's slots.</summary>
    public const string Layer = "Living hinge";

    /// <summary>Slots shorter than this are left out (mm).</summary>
    private const double MinSlot = 0.5;

    /// <param name="firstContourId">Id for the first new contour; the others follow.</param>
    public static LivingHinge Build(LivingHingeSettings settings, string toolId, int firstContourId)
    {
        var warnings = new List<string>();
        var alongY = settings.Along == RasterAxis.Y;
        // Work in a frame where the slots run along "v" and the lines follow each other along "u".
        var u0 = alongY ? settings.X : settings.Y;
        var v0 = alongY ? settings.Y : settings.X;
        var across = Math.Max(0, alongY ? settings.Width : settings.Height);
        var along = Math.Max(0, alongY ? settings.Height : settings.Width);
        var bridge = Math.Max(0.2, settings.Bridge);
        var spacing = Math.Max(0.2, settings.Spacing);
        var wanted = Math.Max(MinSlot, settings.SlotLength);

        var slots = new List<(double U, double From, double To)>();
        if (along < 2 * bridge + MinSlot || across <= 0)
        {
            warnings.Add(Loc.T("Гибкий шарнир: область слишком мала для прорезей с такими перемычками.", "Living hinge: the area is too small for slots with such bridges."));
        }
        else
        {
            // Even lines: n whole slots with a bridge at both ends; odd lines: bridges in the middle of those slots.
            var n = Math.Max(1, (int)Math.Round((along - bridge) / (wanted + bridge)));
            var slot = (along - bridge) / n - bridge;
            var lines = Math.Max(1, (int)Math.Floor(across / spacing + 1e-9) + 1);
            var first = u0 + (across - (lines - 1) * spacing) / 2;
            for (var i = 0; i < lines; i++)
            {
                var u = first + i * spacing;
                var pieces = new List<(double From, double To)>();
                if (i % 2 == 0)
                {
                    for (var k = 0; k < n; k++)
                    {
                        var from = v0 + bridge + k * (slot + bridge);
                        pieces.Add((from, from + slot));
                    }
                }
                else
                {
                    var start = settings.ToEdge ? v0 : v0 + bridge;
                    var end = settings.ToEdge ? v0 + along : v0 + along - bridge;
                    var from = start;
                    for (var k = 0; k < n; k++)
                    {
                        var middle = v0 + bridge + k * (slot + bridge) + slot / 2;
                        pieces.Add((from, middle - bridge / 2));
                        from = middle + bridge / 2;
                    }

                    pieces.Add((from, end));
                }

                // Lines alternate their direction: the beam goes up one line and down the next.
                var kept = pieces.Where(p => p.To - p.From >= MinSlot).ToList();
                if (i % 2 == 1)
                {
                    kept.Reverse();
                    kept = kept.Select(p => (p.To, p.From)).ToList();
                }

                slots.AddRange(kept.Select(p => (u, p.From, p.To)));
            }

            if (slot < 2 * bridge)
            {
                warnings.Add(Loc.T($"Гибкий шарнир: прорези ({slot:0.#} мм) коротки для перемычек {bridge:0.#} мм — панель почти не будет гнуться.",
                    $"Living hinge: the slots ({slot:0.#} mm) are short for {bridge:0.#} mm bridges — the panel will hardly bend."));
            }
        }

        Vec2 P(double u, double v) => alongY ? new Vec2(u, v) : new Vec2(v, u);
        var contours = new List<Contour>();
        var nextId = firstContourId;
        foreach (var (u, from, to) in slots)
        {
            contours.Add(new Contour(nextId++, new Segment[] { new LineSegment(P(u, from), P(u, to)) }, Layer));
        }

        var operation = new LaserVectorOperation
        {
            Name = Loc.T("Гибкий шарнир", "Living hinge"),
            Mode = LaserVectorMode.Line,
            AirAssist = true,
            ToolId = toolId,
            ContourIds = contours.Select(c => c.Id).ToList(),
        };
        return new LivingHinge(contours, operation, warnings);
    }
}
