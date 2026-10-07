using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Machining;

/// <summary>Lid of a finger-joint box.</summary>
public enum BoxLid
{
    /// <summary>Open box: bottom and four walls.</summary>
    None,

    /// <summary>A lift-off lid: a top plate with a smaller plate glued under it that keeps it in place.</summary>
    Overlay,

    /// <summary>
    /// A lid that slides in grooves of the side and back walls (milled or burned half the thickness deep into their
    /// inner faces); the front wall is lower so the lid slides over it.
    /// </summary>
    Sliding,
}

/// <summary>Parameters of a box with finger joints.</summary>
public sealed class FingerBoxSettings
{
    /// <summary>Size along X (mm).</summary>
    public double Width { get; set; } = 100;

    /// <summary>Size along Y (mm).</summary>
    public double Depth { get; set; } = 60;

    /// <summary>Size along Z (mm).</summary>
    public double Height { get; set; } = 40;

    /// <summary>The sizes are the inside of the box (else the outside).</summary>
    public bool InnerDimensions { get; set; } = true;

    /// <summary>Material thickness (mm).</summary>
    public double Thickness { get; set; } = 3;

    /// <summary>Wanted finger width (mm): adjusted on every edge so that an odd number of fingers fits.</summary>
    public double FingerWidth { get; set; } = 10;

    public BoxLid Lid { get; set; } = BoxLid.None;

    /// <summary>Play in every joint (mm): positive = a gap for glue, negative = a press fit.</summary>
    public double Fit { get; set; }

    /// <summary>Gap between the parts on the sheet (mm).</summary>
    public double Gap { get; set; } = 5;

    /// <summary>Width of the sheet the parts are laid out on (mm, 0 = all in one row).</summary>
    public double SheetWidth { get; set; }

    /// <summary>Lower-left corner of the layout in drawing coordinates.</summary>
    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>The cut-out parts of a finger-joint box, laid out flat, and the grooves of a sliding lid.</summary>
/// <param name="Outlines">Closed outline of every part (cut through).</param>
/// <param name="Grooves">Lid grooves on the inner faces (pockets half the thickness deep); empty without a sliding lid.</param>
/// <param name="Outer">Outer size of the box (mm).</param>
public sealed record FingerBox(List<Contour> Outlines, List<Contour> Grooves, List<string> Warnings, Vec3 Outer)
{
    public static string Layer => Loc.T("Коробка", "Box");

    public static string GrooveLayer => Loc.T("Коробка: пазы крышки", "Box: lid grooves");

    /// <summary>Clearance of the lids (mm): a lid must slide or lift without force.</summary>
    public const double LidClearance = 0.2;

    internal enum Part
    {
        Bottom,
        Front,
        Back,
        Left,
        Right,
        Lid,
        LidInsert,
    }

    /// <summary>A part in its own coordinates (as seen from the inside of the box) before the layout.</summary>
    internal sealed record BoxPart(Part Kind, string Name, List<List<Vec2>> Outline, List<List<Vec2>> Grooves);

    /// <param name="firstContourId">Id for the first new contour; the others follow.</param>
    public static FingerBox Build(FingerBoxSettings settings, int firstContourId)
    {
        var warnings = new List<string>();
        var parts = Parts(settings, warnings, out var outer);

        // Lay the parts out in rows.
        var outlines = new List<Contour>();
        var grooveContours = new List<Contour>();
        var nextId = firstContourId;
        var gap = Math.Max(0, settings.Gap);
        double x = settings.X, y = settings.Y, rowHeight = 0;
        foreach (var part in parts)
        {
            var bounds = part.Outline.Aggregate(Bounds2.Empty, (b, r) => b.Union(Bounds2.Of(r)));
            if (settings.SheetWidth > 0 && x > settings.X && x + bounds.Width > settings.X + settings.SheetWidth)
            {
                x = settings.X;
                y += rowHeight + gap;
                rowHeight = 0;
            }

            var shift = new Vec2(x - bounds.MinX, y - bounds.MinY);
            outlines.AddRange(part.Outline.Select(r => Polygon(nextId++, r.Select(p => p + shift).ToList(), Layer)));
            grooveContours.AddRange(part.Grooves.Select(g => Polygon(nextId++, g.Select(p => p + shift).ToList(), GrooveLayer)));
            x += bounds.Width + gap;
            rowHeight = Math.Max(rowHeight, bounds.Height);
        }

        return new FingerBox(outlines, grooveContours, warnings, outer);
    }

    /// <summary>The parts of the box in their own coordinates, in the layout order.</summary>
    internal static List<BoxPart> Parts(FingerBoxSettings settings, List<string> warnings, out Vec3 outer)
    {
        var t = Math.Max(0.5, settings.Thickness);
        var lidSpace = settings.Lid == BoxLid.Sliding ? 2 * t : 0;
        var wo = settings.InnerDimensions ? settings.Width + 2 * t : settings.Width;
        var dO = settings.InnerDimensions ? settings.Depth + 2 * t : settings.Depth;
        var ho = settings.InnerDimensions ? settings.Height + t + lidSpace : settings.Height;
        if (wo < 4 * t || dO < 4 * t || ho < 4 * t + lidSpace)
        {
            warnings.Add(Loc.T("Коробка: размеры слишком малы для такой толщины материала.", "Box: the sizes are too small for this material thickness."));
            outer = new Vec3(wo, dO, ho);
            return new List<BoxPart>();
        }

        outer = new Vec3(wo, dO, ho);

        var fit = settings.Fit;
        var clearance = LidClearance + Math.Max(0, fit);
        var groove = t / 2;
        // Front wall of a sliding-lid box: the lid slides over it.
        var hf = settings.Lid == BoxLid.Sliding ? ho - 2 * t - clearance : ho;
        var fw = Math.Max(t, settings.FingerWidth);

        // Joints, each as segments along it with their owner. The bottom owns the corner cubes.
        var bottomFront = Joint(wo, t, fw, Part.Front, Part.Bottom, cubeLow: true, cubeHigh: true, warnings);
        var bottomBack = Joint(wo, t, fw, Part.Back, Part.Bottom, cubeLow: true, cubeHigh: true, warnings);
        var bottomLeft = Joint(dO, t, fw, Part.Left, Part.Bottom, cubeLow: true, cubeHigh: true, warnings);
        var bottomRight = Joint(dO, t, fw, Part.Right, Part.Bottom, cubeLow: true, cubeHigh: true, warnings);
        var frontLeft = Joint(hf, t, fw, Part.Front, Part.Left, cubeLow: true, cubeHigh: false, warnings);
        var frontRight = Joint(hf, t, fw, Part.Front, Part.Right, cubeLow: true, cubeHigh: false, warnings);
        var backLeft = Joint(ho, t, fw, Part.Back, Part.Left, cubeLow: true, cubeHigh: false, warnings);
        var backRight = Joint(ho, t, fw, Part.Back, Part.Right, cubeLow: true, cubeHigh: false, warnings);
        if (hf < ho)
        {
            // Above the low front wall the side walls are whole.
            frontLeft.Add((hf, ho, Part.Left));
            frontRight.Add((hf, ho, Part.Right));
        }

        var parts = new List<BoxPart>();

        // Bottom: x along u, y along v.
        parts.Add(new BoxPart(Part.Bottom, Loc.T("Дно", "Bottom"), Panel(wo, dO, new[]
        {
            Notches(bottomFront, Part.Bottom, fit, Edge.Low, t, wo, dO, vertical: false),
            Notches(bottomBack, Part.Bottom, fit, Edge.High, t, wo, dO, vertical: false),
            Notches(bottomLeft, Part.Bottom, fit, Edge.Low, t, wo, dO, vertical: true),
            Notches(bottomRight, Part.Bottom, fit, Edge.High, t, wo, dO, vertical: true),
        }), new List<List<Vec2>>()));

        // Front and back: x along u, z along v; bottom joint at v = 0, side joints at u = 0 and u = width.
        foreach (var (name, self, height, bottom, left, right) in new[]
                 {
                     (Loc.T("Перед", "Front"), Part.Front, hf, bottomFront, frontLeft, frontRight),
                     (Loc.T("Зад", "Back"), Part.Back, ho, bottomBack, backLeft, backRight),
                 })
        {
            var isBack = self == Part.Back;
            var outline = Panel(wo, height, new[]
            {
                Notches(bottom, self, fit, Edge.Low, t, wo, height, vertical: false),
                Notches(left, self, fit, Edge.Low, t, wo, height, vertical: true),
                Notches(right, self, fit, Edge.High, t, wo, height, vertical: true),
            });
            var grooves = new List<List<Vec2>>();
            if (isBack && settings.Lid == BoxLid.Sliding)
            {
                grooves.Add(Rect(t - groove, ho - 2 * t - clearance / 2, wo - t + groove, ho - t + clearance / 2));
            }

            // Drawn as seen from inside: the front is mirrored.
            parts.Add(new BoxPart(self, name, isBack ? outline : Mirror(outline, wo), isBack ? grooves : grooves.Select(g => MirrorRing(g, wo)).ToList()));
        }

        // Left and right: y along u, z along v; front joint at u = 0, back joint at u = depth.
        foreach (var (name, self, bottom, front, back) in new[]
                 {
                     (Loc.T("Левая стенка", "Left side"), Part.Left, bottomLeft, frontLeft, backLeft),
                     (Loc.T("Правая стенка", "Right side"), Part.Right, bottomRight, frontRight, backRight),
                 })
        {
            var isRight = self == Part.Right;
            var outline = Panel(dO, ho, new[]
            {
                Notches(bottom, self, fit, Edge.Low, t, dO, ho, vertical: false),
                Notches(front, self, fit, Edge.Low, t, dO, ho, vertical: true),
                Notches(back, self, fit, Edge.High, t, dO, ho, vertical: true),
            });
            var grooves = new List<List<Vec2>>();
            if (settings.Lid == BoxLid.Sliding)
            {
                // Open at the front edge, up to the back wall's groove.
                grooves.Add(Rect(-1, ho - 2 * t - clearance / 2, dO - t + groove, ho - t + clearance / 2));
            }

            parts.Add(new BoxPart(self, name, isRight ? Mirror(outline, dO) : outline, isRight ? grooves.Select(g => MirrorRing(g, dO)).ToList() : grooves));
        }

        if (settings.Lid == BoxLid.Overlay)
        {
            parts.Add(new BoxPart(Part.Lid, Loc.T("Крышка", "Lid"), new List<List<Vec2>> { Rect(0, 0, wo, dO) }, new List<List<Vec2>>()));
            var lipW = wo - 2 * t - 2 * clearance;
            var lipD = dO - 2 * t - 2 * clearance;
            parts.Add(new BoxPart(Part.LidInsert, Loc.T("Вкладыш крышки (приклеить снизу)", "Lid insert (glued under it)"), new List<List<Vec2>> { Rect(0, 0, lipW, lipD) }, new List<List<Vec2>>()));
        }
        else if (settings.Lid == BoxLid.Sliding)
        {
            var lidW = wo - 2 * t + 2 * groove - clearance;
            var lidD = dO - t + groove - clearance / 2;
            var lid = ClipperBridge.ToPaths(new[] { Rect(0, 0, lidW, lidD) });
            // Rounded back corners: a milled groove ends in the cutter's radius.
            var r = Math.Min(2, Math.Min(lidW, lidD) / 4);
            var rounded = ClipperBridge.Offset(ClipperBridge.Offset(ClipperBridge.ToPaths(new[] { Rect(0, -r - 1, lidW, lidD) }), -r), r);
            lid = ClipperBridge.Intersect(rounded, ClipperBridge.ToPaths(new[] { Rect(-1, 0, lidW + 1, lidD + 1) }));
            parts.Add(new BoxPart(Part.Lid, Loc.T("Сдвижная крышка", "Sliding lid"), ClipperBridge.FromPaths(lid), new List<List<Vec2>>()));
        }

        return parts;
    }



    private enum Edge
    {
        Low,
        High,
    }

    /// <summary>
    /// Segments along a joint: the corner cubes (owned by the bottom) at the requested ends, in between an odd
    /// number of fingers alternating between <paramref name="first"/> and <paramref name="second"/>.
    /// </summary>
    private static List<(double From, double To, Part Owner)> Joint(double length, double t, double fingerWidth, Part first, Part second,
        bool cubeLow, bool cubeHigh, List<string> warnings)
    {
        var segments = new List<(double, double, Part)>();
        var start = cubeLow ? t : 0;
        var end = cubeHigh ? length - t : length;
        if (cubeLow)
        {
            segments.Add((0, t, Part.Bottom));
        }

        var exact = (end - start) / fingerWidth;
        var count = Math.Max(3, (int)Math.Round(exact));
        if (count % 2 == 0)
        {
            count = exact > count || count == 3 ? count + 1 : count - 1;
            count = Math.Max(3, count);
        }

        var finger = (end - start) / count;
        if (finger < t && warnings.Count == 0)
        {
            warnings.Add(Loc.T(
                $"Коробка: шипы ({finger:0.#} мм) уже толщины материала — они будут ломаться. Увеличьте размеры или уменьшите ширину шипа.",
                $"Box: the fingers ({finger:0.#} mm) are narrower than the material — they will break. Make the box larger or the fingers narrower."));
        }

        for (var i = 0; i < count; i++)
        {
            segments.Add((start + i * finger, start + (i + 1) * finger, i % 2 == 0 ? first : second));
        }

        if (cubeHigh)
        {
            segments.Add((length - t, length, Part.Bottom));
        }

        return segments;
    }

    /// <summary>
    /// Notch rectangles (material removed to the thickness) of one panel edge: every segment it does not own,
    /// widened by half the fit where it borders a segment the panel owns.
    /// </summary>
    private static List<List<Vec2>> Notches(List<(double From, double To, Part Owner)> joint, Part self, double fit, Edge edge,
        double t, double lu, double lv, bool vertical)
    {
        var notches = new List<List<Vec2>>();
        for (var i = 0; i < joint.Count; i++)
        {
            var (from, to, owner) = joint[i];
            if (owner == self)
            {
                continue;
            }

            if (i > 0 && joint[i - 1].Owner == self)
            {
                from -= fit / 2;
            }

            if (i + 1 < joint.Count && joint[i + 1].Owner == self)
            {
                to += fit / 2;
            }

            // Beyond the panel at the ends of the edge: no slivers along the outline.
            if (i == 0)
            {
                from = Math.Min(from, -1);
            }

            if (i == joint.Count - 1)
            {
                to += 1;
            }

            notches.Add(vertical
                ? edge == Edge.Low ? Rect(-1, from, t, to) : Rect(lu - t, from, lu + 1, to)
                : edge == Edge.Low ? Rect(from, -1, to, t) : Rect(from, lv - t, to, lv + 1));
        }

        return notches;
    }

    private static List<List<Vec2>> Panel(double lu, double lv, IEnumerable<List<List<Vec2>>> notches) =>
        ClipperBridge.FromPaths(ClipperBridge.Difference(ClipperBridge.ToPaths(new[] { Rect(0, 0, lu, lv) }),
            ClipperBridge.Union(ClipperBridge.ToPaths(notches.SelectMany(n => n).Cast<IReadOnlyList<Vec2>>()))));

    private static List<List<Vec2>> Mirror(List<List<Vec2>> outline, double lu) => outline.Select(r => MirrorRing(r, lu)).ToList();

    private static List<Vec2> MirrorRing(List<Vec2> ring, double lu) => Enumerable.Reverse(ring).Select(p => new Vec2(lu - p.X, p.Y)).ToList();

    private static List<Vec2> Rect(double x0, double y0, double x1, double y1) => new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    private static Contour Polygon(int id, List<Vec2> ring, string layer) =>
        new(id, ring.Select((p, k) => (Segment)new LineSegment(p, ring[(k + 1) % ring.Count])).ToList(), layer);
}
